import type { PlayDraft } from './draft'
import { exitsOf, type ExitKind } from './levels'
import { finitePlanNumber as decimal, planShare, positivePlanNumber } from './numbers'
import { wrongSide } from './planChecks'
import { defaultSizeUnits, exitEstimate, positionSize, type SizeUnits } from './sizing'

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

export interface PositionRisk {
  quantity: number | null
  entryAllocation: RiskCoverage
  pricedCoverage: RiskCoverage
  pricedQuantity: number | null
  stops: PlannedExitSummary
  targets: PlannedExitSummary
  rewardToRisk: number | null
  reference: { name: string; amount: number } | null
  riskSharePercent: number | null
}

const finite = (value: number) => Number.isFinite(value) ? value : null
// Match the plan's 64-character share limit; reject longer drafts, never truncate.
const shareDigits = 64
const shareScale = 10n ** BigInt(shareDigits)
const fullShare = 100n * shareScale
const exactShare = (value: string) => {
  const text = value.trim()
  if (text.length > shareDigits || decimal(text) === null) return null
  const [whole = '', fraction = ''] = text.split('.')
  return BigInt(whole + fraction.padEnd(shareDigits, '0'))
}
const shareOf = (value: string) => {
  const share = exactShare(value)
  return share !== null && share <= fullShare ? Number(value) : null
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

export function positionRiskSummary(draft: SummaryDraft, units: SizeUnits = defaultSizeUnits,
  riskReference?: RiskReference | null): PositionRisk {
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

  const stops = scenario('stop')
  const targets = scenario('target')
  const rewardToRisk = stops.totalAmount !== null && stops.totalAmount > 0 && targets.totalAmount !== null
    ? finite(targets.totalAmount / stops.totalAmount) : null
  const referenceAmount = riskReference ? decimal(riskReference.amount) : null
  const reference = riskReference?.name.trim() && riskReference.quote === units.quote
    && referenceAmount !== null && referenceAmount > 0
    ? { name: riskReference.name.trim(), amount: referenceAmount } : null
  const riskSharePercent = reference !== null && stops.totalAmount !== null
    ? finite(stops.totalAmount / reference.amount * 100) : null
  return { quantity, entryAllocation, pricedCoverage, pricedQuantity, stops, targets, rewardToRisk, reference, riskSharePercent }
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
  const lever = decimal(draft.leverage)
  if (lever === null || !Number.isInteger(lever) || lever < 1) return 'The leverage is not a whole number'
  const entries = summary.entryAllocation
  if (entries.status !== 'complete') return entries.percent === null ? 'Entry shares are missing' : `Entry shares total ${gapPercent.format(entries.percent)}%, not 100%`
  const priced = summary.pricedCoverage
  if (priced.status !== 'complete' && priced.percent !== null) return `${gapPercent.format(100 - priced.percent)}% of the position has no entry price`
  const wrong = draft.entries.some(entry => {
    const price = positivePlanNumber(entry.price)
    return planShare(entry.share) !== 0 && entry.stops.some(stop => planShare(stop.share) !== 0 && wrongSide(price, stop, 'stop', draft.direction))
  })
  if (wrong) return 'A stop sits on the wrong side of its entry'
  return coverage.percent === null ? 'The stop coverage is unknown' : `${gapPercent.format(Math.max(0, 100 - coverage.percent))}% of the position has no priced stop`
}
