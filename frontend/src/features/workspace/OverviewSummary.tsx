import { useEffect, useState, type ReactNode } from 'react'
import type { Overview, WorkspaceApi } from '@/api/workspace'
import type { OverviewQuery, OverviewSummaryDocument } from '@/api/overview-summary'
import { useVenues } from '@/api/venues'
import { Button } from '@/components/ui/button'
import { errorMessage } from '@/api/errors'
import { decimalSign } from '@/lib/decimal'
import { PortfolioIcon } from './PortfolioIcon'
import { VenueLogo } from '@/components/venues/VenueBadge'
import { balanceShare } from './overview-share'
import { money } from '@/lib/format'
import './overview-summary.css'
import { NativeSelect } from '@/components/ui/native-select'
import { CoverageBadge, NominalUsdTag, SummaryCoverageBadge } from './CoverageBadge'
import { Time } from '@/components/ui/time'



const signClass = (value: string | null) => {
  const sign = decimalSign(value)
  return sign === 0 ? '' : sign < 0 ? 'overview-negative' : 'overview-positive'
}
/** `beforeSummary` renders between the metric tiles and the Summary table. `workspaceNote` is the workspace API's scope note,
 * shown verbatim in the Summary's one disclosure. */
export function OverviewSummary({ api, query, onQuery, reloadGeneration, fallback, accountCount, beforeSummary, workspaceNote }: {
  api: WorkspaceApi; query: OverviewQuery; onQuery: (query: OverviewQuery) => void; reloadGeneration: number; fallback: Overview; accountCount: number
  beforeSummary?: ReactNode; workspaceNote?: string
}) {
  const venues = useVenues()
  const [retry, setRetry] = useState(0)
  const [state, setState] = useState<{ api: WorkspaceApi; key: string; document?: OverviewSummaryDocument; error?: string } | null>(null)
  const key = JSON.stringify([query.groupBy, query.period, query.scope, reloadGeneration, retry])
  useEffect(() => {
    const controller = new AbortController()
    api.overviewSummary(query, controller.signal).then(document => {
      if (!controller.signal.aborted) setState({ api, key, document })
    }).catch(cause => { if (!controller.signal.aborted) setState({ api, key, error: errorMessage(cause, 'The change could not be completed. Try again.') }) })
    return () => controller.abort()
  }, [api, key, query])
  const current = state?.api === api && state.key === key ? state : null
  const totals = current?.document?.totals
  // The existing owner-wide endpoint is a fallback only while the All read loads. Never show All amounts under a narrower scope.
  const loadingAll = !current && query.scope === 'all'
  const balance = totals?.balance.amountUsd ?? (loadingAll ? fallback.totals.totalBalanceUsd ?? null : null)
  const available = totals?.available.amountUsd ?? (loadingAll ? fallback.totals.availableBalanceUsd ?? null : null)
  return <>
    <section className="workspace-stat-grid overview-metrics" aria-label="Scoped overview totals" aria-busy={!current}>
      <div className="shell-panel metric"><span className="metric-label">Total balance<NominalUsdTag /></span><strong>{money(balance)}</strong>
        {totals ? <SummaryCoverageBadge value={totals.balance} label="Balance" /> : loadingAll && <CoverageBadge label="Balance" noun="enabled account"
          covered={fallback.totals.balanceAccountCount ?? 0} total={accountCount} status={fallback.totals.balanceCoverage ?? 'unavailable'} />}</div>
      <div className="shell-panel metric"><span className="metric-label">Available balance</span><strong>{money(available)}</strong>
        {totals ? <SummaryCoverageBadge value={totals.available} label="Availability" /> : loadingAll && <CoverageBadge label="Availability" noun="enabled account"
          covered={fallback.totals.availableBalanceAccountCount ?? 0} total={accountCount} status={fallback.totals.availableBalanceCoverage ?? 'unavailable'} />}</div>
      <div className="shell-panel metric"><span className="metric-label">Enabled accounts</span><strong>{totals?.accountCount ?? accountCount}</strong></div>
      <div className="shell-panel metric"><span className="metric-label">Perpetual positions</span><strong>{totals?.openPositionCount ?? (loadingAll ? fallback.totals.openPositionCount : '—')}</strong>
        <small className={signClass(totals?.unrealizedPnl.amountUsd ?? null)}>{money(totals?.unrealizedPnl.amountUsd ?? null)} unrealized</small>
        {totals && <SummaryCoverageBadge value={totals.unrealizedPnl} label="Unrealized" />}</div>
      <div className="shell-panel metric"><span className="metric-label">Imported executions</span><strong>{totals?.importedFillCount ?? (loadingAll ? fallback.totals.importedFillCount : '—')}</strong></div>
    </section>
    {beforeSummary}
    <section className="shell-panel overview-summary" aria-label="Overview summary">
      <header className="shell-panel-heading"><h2>Summary</h2><div className="overview-summary-controls">
        <div className="overview-group-toggle" role="group" aria-label="Summary grouping">{(['portfolio', 'account'] as const).map(group => <Button key={group} variant={query.groupBy === group ? 'secondary' : 'ghost'} size="sm" aria-pressed={query.groupBy === group} onClick={() => onQuery({ ...query, groupBy: group })}>By {group}</Button>)}</div>
        <label>Result period<NativeSelect value={query.period} autoComplete="off" onChange={e => onQuery({ ...query, period: e.target.value as OverviewQuery['period'] })}><option value="7">Last 7 days</option><option value="30">Last 30 days</option><option value="90">Last 90 days</option><option value="all">All retained history</option></NativeSelect></label>
      </div></header>
      {!current && <div className="workspace-loading" role="status">Loading summary…</div>}
      {current?.error && <div className="workspace-alert" role="alert">{current.error}<Button variant="ghost" size="sm" onClick={() => setRetry(n => n + 1)}>Retry summary</Button></div>}
      {current?.document && <>
        {!current.document.rows.length ? <p className="workspace-empty small-empty">No enabled accounts in this scope.</p> : <div className="overview-summary-scroll"><table className="overview-summary-table"><caption className="sr-only">Overview summary by {query.groupBy}</caption><thead><tr><th scope="col">Name</th><th scope="col">Balance</th><th scope="col">Available</th><th scope="col">Share of total</th><th scope="col">Open positions</th><th scope="col">Realized result</th></tr></thead><tbody>
          {current.document.rows.map(row => {
            const share = balanceShare(row.shareOfBalance)
            return <tr key={row.id} onClick={() => onQuery({ ...query, scope: row.id })}>
              <th scope="row"><button className="overview-summary-name" aria-label={`Scope overview to ${row.name}`} onClick={e => { e.stopPropagation(); onQuery({ ...query, scope: row.id }) }}>
                {row.icon ? <PortfolioIcon icon={row.icon} /> : row.venueId ? <VenueLogo venueId={row.venueId} size="md" /> : null}<span>{row.name}<small>{row.venueId ? venues.name(row.venueId) : `${row.totals.accountCount} ${row.totals.accountCount === 1 ? 'account' : 'accounts'}`}</small></span>
              </button></th>
              <td><span>{money(row.totals.balance.amountUsd)}</span><SummaryCoverageBadge value={row.totals.balance} label={`${row.name} balance`} /></td>
              <td><span>{money(row.totals.available.amountUsd)}</span><SummaryCoverageBadge value={row.totals.available} label={`${row.name} availability`} /></td>
              <td><span title={row.shareOfBalance ? `${row.shareOfBalance.numerator} / ${row.shareOfBalance.denominator} · rounded for display` : 'Share unavailable'}>{share.label}</span><span className="overview-share-track" aria-hidden="true"><span style={{ width: share.width }} /></span></td>
              <td><span>{row.totals.openPositionCount}</span><span className={signClass(row.totals.unrealizedPnl.amountUsd)}>{money(row.totals.unrealizedPnl.amountUsd)} unrealized</span><SummaryCoverageBadge value={row.totals.unrealizedPnl} label={`${row.name} unrealized`} /></td>
              <td><span className={signClass(row.totals.realizedResult.amountUsd)}>{money(row.totals.realizedResult.amountUsd)}</span><SummaryCoverageBadge value={row.totals.realizedResult} operations label={`${row.name} realized`} /></td>
            </tr>
          })}
        </tbody></table></div>}
        {current.document.omittedGroupCount > 0 && <p className="overview-omitted">+{current.document.omittedGroupCount} {current.document.omittedGroupCount === 1 ? 'group' : 'groups'}, included in totals</p>}
        <div className="overview-realized-total">Scope realized result <strong className={signClass(current.document.totals.realizedResult.amountUsd)}>{money(current.document.totals.realizedResult.amountUsd)}</strong><SummaryCoverageBadge value={current.document.totals.realizedResult} operations label="Realized" /></div>
      </>}
      {(current?.document || workspaceNote) && <details className="operation-scope"><summary>About these numbers</summary>
        {current?.document && <p className="workspace-scope-note">{current.document.scopeNote}</p>}
        {workspaceNote && workspaceNote !== current?.document?.scopeNote && <p className="workspace-scope-note">{workspaceNote}</p>}
        {current?.document && <><p className="workspace-scope-note">Window ends <Time at={current.document.asOfUtc} style="datetime-seconds" />{current.document.fromUtc
          ? <>, starts <Time at={current.document.fromUtc} style="datetime-seconds" /></> : ', all retained history'}.</p>{(['balance', 'available', 'unrealizedPnl', 'realizedResult'] as const).flatMap(name => current.document!.totals[name].coverage.reasons.map((r, i) => <p className="workspace-scope-note" key={`${name}-${i}`}>{name}: {r.count} · {r.reason}</p>))}</>}
      </details>}
    </section>
  </>
}
