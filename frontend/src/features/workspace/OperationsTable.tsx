import { Fragment, useState } from 'react'
import type { ActivityOperation } from '@/api/activity'
import type { BrokerAccount, WorkspaceApi } from '@/api/workspace'
import { ChevronDown, ChevronRight, Inbox } from 'lucide-react'
import { Skeleton, SkeletonStatus } from '@/components/ui/skeleton'
import { OperationDirection } from './OperationDirection'
import { OperationLeverage } from './OperationLeverage'
import { OperationFills } from './OperationFills'
import { NominalUsdTag } from './CoverageBadge'
import { duration, operationDates, operationPriceReturn, operationStatus, resultSign } from './activity-format'
import { amount, money } from '@/lib/format'
import './activity-operations.css'
import { useDisplayZone } from '@/lib/display-zone'
import { otherZoneTime } from '@/lib/time'
import { Time } from '@/components/ui/time'

function Result({ value }: { value: string | null }) {
  return <span className={`operation-result ${resultSign(value)}`} title={value ?? 'Unknown'}>{money(value)}</span>
}

function OperationsHead({ compact }: { compact: boolean }) {
  return <thead><tr>
    <th>Operation</th><th>Account / time</th><th title="Peak position quantity">Size</th>{!compact && <><th>Average entry</th><th>Average exit</th><th title="Before reported fees; venue-reported P&L is preserved in the executions">Closed P&amp;L</th><th title="Stablecoin fees in nominal USD; other tokens listed separately">Fees</th><th title="Settled funding in nominal USD: positive received, negative paid. Partial coverage is not zero.">Funding</th></>}<th title="Fee-adjusted reported execution P&L. Funding inclusion may be unverified; expand for the separate combined total.">Net result</th>{!compact && <th>Play</th>}
  </tr></thead>
}

/**
 * First-load placeholder rows in the operations table's own columns. Rows alternate like real history (an open
 * operation with its remaining size, then closed ones with a duration and a return), and each bar stands in for a
 * line of the real cell at a typical width, so columns and row height stay put when the data arrives. `note`
 * reserves the count line above the table.
 */
export function OperationsSkeleton({ compact = false, rows, note = false }: { compact?: boolean; rows: number; note?: boolean }) {
  const bar = (className: string) => <Skeleton className={`skeleton-line ${className}`} />
  return <SkeletonStatus label="Loading operations…">
    {note && <p className="workspace-scope-note in-panel"><Skeleton className="skeleton-note" /></p>}
    <div className="workspace-table-wrap"><table className="workspace-table operations-table operations-skeleton">
      <OperationsHead compact={compact} />
      <tbody>{Array.from({ length: rows }, (_, row) => {
        const open = row % 4 === 0
        return <tr key={row} className="operation-row-skeleton">
          <td><div className="operation-heading"><span className="operation-toggle" /><div><Skeleton className="skeleton-asset" /><Skeleton className="skeleton-tags" /></div></div></td>
          <td>{bar('skeleton-account')}{bar(`skeleton-small ${open ? 'skeleton-date' : 'skeleton-dates'}`)}{!open && bar('skeleton-small skeleton-duration')}</td>
          <td className="numeric">{bar('skeleton-size')}{open && bar('skeleton-small skeleton-remaining')}</td>
          {!compact && <><td className="numeric">{bar('skeleton-price')}</td><td className="numeric">{bar('skeleton-price')}</td>
            <td className="numeric"><div className="operation-pnl">{bar('skeleton-money')}{!open && <Skeleton className="skeleton-return" />}</div></td>
            <td className="numeric">{bar('skeleton-fee')}</td><td className="numeric">{bar('skeleton-fee')}</td></>}
          <td className="numeric">{bar('skeleton-money')}</td>
          {!compact && <td><Skeleton className="skeleton-badge" /></td>}
        </tr>
      })}</tbody>
    </table></div>
  </SkeletonStatus>
}

export function OperationsTable({ operations, accounts, compact = false, onPreview, api, onReload, refreshGeneration = 0 }: {
  operations: ActivityOperation[]; accounts: BrokerAccount[]; compact?: boolean
  onPreview?: (operation: ActivityOperation) => void
  /** Reads an expanded operation's executions. */
  api: WorkspaceApi
  /** Reloads Activity when an expanded operation no longer exists. */
  onReload?: () => void
  /** Changes with every accepted Activity response, so expanded operations re-read their executions. */
  refreshGeneration?: number
}) {
  const zone = useDisplayZone()
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const toggle = (id: string) => setExpanded(previous => {
    const next = new Set(previous); if (next.has(id)) next.delete(id); else next.add(id); return next
  })
  // Defense in depth: a management reload may disable an account while an earlier activity read is still visible.
  const visible = operations.filter(op => accounts.some(a => a.id === op.accountId && a.isEnabled !== false))
  if (!visible.length) return <div className="workspace-empty small-empty"><Inbox size={27} /><strong>No operations in this view</strong><p>Refresh an enabled exchange account to retrieve fills, or reset the filters.</p></div>
  return <div className="workspace-table-wrap"><table className="workspace-table operations-table"><OperationsHead compact={compact} /><tbody>{visible.map(op => {
    const open = expanded.has(op.id)
    const status = operationStatus(op)
    const priceReturn = operationPriceReturn(op)
    const priceTone = resultSign(priceReturn)
    return <Fragment key={op.id}><tr className="operation-row" data-expanded={open} onClick={event => {
      if (!(event.target as Element).closest('button, a, input, select, textarea') && !window.getSelection()?.toString()) toggle(op.id)
    }}>
      <td><div className="operation-heading"><button type="button" className="operation-toggle" aria-label={`${open ? 'Collapse' : 'Expand'} ${op.contractId} operation`} aria-expanded={open} aria-controls={open ? `operation-${op.id}` : undefined} onClick={() => toggle(op.id)}>{open ? <ChevronDown size={16} /> : <ChevronRight size={16} />}</button><div><strong className="operation-asset">{op.contractId}</strong><div className="operation-tags"><OperationDirection direction={op.direction} /><OperationLeverage operation={op} /><span className={`operation-status ${status.tone}`}>{status.label}</span></div></div></div></td>
      <td><strong>{accounts.find(a => a.id === op.accountId)?.name ?? 'Account'}</strong><small className="operation-dates" title={`Opened: ${op.openedAtUtc ? otherZoneTime(op.openedAtUtc, zone) : 'unknown'}; ${op.closedAtUtc ? `Closed: ${otherZoneTime(op.closedAtUtc, zone)}` : `Last execution: ${otherZoneTime(op.lastOccurredAtUtc, zone)}`}`}>{operationDates(op, zone)}</small>{op.openedAtUtc !== null && op.closedAtUtc !== null && <small>{duration(op.openedAtUtc, op.closedAtUtc)}</small>}</td>
      <td className="numeric"><strong title={op.quantity === null ? 'Peak quantity unknown' : `Peak quantity: ${op.quantity}`}>{op.quantity === null ? '—' : amount(op.quantity)}</strong>{resultSign(op.openQuantity) === 'positive' && <small>Remaining {amount(op.openQuantity!)}</small>}</td>
      {!compact && <><td className="numeric operation-entry"><span title={op.averageEntryPrice ?? 'Unknown'}>{money(op.averageEntryPrice)}</span></td><td className={`numeric operation-result ${priceTone}`}><span title={op.averageExitPrice ?? 'Unknown'}>{money(op.averageExitPrice)}</span></td><td className="numeric"><div className="operation-pnl"><Result value={op.grossClosedPnlUsd} />{priceReturn !== null && <span className={`operation-return operation-result ${priceTone}`} title="Average entry-to-exit price return, direction-adjusted, before fees and leverage. Not an account or margin return.">{priceReturn}</span>}</div></td>
      <td className="numeric operation-fees"><span title={op.stablecoinFeesNominalUsd}>{money(op.stablecoinFeesNominalUsd)}</span>{op.otherFees.map(f => <small key={f.token}>{amount(f.amount)} {f.token}</small>)}</td>
      <td className="numeric"><Result value={op.fundingNetUsd} />{op.fundingStatus !== 'complete' && <small>{op.fundingStatus === 'partial' ? 'Partial' : 'Unavailable'}</small>}</td></>}
      <td className="numeric operation-net"><Result value={op.netResultUsd} /></td>
      {!compact && <td>{op.playId ? <span className="workspace-badge" title={op.playId}>Play {op.playId.slice(0, 8)}</span> : <span className="workspace-badge">{op.playAssociation === 'mixed' ? 'Mixed' : 'Unassigned'}</span>}</td>}
    </tr>{open && <tr id={`operation-${op.id}`}><td colSpan={compact ? 4 : 10} className="operation-expanded">
      {onPreview && <button className="text-action" aria-label={`Inspect ${op.contractId} retrospective eligibility`} onClick={() => onPreview(op)}>Inspect retrospective eligibility</button>}
      <p className="workspace-scope-note operation-details">Entered {op.totalEnteredQuantity === null ? 'unknown' : amount(op.totalEnteredQuantity)} · Exited {op.totalExitedQuantity === null ? 'unknown' : amount(op.totalExitedQuantity)} · Remaining {op.openQuantity === null ? 'unknown' : amount(op.openQuantity)}</p>
      <dl className="operation-funding-summary">
        <div><dt>{op.fundingStatus === 'complete' ? 'Settled funding' : 'Known settled funding'}</dt><dd><Result value={op.fundingNetUsd} /> <NominalUsdTag detail="Stablecoins at 1:1, with no FX/depeg valuation." /></dd></div>
        <div><dt>Total incl. funding</dt><dd>{op.totalResultUsd === null ? 'Unverified' : <Result value={op.totalResultUsd} />}</dd></div>
      </dl>
      <details className="operation-scope"><summary>About these numbers</summary>
        <p className="workspace-scope-note">Venue-reported P&amp;L {money(op.reportedClosedPnlUsd)} ({op.pnlBasis}).</p>
        {op.fundingAsOfUtc !== null && <p className="workspace-scope-note">Funding coverage through <Time at={op.fundingAsOfUtc} />.</p>}
        {op.coverage.length > 0 && <ul className="operation-coverage">{op.coverage.map(reason => <li key={reason}>{reason}</li>)}</ul>}
      </details>
      <OperationFills api={api} operation={op} accounts={accounts} onReload={onReload} refreshGeneration={refreshGeneration} />
    </td></tr>}</Fragment>
  })}</tbody></table></div>
}
