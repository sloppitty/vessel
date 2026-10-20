import type { CandleInterval } from '@/api/candles'

const units: Record<string, [string, string]> = { m: ['minute', ''], h: ['hour', ''], d: ['day', 'D'], w: ['week', 'W'], M: ['month', 'M'] }

/** Compact label in the style of trading terminals: 5m, 4h, D, 3D, W, M. */
export function intervalLabel(interval: CandleInterval) {
  const count = interval.slice(0, -1)
  const short = units[interval.slice(-1)]![1]
  return short ? `${count === '1' ? '' : count}${short}` : interval
}

export function intervalName(interval: CandleInterval) {
  const count = Number(interval.slice(0, -1))
  const unit = units[interval.slice(-1)]![0]
  return `${count} ${unit}${count === 1 ? '' : 's'}`
}

const unitMs: Record<string, number> = { m: 60_000, h: 3_600_000, d: 86_400_000, w: 604_800_000, M: 2_592_000_000 }

/** The interval's length in milliseconds; a month counts as 30 days, enough for padding a view. */
export function intervalMs(interval: CandleInterval) {
  return Number(interval.slice(0, -1)) * unitMs[interval.slice(-1)]!
}
