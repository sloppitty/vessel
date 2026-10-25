import { afterEach, describe, expect, it, vi } from 'vitest'
import { onApiEvent } from './events'
import { createApiCall } from './request'
import { createWorkspaceApi } from './workspace'
import { getSystem } from './system'
import { accountFixture, emptyOverview, savedPlayFixture } from '@/test/workspace-fixture'
import { systemFixture } from '@/test/system-fixture'
import { credentialsFixture } from '@/test/identity-fixture'

const isString = (value: unknown): value is string => typeof value === 'string'
const query = { instrument: 'BTC', interval: '1h' as const }

afterEach(() => vi.useRealTimers())

describe('shared API request pipeline', () => {
  it.each([200, 201, 202, 206, 299])('accepts HTTP %i and guards JSON without changing exact strings', async status => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify('1.0000000000000000000000000001'), { status })))
    expect(await createApiCall(credentialsFixture)('/api/example', { timeout: 10_000, validate: isString })).toBe('1.0000000000000000000000000001')
  })

  it('guards no-content responses rather than assuming every endpoint permits them', async () => {
    const fetch = vi.fn().mockImplementation(async () => new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetch)
    const api = createWorkspaceApi(credentialsFixture)
    await expect(api.deletePlay(savedPlayFixture.summary.id)).resolves.toBeUndefined()
    await expect(api.overview()).rejects.toThrow('The API returned no workspace data.')
    await expect(api.play(savedPlayFixture.summary.id)).rejects.toThrow('The API returned no play data.')
    await expect(getSystem(credentialsFixture)).rejects.toThrow('The API returned an invalid system response.')
  })

  it.each([
    ['workspace', 'This account or portfolio is no longer available.'],
    ['play', 'This play or image is no longer available.'],
    ['system', 'The API request failed with HTTP 404.'],
  ])('keeps the %s not-found message', async (area, message) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('private proxy detail', { status: 404 })))
    const api = createWorkspaceApi(credentialsFixture)
    const request = area === 'workspace' ? api.overview() : area === 'play' ? api.play(savedPlayFixture.summary.id) : getSystem(credentialsFixture)
    await expect(request).rejects.toThrow(message)
  })

  it.each(['invalid JSON', 'incompatible contract'])('keeps endpoint-specific messages for %s', async kind => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(async () => new Response(kind === 'invalid JSON' ? '{' : '{}')))
    const api = createWorkspaceApi(credentialsFixture)
    await expect(api.overview()).rejects.toThrow(kind === 'invalid JSON' ? 'The API returned invalid data.' : 'The API returned incompatible workspace data.')
    await expect(api.play(savedPlayFixture.summary.id)).rejects.toThrow(kind === 'invalid JSON' ? 'The API returned invalid data.' : 'The API returned incompatible play data.')
    await expect(getSystem(credentialsFixture)).rejects.toThrow(kind === 'invalid JSON' ? 'The API returned an invalid system response.' : 'The API returned an incompatible system contract.')
  })

  it.each([
    [401, 'private response'],
    [403, JSON.stringify({ code: 'user-suspended' })],
  ])('emits one unauthorized event for every response mode at HTTP %i', async (status, body) => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(async () => new Response(body, { status })))
    const api = createWorkspaceApi(credentialsFixture)
    const listener = vi.fn()
    const unsubscribe = onApiEvent(api, listener)
    for (const request of [() => api.overview(), () => api.play(savedPlayFixture.summary.id),
      () => api.evidenceImage(savedPlayFixture.summary.id),
      () => api.marketStream(accountFixture.id, query, new AbortController().signal, vi.fn()),
      () => api.monitorStream('0', new AbortController().signal, vi.fn()),
      () => api.accountCredential(accountFixture.id)]) {
      await expect(request()).rejects.toThrow()
    }
    expect(listener.mock.calls).toEqual(Array.from({ length: 6 }, () => [{ type: 'unauthorized' }]))
    unsubscribe()
  })

  it('does not parse or echo credential failure bodies, including correlation IDs', async () => {
    const response = new Response('{"detail":"ro:private"}', { status: 502, headers: { 'X-Correlation-ID': 'a'.repeat(32) } })
    const json = vi.spyOn(response, 'json')
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response))
    await expect(createWorkspaceApi(credentialsFixture).accountCredential(accountFixture.id)).rejects.toThrow(
      'Credential status is unavailable. Retry or ask the operator to check credential storage.')
    expect(json).not.toHaveBeenCalled()
  })

  it('keeps multipart boundaries native and downloads image bytes without JSON guards', async () => {
    const evidence = { id: savedPlayFixture.summary.id, playId: savedPlayFixture.summary.id, source: 'upload', note: '', contentType: 'image/png',
      sizeBytes: 3, sha256: 'a'.repeat(64), createdAtUtc: '2026-10-14T00:00:00Z', updatedAtUtc: '2026-10-14T00:00:00Z', markup: null }
    const fetch = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(evidence)))
      .mockResolvedValueOnce(new Response('png', { headers: { 'Content-Type': 'image/png' } }))
    vi.stubGlobal('fetch', fetch)
    const api = createWorkspaceApi(credentialsFixture)
    await expect(api.uploadEvidence(savedPlayFixture.summary.id, new Blob(['png']), { source: 'upload', name: 'fixture.png', note: '', markup: null })).resolves.toEqual(evidence)
    expect(fetch.mock.calls[0]![1]).toMatchObject({ method: 'POST', body: expect.any(FormData),
      credentials: 'same-origin', referrerPolicy: 'same-origin', cache: 'no-store', redirect: 'error',
      headers: { 'X-Vessel-User': credentialsFixture.userId, Accept: 'application/json' } })
    expect(fetch.mock.calls[0]![1].headers).not.toHaveProperty('Content-Type')
    expect(await (await api.evidenceImage(evidence.id)).text()).toBe('png')
    expect(fetch.mock.calls[1]![1].headers).toEqual({ 'X-Vessel-User': credentialsFixture.userId, Accept: 'image/*' })
  })

  it('retains endpoint timeout budgets and clears completed request timers', async () => {
    vi.useFakeTimers()
    const timeout = vi.spyOn(globalThis, 'setTimeout')
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(systemFixture)))
      .mockResolvedValueOnce(new Response(JSON.stringify(emptyOverview)))
      .mockResolvedValueOnce(new Response(JSON.stringify(savedPlayFixture)))
      .mockResolvedValueOnce(new Response('{}'))
      .mockResolvedValueOnce(new Response('{}'))
      .mockResolvedValueOnce(new Response('{}'))
      .mockResolvedValueOnce(new Response('{}'))
      .mockResolvedValueOnce(new Response('image')))
    const api = createWorkspaceApi(credentialsFixture)
    await getSystem(credentialsFixture)
    await api.overview()
    await api.play(savedPlayFixture.summary.id)
    await expect(api.candles(accountFixture.id, query)).rejects.toThrow()
    await expect(api.sync(accountFixture.id)).rejects.toThrow()
    await expect(api.runAccountMonitor(accountFixture.id)).rejects.toThrow()
    await expect(api.discoverAccounts('lighter', `0x${'a'.repeat(40)}`)).rejects.toThrow()
    await api.evidenceImage(savedPlayFixture.summary.id)
    expect(timeout.mock.calls.map(call => call[1])).toEqual([8_000, 10_000, 15_000, 25_000, 90_000, 10_000, 30_000, 60_000])
    expect(vi.getTimerCount()).toBe(0)
  })

  it('allows sync to finish its sequential venue reads', async () => {
    vi.useFakeTimers()
    let finish!: (response: Response) => void
    let signal!: AbortSignal
    vi.stubGlobal('fetch', vi.fn((_path: string, init: RequestInit) => new Promise<Response>((resolve, reject) => {
      finish = resolve
      signal = init.signal!
      signal.addEventListener('abort', () => reject(new Error('private transport detail')), { once: true })
    })))
    const api = createWorkspaceApi(credentialsFixture)
    const result = api.sync(accountFixture.id).then(value => ({ value }), error => ({ error }))
    await vi.advanceTimersByTimeAsync(65_000)
    expect(signal.aborted).toBe(false)
    finish(new Response(JSON.stringify(accountFixture)))
    expect(await result).toEqual({ value: accountFixture })
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(['caller', 'timeout'])('aborts a pending fetch on %s cancellation without leaking transport errors', async reason => {
    vi.useFakeTimers()
    vi.stubGlobal('fetch', vi.fn((_path: string, init: RequestInit) => new Promise<Response>((_resolve, reject) => {
      init.signal!.addEventListener('abort', () => reject(new Error('private transport detail')), { once: true })
    })))
    const controller = new AbortController()
    const request = createWorkspaceApi(credentialsFixture).overview(controller.signal)
    const assertion = expect(request).rejects.toMatchObject({ kind: 'unavailable',
      message: 'The request did not complete. Check the API and try again.' })
    if (reason === 'caller') controller.abort()
    else await vi.advanceTimersByTimeAsync(10_000)
    await assertion
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(['market', 'monitor'] as const)('does not abort an open %s SSE stream after its header timeout', async kind => {
    vi.useFakeTimers()
    const stream = new ReadableStream<Uint8Array>({ start() { /* Stay open until the caller aborts. */ } })
    const fetch = vi.fn().mockResolvedValue(new Response(stream, { headers: { 'Content-Type': 'text/event-stream' } }))
    vi.stubGlobal('fetch', fetch)
    const controller = new AbortController()
    const api = createWorkspaceApi(credentialsFixture)
    const request = kind === 'market' ? api.marketStream(accountFixture.id, query, controller.signal, vi.fn())
      : api.monitorStream('0', controller.signal, vi.fn())
    await vi.advanceTimersByTimeAsync(25_001)
    expect((fetch.mock.calls[0]![1].signal as AbortSignal).aborted).toBe(false)
    expect(vi.getTimerCount()).toBe(0)
    controller.abort()
    await expect(request).resolves.toBeUndefined()
  })
})
