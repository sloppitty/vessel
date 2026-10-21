import type { BrokerAccount } from '@/api/workspace'
import { asOf, availableSourceLabels, numberFormat } from '@/lib/format'
import type { DraftEntry, DraftExit, PlayDraft } from './draft'
import { averageEntryPrice, exitsOf, levelPrice, type ExitKind } from './levels'
import { formatPlanNumber, planNumber, planShare, positivePlanNumber as positive } from './numbers'
import type { DisplayZone } from '@/lib/display-zone'

/**
 * Whole-position size at a leverage, before fees. Margin sizing fixes the margin, so leverage changes
 * the notional and quantity; quantity sizing fixes the quantity, so leverage changes the margin.
 * Notional uses the quantity-weighted planned average entry. Unknown parts are null.
 */
export interface PositionSize {
  margin: number | null
  notional: number | null
  quantity: number | null
  averageEntry: number | null
}

export function positionSize(draft: Pick<PlayDraft, 'size' | 'sizingMode' | 'entries'>, leverage: number): PositionSize {
  const size = positive(draft.size)
  const averageEntry = averageEntryPrice(draft.entries, 1)
  const lever = Math.max(1, leverage)
  if (size === null) return { margin: null, notional: null, quantity: null, averageEntry }
  if (draft.sizingMode === 'margin') {
    const notional = size * lever
    return { margin: size, notional, quantity: averageEntry === null ? null : notional / averageEntry, averageEntry }
  }
  const notional = averageEntry === null ? null : size * averageEntry
  return { margin: notional === null ? null : notional / lever, notional, quantity: size, averageEntry }
}

/**
 * One exit's underlying price move, return on its margin and estimated result in quote units.
 * Quantity already includes leverage; apply only the entry and exit quantity shares, not leverage again.
 * Before fees, funding and slippage. Missing/invalid quantity or shares leave the amount unknown.
 */
export function exitEstimate(entry: DraftEntry, exit: DraftExit, kind: ExitKind, direction: PlayDraft['direction'], leverage: number, quantity: number | null) {
  const entryPrice = positive(entry.price)
  const price = levelPrice(entryPrice, exit, kind, direction, leverage)
  if (entryPrice === null || price === null || !Number.isFinite(entryPrice) || !Number.isFinite(price)) return null
  const distance = (price - entryPrice) * (direction === 'long' ? 1 : -1) * (kind === 'stop' ? -1 : 1)
  if (distance <= 0) return null
  const fraction = (value: string) => {
    const share = planShare(value)
    return share === null ? null : share / 100
  }
  const entryShare = fraction(entry.share)
  const exitShare = fraction(exit.share)
  const amount = quantity !== null && Number.isFinite(quantity) && quantity >= 0 && entryShare !== null && exitShare !== null
    ? quantity * entryShare * exitShare * distance : null
  const move = distance / entryPrice * 100
  const returnPercent = move * Math.max(1, leverage)
  if (!Number.isFinite(returnPercent)) return null
  return { price, move, returnPercent, amount: Number.isFinite(amount) ? amount : null }
}

/** Units for the size readout: the quote asset (e.g. USDC) and the contract (e.g. ETH) when known. */
export interface SizeUnits {
  quote: string
  base: string
  /** Venue quantity precision; null rounds to five significant figures. */
  quantityDecimals: number | null
}

/** Without a venue contract the units are generic. */
export const defaultSizeUnits: SizeUnits = { quote: 'quote units', base: 'units', quantityDecimals: null }

const money = numberFormat({ maximumFractionDigits: 2 })

export function formatMoney(value: number, units: SizeUnits) {
  return `${money.format(value)} ${units.quote}`
}

export function formatQuantity(value: number, units: SizeUnits) {
  const digits = units.quantityDecimals ?? Math.min(8, Math.max(0, 4 - Math.floor(Math.log10(Math.abs(value) || 1))))
  return `${numberFormat({ maximumFractionDigits: digits }).format(value)} ${units.base}`
}

/** What a leverage change does to the position size, in one sentence. Null when the size is not known. */
export function sizeChange(draft: Pick<PlayDraft, 'size' | 'sizingMode' | 'entries'>, from: number, to: number, units: SizeUnits) {
  const before = positionSize(draft, from)
  const after = positionSize(draft, to)
  if (draft.sizingMode === 'margin' && before.margin !== null && before.notional !== null && after.notional !== null) {
    const quantity = before.quantity !== null && after.quantity !== null
      ? ` (${formatQuantity(before.quantity, units)} → ${formatQuantity(after.quantity, units)})` : ''
    return `Margin stays ${formatMoney(before.margin, units)}; the position goes from ${formatMoney(before.notional, units)} to ${formatMoney(after.notional, units)}${quantity}.`
  }
  if (draft.sizingMode === 'quantity' && before.quantity !== null && before.margin !== null && after.margin !== null)
    return `Quantity stays ${formatQuantity(before.quantity, units)}; margin goes from ${formatMoney(before.margin, units)} to ${formatMoney(after.margin, units)}.`
  return null
}

/**
 * The same position in the other sizing unit: margin ↔ quantity at the leverage and planned average
 * entry. Blank when the size or an entry price is missing, so nothing is invented.
 */
export function convertSize(draft: Pick<PlayDraft, 'size' | 'sizingMode' | 'entries'>, leverage: number, units: SizeUnits) {
  const sized = positionSize(draft, leverage)
  if (draft.sizingMode === 'margin') {
    if (sized.quantity === null) return ''
    return formatPlanNumber(sized.quantity, units.quantityDecimals ?? 6)
  }
  return sized.margin === null ? '' : formatPlanNumber(sized.margin, 2)
}

/** Where the account's available balance comes from: the server selects exactly one source per account. */
export type AvailableSource = NonNullable<BrokerAccount['availableBalanceSource']>
/** The account's normalized available balance (`availableBalanceUsd`), an exact decimal string, with its source. */
export interface AvailableBalance {
  value: string
  /** Null only from an older server that did not name it. */
  source: AvailableSource | null
  /** When the snapshot it comes from was read: the account's last successful refresh. */
  asOfUtc?: string | null
  /** The account's latest refresh failed, so the figure is from an earlier snapshot. */
  refreshFailed?: boolean
}

/**
 * What an enabled account reports as available: the server's one normalized figure (wallet funds for
 * unified wallet accounts, otherwise the venue's available margin or else its withdrawable amount),
 * never the separate stablecoin wallet ledger of a standard account. Null when it is not reported.
 * No age limit is assumed; the figure carries its snapshot time and whether the last refresh failed.
 */
export function availableOf(account: BrokerAccount | null | undefined): AvailableBalance | null {
  if (!account || account.isEnabled === false || account.availableBalanceUsd == null) return null
  return { value: account.availableBalanceUsd, source: account.availableBalanceSource ?? null,
    asOfUtc: account.lastSyncedAtUtc, refreshFailed: account.syncStatus === 'error' }
}

/** "reported margin · as of 14:02 CEST", with "last refresh failed" when the latest refresh did not succeed. */
export const availableLabel = (available: AvailableBalance, zone: DisplayZone) => [available.source ? availableSourceLabels[available.source] : 'available',
  available.asOfUtc ? asOf(available.asOfUtc, zone) : null, available.refreshFailed ? 'last refresh failed' : null].filter(Boolean).join(' · ')

export interface PlayBudget {
  /** Exact decimal string. */
  value: string
  amount: number
  source: 'manual' | AvailableSource | 'available'
}

/** "manual", "wallet", "reported margin" or "reported withdrawal". */
export const budgetSourceLabel = (budget: Pick<PlayBudget, 'source'>) =>
  budget.source === 'manual' || budget.source === 'available' ? budget.source : availableSourceLabels[budget.source]

/**
 * The play's budget: the manual amount when set, otherwise the account's available balance with its
 * source. A known zero is a budget of zero; only an unknown balance leaves the budget unknown (null),
 * and so does a manual amount that is not plan text (stored before the plain-decimal rule).
 */
export function playBudget(draft: Pick<PlayDraft, 'budgetOverride'>, available: AvailableBalance | null | undefined): PlayBudget | null {
  if (draft.budgetOverride !== null) {
    const amount = planNumber(draft.budgetOverride)
    return amount === null ? null : { value: draft.budgetOverride, amount, source: 'manual' }
  }
  if (!available) return null
  return { value: available.value, amount: Number(available.value), source: available.source ?? 'available' }
}

/** Margin above the budget, before fees. Null when it fits or either side is unknown. */
export function marginOverBudget(draft: Pick<PlayDraft, 'size' | 'sizingMode' | 'entries' | 'budgetOverride'>, leverage: number,
  available: AvailableBalance | null | undefined) {
  const budget = playBudget(draft, available)
  const { margin } = positionSize(draft, leverage)
  // Cents of rounding are not worth blocking a plan.
  if (budget === null || !Number.isFinite(budget.amount) || margin === null || margin <= budget.amount + 0.005) return null
  return { margin, budget }
}

/** Why a draft's margin does not fit, in the instrument's quote unit, naming the budget's source. */
export const overBudgetMessage = (over: NonNullable<ReturnType<typeof marginOverBudget>>, units: SizeUnits) =>
  `Margin ${formatMoney(over.margin, units)} is above the ${formatMoney(over.budget.amount, units)} budget (${budgetSourceLabel(over.budget)}).`

/** The size field value that uses the whole budget, or null when it cannot be converted. Quantities round down. */
export function sizeForBudget(draft: Pick<PlayDraft, 'sizingMode' | 'entries'>, leverage: number, budget: string, units: SizeUnits) {
  if (draft.sizingMode === 'margin') return budget
  const averageEntry = averageEntryPrice(draft.entries, 1)
  if (averageEntry === null) return null
  const decimals = units.quantityDecimals ?? 6
  const factor = 10 ** decimals
  return formatPlanNumber(Math.floor(Number(budget) * Math.max(1, leverage) / averageEntry * factor) / factor, decimals)
}

/**
 * Share-weighted distance from the entry price to its stops or targets, on their correct side.
 * Shares are normalized over the levels with a price; blank shares count equally when none is set.
 */
export function averageDistance(entry: DraftEntry, entryPrice: number, kind: ExitKind, direction: PlayDraft['direction'], leverage: number) {
  const levels = weightedLevels(entry, entryPrice, kind, direction, leverage)
  if (!levels.length) return null
  const total = levels.reduce((sum, level) => sum + level.weight, 0)
  return total > 0 ? levels.reduce((sum, level) => sum + level.distance * level.weight, 0) / total : null
}

/**
 * Stops or targets with a price, their distance from the entry and their weight in the average:
 * their share, or 1 each when no share is set. Levels without a price are left out.
 */
export function weightedLevels(entry: DraftEntry, entryPrice: number, kind: ExitKind, direction: PlayDraft['direction'], leverage: number) {
  const levels = exitsOf(entry, kind).flatMap(exit => {
    const price = levelPrice(entryPrice, exit, kind, direction, leverage)
    return price === null ? [] : [{ exit, price, distance: Math.abs(price - entryPrice), share: positive(exit.share) }]
  })
  const weighted = levels.some(level => level.share !== null)
  return levels.map(({ share, ...level }) => ({ ...level, weight: weighted ? share ?? 0 : 1 }))
}

/**
 * Planned reward per unit of risk of one entry: the share-weighted target distance over the
 * share-weighted stop distance, before fees. Null without an entry price, a stop and a target, or
 * when a level sits on the wrong side, since the plan check already flags those.
 */
export function rewardToRisk(entry: DraftEntry, direction: PlayDraft['direction'], leverage: number) {
  const entryPrice = positive(entry.price)
  if (entryPrice === null) return null
  const sign = direction === 'long' ? 1 : -1
  const wrong = (['stop', 'target'] as const).some(kind => exitsOf(entry, kind).some(exit => {
    const price = levelPrice(entryPrice, exit, kind, direction, leverage)
    return price !== null && (price - entryPrice) * sign * (kind === 'target' ? 1 : -1) <= 0
  }))
  if (wrong) return null
  const risk = averageDistance(entry, entryPrice, 'stop', direction, leverage)
  const reward = averageDistance(entry, entryPrice, 'target', direction, leverage)
  return risk === null || reward === null || risk === 0 ? null : reward / risk
}

/** "1:2.5" with at most two decimals. */
export const formatRewardToRisk = (ratio: number) => `1:${numberFormat({ maximumFractionDigits: 2 }).format(ratio)}`

/**
 * Estimated liquidation price of the whole plan, assuming every entry fills at its planned price
 * and the position uses isolated margin. The margin is the notional ÷ leverage, so the price is
 * where that margin plus the unrealized result falls to the maintenance margin: the fraction the
 * venue states for the contract, otherwise assumed to be half the initial margin at the venue
 * maximum leverage. Fees, funding, margin tiers of large positions and the mark price are ignored.
 * Cross margin depends on shared collateral and other positions; it is not always safer.
 * Null without an average entry or maintenance margin, or when there is no liquidation price (1× long).
 */
export function estimatedLiquidation(entries: PlayDraft['entries'], direction: PlayDraft['direction'], leverage: number, maxLeverage: number | null,
  maintenanceMargin: number | null = null) {
  const averageEntry = averageEntryPrice(entries, 1)
  const maintenance = maintenanceMargin !== null && maintenanceMargin > 0 && maintenanceMargin < 1 ? maintenanceMargin
    : maxLeverage !== null && maxLeverage >= 1 ? 1 / (2 * maxLeverage) : null
  if (averageEntry === null || maintenance === null) return null
  const margin = 1 / Math.max(1, leverage)
  const price = direction === 'long'
    ? averageEntry * (1 - margin) / (1 - maintenance)
    : averageEntry * (1 + margin) / (1 + maintenance)
  return price > 0 ? price : null
}
