import { useId } from 'react'
import { Plus, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { createExit, type DraftEntry, type DraftExit, type PlayDraft } from './draft'
import { exitsOf, formatDraggedPrice, levelPrice, priceMovePercent, type ExitKind } from './levels'
import { keptStop, wrongSide } from './planChecks'
import { planInputType, positivePlanNumber, serverPositivePlanNumber } from './numbers'
import { defaultSizeUnits, estimatedLiquidation, exitEstimate, formatMoney, type SizeUnits } from './sizing'
import { entryFieldState, exitFieldState, planTextMarks, useStoredPlanText } from './stored-plan-text'

function LevelUnits({ value, label, onChange }: {
  value: DraftExit['unit']
  label: string
  onChange: (unit: DraftExit['unit']) => void
}) {
  return <div className="level-unit-switch segmented" role="group" aria-label={label}>
    {(['price', 'percent'] as const).map(unit => <button key={unit} type="button" aria-pressed={unit === value}
      aria-label={unit === 'price' ? 'Price' : '% return at leverage'}
      onClick={() => { if (unit !== value) onChange(unit) }}>
      {unit === 'price' ? '$' : '%'}
    </button>)}
  </div>
}

const percent = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 })

const copy = {
  stop: { legend: 'Planned stops', singular: 'stop' },
  target: { legend: 'Planned targets', singular: 'target' },
} as const

/** Stops or targets of one entry. Each closes a share of the entry; percentages are returns at the play's leverage. */
function ExitList({ entry, kind, direction, leverage, onChange, prefix, liquidation, suggested, quantity, units, filled = false }: {
  entry: DraftEntry
  kind: ExitKind
  direction: PlayDraft['direction']
  leverage: number
  onChange: (entry: DraftEntry) => void
  prefix: string
  /** The plan's estimated liquidation price; stops past it are flagged. */
  liquidation: number | null
  /** Levels with a suggestion waiting in the suggestions panel. */
  suggested: ReadonlySet<string> | null
  quantity: number | null
  units: SizeUnits
  /** The entry has filled: its stops may sit at or beyond it (break-even or profit). */
  filled?: boolean
}) {
  const exits = exitsOf(entry, kind)
  const text = copy[kind]
  const set = (next: DraftExit[]) => onChange(kind === 'stop' ? { ...entry, stops: next } : { ...entry, targets: next })
  const update = (id: string, change: Partial<DraftExit>) => set(exits.map(exit => exit.id === id ? { ...exit, ...change } : exit))
  const stored = useStoredPlanText()
  const entryPrice = positivePlanNumber(entry.price)
  // The side check reads stored text as the server does, so a stored 1e2 entry still refuses a long stop at 105.
  const sideEntryPrice = serverPositivePlanNumber(entry.price)
  // Single stops keep the short "stop" names; numbering starts once there are several.
  const label = (index: number) => exits.length > 1 || kind === 'target' ? `${kind} ${index + 1}` : kind

  return <fieldset className={kind === 'stop' ? 'exit-list exit-stops' : 'exit-list exit-targets'}>
    <legend className="sr-only">{text.legend}</legend>
    <div className="exit-list-heading">
      <span>{kind === 'stop' ? 'Stops' : 'Targets'}</span>
      <Button type="button" variant="ghost" size="icon-sm" className="add-target" aria-label={`Add ${text.singular} to ${entry.name}`} title={`Add ${text.singular}`}
        onClick={() => set([...exits, createExit(exits.length ? '' : '100')])}><Plus size={13} aria-hidden="true" /></Button>
    </div>
    {exits.map((exit, index) => {
      const estimate = exitEstimate(entry, exit, kind, direction, leverage, quantity)
      const resolved = exit.unit === 'percent' ? levelPrice(entryPrice, exit, kind, direction, leverage) : null
      const percentValue = exit.unit === 'percent' ? positivePlanNumber(exit.value) : null
      const move = estimate?.move ?? (percentValue !== null ? priceMovePercent(percentValue, leverage) : null)
      const outcome = kind === 'stop' ? 'loss' : 'gain'
      // A stop kept unchanged from the saved plan still saves, so beyond an unfilled entry it is a warning, not an error.
      const beyond = wrongSide(sideEntryPrice, exit, kind, direction, filled)
      const kept = beyond && kind === 'stop' && keptStop(entry, exit, stored)
      const wrong = beyond && !kept
      const stopPrice = kind === 'stop' ? levelPrice(entryPrice, exit, kind, direction, leverage) : null
      const marked = suggested?.has(exit.id) ?? false
      const pastLiquidation = !wrong && stopPrice !== null && liquidation !== null && (direction === 'long' ? stopPrice <= liquidation : stopPrice >= liquidation)
      // Text the server would refuse is an error with its reason; stored text from before the rule, a warning.
      const valueMarks = planTextMarks(exitFieldState(entry, kind, index, exit, 'value', stored), exit.value)
      const shareMarks = planTextMarks(exitFieldState(entry, kind, index, exit, 'share', stored), exit.share)
      return <div key={exit.id} className="exit-row" role="group" aria-label={`${entry.name} ${label(index)}`}>
        <LevelUnits label={`${entry.name} ${label(index)} units`} value={exit.unit}
          onChange={unit => update(exit.id, { unit, value: '' })} />
        <Input id={`${prefix}-${exit.id}-value`} {...planInputType(exit.value)} step="any" min={0} className={kind === 'stop' ? 'exit-value-stop' : 'exit-value-target'}
          placeholder={exit.unit === 'price' ? '$' : kind === 'stop' ? 'Loss %' : 'Gain %'}
          aria-label={`${entry.name} planned ${label(index)} ${exit.unit === 'price' ? 'price (quote units)' : `return at ${leverage}× leverage (%)`}`}
          aria-invalid={wrong || valueMarks?.invalid || undefined} data-warning={pastLiquidation || kept || valueMarks?.warning || undefined}
          data-suggested={marked || undefined}
          title={valueMarks ? valueMarks.title : wrong ? `A ${direction} ${kind} goes ${(kind === 'target') === (direction === 'long') ? 'above' : 'below'} the entry price`
            : kept ? `Saved beyond the entry while it had a fill; it still saves unchanged. Until the entry fills again, a changed ${direction} stop goes ${direction === 'long' ? 'below' : 'above'} it.`
            : pastLiquidation ? `Past the estimated liquidation price of ${formatDraggedPrice(liquidation!)}; it would not trigger first`
            : marked ? 'A suggestion for this level is in Suggestions above the chart.' : undefined}
          value={exit.value} onChange={event => update(exit.id, { value: event.target.value })} />
        <Input id={`${prefix}-${exit.id}-share`} {...planInputType(exit.share)} step="any" min={0} max={100} placeholder="Share %" className="exit-share"
          aria-label={`${entry.name} ${label(index)} share (%)`} value={exit.share}
          aria-invalid={shareMarks?.invalid || undefined} data-warning={shareMarks?.warning || undefined} title={shareMarks?.title}
          onChange={event => update(exit.id, { share: event.target.value })} />
        <Button type="button" variant="ghost" size="icon-sm" className="remove-target" aria-label={`Remove ${entry.name} ${label(index)}`}
          onClick={() => set(exits.filter(current => current.id !== exit.id))}>
          <X aria-hidden="true" size={14} />
        </Button>
        {move !== null && <p className="level-resolved">
          <span>{resolved === null ? `${formatDraggedPrice(move)}% price move (1×)` : `≈ ${formatDraggedPrice(resolved)} · ${formatDraggedPrice(move)}% price move (1×)`}</span>
          {estimate && <span title="Estimated if this exit fills at its planned price, for its share of the entry; before fees, funding and slippage.">
            {formatDraggedPrice(estimate.returnPercent)}% {outcome} at {leverage}×
            {estimate.amount !== null && <> · Est. {outcome} {formatMoney(estimate.amount, units)}</>}
          </span>}
        </p>}
      </div>
    })}
  </fieldset>
}

export function EntryForm({ entry, onChange, idPrefix, direction = 'long', leverage = 1, shareLocked = false, maxLeverage = null, maintenanceMargin = null, liquidation = null, suggested = null, quantity = null, units = defaultSizeUnits, filled = false }: {
  entry: DraftEntry
  onChange: (entry: DraftEntry) => void
  idPrefix?: string
  direction?: PlayDraft['direction']
  /** The play's leverage; percentage levels are returns on margin at it. */
  leverage?: number
  /** A single entry takes the whole position, so its share cannot change. */
  shareLocked?: boolean
  /** Venue maximum for the contract, for the liquidation estimate; null when unknown. */
  maxLeverage?: number | null
  /** Maintenance margin fraction the venue states for the contract; null assumes one from the maximum leverage. */
  maintenanceMargin?: number | null
  /** The whole plan's estimated liquidation price, which stops are checked against. */
  liquidation?: number | null
  /** Stops and targets with a suggestion waiting in the suggestions panel; they get a yellow marker. */
  suggested?: ReadonlySet<string> | null
  /** Full-position quantity, already including leverage; null until the size and average entry are known. */
  quantity?: number | null
  units?: SizeUnits
  /** The entry has a linked or manual fill: its stops may sit at or beyond it, as the server allows. */
  filled?: boolean
}) {
  const generatedId = useId()
  const prefix = idPrefix ?? generatedId
  // With several entries this is the entry on its own; a single entry is the whole plan.
  const own = estimatedLiquidation([{ ...entry, share: '100' }], direction, leverage, maxLeverage, maintenanceMargin)
  const entryPrice = positivePlanNumber(entry.price)
  const stored = useStoredPlanText()
  const shareMarks = shareLocked ? null : planTextMarks(entryFieldState(entry, 'share', stored), entry.share)
  const priceMarks = planTextMarks(entryFieldState(entry, 'price', stored), entry.price)

  return <div className="entry-fields">
    <label htmlFor={`${prefix}-share`}>
      <span>Share %</span>
      <Input id={`${prefix}-share`} {...planInputType(shareLocked ? '100' : entry.share)} step="any" min={0} max={100}
        aria-label={`${entry.name} quantity share (%)`} value={shareLocked ? '100' : entry.share} disabled={shareLocked}
        aria-invalid={shareMarks?.invalid || undefined} data-warning={shareMarks?.warning || undefined}
        title={shareLocked ? 'A single entry takes the whole position. Add an entry to split it.' : shareMarks?.title}
        onChange={event => onChange({ ...entry, share: event.target.value })} />
    </label>
    <label htmlFor={`${prefix}-price`}>
      <span>Entry price</span>
      <Input id={`${prefix}-price`} {...planInputType(entry.price)} step="any" min={0}
        aria-label={`${entry.name} planned entry price (quote units)`} value={entry.price}
        aria-invalid={priceMarks?.invalid || undefined} data-warning={priceMarks?.warning || undefined} title={priceMarks?.title}
        onChange={event => onChange({ ...entry, price: event.target.value })} />
    </label>
    {own !== null && entryPrice !== null && <p className="entry-liquidation" data-testid="entry-liquidation"
      title={`Estimated for isolated margin at ${leverage}×${shareLocked ? '' : ' if only this entry fills'}; ignores fees, funding and margin tiers. Cross margin depends on shared collateral and other positions; it is not always safer.`}>
      Liq. ≈ <strong>{formatDraggedPrice(own)}</strong>
      {' '}<span>{percent.format(Math.abs(own - entryPrice) / entryPrice * 100)}% {direction === 'long' ? 'below' : 'above'} entry{shareLocked ? '' : ' · this entry alone'}</span>
    </p>}
    <ExitList entry={entry} kind="stop" direction={direction} leverage={leverage} onChange={onChange} prefix={prefix} liquidation={liquidation ?? own} suggested={suggested} quantity={quantity} units={units} filled={filled} />
    <ExitList entry={entry} kind="target" direction={direction} leverage={leverage} onChange={onChange} prefix={prefix} liquidation={null} suggested={suggested} quantity={quantity} units={units} />
  </div>
}
