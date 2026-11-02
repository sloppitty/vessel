import { useEffect, useMemo, useState } from 'react'
import type { FillConflictField, FillConflicts, WorkspaceApi } from '@/api/workspace'
import { InfoTip } from '@/components/ui/info-tip'
import { Time } from '@/components/ui/time'

const fieldLabels: Record<FillConflictField, string> = {
  side: 'side', price: 'price', quantity: 'quantity', fee: 'fee', feeToken: 'fee asset', closedPnl: 'closed P&L', time: 'time',
  orderId: 'order ID', transactionHash: 'transaction', venueContract: 'venue market', leverage: 'leverage', direction: 'direction',
}

/**
 * Phase 2, item 14c (F-12): retained fills the venue later reported with different values. Vessel keeps the first copy;
 * this lists which fields differed and when, never either value. Nothing shows while there are none or the read fails.
 */
export function ImportConflicts({ api, accountId, reloadKey }: { api: Pick<WorkspaceApi, 'importConflicts'>; accountId: string; reloadKey: string }) {
  const request = useMemo(() => ({ api, accountId, reloadKey }), [api, accountId, reloadKey])
  const [loaded, setLoaded] = useState<{ request: typeof request; conflicts: FillConflicts } | null>(null)
  useEffect(() => {
    // Clients without the read (older test doubles) show nothing.
    if (typeof request.api.importConflicts !== 'function') return
    const controller = new AbortController()
    request.api.importConflicts(request.accountId, controller.signal)
      .then(conflicts => { if (!controller.signal.aborted) setLoaded({ request, conflicts }) })
      .catch(() => { /* An optional notice: a failed read leaves it out rather than blocking the account. */ })
    return () => controller.abort()
  }, [request])
  const conflicts = loaded?.request === request ? loaded.conflicts : null
  if (!conflicts || conflicts.total === 0) return null
  return <section className="shell-panel" aria-labelledby={`import-conflicts-${accountId}`}>
    <header className="shell-panel-heading"><div className="heading-with-tip">
      <h2 id={`import-conflicts-${accountId}`}>Import conflicts</h2>
      <InfoTip label="About import conflicts">{conflicts.notice}</InfoTip>
    </div><span className="workspace-badge warning-badge">{conflicts.total} {conflicts.total === 1 ? 'fill' : 'fills'}</span></header>
    <div className="workspace-table-wrap"><table className="workspace-table">
      <thead><tr><th>Fill</th><th>Contract</th><th>Fields that differed</th><th>First seen</th><th>Last seen</th><th>Reports</th></tr></thead>
      <tbody>{conflicts.recent.map(c => <tr key={`${c.contractId}:${c.sourceFillId}`}>
        <td><code>{c.sourceFillId}</code></td><td>{c.contractId}</td><td>{c.fields.map(f => fieldLabels[f]).join(', ')}</td>
        <td><Time at={c.firstSeenAtUtc} /></td><td><Time at={c.lastSeenAtUtc} /></td><td>{c.reports}</td>
      </tr>)}</tbody>
    </table></div>
    {conflicts.total > conflicts.recent.length && <p className="muted">Showing the latest {conflicts.recent.length} of {conflicts.total}.</p>}
  </section>
}
