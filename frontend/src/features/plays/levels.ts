import type { PriceOverlay } from '@/components/chart/types'
import { createExit, type DraftEntry, type DraftExit, type PlayDraft } from './draft'
import { finitePlanNumber as unsigned, positiveFinitePlanNumber as positive } from './numbers'

export type ChartView = 'aggregate' | 'selected'
export type ExitKind = 'stop' | 'target'
export type LevelRef = { entryId: string; kind: 'entry' } | { entryId: string; kind: ExitKind; levelId: string }

const separator = '|'
export const overlayId = (ref: LevelRef) => ref.kind === 'entry'
  ? [ref.entryId, ref.kind].join(separator) : [ref.entryId, ref.kind, ref.levelId].join(separator)

export function parseOverlayId(id: string): LevelRef | null {
  const [entryId, kind, levelId] = id.split(separator)
  if (!entryId) return null
  if (kind === 'entry') return { entryId, kind }
  if ((kind === 'stop' || kind === 'target') && levelId) return { entryId, kind, levelId }
  return null
}

/** The play's whole-number leverage, or 1 while it is blank or invalid. */
export function leverageOf(leverage: string) {
  const value = Number(leverage)
  return Number.isInteger(value) && value >= 1 ? value : 1
}

export const exitsOf = (entry: DraftEntry, kind: ExitKind) => kind === 'stop' ? entry.stops : entry.targets
export const withExits = (entry: DraftEntry, kind: ExitKind, exits: DraftExit[]): DraftEntry =>
  kind === 'stop' ? { ...entry, stops: exits } : { ...entry, targets: exits }

/** True when a target sits above the entry: long targets and short stops. */
const above = (kind: ExitKind, direction: PlayDraft['direction']) => (kind === 'target') === (direction === 'long')

/**
 * Plotting position of a stop or target. A percentage is the unsigned return on margin at the
 * play's leverage, so the price moves percent ÷ leverage from the entry, on the side the direction
 * gives. This is display placement, not a profit or risk calculation.
 */
export function levelPrice(entryPrice: number | null, level: DraftExit, kind: ExitKind, direction: PlayDraft['direction'], leverage = 1) {
  if (level.unit === 'price') return positive(level.value)
  const percent = unsigned(level.value)
  if (entryPrice === null || percent === null) return null
  const distance = percent / Math.max(1, leverage)
  const price = entryPrice * (1 + (above(kind, direction) ? distance : -distance) / 100)
  return price > 0 ? price : null
}

/** Price move from the entry, in percent, that a percent level stands for at this leverage. */
export const priceMovePercent = (percent: number, leverage: number) => percent / Math.max(1, leverage)

// Stops and targets share risk colors across entries; the tag accent identifies the entry.
const stopColor = 'var(--negative)'
const targetColor = 'var(--positive)'

export const averageOverlayId = 'aggregate:average-entry'

/**
 * Quantity-weighted planned entry price over entries that have both a price and a positive
 * quantity share. Shares are normalized over those entries. Null unless `minimum` entries contribute
 * (two for the chart line, where a single entry's own line already shows its price).
 */
export function averageEntryPrice(entries: readonly DraftEntry[], minimum = 2) {
  const weighted = entries.flatMap(entry => {
    const price = positive(entry.price)
    const share = positive(entry.share)
    return price !== null && share !== null ? [{ price, share }] : []
  })
  if (weighted.length < Math.max(1, minimum)) return null
  const total = weighted.reduce((sum, item) => sum + item.share, 0)
  return weighted.reduce((sum, item) => sum + item.price * item.share, 0) / total
}

/** Short chart tag of a level, e.g. "E2 SL1". A single entry drops the entry prefix. */
export function levelTag(entries: readonly DraftEntry[], ref: LevelRef) {
  const index = entries.findIndex(entry => entry.id === ref.entryId)
  const entry = entries[index]
  const prefix = entries.length > 1 ? `E${index + 1}` : ''
  if (ref.kind === 'entry' || !entry) return prefix || 'Entry'
  const exits = exitsOf(entry, ref.kind)
  const position = exits.findIndex(exit => exit.id === ref.levelId)
  const name = `${ref.kind === 'stop' ? 'SL' : 'TP'}${exits.length > 1 || ref.kind === 'target' ? position + 1 : ''}`
  return prefix ? `${prefix} ${name}` : name
}

/** What the venue evidence says about one planned level: a short label in its own words, and whether an order is confirmed there. */
export interface LevelEvidence { text: string; confirmed: boolean }
/** Evidence for a planned stop by entry and level, or null when there is nothing to say about it. */
export type StopEvidenceOf = (entryId: string, levelId: string) => LevelEvidence | null

/**
 * Chart levels for the plan. With `stopEvidence`, each stop's tag adds the evidence's words ("SL · not seen at the venue")
 * and a stop without a confirmed resting order is drawn unconfirmed, apart from one that rests at the venue.
 */
export function planOverlays(entries: readonly DraftEntry[], selectedId: string, view: ChartView, direction: PlayDraft['direction'], leverage = 1,
  stopEvidence?: StopEvidenceOf): PriceOverlay[] {
  const overlays: PriceOverlay[] = []
  entries.forEach(entry => {
    const selected = entry.id === selectedId
    if (view === 'selected' && !selected) return
    // Every planned level can be dragged; dragging another entry's level selects that entry.
    const base = { color: entry.color, accent: entry.color, emphasis: selected ? 'selected' as const : 'normal' as const, draggable: true }
    const entryPrice = positive(entry.price)
    const entryRef = { entryId: entry.id, kind: 'entry' } as const
    if (entryPrice !== null) overlays.push({ ...base, id: overlayId(entryRef), label: levelTag(entries, entryRef), price: entryPrice, kind: 'entry' })
    for (const kind of ['stop', 'target'] as const) for (const exit of exitsOf(entry, kind)) {
      const price = levelPrice(entryPrice, exit, kind, direction, leverage)
      const ref = { entryId: entry.id, kind, levelId: exit.id }
      if (price === null) continue
      const evidence = kind === 'stop' ? stopEvidence?.(entry.id, exit.id) ?? null : null
      const tag = levelTag(entries, ref)
      overlays.push({ ...base, id: overlayId(ref), label: evidence ? `${tag} · ${evidence.text}` : tag, price, kind, color: kind === 'stop' ? stopColor : targetColor,
        ...evidence ? { unconfirmed: !evidence.confirmed } : {} })
    }
  })
  const average = view === 'aggregate' ? averageEntryPrice(entries) : null
  if (average !== null) overlays.push({
    id: averageOverlayId, label: 'AVG', price: average, color: 'var(--foreground)', kind: 'reference', emphasis: 'normal', draggable: false,
  })
  return overlays
}

const trim = (text: string) => text.includes('.') ? text.replace(/\.?0+$/, '') : text

/** Decimal places of a tick such as 0.5 or 0.00001, at most twelve. */
export function stepDecimals(step: number) {
  let places = 0
  while (places < 12 && Math.abs(Math.round(step * 10 ** places) - step * 10 ** places) > 1e-9) places++
  return places
}

/**
 * A chart price as the venue would accept it: rounded to the instrument's tick when there is one, otherwise to
 * five significant figures (the rule of venues without ticks), at most eight decimals, no exponent.
 */
export function formatDraggedPrice(price: number, step?: number | null) {
  if (step != null && step > 0) {
    const places = stepDecimals(step)
    return trim((Math.round(price / step) * step).toFixed(places))
  }
  const decimals = Math.min(8, Math.max(0, 4 - Math.floor(Math.log10(price))))
  return trim(price.toFixed(decimals))
}

/** A chart drag chooses a fixed price, switching percentage stops and targets to price units. */
export function applyLevelDrag(entries: readonly DraftEntry[], id: string, price: number, step?: number | null): DraftEntry[] {
  const ref = parseOverlayId(id)
  if (!ref || !Number.isFinite(price) || price <= 0) return [...entries]
  return entries.map(entry => {
    if (entry.id !== ref.entryId) return entry
    if (ref.kind === 'entry') return { ...entry, price: formatDraggedPrice(price, step) }
    return withExits(entry, ref.kind, exitsOf(entry, ref.kind).map(exit =>
      exit.id === ref.levelId ? { ...exit, unit: 'price', value: formatDraggedPrice(price, step) } : exit))
  })
}

/** Default distance for levels added from the chart, as a fraction of the entry price. */
const addedLevelDistance = 0.02

/** Plotted price of one level, or null when it is blank or invalid. */
export function overlayPrice(entry: DraftEntry, id: string, direction: PlayDraft['direction'], leverage = 1) {
  const ref = parseOverlayId(id)
  if (!ref || ref.entryId !== entry.id) return null
  const entryPrice = positive(entry.price)
  if (ref.kind === 'entry') return entryPrice
  const exit = exitsOf(entry, ref.kind).find(current => current.id === ref.levelId)
  return exit ? levelPrice(entryPrice, exit, ref.kind, direction, leverage) : null
}

/**
 * Puts a stop or target at `price`: fills the first blank one of that kind, or adds another. A new
 * level is in price units; the first one of its kind closes the whole entry.
 */
export function placeExit(entry: DraftEntry, kind: ExitKind, price: number, step?: number | null): DraftEntry {
  const exits = exitsOf(entry, kind)
  const value = formatDraggedPrice(price, step)
  const blank = exits.find(exit => exit.value.trim() === '')
  if (blank) return withExits(entry, kind, exits.map(exit => exit.id === blank.id ? { ...exit, unit: 'price', value } : exit))
  return withExits(entry, kind, [...exits, { ...createExit(exits.length ? '' : '100'), value }])
}

/** Sets an entry price, or a stop or target as `placeExit` does, from a price picked on the chart. */
export function placeLevel(entry: DraftEntry, kind: 'entry' | ExitKind, price: number, step?: number | null): DraftEntry {
  if (!Number.isFinite(price) || price <= 0) return entry
  return kind === 'entry' ? { ...entry, price: formatDraggedPrice(price, step) } : placeExit(entry, kind, price, step)
}

/** Adds a stop or target a step beyond the last one, or null without an entry price. */
export function addChartExit(entry: DraftEntry, kind: ExitKind, direction: PlayDraft['direction'], leverage = 1, step?: number | null): DraftEntry | null {
  const entryPrice = positive(entry.price)
  if (entryPrice === null) return null
  const plotted = exitsOf(entry, kind).filter(exit => levelPrice(entryPrice, exit, kind, direction, leverage) !== null).length
  const sign = above(kind, direction) ? 1 : -1
  return placeExit(entry, kind, entryPrice * (1 + sign * addedLevelDistance * (plotted + 1)), step)
}

export function removeExit(entry: DraftEntry, kind: ExitKind, id: string): DraftEntry {
  return withExits(entry, kind, exitsOf(entry, kind).filter(exit => exit.id !== id))
}

export function setExitShare(entry: DraftEntry, kind: ExitKind, id: string, share: string): DraftEntry {
  return withExits(entry, kind, exitsOf(entry, kind).map(exit => exit.id === id ? { ...exit, share } : exit))
}

/** Stops and targets entered as a percentage with a value, in entry order. Their prices depend on the leverage. */
export function percentLevels(entries: readonly DraftEntry[]) {
  return entries.flatMap(entry => (['stop', 'target'] as const).flatMap(kind =>
    exitsOf(entry, kind).filter(exit => exit.unit === 'percent' && unsigned(exit.value) !== null)
      .map(exit => ({ entry, kind, exit, ref: { entryId: entry.id, kind, levelId: exit.id } as LevelRef }))))
}

/** Rewrites percentage stops and targets for a new leverage so their prices stay where they are. */
export function keepPercentLevelPrices(entries: readonly DraftEntry[], from: number, to: number): DraftEntry[] {
  const factor = Math.max(1, to) / Math.max(1, from)
  const rescale = (exit: DraftExit): DraftExit => {
    const percent = exit.unit === 'percent' ? unsigned(exit.value) : null
    return percent === null ? exit : { ...exit, value: trim((percent * factor).toFixed(4)) }
  }
  return entries.map(entry => ({ ...entry, stops: entry.stops.map(rescale), targets: entry.targets.map(rescale) }))
}

const scalarFields = ['name', 'price', 'share'] as const
const exitFields = ['unit', 'value', 'share'] as const

/** Copies only the sub-fields that changed between `from` and `to` onto `current`. */
function patchFields<T extends object, K extends keyof T>(current: T, from: T, to: T, fields: readonly K[]): T {
  let next = current
  for (const field of fields) if (from[field] !== to[field]) next = { ...next, [field]: to[field] }
  return next
}

/** Patches one list of stops or targets by ID: changed sub-fields, additions and removals. */
function patchExits(current: DraftExit[], from: DraftExit[], to: DraftExit[]) {
  const before = new Map(from.map(exit => [exit.id, exit]))
  const after = new Map(to.map(exit => [exit.id, exit]))
  let exits = current
    .filter(exit => !(before.has(exit.id) && !after.has(exit.id)))
    .map(exit => {
      const was = before.get(exit.id)
      const now = after.get(exit.id)
      return was && now ? patchFields(exit, was, now, exitFields) : exit
    })
  to.forEach((exit, index) => {
    if (before.has(exit.id) || exits.some(existing => existing.id === exit.id)) return
    exits = [...exits.slice(0, index), exit, ...exits.slice(index)]
  })
  const unchanged = exits.length === current.length && exits.every((exit, index) => exit === current[index])
  return unchanged ? current : exits
}

/**
 * Applies one recorded edit (`from` → `to`) to the current entry, touching only what the edit
 * changed: scalar fields, and stops and targets by ID (changed sub-fields, additions and removals).
 * Later edits to other fields or other levels, e.g. in the side editor, are preserved.
 */
export function applyEntryEdit(current: DraftEntry, from: DraftEntry, to: DraftEntry): DraftEntry {
  let next = patchFields(current, from, to, scalarFields)
  const stops = patchExits(current.stops, from.stops, to.stops)
  if (stops !== current.stops) next = { ...next, stops }
  const targets = patchExits(current.targets, from.targets, to.targets)
  return targets === current.targets ? next : { ...next, targets }
}
