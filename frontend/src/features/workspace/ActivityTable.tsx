import { OperationDirection } from './OperationDirection'
import { sideDirection, resultSign } from './activity-format'
import type { ImportedFill } from '@/api/fills'
import type { BrokerAccount } from '@/api/workspace'
import { amount, money } from '@/lib/format'
import { Inbox } from 'lucide-react'
import { Time } from '@/components/ui/time'

/** Executions inside one operation: the operation row already names its account and instrument. */
export function ActivityTable({ fills, accounts, compact = false }: { fills: ImportedFill[]; accounts: BrokerAccount[]; compact?: boolean }) {
  const visible = fills.filter(fill => accounts.some(a => a.id === fill.accountId && a.isEnabled !== false))
  if (!visible.length) return <div className="workspace-empty small-empty"><Inbox size={27} /><strong>No imported executions yet</strong><p>Refresh an exchange account to retrieve recent perpetual fills.</p></div>
  return <div className="workspace-table-wrap"><table className="workspace-table"><thead><tr><th>Side</th><th>Time</th><th>Quantity</th><th>Price</th>{!compact && <><th>Reported closed P&amp;L</th><th>Association</th></>}</tr></thead>
    <tbody>{visible.map(fill => <tr key={fill.id}>
      <td><OperationDirection direction={sideDirection(fill)} /></td>
      <td><Time at={fill.occurredAtUtc} /></td>
      <td className="numeric">{amount(fill.quantity)}</td><td className="numeric">{money(fill.price)}</td>
      {!compact && <><td className="numeric"><span className={`operation-result ${resultSign(fill.closedPnlUsd)}`}>{money(fill.closedPnlUsd)}</span><small>Fee {amount(fill.fee)} {fill.feeToken}{fill.pnlBasis === 'net-of-fee' ? ' · P&L after fees' : ''}{fill.feeBasis === 'standard-account-free' ? ' · fee-free account' : ''}</small></td><td><span className="workspace-badge" title={fill.playId ?? undefined}>{fill.playId ? `Play ${fill.playId.slice(0, 8)}` : 'Unassigned'}</span></td></>}
    </tr>)}</tbody></table></div>
}
