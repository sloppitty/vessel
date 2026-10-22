import { errorMessage } from '@/api/errors'
import { useEffect, useMemo, useState, type FormEvent } from 'react'
import type { WorkspaceApi } from '@/api/workspace'
import { cancelReasonLabels, cancelReasons, statusLabels, unresolvedReasonLabels, unresolvedReasons, type CancelReason, type PlayHistory, type PreflightCheck, type UnresolvedReason, maxPreflightNote, type PlanRuleAnswer, type PlanRuleSnapshot, type PlayRule } from '@/api/plays'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { describePlanChanges } from './saved'
import { preflightLabels } from './preflight'
import { evidenceLines } from './unresolved'
import { NativeSelect } from '@/components/ui/native-select'
import { Time } from '@/components/ui/time'

/** Asks why a planned Play's plan changed; the reason is kept with the revision. */
export function RevisionDialog({ revision, pending, error, onSave, onClose }: {
  revision: number; pending: boolean; error: string | null; onSave: (reason: string) => void; onClose: () => void
}) {
  const [reason, setReason] = useState('')
  const submit = (event: FormEvent) => { event.preventDefault(); if (reason.trim()) onSave(reason.trim()) }
  return <Dialog open onOpenChange={next => { if (!next && !pending) onClose() }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>Why did the plan change?</DialogTitle><DialogDescription className="sr-only">
        Saves revision {revision + 1}; the earlier plan stays in the history.
      </DialogDescription></DialogHeader>
      <form className="workspace-form" onSubmit={submit} aria-busy={pending}>
        <label htmlFor="revision-reason">Reason</label>
        <textarea id="revision-reason" value={reason} maxLength={2000} autoFocus disabled={pending} rows={3}
          placeholder="e.g. Moved the stop under the new swing low" onChange={event => setReason(event.target.value)} />
        {error && <p className="error" role="alert">{error}</p>}
        <div className="management-actions"><Button type="button" variant="outline" onClick={onClose} disabled={pending}>Keep editing</Button>
          <Button type="submit" disabled={pending || !reason.trim()}>{pending ? 'Saving…' : 'Save revision'}</Button></div>
      </form>
    </DialogContent>
  </Dialog>
}

/**
 * Declare an Open Play unresolved (phase 1, 1.3a): the owner says its executions can never settle it. Never offered from
 * account flatness or the monitor; the server keeps a snapshot of its execution facts with the declaration.
 */
export function UnresolvedDialog({ pending, error, onDeclare, onClose }: {
  pending: boolean; error: string | null; onDeclare: (reason: UnresolvedReason, note: string) => void; onClose: () => void
}) {
  const [reason, setReason] = useState<UnresolvedReason | ''>('')
  const [note, setNote] = useState('')
  const submit = (event: FormEvent) => { event.preventDefault(); if (reason && note.trim()) onDeclare(reason, note.trim()) }
  return <Dialog open onOpenChange={next => { if (!next && !pending) onClose() }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>Declare this play unresolved?</DialogTitle><DialogDescription>
        Use this when its venue executions can never settle it. It ends with no result and is left out of Review scores and
        sizing. Its linked orders still count: if their fills settle it, it closes. It takes no new orders unless you return
        it to Open.
      </DialogDescription></DialogHeader>
      <form className="workspace-form" onSubmit={submit} aria-busy={pending}>
        <label htmlFor="unresolved-reason">Reason</label>
        <NativeSelect id="unresolved-reason" value={reason} disabled={pending} onChange={event => setReason(event.target.value as UnresolvedReason | '')}>
          <option value="">Choose a reason</option>
          {unresolvedReasons.map(value => <option key={value} value={value}>{unresolvedReasonLabels[value]}</option>)}
        </NativeSelect>
        <label htmlFor="unresolved-note">What happened</label>
        <textarea id="unresolved-note" value={note} maxLength={2000} rows={3} disabled={pending} onChange={event => setNote(event.target.value)} />
        {error && <p className="error" role="alert">{error}</p>}
        <div className="management-actions"><Button type="button" variant="outline" onClick={onClose} disabled={pending}>Keep it open</Button>
          <Button type="submit" disabled={pending || !reason || !note.trim()}>{pending ? 'Declaring…' : 'Declare unresolved'}</Button></div>
      </form>
    </DialogContent>
  </Dialog>
}

/** Return an Unresolved Play to Open, with a reason; its review is needed again once it ends. */
export function ReturnToOpenDialog({ pending, error, onReturn, onClose }: {
  pending: boolean; error: string | null; onReturn: (note: string) => void; onClose: () => void
}) {
  const [note, setNote] = useState('')
  const submit = (event: FormEvent) => { event.preventDefault(); if (note.trim()) onReturn(note.trim()) }
  return <Dialog open onOpenChange={next => { if (!next && !pending) onClose() }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>Return this play to Open?</DialogTitle><DialogDescription>
        It can link orders again and its fills settle it as usual. The declaration stays in its history, and its review is
        needed again once it ends.
      </DialogDescription></DialogHeader>
      <form className="workspace-form" onSubmit={submit} aria-busy={pending}>
        <label htmlFor="return-open-note">Reason</label>
        <textarea id="return-open-note" value={note} maxLength={2000} rows={3} disabled={pending} onChange={event => setNote(event.target.value)} />
        {error && <p className="error" role="alert">{error}</p>}
        <div className="management-actions"><Button type="button" variant="outline" onClick={onClose} disabled={pending}>Keep it unresolved</Button>
          <Button type="submit" disabled={pending || !note.trim()}>{pending ? 'Returning…' : 'Return to Open'}</Button></div>
      </form>
    </DialogContent>
  </Dialog>
}

const paperStopDisclosure = 'Unevaluated time before this action is skipped, not replayed. A retained receipt keeps this Play unscored, even after resuming. Recorded executions and virtual cash are kept.'

export function PauseDialog({ pending, error, onPause, onClose }: {
  pending: boolean; error: string | null; onPause: () => void; onClose: () => void
}) {
  return <Dialog open onOpenChange={next => { if (!next && !pending) onClose() }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>Pause paper play?</DialogTitle><DialogDescription>{paperStopDisclosure}</DialogDescription></DialogHeader>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="management-actions"><Button type="button" variant="outline" onClick={onClose} disabled={pending}>Keep running</Button>
        <Button type="button" onClick={onPause} disabled={pending}>{pending ? 'Pausing…' : 'Pause paper play'}</Button></div>
    </DialogContent>
  </Dialog>
}

export function CancelDialog({ pending, error, onCancel, onClose, paper = false }: {
  pending: boolean; error: string | null; onCancel: (reason: CancelReason, note: string) => void; onClose: () => void; paper?: boolean
}) {
  const [reason, setReason] = useState<CancelReason | ''>('')
  const [note, setNote] = useState('')
  const submit = (event: FormEvent) => { event.preventDefault(); if (reason) onCancel(reason, note.trim()) }
  return <Dialog open onOpenChange={next => { if (!next && !pending) onClose() }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>Cancel this play?</DialogTitle><DialogDescription>
        {paper ? paperStopDisclosure : 'Its plan and history are kept. Remove resting venue orders yourself.'}
      </DialogDescription></DialogHeader>
      <form className="workspace-form" onSubmit={submit} aria-busy={pending}>
        <label htmlFor="cancel-reason">Reason</label>
        <NativeSelect id="cancel-reason" value={reason} disabled={pending} onChange={event => setReason(event.target.value as CancelReason | '')}>
          <option value="">Choose a reason</option>
          {cancelReasons.map(value => <option key={value} value={value}>{cancelReasonLabels[value]}</option>)}
        </NativeSelect>
        <label htmlFor="cancel-note">Note <small>optional</small></label>
        <textarea id="cancel-note" value={note} maxLength={2000} rows={3} disabled={pending} onChange={event => setNote(event.target.value)} />
        {error && <p className="error" role="alert">{error}</p>}
        <div className="management-actions"><Button type="button" variant="outline" onClick={onClose} disabled={pending}>Keep the play</Button>
          <Button type="submit" variant="destructive" disabled={pending || !reason}>{pending ? 'Cancelling…' : 'Cancel play'}</Button></div>
      </form>
    </DialogContent>
  </Dialog>
}

const outcomeLabels = { passed: 'Met', skipped: 'Not met', unknown: 'Unknown' } as const

/** The pre-flight kept with revision 1, one line per check, or a line saying a plan from before it has none. */
const preflightLines = (checks: readonly PreflightCheck[] | null | undefined) =>
  [checks?.length ? `Pre-flight: ${checks.map(check => `${preflightLabels[check.code].toLowerCase()} · ${outcomeLabels[check.outcome].toLowerCase()}`).join('; ')}.`
    : 'Pre-flight not recorded.',
  // Phase 2, item 10b: the owner's cooldown note at a reached loss limit, as written.
  ...(checks?.flatMap(check => check.note ? [`Cooldown note: ${check.note}`] : []) ?? [])]

/** The if-then rule seen at Plan it and the owner's answer, on revision 1 (Phase 2, item 3). */
const ruleLines = (rule: PlanRuleSnapshot | null | undefined) => rule ? [`If-then rule, version ${rule.version}: if ${rule.if}, then ${rule.then} · ${
  rule.answer === 'acknowledged' ? 'acknowledged' : rule.answer === 'exception' ? `exception: ${rule.reason}` : 'not answered'}.`] : []

/**
 * The pre-flight at Plan it (phase 1, 1.5): what the plan already says, recorded with revision 1. It never blocks:
 * Plan it anyway is always available, and so is going back to the plan.
 */
export function PreflightDialog({ checks, pending, error, onPlan, onClose, lossLimit = null, rule = null }: {
  checks: readonly PreflightCheck[]; pending: boolean; error: string | null
  onPlan: (note: string | null, rule: PlanRuleAnswer | null) => void; onClose: () => void
  /** A reached soft loss limit (Phase 2, item 10b): the owner may add a cooldown note, kept with revision 1. */
  lossLimit?: string | null
  /** The mode's if-then rule (Phase 2, item 3): acknowledged, or an exception with a reason, kept with revision 1. */
  rule?: PlayRule | null
}) {
  const [note, setNote] = useState('')
  const [given, setAnswer] = useState<{ version: number; answer: PlanRuleAnswer['answer'] } | null>(null)
  const [reason, setReason] = useState('')
  // An answer belongs to the version it was given for: a rule changed while the dialog is open is asked again. An
  // exception needs its reason; no answer at all never blocks planning.
  const answer = rule && given?.version === rule.version ? given.answer : null
  const ruleAnswer: PlanRuleAnswer | null = rule && answer === 'acknowledged' ? { version: rule.version, answer }
    : rule && answer === 'exception' && reason.trim() ? { version: rule.version, answer, reason: reason.trim() } : null
  const awaitingReason = answer === 'exception' && !reason.trim()
  const reached = checks.some(check => check.code === 'loss-limit' && check.outcome === 'skipped')
  return <Dialog open onOpenChange={next => { if (!next && !pending) onClose() }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>Before planning</DialogTitle><DialogDescription>
        What this plan already says. You can plan it as it is; the checklist is kept with the plan.
      </DialogDescription></DialogHeader>
      <ul className="preflight-checks" aria-label="Pre-flight checks">{checks.map(check => <li key={check.code} data-outcome={check.outcome}>
        <span>{preflightLabels[check.code]}</span><strong>{outcomeLabels[check.outcome]}</strong></li>)}</ul>
      {rule && <fieldset className="workspace-form preflight-rule">
        <legend>Your if-then rule</legend>
        <p>If {rule.if}, then {rule.then}</p>
        <label><input type="radio" name="preflight-rule" checked={answer === 'acknowledged'} disabled={pending} onChange={() => setAnswer({ version: rule.version, answer: 'acknowledged' })} />
          Acknowledged</label>
        <label><input type="radio" name="preflight-rule" checked={answer === 'exception'} disabled={pending} onChange={() => setAnswer({ version: rule.version, answer: 'exception' })} />
          An exception this time</label>
        {answer === 'exception' && <><label htmlFor="preflight-rule-reason">Why this is an exception</label>
          <textarea id="preflight-rule-reason" value={reason} maxLength={500} rows={2} disabled={pending} onChange={event => setReason(event.target.value)} /></>}
      </fieldset>}
      {reached && <div className="workspace-form">
        {lossLimit && <p className="preflight-loss" role="note">{lossLimit}</p>}
        <label htmlFor="preflight-cooldown">Cooldown note (optional)</label>
        <textarea id="preflight-cooldown" value={note} maxLength={maxPreflightNote} rows={3} disabled={pending} onChange={event => setNote(event.target.value)} />
      </div>}
      {error && <p className="error" role="alert">{error}</p>}
      <div className="management-actions"><Button type="button" variant="outline" onClick={onClose} disabled={pending}>Back to the plan</Button>
        <Button type="button" className="play-primary" onClick={() => onPlan(reached && note.trim() ? note.trim() : null, ruleAnswer)}
          disabled={pending || awaitingReason}>
          {pending ? 'Planning…' : 'Plan it anyway'}</Button></div>
    </DialogContent>
  </Dialog>
}

export function DeleteDialog({ pending, error, onDelete, onClose }: {
  pending: boolean; error: string | null; onDelete: () => void; onClose: () => void
}) {
  return <Dialog open onOpenChange={next => { if (!next && !pending) onClose() }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>Delete this draft?</DialogTitle><DialogDescription>
        The draft, its drawings and its saved images are removed.
      </DialogDescription></DialogHeader>
      <div className="workspace-form">
        {error && <p className="error" role="alert">{error}</p>}
        <div className="management-actions"><Button variant="outline" onClick={onClose} disabled={pending}>Keep draft</Button>
          <Button variant="destructive" onClick={onDelete} disabled={pending}>{pending ? 'Deleting…' : 'Delete draft'}</Button></div>
      </div>
    </DialogContent>
  </Dialog>
}

const reviewActionLabels = { completed: 'review completed', reopened: 'reviewed again', 'not-needed': 'no review needed',
  cleared: 'completion cleared: the Play left its final state' } as const


type HistoryItem = { at: string; key: string; title: string; detail: string[] }

/** Plan revisions with what changed, and status changes, newest first. */
export function HistoryDialog({ api, playId, onClose }: { api: WorkspaceApi; playId: string; onClose: () => void }) {
  const key = useMemo(() => ({ api, playId }), [api, playId])
  const [loaded, setLoaded] = useState<{ key: typeof key; result: { history: PlayHistory } | { error: string } } | null>(null)
  const result = loaded?.key === key ? loaded.result : null
  useEffect(() => {
    const controller = new AbortController()
    api.playHistory(playId, controller.signal).then(history => { if (!controller.signal.aborted) setLoaded({ key, result: { history } }) })
      .catch(cause => { if (!controller.signal.aborted) setLoaded({ key, result: { error: errorMessage(cause, 'The change could not be completed. Try again.') } }) })
    return () => controller.abort()
  }, [api, playId, key])
  const items: HistoryItem[] = result && 'history' in result ? [
    ...result.history.revisions.map((revision, index, all): HistoryItem => ({
      at: revision.createdAtUtc, key: `r${revision.number}`, title: `Plan revision ${revision.number} · ${revision.reason}`,
      detail: index === 0 ? ['The plan as first committed.', ...(revision.number === 1 ? [...preflightLines(revision.preflight), ...ruleLines(revision.rule)] : [])] : describePlanChanges(all[index - 1]!.plan, revision.plan),
    })),
    ...result.history.statusChanges.map((change, index): HistoryItem => ({
      at: change.occurredAtUtc, key: `s${index}`, title: `${statusLabels[change.from]} → ${statusLabels[change.to]}${change.source === 'paper' ? ' · from simulated paper executions' : change.source === 'venue' ? ' · from linked venue fills' : change.source === 'manual' ? ' · from manual facts' : ''}`,
      detail: [change.reason ? cancelReasonLabels[change.reason] : '', change.unresolvedReason ? unresolvedReasonLabels[change.unresolvedReason] : '',
        change.note ?? '', ...(change.evidence ? evidenceLines(change.evidence) : [])].filter(Boolean),
    })),
    ...(result.history.reviews ?? []).map(review => ({
      at: review.occurredAtUtc, key: `review${review.number}`,
      title: `Review revision ${review.number}${review.action && review.action !== 'edited' ? ` · ${reviewActionLabels[review.action]}` : ''}`,
      // Review again, No review needed and a cleared completion change only the completion; the text they keep is already listed.
      detail: review.action === 'reopened' || review.action === 'not-needed' || review.action === 'cleared' ? [] : [review.review, review.structuredReview.adherence ? `Adherence: ${review.structuredReview.adherence}` : '',
        review.structuredReview.rule ? `Followed the rule: ${review.structuredReview.rule}` : '',
        review.structuredReview.mistakeTag ? `Mistake: ${review.structuredReview.mistakeTag}` : '',
        review.structuredReview.lesson ? `Lesson: ${review.structuredReview.lesson}` : '',
        review.structuredReview.nextAction ? `Next action: ${review.structuredReview.nextAction}` : ''].filter(Boolean),
    })),
  ].sort((a, b) => b.at.localeCompare(a.at) || b.key.localeCompare(a.key)) : []
  return <Dialog open onOpenChange={next => { if (!next) onClose() }}>
    <DialogContent className="workspace-dialog play-history-dialog">
      <DialogHeader><DialogTitle>Play history</DialogTitle><DialogDescription className="sr-only">
        Saved plan revisions, status changes and review revisions.
      </DialogDescription></DialogHeader>
      {result === null && <p role="status">Loading history…</p>}
      {result && 'error' in result && <p className="error" role="alert">{result.error}</p>}
      {result && 'history' in result && (items.length ? <ol className="play-history">
        {items.map(item => <li key={item.key}><Time at={item.at} /><strong>{item.title}</strong>
          {item.detail.length > 0 && <ul>{item.detail.map((line, index) => <li key={index}>{line}</li>)}</ul>}</li>)}
      </ol> : <p className="muted">Nothing yet. History starts when the play is planned.</p>)}
    </DialogContent>
  </Dialog>
}
