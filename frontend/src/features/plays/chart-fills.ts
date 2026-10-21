import type { CandleInterval } from '@/api/candles'
import { candleSpan, intervalMs } from '@/components/chart/intervals'
import type { ChartFocus, ChartMarker } from '@/components/chart/types'
import type { DisplayZone } from '@/lib/display-zone'
import { formatTime } from '@/lib/time'

/*
 * Fills on the chart (phase 1, 1.1b): each linked or manual fill as a buy or sell marker at its time and price, never a
 * level line, and an ended trade framed by its own dates instead of today.
 */

/** One fill to mark on the chart: exact decimal text as the venue or the ledger reports it. */
export interface ChartFill { id: string; atUtc: string; price: string; quantity: string; side: 'buy' | 'sell'; role: 'entry' | 'exit' }

/**
 * Markers for the fills that can be placed: a valid time and a positive price, on candles of `interval`. Tooltips read
 * in the display zone (1.6e).
 */
export function fillMarkers(fills: readonly ChartFill[], interval: CandleInterval, zone: DisplayZone): ChartMarker[] {
  return fills.flatMap(fill => {
    const time = Date.parse(fill.atUtc), price = Number(fill.price)
    if (!Number.isFinite(time) || !Number.isFinite(price) || price <= 0) return []
    return [{ id: fill.id, time, price, side: fill.side, span: candleSpan(interval),
      tooltip: `${fill.role === 'entry' ? 'Entry' : 'Exit'} fill · ${fill.side === 'buy' ? 'Buy' : 'Sell'} ${fill.quantity} at ${fill.price}\n${formatTime(time, zone, 'datetime-seconds')}` }]
  })
}

/** The trade's dates with room either side: a quarter of its length, and at least five candles. */
export function tradeFocus(markers: readonly ChartMarker[], interval: CandleInterval): ChartFocus | null {
  if (!markers.length) return null
  // One pass, not spread arguments: a long history has more fills than a call can take.
  let from = Infinity, to = -Infinity
  for (const marker of markers) { from = Math.min(from, marker.time); to = Math.max(to, marker.time) }
  const pad = Math.max((to - from) / 4, 5 * intervalMs(interval))
  // Whole milliseconds: the end is a candle request's end time.
  return { from: Math.floor(from - pad), to: Math.ceil(to + pad) }
}
