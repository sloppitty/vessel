import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import jsQR from 'jsqr'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { authErrorMessage, createSessionAuthApi, type PairingStatus } from '@/api/auth'
import { challengeFixture, secondWalletFixture, sessionFixture, walletFixture } from '@/test/identity-fixture'
import { ConnectPhoneDialog } from './ConnectPhoneDialog'
import { pairingUrl } from './pairing-link'

const origin = 'https://vessel.test'
const pairingId = '7c1e9b52-4d8a-4f36-a0e7-2b5d9c3f1a84'
const secret = 'Hn2_-v'.padEnd(43, 'e')
const created = { id: pairingId, secret, typedCode: '7KQ2M-HX9PD', expiresAt: new Date(Date.now() + 120_000).toISOString() }
const signature = `0x${'ef'.repeat(65)}`
type Route = (init: RequestInit) => Response
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
const problem = (status: number, code: string, extra: Record<string, unknown> = {}) => json({ code, ...extra }, status)

let routes: Record<string, Route | Route[]>
let calls: Array<{ path: string; method: string; body: unknown }>
let announcer: () => void
const statusPath = `GET /api/auth/pairings/${pairingId}`
const status = (value: PairingStatus, device: string | null = null) => () => json({ status: value, expiresAt: new Date(Date.now() + 120_000).toISOString(), device })
const sent = (method: string, path: string) => calls.filter(call => call.method === method && call.path === path)

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  calls = []
  routes = {
    'POST /api/auth/step-up/challenge': init => json(challengeFixture(JSON.parse(init.body as string).address)),
    'POST /api/auth/step-up': () => new Response(null, { status: 204 }),
    'POST /api/auth/pairings': () => json(created, 201),
    [statusPath]: status('pending'),
    [`POST /api/auth/pairings/${pairingId}/approve`]: () => new Response(null, { status: 204 }),
    [`DELETE /api/auth/pairings/${pairingId}`]: () => new Response(null, { status: 204 }),
  }
  vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit) => {
    const method = init.method ?? 'GET'
    calls.push({ path, method, body: init.body ? JSON.parse(init.body as string) : undefined })
    const route = routes[`${method} ${path}`]
    const next = Array.isArray(route) ? route.shift() : route
    return next?.(init) ?? new Response(null, { status: 404 })
  }))
  const provider = { request: vi.fn(async ({ method }: { method: string }) => method === 'eth_requestAccounts' ? [walletFixture.address] : signature) }
  announcer = () => window.dispatchEvent(new CustomEvent('eip6963:announceProvider', {
    detail: { info: { uuid: 'rabby-uuid', name: 'Rabby Wallet', icon: null, rdns: 'io.rabby' }, provider },
  }))
  window.addEventListener('eip6963:requestProvider', announcer)
})
afterEach(() => { window.removeEventListener('eip6963:requestProvider', announcer); vi.useRealTimers() })

function mount(options: { origin?: string; accept?: (address: string) => string | null } = {}) {
  const handlers = { onConnected: vi.fn(), onClose: vi.fn() }
  const auth = createSessionAuthApi({ userId: sessionFixture.user.id, sessionId: sessionFixture.session.id })
  const view = render(<ConnectPhoneDialog auth={auth} accept={options.accept ?? (() => null)} origin={options.origin ?? origin} {...handlers} />)
  return { ...view, ...handlers, user: userEvent.setup({ advanceTimers: ms => { vi.advanceTimersByTime(ms) } }) }
}
const poll = (ms = 2_000) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })

async function showQr(user: ReturnType<typeof userEvent.setup>) {
  const dialog = await screen.findByRole('dialog', { name: 'Connect a phone' })
  await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
  await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
  await within(dialog).findByRole('img', { name: 'Pairing QR code' })
  return dialog
}

/** Rasterizes the SVG the dialog draws and reads it back with a QR decoder: what a phone camera would see. */
function decodeQr(svg: Element) {
  const [, , size] = svg.getAttribute('viewBox')!.split(' ').map(Number) as [number, number, number]
  const scale = 6
  const width = size * scale
  const pixels = new Uint8ClampedArray(width * width * 4).fill(255)
  for (const [, x, y, run] of svg.querySelector('path')!.getAttribute('d')!.matchAll(/M(\d+) (\d+)h(\d+)v1h-\d+z/g)) {
    for (let row = 0; row < scale; row++) for (let column = 0; column < Number(run) * scale; column++) {
      const at = ((Number(y) * scale + row) * width + Number(x) * scale + column) * 4
      pixels[at] = pixels[at + 1] = pixels[at + 2] = 0
    }
  }
  return jsQR(pixels, width, width)?.data
}

describe('Connect a phone', () => {
  it('confirms with a linked wallet, shows a scannable QR with a countdown, then the phone, then done', async () => {
    const { user, onConnected } = mount()
    const dialog = await showQr(user)
    expect(sent('POST', '/api/auth/step-up/challenge')[0]!.body).toEqual({ action: 'device.pair', address: walletFixture.address })
    expect(sent('POST', '/api/auth/pairings')).toHaveLength(1)
    const qr = within(dialog).getByRole('img', { name: 'Pairing QR code' })
    expect(decodeQr(qr)).toBe(pairingUrl(origin, secret))
    // The secret lives in the QR only: not in any request line or visible text.
    expect(within(dialog).getByRole('timer')).toHaveAccessibleName(/^Expires in 1:5\d$|^Expires in 2:00$/)
    expect(dialog).not.toHaveTextContent(secret)

    routes[statusPath] = status('claimed', 'Safari on iPhone')
    await poll()
    expect(await within(dialog).findByText('Safari on iPhone wants to connect')).toBeVisible()
    const field = within(dialog).getByLabelText('Code shown on the phone')
    expect(within(dialog).getByRole('button', { name: 'Approve' })).toBeDisabled()
    await user.type(field, '482 913')
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))
    expect(sent('POST', `/api/auth/pairings/${pairingId}/approve`)[0]!.body).toEqual({ code: '482913' })
    expect(await within(dialog).findByText(/Waiting for Safari on iPhone to finish signing in/)).toBeVisible()

    routes[statusPath] = status('completed', 'Safari on iPhone')
    await poll()
    expect(await within(dialog).findByText('Safari on iPhone is connected.')).toBeVisible()
    expect(onConnected).toHaveBeenCalledTimes(1)
    // Done means nothing is left to cancel or poll.
    const reads = sent('GET', `/api/auth/pairings/${pairingId}`).length
    await poll(6_000)
    expect(sent('GET', `/api/auth/pairings/${pairingId}`)).toHaveLength(reads)
    await user.click(within(dialog).getByRole('button', { name: 'Done' }))
    expect(sent('DELETE', `/api/auth/pairings/${pairingId}`)).toHaveLength(0)
  })

  it('reveals the code to type on request', async () => {
    const { user } = mount()
    const dialog = await showQr(user)
    expect(within(dialog).queryByText('7KQ2M-HX9PD')).not.toBeInTheDocument()
    const toggle = within(dialog).getByRole('button', { name: 'Show a code to type' })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    await user.click(toggle)
    expect(within(dialog).getByText('7KQ2M-HX9PD')).toBeVisible()
    expect(within(dialog).getByRole('button', { name: 'Hide the code' })).toHaveAttribute('aria-expanded', 'true')
  })

  it('polls the status every two seconds', async () => {
    const { user } = mount()
    await showQr(user)
    await poll(); await poll(); await poll()
    expect(sent('GET', `/api/auth/pairings/${pairingId}`)).toHaveLength(3)
  })

  it('only offers linked wallets and sends nothing for an unlinked address', async () => {
    const { user } = mount({ accept: address => address === secondWalletFixture.address ? null : 'This address isn’t linked to your journal.' })
    const dialog = await screen.findByRole('dialog', { name: 'Connect a phone' })
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    expect(await within(dialog).findByText('This address isn’t linked to your journal.')).toBeVisible()
    expect(sent('POST', '/api/auth/pairings')).toHaveLength(0)
  })

  it('asks to confirm again when the grant lapsed while the wallet signed', async () => {
    routes['POST /api/auth/pairings'] = [() => problem(403, 'step-up-required'), () => json(created, 201)]
    const { user } = mount()
    const dialog = await screen.findByRole('dialog', { name: 'Connect a phone' })
    await user.click(await within(dialog).findByRole('button', { name: /Rabby Wallet/ }))
    await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
    expect(await within(dialog).findByRole('button', { name: /Rabby Wallet/ })).toBeVisible()
    expect(within(dialog).queryByRole('img', { name: 'Pairing QR code' })).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: /Rabby Wallet/ }))
    await user.click(await within(dialog).findByRole('button', { name: 'Sign with Rabby Wallet' }))
    expect(await within(dialog).findByRole('img', { name: 'Pairing QR code' })).toBeVisible()
  })

  it('ends when the code expires unclaimed and starts over with Try again', async () => {
    const { user } = mount()
    const dialog = await showQr(user)
    routes[statusPath] = status('expired')
    await poll()
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Request expired')
    expect(within(dialog).queryByRole('img', { name: 'Pairing QR code' })).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Try again' }))
    expect(await within(dialog).findByRole('button', { name: /Rabby Wallet/ })).toBeVisible()
  })

  it('asks the server when its countdown ends and ends on its answer', async () => {
    const deadline = Date.now() + 4_000
    routes['POST /api/auth/pairings'] = () => json({ ...created, expiresAt: new Date(deadline).toISOString() }, 201)
    routes[statusPath] = () => json({ status: Date.now() >= deadline ? 'expired' : 'pending', expiresAt: new Date(deadline).toISOString(), device: null })
    const { user } = mount()
    const dialog = await showQr(user)
    await poll(5_000)
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Request expired')
    // The server expired it already: nothing to cancel.
    expect(sent('DELETE', `/api/auth/pairings/${pairingId}`)).toHaveLength(0)
  })

  it('tells the owner when another device used the code', async () => {
    const { user } = mount()
    const dialog = await showQr(user)
    routes[statusPath] = status('contested')
    await poll()
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Another device used the code')
    expect(within(dialog).getByRole('button', { name: 'Try again' })).toBeVisible()
  })

  it('shows the attempts left after a wrong code and keeps the form', async () => {
    const { user } = mount()
    const dialog = await showQr(user)
    routes[statusPath] = status('claimed', 'Chrome on Android')
    await poll()
    routes[`POST /api/auth/pairings/${pairingId}/approve`] = () => problem(400, 'pairing-code-invalid', { attemptsLeft: 2 })
    await user.type(await within(dialog).findByLabelText('Code shown on the phone'), '111111')
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('That confirmation code is not right. 2 attempts left.')
    // The wrong code is cleared so the next try starts fresh.
    expect(within(dialog).getByLabelText('Code shown on the phone')).toHaveValue('')
  })

  it('ends the request after the third wrong code', async () => {
    const { user } = mount()
    const dialog = await showQr(user)
    routes[statusPath] = status('claimed', 'Chrome on Android')
    await poll()
    routes[`POST /api/auth/pairings/${pairingId}/approve`] = () => problem(400, 'pairing-code-invalid', { attemptsLeft: 0 })
    await user.type(await within(dialog).findByLabelText('Code shown on the phone'), '222222')
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Too many wrong codes')
    expect(within(dialog).queryByLabelText('Code shown on the phone')).not.toBeInTheDocument()
  })

  it('shows the Vessel copy when the approval window has passed', async () => {
    const { user } = mount()
    const dialog = await showQr(user)
    routes[statusPath] = status('claimed', 'Chrome on Android')
    await poll()
    routes[`POST /api/auth/pairings/${pairingId}/approve`] = () => problem(400, 'pairing-expired')
    await user.type(await within(dialog).findByLabelText('Code shown on the phone'), '333333')
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Request expired')
    expect(authErrorMessage('pairing-expired')).toBeTruthy()
  })

  it('cancels the pairing and stops polling when the dialog closes', async () => {
    const { user, onClose } = mount()
    const dialog = await showQr(user)
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(onClose).toHaveBeenCalledTimes(1)
    await waitFor(() => expect(sent('DELETE', `/api/auth/pairings/${pairingId}`)).toHaveLength(1))
  })

  it('cancels the pairing and stops polling when the dialog goes away', async () => {
    const { user, unmount } = mount()
    await showQr(user)
    await poll()
    const reads = sent('GET', `/api/auth/pairings/${pairingId}`).length
    unmount()
    await waitFor(() => expect(sent('DELETE', `/api/auth/pairings/${pairingId}`)).toHaveLength(1))
    await poll(6_000)
    expect(sent('GET', `/api/auth/pairings/${pairingId}`)).toHaveLength(reads)
  })

  it('stops after a few unreachable reads', async () => {
    const { user } = mount()
    const dialog = await showQr(user)
    routes[statusPath] = () => { throw new TypeError('offline') }
    for (let attempt = 0; attempt < 5; attempt++) await poll()
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Vessel is unreachable')
  })

  it('does not create a pairing from an effect: nothing is sent before the wallet signs', async () => {
    mount()
    await screen.findByRole('dialog', { name: 'Connect a phone' })
    expect(sent('POST', '/api/auth/pairings')).toHaveLength(0)
    expect(sent('POST', '/api/auth/step-up/challenge')).toHaveLength(0)
  })
})

describe('a loopback address', () => {
  it.each(['http://localhost:5180', 'http://127.0.0.1:5180', 'http://[::1]:5180'])('explains that a phone cannot open %s and offers no QR', async address => {
    mount({ origin: address })
    const dialog = await screen.findByRole('dialog', { name: 'Connect a phone' })
    expect(within(dialog).getByRole('note')).toHaveTextContent('A phone can’t open this address')
    expect(within(dialog).queryByRole('img')).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: /Rabby Wallet/ })).not.toBeInTheDocument()
    expect(calls).toHaveLength(0)
  })
})
