import type { PlayReceipt, PlayStatus } from '@/api/plays'
import { duration } from '@/features/workspace/activity-format'

/** From the first entry fill to the last exit fill once closed; so far while open. Unknown before any fill. */
export function timeHeld(receipt: PlayReceipt, status: PlayStatus, now: string = new Date().toISOString()) {
  if (receipt.firstEntryAtUtc === null) return null
  if (status === 'closed' && receipt.lastExitAtUtc !== null) return duration(receipt.firstEntryAtUtc, receipt.lastExitAtUtc)
  return status === 'open' ? `${duration(receipt.firstEntryAtUtc, now)} so far` : null
}

/**
 * R to two places from its exact string. A non-zero R that would round to zero reads "−<0.01R" or "+<0.01R", so a small
 * loss never looks like a scratch; only an exact zero reads "0.00R".
 */
export function multiple(value: string) {
  const negative = value.startsWith('-')
  const magnitude = negative ? value.slice(1) : value
  if (!/[1-9]/.test(magnitude)) return '0.00R'
  if (/^0\.00[0-4]/.test(magnitude)) return `${negative ? '−' : '+'}<0.01R`
  return `${new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2, signDisplay: 'exceptZero' })
    .format(value as Intl.StringNumericLiteral)}R`
}
