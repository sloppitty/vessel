import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/errors'
import { createWorkspaceApi, type CandleSeries, type VenueCandle, type WorkspaceApi } from '@/api/workspace'
import { candleSeriesFixture } from '@/test/workspace-fixture'
import { candleCache, candleLimit, clearCandleCache, evictAccountCandles, pageLifetime } from './candleCache'
import { useCandles } from './useCandles'
import { credentialsFixture } from '@/test/identity-fixture'

function candle(time: number, trades = 10, close = '103'): VenueCandle {
  return { openTime: time, closeTime: time + 59_999, open: '100', high: '105', low: '95', close, volume: '20', trades }
}
/** A window whose fetched range is exactly its candles unless a range is given. */
const series = (...candles: VenueCandle[]): CandleSeries => ({ ...candleSeriesFixture, candles, historyExhausted: false,
  requestedFrom: candles[0]?.openTime ?? 0, requestedTo: candles.at(-1)?.closeTime ?? 0 })
const windowed = (requestedFrom: number, requestedTo: number, ...candles: VenueCandle[]): CandleSeries =>
  ({ ...series(...candles), requestedFrom, requestedTo })
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (error: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
function setup(candles = vi.fn<WorkspaceApi['candles']>().mockResolvedValue(series(candle(60_000), candle(120_000)))) {
  const api = { ...createWorkspaceApi(credentialsFixture), candles }
  const cache = candleCache(api)
  const entry = cache.entry('lighter-main', 'BTC', '1m')
  return { api, cache, entry, candles }
}
afterEach(() => vi.restoreAllMocks())

describe('session candle cache', () => {
  it('shows switch-back candles and their observation time before revalidation resolves', async () => {
    const { api, candles } = setup()
    const source = { api, accountId: 'lighter-main', instrument: 'BTC', interval: '1m' as const }
    const first = renderHook(() => useCandles(source))
    await waitFor(() => expect(first.result.current.status).toBe('ready'))
    const display = first.result.current.candles[0]
    first.unmount()
    const pending = deferred<CandleSeries>()
    candles.mockReturnValueOnce(pending.promise)
    const second = renderHook(() => useCandles(source))
    expect(second.result.current.candles[0]).toBe(display)
    expect(second.result.current.retrievedAt).toBe(candleSeriesFixture.retrievedAt)
    expect(second.result.current.refreshing).toBe(true)
    await act(async () => pending.resolve(series(candle(60_000), candle(120_000))))
    expect(second.result.current.refreshing).toBe(false)
  })

  it('merges revalidation corrections but retains unchanged numeric objects', async () => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    const unchanged = entry.snapshot.candles[0]
    candles.mockResolvedValueOnce(series(candle(60_000), candle(120_000, 20, '104')))
    await cache.load(entry, 'latest')
    expect(entry.snapshot.candles[0]).toBe(unchanged)
    expect(entry.snapshot.candles[1]?.close).toBe(104)
  })

  it('does not reconvert numeric displays for a trade-count-only tick', async () => {
    const { cache, entry } = setup()
    await cache.load(entry, 'latest')
    const unchanged = entry.snapshot.candles[1]
    cache.upsert(entry, candle(120_000, 11), '2026-10-11T12:00:00Z')
    expect(entry.snapshot.candles[1]).toBe(unchanged)
    expect(entry.snapshot.venueCandles[1]?.trades).toBe(11)
  })

  it('replaces disconnected ranges and restarts history paging', async () => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    candles.mockResolvedValueOnce({ ...series(candle(0)), historyExhausted: true })
    await cache.load(entry, 'older')
    candles.mockResolvedValueOnce(series(candle(600_000), candle(660_000)))
    await cache.load(entry, 'latest')
    expect(entry.snapshot.candles.map(c => c.time)).toEqual([600_000, 660_000])
    expect(entry.snapshot.historyExhausted).toBe(false)
    expect(entry.pages.size).toBe(0)
    await cache.load(entry, 'older')
    expect(candles).toHaveBeenLastCalledWith('lighter-main', { instrument: 'BTC', interval: '1m', endTime: 599_999 }, expect.any(AbortSignal))
  })

  it('uses range overlap, not missing timestamp slots, to merge sparse windows', async () => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    candles.mockResolvedValueOnce(series(candle(90_000), candle(180_000)))
    await cache.load(entry, 'latest')
    expect(entry.snapshot.candles.map(c => c.time)).toEqual([60_000, 90_000, 120_000, 180_000])
  })

  it('keeps history when fetched windows overlap through a no-trade period', async () => {
    const { cache, entry, candles } = setup(vi.fn<WorkspaceApi['candles']>().mockResolvedValue(windowed(0, 300_000, candle(60_000), candle(120_000))))
    await cache.load(entry, 'latest')
    candles.mockResolvedValueOnce(windowed(100_000, 400_000, candle(360_000)))
    await cache.load(entry, 'latest')
    expect(entry.snapshot.candles.map(c => c.time)).toEqual([60_000, 120_000, 360_000])
  })

  it('replaces the series when fetched windows are disjoint, even after a live candle bridged them', async () => {
    const { cache, entry, candles } = setup(vi.fn<WorkspaceApi['candles']>().mockResolvedValue(windowed(0, 300_000, candle(60_000), candle(120_000))))
    await cache.load(entry, 'latest')
    cache.upsert(entry, candle(900_000), '2026-10-11T12:00:00Z')
    candles.mockResolvedValueOnce(windowed(800_000, 1_000_000, candle(840_000), candle(900_000)))
    await cache.load(entry, 'latest')
    expect(entry.snapshot.candles.map(c => c.time)).toEqual([840_000, 900_000])
  })

  it('ignores a late failure from a read that a refresh superseded', async () => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    const activation = deferred<CandleSeries>()
    candles.mockReturnValueOnce(activation.promise).mockResolvedValueOnce(series(candle(60_000), candle(120_000, 20, '104')))
    const stale = cache.load(entry, 'latest')
    await cache.load(entry, 'refresh')
    activation.reject(new ApiError('http', 'This market is no longer listed.', 409))
    await stale
    expect(cache.entries.get(entry.key)).toBe(entry)
    expect(entry.snapshot.status).toBe('ready')
    expect(entry.snapshot.candles[1]?.close).toBe(104)
  })

  it('expires older pages at activation without renewing their age from REST or live', async () => {
    let now = 1000
    vi.spyOn(Date, 'now').mockImplementation(() => now)
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    candles.mockResolvedValueOnce(series(candle(0)))
    await cache.load(entry, 'older')
    const fetchedAt = entry.pages.values().next().value!.fetchedAt
    now += pageLifetime
    cache.upsert(entry, candle(120_000, 30), '2026-10-11T12:00:00Z')
    await cache.load(entry, 'latest')
    expect(entry.pages.values().next().value!.fetchedAt).toBe(fetchedAt)
    expect(entry.snapshot.candles[0]?.time).toBe(0)
    now++
    cache.activate(entry)
    expect(entry.snapshot.candles.map(c => c.time)).toEqual([60_000, 120_000])
    expect(entry.snapshot.historyExhausted).toBe(false)
  })

  it('isolates Lighter main/subaccount and connection instances', async () => {
    const { cache, entry, api } = setup()
    await cache.load(entry, 'latest')
    expect(cache.entry('lighter-subaccount', 'BTC', '1m').snapshot.candles).toEqual([])
    expect(candleCache({ ...api }).entry('lighter-main', 'BTC', '1m').snapshot.candles).toEqual([])
  })

  it.each([401, 403])('clears all entries on HTTP %s', async status => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    const other = cache.entry('lighter-subaccount', 'ETH', '1m')
    await cache.load(other, 'latest')
    candles.mockRejectedValueOnce(new ApiError('http', 'Unauthorized.', status))
    await cache.load(entry, 'latest')
    expect(cache.entries.size).toBe(0)
    expect(entry.snapshot.candles).toEqual([])
    expect(other.snapshot.candles).toEqual([])
  })

  it.each([404, 409])('drops stale candles and shows an error on HTTP %s', async status => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    candles.mockRejectedValueOnce(new ApiError('http', 'This market is no longer listed.', status))
    await cache.load(entry, 'latest')
    expect(entry.snapshot.status).toBe('error')
    expect(entry.snapshot.error).toBe('This market is no longer listed.')
    expect(entry.snapshot.candles).toEqual([])
  })

  it('ends other charts of the account only when the account is gone, not for one unlisted market', async () => {
    const { cache, entry, candles } = setup()
    const other = cache.entry('lighter-main', 'ETH', '1m')
    await Promise.all([cache.load(entry, 'latest'), cache.load(other, 'latest')])
    const ended = vi.fn()
    cache.onInvalidate('lighter-main', ended)
    candles.mockRejectedValueOnce(new ApiError('http', 'This market is no longer listed.', 409))
    await cache.load(entry, 'latest')
    expect(ended).not.toHaveBeenCalled()
    expect(other.snapshot.status).toBe('ready')
    candles.mockRejectedValueOnce(new ApiError('http', 'Account not found.', 404))
    await cache.load(other, 'latest')
    expect(ended).toHaveBeenCalledOnce()
  })

  it('lets Try again recover a chart whose entry was dropped', async () => {
    const { api, candles } = setup()
    candles.mockRejectedValueOnce(new ApiError('http', 'Enable the account before reading market data.', 409))
    const hook = renderHook(() => useCandles({ api, accountId: 'lighter-main', instrument: 'BTC', interval: '1m' }))
    await waitFor(() => expect(hook.result.current.error).toBe('Enable the account before reading market data.'))
    act(() => hook.result.current.refresh())
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    expect(hook.result.current.candles.map(c => c.time)).toEqual([60_000, 120_000])
    // The retry keeps Refresh's server-cache bypass, and activation joins it instead of reading again.
    expect(candles).toHaveBeenCalledTimes(2)
    expect(candles).toHaveBeenLastCalledWith('lighter-main', { instrument: 'BTC', interval: '1m', refresh: true }, expect.any(AbortSignal))
  })

  it.each([['succeeds', null], ['fails', new ApiError('http', 'Venue unavailable.', 502)]] as const)(
    'reads once when Try again %s before its activation', async (_, failure) => {
      const { api, candles } = setup()
      candles.mockRejectedValueOnce(new ApiError('http', 'Enable the account before reading market data.', 409))
      const hook = renderHook(() => useCandles({ api, accountId: 'lighter-main', instrument: 'BTC', interval: '1m' }))
      await waitFor(() => expect(hook.result.current.status).toBe('error'))
      if (failure) candles.mockRejectedValueOnce(failure)
      // The read settles inside the act scope, before React runs the replacement's activation.
      await act(async () => { hook.result.current.refresh(); await new Promise(resolve => setTimeout(resolve, 0)) })
      expect(candles).toHaveBeenCalledTimes(2)
      expect(candles).toHaveBeenLastCalledWith('lighter-main', { instrument: 'BTC', interval: '1m', refresh: true }, expect.any(AbortSignal))
      expect(hook.result.current.status).toBe(failure ? 'error' : 'ready')
      expect(hook.result.current.error).toBe(failure ? 'Venue unavailable.' : null)
    })

  it('revalidates a later switch-back after a retry whose activation was abandoned', async () => {
    const { api, candles } = setup()
    candles.mockRejectedValueOnce(new ApiError('http', 'Enable the account before reading market data.', 409))
    const hook = renderHook(({ interval }: { interval: '1m' | '5m' }) => useCandles({ api, accountId: 'lighter-main', instrument: 'BTC', interval }),
      { initialProps: { interval: '1m' } })
    await waitFor(() => expect(hook.result.current.status).toBe('error'))
    act(() => { hook.result.current.refresh(); hook.rerender({ interval: '5m' }) })
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    const before = candles.mock.calls.length
    hook.rerender({ interval: '1m' })
    await waitFor(() => expect(candles).toHaveBeenCalledTimes(before + 1))
    expect(candles).toHaveBeenLastCalledWith('lighter-main', { instrument: 'BTC', interval: '1m' }, expect.any(AbortSignal))
  })

  it('still expires old pages when activation skips its read', async () => {
    let now = 1000
    vi.spyOn(Date, 'now').mockImplementation(() => now)
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    candles.mockResolvedValueOnce(series(candle(0)))
    await cache.load(entry, 'older')
    now += pageLifetime + 1
    const calls = candles.mock.calls.length
    cache.activate(entry, false)
    expect(entry.snapshot.candles.map(c => c.time)).toEqual([60_000, 120_000])
    expect(candles).toHaveBeenCalledTimes(calls)
  })

  it.each([new ApiError('http', 'Venue unavailable.', 502), new TypeError('Network unavailable')])('retains cached candles on a refresh failure (%s)', async error => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    const before = entry.snapshot.candles
    candles.mockRejectedValueOnce(error)
    await cache.load(entry, 'refresh')
    expect(entry.snapshot.status).toBe('ready')
    expect(entry.snapshot.candles).toBe(before)
    expect(entry.snapshot.error).not.toBeNull()
  })

  it.each(['disable', 'delete'])('aborts pending account reads and discards late REST and live after %s', async () => {
    const { cache, entry, candles, api } = setup()
    await cache.load(entry, 'latest')
    const pending = deferred<CandleSeries>()
    candles.mockReturnValueOnce(pending.promise)
    const loading = cache.load(entry, 'latest')
    const signal = candles.mock.calls.at(-1)![2]!
    evictAccountCandles(api, entry.accountId)
    expect(signal.aborted).toBe(true)
    cache.upsert(entry, candle(180_000), '2026-10-11T12:00:00Z')
    pending.resolve(series(candle(180_000)))
    await loading
    expect(entry.snapshot.candles).toEqual([])
    expect(cache.entries.size).toBe(0)
  })

  it('discards late reads from an old connection and an invalidated/recreated key', async () => {
    const pending = deferred<CandleSeries>()
    const { cache, entry, api } = setup(vi.fn<WorkspaceApi['candles']>().mockReturnValue(pending.promise))
    const loading = cache.load(entry, 'latest')
    clearCandleCache(api)
    const replacement = cache.entry(entry.accountId, 'BTC', '1m')
    pending.resolve(series(candle(120_000)))
    await loading
    expect(replacement.snapshot.candles).toEqual([])
    expect(entry.snapshot.candles).toEqual([])
  })

  it('deduplicates reads and does not abort when one of two consumers unmounts', async () => {
    const pending = deferred<CandleSeries>()
    const { api, candles } = setup(vi.fn<WorkspaceApi['candles']>().mockReturnValue(pending.promise))
    const source = { api, accountId: 'lighter-main', instrument: 'BTC', interval: '1m' as const }
    const first = renderHook(() => useCandles(source)), second = renderHook(() => useCandles(source))
    first.unmount()
    expect(candles).toHaveBeenCalledTimes(1)
    expect(candles.mock.calls[0]![2]!.aborted).toBe(false)
    await act(async () => pending.resolve(series(candle(120_000))))
    expect(second.result.current.candles).toHaveLength(1)
  })

  it('uses LRU last-view order, retains only 20 entries and never evicts a visible entry', () => {
    const { cache, entry } = setup()
    const unsubscribe = cache.subscribe(entry, vi.fn())
    const old = cache.entry('old', 'BTC', '1m')
    for (let i = 0; i < 30; i++) cache.entry(`account-${i}`, 'BTC', '1m')
    expect(cache.entries.size).toBe(20)
    expect(cache.entries.get(entry.key)).toBe(entry)
    expect(cache.entries.has(old.key)).toBe(false)
    unsubscribe()
  })

  it('stops paging at the local cap without claiming venue exhaustion or looping', async () => {
    const { cache, entry, candles } = setup()
    candles.mockResolvedValueOnce(series(...Array.from({ length: candleLimit }, (_, i) => candle(i * 60_000))))
    await cache.load(entry, 'latest')
    expect(entry.snapshot.localHistoryLimit).toBe(true)
    expect(entry.snapshot.historyExhausted).toBe(false)
    await cache.load(entry, 'older'); await cache.load(entry, 'older')
    expect(candles).toHaveBeenCalledTimes(1)
    cache.upsert(entry, candle(candleLimit * 60_000), '2026-10-11T12:00:00Z')
    expect(entry.snapshot.candles).toHaveLength(candleLimit)
    expect(entry.snapshot.candles.at(-1)?.time).toBe(candleLimit * 60_000)
    expect(entry.snapshot.localHistoryLimit).toBe(true)
  })

  it('keeps a whole just-loaded page when reaching the cap, retiring previously retained history', async () => {
    const { cache, entry, candles } = setup()
    candles.mockResolvedValueOnce(series(...Array.from({ length: 4900 }, (_, i) => candle((i + 501) * 60_000))))
    await cache.load(entry, 'latest')
    const older = Array.from({ length: 500 }, (_, i) => candle((i + 1) * 60_000))
    candles.mockResolvedValueOnce(series(...older))
    await cache.load(entry, 'older')
    expect(entry.snapshot.candles).toHaveLength(candleLimit)
    expect(entry.snapshot.venueCandles.slice(0, 500)).toEqual(older)
    expect(entry.snapshot.candles.at(-1)?.time).toBe(5400 * 60_000)
    expect(entry.snapshot.localHistoryLimit).toBe(true)
    expect(entry.snapshot.historyExhausted).toBe(false)
  })

  it('stops automatic paging on an empty non-exhausted page instead of refetching forever', async () => {
    const { api, candles } = setup()
    const hook = renderHook(() => useCandles({ api, accountId: 'lighter-main', instrument: 'BTC', interval: '1m' }))
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    candles.mockResolvedValueOnce(series())
    act(() => hook.result.current.loadOlder())
    await waitFor(() => expect(hook.result.current.olderError).not.toBeNull())
    act(() => { hook.result.current.loadOlder(); hook.result.current.loadOlder() })
    expect(candles).toHaveBeenCalledTimes(2)
  })

  it('sends refresh only for explicit Refresh, not activation, recovery or paging', async () => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest'); await cache.load(entry, 'refresh'); await cache.load(entry, 'older')
    expect(candles.mock.calls.map(call => call[1].refresh)).toEqual([undefined, true, undefined])
  })
})

describe('REST/live ordering', () => {
  it.each(['success', 'failure'])('reapplies coalesced buffered ticks on REST %s', async outcome => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    const pending = deferred<CandleSeries>()
    candles.mockReturnValueOnce(pending.promise)
    const loading = cache.load(entry, 'refresh')
    cache.upsert(entry, candle(120_000, 40, '104'), '2026-10-11T12:00:00Z')
    cache.upsert(entry, candle(120_000, 30, '102'), '2026-10-11T12:00:01Z')
    expect(entry.buffer.size).toBe(1)
    if (outcome === 'success') pending.resolve(series(candle(60_000), candle(120_000, 20)))
    else pending.reject(new TypeError('Network unavailable'))
    await loading
    expect(entry.snapshot.venueCandles.at(-1)?.trades).toBe(40)
    expect(entry.snapshot.candles.at(-1)?.close).toBe(104)
    expect(entry.buffer.size).toBe(0)
  })

  it('lets REST win for a candle closed by rollover during refresh', async () => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    const pending = deferred<CandleSeries>()
    candles.mockReturnValueOnce(pending.promise)
    const loading = cache.load(entry, 'refresh')
    cache.upsert(entry, candle(120_000, 30, '104'), '2026-10-11T12:00:00Z')
    cache.upsert(entry, candle(180_000, 5, '105'), '2026-10-11T12:01:00Z')
    pending.resolve(series(candle(120_000, 20, '101')))
    await loading
    expect(entry.snapshot.candles.map(c => c.close)).toEqual([103, 101, 105])
  })

  it('rejects a stale forming relay replay and accepts equal-count later arrivals', async () => {
    const { cache, entry } = setup()
    await cache.load(entry, 'latest')
    cache.upsert(entry, candle(120_000, 5, '101'), '2026-10-11T12:00:00Z')
    expect(entry.snapshot.candles.at(-1)?.close).toBe(103)
    cache.upsert(entry, candle(120_000, 10, '104'), '2026-10-11T12:00:01Z')
    expect(entry.snapshot.candles.at(-1)?.close).toBe(104)
  })

  it('uses exact larger volume when trade counts are missing', async () => {
    const { cache, entry, candles } = setup()
    candles.mockResolvedValueOnce(series({ ...candle(120_000), trades: null, volume: '10000000000000000000.000002' }))
    await cache.load(entry, 'latest')
    cache.upsert(entry, { ...candle(120_000, 0, '101'), trades: null, volume: '10000000000000000000.000001' }, '2026-10-11T12:00:00Z')
    expect(entry.snapshot.candles.at(-1)?.close).toBe(103)
  })

  it('applies an older-open correction even when it has fewer trades', async () => {
    const { cache, entry } = setup()
    await cache.load(entry, 'latest')
    cache.upsert(entry, candle(60_000, 1, '102'), '2026-10-11T12:00:00Z')
    expect(entry.snapshot.candles[0]?.close).toBe(102)
  })

  it('discards buffered ticks when the entry is invalidated', async () => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    const pending = deferred<CandleSeries>()
    candles.mockReturnValueOnce(pending.promise)
    const loading = cache.load(entry, 'refresh')
    cache.upsert(entry, candle(180_000), '2026-10-11T12:00:00Z')
    cache.invalidate()
    pending.resolve(series(candle(120_000)))
    await loading
    expect(entry.buffer.size).toBe(0)
    expect(entry.snapshot.candles).toEqual([])
  })

  it.each(['older-first', 'refresh-first'])('lets an explicit refresh replace a pending older read (%s)', async order => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    const older = deferred<CandleSeries>(), refresh = deferred<CandleSeries>()
    candles.mockReturnValueOnce(older.promise).mockReturnValueOnce(refresh.promise)
    const olderRead = cache.load(entry, 'older'), refreshRead = cache.load(entry, 'refresh')
    expect((candles.mock.calls[1]![2] as AbortSignal).aborted).toBe(true)
    expect(entry.snapshot.loadingOlder).toBe(false)
    if (order === 'older-first') { older.resolve(series(candle(0))); await olderRead; refresh.resolve(series(candle(60_000, 20, '104'), candle(120_000, 20))) }
    else { refresh.resolve(series(candle(60_000, 20, '104'), candle(120_000, 20))); await refreshRead; older.resolve(series(candle(0))) }
    await Promise.all([olderRead, refreshRead])
    expect(entry.snapshot.candles.map(c => c.time)).toEqual([60_000, 120_000])
    expect(entry.snapshot.candles[0]?.close).toBe(104)
    expect(entry.snapshot.loadingOlder).toBe(false)
    candles.mockResolvedValueOnce(series(candle(0)))
    await cache.load(entry, 'older')
    expect(entry.snapshot.candles.map(c => c.time)).toEqual([0, 60_000, 120_000])
  })

  it('does not resurrect old history when a refresh gap replaces the series during paging', async () => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    const older = deferred<CandleSeries>()
    candles.mockReturnValueOnce(older.promise).mockResolvedValueOnce(series(candle(600_000)))
    const olderRead = cache.load(entry, 'older')
    await cache.load(entry, 'refresh')
    older.resolve(series(candle(0)))
    await olderRead
    expect(entry.snapshot.candles.map(c => c.time)).toEqual([600_000])
  })

  it('does not let an earlier activation response overwrite an explicit refresh', async () => {
    const { cache, entry, candles } = setup()
    await cache.load(entry, 'latest')
    const activation = deferred<CandleSeries>()
    candles.mockReturnValueOnce(activation.promise).mockResolvedValueOnce(series(candle(120_000, 20, '104')))
    const activationRead = cache.load(entry, 'latest')
    await cache.load(entry, 'refresh')
    activation.resolve(series(candle(120_000, 10, '101')))
    await activationRead
    expect(entry.snapshot.candles.at(-1)?.close).toBe(104)
  })
})

it('coalesces recovery with an explicit refresh rather than superseding the bypassed read', async () => {
  const { cache, entry, candles } = setup()
  await cache.load(entry, 'latest')
  const pending = deferred<CandleSeries>()
  candles.mockReturnValueOnce(pending.promise)
  const explicit = cache.load(entry, 'refresh')
  expect(cache.load(entry, 'latest')).toBe(explicit)
  expect(cache.load(entry, 'refresh')).toBe(explicit)
  pending.resolve(series(candle(120_000, 20, '104')))
  await explicit
  expect(candles).toHaveBeenCalledTimes(2)
  expect(entry.snapshot.candles.at(-1)?.close).toBe(104)
})

it('retains cached prices when a revalidation returns an empty window', async () => {
  const { cache, entry, candles } = setup()
  await cache.load(entry, 'latest')
  candles.mockResolvedValueOnce(series())
  await cache.load(entry, 'latest')
  expect(entry.snapshot.candles).toHaveLength(2)
})

it('bounds live appends while an older page is in flight, then keeps the arriving page', async () => {
  const { cache, entry, candles } = setup()
  candles.mockResolvedValueOnce(series(...Array.from({ length: 4999 }, (_, i) => candle((i + 10) * 60_000))))
  await cache.load(entry, 'latest')
  const pending = deferred<CandleSeries>()
  candles.mockReturnValueOnce(pending.promise)
  const older = cache.load(entry, 'older')
  cache.upsert(entry, candle(5009 * 60_000), '2026-10-11T12:00:00Z')
  cache.upsert(entry, candle(5010 * 60_000), '2026-10-11T12:01:00Z')
  pending.resolve(series(candle(60_000), candle(120_000)))
  await older
  expect(entry.snapshot.candles).toHaveLength(5000)
  expect(entry.snapshot.candles.slice(0, 2).map(c => c.time)).toEqual([60_000, 120_000])
  expect(entry.snapshot.candles.at(-1)?.time).toBe(5010 * 60_000)
  await cache.load(entry, 'older')
  expect(candles).toHaveBeenCalledTimes(2)
})

it('aborts even superseded tail requests when the account is disabled', async () => {
  const { cache, entry, candles, api } = setup()
  await cache.load(entry, 'latest')
  const activation = deferred<CandleSeries>(), explicit = deferred<CandleSeries>()
  candles.mockReturnValueOnce(activation.promise).mockReturnValueOnce(explicit.promise)
  const oldRead = cache.load(entry, 'latest'), newRead = cache.load(entry, 'refresh')
  const signals = candles.mock.calls.slice(1).map(call => call[2]!)
  evictAccountCandles(api, entry.accountId)
  expect(signals.every(signal => signal.aborted)).toBe(true)
  activation.resolve(series(candle(180_000))); explicit.resolve(series(candle(120_000)))
  await Promise.all([oldRead, newRead])
  expect(entry.snapshot.candles).toHaveLength(0)
})

it('still clears authorization on a superseded but current-connection request failure', async () => {
  const { cache, entry, candles } = setup()
  await cache.load(entry, 'latest')
  const activation = deferred<CandleSeries>()
  candles.mockReturnValueOnce(activation.promise).mockResolvedValueOnce(series(candle(120_000, 20)))
  const oldRead = cache.load(entry, 'latest')
  await cache.load(entry, 'refresh')
  activation.reject(new ApiError('http', 'Unauthorized.', 401))
  await oldRead
  expect(cache.entries.size).toBe(0)
})
