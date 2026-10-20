import { ApiError, errorMessage } from '@/api/errors'
import { useCallback, useEffect, useId, useRef, useState, type FormEvent } from 'react'
import type { WorkspaceApi } from '@/api/workspace'
import type { SavedPlay } from '@/api/plays'
import type { ManualExecutionCorrection, ManualExecutions } from '@/api/diary'
import { Button } from '@/components/ui/button'
import { InfoTip } from '@/components/ui/info-tip'
import { emptyManualDraft, hasManualDraft, manualCorrectionDraft, manualRequest, type ManualExecutionDraft } from './manual'
import type { SavedState } from './saved'
import type { DraftEntry } from './draft'
import { nominalStablecoins } from './stablecoins'
import { NativeSelect } from '@/components/ui/native-select'

export function ManualExecutionPanel({ api, saved, entries, enabled, dirty, form, onChange, onStored, onBegin, working = false, observedVersion }: {
  api: WorkspaceApi
  saved: SavedState | null
  entries: DraftEntry[]
  enabled: boolean
  dirty: boolean
  form: ManualExecutionDraft
  onChange: (form: ManualExecutionDraft) => void
  /** Observe the whole document before using its ledger; false rejects an old or foreign response. */
  onStored: (play: SavedPlay) => boolean
  /** Acquire the page's shared mutation guard and return its owner-only cleanup, or refuse. */
  onBegin: () => (() => void) | null
  working?: boolean
  observedVersion?: number
}) {
  const id = useId()
  const playId = saved?.summary.id
  const [loaded, setLoaded] = useState<{ api: WorkspaceApi; ledger: ManualExecutions } | null>(null)
  const version = Math.max(saved?.summary.version ?? 0, observedVersion ?? 0)
  const ledger = loaded?.api === api && loaded.ledger.play.summary.id === playId && loaded.ledger.play.summary.version >= version
    ? loaded.ledger : null
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const submission = useRef<(() => void) | null>(null)
  const pending = busy || working
  const [reload, setReload] = useState(0)
  // Changing journal tabs must not let a late response replace another Play.
  const alive = useRef(true)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  const latest = useRef({ api, playId })
  useEffect(() => {
    latest.current = { api, playId }
  }, [api, playId])
  const stored = useRef(onStored)
  useEffect(() => { stored.current = onStored })
  const acceptLedger = useCallback((result: ManualExecutions, mutation = false) => {
    const current = latest.current
    if (current.api !== api || current.playId !== result.play.summary.id) return false
    // The session owns the high-water mark, including documents not adopted because of local edits.
    if (!stored.current(result.play)) {
      if (alive.current) setError(`${mutation ? 'The execution was already saved. ' : ''}This response is older than the latest known Play. Reload the manual ledger for current records and totals.`)
      return false
    }
    if (alive.current) { setLoaded({ api, ledger: result }); setError(null) }
    return true
  }, [api])
  useEffect(() => {
    if (!playId) return
    const controller = new AbortController()
    api.manualExecutions(playId, controller.signal).then(result => {
      if (controller.signal.aborted) return
      acceptLedger(result)
    }).catch(cause => { if (!controller.signal.aborted) setError(errorMessage(cause, 'The change could not be completed. Try again.')) })
    return () => controller.abort()
  }, [api, playId, reload, version, acceptLedger])

  const summary = saved?.summary
  const canAdd = !!summary && ['planned', 'open'].includes(summary.status)
  const canCorrect = !!summary && ['open', 'closed'].includes(summary.status)
  const permitted = enabled && !dirty && (form.executionId ? canCorrect : canAdd)
  const change = (patch: Partial<ManualExecutionDraft>) => {
    if (submission.current || pending) return
    const { attempt: _attempt, ...current } = form
    void _attempt
    onChange({ ...current, ...patch })
    setError(null)
  }
  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!saved || !permitted || pending || submission.current) return
    const request = manualRequest(form, saved.summary.version, saved.summary.direction, entries.map(entry => entry.id))
    if ('error' in request) { setError(request.error); return }
    const finish = onBegin()
    if (!finish) return
    submission.current = finish
    // Freeze both request identity and expectedVersion. An uncertain retry sends the exact same payload.
    onChange({ ...form, attempt: request.input })
    setBusy(true); setError(null)
    try {
      const result = form.executionId
        ? await api.correctManualExecution(saved.summary.id, form.executionId, request.input as ManualExecutionCorrection)
        : await api.addManualExecution(saved.summary.id, request.input)
      if (latest.current.api !== api || latest.current.playId !== saved.summary.id) return
      // The shell guards these callbacks by Play identity and mount lifetime.
      // Closing a journal tab must not lose the successful result or leave its input queued.
      acceptLedger(result, true)
      // An older replay still confirms this frozen request succeeded, but cannot supply current totals.
      onChange(emptyManualDraft())
    } catch (cause) {
      // Conflict advice only when the server reports a version conflict.
      const conflict = cause instanceof ApiError && cause.status === 409
      if (alive.current && latest.current.api === api) setError(errorMessage(cause, 'The change could not be completed. Try again.') +
        (conflict ? ' Reload the ledger and review the facts before editing and resubmitting.' : ''))
    } finally {
      // Page actions must unlock even if the Execution tab was closed during the request.
      finish()
      if (submission.current === finish) {
        submission.current = null
        if (alive.current) setBusy(false)
      }
    }
  }
  const field = (key: 'quantity' | 'price' | 'fee' | 'feeCurrency' | 'source' | 'reason', label: string, max: number, decimal = false, list?: string) =>
    <label htmlFor={`${id}-${key}`}>{label}<input id={`${id}-${key}`} type="text" inputMode={decimal ? 'decimal' : 'text'}
      maxLength={max} value={form[key]} list={list} onChange={event => change({ [key]: event.target.value })} /></label>

  return <div className="manual-executions">
    <p className="muted" title="Inverse contracts, other quote currencies and implicit contract multipliers are unsupported.">Linear USD-quoted contracts only, one base unit per quantity.</p>
    {!enabled ? <p role="status">This account is unavailable or disabled. Manual execution changes are blocked.</p>
      : !saved || summary?.status === 'draft' ? <p role="status">Save the Play, then plan it before recording an execution.</p>
      : summary?.status === 'paused' || summary?.status === 'cancelled' ? <p role="status">Recording requires a Planned or Open Play. Closed Plays allow corrections only.</p> : null}
    {dirty && <p role="status">Save the Play changes before changing executions.</p>}
    {saved && <Button type="button" size="sm" variant="outline" disabled={pending}
      onClick={() => { setError(null); setReload(value => value + 1) }}>Reload manual ledger</Button>}
    {error && <p className="error" role="alert">{error}</p>}
    {ledger && <>
      <dl className="manual-totals" aria-label="Manual execution totals">
        <div><dt>Entered quantity</dt><dd>{ledger.totals.enteredQuantity}</dd></div>
        <div><dt>Exited quantity</dt><dd>{ledger.totals.exitedQuantity}</dd></div>
        <div><dt>Open quantity</dt><dd>{ledger.totals.openQuantity}</dd></div>
        <div><dt>Gross realized USD</dt><dd>{ledger.totals.grossResultUsd ?? 'Unavailable'}</dd></div>
        <div><dt>Net realized USD<InfoTip label="About the Play-local result">The Play-local result on the manual ledger basis: sell proceeds minus buy costs at chronological average cost, less listed stablecoin fees. Review and sizing score this same figure.</InfoTip></dt><dd>{ledger.totals.netResultUsd ?? 'Unavailable — costs incomplete'}</dd></div>
      </dl>
      {(!ledger.totals.feesComplete || ledger.notice) && <p className="muted">{ledger.totals.feesComplete ? '' : 'Unknown fees are not zero. '}{ledger.notice}</p>}
      <ol className="manual-records" aria-label="Manual executions">
        {ledger.records.map(record => <li key={record.id}>
          <strong>{record.role === 'entry' ? 'Entry' : 'Exit'} · {record.side} {record.quantity} at {record.price}</strong>
          <time dateTime={record.occurredAtUtc}>{record.occurredAtUtc}</time>
          <span>Source: {record.source} · Fee: {record.fee === null ? 'Unknown' : `${record.fee} ${record.feeCurrency}`}</span>
          <Button type="button" size="sm" variant="outline" disabled={!enabled || dirty || pending || !canCorrect || hasManualDraft(form)}
            aria-label={`Correct ${record.id}`} onClick={() => { if (submission.current || pending) return; setError(null); onChange(manualCorrectionDraft(record)) }}>Correct</Button>
        </li>)}
      </ol>
      <details className="manual-history"><summary>Execution audit history ({ledger.history.length})</summary>
        <ol>{ledger.history.map(change => <li key={change.id}>
          <strong>{change.before ? 'Correction' : 'Recorded'} · {change.reason ?? 'Initial record'}</strong>
          <time dateTime={change.occurredAtUtc}>{change.occurredAtUtc}</time>
          {change.before && <p>Before: {change.before.role} · {change.before.side} {change.before.quantity} at {change.before.price} · {change.before.occurredAtUtc} · {change.before.source} · fee {change.before.fee === null ? 'unknown' : `${change.before.fee} ${change.before.feeCurrency}`}</p>}
          <p>After: {change.after.role} · {change.after.side} {change.after.quantity} at {change.after.price} · {change.after.occurredAtUtc} · {change.after.source} · fee {change.after.fee === null ? 'unknown' : `${change.after.fee} ${change.after.feeCurrency}`}</p>
        </li>)}</ol>
      </details>
    </>}
    {(canAdd || hasManualDraft(form)) && <form onSubmit={event => { void submit(event) }} noValidate aria-label={form.executionId ? 'Correct manual execution' : 'Record manual execution'}>
      <fieldset disabled={!permitted || pending}><legend>{form.executionId ? 'Correct execution' : 'Record execution'}
        <InfoTip label="How executions are recorded">
          <span>Times are UTC, such as 2026-10-13T10:00:00.123456Z; Z or +00:00 is optional. Whole microseconds or coarser; finer precision is rejected by the server, never rounded.</span>
          <span>Decimal or scientific text is kept exactly. Leave both fee fields blank for unknown; known zero needs 0 and a currency. Fees in {nominalStablecoins.join(', ')} are deducted at nominal 1:1, without FX or depeg valuation; other currencies are kept but leave the net result unknown.</span>
          <span>The server validates precision, chronology and over-exits; a Closed correction must remain a closed round trip.</span>
        </InfoTip></legend>
        <div className="manual-fields">
          <label htmlFor={`${id}-role`}>Role<NativeSelect id={`${id}-role`} value={form.role} disabled={!!form.executionId}
            onChange={event => change({ role: event.target.value as 'entry' | 'exit', entryId: '' })}><option value="entry">Entry</option><option value="exit">Exit</option></NativeSelect></label>
          {form.role === 'entry' && <label htmlFor={`${id}-entry`}>Saved plan entry<NativeSelect id={`${id}-entry`} value={form.entryId} disabled={!!form.executionId}
            onChange={event => change({ entryId: event.target.value })}><option value="">Choose entry</option>{entries.map(entry => <option key={entry.id} value={entry.id}>{entry.name}</option>)}</NativeSelect></label>}
          <label htmlFor={`${id}-time`}>Execution time (UTC)<input id={`${id}-time`} type="text" maxLength={64}
            placeholder="YYYY-MM-DDTHH:mm:ss.ffffffZ" aria-describedby={`${id}-time-help`} value={form.occurredAtUtc}
            onChange={event => change({ occurredAtUtc: event.target.value })} /></label>
          <label htmlFor={`${id}-side`}>Side<NativeSelect id={`${id}-side`} value={form.side} disabled={!!form.executionId}
            onChange={event => change({ side: event.target.value as ManualExecutionDraft['side'] })}><option value="">Choose side</option><option value="buy">Buy</option><option value="sell">Sell</option></NativeSelect></label>
          {field('quantity', 'Execution quantity', 96, true)}{field('price', 'Execution price', 96, true)}
          {field('fee', 'Fee amount (blank = unknown)', 96, true)}{field('feeCurrency', 'Fee currency', 32, false, `${id}-fee-currencies`)}
          <datalist id={`${id}-fee-currencies`}>{nominalStablecoins.map(symbol => <option key={symbol} value={symbol} />)}</datalist>
          {field('source', 'Source label', 100)}{form.executionId && field('reason', 'Correction reason', 2000)}
        </div>
        <p id={`${id}-time-help`} className="sr-only">UTC date and time, such as 2026-10-13T10:00:00.123456Z. Whole microseconds or coarser.</p>
        <Button type="submit" size="sm">{busy ? 'Recording…' : form.executionId ? 'Save correction' : form.attempt ? 'Retry same execution' : 'Record execution'}</Button>
      </fieldset>
      {hasManualDraft(form) && <Button type="button" variant="ghost" size="sm" disabled={pending}
        onClick={() => { if (submission.current || pending) return; onChange(emptyManualDraft()); setError(null) }}>Discard execution edit</Button>}
    </form>}
  </div>
}
