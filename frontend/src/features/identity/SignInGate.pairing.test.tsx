import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { authErrorMessage } from '@/api/auth'
import { sessionFixture, walletFixture } from '@/test/identity-fixture'
import { channelBus } from '@/test/session-channel'
import { systemFixture } from '@/test/system-fixture'
import { pairingUrl } from './pairing-link'
import { SignInGate } from './SignInGate'

const secret = 'Pq7_-m'.padEnd(43, 'c')
const claim = { code: '482913', expiresAt: new Date(Date.now() + 120_000).toISOString(), journal: { displayName: 'Sam Rivera', wallet: walletFixture.address } }
type Route = (init: RequestInit) => Response | Promise<Response>
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
const problem = (status: number, code: string) => json({ title: 'private', code }, status)

let routes: Record<string, Route>
let calls: Array<{ path: string; body: unknown; headers: Record<string, string> }>

beforeEach(() => {
  calls = []
  routes = {
    '/api/auth/session': () => problem(401, 'session-required'),
    '/api/auth/pairings/claim': () => json(claim),
    '/api/auth/pairings/complete': () => json({ status: 'waiting' }),
    '/api/system': () => json(systemFixture),
  }
  vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit) => {
    calls.push({ path, body: init.body ? JSON.parse(init.body as string) : undefined, headers: init.headers as Record<string, string> })
    const route = routes[path]
    return route ? route(init) : new Response(null, { status: 404 })
  }))
  window.history.replaceState(null, '', `/#pair=${secret}`)
})
afterEach(() => { vi.useRealTimers(); window.history.replaceState(null, '', '/') })

const sent = (path: string) => calls.filter(call => call.path === path)

function mount(strict = false) {
  const bus = channelBus()
  const gate = <SignInGate registration="closed" originAccepted channels={bus.factory} navigate={vi.fn()}>
    {(_system, _disconnect, _api, identity) => <p>Shell for {identity.session.user.displayName}</p>}
  </SignInGate>
  const view = render(strict ? <StrictMode>{gate}</StrictMode> : gate)
  return { ...view, bus, user: userEvent.setup({ advanceTimers: ms => { if (vi.isFakeTimers()) vi.advanceTimersByTime(ms) } }) }
}
/** Lets the 2-second poll fire. */
const poll = () => act(async () => { await vi.advanceTimersByTimeAsync(2_000) })

describe('a pairing link opened on a device with no session', () => {
  it('removes the fragment at once, claims the secret and shows the confirmation code', async () => {
    mount()
    expect(window.location.hash).toBe('')
    expect(await screen.findByRole('heading', { name: 'Confirm on your computer' })).toBeVisible()
    expect(screen.getByText('482 913')).toBeVisible()
    expect(screen.getByText('Sam Rivera')).toBeVisible()
    expect(sent('/api/auth/pairings/claim')).toHaveLength(1)
    expect(sent('/api/auth/pairings/claim')[0]!.body).toEqual({ secret })
    expect(sent('/api/auth/pairings/claim')[0]!.headers).toMatchObject({ 'X-Vessel-Request': '1' })
    // The secret is posted in the body only, never in a request line.
    expect(calls.every(call => !call.path.includes(secret))).toBe(true)
    expect(screen.queryByRole('button', { name: /Synthetic|Rabby/ })).not.toBeInTheDocument()
  })

  it('claims once even when React mounts the screen twice', async () => {
    mount(true)
    expect(await screen.findByText('482 913')).toBeVisible()
    expect(sent('/api/auth/pairings/claim')).toHaveLength(1)
  })

  it('keeps me signed in is checked by default', async () => {
    mount()
    expect(await screen.findByRole('checkbox', { name: 'Keep me signed in on this device' })).toBeChecked()
  })

  it('polls every two seconds with the checkbox state and opens the workspace like a normal sign-in', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const { user, bus } = mount()
    const remember = await screen.findByRole('checkbox', { name: 'Keep me signed in on this device' })
    await poll()
    expect(sent('/api/auth/pairings/complete')).toHaveLength(1)
    expect(sent('/api/auth/pairings/complete')[0]!.body).toEqual({ remember: true })
    await user.click(remember)
    await poll()
    expect(sent('/api/auth/pairings/complete')[1]!.body).toEqual({ remember: false })
    routes['/api/auth/pairings/complete'] = () => json({ status: 'signed-in', session: sessionFixture })
    await poll()
    expect(await screen.findByText(`Shell for ${sessionFixture.user.displayName}`)).toBeVisible()
    expect(bus.posted).toEqual([{ type: 'signed-in', userId: sessionFixture.user.id, sessionId: sessionFixture.session.id }])
    await poll()
    expect(sent('/api/auth/pairings/complete')).toHaveLength(3)
  })

  it.each([
    ['expired', 'Request expired'],
    ['cancelled', 'Request cancelled'],
    ['contested', 'Another device used this code'],
  ])('ends with a message on %s and returns to sign-in', async (status, title) => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    routes['/api/auth/pairings/complete'] = () => json({ status })
    const { user } = mount()
    await screen.findByText('482 913')
    await poll()
    expect(await screen.findByText(title)).toBeVisible()
    expect(screen.queryByText('482 913')).not.toBeInTheDocument()
    await poll()
    expect(sent('/api/auth/pairings/complete')).toHaveLength(1)
    await user.click(screen.getByRole('button', { name: 'Back to sign-in' }))
    expect(await screen.findByRole('heading', { name: 'Sign in to Vessel' })).toBeVisible()
  })

  it('ends when the server says the confirmation window passed', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const deadline = Date.now() + 5_000
    routes['/api/auth/pairings/claim'] = () => json({ ...claim, expiresAt: new Date(deadline).toISOString() })
    routes['/api/auth/pairings/complete'] = () => json({ status: Date.now() >= deadline ? 'expired' : 'waiting' })
    mount()
    await screen.findByText('482 913')
    await act(async () => { await vi.advanceTimersByTimeAsync(6_000) })
    expect(await screen.findByText('Request expired')).toBeVisible()
  })

  it.each([
    ['pairing-contested', 409, 'Another device used this code'],
    ['pairing-expired', 400, 'Request expired'],
    ['pairing-invalid', 400, 'Could not connect'],
  ])('explains a refused claim (%s)', async (code, status, title) => {
    routes['/api/auth/pairings/claim'] = () => problem(status, code)
    mount()
    expect(await screen.findByText(title)).toBeVisible()
    expect(screen.getByRole('alert')).toHaveTextContent(code === 'pairing-invalid' ? authErrorMessage(code)! : /.+/)
    expect(screen.getByRole('button', { name: 'Back to sign-in' })).toBeVisible()
  })

  it('shows the chooser after a teardown instead of probing, but still claims the secret', async () => {
    sessionStorage.setItem('vessel:device:landing', 'sign-in')
    mount()
    expect(await screen.findByText('482 913')).toBeVisible()
    expect(sent('/api/auth/session')).toHaveLength(0)
  })
})

describe('a pairing link opened on a device that is already signed in', () => {
  it('pairs nothing, removes the fragment and says so', async () => {
    routes['/api/auth/session'] = () => json(sessionFixture)
    const { user } = mount()
    expect(window.location.hash).toBe('')
    expect(await screen.findByText('This device is already signed in')).toBeVisible()
    expect(sent('/api/auth/pairings/claim')).toHaveLength(0)
    expect(sent('/api/auth/pairings/complete')).toHaveLength(0)
    expect(sent('/api/system')).toHaveLength(0)
    await user.click(screen.getByRole('button', { name: 'Open Vessel' }))
    expect(await screen.findByText(`Shell for ${sessionFixture.user.displayName}`)).toBeVisible()
    await waitFor(() => expect(sent('/api/system')).toHaveLength(1))
  })

  it('without a fragment opens the workspace directly, as before', async () => {
    window.history.replaceState(null, '', '/')
    routes['/api/auth/session'] = () => json(sessionFixture)
    mount()
    expect(await screen.findByText(`Shell for ${sessionFixture.user.displayName}`)).toBeVisible()
    expect(screen.queryByText('This device is already signed in')).not.toBeInTheDocument()
  })
})

describe('without a fragment', () => {
  it('shows the wallet chooser and starts no pairing', async () => {
    window.history.replaceState(null, '', '/')
    mount()
    expect(await screen.findByRole('heading', { name: 'Sign in to Vessel' })).toBeVisible()
    expect(sent('/api/auth/pairings/claim')).toHaveLength(0)
  })

  it('leaves an unrelated fragment alone', async () => {
    window.history.replaceState(null, '', '/#plays')
    mount()
    await screen.findByRole('heading', { name: 'Sign in to Vessel' })
    expect(window.location.hash).toBe('#plays')
  })
})

describe('the pairing link the desktop shows', () => {
  it('has the shape the gate reads', () => {
    expect(pairingUrl('https://vessel.test', secret)).toBe(`https://vessel.test/#pair=${secret}`)
  })
})
