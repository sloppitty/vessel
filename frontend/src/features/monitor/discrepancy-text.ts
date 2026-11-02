import type { MonitorDiscrepancy } from '@/api/monitor'
import { decimalDifference, decimalSign } from '@/lib/decimal'

/** Human titles per kind, in the Notification Hub's wording (`NotificationComposer.DiscrepancyText`). */
const titles: Record<string, string> = {
  'protection.missing': 'No stop protects the open position',
  'protection.cancelled': 'The stop was cancelled',
  'protection.short': 'The stop covers less than the position',
  'protection.excess': 'Resting exits exceed the position',
  'protection.beyond-liquidation': "The stop sits beyond the venue's liquidation price",
  'protection.orphaned': 'A stop or target still rests after the Play ended',
  'entry.orphaned': 'An entry order still rests after the Play ended',
  'level.drift': 'A linked order no longer matches its plan level',
  'position.missing': 'The venue shows no position',
  'position.opposite': 'The venue position is on the opposite side',
  'position.short': 'The venue position is smaller than expected',
  'position.excess': 'The venue position is larger than expected',
  'fill.unlinked': 'A fill is not linked to a Play',
}

/** One next step per kind. Vessel never sends orders, so venue changes are the owner's to make. */
const actions: Record<string, string> = {
  'protection.missing': 'Place a stop at the venue for the open quantity, then Check.',
  'protection.cancelled': 'Place a new stop at the venue, then Check.',
  'protection.short': 'Resize the stop at the venue to cover the open quantity, then Check.',
  'protection.excess': 'Reduce the extra exit orders at the venue to the open quantity.',
  'protection.beyond-liquidation': 'Move the stop inside the liquidation price at the venue, or add margin, then Check. Under cross margin the price moves with the account.',
  'protection.orphaned': 'Cancel the leftover stop or target at the venue if you no longer want it.',
  'entry.orphaned': 'Cancel the leftover entry order at the venue if you no longer want it.',
  'level.drift': 'Move the order back to its plan level at the venue, or revise the plan.',
  'position.missing': 'Check the venue: if the position was closed there, link the closing fills.',
  'position.opposite': 'Check the venue position and link the fills that reversed it.',
  'position.short': 'Link the fills that reduced the position, or check it at the venue.',
  'position.excess': 'Link the extra fills to their Play, or reduce the position at the venue.',
  'fill.unlinked': 'Link the fill to its Play from that Play\'s Execution tab.',
}

export const discrepancyTitle = (kind: string) => titles[kind] ?? 'Monitor discrepancy'
/** For a record that could not be verified and is not open: its condition is unknown, so the step is to look. */
export const unverifiableAction = 'Confirm it at the venue, then dismiss it once it is settled.'
export const discrepancyAction = (kind: string) => actions[kind] ?? 'Check the account at the venue.'

export interface DiscrepancyFact { label: string; value: string }

const text = (value: unknown) => typeof value === 'string' && value.length > 0 ? value : null
const unknown = 'Unknown'
const sizedKinds = new Set(['protection.missing', 'protection.cancelled', 'protection.short', 'protection.excess'])
const positionKinds = new Set(['position.missing', 'position.opposite', 'position.short', 'position.excess'])

/**
 * The record's facts as label and exact value. The uncovered quantity is only computed when the stops are sized orders
 * (`stopCoverage: orders`): a whole-position stop has no size of its own, and unknown coverage stays unknown.
 * Missing or malformed values read "Unknown", never 0.
 */
export function discrepancyFacts(record: MonitorDiscrepancy, time: (value: string) => string): DiscrepancyFact[] {
  const details = record.details
  const facts: DiscrepancyFact[] = []
  if (sizedKinds.has(record.kind)) {
    const quantity = text(details.quantity)
    const coverage = details.stopCoverage
    facts.push({ label: 'Open quantity', value: quantity ?? unknown })
    if (coverage === 'orders') {
      const stops = text(details.stopQuantity)
      facts.push({ label: 'Stop quantity', value: stops ?? unknown })
      const uncovered = quantity && stops ? decimalDifference(quantity, stops) : null
      facts.push({ label: 'Uncovered', value: uncovered === null ? unknown : decimalSign(uncovered) < 0 ? '0' : uncovered })
    } else {
      facts.push({ label: 'Stop coverage', value: coverage === 'whole-position' ? 'Whole-position stop' : unknown })
    }
    if (record.kind === 'protection.excess') facts.push({ label: 'Target quantity', value: text(details.targetQuantity) ?? unknown })
  } else if (positionKinds.has(record.kind)) {
    facts.push({ label: 'Expected by open Plays', value: text(details.expectedQuantity) ?? unknown })
    facts.push({ label: 'Venue position', value: text(details.venueQuantity) ?? unknown })
  } else if (record.kind === 'protection.beyond-liquidation') {
    // The venue's own figures (phase 2, 8b); under cross margin the price moves with the account.
    facts.push({ label: 'Venue liquidation', value: text(details.liquidationPrice) ?? unknown })
    facts.push({ label: 'Margin mode', value: text(details.marginMode) ?? unknown })
    const beyond = (Array.isArray(details.stops) ? details.stops : []).filter(isObject).filter(stop => stop.beyond === true)
    facts.push({ label: 'Stops beyond it', value: beyond.length ? beyond.map(stop => text(stop.price) ?? unknown).join(', ') : unknown })
  } else if (record.kind === 'fill.unlinked') {
    facts.push({ label: 'Unlinked fills', value: typeof details.count === 'number' ? String(details.count) : unknown })
    facts.push({ label: 'Total quantity', value: text(details.totalQuantity) ?? unknown })
  }
  facts.push({ label: 'Last observed', value: record.lastObservedAtUtc ? time(record.lastObservedAtUtc) : unknown })
  return facts
}

const isObject = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value)

/** Parses a monitor level key (`stop|entry-1|stop-1`, `entry|entry-1`, `exit`) for naming. */
export function levelKey(key: unknown): { role: 'entry' | 'stop' | 'target' | 'exit'; entryId: string | null; levelId: string | null } | null {
  if (typeof key !== 'string') return null
  const [role, entryId = null, levelId = null] = key.split('|')
  return role === 'entry' || role === 'stop' || role === 'target' || role === 'exit' ? { role, entryId, levelId } : null
}

/**
 * Orders a record lists, as "Order 900000028 · stop · resting". `name` may replace the role with the plan level's
 * name ("Entry 1 stop") where the Play is known. Drift rows also say where the order rests against its plan level.
 */
export function discrepancyOrders(record: MonitorDiscrepancy, name?: (key: unknown) => string | null): string[] {
  const details = record.details
  if (record.kind === 'level.drift') {
    return (Array.isArray(details.levels) ? details.levels : []).filter(isObject).map(level =>
      `Order ${text(level.orderId) ?? unknown} · at ${text(level.actualPrice) ?? unknown}, plan ${text(level.plannedPrice) ?? 'not priced'}`)
  }
  const orders = Array.isArray(details.orders) ? details.orders.filter(isObject) : []
  return orders.map(order => {
    const role = text(order.role)?.toLowerCase() ?? levelKey(order.level)?.role ?? 'order'
    const level = name?.(order.level) ?? role
    const resting = order.resting === true ? 'resting' : order.resting === false ? 'not resting' : 'resting unknown'
    return `Order ${text(order.orderId) ?? unknown} · ${level} · ${resting}${order.wholePosition === true ? ' · whole position' : ''}`
  })
}
