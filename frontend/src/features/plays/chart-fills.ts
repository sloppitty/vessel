import type { CandleInterval } from '@/api/candles'
import { intervalMs } from '@/components/chart/intervals'
import type { ChartFocus, ChartMarker } from '@/components/chart/types'

/*
 * Fills on the chart (phase 1, 1.1b): each linked or manual fill as a buy or sell marker at its time and price, never a
 * level line, and an ended trade framed by its own dates instead of today.
 */

/** One fill to mark on the chart: exact decimal text as the venue or the ledger reports it. */
export interface ChartFill { id: string; atUtc: string; price: string; quantity: string; side: 'buy' | 'sell'; role: 'entry' | 'exit' }

const fillTime = new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'UTC' })

/** Markers for the fills that can be placed: a valid time and a positive price. */
export function fillMarkers(fills: readonly ChartFill[]): ChartMarker[] {
  return fills.flatMap(fill => {
    const time = Date.parse(fill.atUtc), price = Number(fill.price)
    if (!Number.isFinite(time) || !Number.isFinite(price) || price <= 0) return []
    return [{ id: fill.id, time, price, side: fill.side,
      tooltip: `${fill.role === 'entry' ? 'Entry' : 'Exit'} fill · ${fill.side === 'buy' ? 'Buy' : 'Sell'} ${fill.quantity} at ${fill.price}\n${fillTime.format(time)} UTC` }]
  })
}

/** The trade's dates with room either side: a quarter of its length, and at least five candles. */
export function tradeFocus(markers: readonly ChartMarker[], interval: CandleInterval): ChartFocus | null {
  if (!markers.length) return null
  const from = Math.min(...markers.map(marker => marker.time)), to = Math.max(...markers.map(marker => marker.time))
  const pad = Math.max((to - from) / 4, 5 * intervalMs(interval))
  return { from: from - pad, to: to + pad }
}
