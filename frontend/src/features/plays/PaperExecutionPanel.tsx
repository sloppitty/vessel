import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { errorMessage } from '@/api/errors'
import { isPaperPlayState, type PaperPlayState } from '@/api/paper'
import type { WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'

type PaperExecutionApi = Pick<WorkspaceApi, 'paperState'>
type PaperStateRequest = { api: PaperExecutionApi; playId: string; attempt: number; reloadGeneration: number }
export interface PaperExecutionPanelProps {
  api: PaperExecutionApi
  playId: string
  onState?: (state: PaperPlayState | null) => void
  reloadGeneration?: number
}
const statusLabels: Record<PaperPlayState['status'], string> = {
  'not-armed': 'Not armed', waiting: 'Waiting', ready: 'Ready',
  ambiguous: 'Ambiguous candle', gap: 'Candle history gap', disabled: 'Paper execution disabled',
  blocked: 'Capital admission blocked', suspended: 'Paper execution suspended',
}
const roleLabels: Record<PaperPlayState['executions'][number]['role'], string> = {
  entry: 'Simulated entry', stop: 'Simulated stop', target: 'Simulated target', exit: 'Simulated exit',
}
const time = (value: string) => `${new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' })} UTC`

/** Reads local paper facts only; timed observation never calls a venue, refreshes candles or starts a check. */
export function PaperExecutionPanel({ api, playId, onState, reloadGeneration = 0 }: PaperExecutionPanelProps) {
  const id = useId()
  const [attempt, setAttempt] = useState(0)
  const request = useMemo(() => ({ api, playId, attempt, reloadGeneration }), [api, playId, attempt, reloadGeneration])
  const [loaded, setLoaded] = useState<{
    request: PaperStateRequest; state: PaperPlayState | null; error: string | null
  } | null>(null)
  const changed = useRef(onState)
  useEffect(() => { changed.current = onState }, [onState])
  useEffect(() => {
    const controller = new AbortController()
    changed.current?.(null)
    void (async () => {
      try {
        if (typeof request.api.paperState !== 'function') throw new Error('Paper execution unavailable')
        const state = await request.api.paperState(request.playId, controller.signal)
        if (controller.signal.aborted) return
        if (!isPaperPlayState(state) || state.playId.toLowerCase() !== request.playId.toLowerCase()) throw new Error('Invalid paper execution state')
        setLoaded({ request, state, error: null })
        changed.current?.(state)
      } catch (cause) {
        if (!controller.signal.aborted) {
          setLoaded({ request, state: null,
            error: errorMessage(cause, 'Paper execution could not be loaded. No simulated result is available.') })
          changed.current?.(null)
        }
      }
    })()
    return () => controller.abort()
  }, [request])
  const current = loaded?.request === request ? loaded : null
  const state = current?.state
  const loading = current === null

  useEffect(() => {
    if (!state || state.nextCheckAtUtc === null || ['not-armed', 'disabled', 'suspended'].includes(state.status)) return
    let timer: ReturnType<typeof setTimeout> | undefined
    const schedule = () => {
      clearTimeout(timer)
      if (document.visibilityState === 'hidden') return
      const now = Date.now()
      const next = Date.parse(state.nextCheckAtUtc ?? '')
      const delay = Number.isFinite(next) && next > now ? Math.min(300_000, Math.max(5000, next + 2000 - now)) : 60_000
      timer = setTimeout(() => setAttempt(value => value + 1), delay)
    }
    schedule()
    document.addEventListener('visibilitychange', schedule)
    return () => { clearTimeout(timer); document.removeEventListener('visibilitychange', schedule) }
  }, [state])

  return <section className="execution-panel" aria-labelledby={`${id}-heading`} aria-busy={loading}>
    <div className="execution-header">
      <div><h3 id={`${id}-heading`}>Paper execution · simulation</h3>
        <p className="muted">Simulated executions at defined prices, with zero fees and slippage. No real orders or imported fills.
          This panel observes recorded paper state while visible. Reload does not run a paper check.</p>
        <p className="muted">Model v1 uses 12 decimal places: quantity rounds down, USD margin rounds up, and P&amp;L rounds toward negative infinity.
          Defined plan prices are unchanged.</p></div>
      <Button type="button" variant="outline" size="sm" disabled={loading} onClick={() => setAttempt(value => value + 1)}>Reload paper execution</Button>
    </div>
    {loading && <p className="muted" role="status">Loading paper execution…</p>}
    {current?.error && <p className="error" role="alert">{current.error}</p>}
    {state && <>
      <p role="status"><span className={`workspace-badge${['ambiguous', 'gap', 'blocked', 'suspended'].includes(state.status) ? ' warning-badge' : ''}`}>
        {statusLabels[state.status]}</span></p>
      {state.status === 'ambiguous' && <p className="muted">The candle does not establish execution order. No intrabar winner is assumed;
        unresolved execution is not scored.</p>}
      {state.status === 'gap' && <p className="muted">A complete candle window is unavailable. Unresolved execution is not scored.</p>}
      {state.status === 'blocked' && <p className="muted">New capital admission was blocked. Protective stops and targets for open tranches continue
        to be evaluated at scheduled paper checks.</p>}
      {state.status === 'suspended' && <p className="muted">Paper evaluation is suspended. Re-enabling resumes prospectively at a fresh minute boundary.
        Reservations remain held while open exposure is unknown.</p>}
      {state.hasUnscoredHistory && <p className="workspace-scope-note">Unscored history: this Play has an unevaluated period or unresolved history.
        Its outcome remains excluded from scoring, even after resumption.</p>}
      <dl className="execution-totals" aria-label="Simulated progress">
        <div><dt>Entered quantity</dt><dd>{state.enteredQuantity}</dd></div>
        <div><dt>Exited quantity</dt><dd>{state.exitedQuantity}</dd></div>
        <div><dt>Open quantity</dt><dd>{state.openQuantity}</dd></div>
        <div><dt>Realized simulated P&amp;L</dt><dd>{state.realizedPnlUsd} USD</dd></div>
        <div><dt>Last paper check</dt><dd>{state.checkedAtUtc ? <time dateTime={state.checkedAtUtc}>{time(state.checkedAtUtc)}</time> : 'Not checked yet'}</dd></div>
        <div><dt>Next paper check</dt><dd>{state.nextCheckAtUtc ? <time dateTime={state.nextCheckAtUtc}>{time(state.nextCheckAtUtc)}</time> : 'Not scheduled'}</dd></div>
      </dl>
      {state.declinedEntries.length > 0 && <section className="execution-section" aria-label="Declined entries">
        <h3>Declined entries</h3>
        <p className="muted">A declined entry is never retroactively simulated, even if capital later becomes available.</p>
        <ul className="execution-list">{state.declinedEntries.map((entry, index) => <li key={index}>
          <span><strong>Entry ID {entry.entryId}</strong><small>{entry.reason}</small>
            <small>Declined at <time dateTime={entry.declinedAtUtc}>{time(entry.declinedAtUtc)}</time></small></span>
        </li>)}</ul>
      </section>}
      {state.suspensions.length > 0 && <section className="execution-section" aria-label="Suspension history">
        <h3>Suspension history</h3>
        <p className="muted">Resumption is prospective at a fresh minute boundary. Disabled-period price touches are never replayed.</p>
        <ul className="execution-list">{state.suspensions.map((suspension, index) => <li key={index}>
          <span><strong>{suspension.reason}</strong>
            <small>Suspended at <time dateTime={suspension.suspendedAtUtc}>{time(suspension.suspendedAtUtc)}</time></small>
            <small>{suspension.resumedAtUtc
              ? <>Resumed at <time dateTime={suspension.resumedAtUtc}>{time(suspension.resumedAtUtc)}</time></>
              : 'Not resumed'}</small></span>
        </li>)}</ul>
      </section>}
      {state.issues.length > 0 && <section className="execution-section execution-issues" aria-label="Paper execution issues">
        <h3>Paper execution issues</h3>
        <ul className="execution-list">{state.issues.map((issue, index) => <li key={index}>{issue}</li>)}</ul>
      </section>}
      <section className="execution-section" aria-label="Simulated executions">
        <h3>Simulated executions</h3>
        {state.executions.length > 0 ? <ul className="execution-list">{state.executions.map(execution => <li key={execution.id}>
          <span><strong>{roleLabels[execution.role]}</strong>
            <small>Quantity {execution.quantity} at {execution.price} · simulated P&amp;L {execution.pnlUsd} USD</small>
            <small>Simulated at <time dateTime={execution.occurredAtUtc}>{time(execution.occurredAtUtc)}</time></small>
            <small>Candle <time dateTime={execution.candleOpenAtUtc}>{time(execution.candleOpenAtUtc)}</time> – <time dateTime={execution.candleCloseAtUtc}>{time(execution.candleCloseAtUtc)}</time></small>
            <small>Entry ID {execution.entryId}{execution.levelId !== null && ` · Level ID ${execution.levelId}`}</small>
          </span>
        </li>)}</ul> : <p className="muted">No simulated executions recorded.</p>}
      </section>
    </>}
  </section>
}
