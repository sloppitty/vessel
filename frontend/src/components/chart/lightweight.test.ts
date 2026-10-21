import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { computeIndicators, defaultIndicators } from './indicators'
import type { ChartAdapter, ChartCallbacks, ChartCandle, PriceOverlay } from './types'
import { testFrames } from '@/test/testFrames'

vi.unmock('@/components/chart/lightweight')
const library = vi.hoisted(() => ({ createChart: vi.fn(), markers: { setMarkers: vi.fn(), detach: vi.fn() } }))
vi.mock('lightweight-charts', () => ({
  createChart: library.createChart, createSeriesMarkers: () => library.markers, CandlestickSeries: 'candles', LineSeries: 'line', HistogramSeries: 'volume',
  ColorType: { Solid: 'solid' }, CrosshairMode: { Normal: 0 }, LineStyle: { Dashed: 2 },
}))
import { createLightweightAdapter } from './lightweight'

type Point = { time: number; open?: number; high?: number; low?: number; close?: number; value?: number; color?: string }
function makeSeries(kind: string) {
    let data: Point[] = []
    let autoScale = true
    const scale = {
      options: () => ({ autoScale }),
      applyOptions: vi.fn((options: { autoScale?: boolean }) => { if (options.autoScale !== undefined) autoScale = options.autoScale }),
    }
    const primitives: { find?: (id: string) => PriceOverlay | undefined }[] = []
    const current = {
      kind, primitives, data: () => data,
      setData: vi.fn((next: Point[]) => { data = [...next] }),
      update: vi.fn((point: Point) => {
        if (point.time < data.at(-1)!.time) throw new Error('historical update')
        if (point.time === data.at(-1)?.time) data[data.length - 1] = point
        else data.push(point)
      }),
      applyOptions: vi.fn(), priceScale: () => scale, createPriceLine: vi.fn(),
      priceToCoordinate: (price: number) => 1000 - price, coordinateToPrice: (y: number) => 1000 - y,
      priceFormatter: () => ({ format: String }),
      attachPrimitive: (primitive: { find?: (id: string) => PriceOverlay | undefined; attached?: (params: { series: unknown; requestUpdate: () => void }) => void }) => {
        primitives.push(primitive); primitive.attached?.({ series: current, requestUpdate: vi.fn() })
      },
    }
    return current
}
function fakeChart() {
  const series: ReturnType<typeof makeSeries>[] = []
  const addSeries = vi.fn((kind: string) => { const current = makeSeries(kind); series.push(current); return current })
  const timeScale = { width: () => 800, height: () => 20, logicalToCoordinate: (logical: number) => logical * 100,
    getVisibleLogicalRange: vi.fn((): { from: number; to: number } | null => ({ from: 0, to: 7 })),
    // As the library does, a coordinate rounds up to a whole bar.
    coordinateToLogical: (x: number) => Math.ceil(x / 100 - 1e-9), scrollToPosition: vi.fn(), setVisibleRange: vi.fn(), subscribeVisibleLogicalRangeChange: vi.fn(), unsubscribeVisibleLogicalRangeChange: vi.fn() }
  const chart = {
    addSeries, series, applyOptions: vi.fn(), removeSeries: vi.fn(), remove: vi.fn(),
    subscribeClick: vi.fn(), subscribeCrosshairMove: vi.fn(), unsubscribeCrosshairMove: vi.fn(),
    priceScale: () => ({ applyOptions: vi.fn() }),
    panes: () => [{ getHTMLElement: () => null, setStretchFactor: vi.fn() }], paneSize: () => ({ width: 800, height: 1000 }),
    timeScale: () => timeScale,
  }
  library.createChart.mockReturnValue(chart)
  return chart
}
const candles: ChartCandle[] = Array.from({ length: 6 }, (_, index) => ({ time: (index + 1) * 60_000, open: 100, high: 120, low: 90, close: [100, 101, 99, 103, 101, 105][index]!, volume: 12 }))
const settings = { ...defaultIndicators, emas: defaultIndicators.emas.map(ema => ({ ...ema, enabled: true, period: 2 })), rsi: { ...defaultIndicators.rsi, period: 2 } }
const events = (): ChartCallbacks => ({
  onLevelDrag: vi.fn(), onLevelSelect: vi.fn(), onLevelCancel: vi.fn(), onNeedOlder: vi.fn(), onDrawingCreate: vi.fn(),
  onDrawingChange: vi.fn(), onDrawingSelect: vi.fn(), onDrawingCancel: vi.fn(), onLevelEdit: vi.fn(), onPricePick: vi.fn(), onCrosshairMove: vi.fn(), onPaneLayout: vi.fn(), onMarkerHover: vi.fn(),
})
function pointer(container: HTMLElement, type: string, price: number, x = 400) {
  const event = new MouseEvent(type, { clientX: x, clientY: 1000 - price, bubbles: true, button: 0, buttons: type === 'pointermove' ? 1 : 0 })
  Object.defineProperty(event, 'pointerId', { value: 1 })
  container.dispatchEvent(event)
}
let adapter: ChartAdapter
let frames: ReturnType<typeof testFrames>
let chart: ReturnType<typeof fakeChart>
let container: HTMLDivElement
let callbacks: ChartCallbacks
beforeEach(() => {
  frames = testFrames(); chart = fakeChart(); container = document.createElement('div'); callbacks = events()
  adapter = createLightweightAdapter(container, callbacks)
  adapter.setCandles(candles, true)
})
afterEach(() => adapter.destroy())

const variants = [
  { name: 'forming', next: [...candles.slice(0, -1), { ...candles.at(-1)!, close: 106, volume: 13 }], incremental: true },
  { name: 'append', next: [...candles, { ...candles.at(-1)!, time: 420_000, close: 108 }], incremental: true },
  { name: 'append + update', next: [...candles.slice(0, -1), { ...candles.at(-1)!, close: 106, volume: 13 }, { ...candles.at(-1)!, time: 420_000, close: 108 }], incremental: true },
  { name: 'prepend', next: [{ ...candles[0]!, time: 0 }, ...candles], incremental: false },
  { name: 'correction', next: [candles[0]!, { ...candles[1]!, close: 110, volume: 14 }, ...candles.slice(2)], incremental: false },
  { name: 'trim', next: candles.slice(1), incremental: false },
  { name: 'reset', next: candles, incremental: false, reset: true },
]
describe('adapter incremental candle and indicator rendering', () => {
  it.each(variants)('$name matches a full replacement on every series', ({ next, incremental, reset = false }) => {
    adapter.setIndicators(computeIndicators(candles, settings))
    for (const series of chart.series) { series.setData.mockClear(); series.update.mockClear() }
    adapter.setCandles(next, reset)
    adapter.setIndicators(computeIndicators(next, settings))
    const actual = chart.series.map(series => series.data())
    for (const series of chart.series) {
      expect(series.setData).toHaveBeenCalledTimes(incremental ? 0 : 1)
      if (incremental) expect(series.update).toHaveBeenCalled()
    }
    // A fresh renderer uses full setData for the same inputs.
    const original = chart
    chart = fakeChart()
    const fresh = createLightweightAdapter(document.createElement('div'), events())
    fresh.setCandles(next, true); fresh.setIndicators(computeIndicators(next, settings))
    expect(actual).toEqual(chart.series.map(series => series.data()))
    fresh.destroy(); chart = original
  })

  it('does no renderer writes for value-equivalent arrays and changes price format only with precision', () => {
    adapter.setIndicators(computeIndicators(candles, settings))
    for (const series of chart.series) { series.setData.mockClear(); series.update.mockClear(); series.applyOptions.mockClear() }
    adapter.setCandles(candles.map(candle => ({ ...candle })), false)
    adapter.setIndicators(computeIndicators(candles, settings))
    for (const series of chart.series) {
      expect(series.setData).not.toHaveBeenCalled(); expect(series.update).not.toHaveBeenCalled()
      expect(series.applyOptions.mock.calls.flat()).not.toEqual(expect.arrayContaining([expect.objectContaining({ priceFormat: expect.anything() })]))
    }
    adapter.setCandles([...candles.slice(0, -1), { ...candles.at(-1)!, close: 105.125 }], false)
    expect(chart.series[0]!.applyOptions).toHaveBeenCalledWith({ priceFormat: { type: 'price', precision: 3, minMove: 0.001 } })
    chart.series.slice(1, 5).forEach(series => expect(series.applyOptions).toHaveBeenCalledWith({ priceFormat: { type: 'price', precision: 3, minMove: 0.001 } }))
  })
})

describe('adapter level gesture lifecycle', () => {
  const overlay: PriceOverlay = { id: 'level', label: 'Entry', price: 100, color: '#ffffff', emphasis: 'selected', kind: 'entry', draggable: true }
  it('shows the latest pointer immediately, batches moves, and flushes pointerup before the frame', () => {
    adapter.setOverlays([overlay]); pointer(container, 'pointerdown', 100)
    pointer(container, 'pointermove', 101); pointer(container, 'pointermove', 102)
    expect(callbacks.onLevelDrag).not.toHaveBeenCalled()
    expect(chart.series[0]!.primitives[0]!.find?.('level')?.price).toBe(102)
    pointer(container, 'pointerup', 103); frames.run()
    // The capture released by pointerup no longer matches a gesture.
    pointer(container, 'lostpointercapture', 104)
    expect(callbacks.onLevelDrag).toHaveBeenCalledExactlyOnceWith('level', 103, 'end')
  })

  it.each(['pointercancel', 'lostpointercapture'])('discards the preview on %s and cancels the pending frame', type => {
    adapter.setOverlays([overlay]); pointer(container, 'pointerdown', 100); pointer(container, 'pointermove', 102)
    pointer(container, type, 103); frames.run()
    expect(callbacks.onLevelDrag).not.toHaveBeenCalled()
    expect(callbacks.onLevelCancel).toHaveBeenCalledTimes(1)
  })

  it.each(['destroy', 'source'] as const)('%s cancels queued publications', how => {
    adapter.setOverlays([overlay]); pointer(container, 'pointerdown', 100); pointer(container, 'pointermove', 102)
    if (how === 'destroy') adapter.destroy()
    else adapter.setCandles(candles, true)
    frames.run()
    expect(callbacks.onLevelDrag).not.toHaveBeenCalled()
    expect(callbacks.onLevelCancel).toHaveBeenCalledTimes(1)
    if (how === 'destroy') adapter = { ...adapter, destroy: vi.fn() }
  })

  it('suppresses only equivalent rounded level moves', () => {
    adapter.setOverlays([{ ...overlay, priceStep: 0.25 }]); pointer(container, 'pointerdown', 100)
    pointer(container, 'pointermove', 101); frames.run()
    pointer(container, 'pointermove', 101.01); frames.run()
    pointer(container, 'pointermove', 101.3); frames.run()
    pointer(container, 'pointerup', 101.3)
    expect(callbacks.onLevelDrag).toHaveBeenCalledTimes(3)
    expect(callbacks.onLevelDrag).toHaveBeenNthCalledWith(1, 'level', 101, 'move')
    expect(callbacks.onLevelDrag).toHaveBeenNthCalledWith(2, 'level', expect.closeTo(101.3), 'move')
    expect(callbacks.onLevelDrag).toHaveBeenLastCalledWith('level', expect.closeTo(101.3), 'end')
  })
})


describe('adapter drawing gesture cancellation', () => {
  it.each(['pointercancel', 'lostpointercapture', 'source', 'destroy'] as const)('%s drops pending drawing changes', how => {
    adapter.setDrawings([{ id: 'drawing', schemaVersion: 1, kind: 'vertical-line', points: [{ time: 300_000, price: 100 }] }], null)
    pointer(container, 'pointerdown', 500)
    pointer(container, 'pointermove', 500, 450)
    expect(callbacks.onDrawingChange).not.toHaveBeenCalled()
    if (how === 'pointercancel' || how === 'lostpointercapture') pointer(container, how, 500, 450)
    else if (how === 'source') adapter.setCandles(candles, true)
    else adapter.destroy()
    frames.run()
    expect(callbacks.onDrawingChange).not.toHaveBeenCalled()
    expect(callbacks.onDrawingCancel).toHaveBeenCalledTimes(1)
    if (how === 'destroy') adapter = { ...adapter, destroy: vi.fn() }
  })
})

describe('fill markers and trade focus (phase 1, 1.1b)', () => {
  const marker = (id: string, time: number, side: 'buy' | 'sell' = 'buy') => ({ id, time, price: 101.5, side, tooltip: `${id} tooltip`, span: 60_000 })
  it('puts each fill on the candle it falls in, as a shape at its price, and leaves out fills outside the candles', () => {
    library.markers.setMarkers.mockClear()
    // Candles open each minute from 60s to 360s; the last one spans one more minute.
    adapter.setMarkers([marker('late', 395_000, 'sell'), marker('early', 30_000), marker('inside', 125_000), marker('after', 430_000)])
    const placed = library.markers.setMarkers.mock.lastCall![0] as { id: string; time: number; price: number; shape: string; position: string }[]
    expect(placed.map(item => [item.id, item.time, item.shape])).toEqual([['inside', 120, 'arrowUp'], ['late', 360, 'arrowDown']])
    expect(placed.every(item => item.position === 'atPriceMiddle' && item.price === 101.5)).toBe(true)
    // New candles move a fill onto the candle that now holds it.
    adapter.setCandles([...candles, { ...candles.at(-1)!, time: 420_000 }], false)
    expect((library.markers.setMarkers.mock.lastCall![0] as { id: string }[]).map(item => item.id)).toEqual(['inside', 'late', 'after'])
  })

  it('keeps a fill off a candle whose own span it is outside, across a gap or on a lone candle', () => {
    const placed = () => (library.markers.setMarkers.mock.lastCall![0] as { id: string; time: number }[]).map(item => [item.id, item.time])
    // One-minute candles at 60s, 120s and 360s: 240s falls in the gap, 480s after the last candle's minute.
    adapter.setCandles([candles[0]!, candles[1]!, { ...candles[0]!, time: 360_000 }], true)
    adapter.setMarkers([marker('gap', 240_000), marker('after', 480_000), marker('inside', 370_000)])
    expect(placed()).toEqual([['inside', 360]])
    // A lone candle holds only its own minute.
    adapter.setCandles([candles[0]!], true)
    adapter.setMarkers([marker('later', 3_600_000), marker('own', 90_000)])
    expect(placed()).toEqual([['own', 60]])
  })

  it('reports the fill under the pointer for its tooltip', () => {
    adapter.setMarkers([marker('fill-1', 125_000)])
    const crosshair = chart.subscribeCrosshairMove.mock.calls[0]![0] as (event: unknown) => void
    crosshair({ time: 120, hoveredObjectId: 'fill-1', point: { x: 40, y: 80 } })
    expect(callbacks.onMarkerHover).toHaveBeenLastCalledWith(['fill-1'], { x: 40, y: 80 })
    crosshair({ time: 120, hoveredObjectId: 'level:entry', point: { x: 40, y: 90 } })
    expect(callbacks.onMarkerHover).toHaveBeenLastCalledWith([], null)
  })

  it('lists every fill drawn as the one shape, at one price in one candle', () => {
    adapter.setMarkers([marker('first', 125_000), marker('second', 150_000, 'sell'), { ...marker('other price', 130_000), price: 99 }, marker('next candle', 185_000)])
    const crosshair = chart.subscribeCrosshairMove.mock.calls[0]![0] as (event: unknown) => void
    crosshair({ time: 120, hoveredObjectId: 'second', point: { x: 100, y: 898 } })
    expect(callbacks.onMarkerHover).toHaveBeenLastCalledWith(['first', 'second'], { x: 100, y: 898 })
    crosshair({ time: 120, hoveredObjectId: 'other price', point: { x: 100, y: 901 } })
    expect(callbacks.onMarkerHover).toHaveBeenLastCalledWith(['other price'], { x: 100, y: 901 })
  })

  it('lets a fill at its planned price keep its tooltip: the level gives way only on the marker', () => {
    // Bars are 100 px apart, so the fill in the 120s candle is drawn at x 100, on the entry level's y.
    adapter.setOverlays([{ id: 'level', label: 'Entry', price: 101.5, color: '#ffffff', emphasis: 'selected', kind: 'entry', draggable: true }])
    adapter.setMarkers([marker('fill-1', 125_000)])
    const primitive = chart.series[0]!.primitives[0] as unknown as { hitTest(x: number, y: number): { externalId: string } | null }
    expect(primitive.hitTest(100, 1000 - 101.5)).toBeNull()
    expect(primitive.hitTest(104, 1000 - 103)).toBeNull()
    expect(primitive.hitTest(400, 1000 - 101.5)?.externalId).toBe('level')
    // Beside the buy arrow's shaft, outside the library's own marker hit area, the level keeps the pointer.
    expect(primitive.hitTest(117, 1000 - 101.5)?.externalId).toBe('level')
    // Beside the marker, even within its bar's reach at this zoom, the line is the level's again.
    expect(primitive.hitTest(140, 1000 - 101.5)?.externalId).toBe('level')
    expect(primitive.hitTest(60, 1000 - 101.5)?.externalId).toBe('level')
    // Pressing the marker neither drags nor selects the level; elsewhere on the line it still drags.
    pointer(container, 'pointerdown', 101.5, 100); pointer(container, 'pointerup', 101.5, 100)
    expect(callbacks.onLevelSelect).not.toHaveBeenCalled()
    pointer(container, 'pointerdown', 101.5, 400); pointer(container, 'pointermove', 103, 400); pointer(container, 'pointerup', 103, 400)
    expect(callbacks.onLevelDrag).toHaveBeenLastCalledWith('level', 103, 'end')
  })

  it('gives way only to markers the library draws, never to one beyond the visible bars and their neighbours', () => {
    // Bars 3 to 7 are in view: the library draws the fill there and the one just before it, not the oldest.
    vi.mocked(chart.timeScale().getVisibleLogicalRange).mockReturnValue({ from: 3, to: 7 })
    adapter.setOverlays([{ id: 'level', label: 'Entry', price: 101.5, color: '#ffffff', emphasis: 'selected', kind: 'entry', draggable: true }])
    adapter.setMarkers([marker('oldest', 65_000), { ...marker('before', 125_000), price: 50 }, { ...marker('shown', 305_000), price: 50 }])
    const primitive = chart.series[0]!.primitives[0] as unknown as { hitTest(x: number, y: number): { externalId: string } | null }
    expect(primitive.hitTest(0, 1000 - 101.5)?.externalId).toBe('level')
    expect(primitive.hitTest(100, 1000 - 50)).toBeNull()
  })

  it('opens on the focus at a reset, once per range, so later panning is left alone', () => {
    const range = chart.timeScale().setVisibleRange
    range.mockClear()
    adapter.setFocus({ from: 90_000, to: 300_000 })
    expect(range).toHaveBeenLastCalledWith({ from: 90, to: 300 })
    adapter.setFocus({ from: 90_000, to: 300_000 })
    expect(range).toHaveBeenCalledOnce()
    adapter.setCandles(candles, true)
    expect(range).toHaveBeenCalledTimes(2)
    adapter.setFocus(null)
    adapter.setCandles(candles, true)
    expect(range).toHaveBeenCalledTimes(2)
  })

  it('applies a focus that starts before the loaded candles again as older pages arrive, until the owner moves the view', () => {
    const range = chart.timeScale().setVisibleRange
    adapter.setCandles(candles, true)
    range.mockClear()
    // The trade starts 30 seconds in; the first page starts at 60 seconds, so the older page is asked for directly.
    vi.mocked(callbacks.onNeedOlder).mockClear()
    adapter.setFocus({ from: 30_000, to: 300_000 })
    expect(range).toHaveBeenCalledOnce()
    expect(callbacks.onNeedOlder).toHaveBeenCalledOnce()
    const older = (time: number) => [{ ...candles[0]!, time }, ...candles]
    adapter.setCandles(older(0), false)
    expect(range).toHaveBeenCalledTimes(2)
    // Covered now: no more pages are asked for, and a further page leaves the view alone.
    expect(callbacks.onNeedOlder).toHaveBeenCalledOnce()
    adapter.setCandles([{ ...candles[0]!, time: -60_000 }, ...older(0)], false)
    expect(range).toHaveBeenCalledTimes(2)
    // Thousands of candles can be drawn in one view.
    expect(library.createChart.mock.calls[0]![1]).toMatchObject({ timeScale: { minBarSpacing: 0.05 } })
    // The owner moving the view first ends the wait.
    adapter.setFocus({ from: -300_000, to: 300_000 })
    expect(range).toHaveBeenCalledTimes(3)
    container.dispatchEvent(new Event('wheel'))
    adapter.setCandles([{ ...candles[0]!, time: -120_000 }, { ...candles[0]!, time: -60_000 }, ...older(0)], false)
    expect(range).toHaveBeenCalledTimes(3)
  })

  it('keeps a fill on a monthly candle within its calendar month', () => {
    const at = (text: string) => Date.parse(text)
    const month = { months: 1 }
    adapter.setCandles([{ ...candles[0]!, time: at('2026-01-01T00:00:00Z') }, { ...candles[0]!, time: at('2026-02-01T00:00:00Z') }], true)
    adapter.setMarkers([{ ...marker('january', at('2026-01-31T12:00:00Z')), span: month }, { ...marker('march', at('2026-03-01T00:00:00Z')), span: month },
      { ...marker('february', at('2026-02-28T23:59:59Z')), span: month }])
    const placed = (library.markers.setMarkers.mock.lastCall![0] as { id: string; time: number }[]).map(item => [item.id, item.time])
    expect(placed).toEqual([['january', at('2026-01-01T00:00:00Z') / 1000], ['february', at('2026-02-01T00:00:00Z') / 1000]])
  })
})
