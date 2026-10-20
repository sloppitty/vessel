import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { newDiaryRequestId } from '@/api/diary'
import { errorMessage } from '@/api/errors'
import { playNoteStateLabels, playNoteStates, type PlayNote, type PlayNoteState, type PlaySummary } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { NativeSelect } from '@/components/ui/native-select'
import { time } from '@/lib/format'
import type { PlayLogChange, PlayLogDraft } from './log-draft'

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
  // An older server sends no completion and has no completion actions; its lists still read review text as before.
  if (summary.reviewCompletion === undefined) return null
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
 * its request ID, so trying again never writes the entry twice. The entry being written lives in `draft` when the page
 * keeps it, otherwise here.
 */
export function PlayLog({ api, playId, draft, onDraft }: {
  api: WorkspaceApi; playId: string; draft?: PlayLogDraft | undefined; onDraft?: (change: PlayLogChange) => void
}) {
  const id = useId()
  const [notes, setNotes] = useState<PlayNote[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [own, setOwn] = useState<PlayLogDraft | undefined>(undefined)
  const kept = onDraft ? draft : own
  const current = kept?.playId === playId ? kept : { playId, text: '', state: '' as const }
  const change = (next: PlayLogChange) => { if (onDraft) onDraft(next); else setOwn(next) }
  // Typing here makes this Play's entry the kept one.
  const edit = (patch: Partial<PlayLogDraft>) => change(stored => ({ ...(stored?.playId === playId ? stored : { playId, text: '', state: '' }), ...patch }))
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [reload, setReload] = useState(0)
  // A write confirmed while another Log was mounted counts here too: the list is read again.
  const written = kept?.playId === playId ? kept.written ?? 0 : 0
  // Reads and writes answer out of order. A list counts only if it was read after the latest written list arrived, so an
  // older read never hides an entry that a write already showed.
  const sequence = useRef(0)
  const shown = useRef(0)
  // The confirmed-write count this Log's own write produced: it already shows that list, so it needs no read for it.
  const ownWrite = useRef(-1)
  const lastRead = useRef<{ api: WorkspaceApi; playId: string; reload: number } | null>(null)
  useEffect(() => {
    const previous = lastRead.current
    lastRead.current = { api, playId, reload }
    if (previous?.api === api && previous.playId === playId && previous.reload === reload && written === ownWrite.current) return
    const controller = new AbortController()
    const read = ++sequence.current
    api.playNotes(playId, controller.signal).then(value => {
      if (controller.signal.aborted || read < shown.current) return
      shown.current = read
      setNotes(value); setLoadError(null)
    }).catch(cause => { if (!controller.signal.aborted && read >= shown.current) setLoadError(errorMessage(cause, 'The log could not be loaded.')) })
    return () => controller.abort()
  }, [api, playId, reload, written])

  async function add(event: FormEvent) {
    event.preventDefault()
    const entry = { text: current.text.trim(), state: current.state === '' ? null : current.state }
    if (!entry.text || saving) return
    // The same entry keeps its request ID until it is written; an edited entry is a new request.
    const attempt = current.attempt?.text === entry.text && current.attempt.state === entry.state ? current.attempt
      : { requestId: newDiaryRequestId(), ...entry }
    edit({ attempt })
    setSaving(true); setSaveError(null)
    try {
      const written = await api.addPlayNote(playId, attempt)
      shown.current = ++sequence.current
      setNotes(written); setLoadError(null)
      // Possibly after a remount, with newer text typed: only the entry this write sent is cleared.
      change(stored => {
        if (stored?.playId !== playId || stored.attempt?.requestId !== attempt.requestId) return stored
        const count = (stored.written ?? 0) + 1
        ownWrite.current = count
        if (stored.text.trim() === attempt.text && (stored.state === '' ? null : stored.state) === attempt.state) return { playId, text: '', state: '', written: count }
        const { attempt: _sent, ...rest } = stored
        void _sent
        return { ...rest, written: count }
      })
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
      <textarea id={`${id}-text`} rows={2} maxLength={2000} value={current.text} disabled={saving} placeholder="What is happening, and how you feel about it"
        onChange={event => edit({ text: event.target.value })} />
      <label htmlFor={`${id}-state`}>State <small>optional</small></label>
      <NativeSelect id={`${id}-state`} value={current.state} disabled={saving} onChange={event => edit({ state: event.target.value as PlayNoteState | '' })}>
        <option value="">No state</option>
        {playNoteStates.map(value => <option key={value} value={value}>{playNoteStateLabels[value]}</option>)}
      </NativeSelect>
      {saveError && <p className="error" role="alert">{saveError}</p>}
      <Button type="submit" size="sm" variant="outline" disabled={saving || !current.text.trim()}>{saving ? 'Adding…' : 'Add to log'}</Button>
    </form>
  </section>
}
