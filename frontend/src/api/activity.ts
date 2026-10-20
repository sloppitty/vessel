import { ApiError } from './errors'
import { isImportedFill, type ImportedFill } from './fills'
import { isCount, isFilledText, isGuid, isIsoTimestamp, isListOf, isNonNegativeDecimal, isNullOr, isPlainDecimal, isRecord } from './guards'
import { validOverviewScope } from './overview-summary'

export interface OperationFill {
  /** The untouched retained fact. Allocation fields describe only this operation's share. */
  fill: ImportedFill
  role: 'entry' | 'exit' | 'unknown'
  quantity: string
  fee: string
  closedPnlUsd: string
}
/** One signed-position round trip as a page row. Its executions load separately on expand. */
export interface ActivityOperation {
  id: string
  accountId: string
  contractId: string
  direction: 'long' | 'short' | null
  status: 'open' | 'closed' | 'incomplete'
  openedAtUtc: string | null
  closedAtUtc: string | null
  lastOccurredAtUtc: string
  quantity: string | null
  totalEnteredQuantity: string | null
  totalExitedQuantity: string | null
  openQuantity: string | null
  averageEntryPrice: string | null
  averageExitPrice: string | null
  entryOrderCount: number
  exitOrderCount: number
  fillCount: number
  reportedClosedPnlUsd: string
  grossClosedPnlUsd: string | null
  stablecoinFeesNominalUsd: string
  otherFees: { token: string; amount: string }[]
  netResultUsd: string | null
  /** Combined result only when funding coverage and the execution P&L funding basis are verified. */
  totalResultUsd: string | null
  fundingNetUsd: string | null
  fundingStatus: 'complete' | 'partial' | 'unavailable'
  fundingPnlBasis: 'included' | 'excluded' | 'unknown'
  fundingAsOfUtc: string | null
  fundingPaymentCount: number
  /**
   * The latest venue-reported position leverage observed during the operation (its executions or refresh snapshots),
   * whole numbers only. Min and max differ when it changed. All null when none was observed. Only `leverage` is null,
   * with the range kept, when the latest observation time reported conflicting values. Never inferred.
   */
  leverage: number | null
  leverageMin: number | null
  leverageMax: number | null
  pnlBasis: 'gross' | 'net-of-fee' | 'mixed'
  playId: string | null
  playAssociation: 'linked' | 'unassigned' | 'mixed'
  /** The linked Play's current title on Activity pages; null when unlinked or unreadable, absent in a preview. */
  playTitle?: string | null
  coverage: string[]
}
/** An operation with its constituent rows, as a retrospective preview carries it. */
export interface OperationWithFills extends ActivityOperation {
  fills: OperationFill[]
  omittedFillCount: number
}
export interface ActivityCoverage { accountId: string; contractId: string | null; notice: string }
export interface FundingPayment {
  id: string
  accountId: string
  sourceId: string
  contractId: string
  venueContractId: string | null
  occurredAtUtc: string
  occurredAtUnixNanoseconds: string | null
  /** Positive is received, negative is paid. */
  amount: string
  asset: string
}
export interface ActivityPage {
  operations: ActivityOperation[]
  page: number
  pageSize: number
  /** Operations after every filter. */
  totalCount: number
  /** Operations in scope before the owner's filters. */
  scopeCount: number
  retainedFillCount: number
  /** The server time rolling periods count back from. */
  asOfUtc: string
  /** Opaque token of the scoped operation set; a different value on a later page means Activity changed. */
  version: string
  facets: { accountIds: string[]; contracts: string[] }
  coverage: ActivityCoverage[]
  scopeNote: string
  unassignedFundingPayments: FundingPayment[]
  omittedUnassignedFundingPaymentCount: number
}
export interface ActivityFills {
  operationId: string
  fills: OperationFill[]
  omittedFillCount: number
  fundingPayments: FundingPayment[]
  omittedFundingPaymentCount: number
}

export const activityPeriods = ['all', '7', '30', '90', 'custom'] as const
export type ActivityPeriod = typeof activityPeriods[number]
export const activityStatuses = ['open', 'partial', 'closed', 'incomplete'] as const
export type ActivityStatusFilter = typeof activityStatuses[number]
/** Server-side Activity filters. Blank text and `all` are the defaults and are not sent. */
export interface ActivityQuery {
  /** Overview scope: `all`, `unassigned`, `portfolio:{id}` or `account:{id}`. */
  scope?: string
  /** Account detail: only this account. */
  accountId?: string
  account?: string
  /** A portfolio ID or `unassigned`. */
  portfolio?: string
  contract?: string
  direction?: 'long' | 'short' | ''
  status?: ActivityStatusFilter | ''
  period?: ActivityPeriod
  /** `yyyy-MM-dd`, inclusive UTC days, only with the custom period. */
  from?: string
  to?: string
  page: number
  pageSize: number
}

/** Activity allows longer decimal strings than most endpoints. */
const decimalLength = 160
/** Rows one expanded operation may return; the earliest are kept. */
export const operationFillLimit = 1_000
export const activityPageSizeLimit = 100
// The server returns complete scoped coverage and facets, unbounded like the retained history they describe.
const unbounded = Number.POSITIVE_INFINITY
export const isFundingPayment = (v: unknown): v is FundingPayment =>
  isRecord(v) && isGuid(v.id) && isGuid(v.accountId) && isFilledText(v.sourceId, 128) &&
  isFilledText(v.contractId, 128) && isNullOr(v.venueContractId, x => isFilledText(x, 128)) &&
  isIsoTimestamp(v.occurredAtUtc) && isNullOr(v.occurredAtUnixNanoseconds, x => typeof x === 'string' && /^(0|[1-9][0-9]{0,18})$/.test(x) && BigInt(x) <= 9223372036854775807n) &&
  isPlainDecimal(v.amount, decimalLength) && isFilledText(v.asset, 64)

/** `{GUID}:long` or `:short`, as Activity and retrospective previews identify an operation. */
export const isOperationId = (v: unknown): v is string =>
  isFilledText(v, 48) && /^[\da-f-]{36}:(long|short)$/.test(v) && isGuid(v.slice(0, 36))
const isDay = (v: unknown): v is string => {
  if (typeof v !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(v)) return false
  const day = new Date(`${v}T00:00:00Z`)
  return Number.isFinite(day.getTime()) && day.toISOString().startsWith(v)
}
const isPageNumber = (v: unknown): v is number => isCount(v) && v >= 1
const isPageSizeValue = (v: unknown): v is number => isCount(v) && v >= 1 && v <= activityPageSizeLimit
const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()

function invalidQuery(): never {
  throw new ApiError('invalid-response', 'The activity request is invalid.')
}
export function activityPath(query: ActivityQuery): string {
  const params = new URLSearchParams()
  if (query.scope !== undefined && query.scope !== 'all') {
    if (!validOverviewScope(query.scope)) invalidQuery()
    params.set('scope', query.scope)
  }
  if (query.accountId !== undefined) { if (!isGuid(query.accountId)) invalidQuery(); params.set('accountId', query.accountId) }
  if (query.account) { if (!isGuid(query.account)) invalidQuery(); params.set('account', query.account) }
  if (query.portfolio) { if (query.portfolio !== 'unassigned' && !isGuid(query.portfolio)) invalidQuery(); params.set('portfolio', query.portfolio) }
  if (query.contract) { if (query.contract.length > 128) invalidQuery(); params.set('contract', query.contract) }
  if (query.direction) { if (query.direction !== 'long' && query.direction !== 'short') invalidQuery(); params.set('direction', query.direction) }
  if (query.status) { if (!activityStatuses.includes(query.status)) invalidQuery(); params.set('status', query.status) }
  const period = query.period ?? 'all'
  if (!activityPeriods.includes(period)) invalidQuery()
  if (period !== 'all') params.set('period', period)
  if (period === 'custom') {
    if (query.from) { if (!isDay(query.from)) invalidQuery(); params.set('from', query.from) }
    if (query.to) { if (!isDay(query.to)) invalidQuery(); params.set('to', query.to) }
  }
  if (!isPageNumber(query.page) || !isPageSizeValue(query.pageSize)) invalidQuery()
  params.set('page', String(query.page))
  params.set('pageSize', String(query.pageSize))
  return `/api/activity?${params}`
}
export function activityFillsPath(accountId: string, operationId: string): string {
  if (!isGuid(accountId) || !isOperationId(operationId)) throw new ApiError('invalid-response', 'The operation request is invalid.')
  return `/api/activity/fills?${new URLSearchParams({ accountId, operationId })}`
}

const isLeverage = (v: unknown): v is number => typeof v === 'number' && Number.isInteger(v) && v >= 1 && v <= 1000
/** All three null; or an observed range with the latest inside it, or unknown when the latest time reported conflicting (so different) values. */
const validLeverage = (op: Record<string, unknown>) => op.leverageMin === null && op.leverageMax === null
  ? op.leverage === null
  : isLeverage(op.leverageMin) && isLeverage(op.leverageMax) && op.leverageMin <= op.leverageMax &&
    (op.leverage === null ? op.leverageMin < op.leverageMax : isLeverage(op.leverage) && op.leverageMin <= op.leverage && op.leverage <= op.leverageMax)

const isCoverage = (c: unknown): c is ActivityCoverage => isRecord(c) && isGuid(c.accountId) &&
  isNullOr(c.contractId, x => isFilledText(x, 128)) && isFilledText(c.notice, 2000)

/** One operation row: its facts and the invariants between them, without execution rows. */
export function isActivityOperation(op: unknown): op is ActivityOperation {
  if (!isRecord(op) || !isOperationId(op.id) ||
    !isGuid(op.accountId) || !isFilledText(op.contractId, 128) || !['long', 'short', null].includes(op.direction as string | null) ||
    !['open', 'closed', 'incomplete'].includes(String(op.status)) || !isIsoTimestamp(op.lastOccurredAtUtc) ||
    !isNullOr(op.openedAtUtc, isIsoTimestamp) || !isNullOr(op.closedAtUtc, isIsoTimestamp) ||
    !['quantity', 'totalEnteredQuantity', 'totalExitedQuantity', 'openQuantity', 'averageEntryPrice', 'averageExitPrice'].every(k => isNullOr(op[k], x => isNonNegativeDecimal(x, decimalLength))) ||
    !['entryOrderCount', 'exitOrderCount', 'fillCount'].every(k => isCount(op[k])) ||
    !isPlainDecimal(op.reportedClosedPnlUsd, decimalLength) || !isNullOr(op.grossClosedPnlUsd, x => isPlainDecimal(x, decimalLength)) ||
    !isPlainDecimal(op.stablecoinFeesNominalUsd, decimalLength) || !isNullOr(op.netResultUsd, x => isPlainDecimal(x, decimalLength)) ||
    !isNullOr(op.totalResultUsd, x => isPlainDecimal(x, decimalLength)) || !isNullOr(op.fundingNetUsd, x => isPlainDecimal(x, decimalLength)) ||
    !['complete', 'partial', 'unavailable'].includes(String(op.fundingStatus)) ||
    !['included', 'excluded', 'unknown'].includes(String(op.fundingPnlBasis)) ||
    !isNullOr(op.fundingAsOfUtc, isIsoTimestamp) || !isCount(op.fundingPaymentCount) || !validLeverage(op) ||
    op.totalResultUsd !== null && (op.fundingPnlBasis === 'unknown' || op.fundingStatus !== 'complete') ||
    !['gross', 'net-of-fee', 'mixed'].includes(String(op.pnlBasis)) || !isNullOr(op.playId, isGuid) ||
    !['linked', 'unassigned', 'mixed'].includes(String(op.playAssociation)) || !isListOf(op.coverage, n => isFilledText(n, 2000), 100) ||
    !Array.isArray(op.otherFees) || !op.otherFees.every(f => isRecord(f) && isFilledText(f.token, 64) && isPlainDecimal(f.amount, decimalLength)) ||
    (op.fillCount as number) === 0 || (op.entryOrderCount as number) > (op.fillCount as number) || (op.exitOrderCount as number) > (op.fillCount as number) ||
    (op.playAssociation === 'linked') !== (op.playId !== null) ||
    op.playTitle !== undefined && !isNullOr(op.playTitle, x => typeof x === 'string' && x.length <= 200) || op.playTitle != null && op.playId === null) return false
  if (op.status === 'incomplete') {
    if (!op.coverage.length) return false
    if (op.direction === null) {
      return op.openedAtUtc === null && op.closedAtUtc === null && op.quantity === null && op.openQuantity === null &&
        op.totalEnteredQuantity === null && op.totalExitedQuantity === null && op.averageEntryPrice === null && op.averageExitPrice === null
    }
    return op.openedAtUtc === null && op.quantity === null && op.totalEnteredQuantity === null && op.averageEntryPrice === null &&
      op.openQuantity !== null && op.totalExitedQuantity !== null
  }
  if (op.direction === null || op.openedAtUtc === null || op.openQuantity === null ||
    op.totalEnteredQuantity === null || op.totalExitedQuantity === null || op.averageEntryPrice === null ||
    (op.status === 'closed') !== (op.closedAtUtc !== null) || op.closedAtUtc !== null && Date.parse(String(op.closedAtUtc)) < Date.parse(String(op.openedAtUtc))) return false
  // Orders sharing a timestamp may disagree on the peak position: boundaries, totals and prices stay known,
  // only the peak quantity is unknown, and the server says so in the operation's coverage.
  return op.quantity !== null || op.coverage.length > 0
}

/** Execution rows of one operation, all from the same account and instrument, at most `limit`. */
function areOperationFills(fills: unknown, accountId: string, contractId: string | null, limit: number): fills is OperationFill[] {
  if (!Array.isArray(fills) || fills.length > limit) return false
  const contract = contractId ?? (isRecord(fills[0]) && isRecord(fills[0].fill) ? fills[0].fill.contractId : null)
  return fills.every(f => isRecord(f) && isImportedFill(f.fill) && same(f.fill.accountId, accountId) && f.fill.contractId === contract &&
    ['entry', 'exit', 'unknown'].includes(String(f.role)) && isPlainDecimal(f.quantity, decimalLength) &&
    (f.role === 'unknown' || isNonNegativeDecimal(f.quantity, decimalLength)) && isPlainDecimal(f.fee, decimalLength) && isPlainDecimal(f.closedPnlUsd, decimalLength))
}

/** An operation that carries its rows, at most `limit` of them. */
export function isOperationWithFills(op: unknown, limit = operationFillLimit): op is OperationWithFills {
  return isActivityOperation(op) && isRecord(op) && isCount(op.omittedFillCount) && areOperationFills(op.fills, op.accountId, op.contractId, limit) &&
    op.fillCount === (op.fills as unknown[]).length + (op.omittedFillCount as number)
}

const matchesQuery = (op: ActivityOperation, query: ActivityQuery) =>
  (query.accountId === undefined || same(op.accountId, query.accountId)) &&
  (!query.scope?.startsWith('account:') || same(op.accountId, query.scope.slice(8))) &&
  (!query.account || same(op.accountId, query.account)) && (!query.contract || op.contractId === query.contract) &&
  (!query.direction || op.direction === query.direction) &&
  (!query.status || (query.status === 'partial' ? op.status === 'open' : op.status === query.status))

/** A page of Activity: exact counts, deterministic order and rows that satisfy the request's filters. */
export function isActivityPage(value: unknown, query?: ActivityQuery): value is ActivityPage {
  if (!isRecord(value) || !Array.isArray(value.operations) || !isPageNumber(value.page) || !isPageSizeValue(value.pageSize) ||
    query !== undefined && value.pageSize !== query.pageSize ||
    !isCount(value.totalCount) || !isCount(value.scopeCount) || value.totalCount > value.scopeCount || !isCount(value.retainedFillCount) ||
    !isIsoTimestamp(value.asOfUtc) || !isFilledText(value.version, 256) || !isFilledText(value.scopeNote, 4000) ||
    !isListOf(value.coverage, isCoverage, unbounded) || !isRecord(value.facets) ||
    !isListOf(value.unassignedFundingPayments, isFundingPayment, 100) || !isCount(value.omittedUnassignedFundingPaymentCount) ||
    !isListOf(value.facets.accountIds, isGuid, unbounded) || !isListOf(value.facets.contracts, c => isFilledText(c, 128), unbounded)) return false
  const { page, pageSize, totalCount } = value
  const pages = Math.max(1, Math.ceil(totalCount / pageSize))
  if (page > pages || value.operations.length !== Math.max(0, Math.min(pageSize, totalCount - (page - 1) * pageSize))) return false
  const accounts = new Set((value.facets.accountIds as string[]).map(id => id.toLowerCase()))
  const contracts = new Set(value.facets.contracts as string[])
  if (accounts.size !== value.facets.accountIds.length || contracts.size !== value.facets.contracts.length) return false
  const ids = new Set<string>()
  let previous = Number.POSITIVE_INFINITY
  for (const op of value.operations) {
    if (!isActivityOperation(op) || ids.has(op.id) || !accounts.has(op.accountId.toLowerCase()) || !contracts.has(op.contractId) ||
      query !== undefined && !matchesQuery(op, query)) return false
    const at = Date.parse(op.lastOccurredAtUtc)
    if (at > previous) return false
    ids.add(op.id); previous = at
  }
  const only = query?.accountId ?? (query?.scope?.startsWith('account:') ? query.scope.slice(8) : undefined)
  return only === undefined || (value.coverage as ActivityCoverage[]).every(c => same(c.accountId, only)) &&
    (value.unassignedFundingPayments as FundingPayment[]).every(p => same(p.accountId, only)) &&
    (value.facets.accountIds as string[]).every(id => same(id, only))
}

/** One operation's rows on expand: chronological, from the requested account, at most 1,000. */
export function isActivityFills(value: unknown, accountId: string, operationId: string): value is ActivityFills {
  if (!isRecord(value) || value.operationId !== operationId || !isCount(value.omittedFillCount) ||
    !isListOf(value.fundingPayments, p => isFundingPayment(p) && same(p.accountId, accountId), operationFillLimit) ||
    !isCount(value.omittedFundingPaymentCount) ||
    !areOperationFills(value.fills, accountId, null, operationFillLimit) || value.fills.length + value.omittedFillCount === 0) return false
  let previous = Number.NEGATIVE_INFINITY
  for (const part of value.fills) {
    const at = Date.parse(part.fill.occurredAtUtc)
    if (at < previous) return false
    previous = at
  }
  return true
}
