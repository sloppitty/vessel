import { memo, useEffect, useImperativeHandle, useMemo, useRef, useState, type KeyboardEvent, type Ref } from 'react'
import type { ChartDrawing, DrawingKind } from './drawings'
import { IndicatorOverlay } from './IndicatorControls'
import { computeIndicators, describeIndicators, type IndicatorSettings, type PaneLayout } from './indicators'
import { createLazyLightweightAdapter } from './lazy'
import { useDisplayZone } from '@/lib/display-zone'
import type { ChartAdapter, ChartAdapterFactory, ChartCallbacks, ChartCandle, ChartFocus, ChartMarker, PriceOverlay } from './types'
import './chart.css'

const noDrawings: readonly ChartDrawing[] = []
const noMarkers: readonly ChartMarker[] = []

/** Imperative actions for the feature that owns the chart. */
export interface CandleChartControl {
  capture(caption: string): Promise<Blob | null>
  /** The indicators as shown, for a caption; empty without indicators. */
  indicatorSummary(): string
}

/**
 * Reusable candle chart. Features supply candles, price overlays and drawings; the renderer stays
 * behind the adapter so it never sees plays, journals or execution data.
 */
export const CandleChart = memo(function CandleChart({
  candles, overlays, viewKey, label, drawings = noDrawings, selectedDrawingId = null, tool = null, magnet = false, pricePicker = false,
  markers = noMarkers, focus = null,
  onKeyDown, createAdapter = createLazyLightweightAdapter, controlRef, indicators, onIndicatorsChange, onExpand, ...handlers
}: {
  candles: readonly ChartCandle[]
  overlays: readonly PriceOverlay[]
  /** Changing the key (instrument, interval) re-anchors the view at the latest candle. */
  viewKey: string
  label: string
  drawings?: readonly ChartDrawing[]
  selectedDrawingId?: string | null
  tool?: DrawingKind | null
  magnet?: boolean
  /** Clicks report a price through `onPricePick`, e.g. to place a plan level. */
  pricePicker?: boolean
  onKeyDown?: (event: KeyboardEvent<HTMLDivElement>) => void
  createAdapter?: ChartAdapterFactory
  controlRef?: Ref<CandleChartControl>
  /** Moving averages, volume and RSI; omitted, the chart shows candles only. */
  indicators?: IndicatorSettings | undefined
  onIndicatorsChange?: ((next: IndicatorSettings) => void) | undefined
  /** Opens a larger chart; offered on panes too short to plot. */
  onExpand?: (() => void) | undefined
  /** Fills drawn at their time and price, with a tooltip on hover (phase 1, 1.1b). */
  markers?: readonly ChartMarker[]
  /** The range to open on instead of the latest candles, e.g. a closed trade's dates. */
  focus?: ChartFocus | null
} & { [K in keyof Omit<ChartCallbacks, 'onCrosshairMove' | 'onPaneLayout' | 'onMarkerHover'>]?: ChartCallbacks[K] | undefined }) {
  const container = useRef<HTMLDivElement>(null)
  const adapter = useRef<ChartAdapter | null>(null)
  const shownKey = useRef<string | null>(null)
  const callbacks = useRef(handlers)
  useEffect(() => { callbacks.current = handlers })
  const [panes, setPanes] = useState<PaneLayout[]>([])
  const [hoverTime, setHoverTime] = useState<number | null>(null)
  const [hoverMarker, setHoverMarker] = useState<{ id: string; x: number; y: number } | null>(null)
  const shown = useRef({ indicators, panes })
  useEffect(() => { shown.current = { indicators, panes } })
  useImperativeHandle(controlRef, () => ({
    capture: caption => adapter.current?.capture(caption) ?? Promise.resolve(null),
    indicatorSummary: () => {
      const { indicators: settings, panes: layout } = shown.current
      return settings ? describeIndicators(settings, Object.fromEntries(layout.map(pane => [pane.id, pane.effectiveSize]))) : ''
    },
  }), [])

  useEffect(() => {
    const created = createAdapter(container.current!, {
      onLevelSelect: id => callbacks.current.onLevelSelect?.(id),
      onLevelDrag: (id, price, phase) => callbacks.current.onLevelDrag?.(id, price, phase),
      onNeedOlder: () => callbacks.current.onNeedOlder?.(),
      onDrawingCreate: drawing => callbacks.current.onDrawingCreate?.(drawing),
      onDrawingChange: (drawing, phase) => callbacks.current.onDrawingChange?.(drawing, phase),
      onDrawingSelect: id => callbacks.current.onDrawingSelect?.(id),
      onDrawingCancel: () => callbacks.current.onDrawingCancel?.(),
      onLevelCancel: () => callbacks.current.onLevelCancel?.(),
      onLevelEdit: (id, anchor) => callbacks.current.onLevelEdit?.(id, anchor),
      onPricePick: price => callbacks.current.onPricePick?.(price),
      onCrosshairMove: setHoverTime,
      onPaneLayout: setPanes,
      onMarkerHover: (id, anchor) => setHoverMarker(id && anchor ? { id, ...anchor } : null),
    })
    adapter.current = created
    shownKey.current = null
    return () => {
      created.destroy()
      adapter.current = null
    }
  }, [createAdapter])

  // The display zone first, then the focus before the candles, so a reset opens on it.
  const zone = useDisplayZone()
  useEffect(() => { adapter.current?.setTimeZone(zone) }, [zone, createAdapter])
  useEffect(() => { adapter.current?.setFocus(focus) }, [focus, createAdapter])
  useEffect(() => {
    const reset = shownKey.current !== viewKey
    adapter.current?.setCandles(candles, reset)
    if (candles.length) shownKey.current = viewKey
  }, [candles, viewKey, createAdapter])

  useEffect(() => { adapter.current?.setOverlays(overlays) }, [overlays, createAdapter])
  useEffect(() => { adapter.current?.setMarkers(markers) }, [markers, createAdapter])
  const tooltip = hoverMarker && markers.find(marker => marker.id === hoverMarker.id)
  useEffect(() => { adapter.current?.setDrawings(drawings, selectedDrawingId) }, [drawings, selectedDrawingId, createAdapter])
  useEffect(() => { adapter.current?.setDrawingTool(tool, magnet) }, [tool, magnet, createAdapter])
  useEffect(() => { adapter.current?.setPricePicker(pricePicker) }, [pricePicker, createAdapter])
  const indicatorView = useMemo(() => indicators ? computeIndicators(candles, indicators) : null, [candles, indicators])
  useEffect(() => { if (indicatorView) adapter.current?.setIndicators(indicatorView) }, [indicatorView, createAdapter])
  const timeIndex = useMemo(() => new Map(candles.map((candle, index) => [candle.time, index])), [candles])
  const hoverIndex = hoverTime === null ? null : timeIndex.get(hoverTime) ?? null

  // Focusable so Delete, Escape and undo shortcuts reach the feature while the pointer works on the canvas.
  return <div className="candle-chart-frame">
    <div ref={container} className="candle-chart" role="application" aria-roledescription="chart" aria-label={label}
      tabIndex={0} onKeyDown={onKeyDown} />
    {tooltip && hoverMarker && <div className="chart-marker-tooltip" role="tooltip" style={{ left: hoverMarker.x, top: hoverMarker.y }}>{tooltip.tooltip}</div>}
    {markers.length > 0 && <ul className="sr-only" aria-label="Fills on the chart">{markers.map(marker => <li key={marker.id}>{marker.tooltip}</li>)}</ul>}
    {indicators && indicatorView && onIndicatorsChange && candles.length > 0 &&
      <IndicatorOverlay settings={indicators} view={indicatorView} panes={panes} hoverIndex={hoverIndex} onChange={onIndicatorsChange} onExpand={onExpand} />}
  </div>
})
