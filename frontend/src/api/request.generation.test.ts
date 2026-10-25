import { describe, expect, it, vi } from 'vitest'
import { onApiEvent, type ApiEvent } from './events'
import { createSessionAuthApi } from './auth'
import { createApiCall, sessionChangedRead, sessionChangedWrite, type AuthFailure } from './request'
import { createWorkspaceApi } from './workspace'
import { candleSeriesFixture } from '@/test/workspace-fixture'
import { sessionFixture } from '@/test/identity-fixture'
import { eventCursor, monitorEventFixture } from '@/test/monitor-fixture'
import { notificationCursor, notificationHeadFixture, notificationWith } from '@/test/notification-fixture'

const userId = sessionFixture.user.id
const sessionId = sessionFixture.session.id
const accountId = '5c5e3f43-2aa4-4b7a-9d1e-0e6b0d3c5a11'
const encoder = new TextEncoder()
const isText = (value: unknown): value is string => typeof value === 'string'
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
const problem = (status: number, code: string) => json({ code }, status)
const frame = (event: string, data: unknown) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`

/** The tab's generation, as `SessionController` exposes it; `advance` is another tab adopting a new session. */
function generation() {
  let value = 0
  const listeners = new Set<() => void>()
  return {
    current: () => value,
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener) } },
    advance: () => { value++; for (const listener of [...listeners]) listener() },
    listeners: () => listeners.size,
    ended: new AbortController().signal,
  }
}

/** An SSE response that stays open: `push` sends text, and an abort of the request ends the body like the browser. */
function liveStream(signal: AbortSignal | null | undefined) {
  let controller!: ReadableStreamDefaultController<Uint8Array>
  const body = new ReadableStream<Uint8Array>({ start(value) { controller = value } })
  signal?.addEventListener('abort', () => { try { controller.error(new DOMException('aborted', 'AbortError')) } catch { /* closed */ } }, { once: true })
  return { response: new Response(body, { headers: { 'Content-Type': 'text/event-stream' } }), push: (text: string) => controller.enqueue(encoder.encode(text)) }
}

describe('session generation', () => {
  it('drops a successful answer sent under an older generation and reads once more under the current one', async () => {
    const tab = generation()
    const fetch = vi.fn()
      .mockImplementationOnce(async () => { tab.advance(); return json('from S1') })
      .mockImplementationOnce(async () => json('from S2'))
    vi.stubGlobal('fetch', fetch)
    const call = createApiCall({ kind: 'session', userId, sessionId, generation: tab })
    await expect(call('/api/example', { timeout: 1_000, validate: isText })).resolves.toBe('from S2')
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('checks the generation after the body is decoded, not only after the headers', async () => {
    const tab = generation()
    const body = new ReadableStream<Uint8Array>({ pull(controller) { tab.advance(); controller.enqueue(encoder.encode('"from S1"')); controller.close() } })
    const fetch = vi.fn().mockResolvedValueOnce(new Response(body)).mockResolvedValueOnce(json('from S2'))
    vi.stubGlobal('fetch', fetch)
    const call = createApiCall({ kind: 'session', userId, sessionId, generation: tab })
    await expect(call('/api/example', { timeout: 1_000, validate: isText })).resolves.toBe('from S2')
  })

  it('never reports a stale 401 or emits unauthorized, so the current candle cache survives', async () => {
    const tab = generation()
    const failures: AuthFailure[] = []
    const events: ApiEvent[] = []
    vi.stubGlobal('fetch', vi.fn()
      .mockImplementationOnce(async () => { tab.advance(); return problem(401, 'session-revoked') })
      .mockImplementationOnce(async () => json({ portfolios: [] }, 500)))
    const api = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab }, failure => failures.push(failure))
    onApiEvent(api, event => events.push(event))
    await expect(api.accounts()).rejects.toMatchObject({ status: 500 })
    expect(failures).toEqual([])
    expect(events).toEqual([])
  })

  it('reports a current 401 with the generation it was sent under', async () => {
    const tab = generation()
    tab.advance()
    const failures: AuthFailure[] = []
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problem(401, 'session-revoked')))
    const api = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab }, failure => failures.push(failure))
    await expect(api.accounts()).rejects.toMatchObject({ status: 401 })
    expect(failures).toEqual([{ status: 401, code: 'session-revoked', generation: 1 }])
  })

  it('reads at most twice; a second stale answer is a neutral session change', async () => {
    const tab = generation()
    const failures: AuthFailure[] = []
    const fetch = vi.fn().mockImplementation(async () => { tab.advance(); return problem(401, 'session-revoked') })
    vi.stubGlobal('fetch', fetch)
    const call = createApiCall({ kind: 'session', userId, sessionId, generation: tab }, failure => failures.push(failure))
    await expect(call('/api/example', { timeout: 1_000, validate: isText })).rejects.toMatchObject({ code: 'session-changed', message: sessionChangedRead })
    expect(fetch).toHaveBeenCalledTimes(2)
    expect(failures).toEqual([])
  })

  it('never resends a write; its dropped answer says it may already be saved and asks the shell to reload', async () => {
    const tab = generation()
    const events: ApiEvent[] = []
    const fetch = vi.fn().mockImplementation(async () => { tab.advance(); return json(null, 500) })
    vi.stubGlobal('fetch', fetch)
    const api = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab })
    onApiEvent(api, event => events.push(event))
    await expect(api.deletePlay('8b7c7a64-34b4-4d61-9b5f-1b0b9d5c1e20')).rejects.toMatchObject({ code: 'session-changed', message: sessionChangedWrite })
    expect(fetch).toHaveBeenCalledOnce()
    expect(events).toEqual([{ type: 'session-changed' }])
  })

  it('applies to the session-bound auth routes too', async () => {
    const tab = generation()
    const fetch = vi.fn().mockImplementationOnce(async () => { tab.advance(); return json([]) }).mockImplementationOnce(async () => json([]))
    vi.stubGlobal('fetch', fetch)
    await expect(createSessionAuthApi({ userId, sessionId, generation: tab }).sessions()).resolves.toEqual([])
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('discards a notification head from an older session, including its 401, and rereads under the current one', async () => {
    const tab = generation(), failure = vi.fn()
    const fetch = vi.fn()
      .mockImplementationOnce(async () => { tab.advance(); return problem(401, 'session-expired') })
      .mockResolvedValueOnce(json({ ...notificationHeadFixture, unreadCount: 2 }))
    vi.stubGlobal('fetch', fetch)
    const api = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab }, failure)
    await expect(api.notificationHead()).resolves.toMatchObject({ unreadCount: 2 })
    expect(fetch).toHaveBeenCalledTimes(2)
    expect(failure).not.toHaveBeenCalled()
  })

  it('never replays a notification write answered under an older session', async () => {
    const tab = generation(), changed = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async () => { tab.advance(); return new Response(null, { status: 202 }) }))
    const api = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab })
    onApiEvent(api, changed)
    await expect(api.requestTestNotification()).rejects.toMatchObject({ code: 'session-changed', message: sessionChangedWrite })
    expect(fetch).toHaveBeenCalledOnce()
    expect(changed).toHaveBeenCalledExactlyOnceWith({ type: 'session-changed' })
  })

  it('keeps the session failure code and its copy on notification calls', async () => {
    const failure = vi.fn()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problem(403, 'user-suspended')))
    const api = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: generation() }, failure)
    await expect(api.notificationHead()).rejects.toMatchObject({
      code: 'user-suspended', message: 'The operator of this server has suspended this journal. Contact them to restore access.',
    })
    expect(failure).toHaveBeenCalledExactlyOnceWith({ status: 403, code: 'user-suspended', generation: 0 })
  })

  it.each(['head', 'claim', 'test'] as const)('keeps a pending notification %s and later calls pending after session end', async method => {
    const ended = new AbortController(), tab = { ...generation(), ended: ended.signal }
    let answer!: (response: Response) => void
    const fetch = vi.fn(() => new Promise<Response>(resolve => { answer = resolve }))
    vi.stubGlobal('fetch', fetch)
    const failure = vi.fn(), settled = vi.fn()
    const api = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab }, failure)
    const run = () => method === 'head' ? api.notificationHead() : method === 'claim' ? api.claimNotifications() : api.requestTestNotification()
    void run().then(settled, settled)
    ended.abort()
    answer(method === 'head' ? json(notificationHeadFixture) : method === 'claim' ? json({ popups: [notificationWith(1)], more: 0 }) : new Response(null, { status: 202 }))
    void run().then(settled, settled)
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(fetch).toHaveBeenCalledOnce()
    expect(settled).not.toHaveBeenCalled()
    expect(failure).not.toHaveBeenCalled()
  })

  it('keeps the call whose 401 ends the session pending, and still rejects a 403 that ends nothing', async () => {
    const ended = new AbortController(), tab = { ...generation(), ended: ended.signal }
    const failure = vi.fn((answer: AuthFailure) => { if (answer.status === 401) ended.abort() })
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(problem(403, 'forbidden'))
      .mockResolvedValueOnce(problem(401, 'session-required')))
    const call = createApiCall({ kind: 'session', userId, sessionId, generation: tab }, failure)
    const validate = (value: unknown): value is undefined => value === undefined
    await expect(call('/api/example', { method: 'DELETE', timeout: 1_000, validate })).rejects.toMatchObject({ status: 403 })
    expect(ended.signal.aborted).toBe(false)
    const settled = vi.fn()
    void call('/api/example', { method: 'DELETE', timeout: 1_000, validate }).then(settled, settled)
    await vi.waitFor(() => expect(failure).toHaveBeenCalledTimes(2))
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(ended.signal.aborted).toBe(true)
    expect(settled).not.toHaveBeenCalled()
  })

  it('leaves calls without a generation exactly as before', async () => {
    const fetch = vi.fn().mockImplementation(async () => json('ok'))
    vi.stubGlobal('fetch', fetch)
    await expect(createApiCall({ kind: 'public' })('/api/example', { timeout: 1_000, validate: isText })).resolves.toBe('ok')
    await expect(createApiCall({ kind: 'session', userId, sessionId })('/api/example', { timeout: 1_000, validate: isText })).resolves.toBe('ok')
    expect(fetch).toHaveBeenCalledTimes(2)
  })
})

describe('generation-bound streams', () => {
  const [candle, later] = candleSeriesFixture.candles
  const query = { instrument: 'BTC', interval: '1h' as const }

  it('observes a late open rejection after the session-end promise wins the race', async () => {
    const ended = new AbortController()
    const tab = { ...generation(), ended: ended.signal }
    let rejectCancel!: (cause: unknown) => void
    const cancel = vi.fn(() => new Promise<void>((_resolve, reject) => { rejectCancel = reject }))
    // Invalid headers start asynchronous cleanup before `open` rejects.
    const body = new ReadableStream<Uint8Array>({ cancel })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(body, { headers: { 'Content-Type': 'text/html' } })))
    const unhandled = vi.fn()
    process.on('unhandledRejection', unhandled)
    try {
      const done = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab })
        .marketStream(accountId, query, new AbortController().signal, vi.fn())
      await vi.waitFor(() => expect(cancel).toHaveBeenCalledOnce())
      ended.abort()
      await expect(done).resolves.toBeUndefined()
      expect(tab.listeners()).toBe(0)
      // Cleanup completes after `over`; the invalid stream now rejects `open`.
      rejectCancel(new Error('late cleanup rejection'))
      await new Promise(resolve => setTimeout(resolve, 20))
      expect(unhandled).not.toHaveBeenCalled()
      expect(fetch).toHaveBeenCalledOnce()
    } finally { process.off('unhandledRejection', unhandled) }
  })

  it('still rejects an open failure that settles before session end', async () => {
    const tab = generation()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { headers: { 'Content-Type': 'text/html' } })))
    await expect(createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab })
      .marketStream(accountId, query, new AbortController().signal, vi.fn()))
      .rejects.toMatchObject({ kind: 'invalid-response', message: 'The API returned an invalid live market stream.' })
    expect(tab.listeners()).toBe(0)
  })

  it('closes the live market reader on a new generation, drops its frames and reopens under the current one', async () => {
    const tab = generation()
    const streams: Array<ReturnType<typeof liveStream>> = []
    vi.stubGlobal('fetch', vi.fn(async (_path: string, init: RequestInit) => {
      const stream = liveStream(init.signal)
      streams.push(stream)
      return stream.response
    }))
    const events: unknown[] = []
    const stop = new AbortController()
    const done = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab }).marketStream(accountId, query, stop.signal, event => events.push(event))
    await vi.waitFor(() => expect(streams).toHaveLength(1))
    streams[0]!.push(frame('candle', candle))
    await vi.waitFor(() => expect(events).toHaveLength(1))
    tab.advance()
    // The old reader is cancelled: a frame it still had in flight never reaches the chart.
    try { streams[0]!.push(frame('candle', later)) } catch { /* the body already ended */ }
    await vi.waitFor(() => expect(streams).toHaveLength(2))
    streams[1]!.push(frame('candle', later))
    await vi.waitFor(() => expect(events).toHaveLength(2))
    expect(events).toEqual([{ type: 'candle', candle }, { type: 'candle', candle: later }])
    stop.abort()
    await expect(done).resolves.toBeUndefined()
    expect(tab.listeners()).toBe(0)
  })

  it('never opens the reader of a stream whose headers arrive after the generation changed', async () => {
    const tab = generation()
    const stale = liveStream(undefined)
    const cancel = vi.spyOn(stale.response.body!, 'cancel')
    const fresh: Array<ReturnType<typeof liveStream>> = []
    vi.stubGlobal('fetch', vi.fn()
      .mockImplementationOnce(async () => { tab.advance(); return stale.response })
      .mockImplementation(async (_path: string, init: RequestInit) => { const stream = liveStream(init.signal); fresh.push(stream); return stream.response }))
    const events: unknown[] = []
    const stop = new AbortController()
    const done = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab }).marketStream(accountId, query, stop.signal, event => events.push(event))
    await vi.waitFor(() => expect(fresh).toHaveLength(1))
    expect(cancel).toHaveBeenCalled()
    fresh[0]!.push(frame('candle', candle))
    await vi.waitFor(() => expect(events).toHaveLength(1))
    stop.abort()
    await done
  })

  it('reopens the multiplexed stream with both independent cursors after a new session generation', async () => {
    const tab = generation()
    const head = `${'a'.repeat(32)}.41`
    const paths: string[] = []
    const streams: Array<ReturnType<typeof liveStream>> = []
    vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit) => {
      paths.push(path)
      const stream = liveStream(init.signal)
      streams.push(stream)
      return stream.response
    }))
    const frames: unknown[] = []
    const stop = new AbortController()
    const done = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab })
      .monitorStream('0', stop.signal, value => frames.push(value), notificationCursor(10))
    await vi.waitFor(() => expect(streams).toHaveLength(1))
    streams[0]!.push(frame('status', { state: 'reset', nextCursor: head }))
    streams[0]!.push(frame('notifications', { state: 'ready', nextCursor: notificationCursor(10), unreadCount: 1, enabled: true }))
    streams[0]!.push(frame('notification', { cursor: notificationCursor(11), unreadCount: 2, enabled: true, notification: notificationWith(1) }))
    streams[0]!.push(`id: ${eventCursor('42')}\n${frame('event', monitorEventFixture)}`)
    await vi.waitFor(() => expect(frames).toHaveLength(4))
    tab.advance()
    try { streams[0]!.push(frame('notification', { cursor: notificationCursor(12), unreadCount: 3, enabled: true, notification: notificationWith(2) })) } catch { /* cancelled */ }
    await vi.waitFor(() => expect(streams).toHaveLength(2))
    const resumed = new URL(paths[1]!, 'https://vessel.test').searchParams
    expect(resumed.get('after')).toBe(eventCursor('42'))
    expect(resumed.get('notificationsAfter')).toBe(notificationCursor(11))
    // A notification reset changes only that feed's cursor, even when its sequence moves backwards.
    streams[1]!.push(frame('notifications', { state: 'reset', nextCursor: notificationCursor(1, 1), unreadCount: 0, enabled: true }))
    await vi.waitFor(() => expect(frames).toHaveLength(5))
    tab.advance()
    await vi.waitFor(() => expect(streams).toHaveLength(3))
    const reset = new URL(paths[2]!, 'https://vessel.test').searchParams
    expect(reset.get('after')).toBe(eventCursor('42'))
    expect(reset.get('notificationsAfter')).toBe(notificationCursor(1, 1))
    expect(frames).toHaveLength(5) // No queued notification from the old generation.
    for (const [, init] of vi.mocked(fetch).mock.calls) {
      expect(init).toMatchObject({ credentials: 'same-origin', referrerPolicy: 'same-origin', headers: { 'X-Vessel-User': userId } })
    }
    stop.abort()
    await done
  })

  it.each(['headers', 'body'] as const)('settles the notification stream quietly when the session ends during %s and never reopens', async phase => {
    const ended = new AbortController(), tab = { ...generation(), ended: ended.signal }
    const stream = liveStream(undefined), onFrame = vi.fn()
    vi.stubGlobal('fetch', vi.fn((_path: string, init: RequestInit) => phase === 'body' ? Promise.resolve(stream.response)
      : new Promise<Response>((_resolve, reject) => init.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true }))))
    const api = createWorkspaceApi({ kind: 'session', userId, sessionId, generation: tab })
    const done = api.monitorStream(eventCursor('1'), new AbortController().signal, onFrame, notificationCursor(1))
    await vi.waitFor(() => expect(tab.listeners()).toBe(1))
    if (phase === 'body') {
      stream.push(frame('notifications', { state: 'ready', nextCursor: notificationCursor(1), unreadCount: 0, enabled: true }))
      await vi.waitFor(() => expect(onFrame).toHaveBeenCalledOnce())
    }
    ended.abort()
    await expect(done).resolves.toBeUndefined()
    onFrame.mockClear()
    try { stream.push(frame('notification', { cursor: notificationCursor(2), unreadCount: 1, enabled: true, notification: notificationWith(1) })) } catch { /* cancelled */ }
    tab.advance()
    await api.monitorStream(eventCursor('1'), new AbortController().signal, onFrame, notificationCursor(1))
    expect(fetch).toHaveBeenCalledOnce()
    expect(onFrame).not.toHaveBeenCalled()
    expect(tab.listeners()).toBe(0)
  })
})
