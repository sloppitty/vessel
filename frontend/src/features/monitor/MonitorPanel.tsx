import { useEffect, useState } from 'react'
import type { AccountMonitor, MonitorCoverage, MonitorDiscrepancy, MonitorWatch } from '@/api/monitor'
import { Button } from '@/components/ui/button'
import { InfoTip } from '@/components/ui/info-tip'
import { RefreshCw, ShieldQuestion, TriangleAlert } from 'lucide-react'
import { countIncidents, incidentSummary, incidentTone, incidentsBeyondCap, incidentsOf, isIncident, topTone } from './incidents'
import { discrepancyAction, discrepancyFacts, discrepancyOrders, discrepancyTitle, unverifiableAction } from './discrepancy-text'
import { coverageHeading, coverageLines, coverageTone } from './coverage'
import './monitor.css'

const labels = { live: 'Monitored', stale: 'Monitor stale', degraded: 'Monitor degraded', 'credential-error': 'Monitor credential error', idle: 'Monitor idle' }
const time = (value: string | null) => value ? new Date(value).toLocaleString() : 'not recorded'

export function MonitorBadge({ monitor }: { monitor?: AccountMonitor | null }) {
  return monitor && monitor.state !== 'live' && monitor.state !== 'idle'
    ? <span className="monitor-badge" title={monitor.error ?? undefined}>{labels[monitor.state]}</span> : null
}

function checkLabel(monitor: AccountMonitor, busy: boolean, remaining: number) {
  // Scheduled runs and owner refreshes share admission with Check; do not present them as the owner's Check.
  if (monitor.manualState !== 'idle' && monitor.runTrigger === 'scheduled') return 'Scheduled check running'
  if (monitor.manualState !== 'idle' && monitor.runTrigger === 'refresh') return 'Account refresh running'
  if (busy || monitor.manualState === 'running') return 'Checking…'
  if (monitor.manualState === 'pending') return 'Check pending…'
  return remaining ? `Check in ${remaining}s` : 'Check'
}

function CheckButton({ until, monitor, busy, blocked, onCheck }: {
  until: number; monitor: AccountMonitor; busy: boolean; blocked: boolean; onCheck: () => void
}) {
  const [now, setNow] = useState(() => Date.now())
  const counting = until > now
  // Tick only while a countdown is showing; an idle panel keeps no timer.
  useEffect(() => {
    if (!counting) return
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [counting])
  const remaining = Math.max(0, Math.ceil((until - now) / 1000))
  const running = busy || monitor.manualState !== 'idle'
  return <Button size="sm" variant="outline" disabled={running || remaining > 0 || blocked} aria-busy={running} onClick={onCheck}>
    <RefreshCw size={13} className={running ? 'is-spinning' : ''} />{checkLabel(monitor, busy, remaining)}
  </Button>
}

/**
 * The heading follows open incidents, then stop coverage, before the run state, so a healthy run never reads
 * "Monitored" beside an open record, a structural unknown, shared or unsupported coverage. `discrepancies` and
 * `coverage` are what the panel speaks for: the account's by default, a Play's own in its Execution tab.
 * `currentBookRead: false` is the venue capability: its order reader does not return the current book.
 */
export function MonitorPanel({ monitor, discrepancies, coverage = monitor.coverage ?? [], currentBookRead = true, busy, error, retryAtUtc,
  statusUnavailable = false, onCheck, onManageCredential }: {
  monitor: AccountMonitor; busy: boolean; error: string | null; retryAtUtc?: string | undefined
  discrepancies?: readonly MonitorDiscrepancy[] | undefined
  coverage?: readonly MonitorCoverage[]
  currentBookRead?: boolean
  onCheck: () => void; onManageCredential?: (() => void) | undefined
  statusUnavailable?: boolean
}) {
  const until = Math.max(Date.parse(monitor.nextManualAllowedAtUtc ?? '') || 0, Date.parse(retryAtUtc ?? '') || 0)
  // The account's own records may be cut by the server's cap; a Play's list (the Execution tab) carries no such flag.
  const counts = discrepancies ? countIncidents(incidentsOf(discrepancies))
    : countIncidents(incidentsOf(monitor.discrepancies), incidentsBeyondCap([monitor]))
  const tone = topTone(counts)
  // Other run states already name themselves ("Monitor degraded"); only a live run could claim "Monitored".
  const coverageTitle = monitor.state === 'live' ? coverageHeading(coverage) ?? (currentBookRead ? null : 'Current book not read') : null
  const lines = coverageLines(coverage, time, currentBookRead)
  return <section className="monitor-panel" aria-label="Background monitor">
    <div className="monitor-heading">
      <div>{tone ? <><strong className="monitor-attention" data-tone={tone}><TriangleAlert size={14} aria-hidden="true" />Needs attention · {incidentSummary(counts)}</strong>
        {monitor.state !== 'live' && monitor.state !== 'idle' && <span className="monitor-badge">{labels[monitor.state]}</span>}</>
        : coverageTitle ? <strong className="monitor-coverage-heading" data-tone={coverageTone(coverage) ?? 'info'}>
          <ShieldQuestion size={14} aria-hidden="true" />{coverageTitle}</strong>
        : <strong>{labels[monitor.state]}</strong>}
        {(monitor.lastSuccessAtUtc || monitor.nextRunAtUtc) && <p className="muted">
          {monitor.lastSuccessAtUtc && <>Last success {time(monitor.lastSuccessAtUtc)}</>}
          {monitor.lastSuccessAtUtc && monitor.nextRunAtUtc && ' · '}
          {monitor.nextRunAtUtc && <>Next {time(monitor.nextRunAtUtc)}</>}
        </p>}
      </div>
      {/* A new cooldown remounts the button so its countdown starts from the current time. */}
      <CheckButton key={until} until={until} monitor={monitor} busy={busy} blocked={statusUnavailable} onCheck={onCheck} />
    </div>
    {lines.map(line => <p key={line} className="monitor-coverage">{line}</p>)}
    {!monitor.workerOnline && <p className="monitor-warning">Worker offline. Scheduled checks and requested runs may be delayed.</p>}
    {monitor.state === 'stale' && <p className="monitor-warning">Fills may be missing. Last attempt {time(monitor.lastAttemptAtUtc)}.</p>}
    {monitor.state === 'degraded' && <p className="monitor-warning">Incomplete venue coverage.</p>}
    {monitor.error && <p className="monitor-warning">{monitor.error}</p>}
    {monitor.state === 'credential-error' && (onManageCredential
      ? <Button size="sm" variant="outline" onClick={onManageCredential}>Manage account credential</Button>
      : <p className="monitor-warning">Update the read-only credential from this account's Manage account dialog.</p>)}
    {error && <p className="error" role="alert">{error}</p>}
    {statusUnavailable && <p className="monitor-warning">Check is blocked until stored monitor status can be refreshed.</p>}
  </section>
}

/** `levelName` names a plan level from its monitor key ("Entry 1 stop") where the Play is known. */
/** `truncated`: the server's cap left older records out (incidents are listed first). */
export function MonitorDiscrepancies({ discrepancies, busy, onDismiss, levelName, truncated = false }: {
  discrepancies: MonitorDiscrepancy[]; busy: boolean; onDismiss: (id: string, note?: string) => Promise<void>
  levelName?: ((key: unknown) => string | null) | undefined
  truncated?: boolean
}) {
  const [notes, setNotes] = useState<Record<string, string>>({})
  const [pending, setPending] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  async function dismiss(id: string) {
    if (pending || busy) return
    setPending(id); setError(null)
    try { await onDismiss(id, notes[id]?.trim() || undefined) } catch {
      setError('Dismissal was not confirmed. Reload stored state before trying again.')
    } finally { setPending(null) }
  }
  if (!discrepancies.length) return null
  return <section className="monitor-discrepancies" aria-label="Monitor discrepancies">
    <h3>Monitor discrepancies <InfoTip label="About dismissing warnings">Dismissal acknowledges a warning. It does not change executions or stop checks, including resting exits after closure or cancellation.</InfoTip></h3>
    {error && <p className="error" role="alert">{error}</p>}
    <ul>{discrepancies.map(item => {
      const title = discrepancyTitle(item.kind)
      // Open, or unverifiable and not dismissed since: the records that still await the owner and can be dismissed.
      const incident = isIncident(item)
      const unverifiable = item.outcome === 'unverifiable' && incident
      const orders = discrepancyOrders(item, levelName)
      return <li key={item.id} data-incident={incident ? incidentTone(item) : undefined}>
        <div><strong>{title}<span className="monitor-contract"> · {item.contractId}</span></strong>
          <span className="monitor-badge" data-severity={item.severity} data-outcome={unverifiable ? 'unverifiable' : undefined}>
            {item.severity} · {unverifiable ? 'unverifiable' : item.state}</span></div>
        <small>Opened {time(item.openedAtUtc)}{item.confirmedAtUtc ? ` · confirmed ${time(item.confirmedAtUtc)}` : ''}</small>
        <dl className="monitor-facts">
          {discrepancyFacts(item, time).map(fact => <div key={fact.label}><dt>{fact.label}</dt><dd>{fact.value}</dd></div>)}
          {orders.length > 0 && <div className="monitor-orders"><dt>Orders</dt><dd>{orders.map((order, index) => <span key={index}>{order}</span>)}</dd></div>}
        </dl>
        {incident && <p className="monitor-next">Next: {unverifiable && item.state !== 'open' ? unverifiableAction : discrepancyAction(item.kind)}</p>}
        {unverifiable && <p className="monitor-unverifiable">
          Unverifiable since {time(item.unverifiableAtUtc)}: the venue could not confirm or clear this within the monitor lifetime.
          It is not resolved and is no longer checked; a later fresh observation can still decide it.</p>}
        {Object.keys(item.details).length > 0 && <details><summary>Recorded details</summary><pre>{JSON.stringify(item.details, null, 2)}</pre></details>}
        {item.note && <p>{item.note}</p>}
        {incident && <div className="monitor-dismiss">
          <label><span className="sr-only">Dismissal note: {title} · {item.contractId}</span>
            <input maxLength={500} placeholder="Optional dismissal note" value={notes[item.id] ?? ''}
              disabled={busy || pending !== null} onChange={event => { const value = event.target.value; setNotes(current => ({ ...current, [item.id]: value })) }} /></label>
          <Button size="sm" variant="outline" disabled={busy || pending !== null} onClick={() => { void dismiss(item.id) }}>
            {pending === item.id ? 'Dismissing…' : 'Dismiss'}</Button>
        </div>}
      </li>
    })}</ul>
    {truncated && <p className="muted monitor-truncated">Older records not shown.</p>}
  </section>
}

const watchStates = { watching: 'Watching', resolved: 'No longer resting', unverifiable: 'Unverifiable' } as const

/**
 * Linked orders of an ended Play that the monitor keeps checking until the venue shows they no longer rest. `levelName`
 * names a watch's plan level ("Entry 1 stop") where the Play's links are known.
 */
export function MonitorWatches({ watches, levelName }: { watches: MonitorWatch[]; levelName?: ((watch: MonitorWatch) => string | null) | undefined }) {
  if (!watches.length) return null
  return <section className="monitor-watches" aria-label="Watched orders">
    <h3>Watched orders <InfoTip label="About watched orders">After a Play ends, its linked orders are checked until the venue reports them filled, cancelled or no longer open.</InfoTip></h3>
    <ul>{watches.map(watch => <li key={watch.linkId}>
      <div><strong>Order {watch.orderId} · {levelName?.(watch) ?? watch.role}</strong>
        <span className="monitor-badge" data-watch={watch.state}>{watchStates[watch.state]}</span></div>
      <small>Watched since {time(watch.startedAtUtc)}{watch.endedAtUtc ? ` · ended ${time(watch.endedAtUtc)}` : ''}</small>
      {watch.state === 'unverifiable' && <p className="monitor-unverifiable">
        The watch lifetime passed without venue proof. The order may still rest at the venue; check it there.</p>}
    </li>)}</ul>
  </section>
}
