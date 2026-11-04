import type { PriceOverlay } from '@/components/chart/types'
import type { PlanEntry, PlanExit, PlanRevision, PlayPlan } from '@/api/plays'
import { exactCompare, exactNumber, type Exact } from '@/lib/decimal'
import { exactLevelPrice, leverageOf } from './levels'
import { formatPlanNumber } from './numbers'

/*
 * How a closed Play's stops moved: each entry's stops as first planned beside the stops it ended with. Everything comes
 * from the saved plan revisions; nothing is inferred from fills or orders, and a stop that never had a price is shown as
 * its plan text only.
 */

/** One stop as a revision stated it, with the price it stands for when it has one. */
export interface StopLevel {
  levelId: string
  /** The plan text: a price, or a return on margin at the Play's leverage. */
  unit: PlanExit['unit']
  value: string
  /** The exact price, or null when the level has no price (a percentage without a priced entry). */
  price: Exact | null
}

export interface EntryStopHistory {
  entryId: string
  /** The plan revision whose stops are the original ones: the first revision that holds this entry. */
  fromRevision: number
  original: StopLevel[]
  final: StopLevel[]
  /** Later revisions that changed this entry's stops. */
  moves: number
}

const levelsOf = (entry: PlanEntry, plan: PlayPlan): StopLevel[] => entry.stops.map(stop => ({
  levelId: stop.id, unit: stop.unit, value: stop.value,
  price: exactLevelPrice(entry, stop, 'stop', plan.direction, leverageOf(plan.leverage)),
}))

const samePrice = (a: Exact | null, b: Exact | null) => a !== null && b !== null && exactCompare(a, b) === 0
/** The same stops, in the same order, at the same prices or, without one, the same text. */
const sameLevels = (a: readonly StopLevel[], b: readonly StopLevel[]) => a.length === b.length && a.every((level, index) => {
  const other = b[index]!
  return level.price !== null || other.price !== null ? samePrice(level.price, other.price) : level.unit === other.unit && level.value === other.value
})

/**
 * Each entry of the final plan with its original and final stops and how many later revisions changed them. Revisions are
 * read in number order; an entry added by a later revision starts from that revision. Entries without any revision are left
 * out. The final stops are the final plan's.
 */
export function stopHistory(revisions: readonly PlanRevision[], final: PlayPlan): EntryStopHistory[] {
  const ordered = [...revisions].sort((a, b) => a.number - b.number)
  return final.entries.flatMap(entry => {
    let first: { revision: number; levels: StopLevel[] } | null = null
    let previous: StopLevel[] | null = null
    let moves = 0
    for (const revision of ordered) {
      const planned = revision.plan.entries.find(item => item.id === entry.id)
      if (!planned) continue
      const levels = levelsOf(planned, revision.plan)
      if (first === null) first = { revision: revision.number, levels }
      else if (previous !== null && !sameLevels(previous, levels)) moves += 1
      previous = levels
    }
    if (first === null) return []
    const finalLevels = levelsOf(entry, final)
    // A last change saved after the last listed revision still counts.
    if (previous !== null && !sameLevels(previous, finalLevels)) moves += 1
    return [{ entryId: entry.id, fromRevision: first.revision, original: first.levels, final: finalLevels, moves }]
  })
}

/** True when any entry's stops changed after they were first planned. */
export const stopsMoved = (history: readonly EntryStopHistory[]) => history.some(entry => entry.moves > 0)

/** A stop for display: its price, or its percentage with the price it stands for. */
export function stopText(level: StopLevel) {
  const price = level.price === null ? null : formatPlanNumber(exactNumber(level.price), 12)
  if (level.unit === 'price') return price ?? level.value
  return price === null ? `${level.value} %` : `${level.value} % (${price})`
}

export const originalStopOverlayPrefix = 'original-stop:'

/**
 * Faint, fixed chart lines for original stops whose price the entry no longer has a stop at. `label` names the entry's
 * stop as the chart does ("SL", "E2 SL1"). Only priced stops are drawn.
 */
export function originalStopOverlays(history: readonly EntryStopHistory[], label: (entryId: string, index: number, count: number) => string,
  visible: (entryId: string) => boolean = () => true): PriceOverlay[] {
  return history.flatMap(entry => {
    if (entry.moves === 0 || !visible(entry.entryId)) return []
    return entry.original.flatMap((level, index) => {
      if (level.price === null || entry.final.some(current => samePrice(current.price, level.price))) return []
      return [{
        id: `${originalStopOverlayPrefix}${entry.entryId}:${level.levelId}`, label: `${label(entry.entryId, index, entry.original.length)} orig`,
        price: exactNumber(level.price), color: 'var(--negative)', kind: 'reference' as const, emphasis: 'normal' as const, draggable: false,
        unconfirmed: true,
      }]
    })
  })
}
