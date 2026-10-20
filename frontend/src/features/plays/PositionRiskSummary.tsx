import type { PlayDraft } from './draft'
import {
  noEntryFills, positionRiskSummary, signedPayoffText, type EntryFills, type PayoffBasis, type PlannedExitSummary, type RiskCoverage, type RiskReference,
  type StopPayoff,
} from './position-risk-summary'
import { defaultSizeUnits, formatMoney, formatQuantity, formatRewardToRisk, type SizeUnits } from './sizing'
import { InfoTip } from '@/components/ui/info-tip'
import { exactSignOf } from '@/lib/decimal'
import './position-risk-summary.css'

export interface PositionRiskSummaryProps {
  draft: PlayDraft
  units?: SizeUnits
  /** Explicit named, positive reference in `units.quote`. Never inferred from margin or budget. */
  riskReference?: RiskReference | null
  /** Filled entries and their average fills, or `'unknown'` while they load; the all-stops result is measured from them. */
  fills?: EntryFills | 'unknown'
}

const percent = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 })
const percentText = (value: number) => value > 0 && value < 0.01 ? '< 0.01%' : `${percent.format(value)}%`
const moneyText = (value: number, units: SizeUnits) =>
  value > 0 && value < 0.01 ? `< 0.01 ${units.quote}` : formatMoney(value, units)
const coverageText = (value: RiskCoverage) =>
  `${value.percent === null ? 'Unknown' : percentText(value.percent)} · ${value.status}`

const basisText: Record<PayoffBasis, string> = {
  'average fill': 'From the average fill',
  'planned entry': 'From the planned entry',
  mixed: 'From average fills and planned entries',
}

/** The all-stops row: what every stop filling would lock in or put at risk, and what it is measured from. */
function StopOutcome({ value, coverage, units }: { value: StopPayoff; coverage: RiskCoverage; units: SizeUnits }) {
  // No position: the row reads 0 as before, with no reference to measure from.
  const basis = !value.empty && value.basis && basisText[value.basis]
  if (value.pending) return <>Unknown<small>Waiting for the entries' fills</small></>
  if (value.rounding) return <>Unknown<small>Within the rounding of the reported average fill</small></>
  if (value.total !== null) return <>{signedPayoffText(value.total, units, value.empty)}{basis && <small>{basis}</small>}</>
  if (coverage.status === 'invalid') return <>Unknown<small>Invalid allocation</small></>
  return <>{value.known === null ? 'Unknown' : `Known ${signedPayoffText(value.known, units, value.empty)}`}
    <small>Total unknown{basis && ` · ${basis.toLowerCase()}`}</small></>
}

function Outcome({ value, units }: { value: PlannedExitSummary; units: SizeUnits }) {
  if (value.totalAmount !== null) return <>{moneyText(value.totalAmount, units)}</>
  if (value.coverage.status === 'invalid') return <>Unknown<small>Invalid allocation</small></>
  return <>{value.knownAmount === null ? 'Unknown' : `Known ${moneyText(value.knownAmount, units)}`}
    <small>Total unknown</small></>
}

/** Append inside the approved bottom PositionSummary. This component owns no account/API reads. */
export function PositionRiskSummary({ draft, units = defaultSizeUnits, riskReference, fills = noEntryFills }: PositionRiskSummaryProps) {
  const summary = positionRiskSummary(draft, units, riskReference, fills)
  const payoff = summary.stopPayoff.total ?? summary.stopPayoff.known
  const tone = payoff === null || summary.stopPayoff.empty ? undefined : exactSignOf(payoff) > 0 ? 'gain' : exactSignOf(payoff) < 0 ? 'loss' : undefined
  // Stops that lock in a result or break even on a sized position: there is no risk to measure reward against.
  const lockedIn = summary.stopPayoff.total !== null && exactSignOf(summary.stopPayoff.total) >= 0 && (summary.quantity ?? 0) > 0
  const coverages: Array<[string, RiskCoverage, string]> = [
    ['Entry allocation', summary.entryAllocation, 'Entry quantity shares of the full position.'],
    ['Stop allocation', summary.stops.allocation, 'Allocated stop shares weighted by actual entry shares, even when prices are missing.'],
    ['Target allocation', summary.targets.allocation, 'Allocated target shares weighted by actual entry shares, even when prices are missing.'],
    ['Stop coverage', summary.stops.coverage, 'Full-position quantity with priced entries and planned stops on their loss side, or anywhere once the entry has filled. Not observed stop protection.'],
    ['Target coverage', summary.targets.coverage, 'Full-position quantity with priced entries and correctly sided planned targets. Not executed exits.'],
  ]
  return <div className="position-risk-summary" role="group" aria-label="Planned position money summary">
    <p className="position-risk-basis">Planned, before fees, slippage and funding
      <InfoTip label="About planned estimates">Planned estimates assume fills at the stated prices. Stops and targets are separate scenarios.
        Actual loss can exceed planned loss. A filled entry's stops are measured from its average fill.</InfoTip></p>
    <dl className="position-risk-metrics">
      <div><dt>All stops</dt><dd data-tone={tone} data-incomplete={summary.stopPayoff.total === null}>
        <StopOutcome value={summary.stopPayoff} coverage={summary.stops.coverage} units={units} />
      </dd></div>
      <div><dt>All-targets planned gain</dt><dd data-tone="gain" data-incomplete={summary.targets.totalAmount === null}>
        <Outcome value={summary.targets} units={units} />
      </dd></div>
      <div><dt>Planned R:R</dt><dd data-incomplete={summary.rewardToRisk === null && !lockedIn}
        data-warning={summary.rewardToRisk !== null && summary.rewardToRisk < 1}>
        {summary.rewardToRisk !== null ? formatRewardToRisk(summary.rewardToRisk) : lockedIn ? <>None<small>No loss at the stops</small></> : 'Unknown'}
      </dd></div>
      <div><dt>{summary.reference ? `Risk / ${summary.reference.name}` : 'Risk share'}</dt>
        <dd data-incomplete={summary.riskSharePercent === null}>
          {summary.riskSharePercent === null ? 'Unknown' : percentText(summary.riskSharePercent)}
          {!summary.reference && <small>Needs a named positive reference in {units.quote}</small>}
        </dd>
      </div>
    </dl>
    <dl className="position-risk-coverage">
      <div><dt>Priced quantity</dt><dd>
        {summary.pricedQuantity === null ? 'Unknown' : formatQuantity(summary.pricedQuantity, units)}
        <small>{summary.pricedCoverage.percent === null ? 'Unknown' : percentText(summary.pricedCoverage.percent)}
          {' '}of full position · {summary.pricedCoverage.status}</small>
      </dd></div>
      {coverages.map(([label, value, description]) => <div key={label} title={description}><dt>{label}</dt>
        <dd data-warning={value.status !== 'complete'}>{coverageText(value)}</dd>
      </div>)}
    </dl>
  </div>
}
