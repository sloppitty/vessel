import { useCallback, useEffect, useId, useRef, useState, type CSSProperties } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { addEntry as withEntry, createNextEntry, removeEntry as withoutEntry, splitEqually as equalSplit, type DraftEntry, type PlayDraft } from './draft'
import { EntryForm } from './EntryForm'
import { formatDraggedPrice, leverageOf, percentLevels } from './levels'
import { AvailableBudget } from './WorkspacePanels'
import {
  convertSize, defaultSizeUnits, estimatedLiquidation, formatMoney, formatQuantity, formatRewardToRisk, marginOverBudget, overBudgetMessage, positionSize, rewardToRisk,
  sizeForBudget, type AvailableBalance, type SizeUnits,
} from './sizing'
import { Equal, Expand, Plus, Trash2 } from 'lucide-react'
import { NativeSelect } from '@/components/ui/native-select'
import { planInputType, planNumber } from './numbers'
import { planTextMarks, sizeState, useStoredPlanText } from './stored-plan-text'

const leveragePresets = [1, 5, 10, 25, 50]
/** Control range when the venue maximum is unknown, e.g. manual instruments. Saved plans accept up to 100×. */
export const defaultMaxLeverage = 100

/** Fields with a suggestion waiting in the suggestions panel; they get a yellow marker. */
export interface SuggestionMarks {
  size: boolean
  leverage: boolean
  /** Stop and target IDs. */
  levels: ReadonlySet<string>
}
const markTitle = 'A suggestion for this field is in Suggestions above the chart.'

function allocatedShare(entries: DraftEntry[]) {
  const shares = entries.filter(entry => entry.share.trim()).map(entry => planNumber(entry.share))
  const known = shares.filter((share): share is number => share !== null)
  if (!known.length || known.length < shares.length) return null
  return Number(known.reduce((total, share) => total + share, 0).toFixed(2))
}
export function PositionEditor({ draft, onChange, onLeverageChange, selectedId, selectionRequest = 0, onSelect, maxLeverage = null, maintenanceMargin = null, instrumentName = '', units = defaultSizeUnits, availableBudget = null, checkBudget = true,
  marks = null }: {
  draft: PlayDraft
  onChange: (draft: PlayDraft) => void
  onLeverageChange: (value: string) => void
  selectedId: string
  selectionRequest?: number
  onSelect: (id: string) => void
  /** Venue maximum for the chosen contract, or null when unknown. */
  maxLeverage?: number | null
  /** Maintenance margin fraction the venue states for the contract, or null. */
  maintenanceMargin?: number | null
  instrumentName?: string
  /** Quote asset and contract for the size readout. */
  units?: SizeUnits
  /** The account's available balance and its source, used as the budget unless a manual one is set. */
  availableBudget?: AvailableBalance | null
  /** Holds the margin to the budget. Off once orders may rest, since the venue then counts their margin as used. */
  checkBudget?: boolean
  /** Fields with a waiting suggestion; null marks none (read-only plays, or the record did not load). */
  marks?: SuggestionMarks | null
}) {
  const leverageLimit = maxLeverage ?? defaultMaxLeverage
  const stored = useStoredPlanText()
  const leverage = leverageOf(draft.leverage)
  const presets = leveragePresets.filter(preset => preset < leverageLimit).concat(leverageLimit)
  const prefix = useId()
  const [expanded, setExpanded] = useState(false)
  const sidebar = useRef<HTMLDivElement>(null)
  const entryButtons = useRef(new Map<string, HTMLButtonElement>())
  const expandButton = useRef<HTMLButtonElement>(null)
  const previousSelection = useRef({ id: selectedId, request: selectionRequest })
  const sidebarBeforeExpansion = useRef({ selectedId, scrollTop: 0 })
  const selected = draft.entries.find(entry => entry.id === selectedId) ?? draft.entries[0]
  const allocated = allocatedShare(draft.entries)

  const revealEntry = useCallback((id: string, focus: boolean) => {
    const container = sidebar.current
    const button = entryButtons.current.get(id)
    if (!container || !button) return
    const top = button.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop
    container.scrollTo({ top: Math.max(0, top - 12), behavior: 'auto' })
    if (focus) button.focus({ preventScroll: true })
  }, [])

  useEffect(() => {
    if (previousSelection.current.id === selectedId && previousSelection.current.request === selectionRequest) return
    previousSelection.current = { id: selectedId, request: selectionRequest }
    revealEntry(selectedId, !expanded)
  }, [selectedId, selectionRequest, expanded, revealEntry])

  function selectEntry(id: string) {
    if (id === selectedId) revealEntry(id, !expanded)
    onSelect(id)
  }

  function updateEntry(entry: DraftEntry) {
    onChange({ ...draft, entries: draft.entries.map(current => current.id === entry.id ? entry : current) })
  }

  // Percentage stops and targets are returns at the leverage, so with any of them a new leverage is
  // previewed in the controls and only applied once the owner chooses what happens to those levels.
  const [leverageDraft, setLeverageDraft] = useState<string | null>(null)
  const needsConfirmation = percentLevels(draft.entries).length > 0
  const shownLeverage = leverageDraft ?? draft.leverage
  const clampLeverage = (value: string) => value === '' ? '' : String(Math.min(leverageLimit, Math.max(1, Math.round(Number(value)))))

  function updateLeverage(value: string) {
    const next = clampLeverage(value)
    if (needsConfirmation) setLeverageDraft(next)
    else onLeverageChange(next)
  }

  /** Asks about percentage levels before applying a previewed leverage. A blank value goes back to the current one. */
  function commitLeverage(value = leverageDraft) {
    if (value === null || !needsConfirmation) return
    if (value === '' || Number(value) === leverage) { setLeverageDraft(null); return }
    setLeverageDraft(null)
    onLeverageChange(value)
  }

  const sized = positionSize(draft, leverage)
  const liquidation = estimatedLiquidation(draft.entries, draft.direction, leverage, maxLeverage, maintenanceMargin)
  const overBudget = checkBudget ? marginOverBudget(draft, leverage, availableBudget) : null
  // A zero (or negative) budget has no size to offer.
  const budgetSize = overBudget && overBudget.budget.amount > 0 ? sizeForBudget(draft, leverage, overBudget.budget.value, units) : null
  const sizeMarks = planTextMarks(sizeState(draft, stored), draft.size)
  // One entry takes the whole position, so its share and the split are fixed.
  const single = draft.entries.length === 1

  function splitEqually() {
    onChange({ ...draft, entries: equalSplit(draft.entries) })
  }

  function addEntry() {
    const entry = createNextEntry(draft.entries)
    onChange({ ...draft, entries: withEntry(draft.entries, entry) })
    onSelect(entry.id)
  }

  function removeEntry(id: string) {
    if (single) return
    const entries = withoutEntry(draft.entries, id)
    onChange({ ...draft, entries })
    if (selected?.id === id) onSelect(entries[0]!.id)
  }

  const shown = (id: string) => selected?.id === id && !(expanded && selected?.id === id)
  return <section className="plays-position panel position-panel" aria-label="Position" data-testid="position-panel">
    <h2 className="sr-only">Position</h2>
      <div className="position-context position-sizing position-builder">
        <AvailableBudget key={draft.accountId} draft={draft} onChange={onChange} available={availableBudget} />
        <div className="whole-size-input">
          <label htmlFor={`${prefix}-size`}>
            <span>{draft.sizingMode === 'margin' ? 'Margin' : 'Quantity'}</span>
            <Input id={`${prefix}-size`} {...planInputType(draft.size)} min={0} step="any" value={draft.size} placeholder="0"
              aria-invalid={overBudget || sizeMarks?.invalid ? true : undefined} aria-describedby={overBudget ? `${prefix}-over-budget` : undefined}
              data-warning={sizeMarks?.warning || undefined} data-suggested={marks?.size || undefined}
              title={sizeMarks?.title ?? (marks?.size ? markTitle : undefined)}
            aria-label={draft.sizingMode === 'margin' ? `Whole-position margin (${units.quote})` : `Whole-position quantity (${units.base})`}
              onChange={event => onChange({ ...draft, size: event.target.value })} />
          </label>
          <div className="size-unit-field">
            <span aria-hidden="true">&nbsp;</span>
            {/* Switching keeps the same position when it can be converted at the leverage and average entry. */}
            <div className="size-unit-switch segmented" role="group" aria-label="Whole-position sizing">
              {(['margin', 'quantity'] as const).map(mode => <button key={mode} type="button" aria-pressed={draft.sizingMode === mode}
                aria-label={mode === 'margin' ? `Margin in ${units.quote}` : `Quantity in ${units.base}`}
                onClick={() => {
                  if (mode !== draft.sizingMode) onChange({ ...draft, sizingMode: mode, size: convertSize(draft, leverage, units) })
                }}>{mode === 'margin' ? '$' : units.base === defaultSizeUnits.base ? 'Qty' : units.base}</button>)}
            </div>
          </div>
          {sized.notional !== null || sized.margin !== null || liquidation !== null ? <p className="position-size-readout" data-testid="size-readout">
            {sized.notional !== null && <span>Position <strong>{formatMoney(sized.notional, units)}</strong></span>}
            {draft.sizingMode === 'quantity' && sized.margin !== null && <span>Margin <strong>{formatMoney(sized.margin, units)}</strong></span>}
            {sized.quantity !== null && draft.sizingMode === 'margin' && <span>≈ <strong>{formatQuantity(sized.quantity, units)}</strong></span>}
            <small>at {leverage}×</small>
            {liquidation !== null && <span className="size-liquidation" data-testid="liquidation-estimate"
              title="Estimated for isolated margin with every entry filled at its planned price; ignores fees, funding and margin tiers. Cross margin depends on shared collateral and other positions; it is not always safer. The venue shows the actual liquidation estimate.">
              Liq. ≈ <strong>{formatDraggedPrice(liquidation)}</strong></span>}
          </p> : null}
          {overBudget && <p id={`${prefix}-over-budget`} className="size-over-budget" role="alert">
            <span>{overBudgetMessage(overBudget, units)}</span>
            {budgetSize !== null && <Button type="button" variant="outline" size="sm" onClick={() => onChange({ ...draft, size: budgetSize })}>
              Use {draft.sizingMode === 'margin' ? formatMoney(overBudget.budget.amount, units) : formatQuantity(Number(budgetSize), units)}</Button>}
          </p>}
        </div>
        <div className="leverage-controls">
          <label htmlFor={`${prefix}-leverage-slider`}>Leverage {maxLeverage && <small>Max {maxLeverage}×</small>}</label>
          <div className="leverage-input-row">
            <input id={`${prefix}-leverage-slider`} type="range" min={1} max={leverageLimit} step={1} value={Math.min(leverageOf(shownLeverage), leverageLimit)}
              style={{ '--range-progress': `${leverageLimit > 1 ? (Math.min(leverageOf(shownLeverage), leverageLimit) - 1) / (leverageLimit - 1) * 100 : 100}%` } as CSSProperties}
              aria-label="Leverage slider (×)" aria-valuetext={shownLeverage ? `${shownLeverage} times` : 'Not specified'}
              onChange={event => updateLeverage(event.target.value)}
              onPointerUp={event => commitLeverage(clampLeverage(event.currentTarget.value))} onBlur={() => commitLeverage()} />
            <Input id={`${prefix}-leverage`} type="number" min={1} max={leverageLimit} step={1} value={shownLeverage}
              aria-label="Leverage (×)" data-suggested={marks?.leverage || undefined} title={marks?.leverage ? markTitle : undefined} onChange={event => updateLeverage(event.target.value)} onBlur={() => commitLeverage()}
              onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); commitLeverage() } }} />
            <span aria-hidden="true">×</span>
          </div>
          <div className="leverage-presets" role="group" aria-label="Leverage presets">
            {presets.map(preset => <button key={preset} type="button" aria-pressed={shownLeverage === String(preset)}
              onClick={() => needsConfirmation ? commitLeverage(String(preset)) : updateLeverage(String(preset))}>{preset}×</button>)}
          </div>
        </div>
        {leverage > leverageLimit && <p className="leverage-warning" role="alert">{leverage}× is above the {leverageLimit}× venue maximum for {instrumentName || 'this contract'}.</p>}
          </div>
        <div className="entries-heading">
          <h3>Entries <span className="entry-count">{draft.entries.length}</span></h3>
          <span className="entries-allocated" data-complete={allocated === 100} title="Share of the position's quantity given to entries">{allocated === null ? '—' : `${allocated}%`}</span>
          <Button type="button" variant="ghost" size="icon-sm" className="split-equally" aria-label="Split equally" title={single ? 'A single entry takes the whole position' : 'Split shares equally'} disabled={single} onClick={splitEqually}><Equal size={14} aria-hidden="true" /></Button>
          <Button type="button" variant="ghost" size="icon-sm" className="add-entry" aria-label="Add entry" title="Add entry" onClick={addEntry}><Plus size={14} aria-hidden="true" /></Button>
          <Dialog open={expanded} onOpenChange={open => {
            if (open) sidebarBeforeExpansion.current = { selectedId, scrollTop: sidebar.current?.scrollTop ?? 0 }
            setExpanded(open)
          }}>
            <DialogTrigger asChild>
              <Button ref={expandButton} type="button" variant="ghost" size="icon-sm" className="expand-entry" aria-label="Expand selected entry" title="Expand selected entry" disabled={!selected}><Expand size={14} aria-hidden="true" /></Button>
            </DialogTrigger>
            <DialogContent className="entry-dialog plays-entry-dialog" onCloseAutoFocus={event => {
              event.preventDefault()
              if (selectedId === sidebarBeforeExpansion.current.selectedId) {
                sidebar.current?.scrollTo({ top: sidebarBeforeExpansion.current.scrollTop, behavior: 'auto' })
              } else {
                revealEntry(selectedId, false)
              }
              expandButton.current?.focus({ preventScroll: true })
            }}>
              <DialogHeader>
                <DialogTitle>Expanded entry editor</DialogTitle>
                <DialogDescription className="sr-only">The same entry form with more room. Escape returns to the list.</DialogDescription>
              </DialogHeader>
              <label htmlFor={`${prefix}-expanded-entry`}>Entry
                <NativeSelect density="compact" id={`${prefix}-expanded-entry`} value={selected?.id ?? ''} onChange={event => selectEntry(event.target.value)}>
                  {draft.entries.map(entry => <option key={entry.id} value={entry.id}>{entry.name}</option>)}
                </NativeSelect>
              </label>
              {selected && <EntryForm key={selected.id} entry={selected} onChange={updateEntry} idPrefix={`${prefix}-expanded-${selected.id}`} direction={draft.direction} leverage={leverage} shareLocked={single} maxLeverage={maxLeverage} maintenanceMargin={maintenanceMargin} liquidation={liquidation} suggested={marks?.levels ?? null} quantity={sized.quantity} units={units} />}
            </DialogContent>
          </Dialog>
        </div>
        <div ref={sidebar} className="entry-sidebar" data-testid="entry-sidebar" tabIndex={0} aria-label="Entries">
          {draft.entries.map(entry => <article key={entry.id} className="entry-card" data-selected={selected?.id === entry.id}
            style={{ '--entry-color': entry.color } as CSSProperties} aria-label={`${entry.name} editor`}>
            <div className="entry-header-row">
              <button type="button" className="entry-header" aria-pressed={selected?.id === entry.id} aria-expanded={selected?.id === entry.id}
                ref={node => { if (node) entryButtons.current.set(entry.id, node); else entryButtons.current.delete(entry.id) }}
                onClick={() => selectEntry(entry.id)}>
                <strong><i aria-hidden="true" />{entry.name}</strong>
                <span className="entry-header-price">{entry.price ? `@ ${entry.price}` : 'No price'}</span>
                <span>{entry.share === '' ? '—' : `${entry.share}%`}</span>
                <RewardToRisk entry={entry} direction={draft.direction} leverage={leverage} />
                {selected?.id !== entry.id && <small className="entry-header-levels">{levelCount(entry)}</small>}
              </button>
              {draft.entries.length > 1 && <Button type="button" variant="ghost" size="icon-sm" className="remove-entry" aria-label={`Remove ${entry.name}`}
                title={`Remove ${entry.name}`} onClick={() => removeEntry(entry.id)}><Trash2 size={13} aria-hidden="true" /></Button>}
            </div>
            {shown(entry.id) && <EntryForm entry={entry} onChange={updateEntry} idPrefix={`${prefix}-sidebar-${entry.id}`} direction={draft.direction} leverage={leverage} shareLocked={single} maxLeverage={maxLeverage} maintenanceMargin={maintenanceMargin} liquidation={liquidation} suggested={marks?.levels ?? null} quantity={sized.quantity} units={units} />}
            {expanded && selected?.id === entry.id && <p className="muted expanded-placeholder">Editing in the expanded view.</p>}
          </article>)}
        </div>
  </section>
}

/** Planned reward to risk in the entry header; a dash until it has a price, a stop and a target. */
function RewardToRisk({ entry, direction, leverage }: { entry: DraftEntry; direction: PlayDraft['direction']; leverage: number }) {
  const ratio = rewardToRisk(entry, direction, leverage)
  return <span className="entry-header-rr" data-weak={ratio !== null && ratio < 1 ? true : undefined}
    title="Planned reward to risk: share-weighted target distance over stop distance, before fees">
    <small>R:R</small> {ratio === null ? '—' : formatRewardToRisk(ratio)}
  </span>
}

/** "2 SL · 1 TP" for a collapsed entry, counting levels with a value. */
function levelCount(entry: DraftEntry) {
  const stops = entry.stops.filter(stop => stop.value.trim() !== '').length
  const targets = entry.targets.filter(target => target.value.trim() !== '').length
  return [stops && `${stops} SL`, targets && `${targets} TP`].filter(Boolean).join(' · ')
}
