import { useSyncExternalStore } from 'react'
import { exactAbs, exactAdd, exactDecimal, exactDiv, exactInteger, exactMul, exactSignOf, type Exact } from '@/lib/decimal'
import { onPreferenceScope, preferenceKey } from '@/lib/preference-scope'
import { exactPlanNumber } from './numbers'
import type { SummaryDraft } from './position-risk-summary'

/**
 * Planning cost assumptions (Phase 2, item 10a): a round-trip fee and a slippage percentage per venue, entered by the
 * owner in Settings and kept in this browser. There are no defaults; a venue with neither set shows nothing. They only
 * annotate the Play summary beside its gross figures: sizing, Review and every stored result never read them.
 */
export interface CostAssumption {
  /** Round-trip fee as a percentage of the position size, an exact decimal string, or null when not set. */
  feePercent: string | null
  /** Round-trip slippage as a percentage of the position size, or null when not set. */
  slippagePercent: string | null
}

export type CostAssumptions = Readonly<Record<string, CostAssumption>>

const name = 'costs.assumptions.v1'
export const maxCostPercent = 10

/** At least 0 and at most 10, a plain decimal with at most four places; blank is not set. Undefined when invalid. */
export function costPercentText(text: string): string | null | undefined {
  const value = text.trim()
  if (value === '') return null
  if (!/^\d+(\.\d{1,4})?$/.test(value)) return undefined
  return Number(value) <= maxCostPercent ? value : undefined
}

const valid = (value: unknown): value is string | null =>
  value === null || (typeof value === 'string' && costPercentText(value) === value)

export function readCostAssumptions(): CostAssumptions {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(preferenceKey(name)) ?? 'null')
    if (typeof stored !== 'object' || stored === null || Array.isArray(stored)) return {}
    const result: Record<string, CostAssumption> = {}
    for (const [venue, entry] of Object.entries(stored as Record<string, unknown>)) {
      if (typeof entry !== 'object' || entry === null) continue
      const { feePercent = null, slippagePercent = null } = entry as Record<string, unknown>
      // A malformed stored value is dropped, never read as zero.
      if (valid(feePercent) && valid(slippagePercent) && (feePercent !== null || slippagePercent !== null))
        result[venue] = { feePercent, slippagePercent }
    }
    return result
  } catch {
    return {}
  }
}

let current: CostAssumptions | undefined
let storedText: string | null = null
const listeners = new Set<() => void>()
const readStorage = () => {
  try { return localStorage.getItem(preferenceKey(name)) } catch { return null }
}
// Reread another tab's change when the first view mounts; mounted views keep one stable snapshot until notified.
const snapshot = () => {
  if (!current || (!listeners.size && readStorage() !== storedText)) {
    current = readCostAssumptions()
    storedText = readStorage()
  }
  return current
}
const subscribe = (listener: () => void) => {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}
const reset = () => {
  current = undefined
  for (const listener of [...listeners]) listener()
}
onPreferenceScope(reset)
if (typeof window !== 'undefined') window.addEventListener('storage', event => {
  if (event.key === null || event.key === preferenceKey(name)) reset()
})

/** Sets one venue's assumption; both blank removes it. */
function update(venueId: string, assumption: CostAssumption) {
  const next: Record<string, CostAssumption> = { ...snapshot() }
  if (assumption.feePercent === null && assumption.slippagePercent === null) delete next[venueId]
  else next[venueId] = assumption
  current = next
  try { localStorage.setItem(preferenceKey(name), JSON.stringify(current)) } catch { /* Preferences stay in memory. */ }
  storedText = readStorage()
  for (const listener of [...listeners]) listener()
}

export function useCostAssumptions() {
  return [useSyncExternalStore(subscribe, snapshot), update] as const
}

export interface AssumedCosts {
  assumption: CostAssumption
  /** The assumed round-trip cost in the quote asset, positive; null when the position size is unknown. */
  cost: Exact | null
  /** The cost over the plan's loss at its stops, positive; null when that loss is unknown or there is none. */
  r: Exact | null
}

/**
 * The planned position size, exactly: margin × leverage in margin sizing, or the quantity × the share-weighted planned
 * average entry in quantity sizing, which needs every entry priced and the shares to total 100%.
 */
export function plannedNotional(draft: SummaryDraft): Exact | null {
  const size = exactPlanNumber(draft.size)
  if (size === null || exactSignOf(size) <= 0) return null
  if (draft.sizingMode === 'margin') {
    const leverage = Number(draft.leverage)
    return Number.isInteger(leverage) && leverage >= 1 ? exactMul(size, exactInteger(leverage)) : null
  }
  let shares = exactInteger(0)
  let weighted = exactInteger(0)
  for (const entry of draft.entries) {
    const price = exactPlanNumber(entry.price)
    const share = exactPlanNumber(entry.share)
    if (share === null || exactSignOf(share) < 0) return null
    if (exactSignOf(share) === 0) continue
    if (price === null || exactSignOf(price) <= 0) return null
    shares = exactAdd(shares, share)
    weighted = exactAdd(weighted, exactMul(price, share))
  }
  if (shares.n !== 100n * shares.d) return null
  const average = exactDiv(weighted, shares)
  return average === null ? null : exactMul(size, average)
}

/**
 * The owner's assumed costs for this plan. `atStops` is the all-stops result (negative is a loss); R is measured only
 * against a known loss. A percentage that is not set adds nothing and is shown as not set, never as zero.
 */
export function assumedCosts(draft: SummaryDraft, assumption: CostAssumption | null | undefined, atStops: Exact | null): AssumedCosts | null {
  if (!assumption || (assumption.feePercent === null && assumption.slippagePercent === null)) return null
  const fee = assumption.feePercent === null ? exactInteger(0) : exactDecimal(assumption.feePercent)
  const slippage = assumption.slippagePercent === null ? exactInteger(0) : exactDecimal(assumption.slippagePercent)
  const notional = plannedNotional(draft)
  if (fee === null || slippage === null) return null
  const cost = notional === null ? null : exactDiv(exactMul(notional, exactAdd(fee, slippage)), exactInteger(100))
  const loss = atStops !== null && exactSignOf(atStops) < 0 ? exactAbs(atStops) : null
  return { assumption, cost, r: cost === null || loss === null ? null : exactDiv(cost, loss) }
}
