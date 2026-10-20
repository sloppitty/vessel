import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react'
import type { BrokerAccount, Portfolio, WorkspaceApi } from '@/api/workspace'
import type { SizingDocument } from '@/api/sizing'
import { ExternalLink, PencilLine, RefreshCw, TriangleAlert } from 'lucide-react'
import { statusLabels, type PlayStatus } from '@/api/plays'
import { useVenues } from '@/api/venues'
import { AccountSelect } from '@/components/accounts/AccountSelect'
import { Input } from '@/components/ui/input'
import { InfoTip } from '@/components/ui/info-tip'
import type { DraftEvidence, PlayDraft } from './draft'
import { captureFileName, createEvidence, evidenceLimits } from './evidence'
import { DirectionToggle } from './DirectionToggle'
import { InstrumentPicker } from './InstrumentPicker'
import { pairLabel, useInstrumentCatalog } from './instruments'
import { leverageOf, type StopEvidenceOf } from './levels'
import { positivePlanNumber } from './numbers'
import { useLeverageChange } from './useLeverageChange'
import { availableOf, budgetSourceLabel, defaultSizeUnits, estimatedLiquidation, playBudget, type SizeUnits } from './sizing'
import { planSuggestions } from './suggestions'
import { SuggestionsPanel } from './Suggestions'
import type { SizingStatus } from './useSizing'
import { PlanNotices } from './PlanNotices'
import { PositionEditor, defaultMaxLeverage, type SuggestionMarks } from './PositionEditor'
import { ChartPanel } from './PlayChart'
import { CapitalContext, PlayJournal, PositionSummary } from './WorkspacePanels'
import { noFilledEntries } from './planChecks'
import { noEntryFills, type EntryFills } from './position-risk-summary'
import { isNominalUsdQuote } from './stablecoins'
import { defaultPlayTitle } from './default-title'
import './plays-workspace.css'
import { NativeSelect } from '@/components/ui/native-select'
import { pickerAccounts } from '@/features/workspace/account-picker-options'
import { incidentSummary, topTone, type IncidentCounts } from '@/features/monitor/incidents'
import '@/features/monitor/incidents.css'


export function PlayWorkspace({ accounts, portfolios, api, draft, onChange, onReload, loading = false, status = 'draft', actions, revision = null,
  lockInstrument = false, lockDirection = false, readOnly = false, execution, sizing = null, sizingStatus = 'ready', onRetrySizing, onRiskChange,
  filledEntries, fills: entryFills, onUnits, onPriceStep, retrospective = false, incidents, stopEvidence }: {
  accounts: BrokerAccount[]
  portfolios: Portfolio[]
  api: WorkspaceApi
  draft: PlayDraft
  onChange: (draft: PlayDraft) => void
  onReload?: () => void
  loading?: boolean
  status?: PlayStatus
  /** Save and lifecycle controls; without them the page is an unsaved local draft. */
  actions?: ReactNode
  /** Plan revision of a saved, planned Play once it has been revised. */
  revision?: number | null
  /** Account and instrument are fixed once a Play is planned. */
  lockInstrument?: boolean
  /** Direction is fixed once an entry has filled: it decides which side closes that position. */
  lockDirection?: boolean
  /** Closed and cancelled Plays keep their plan; only the review and evidence change. */
  readOnly?: boolean
  /** Execution tab content for a saved play. */
  execution?: ReactNode
  /** This Play's open monitor incidents, from the shell's monitor summary; shown in the header with a way to the Execution tab. */
  incidents?: IncidentCounts | undefined
  /** Venue evidence for the planned stops of a saved, Open Play; the chart draws unconfirmed stops apart. */
  stopEvidence?: StopEvidenceOf | undefined
  retrospective?: boolean
  /** Risk settings and record for sizing suggestions, which only apply to editable plays. */
  sizing?: SizingDocument | null
  /** Whether `sizing` is current; a stale or failed record offers no Accept and marks no field. */
  sizingStatus?: SizingStatus
  onRetrySizing?: () => void
  onRiskChange?: (riskPercent: string) => Promise<void>
  /**
   * Entries with a linked or manual fill (`'all'` when one has filled but not which): no size or leverage suggestions,
   * and their stops may sit at or beyond the entry.
   */
  filledEntries?: ReadonlySet<string> | 'all'
  /**
   * Which entries have filled and each one's average fill, from the execution or the manual ledger, or `'unknown'` while it
   * loads. Their stops may sit at or beyond the entry, and the Summary measures them from the fill. Without it, the entries
   * in `filledEntries` count as filled with no known average.
   */
  fills?: EntryFills | 'unknown'
  /** The size units in use, so the page can word its own messages in the instrument's quote asset. */
  onUnits?: (units: SizeUnits) => void
  /** The instrument's tick, or null, so the page can match order prices as the server does. */
  onPriceStep?: (step: number | null) => void
}) {
  const fieldId = useId()
  const [selectedId, setSelectedId] = useState(draft.entries[0]?.id ?? '')
  const [selectionRequest, setSelectionRequest] = useState(0)
  const [portfolioFilter, setPortfolioFilter] = useState('')
  const [evidenceRequest, setEvidenceRequest] = useState(0)
  const enabledAccounts = accounts.filter(account => account.isEnabled !== false)
  const account = enabledAccounts.find(account => account.id === draft.accountId)
  const accountId = account?.id ?? ''
  const matchesFilter = (account: BrokerAccount, filter: string) => !filter ||
    (filter === 'unassigned' ? account.portfolioId === null : account.portfolioId === filter)
  const filterExists = portfolioFilter === 'unassigned' ? enabledAccounts.some(account => account.portfolioId === null) :
    portfolios.some(portfolio => portfolio.id === portfolioFilter)
  const effectiveFilter = filterExists && (!account || matchesFilter(account, portfolioFilter)) ? portfolioFilter : ''
  if (portfolioFilter !== effectiveFilter) setPortfolioFilter(effectiveFilter)
  const filteredAccounts = enabledAccounts.filter(account => matchesFilter(account, effectiveFilter))
  const accountChoices = pickerAccounts(filteredAccounts, portfolios)

  // A reload can disable or delete the chosen account. Clear it like an account change so a
  // venue contract is never shown as a manual label. Loading keeps the last known list.
  const accountGone = draft.accountId !== '' && !loading && !account && !lockInstrument
  useEffect(() => {
    if (accountGone) onChange({ ...draft, accountId: '', instrument: '', instrumentSource: 'manual', budgetOverride: null })
  }, [accountGone, draft, onChange])

  // Drawings follow the venue instrument, so switching away and back keeps them.
  const drawingKey = account && draft.instrument && draft.instrumentSource === 'venue' ? `${account.venueId}:${draft.instrument}` : ''

  const venues = useVenues()
  const catalog = useInstrumentCatalog(api, account)
  const instrumentInfo = draft.instrumentSource === 'venue' ? catalog.catalog?.instruments.find(item => item.contractId === draft.instrument) : undefined
  const instrumentName = instrumentInfo ? pairLabel(instrumentInfo) : draft.instrument
  const maxLeverage = instrumentInfo?.maxLeverage ?? null
  const maintenanceMargin = instrumentInfo?.maintenanceMarginFraction ?? null
  const priceStep = instrumentInfo?.priceStep ?? null
  const manualUsd = !!account && venues.can(account.venueId, 'manualExecutions')
  const units: SizeUnits = instrumentInfo
    ? { quote: instrumentInfo.quoteAsset, base: instrumentInfo.contractId, quantityDecimals: instrumentInfo.quantityDecimals }
    : manualUsd ? { quote: 'USD', base: draft.instrument, quantityDecimals: null } : defaultSizeUnits
  // The Summary's risk share divides by the same account balance the size suggestion uses; an explicit
  // manual budget is a labelled fallback. Without either the share stays unknown.
  const balanceText = account?.balanceUsd != null && Number(account.balanceUsd) > 0 ? account.balanceUsd : null
  const riskAmount = balanceText ?? (draft.budgetOverride && positivePlanNumber(draft.budgetOverride) !== null ? draft.budgetOverride : null)
  const riskName = balanceText ? `${account!.name} balance` : 'Explicit manual budget'
  // Listed stablecoins count as nominal USD at 1:1, with no FX or depeg valuation.
  const riskReference = riskAmount && isNominalUsdQuote(units.quote)
    ? { name: units.quote === 'USD' ? riskName : `${riskName} · nominal USD/${units.quote} at 1:1`, amount: riskAmount, quote: units.quote } : null
  const { quote, base, quantityDecimals } = units
  useEffect(() => { onUnits?.({ quote, base, quantityDecimals }) }, [onUnits, quote, base, quantityDecimals])
  useEffect(() => { onPriceStep?.(priceStep) }, [onPriceStep, priceStep])

  // Manual instruments are labels: say what Vessel assumes about them beside the Instrument label.
  const manualNote = retrospective ? undefined : manualUsd
    ? 'Manual label, not venue-validated. Manual plans assume a linear USD quote and one base unit per quantity. Other quote currencies, inverse contracts and implicit multipliers are unsupported.'
    : undefined

  const tradeUrl = account ? venues.tradeUrl(account.venueId, draft.instrument, draft.instrumentSource, instrumentInfo?.venueContractId) : null
  // In a read-only Play, the chart can still be viewed but not edited.
  const planChange = readOnly ? () => {} : onChange

  // Captures and uploads finish asynchronously, so they apply to the latest draft, not the one from the click.
  const latest = useRef({ draft, onChange })
  useEffect(() => { latest.current = { draft, onChange } })
  const updateEvidence = (update: (evidence: DraftEvidence[]) => DraftEvidence[]) => {
    const { draft: current, onChange: change } = latest.current
    change({ ...current, evidence: update(current.evidence) })
  }
  function addCapture(image: Blob, context: string) {
    if (latest.current.draft.evidence.length >= evidenceLimits.maxItems) return `A play can hold at most ${evidenceLimits.maxItems} images.`
    if (image.size > evidenceLimits.maxBytes) return `The capture is larger than ${evidenceLimits.maxBytes / 1024 / 1024} MB.`
    const now = new Date()
    updateEvidence(evidence => [...evidence, createEvidence('capture', image, captureFileName(now), context, now)])
    setEvidenceRequest(current => current + 1)
    return null
  }

  const leftColumn = useRef<HTMLDivElement>(null)
  // Runs after the expanded chart closes, so the journal is in the page again.
  const showEvidence = () => requestAnimationFrame(() =>
    leftColumn.current?.querySelector('[data-testid="journal-panel"]')?.scrollIntoView?.({ behavior: 'smooth', block: 'start' }))
  const [executionRequest, setExecutionRequest] = useState(0)
  const incidentTone = incidents ? topTone(incidents) : null
  const showExecution = () => { setExecutionRequest(current => current + 1); showEvidence() }

  const fills = useMemo<EntryFills | 'unknown'>(() => entryFills ?? { ...noEntryFills, filled: filledEntries instanceof Set ? filledEntries : noFilledEntries },
    [entryFills, filledEntries])
  // While the fills load, every stop keeps the wrong-side rule; the server decides per entry from its own facts.
  const filled = fills === 'unknown' ? noFilledEntries : fills.filled

  // Suggestions sit in their own panel above the workspace; the fields only carry a marker.
  const leverage = leverageOf(draft.leverage)
  const leverageChange = useLeverageChange(draft, onChange, units)
  const balance = account?.balanceUsd != null && Number(account.balanceUsd) > 0 ? Number(account.balanceUsd) : null
  const available = availableOf(account)
  const budget = status === 'draft' ? playBudget(draft, available) : null
  const suggestions = sizing && !readOnly ? planSuggestions({ draft, leverage, maxLeverage, maintenanceMargin, priceStep, sizing, units, balance,
    budget: budget?.amount ?? null, budgetSource: budget ? budgetSourceLabel(budget) : null, ...(filledEntries ? { filledEntries } : {}),
    defaultMaximum: defaultMaxLeverage }) : null
  // A stale or missing record cannot back a suggestion, so no field is marked for one.
  const sizingCurrent = sizingStatus === 'ready' || sizingStatus === 'loading'
  const marks: SuggestionMarks | null = suggestions && sizingCurrent ? {
    size: !!suggestions.size, leverage: !!suggestions.leverage,
    levels: new Set([...suggestions.stops, ...suggestions.targets].map(level => level.levelId)),
  } : null
  function acceptLevel(entryId: string, kind: 'stop' | 'target', levelId: string, value: string) {
    onChange({ ...draft, entries: draft.entries.map(entry => entry.id !== entryId ? entry : kind === 'stop'
      ? { ...entry, stops: entry.stops.map(stop => stop.id === levelId ? { ...stop, value } : stop) }
      : { ...entry, targets: entry.targets.map(target => target.id === levelId ? { ...target, value } : target) }) })
  }

  function chooseAccount(id: string) {
    const next = enabledAccounts.find(account => account.id === id)
    onChange({ ...draft, accountId: id, instrument: '',
      instrumentSource: next && venues.can(next.venueId, 'instruments') ? 'venue' : 'manual', budgetOverride: null })
  }

  return <section className="plays-page" aria-label="Play draft workspace">
    <h1 className="sr-only">Plays</h1>
    <div className="plays-draft-heading">
      <div className="page-heading-copy">
        <input className="plays-title-input" aria-label="Play title" value={draft.title} maxLength={200}
          placeholder={readOnly ? 'Name this play' : defaultPlayTitle(draft)} disabled={readOnly} onChange={event => onChange({ ...draft, title: event.target.value })} />
      </div>
      <div className="plays-heading-actions">
        {incidents && incidentTone && <button type="button" className="play-incidents" data-tone={incidentTone} disabled={!execution}
          title={execution ? 'Show in the Execution tab' : undefined} onClick={showExecution}>
          <TriangleAlert size={14} aria-hidden="true" />Needs attention · {incidentSummary(incidents)}</button>}
        {tradeUrl && <a className="plays-venue-link" href={tradeUrl} target="_blank" rel="noopener noreferrer"
          title="Place the planned orders on the venue. Vessel never sends orders.">
          <ExternalLink size={14} aria-hidden="true" />Open {instrumentName} on {venues.name(account!.venueId)}</a>}
        {actions ?? <span className="workspace-badge"><PencilLine size={13} />Local draft</span>}
      </div>
    </div>
    <fieldset className="plays-draft-fields" disabled={readOnly}>
      <legend className="sr-only">Play context</legend>
      <label className="plays-portfolio-field">Portfolio<NativeSelect density="bare" value={effectiveFilter} disabled={lockInstrument} onChange={event => {
        const next = event.target.value
        setPortfolioFilter(next)
        if (account && next && (next === 'unassigned' ? account.portfolioId !== null : account.portfolioId !== next)) chooseAccount('')
      }}>
        <option value="">All accounts</option>
        {portfolios.map(portfolio => <option key={portfolio.id} value={portfolio.id}>{portfolio.name}</option>)}
        {enabledAccounts.some(account => account.portfolioId === null) && <option value="unassigned">Unassigned accounts</option>}
      </NativeSelect></label>
      <div className="plays-account-field">
        <div className="plays-field-heading plays-account-heading"><span id={`${fieldId}-account-label`} className="plays-field-label">Account</span></div>
        <div className="plays-select-row">
          <AccountSelect density="bare" id={`${fieldId}-account`} labelledBy={`${fieldId}-account-label`} value={lockInstrument ? draft.accountId : accountId}
            accounts={accountChoices} onChange={chooseAccount} disabled={lockInstrument || (loading && !accounts.length)}
            placeholder={!loading && !enabledAccounts.length ? 'No enabled accounts' : 'Choose an account'}
            missingLabel={lockInstrument ? 'Unavailable or disabled account' : undefined} />{onReload && <button type="button" className="plays-reload-accounts" aria-label="Reload accounts" title="Reload accounts"
            onClick={onReload} disabled={loading} aria-busy={loading}>
            <RefreshCw size={13} className={loading ? 'is-spinning' : ''} aria-hidden="true" /></button>}
        </div>
      </div>
      {lockInstrument ? <div className="plays-instrument-field"><div className="plays-field-heading"><label htmlFor="plays-fixed-instrument">Instrument</label>
          {manualNote && <InfoTip label="About manual instruments">{manualNote}</InfoTip>}</div>
        <Input id="plays-fixed-instrument" aria-label="Perpetual instrument" value={instrumentName} readOnly title="Fixed once planned" />
      </div> : <InstrumentPicker key={accountId} catalog={catalog} value={draft.instrument} source={draft.instrumentSource} manualNote={manualNote}
        onChange={(instrument, instrumentSource) => {
          // A new contract can allow less leverage than the plan uses; keep the plan within the venue maximum.
          const max = instrumentSource === 'venue' ? catalog.catalog?.instruments.find(item => item.contractId === instrument)?.maxLeverage : undefined
          const leverage = max !== undefined && leverageOf(draft.leverage) > max ? String(max) : draft.leverage
          onChange({ ...draft, instrument, instrumentSource, leverage })
        }} />}
      <div className="direction-field"><span className="field-label">Direction</span>
        <DirectionToggle value={draft.direction} onChange={direction => onChange({ ...draft, direction })} disabled={lockDirection}
          title={lockDirection ? 'An entry has filled, so the direction stays. Start a new play for the opposite idea.' : undefined} />
      </div>
      <div className="plays-context-status"><span className="field-label">Status</span><span className="plays-status-pills">
        <span className={`badge play-status-${status}`} title={readOnly && !retrospective ? 'The plan is kept as it was; review and images can change' : undefined}>{statusLabels[status]}</span>
        {revision !== null && <span className="badge play-revision" title="Saved plan changes ask for a reason">Revision {revision}</span>}
        {retrospective && <InfoTip className="badge play-retrospective" trigger="Retrospective">
          Recorded after execution: no original plan, thesis, initial stop or initial-risk R.</InfoTip>}
      </span></div>
    </fieldset>
    {!retrospective && <PlanNotices draft={draft} onChange={onChange} readOnly={readOnly} lockDirection={lockDirection} maxLeverage={maxLeverage} maintenanceMargin={maintenanceMargin}
      filledEntries={filled} />}
    <CapitalContext accounts={enabledAccounts} portfolios={portfolios} draft={{ ...draft, accountId }} retrospective={retrospective} />
    {!readOnly && onRiskChange && (suggestions || sizingStatus === 'failed') && <SuggestionsPanel plan={suggestions} sizing={sizing}
      status={sizingStatus} onRetry={onRetrySizing} draft={draft} leverage={leverage} units={units}
      onSize={size => onChange({ ...draft, size })} onLevel={acceptLevel} onRiskChange={onRiskChange}
      onLeverage={next => leverageChange.request(String(next))} />}
    {leverageChange.dialog}
    <div className="workspace" data-testid="workspace">
      <div ref={leftColumn} className="left-column" data-testid="left-column">
        <ChartPanel entries={retrospective ? [] : draft.entries} selectedId={selectedId} onSelect={id => {
          setSelectedId(id)
          setSelectionRequest(current => current + 1)
        }} instrument={draft.instrument} instrumentName={instrumentName} venue={account ? venues.name(account.venueId) : null} direction={draft.direction}
        intervals={account ? venues.find(account.venueId)?.intervals : undefined} priceStep={priceStep}
        streamable={account ? venues.can(account.venueId, 'stream') : false}
        marketContext={account ? venues.can(account.venueId, 'marketContext') : false}
        leverage={retrospective ? 1 : leverageOf(draft.leverage)} liquidation={retrospective ? null : estimatedLiquidation(draft.entries, draft.direction, leverageOf(draft.leverage), maxLeverage, maintenanceMargin)}
        source={account && venues.can(account.venueId, 'candles') && draft.instrumentSource === 'venue' ? { api, accountId: account.id } : null}
        onEntriesChange={entries => planChange({ ...draft, entries })}
        drawings={drawingKey ? draft.drawings[drawingKey] : undefined}
        onDrawingsChange={drawings => { if (drawingKey) planChange({ ...draft, drawings: { ...draft.drawings, [drawingKey]: drawings } }) }}
        editable={!readOnly} onCapture={addCapture} onShowEvidence={showEvidence} stopEvidence={retrospective ? undefined : stopEvidence} />
        <PlayJournal notes={draft.notes} onChange={notes => onChange({ ...draft, notes: readOnly ? { ...draft.notes, review: notes.review } : notes })}
          execution={execution} readOnly={readOnly} retrospective={retrospective} status={status}
          structuredReview={draft.structuredReview}
          onStructuredReviewChange={structuredReview => onChange({ ...draft, structuredReview })}
          evidence={draft.evidence} onEvidenceChange={updateEvidence} evidenceRequest={evidenceRequest} executionRequest={executionRequest} />
      </div>
      {retrospective ? <section className="panel retrospective-position" aria-label="No original plan"><h2>No original plan</h2></section>
        : readOnly ? <fieldset className="plays-readonly-position" disabled><legend className="sr-only">Position (read-only)</legend>
        <PositionEditor onLeverageChange={leverageChange.request} draft={draft} onChange={planChange} selectedId={selectedId} selectionRequest={selectionRequest} onSelect={setSelectedId} maxLeverage={maxLeverage} maintenanceMargin={maintenanceMargin} units={units} availableBudget={available} checkBudget={false} filledEntries={filled} />
      </fieldset> : <PositionEditor onLeverageChange={leverageChange.request} draft={draft} onChange={onChange} selectedId={selectedId} selectionRequest={selectionRequest} onSelect={setSelectedId}
        maxLeverage={maxLeverage} maintenanceMargin={maintenanceMargin} instrumentName={instrumentName} units={units} availableBudget={available} checkBudget={status === 'draft'}
        marks={marks} filledEntries={filled} />}
    </div>
    {!retrospective && <PositionSummary draft={draft} units={units} instrumentName={instrumentName} riskReference={riskReference} fills={fills} />}
  </section>
}
