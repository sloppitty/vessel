import type { NextAction, PreflightCheck, PreflightCode } from '@/api/plays'
import type { SizingDocument } from '@/api/sizing'
import { exactAdd, exactCompare, exactDecimal, exactInteger, exactZero, type Exact } from '@/lib/decimal'
import type { PlayDraft } from './draft'
import { levelPrice } from './levels'
import { positivePlanNumber } from './numbers'
import { stopsPastLiquidation } from './planChecks'
import { stopCoverageGap, type PositionRisk } from './position-risk-summary'

/*
 * The pre-flight at Plan it (phase 1, 1.5): a non-blocking checklist of what the plan already says, recorded with
 * revision 1. Nothing here blocks planning; "unknown" means an input is missing, never that a check passed or failed.
 */

export const preflightLabels: Record<PreflightCode, string> = {
  'priced-stops': 'A priced stop for every priced entry',
  allocations: 'Entry, stop and target shares each total 100%',
  thesis: 'Thesis and invalidation are written',
  risk: 'Risk within the sizing setting at the current ladder level',
  liquidation: 'The estimated liquidation lies beyond every stop',
}

export interface PreflightInput {
  draft: Pick<PlayDraft, 'entries' | 'direction' | 'leverage' | 'notes'>
  /** The Summary's position risk, measured from the same balance the size suggestion uses. */
  risk: Pick<PositionRisk, 'riskSharePercent'> | null
  sizing: SizingDocument | null
  /** The estimated liquidation price, or null when it cannot be estimated. */
  liquidation: number | null
}

const hundred = exactInteger(100)
const total = (shares: readonly string[]): Exact | null => shares.reduce<Exact | null>((sum, share) => {
  const value = exactDecimal(share.trim())
  return sum === null || value === null ? null : exactAdd(sum, value)
}, exactZero)
const isHundred = (shares: readonly string[]) => { const sum = total(shares); return sum !== null && exactCompare(sum, hundred) === 0 }

/** Every check, in display order, as it stands for this plan now. */
export function preflightChecks({ draft, risk, sizing, liquidation }: PreflightInput): PreflightCheck[] {
  const leverage = positivePlanNumber(draft.leverage) ?? 1
  const outcome = (code: PreflightCode, value: PreflightCheck['outcome']): PreflightCheck => ({ code, outcome: value })
  const allocations = isHundred(draft.entries.map(entry => entry.share)) && draft.entries.every(entry =>
    (entry.stops.length === 0 || isHundred(entry.stops.map(stop => stop.share))) &&
    (entry.targets.length === 0 || isHundred(entry.targets.map(target => target.share))))
  // The risk the Summary shows against the most the setting allows at the current ladder level.
  const setting = sizing ? Number(sizing.settings.riskPercent) * Number(sizing.exposure.multiplier) : null
  const share = risk?.riskSharePercent ?? null
  const riskOutcome = setting === null || !Number.isFinite(setting) || share === null ? 'unknown' : share <= setting + 1e-9 ? 'passed' : 'skipped'
  // Liquidation can be judged only against priced stops.
  const pricedStops = draft.entries.some(entry => entry.stops.some(stop =>
    levelPrice(positivePlanNumber(entry.price), stop, 'stop', draft.direction, leverage) !== null))
  const liquidationOutcome = liquidation === null || !pricedStops ? 'unknown'
    : stopsPastLiquidation(draft.entries, draft.direction, leverage, liquidation).length === 0 ? 'passed' : 'skipped'
  return [
    outcome('priced-stops', stopCoverageGap(draft) === null ? 'passed' : 'skipped'),
    outcome('allocations', allocations ? 'passed' : 'skipped'),
    outcome('thesis', draft.notes.thesis.trim() && draft.notes.invalidation.trim() ? 'passed' : 'skipped'),
    outcome('risk', riskOutcome),
    outcome('liquidation', liquidationOutcome),
  ]
}

/**
 * The thesis with a recent next action appended as an if-then for the owner to complete, naming its source Play:
 * `If …, then <next action> (from <title>)`.
 */
export function withIfThen(thesis: string, action: Pick<NextAction, 'nextAction' | 'title'>) {
  const line = `If …, then ${action.nextAction.trim()} (from ${action.title.trim() || 'an earlier Play'})`
  return thesis.trim() ? `${thesis.trimEnd()}\n\n${line}` : line
}

/** Plan it plans at once when every check passed; otherwise the owner sees the checklist first. */
export const preflightClear = (checks: readonly PreflightCheck[]) => checks.every(check => check.outcome === 'passed')
