import { useEffect, useState, type ReactNode } from 'react'
import type { AccountMonitor, MonitorDiscrepancy, MonitorWatch } from '@/api/monitor'
import { Button } from '@/components/ui/button'
import { InfoTip } from '@/components/ui/info-tip'
import { RefreshCw } from 'lucide-react'
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

export function MonitorPanel({ monitor, busy, error, retryAtUtc, statusUnavailable = false, onCheck, onManageCredential }: {
  monitor: AccountMonitor; busy: boolean; error: string | null; retryAtUtc?: string | undefined
  onCheck: () => void; onManageCredential?: (() => void) | undefined
  statusUnavailable?: boolean
}) {
  const until = Math.max(Date.parse(monitor.nextManualAllowedAtUtc ?? '') || 0, Date.parse(retryAtUtc ?? '') || 0)
  return <section className="monitor-panel" aria-label="Background monitor">
    <div className="monitor-heading">
      <div><strong>{labels[monitor.state]}</strong>
        {(monitor.lastSuccessAtUtc || monitor.nextRunAtUtc) && <p className="muted">
          {monitor.lastSuccessAtUtc && <>Last success {time(monitor.lastSuccessAtUtc)}</>}
          {monitor.lastSuccessAtUtc && monitor.nextRunAtUtc && ' · '}
          {monitor.nextRunAtUtc && <>Next {time(monitor.nextRunAtUtc)}</>}
        </p>}
      </div>
      {/* A new cooldown remounts the button so its countdown starts from the current time. */}
      <CheckButton key={until} until={until} monitor={monitor} busy={busy} blocked={statusUnavailable} onCheck={onCheck} />
    </div>
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

export function MonitorDiscrepancies({ discrepancies, busy, onDismiss, action }: {
  discrepancies: MonitorDiscrepancy[]; busy: boolean; onDismiss: (id: string, note?: string) => Promise<void>
  /** What the owner can do about one record besides dismissing it, such as rebinding a drifted level. */
  action?: ((item: MonitorDiscrepancy) => ReactNode) | undefined
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
    <ul>{discrepancies.map(item => <li key={item.id}>
      <div><strong>{item.kind.replaceAll('-', ' ').replaceAll('_', ' ')} · {item.contractId}</strong>
        <span className="monitor-badge" data-severity={item.severity} data-outcome={item.outcome ?? undefined}>
          {item.severity} · {item.outcome === 'unverifiable' ? 'unverifiable' : item.state}</span></div>
      <small>Opened {time(item.openedAtUtc)}{item.confirmedAtUtc ? ` · confirmed ${time(item.confirmedAtUtc)}` : ''}</small>
      {item.outcome === 'unverifiable' && <p className="monitor-unverifiable">
        Unverifiable since {time(item.unverifiableAtUtc)}: the venue could not confirm or clear this within the monitor lifetime.
        It is not resolved and is no longer checked; a later fresh observation can still decide it.</p>}
      {Object.keys(item.details).length > 0 && <details><summary>Recorded details</summary><pre>{JSON.stringify(item.details, null, 2)}</pre></details>}
      {item.note && <p>{item.note}</p>}
      {action?.(item)}
      {(item.state === 'open' || item.outcome === 'unverifiable') && <div className="monitor-dismiss">
        <label><span className="sr-only">Dismissal note for {item.kind} {item.contractId}</span>
          <input maxLength={500} placeholder="Optional dismissal note" value={notes[item.id] ?? ''}
            disabled={busy || pending !== null} onChange={event => { const value = event.target.value; setNotes(current => ({ ...current, [item.id]: value })) }} /></label>
        <Button size="sm" variant="outline" disabled={busy || pending !== null} onClick={() => { void dismiss(item.id) }}>
          {pending === item.id ? 'Dismissing…' : 'Dismiss'}</Button>
      </div>}
    </li>)}</ul>
  </section>
}

const watchStates = { watching: 'Watching', resolved: 'No longer resting', unverifiable: 'Unverifiable' } as const
const watchRoles = { entry: 'Entry', stop: 'Stop', target: 'Target', exit: 'Exit' } as const

/** Linked orders of an ended Play that the monitor keeps checking until the venue shows they no longer rest. */
export function MonitorWatches({ watches }: { watches: MonitorWatch[] }) {
  if (!watches.length) return null
  return <section className="monitor-watches" aria-label="Watched orders">
    <h3>Watched orders <InfoTip label="About watched orders">After a Play ends, its linked orders are checked until the venue reports them filled, cancelled or no longer open.</InfoTip></h3>
    <ul>{watches.map(watch => <li key={watch.linkId}>
      <div><strong>{watchRoles[watch.role]} order {watch.orderId}</strong>
        <span className="monitor-badge" data-watch={watch.state}>{watchStates[watch.state]}</span></div>
      <small>Watched since {time(watch.startedAtUtc)}{watch.endedAtUtc ? ` · ended ${time(watch.endedAtUtc)}` : ''}</small>
      {watch.state === 'unverifiable' && <p className="monitor-unverifiable">
        The watch lifetime passed without venue proof. The order may still rest at the venue; check it there.</p>}
    </li>)}</ul>
  </section>
}
