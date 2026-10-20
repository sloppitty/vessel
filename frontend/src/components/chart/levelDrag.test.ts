import { afterEach, describe, expect, it, vi } from 'vitest'
import { createChart, type IChartApi, type ISeriesPrimitive, type SeriesAttachedParameter, type Time } from 'lightweight-charts'
import { createLightweightAdapter } from './lightweight'
import type { ChartAdapter, ChartCallbacks, PriceOverlay } from './types'
import { testFrames } from '@/test/testFrames'

vi.unmock('./lightweight')
vi.mock('lightweight-charts', async importOriginal => ({
  ...await importOriginal<typeof import('lightweight-charts')>(),
  createChart: vi.fn(),
  // The fake series has no data subscriptions; fill markers are covered in lightweight.test.ts.
  createSeriesMarkers: () => ({ setMarkers: vi.fn(), detach: vi.fn() }),
}))

let adapter: ChartAdapter | undefined
afterEach(() => { adapter?.destroy(); adapter = undefined })

/** Exercise Vessel's real pointer handlers with a deterministic price scale; no jsdom canvas. */
function setup(autoScale = true) {
  const frames = testFrames()
  const observers: ResizeObserverCallback[] = []
  vi.stubGlobal('ResizeObserver', class {
    constructor(callback: ResizeObserverCallback) { observers.push(callback) }
    observe() {}
    disconnect() {}
  })
  const container = document.createElement('div')
  container.getBoundingClientRect = () => new DOMRect(0, 0, 600, 400)
  let captured = false
  container.setPointerCapture = () => { captured = true }
  container.hasPointerCapture = () => captured
  container.releasePointerCapture = () => {
    expect(captured).toBe(true)
    captured = false
    container.dispatchEvent(new PointerEvent('lostpointercapture', { pointerId: 1 }))
  }
  const scale = {
    options: () => ({ autoScale }),
    applyOptions: vi.fn((options: { autoScale: boolean }) => { autoScale = options.autoScale }),
  }
  const series = {
    priceScale: () => scale,
    priceToCoordinate: (price: number) => 300 - price,
    coordinateToPrice: vi.fn((y: number) => 300 - y),
    attachPrimitive: (primitive: ISeriesPrimitive<Time>) =>
      primitive.attached?.({ series, requestUpdate: vi.fn() } as unknown as SeriesAttachedParameter<Time>),
    applyOptions: vi.fn(), setData: vi.fn(),
  }
  const timeScale = {
    width: () => 550, subscribeVisibleLogicalRangeChange: vi.fn(), unsubscribeVisibleLogicalRangeChange: vi.fn(), scrollToPosition: vi.fn(),
  }
  const chart = {
    addSeries: () => series, applyOptions: vi.fn(), timeScale: () => timeScale, priceScale: () => scale,
    paneSize: () => ({ width: 550, height: 350 }), panes: () => [],
    subscribeClick: vi.fn(), subscribeCrosshairMove: vi.fn(), unsubscribeCrosshairMove: vi.fn(), remove: vi.fn(),
  }
  vi.mocked(createChart).mockReturnValue(chart as unknown as IChartApi)
  const callbacks: ChartCallbacks = {
    onLevelSelect: vi.fn(), onLevelDrag: vi.fn(), onLevelCancel: vi.fn(), onNeedOlder: vi.fn(), onDrawingCreate: vi.fn(), onDrawingChange: vi.fn(),
    onDrawingSelect: vi.fn(), onLevelEdit: vi.fn(), onPricePick: vi.fn(), onCrosshairMove: vi.fn(), onPaneLayout: vi.fn(),
  }
  adapter = createLightweightAdapter(container, callbacks)
  const stop: PriceOverlay = { id: 'stop', price: 100, label: 'SL', kind: 'stop', emphasis: 'selected', color: '#ff0000', draggable: true }
  adapter.setOverlays([stop])
  const pointer = (type: string, y: number, buttons = 1, pointerId = 1) => {
    if (type === 'lostpointercapture') captured = false
    container.dispatchEvent(new PointerEvent(type, { clientX: 100, clientY: y, button: 0, buttons, pointerId }))
  }
  const resize = () => observers.at(-1)!([], {} as ResizeObserver)
  return { scale, chart, callbacks, pointer, stop, resize, frames }
}

describe('level drag stability', () => {
  it.each([true, false])('freezes autoscaling and restores the original setting (%s) on release', original => {
    const { pointer, scale, callbacks, chart, stop, frames } = setup(original)
    // Grab inside the hit tolerance, not exactly on the line.
    pointer('pointerdown', 203)
    expect(scale.options().autoScale).toBe(false)
    pointer('pointermove', 203)
    expect(callbacks.onLevelDrag).not.toHaveBeenCalled()
    pointer('pointermove', 193)
    frames.run()
    expect(callbacks.onLevelDrag).toHaveBeenLastCalledWith('stop', 110, 'move')
    adapter!.setOverlays([{ ...stop, price: 110 }])
    // Live data can redraw while dragging, but must not turn autoscaling back on.
    adapter!.setCandles([{ time: 1_000, open: 100, high: 200, low: 90, close: 200, volume: 1 }], false)
    expect(scale.options().autoScale).toBe(false)
    for (let i = 0; i < 10; i++) pointer('pointermove', 193)
    frames.run()
    expect(callbacks.onLevelDrag).toHaveBeenCalledTimes(1)
    pointer('pointerup', 193, 0)
    expect(callbacks.onLevelDrag).toHaveBeenLastCalledWith('stop', 110, 'end')
    expect(scale.options().autoScale).toBe(original)
    expect(chart.applyOptions).toHaveBeenLastCalledWith({ handleScroll: true, handleScale: true })
  })

  it.each(['pointercancel', 'lostpointercapture', 'blur', 'buttons-up', 'resize', 'reset', 'destroy'])('discards a preview on %s and unlocks the chart', interruption => {
    const { pointer, callbacks, scale, chart, resize, frames } = setup()
    pointer('pointerdown', 200)
    pointer('pointermove', 190)
    frames.run()
    if (interruption === 'blur') window.dispatchEvent(new Event('blur'))
    else if (interruption === 'buttons-up') pointer('pointermove', 190, 0)
    else if (interruption === 'resize') resize()
    else if (interruption === 'reset') adapter!.setCandles([], true)
    else if (interruption === 'destroy') { adapter!.destroy(); adapter = undefined }
    else pointer(interruption, 190, 0)
    frames.run()
    expect(callbacks.onLevelCancel).toHaveBeenCalledExactlyOnceWith()
    expect(scale.options().autoScale).toBe(true)
    expect(chart.applyOptions).toHaveBeenLastCalledWith({ handleScroll: true, handleScale: true })
    pointer('pointerup', 190, 0)
    expect(callbacks.onLevelDrag).toHaveBeenCalledExactlyOnceWith('stop', 110, 'move')
  })

  it('treats a click as selection and ignores other pointers', () => {
    const { pointer, callbacks } = setup()
    pointer('pointerdown', 200)
    pointer('pointermove', 180, 1, 2)
    pointer('pointerup', 180, 0, 2)
    pointer('pointerup', 200, 0)
    expect(callbacks.onLevelSelect).toHaveBeenCalledExactlyOnceWith('stop')
    expect(callbacks.onLevelDrag).not.toHaveBeenCalled()
  })
})
