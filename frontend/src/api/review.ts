import { isTradingMode, type TradingMode } from './paper'
import { isCount, isGuid, isListOf, isNonBlankText, isNullOr, isRecord, isReviewDecimal, isText, isTimestamp } from './guards'
import { isOptionalPlayLocalResult, type PlayLocalResult } from './local-result'

export const reviewPeriods = [
  { id: 'day', label: 'Last 24 hours' }, { id: 'week', label: 'Last 7 days' },
  { id: 'month', label: 'Last 30 days' }, { id: 'year', label: 'Last 365 days' },
  { id: 'all', label: 'All recorded' },
] as const
export type ReviewPeriod = typeof reviewPeriods[number]['id']
export type ReviewGroupBy = 'portfolios' | 'accounts' | 'assets'
/** `auto` adds imported positions while the selection has fewer than `minPlays` scored Plays. */
export type ReviewPositionsMode = 'auto' | 'include' | 'exclude'
export interface ReviewQuery {
  period: ReviewPeriod; portfolio: string; accountId?: string; instrument?: string; offset?: number
  positions?: ReviewPositionsMode; minPlays?: number
}
export const defaultReviewQuery: ReviewQuery = { period: 'month', portfolio: 'all' }
/** Authoritative statistics are decimal strings from the API, never recomputed from a paged sample. */
export interface ReviewMetrics {
  count: number; wins: number; losses: number; scratches: number
  net: string | null; batting: string | null; averageGain: string | null; averageLoss: string | null
  payoff: string | null; profitFactor: string | null; averageWin: string | null; averageLossUsd: string | null
  expectancy: string | null
  /** Estimated R (planned-stop model), over the `riskCount` Plays with a complete initial stop. */
  averageR: string | null; riskCount: number; reviewed: number
  drawdown: string | null; maxWins: number; maxLosses: number
  /** Plays whose Play-local and venue results differ in sign. These four are absent from servers before October 19, 2026. */
  signDiffers?: number
  /** Plays with no Play-local result. */
  localUnknown?: number
  /** Losses whose venue result before fees is zero or better. Win, loss and scratch are unchanged. */
  feeOnlyLosses?: number
  /** Losses whose venue result before fees is unknown; not counted as fee-only. */
  unknownGrossLosses?: number
  /** Imported positions among `count`; absent from servers before October 20, 2026. */
  positions?: number
}
export interface ReviewGroup { id: string; name: string; detail: string; metrics: ReviewMetrics }
export interface ReviewPoint { atUtc: string; net: string; cumulative: string; drawdown: string; count: number }
/** A scored Play, or an imported position (origin `position`: no ID, R, review or Play-local result). */
export interface ReviewPlay {
  id: string | null; operationId?: string | null; title: string; accountId: string; accountName: string; venueId: string; instrument: string
  closedAtUtc: string; net: string; returnPercent: string; rMultiple: string | null; reviewed: boolean
  origin?: 'planned' | 'retrospective' | 'position'
  executionSource?: 'imported' | 'manual' | 'paper'
  /** The Play-local result beside the scored `net`; absent from servers before October 19, 2026. */
  local?: PlayLocalResult | null
  /** The plan was written after the Play's first execution (D-17); scored like any other. Absent from older servers. */
  plannedAfterExecution?: boolean
}
export interface ReviewExclusion {
  playId: string; title: string; accountId: string; accountName: string; instrument: string; reasons: string[]
}
export interface ReviewExclusions {
  items: ReviewExclusion[]; total: number; offset: number; nextOffset: number | null; notice: string
}
export interface ReviewAccount {
  id: string; name: string; portfolioId: string | null; venueId: string
  lastSyncedAtUtc: string | null; syncStatus: string; historyNotice: string | null
}
export interface ReviewDocument {
  tradingMode?: TradingMode
  asOfUtc: string; fromUtc: string | null; period: ReviewPeriod; currency: 'USD'
  options: { portfolios: Array<{ id: string; name: string }>; accounts: ReviewAccount[]; instruments: string[] }
  periods: Array<{ id: ReviewPeriod; count: number; net: string | null; positions?: number }>
  metrics: ReviewMetrics; series: ReviewPoint[]; bucketDays: number
  groups: Record<ReviewGroupBy, ReviewGroup[]>
  coverage: {
    closedCandidates: number; incompleteFees: number; incompleteExecutions: number; openPlays: number
    unassignedFills: number; firstFillAtUtc: string | null; notice: string
  }
  plays: ReviewPlay[]; offset: number; nextOffset: number | null
  /** Whether imported positions are in this report; absent from servers before October 20, 2026. */
  positions?: ReviewPositions
}
export interface ReviewPositions {
  mode: ReviewPositionsMode; included: boolean; minPlays: number
  /** Scored Plays in the selection, which auto compares with `minPlays`. */
  plays: number
  /** Included positions and closed positions that cannot be scored; null when positions were not read. */
  positions: number | null; omitted: number | null
  /** How many positions one Play counts as in rate metrics. */
  playWeight: string
}
/** Lists hold at most 10,000 items unless stated. */
const listLimit = 10_000
const period = (v: unknown) => reviewPeriods.some(p => p.id === v)
export const isReviewMetrics = (v: unknown): v is ReviewMetrics => isRecord(v) &&
  ['count', 'wins', 'losses', 'scratches', 'riskCount', 'reviewed', 'maxWins', 'maxLosses'].every(key => isCount(v[key])) &&
  ['net', 'batting', 'averageGain', 'averageLoss', 'payoff', 'profitFactor', 'averageWin', 'averageLossUsd', 'expectancy', 'averageR', 'drawdown']
    .every(key => isNullOr(v[key], isReviewDecimal)) &&
  (v.wins as number) + (v.losses as number) + (v.scratches as number) === v.count &&
  (v.positions === undefined || isCount(v.positions) && v.positions <= (v.count as number)) &&
  // Stops, reviews and Play-local results belong to Plays, never to imported positions.
  ['riskCount', 'reviewed', 'signDiffers', 'localUnknown'].every(key => v[key] === undefined ||
    (v[key] as number) <= (v.count as number) - ((v.positions as number | undefined) ?? 0)) &&
  (v.batting === null || Number(v.batting) >= 0 && Number(v.batting) <= 1) &&
  (v.count === 0 ? v.net === null : isReviewDecimal(v.net)) &&
  ['signDiffers', 'localUnknown'].every(key => v[key] === undefined || isCount(v[key])) &&
  ['feeOnlyLosses', 'unknownGrossLosses'].every(key => v[key] === undefined || isCount(v[key]) && (v[key] as number) <= (v.losses as number))
const group = (v: unknown) => isRecord(v) && isText(v.id, 128) && isText(v.name, 200) && isText(v.detail, 2000) && isReviewMetrics(v.metrics)
const play = (v: unknown) => isRecord(v) &&
  (v.origin === 'position' ? v.id === null && isNonBlankText(v.operationId, 200) && (v.local === undefined || v.local === null) &&
    v.reviewed === false && (v.executionSource === undefined || v.executionSource === 'imported') : isGuid(v.id)) && isText(v.title, 200) && isGuid(v.accountId) && isText(v.accountName, 200) &&
  isText(v.venueId, 64) && isText(v.instrument, 128) && isTimestamp(v.closedAtUtc, 64) && isReviewDecimal(v.net) &&
  isReviewDecimal(v.returnPercent) && isNullOr(v.rMultiple, isReviewDecimal) && typeof v.reviewed === 'boolean' &&
  (v.origin === undefined || v.origin === 'planned' || v.origin === 'retrospective' || v.origin === 'position') &&
  (v.executionSource === undefined || v.executionSource === 'imported' || v.executionSource === 'manual' || v.executionSource === 'paper') &&
  (v.origin !== 'retrospective' && v.origin !== 'position' || v.rMultiple === null) &&
  (v.origin === 'position' || isOptionalPlayLocalResult(v.local, isReviewDecimal)) &&
  (!isRecord(v.local) || v.local.basis === 'manual-ledger' === (v.executionSource === 'manual') &&
    (v.local.basis === 'paper-simulation') === (v.executionSource === 'paper')) &&
  (v.plannedAfterExecution === undefined || typeof v.plannedAfterExecution === 'boolean' &&
    (!v.plannedAfterExecution || v.origin !== 'retrospective' && v.origin !== 'position'))
const account = (v: unknown) => isRecord(v) && isGuid(v.id) && isText(v.name, 200) && isNullOr(v.portfolioId, isGuid) &&
  isText(v.venueId, 64) && isNullOr(v.lastSyncedAtUtc, x => isTimestamp(x, 64)) && isText(v.syncStatus, 32) &&
  isNullOr(v.historyNotice, x => isText(x, 1000))
export const isReviewDocument = (v: unknown): v is ReviewDocument => isRecord(v) &&
  (v.tradingMode === undefined || isTradingMode(v.tradingMode)) &&
  isTimestamp(v.asOfUtc, 64) && isNullOr(v.fromUtc, x => isTimestamp(x, 64)) && period(v.period) && v.currency === 'USD' &&
  isRecord(v.options) && isListOf(v.options.portfolios, p => isRecord(p) && isGuid(p.id) && isText(p.name, 200), listLimit) &&
  isListOf(v.options.accounts, account, listLimit) && isListOf(v.options.instruments, i => isText(i, 128), listLimit) &&
  isListOf(v.periods, p => isRecord(p) && period(p.id) && isCount(p.count) && isNullOr(p.net, isReviewDecimal) &&
    (p.positions === undefined || isCount(p.positions) && p.positions <= p.count), 5) &&
  (v.periods as Array<{ id: string }>).length === 5 && new Set((v.periods as Array<{ id: string }>).map(p => p.id)).size === 5 &&
  isReviewMetrics(v.metrics) &&
  isListOf(v.series, p => isRecord(p) && isTimestamp(p.atUtc, 64) && isReviewDecimal(p.net) && isReviewDecimal(p.cumulative) && isReviewDecimal(p.drawdown) && isCount(p.count), 366) &&
  isCount(v.bucketDays) && v.bucketDays > 0 &&
  isRecord(v.groups) && isListOf(v.groups.portfolios, group, listLimit) && isListOf(v.groups.accounts, group, listLimit) && isListOf(v.groups.assets, group, listLimit) &&
  isRecord(v.coverage) && ['closedCandidates', 'incompleteFees', 'incompleteExecutions', 'openPlays', 'unassignedFills'].every(key => isCount((v.coverage as Record<string, unknown>)[key])) &&
  isNullOr(v.coverage.firstFillAtUtc, x => isTimestamp(x, 64)) && isText(v.coverage.notice, 4000) && isListOf(v.plays, play, 50) &&
  (v.plays as ReviewPlay[]).every(p => (p.executionSource === 'paper') === (v.tradingMode === 'paper')) &&
  isCount(v.offset) && (v.nextOffset === null || isCount(v.nextOffset) && v.nextOffset > v.offset) &&
  (v.positions === undefined || isReviewPositions(v.positions)) &&
  (v.tradingMode !== 'paper' ||
    (v.positions === undefined || v.positions.mode === 'exclude' && !v.positions.included) &&
    !(v.metrics as ReviewMetrics).positions &&
    (v.periods as ReviewDocument['periods']).every(p => !p.positions) &&
    Object.values(v.groups as ReviewDocument['groups']).every(rows => rows.every(row => !row.metrics.positions)))
const isReviewPositions = (v: unknown): v is ReviewPositions => isRecord(v) &&
  (v.mode === 'auto' || v.mode === 'include' || v.mode === 'exclude') && typeof v.included === 'boolean' &&
  isCount(v.minPlays) && isCount(v.plays) && isNullOr(v.positions, isCount) && isNullOr(v.omitted, isCount) &&
  (v.positions === null) === !v.included && (v.omitted === null) === !v.included && isReviewDecimal(v.playWeight)

export function reviewPath(query: ReviewQuery): string {
  const params = new URLSearchParams({ period: query.period, portfolio: query.portfolio })
  if (query.accountId) params.set('accountId', query.accountId)
  if (query.instrument) params.set('instrument', query.instrument)
  if (query.offset !== undefined) params.set('offset', String(query.offset))
  if (query.positions !== undefined) params.set('positions', query.positions)
  if (query.minPlays !== undefined) params.set('minPlays', String(query.minPlays))
  return `/api/review?${params}`
}

export const isReviewExclusions = (v: unknown): v is ReviewExclusions => isRecord(v) &&
  isListOf(v.items, item => isRecord(item) && isGuid(item.playId) && isText(item.title, 200) && isGuid(item.accountId) &&
    isText(item.accountName, 200) && isText(item.instrument, 128) && isListOf(item.reasons, reason => isNonBlankText(reason, 2000), listLimit) &&
    (item.reasons as string[]).length > 0, 50) &&
  isCount(v.total) && isCount(v.offset) && (v.items as unknown[]).length <= v.total &&
  (v.items as unknown[]).length === Math.min(50, Math.max(0, v.total - v.offset)) &&
  new Set((v.items as ReviewExclusion[]).map(item => item.playId)).size === (v.items as unknown[]).length &&
  (v.nextOffset === null ? v.offset + (v.items as unknown[]).length >= v.total :
    isCount(v.nextOffset) && v.nextOffset === v.offset + (v.items as unknown[]).length && v.nextOffset < v.total && v.nextOffset > v.offset) &&
  isText(v.notice, 4000)

export function reviewExclusionsPath(query: ReviewQuery): string {
  return reviewPath(query).replace('/api/review?', '/api/review/exclusions?')
}
