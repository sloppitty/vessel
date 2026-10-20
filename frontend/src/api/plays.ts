import { ApiError } from './errors'
import { isCount, isGuid, isNullOr, isPlainDecimal, isRecord, isText, isTimestamp } from './guards'
import { isImageMarkup, type ImageMarkup } from './markup'
import { validOverviewScope } from './overview-summary'
import { isAccountMonitor, isMonitorDiscrepancy, isMonitorWatch, type AccountMonitor, type MonitorDiscrepancy, type MonitorWatch } from './monitor'
import { isOptionalPlayLocalResult, type PlayLocalResult } from './local-result'

export const playStatuses = ['draft', 'planned', 'paused', 'open', 'closed', 'cancelled'] as const
export type PlayStatus = typeof playStatuses[number]
export const cancelReasons = ['invalidated', 'missed', 'changed-mind', 'expired', 'mistake', 'other'] as const
export type CancelReason = typeof cancelReasons[number]
export interface StructuredReview {
  adherence: 'followed' | 'deviated' | 'not-applicable' | null
  mistakeTag: string | null
  lesson: string
  nextAction: string
}
export const emptyStructuredReview = (): StructuredReview => ({ adherence: null, mistakeTag: null, lesson: '', nextAction: '' })
export const hasStructuredReview = (review?: StructuredReview) =>
  !!review && !!(review.adherence || review.mistakeTag?.trim() || review.lesson.trim() || review.nextAction.trim())

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
}
export interface SavedPlay {
  summary: PlaySummary
  plan: PlayPlan
  /** Chart drawings per venue instrument key; validated by the chart when loaded. */
  drawings: Record<string, unknown[]>
  review: string
  structuredReview?: StructuredReview | null
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
export interface PlanRevision { number: number; status: PlayStatus; reason: string; createdAtUtc: string; plan: PlayPlan }
export interface StatusChange { from: PlayStatus; to: PlayStatus; reason: CancelReason | null; note: string | null; occurredAtUtc: string; source?: 'owner' | 'venue' | 'manual' }
export interface ReviewRevision { number: number; occurredAtUtc: string; review: string; structuredReview: StructuredReview }
export interface PlayHistory { revisions: PlanRevision[]; statusChanges: StatusChange[]; reviews?: ReviewRevision[] | null }
export type StatusRequest =
  | { status: 'planned' | 'paused' }
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
export const isPlaySummary = (v: unknown): v is PlaySummary => isRecord(v) && isGuid(v.id) && isText(v.title, 200) &&
  playStatuses.includes(v.status as PlayStatus) && isGuid(v.accountId) && isText(v.venueId, 64) && isNullOr(v.instrument, x => isText(x, 128)) &&
  (v.instrumentSource === 'venue' || v.instrumentSource === 'manual') && (v.direction === 'long' || v.direction === 'short') &&
  isCount(v.planRevision) && isCount(v.version) && isNullOr(v.cancelReason, x => cancelReasons.includes(x as CancelReason)) &&
  isTimestamp(v.createdAtUtc, timeLength) && isTimestamp(v.updatedAtUtc, timeLength) &&
  isNullOr(v.plannedAtUtc, x => isTimestamp(x, timeLength)) && isNullOr(v.endedAtUtc, x => isTimestamp(x, timeLength)) &&
  typeof v.hasReview === 'boolean' && (v.origin === undefined || v.origin === 'planned' || v.origin === 'retrospective') &&
  (v.reviewedAtUtc === undefined || isNullOr(v.reviewedAtUtc, x => isTimestamp(x, timeLength)))
export const isStructuredReview = (v: unknown): v is StructuredReview => isRecord(v) &&
  (v.adherence === null || ['followed', 'deviated', 'not-applicable'].includes(v.adherence as string)) &&
  isNullOr(v.mistakeTag, x => isText(x, 100)) && isText(v.lesson, 4000) && isText(v.nextAction, 4000)
// Retrospective documents have a deliberately empty compatibility plan, not an original plan.
const isRetrospectivePlan = (v: unknown) => isRecord(v) && Array.isArray(v.entries) && v.entries.length === 0 &&
  v.leverage === '0' && isRecord(v.notes) && ['thesis', 'invalidation', 'strategy', 'evidence'].every(k => v.notes && (v.notes as Record<string, unknown>)[k] === '') &&
  isPlayPlan({ ...v, entries: [{ id: 'compatibility', name: '', color: '', share: '', price: '', stops: [], targets: [] }] })
export const isSavedPlay = (v: unknown): v is SavedPlay => isRecord(v) && isPlaySummary(v.summary) &&
  (v.summary.origin === 'retrospective' ? isRetrospectivePlan(v.plan) : isPlayPlan(v.plan)) &&
  isRecord(v.drawings) && Object.values(v.drawings).every(Array.isArray) && isText(v.review, noteLength) &&
  (v.structuredReview == null || isStructuredReview(v.structuredReview))
export const isPlayHistory = (v: unknown): v is PlayHistory => isRecord(v) && Array.isArray(v.revisions) && Array.isArray(v.statusChanges) &&
  v.revisions.every(r => isRecord(r) && isCount(r.number) && playStatuses.includes(r.status as PlayStatus) && isText(r.reason, 2000) &&
    isTimestamp(r.createdAtUtc, timeLength) && isPlayPlan(r.plan)) &&
  v.statusChanges.every(c => isRecord(c) && playStatuses.includes(c.from as PlayStatus) && playStatuses.includes(c.to as PlayStatus) &&
    isNullOr(c.reason, x => cancelReasons.includes(x as CancelReason)) && isNullOr(c.note, x => isText(x, 2000)) && isTimestamp(c.occurredAtUtc, timeLength) &&
    (c.source === undefined || c.source === 'owner' || c.source === 'venue' || c.source === 'manual')) &&
  (v.reviews == null || Array.isArray(v.reviews) && v.reviews.every(r => isRecord(r) && isCount(r.number) &&
    isTimestamp(r.occurredAtUtc, timeLength) && isText(r.review, noteLength) && isStructuredReview(r.structuredReview)))
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
export type PlayCounts = Record<'drafts' | 'progress' | 'review' | 'reviewed' | 'closed' | 'cancelled' | 'all', number>

/** Whether a closed or cancelled Play counts as reviewed, as the list views define it. */
export const isReviewed = (play: PlaySummary) => play.hasReview || !!play.reviewedAtUtc
const endedStatus = (status: PlayStatus) => status === 'closed' || status === 'cancelled'
/** Whether a Play belongs to a list view, as the server selects it. */
export const playInView = (play: PlaySummary, view: PlayView) => {
  switch (view) {
    case 'drafts': return play.status === 'draft'
    case 'progress': return play.status === 'planned' || play.status === 'paused' || play.status === 'open'
    case 'review': return endedStatus(play.status) && !isReviewed(play)
    case 'reviewed': return endedStatus(play.status) && isReviewed(play)
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
  const c = v as PlayCounts
  return c.drafts + c.progress + c.closed + c.cancelled === c.all && c.review + c.reviewed === c.closed + c.cancelled
}

export const statusLabels: Record<PlayStatus, string> = {
  draft: 'Draft', planned: 'Planned', paused: 'Paused', open: 'Open', closed: 'Closed', cancelled: 'Cancelled',
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
}
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
  isText(v.venueStatus, 64) && isTimestamp(v.statusAtUtc, timeLength)
const executionFill = (v: unknown): v is ExecutionFill => isRecord(v) && isText(v.sourceFillId, 128) && isText(v.direction, 128) &&
  isPlainDecimal(v.price) && isPlainDecimal(v.quantity) && isPlainDecimal(v.fee) && isText(v.feeToken, 64) && isPlainDecimal(v.closedPnlUsd) && isTimestamp(v.occurredAtUtc, timeLength) &&
  (v.side === 'buy' || v.side === 'sell') && ['open', 'close', 'flip', 'unknown'].includes(v.positionEffect as string) &&
  (v.pnlBasis === 'gross' || v.pnlBasis === 'net-of-fee')
const orderLink = (v: unknown): v is OrderLink => isRecord(v) && isGuid(v.id) && ['entry', 'stop', 'target', 'exit'].includes(v.role as string) &&
  isNullOr(v.entryId, x => isText(x, 64)) && isNullOr(v.levelId, x => isText(x, 64)) && (v.state === 'linked' || v.state === 'suggested') &&
  (v.source === 'automatic' || v.source === 'owner') && (v.order === null || executionOrder(v.order)) && isPlainDecimal(v.filledQuantity) &&
  Array.isArray(v.fills) && v.fills.every(executionFill)
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
  Array.isArray(v.unlinkedOrders) && v.unlinkedOrders.every(executionOrder) && isText(v.notice, 1000) &&
  Array.isArray(v.issues) && v.issues.every(issue => isText(issue, 1000)) &&
  (v.monitor === null || isAccountMonitor(v.monitor)) && Array.isArray(v.discrepancies) && v.discrepancies.every(isMonitorDiscrepancy) &&
  Array.isArray(v.watches) && v.watches.every(w => isMonitorWatch(w) && w.playId === v.playId)
