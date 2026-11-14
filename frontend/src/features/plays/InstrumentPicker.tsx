import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import type { VenueInstrument } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { InfoTip } from '@/components/ui/info-tip'
import { useVenues } from '@/api/venues'
import type { PlayDraft } from './draft'
import { pairLabel, searchInstruments, type InstrumentCatalogState } from './instruments'
import { cascade, type Facet } from './instruments-cascade'

export function InstrumentPicker({ catalog: state, value, source, onChange, manualNote = 'Manual label, not venue-validated.', allowManual = true }: {
  allowManual?: boolean
  catalog: InstrumentCatalogState
  value: string
  source: PlayDraft['instrumentSource']
  onChange: (value: string, source: PlayDraft['instrumentSource']) => void
  /** What a manual label means, shown from an info icon beside the field label. */
  manualNote?: string | undefined
}) {
  const id = useId()
  const { accountId, catalog, error, loading } = state
  const manual = allowManual && (accountId === null || source === 'manual')
  const venueName = useVenues().name(catalog?.venueId ?? '')
  const all = useMemo(() => catalog?.instruments ?? [], [catalog])
  // A hidden source leaves the selector only: its markets stay in the catalogue, so a saved Play still resolves.
  const choices = useMemo(() => all.filter(instrument => !instrument.source || !catalog?.disabledSources?.includes(instrument.source)), [all, catalog])
  const selected = all.find(instrument => instrument.contractId === value)
  const hidden = selected && !choices.includes(selected) ? selected : null

  // Switching between the venue catalogue and a manual label sits beside the field label.
  const sourceSwitch = allowManual && accountId !== null && (manual
    ? <Button type="button" variant="ghost" size="sm" onClick={() => { onChange(value, 'venue'); state.retry() }}>Use venue catalogue</Button>
    : <Button type="button" variant="ghost" size="sm" aria-label="Enter manually" onClick={() => onChange(value, 'manual')}>Manual</Button>)

  return <div className="plays-instrument-field">
    <div className="plays-field-heading">
      <label htmlFor={`${id}-instrument`}>Instrument</label>
      {manual && <InfoTip label="About manual instruments">{manualNote}</InfoTip>}
      {sourceSwitch && <span className="instrument-actions">{sourceSwitch}</span>}
    </div>
    {manual ? <Input id={`${id}-instrument`} aria-label="Perpetual instrument" value={value}
      placeholder="Manual contract label" onChange={event => onChange(event.target.value, 'manual')} />
      : <InstrumentSearch id={`${id}-instrument`} choices={choices} value={value} selected={selected} venueName={venueName}
        disabled={loading || !choices.length} loading={loading} onSelect={contract => onChange(contract, 'venue')} />}
    <div className="instrument-feedback">
      {error && <p role="alert">{error}</p>}
      {!manual && catalog && (!choices.length || value && !selected) && <p>{!choices.length && 'No selectable perpetual contracts.'}
        {value && !selected && <span>{choices.length ? '' : ' · '}{value} is not in the current catalogue</span>}</p>}
      {!manual && hidden && <p>{hidden.sourceName ?? hidden.source} is off in Settings. This market stays on saved Plays, but new Plays can&apos;t pick it.</p>}
      {accountId !== null && error && <div className="instrument-actions">
        <Button type="button" variant="ghost" size="sm" onClick={state.retry}>Retry instruments</Button>
      </div>}
    </div>
  </div>
}

/** One narrowing column of the cascade. Pressing it never takes focus from the search field. */
function FacetColumn({ title, allLabel, facets, total, value, onChange }: {
  title: string; allLabel: string; facets: Facet[]; total: number; value: string | null; onChange: (key: string | null) => void
}) {
  const item = (key: string | null, label: string, count: number) => <button key={key ?? 'all'} type="button" tabIndex={-1}
    aria-pressed={value === key} onClick={() => onChange(key)}><span>{label}</span>{' '}<small>{count}</small></button>
  return <div className="instrument-column" role="group" aria-label={title}>
    <h3>{title}</h3>{item(null, allLabel, total)}{facets.map(facet => item(facet.key, facet.label, facet.count))}
  </div>
}

/**
 * The instrument selector for every venue: a search field over cascading columns, category, then source, then instrument.
 * Columns come from the catalogue, so a venue with one source or no labels simply shows fewer. Typing filters; arrow keys and
 * Enter pick; left and right move through the categories while the search is empty; Escape restores the current choice.
 */
function InstrumentSearch({ id, choices, value, selected, venueName, disabled, loading, onSelect }: {
  id: string
  choices: readonly VenueInstrument[]
  value: string
  selected: VenueInstrument | undefined
  venueName: string
  disabled: boolean
  loading: boolean
  onSelect: (contractId: string) => void
}) {
  const listId = `${id.replace(/[^a-zA-Z0-9_-]/g, '')}-list`
  const optionId = (contractId: string) => `${listId}-option-${Array.from(contractId, character => character.codePointAt(0)!.toString(16)).join('-')}`
  const display = selected ? pairLabel(selected) : value
  const [query, setQuery] = useState<string | null>(null)
  const [category, setCategory] = useState<string | null>(null)
  const [source, setSource] = useState<string | null>(null)
  const [active, setActive] = useState(0)
  const open = query !== null
  const matches = useMemo(() => open ? searchInstruments(choices, query) : [], [choices, open, query])
  const view = useMemo(() => cascade(matches, venueName, category, source), [matches, venueName, category, source])
  const results = view.instruments
  const highlighted = results[Math.min(active, results.length - 1)]
  // Which columns exist depends on the catalogue, not the current filter, so the panel keeps its shape while narrowing.
  const shape = useMemo(() => cascade(choices, venueName, null, null), [choices, venueName])
  const showCategories = open && shape.categories.length > 1
  const showSources = open && shape.sources.length > 1
  const panel = useRef<HTMLDivElement>(null)

  // The panel is wider than the field: pull it back in from the right edge of the page.
  useLayoutEffect(() => {
    const element = panel.current
    if (!element) return
    element.style.removeProperty('--instrument-shift')
    const overflow = element.getBoundingClientRect().right - (document.documentElement.clientWidth - 16)
    if (overflow > 0) element.style.setProperty('--instrument-shift', `${-Math.ceil(overflow)}px`)
  }, [open, showCategories, showSources])

  // Arrow keys can move the highlight past the visible rows of a long list; keep it in view.
  const highlightedId = open && highlighted ? optionId(highlighted.contractId) : undefined
  useEffect(() => {
    if (highlightedId) document.getElementById(highlightedId)?.scrollIntoView?.({ block: 'nearest' })
  }, [highlightedId])

  const close = () => setQuery(null)
  const start = () => { setQuery(''); setActive(0); setCategory(null); setSource(null) }
  const choose = (instrument: VenueInstrument) => {
    onSelect(instrument.contractId)
    close()
  }
  const pickCategory = (key: string | null) => { setCategory(key); setActive(0) }
  const pickSource = (key: string | null) => { setSource(key); setActive(0) }
  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      if (!open) { start(); return }
      const step = event.key === 'ArrowDown' ? 1 : -1
      setActive(current => Math.max(0, Math.min(results.length - 1, current + step)))
    } else if ((event.key === 'ArrowLeft' || event.key === 'ArrowRight') && open && !query && showCategories) {
      event.preventDefault()
      const keys = [null, ...view.categories.map(facet => facet.key)]
      const step = event.key === 'ArrowRight' ? 1 : -1
      pickCategory(keys[(keys.indexOf(category) + step + keys.length) % keys.length] ?? null)
    } else if (event.key === 'Enter' && open) {
      event.preventDefault()
      if (highlighted) choose(highlighted)
    } else if (event.key === 'Escape' && open) {
      event.preventDefault()
      event.stopPropagation()
      close()
    }
  }

  return <div className="instrument-search" data-open={open}>
    <Input id={id} role="combobox" aria-label="Perpetual instrument" aria-expanded={open} aria-controls={listId}
      aria-autocomplete="list" aria-activedescendant={highlightedId}
      autoComplete="off" spellCheck={false} disabled={disabled} aria-busy={loading}
      placeholder={loading ? 'Loading venue instruments…' : choices.length ? 'Search, e.g. BTC' : 'Catalogue unavailable'}
      value={open ? query : display}
      // Tabbing in selects the current pair so typing replaces it; a click or the arrow keys open the full list.
      onFocus={event => event.currentTarget.select()}
      onClick={() => { if (!open) start() }}
      onChange={event => { if (!open) { setCategory(null); setSource(null) } setQuery(event.target.value); setActive(0) }}
      onBlur={close} onKeyDown={onKeyDown} />
    {open && <div ref={panel} className="instrument-cascade" data-columns={1 + Number(showCategories) + Number(showSources)}
      // Pointer down keeps focus in the input, so blur does not close the panel before a click lands.
      onPointerDown={event => event.preventDefault()}>
      {showCategories && <FacetColumn title="Category" allLabel="All" facets={view.categories} value={category} onChange={pickCategory}
        total={view.categories.reduce((sum, facet) => sum + facet.count, 0)} />}
      {showSources && <FacetColumn title="Source" allLabel="All" facets={view.sources} value={source} onChange={pickSource}
        total={view.sources.reduce((sum, facet) => sum + facet.count, 0)} />}
      <div className="instrument-column instrument-results">
        {(showCategories || showSources) && <h3>Instrument</h3>}
        <ul id={listId} role="listbox" aria-label="Perpetual instruments" className="instrument-options">
          {results.map(instrument => <li key={instrument.contractId} id={optionId(instrument.contractId)} role="option"
            aria-selected={instrument === highlighted} data-current={instrument.contractId === value} onClick={() => choose(instrument)}>
            <span>{pairLabel(instrument)}{instrument.source && <em className="instrument-source">{instrument.source.toUpperCase()}</em>}
              {instrument.isolatedOnly && <em className="instrument-source">Isolated</em>}</span><small>{instrument.maxLeverage}×</small>
          </li>)}
          {results.length === 0 && <li className="instrument-empty" role="presentation">No perpetual matches “{query}”.</li>}
        </ul>
      </div>
    </div>}
  </div>
}
