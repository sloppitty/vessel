import { isCount, isNullOr, isPlainDecimal, isRecord, isText, isTimestamp } from './guards'

/**
 * The owner's risk setting, closed-play record and the limits suggestions follow (`GET /api/sizing`).
 * Numbers are invariant decimal strings; unknowns are null. Mechanical estimates, not investment advice.
 */
export type ExposureLevel = 'full' | 'half' | 'quarter'
export type PlayOutcome = 'win' | 'loss' | 'scratch'

export interface SizingSettings { riskPercent: string }
export interface SizingExposure {
  level: ExposureLevel
  /** 1, 0.5 or 0.25 of the risk setting. */
  multiplier: string
  reason: string
  lossStreak: number
  winsSinceStepDown: number
  /**
   * Decided results sharing an exact exit time with another. They keep a stable, non-economic order and the reason
   * says so when above zero. Absent from servers before October 19, 2026.
   */
  tiedOutcomes?: number
}
/** Where the record comes from: closed Plays, closed Activity positions while no Play has a win or loss, or neither. */
export type RecordSource = 'plays' | 'activity' | 'none'
export interface RecordedPlay {
  /** Null for an Activity position, which is not a Play. */
  playId: string | null
  /** An Activity fallback outcome's operation and account; absent or null for a Play. */
  operationId?: string | null
  accountId?: string | null
  title: string
  instrument: string | null
  /** The latest valid linked exit of a venue-linked Play, the owner-entered exit of a manual ledger, an operation's closing fill. */
  closedAtUtc: string
  /** Audit only: when a check or refresh detected a venue-linked Play's close; absent or null otherwise. */
  detectedAtUtc?: string | null
  netResultUsd: string
  returnPercent: string | null
  riskUsd: string | null
  rMultiple: string | null
  outcome: PlayOutcome
  /** False when fees in another token were left out of the net result. */
  feesComplete: boolean
}
export interface SizingRecord {
  source: RecordSource
  /** Closed outcomes of the source: Plays, or Activity positions when `source` is `activity`. */
  closedPlays: number
  decidedPlays: number
  window: number
  wins: number
  losses: number
  scratches: number
  battingAverage: string | null
  averageGainPercent: string | null
  averageLossPercent: string | null
  winLossRatio: string | null
  breakEvenRewardRisk: string | null
  averageWinR: string | null
  averageLossR: string | null
  recent: RecordedPlay[]
  /**
   * Decided results in the window whose Play-local result differs in sign from the scored venue result, and those with no
   * Play-local result. Null for the Activity fallback (positions are not Plays). These four are absent from servers before
   * October 19, 2026.
   */
  localSignChanges?: number | null
  localUnknown?: number | null
  /** Losses in the window whose scored result before fees is zero or better. Win, loss and scratch are unchanged. */
  feeOnlyLosses?: number
  /** Losses in the window whose scored result before fees is unknown; not counted as fee-only. */
  unknownGrossLosses?: number
}
export interface SizingLimits {
  effectiveRiskPercent: string
  /** Largest stop distance from its entry, in percent of the entry price. */
  maxStopPercent: string
  minRewardRisk: string
  maxStopSource: 'default' | 'averageGain'
  minRewardRiskSource: 'default' | 'battingAverage'
}
export interface SizingDocument {
  settings: SizingSettings
  exposure: SizingExposure
  record: SizingRecord
  limits: SizingLimits
  notice: string
}

/** The risk setting the API accepts, in percent of the balance. Above the warning level the editor warns. */
export const riskPercentRange = { min: 0.1, max: 5, warnAbove: 2.5 } as const

const recorded = (v: unknown): v is RecordedPlay => isRecord(v) && isNullOr(v.playId, x => isText(x, 64)) && isText(v.title, 200) &&
  (v.operationId === undefined || isNullOr(v.operationId, x => isText(x, 48))) && (v.accountId === undefined || isNullOr(v.accountId, x => isText(x, 64))) &&
  isNullOr(v.instrument, x => isText(x, 128)) && isTimestamp(v.closedAtUtc, 64) &&
  (v.detectedAtUtc === undefined || isNullOr(v.detectedAtUtc, x => isTimestamp(x, 64))) && isPlainDecimal(v.netResultUsd) &&
  isNullOr(v.returnPercent, isPlainDecimal) && isNullOr(v.riskUsd, isPlainDecimal) && isNullOr(v.rMultiple, isPlainDecimal) &&
  ['win', 'loss', 'scratch'].includes(v.outcome as string) && typeof v.feesComplete === 'boolean'
const record = (v: unknown): v is SizingRecord => isRecord(v) && ['plays', 'activity', 'none'].includes(v.source as string) &&
  ['closedPlays', 'decidedPlays', 'window', 'wins', 'losses', 'scratches'].every(k => isCount(v[k])) &&
  ['battingAverage', 'averageGainPercent', 'averageLossPercent', 'winLossRatio', 'breakEvenRewardRisk', 'averageWinR', 'averageLossR']
    .every(k => isNullOr(v[k], isPlainDecimal)) &&
  Array.isArray(v.recent) && v.recent.length <= 100 && v.recent.every(recorded) &&
  ['localSignChanges', 'localUnknown'].every(k => v[k] === undefined || isNullOr(v[k], x => isCount(x) && x <= (v.decidedPlays as number))) &&
  ['feeOnlyLosses', 'unknownGrossLosses'].every(k => v[k] === undefined || isCount(v[k]) && (v[k] as number) <= (v.losses as number))

export const isSizingDocument = (v: unknown): v is SizingDocument => isRecord(v) &&
  isRecord(v.settings) && isPlainDecimal(v.settings.riskPercent) &&
  isRecord(v.exposure) && ['full', 'half', 'quarter'].includes(v.exposure.level as string) && isPlainDecimal(v.exposure.multiplier) &&
  isText(v.exposure.reason, 2000) && isCount(v.exposure.lossStreak) && isCount(v.exposure.winsSinceStepDown) &&
  (v.exposure.tiedOutcomes === undefined || isCount(v.exposure.tiedOutcomes)) &&
  record(v.record) &&
  isRecord(v.limits) && ['effectiveRiskPercent', 'maxStopPercent', 'minRewardRisk'].every(k => isPlainDecimal((v.limits as Record<string, unknown>)[k])) &&
  ['default', 'averageGain'].includes(v.limits.maxStopSource as string) &&
  ['default', 'battingAverage'].includes(v.limits.minRewardRiskSource as string) &&
  isText(v.notice, 2000)
