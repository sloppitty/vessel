import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { authErrorMessage } from '@/api/auth'
import { walletFixture } from '@/test/identity-fixture'
import { channelBus } from '@/test/session-channel'
import { systemFixture } from '@/test/system-fixture'
import { pairingUrl } from './pairing-link'
import { SignInGate } from './SignInGate'

const secret = 'Lm4_-z'.padEnd(43, 'd')
const claim = { code: '071925', expiresAt: new Date(Date.now() + 120_000).toISOString(), journal: { displayName: 'Sam Rivera', wallet: walletFixture.address } }
type Route = (init: RequestInit) => Response
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
const problem = (status: number, code: string) => json({ title: 'private', code }, status)

let routes: Record<string, Route>
let calls: Array<{ path: string; body: unknown }>
let tracks: Array<{ stop: ReturnType<typeof vi.fn> }>

const device = (secure: boolean, usable: boolean) => {
  Object.defineProperty(window, 'isSecureContext', { configurable: true, value: secure })
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: usable ? { getUserMedia: vi.fn(async () => ({ getTracks: () => tracks })) } : undefined })
}

beforeEach(() => {
  calls = []
  tracks = [{ stop: vi.fn() }]
  routes = {
    '/api/auth/session': () => problem(401, 'session-required'),
    '/api/auth/pairings/claim': () => json(claim),
    '/api/auth/pairings/complete': () => json({ status: 'waiting' }),
    '/api/system': () => json(systemFixture),
  }
  vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit) => {
    calls.push({ path, body: init.body ? JSON.parse(init.body as string) : undefined })
    const route = routes[path]
    return route ? route(init) : new Response(null, { status: 404 })
  }))
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined)
  window.history.replaceState(null, '', '/')
})
afterEach(() => {
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: undefined })
  Object.defineProperty(window, 'isSecureContext', { configurable: true, value: true })
})

function mount() {
  render(<SignInGate registration="closed" originAccepted channels={channelBus().factory} navigate={vi.fn()}>
    {() => <p>Shell</p>}
  </SignInGate>)
  return userEvent.setup()
}
const claims = () => calls.filter(call => call.path === '/api/auth/pairings/claim')

describe('sign-in screen with a usable camera', () => {
  beforeEach(() => device(true, true))

  it('offers Scan a code from your desktop beside the wallets', async () => {
    mount()
    expect(await screen.findByRole('button', { name: 'Scan a code from your desktop' })).toBeVisible()
    // Wallet sign-in stays on the same screen.
    expect(screen.getByText('In this browser')).toBeVisible()
  })

  it('scans a link on this origin and continues on the pairing screen without leaving the page', async () => {
    vi.stubGlobal('BarcodeDetector', class {
      static getSupportedFormats = async () => ['qr_code']
      detect = async () => [{ rawValue: pairingUrl(window.location.origin, secret) }]
    })
    const user = mount()
    await user.click(await screen.findByRole('button', { name: 'Scan a code from your desktop' }))
    await user.click(screen.getByRole('button', { name: 'Start camera' }))
    expect(await screen.findByText('071 925')).toBeVisible()
    expect(claims()).toHaveLength(1)
    expect(claims()[0]!.body).toEqual({ secret })
    expect(tracks[0]!.stop).toHaveBeenCalled()
  })

  it('moves from the scanner to the typed code and back', async () => {
    const user = mount()
    await user.click(await screen.findByRole('button', { name: 'Scan a code from your desktop' }))
    await user.click(screen.getByRole('button', { name: 'Enter a code instead' }))
    expect(screen.getByRole('heading', { name: 'Enter a code' })).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Back' }))
    expect(screen.getByRole('heading', { name: 'Sign in to Vessel' })).toBeVisible()
  })
})

describe('sign-in screen without a usable camera', () => {
  it.each([['a plain-HTTP origin', false, true], ['a browser without getUserMedia', true, false]])('on %s it points to the camera app and the typed code', async (_label, secure, usable) => {
    device(secure, usable)
    mount()
    const group = await screen.findByRole('group', { name: 'Signed in on a computer?' })
    expect(within(group).getByText(/camera app/)).toBeVisible()
    expect(within(group).getByRole('button', { name: 'Enter a code instead' })).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Scan a code from your desktop' })).not.toBeInTheDocument()
  })

  it('claims a typed code as typed and continues on the pairing screen', async () => {
    device(false, false)
    const user = mount()
    await user.click(await screen.findByRole('button', { name: 'Enter a code instead' }))
    const input = screen.getByLabelText('Code from your computer')
    expect(screen.getByRole('button', { name: 'Continue' })).toBeDisabled()
    await user.type(input, '7kq2m-hx9pd')
    await user.click(screen.getByRole('button', { name: 'Continue' }))
    expect(await screen.findByText('071 925')).toBeVisible()
    expect(claims()).toHaveLength(1)
    expect(claims()[0]!.body).toEqual({ typedCode: '7kq2m-hx9pd' })
  })

  it('keeps a wrong typed code on the form with the server copy', async () => {
    device(false, false)
    routes['/api/auth/pairings/claim'] = () => problem(400, 'pairing-invalid')
    const user = mount()
    await user.click(await screen.findByRole('button', { name: 'Enter a code instead' }))
    await user.type(screen.getByLabelText('Code from your computer'), 'AAAAA BBBBB')
    await user.click(screen.getByRole('button', { name: 'Continue' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(authErrorMessage('pairing-invalid')!)
    expect(screen.getByLabelText('Code from your computer')).toHaveValue('AAAAA BBBBB')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Continue' })).toBeEnabled())
  })
})
