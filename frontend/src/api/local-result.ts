import { isNonBlankText, isNullOr, isPlainDecimal, isRecord } from './guards'

/**
 * The Play-local result beside the scored venue result (October 19, 2026): the play's own fills, sell proceeds minus buy
 * costs (direction × (exits − entries)), less every trading fee. No funding, not total profit, never scored. A manual ledger's result is its Play-local result.
 */
export interface PlayLocalResult {
  /** Exact decimal string; null when unknown. */
  netUsd: string | null
  /** `executions` for linked venue fills, `manual-ledger` for owner-entered facts. */
  basis: 'executions' | 'manual-ledger' | 'paper-simulation'
  /** Why the result is unknown, in the owner's words; null when known. */
  reason: string | null
  /** Both results are known and their signs (negative, zero, positive) differ. */
  signDiffers: boolean
}

/** A known result has no reason, an unknown one has a reason and never a sign difference. */
export const isPlayLocalResult = (v: unknown, decimal: (x: unknown) => boolean = isPlainDecimal): v is PlayLocalResult => isRecord(v) &&
  isNullOr(v.netUsd, decimal) && (v.basis === 'executions' || v.basis === 'manual-ledger' || v.basis === 'paper-simulation') &&
  isNullOr(v.reason, x => isNonBlankText(x, 500)) && typeof v.signDiffers === 'boolean' &&
  (v.netUsd === null) === (v.reason !== null) && (v.netUsd !== null || !v.signDiffers)

/** Absent from servers before October 19, 2026. */
export const isOptionalPlayLocalResult = (v: unknown, decimal?: (x: unknown) => boolean) => v === undefined || isPlayLocalResult(v, decimal)
