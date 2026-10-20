import {
  exactAbs, exactAdd, exactDiv, exactFixed, exactInteger, exactMul, exactNumber, exactSignOf, exactSub, exactZero, type Exact,
} from '@/lib/decimal'
import type { PlayDraft } from './draft'
import { exactLevelPrice, exitsOf, type ExitKind } from './levels'
import { exactPlanNumber, finitePlanNumber as decimal, planShare, planTextLength, serverPositivePlanNumber } from './numbers'
import { entryFilled, noFilledEntries, wrongSide, type FilledEntries } from './planChecks'
import { defaultSizeUnits, exitEstimate, positionSize, type SizeUnits } from './sizing'
import { numberFormat } from '@/lib/format'

/** The caller supplies a named reference in the position's quote asset. No conversion is inferred. */
export interface RiskReference {
  name: string
  amount: string
  quote: string
}

export interface RiskCoverage {
  /** Known percentage of the full position, not of the priced or allocated subset. */
  percent: number | null
  status: 'complete' | 'partial' | 'unknown' | 'invalid'
}

export interface PlannedExitSummary {
  allocation: RiskCoverage
  coverage: RiskCoverage
  /** Subtotal of modeled tranches, never substituted for an incomplete full-plan total. */
  knownAmount: number | null
  totalAmount: number | null
}

/** Where the all-stops result is measured from: each filled entry's average fill, otherwise its planned price. */
export type PayoffBasis = 'average fill' | 'planned entry' | 'mixed'

/**
 * The all-stops result, signed and exact: positive is locked in, negative at risk, zero break-even. Unknown is null,
 * never zero. Phase 0, 0.5b: a filled entry's stops may sit at or beyond the entry.
 */
export interface StopPayoff {
  /** The modeled stops' result, also when coverage is partial. */
  known: Exact | null
  /** The whole position's result, only with complete stop coverage. */
  total: Exact | null
  basis: PayoffBasis | null
  /** The size is zero: there is no position, so a zero result is not break-even. */
  empty: boolean
  /** The entries' fills are still loading, so the result is unknown rather than measured from the planned entries. */
  pending: boolean
  /** A rounded average fill leaves the result too close to zero to know its sign, so it is unknown. */
  rounding: boolean
}

/**
 * What the execution or the manual ledger says about the entries: which have filled, and each one's exact average fill
 * price where it is known. `'unknown'` while it loads.
 */
export interface EntryFills {
  filled: FilledEntries
  averages: ReadonlyMap<string, Exact>
  /** Entries whose average is a rounded figure (28 places) rather than exact; a result within that rounding has no known sign. */
  rounded?: ReadonlySet<string>
}

export const noEntryFills: EntryFills = { filled: noFilledEntries, averages: new Map<string, Exact>() }

export interface PositionRisk {
  quantity: number | null
  entryAllocation: RiskCoverage
  pricedCoverage: RiskCoverage
  pricedQuantity: number | null
  /** Loss amounts (`knownAmount`, `totalAmount`) are the payoff's negation, as display numbers; a locked-in result is negative. */
  stops: PlannedExitSummary
  stopPayoff: StopPayoff
  targets: PlannedExitSummary
  rewardToRisk: number | null
  reference: { name: string; amount: number } | null
  riskSharePercent: number | null
}

const finite = (value: number) => Number.isFinite(value) ? value : null
// Plan text holds at most 64 characters, so 64 fraction digits keep every share exact; nothing is truncated.
const shareDigits = planTextLength
const shareScale = 10n ** BigInt(shareDigits)
const fullShare = 100n * shareScale
const exactShare = (value: string) => {
  const text = value.trim()
  if (decimal(text) === null) return null
  const [whole = '', fraction = ''] = text.split('.')
  return BigInt(whole + fraction.padEnd(shareDigits, '0'))
}
const shareOf = (value: string) => {
  const share = exactShare(value)
  return share !== null && share <= fullShare ? decimal(value) : null
}

function coverage(percent: number | null, complete: boolean, invalid = false): RiskCoverage {
  return { percent, status: invalid ? 'invalid' : complete ? 'complete' : percent === null ? 'unknown' : 'partial' }
}

function allocation(values: string[]): RiskCoverage {
  const shares = values.map(exactShare)
  const known = shares.filter((share): share is bigint => share !== null)
  const total = known.reduce((sum, share) => sum + share, 0n)
  const percent = values.length === 0 ? 0 : known.length ? finite(Number(`${total}e-${shareDigits}`)) : null
  const invalid = values.some((value, index) => value.trim() !== '' && (shares[index] === null || (shares[index] ?? 0n) > fullShare))
    || total > fullShare || (known.length > 0 && percent === null)
  return coverage(percent, !invalid && known.length === values.length && total === fullShare, invalid)
}

/**
 * Linear planned payoff, using the same quantity and level conversion as the editor.
 * No fees, slippage, funding or execution claims. Zero-share entries/exits contribute nothing.
 * Unlike per-entry R:R, missing exit shares/prices are never normalized or equally distributed.
 */
/** The parts of a draft the summary reads. */
export type SummaryDraft = Pick<PlayDraft, 'entries' | 'direction' | 'size' | 'sizingMode' | 'leverage'>

/** Plan text as an exact value; anything the plan grammar refuses is unknown, as for every estimate. */
const exactPlan = exactPlanNumber

/**
 * The full-position quantity, exactly: the size in quantity sizing, or margin × leverage over the share-weighted planned
 * average entry (the editor's `positionSize`). The caller decides whether the plan can be sized at all.
 */
function exactQuantity(draft: SummaryDraft, leverage: number): Exact | null {
  const size = exactPlan(draft.size)
  if (size === null || exactSignOf(size) === 0 || draft.sizingMode === 'quantity') return size
  let shares = exactZero
  let notional = exactZero
  for (const entry of draft.entries) {
    const price = exactPlan(entry.price)
    const share = exactPlan(entry.share)
    if (price === null || share === null || exactSignOf(price) <= 0 || exactSignOf(share) <= 0) continue
    shares = exactAdd(shares, share)
    notional = exactAdd(notional, exactMul(price, share))
  }
  const average = exactDiv(notional, shares)
  return average === null ? null : exactDiv(exactMul(size, exactInteger(leverage)), average)
}

export function positionRiskSummary(draft: SummaryDraft, units: SizeUnits = defaultSizeUnits,
  riskReference?: RiskReference | null, entryFills: EntryFills | 'unknown' = noEntryFills): PositionRisk {
  const pending = entryFills === 'unknown'
  const fills = pending ? noEntryFills : entryFills
  const entryAllocation = allocation(draft.entries.map(entry => entry.share))
  const priced = draft.entries.flatMap(entry => {
    const share = shareOf(entry.share)
    const price = decimal(entry.price)
    return share !== null && (share === 0 || (price !== null && price > 0)) ? [share] : []
  })
  const pricedPercent = priced.length ? finite(priced.reduce((sum, share) => sum + share, 0)) : null
  const pricedCoverage = coverage(pricedPercent,
    entryAllocation.status === 'complete' && priced.length === draft.entries.length && pricedPercent !== null,
    entryAllocation.status === 'invalid')

  const size = decimal(draft.size)
  const lever = decimal(draft.leverage)
  const leverage = lever !== null && Number.isInteger(lever) && lever >= 1 ? lever : null
  // Margin-derived quantity requires the entire average. The editor's partial average is not enough.
  const canSize = size !== null && leverage !== null && (size === 0 || draft.sizingMode === 'quantity'
    || (entryAllocation.status === 'complete' && pricedCoverage.status === 'complete'))
  const sized = canSize ? positionSize(draft, leverage!) : null
  const validAverage = sized?.averageEntry !== null && sized?.averageEntry !== undefined
    && Number.isFinite(sized.averageEntry) && sized.averageEntry > 0
  const quantity = !canSize ? null : size === 0 ? 0
    : draft.sizingMode === 'margin' && !validAverage ? null : finite(sized?.quantity ?? NaN)
  const pricedQuantity = quantity !== null && pricedPercent !== null && pricedCoverage.status !== 'invalid'
    ? finite(quantity * (pricedPercent / 100)) : null

  function scenario(kind: ExitKind): PlannedExitSummary {
    let allocated = 0
    let covered = 0
    let knownAmount = 0
    let hasAmount = false
    let overflow = false
    let completeAllocation = entryAllocation.status === 'complete'
    let completeCoverage = pricedCoverage.status === 'complete' && leverage !== null
    let invalid = entryAllocation.status === 'invalid'
    let hasShare = false
    for (const entry of draft.entries) {
      const entryShare = shareOf(entry.share)
      if (entryShare === null) continue
      hasShare = true
      if (entryShare === 0) continue
      const exits = exitsOf(entry, kind)
      const exitAllocation = allocation(exits.map(exit => exit.share))
      allocated += entryShare / 100 * (exitAllocation.percent ?? 0)
      completeAllocation &&= exitAllocation.status === 'complete'
      invalid ||= exitAllocation.status === 'invalid'
      for (const exit of exits) {
        const exitShare = shareOf(exit.share)
        if (exitShare === null || exitShare === 0 || leverage === null) continue
        const estimate = exitEstimate(entry, exit, kind, draft.direction, leverage, quantity)
        if (estimate === null) { completeCoverage = false; continue }
        covered += entryShare / 100 * exitShare
        if (estimate.amount !== null) {
          knownAmount += estimate.amount
          hasAmount = true
        } else if (quantity !== null) overflow = true
      }
    }
    const allocatedPercent = hasShare ? finite(allocated) : null
    const coveredPercent = hasShare ? finite(covered) : null
    const allocationCoverage = coverage(allocatedPercent, completeAllocation && allocatedPercent !== null, invalid)
    const exitCoverage = coverage(coveredPercent,
      allocationCoverage.status === 'complete' && completeCoverage && coveredPercent !== null, invalid)
    const subtotal = hasAmount && !invalid && !overflow ? finite(knownAmount) : null
    return {
      allocation: allocationCoverage, coverage: exitCoverage, knownAmount: subtotal,
      totalAmount: exitCoverage.status === 'complete' ? subtotal : null,
    }
  }

  /**
   * The all-stops result with the same coverage rules, in exact arithmetic. An unfilled entry's stop counts only on the
   * loss side of its planned price, as before. A filled entry's priced stop counts wherever it sits, measured from the
   * entry's average fill when the execution reports one, otherwise from the planned price.
   */
  function stopScenario(): { summary: PlannedExitSummary; payoff: StopPayoff } {
    const size = canSize ? exactQuantity(draft, leverage!) : null
    const direction = exactInteger(draft.direction === 'long' ? 1 : -1)
    let allocated = 0
    let covered = 0
    let known = exactZero
    let hasAmount = false
    let completeAllocation = entryAllocation.status === 'complete'
    let completeCoverage = pricedCoverage.status === 'complete' && leverage !== null
    let invalid = entryAllocation.status === 'invalid'
    let hasShare = false
    const bases = new Set<'fill' | 'plan'>()
    let roundedUsed = false
    for (const entry of draft.entries) {
      const entryShare = shareOf(entry.share)
      if (entryShare === null) continue
      hasShare = true
      if (entryShare === 0) continue
      const exitAllocation = allocation(entry.stops.map(stop => stop.share))
      allocated += entryShare / 100 * (exitAllocation.percent ?? 0)
      completeAllocation &&= exitAllocation.status === 'complete'
      invalid ||= exitAllocation.status === 'invalid'
      const planned = exactPlan(entry.price)
      const filled = entryFilled(fills.filled, entry.id)
      const average = filled ? fills.averages.get(entry.id) ?? null : null
      const fromFill = average !== null && exactSignOf(average) > 0
      const reference = fromFill ? average : planned
      for (const stop of entry.stops) {
        const stopShare = shareOf(stop.share)
        if (stopShare === null || stopShare === 0 || leverage === null) continue
        const price = exactLevelPrice(entry, stop, 'stop', draft.direction, leverage)
        const lossSide = price !== null && planned !== null && exactSignOf(planned) > 0 &&
          exactSignOf(exactMul(direction, exactSub(planned, price))) > 0
        if (price === null || reference === null || planned === null || exactSignOf(planned) <= 0 || (!filled && !lossSide)) {
          completeCoverage = false
          continue
        }
        covered += entryShare / 100 * stopShare
        bases.add(fromFill ? 'fill' : 'plan')
        roundedUsed ||= fromFill && !!fills.rounded?.has(entry.id)
        if (size === null) continue
        const portion = exactDiv(exactMul(exactPlan(entry.share)!, exactPlan(stop.share)!), exactInteger(10_000))!
        known = exactAdd(known, exactMul(exactMul(size, portion), exactMul(direction, exactSub(price, reference))))
        hasAmount = true
      }
    }
    const allocatedPercent = hasShare ? finite(allocated) : null
    const coveredPercent = hasShare ? finite(covered) : null
    const allocationCoverage = coverage(allocatedPercent, completeAllocation && allocatedPercent !== null, invalid)
    const exitCoverage = coverage(coveredPercent,
      allocationCoverage.status === 'complete' && completeCoverage && coveredPercent !== null, invalid)
    // A rounded average is off by at most half a unit in the 28th place, so the stops' result can be off by at most the
    // quantity times that: within it, its sign is not known.
    const rounding = roundedUsed && hasAmount && size !== null &&
      exactSignOf(exactSub(exactDiv(exactAbs(size), exactInteger(2n * 10n ** 28n))!, exactAbs(known))) >= 0
    // While the fills load, no stop can be measured: the planned entry would be the wrong reference for a filled one.
    const payoff = hasAmount && !invalid && !pending && !rounding ? known : null
    const total = exitCoverage.status === 'complete' ? payoff : null
    const loss = payoff === null ? null : finite(-exactNumber(payoff) || 0)
    return {
      summary: { allocation: allocationCoverage, coverage: exitCoverage, knownAmount: loss, totalAmount: total === null ? null : loss },
      payoff: { known: payoff, total, empty: size !== null && exactSignOf(size) === 0, pending, rounding,
        basis: pending || bases.size === 0 ? null : bases.size > 1 ? 'mixed' : bases.has('fill') ? 'average fill' : 'planned entry' },
    }
  }

  const { summary: stops, payoff: stopPayoff } = stopScenario()
  const targets = scenario('target')
  // A result that is not a loss at the stops has no risk to measure reward against.
  const rewardToRisk = stops.totalAmount !== null && stops.totalAmount > 0 && targets.totalAmount !== null
    ? finite(targets.totalAmount / stops.totalAmount) : null
  const referenceAmount = riskReference ? decimal(riskReference.amount) : null
  const reference = riskReference?.name.trim() && riskReference.quote === units.quote
    && referenceAmount !== null && referenceAmount > 0
    ? { name: riskReference.name.trim(), amount: referenceAmount } : null
  // Stops that lock in a result risk nothing of the balance: a known zero, not unknown.
  const riskSharePercent = reference !== null && stops.totalAmount !== null && stopPayoff.total !== null
    ? exactSignOf(stopPayoff.total) >= 0 ? 0 : finite(stops.totalAmount / reference.amount * 100) : null
  return { quantity, entryAllocation, pricedCoverage, pricedQuantity, stops, stopPayoff, targets, rewardToRisk, reference, riskSharePercent }
}

const payoffMoney = numberFormat({ maximumFractionDigits: 2 })

/**
 * "+5 USDC locked in", "−5 USDC at risk" or "0 USDC break-even": rounded once from the exact value for display, so a
 * small result reads "< 0.01" and the sign is never rounded away.
 */
export function signedPayoffText(value: Exact, units: SizeUnits, empty = false) {
  // Without a position there is nothing to lock in or put at risk.
  if (empty && exactSignOf(value) === 0) return `0 ${units.quote}`
  const sign = exactSignOf(value)
  const text = exactFixed(value, 2).replace(/^-/, '')
  const amount = sign !== 0 && text === '0' ? '< 0.01' : payoffMoney.format(text as Intl.StringNumericLiteral)
  return sign > 0 ? `+${amount} ${units.quote} locked in` : sign < 0 ? `\u2212${amount} ${units.quote} at risk` : `0 ${units.quote} break-even`
}

const gapPercent = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 })

/**
 * What keeps the planned stops from covering the whole position, by the summary's coverage rule:
 * entry shares total 100%, every entry with a share is priced, each entry's stop shares total 100%,
 * and every stop with a share is priced on the loss side at a whole leverage. Null when coverage is
 * complete, which sizing by risk requires: otherwise part of the position has no bounded loss.
 * The size does not matter to coverage.
 */
export function stopCoverageGap(draft: Pick<PlayDraft, 'entries' | 'direction' | 'leverage'>): string | null {
  const summary = positionRiskSummary({ ...draft, size: '', sizingMode: 'quantity' })
  const coverage = summary.stops.coverage
  if (coverage.status === 'complete') return null
  if (coverage.status === 'invalid' || summary.entryAllocation.status === 'invalid') return 'Entry or stop shares are not valid or exceed 100%'
  // As in the summary, a blank leverage is unknown rather than 1×.
  if (draft.leverage.trim() === '') return 'The leverage is not set'
  const lever = decimal(draft.leverage)
  if (lever === null || !Number.isInteger(lever) || lever < 1) return 'The leverage is not a whole number'
  const entries = summary.entryAllocation
  if (entries.status !== 'complete') return entries.percent === null ? 'Entry shares are missing' : `Entry shares total ${gapPercent.format(entries.percent)}%, not 100%`
  const priced = summary.pricedCoverage
  if (priced.status !== 'complete' && priced.percent !== null) return `${gapPercent.format(100 - priced.percent)}% of the position has no entry price`
  const wrong = draft.entries.some(entry => {
    // Read as the server's side check reads it, like every wrongSide caller that names a wrong stop.
    const price = serverPositivePlanNumber(entry.price)
    return planShare(entry.share) !== 0 && entry.stops.some(stop => planShare(stop.share) !== 0 && wrongSide(price, stop, 'stop', draft.direction))
  })
  if (wrong) return 'A stop sits on the wrong side of its entry'
  return coverage.percent === null ? 'The stop coverage is unknown' : `${gapPercent.format(Math.max(0, 100 - coverage.percent))}% of the position has no priced stop`
}
