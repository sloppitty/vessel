import { type TradingMode } from './paper'
import { ApiError } from './errors'
import { isCount, isGuid, isNullOr, isPlainDecimal, isRecord, isText, isTimestamp } from './guards'
import { isImageMarkup, type ImageMarkup } from './markup'
import { validOverviewScope } from './overview-summary'
import { isAccountMonitor, isMonitorDiscrepancy, isMonitorWatch, type AccountMonitor, type MonitorDiscrepancy, type MonitorWatch } from './monitor'
import { isOptionalPlayLocalResult, type PlayLocalResult } from './local-result'

export const playStatuses = ['draft', 'planned', 'paused', 'open', 'closed', 'cancelled', 'unresolved'] as const
export type PlayStatus = typeof playStatuses[number]
/** Statuses that end a Play: its review can be completed. Unresolved is declared by the owner (phase 1, 1.3a). */
export const endedStatuses: readonly PlayStatus[] = ['closed', 'cancelled', 'unresolved']
export const isEndedStatus = (status: PlayStatus) => endedStatuses.includes(status)
export const cancelReasons = ['invalidated', 'missed', 'changed-mind', 'expired', 'mistake', 'other'] as const
export type CancelReason = typeof cancelReasons[number]
/** Why the owner declared an Open Play unresolved: its executions can never settle it. */
export const unresolvedReasons = ['shared-close', 'flip', 'forced-exit-not-retained', 'other'] as const
export type UnresolvedReason = typeof unresolvedReasons[number]
export const unresolvedReasonLabels: Record<UnresolvedReason, string> = {
  'shared-close': 'Shared close', flip: 'Flip', 'forced-exit-not-retained': 'Forced exit not retained', other: 'Other',
}
/** The execution facts kept with a declaration: quantities, linked orders and the problems that kept it from settling. */
export interface UnresolvedEvidence {
  /** Entered and exited quantity; open is entered minus exited, negative when exits exceed entries. */
  entered: string; exited: string; open: string
  /** Every linked order; at most 200 are listed. */
  orderCount: number
  orders: { orderId: string; role: string; entryId: string | null; levelId: string | null; status: string; filled: string }[]
  problems: string[]
}
export interface StructuredReview {
  adherence: 'followed' | 'deviated' | 'not-applicable' | null
  mistakeTag: string | null
  lesson: string
  nextAction: string
  /** "Followed the rule?" for a Play planned under an if-then rule (Phase 2, item 3); absent from older servers. */
  rule?: 'followed' | 'deviated' | 'not-applicable' | null
}
export const emptyStructuredReview = (): StructuredReview => ({ adherence: null, mistakeTag: null, lesson: '', nextAction: '' })
export const hasStructuredReview = (review?: StructuredReview) =>
  !!review && !!(review.adherence || review.mistakeTag?.trim() || review.lesson.trim() || review.nextAction.trim() || review.rule)

/** The if-then rule a Play was planned under, fixed at Plan it (Phase 2, item 3). */
export interface PlayRuleSeen { version: number; if: string; then: string }
/** One rule version; `sourceTitle` names the Play its next action was promoted from. */
export interface PlayRule extends PlayRuleSeen { sourcePlayId: string | null; sourceTitle: string | null; createdAtUtc: string }
/** The active rule in one trading mode, or none. */
export interface PlayRuleState { active: PlayRule | null; tradingMode: TradingMode }
/** What revision 1 keeps: the rule seen at Plan it and the owner's answer, if any. */
export interface PlanRuleSnapshot extends PlayRuleSeen { sourcePlayId: string | null; answer: 'acknowledged' | 'exception' | null; reason: string | null }
/** The owner's answer at Plan it: acknowledged, or an exception with a reason. */
/**
 * The rule version Plan it showed and the owner's answer, if any. Without it nothing is recorded, so an unseen rule is
 * never attributed to the Play (Sol round 1 on item 3).
 */
export interface PlanRuleAnswer { version: number; answer?: 'acknowledged' | 'exception'; reason?: string }
export const maxRulePart = 500
export const ruleAnswers = ['followed', 'deviated', 'not-applicable'] as const
const rulePart = (v: unknown) => isText(v, maxRulePart) && v.trim() !== ''
export const isPlayRuleSeen = (v: unknown): v is PlayRuleSeen => isRecord(v) && isCount(v.version) && v.version >= 1 &&
  rulePart(v.if) && rulePart(v.then)
export const isPlayRuleState = (v: unknown): v is PlayRuleState => isRecord(v) && (v.tradingMode === 'real' || v.tradingMode === 'paper') &&
  (v.active === null || isPlayRuleSeen(v.active) && isRecord(v.active) && isNullOr(v.active.sourcePlayId, isGuid) &&
    isNullOr(v.active.sourceTitle, x => isText(x, 200)) && isTimestamp(v.active.createdAtUtc, 64))
export const isPlanRuleSnapshot = (v: unknown): v is PlanRuleSnapshot => isPlayRuleSeen(v) && isRecord(v) && isNullOr(v.sourcePlayId, isGuid) &&
  (v.answer === null || v.answer === 'acknowledged' || v.answer === 'exception') &&
  isNullOr(v.reason, x => isText(x, 500)) && (v.answer === 'exception') === (typeof v.reason === 'string')

/** A stop or target. A percent value is the return on margin at the plan's leverage; share is the part of the entry it closes. */
export interface PlanExit { id: string; unit: 'price' | 'percent'; value: string; share: string }
export interface PlanEntry { id: string; name: string; color: string; share: string; price: string; stops: PlanExit[]; targets: PlanExit[] }
/** The plan as stored and revised. Number fields keep the exact text entered. */
export interface PlayPlan {
  direction: 'long' | 'short'
  sizingMode: 'margin' | 'quantity'
  size: string
  leverage: string
  budgetOverride: string | null
  entries: PlanEntry[]
  notes: { thesis: string; invalidation: string; strategy: string; evidence: string }
}
export interface PlaySummary {
  tradingMode?: TradingMode
  id: string
  title: string
  status: PlayStatus
  accountId: string
  venueId: string
  instrument: string | null
  instrumentSource: 'venue' | 'manual'
  direction: 'long' | 'short'
  planRevision: number
  version: number
  cancelReason: CancelReason | null
  createdAtUtc: string
  updatedAtUtc: string
  plannedAtUtc: string | null
  endedAtUtc: string | null
  /** Whether a nonblank review note has been saved, independently of lifecycle status. */
  hasReview: boolean
  /** Absent on older servers: those records were planned, never retrospective. */
  origin?: 'planned' | 'retrospective'
  reviewedAtUtc?: string | null
  /**
   * How the review was completed, explicitly (phase 1, 1.2): Complete review or changed answers, No review needed, or kept
   * from before completion existed. Null while it needs review; absent from older servers.
   */
  reviewCompletion?: ReviewCompletion | null
  reviewCompletedAtUtc?: string | null
}
export type ReviewCompletion = 'completed' | 'not-needed' | 'legacy'
/** An append-only in-trade log entry with an optional state; it never counts as a review. */
export interface PlayNote { id: string; text: string; state: PlayNoteState | null; createdAtUtc: string }
export const playNoteStates = ['calm', 'rushed', 'fomo', 'tired', 'after-loss'] as const
export type PlayNoteState = typeof playNoteStates[number]
export const playNoteStateLabels: Record<PlayNoteState, string> = { calm: 'Calm', rushed: 'Rushed', fomo: 'FOMO', tired: 'Tired', 'after-loss': 'After a loss' }
export interface SavedPlay {
  summary: PlaySummary
  plan: PlayPlan
  /** Chart drawings per venue instrument key; validated by the chart when loaded. */
  drawings: Record<string, unknown[]>
  review: string
  structuredReview?: StructuredReview | null
  /** The if-then rule the Play was planned under (Phase 2, item 3); absent without one. */
  rule?: PlayRuleSeen | null
}
export interface PlayFields {
  accountId: string
  instrument: string | null
  instrumentSource: 'venue' | 'manual'
  title: string
  plan: PlayPlan
  drawings: Record<string, unknown[]>
  review: string
  structuredReview?: StructuredReview
}
export interface PlanRevision {
  number: number; status: PlayStatus; reason: string; createdAtUtc: string; plan: PlayPlan
  /** The pre-flight seen at Plan it, on revision 1 only; null or absent for older plans and later revisions. */
  preflight?: PreflightCheck[] | null
  /** The if-then rule seen at Plan it and the owner's answer, on revision 1 only (Phase 2, item 3). */
  rule?: PlanRuleSnapshot | null
}
/** The non-blocking checks shown at Plan it, in display order (phase 1, 1.5). */
export const preflightCodes = ['priced-stops', 'allocations', 'thesis', 'risk', 'liquidation', 'loss-limit'] as const
export type PreflightCode = typeof preflightCodes[number]
/** `note` is the owner's optional cooldown note, only on a reached loss limit (Phase 2, item 10b). */
export interface PreflightCheck { code: PreflightCode; outcome: 'passed' | 'skipped' | 'unknown'; note?: string }
export const maxPreflightNote = 500
/** One of the owner's recent next actions and the Play it was written for. */
export interface NextAction { playId: string; title: string; nextAction: string; reviewedAtUtc: string | null }
export interface StatusChange {
  from: PlayStatus; to: PlayStatus; reason: CancelReason | null; note: string | null; occurredAtUtc: string; source?: 'owner' | 'venue' | 'manual' | 'paper'
  /** On a change to Unresolved: the owner's reason and the execution facts kept with it. */
  unresolvedReason?: UnresolvedReason | null
  evidence?: UnresolvedEvidence | null
}
export const reviewActions = ['edited', 'completed', 'reopened', 'not-needed', 'cleared'] as const
export interface ReviewRevision {
  number: number; occurredAtUtc: string; review: string; structuredReview: StructuredReview
  /** What happened: edited, completed, reopened, not-needed, or cleared when the Play left a final state; absent from older servers. */
  action?: typeof reviewActions[number]
}
export interface PlayHistory { revisions: PlanRevision[]; statusChanges: StatusChange[]; reviews?: ReviewRevision[] | null }
export type StatusRequest =
  | { status: 'planned'; preflight?: PreflightCheck[]; rule?: PlanRuleAnswer }
  | { status: 'paused' }
  | { status: 'cancelled'; reason: CancelReason; note?: string }
export interface SavedEvidence {
  id: string
  playId: string
  source: 'capture' | 'upload'
  contentType: string
  sizeBytes: number
  sha256: string
  note: string
  createdAtUtc: string
  updatedAtUtc: string
  markup: ImageMarkup | null
}

/** Notes and reviews are at most 20,000 characters; timestamps at most 64. */
const noteLength = 20_000
const timeLength = 64
const exit = (v: unknown): v is PlanExit => isRecord(v) && isText(v.id, 64) && (v.unit === 'price' || v.unit === 'percent') &&
  isText(v.value, 64) && isText(v.share, 64)
const entry = (v: unknown): v is PlanEntry => isRecord(v) && isText(v.id, 64) && isText(v.name, 100) && isText(v.color, 16) &&
  isText(v.share, 64) && isText(v.price, 64) && Array.isArray(v.stops) && v.stops.every(exit) && Array.isArray(v.targets) && v.targets.every(exit)
export const isPlayPlan = (v: unknown): v is PlayPlan => isRecord(v) && (v.direction === 'long' || v.direction === 'short') &&
  (v.sizingMode === 'margin' || v.sizingMode === 'quantity') && isText(v.size, 64) && isText(v.leverage, 8) &&
  isNullOr(v.budgetOverride, x => isText(x, 64)) && Array.isArray(v.entries) && v.entries.length > 0 && v.entries.every(entry) &&
  isRecord(v.notes) && ['thesis', 'invalidation', 'strategy', 'evidence'].every(k => isText((v.notes as Record<string, unknown>)[k], noteLength))
export const isPlaySummary = (v: unknown): v is PlaySummary => isRecord(v) &&
  (v.tradingMode === undefined || v.tradingMode === 'real' || v.tradingMode === 'paper') && isGuid(v.id) && isText(v.title, 200) &&
  playStatuses.includes(v.status as PlayStatus) && isGuid(v.accountId) && isText(v.venueId, 64) && isNullOr(v.instrument, x => isText(x, 128)) &&
  (v.instrumentSource === 'venue' || v.instrumentSource === 'manual') && (v.direction === 'long' || v.direction === 'short') &&
  isCount(v.planRevision) && isCount(v.version) && isNullOr(v.cancelReason, x => cancelReasons.includes(x as CancelReason)) &&
  isTimestamp(v.createdAtUtc, timeLength) && isTimestamp(v.updatedAtUtc, timeLength) &&
  isNullOr(v.plannedAtUtc, x => isTimestamp(x, timeLength)) && isNullOr(v.endedAtUtc, x => isTimestamp(x, timeLength)) &&
  typeof v.hasReview === 'boolean' && (v.origin === undefined || v.origin === 'planned' || v.origin === 'retrospective') &&
  (v.reviewedAtUtc === undefined || isNullOr(v.reviewedAtUtc, x => isTimestamp(x, timeLength))) &&
  (v.reviewCompletion === undefined || isNullOr(v.reviewCompletion, x => ['completed', 'not-needed', 'legacy'].includes(x as string))) &&
  (v.reviewCompletedAtUtc === undefined || isNullOr(v.reviewCompletedAtUtc, x => isTimestamp(x, timeLength))) &&
  (v.reviewCompletion == null) === (v.reviewCompletedAtUtc == null)
export const isStructuredReview = (v: unknown): v is StructuredReview => isRecord(v) &&
  (v.adherence === null || ['followed', 'deviated', 'not-applicable'].includes(v.adherence as string)) &&
  isNullOr(v.mistakeTag, x => isText(x, 100)) && isText(v.lesson, 4000) && isText(v.nextAction, 4000) &&
  (v.rule == null || ruleAnswers.includes(v.rule as typeof ruleAnswers[number]))
// Retrospective documents have a deliberately empty compatibility plan, not an original plan.
const isRetrospectivePlan = (v: unknown) => isRecord(v) && Array.isArray(v.entries) && v.entries.length === 0 &&
  v.leverage === '0' && isRecord(v.notes) && ['thesis', 'invalidation', 'strategy', 'evidence'].every(k => v.notes && (v.notes as Record<string, unknown>)[k] === '') &&
  isPlayPlan({ ...v, entries: [{ id: 'compatibility', name: '', color: '', share: '', price: '', stops: [], targets: [] }] })
export const isSavedPlay = (v: unknown): v is SavedPlay => isRecord(v) && isPlaySummary(v.summary) &&
  (v.summary.origin === 'retrospective' ? isRetrospectivePlan(v.plan) : isPlayPlan(v.plan)) &&
  isRecord(v.drawings) && Object.values(v.drawings).every(Array.isArray) && isText(v.review, noteLength) &&
  (v.structuredReview == null || isStructuredReview(v.structuredReview)) && (v.rule == null || isPlayRuleSeen(v.rule))
export const isPreflightCheck = (v: unknown): v is PreflightCheck => isRecord(v) && preflightCodes.includes(v.code as PreflightCode) &&
  ['passed', 'skipped', 'unknown'].includes(v.outcome as string) &&
  (v.note === undefined || v.code === 'loss-limit' && v.outcome === 'skipped' && isText(v.note, maxPreflightNote) && v.note.trim() !== '')
export const isNextAction = (v: unknown): v is NextAction => isRecord(v) && isGuid(v.playId) && isText(v.title, 200) &&
  isText(v.nextAction, 4000) && v.nextAction.trim() !== '' && isNullOr(v.reviewedAtUtc, x => isTimestamp(x, timeLength))
const isEvidenceAmount = (v: unknown) => typeof v === 'string' && /^-?[0-9]+(\.[0-9]+)?$/.test(v) && v.length <= 80
export const isUnresolvedEvidence = (v: unknown): v is UnresolvedEvidence => isRecord(v) && isEvidenceAmount(v.entered) &&
  isEvidenceAmount(v.exited) && isEvidenceAmount(v.open) && isCount(v.orderCount) && Array.isArray(v.orders) && v.orders.length <= 200 &&
  v.orders.length <= v.orderCount && v.orders.every(o => isRecord(o) && isText(o.orderId, 128) && isText(o.role, 16) &&
    isNullOr(o.entryId, x => isText(x, 64)) && isNullOr(o.levelId, x => isText(x, 64)) && isText(o.status, 32) && isEvidenceAmount(o.filled)) &&
  Array.isArray(v.problems) && v.problems.length <= 50 && v.problems.every(x => isText(x, 2000))
export const isPlayHistory = (v: unknown): v is PlayHistory => isRecord(v) && Array.isArray(v.revisions) && Array.isArray(v.statusChanges) &&
  v.revisions.every(r => isRecord(r) && isCount(r.number) && playStatuses.includes(r.status as PlayStatus) && isText(r.reason, 2000) &&
    isTimestamp(r.createdAtUtc, timeLength) && isPlayPlan(r.plan) &&
    (r.preflight == null || Array.isArray(r.preflight) && r.preflight.length <= preflightCodes.length && r.preflight.every(isPreflightCheck)) &&
    (r.rule == null || r.number === 1 && isPlanRuleSnapshot(r.rule))) &&
  v.statusChanges.every(c => isRecord(c) && playStatuses.includes(c.from as PlayStatus) && playStatuses.includes(c.to as PlayStatus) &&
    isNullOr(c.reason, x => cancelReasons.includes(x as CancelReason)) && isNullOr(c.note, x => isText(x, 2000)) && isTimestamp(c.occurredAtUtc, timeLength) &&
    (c.source === undefined || c.source === 'owner' || c.source === 'venue' || c.source === 'manual' || c.source === 'paper') &&
    // A declaration names its reason; a return to Open names the reason of the declaration it undoes (1.3a).
    (c.unresolvedReason == null || (c.to === 'unresolved' || c.from === 'unresolved' && c.to === 'open') &&
      unresolvedReasons.includes(c.unresolvedReason as UnresolvedReason)) &&
    (c.evidence == null || c.to === 'unresolved' && isUnresolvedEvidence(c.evidence))) &&
  (v.reviews == null || Array.isArray(v.reviews) && v.reviews.every(r => isRecord(r) && isCount(r.number) &&
    isTimestamp(r.occurredAtUtc, timeLength) && isText(r.review, noteLength) && isStructuredReview(r.structuredReview) &&
    (r.action === undefined || reviewActions.includes(r.action as typeof reviewActions[number]))))
export const isPlayNote = (v: unknown): v is PlayNote => isRecord(v) && isGuid(v.id) && isText(v.text, 2000) && v.text.trim() !== '' &&
  (v.state === null || playNoteStates.includes(v.state as PlayNoteState)) && isTimestamp(v.createdAtUtc, timeLength)
export const isSavedEvidence = (v: unknown): v is SavedEvidence => isRecord(v) && isGuid(v.id) && isGuid(v.playId) &&
  (v.source === 'capture' || v.source === 'upload') && isText(v.contentType, 64) && isCount(v.sizeBytes) && isText(v.sha256, 64) &&
  isText(v.note, 4000) && isTimestamp(v.createdAtUtc, timeLength) && isTimestamp(v.updatedAtUtc, timeLength) && isNullOr(v.markup, isImageMarkup)

/** Saved-Play list views. Drafts and In progress return in full (at most 200 rows); the others are paged. */
export const playViews = ['drafts', 'progress', 'review', 'reviewed', 'closed', 'cancelled', 'all'] as const
export type PlayView = typeof playViews[number]
export const pagedPlayViews: readonly PlayView[] = ['review', 'reviewed', 'closed', 'cancelled', 'all']
export const unpagedPlayLimit = 200
export const playSearchLength = 100
export interface PlayListQuery {
  view: PlayView
  /** Case-insensitive title or instrument search; blank means none. */
  q?: string
  /** Overview scope: `all`, `unassigned`, `portfolio:{id}` or `account:{id}`. */
  scope?: string
  page?: number
  pageSize?: number
}
export interface PlayList { plays: PlaySummary[]; view: PlayView; page: number; pageSize: number; totalCount: number }
/** Counts per view; `unresolved` has no tab of its own and is absent from servers before phase 1.3a. */
export type PlayCounts = Record<'drafts' | 'progress' | 'review' | 'reviewed' | 'closed' | 'cancelled' | 'all', number> & { unresolved?: number }

/** Whether a closed or cancelled Play counts as reviewed, as the list views define it. */
/**
 * Whether a Play's review is complete. Completion is explicit (decision 3); a server from before it existed still listed a
 * Play with review text, answers or a review time as reviewed.
 */
export const isReviewed = (play: PlaySummary) => play.reviewCompletion !== undefined ? play.reviewCompletion !== null
  : play.hasReview || !!play.reviewedAtUtc
/** Whether a Play belongs to a list view, as the server selects it. */
export const playInView = (play: PlaySummary, view: PlayView) => {
  switch (view) {
    case 'drafts': return play.status === 'draft'
    case 'progress': return play.status === 'planned' || play.status === 'paused' || play.status === 'open'
    case 'review': return isEndedStatus(play.status) && !isReviewed(play)
    case 'reviewed': return isEndedStatus(play.status) && isReviewed(play)
    case 'closed': case 'cancelled': return play.status === view
    case 'all': return true
  }
}
function invalidPlayQuery(): never {
  throw new ApiError('invalid-response', 'The plays request is invalid.')
}
function listParams(q: string | undefined, scope: string | undefined) {
  const params = new URLSearchParams()
  const search = q?.trim() ?? ''
  if (search) { if (search.length > playSearchLength) invalidPlayQuery(); params.set('q', search) }
  // Without a scope the Plays page keeps Plays on disabled accounts; any scope, even `all`, keeps enabled accounts only.
  if (scope !== undefined) { if (!validOverviewScope(scope)) invalidPlayQuery(); params.set('scope', scope) }
  return params
}
const isPageNumber = (v: unknown): v is number => isCount(v) && v >= 1
const isPageSizeValue = (v: unknown): v is number => isCount(v) && v >= 1 && v <= 100
export function playListPath(query: PlayListQuery): string {
  if (!playViews.includes(query.view)) invalidPlayQuery()
  const params = listParams(query.q, query.scope)
  params.set('view', query.view)
  if (query.page !== undefined) { if (!isPageNumber(query.page)) invalidPlayQuery(); params.set('page', String(query.page)) }
  if (query.pageSize !== undefined) { if (!isPageSizeValue(query.pageSize)) invalidPlayQuery(); params.set('pageSize', String(query.pageSize)) }
  return `/api/plays?${params}`
}
export function playCountsPath(query: { q?: string; scope?: string }): string {
  const params = listParams(query.q, query.scope).toString()
  return `/api/plays/counts${params ? `?${params}` : ''}`
}
/** A list page: the requested view, exact counts and rows that belong to the view and scope. */
export function isPlayList(v: unknown, query: PlayListQuery): v is PlayList {
  if (!isRecord(v) || v.view !== query.view || !Array.isArray(v.plays) || !isPageNumber(v.page) || !isCount(v.pageSize) || v.pageSize < 1 ||
    !isCount(v.totalCount) || !v.plays.every(isPlaySummary)) return false
  const plays = v.plays as PlaySummary[], total = v.totalCount
  if (pagedPlayViews.includes(query.view)) {
    if (query.pageSize !== undefined && v.pageSize !== query.pageSize || v.pageSize > 100 || v.page > Math.max(1, Math.ceil(total / v.pageSize)) ||
      plays.length !== Math.max(0, Math.min(v.pageSize, total - (v.page - 1) * v.pageSize))) return false
  } else if (plays.length > unpagedPlayLimit || !(plays.length === Math.min(total, unpagedPlayLimit) ||
    query.pageSize !== undefined && plays.length === Math.min(total, query.pageSize))) return false
  const account = query.scope?.startsWith('account:') ? query.scope.slice(8).toLowerCase() : null
  return new Set(plays.map(play => play.id)).size === plays.length &&
    plays.every(play => playInView(play, query.view) && (account === null || play.accountId.toLowerCase() === account))
}
export function isPlayCounts(v: unknown): v is PlayCounts {
  if (!isRecord(v) || !['drafts', 'progress', 'review', 'reviewed', 'closed', 'cancelled', 'all'].every(k => isCount(v[k]))) return false
  if (v.unresolved !== undefined && !isCount(v.unresolved)) return false
  const c = v as PlayCounts
  const unresolved = c.unresolved ?? 0
  return c.drafts + c.progress + c.closed + c.cancelled + unresolved === c.all && c.review + c.reviewed === c.closed + c.cancelled + unresolved
}

export const statusLabels: Record<PlayStatus, string> = {
  draft: 'Draft', planned: 'Planned', paused: 'Paused', open: 'Open', closed: 'Closed', cancelled: 'Cancelled', unresolved: 'Unresolved',
}
export const cancelReasonLabels: Record<CancelReason, string> = {
  invalidated: 'Thesis invalidated', missed: 'Missed the entry', 'changed-mind': 'Changed my mind',
  expired: 'Expired', mistake: 'Created by mistake', other: 'Other',
}

/** A venue order as last seen. Prices and sizes keep the venue's exact decimals. */
export interface ExecutionOrder {
  orderId: string
  side: 'buy' | 'sell'
  orderType: string
  limitPrice: string
  triggerPrice: string | null
  reduceOnly: boolean
  isPositionTpsl: boolean
  originalSize: string
  remainingSize: string
  placedAtUtc: string
  status: 'open' | 'filled' | 'triggered' | 'canceled' | 'rejected' | 'other'
  venueStatus: string
  statusAtUtc: string
  /** For an order offered to link: the roles the server would accept on this Play, by its side, mechanics and fills. */
  roles?: LinkRole[]
}
export interface ExecutionFill {
  sourceFillId: string
  direction: string
  price: string
  quantity: string
  fee: string
  feeToken: string
  closedPnlUsd: string
  occurredAtUtc: string
  side: 'buy' | 'sell'
  positionEffect: PositionEffect
  pnlBasis: PnlBasis
}
/** What a fill did to the position. Unknown when the venue's wording does not say. */
export type PositionEffect = 'open' | 'close' | 'flip' | 'unknown'
/** Gross: fees are reported separately. Net-of-fee: the venue already took the fee off the closed PnL. */
export type PnlBasis = 'gross' | 'net-of-fee'
export type LinkRole = 'entry' | 'stop' | 'target' | 'exit'
export interface OrderLink {
  id: string
  role: LinkRole
  entryId: string | null
  /** Stop or target ID within the entry; null for entries and unplanned exits. */
  levelId: string | null
  state: 'linked' | 'suggested'
  source: 'automatic' | 'owner'
  order: ExecutionOrder | null
  filledQuantity: string
  fills: ExecutionFill[]
  /** On a suggestion: its order is also offered to another active Play (phase 1, 1.6d); absent from an older server. */
  otherPlay?: boolean | null
}
/** A position's venue-reported risk context: the venue's figures, never the plan's estimate; null is not reported. */
export interface VenueRisk {
  signedQuantity: string
  entryPrice: string
  liquidationPrice: string | null
  marginMode: 'cross' | 'isolated' | null
  observedAtUtc: string
}
const isVenueRisk = (v: unknown): v is VenueRisk => isRecord(v) && isPlainDecimal(v.signedQuantity) && isPlainDecimal(v.entryPrice) &&
  isNullOr(v.liquidationPrice, isPlainDecimal) && (v.marginMode === null || v.marginMode === 'cross' || v.marginMode === 'isolated') &&
  isTimestamp(v.observedAtUtc)

export interface PlayExecution {
  playId: string
  status: PlayStatus
  tracked: boolean
  reason: string | null
  checkedAtUtc: string | null
  totals: {
    enteredQuantity: string; exitedQuantity: string; openQuantity: string; closedPnlUsd: string; closedPnlBasis: PnlBasis | 'mixed'
    fees: Array<{ token: string; amount: string }>
    /**
     * Venue closed PnL less listed stablecoin fees on before-fee fills, over the counted fills, so far while open. Review and
     * sizing score this basis only once the play is closed and settled. Null when a fee is unverified or in another token.
     * With `local`, absent from servers before October 19, 2026.
     */
    venueNetUsd?: string | null
    /** The Play-local result beside it, known once the play is closed and its fills settle it. */
    local?: PlayLocalResult
  }
  entries: EntryProgress[]
  links: OrderLink[]
  suggestions: OrderLink[]
  unlinkedOrders: ExecutionOrder[]
  /**
   * Compatible orders placed in the lookback before the Play was created and not linked to any Play (Phase 2, item 4), for
   * the owner to link by hand; the automatic matcher never reaches back. Absent from older servers.
   */
  earlierOrders?: ExecutionOrder[]
  /** The account's current venue position on the Play's contract with its reported risk context (Phase 2, item 8a). */
  venuePosition?: VenueRisk | null
  /** The first risk context the venue reported at or after the Play's first entry fill. */
  riskAtFirstFill?: VenueRisk | null
  notice: string
  /** Why linked fills cannot settle the play: missing executions, contradicting fills, or exits before or beyond the entries. */
  issues: string[]
  monitor: AccountMonitor | null
  discrepancies: MonitorDiscrepancy[]
  /** Linked orders still watched after the Play ended; empty while it is active. */
  watches: MonitorWatch[]
  /** Times, averages and estimated R by Review's rules; absent from servers before Phase 1. */
  receipt?: PlayReceipt | null
}
/** One planned entry's fills beside its plan (phase 1, 1.1a). */
export interface EntryProgress {
  entryId: string
  filledQuantity: string
  averageFillPrice: string | null
  restingOrders: number
  /**
   * The entry as planned, from the plan revision in force at its first fill (`planRevision`, null for a Draft or a
   * retrospective Play); before any fill, the current plan. Null when the plan cannot give it; absent from older servers.
   */
  plannedPrice?: string | null
  plannedQuantity?: string | null
  planRevision?: number | null
}
/**
 * What happened, beside the plan, by Review's own rules: the first entry and last exit fills (time held runs between
 * them once closed), average entry and exit, and estimated R (planned-stop model) with the initial risk it divides by, or
 * why it is unknown.
 */
export interface PlayReceipt {
  firstEntryAtUtc: string | null
  lastExitAtUtc: string | null
  averageEntryPrice: string | null
  averageExitPrice: string | null
  fillCount: number
  estimatedR: string | null
  initialRisk: string | null
  estimatedRReason: string | null
  /** The plan was written after the Play's first execution (D-17); absent from servers before Phase 1.4a. */
  plannedAfterExecution?: boolean
}
export interface LinkOrder { orderId: string; role: LinkRole; entryId?: string; levelId?: string }
/**
 * Answers a `level.drift` record: the linked stop or target takes the observed order's acting price in one plan revision.
 * The server refuses (409) when the Play moved past `expectedVersion` or the order no longer rests at `observedPrice`.
 */
export interface RebindLevel { expectedVersion: number; orderId: string; observedPrice: string; reason: string }

const executionOrder = (v: unknown): v is ExecutionOrder => isRecord(v) && isText(v.orderId, 128) && (v.side === 'buy' || v.side === 'sell') &&
  isText(v.orderType, 32) && isPlainDecimal(v.limitPrice) && (v.triggerPrice === null || isPlainDecimal(v.triggerPrice)) &&
  typeof v.reduceOnly === 'boolean' && typeof v.isPositionTpsl === 'boolean' && isPlainDecimal(v.originalSize) && isPlainDecimal(v.remainingSize) &&
  isTimestamp(v.placedAtUtc, timeLength) && ['open', 'filled', 'triggered', 'canceled', 'rejected', 'other'].includes(v.status as string) &&
  isText(v.venueStatus, 64) && isTimestamp(v.statusAtUtc, timeLength) &&
  (v.roles === undefined || Array.isArray(v.roles) && v.roles.length <= 4 && v.roles.every(r => ['entry', 'stop', 'target', 'exit'].includes(r as string)))
const executionFill = (v: unknown): v is ExecutionFill => isRecord(v) && isText(v.sourceFillId, 128) && isText(v.direction, 128) &&
  isPlainDecimal(v.price) && isPlainDecimal(v.quantity) && isPlainDecimal(v.fee) && isText(v.feeToken, 64) && isPlainDecimal(v.closedPnlUsd) && isTimestamp(v.occurredAtUtc, timeLength) &&
  (v.side === 'buy' || v.side === 'sell') && ['open', 'close', 'flip', 'unknown'].includes(v.positionEffect as string) &&
  (v.pnlBasis === 'gross' || v.pnlBasis === 'net-of-fee')
const orderLink = (v: unknown): v is OrderLink => isRecord(v) && isGuid(v.id) && ['entry', 'stop', 'target', 'exit'].includes(v.role as string) &&
  isNullOr(v.entryId, x => isText(x, 64)) && isNullOr(v.levelId, x => isText(x, 64)) && (v.state === 'linked' || v.state === 'suggested') &&
  (v.source === 'automatic' || v.source === 'owner') && (v.order === null || executionOrder(v.order)) && isPlainDecimal(v.filledQuantity) &&
  Array.isArray(v.fills) && v.fills.every(executionFill) && (v.otherPlay === undefined || v.otherPlay === null || typeof v.otherPlay === 'boolean')
export const isEntryProgress = (e: unknown): e is EntryProgress => isRecord(e) && isText(e.entryId, 64) && isPlainDecimal(e.filledQuantity) &&
  isNullOr(e.averageFillPrice, isPlainDecimal) && isCount(e.restingOrders) && (e.plannedPrice === undefined || isNullOr(e.plannedPrice, isPlainDecimal)) &&
  (e.plannedQuantity === undefined || isNullOr(e.plannedQuantity, isPlainDecimal)) && (e.planRevision === undefined || isNullOr(e.planRevision, isCount))
export const isPlayReceipt = (r: unknown): r is PlayReceipt => isRecord(r) &&
  isNullOr(r.firstEntryAtUtc, x => isTimestamp(x, timeLength)) && isNullOr(r.lastExitAtUtc, x => isTimestamp(x, timeLength)) &&
  isNullOr(r.averageEntryPrice, isPlainDecimal) && isNullOr(r.averageExitPrice, isPlainDecimal) && isCount(r.fillCount) &&
  isNullOr(r.estimatedR, isPlainDecimal) && isNullOr(r.initialRisk, isPlainDecimal) && isNullOr(r.estimatedRReason, x => isText(x, 500)) &&
  (r.plannedAfterExecution === undefined || typeof r.plannedAfterExecution === 'boolean') &&
  // R and its denominator come together, or neither comes and a reason says why.
  (r.estimatedR === null) === (r.initialRisk === null) && (r.estimatedR === null) === (r.estimatedRReason !== null)
export const isPlayExecution = (v: unknown): v is PlayExecution => isRecord(v) && isGuid(v.playId) && playStatuses.includes(v.status as PlayStatus) &&
  typeof v.tracked === 'boolean' && isNullOr(v.reason, x => isText(x, 500)) && isNullOr(v.checkedAtUtc, x => isTimestamp(x, timeLength)) &&
  isRecord(v.totals) && ['enteredQuantity', 'exitedQuantity', 'openQuantity', 'closedPnlUsd'].every(k => isPlainDecimal((v.totals as Record<string, unknown>)[k])) &&
  ['gross', 'net-of-fee', 'mixed'].includes(v.totals.closedPnlBasis as string) &&
  Array.isArray(v.totals.fees) && v.totals.fees.every(f => isRecord(f) && isText(f.token, 64) && isPlainDecimal(f.amount)) &&
  (v.totals.venueNetUsd === undefined || isNullOr(v.totals.venueNetUsd, isPlainDecimal)) && isOptionalPlayLocalResult(v.totals.local) &&
  Array.isArray(v.entries) && v.entries.every(isEntryProgress) && (v.receipt === undefined || isNullOr(v.receipt, isPlayReceipt)) &&
  Array.isArray(v.links) && v.links.every(orderLink) && Array.isArray(v.suggestions) && v.suggestions.every(orderLink) &&
  Array.isArray(v.unlinkedOrders) && v.unlinkedOrders.every(executionOrder) &&
  (v.earlierOrders === undefined || Array.isArray(v.earlierOrders) && v.earlierOrders.length <= 20 && v.earlierOrders.every(executionOrder)) &&
  (v.venuePosition === undefined || isNullOr(v.venuePosition, isVenueRisk)) && (v.riskAtFirstFill === undefined || isNullOr(v.riskAtFirstFill, isVenueRisk)) &&
  isText(v.notice, 1000) &&
  Array.isArray(v.issues) && v.issues.every(issue => isText(issue, 1000)) &&
  (v.monitor === null || isAccountMonitor(v.monitor)) && Array.isArray(v.discrepancies) && v.discrepancies.every(isMonitorDiscrepancy) &&
  Array.isArray(v.watches) && v.watches.every(w => isMonitorWatch(w) && w.playId === v.playId)
