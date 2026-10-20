import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ComponentProps, type CSSProperties, type KeyboardEvent, type ReactNode, type RefObject } from 'react'
import { ArrowRightToLine, Camera, ChartNoAxesCombined, ChartSpline, Layers, Maximize2, OctagonX, RefreshCw, Star, Target } from 'lucide-react'
import { Popover } from 'radix-ui'
import { candleIntervals, type CandleInterval } from '@/api/candles'
import type { WorkspaceApi } from '@/api/workspace'
import { CandleChart, type CandleChartControl } from '@/components/chart/CandleChart'
import { ChartHeader, type ChartStat } from '@/components/chart/ChartHeader'
import { ChartIconButton, ChartToolbar, ChartToolbarDivider } from '@/components/chart/ChartToolbar'
import { ChartToolRail, type ChartToolGroup } from '@/components/chart/ChartToolRail'
import { drawingColors, type ChartDrawing } from '@/components/chart/drawings'
import { DrawingEditBar } from '@/components/chart/DrawingEditBar'
import { crosshairTool, drawingToolGroups, drawingToolHints, drawingToolLabels, drawingToolsByKind, drawingUtilityIcons, isDrawingKind } from '@/components/chart/drawingTools'
import { IndicatorSettingsPopover } from '@/components/chart/IndicatorControls'
import { intervalName } from '@/components/chart/intervals'
import { LiveIndicator } from '@/components/chart/LiveIndicator'
import { TimeframeBar } from '@/components/chart/TimeframeBar'
import type { ChartAdapterFactory, ChartMarker, PriceOverlay } from '@/components/chart/types'
import { fillMarkers, tradeFocus, type ChartFill } from './chart-fills'
import { useChartHistory } from '@/components/chart/useChartHistory'
import { useDrawingEditor } from '@/components/chart/useDrawingEditor'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog'
import { useChartPreferences } from '@/features/market/chartPreferences'
import { useCandles } from '@/features/market/useCandles'
import { useLiveMarket } from '@/features/market/useLiveMarket'
import { describeMarket, useMarketContext } from '@/features/market/useMarketContext'
import { addEntry, createNextEntry, removeEntry, type DraftEntry, type PlayDraft } from './draft'
import { LevelEditor } from './LevelEditor'
import { applyEntryEdit, applyLevelDrag, averageEntryPrice, formatDraggedPrice, levelTag, parseOverlayId, placeLevel, planOverlays, type ChartView, type ExitKind, type StopEvidenceOf } from './levels'
import { positivePlanNumber } from './numbers'
import { useDisplayZone } from '@/lib/display-zone'
import { formatTime } from '@/lib/time'

export interface ChartSource {
  api: WorkspaceApi
  accountId: string
}

interface ChartPanelProps {
  entries: DraftEntry[]
  selectedId: string
  onSelect: (id: string) => void
  instrument: string
  /** Display name such as BTC/USDC; defaults to the contract. */
  instrumentName?: string
  venue?: string | null
  direction?: PlayDraft['direction']
  /** The play's leverage; percentage levels are returns at it. */
  leverage?: number
  /** Estimated liquidation price of the whole plan, drawn as a reference line. */
  liquidation?: number | null
  /** False for read-only plays: levels can be viewed but not placed or dragged. */
  editable?: boolean
  /** Brings the Evidence tab into view after a capture. */
  onShowEvidence?: () => void
  /** Venue market data for the chosen account; null keeps the placeholder (manual or unsupported). */
  source?: ChartSource | null
  onEntriesChange?: (entries: DraftEntry[]) => void
  /** Drawings for the current instrument; the caller keeps them per instrument. */
  drawings?: readonly ChartDrawing[] | undefined
  onDrawingsChange?: (drawings: ChartDrawing[]) => void
  /** Adds a chart capture to the draft evidence; returns why it was not added, or null. */
  onCapture?: (image: Blob, context: string) => string | null
  createAdapter?: ChartAdapterFactory
  /** Intervals the venue serves; the timeframe bar offers only these. Defaults to all. */
  intervals?: readonly CandleInterval[] | undefined
  /** The instrument's price tick; levels placed or dragged on the chart round to it. */
  priceStep?: number | null
  /** False for venues without a live stream: the chart refreshes on request only. */
  streamable?: boolean
  /** False for venues without market context: the header statistics are not requested. */
  marketContext?: boolean
  /**
   * Venue evidence for each planned stop of a saved, Open Play. A stop without a confirmed resting order is drawn apart
   * and its tag says what the evidence says ("not seen at the venue", "resting as of 14:02"). Absent for drafts.
   */
  stopEvidence?: StopEvidenceOf | undefined
  /** The Play's fills, marked at their time and price and never drawn as levels (phase 1, 1.1b). */
  fills?: readonly ChartFill[] | undefined
  /** Open on the trade's dates, padded either side, instead of today: an ended Play with fills. */
  tradeDates?: boolean
}

const noDrawings: readonly ChartDrawing[] = []
const noFills: readonly ChartFill[] = []

type PlanTool = 'entry' | ExitKind
/** The estimated liquidation price as a dashed reference line; it is derived, so it cannot be dragged. */
const liquidationOverlay = (price: number): PriceOverlay => ({
  id: 'aggregate:liquidation', label: 'LIQ ≈', price, color: 'var(--negative)', kind: 'reference', emphasis: 'normal', draggable: false,
})
const planToolIcon = { size: 16, strokeWidth: 1.6, 'aria-hidden': true } as const

export function ChartPanel({ entries, selectedId, onSelect, instrument, instrumentName = instrument, venue = null, direction = 'long', leverage = 1, liquidation = null,
  editable = true, source = null, onEntriesChange, drawings = noDrawings, onDrawingsChange, onCapture, onShowEvidence, createAdapter,
  intervals = candleIntervals, priceStep = null, streamable = true, marketContext = true, stopEvidence, fills = noFills, tradeDates = false }: ChartPanelProps) {
  const zone = useDisplayZone()
  const [view, setView] = useState<ChartView>('aggregate')
  const [planTool, setPlanTool] = useState<PlanTool | null>(null)
  const [preferences, setPreferences] = useChartPreferences()
  const scopeKey = `${source?.accountId ?? ''}|${instrument}`
  const history = useChartHistory(scopeKey)
  const editor = useDrawingEditor(drawings, next => onDrawingsChange?.(next), scopeKey, history)
  const [levelEdit, setLevelEdit] = useState<{ id: string; anchor: { x: number; y: number } } | null>(null)
  // History actions and drag gestures read the latest entries, not the ones captured when they started.
  const latest = useRef({ entries, onEntriesChange })
  useEffect(() => { latest.current = { entries, onEntriesChange } })
  const dragStart = useRef<{ entry: DraftEntry; scopeKey: string } | null>(null)
  // Only overlays preview during a drag: suggestions/notices must not move the canvas under the pointer.
  const [dragPreview, setDragPreview] = useState<{ source: DraftEntry[]; entries: DraftEntry[]; scopeKey: string } | null>(null)
  const chartEntries = dragPreview?.source === entries && dragPreview.scopeKey === scopeKey ? dragPreview.entries : entries

  /** Records an entry edit made on the chart so undo/redo reverts only the fields it changed. */
  const recordEntryEdit = (label: string, before: DraftEntry, after: DraftEntry, scope?: string) => {
    const apply = (from: DraftEntry, to: DraftEntry) => latest.current.onEntriesChange?.(latest.current.entries.map(entry =>
      entry.id === before.id ? applyEntryEdit(entry, from, to) : entry))
    history.push({ label, undo: () => apply(after, before), redo: () => apply(before, after) }, scope)
  }
  const applyEntry = (next: DraftEntry, label: string) => {
    const before = entries.find(entry => entry.id === next.id)
    if (!before) return
    recordEntryEdit(label, before, next)
    onEntriesChange?.(entries.map(entry => entry.id === next.id ? next : entry))
  }
  const levelProps = {
    // Interrupted gestures have not changed the draft and must not create an undo step.
    onLevelCancel: () => {
      dragStart.current = null
      setDragPreview(null)
    },
    onLevelSelect: (id: string) => { const ref = parseOverlayId(id); if (ref) onSelect(ref.entryId) },
    onLevelDrag: (id: string, price: number, phase: 'move' | 'end') => {
      if (phase === 'end') setDragPreview(null)
      if (!editable) return
      const ref = parseOverlayId(id)
      const current = latest.current.entries
      const entry = ref && current.find(item => item.id === ref.entryId)
      if (!ref || !entry) return
      dragStart.current ??= { entry, scopeKey }
      if (dragStart.current.scopeKey !== scopeKey) { dragStart.current = null; return }
      const next = applyLevelDrag(current, id, price, priceStep)
      if (phase === 'move') setDragPreview({ source: current, entries: next, scopeKey })
      else {
        const after = next.find(item => item.id === ref.entryId)!
        if (JSON.stringify(entry) !== JSON.stringify(after)) {
          recordEntryEdit(`Move ${entry.name} level`, entry, after, dragStart.current.scopeKey)
          latest.current.onEntriesChange?.(next)
        }
        dragStart.current = null
        if (ref.entryId !== selectedId) onSelect(ref.entryId)
      }
    },
    onLevelEdit: (id: string, anchor: { x: number; y: number }) => {
      const ref = parseOverlayId(id)
      if (!ref) return
      setLevelEdit({ id, anchor })
      if (ref.entryId !== selectedId) onSelect(ref.entryId)
    },
  }
  const selectedEntry = entries.find(entry => entry.id === selectedId) ?? entries[0]
  const selectedTag = selectedEntry ? levelTag(entries, { entryId: selectedEntry.id, kind: 'entry' }) : ''
  const selectedName = entries.length > 1 && selectedEntry ? selectedEntry.name : 'the entry'
  /**
   * Places a level at a price picked on the chart, one pick per tool use. The entry tool prices the
   * selected entry when it has no price yet, and otherwise adds a new entry at that price.
   */
  const pickPrice = (price: number) => {
    const current = latest.current.entries
    const entry = current.find(item => item.id === selectedEntry?.id)
    if (!planTool || !entry) return
    setPlanTool(null)
    if (planTool === 'entry' && positivePlanNumber(entry.price) !== null) {
      const added = placeLevel(createNextEntry(current), 'entry', price, priceStep)
      const change = (next: DraftEntry[]) => latest.current.onEntriesChange?.(next)
      change(addEntry(current, added))
      history.push({ label: `Add ${added.name}`,
        undo: () => change(removeEntry(latest.current.entries, added.id)),
        redo: () => change(addEntry(latest.current.entries.filter(item => item.id !== added.id), added)) })
      onSelect(added.id)
      return
    }
    applyEntry(placeLevel(entry, planTool, price, priceStep), planTool === 'entry' ? `Set ${selectedTag} entry price` : `Add ${selectedTag} ${planTool}`)
  }
  const onChartKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const key = event.key.toLowerCase()
    if (planTool && key === 'escape') { event.preventDefault(); setPlanTool(null); return }
    if ((event.metaKey || event.ctrlKey) && (key === 'y' || key === 'z' && event.shiftKey)) { event.preventDefault(); history.redo() }
    else if ((event.metaKey || event.ctrlKey) && key === 'z') { event.preventDefault(); history.undo() }
    else editor.onKeyDown(event)
  }
  // A preferred interval the venue does not serve falls back to the hour, or the venue's first interval.
  const interval = intervals.includes(preferences.interval) ? preferences.interval
    : intervals.includes('1h') ? '1h' : intervals[0] ?? preferences.interval
  const markers = useMemo(() => fillMarkers(fills, interval, zone), [fills, interval, zone])
  const [expanded, setExpanded] = useState(false)
  const expandButton = useRef<HTMLButtonElement>(null)
  const live = source !== null && instrument !== ''
  const overlays = useMemo(() => {
    const plan = planOverlays(chartEntries, selectedId, view, direction, leverage, stopEvidence).map(overlay => ({ ...overlay, priceStep, draggable: overlay.draggable && editable }))
    return liquidation === null ? plan : [...plan, liquidationOverlay(liquidation)]
  }, [chartEntries, selectedId, view, direction, leverage, liquidation, priceStep, editable, stopEvidence])
  // The HTML legend stays committed too, so a wrapping value cannot resize the canvas mid-drag.
  const average = view === 'aggregate' ? averageEntryPrice(entries) : null
  const several = entries.length > 1

  const priced = selectedEntry !== undefined && positivePlanNumber(selectedEntry.price) !== null
  const planTools = editable && selectedEntry ? [
    { id: 'plan:entry', label: 'Add an entry on the chart', description: priced ? 'Click the chart at the price of a new entry.' : `Click the chart at ${selectedName} price.`, icon: <ArrowRightToLine {...planToolIcon} />, available: true },
    { id: 'plan:stop', label: `Add a stop to ${selectedName} on the chart`, description: 'Click the chart at the stop price. Add several for partial stops.', icon: <OctagonX {...planToolIcon} />, available: priced, unavailableReason: 'Set the entry price first' },
    { id: 'plan:target', label: `Add a target to ${selectedName} on the chart`, description: 'Click the chart at the target price. Add several for partial targets.', icon: <Target {...planToolIcon} />, available: priced, unavailableReason: 'Set the entry price first' },
  ] : []
  const selectTool = (id: string) => {
    if (id.startsWith('plan:')) {
      const tool = id.slice(5) as PlanTool
      editor.setTool(null)
      setPlanTool(current => current === tool ? null : tool)
      return
    }
    if (id === 'magnet') setPreferences({ magnet: !preferences.magnet })
    else if (id === 'undo') history.undo()
    else if (id === 'redo') history.redo()
    else if (id === 'clear') editor.clear()
    else if (isDrawingKind(id)) {
      setPlanTool(null)
      editor.setTool(editor.tool === id ? null : id)
      const group = drawingToolGroups.find(item => item.kinds.includes(id))
      if (group && preferences.toolChoice[group.id] !== id) setPreferences({ toolChoice: { ...preferences.toolChoice, [group.id]: id } })
    } else { setPlanTool(null); editor.setTool(null) }
  }
  const railTools = [crosshairTool, ...drawingToolGroups.map((group): ChartToolGroup => ({
    id: group.id, label: group.label, tools: group.kinds.map(kind => drawingToolsByKind[kind]),
    current: preferences.toolChoice[group.id] ?? group.kinds[0]!,
  }))]
  const toggleFavorite = (id: string) => {
    if (!isDrawingKind(id)) return
    const favorites = preferences.drawingFavorites
    setPreferences({ drawingFavorites: favorites.includes(id) ? favorites.filter(kind => kind !== id) : [...favorites, id] })
  }
  const favoriteTools = <div className="chart-favorite-tools" role="group" aria-label="Favorite drawing tools">
    {preferences.drawingFavorites.map(kind => {
      const tool = drawingToolsByKind[kind]
      return <ChartIconButton key={kind} label={tool.label} description={tool.description} icon={tool.icon} pressed={editor.tool === kind} onClick={() => selectTool(kind)} />
    })}
    <Popover.Root>
      <Popover.Trigger asChild>
        <button type="button" className="chart-icon-button chart-favorites-edit" aria-label="Choose favorite tools" title="Choose favorite tools"><Star size={14} aria-hidden="true" /></button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content className="chart-popover chart-tool-flyout" align="start" sideOffset={6} aria-label="Favorite tools">
          <p className="chart-tool-flyout-title">Favorites</p>
          <ul>
            {drawingToolGroups.flatMap(group => group.kinds).map(kind => {
              const tool = drawingToolsByKind[kind]
              const favorite = preferences.drawingFavorites.includes(kind)
              return <li key={kind}>
                <span className="chart-popover-choice">{tool.icon}<span>{tool.label}</span></span>
                <button type="button" className="chart-tool-favorite" aria-pressed={favorite} onClick={() => toggleFavorite(kind)}
                  aria-label={favorite ? `Remove ${tool.label} from favorites` : `Add ${tool.label} to favorites`}>
                  <Star size={13} aria-hidden="true" fill={favorite ? 'currentColor' : 'none'} /></button>
              </li>
            })}
          </ul>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  </div>
  const rail = <ChartToolRail tools={railTools} active={planTool ? `plan:${planTool}` : editor.tool ?? 'crosshair'}
    favorites={preferences.drawingFavorites} onToggleFavorite={toggleFavorite}
    groups={[{ label: `Plan levels for ${selectedEntry?.name ?? 'the entry'}`, tools: planTools }]} onSelect={selectTool} footer={[
    { id: 'magnet', label: 'Snap to candles', description: 'Pulls anchors to a nearby open, high, low or close.', icon: drawingUtilityIcons.magnet, available: true, pressed: preferences.magnet },
    { id: 'undo', label: history.undoLabel ? `Undo: ${history.undoLabel}` : 'Undo', icon: drawingUtilityIcons.undo, available: history.canUndo, pressed: false },
    { id: 'redo', label: history.redoLabel ? `Redo: ${history.redoLabel}` : 'Redo', icon: drawingUtilityIcons.redo, available: history.canRedo, pressed: false },
    { id: 'clear', label: `Clear unlocked ${instrument} drawings`, icon: drawingUtilityIcons.clear, available: editor.clearable, pressed: false, destructive: true },
  ]} unavailableReason="Nothing to change" />
  const planHint = planTool && `${planTool === 'entry' ? priced ? 'Click to add an entry' : `Click to set ${selectedName} price` : `Click to add a ${planTool} to ${selectedName}`}. Esc cancels.`
  const drawingBar = planHint ? <div className="chart-drawing-bar" role="status">{planHint}</div>
    : editor.tool ? <div className="chart-drawing-bar" role="status">{drawingToolHints[editor.tool]} Esc cancels.</div>
    : editor.selected ? <DrawingEditBar drawing={editor.selected} label={drawingToolLabels[editor.selected.kind]} defaultColor={drawingColors[0]}
      onStyle={style => editor.setStyle(editor.selected!.id, style)} onLocked={locked => editor.setLocked(editor.selected!.id, locked)}
      onDelete={editor.remove} onText={text => editor.setText(editor.selected!.id, text)} onTextFocus={editor.beginTextEdit} onTextBlur={editor.endTextEdit} /> : null
  const editedRef = levelEdit ? parseOverlayId(levelEdit.id) : null
  const editedIndex = editedRef ? entries.findIndex(entry => entry.id === editedRef.entryId) : -1
  const levelEditor = levelEdit && editedIndex >= 0 && editable ? <LevelEditor key={levelEdit.id} entries={entries} entry={entries[editedIndex]!}
    overlayId={levelEdit.id} anchor={levelEdit.anchor} direction={direction} leverage={leverage} priceStep={priceStep} onApply={applyEntry}
    onClose={() => setLevelEdit(null)} /> : null
  const drawingProps = {
    drawings, selectedDrawingId: editor.selectedId, tool: editor.tool, magnet: preferences.magnet,
    pricePicker: planTool !== null, onPricePick: pickPrice,
    onDrawingCreate: editor.create, onDrawingChange: editor.change, onDrawingSelect: editor.select, onDrawingCancel: editor.cancelGesture, onKeyDown: onChartKeyDown,
    indicators: preferences.indicators, onIndicatorsChange: (indicators: typeof preferences.indicators) => setPreferences({ indicators }),
    ...levelProps,
  }
  const indicatorMenu = <IndicatorSettingsPopover settings={preferences.indicators} onChange={indicators => setPreferences({ indicators })}>
    <ChartIconButton label="Indicators" description="Moving averages, volume and RSI." icon={<ChartSpline size={15} aria-hidden="true" />} />
  </IndicatorSettingsPopover>

  return <section className="panel chart-panel" aria-label="Chart" data-testid="chart-panel">
    {live ? <LiveChart key={`${source.accountId}|${instrument}`} source={source} instrument={instrument} interval={interval}
      instrumentName={instrumentName} caption={`${instrumentName} · ${venue ? `${venue} ` : ''}trade candles`} venue={venue} onCapture={onCapture} onShowEvidence={onShowEvidence}
      timeframes={<TimeframeBar value={interval} favorites={preferences.favorites} available={intervals}
        onChange={next => setPreferences({ interval: next })} onFavoritesChange={favorites => setPreferences({ favorites })} />}
      // A trade-dated chart shows the trade's candles; a live tail would join today onto them.
      liveUpdates={preferences.live && streamable && !tradeDates} streamable={streamable && !tradeDates} marketContext={marketContext} onLiveUpdatesChange={on => setPreferences({ live: on })}
      markers={markers} tradeDates={tradeDates}
      viewMenu={<>{favoriteTools}<ChartToolbarDivider />{indicatorMenu}</>} rail={rail} drawingBar={<>{drawingBar}{levelEditor}</>} drawingProps={drawingProps} overlays={overlays} createAdapter={createAdapter} expanded={expanded} expandButton={expandButton}
      onExpandedChange={setExpanded} onDialogClosed={() => expandButton.current?.focus({ preventScroll: true })} />
      : <>
        <ChartToolbar label="Chart controls" end={<>
          <span className="chart-status"><span className="chart-source">{instrument ? `${instrumentName}${venue ? ` · ${venue}` : ''}` : 'No perpetual instrument selected'}</span></span>
          <ChartIconButton label="Capture chart" icon={<Camera size={15} aria-hidden="true" />} disabled disabledReason="Needs market data" />
          <ChartIconButton label="Expand chart" icon={<Maximize2 size={15} aria-hidden="true" />} disabled disabledReason="Needs market data" />
        </>} />
        <div className="chart-placeholder">
          <span className="chart-placeholder-icon" aria-hidden="true"><ChartNoAxesCombined size={27} /></span>
          <strong>{instrument ? 'No market data for this instrument' : 'Choose an instrument'}</strong>
          <p>{instrument ? 'Manual accounts and labels have no candle provider yet. Planned levels stay in the editor.' : 'Select an exchange account and perpetual to load candles.'}</p>
        </div>
      </>}
    {/* One entry is both the whole plan and the selection, so the view choice only appears with several. */}
    {several && <div className="chart-legend" role="group" aria-label="Planned entries">
      <button type="button" className="chart-legend-all" aria-pressed={view === 'aggregate'} aria-label="All entries"
        title="Show all entries and the average entry" onClick={() => setView('aggregate')}>
        <Layers size={14} aria-hidden="true" /><span>All</span></button>
      {entries.map((entry) => <button key={entry.id} type="button" aria-pressed={view === 'selected' && entry.id === selectedId}
        data-selected={entry.id === selectedId} title={`Show only ${entry.name}`}
        style={{ '--entry-color': entry.color } as CSSProperties} onClick={() => { setView('selected'); onSelect(entry.id) }}>
        <i aria-hidden="true" /><span>{entry.name}</span>
        <small>{entry.share ? `${entry.share}%` : 'Share not set'}</small>
      </button>)}
      {average !== null && <span className="chart-legend-average"><i aria-hidden="true" />Average entry <strong>{formatDraggedPrice(average)}</strong></span>}
    </div>}
  </section>
}

function LiveChart({ source, instrument, instrumentName, interval, caption, venue, onCapture, onShowEvidence, liveUpdates, streamable, onLiveUpdatesChange, marketContext, timeframes, viewMenu, rail, drawingBar, drawingProps, overlays, createAdapter, expanded, expandButton, onExpandedChange, onDialogClosed, markers, tradeDates }: {
  source: ChartSource
  instrument: string
  instrumentName: string
  interval: CandleInterval
  caption: string
  venue: string | null
  onCapture?: ((image: Blob, context: string) => string | null) | undefined
  onShowEvidence?: (() => void) | undefined
  liveUpdates: boolean
  streamable: boolean
  marketContext: boolean
  onLiveUpdatesChange: (on: boolean) => void
  timeframes: ReactNode
  viewMenu: ReactNode
  rail: ReactNode
  drawingBar: ReactNode
  drawingProps: Partial<ComponentProps<typeof CandleChart>>
  overlays: PriceOverlay[]
  createAdapter?: ChartAdapterFactory | undefined
  expanded: boolean
  expandButton: RefObject<HTMLButtonElement | null>
  onExpandedChange: (open: boolean) => void
  onDialogClosed: () => void
  markers: readonly ChartMarker[]
  tradeDates: boolean
}) {
  const zone = useDisplayZone()
  // An ended trade opens on its dates: candles end shortly after its last fill, and the view frames it.
  const focus = useMemo(() => tradeDates ? tradeFocus(markers, interval) : null, [tradeDates, markers, interval])
  const data = useCandles({ ...source, instrument, interval, anchor: focus?.to ?? null })
  // Market/status ticks recreate loadOlder in the current hook. Forward to the latest function
  // without changing CandleChart's props; ordinary memo still checks every callback.
  const loadOlder = useRef(data.loadOlder)
  useLayoutEffect(() => { loadOlder.current = data.loadOlder })
  const onNeedOlder = useCallback(() => loadOlder.current(), [])
  const onExpand = useCallback(() => onExpandedChange(true), [onExpandedChange])
  const market = useMarketContext(source.api, source.accountId, instrument, marketContext)
  const refresh = () => { data.refresh(); market.refresh() }
  // One stream per chart; the expanded dialog renders from the same state.
  const live = useLiveMarket({
    api: source.api, accountId: source.accountId, instrument, interval, enabled: liveUpdates, ready: data.status === 'ready',
    onCandle: data.upsert, onContext: market.apply, refreshCandles: data.revalidate, refreshContext: market.refresh,
  })
  // The inline and expanded charts each own a control: closing the dialog unmounts its chart after the
  // inline one has remounted, and a shared ref would then be cleared under the visible chart.
  const inlineChart = useRef<CandleChartControl>(null)
  const dialogChart = useRef<CandleChartControl>(null)
  const [capture, setCapture] = useState<{ busy: boolean; message: string; failed: boolean }>({ busy: false, message: '', failed: false })
  useEffect(() => {
    if (!capture.message) return
    const timer = setTimeout(() => setCapture(current => ({ ...current, message: '' })), 8000)
    return () => clearTimeout(timer)
  }, [capture.message])
  const latestCapture = useRef(onCapture)
  useEffect(() => { latestCapture.current = onCapture })
  const captureChart = async () => {
    if (!onCapture || capture.busy) return
    const now = new Date()
    const context = [instrumentName, venue, intervalName(interval)].filter(Boolean).join(' · ')
    setCapture({ busy: true, message: '', failed: false })
    let image: Blob | null
    try {
      // The canvas has no legend, so the caption names the indicators as shown.
      const control = expanded ? dialogChart.current : inlineChart.current
      image = control ? await control.capture([context, formatTime(now, zone), control.indicatorSummary(), 'Planned levels are not fills']
        .filter(Boolean).join(' · ')) : null
    } catch {
      image = null
    }
    const problem = !image ? 'The chart could not be captured. Browser privacy or fingerprinting protection can block chart images; allow canvas access for this site and try again.'
      : latestCapture.current?.(image, context) ?? null
    setCapture({ busy: false, message: problem ?? 'Capture added to the Evidence tab of the journal.', failed: problem !== null })
  }
  const showEvidence = () => {
    onExpandedChange(false)
    onShowEvidence?.()
  }
  const updatedAt = [data.retrievedAt, live.lastEventAt].filter((value): value is string => value !== null)
    .reduce<string | null>((latest, value) => latest === null || Date.parse(value) > Date.parse(latest) ? value : latest, null)
  const described = market.context ? describeMarket(market.context) : null
  const stats: ChartStat[] = [
    { label: 'Mark', value: market.context?.markPrice ?? '—' },
    { label: 'Oracle', value: market.context?.oraclePrice ?? '—' },
    { label: '24h change', value: described?.change ?? '—', ...(described && described.direction !== 'flat' ? { tone: described.direction } : {}) },
    { label: '24h volume', hint: 'notional', value: described?.volume ?? '—' },
    { label: 'Open interest', value: described ? `${described.openInterest} ${instrument}` : '—' },
    { label: 'Funding', hint: '1h', value: described?.funding ?? '—' },
  ]
  // The instrument is already chosen and shown in the play fields, so the header carries statistics only.
  const header = marketContext && <ChartHeader stats={stats} statsLabel={`${instrument} market statistics`}
    notice={market.error && <p className="chart-header-error" role="alert">{market.error}</p>} />
  const status = data.status === 'loading' ? 'Loading candles…'
    : data.refreshing ? `Refreshing…${updatedAt ? ` Updated ${formatTime(updatedAt, zone, 'time-seconds')} (cached)` : ''}`
      : data.loadingOlder ? 'Loading older candles…'
        : updatedAt ? `Updated ${formatTime(updatedAt, zone, 'time-seconds')}` : ''
  const toolbar = (inDialog: boolean) => <ChartToolbar label="Chart controls" end={<>
    {/* Streamed updates change the time every few seconds; only announce it when updates are manual. */}
    <span className="chart-status" role="status" aria-live={live.state === 'off' ? 'polite' : 'off'} title={`${caption}${status ? ` · ${status}` : ''}`}>
      {/* The instrument is shown in the play fields; the source stays available as a tooltip and to screen readers. */}
      <span className="chart-source sr-only">{caption} · </span>{status && <span>{status}</span>}</span>
    {streamable && <LiveIndicator state={live.state} onToggle={onLiveUpdatesChange} />}
    <ChartIconButton label="Refresh" icon={<RefreshCw size={15} aria-hidden="true" className={data.refreshing ? 'is-spinning' : ''} />}
      onClick={refresh} disabled={data.status === 'loading' || data.refreshing} aria-busy={data.refreshing} />
    <ChartToolbarDivider />
    <ChartIconButton label="Capture chart" icon={<Camera size={15} aria-hidden="true" />} onClick={() => void captureChart()}
      disabled={!onCapture || data.status !== 'ready' || !data.candles.length || capture.busy} disabledReason={capture.busy ? 'Capturing' : 'Needs loaded candles'} aria-busy={capture.busy} />
    {!inDialog && <ChartIconButton ref={expandButton} label="Expand chart" icon={<Maximize2 size={15} aria-hidden="true" />} onClick={() => onExpandedChange(true)} />}
  </>}>
    {timeframes}
    <ChartToolbarDivider />
    {viewMenu}
  </ChartToolbar>
  const chartProps = {
    // The anchor is part of the view: candles for the trade's dates arrive as a reset, which applies the focus.
    candles: data.candles, overlays, viewKey: `${instrument}|${interval}|${focus?.to ?? 'live'}`, markers, focus,
    label: `${instrument} ${intervalName(interval)} trade candles with planned levels. Edit levels in the entry editor.`,
    onNeedOlder,
    ...drawingProps,
    ...(createAdapter ? { createAdapter } : {}),
  }
  const stage = (testId: string) => <div className="chart-stage">
    {rail}
    <div className="chart-body" data-testid={testId}>
      <CandleChart {...chartProps} controlRef={testId === 'chart-body' ? inlineChart : dialogChart}
        {...(testId === 'chart-body' ? { onExpand } : {})} />
      {drawingBar}
      {data.status === 'loading' && <p className="chart-state">Loading {instrument} candles…</p>}
      {data.status === 'error' && <div className="chart-state" role="alert"><p>{data.error}</p><Button type="button" size="sm" variant="outline" onClick={data.refresh}>Try again</Button></div>}
      {data.status === 'ready' && !data.candles.length && <p className="chart-state">The venue returned no candles for this window.</p>}
    </div>
  </div>
  const notices = <>
    {live.notice && <p className="chart-notice" role="status">{live.notice} Turn Live off and on to reconnect.</p>}
    {capture.message && <p className={capture.failed ? 'chart-notice' : 'chart-notice chart-notice-success'} role={capture.failed ? 'alert' : 'status'}>{capture.message}
      {!capture.failed && onShowEvidence && <> <button type="button" onClick={showEvidence}>Show</button></>}</p>}
    {data.status === 'ready' && data.error && <p className="chart-notice" role="alert">Refresh failed: {data.error} Showing the previous candles.</p>}
    {data.olderError && <p className="chart-notice" role="alert">Older candles failed to load. <button type="button" onClick={data.retryOlder}>Retry</button></p>}
    {data.localHistoryLimit && <p className="chart-notice">Local history limit reached. Older paging is stopped at 5,000 candles.</p>}
    {data.historyExhausted && data.candles.length > 0 && <p className="chart-notice">Start of available venue history. {data.notice}</p>}
  </>

  return <>
    {expanded ? <div className="chart-body chart-body-parked"><p className="chart-state">Chart open in the expanded view.</p></div>
      : <>{header}{toolbar(false)}{stage('chart-body')}{notices}</>}
    <Dialog open={expanded} onOpenChange={onExpandedChange}>
      <DialogContent className="chart-dialog plays-chart-dialog" onCloseAutoFocus={event => { event.preventDefault(); onDialogClosed() }}>
        <DialogTitle className="sr-only">{instrument} chart</DialogTitle>
        <DialogDescription className="sr-only">The same candles, planned levels and selection. Escape returns to the workspace.</DialogDescription>
        {header}
        {toolbar(true)}
        {stage('chart-dialog-body')}
        {notices}
      </DialogContent>
    </Dialog>
  </>
}
