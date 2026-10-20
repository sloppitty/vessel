import type { PlayReceipt, PlayStatus } from '@/api/plays'
import { duration } from '@/features/workspace/activity-format'

/** From the first entry fill to the last exit fill once closed; so far while open. Unknown before any fill. */
export function timeHeld(receipt: PlayReceipt, status: PlayStatus, now: string = new Date().toISOString()) {
  if (receipt.firstEntryAtUtc === null) return null
  if (status === 'closed' && receipt.lastExitAtUtc !== null) return duration(receipt.firstEntryAtUtc, receipt.lastExitAtUtc)
  return status === 'open' ? `${duration(receipt.firstEntryAtUtc, now)} so far` : null
}
