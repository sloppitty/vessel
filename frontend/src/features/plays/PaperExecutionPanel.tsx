import { price } from '@/lib/format'
import { useEffect, useId, useRef, type ReactNode } from 'react'
import { isPaperPlayState, type PaperPlayState } from '@/api/paper'
import type { WorkspaceApi } from '@/api/workspace'
import { useApiRead } from '@/lib/use-api-read'
import { Button } from '@/components/ui/button'
import { Time } from '@/components/ui/time'

type PaperExecutionApi = Pick<WorkspaceApi, 'paperState'>

/** An ISO-8601 instant inside stored text, such as the skipped interval a suspension reason names. */
const instant = /\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})/g

/** Stored text with each instant it names shown in the display zone, like every other time (phase 1, 1.6e). */
function ZonedText({ text }: { text: string }) {
  const parts: ReactNode[] = []
  let last = 0
  for (const match of text.matchAll(instant)) {
    parts.push(text.slice(last, match.index), <Time key={match.index} at={match[0]} style="datetime-seconds" />)
    last = match.index + match[0].length
  }
  parts.push(text.slice(last))
  return <>{parts}</>
}
export interface PaperExecutionPanelProps {
  api: PaperExecutionApi
  playId: string
  onState?: (state: PaperPlayState | null) => void
  reloadGeneration?: number
  tick?: number | null
}
const statusLabels: Record<PaperPlayState['status'], string> = {
  'not-armed': 'Not armed', waiting: 'Waiting', ready: 'Ready',
  ambiguous: 'Ambiguous candle', gap: 'Candle history gap', disabled: 'Paper execution disabled',
  blocked: 'Capital admission blocked', suspended: 'Paper execution suspended',
}
const roleLabels: Record<PaperPlayState['executions'][number]['role'], string> = {
  entry: 'Simulated entry', stop: 'Simulated stop', target: 'Simulated target', exit: 'Simulated exit',
}

/** Reads local paper facts only; timed observation never calls a venue, refreshes candles or starts a check. */
export function PaperExecutionPanel({ api, playId, onState, reloadGeneration = 0, tick }: PaperExecutionPanelProps) {
  const id = useId()
  const read = useApiRead(api, playId, (client, signal) => {
    if (typeof client.paperState !== 'function') throw new Error('Paper execution unavailable')
    return client.paperState(playId, signal).then(state => {
      if (!isPaperPlayState(state) || state.playId.toLowerCase() !== playId.toLowerCase()) throw new Error('Invalid paper execution state')
      return state
    })
  }, { fallback: 'Paper execution could not be loaded. No simulated result is available.', reloadKey: reloadGeneration })
  const { data: state, loading, reload } = read
  const changed = useRef(onState)
  useEffect(() => { changed.current = onState }, [onState])
  // The parent sees the current read's state, and none while a read runs or after it failed.
  useEffect(() => { changed.current?.(state) }, [state])

  useEffect(() => {
    if (!state || state.nextCheckAtUtc === null || ['not-armed', 'disabled', 'suspended'].includes(state.status)) return
    let timer: ReturnType<typeof setTimeout> | undefined
    const schedule = () => {
      clearTimeout(timer)
      if (document.visibilityState === 'hidden') return
      const now = Date.now()
      const next = Date.parse(state.nextCheckAtUtc ?? '')
      const delay = Number.isFinite(next) && next > now ? Math.min(300_000, Math.max(5000, next + 2000 - now)) : 60_000
      timer = setTimeout(reload, delay)
    }
    schedule()
    document.addEventListener('visibilitychange', schedule)
    return () => { clearTimeout(timer); document.removeEventListener('visibilitychange', schedule) }
  }, [state, reload])

  return <section className="execution-panel" aria-labelledby={`${id}-heading`} aria-busy={loading}>
    <div className="execution-header">
      <div><h3 id={`${id}-heading`}>Paper execution · simulation</h3>
        <p className="muted">Simulated executions at defined prices, with zero fees and slippage. No real orders or imported fills.
          This panel observes recorded paper state while visible. Reload does not run a paper check.</p>
        <p className="muted">Model v1 uses 12 decimal places: quantity rounds down, USD margin rounds up, and P&amp;L rounds toward negative infinity.
          Defined plan prices are unchanged.</p></div>
      <Button type="button" variant="outline" size="sm" disabled={loading} onClick={reload}>Reload paper execution</Button>
    </div>
    {loading && <p className="muted" role="status">Loading paper execution…</p>}
    {read.error && <p className="error" role="alert">{read.error}</p>}
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
        <div><dt>Last paper check</dt><dd>{state.checkedAtUtc ? <Time at={state.checkedAtUtc} /> : 'Not checked yet'}</dd></div>
        <div><dt>Next paper check</dt><dd>{state.nextCheckAtUtc ? <Time at={state.nextCheckAtUtc} /> : 'Not scheduled'}</dd></div>
      </dl>
      {state.declinedEntries.length > 0 && <section className="execution-section" aria-label="Declined entries">
        <h3>Declined entries</h3>
        <p className="muted">A declined entry is never retroactively simulated, even if capital later becomes available.</p>
        <ul className="execution-list">{state.declinedEntries.map((entry, index) => <li key={index}>
          <span><strong>Entry ID {entry.entryId}</strong><small>{entry.reason}</small>
            <small>Declined at <Time at={entry.declinedAtUtc} /></small></span>
        </li>)}</ul>
      </section>}
      {state.suspensions.length > 0 && <section className="execution-section" aria-label="Suspension history">
        <h3>Suspension history</h3>
        <p className="muted">Resumption is prospective at a fresh minute boundary. Disabled-period price touches are never replayed.</p>
        <ul className="execution-list">{state.suspensions.map((suspension, index) => <li key={index}>
          <span><strong><ZonedText text={suspension.reason} /></strong>
            <small>Suspended at <Time at={suspension.suspendedAtUtc} /></small>
            <small>{suspension.resumedAtUtc
              ? <>Resumed at <Time at={suspension.resumedAtUtc} /></>
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
            <small>Quantity {execution.quantity} at <span title={execution.price}>{price(execution.price, tick)}</span> · simulated P&amp;L {execution.pnlUsd} USD</small>
            <small>Simulated at <Time at={execution.occurredAtUtc} /></small>
            <small>Candle <Time at={execution.candleOpenAtUtc} /> – <Time at={execution.candleCloseAtUtc} /></small>
            <small>Entry ID {execution.entryId}{execution.levelId !== null && ` · Level ID ${execution.levelId}`}</small>
          </span>
        </li>)}</ul> : <p className="muted">No simulated executions recorded.</p>}
      </section>
    </>}
  </section>
}
