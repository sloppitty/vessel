import { createContext, useContext } from 'react'
import type { PlayPlan } from '@/api/plays'
import type { DraftEntry, DraftExit, PlayDraft } from './draft'
import { keptPlanTextTitle, planTextState, type PlanTextState } from './numbers'

type ExitKind = 'stop' | 'target'

/**
 * The saved text of each plan number field, keyed as the server matches unchanged text (`PlayDocuments.Normalize`):
 * the size and budget, an entry's price or share by entry ID, and a stop's or target's value (in its unit) or share
 * by entry and level ID. Text found here unchanged still saves, even where it predates the plain-decimal rule.
 */
export type StoredPlanText = ReadonlyMap<string, string>

export const entryFieldKey = (entryId: string, field: 'price' | 'share') => `entry ${entryId} ${field}`
export const levelFieldKey = (kind: ExitKind, entryId: string, exit: Pick<DraftExit, 'id' | 'unit'>, field: 'value' | 'share') =>
  field === 'value' ? `${kind} ${entryId} ${exit.id} ${exit.unit} value` : `${kind} ${entryId} ${exit.id} share`

export function storedPlanText(plan: PlayPlan | null | undefined): StoredPlanText {
  const text = new Map<string, string>()
  if (!plan) return text
  // The direction decides which side of an entry a stop must sit on, so an unchanged stop is kept only in it.
  text.set('direction', plan.direction)
  text.set('size', plan.size)
  if (plan.budgetOverride !== null) text.set('budget', plan.budgetOverride)
  for (const entry of plan.entries) {
    text.set(entryFieldKey(entry.id, 'share'), entry.share)
    text.set(entryFieldKey(entry.id, 'price'), entry.price)
    for (const [kind, exits] of [['stop', entry.stops], ['target', entry.targets]] as const) for (const exit of exits) {
      text.set(levelFieldKey(kind, entry.id, exit, 'value'), exit.value)
      text.set(levelFieldKey(kind, entry.id, exit, 'share'), exit.share)
    }
  }
  return text
}

/** The saved Play's field text for the editor; a new draft has none, so every field must be plan text. */
export const StoredPlanTextContext = createContext<StoredPlanText>(new Map())
export const useStoredPlanText = () => useContext(StoredPlanTextContext)

/** Field names as the server words them, e.g. "Entry 1 stop 2 share". */
const exitLabel = (entry: DraftEntry, kind: ExitKind, index: number) => `${entry.name} ${kind} ${index + 1}`

export const sizeState = (draft: Pick<PlayDraft, 'size'>, stored: StoredPlanText) => planTextState(draft.size, 'Size', stored.get('size'))
export const entryFieldState = (entry: DraftEntry, field: 'price' | 'share', stored: StoredPlanText) =>
  planTextState(entry[field], `${entry.name} ${field}`, stored.get(entryFieldKey(entry.id, field)))
export function exitFieldState(entry: DraftEntry, kind: ExitKind, index: number, exit: DraftExit, field: 'value' | 'share', stored: StoredPlanText) {
  const label = exitLabel(entry, kind, index)
  return field === 'value'
    ? planTextState(exit.value, label, stored.get(levelFieldKey(kind, entry.id, exit, 'value')), exit.unit === 'percent' ? kind : undefined)
    : planTextState(exit.share, `${label} share`, stored.get(levelFieldKey(kind, entry.id, exit, 'share')))
}

/** Input attributes for a field's state: text that cannot be saved is an error with its reason, kept text a warning. */
export function planTextMarks(state: PlanTextState, value: string) {
  return state.status === 'invalid' ? { invalid: true, warning: false, title: state.reason }
    : state.status === 'kept' ? { invalid: false, warning: true, title: keptPlanTextTitle(value) } : null
}

/** Plan numbers that cannot be saved, in the order the server checks them. The server refuses the same with 400. */
export function planNumberIssues(draft: Pick<PlayDraft, 'size' | 'budgetOverride' | 'entries'>, stored: StoredPlanText): string[] {
  const states: PlanTextState[] = draft.entries.flatMap(entry => [
    entryFieldState(entry, 'share', stored), entryFieldState(entry, 'price', stored),
    ...(['stop', 'target'] as const).flatMap(kind => (kind === 'stop' ? entry.stops : entry.targets).flatMap((exit, index) => [
      exitFieldState(entry, kind, index, exit, 'value', stored), exitFieldState(entry, kind, index, exit, 'share', stored)])),
  ])
  states.push(sizeState(draft, stored))
  if (draft.budgetOverride !== null) states.push(planTextState(draft.budgetOverride, 'Budget', stored.get('budget')))
  return states.flatMap(state => state.status === 'invalid' ? [state.reason] : [])
}

/** One line for the blocked save. */
export const planNumberSummary = (issues: readonly string[]) =>
  issues.length === 1 ? issues[0]! : `${issues.length} plan numbers cannot be saved. ${issues[0]}`

/**
 * The plan as sent: each number field trimmed of surrounding whitespace, except stored text sent back unchanged, so
 * an earlier plan is never rewritten by a save that did not touch it.
 */
export function trimPlanNumbers(plan: PlayPlan, stored: StoredPlanText): PlayPlan {
  const trim = (key: string, value: string) => stored.get(key) === value ? value : value.trim()
  return {
    ...plan, size: trim('size', plan.size), budgetOverride: plan.budgetOverride === null ? null : trim('budget', plan.budgetOverride),
    entries: plan.entries.map(entry => ({
      ...entry, share: trim(entryFieldKey(entry.id, 'share'), entry.share), price: trim(entryFieldKey(entry.id, 'price'), entry.price),
      stops: entry.stops.map(exit => trimExit('stop', entry.id, exit, trim)),
      targets: entry.targets.map(exit => trimExit('target', entry.id, exit, trim)),
    })),
  }
}

function trimExit<T extends DraftExit>(kind: ExitKind, entryId: string, exit: T, trim: (key: string, value: string) => string): T {
  return { ...exit, value: trim(levelFieldKey(kind, entryId, exit, 'value'), exit.value), share: trim(levelFieldKey(kind, entryId, exit, 'share'), exit.share) }
}
