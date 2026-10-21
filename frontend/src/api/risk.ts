import { isCount, isGuid, isIsoTimestamp, isListOf, isNullOr, isRecord, isSummaryDecimal, isText } from './guards'

/**
 * Open risk (heat) across the owner's Planned and Open real Plays (`GET /api/risk/open`, Phase 2 item 1). Amounts are
 * exact decimal strings in nominal USD; long and short are never netted. A Play that cannot be measured is counted as
 * unknown and never partly summed. The soft limit is the owner's and is flagged, never enforced.
 */
export interface OpenRiskDirection {
  /** The known Plays' sum, "0" when none is known. */
  knownUsd: string
  knownCount: number
  unknownCount: number
  /** A Play's risk did not terminate within 28 places and was rounded once. */
  rounded: boolean
}
export interface OpenRiskPlay {
  playId: string
  title: string
  direction: 'long' | 'short'
  status: 'planned' | 'open'
  accountId: string
  riskUsd: string | null
  reason: string | null
  rounded: boolean
  /** The monitor's last recorded stop coverage of an Open venue Play; null for Planned and manual Plays. */
  protection: OpenRiskProtectionState | null
}
export type OpenRiskProtectionState = 'covered' | 'shared' | 'not-covered' | 'unknown' | 'unsupported' | 'stale' | 'not-checked'
const protectionStates: readonly string[] = ['covered', 'shared', 'not-covered', 'unknown', 'unsupported', 'stale', 'not-checked']
const unverifiedStates: readonly string[] = ['unknown', 'unsupported', 'stale', 'not-checked']
export interface OpenRisk {
  asOfUtc: string
  scope: string
  balance: { amountUsd: string | null; contributingAccountCount: number; totalAccountCount: number }
  long: OpenRiskDirection
  short: OpenRiskDirection
  notIncluded: { reason: 'paused' | 'unresolved'; count: number }[]
  /** Open venue Plays whose recorded coverage confirms no covering stop, and those whose coverage is not verified. */
  protection: { notCovered: number; unverified: number }
  plays: OpenRiskPlay[]
  limitPercent: string | null
  notice: string
}
export interface RiskSettings { openRiskLimitPercent: string | null }

/** The parts of a plan open risk reads; numbers are the editor's text and may be blank in a Draft. */
export interface RiskPlan {
  direction: 'long' | 'short'
  sizingMode: 'margin' | 'quantity'
  size: string
  leverage: string
  entries: { id: string; share: string; price: string; stops: { id: string; unit: 'price' | 'percent'; value: string; share: string }[] }[]
}
export interface OpenRiskPreviewRequest { playId: string | null; accountId: string; plan: RiskPlan }
export interface OpenRiskPreview {
  playId: string | null
  direction: 'long' | 'short'
  riskUsd: string | null
  reason: string | null
  rounded: boolean
}

const direction = (v: unknown): v is OpenRiskDirection => isRecord(v) && isSummaryDecimal(v.knownUsd) && !v.knownUsd.startsWith('-') &&
  isCount(v.knownCount) && isCount(v.unknownCount) && typeof v.rounded === 'boolean'
const risk = (v: unknown) => isNullOr(v, x => isSummaryDecimal(x) && !(x as string).startsWith('-'))
const reason = (v: unknown) => isNullOr(v, x => isText(x, 500))
const play = (v: unknown): v is OpenRiskPlay => isRecord(v) && isGuid(v.playId) && isText(v.title, 200) &&
  (v.direction === 'long' || v.direction === 'short') && (v.status === 'planned' || v.status === 'open') && isGuid(v.accountId) &&
  risk(v.riskUsd) && reason(v.reason) && (v.riskUsd === null) !== (v.reason === null) && typeof v.rounded === 'boolean' &&
  (v.protection === null || protectionStates.includes(v.protection as string))

export const isOpenRisk = (v: unknown, scope: string): v is OpenRisk => isRecord(v) && isIsoTimestamp(v.asOfUtc) && v.scope === scope &&
  isRecord(v.balance) && isNullOr(v.balance.amountUsd, isSummaryDecimal) && isCount(v.balance.contributingAccountCount) &&
  isCount(v.balance.totalAccountCount) && v.balance.contributingAccountCount <= v.balance.totalAccountCount &&
  direction(v.long) && direction(v.short) &&
  isListOf(v.notIncluded, x => isRecord(x) && (x.reason === 'paused' || x.reason === 'unresolved') && isCount(x.count) && x.count > 0, 2) &&
  isRecord(v.protection) && isCount(v.protection.notCovered) && isCount(v.protection.unverified) && isListOf(v.plays, play, 10_000) &&
  v.long.knownCount + v.long.unknownCount + v.short.knownCount + v.short.unknownCount === v.plays.length &&
  v.protection.notCovered === v.plays.filter(p => (p as OpenRiskPlay).protection === 'not-covered').length &&
  v.protection.unverified === v.plays.filter(p => unverifiedStates.includes((p as OpenRiskPlay).protection ?? '')).length &&
  isNullOr(v.limitPercent, isSummaryDecimal) && isText(v.notice, 4000)

export const isRiskSettings = (v: unknown): v is RiskSettings => isRecord(v) && isNullOr(v.openRiskLimitPercent, isSummaryDecimal)

export const isOpenRiskPreview = (v: unknown, request: OpenRiskPreviewRequest): v is OpenRiskPreview => isRecord(v) &&
  v.playId === request.playId && v.direction === request.plan.direction && risk(v.riskUsd) && reason(v.reason) &&
  (v.riskUsd === null) !== (v.reason === null) && typeof v.rounded === 'boolean'

export const openRiskPath = (scope: string) => `/api/risk/open?scope=${encodeURIComponent(scope)}`
