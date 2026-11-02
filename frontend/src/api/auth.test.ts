import { describe, expect, it, vi } from 'vitest'
import { ApiError } from './errors'
import { onApiEvent } from './events'
import { authErrorMessage, challengeAddress, createPublicAuthApi, createSessionAuthApi, isSession, pairingCodeMessage } from './auth'
import { createApiCall } from './request'
import { createWorkspaceApi, endsSession } from './workspace'
import { getSystem } from './system'
import { systemFixture } from '@/test/system-fixture'
import { challengeFixture as challengeFixtureFor, sessionFixture, walletFixture } from '@/test/identity-fixture'

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
      'request-too-large', 'rate-limited', 'wallet-limit', 'identity-unavailable',
      'pairing-invalid', 'pairing-expired', 'pairing-code-invalid', 'pairing-contested']) expect(authErrorMessage(code), code).toBeTruthy()
    expect(authErrorMessage('toString')).toBeUndefined()
  })
})

const pairingId = '7c1e9b52-4d8a-4f36-a0e7-2b5d9c3f1a84'
const secret = 'A'.repeat(43)
const created = { id: pairingId, secret, typedCode: '7KQ2M-HX9PD', expiresAt: '2026-10-27T10:02:00.000Z' }
const ok = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

describe('device pairing', () => {
  const session = () => createSessionAuthApi({ userId, sessionId })

  it('creates a pairing with a bodyless session POST and validates the answer', async () => {
    const fetch = vi.fn().mockResolvedValue(ok(created, 201))
    vi.stubGlobal('fetch', fetch)
    await expect(session().createPairing()).resolves.toEqual(created)
    const [path, init] = fetch.mock.calls[0] as [string, RequestInit]
    expect(path).toBe('/api/auth/pairings')
    expect(init.method).toBe('POST')
    expect(init.body).toBeUndefined()
    expect((init.headers as Record<string, string>)['X-Vessel-User']).toBe(userId)
  })

  it.each([
    ['a short secret', { ...created, secret: 'AAAA' }],
    ['a secret with padding', { ...created, secret: `${'A'.repeat(42)}=` }],
    ['a typed code without its dash', { ...created, typedCode: '7KQ2MHX9PD' }],
    ['an invalid identifier', { ...created, id: 'nope' }],
    ['a missing deadline', { id: created.id, secret, typedCode: created.typedCode }],
  ])('refuses a created pairing with %s', async (_label, body) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(ok(body, 201)))
    await expect(session().createPairing()).rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it('maps a missing grant to the step-up copy', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problem(403, 'step-up-required')))
    await expect(session().createPairing()).rejects.toMatchObject({ status: 403, code: 'step-up-required' })
  })

  it('requests a device.pair step-up challenge without a target', async () => {
    const fetch = vi.fn().mockResolvedValue(ok(challengeFixtureFor()))
    vi.stubGlobal('fetch', fetch)
    await session().stepUpChallenge({ action: 'device.pair', address: walletFixture.address })
    const [path, init] = fetch.mock.calls[0] as [string, RequestInit]
    expect(path).toBe('/api/auth/step-up/challenge')
    expect(JSON.parse(init.body as string)).toEqual({ action: 'device.pair', address: walletFixture.address })
  })

  it('reads the pairing status for the creating session', async () => {
    const fetch = vi.fn().mockResolvedValue(ok({ status: 'claimed', expiresAt: created.expiresAt, device: 'Safari on iPhone' }))
    vi.stubGlobal('fetch', fetch)
    await expect(session().pairingStatus(pairingId)).resolves.toEqual({ status: 'claimed', expiresAt: created.expiresAt, device: 'Safari on iPhone' })
    expect((fetch.mock.calls[0] as [string])[0]).toBe(`/api/auth/pairings/${pairingId}`)
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(ok({ status: 'pending', expiresAt: created.expiresAt, device: null })))
    await expect(session().pairingStatus(pairingId)).resolves.toMatchObject({ device: null })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(ok({ status: 'weird', expiresAt: created.expiresAt, device: null })))
    await expect(session().pairingStatus(pairingId)).rejects.toMatchObject({ kind: 'invalid-response' })
    expect(() => session().pairingStatus('not-a-guid')).toThrow(ApiError)
  })

  it('approves with the typed confirmation code and cancels with a bodyless DELETE', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetch)
    await session().approvePairing(pairingId, '482913')
    await session().cancelPairing(pairingId)
    const [approve, cancel] = fetch.mock.calls as [[string, RequestInit], [string, RequestInit]]
    expect(approve[0]).toBe(`/api/auth/pairings/${pairingId}/approve`)
    expect(JSON.parse(approve[1].body as string)).toEqual({ code: '482913' })
    expect(cancel[0]).toBe(`/api/auth/pairings/${pairingId}`)
    expect(cancel[1].method).toBe('DELETE')
    expect(cancel[1].body).toBeUndefined()
  })

  it('carries attemptsLeft on a wrong code and words it', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: 'pairing-code-invalid', attemptsLeft: 2 }),
      { status: 400, headers: { 'Content-Type': 'application/problem+json' } })))
    const error = await session().approvePairing(pairingId, '000000').catch((cause: unknown) => cause) as ApiError
    expect(error).toMatchObject({ status: 400, code: 'pairing-code-invalid', attemptsLeft: 2 })
    expect(pairingCodeMessage(error)).toBe('That confirmation code is not right. 2 attempts left.')
    expect(pairingCodeMessage(new ApiError('http', 'x', 400, undefined, 'pairing-code-invalid', 1))).toContain('1 attempt left.')
    expect(pairingCodeMessage(new ApiError('http', 'x', 400, undefined, 'pairing-code-invalid', 0))).toContain('cancelled')
    expect(pairingCodeMessage(new ApiError('http', 'x', 400, undefined, 'pairing-code-invalid'))).toBe('That confirmation code is not right.')
  })

  it('ignores an implausible attemptsLeft', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: 'pairing-code-invalid', attemptsLeft: 'many' }), { status: 400 })))
    const error = await session().approvePairing(pairingId, '000000').catch((cause: unknown) => cause) as ApiError
    expect(error.attemptsLeft).toBeUndefined()
  })

  it('claims with exactly one of secret or typedCode through the public kind', async () => {
    const claim = { code: '482913', expiresAt: created.expiresAt, journal: { displayName: 'Sam Rivera', wallet: walletFixture.address } }
    const fetch = vi.fn().mockImplementation(async () => ok(claim))
    vi.stubGlobal('fetch', fetch)
    await expect(createPublicAuthApi().claimPairing({ secret })).resolves.toEqual(claim)
    await createPublicAuthApi().claimPairing({ typedCode: '7kq2m hx9pd' })
    const [first, second] = fetch.mock.calls as [[string, RequestInit], [string, RequestInit]]
    expect(first[0]).toBe('/api/auth/pairings/claim')
    expect(JSON.parse(first[1].body as string)).toEqual({ secret })
    expect(JSON.parse(second[1].body as string)).toEqual({ typedCode: '7kq2m hx9pd' })
    expect(first[1].headers).toEqual({ 'X-Vessel-Request': '1', Accept: 'application/json', 'Content-Type': 'application/json' })
    expect(first[1].credentials).toBe('same-origin')
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(ok({ ...claim, code: '12345' })))
    await expect(createPublicAuthApi().claimPairing({ secret })).rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it.each([
    ['pairing-invalid', 400], ['pairing-expired', 400], ['pairing-contested', 409],
  ])('shows Vessel copy for a %s claim, never the server text', async (code, status) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problem(status, code)))
    const error = await createPublicAuthApi().claimPairing({ secret }).catch((cause: unknown) => cause) as ApiError
    expect(error.code).toBe(code)
    expect(error.message).toBe(authErrorMessage(code))
    expect(error.message).not.toContain('private')
  })

  it('validates completion answers', async () => {
    const complete = (body: unknown) => {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(ok(body)))
      return createPublicAuthApi().completePairing({ remember: true })
    }
    await expect(complete({ status: 'waiting' })).resolves.toEqual({ status: 'waiting' })
    await expect(complete({ status: 'signed-in', session: sessionFixture })).resolves.toMatchObject({ status: 'signed-in' })
    for (const status of ['expired', 'cancelled', 'contested']) await expect(complete({ status })).resolves.toEqual({ status })
    await expect(complete({ status: 'signed-in' })).rejects.toMatchObject({ kind: 'invalid-response' })
    await expect(complete({ status: 'approved' })).rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it('posts remember with the completion poll', async () => {
    const fetch = vi.fn().mockResolvedValue(ok({ status: 'waiting' }))
    vi.stubGlobal('fetch', fetch)
    await createPublicAuthApi().completePairing({ remember: false })
    const [path, init] = fetch.mock.calls[0] as [string, RequestInit]
    expect(path).toBe('/api/auth/pairings/complete')
    expect(JSON.parse(init.body as string)).toEqual({ remember: false })
  })

  it('requires a source on every session row', async () => {
    const row = { id: sessionId, current: true, createdAt: '2026-10-17T09:41:12.000Z', lastSeenAt: '2026-10-17T09:41:12.000Z', remember: false, userAgent: null, walletAddress: null }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(ok([{ ...row, source: 'pairing' }])))
    await expect(session().sessions()).resolves.toHaveLength(1)
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(ok([row])))
    await expect(session().sessions()).rejects.toMatchObject({ kind: 'invalid-response' })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(ok([{ ...row, source: 'other' }])))
    await expect(session().sessions()).rejects.toMatchObject({ kind: 'invalid-response' })
  })
})
