import type { ChartDrawing, DrawingKind } from './drawings'
import type { IndicatorView, PaneLayout } from './indicators'

/** Renderer inputs are independent of plays, journals and execution matching. */

/** Display candle. Times are UTC Unix milliseconds; prices are parsed for plotting only. */
export interface ChartCandle {
  time: number
  open: number
  high: number
  low: number
  close: number
  /** Traded volume as the venue reports it, for the volume pane. */
  volume: number
}

/** A horizontal price level supplied by a feature, e.g. a planned entry, stop or target. */
export interface PriceOverlay {
  id: string
  /** Short canvas/axis tag, e.g. "E1 · T2". */
  label: string
  price: number
  /** Line and tag color. CSS custom properties such as `var(--negative)` are resolved by the renderer. */
  color: string
  /** Optional marker drawn at the start of the tag, e.g. the owning entry's color. */
  accent?: string
  /** `reference` marks derived, non-editable levels such as an average. */
  kind: 'entry' | 'stop' | 'target' | 'reference'
  emphasis: 'selected' | 'normal'
  draggable: boolean
  /** Venue tick for suppressing equivalent drag publications; absent uses five significant figures. */
  priceStep?: number | null
  /**
   * True when the level has no confirmed counterpart, e.g. a planned stop not seen resting at the venue: drawn with a
   * hollow tag and a fainter line. False or absent draws it as usual.
   */
  unconfirmed?: boolean
}

export interface ChartCallbacks {
  onLevelSelect(id: string): void
  /** Frame-batched previews while dragging, then once with phase "end" to commit. */
  onLevelDrag(id: string, price: number, phase: 'move' | 'end'): void
  /** The visible range is close to the oldest loaded candle. */
  onNeedOlder(): void
  onDrawingCreate(drawing: ChartDrawing): void
  /** Called continuously while moving or resizing, then once with phase "end". */
  onDrawingChange(drawing: ChartDrawing, phase: 'move' | 'end'): void
  onDrawingSelect(id: string | null): void
  /** Ends a gesture cut short by a reset or unmount; changes already published become one undo step. */
  onDrawingCancel?(): void
  /** Discards an interrupted level preview without changing the draft. */
  onLevelCancel?(): void
  /** Asks to edit a level in place, e.g. after a double-click or a click on its tag. `anchor` is in pane pixels. */
  onLevelEdit(id: string, anchor: { x: number; y: number }): void
  /** A click while the price picker is on, with the price under the pointer (magnet applied). */
  onPricePick(price: number): void
  /** The crosshair's candle time (UTC ms), or null when the pointer leaves the chart. */
  onCrosshairMove(time: number | null): void
  /** Indicator panes moved or resized, e.g. after a layout change or a separator drag. */
  onPaneLayout(panes: PaneLayout[]): void
}

/** Boundary that keeps the charting library out of feature code. */
export interface ChartAdapter {
  /** `reset` re-anchors the view at the latest candle, e.g. after an instrument or interval change. */
  setCandles(candles: readonly ChartCandle[], reset: boolean): void
  setOverlays(overlays: readonly PriceOverlay[]): void
  setDrawings(drawings: readonly ChartDrawing[], selectedId: string | null): void
  /** `null` is the crosshair: select, move and resize instead of creating. */
  setDrawingTool(tool: DrawingKind | null, magnet: boolean): void
  /** While on, clicks report a price through `onPricePick` instead of selecting, drawing or panning. */
  setPricePicker(active: boolean): void
  /** Moving averages on the price pane; volume and RSI in panes below it, added and removed as needed. */
  setIndicators(view: IndicatorView): void
  /**
   * PNG of the chart as shown, including planned levels and drawings but not the crosshair, with
   * `caption` in a footer. Null until the renderer is ready.
   */
  capture(caption: string): Promise<Blob | null>
  destroy(): void
}

export type ChartAdapterFactory = (container: HTMLElement, callbacks: ChartCallbacks) => ChartAdapter
