import { isTradingMode, type TradingMode } from './paper'
import { isCount, isFilledText, isGuid, isListOf, isNonBlankText, isNullOr, isRecord, isReviewDecimal, isText, isTimestamp } from './guards'
import { isWeekStart, type WeekStart } from './reporting'
import { isOptionalPlayLocalResult, type PlayLocalResult } from './local-result'
import { isUnresolvedEvidence, preflightCodes, unresolvedReasons, type PreflightCode, type UnresolvedEvidence, type UnresolvedReason } from './plays'

export const reviewPeriods = [
  { id: 'day', label: 'Last 24 hours' }, { id: 'week', label: 'Last 7 days' },
  { id: 'month', label: 'Last 30 days' }, { id: 'year', label: 'Last 365 days' },
  { id: 'all', label: 'All recorded' },
] as const
export type ReviewPeriod = typeof reviewPeriods[number]['id']
export type ReviewGroupBy = 'portfolios' | 'accounts' | 'assets' | 'origins' | 'directions'
/** `auto` adds imported positions while the selection has fewer than `minPlays` scored Plays. */
export type ReviewPositionsMode = 'auto' | 'include' | 'exclude'
export interface ReviewQuery {
  period: ReviewPeriod; portfolio: string; accountId?: string; instrument?: string; offset?: number
  positions?: ReviewPositionsMode; minPlays?: number
  /** A local date (yyyy-MM-dd): the reporting week holding it replaces the rolling period (Phase 2, item 2). */
  week?: string
}
/**
 * The reporting week a report covers: its first local day, half-open UTC range, the reporting zone and week start.
 * `data` is `some`, `empty` (data exists, but nothing closed, was cancelled or declared Unresolved in the week) or `none`
 * (Vessel holds nothing that early in this scope).
 */
export interface ReviewWeek {
  start: string; fromUtc: string; toUtc: string; timeZone: string; weekStart: WeekStart; data: 'some' | 'empty' | 'none'
}
const localDate = /^\d{4}-\d{2}-\d{2}$/
export const isReviewWeek = (v: unknown): v is ReviewWeek => isRecord(v) && typeof v.start === 'string' && localDate.test(v.start) &&
  isTimestamp(v.fromUtc, 64) && isTimestamp(v.toUtc, 64) && Date.parse(v.fromUtc) < Date.parse(v.toUtc) && isFilledText(v.timeZone, 64) &&
  isWeekStart(v.weekStart) && (v.data === 'some' || v.data === 'empty' || v.data === 'none')
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
  /** The stored answers and completion (phase 1, 1.4b); absent from older servers. */
  adherence?: 'followed' | 'deviated' | 'not-applicable' | null
  mistakeTag?: string | null
  direction?: string
  reviewCompletion?: 'completed' | 'not-needed' | 'legacy' | null
}
/** Process and outcome as cases (phase 1, 1.4b): counts only; a rate is shown only where its own n is at least 10. */
export interface ReviewProcess {
  followed: { wins: number; losses: number; scratches: number }
  deviated: { wins: number; losses: number; scratches: number }
  retrospective: number; unscored: number; unanswered: number; notApplicable: number
  mistakeTags: Array<{ tag: string; wins: number; losses: number; scratches: number; unscored: number }>
  /** Every tag in the period; at most 200 are listed. */
  mistakeTagCount: number
}
export const processCells = ['followed-win', 'followed-loss', 'followed-scratch', 'deviated-win', 'deviated-loss', 'deviated-scratch'] as const
export const processLanes = ['retrospective', 'unscored', 'unanswered', 'not-applicable'] as const
/** One drill-down: exactly one cell, lane, mistake tag or skipped pre-flight check. */
export type ReviewProcessSelection =
  | { cell: typeof processCells[number] } | { lane: typeof processLanes[number] } | { mistakeTag: string }
  | { skippedCheck: PreflightCode | 'not-recorded' } | { rule: typeof ruleSelectors[number] }
/** The if-then rule's drill-downs (Phase 2, item 3): a review answer, unanswered, or the answer at Plan it. */
export const ruleSelectors = ['followed', 'deviated', 'not-applicable', 'unanswered', 'acknowledged', 'exception', 'not-acknowledged'] as const
/** One answer's Plays by outcome. */
export interface ReviewRuleRow { wins: number; losses: number; scratches: number; unscored: number }
/**
 * The if-then rule over the period's Plays planned under one (Phase 2, item 3): how many carried it, the answers at Plan it,
 * and "Followed the rule?" by outcome. Counts and drill-down only.
 */
export interface ReviewRule {
  carried: number; acknowledged: number; exceptions: number; notAcknowledged: number
  followed: ReviewRuleRow; deviated: ReviewRuleRow; notApplicable: ReviewRuleRow; unanswered: ReviewRuleRow
}
const ruleRow = (v: unknown): v is ReviewRuleRow => isRecord(v) && ['wins', 'losses', 'scratches', 'unscored'].every(k => isCount(v[k]))
const rowTotal = (row: ReviewRuleRow) => row.wins + row.losses + row.scratches + row.unscored
export const isReviewRule = (v: unknown): v is ReviewRule => isRecord(v) &&
  ['carried', 'acknowledged', 'exceptions', 'notAcknowledged'].every(k => isCount(v[k])) &&
  ruleRow(v.followed) && ruleRow(v.deviated) && ruleRow(v.notApplicable) && ruleRow(v.unanswered) &&
  (v.acknowledged as number) + (v.exceptions as number) + (v.notAcknowledged as number) === v.carried &&
  [v.followed, v.deviated, v.notApplicable, v.unanswered].reduce((sum: number, row) => sum + rowTotal(row as ReviewRuleRow), 0) === v.carried
export interface ReviewProcessPlay {
  id: string; title: string; accountId: string; accountName: string; venueId: string; instrument: string
  status: 'closed' | 'cancelled' | 'unresolved'; origin: 'planned' | 'retrospective'; direction: string; atUtc: string
  /** Null where Review does not score the Play. */
  net: string | null; returnPercent: string | null; rMultiple: string | null
  adherence: 'followed' | 'deviated' | 'not-applicable' | null; mistakeTag: string | null
  plannedAfterExecution: boolean; reviewCompletion: 'completed' | 'not-needed' | 'legacy' | null
}
export interface ReviewProcessPage { items: ReviewProcessPlay[]; total: number; offset: number; nextOffset: number | null }
export interface ReviewExclusion {
  playId: string; title: string; accountId: string; accountName: string; instrument: string; reasons: string[]
  /** An Unresolved Play's declaration: reason, note, time and execution facts (phase 1, 1.3a). */
  unresolved?: { reason: UnresolvedReason; note: string | null; declaredAtUtc: string; evidence: UnresolvedEvidence | null } | null
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
  /** Origins and directions are in a fixed order and absent from servers before phase 1.4b. */
  groups: Record<'portfolios' | 'accounts' | 'assets', ReviewGroup[]> & Partial<Record<'origins' | 'directions', ReviewGroup[]>>
  coverage: {
    closedCandidates: number; incompleteFees: number; incompleteExecutions: number; openPlays: number
    unassignedFills: number; firstFillAtUtc: string | null; notice: string
    /** Unresolved Plays declared in the period, excluded with no result (phase 1, 1.3a); absent from older servers. */
    unresolved?: number
    /** Plays cancelled in the period, listed with the exclusions (phase 1, 1.4b); absent from an older server. */
    cancelled?: number
    /** Closed Plays with a fill allocated between Plays: venue result not allocated (Phase 2, item 11); absent from older servers. */
    notAllocated?: number
  }
  plays: ReviewPlay[]; offset: number; nextOffset: number | null
  /**
   * The pre-flight recorded at Plan it over the period's closed planned Plays, scored or excluded: how many have one, how
   * many were planned before it existed, and per check how many went ahead without it. Absent from older servers.
   */
  preflight?: { recorded: number; notRecorded: number; skipped: Array<{ code: PreflightCode; count: number }> }
  /** Absent from servers before phase 1.4b. */
  process?: ReviewProcess
  /** Whether imported positions are in this report; absent from servers before October 20, 2026. */
  positions?: ReviewPositions
  /** The if-then rule's answers over the same Plays (Phase 2, item 3); absent from older servers. */
  rule?: ReviewRule
  /** The reporting week, present only when the query names one (Phase 2, item 2). */
  week?: ReviewWeek
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
    (!v.plannedAfterExecution || v.origin !== 'retrospective' && v.origin !== 'position')) &&
  answers(v) && (v.direction === undefined || directions.includes(v.direction as string)) &&
  (v.reviewCompletion === undefined || isNullOr(v.reviewCompletion, x => ['completed', 'not-needed', 'legacy'].includes(x as string))) &&
  (v.origin !== 'position' || !v.adherence && !v.mistakeTag && !v.reviewCompletion)
/** A Play's plan direction, or none; an imported position's direction, or none when its side is unknown. */
const directions = ['', 'long', 'short']
const answers = (v: Record<string, unknown>) =>
  (v.adherence === undefined || isNullOr(v.adherence, x => ['followed', 'deviated', 'not-applicable'].includes(x as string))) &&
  (v.mistakeTag === undefined || isNullOr(v.mistakeTag, x => isText(x, 100)))
const processRow = (v: unknown) => isRecord(v) && ['wins', 'losses', 'scratches'].every(k => isCount(v[k]))
const isReviewProcess = (v: unknown): v is ReviewProcess => isRecord(v) && processRow(v.followed) && processRow(v.deviated) &&
  ['retrospective', 'unscored', 'unanswered', 'notApplicable', 'mistakeTagCount'].every(k => isCount(v[k])) &&
  isListOf(v.mistakeTags, t => isRecord(t) && isNonBlankText(t.tag, 100) && ['wins', 'losses', 'scratches', 'unscored'].every(k => isCount(t[k])), 200) &&
  (v.mistakeTags as unknown[]).length <= (v.mistakeTagCount as number)
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
  (v.groups.origins === undefined || isListOf(v.groups.origins, group, 3)) && (v.groups.directions === undefined || isListOf(v.groups.directions, group, 2)) &&
  (v.process === undefined || isReviewProcess(v.process)) && (v.rule === undefined || isReviewRule(v.rule)) &&
  isRecord(v.coverage) && ['closedCandidates', 'incompleteFees', 'incompleteExecutions', 'openPlays', 'unassignedFills'].every(key => isCount((v.coverage as Record<string, unknown>)[key])) &&
  isNullOr(v.coverage.firstFillAtUtc, x => isTimestamp(x, 64)) && isText(v.coverage.notice, 4000) &&
  (v.coverage.unresolved === undefined || isCount(v.coverage.unresolved)) &&
  (v.coverage.cancelled === undefined || isCount(v.coverage.cancelled)) &&
  (v.coverage.notAllocated === undefined || isCount(v.coverage.notAllocated)) && isListOf(v.plays, play, 50) &&
  (v.plays as ReviewPlay[]).every(p => (p.executionSource === 'paper') === (v.tradingMode === 'paper')) &&
  isCount(v.offset) && (v.nextOffset === null || isCount(v.nextOffset) && v.nextOffset > v.offset) &&
  (v.preflight === undefined || isRecord(v.preflight) && isCount(v.preflight.recorded) && isCount(v.preflight.notRecorded) &&
    isListOf(v.preflight.skipped, s => isRecord(s) && preflightCodes.includes(s.code as PreflightCode) && isCount(s.count) &&
      s.count <= ((v.preflight as Record<string, unknown>).recorded as number), preflightCodes.length)) &&
  (v.positions === undefined || isReviewPositions(v.positions)) &&
  (v.week === undefined || isReviewWeek(v.week)) &&
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
  if (query.week) params.set('week', query.week)
  return `/api/review?${params}`
}

export const isReviewExclusions = (v: unknown): v is ReviewExclusions => isRecord(v) &&
  isListOf(v.items, item => isRecord(item) && isGuid(item.playId) && isText(item.title, 200) && isGuid(item.accountId) &&
    isText(item.accountName, 200) && isText(item.instrument, 128) && isListOf(item.reasons, reason => isNonBlankText(reason, 2000), listLimit) &&
    (item.reasons as string[]).length > 0 && (item.unresolved == null || isRecord(item.unresolved) &&
      unresolvedReasons.includes(item.unresolved.reason as UnresolvedReason) && isNullOr(item.unresolved.note, x => isText(x, 2000)) &&
      isTimestamp(item.unresolved.declaredAtUtc, 64) && isNullOr(item.unresolved.evidence, isUnresolvedEvidence)), 50) &&
  isCount(v.total) && isCount(v.offset) && (v.items as unknown[]).length <= v.total &&
  (v.items as unknown[]).length === Math.min(50, Math.max(0, v.total - v.offset)) &&
  new Set((v.items as ReviewExclusion[]).map(item => item.playId)).size === (v.items as unknown[]).length &&
  (v.nextOffset === null ? v.offset + (v.items as unknown[]).length >= v.total :
    isCount(v.nextOffset) && v.nextOffset === v.offset + (v.items as unknown[]).length && v.nextOffset < v.total && v.nextOffset > v.offset) &&
  isText(v.notice, 4000)

export function reviewExclusionsPath(query: ReviewQuery): string {
  return reviewPath(query).replace('/api/review?', '/api/review/exclusions?')
}

export function reviewProcessPath(query: ReviewQuery, selection: ReviewProcessSelection): string {
  const params = new URLSearchParams(reviewPath(query).slice('/api/review?'.length))
  const [key, value] = Object.entries(selection)[0]!
  params.set(key, value)
  return `/api/review/process?${params}`
}

export const isReviewProcessPage = (v: unknown): v is ReviewProcessPage => isRecord(v) &&
  isListOf(v.items, item => isRecord(item) && isGuid(item.id) && isText(item.title, 200) && isGuid(item.accountId) &&
    isText(item.accountName, 200) && isText(item.venueId, 64) && isText(item.instrument, 128) &&
    ['closed', 'cancelled', 'unresolved'].includes(item.status as string) && ['planned', 'retrospective'].includes(item.origin as string) &&
    directions.includes(item.direction as string) && isTimestamp(item.atUtc, 64) &&
    ['net', 'returnPercent', 'rMultiple'].every(k => isNullOr(item[k], isReviewDecimal)) && answers(item) &&
    // Only a scored Closed Play has a result, and a result always has its return; retrospective Plays have no plan R.
    (item.net === null ? item.returnPercent === null && item.rMultiple === null : item.status === 'closed' && item.returnPercent !== null) &&
    typeof item.plannedAfterExecution === 'boolean' && (item.origin === 'planned' || item.rMultiple === null && !item.plannedAfterExecution) &&
    isNullOr(item.reviewCompletion, x => ['completed', 'not-needed', 'legacy'].includes(x as string)), 50) &&
  isCount(v.total) && isCount(v.offset) && (v.items as unknown[]).length === Math.min(50, Math.max(0, v.total - v.offset)) &&
  new Set((v.items as ReviewProcessPlay[]).map(item => item.id)).size === (v.items as unknown[]).length &&
  (v.nextOffset === null ? v.offset + (v.items as unknown[]).length >= v.total :
    isCount(v.nextOffset) && v.nextOffset === v.offset + (v.items as unknown[]).length && v.nextOffset < v.total)
