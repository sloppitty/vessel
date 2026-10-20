import { isTradingMode, type TradingMode } from './paper'
import { ApiError } from './errors'
import { isCount, isFilledText, isIsoTimestamp, isNonEmptyGuid, isRecord, isSummaryDecimal } from './guards'
import { isPortfolioIconKey } from './portfolio-icons'

export type OverviewGroup = 'portfolio' | 'account'
export type OverviewPeriod = '7' | '30' | '90' | 'all'
export interface OverviewQuery { groupBy: OverviewGroup; period: OverviewPeriod; scope: string }
export interface SummaryCoverage {
  contributingAccountCount: number; totalAccountCount: number; contributingOperationCount: number; excludedOperationCount: number
  reasons: { reason: string; count: number }[]
}
export interface SummaryAmount { amountUsd: string | null; coverage: SummaryCoverage }
export interface SummaryTotals {
  accountCount: number; balance: SummaryAmount; available: SummaryAmount; openPositionCount: number
  unrealizedPnl: SummaryAmount; realizedResult: SummaryAmount; importedFillCount: number; paperExecutionCount?: number
}
export interface SummaryRow {
  id: string; name: string; icon: string | null; venueId: string | null; totals: SummaryTotals
  shareOfBalance: { numerator: string; denominator: string } | null
}
export interface OverviewSummaryDocument extends OverviewQuery {
  tradingMode?: TradingMode
  asOfUtc: string; fromUtc: string | null; totals: SummaryTotals; rows: SummaryRow[]
  totalGroupCount: number; omittedGroupCount: number; scopeNote: string
}
export const validOverviewScope = (scope: string) => scope === 'all' || scope === 'unassigned' || /^(portfolio|account):/.test(scope) && isNonEmptyGuid(scope.slice(scope.indexOf(':') + 1))
export function overviewSummaryPath(query: OverviewQuery): string {
  if (!['portfolio', 'account'].includes(query.groupBy) || !['7', '30', '90', 'all'].includes(query.period) || typeof query.scope !== 'string' || !validOverviewScope(query.scope))
    throw new ApiError('invalid-response', 'The overview summary request is invalid.')
  return `/api/overview/summary?${new URLSearchParams({ ...query })}`
}
function summaryAmount(v: unknown, accounts: number): v is SummaryAmount {
  if (!isRecord(v) || !(v.amountUsd === null || isSummaryDecimal(v.amountUsd)) || !isRecord(v.coverage)) return false
  const c = v.coverage
  return ['contributingAccountCount', 'totalAccountCount', 'contributingOperationCount', 'excludedOperationCount'].every(k => isCount(c[k])) &&
    c.totalAccountCount === accounts && (c.contributingAccountCount as number) <= accounts &&
    (v.amountUsd === null) === (c.contributingAccountCount === 0) &&
    Array.isArray(c.reasons) && (c.contributingAccountCount === accounts && c.excludedOperationCount === 0 || c.reasons.length > 0) && c.reasons.length <= 10 && c.reasons.every(r => isRecord(r) && isFilledText(r.reason, 2000) && isCount(r.count) && r.count > 0)
}
function totals(v: unknown): v is SummaryTotals {
  if (!(isRecord(v) && isCount(v.accountCount) && isCount(v.openPositionCount) && (v.paperExecutionCount === undefined || isCount(v.paperExecutionCount)) && isCount(v.importedFillCount) &&
    ['balance', 'available', 'unrealizedPnl', 'realizedResult'].every(k => summaryAmount(v[k], v.accountCount as number)))) return false
  const checked = v as unknown as SummaryTotals
  return ['balance', 'available', 'unrealizedPnl'].every(k => {
    const c = checked[k as 'balance' | 'available' | 'unrealizedPnl'].coverage
    return c.contributingOperationCount === 0 && c.excludedOperationCount === 0
  }) && checked.realizedResult.coverage.contributingOperationCount >= checked.realizedResult.coverage.contributingAccountCount
}
export function isOverviewSummary(v: unknown, query: OverviewQuery): v is OverviewSummaryDocument {
  if (!isRecord(v) || v.tradingMode !== undefined && !isTradingMode(v.tradingMode) || v.groupBy !== query.groupBy || v.period !== query.period || v.scope !== query.scope || !isIsoTimestamp(v.asOfUtc) ||
    !(query.period === 'all' ? v.fromUtc === null : isIsoTimestamp(v.fromUtc) && Date.parse(v.fromUtc) === Date.parse(v.asOfUtc) - Number(query.period) * 86_400_000) ||
    !totals(v.totals) || !Array.isArray(v.rows) || v.rows.length > 200 || !isCount(v.totalGroupCount) || !isCount(v.omittedGroupCount) ||
    v.totalGroupCount !== v.rows.length + v.omittedGroupCount || v.totalGroupCount > v.totals.accountCount || !isFilledText(v.scopeNote, 4000)) return false
  const total = v.totals
  if (v.tradingMode === 'paper' ? total.importedFillCount !== 0 : (total.paperExecutionCount ?? 0) !== 0) return false
  if (query.scope.startsWith('account:') && (total.accountCount > 1 || v.totalGroupCount > 1)) return false
  if (query.groupBy === 'account' && v.totalGroupCount !== total.accountCount) return false
  return new Set(v.rows.map(r => isRecord(r) ? r.id : null)).size === v.rows.length && v.rows.every(r => {
    if (!isRecord(r) || !isFilledText(r.id, 48) || !isFilledText(r.name, 200) || !totals(r.totals) || r.totals.accountCount < 1 || r.totals.accountCount > total.accountCount) return false
    if (query.groupBy === 'account' ? !r.id.startsWith('account:') || !isNonEmptyGuid(r.id.slice(8)) || r.icon !== null || !isFilledText(r.venueId, 64) || r.totals.accountCount !== 1 :
      !(r.id === 'unassigned' || r.id.startsWith('portfolio:') && isNonEmptyGuid(r.id.slice(10))) || r.venueId !== null || !isPortfolioIconKey(r.icon)) return false
    if (query.groupBy === 'portfolio' && query.scope !== 'all' && !query.scope.startsWith('account:') && r.id.toLowerCase() !== query.scope.toLowerCase() ||
      query.groupBy === 'account' && query.scope.startsWith('account:') && r.id.toLowerCase() !== query.scope.toLowerCase()) return false
    return r.shareOfBalance === null ? r.totals.balance.amountUsd === null || total.balance.amountUsd === null || isSummaryDecimal(total.balance.amountUsd) && /^-?0(?:\.0+)?$/.test(total.balance.amountUsd) || total.balance.amountUsd.startsWith('-') :
      isRecord(r.shareOfBalance) && isSummaryDecimal(r.shareOfBalance.numerator) && isSummaryDecimal(r.shareOfBalance.denominator) &&
      !r.shareOfBalance.denominator.startsWith('-') && !/^0(?:\.0+)?$/.test(r.shareOfBalance.denominator) &&
      r.shareOfBalance.numerator === r.totals.balance.amountUsd && r.shareOfBalance.denominator === total.balance.amountUsd
  })
}
