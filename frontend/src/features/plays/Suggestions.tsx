import { useId, useState, type FormEvent, type ReactNode } from 'react'
import { Check, ChevronDown, Lightbulb, X } from 'lucide-react'
import { riskPercentRange, type SizingDocument } from '@/api/sizing'
import { Button } from '@/components/ui/button'
import { InfoTip } from '@/components/ui/info-tip'
import { Input } from '@/components/ui/input'
import type { PlayDraft } from './draft'
import { formatDraggedPrice } from './levels'
import { errorMessage } from '@/api/errors'
import { formatMoney, formatQuantity, formatRewardToRisk, type SizeUnits } from './sizing'
import { exposureLabels, suggestionCount, type PlanSuggestions } from './suggestions'

const number = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 })
/** "1.25" from an API decimal string, or null when it is missing or not a number. */
const decimal = (value: string | null) => value === null || !Number.isFinite(Number(value)) ? null : number.format(Number(value))
/** Risk percentages keep a third decimal, e.g. 0.625% at half of 1.25%. */
const riskNumber = new Intl.NumberFormat(undefined, { maximumFractionDigits: 3 })
const percentOf = (fraction: string | null) => fraction === null ? null : decimal(String(Number(fraction) * 100))

/** One suggestion: what changes, the Accept button and why, in its own row of the panel. */
function SuggestionRow({ title, value, current, label, onAccept, testId, children }: {
  title: string
  value: ReactNode
  /** The field's current value, shown struck through beside the suggestion. */
  current?: string | null
  /** What Accept does, for assistive technology, e.g. "Accept suggested size". */
  label: string
  /** Without it the row only informs. */
  onAccept?: (() => void) | undefined
  testId: string
  children: ReactNode
}) {
  return <li className="suggestion-row" data-testid={testId}>
    <div className="suggestion-row-head">
      <span className="suggestion-row-title">{title}</span>
      <span className="suggestion-row-value">
        {current ? <><s>{current}</s> → </> : null}<strong>{value}</strong>
      </span>
      {onAccept && <Button type="button" variant="outline" size="sm" aria-label={label} onClick={onAccept}>Accept</Button>}
    </div>
    <p className="suggestion-row-why">{children}</p>
  </li>
}

/**
 * Sizing suggestions in one yellow block above the workspace, kept apart from the entered plan. Each
 * row says what would change and why; nothing applies until the owner accepts it. The record and the
 * risk setting the suggestions come from sit beside them, with the rules behind them.
 */
export function SuggestionsPanel({ plan, sizing, draft, leverage, units, onSize, onLevel, onLeverage, onRiskChange, defaultOpen = false }: {
  plan: PlanSuggestions
  sizing: SizingDocument
  draft: Pick<PlayDraft, 'sizingMode' | 'direction'>
  leverage: number
  units: SizeUnits
  onSize: (value: string) => void
  onLevel: (entryId: string, kind: 'stop' | 'target', levelId: string, value: string) => void
  onLeverage: (leverage: number) => void
  onRiskChange: (riskPercent: string) => Promise<void>
  defaultOpen?: boolean
}) {
  const id = useId()
  const [open, setOpen] = useState(defaultOpen)
  const count = suggestionCount(plan)
  const { limits, exposure, record } = sizing
  const reduced = exposure.level !== 'full'
  const formatSize = (value: string) => draft.sizingMode === 'margin' ? formatMoney(Number(value), units) : formatQuantity(Number(value), units)
  const level = (unit: 'price' | 'percent', value: string, price: number) => unit === 'percent' ? `${value}% (≈ ${formatDraggedPrice(price)})` : value
  const maxStop = decimal(limits.maxStopPercent)
  const minRewardRisk = Number(limits.minRewardRisk)
  const riskRule = `${decimal(sizing.settings.riskPercent)}% risk${reduced ? ` × ${percentOf(exposure.multiplier)}% exposure` : ''}`

  const headline = count === 0 ? 'Plan within your limits'
    : [plan.size && plan.size.overBudget === null ? `Size ${formatSize(plan.size.value)}` : null,
      ...plan.stops.map(stop => `${stop.entryName} stop ${stop.value}`), ...plan.targets.map(target => `${target.entryName} target ${target.value}`),
      plan.leverage ? `${plan.leverage.leverage}×` : null].filter(Boolean).join(' · ')

  return <section className="sizing-suggestions" data-open={open} data-testid="suggestions-panel" aria-label="Sizing suggestions">
    <button type="button" className="sizing-suggestions-summary" aria-expanded={open} aria-controls={`${id}-body`} onClick={() => setOpen(!open)}>
      <Lightbulb size={16} aria-hidden="true" />
      <span className="sizing-suggestions-title">
        <strong>Suggestions{count > 0 && <span className="sizing-suggestions-count">{count}</span>}
          <span className="sizing-suggestions-tag" title="From your risk setting and record. Nothing changes until you accept.">Estimates</span></strong>
      </span>
      {reduced && <span className="sizing-suggestions-exposure">{exposureLabels[exposure.level]}</span>}
      <span className="sizing-suggestions-headline">{headline}</span>
      <ChevronDown size={16} aria-hidden="true" className="sizing-suggestions-chevron" />
    </button>
    {open && <div id={`${id}-body`} className="sizing-suggestions-body">
      <div className="sizing-suggestions-list">
        <h3>For this plan</h3>
        {count === 0 && !plan.size && emptyReason(plan) && <p className="suggestion-empty" data-testid="suggestions-empty">{emptyReason(plan)}</p>}
        <ul>
          {plan.size && (plan.size.overBudget === null
            ? <SuggestionRow testId="size-suggestion" title="Size" label="Accept suggested size" value={formatSize(plan.size.value)}
              onAccept={() => onSize(plan.size!.value)}>
              Loses <strong>{formatMoney(plan.size.risk, units)}</strong> if every stop fills: {formatMoney(plan.balance!, units)} balance × {riskRule}
              {' '}= {riskNumber.format(plan.size.effectiveRiskPercent)}%. Each {units.base === 'units' ? 'unit' : units.base} loses {formatDraggedPrice(plan.lossPerUnit!)} at the stops,
              so the position is risk ÷ loss per unit{draft.sizingMode === 'margin' ? `, as margin at ${leverage}×` : ''}. Before fees.
            </SuggestionRow>
            : <SuggestionRow testId="size-suggestion" title="Size" label="Accept suggested size" value={`${formatMoney(plan.size.margin, units)} margin`}>
              Risking {formatMoney(plan.size.risk, units)} ({riskRule}) needs more margin than the {formatMoney(plan.size.overBudget, units)} budget at {leverage}×.
              {plan.leverage ? ' Raise the leverage below, or tighten the stops.' : ' Tighten the stops or raise the budget.'}
            </SuggestionRow>)}
          {plan.stops.map(stop => <SuggestionRow key={stop.levelId} testId="max-stop-suggestion" title={`${stop.entryName} · ${stop.label}`}
            label={`Accept suggested ${stop.entryName} ${stop.label}`} current={stop.current} value={level(stop.unit, stop.value, stop.price)}
            onAccept={() => onLevel(stop.entryId, 'stop', stop.levelId, stop.value)}>
            {number.format(stop.distancePercent)}% from the entry, past the {maxStop}% maximum.
            {' '}{limits.maxStopSource === 'averageGain'
              ? <>That is half your {decimal(record.averageGainPercent)}% average gain: Minervini cuts losses at about half the average gain, so one loss never undoes more than half a win.</>
              : <>Minervini's absolute maximum is 10% from the entry; his average loss is much smaller. Once you have five wins, the limit follows half your average gain.</>}
          </SuggestionRow>)}
          {plan.targets.map(target => <SuggestionRow key={target.levelId} testId="min-target-suggestion" title={`${target.entryName} · ${target.label}`}
            label={`Accept suggested ${target.entryName} ${target.label}`} current={target.current} value={level(target.unit, target.value, target.price)}
            onAccept={() => onLevel(target.entryId, 'target', target.levelId, target.value)}>
            Planned R:R is {formatRewardToRisk(target.ratio)}, below {formatRewardToRisk(minRewardRisk)}.
            {' '}{limits.minRewardRiskSource === 'battingAverage'
              ? <>Winning {percentOf(record.battingAverage)}% of your {record.source === 'activity' ? 'closed positions' : 'plays'}, you break even at {formatRewardToRisk(minRewardRisk)}; below it the record loses money.</>
              : <>Minervini aims for at least 2:1, so a 50% win rate still makes money. Moving the farthest target is one way; a tighter stop is another.</>}
          </SuggestionRow>)}
          {plan.leverage && <SuggestionRow testId="leverage-suggestion" title="Leverage" label="Accept suggested leverage"
            current={`${leverage}×`} value={`${plan.leverage.leverage}×`} onAccept={() => onLeverage(plan.leverage!.leverage)}>
            {plan.leverage.reason === 'budget' ? <>The margin does not fit the {formatMoney(plan.budget!, units)} budget at {leverage}×. {plan.leverage.leverage}× is the lowest that fits with the estimated liquidation past the stops.</>
              : plan.leverage.reason === 'liquidation' ? <>At {leverage}× the estimated liquidation is inside a stop, so the stop would never trigger. {plan.leverage.leverage}× keeps it past the stops.</>
              : <>Exposure is reduced after losses. Minervini gets off margin while trading is going badly, so this is the lowest leverage that still fits the budget.</>}
            {' '}Isolated margin, before fees and funding.
          </SuggestionRow>}
        </ul>
      </div>
      <RecordColumn sizing={sizing} onRiskChange={onRiskChange} />
    </div>}
  </section>
}

/** Why there is no size suggestion; a plan within its limits needs no reason, the headline says so. */
function emptyReason(plan: PlanSuggestions) {
  if (plan.balance === null) return 'Choose an account with a balance to size by risk.'
  if (plan.lossPerUnit === null) return 'Give each priced entry a stop on the right side to size by risk.'
  return null
}

/** The record and rules behind the suggestions, with the risk per trade editable in place. */
function RecordColumn({ sizing, onRiskChange }: { sizing: SizingDocument; onRiskChange: (riskPercent: string) => Promise<void> }) {
  const { record, exposure, limits } = sizing
  const decided = Math.min(record.decidedPlays, record.window)
  const activity = record.source === 'activity'
  const positions = record.closedPlays === 1 ? 'position' : 'positions'
  return <div className="sizing-suggestions-record" data-testid="track-record">
    <h3>Your record</h3>
    {activity && <p className="record-source-note" data-testid="record-source">
      <span className="record-source">From Activity</span>
      <span>Based on {record.closedPlays} closed {positions} from Activity — no closed play with a win or loss yet.</span>
      <InfoTip label="About the Activity record">
        {sizing.notice}
      </InfoTip>
    </p>}
    {record.decidedPlays === 0
      ? <p className="suggestion-empty">{record.closedPlays === 0 ? 'No closed plays yet.' : 'No wins or losses yet.'} Limits use the defaults until there are some.</p>
      : <dl className="record-figures">
        <div><dt>Win rate</dt><dd>{percentOf(record.battingAverage) ?? '—'}%<small>{record.wins} of last {decided}{activity ? ' positions' : ''}</small></dd></div>
        <div><dt>Win/loss ratio</dt><dd>{decimal(record.winLossRatio) ?? '—'}<small>avg +{decimal(record.averageGainPercent) ?? '—'}% / −{decimal(record.averageLossPercent) ?? '—'}%</small></dd></div>
        <div><dt>Break-even R:R</dt><dd>{record.breakEvenRewardRisk === null ? '—' : formatRewardToRisk(Number(record.breakEvenRewardRisk))}<small>at this win rate</small></dd></div>
      </dl>}
    <p className="record-exposure" data-reduced={exposure.level !== 'full' || undefined}>
      <strong>{exposure.level === 'full' ? 'Full size' : exposureLabels[exposure.level]}</strong> · {exposure.reason}
    </p>
    <div className="record-limits">
      <RiskSetting sizing={sizing} onRiskChange={onRiskChange} />
      <span>Effective <strong>{riskNumber.format(Number(limits.effectiveRiskPercent))}%</strong></span>
      <span>Max stop <strong>{decimal(limits.maxStopPercent)}%</strong></span>
      <span>Min R:R <strong>{formatRewardToRisk(Number(limits.minRewardRisk))}</strong></span>
    </div>
    <details className="record-rules">
      <summary>How these are worked out</summary>
      <ul>
        <li><strong>Size</strong>: risk a fixed share of the account balance per play (Minervini: about 1.25%, never above 2.5%). Position = amount at risk ÷ loss per unit at the stops.</li>
        <li><strong>Stops</strong>: never more than 10% from the entry; once you have five wins, no more than half your average gain.</li>
        <li><strong>Targets</strong>: at least 2:1, or the break-even R:R of your win rate once you have ten wins and losses, whichever is higher.</li>
        <li><strong>Exposure</strong>: size halves after three losses in a row and halves again after two more. It steps back up after two wins with a positive result since stepping down. Minervini gives no fixed counts; these are Vessel's.</li>
        <li><strong>Record</strong>: your last {record.window} closed plays with wins or losses, all enabled accounts, from linked fills: venue closed PnL less fees.
          Until a play has a win or loss, closed positions from Activity stand in, scored the same way without R multiples.</li>
      </ul>
      <p>Mechanical estimates before fees, funding and slippage, not advice.</p>
    </details>
  </div>
}

function RiskSetting({ sizing, onRiskChange }: { sizing: SizingDocument; onRiskChange: (riskPercent: string) => Promise<void> }) {
  const id = useId()
  const [editing, setEditing] = useState(false)
  const [value, setValue] = useState('')
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const risk = Number(sizing.settings.riskPercent)
  const typed = Number(value)
  const valid = /^\d+(\.\d+)?$/.test(value.trim()) && typed >= riskPercentRange.min && typed <= riskPercentRange.max

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!valid) { setError(`${riskPercentRange.min} to ${riskPercentRange.max}%`); return }
    setSaving(true)
    try {
      await onRiskChange(value.trim())
      setEditing(false)
      setError('')
    } catch (cause) {
      setError(errorMessage(cause, 'The change could not be completed. Try again.'))
    } finally {
      setSaving(false)
    }
  }

  return editing ? <form className="track-record-risk" onSubmit={event => { void save(event) }} noValidate
    onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); setEditing(false); setError('') } }}>
    <label htmlFor={`${id}-risk`}>Risk</label>
    <Input id={`${id}-risk`} type="number" step="any" min={riskPercentRange.min} max={riskPercentRange.max} autoFocus value={value}
      aria-label="Risk per trade (%)" aria-invalid={!!error || undefined} aria-describedby={error || typed > riskPercentRange.warnAbove ? `${id}-risk-note` : undefined}
      disabled={saving} onChange={event => { setValue(event.target.value); setError('') }} />
    <span aria-hidden="true">%</span>
    <Button type="submit" variant="ghost" size="icon-sm" aria-label="Save risk per trade" disabled={saving}><Check size={13} aria-hidden="true" /></Button>
    <Button type="button" variant="ghost" size="icon-sm" aria-label="Cancel" disabled={saving} onClick={() => { setEditing(false); setError('') }}><X size={13} aria-hidden="true" /></Button>
    {(error || typed > riskPercentRange.warnAbove) && <small id={`${id}-risk-note`} className="track-record-note" role={error ? 'alert' : undefined}>
      {error || `Above ${riskPercentRange.warnAbove}%`}</small>}
  </form> : <button type="button" className="track-record-edit" data-warning={risk > riskPercentRange.warnAbove || undefined}
    aria-label={`Risk per trade ${decimal(sizing.settings.riskPercent)}%. Edit`}
    title={risk > riskPercentRange.warnAbove ? `Above ${riskPercentRange.warnAbove}% per trade` : 'Risk per trade, as a share of the account balance'}
    onClick={() => { setValue(sizing.settings.riskPercent); setError(''); setEditing(true) }}>
    Risk {decimal(sizing.settings.riskPercent)}% per play
  </button>
}
