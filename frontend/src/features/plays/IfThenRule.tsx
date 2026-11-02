import { useState } from 'react'
import { maxRulePart, type NextAction, type PlayRule, type PlayRuleState } from '@/api/plays'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'

/** The page's hold on the mode's one if-then rule (Phase 2, item 3). */
export interface RuleControls {
  /** The mode's rule, or null while it loads or after a failed read. */
  state: PlayRuleState | null
  onWrite: () => void
  onPromote: (action: NextAction) => void
  onArchive: (rule: PlayRule) => void
  /** Opens the Play a promoted rule came from (Sol round 1 on item 3). */
  onOpenSource?: (playId: string) => void
}

/** The rule in the Thesis tab: what it says, and how to change or archive it. Nothing shows until it is read. */
export function IfThenRuleLine({ controls }: { controls: RuleControls }) {
  if (!controls.state) return null
  const active = controls.state.active
  return <div className="if-then-rule" role="group" aria-label="If-then rule">
    {active ? <p><strong>Your if-then rule</strong> · version {active.version}<br />If {active.if}, then {active.then}
      {active.sourcePlayId && active.sourceTitle ? <small> · from {controls.onOpenSource
        ? <button type="button" className="text-action" onClick={() => controls.onOpenSource!(active.sourcePlayId!)}>{active.sourceTitle}</button>
        : active.sourceTitle}</small> : null}</p>
      : <p className="muted">No if-then rule yet. Promote one of your next actions below, or write one.</p>}
    <span className="if-then-rule-actions">
      <Button type="button" size="sm" variant="outline" onClick={controls.onWrite}>{active ? 'Change the rule' : 'Write a rule'}</Button>
      {active && <Button type="button" size="sm" variant="ghost" onClick={() => controls.onArchive(active)}>Archive the rule</Button>}
    </span>
  </div>
}

/** An editor's text, its source, and the active version it was opened on, which its save names. */
export interface RuleDraft { if: string; then: string; source: { playId: string; title: string } | null; expectedVersion: number | null }

/**
 * Writes or changes the rule; a promoted next action fills its Then and names its Play. Plays already planned keep theirs.
 * After a conflict the editor saves nothing until the owner loads the current rule, so another version is never replaced
 * unseen (Sol round 1 on item 3).
 */
export function RuleDialog({ active, draft, pending, error, onReload, onSave, onClose }: {
  active: PlayRule | null; draft: RuleDraft; pending: boolean; error: string | null
  /** Offered after a conflict once a newer rule than the editor's has been read; until it is used, nothing saves. */
  onReload?: (() => void) | undefined
  onSave: (rule: { if: string; then: string }) => void; onClose: () => void
}) {
  const conflict = !!onReload
  const [ifText, setIf] = useState(draft.if)
  const [thenText, setThen] = useState(draft.then)
  const valid = ifText.trim() !== '' && thenText.trim() !== ''
  return <Dialog open onOpenChange={next => { if (!next && !pending) onClose() }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>{active ? 'Change your if-then rule' : 'Your if-then rule'}</DialogTitle><DialogDescription>
        One rule at a time: Plan it shows it, and the review asks whether you followed it.{active ? ' Plays planned under the current version keep it.' : ''}
      </DialogDescription></DialogHeader>
      <div className="workspace-form">
        {draft.source && <p className="muted">From the next action of {draft.source.title || 'an earlier Play'}.</p>}
        <label htmlFor="rule-if">If</label>
        <textarea id="rule-if" value={ifText} maxLength={maxRulePart} rows={2} disabled={pending} onChange={event => setIf(event.target.value)} />
        <label htmlFor="rule-then">Then</label>
        <textarea id="rule-then" value={thenText} maxLength={maxRulePart} rows={2} disabled={pending} onChange={event => setThen(event.target.value)} />
        {error && <p className="error" role="alert">{error}</p>}
        {conflict && <div className="workspace-form"><p className="field-help">The rule changed since you opened this editor. Load the current
          rule to review it; your text here is not saved.</p>
          <Button type="button" variant="outline" disabled={pending} onClick={onReload}>Load the current rule</Button></div>}
        <div className="management-actions"><Button type="button" variant="outline" onClick={onClose} disabled={pending}>Cancel</Button>
          <Button type="button" className="play-primary" disabled={!valid || pending || conflict} onClick={() => onSave({ if: ifText.trim(), then: thenText.trim() })}>
            {pending ? 'Saving…' : 'Save the rule'}</Button></div>
      </div>
    </DialogContent>
  </Dialog>
}
