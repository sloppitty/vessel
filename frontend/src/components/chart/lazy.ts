import type { ChartDrawing, DrawingKind } from './drawings'
import type { IndicatorView } from './indicators'
import type { ChartAdapter, ChartAdapterFactory, ChartCandle, ChartFocus, ChartMarker, PriceOverlay } from './types'

let renderer: Promise<typeof import('./lightweight')> | null = null

/** Shared by the Plays page and every chart; a rejected chunk stays failed until reload. */
export function preloadLightweightRenderer() {
  renderer ??= import('./lightweight')
  return renderer
}

/** Loads the canvas renderer on first use and replays the latest inputs once it is ready. */
export const createLazyLightweightAdapter: ChartAdapterFactory = (container, callbacks) => {
  let adapter: ChartAdapter | null = null
  let destroyed = false
  let errorState: HTMLElement | null = null
  let candles: readonly ChartCandle[] | null = null
  let overlays: readonly PriceOverlay[] | null = null
  let drawings: [readonly ChartDrawing[], string | null] | null = null
  let tool: [DrawingKind | null, boolean] | null = null
  let picking = false
  let indicators: IndicatorView | null = null
  let markers: readonly ChartMarker[] | null = null
  let focus: ChartFocus | null = null
  void preloadLightweightRenderer().then(({ createLightweightAdapter }) => {
    if (destroyed) return
    adapter = createLightweightAdapter(container, callbacks)
    // The focus first, so the replayed candles open on it.
    if (focus) adapter.setFocus(focus)
    if (candles) adapter.setCandles(candles, true)
    if (markers) adapter.setMarkers(markers)
    if (indicators) adapter.setIndicators(indicators)
    if (overlays) adapter.setOverlays(overlays)
    if (drawings) adapter.setDrawings(...drawings)
    if (tool) adapter.setDrawingTool(...tool)
    if (picking) adapter.setPricePicker(true)
  }).catch(() => {
    if (destroyed) return
    errorState = document.createElement('p')
    errorState.className = 'chart-state'
    errorState.setAttribute('role', 'alert')
    errorState.textContent = 'The chart renderer could not be loaded. Reload the page to try again.'
    container.append(errorState)
  })
  return {
    setCandles(next, reset) {
      if (adapter) adapter.setCandles(next, reset)
      else candles = next
    },
    setOverlays(next) {
      if (adapter) adapter.setOverlays(next)
      else overlays = next
    },
    setDrawings(next, selectedId) {
      if (adapter) adapter.setDrawings(next, selectedId)
      else drawings = [next, selectedId]
    },
    setDrawingTool(next, magnet) {
      if (adapter) adapter.setDrawingTool(next, magnet)
      else tool = [next, magnet]
    },
    setPricePicker(active) {
      if (adapter) adapter.setPricePicker(active)
      else picking = active
    },
    setIndicators(next) {
      if (adapter) adapter.setIndicators(next)
      else indicators = next
    },
    setMarkers(next) {
      if (adapter) adapter.setMarkers(next)
      else markers = next
    },
    setFocus(next) {
      if (adapter) adapter.setFocus(next)
      else focus = next
    },
    capture(caption) {
      return adapter ? adapter.capture(caption) : Promise.resolve(null)
    },
    destroy() {
      destroyed = true
      adapter?.destroy()
      errorState?.remove()
    },
  }
}
