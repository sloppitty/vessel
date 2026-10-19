import { ArrowDownUp, CircleAlert, Repeat, TriangleAlert } from 'lucide-react'
import { Button } from '@/components/ui/button'
import type { PlayDraft } from './draft'
import { noFilledEntries, planFixes, planIssues, stopsPastLiquidation, type FilledEntries } from './planChecks'
import { leverageOf } from './levels'
import { estimatedLiquidation } from './sizing'

/** Problems in the plan, with the corrections that clear them. Shown between the play fields and the workspace. */
export function PlanNotices({ draft, onChange, readOnly = false, lockDirection = false, maxLeverage = null, maintenanceMargin = null,
  filledEntries = noFilledEntries }: {
  draft: PlayDraft
  onChange: (draft: PlayDraft) => void
  readOnly?: boolean
  /** A play with entry fills keeps its direction, so switching it is not offered. */
  lockDirection?: boolean
  /** Venue maximum for the contract, for the liquidation estimate. */
  maxLeverage?: number | null
  /** Maintenance margin fraction the venue states for the contract, or null. */
  maintenanceMargin?: number | null
  /** Entries with a linked or manual fill: their stops may sit at or beyond the entry. */
  filledEntries?: FilledEntries
}) {
  const issues = planIssues(draft.entries, draft.direction, filledEntries)
  const leverage = leverageOf(draft.leverage)
  // Wrong-side stops are already errors; the liquidation warning only applies to a consistent plan.
  const pastLiquidation = issues.length ? [] : stopsPastLiquidation(draft.entries, draft.direction, leverage,
    estimatedLiquidation(draft.entries, draft.direction, leverage, maxLeverage, maintenanceMargin))
  if (!issues.length && !pastLiquidation.length) return null
  if (!issues.length) return <section className="plays-notifications" aria-label="Plan notifications">
    <div className="plays-notification is-warning" role="status">
      <TriangleAlert size={15} aria-hidden="true" />
      <div className="plays-notification-body">
        <strong>{pastLiquidation.length === 1 ? 'A stop' : 'Stops'} would not trigger before liquidation</strong>
        <ul>{pastLiquidation.map(item => <li key={item.levelId}>{item.message}</li>)}</ul>
      </div>
    </div>
  </section>
  const fixes = planFixes(draft, filledEntries)
  const swapped = fixes.swapped
  const reversed = lockDirection ? null : fixes.reversed
  const other = draft.direction === 'long' ? 'Short' : 'Long'
  return <section className="plays-notifications" aria-label="Plan notifications">
    <div className="plays-notification is-error" role="alert">
      <CircleAlert size={15} aria-hidden="true" />
      <div className="plays-notification-body">
        <strong>The stops and targets do not fit a {draft.direction}{readOnly ? '' : '. Fix them before saving.'}</strong>
        <ul>{issues.map(issue => <li key={issue.levelId}>{issue.message}</li>)}</ul>
      </div>
      {!readOnly && (swapped || reversed) && <div className="plays-notification-actions">
        {swapped && <Button type="button" size="sm" variant="outline" onClick={() => onChange({ ...draft, entries: swapped })}>
          <ArrowDownUp size={13} aria-hidden="true" />Swap stops and targets</Button>}
        {reversed && <Button type="button" size="sm" variant="outline" onClick={() => onChange({ ...draft, direction: reversed })}>
          <Repeat size={13} aria-hidden="true" />Switch to {other}</Button>}
      </div>}
    </div>
  </section>
}
