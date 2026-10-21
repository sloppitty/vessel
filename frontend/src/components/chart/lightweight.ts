import {
  CandlestickSeries, ColorType, CrosshairMode, HistogramSeries, LineSeries, LineStyle, createChart, createSeriesMarkers,
  type AutoscaleInfo, type IChartApi, type IPrimitivePaneRenderer, type MouseEventParams, type IPrimitivePaneView, type PriceFormat, type ISeriesApi,
  type ISeriesPrimitive, type ISeriesPrimitiveAxisView, type Logical, type LogicalRange, type PrimitiveHoveredItem,
  type SeriesAttachedParameter, type Time, type UTCTimestamp, TickMarkType,
} from 'lightweight-charts'
import type { DisplayZone } from '@/lib/display-zone'
import { formatIn, formatTime } from '@/lib/time'
import { DrawingController, type DrawingSpace, type DrawingTheme } from './drawingController'
import { TimeIndex, snapPrice } from './drawings'
import { FrameEdit, roundedLevelPrice } from './frameEdit'
import { createSeriesWriter } from './seriesWriter'
import { candleEnd } from './intervals'
import type { IndicatorPaneId, IndicatorView, PaneLayout, PaneSize } from './indicators'
import { readableInk } from './ink'
import { barHeight, solvePaneLayout, type SolvedLayout } from './paneLayout'
import type { ChartAdapter, ChartAdapterFactory, ChartCandle, ChartFocus, ChartMarker, PriceOverlay } from './types'

type DrawTarget = Parameters<IPrimitivePaneRenderer['draw']>[0]

interface ChartTheme {
  background: string
  foreground: string
  muted: string
  border: string
  grid: string
  font: string
  candleUp: string
  candleUpEdge: string
  candleDown: string
  candleDownEdge: string
  drawing: string
  positive: string
  negative: string
  card: string
}

const hitTolerance = 6
/** Levels within ±50% of the latest close widen the price scale; farther ones stay off-scale. */
const autoscaleReach = 0.5
/** Media-pixel area that Lightweight Charts uses for its attribution logo. */
const logoZone = { width: 60, height: 46 }

function readTheme(element: HTMLElement): ChartTheme {
  const style = getComputedStyle(element)
  const token = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback
  return {
    background: token('--background', '#0f1114'),
    foreground: token('--foreground', '#e9edf2'),
    muted: token('--muted-foreground', '#abb3c0'),
    border: token('--border', '#394350'),
    grid: token('--secondary', '#1e2229'),
    font: style.fontFamily || 'system-ui, sans-serif',
    // Monochrome candles keep red and green free for stops and targets.
    candleUp: token('--chart-candle-up', '#e9edf2'),
    candleUpEdge: token('--chart-candle-up-edge', '#e9edf2'),
    candleDown: token('--chart-candle-down', '#050607'),
    candleDownEdge: token('--chart-candle-down-edge', '#9aa3b0'),
    drawing: token('--chart-drawing', '#8fb8ff'),
    positive: token('--positive', '#a9d6b6'),
    negative: token('--negative', '#f2b3ac'),
    card: token('--card', '#171a1f'),
  }
}

/** Resolves `var(--token)` colors against the chart container; `clear` forgets them after a theme change. */
function colorResolver(element: HTMLElement) {
  const cache = new Map<string, string>()
  const resolve = (color: string) => {
    const name = /^var\((--[\w-]+)\)$/.exec(color.trim())?.[1]
    if (!name) return color
    if (!cache.has(name)) cache.set(name, getComputedStyle(element).getPropertyValue(name).trim() || '#abb3c0')
    return cache.get(name)!
  }
  return Object.assign(resolve, { clear: () => cache.clear() })
}

/** Decimal places needed to show the loaded prices without rounding them away. */
export function pricePrecision(candles: readonly ChartCandle[]) {
  let precision = 0
  for (const candle of candles.slice(-200)) {
    for (const value of [candle.open, candle.high, candle.low, candle.close]) {
      const text = String(value)
      if (text.includes('e')) return 8
      precision = Math.max(precision, text.split('.')[1]?.length ?? 0)
    }
  }
  return Math.min(precision, 8)
}

/** Draws planned levels on the main pane and price axis, and reports hits for selection and dragging. */
class LevelsPrimitive implements ISeriesPrimitive<Time> {
  private overlays: readonly PriceOverlay[] = []
  private raw: readonly PriceOverlay[] = []
  private preview: { id: string; price: number } | null = null

  dragPreview(preview: { id: string; price: number } | null) {
    this.preview = preview
    this.set(this.raw)
  }
  private axisViews: readonly ISeriesPrimitiveAxisView[] = []
  private series: ISeriesApi<'Candlestick'> | null = null
  private requestUpdate: (() => void) | null = null
  private readonly views: readonly IPrimitivePaneView[]
  /** Last drawn label tags in pane pixels, for click-to-edit. */
  private tags: { id: string; left: number; right: number; top: number; bottom: number }[] = []

  constructor(private readonly theme: ChartTheme, private readonly resolve: ReturnType<typeof colorResolver>) {
    this.views = [{ zOrder: () => 'top', renderer: () => ({ draw: target => this.draw(target) }) }]
  }

  attached({ series, requestUpdate }: SeriesAttachedParameter<Time>) {
    this.series = series as ISeriesApi<'Candlestick'>
    this.requestUpdate = requestUpdate
  }

  detached() {
    this.series = null
    this.requestUpdate = null
  }

  set(overlays: readonly PriceOverlay[]) {
    this.raw = overlays
    // Selected levels are drawn last so they stay on top of overlapping aggregate levels.
    this.overlays = [...overlays]
      .sort((a, b) => Number(a.emphasis === 'selected') - Number(b.emphasis === 'selected'))
      .map(overlay => ({ ...overlay, ...(this.preview?.id === overlay.id ? { price: this.preview.price } : {}), color: this.resolve(overlay.color), ...(overlay.accent ? { accent: this.resolve(overlay.accent) } : {}) }))
    // An unconfirmed level (e.g. a stop not seen resting at the venue) is hollow: its colour on the chart background.
    this.axisViews = this.overlays.map(overlay => ({
      coordinate: () => this.y(overlay.price) ?? -1000,
      text: () => this.series?.priceFormatter().format(overlay.price) ?? String(overlay.price),
      textColor: () => overlay.unconfirmed ? overlay.color : readableInk(overlay.color),
      backColor: () => overlay.unconfirmed ? this.theme.background : overlay.color,
    }))
    this.requestUpdate?.()
  }

  /** Re-reads token colors after the theme changes. */
  retheme() {
    this.resolve.clear()
    this.set(this.raw)
  }

  find(id: string) {
    return this.overlays.find(overlay => overlay.id === id)
  }

  /** Nearest level within the hit tolerance, preferring the selected entry's levels. */
  levelAt(y: number) {
    let best: { overlay: PriceOverlay; distance: number } | null = null
    for (const overlay of this.overlays) {
      const levelY = this.y(overlay.price)
      if (levelY === null) continue
      const distance = Math.abs(levelY - y)
      if (distance > hitTolerance) continue
      if (!best || distance < best.distance || distance === best.distance && overlay.emphasis === 'selected') best = { overlay, distance }
    }
    return best
  }

  /** Level whose tag contains the point, if any. */
  tagAt(x: number, y: number) {
    const tag = [...this.tags].reverse().find(item => x >= item.left && x <= item.right && y >= item.top && y <= item.bottom)
    return tag ? { overlay: this.find(tag.id)!, x: tag.right, y: (tag.top + tag.bottom) / 2 } : null
  }

  paneViews() { return this.views }
  priceAxisViews() { return this.axisViews }
  updateAllViews() { /* Coordinates are resolved while drawing. */ }

  /** Latest close; levels far from it are left out of autoscaling so a mismatched plan cannot flatten the candles. */
  reference: number | null = null
  /** Points on a fill marker's shape: levels give way there, so a fill at its planned price keeps its tooltip. */
  yieldAt: (x: number, y: number) => boolean = () => false
  /** The attribution logo sits in the bottom pane, so tags avoid it only while the price pane is the only one. */
  avoidLogo = true

  autoscaleInfo(): AutoscaleInfo | null {
    const reference = this.reference
    const prices = this.overlays.map(overlay => overlay.price)
      .filter(price => reference === null || Math.abs(price / reference - 1) <= autoscaleReach)
    if (!prices.length) return null
    return { priceRange: { minValue: Math.min(...prices), maxValue: Math.max(...prices) } }
  }

  hitTest(x: number, y: number): PrimitiveHoveredItem | null {
    const hit = this.yieldAt(x, y) ? null : this.levelAt(y)
    return hit ? {
      externalId: hit.overlay.id, zOrder: 'top', distance: hit.distance, hitTestPriority: 1,
      cursorStyle: hit.overlay.draggable ? 'ns-resize' : 'pointer',
    } : null
  }

  private y(price: number) {
    return this.series?.priceToCoordinate(price) ?? null
  }

  private draw(target: DrawTarget) {
    target.useBitmapCoordinateSpace(({ context, bitmapSize, horizontalPixelRatio: h, verticalPixelRatio: v }) => {
      context.save()
      context.font = `600 ${Math.round(10 * v)}px ${this.theme.font}`
      context.textBaseline = 'middle'
      const tags: typeof this.tags = []
      for (const overlay of this.overlays) {
        const y = this.y(overlay.price)
        if (y === null) continue
        const selected = overlay.emphasis === 'selected'
        const lineWidth = Math.max(1, Math.round((selected ? 2 : 1) * v))
        const lineY = Math.round(y * v) + (lineWidth % 2 ? 0.5 : 0)
        const alpha = selected || overlay.kind === 'reference' ? 1 : 0.72
        // An unconfirmed level keeps its colour and position but reads as a sparser, fainter line.
        context.globalAlpha = overlay.unconfirmed ? alpha * 0.6 : alpha
        context.strokeStyle = overlay.color
        context.lineWidth = lineWidth
        context.setLineDash(overlay.unconfirmed ? [2 * h, 7 * h] : overlay.kind === 'entry' ? [] : overlay.kind === 'stop' ? [2 * h, 3 * h]
          : overlay.kind === 'reference' ? [10 * h, 3 * h, 2 * h, 3 * h] : [7 * h, 4 * h])
        context.beginPath()
        context.moveTo(0, lineY)
        context.lineTo(bitmapSize.width, lineY)
        context.stroke()
        context.setLineDash([])
        context.globalAlpha = alpha

        const padding = 5 * h
        const tagHeight = Math.round(16 * v)
        const marker = overlay.accent && overlay.accent !== overlay.color ? Math.round(4 * h) : 0
        const tagWidth = Math.ceil(context.measureText(overlay.label).width + padding * 2 + marker)
        const tagY = Math.round(lineY - tagHeight / 2)
        // Keep tags clear of the required TradingView attribution in the bottom-left corner.
        const inLogoZone = this.avoidLogo && tagY + tagHeight > bitmapSize.height - logoZone.height * v
        const tagX = Math.round((inLogoZone ? logoZone.width : 8) * h)
        // A hollow tag (outlined on the chart background) marks an unconfirmed level; a filled one a confirmed or planned level.
        context.fillStyle = overlay.unconfirmed ? this.theme.background : overlay.color
        context.fillRect(tagX, tagY, tagWidth, tagHeight)
        if (overlay.unconfirmed) {
          const edge = Math.max(1, Math.round(v))
          context.lineWidth = edge
          context.strokeStyle = overlay.color
          context.strokeRect(tagX + edge / 2, tagY + edge / 2, tagWidth - edge, tagHeight - edge)
        }
        if (marker) {
          context.fillStyle = overlay.accent!
          context.fillRect(tagX, tagY, marker, tagHeight)
        }
        context.fillStyle = overlay.unconfirmed ? overlay.color : readableInk(overlay.color)
        context.fillText(overlay.label, tagX + marker + padding, lineY)
        if (overlay.kind !== 'reference') tags.push({ id: overlay.id, left: tagX / h, right: (tagX + tagWidth) / h, top: tagY / v, bottom: (tagY + tagHeight) / v })

        // Handles mark the selected entry; other entries' levels still drag from the line.
        if (overlay.draggable && selected) {
          context.globalAlpha = 1
          context.beginPath()
          context.arc(bitmapSize.width - 22 * h, lineY, 4.5 * h, 0, Math.PI * 2)
          context.fillStyle = overlay.color
          context.fill()
          context.lineWidth = Math.max(1, Math.round(2 * h))
          context.strokeStyle = this.theme.background
          context.stroke()
        }
      }
      this.tags = tags
      context.restore()
    })
  }
}

/** Paints user drawings on the chart canvas (so captures include them) and reports hover cursors. */
class DrawingsPrimitive implements ISeriesPrimitive<Time> {
  controller: DrawingController | null = null
  requestUpdate: () => void = () => {}
  private readonly views: readonly IPrimitivePaneView[]

  constructor(private readonly theme: DrawingTheme) {
    this.views = [{ renderer: () => ({ draw: target => target.useMediaCoordinateSpace(({ context }) => this.controller?.render(context, this.theme)) }) }]
  }

  attached({ requestUpdate }: SeriesAttachedParameter<Time>) { this.requestUpdate = requestUpdate }
  detached() { this.requestUpdate = () => {} }
  paneViews() { return this.views }
  updateAllViews() { /* Coordinates are resolved while drawing. */ }

  hitTest(x: number, y: number): PrimitiveHoveredItem | null {
    const cursor = this.controller?.cursor(x, y)
    return cursor ? { externalId: 'drawing', zOrder: 'normal', cursorStyle: cursor, hitTestPriority: 2 } : null
  }
}

/** Pixels within which the magnet pulls an anchor to a candle's open, high, low or close. */
const magnetReach = 12

/** An axis tick in the display zone: the tick's own instant, read there (phase 1, 1.6e). */
function tickLabel(ms: number, type: TickMarkType, zone: DisplayZone) {
  return formatIn(ms, zone, type === TickMarkType.Year ? 'year' : type === TickMarkType.Month ? 'month'
    : type === TickMarkType.DayOfMonth ? 'day-of-month' : type === TickMarkType.TimeWithSeconds ? 'time-seconds' : 'time')
}
const seconds = (time: Time) => typeof time === 'number' ? time * 1000 : null

/** The narrowest a candle may be drawn, in pixels, so a trade of thousands of candles still fits a chart. */
const minimumBarSpacing = 0.05

/** Bars of space right of the last candle when a chart opens. */
const openingRightOffset = 12

/** Space kept above indicator values for the pane's control bar. */
const barRoom = barHeight + 4
const toTime = (ms: number) => Math.floor(ms / 1000) as UTCTimestamp
type IndicatorPoint = { time: number; value: number | null; color?: string }
/** Whitespace preserves the undefined EMA/RSI prefix; only changed points are converted. */
const indicatorWriter = (series: ISeriesApi<'Line'> | ISeriesApi<'Histogram'>) => createSeriesWriter<IndicatorPoint, { time: UTCTimestamp; value?: number; color?: string }>(
  series, (a, b) => a.time === b.time && a.value === b.value && a.color === b.color,
  point => point.value === null ? { time: toTime(point.time) }
    : { time: toTime(point.time), value: point.value, ...(point.color ? { color: point.color } : {}) },
)
const withAlpha = (color: string, alpha: number) => /^#[0-9a-f]{6}$/i.test(color)
  ? `${color}${Math.round(alpha * 255).toString(16).padStart(2, '0')}` : color

export const createLightweightAdapter: ChartAdapterFactory = (container, callbacks) => {
  const theme = readTheme(container)
  const chartColors = () => ({
    layout: {
      background: { type: ColorType.Solid, color: theme.background }, textColor: theme.muted,
      panes: { separatorColor: theme.border, separatorHoverColor: withAlpha(theme.muted, 0.22) },
    },
    grid: { vertLines: { color: theme.grid }, horzLines: { color: theme.grid } },
    rightPriceScale: { borderColor: theme.border },
    timeScale: { borderColor: theme.border },
  } as const)
  const candleColors = () => ({
    upColor: theme.candleUp, downColor: theme.candleDown,
    borderUpColor: theme.candleUpEdge, borderDownColor: theme.candleDownEdge,
    wickUpColor: theme.candleUpEdge, wickDownColor: theme.candleDownEdge,
  })
  const chart: IChartApi = createChart(container, {
    autoSize: true,
    ...chartColors(),
    layout: { ...chartColors().layout, fontFamily: theme.font, fontSize: 11, attributionLogo: true },
    // Room right of the last candle keeps it clear of the price scale labels and level tags when the chart opens.
    // A long trade's whole range must fit: the library's default stops zooming out at half a pixel per candle.
    timeScale: { ...chartColors().timeScale, timeVisible: true, secondsVisible: false, rightOffset: openingRightOffset, minBarSpacing: minimumBarSpacing },
    crosshair: { mode: CrosshairMode.Normal },
  })
  // Times read in the display zone and name it; UTC until the page says otherwise (1.6e).
  let zone: DisplayZone = 'utc'
  const applyZone = () => chart.applyOptions({
    localization: { timeFormatter: (time: Time) => { const ms = seconds(time); return ms === null ? '' : formatTime(ms, zone, 'day-time') } },
    timeScale: { tickMarkFormatter: (time: Time, type: TickMarkType) => { const ms = seconds(time); return ms === null ? null : tickLabel(ms, type, zone) } },
  })
  applyZone()
  const series = chart.addSeries(CandlestickSeries, { ...candleColors(), borderVisible: true })
  const levels = new LevelsPrimitive(theme, colorResolver(container))
  series.attachPrimitive(levels)
  // Fills as buy and sell shapes at their price, never lines, so a fill cannot be read as a level (phase 1, 1.1b).
  const fillMarkers = createSeriesMarkers(series, [])
  let markers: readonly ChartMarker[] = []
  let hoveredMarker = ''
  // Placed fills in candle order, and for each the fills sharing its shape: one price in one candle.
  let placed: { id: string; index: number; price: number }[] = []
  let shapes = new Map<string, readonly string[]>()
  const placeMarkers = () => {
    const times = loaded.map(candle => candle.time)
    const shown = markers.flatMap(marker => {
      // The candle the fill falls in: the latest one at or before it, and only within that candle's own span.
      let low = 0, high = times.length - 1, found = -1
      while (low <= high) {
        const middle = (low + high) >> 1
        if (times[middle]! <= marker.time) { found = middle; low = middle + 1 } else high = middle - 1
      }
      if (found < 0 || marker.time >= candleEnd(times[found]!, marker.span) || candleEnd(times[found]!, marker.span) <= times[found]!) return []
      return [{ index: found, shape: { id: marker.id, time: (times[found]! / 1000) as UTCTimestamp, position: 'atPriceMiddle' as const, price: marker.price,
        shape: marker.side === 'buy' ? 'arrowUp' as const : 'arrowDown' as const, color: marker.side === 'buy' ? theme.positive : theme.negative } }]
    }).sort((a, b) => a.shape.time - b.shape.time)
    placed = shown.map(({ index, shape }) => ({ id: shape.id, index, price: shape.price }))
    const groups = new Map<string, string[]>()
    for (const item of placed) {
      const key = `${item.index} ${item.price}`
      const group = groups.get(key)
      if (group) group.push(item.id); else groups.set(key, [item.id])
    }
    shapes = new Map(placed.map(item => [item.id, groups.get(`${item.index} ${item.price}`)!]))
    fillMarkers.setMarkers(shown.map(item => item.shape))
  }
  /** Whether a fill marker's shape holds the point, sized as the library draws it, with its few pixels of tolerance. */
  const onMarker = (x: number, y: number) => {
    if (!placed.length) return false
    const scale = chart.timeScale()
    const first = scale.logicalToCoordinate(0 as Logical), second = scale.logicalToCoordinate(1 as Logical)
    if (first === null || second === null) return false
    const half = Math.min(Math.max(second - first, 12), 30) / 2 + 3
    // Whole bars that may hold a marker within reach (the library rounds up to a bar); each is then measured exactly.
    const from = scale.coordinateToLogical(x - half), to = scale.coordinateToLogical(x + half), visible = scale.getVisibleLogicalRange()
    if (from === null || to === null || visible === null) return false
    // The first placed fill at or after (or, with `after`, past) a bar.
    const bound = (bar: number, after = false) => {
      let low = 0, high = placed.length
      while (low < high) {
        const middle = (low + high) >> 1
        if (after ? placed[middle]!.index <= bar : placed[middle]!.index < bar) low = middle + 1; else high = middle
      }
      return low
    }
    // Only the markers the library draws: those on the visible bars and one more beyond each side.
    let drawn = bound(Math.floor(visible.from)), end = bound(Math.ceil(visible.to), true)
    if (drawn > 0 && drawn < placed.length) drawn--
    if (end > 0 && end < placed.length) end++
    for (let at = Math.max(bound(from), drawn); at < end && placed[at]!.index <= to; at++) {
      const markerX = scale.logicalToCoordinate(placed[at]!.index as Logical), markerY = series.priceToCoordinate(placed[at]!.price)
      if (markerX !== null && markerY !== null && Math.abs(markerX - x) <= half && Math.abs(markerY - y) <= half) return true
    }
    return false
  }
  levels.yieldAt = onMarker
  let focus: ChartFocus | null = null
  // The chart can only show loaded candles: while the focus starts before them, it is applied again as older pages
  // arrive, until it fits or the owner moves the view.
  let focusPending = false
  const applyFocus = () => {
    if (!focus || !loaded.length) return
    chart.timeScale().setVisibleRange({ from: (focus.from / 1000) as UTCTimestamp, to: (focus.to / 1000) as UTCTimestamp })
    focusPending = focus.from < loaded[0]!.time
    // The trade starts before the loaded candles: ask for the older page directly, since a wide range can leave the
    // view's left edge, which otherwise asks for it, off screen. Loading stops on its own when no older candles come.
    if (focusPending) callbacks.onNeedOlder()
  }
  const keepView = () => { focusPending = false }
  container.addEventListener('pointerdown', keepView, true)
  container.addEventListener('wheel', keepView, { capture: true, passive: true })
  let candleCount = 0
  let loaded: readonly ChartCandle[] = []
  let index = new TimeIndex([])
  const writeCandles = createSeriesWriter<ChartCandle, { time: UTCTimestamp; open: number; high: number; low: number; close: number }>(
    series, (a, b) => a.time === b.time && a.open === b.open && a.high === b.high && a.low === b.low && a.close === b.close && a.volume === b.volume,
    candle => ({ time: toTime(candle.time), open: candle.open, high: candle.high, low: candle.low, close: candle.close }),
  )

  /**
   * The library converts only whole bars (x → bar rounds up, a fractional bar → 0), which would tie every
   * anchor to a candle. Bars are evenly spaced, so drawings convert linearly from bars 0 and 1.
   */
  const barAxis = () => {
    const scale = chart.timeScale()
    const origin = scale.logicalToCoordinate(0 as Logical)
    const next = scale.logicalToCoordinate(1 as Logical)
    return origin === null || next === null || next === origin ? null : { origin, spacing: next - origin }
  }
  const floatLogical = (x: number) => {
    const axis = barAxis()
    return axis ? (x - axis.origin) / axis.spacing : chart.timeScale().coordinateToLogical(x)
  }
  const space: DrawingSpace = {
    x: time => {
      const logical = index.logical(time)
      const axis = barAxis()
      return logical === null || !axis ? null : axis.origin + logical * axis.spacing
    },
    y: price => series.priceToCoordinate(price),
    point: (x, y, snap) => {
      const logical = floatLogical(x)
      const price = series.coordinateToPrice(y)
      if (logical === null || price === null || !Number.isFinite(price) || price <= 0) return null
      const candle = snap ? loaded[index.candleIndex(logical) ?? -1] : undefined
      if (candle) {
        // Snap only near a candle price, so a click away from the candles keeps the price under the cursor.
        const snapped = snapPrice(candle, price)
        const snappedY = series.priceToCoordinate(snapped)
        return { time: candle.time, price: snappedY !== null && Math.abs(snappedY - y) <= magnetReach ? snapped : price }
      }
      const time = index.time(logical)
      return time === null ? null : { time, price }
    },
    width: () => chart.timeScale().width(),
    height: () => chart.paneSize().height,
    formatPrice: price => series.priceFormatter().format(price),
    formatTime: time => formatTime(time, zone, 'day-time'),
    bars: (from, to) => {
      const start = index.logical(from)
      const end = index.logical(to)
      return start === null || end === null ? null : end - start
    },
  }
  const drawingTheme = (): DrawingTheme => ({
    line: theme.drawing, text: theme.foreground, muted: theme.muted, background: theme.card,
    positive: theme.positive, negative: theme.negative, font: theme.font,
  })
  const drawingsTheme = drawingTheme()
  const drawingsLayer = new DrawingsPrimitive(drawingsTheme)
  series.attachPrimitive(drawingsLayer)
  const drawings = new DrawingController(space, {
    onDrawingCreate: drawing => callbacks.onDrawingCreate(drawing),
    onDrawingChange: (drawing, phase) => callbacks.onDrawingChange(drawing, phase),
    onDrawingSelect: id => callbacks.onDrawingSelect(id),
    onDrawingCancel: () => callbacks.onDrawingCancel?.(),
  }, () => drawingsLayer.requestUpdate())
  drawingsLayer.controller = drawings

  // Indicators: moving averages share the price pane; volume and RSI each get a pane below it.
  const averages = new Map<string, ISeriesApi<'Line'>>()
  /** Averages use the candles' precision. */
  let priceFormat: PriceFormat = { type: 'price', precision: 2, minMove: 0.01 }
  const indicatorPanes: { id: IndicatorPaneId; series: ISeriesApi<'Histogram'> | ISeriesApi<'Line'>; size: PaneSize }[] = []
  const indicatorWriters = new Map<ISeriesApi<'Line'> | ISeriesApi<'Histogram'>, ReturnType<typeof indicatorWriter>>()
  let resetIndicators = false
  const writeIndicator = (series: ISeriesApi<'Line'> | ISeriesApi<'Histogram'>, points: IndicatorPoint[]) => {
    let write = indicatorWriters.get(series)
    if (!write) { write = indicatorWriter(series); indicatorWriters.set(series, write) }
    write(points, resetIndicators)
  }
  let shownPanes = ''
  let destroyed = false
  /** Averages far from the latest close stay off the scale, like distant plan levels. */
  const nearPrice = (base: () => AutoscaleInfo | null) => {
    const info = base()
    const reference = levels.reference
    if (!info?.priceRange || reference === null) return info
    const { minValue, maxValue } = info.priceRange
    return Math.abs(minValue / reference - 1) <= autoscaleReach && Math.abs(maxValue / reference - 1) <= autoscaleReach ? info : null
  }

  // Pane layout. New panes have no element or size until the chart draws them, so layout and
  // measurement wait for frames; heights are applied as stretch factors and read back from the DOM.
  const frames = new Set<number>()
  const nextFrame = (run: () => void) => {
    const id = requestAnimationFrame(() => { frames.delete(id); if (!destroyed) run() })
    frames.add(id)
  }
  /** Last solved layout for the current panes; null until they have been laid out. */
  let solved: SolvedLayout | null = null
  /** Pane heights the owner dragged to; kept across resizes, dropped when panes change from a bar or are shown or hidden. */
  let dragged: number[] | null = null
  let appliedFactors = ''
  /** No factors are written while a pointer is down, so the library's separator drag is not fought. */
  let pressed: number[] | null | false = false
  const paneRows = () => chart.panes().map(pane => pane.getHTMLElement())
  /** The row holds the left axis, the plot and the right axis; bars span the plot. */
  const plotCell = (row: HTMLElement) => (row as HTMLTableRowElement).cells?.[1] ?? row
  const drawn = () => {
    const rows = paneRows()
    return rows.length === indicatorPanes.length + 1 && rows.every(row => row !== null)
  }
  const paneHeights = () => chart.panes().map((_, index) => chart.paneSize(index).height)
  let reported = ''
  const emit = (layout: PaneLayout[]) => {
    const key = JSON.stringify(layout)
    if (key === reported) return
    reported = key
    callbacks.onPaneLayout(layout)
  }
  /** Reports where indicator panes are drawn, for their control bars. */
  const report = () => {
    if (destroyed) return
    if (!indicatorPanes.length) return emit([])
    if (!solved) return emit([])
    if (!drawn()) return
    const origin = container.getBoundingClientRect()
    // Rectangles include CSS transforms (e.g. a dialog opening); dividing by the scale gives layout pixels.
    const scaleX = origin.width / (container.offsetWidth || 1) || 1
    const scaleY = origin.height / (container.offsetHeight || 1) || 1
    const rows = paneRows() as HTMLElement[]
    emit(indicatorPanes.map((pane, index) => {
      const row = rows[index + 1]!
      const plot = plotCell(row)
      const rowBox = row.getBoundingClientRect()
      const plotBox = plot.getBoundingClientRect()
      const { effectiveSize, compacted } = solved!.panes[index]!
      return {
        id: pane.id, top: (rowBox.top - origin.top) / scaleY, height: rowBox.height / scaleY,
        left: (plotBox.left - origin.left) / scaleX, width: plotBox.width / scaleX, effectiveSize, compacted,
      }
    }))
  }
  // Rows are reused by position when panes change, so the observed set is refreshed after each layout.
  // Plot cells are observed too: a wider price scale narrows the plot without resizing the row.
  const observed = new Set<HTMLElement>()
  const rowObserver = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(report)
  const observeRows = () => {
    const rows = new Set(paneRows().filter((row): row is HTMLElement => row !== null).flatMap(row => [row, plotCell(row)]))
    for (const row of observed) if (!rows.has(row)) { rowObserver?.unobserve(row); observed.delete(row) }
    for (const row of rows) if (!observed.has(row)) { rowObserver?.observe(row); observed.add(row) }
  }
  let settling = false
  /** A report two frames on, after the chart has drawn any change; covers rows that moved without resizing. */
  const settle = () => {
    if (settling) return
    settling = true
    nextFrame(() => nextFrame(() => { settling = false; report() }))
  }
  /** The chart divides its height, less the time axis and one-pixel separators, between panes. */
  const availableHeight = () => container.clientHeight - chart.timeScale().height() - (chart.panes().length - 1)
  /** Space the last layout was solved for; a release after a resize is not mistaken for a drag. */
  let solvedFor = 0
  let queued = false
  let attempts = 0
  const queueLayout = () => {
    if (queued) return
    queued = true
    nextFrame(() => { queued = false; layout() })
  }
  /** Solves pane heights for the space available and applies them. */
  const layout = () => {
    if (pressed !== false || releasing) return
    if (!indicatorPanes.length) { solved = null; appliedFactors = ''; observeRows(); report(); return }
    if (!drawn()) {
      if (attempts++ < 30) queueLayout()
      return
    }
    attempts = 0
    const available = availableHeight()
    solvedFor = available
    solved = solvePaneLayout(available, indicatorPanes.map(({ id, size }) => ({ id, size })), dragged)
    // Too short even for the bars: leave the chart's own layout and show no bars.
    if (!solved) { report(); return }
    indicatorPanes.forEach((pane, index) => pane.series.applyOptions({ visible: solved!.panes[index]!.effectiveSize !== 'minimized' }))
    const factors = [solved.price, ...solved.panes.map(pane => pane.height)]
    const key = factors.map(value => value.toFixed(1)).join('|')
    if (key !== appliedFactors) {
      appliedFactors = key
      chart.panes().forEach((pane, index) => pane.setStretchFactor(Math.max(factors[index]!, 1)))
    }
    observeRows()
    settle()
  }
  // A resize ends any press: a release outside the window may never arrive (seen in Firefox).
  const containerObserver = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => {
    cancelLevelDrag()
    if (pressed !== false) onRelease()
    queueLayout()
  })
  containerObserver?.observe(container)
  /** The latest release still waiting for the chart to draw; layout waits for it. */
  let releasing = 0
  const onPress = () => {
    if (indicatorPanes.length) pressed = drawn() ? paneHeights() : null
  }
  /**
   * After any release, reconciles the layout once the chart has drawn: heights that differ from the
   * pressed (or last solved) ones came from a separator drag and are kept as preferences, floors
   * still applying. Every release runs this, so a drag the library finishes after a forced release
   * (blur, resize, lost pointer-up) is still reconciled.
   */
  const onRelease = () => {
    const before = pressed === false ? null : pressed
    pressed = false
    const release = ++releasing
    nextFrame(() => nextFrame(() => {
      if (release !== releasing || pressed !== false) return
      releasing = 0
      if (!indicatorPanes.length || !drawn()) return queueLayout()
      const after = paneHeights()
      const reference = before ?? (solved && Math.abs(availableHeight() - solvedFor) <= 1 ? [solved.price, ...solved.panes.map(pane => pane.height)] : null)
      if (reference && after.length === reference.length && after.some((height, index) => Math.abs(height - reference[index]!) > 2)) {
        dragged = after
        appliedFactors = ''
      }
      queueLayout()
    }))
  }
  // A move with no button down also ends a press whose release was lost.
  const onDocumentMove = (event: PointerEvent) => { if (pressed !== false && event.buttons === 0) onRelease() }
  const onBlur = () => { if (pressed !== false) onRelease() }
  container.addEventListener('pointerdown', onPress, true)
  document.addEventListener('pointerup', onRelease, true)
  document.addEventListener('pointercancel', onRelease, true)
  // The library drags separators with mouse events, so their release is followed too.
  document.addEventListener('mouseup', onRelease, true)
  document.addEventListener('pointermove', onDocumentMove, true)
  window.addEventListener('blur', onBlur)

  const setIndicators = (view: IndicatorView) => {
    const times = view.times
    for (const [id, series] of averages) {
      if (view.lines.some(line => line.id === id)) continue
      chart.removeSeries(series)
      averages.delete(id)
      indicatorWriters.delete(series)
    }
    for (const line of view.lines) {
      let series = averages.get(line.id)
      if (!series) {
        series = chart.addSeries(LineSeries, {
          lineWidth: 1, priceLineVisible: false, crosshairMarkerRadius: 3, autoscaleInfoProvider: nearPrice, priceFormat,
        })
        averages.set(line.id, series)
      }
      series.applyOptions({ color: line.color })
      writeIndicator(series, line.values.map((value, index) => ({ time: times[index]!, value })))
    }

    // Panes are rebuilt in a fixed order (volume above RSI) when one is shown or hidden.
    const wanted = [view.volume && 'volume', view.rsi && 'rsi'].filter(Boolean).join('|')
    const rebuilt = wanted !== shownPanes
    if (rebuilt) {
      for (const pane of indicatorPanes.splice(0)) { chart.removeSeries(pane.series); indicatorWriters.delete(pane.series) }
      if (view.volume) {
        const series = chart.addSeries(HistogramSeries, {
          priceFormat: { type: 'volume' }, priceLineVisible: false,
          // Pixel room above keeps the bars clear of the pane's control bar.
          autoscaleInfoProvider: (base: () => AutoscaleInfo | null) => {
            const info = base()
            return info ? { ...info, margins: { above: barRoom, below: 0 } } : null
          },
        }, chart.panes().length)
        series.priceScale().applyOptions({ scaleMargins: { top: 0, bottom: 0 } })
        indicatorPanes.push({ id: 'volume', size: view.volume.size, series })
      }
      if (view.rsi) {
        const series = chart.addSeries(LineSeries, {
          lineWidth: 1, priceLineVisible: false, crosshairMarkerRadius: 3,
          priceFormat: { type: 'price', precision: 2, minMove: 0.01 },
          autoscaleInfoProvider: () => ({ priceRange: { minValue: 0, maxValue: 100 }, margins: { above: barRoom, below: 6 } }),
        }, chart.panes().length)
        series.priceScale().applyOptions({ scaleMargins: { top: 0, bottom: 0 } })
        for (const price of view.rsi.guides) series.createPriceLine({ price, color: theme.muted, lineWidth: 1, lineStyle: LineStyle.Dashed, axisLabelVisible: false, title: '' })
        indicatorPanes.push({ id: 'rsi', size: view.rsi.size, series })
      }
      shownPanes = wanted
      levels.avoidLogo = indicatorPanes.length === 0
      solved = null
    }
    let resized = false
    for (const pane of indicatorPanes) {
      const size = pane.id === 'volume' ? view.volume!.size : view.rsi!.size
      if (size !== pane.size) resized = true
      pane.size = size
    }
    const volume = indicatorPanes.find(pane => pane.id === 'volume')
    if (volume && view.volume) {
      const { values, up } = view.volume
      writeIndicator(volume.series, values.map((value, index) => ({
        time: times[index]!, value, color: withAlpha(up[index] ? theme.candleUp : theme.candleDownEdge, 0.42),
      })))
    }
    const rsiPane = indicatorPanes.find(pane => pane.id === 'rsi')
    if (rsiPane && view.rsi) {
      rsiPane.series.applyOptions({ color: view.rsi.color })
      writeIndicator(rsiPane.series, view.rsi.values.map((value, index) => ({ time: times[index]!, value })))
    }
    resetIndicators = false
    // Dragged heights last until panes are shown, hidden or resized from a bar; candle updates keep them.
    if (rebuilt || resized) {
      dragged = null
      appliedFactors = ''
      queueLayout()
    }
    // New values can widen the price scale, moving the plot without resizing anything observed.
    settle()
  }

  chart.subscribeClick(event => {
    if (typeof event.hoveredObjectId === 'string' && levels.find(event.hoveredObjectId)) callbacks.onLevelSelect(event.hoveredObjectId)
  })
  const onRange = (range: LogicalRange | null) => {
    if (range && candleCount > 0 && range.from < 15) callbacks.onNeedOlder()
  }
  chart.timeScale().subscribeVisibleLogicalRangeChange(onRange)
  let crosshairTime: number | null = null
  const onCrosshair = (event: MouseEventParams<Time>) => {
    // The library reports one marker; fills drawn as the same shape are all listed.
    const hovered = typeof event.hoveredObjectId === 'string' ? shapes.get(event.hoveredObjectId) ?? [] : []
    if (hovered.join('\n') !== hoveredMarker) {
      hoveredMarker = hovered.join('\n')
      callbacks.onMarkerHover?.(hovered, hovered.length && event.point ? { x: event.point.x, y: event.point.y } : null)
    }
    const time = typeof event.time === 'number' ? event.time * 1000 : null
    if (time === crosshairTime) return
    crosshairTime = time
    callbacks.onCrosshairMove(time)
  }
  chart.subscribeCrosshairMove(onCrosshair)

  // Drawings and level drags are handled before the chart sees the pointer, so panning stays off while they move.
  let drag: { id: string; pointerId: number; price: number; moved: boolean; edit: FrameEdit<number>; autoScale: boolean; offsetY: number } | null = null
  let drawingPointer: number | null = null
  // While picking, a click on the pane reports its price instead of panning, drawing or dragging.
  let picking = false
  const pane = (event: PointerEvent) => {
    const rect = container.getBoundingClientRect()
    return { x: event.clientX - rect.left, y: event.clientY - rect.top }
  }
  // A gesture that started on the price pane stays on it: Volume and RSI coordinates are not prices.
  const pricePane = (event: PointerEvent) => {
    const { x, y } = pane(event)
    return {
      x: Math.min(Math.max(x, 0), chart.timeScale().width()),
      y: Math.min(Math.max(y, 0), chart.paneSize(0).height),
    }
  }
  const lockChart = (locked: boolean) => chart.applyOptions({ handleScroll: !locked, handleScale: !locked })
  const finishLevelDrag = (cancel: boolean, price?: number) => {
    if (!drag) return
    const finished = drag
    drag = null
    // Capture may already be gone on cancellation/blur; releasing an inactive pointer can throw.
    if (container.hasPointerCapture?.(finished.pointerId)) container.releasePointerCapture(finished.pointerId)
    series.priceScale().applyOptions({ autoScale: finished.autoScale })
    lockChart(false)
    if (cancel) { finished.edit.cancel(); callbacks.onLevelCancel?.() }
    else if (finished.moved) finished.edit.end(price ?? finished.price)
    else { finished.edit.cancel(); callbacks.onLevelSelect(finished.id) }
    levels.dragPreview(null)
  }
  const cancelLevelDrag = () => finishLevelDrag(true)
  const consume = (event: PointerEvent) => { event.preventDefault(); event.stopPropagation() }
  const onPointerDown = (event: PointerEvent) => {
    if (event.button !== 0 || drag) return
    const { x, y } = pane(event)
    // Drawings, levels and picking belong to the price pane; indicator panes and the axes keep the chart's own handling.
    if (x > chart.timeScale().width() || y > chart.paneSize(0).height) return
    container.focus({ preventScroll: true })
    if (picking) {
      consume(event)
      const point = space.point(x, y, drawings.magnet)
      if (point) callbacks.onPricePick(point.price)
      return
    }
    if (!index.empty && drawings.pointerDown(x, y)) {
      consume(event)
      drawingPointer = event.pointerId
      container.setPointerCapture?.(event.pointerId)
      lockChart(true)
      return
    }
    const tag = levels.tagAt(x, y)
    if (tag) {
      consume(event)
      callbacks.onLevelEdit(tag.overlay.id, { x: tag.x, y: tag.y })
      return
    }
    const hit = onMarker(x, y) ? null : levels.levelAt(y)
    if (!hit?.overlay.draggable) return
    consume(event)
    const { id, priceStep } = hit.overlay
    drag = { id, pointerId: event.pointerId, price: hit.overlay.price, moved: false,
      autoScale: series.priceScale().options().autoScale, offsetY: (series.priceToCoordinate(hit.overlay.price) ?? y) - y,
      edit: new FrameEdit((price, phase) => callbacks.onLevelDrag(id, price, phase),
        (before, after) => roundedLevelPrice(before, priceStep) === roundedLevelPrice(after, priceStep), hit.overlay.price) }
    // Dragged overlays and live candles must not change the price beneath a stationary pointer.
    series.priceScale().applyOptions({ autoScale: false })
    container.setPointerCapture?.(event.pointerId)
    lockChart(true)
  }
  // While a tool is active, the chart's crosshair (which jumps from bar to bar) gives way to a free guide.
  let guiding = false
  const updateGuide = () => {
    const active = picking || drawings.tool !== null
    if (active === guiding) return
    guiding = active
    chart.applyOptions({ crosshair: { vertLine: { visible: !active, labelVisible: !active } } })
    if (!active) drawings.setGuide(null)
  }
  const onPointerMove = (event: PointerEvent) => {
    if (guiding) drawings.setGuide(pane(event).x)
    if (drawings.busy) {
      const { x, y } = pricePane(event)
      if (drawings.pointerMove(x, y)) consume(event)
      return
    }
    if (!drag || event.pointerId !== drag.pointerId) return
    if (event.buttons === 0) { cancelLevelDrag(); return }
    consume(event)
    const price = series.coordinateToPrice(Math.min(Math.max(pane(event).y + drag.offsetY, 0), chart.paneSize(0).height))
    if (price === null || !Number.isFinite(price) || price <= 0 || price === drag.price) return
    drag.price = price
    drag.moved = true
    levels.dragPreview({ id: drag.id, price })
    drag.edit.move(price)
  }
  const endDrag = (event: PointerEvent) => {
    if (drawingPointer === event.pointerId) {
      event.stopPropagation()
      drawingPointer = null
      container.releasePointerCapture?.(event.pointerId)
      const { x, y } = pricePane(event)
      if (event.type !== 'pointerup') drawings.cancel()
      else drawings.pointerUp(x, y)
      // A two-click creation keeps panning off until the second click.
      lockChart(drawings.busy)
      return
    }
    if (!drag || event.pointerId !== drag.pointerId) return
    event.stopPropagation()
    if (event.type !== 'pointerup') { cancelLevelDrag(); return }
    // Preserve the final-pointer flush, with the original grab offset.
    const price = series.coordinateToPrice(Math.min(Math.max(pane(event).y + drag.offsetY, 0), chart.paneSize(0).height))
    finishLevelDrag(false, price !== null && Number.isFinite(price) && price > 0 ? price : drag.price)
  }
  /** Ends level and drawing gestures on reset or destroy; state is cleared before capture is released. */
  const releaseGestures = () => {
    const pointer = drawingPointer
    drawingPointer = null
    cancelLevelDrag()
    if (pointer !== null) container.releasePointerCapture?.(pointer)
    drawings.cancel()
  }
  const onDoubleClick = (event: MouseEvent) => {
    const rect = container.getBoundingClientRect()
    const x = event.clientX - rect.left
    const y = event.clientY - rect.top
    if (x > chart.timeScale().width() || y > chart.paneSize(0).height || picking || drawings.tool || drawings.drawingAt(x, y) || onMarker(x, y)) return
    const hit = levels.levelAt(y)
    if (!hit || hit.overlay.kind === 'reference') return
    event.preventDefault()
    callbacks.onLevelEdit(hit.overlay.id, { x, y: series.priceToCoordinate(hit.overlay.price) ?? y })
  }
  container.addEventListener('dblclick', onDoubleClick)
  container.addEventListener('pointerdown', onPointerDown, true)
  container.addEventListener('pointermove', onPointerMove, true)
  container.addEventListener('pointerup', endDrag, true)
  container.addEventListener('pointercancel', endDrag, true)
  // Capture taken away without a pointerup ends the gesture like a cancel; after our own release nothing matches.
  container.addEventListener('lostpointercapture', endDrag, true)
  window.addEventListener('blur', cancelLevelDrag)
  const onPointerLeave = () => drawings.setGuide(null)
  container.addEventListener('pointerleave', onPointerLeave)

  // The owner can switch or customize the theme while a chart is open; tokens live on the root element.
  let rethemeFrame = 0
  const themeObserver = new MutationObserver(() => {
    cancelAnimationFrame(rethemeFrame)
    rethemeFrame = requestAnimationFrame(() => {
      Object.assign(theme, readTheme(container))
      Object.assign(drawingsTheme, drawingTheme())
      chart.applyOptions(chartColors())
      series.applyOptions(candleColors())
      levels.retheme()
      placeMarkers()
      drawingsLayer.requestUpdate()
    })
  })
  themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['style', 'data-theme'] })

  const adapter: ChartAdapter = {
    setCandles(candles, reset) {
      if (reset) {
        releaseGestures()
        levels.dragPreview(null)
        lockChart(false)
        resetIndicators = true
      }
      candleCount = candles.length
      levels.reference = candles.at(-1)?.close ?? null
      const timesChanged = writeCandles(candles, reset)
      loaded = candles
      if (timesChanged) { index = new TimeIndex(candles); placeMarkers() }
      const precision = pricePrecision(candles)
      if (priceFormat.type !== 'price' || precision !== priceFormat.precision) {
        priceFormat = { type: 'price', precision, minMove: 10 ** -precision }
        series.applyOptions({ priceFormat })
        for (const average of averages.values()) average.applyOptions({ priceFormat })
      }
      if (reset) {
        chart.priceScale('right').applyOptions({ autoScale: true })
        // Jump, not animate, to the latest candles, or to the focus, e.g. a closed trade's dates.
        chart.timeScale().scrollToPosition(openingRightOffset, false)
        applyFocus()
      } else if (focusPending && timesChanged) applyFocus()
    },
    setMarkers(next) {
      markers = next
      placeMarkers()
    },
    setTimeZone(next) {
      if (next === zone) return
      zone = next
      applyZone()
      drawingsLayer.requestUpdate()
    },
    setFocus(range) {
      // The same range again leaves the owner's panning alone.
      if (range?.from === focus?.from && range?.to === focus?.to) return
      focus = range
      applyFocus()
    },
    setOverlays(overlays) {
      levels.set(overlays)
    },
    setDrawings(next, selectedId) {
      drawings.drawings = next
      drawings.selectedId = selectedId
      drawingsLayer.requestUpdate()
    },
    setDrawingTool(tool, magnet) {
      drawings.magnet = magnet
      if (tool !== drawings.tool) {
        drawings.setTool(tool)
        lockChart(false)
      }
      updateGuide()
    },
    setIndicators,
    setPricePicker(active) {
      picking = active
      container.style.cursor = active ? 'crosshair' : ''
      updateGuide()
    },
    async capture(caption) {
      // Levels and drawings are series primitives, so they are on the screenshot canvas. Browsers with
      // canvas fingerprinting protection can refuse or blank the export; the caller reports a null result.
      try {
        const shot = chart.takeScreenshot(true, false)
        const ratio = shot.width / Math.max(1, container.clientWidth)
        const font = `500 ${Math.round(11 * ratio)}px ${theme.font}`
        const padding = Math.round(10 * ratio)
        const lineHeight = Math.round(16 * ratio)
        // The caption wraps between its parts so a narrow chart still shows all of it.
        const measure = document.createElement('canvas').getContext('2d')
        if (measure) measure.font = font
        const fits = (text: string) => !measure || measure.measureText(text).width <= shot.width - padding * 2
        const lines: string[] = []
        // Parts join with " · " and wrap between parts; a part too long for a line wraps between words.
        caption.split(' · ').forEach((part, index) => {
          part.split(' ').forEach((word, at) => {
            const last = lines.at(-1)
            const glue = at > 0 ? ' ' : index > 0 ? ' · ' : ''
            if (last !== undefined && fits(last + glue + word)) lines[lines.length - 1] = last + glue + word
            else lines.push(word)
          })
        })
        const footer = Math.round(10 * ratio) + lines.length * lineHeight
        const canvas = document.createElement('canvas')
        canvas.width = shot.width
        canvas.height = shot.height + footer
        const context = canvas.getContext('2d')
        if (!context || !shot.width || !shot.height) return null
        context.drawImage(shot, 0, 0)
        context.fillStyle = theme.card
        context.fillRect(0, shot.height, canvas.width, footer)
        context.fillStyle = theme.border
        context.fillRect(0, shot.height, canvas.width, Math.max(1, Math.round(ratio)))
        context.font = font
        context.fillStyle = theme.muted
        context.textBaseline = 'middle'
        lines.forEach((line, index) => context.fillText(line, padding, shot.height + Math.round(5 * ratio) + lineHeight * (index + 0.5)))
        return await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/png'))
      } catch {
        return null
      }
    },
    destroy() {
      releaseGestures()
      themeObserver.disconnect()
      cancelAnimationFrame(rethemeFrame)
      container.removeEventListener('dblclick', onDoubleClick)
      container.removeEventListener('pointerdown', onPointerDown, true)
      container.removeEventListener('pointerdown', keepView, true)
      container.removeEventListener('wheel', keepView, true)
      container.removeEventListener('pointermove', onPointerMove, true)
      container.removeEventListener('pointerup', endDrag, true)
      container.removeEventListener('pointercancel', endDrag, true)
      container.removeEventListener('lostpointercapture', endDrag, true)
      window.removeEventListener('blur', cancelLevelDrag)
      container.removeEventListener('pointerleave', onPointerLeave)
      chart.timeScale().unsubscribeVisibleLogicalRangeChange(onRange)
      chart.unsubscribeCrosshairMove(onCrosshair)
      destroyed = true
      for (const frame of frames) cancelAnimationFrame(frame)
      rowObserver?.disconnect()
      containerObserver?.disconnect()
      container.removeEventListener('pointerdown', onPress, true)
      document.removeEventListener('pointerup', onRelease, true)
      document.removeEventListener('pointercancel', onRelease, true)
      document.removeEventListener('mouseup', onRelease, true)
      document.removeEventListener('pointermove', onDocumentMove, true)
      window.removeEventListener('blur', onBlur)
      chart.remove()
    },
  }
  return adapter
}
