import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { MarketContext, WorkspaceApi } from '@/api/workspace'
import { ApiError, errorMessage } from '@/api/errors'
import { candleCache } from './candleCache'
import { numberFormat } from '@/lib/format'

/**
 * Venue header statistics, loaded once per instrument, refreshed with the chart and replaced by live updates.
 * Disabled for venues that do not advertise market context: nothing is requested and no error is shown.
 */
export function useMarketContext(api: WorkspaceApi, accountId: string, instrument: string, enabled = true) {
  const key = `${accountId}|${instrument}`
  const scope = useMemo(() => ({ api, accountId, instrument, enabled }), [api, accountId, instrument, enabled])
  const [state, setState] = useState<{ scope: typeof scope; context: MarketContext | null; error: string | null }>({ scope, context: null, error: null })
  const controller = useRef<AbortController | null>(null)
  const active = useRef<typeof scope | null>(null)

  /** Replaces the statistics with a streamed or REST observation unless a newer one is already shown. */
  const apply = useCallback((context: MarketContext) => {
    if (!enabled || active.current !== scope || context.instrument !== instrument) return
    setState(previous => previous.scope === scope && previous.context &&
      Date.parse(previous.context.observedAt) > Date.parse(context.observedAt) ? previous : { scope, context, error: null })
  }, [enabled, instrument, scope])

  const load = useCallback(() => {
    if (!enabled || active.current !== scope) return
    controller.current?.abort()
    const abort = new AbortController()
    controller.current = abort
    api.marketContext(accountId, instrument, abort.signal).then(context => {
      if (!abort.signal.aborted) apply(context)
    }, (error: unknown) => {
      if (abort.signal.aborted) return
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) candleCache(api).invalidate()
      if (error instanceof ApiError && (error.status === 404 || error.status === 409)) candleCache(api).invalidate(accountId)
      if (!abort.signal.aborted) setState(previous => ({ scope, context: previous.scope === scope ? previous.context : null,
        error: errorMessage(error, 'Market statistics could not be loaded.') }))
    })
  }, [api, accountId, instrument, enabled, scope, apply])

  useEffect(() => {
    active.current = scope
    const unsubscribe = candleCache(api).onInvalidate(accountId, () => controller.current?.abort())
    load()
    return () => { active.current = null; unsubscribe(); controller.current?.abort() }
  }, [load, api, accountId, scope])

  return { key, context: state.scope === scope ? state.context : null, error: state.scope === scope ? state.error : null, refresh: load, apply }
}

const decimals = (value: string) => value.split('.')[1]?.length ?? 0
const grouped = numberFormat({ maximumFractionDigits: 0 })
const groupedPrecise = numberFormat({ maximumFractionDigits: 2 })

/** Display-only statistics derived from venue strings; not valuation or performance figures. */
export function describeMarket(context: MarketContext) {
  const mark = Number(context.markPrice)
  // Without a previous-day price there is no 24-hour change to show.
  const previous = context.previousDayPrice === null ? null : Number(context.previousDayPrice)
  const difference = previous === null ? 0 : mark - previous
  const places = Math.max(decimals(context.markPrice), decimals(context.previousDayPrice ?? '0'))
  const sign = difference > 0 ? '+' : difference < 0 ? '−' : ''
  const percent = previous !== null && previous > 0 ? Math.abs(difference / previous * 100).toFixed(2) : null
  return {
    direction: difference > 0 ? 'up' as const : difference < 0 ? 'down' as const : 'flat' as const,
    change: previous === null ? '—' : `${sign}${Math.abs(difference).toFixed(places)}${percent === null ? '' : ` / ${sign}${percent}%`}`,
    volume: grouped.format(Number(context.dayNotionalVolume)),
    openInterest: groupedPrecise.format(Number(context.openInterest)),
    funding: `${Number(context.fundingRate) < 0 ? '−' : ''}${Math.abs(Number(context.fundingRate) * 100).toFixed(4)}%`,
  }
}
