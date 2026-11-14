import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { MonitorStreamEvent } from '@/api/monitor'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import { eventCursor, eventFrame, monitorEventFixture, monitorSummaryFixture } from '@/test/monitor-fixture'
import { ApiError } from '@/api/errors'
import { notificationCursor, notificationFixture, notificationHeadFixture } from '@/test/notification-fixture'
import { sessionFixture } from '@/test/identity-fixture'
import { channelBus } from '@/test/session-channel'
import { SessionController } from '@/features/identity/session'
import { reconnectDelay, stableStream, useMonitorEvents, type NotificationStreamHandlers } from './useMonitorEvents'

/** The head the client reads before any stored state; the fixture event lies after it. */
const headSequence = eventCursor('9007199254740990')
function client() {
  const connections: { after: string; signal: AbortSignal; emit: (frame: MonitorStreamEvent) => void; finish: () => void; notificationsAfter: string | undefined }[] = []
  const api = {
    monitorEvents: vi.fn().mockResolvedValue({ events: [], nextCursor: headSequence, resetRequired: true }),
    monitor: vi.fn().mockResolvedValue(monitorSummaryFixture),
    monitorStream: vi.fn((after: string, signal: AbortSignal, emit: (frame: MonitorStreamEvent) => void, notificationsAfter?: string) => new Promise<void>(resolve => {
      connections.push({ after, signal, emit, finish: resolve, notificationsAfter })
      signal.addEventListener('abort', () => resolve(), { once: true })
    })),
    runAccountMonitor: vi.fn(), sync: vi.fn(),
    notificationHead: vi.fn().mockResolvedValue(notificationHeadFixture),
  } as unknown as WorkspaceApi
  return { api, connections }
}
beforeEach(() => vi.useFakeTimers())
afterEach(() => vi.useRealTimers())
const advance = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })
const ready = (nextCursor = headSequence) => ({ type: 'status', status: { state: 'ready', nextCursor } }) as const

it('does not regress the unread count when a head read loses to a same-generation frame', async () => {
  const { api, connections } = client()
  const feed = { onHead: vi.fn(), onStatus: vi.fn(), onFrame: vi.fn() }
  renderHook(() => useMonitorEvents(api, true, vi.fn(), 0, feed))
  await advance(0)
  act(() => connections[0]!.emit(ready()))
  let answer!: (head: typeof notificationHeadFixture) => void
  vi.mocked(api.notificationHead).mockImplementationOnce(() => new Promise(resolve => { answer = resolve }))
  await advance(60_000)
  act(() => connections[0]!.emit({ type: 'notification', frame: {
    cursor: notificationCursor(11), unreadCount: 1, enabled: true, notification: notificationFixture,
  } }))
  feed.onHead.mockClear()
  await act(async () => { answer({ ...notificationHeadFixture, unreadCount: 0 }) })
  expect(feed.onHead).not.toHaveBeenCalled()
})

describe('one shell monitor subscription', () => {
  it('reads the head before stored state and streams from it, so a change committed in between is delivered without a second load', async () => {
    const { api, connections } = client()
    const reload = vi.fn()
    const view = renderHook(() => useMonitorEvents(api, true, reload, 0))
    // Stored reads (the shell's and the summary) wait for the head cursor.
    expect(view.result.current.cursorReady).toBe(false)
    expect(api.monitor).not.toHaveBeenCalled()
    await advance(0)
    expect(api.monitorEvents).toHaveBeenCalledOnce()
    expect(view.result.current.cursorReady).toBe(true)
    expect(api.monitor).toHaveBeenCalledOnce()
    expect(connections[0]!.after).toBe(headSequence)
    // The server replays a change committed after the head read but before the stream opened.
    act(() => {
      connections[0]!.emit(ready())
      connections[0]!.emit(eventFrame())
    })
    await advance(250)
    expect(reload).toHaveBeenCalledOnce()
  })
  it('does not reload for the opening ready status of a fresh load', async () => {
    const { api, connections } = client()
    const reload = vi.fn()
    renderHook(() => useMonitorEvents(api, true, reload, 0))
    await advance(0)
    act(() => connections[0]!.emit(ready()))
    await advance(300)
    expect(reload).not.toHaveBeenCalled()
  })
  it('falls back to the stream head and reloads once when the head cannot be read', async () => {
    const { api, connections } = client()
    vi.mocked(api.monitorEvents).mockRejectedValueOnce(new ApiError('unavailable', 'Down.'))
    const reload = vi.fn()
    const view = renderHook(() => useMonitorEvents(api, true, reload, 0))
    await advance(0)
    expect(view.result.current.cursorReady).toBe(true)
    expect(connections[0]!.after).not.toBe(headSequence)
    act(() => connections[0]!.emit({ type: 'status', status: { state: 'reset', nextCursor: eventCursor('41') } }))
    await advance(250)
    expect(reload).toHaveBeenCalledOnce()
    act(() => connections[0]!.finish())
    await advance(2000)
    expect(connections[1]!.after).toBe(eventCursor('41'))
  })
  it('debounces owner events and reset into stored reloads without browser persistence or venue reads', async () => {
    const { api, connections } = client()
    const reload = vi.fn()
    const local = vi.spyOn(Storage.prototype, 'setItem')
    const view = renderHook(() => useMonitorEvents(api, true, reload, 0))
    await advance(0)
    act(() => {
      connections[0]!.emit(ready())
      connections[0]!.emit(eventFrame())
      connections[0]!.emit(eventFrame({ ...monitorEventFixture, sequence: '9007199254740994' }))
      connections[0]!.emit({ type: 'status', status: { state: 'reset', nextCursor: eventCursor('9007199254740995') } })
    })
    await advance(249)
    expect(reload).not.toHaveBeenCalled()
    await advance(1)
    expect(reload).toHaveBeenCalledOnce()
    expect(api.monitorStream).toHaveBeenCalledOnce()
    expect(api.sync).not.toHaveBeenCalled()
    expect(api.runAccountMonitor).not.toHaveBeenCalled()
    expect(local).not.toHaveBeenCalled()
    view.unmount()
    local.mockRestore()
  })
  it('adopts a reset into a new stream epoch and treats its events as new whatever their sequence', async () => {
    const { api, connections } = client()
    const reload = vi.fn()
    renderHook(() => useMonitorEvents(api, true, reload, 0))
    await advance(0)
    const restored = 'fedcba9876543210fedcba9876543210'
    act(() => {
      connections[0]!.emit(ready())
      connections[0]!.emit({ type: 'status', status: { state: 'reset', nextCursor: eventCursor('1', restored) } })
    })
    await advance(250)
    expect(reload).toHaveBeenCalledOnce()
    act(() => {
      connections[0]!.emit({ type: 'event', event: { ...monitorEventFixture, sequence: '2' }, cursor: eventCursor('2', restored) })
      connections[0]!.finish()
    })
    await advance(2000)
    expect(reload).toHaveBeenCalledTimes(2)
    expect(connections[1]!.after).toBe(eventCursor('2', restored))
  })
  it('reconnects with exact cursors, ignores duplicate events and permits a server reset to a lower cursor', async () => {
    const { api, connections } = client()
    const reload = vi.fn()
    renderHook(() => useMonitorEvents(api, true, reload, 0))
    await advance(0)
    act(() => {
      connections[0]!.emit(ready())
      connections[0]!.emit(eventFrame())
      connections[0]!.finish()
    })
    await advance(2000)
    expect(connections[1]!.after).toBe(eventCursor(monitorEventFixture.sequence))
    reload.mockClear()
    act(() => connections[1]!.emit(eventFrame()))
    await advance(250)
    expect(reload).not.toHaveBeenCalled()
    act(() => {
      connections[1]!.emit({ type: 'status', status: { state: 'reset', nextCursor: eventCursor('2') } })
      connections[1]!.finish()
    })
    await advance(2000)
    expect(connections[2]!.after).toBe(eventCursor('2'))
    expect(reload).toHaveBeenCalledOnce()
  })
  it('does not acknowledge undispatched catch-up events from a ready frame', async () => {
    const { api, connections } = client()
    const reload = vi.fn()
    renderHook(() => useMonitorEvents(api, true, reload, 0))
    await advance(0)
    act(() => {
      connections[0]!.emit({ type: 'status', status: { state: 'ready', nextCursor: eventCursor('9007199254740999') } })
      connections[0]!.emit(eventFrame())
      connections[0]!.finish()
    })
    await advance(2000)
    expect(connections[1]!.after).toBe(eventCursor(monitorEventFixture.sequence))
    expect(reload).toHaveBeenCalledOnce()
  })
  it('backs off a ready-then-close loop without reloading the shell on each reconnect', async () => {
    const { api, connections } = client()
    vi.spyOn(Math, 'random').mockReturnValue(1)
    const reload = vi.fn()
    renderHook(() => useMonitorEvents(api, true, reload, 0))
    await advance(0)
    const cycle = (index: number) => act(() => { connections[index]!.emit(ready()); connections[index]!.finish() })
    cycle(0)
    for (const [index, delay] of [[1, 2000], [2, 4000], [3, 8000], [4, 16_000]] as const) {
      await advance(delay - 1)
      expect(connections).toHaveLength(index)
      await advance(1)
      expect(connections).toHaveLength(index + 1)
      expect(connections[index]!.after).toBe(headSequence)
      cycle(index)
    }
    expect(reload).not.toHaveBeenCalled()
  })
  it('ends the backoff once a stream stays open or delivers an event', async () => {
    const { api, connections } = client()
    vi.spyOn(Math, 'random').mockReturnValue(1)
    renderHook(() => useMonitorEvents(api, true, vi.fn(), 0))
    await advance(0)
    for (const index of [0, 1, 2]) {
      act(() => { connections[index]!.emit(ready()); connections[index]!.finish() })
      await advance(2000 * 2 ** index)
    }
    act(() => connections[3]!.emit(ready()))
    await advance(stableStream)
    act(() => connections[3]!.finish())
    await advance(2000)
    expect(connections).toHaveLength(5)
  })
  it('uses a 60-second stored-data fallback while disconnected and health reads while ready', async () => {
    const { api, connections } = client()
    const reload = vi.fn()
    renderHook(() => useMonitorEvents(api, true, reload, 0))
    await advance(0)
    await advance(59_999)
    expect(reload).not.toHaveBeenCalled()
    await advance(251)
    expect(reload).toHaveBeenCalledOnce()
    act(() => connections[0]!.emit(ready()))
    await advance(250)
    reload.mockClear()
    await advance(60_000)
    expect(reload).not.toHaveBeenCalled()
    expect(api.monitor).toHaveBeenCalledTimes(2)
    expect(api.sync).not.toHaveBeenCalled()
  })
  it('reloads once when the stream returns after a fallback reload, so a failed reload does not stay on screen', async () => {
    const { api, connections } = client()
    const reload = vi.fn()
    renderHook(() => useMonitorEvents(api, true, reload, 0))
    await advance(0)
    // The stream never became ready: the fallback reloads stored data, which fails while the API is down.
    await advance(60_250)
    expect(reload).toHaveBeenCalledOnce()
    // The stream connects with no events to catch up on; nothing else would clear the failed reload.
    act(() => connections[0]!.emit(ready()))
    await advance(250)
    expect(reload).toHaveBeenCalledTimes(2)
    // A later status of the same healthy stream adds nothing.
    act(() => connections[0]!.emit(ready()))
    await advance(250)
    expect(reload).toHaveBeenCalledTimes(2)
  })
  it('aborts on API replacement, disconnect and unmount and ignores late callbacks and queued reloads', async () => {
    const first = client(), second = client()
    const reload = vi.fn()
    const view = renderHook(({ api, enabled }) => useMonitorEvents(api, enabled, reload, 0), { initialProps: { api: first.api, enabled: true } })
    await advance(0)
    act(() => {
      first.connections[0]!.emit(ready())
      first.connections[0]!.emit(eventFrame())
    })
    view.rerender({ api: second.api, enabled: true })
    expect(first.connections[0]!.signal.aborted).toBe(true)
    act(() => first.connections[0]!.emit(eventFrame({ ...monitorEventFixture, sequence: '999999999999999999' })))
    await advance(1000)
    expect(reload).not.toHaveBeenCalled()
    expect(second.connections[0]!.after).toBe(headSequence)
    view.rerender({ api: second.api, enabled: false })
    expect(second.connections[0]!.signal.aborted).toBe(true)
    await advance(120_000)
    expect(reload).not.toHaveBeenCalled()
    view.rerender({ api: second.api, enabled: true })
    await advance(0)
    view.unmount()
    expect(second.connections[1]!.signal.aborted).toBe(true)
  })
  it('backs off exponentially with jitter and honours Retry-After', async () => {
    expect(reconnectDelay(1, 0)).toBe(1000)
    expect(reconnectDelay(1, 1)).toBe(2000)
    expect(reconnectDelay(3, 1)).toBe(8000)
    expect(reconnectDelay(20, 1)).toBe(60_000)
    const { api } = client()
    vi.mocked(api.monitorStream).mockImplementation(() => Promise.reject(new ApiError('unavailable', 'Down.')))
    vi.spyOn(Math, 'random').mockReturnValue(1)
    renderHook(() => useMonitorEvents(api, true, vi.fn(), 0))
    await advance(0)
    expect(api.monitorStream).toHaveBeenCalledTimes(1)
    await advance(2000)
    expect(api.monitorStream).toHaveBeenCalledTimes(2)
    await advance(3999)
    expect(api.monitorStream).toHaveBeenCalledTimes(2)
    await advance(1)
    expect(api.monitorStream).toHaveBeenCalledTimes(3)
    vi.mocked(api.monitorStream).mockImplementationOnce(() => Promise.reject(new ApiError('http', 'Busy.', 503, new Date(Date.now() + 30_000).toISOString())))
    await advance(8000)
    expect(api.monitorStream).toHaveBeenCalledTimes(4)
    await advance(29_000)
    expect(api.monitorStream).toHaveBeenCalledTimes(4)
    await advance(1000)
    expect(api.monitorStream).toHaveBeenCalledTimes(5)
  })
  it('stops hammering after repeated 429 stream refusals and says why', async () => {
    const { api } = client()
    vi.mocked(api.monitorStream).mockImplementation(() => Promise.reject(new ApiError('http', 'At most four event streams may be open per owner.', 429,
      new Date(Date.now() + 5_000).toISOString())))
    const view = renderHook(() => useMonitorEvents(api, true, vi.fn(), 0))
    await advance(0)
    await advance(120_000)
    expect(vi.mocked(api.monitorStream).mock.calls.length).toBeLessThanOrEqual(4)
    expect(view.result.current.notice).toMatch(/too many Vessel tabs/)
  })
})

describe('the notification feed on the shared stream', () => {
  const handlers = () => ({ onHead: vi.fn(), onStatus: vi.fn(), onFrame: vi.fn() }) satisfies NotificationStreamHandlers
  const item = (sequence: number, reset = 0) => ({ type: 'notification', frame: {
    cursor: notificationCursor(sequence, reset), unreadCount: 1, enabled: true, notification: notificationFixture } }) as const
  const status = (state: 'ready' | 'reset', sequence: number, reset = 0) =>
    ({ type: 'notifications', status: { state, nextCursor: notificationCursor(sequence, reset), unreadCount: 2, enabled: true } }) as const

  it('resumes both cursors in Wallet mode after the server closes a stream at its maximum lifetime', async () => {
    const streams: { path: string; body: ReadableStreamDefaultController<Uint8Array> }[] = []
    const encoder = new TextEncoder()
    const fetcher = vi.fn((path: string) => {
      if (path.startsWith('/api/events/stream?')) {
        const body = new ReadableStream<Uint8Array>({ start(body) { streams.push({ path, body }) } })
        return Promise.resolve(new Response(body, { headers: { 'Content-Type': 'text/event-stream' } }))
      }
      return Promise.resolve(new Response(JSON.stringify(path.startsWith('/api/events?')
        ? { events: [], nextCursor: headSequence, resetRequired: false }
        : path === '/api/notifications/head' ? notificationHeadFixture : monitorSummaryFixture)))
    })
    vi.stubGlobal('fetch', fetcher)
    const tab = new SessionController({ session: sessionFixture, channels: channelBus().factory, navigate: vi.fn() })
    const api = createWorkspaceApi(tab.credentials(), tab.handleFailure), feed = handlers()
    const view = renderHook(() => useMonitorEvents(api, true, vi.fn(), 0, feed))
    await advance(0)
    const send = (event: string, data: unknown, id = '') => streams[0]!.body.enqueue(encoder.encode(
      `${id ? `id: ${id}\n` : ''}event: ${event}\ndata: ${JSON.stringify(data)}\n\n`,
    ))
    send('status', { state: 'ready', nextCursor: headSequence })
    send('notifications', { state: 'ready', nextCursor: notificationHeadFixture.nextCursor, unreadCount: 0, enabled: true })
    send('event', monitorEventFixture, eventCursor(monitorEventFixture.sequence))
    send('notification', item(11).frame)
    await advance(stableStream + 1)
    // StreamMaxLifetime ends with EOF, not an auth failure. Reconnect without losing either feed.
    streams[0]!.body.close()
    await advance(2000)
    expect(streams).toHaveLength(2)
    const resumed = new URL(streams[1]!.path, 'https://vessel.test').searchParams
    expect(resumed.get('after')).toBe(eventCursor(monitorEventFixture.sequence))
    expect(resumed.get('notificationsAfter')).toBe(notificationCursor(11))
    expect(feed.onFrame).toHaveBeenCalledOnce()
    for (const [, init] of vi.mocked(fetch).mock.calls) {
      expect(init).toMatchObject({ credentials: 'same-origin', referrerPolicy: 'same-origin', headers: { 'X-Vessel-User': sessionFixture.user.id } })
    }
    view.unmount(); tab.dispose()
  })

  it('reads the notification head after the event head and streams both cursors', async () => {
    const { api, connections } = client()
    const feed = handlers()
    const view = renderHook(() => useMonitorEvents(api, true, vi.fn(), 0, feed))
    await advance(0)
    expect(view.result.current.cursorReady).toBe(true)
    expect(vi.mocked(api.monitorEvents).mock.invocationCallOrder[0]).toBeLessThan(vi.mocked(api.notificationHead).mock.invocationCallOrder[0]!)
    expect(feed.onHead).toHaveBeenCalledWith(notificationHeadFixture)
    expect(connections[0]!.after).toBe(headSequence)
    expect(connections[0]!.notificationsAfter).toBe(notificationHeadFixture.nextCursor)
  })
  it('never reloads stored state for notification frames and resumes from the last one handled', async () => {
    const { api, connections } = client()
    const feed = handlers(), reload = vi.fn()
    renderHook(() => useMonitorEvents(api, true, reload, 0, feed))
    await advance(0)
    act(() => {
      connections[0]!.emit(ready())
      connections[0]!.emit(status('ready', 10))
      connections[0]!.emit(item(11))
      connections[0]!.emit(item(11))
      connections[0]!.emit(item(9))
      connections[0]!.emit(item(12))
    })
    await advance(1000)
    expect(reload).not.toHaveBeenCalled()
    expect(feed.onStatus).toHaveBeenCalledOnce()
    expect(feed.onFrame.mock.calls.map(([frame]) => frame.cursor)).toEqual([notificationCursor(11), notificationCursor(12)])
    act(() => connections[0]!.finish())
    await advance(2000)
    // The event cursor is unchanged; the notification cursor is the last frame's own, never an SSE id.
    expect(connections[1]!.after).toBe(headSequence)
    expect(connections[1]!.notificationsAfter).toBe(notificationCursor(12))
  })
  it('keeps the two resets independent and rereads the notification head after its own reset', async () => {
    const { api, connections } = client()
    const feed = handlers(), reload = vi.fn()
    renderHook(() => useMonitorEvents(api, true, reload, 0, feed))
    await advance(0)
    vi.mocked(api.notificationHead).mockResolvedValue({ ...notificationHeadFixture, nextCursor: notificationCursor(3, 1), unreadCount: 2 })
    act(() => {
      connections[0]!.emit(ready())
      connections[0]!.emit(status('reset', 3, 1))
    })
    await advance(300)
    expect(reload).not.toHaveBeenCalled()
    expect(feed.onStatus).toHaveBeenLastCalledWith(expect.objectContaining({ state: 'reset' }))
    expect(api.notificationHead).toHaveBeenCalledTimes(2)
    expect(feed.onHead).toHaveBeenLastCalledWith(expect.objectContaining({ unreadCount: 2 }))
    // A frame from before the reset is ignored.
    act(() => connections[0]!.emit(item(40)))
    expect(feed.onFrame).not.toHaveBeenCalled()
    act(() => connections[0]!.emit({ type: 'status', status: { state: 'reset', nextCursor: eventCursor('2') } }))
    await advance(250)
    expect(reload).toHaveBeenCalledOnce()
    act(() => connections[0]!.finish())
    await advance(2000)
    expect(connections[1]!.after).toBe(eventCursor('2'))
    expect(connections[1]!.notificationsAfter).toBe(notificationCursor(3, 1))
  })
  it('ignores a head answer from an older reset version', async () => {
    const { api, connections } = client()
    const feed = handlers()
    renderHook(() => useMonitorEvents(api, true, vi.fn(), 0, feed))
    await advance(0)
    let answer: (head: typeof notificationHeadFixture) => void = () => undefined
    vi.mocked(api.notificationHead).mockImplementationOnce(() => new Promise(resolve => { answer = resolve }))
    act(() => connections[0]!.emit(ready()))
    // The fallback timer starts a head read; a reset arrives before it answers.
    await advance(60_000)
    vi.mocked(api.notificationHead).mockResolvedValueOnce({ ...notificationHeadFixture, nextCursor: notificationCursor(1, 2), unreadCount: 5 })
    act(() => connections[0]!.emit(status('reset', 1, 2)))
    await advance(0)
    feed.onHead.mockClear()
    await act(async () => { answer({ ...notificationHeadFixture, unreadCount: 99 }) })
    expect(feed.onHead).not.toHaveBeenCalled()
  })
  it('streams without notifications when their head fails, then adds them once a fallback read succeeds', async () => {
    const { api, connections } = client()
    vi.mocked(api.notificationHead).mockRejectedValueOnce(new ApiError('unavailable', 'Down.'))
    const feed = handlers(), reload = vi.fn()
    const view = renderHook(() => useMonitorEvents(api, true, reload, 0, feed))
    await advance(0)
    expect(view.result.current.cursorReady).toBe(true)
    expect(api.monitor).toHaveBeenCalledOnce()
    expect(connections[0]!.notificationsAfter).toBeUndefined()
    act(() => { connections[0]!.emit(ready()); connections[0]!.emit(eventFrame()) })
    await advance(60_000)
    expect(reload).toHaveBeenCalledOnce()
    expect(feed.onHead).toHaveBeenCalledWith(notificationHeadFixture)
    // The open stream is replaced at once, without backoff, keeping the event cursor it reached.
    expect(connections[0]!.signal.aborted).toBe(true)
    expect(connections[1]!.after).toBe(eventCursor(monitorEventFixture.sequence))
    expect(connections[1]!.notificationsAfter).toBe(notificationHeadFixture.nextCursor)
  })
  it('refreshes the worker status from the head on the 60-second fallback', async () => {
    const { api, connections } = client()
    const feed = handlers()
    renderHook(() => useMonitorEvents(api, true, vi.fn(), 0, feed))
    await advance(0)
    act(() => connections[0]!.emit(ready()))
    vi.mocked(api.notificationHead).mockResolvedValueOnce({ ...notificationHeadFixture, workerOnline: false })
    await advance(60_000)
    expect(feed.onHead).toHaveBeenLastCalledWith(expect.objectContaining({ workerOnline: false }))
    expect(connections).toHaveLength(1)
  })
})
