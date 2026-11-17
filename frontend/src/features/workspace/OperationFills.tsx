import type { ActivityFills, ActivityOperation } from '@/api/activity'
import { ApiError } from '@/api/errors'
import type { BrokerAccount, WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { Skeleton, SkeletonStatus } from '@/components/ui/skeleton'
import { useDelayedFlag } from '@/lib/use-delayed-flag'
import { useApiRead } from '@/lib/use-api-read'
import { ActivityTable } from './ActivityTable'
import { amount, money, price } from '@/lib/format'
import { Time } from '@/components/ui/time'
import { FundingPaymentsTable } from './FundingPaymentsTable'

/** An expanded operation's executions, read on demand. A 404 means the history changed under the page. */
export function OperationFills({ api, operation, accounts, onReload, refreshGeneration = 0 }: {
  api: WorkspaceApi; operation: ActivityOperation; accounts: BrokerAccount[]; onReload?: (() => void) | undefined
  /** Changes with every accepted Activity response; per-execution Play associations can change without the row's facts. */
  refreshGeneration?: number
}) {
  const { accountId, id } = operation
  // Re-read on every accepted Activity response and when the row's facts change, not on every render.
  const key = JSON.stringify([accountId, id, operation.fillCount, operation.lastOccurredAtUtc, operation.playId, operation.playAssociation,
    operation.fundingPaymentCount, operation.fundingAsOfUtc, refreshGeneration])
  const read = useApiRead(api, key, (client, signal) => client.activityFills(accountId, id, signal), { fallback: 'Executions could not be loaded.' })
  const pending = read.loading
  const skeleton = useDelayedFlag(pending)
  if (pending) return skeleton ? <SkeletonStatus label="Loading executions…" className="operation-fills-skeleton">
    {[0, 1].map(row => <span key={row} className="operation-fills-skeleton-row"><Skeleton /><Skeleton /><Skeleton /><Skeleton /></span>)}
  </SkeletonStatus> : <div className="operation-fills-pending" aria-busy="true" />
  if (read.cause instanceof ApiError && read.cause.status === 404) return <div className="workspace-alert" role="alert"><span>This operation changed since the page loaded.</span>
    {onReload && <Button variant="ghost" size="sm" onClick={onReload}>Reload activity</Button>}</div>
  if (read.error || !read.data) return <div className="workspace-alert" role="alert"><span>{read.error}</span>
    <Button variant="ghost" size="sm" onClick={read.reload}>Retry executions</Button></div>
  const result: { fills: ActivityFills } = { fills: read.data }
  if (result.fills.tradingMode === 'paper') return <>
    <p className="workspace-scope-note">Simulated executions at defined levels. Zero fees and slippage. Candle windows do not prove intrabar ordering.</p>
    <div className="workspace-table-wrap"><table className="workspace-table"><thead><tr><th>Role</th><th>Price</th><th>Quantity</th><th>Simulated P&amp;L</th><th>Time</th><th>Candle window</th></tr></thead>
      <tbody>{result.fills.paperExecutions?.map(execution => <tr key={execution.id}><td>{execution.role}</td><td title={execution.price}>{price(execution.price)}</td><td title={execution.quantity}>{amount(execution.quantity)}</td><td title={execution.pnlUsd}>{money(execution.pnlUsd)}</td><td><Time at={execution.occurredAtUtc} /></td><td><Time at={execution.candleOpenAtUtc} /> – <Time at={execution.candleCloseAtUtc} /></td></tr>)}</tbody></table></div>
    {!!result.fills.omittedPaperExecutionCount && <p className="operation-incomplete">{result.fills.omittedPaperExecutionCount} simulated execution rows omitted. Totals include all paper executions.</p>}
  </>
  const { fills, omittedFillCount } = result.fills
  const flips = fills.filter(part => part.fill.positionEffect === 'flip')
  return <>
    {flips.length > 0 && <p className="workspace-scope-note">Flip rows show this operation's allocated quantity, fee and P&amp;L.</p>}
    {fills.length > 0 ? <ActivityTable fills={fills.map(part => ({ ...part.fill, quantity: part.quantity, fee: part.fee, closedPnlUsd: part.closedPnlUsd }))} accounts={accounts} />
      : <p className="workspace-scope-note">Execution rows were omitted by the response limit.</p>}
    {flips.length > 0 && <details className="operation-originals"><summary>Original flip facts</summary><ActivityTable fills={flips.map(part => part.fill)} accounts={accounts} /></details>}
    {omittedFillCount > 0 && <p className="operation-incomplete">Showing the first {fills.length.toLocaleString('en-US')} executions; operation totals include all {operation.fillCount.toLocaleString('en-US')}.</p>}
    {result.fills.fundingPayments.length > 0 && <>
      <h4>Settled funding</h4>
      <FundingPaymentsTable payments={result.fills.fundingPayments} />
    </>}
    {result.fills.omittedFundingPaymentCount > 0 && <p className="operation-incomplete">Some funding rows are omitted; the operation funding sum includes all attributed settlements.</p>}
  </>
}
