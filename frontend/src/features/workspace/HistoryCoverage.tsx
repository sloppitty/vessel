import { useEffect, useMemo, useState } from 'react'
import { errorMessage } from '@/api/errors'
import type { FillCoverage, HistoryBackfill, HistoryCoverage as Coverage, HistoryGap, WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { InfoTip } from '@/components/ui/info-tip'
import { Time } from '@/components/ui/time'

/** How often the panel reads again while a backfill is reading (Phase 2, item 6b). */
export const backfillPollMs = 15_000

function backfillText(b: HistoryBackfill) {
  const read = `${b.pages} ${b.pages === 1 ? 'page' : 'pages'}, ${b.addedFills} ${b.addedFills === 1 ? 'execution' : 'executions'} added`
  return b.state === 'queued' ? `Reading behind protection checks · ${read}`
    : b.state === 'completed' ? `Covered · ${read}`
    : b.state === 'narrowed' ? `Partly covered: the start is beyond the venue's reach · ${read}`
    : `Stopped: ${b.error ?? 'the backfill failed.'}`
}

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
export function HistoryCoverage({ api, accountId, reloadKey }: {
  api: Pick<WorkspaceApi, 'historyCoverage'> & Partial<Pick<WorkspaceApi, 'startBackfill'>>; accountId: string; reloadKey: string
}) {
  const [generation, setGeneration] = useState(0)
  const request = useMemo(() => ({ api, accountId, reloadKey, generation }), [api, accountId, reloadKey, generation])
  const [loaded, setLoaded] = useState<{ request: typeof request; coverage: Coverage } | null>(null)
  const [starting, setStarting] = useState<{ gapId: string; error: string | null } | null>(null)
  useEffect(() => {
    // Clients without the read (older test doubles) show nothing.
    if (typeof request.api.historyCoverage !== 'function') return
    const controller = new AbortController()
    request.api.historyCoverage(request.accountId, controller.signal)
      .then(coverage => { if (!controller.signal.aborted) setLoaded({ request, coverage }) })
      .catch(() => { /* Coverage is context: a failed read leaves it out rather than blocking the account. */ })
    return () => controller.abort()
  }, [request])
  const coverage = loaded?.request.api === api && loaded.request.accountId === accountId ? loaded.coverage : null
  const active = coverage?.backfills?.find(b => b.state === 'queued') ?? null
  // While a backfill reads, the panel reads again now and then; it stops with the backfill or the page.
  useEffect(() => {
    if (!active) return
    const timer = setTimeout(() => setGeneration(value => value + 1), backfillPollMs)
    return () => clearTimeout(timer)
  }, [active, loaded])
  async function start(gapId: string) {
    if (!api.startBackfill || starting && !starting.error) return
    setStarting({ gapId, error: null })
    try {
      await api.startBackfill(accountId, gapId)
      setStarting(null)
    } catch (cause) {
      setStarting({ gapId, error: errorMessage(cause, 'The backfill could not be started.') })
    }
    setGeneration(value => value + 1)
  }
  const canBackfill = coverage?.backfillAvailable === true && !!api.startBackfill && !active
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
      <ul className="history-gaps">{coverage.openGaps.map(gap => <li key={gap.id}>{gapSpan(gap)} <small>· found <Time at={gap.openedAtUtc} /></small>
        {gap.beyondVenueReach ? <small> · beyond the venue&apos;s reach</small>
          : canBackfill && <Button type="button" size="sm" variant="outline" disabled={starting?.gapId === gap.id && !starting.error}
            aria-describedby={`${id}-backfill-help`} onClick={() => { void start(gap.id) }}>Backfill</Button>}
        {starting?.gapId === gap.id && starting.error && <span className="error" role="alert"> {starting.error}</span>}</li>)}</ul>
      {canBackfill && <p id={`${id}-backfill-help`} className="muted">Backfill reads the gap&apos;s executions from the venue, behind protection
        checks. It never creates a Play or an execution notification.</p>}
      {coverage.openGapCount > coverage.openGaps.length && <p className="muted">Showing the latest {coverage.openGaps.length} of {coverage.openGapCount}.</p>}
    </div> : <p className="muted">No open gap recorded.</p>}
    {coverage.backfills && coverage.backfills.length > 0 && <div role="group" aria-label="Backfills">
      <ul className="history-gaps">{coverage.backfills.map(b => <li key={b.id}>{backfillText(b)} <small>· started <Time at={b.createdAtUtc} /></small></li>)}</ul>
    </div>}
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
