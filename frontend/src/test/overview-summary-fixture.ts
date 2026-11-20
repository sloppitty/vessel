import type { OverviewQuery, OverviewSummaryDocument, SummaryAmount, SummaryTotals } from '@/api/overview-summary'
import { accountFixture, portfolioFixture } from './workspace-fixture'

export const summaryAmount = (amountUsd: string | null, total = 1): SummaryAmount => ({ amountUsd, coverage: {
  contributingAccountCount: amountUsd === null ? 0 : total, totalAccountCount: total, contributingOperationCount: 0, excludedOperationCount: 0,
  reasons: amountUsd === null && total > 0 ? [{ reason: 'Recorded value unavailable.', count: total }] : [],
} })
export const summaryTotals = (balance = '1250.123456', accounts = 1): SummaryTotals => ({ accountCount: accounts,
  balance: summaryAmount(balance, accounts), available: summaryAmount(null, accounts), openPositionCount: 0,
  unrealizedPnl: summaryAmount(null, accounts), realizedResult: summaryAmount(null, accounts), importedFillCount: 0,
  paperExecutionCount: 0, unrealizedPnlAsOfUtc: null,
})
export function overviewSummaryFixture(query: OverviewQuery = { groupBy: 'portfolio', period: '30', scope: 'all' }): OverviewSummaryDocument {
  const asOfUtc = '2026-10-13T12:00:00.000Z'
  const totals = summaryTotals()
  return { ...query, tradingMode: 'real', asOfUtc, fromUtc: query.period === 'all' ? null : new Date(Date.parse(asOfUtc) - Number(query.period) * 86_400_000).toISOString(), totals,
    rows: [{ id: query.groupBy === 'portfolio' ? `portfolio:${portfolioFixture.id}` : `account:${accountFixture.id}`,
      name: query.groupBy === 'portfolio' ? portfolioFixture.name : accountFixture.name, icon: query.groupBy === 'portfolio' ? 'folder' : null,
      venueId: query.groupBy === 'account' ? 'manual' : null, totals, shareOfBalance: { numerator: totals.balance.amountUsd!, denominator: totals.balance.amountUsd! } }],
    totalGroupCount: 1, omittedGroupCount: 0, scopeNote: 'Enabled synthetic fixture accounts. Retained history only.',
  }
}
