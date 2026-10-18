import type { ActivityOperation } from '@/api/activity'
import './activity-operations.css'

/**
 * Venue-reported position leverage observed during an operation: `10×`, or a range when it changed. Unknown is shown
 * as unknown, never inferred from margin, notional or a plan. Conflicting reports at the latest time keep the range
 * and leave the latest unknown.
 */
export function OperationLeverage({ operation }: { operation: Pick<ActivityOperation, 'leverage' | 'leverageMin' | 'leverageMax'> }) {
  const { leverage, leverageMin, leverageMax } = operation
  if (leverageMin === null || leverageMax === null) {
    return <span className="operation-leverage unknown" title="Leverage unknown: the venue reported none for this position while Vessel observed it. It is never inferred.">
      <span aria-hidden="true">—×</span><span className="sr-only">Leverage unknown</span>
    </span>
  }
  const changed = leverageMin !== leverageMax
  const latest = leverage === null ? 'latest unknown' : `latest ${leverage}×`
  const title = changed
    ? `Venue-reported leverage changed between ${leverageMin}× and ${leverageMax}× during this operation; ${latest}${leverage === null ? ': the latest observation reported conflicting values' : ''}.`
    : `Venue-reported leverage: ${leverageMin}×.`
  return <span className="operation-leverage" data-changed={changed || undefined} title={title}>
    <span className="sr-only">Leverage </span>{changed ? `${leverageMin}–${leverageMax}×` : `${leverageMin}×`}
    {changed && <span className="sr-only">, {latest}</span>}
  </span>
}
