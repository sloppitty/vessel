import type { DraftEntry, DraftExit, PlayDraft } from './draft'
import { levelName } from './execution'
import { exitsOf, formatDraggedPrice, levelPrice, type ExitKind } from './levels'
import { positivePlanNumber as positive, serverPlanNumber, serverPositivePlanNumber as storedPositive } from './numbers'
import { entryFieldKey, levelFieldKey, type StoredPlanText } from './stored-plan-text'

type Direction = PlayDraft['direction']

/** Long targets and short stops sit above the entry. */
const belongsAbove = (kind: ExitKind, direction: Direction) => (kind === 'target') === (direction === 'long')

/**
 * Entries with a linked or manual fill, by ID, as the execution (its per-entry filled quantity, which leaves out fills
 * that contradict the entry) or the manual ledger reports them. The server decides the same per entry.
 */
export type FilledEntries = ReadonlySet<string>
export const noFilledEntries: FilledEntries = new Set<string>()
export const entryFilled = (filled: FilledEntries | undefined, entryId: string) => !!filled?.has(entryId)

/**
 * A stop unchanged from the saved plan (the same text in the same unit, on the same entry price, in the same direction).
 * The server does not check its side again, so a stop saved beyond an entry while it had a fill still saves after that
 * fill is unlinked. Those are everything the side check reads: changing any of them, the direction included, checks again.
 */
export function keptStop(entry: DraftEntry, stop: DraftExit, stored: StoredPlanText | undefined, direction: Direction) {
  const same = (key: string, value: string) => { const text = stored?.get(key); return text !== undefined && (text === value || text === value.trim()) }
  return stored?.get('direction') === direction &&
    same(levelFieldKey('stop', entry.id, stop, 'value'), stop.value) && same(entryFieldKey(entry.id, 'price'), entry.price)
}

/**
 * A 0 % stop: break-even, at the entry by definition. Read as the server's side check reads stored text (`0`, `0.00`);
 * a blank stop is not set, so it is no stop, not break-even.
 */
export const breakEvenStop = (exit: DraftExit, kind: ExitKind) => kind === 'stop' && exit.unit === 'percent' && serverPlanNumber(exit.value) === 0

/**
 * A stop or target in price units on the wrong side of its entry, or at it. Percent levels are always placed on the right
 * side, except a 0 % stop: it sits at the entry, priced or not, so before a fill it is refused like a price stop there
 * (the owner's decision of October 20, 2026). Prices are read as the server's side check reads them, so stored text such
 * as 1e2 counts; pass the entry price read the same way (`serverPositivePlanNumber`). Once its entry has filled, a stop
 * protects a position that exists and may sit at or beyond the entry (break-even or locked-in profit), as the server
 * allows; targets keep their side.
 */
export function wrongSide(entryPrice: number | null, exit: DraftExit, kind: ExitKind, direction: Direction, filled = false) {
  if (kind === 'stop' && filled) return false
  if (breakEvenStop(exit, kind)) return true
  const price = exit.unit === 'price' ? storedPositive(exit.value) : null
  if (entryPrice === null || price === null) return false
  return belongsAbove(kind, direction) ? price <= entryPrice : price >= entryPrice
}

export interface PlanIssue {
  entryId: string
  kind: ExitKind
  levelId: string
  message: string
  /** A 0 % stop before a fill: no swap or direction switch can make it valid. */
  breakEven?: boolean
}

/**
 * Stops and targets that contradict the direction. A plan with issues cannot be saved. A stop kept unchanged from the
 * saved plan is not an issue (see `keptStopWarnings`).
 */
export function planIssues(entries: readonly DraftEntry[], direction: Direction, filled: FilledEntries = noFilledEntries,
  stored?: StoredPlanText): PlanIssue[] {
  return sideIssues(entries, direction, filled, (entry, kind, exit) => !(kind === 'stop' && keptStop(entry, exit, stored, direction)))
}

/**
 * Stops beyond an entry without a fill that are kept unchanged from the saved plan, for instance after the entry's fill
 * was unlinked. They still save, so they are warnings: changing one needs the entry to have a fill again.
 */
export function keptStopWarnings(entries: readonly DraftEntry[], direction: Direction, filled: FilledEntries = noFilledEntries,
  stored?: StoredPlanText): PlanIssue[] {
  return sideIssues(entries, direction, filled, (entry, kind, exit) => kind === 'stop' && keptStop(entry, exit, stored, direction)).map(issue => ({
    ...issue, message: `${issue.message.replace(/; a .*$/, '')}, but its entry has no fill. It was saved there and still saves unchanged.`,
  }))
}

function sideIssues(entries: readonly DraftEntry[], direction: Direction, filled: FilledEntries,
  include: (entry: DraftEntry, kind: ExitKind, exit: DraftExit) => boolean): PlanIssue[] {
  return entries.flatMap(entry => {
    const entryPrice = storedPositive(entry.price)
    return (['stop', 'target'] as const).flatMap(kind => exitsOf(entry, kind)
      .filter(exit => wrongSide(entryPrice, exit, kind, direction, entryFilled(filled, entry.id)) && include(entry, kind, exit))
      .map(exit => {
        const breakEven = breakEvenStop(exit, kind)
        const side = breakEven || storedPositive(exit.value) === entryPrice ? 'at' : belongsAbove(kind, direction) ? 'below' : 'above'
        return {
          entryId: entry.id, kind, levelId: exit.id, ...breakEven ? { breakEven } : {},
          message: `${levelName(entries, { role: kind, entryId: entry.id, levelId: exit.id })} is ${side} the entry price; a ${direction} ${kind} goes ${belongsAbove(kind, direction) ? 'above' : 'below'} it.${breakEven ? ' 0 % (break-even) needs a fill on the entry.' : ''}`,
        }
      }))
  })
}

/**
 * Corrections that clear every issue, when there is one: swapping the stops and targets of the
 * entries with issues, or switching the direction. Null when it would not fix the whole plan.
 */
export function planFixes(draft: Pick<PlayDraft, 'entries' | 'direction'>, filled: FilledEntries = noFilledEntries, stored?: StoredPlanText) {
  const issues = planIssues(draft.entries, draft.direction, filled, stored)
  if (!issues.length) return { swapped: null, reversed: null }
  const affected = new Set(issues.map(issue => issue.entryId))
  const swapped = draft.entries.map(entry => affected.has(entry.id) ? { ...entry, stops: entry.targets, targets: entry.stops } : entry)
  const reversed: Direction = draft.direction === 'long' ? 'short' : 'long'
  return {
    // Swapping would only move a 0 % stop among the targets, where it is not checked: it is not offered for one.
    swapped: issues.some(issue => issue.breakEven) || planIssues(swapped, draft.direction, filled, stored).length ? null : swapped,
    reversed: planIssues(draft.entries, reversed, filled, stored).length ? null : reversed,
  }
}

/** One line for the save error. */
export function planIssueSummary(issues: readonly PlanIssue[]) {
  return issues.length === 1 ? issues[0]!.message : `${issues.length} stops and targets are on the wrong side of their entry.`
}

/**
 * Stops that would not trigger before the estimated liquidation price: at or below it for a long,
 * at or above it for a short. A warning, not an error, since the liquidation price is an estimate.
 */
export function stopsPastLiquidation(entries: readonly DraftEntry[], direction: Direction, leverage: number, liquidation: number | null) {
  if (liquidation === null) return []
  return entries.flatMap(entry => entry.stops.flatMap(stop => {
    const price = levelPrice(positive(entry.price), stop, 'stop', direction, leverage)
    if (price === null || (direction === 'long' ? price > liquidation : price < liquidation)) return []
    return [{ entryId: entry.id, levelId: stop.id,
      message: `${levelName(entries, { role: 'stop', entryId: entry.id, levelId: stop.id })} at ${formatDraggedPrice(price)} is past the estimated liquidation price of ${formatDraggedPrice(liquidation)}.` }]
  }))
}
