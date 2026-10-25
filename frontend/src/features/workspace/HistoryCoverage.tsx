import { useEffect, useMemo, useState } from 'react'
import type { FillCoverage, HistoryCoverage as Coverage, HistoryGap, WorkspaceApi } from '@/api/workspace'
import { InfoTip } from '@/components/ui/info-tip'
import { Time } from '@/components/ui/time'

const sourceLabels: Record<FillCoverage['source'], string> = { refresh: 'Refresh', check: 'Scheduled or owner check' }

function Span({ from, to, unknown }: { from: string | null; to: string | null; unknown: string }) {
  return <>{from ? <Time at={from} /> : unknown} – {to ? <Time at={to} /> : 'end unknown'}</>
}

const gapSpan = (gap: HistoryGap) => <Span from={gap.fromUtc} to={gap.toUtc} unknown="start unknown" />

/**
 * Phase 2, item 6: whether the account keeps its execution history, the gaps its imports may have skipped, and what each
 * import covered by the 0.4b continuity rule. A gap closes only when a later read proves its span covered. Nothing shows
 * when the read fails.
 */
export function HistoryCoverage({ api, accountId, reloadKey }: { api: Pick<WorkspaceApi, 'historyCoverage'>; accountId: string; reloadKey: string }) {
  const request = useMemo(() => ({ api, accountId, reloadKey }), [api, accountId, reloadKey])
  const [loaded, setLoaded] = useState<{ request: typeof request; coverage: Coverage } | null>(null)
  useEffect(() => {
    // Clients without the read (older test doubles) show nothing.
    if (typeof request.api.historyCoverage !== 'function') return
    const controller = new AbortController()
    request.api.historyCoverage(request.accountId, controller.signal)
      .then(coverage => { if (!controller.signal.aborted) setLoaded({ request, coverage }) })
      .catch(() => { /* Coverage is context: a failed read leaves it out rather than blocking the account. */ })
    return () => controller.abort()
  }, [request])
  const coverage = loaded?.request === request ? loaded.coverage : null
  if (!coverage || !coverage.available && coverage.recordCount === 0) return null
  const id = `history-coverage-${accountId}`
  return <section className="shell-panel history-coverage" aria-labelledby={id}>
    <header className="shell-panel-heading"><div className="heading-with-tip">
      <h2 id={id}>History coverage</h2>
      <InfoTip label="About history coverage">{coverage.notice}</InfoTip>
    </div><span className="workspace-badge">
      {coverage.keepExecutionHistory ? 'Keeping execution history' : 'History not kept'}</span></header>
    {coverage.keepExecutionHistory && <p className="muted">Read on the monitor&apos;s schedule even without a Play, after protection checks.</p>}
    {coverage.openGapCount > 0 ? <div role="group" aria-label="Possible gaps">
      <p><strong>{coverage.openGapCount} possible {coverage.openGapCount === 1 ? 'gap' : 'gaps'}</strong> in the imported executions:
        missing executions in {coverage.openGapCount === 1 ? 'it' : 'them'} cannot be ruled out.</p>
      <ul className="history-gaps">{coverage.openGaps.map(gap => <li key={gap.id}>{gapSpan(gap)} <small>· found <Time at={gap.openedAtUtc} /></small></li>)}</ul>
      {coverage.openGapCount > coverage.openGaps.length && <p className="muted">Showing the latest {coverage.openGaps.length} of {coverage.openGapCount}.</p>}
    </div> : <p className="muted">No open gap recorded.</p>}
    {coverage.closedGaps.length > 0 && <details><summary>Closed gaps ({coverage.closedGaps.length})</summary>
      <ul className="history-gaps">{coverage.closedGaps.map(gap => <li key={gap.id}>{gapSpan(gap)} <small>· covered <Time at={gap.closedAtUtc!} /></small></li>)}</ul>
    </details>}
    {coverage.recent.length > 0 && <div className="workspace-table-wrap"><table className="workspace-table">
      <caption className="sr-only">Imports, newest first</caption>
      <thead><tr><th>Import</th><th>Reads</th><th>Last read</th><th>Covered</th><th>Possible gap</th></tr></thead>
      <tbody>{coverage.recent.map(r => <tr key={`${r.source}:${r.firstObservedAtUtc}`}>
        <td>{sourceLabels[r.source]}{r.requestedFromUtc && <small> · from <Time at={r.requestedFromUtc} /></small>}</td>
        <td>{r.importCount}</td><td><Time at={r.lastObservedAtUtc} /></td>
        <td>{r.complete ? 'Complete history' : r.coveredToUtc ? <Span from={r.coveredFromUtc} to={r.coveredToUtc} unknown="start" />
          : r.truncated === null ? 'Not known' : 'Nothing proven'}</td>
        <td>{r.gapFromUtc ? <Span from={r.gapFromUtc} to={r.gapToUtc} unknown="start unknown" /> : '—'}</td>
      </tr>)}</tbody>
    </table></div>}
    {coverage.recordCount > coverage.recent.length && <p className="muted">Showing the latest {coverage.recent.length} of {coverage.recordCount} records.</p>}
  </section>
}
