import { StrictMode } from 'react'
import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/errors'
import { createWorkspaceApi, type MarketContext, type WorkspaceApi } from '@/api/workspace'
import { candleCache } from './candleCache'
import { useMarketContext } from './useMarketContext'
import { credentialsFixture } from '@/test/identity-fixture'

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

const context = (observedAt = '2026-10-14T12:00:00Z', instrument = 'BTC'): MarketContext => ({
  venueId: 'test', instrument, observedAt, markPrice: '12345.00000001', oraclePrice: '12344.9', midPrice: null,
  previousDayPrice: null, dayNotionalVolume: '9999999999999999.1234', openInterest: '500.0001',
  fundingRate: '0.00001234', premium: null, notice: 'Synthetic observation.',
})

function source() {
  const requests: ReturnType<typeof deferred<MarketContext>>[] = []
  const marketContext = vi.fn<WorkspaceApi['marketContext']>().mockImplementation(() => {
    const request = deferred<MarketContext>()
    requests.push(request)
    return request.promise
  })
  return { api: { ...createWorkspaceApi(credentialsFixture), marketContext }, requests, marketContext }
}

type Props = { api: WorkspaceApi; accountId: string; instrument: string; enabled: boolean }
function setup() {
  const original = source()
  const props: Props = { api: original.api, accountId: 'account-a', instrument: 'BTC', enabled: true }
  const view = renderHook(({ api, accountId, instrument, enabled }: Props) =>
    useMarketContext(api, accountId, instrument, enabled), { initialProps: props })
  return { ...original, props, ...view }
}

beforeEach(() => vi.useFakeTimers())
afterEach(() => vi.useRealTimers())

describe('Market context observations', () => {
  it('loads once, preserves venue strings and refreshes only on request', async () => {
    const view = setup()
    expect(view.marketContext).toHaveBeenCalledWith('account-a', 'BTC', expect.any(AbortSignal))
    expect(view.result.current.context).toBeNull()
    const observed = context()
    await act(async () => view.requests[0]!.resolve(observed))
    expect(view.result.current.context).toBe(observed)
    act(() => vi.advanceTimersByTime(300_000))
    expect(view.marketContext).toHaveBeenCalledTimes(1)
    act(() => view.result.current.refresh())
    expect(view.marketContext).toHaveBeenCalledTimes(2)
    expect(view.result.current.context).toBe(observed)
  })

  it('ignores observations for other instruments and older streamed observations', async () => {
    const view = setup()
    await act(async () => view.requests[0]!.resolve(context()))
    const latest = context('2026-10-14T12:02:00Z')
    act(() => view.result.current.apply(latest))
    act(() => view.result.current.apply(context('2026-10-14T12:03:00Z', 'ETH')))
    act(() => view.result.current.apply(context('2026-10-14T12:01:00Z')))
    expect(view.result.current.context).toBe(latest)
    const equalTime = { ...latest, markPrice: '12346.00000001' }
    act(() => view.result.current.apply(equalTime))
    expect(view.result.current.context).toBe(equalTime)
  })

  it('does not regress a newer streamed observation when an older REST request finishes', async () => {
    const view = setup()
    const latest = context('2026-10-14T12:02:00Z')
    act(() => view.result.current.apply(latest))
    await act(async () => view.requests[0]!.resolve(context()))
    expect(view.result.current.context).toBe(latest)
  })

  it('keeps the last observation on refresh failure and clears the error on apply', async () => {
    const view = setup()
    const observed = context()
    await act(async () => view.requests[0]!.resolve(observed))
    act(() => view.result.current.refresh())
    await act(async () => view.requests[1]!.reject(new Error('Synthetic failure')))
    expect(view.result.current.context).toBe(observed)
    expect(view.result.current.error).toBe('Market statistics could not be loaded.')
    const latest = context('2026-10-14T12:03:00Z')
    act(() => view.result.current.apply(latest))
    expect(view.result.current.context).toBe(latest)
    expect(view.result.current.error).toBeNull()
  })
})

describe('Market context request lifetime', () => {
  it.each(['resolve', 'reject'] as const)('aborts the previous refresh and ignores its late %s', async finish => {
    const view = setup()
    const firstSignal = view.marketContext.mock.calls[0]![2]!
    act(() => view.result.current.refresh())
    expect(firstSignal.aborted).toBe(true)
    const latest = context('2026-10-14T12:02:00Z')
    await act(async () => view.requests[1]!.resolve(latest))
    await act(async () => finish === 'resolve' ? view.requests[0]!.resolve(context())
      : view.requests[0]!.reject(new ApiError('http', 'Obsolete failure', 401)))
    expect(view.result.current.context).toBe(latest)
    expect(view.result.current.error).toBeNull()
  })

  it.each(['account', 'instrument', 'api'] as const)('clears stale data and ignores old callbacks after %s replacement', async replacement => {
    const view = setup()
    await act(async () => view.requests[0]!.resolve(context()))
    const oldApply = view.result.current.apply
    const oldRefresh = view.result.current.refresh
    act(() => oldRefresh())
    const oldSignal = view.marketContext.mock.calls[1]![2]!
    const nextSource = source()
    const next = { ...view.props,
      ...(replacement === 'account' ? { accountId: 'account-b' } : {}),
      ...(replacement === 'instrument' ? { instrument: 'ETH' } : {}),
      ...(replacement === 'api' ? { api: nextSource.api } : {}),
    }
    view.rerender(next)
    expect(oldSignal.aborted).toBe(true)
    expect(view.result.current.context).toBeNull()
    expect(view.result.current.error).toBeNull()
    const currentSource = replacement === 'api' ? nextSource : view
    const latestRequest = currentSource.requests.at(-1)!
    const latestSignal = currentSource.marketContext.mock.calls.at(-1)![2]!
    const latest = context('2026-10-14T12:03:00Z', next.instrument)
    await act(async () => latestRequest.resolve(latest))
    act(() => { oldApply(context('2026-10-14T12:04:00Z')); oldRefresh() })
    await act(async () => view.requests[1]!.resolve(context()))
    expect(view.result.current.context).toBe(latest)
    expect(view.result.current.error).toBeNull()
    expect(latestSignal.aborted).toBe(false)
    expect(currentSource.marketContext).toHaveBeenCalledTimes(replacement === 'api' ? 1 : 3)
  })

  it('does not load or accept observations for an unsupported venue and aborts when disabled', async () => {
    const original = source()
    const props: Props = { api: original.api, accountId: 'account', instrument: 'BTC', enabled: false }
    const view = renderHook((p: Props) => useMarketContext(p.api, p.accountId, p.instrument, p.enabled), { initialProps: props })
    act(() => { view.result.current.refresh(); view.result.current.apply(context()) })
    expect(original.marketContext).not.toHaveBeenCalled()
    expect(view.result.current.context).toBeNull()
    view.rerender({ ...props, enabled: true })
    const signal = original.marketContext.mock.calls[0]![2]!
    await act(async () => original.requests[0]!.resolve(context()))
    view.rerender(props)
    expect(signal.aborted).toBe(true)
    expect(view.result.current.context).toBeNull()
    act(() => view.result.current.refresh())
    expect(original.marketContext).toHaveBeenCalledTimes(1)
  })

  it('aborts on unmount and makes retained callbacks inert', async () => {
    const view = setup()
    const signal = view.marketContext.mock.calls[0]![2]!
    const { apply, refresh } = view.result.current
    view.unmount()
    expect(signal.aborted).toBe(true)
    act(() => { apply(context()); refresh() })
    await act(async () => view.requests[0]!.resolve(context()))
    expect(view.marketContext).toHaveBeenCalledTimes(1)
  })

  it('aborts the discarded StrictMode request and accepts only the active one', async () => {
    const original = source()
    const view = renderHook(() => useMarketContext(original.api, 'account', 'BTC'), { wrapper: StrictMode })
    expect(original.marketContext).toHaveBeenCalledTimes(2)
    expect(original.marketContext.mock.calls[0]![2]!.aborted).toBe(true)
    await act(async () => original.requests[1]!.resolve(context('2026-10-14T12:02:00Z')))
    await act(async () => original.requests[0]!.resolve(context()))
    expect(view.result.current.context?.observedAt).toBe('2026-10-14T12:02:00Z')
  })

  it('aborts its request on cache invalidation but not on another account invalidation', () => {
    const view = setup()
    const signal = view.marketContext.mock.calls[0]![2]!
    act(() => candleCache(view.api).invalidate('other-account'))
    expect(signal.aborted).toBe(false)
    act(() => candleCache(view.api).invalidate('account-a'))
    expect(signal.aborted).toBe(true)
  })

  it.each([401, 403, 404, 409])('keeps cache invalidation scoped for HTTP %s', async status => {
    const view = setup()
    const cache = candleCache(view.api)
    cache.entry('account-a', 'BTC', '1h')
    cache.entry('other-account', 'ETH', '1h')
    await act(async () => view.requests[0]!.reject(new ApiError('http', 'Synthetic access failure', status)))
    expect(cache.entries.size).toBe(status === 401 || status === 403 ? 0 : 1)
    expect(cache.entries.has(JSON.stringify(['account-a', 'BTC', '1h']))).toBe(false)
  })
})
