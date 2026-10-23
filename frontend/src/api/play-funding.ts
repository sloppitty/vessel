import { isCount, isNullOr, isPlainDecimal, isRecord, isText, isTimestamp } from './guards'

/** Why settled funding is not shown as the Play's own, by state (Phase 2, item 9b). */
export const playFundingReasons = {
  shared: ['other-play', 'unlinked-fill', 'open-position'],
  incomplete: ['identity', 'missing-executions', 'not-settled', 'omitted-executions', 'history-gap', 'history-unproven',
    'flat-start-unproven', 'flat-end-unproven', 'operation-boundaries', 'funding-coverage', 'funding-ambiguous', 'funding-asset'],
  unavailable: ['no-funding-read', 'account-disabled'],
} as const
type Reason<S extends keyof typeof playFundingReasons> = typeof playFundingReasons[S][number]

/**
 * Settled funding beside a Play (Phase 2, item 9b), shown only with proven sole exposure over its holding window: from
 * the first linked fill to the last once its fills are flat again, otherwise to the latest covered read. `sole` carries
 * the exact nominal sum of the settlements in the window, their asset when they share one, their count and the window;
 * every other state only its reason. The amount is never added to the venue or Play-local result.
 */
export type PlayFunding =
  | { state: 'sole'; reason: null; amountUsd: string; asset: string | null; paymentCount: number; fromUtc: string; toUtc: string }
  | { state: 'shared'; reason: Reason<'shared'>; amountUsd: null; asset: null; paymentCount: null; fromUtc: null; toUtc: null }
  | { state: 'incomplete'; reason: Reason<'incomplete'>; amountUsd: null; asset: null; paymentCount: null; fromUtc: null; toUtc: null }
  | { state: 'unavailable'; reason: Reason<'unavailable'>; amountUsd: null; asset: null; paymentCount: null; fromUtc: null; toUtc: null }

const timeLength = 64

/** A sole sum comes with its window and count and no reason; any other state with a known reason and nothing else. */
export const isPlayFunding = (v: unknown): v is PlayFunding => {
  if (!isRecord(v)) return false
  if (v.state === 'sole')
    return v.reason === null && isPlainDecimal(v.amountUsd) && isNullOr(v.asset, x => isText(x, 64) && x.length > 0) && isCount(v.paymentCount) &&
      isTimestamp(v.fromUtc, timeLength) && isTimestamp(v.toUtc, timeLength) && Date.parse(v.fromUtc) <= Date.parse(v.toUtc)
  return (v.state === 'shared' || v.state === 'incomplete' || v.state === 'unavailable') &&
    (playFundingReasons[v.state] as readonly unknown[]).includes(v.reason) &&
    v.amountUsd === null && v.asset === null && v.paymentCount === null && v.fromUtc === null && v.toUtc === null
}

/** Absent for a manual instrument, before the first entry fill, and from servers before Phase 2, item 9b. */
export const isOptionalPlayFunding = (v: unknown) => v === undefined || isPlayFunding(v)
