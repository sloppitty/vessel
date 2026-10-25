import { act, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type MarketStreamEvent, type WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { candleCache, clearCandleCache, evictAccountCandles } from './candleCache'
import { useLiveMarket } from './useLiveMarket'
import { credentialsFixture } from '@/test/identity-fixture'

function setup() {
  let emit!: (event: MarketStreamEvent) => void
  let reject!: (error: unknown) => void
  let signal!: AbortSignal
  const marketStream = vi.fn<WorkspaceApi['marketStream']>().mockImplementation((_account, _query, abort, callback) => {
    signal = abort; emit = callback
    return new Promise<void>((_resolve, no) => { reject = no })
  })
  const api = { ...createWorkspaceApi(credentialsFixture), marketStream }
  const callbacks = { onCandle: vi.fn(), onContext: vi.fn(), refreshCandles: vi.fn(), refreshContext: vi.fn() }
  const view = renderHook(() => useLiveMarket({ api, accountId: 'account', instrument: 'BTC', interval: '1h', enabled: true, ready: true, ...callbacks }))
  const status = (state: 'reconnecting' | 'stale' | 'live') => act(() => emit({ type: 'status', status: { state, observedAt: '2026-10-11T12:00:00Z' } }))
  return { api, marketStream, callbacks, view, status, emit: (event: MarketStreamEvent) => emit(event), signal: () => signal, reject: (error: unknown) => reject(error) }
}
afterEach(() => vi.restoreAllMocks())

describe('live cache lifecycle', () => {
  it('shows the stream admission wait and does not reconnect or poll after a 429', async () => {
    vi.useFakeTimers()
    const { reject, view, marketStream, callbacks } = setup()
    try {
      await act(async () => reject(new ApiError('http', 'Generic failure.', 429, new Date(Date.now() + 60_000).toISOString())))
      expect(view.result.current).toMatchObject({ state: 'stale', notice: 'Too many requests. Wait 60 seconds before trying again.' })
      await act(async () => { await vi.advanceTimersByTimeAsync(120_000) })
      act(() => document.dispatchEvent(new Event('visibilitychange')))
      expect(marketStream).toHaveBeenCalledOnce()
      expect(callbacks.refreshCandles).not.toHaveBeenCalled()
      expect(callbacks.refreshContext).not.toHaveBeenCalled()
    } finally { vi.useRealTimers() }
  })

  it.each(['reconnecting', 'stale'] as const)('revalidates after upstream %s → live without reopening SSE', state => {
    const { callbacks, status, marketStream } = setup()
    status('live'); status(state); status('live'); status('live')
    expect(callbacks.refreshCandles).toHaveBeenCalledTimes(1)
    expect(marketStream).toHaveBeenCalledTimes(1)
  })

  it.each(['disable', 'delete', 'disconnect'])('aborts SSE and ignores late events on %s', reason => {
    const { api, signal, status, callbacks, emit } = setup()
    act(() => reason === 'disconnect' ? clearCandleCache(api) : evictAccountCandles(api, 'account'))
    expect(signal().aborted).toBe(true)
    status('live')
    act(() => emit({ type: 'candle', candle: { openTime: 1, closeTime: 2, open: '1', high: '2', low: '1', close: '2', volume: '1', trades: 1 } }))
    expect(callbacks.onCandle).not.toHaveBeenCalled()
    expect(callbacks.refreshCandles).not.toHaveBeenCalled()
  })

  it.each([401, 403])('clears the connection rather than reconnecting after stream HTTP %s', async code => {
    const { api, reject, signal } = setup()
    const cache = candleCache(api)
    cache.entry('other-account', 'ETH', '1h')
    await act(async () => reject(new ApiError('http', 'Unauthorized.', code)))
    expect(signal().aborted).toBe(true)
    expect(cache.entries.size).toBe(0)
  })
})
