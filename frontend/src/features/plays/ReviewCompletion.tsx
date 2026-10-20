import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { newDiaryRequestId } from '@/api/diary'
import { errorMessage } from '@/api/errors'
import { playNoteStateLabels, playNoteStates, type PlayNote, type PlayNoteState, type PlaySummary } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { NativeSelect } from '@/components/ui/native-select'
import { time } from '@/lib/format'

/*
 * Review completion and the in-trade log (phase 1, 1.2, decision 3). A review is complete only when the owner says so:
 * Complete review, a save of changed answers, or No review needed for a cancellation that never filled. Writing text never
 * completes it, and log entries never count as a review.
 */

export type ReviewAction = 'complete' | 'reopen' | 'not-needed'

const completedLabels = {
  completed: 'Review completed',
  'not-needed': 'No review needed',
  legacy: 'Reviewed before completion was recorded',
} as const

const day = (at: string) => new Date(at).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })

/**
 * Where the review stands, with the action that changes it; it waits until unsaved edits are saved. No review needed is
 * not offered once the Play is known to have fills; the server refuses it for any linked fill or manual execution.
 */
export function ReviewCompletionBar({ summary, dirty, busy, filled = false, onAction }: {
  summary: Pick<PlaySummary, 'status' | 'reviewCompletion' | 'reviewCompletedAtUtc'>
  dirty: boolean; busy: boolean; filled?: boolean; onAction: (action: ReviewAction) => void
}) {
  const blocked = dirty ? 'Save your changes first' : undefined
  if (summary.status !== 'closed' && summary.status !== 'cancelled') {
    return summary.status === 'open'
      ? <p className="review-completion muted">Notes during the trade belong in the Log. The review is completed after the Play ends.</p>
      : null
  }
  if (summary.reviewCompletion) {
    return <div className="review-completion" role="group" aria-label="Review completion">
      <span><strong>{completedLabels[summary.reviewCompletion]}</strong>
        {summary.reviewCompletedAtUtc && <small> · {day(summary.reviewCompletedAtUtc)}</small>}</span>
      <Button type="button" size="sm" variant="outline" disabled={busy || dirty} title={blocked} onClick={() => onAction('reopen')}>Review again</Button>
    </div>
  }
  return <div className="review-completion" role="group" aria-label="Review completion">
    <span>This review is not complete yet.</span>
    <span className="review-completion-actions">
      {summary.status === 'cancelled' && !filled && <Button type="button" size="sm" variant="outline" disabled={busy || dirty} title={blocked}
        onClick={() => onAction('not-needed')}>No review needed</Button>}
      <Button type="button" size="sm" className="play-primary" disabled={busy || dirty} title={blocked}
        onClick={() => onAction('complete')}>Complete review</Button>
    </span>
  </div>
}

/**
 * The Play's in-trade log: append-only entries with an optional state, read and written on their own. A failed write keeps
 * its request ID, so trying again never writes the entry twice.
 */
export function PlayLog({ api, playId }: { api: WorkspaceApi; playId: string }) {
  const id = useId()
  const [notes, setNotes] = useState<PlayNote[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [text, setText] = useState('')
  const [state, setState] = useState<PlayNoteState | ''>('')
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const attempt = useRef<{ requestId: string; text: string; state: PlayNoteState | null } | null>(null)
  const [reload, setReload] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    api.playNotes(playId, controller.signal).then(value => { if (!controller.signal.aborted) { setNotes(value); setLoadError(null) } })
      .catch(cause => { if (!controller.signal.aborted) setLoadError(errorMessage(cause, 'The log could not be loaded.')) })
    return () => controller.abort()
  }, [api, playId, reload])

  async function add(event: FormEvent) {
    event.preventDefault()
    const entry = { text: text.trim(), state: state === '' ? null : state }
    if (!entry.text || saving) return
    // The same entry keeps its request ID until it is written; an edited entry is a new request.
    if (attempt.current?.text !== entry.text || attempt.current.state !== entry.state) attempt.current = { requestId: newDiaryRequestId(), ...entry }
    setSaving(true); setSaveError(null)
    try {
      setNotes(await api.addPlayNote(playId, attempt.current))
      attempt.current = null
      setText(''); setState('')
    } catch (cause) {
      setSaveError(errorMessage(cause, 'The entry could not be added. Try again; it will not be written twice.'))
    } finally { setSaving(false) }
  }

  return <section className="play-log" aria-label="In-trade log">
    <p className="muted">Timestamped notes as the trade unfolds. Entries cannot be edited or deleted, and they never count as a review.</p>
    {loadError ? <div role="alert"><p className="error">{loadError}</p>
      <Button type="button" size="sm" variant="outline" onClick={() => setReload(n => n + 1)}>Reload the log</Button></div>
      : notes === null ? <p className="muted" role="status">Loading the log…</p>
      : notes.length === 0 ? <p className="muted">No entries yet.</p>
      : <ol className="play-log-entries">{notes.map(note => <li key={note.id}>
        <span className="play-log-meta"><time dateTime={note.createdAtUtc}>{time(note.createdAtUtc)}</time>
          {note.state && <span className="badge play-log-state">{playNoteStateLabels[note.state]}</span>}</span>
        <p>{note.text}</p>
      </li>)}</ol>}
    <form className="play-log-form" onSubmit={event => { void add(event) }}>
      <label htmlFor={`${id}-text`}>Log entry</label>
      <textarea id={`${id}-text`} rows={2} maxLength={2000} value={text} disabled={saving} placeholder="What is happening, and how you feel about it"
        onChange={event => setText(event.target.value)} />
      <label htmlFor={`${id}-state`}>State <small>optional</small></label>
      <NativeSelect id={`${id}-state`} value={state} disabled={saving} onChange={event => setState(event.target.value as PlayNoteState | '')}>
        <option value="">No state</option>
        {playNoteStates.map(value => <option key={value} value={value}>{playNoteStateLabels[value]}</option>)}
      </NativeSelect>
      {saveError && <p className="error" role="alert">{saveError}</p>}
      <Button type="submit" size="sm" variant="outline" disabled={saving || !text.trim()}>{saving ? 'Adding…' : 'Add to log'}</Button>
    </form>
  </section>
}
