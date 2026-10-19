import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from './workspace'
import { isAccountMonitor, isMonitorDiscrepancy, isMonitorEvent, isMonitorSummary, isMonitorWatch, monitorFrame } from './monitor'
import { discrepancyFixture, eventCursor, monitorEventFixture, monitorFixture, monitorSummaryFixture } from '@/test/monitor-fixture'
import { accountFixture, savedPlayFixture } from '@/test/workspace-fixture'

afterEach(() => vi.unstubAllGlobals())
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })

describe('monitor contract', () => {
  it('validates nullable times, exact detail strings and large string cursors', () => {
    expect(isMonitorSummary(monitorSummaryFixture)).toBe(true)
    expect(isAccountMonitor({ ...monitorFixture, discrepancies: [discrepancyFixture] })).toBe(true)
    expect(isMonitorEvent(monitorEventFixture)).toBe(true)
    expect(isMonitorEvent({ ...monitorEventFixture, dedupKey: 'a'.repeat(1024) })).toBe(true)
    expect(isMonitorEvent({ ...monitorEventFixture, dedupKey: 'a'.repeat(1025) })).toBe(false)
    expect(isMonitorEvent({ ...monitorEventFixture, sequence: Number(monitorEventFixture.sequence) })).toBe(false)
    expect(isMonitorEvent({ ...monitorEventFixture, sequence: '-1' })).toBe(false)
    expect(isAccountMonitor({ ...monitorFixture, nextManualAllowedAtUtc: 'tomorrow' })).toBe(false)
    expect(isAccountMonitor({ ...monitorFixture, nextManualAllowedAtUtc: '10' })).toBe(false)
    expect(isAccountMonitor({ ...monitorFixture, nextManualAllowedAtUtc: '2026-10-14T10:00:00+03:00' })).toBe(false)
    expect(isAccountMonitor({ ...monitorFixture, nextManualAllowedAtUtc: '2026-10-14T10:00:00.1234567+00:00' })).toBe(true)
    expect(isAccountMonitor({ ...monitorFixture, manualState: 'unknown' })).toBe(false)
    expect(isMonitorSummary({ ...monitorSummaryFixture, accounts: [monitorFixture, monitorFixture] })).toBe(false)
  })
  it('requires every subject and source field as an explicit null rather than omitted', () => {
    const omitted: Record<string, unknown> = { ...monitorEventFixture.subject }
    delete omitted.venueId
    expect(isMonitorEvent({ ...monitorEventFixture, subject: omitted })).toBe(false)
    expect(isMonitorEvent({ ...monitorEventFixture, subject: { ...monitorEventFixture.subject, extra: null } })).toBe(false)
    expect(isMonitorEvent({ ...monitorEventFixture, source: { trigger: 'scheduled' } })).toBe(false)
    expect(isMonitorEvent({ ...monitorEventFixture, source: { ...monitorEventFixture.source, orderIds: ['o-1'] } })).toBe(true)
    // An event's sequence is a bare decimal within its epoch, never a cursor.
    expect(isMonitorEvent({ ...monitorEventFixture, sequence: eventCursor('1') })).toBe(false)
  })
  it('accepts an unverifiable outcome with its time, including a public unverifiable candidate', () => {
    const unverifiable = { ...discrepancyFixture, outcome: 'unverifiable', unverifiableAtUtc: '2026-10-21T10:00:00Z' }
    expect(isMonitorDiscrepancy(unverifiable)).toBe(true)
    expect(isMonitorDiscrepancy({ ...unverifiable, state: 'candidate' })).toBe(true)
    expect(isMonitorDiscrepancy({ ...discrepancyFixture, state: 'candidate' })).toBe(false)
    expect(isMonitorDiscrepancy({ ...unverifiable, unverifiableAtUtc: null })).toBe(false)
    expect(isMonitorDiscrepancy({ ...discrepancyFixture, unverifiableAtUtc: '2026-10-21T10:00:00Z' })).toBe(false)
    expect(isMonitorDiscrepancy({ ...discrepancyFixture, outcome: 'resolved' })).toBe(false)
    const older: Record<string, unknown> = { ...discrepancyFixture }
    delete older.outcome
    expect(isMonitorDiscrepancy(older)).toBe(false)
  })
  it('requires the last observation time as a UTC timestamp or an explicit null', () => {
    expect(isMonitorDiscrepancy({ ...discrepancyFixture, lastObservedAtUtc: null })).toBe(true)
    expect(isMonitorDiscrepancy({ ...discrepancyFixture, lastObservedAtUtc: '2026-10-14T10:00:00.1234567+00:00' })).toBe(true)
    expect(isMonitorDiscrepancy({ ...discrepancyFixture, lastObservedAtUtc: '2026-10-14 10:00' })).toBe(false)
    const older: Record<string, unknown> = { ...discrepancyFixture }
    delete older.lastObservedAtUtc
    expect(isMonitorDiscrepancy(older)).toBe(false)
  })
  it('validates watched orders and keeps a watching order open-ended', () => {
    const watch = { linkId: 'link-1', playId: savedPlayFixture.summary.id, orderId: 'exit-1', role: 'stop', state: 'unverifiable',
      startedAtUtc: '2026-10-14T10:00:00Z', endedAtUtc: '2026-10-21T10:00:00Z' }
    expect(isMonitorWatch(watch)).toBe(true)
    expect(isMonitorWatch({ ...watch, endedAtUtc: null })).toBe(true)
    expect(isMonitorWatch({ ...watch, state: 'watching' })).toBe(false)
    expect(isMonitorWatch({ ...watch, state: 'watching', endedAtUtc: null })).toBe(true)
    expect(isMonitorWatch({ ...watch, state: 'gone' })).toBe(false)
    expect(isMonitorWatch({ ...watch, role: 'entry' })).toBe(true)
    expect(isMonitorWatch({ ...watch, role: 'protection' })).toBe(false)
  })
  it('ignores malformed frames and unsupported event types', () => {
    const cursor = eventCursor('10')
    for (const [type, data, id] of [['event', '{', cursor], ['event', '{}', cursor], ['message', JSON.stringify(monitorEventFixture), cursor],
      // An event frame without a valid SSE id cannot be resumed after.
      ['event', JSON.stringify(monitorEventFixture), ''], ['event', JSON.stringify(monitorEventFixture), '10'],
      ['status', `{"state":"reset","nextCursor":10}`, ''], ['status', '{"state":"reset","nextSequence":"10"}', ''],
      ['status', `{"state":"other","nextCursor":"${cursor}"}`, '']]) {
      expect(monitorFrame(type!, data!, id)).toBeNull()
    }
    expect(monitorFrame('status', `{"state":"reset","nextCursor":"${cursor}"}`)).toEqual({ type: 'status', status: { state: 'reset', nextCursor: cursor } })
    expect(monitorFrame('event', JSON.stringify(monitorEventFixture), cursor)).toEqual({ type: 'event', event: monitorEventFixture, cursor })
  })
  it('uses authenticated no-store reads and a body-free account run request, not execution/check', async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(json(monitorSummaryFixture)).mockResolvedValueOnce(json(monitorFixture))
      .mockResolvedValueOnce(json({ requestId: 'run-1', monitor: { ...monitorFixture, manualState: 'pending', runTrigger: 'owner' } }, 202))
    vi.stubGlobal('fetch', fetcher)
    const api = createWorkspaceApi('private-token')
    const signal = new AbortController().signal
    await api.monitor(signal)
    await api.accountMonitor(accountFixture.id, signal)
    await api.runAccountMonitor(accountFixture.id, signal)
    expect(fetcher.mock.calls.map(call => call[0])).toEqual(['/api/monitor', `/api/accounts/${accountFixture.id}/monitor`, `/api/accounts/${accountFixture.id}/monitor/run`])
    for (const [, init] of fetcher.mock.calls) expect(init).toMatchObject({ credentials: 'omit', cache: 'no-store', headers: { Authorization: 'Bearer private-token' } })
    expect(fetcher.mock.calls[2]![1]).toMatchObject({ method: 'POST' })
    expect(fetcher.mock.calls[2]![1].body).toBeUndefined()
  })
  it('rejects monitor state belonging to another account and preserves Retry-After on 429', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(json({ ...monitorFixture, accountId: savedPlayFixture.summary.id }))
      .mockResolvedValueOnce(new Response(null, { status: 429, headers: { 'Retry-After': '60' } })))
    const api = createWorkspaceApi('test')
    await expect(api.accountMonitor(accountFixture.id)).rejects.toMatchObject({ kind: 'invalid-response' })
    const before = Date.now()
    const failure = await api.runAccountMonitor(accountFixture.id).catch(error => error)
    expect(failure.status).toBe(429)
    expect(Date.parse(failure.retryAtUtc)).toBeGreaterThanOrEqual(before + 60_000)
  })
  it('explains an offline monitor when a check is refused with 503', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(new Response(null, { status: 503 })))
    const failure = await createWorkspaceApi('test').runAccountMonitor(accountFixture.id).catch(error => error)
    expect(failure).toMatchObject({ status: 503, message: 'The monitor is offline, so the check could not start. Try again once the monitor is back.' })
  })
  it('dismisses from either Play or account using exact IDs and plain-text notes', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetcher)
    const api = createWorkspaceApi('test')
    await api.dismissPlayDiscrepancy(savedPlayFixture.summary.id, discrepancyFixture.id, 'Still investigating.')
    await api.dismissAccountDiscrepancy(accountFixture.id, discrepancyFixture.id)
    expect(fetcher.mock.calls[0]).toEqual([`/api/plays/${savedPlayFixture.summary.id}/discrepancies/${discrepancyFixture.id}/dismiss`,
      expect.objectContaining({ method: 'POST', body: '{"note":"Still investigating."}' })])
    expect(fetcher.mock.calls[1]![0]).toBe(`/api/accounts/${accountFixture.id}/discrepancies/${discrepancyFixture.id}/dismiss`)
  })
  it('decodes split frames, skips malformed messages, and cancels the fetch and reader on abort', async () => {
    const cancelled = vi.fn()
    let writer!: ReadableStreamDefaultController<Uint8Array>
    const body = new ReadableStream<Uint8Array>({ start(controller) { writer = controller }, cancel: cancelled })
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response(body, { headers: { 'Content-Type': 'text/event-stream' } }))
    vi.stubGlobal('fetch', fetcher)
    const abort = new AbortController()
    const received = vi.fn()
    const task = createWorkspaceApi('test').monitorStream(eventCursor('9007199254740992'), abort.signal, received)
    await vi.waitFor(() => expect(fetcher).toHaveBeenCalled())
    const encoder = new TextEncoder()
    writer.enqueue(encoder.encode(': keepalive\r\nevent: event\ndata: invalid\n\nevent: event\nda'))
    writer.enqueue(encoder.encode(`ta: ${JSON.stringify(monitorEventFixture)}\nid: ${eventCursor('9007199254740993')}\n\nevent: status\ndata: {"state":"ready","nextCursor":"${eventCursor('9007199254740993')}"}\n\n`))
    await vi.waitFor(() => expect(received).toHaveBeenCalledTimes(2))
    expect(received.mock.calls[0]![0]).toEqual({ type: 'event', event: monitorEventFixture, cursor: eventCursor('9007199254740993') })
    abort.abort()
    await task
    expect(cancelled).toHaveBeenCalledOnce()
    expect(fetcher.mock.calls[0]![0]).toBe(`/api/events/stream?after=${eventCursor('9007199254740992')}`)
    expect(fetcher.mock.calls[0]![1]).toMatchObject({ signal: abort.signal, headers: { Authorization: 'Bearer test', Accept: 'text/event-stream' } })
  })
})
