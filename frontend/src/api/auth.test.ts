import { describe, expect, it, vi } from 'vitest'
import { ApiError } from './errors'
import { onApiEvent } from './events'
import { authErrorMessage, challengeAddress, createPublicAuthApi, createSessionAuthApi, isSession } from './auth'
import { createApiCall } from './request'
import { createWorkspaceApi, endsSession } from './workspace'
import { getSystem } from './system'
import { systemFixture } from '@/test/system-fixture'
import { sessionFixture, walletFixture } from '@/test/identity-fixture'

const isString = (value: unknown): value is string => typeof value === 'string'
const userId = sessionFixture.user.id
const sessionId = sessionFixture.session.id
const problem = (status: number, code: string, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify({ title: 'private title', detail: 'private detail', code }), { status, headers: { 'Content-Type': 'application/problem+json', ...headers } })

function lastInit(fetch: ReturnType<typeof vi.fn>) {
  const [, init] = fetch.mock.calls.at(-1) as [string, RequestInit]
  return { ...init, headers: init.headers as Record<string, string> }
}

describe('transport kinds', () => {
  it('sends the session cookie, the tab binding and a real Origin for the session kind', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response('"ok"'))
    vi.stubGlobal('fetch', fetch)
    await createApiCall({ kind: 'session', userId, sessionId })('/api/example', { timeout: 1_000, validate: isString, method: 'POST', body: '{}' })
    const init = lastInit(fetch)
    expect(init.credentials).toBe('same-origin')
    expect(init.referrerPolicy).toBe('same-origin')
    expect(init.headers).toEqual({ 'X-Vessel-User': userId, Accept: 'application/json', 'Content-Type': 'application/json' })
    expect(init.headers).not.toHaveProperty('Authorization')
  })

  it('marks public auth calls with X-Vessel-Request and no user', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ registration: 'closed', originAccepted: true })))
    vi.stubGlobal('fetch', fetch)
    await expect(createPublicAuthApi().config('http://vessel.test:5180')).resolves.toEqual({ registration: 'closed', originAccepted: true })
    const [path] = fetch.mock.calls[0] as [string]
    expect(path).toBe('/api/auth/config?origin=http%3A%2F%2Fvessel.test%3A5180')
    const init = lastInit(fetch)
    expect(init.credentials).toBe('same-origin')
    expect(init.referrerPolicy).toBe('same-origin')
    expect(init.headers).toEqual({ 'X-Vessel-Request': '1', Accept: 'application/json' })
  })

  it('refuses a configuration without a registration policy, such as the removed Token mode answer', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ mode: 'token' }))))
    await expect(createPublicAuthApi().config('http://vessel.test:5180')).rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it('applies the session kind to stream headers and the system read', async () => {
    const fetch = vi.fn().mockImplementation(async (path: string) => path === '/api/system'
      ? new Response(JSON.stringify(systemFixture)) : new Response('', { headers: { 'Content-Type': 'text/event-stream' } }))
    vi.stubGlobal('fetch', fetch)
    await getSystem({ kind: 'session', userId, sessionId })
    expect(lastInit(fetch).headers['X-Vessel-User']).toBe(userId)
    const api = createWorkspaceApi({ kind: 'session', userId, sessionId })
    await api.marketStream('5c5e3f43-2aa4-4b7a-9d1e-0e6b0d3c5a11', { instrument: 'BTC', interval: '1h' }, new AbortController().signal, () => {})
    const init = lastInit(fetch)
    expect(init.headers).toMatchObject({ 'X-Vessel-User': userId, Accept: 'text/event-stream' })
    expect(init.credentials).toBe('same-origin')
    expect(init.referrerPolicy).toBe('same-origin')
  })

  it('refuses a session kind without a user ID before any request', () => {
    expect(() => createApiCall({ kind: 'session', userId: 'not-a-guid', sessionId })).toThrow(ApiError)
  })
})

describe('auth failures', () => {
  it('reports the code and the generation read when the request started', async () => {
    const generation = { current: () => 3, subscribe: () => () => {}, ended: new AbortController().signal }
    const failures: unknown[] = []
    vi.stubGlobal('fetch', vi.fn().mockImplementation(async () => problem(401, 'session-revoked')))
    const call = createApiCall({ kind: 'session', userId, sessionId, generation }, failure => failures.push(failure))
    await expect(call('/api/example', { timeout: 1_000, validate: isString, codeMessage: authErrorMessage }))
      .rejects.toMatchObject({ kind: 'unauthorized', status: 401, code: 'session-revoked', message: 'Your session was signed out. Sign in again.' })
    expect(failures).toEqual([{ status: 401, code: 'session-revoked', generation: 3 }])
  })

  it('never shows the server text and keeps Retry-After for rate limits', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problem(429, 'rate-limited', { 'Retry-After': '30' })))
    const error = await createPublicAuthApi().challenge(walletFixture.address).catch((cause: unknown) => cause) as ApiError
    expect(error.message).toBe('Too many attempts. Wait a moment before trying again.')
    expect(error.code).toBe('rate-limited')
    expect(Date.parse(error.retryAtUtc!) - Date.now()).toBeGreaterThan(25_000)
  })

  it('uses the identity copy for 503 identity-unavailable', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problem(503, 'identity-unavailable')))
    await expect(createPublicAuthApi().session()).rejects.toThrow('Sign-in is unavailable right now. Try again shortly.')
  })

  it.each([
    [401, 'session-expired', true],
    [401, undefined, true],
    [403, 'user-suspended', true],
    [403, 'step-up-required', false],
    [403, 'csrf-rejected', false],
    [403, 'origin-rejected', false],
    [403, undefined, false],
  ] as const)('a session %i %s ends the session: %s', async (status, code, ends) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(code ? problem(status, code) : new Response('private', { status })))
    const credentials = { kind: 'session', userId, sessionId } as const
    const failures: unknown[] = []
    const api = createWorkspaceApi(credentials, failure => failures.push(failure))
    const events: unknown[] = []
    onApiEvent(api, event => events.push(event))
    const error = await api.overview().catch((cause: unknown) => cause) as ApiError
    expect(endsSession({ status, ...code ? { code } : {} })).toBe(ends)
    expect(events).toEqual(ends ? [{ type: 'unauthorized' }] : [])
    expect(failures).toHaveLength(1)
    expect(error.message).not.toContain('private')
    if (code === 'step-up-required') expect(error.message).toBe('Confirm with a wallet already linked to this journal to continue.')
  })
})

describe('auth endpoints', () => {
  it('validates the session DTO strictly', () => {
    expect(isSession(sessionFixture)).toBe(true)
    expect(isSession({ ...sessionFixture, user: { ...sessionFixture.user, role: 'admin' } })).toBe(false)
    expect(isSession({ ...sessionFixture, wallet: { ...sessionFixture.wallet, address: '0x12' } })).toBe(false)
    expect(isSession({ ...sessionFixture, user: { ...sessionFixture.user, displayName: 'bad\u0007name' } })).toBe(false)
  })

  it('refuses an invalid address before sending a challenge', async () => {
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    await expect(createPublicAuthApi().challenge('0x1234')).rejects.toMatchObject({ code: 'address-invalid' })
    expect(fetch).not.toHaveBeenCalled()
  })

  it('reads the address line of the server message', () => {
    expect(challengeAddress(`vessel.test wants you to sign in with your Ethereum account:\n${walletFixture.address}\n\nSign in to Vessel.`)).toBe(walletFixture.address)
    expect(challengeAddress('nothing here')).toBeNull()
  })

  it('posts sign-out with the expected session and maps session-changed', async () => {
    const fetch = vi.fn().mockResolvedValue(problem(409, 'session-changed'))
    vi.stubGlobal('fetch', fetch)
    await expect(createSessionAuthApi({ userId, sessionId }).signOut(sessionId)).rejects.toMatchObject({ status: 409, code: 'session-changed' })
    const [path, init] = fetch.mock.calls[0] as [string, RequestInit]
    expect(path).toBe('/api/auth/sign-out')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ expectedSessionId: sessionId })
  })

  it('has copy for every contract error code', () => {
    for (const code of ['request-invalid', 'address-invalid', 'challenge-invalid', 'challenge-expired', 'signature-invalid', 'signature-unsupported',
      'signature-mismatch', 'registration-invalid', 'display-name-invalid', 'label-invalid', 'session-required', 'session-expired', 'session-revoked',
      'session-user-mismatch', 'origin-rejected', 'csrf-rejected', 'registration-closed', 'invitation-required', 'invitation-invalid', 'user-suspended',
      'step-up-required', 'operator-required', 'not-found', 'wallet-taken', 'wallet-already-linked', 'last-wallet', 'revision-conflict', 'session-changed',
      'request-too-large', 'rate-limited', 'wallet-limit', 'identity-unavailable']) expect(authErrorMessage(code), code).toBeTruthy()
    expect(authErrorMessage('toString')).toBeUndefined()
  })
})
