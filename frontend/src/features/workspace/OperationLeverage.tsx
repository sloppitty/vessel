import type { ActivityOperation } from '@/api/activity'
import './activity-operations.css'

/**
 * Venue-reported position leverage observed during an operation: `10×`, or a range when it changed. Unknown is shown
 * as unknown, never inferred from margin, notional or a plan.
 */
export function OperationLeverage({ operation }: { operation: Pick<ActivityOperation, 'leverage' | 'leverageMin' | 'leverageMax'> }) {
  const { leverage, leverageMin, leverageMax } = operation
  if (leverage === null || leverageMin === null || leverageMax === null) {
    return <span className="operation-leverage unknown" title="Leverage unknown: the venue reported none for this position while Vessel observed it. It is never inferred.">
      <span aria-hidden="true">—×</span><span className="sr-only">Leverage unknown</span>
    </span>
  }
  const changed = leverageMin !== leverageMax
  return <span className="operation-leverage" data-changed={changed || undefined}
    title={changed ? `Venue-reported leverage changed between ${leverageMin}× and ${leverageMax}× during this operation; latest ${leverage}×.` : `Venue-reported leverage: ${leverage}×.`}>
    <span className="sr-only">Leverage </span>{changed ? `${leverageMin}–${leverageMax}×` : `${leverage}×`}{changed && <span className="sr-only">, latest {leverage}×</span>}
  </span>
}
