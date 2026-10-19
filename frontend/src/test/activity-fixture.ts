import type { ActivityFills, ActivityOperation, ActivityPage, OperationFill, OperationWithFills } from '@/api/activity'
import type { ImportedFill } from '@/api/fills'
import { accountFixture } from './workspace-fixture'

export const activityFillFixture: ImportedFill = {
  id: '12345678-1234-1234-1234-123456789abc', accountId: accountFixture.id, contractId: 'BTC',
  side: 'buy', direction: 'Open Long', positionEffect: 'open', feeBasis: 'reported', pnlBasis: 'gross',
  price: '100.123456789123456789', quantity: '2', fee: '0.01', feeToken: 'USDC', closedPnlUsd: '0',
  occurredAtUtc: '2026-10-12T10:00:00Z', orderId: '1', sourceFillId: '1', transactionHash: '', playId: null,
}
export const operationFillsFixture: OperationFill[] = [{ fill: activityFillFixture, role: 'entry', quantity: '2', fee: '0.01', closedPnlUsd: '0' }]
export const operationFixture: ActivityOperation = {
  id: `${activityFillFixture.id}:long`, accountId: accountFixture.id, contractId: 'BTC', direction: 'long', status: 'open',
  openedAtUtc: activityFillFixture.occurredAtUtc, closedAtUtc: null, lastOccurredAtUtc: activityFillFixture.occurredAtUtc,
  quantity: '2', totalEnteredQuantity: '2', totalExitedQuantity: '0', openQuantity: '2', averageEntryPrice: activityFillFixture.price,
  averageExitPrice: null, entryOrderCount: 1, exitOrderCount: 0, fillCount: 1, reportedClosedPnlUsd: '0', grossClosedPnlUsd: '0',
  stablecoinFeesNominalUsd: '0.01', otherFees: [], netResultUsd: '-0.01', pnlBasis: 'gross', playId: null, playAssociation: 'unassigned',
  totalResultUsd: null, fundingNetUsd: null, fundingStatus: 'unavailable', fundingPnlBasis: 'unknown', fundingAsOfUtc: null, fundingPaymentCount: 0,
  leverage: null, leverageMin: null, leverageMax: null,
  coverage: [],
}
/** The same operation with its rows, as a retrospective preview carries it. */
export const operationWithFillsFixture: OperationWithFills = { ...operationFixture, fills: operationFillsFixture, omittedFillCount: 0 }
export const activityFillsFixture: ActivityFills = { operationId: operationFixture.id, fills: operationFillsFixture, omittedFillCount: 0,
  fundingPayments: [], omittedFundingPaymentCount: 0 }

/** A backend-shaped page: counts, facets and order follow the given operations unless overridden. */
export function activityPage(operations: ActivityOperation[], overrides: Partial<ActivityPage> = {}): ActivityPage {
  const totalCount = overrides.totalCount ?? operations.length
  return {
    operations, page: 1, pageSize: 25, totalCount, scopeCount: totalCount, retainedFillCount: operations.reduce((n, op) => n + op.fillCount, 0),
    asOfUtc: '2026-10-13T00:00:00Z', version: 'fixture-v1',
    facets: { accountIds: [...new Set(operations.map(op => op.accountId))].sort(), contracts: [...new Set(operations.map(op => op.contractId))].sort() },
    coverage: [], scopeNote: 'Retained fixture fills only.', unassignedFundingPayments: [], omittedUnassignedFundingPaymentCount: 0, ...overrides,
  }
}
export const activityFixture: ActivityPage = activityPage([operationFixture])

const peakFill = (n: number, minute: number, side: 'buy' | 'sell', quantity: string, closedPnlUsd = '0', fee = '0'): OperationFill => ({
  fill: { ...activityFillFixture, id: `${String(n).padStart(8, '0')}-0000-4000-8000-000000000000`, side,
    direction: side === 'buy' ? 'Open Long' : 'Close Long', positionEffect: side === 'buy' ? 'open' : 'close', price: '100', quantity, fee,
    closedPnlUsd, occurredAtUtc: `2026-10-12T00:0${minute}:00Z`, orderId: String(minute), sourceFillId: String(n) },
  role: side === 'buy' ? 'entry' : 'exit', quantity, fee, closedPnlUsd,
})
/**
 * Mirrors the backend case `Same_timestamp_peak_disagreement_keeps_boundaries_prices_and_money_but_quantity_unknown`:
 * an entry and an exit share a timestamp, so the peak is unknown while boundaries, totals, prices and money stay known.
 */
export const unknownPeakOperationFixture: OperationWithFills = {
  id: '00000001-0000-4000-8000-000000000000:long', accountId: accountFixture.id, contractId: 'BTC', direction: 'long', status: 'closed',
  openedAtUtc: '2026-10-12T00:00:00Z', closedAtUtc: '2026-10-12T00:02:00Z', lastOccurredAtUtc: '2026-10-12T00:02:00Z',
  quantity: null, totalEnteredQuantity: '3', totalExitedQuantity: '3', openQuantity: '0', averageEntryPrice: '100', averageExitPrice: '100',
  entryOrderCount: 2, exitOrderCount: 2, fillCount: 4, reportedClosedPnlUsd: '30', grossClosedPnlUsd: '30', stablecoinFeesNominalUsd: '3',
  otherFees: [], netResultUsd: '27', pnlBasis: 'gross', playId: null, playAssociation: 'unassigned',
  totalResultUsd: null, fundingNetUsd: null, fundingStatus: 'unavailable', fundingPnlBasis: 'unknown', fundingAsOfUtc: null, fundingPaymentCount: 0,
  leverage: null, leverageMin: null, leverageMax: null,
  coverage: ['Effect-consistent orders at a shared timestamp disagree on peak position; maximum quantity is unknown, while operation boundaries and totals are retained.'],
  fills: [peakFill(1, 0, 'buy', '2'), peakFill(2, 1, 'buy', '1'), peakFill(3, 1, 'sell', '1', '10', '1'), peakFill(4, 2, 'sell', '2', '20', '2')],
  omittedFillCount: 0,
}
/** The page row of the unknown-peak operation, without its execution rows. */
export const unknownPeakSummaryFixture: ActivityOperation = (() => {
  const row: Partial<OperationWithFills> = { ...unknownPeakOperationFixture }
  delete row.fills; delete row.omittedFillCount
  return row as ActivityOperation
})()
