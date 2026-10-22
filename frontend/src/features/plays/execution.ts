import type { ExecutionFill, ExecutionOrder, LinkOrder, LinkRole, OrderLink, PlaySummary } from '@/api/plays'
import type { MonitorDiscrepancy } from '@/api/monitor'
import { exactDecimal } from '@/lib/decimal'
import type { DraftEntry } from './draft'
import { exactLevelPrice, pricesMatch } from './levels'

export function describeOrder(order: ExecutionOrder) {
  const side = order.side === 'buy' ? 'Buy' : 'Sell'
  const price = order.triggerPrice ? `trigger ${order.triggerPrice}` : `at ${order.limitPrice}`
  const size = order.isPositionTpsl ? 'whole position' : order.originalSize
  return `${side} ${order.orderType.toLowerCase()} ${price} · ${size}`
}

/**
 * Suggestions grouped by order, in first-seen order: each order once, with every level of this Play it matches, and
 * whether another active Play is also offered it (phase 1, 1.6d). A suggestion without order details stands alone.
 */
export function suggestionGroups(suggestions: readonly OrderLink[]) {
  const groups = new Map<string, { key: string; links: OrderLink[]; otherPlay: boolean }>()
  for (const link of suggestions) {
    const key = link.order ? `order:${link.order.orderId}` : `link:${link.id}`
    const group = groups.get(key) ?? { key, links: [], otherPlay: false }
    group.links.push(link)
    group.otherPlay ||= link.otherPlay === true
    groups.set(key, group)
  }
  return [...groups.values()]
}

/** Targets are always numbered; a single stop is just "stop", like the chart tags. */
const exitName = (entry: DraftEntry, kind: 'stop' | 'target', index: number) =>
  kind === 'target' || entry.stops.length > 1 ? `${entry.name} ${kind} ${index + 1}` : `${entry.name} ${kind}`

/** Readable name of a plan level, e.g. "Entry 1 target 2". */
export function levelName(entries: readonly DraftEntry[], link: Pick<OrderLink, 'role' | 'entryId' | 'levelId'>, origin: PlaySummary['origin'] = 'planned') {
  if (origin === 'retrospective') return link.role === 'entry' ? 'Actual entry' : 'Actual exit'
  if (link.role === 'exit') return 'Unplanned exit'
  const entry = entries.find(item => item.id === link.entryId)
  if (link.role === 'entry') return entry?.name ?? 'Removed entry'
  const index = (link.role === 'stop' ? entry?.stops : entry?.targets)?.findIndex(exit => exit.id === link.levelId) ?? -1
  return entry && index >= 0 ? exitName(entry, link.role, index) : `${entry?.name ?? 'Removed entry'} removed ${link.role}`
}

/**
 * Whether the order can take the role on a play of this direction: an entry buys for a long and can open a
 * position; stops, targets and exits trade the other side. The server also checks what the fills did.
 */
export function orderFitsRole(order: ExecutionOrder, role: LinkRole, direction: 'long' | 'short') {
  const entrySide = direction === 'long' ? 'buy' : 'sell'
  return role === 'entry' ? order.side === entrySide && !order.reduceOnly && !order.isPositionTpsl : order.side !== entrySide
}

/** The server's stop classification (`OrderMechanics`): a trigger order whose type names a stop and not a take-profit. */
export const isStopMechanism = (order: ExecutionOrder) => {
  if (order.triggerPrice === null) return false
  const words = order.orderType.toLowerCase().replace(/[-_]/g, ' ')
  return !words.includes('take profit') && words.includes('stop')
}

export function levelOptions(entries: readonly DraftEntry[], open: boolean) {
  const options: Array<{ value: string; label: string; request: Omit<LinkOrder, 'orderId'> }> = []
  for (const entry of entries) {
    options.push({ value: `entry|${entry.id}`, label: entry.name, request: { role: 'entry', entryId: entry.id } })
    for (const kind of ['stop', 'target'] as const) (kind === 'stop' ? entry.stops : entry.targets).forEach((exit, index) => options.push({
      value: `${kind}|${entry.id}|${exit.id}`, label: exitName(entry, kind, index), request: { role: kind, entryId: entry.id, levelId: exit.id },
    }))
  }
  if (open) options.push({ value: 'exit', label: 'Unplanned exit', request: { role: 'exit' } })
  return options
}

/** "Open Long · 3 fills, average 84541". The average is weighted by quantity and only for display. */
export function fillSummary(fills: readonly ExecutionFill[]) {
  if (fills.length === 1) return `${fills[0]!.direction} ${fills[0]!.quantity} at ${fills[0]!.price}`
  const quantity = fills.reduce((sum, fill) => sum + Number(fill.quantity), 0)
  const average = fills.reduce((sum, fill) => sum + Number(fill.price) * Number(fill.quantity), 0) / quantity
  const directions = [...new Set(fills.map(fill => fill.direction))].join(', ')
  return `${directions} · ${fills.length} fills, average ${Number(average.toPrecision(8))}`
}

/** A linked stop or target a `level.drift` record names, and the price its order rests at. */
export interface LevelDrift {
  link: OrderLink
  /** The order's acting price as Vessel stores it (trigger, else limit): what a rebind sends as observed. */
  observed: string
  /** The plan price the monitor compared it with. */
  planned: string
  /** The plan matches the order now; the monitor clears the record on its next check. */
  applied: boolean
  /** The level's percentage when it is one: a rebind turns it into the observed price. */
  percent: string | null
}

/** What the plan's levels resolve against, as the server resolves and matches them. */
export interface DriftContext {
  direction: 'long' | 'short'
  leverage: number
  /** The instrument's tick; prices match within it when it is coarser than the fifth significant figure. */
  tick: number | null
}

/**
 * The drifted linked stops and targets of a `level.drift` record, with their resting order's price. A row matches as the
 * monitor decides (`pricesMatch`: the fifth significant figure, or a coarser tick), so only orders it found away from
 * their level are offered. Orders that no longer rest, entries, unplanned exits and malformed rows are left out. An order
 * price is never a fill: a rebind only moves the plan level to it.
 */
export function levelDrifts(discrepancy: MonitorDiscrepancy, links: readonly OrderLink[], entries: readonly DraftEntry[],
  context: DriftContext): LevelDrift[] {
  const rows = discrepancy.kind === 'level.drift' && Array.isArray(discrepancy.details.levels) ? discrepancy.details.levels as unknown[] : []
  return rows.flatMap(row => {
    if (typeof row !== 'object' || row === null) return []
    const { orderId, actualPrice, plannedPrice } = row as Record<string, unknown>
    if (typeof orderId !== 'string' || typeof actualPrice !== 'string' || typeof plannedPrice !== 'string') return []
    const [recorded, recordedPlan] = [exactDecimal(actualPrice), exactDecimal(plannedPrice)]
    if (recorded === null || recordedPlan === null || pricesMatch(recorded, recordedPlan, context.tick)) return []
    const link = links.find(item => (item.role === 'stop' || item.role === 'target') && item.state === 'linked' && item.order?.orderId === orderId)
    if (!link?.order || link.order.status !== 'open' || (link.role !== 'stop' && link.role !== 'target')) return []
    const observed = link.order.triggerPrice ?? link.order.limitPrice
    const price = exactDecimal(observed)
    const entry = entries.find(item => item.id === link.entryId)
    const level = (link.role === 'stop' ? entry?.stops : entry?.targets)?.find(item => item.id === link.levelId)
    if (price === null || !entry || !level) return []
    const current = exactLevelPrice(entry, level, link.role, context.direction, context.leverage)
    return [{ link, observed, planned: plannedPrice, applied: current !== null && pricesMatch(price, current, context.tick),
      percent: level.unit === 'percent' ? level.value.trim() : null }]
  })
}
