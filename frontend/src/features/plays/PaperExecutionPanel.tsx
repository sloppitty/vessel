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
    if (!state || state.status === 'not-armed' || state.status === 'disabled') return
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
          This panel observes recorded paper state while visible. Reload does not run a paper check.</p></div>
      <Button type="button" variant="outline" size="sm" disabled={loading} onClick={() => setAttempt(value => value + 1)}>Reload paper execution</Button>
    </div>
    {loading && <p className="muted" role="status">Loading paper execution…</p>}
    {current?.error && <p className="error" role="alert">{current.error}</p>}
    {state && <>
      <p role="status"><span className={`workspace-badge${state.status === 'ambiguous' || state.status === 'gap' ? ' warning-badge' : ''}`}>
        {statusLabels[state.status]}</span></p>
      {state.status === 'ambiguous' && <p className="muted">The candle does not establish execution order. No intrabar winner is assumed;
        unresolved execution is not scored.</p>}
      {state.status === 'gap' && <p className="muted">A complete candle window is unavailable. Unresolved execution is not scored.</p>}
      <dl className="execution-totals" aria-label="Simulated progress">
        <div><dt>Entered quantity</dt><dd>{state.enteredQuantity}</dd></div>
        <div><dt>Exited quantity</dt><dd>{state.exitedQuantity}</dd></div>
        <div><dt>Open quantity</dt><dd>{state.openQuantity}</dd></div>
        <div><dt>Realized simulated P&amp;L</dt><dd>{state.realizedPnlUsd} USD</dd></div>
        <div><dt>Last paper check</dt><dd>{state.checkedAtUtc ? <time dateTime={state.checkedAtUtc}>{time(state.checkedAtUtc)}</time> : 'Not checked yet'}</dd></div>
        <div><dt>Next paper check</dt><dd>{state.nextCheckAtUtc ? <time dateTime={state.nextCheckAtUtc}>{time(state.nextCheckAtUtc)}</time> : 'Not scheduled'}</dd></div>
      </dl>
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
