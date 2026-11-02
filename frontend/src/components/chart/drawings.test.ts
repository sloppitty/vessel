import { describe, expect, it, vi } from 'vitest'
import { DrawingController, buildPoints, type DrawingSpace } from './drawingController'
import { TimeIndex, fibonacciLevels, fibonacciPrice, formatDuration, goldenPocket, isChartDrawing, positionStats, priceRangeStats, snapPrice, type ChartDrawing } from './drawings'

const hour = 3_600_000
const candles = [0, 1, 2, 3].map(i => ({ time: 1_000 * hour + i * hour, open: 100 + i, high: 105 + i, low: 95 + i, close: 102 + i, volume: 10 }))

describe('time index', () => {
  const index = new TimeIndex(candles)
  it('interpolates inside the data and extrapolates by the bar interval outside it', () => {
    expect(index.logical(candles[2]!.time)).toBe(2)
    expect(index.logical(candles[1]!.time + hour / 2)).toBe(1.5)
    expect(index.logical(candles[3]!.time + 5 * hour)).toBe(8)
    expect(index.logical(candles[0]!.time - 2 * hour)).toBe(-2)
    for (const logical of [-2, 0, 1.5, 3, 8]) expect(index.logical(index.time(logical)!)).toBeCloseTo(logical)
  })
  it('reports empty data instead of inventing positions', () => {
    const empty = new TimeIndex([])
    expect(empty.empty).toBe(true)
    expect(empty.logical(1)).toBeNull()
    expect(empty.time(1)).toBeNull()
  })
})

describe('drawing geometry', () => {
  it('snaps prices to the nearest candle value', () => {
    expect(snapPrice(candles[0]!, 104.4)).toBe(105)
    expect(snapPrice(candles[0]!, 99)).toBe(100)
  })
  it('places Fibonacci level 1 at the start and 0 at the end', () => {
    const start = { time: 0, price: 100 }
    const end = { time: 1, price: 200 }
    expect(fibonacciPrice(start, end, 1)).toBe(100)
    expect(fibonacciPrice(start, end, 0)).toBe(200)
    expect(fibonacciPrice(start, end, 0.5)).toBe(150)
  })
  it('mirrors the position stop at 1R and reports display distances', () => {
    const points = buildPoints('position', { time: 0, price: 100 }, { time: 10, price: 110 })
    expect(points).toEqual([{ time: 0, price: 100 }, { time: 10, price: 110 }, { time: 10, price: 90 }])
    expect(positionStats(points)).toEqual({ side: 'long', targetPercent: 10, stopPercent: 10, ratio: 1 })
    expect(positionStats([{ time: 0, price: 100 }, { time: 1, price: 80 }, { time: 1, price: 105 }])).toMatchObject({ side: 'short', ratio: 4 })
    expect(buildPoints('position', { time: 0, price: 10 }, { time: 1, price: 50 })[2]!.price).toBeGreaterThan(0)
  })
  it('includes the golden pocket between 0.618 and 0.65', () => {
    expect(fibonacciLevels).toEqual([0, 0.236, 0.382, 0.5, 0.618, 0.65, 0.786, 1])
    expect(goldenPocket.map(level => fibonacciPrice({ time: 0, price: 100 }, { time: 1, price: 200 }, level))).toEqual([138.2, 135])
  })
  it('measures price and date ranges for display', () => {
    expect(priceRangeStats({ time: 0, price: 80 }, { time: 1, price: 100 })).toEqual({ change: 20, percent: 25 })
    expect(priceRangeStats({ time: 0, price: 100 }, { time: 1, price: 80 })).toEqual({ change: -20, percent: -20 })
    expect(formatDuration(45 * 60_000)).toBe('45m')
    expect(formatDuration(-(2 * 60 + 15) * 60_000)).toBe('2h 15m')
    expect(formatDuration((3 * 24 + 4) * 3_600_000)).toBe('3d 4h')
    expect(formatDuration(2 * 24 * 3_600_000)).toBe('2d')
  })
  it('validates stored drawings by kind and anchors', () => {
    const valid: ChartDrawing = { id: 'a', schemaVersion: 1, kind: 'trend-line', points: [{ time: 1, price: 2 }, { time: 3, price: 4 }] }
    expect(isChartDrawing(valid)).toBe(true)
    expect(isChartDrawing({ ...valid, points: [{ time: 1, price: 2 }] })).toBe(false)
    expect(isChartDrawing({ ...valid, schemaVersion: 2 })).toBe(false)
    expect(isChartDrawing({ ...valid, points: [{ time: 1, price: -2 }, { time: 3, price: 4 }] })).toBe(false)
    expect(isChartDrawing({ ...valid, kind: 'position', points: [{ time: 1, price: 2 }, { time: 3, price: 4 }, { time: 3, price: 1 }] })).toBe(true)
    expect(isChartDrawing({ ...valid, kind: 'vertical-line', points: [{ time: 1, price: 2 }] })).toBe(true)
    expect(isChartDrawing({ ...valid, kind: 'date-range' })).toBe(true)
    expect(isChartDrawing({ ...valid, kind: 'price-range', points: [{ time: 1, price: 2 }] })).toBe(false)
  })
})

/** 1 px per minute and 1 px per price unit, inverted like a screen. */
const space: DrawingSpace = {
  x: time => time / 60_000,
  y: price => 1000 - price,
  point: (x, y, snap) => ({ time: (snap ? Math.round(x / 60) * 60 : x) * 60_000, price: 1000 - y }),
  width: () => 800,
  height: () => 600,
  formatPrice: price => price.toFixed(2),
  formatTime: time => `t${time}`,
  bars: (from, to) => (to - from) / 3_600_000,
}

function controller() {
  const callbacks = { onDrawingCreate: vi.fn(), onDrawingChange: vi.fn(), onDrawingSelect: vi.fn() }
  return { callbacks, drawings: new DrawingController(space, callbacks, vi.fn()) }
}

describe('drawing controller', () => {
  it('creates two-point drawings by dragging or by two clicks', () => {
    const { callbacks, drawings } = controller()
    drawings.setTool('trend-line')
    expect(drawings.pointerDown(100, 500)).toBe(true)
    drawings.pointerMove(150, 450)
    drawings.pointerUp(200, 400)
    expect(callbacks.onDrawingCreate).toHaveBeenLastCalledWith(expect.objectContaining({
      kind: 'trend-line', schemaVersion: 1, points: [{ time: 6_000_000, price: 500 }, { time: 12_000_000, price: 600 }],
    }))
    drawings.pointerDown(100, 500)
    drawings.pointerUp(100, 500)
    expect(drawings.busy).toBe(true)
    drawings.pointerMove(300, 300)
    drawings.pointerDown(300, 300)
    expect(callbacks.onDrawingCreate).toHaveBeenCalledTimes(2)
    expect(drawings.busy).toBe(false)
  })

  it('creates one-point drawings immediately and applies the magnet', () => {
    const { callbacks, drawings } = controller()
    drawings.magnet = true
    drawings.setTool('text')
    drawings.pointerDown(130, 500)
    expect(callbacks.onDrawingCreate).toHaveBeenCalledWith(expect.objectContaining({ kind: 'text', text: 'Note', points: [{ time: 120 * 60_000, price: 500 }] }))
  })

  it('selects, moves and resizes existing drawings, and leaves empty clicks to the chart', () => {
    const { callbacks, drawings } = controller()
    const line: ChartDrawing = { id: 't', schemaVersion: 1, kind: 'trend-line', points: [{ time: 6_000_000, price: 500 }, { time: 12_000_000, price: 600 }] }
    drawings.drawings = [line]
    expect(drawings.pointerDown(150, 450)).toBe(true)
    expect(callbacks.onDrawingSelect).toHaveBeenCalledWith('t')
    drawings.pointerMove(160, 440)
    drawings.pointerUp(160, 440)
    expect(callbacks.onDrawingChange).toHaveBeenLastCalledWith({ ...line, points: [{ time: 6_600_000, price: 510 }, { time: 12_600_000, price: 610 }] }, 'end')

    drawings.selectedId = 't'
    drawings.pointerDown(200, 400)
    drawings.pointerMove(250, 380)
    drawings.pointerUp(250, 380)
    expect(callbacks.onDrawingChange).toHaveBeenLastCalledWith({ ...line, points: [line.points[0], { time: 15_000_000, price: 620 }] }, 'end')

    expect(drawings.pointerDown(700, 50)).toBe(false)
    expect(callbacks.onDrawingSelect).toHaveBeenLastCalledWith(null)
  })

  it('moves a vertical line only in time and selects it near its x', () => {
    const { callbacks, drawings } = controller()
    drawings.setTool('vertical-line')
    drawings.pointerDown(300, 420)
    const created = callbacks.onDrawingCreate.mock.lastCall![0] as ChartDrawing
    expect(created).toMatchObject({ kind: 'vertical-line', points: [{ time: 18_000_000, price: 580 }] })
    drawings.setTool(null)
    drawings.drawings = [created]
    expect(drawings.drawingAt(303, 50)?.id).toBe(created.id)
    expect(drawings.drawingAt(320, 50)).toBeNull()
    drawings.pointerDown(300, 100)
    drawings.pointerMove(340, 160)
    drawings.pointerUp(340, 160)
    expect(callbacks.onDrawingChange).toHaveBeenLastCalledWith({ ...created, points: [{ time: 20_400_000, price: 580 }] }, 'end')
  })

  it('draws date and price ranges as two-point boxes with handles', () => {
    const { callbacks, drawings } = controller()
    for (const kind of ['date-range', 'price-range'] as const) {
      drawings.setTool(kind)
      drawings.pointerDown(100, 500)
      drawings.pointerMove(200, 400)
      drawings.pointerUp(200, 400)
      expect(callbacks.onDrawingCreate).toHaveBeenLastCalledWith(expect.objectContaining({
        kind, points: [{ time: 6_000_000, price: 500 }, { time: 12_000_000, price: 600 }],
      }))
    }
    const range = callbacks.onDrawingCreate.mock.lastCall![0] as ChartDrawing
    drawings.setTool(null)
    drawings.drawings = [range]
    drawings.selectedId = range.id
    expect(drawings.cursor(200, 400)).toBe('grab')
    expect(drawings.cursor(150, 450)).toBe('move')
  })

  it('places long and short boxes by a click or sizes them by a drag, on the side of the tool', () => {
    const { callbacks, drawings } = controller()
    drawings.setTool('long-position')
    drawings.pointerDown(100, 500)
    drawings.pointerUp(100, 500)
    // A click: entry at the pointer, target 60 px above, stop 40 px below, 140 px wide.
    expect(callbacks.onDrawingCreate).toHaveBeenLastCalledWith(expect.objectContaining({
      kind: 'long-position', points: [{ time: 6_000_000, price: 500 }, { time: 14_400_000, price: 560 }, { time: 14_400_000, price: 460 }],
    }))
    drawings.setTool('short-position')
    drawings.pointerDown(100, 500)
    drawings.pointerMove(200, 450)
    drawings.pointerUp(200, 450)
    // A drag upwards still makes a short: target below, stop mirrored above at 1R.
    expect(callbacks.onDrawingCreate).toHaveBeenLastCalledWith(expect.objectContaining({
      kind: 'short-position', points: [{ time: 6_000_000, price: 500 }, { time: 12_000_000, price: 450 }, { time: 12_000_000, price: 550 }],
    }))
    expect(isChartDrawing(callbacks.onDrawingCreate.mock.lastCall![0])).toBe(true)
  })

  it('keeps position target and stop on the shared end time', () => {
    const { callbacks, drawings } = controller()
    const position: ChartDrawing = { id: 'p', schemaVersion: 1, kind: 'position', points: buildPoints('position', { time: 6_000_000, price: 500 }, { time: 12_000_000, price: 550 }) }
    drawings.drawings = [position]
    drawings.selectedId = 'p'
    drawings.pointerDown(200, 550)
    drawings.pointerMove(260, 560)
    drawings.pointerUp(260, 560)
    expect(callbacks.onDrawingChange).toHaveBeenLastCalledWith({ ...position, points: [
      { time: 6_000_000, price: 500 }, { time: 15_600_000, price: 550 }, { time: 15_600_000, price: 440 },
    ] }, 'end')
  })

  it('moves horizontal lines by price only and cancels creation', () => {
    const { callbacks, drawings } = controller()
    const level: ChartDrawing = { id: 'h', schemaVersion: 1, kind: 'horizontal-line', points: [{ time: 6_000_000, price: 500 }] }
    drawings.drawings = [level]
    drawings.pointerDown(400, 500)
    drawings.pointerMove(450, 480)
    drawings.pointerUp(450, 480)
    expect(callbacks.onDrawingChange).toHaveBeenLastCalledWith({ ...level, points: [{ time: 6_000_000, price: 520 }] }, 'end')
    drawings.setTool('zone')
    drawings.pointerDown(10, 10)
    drawings.cancel()
    expect(drawings.busy).toBe(false)
    expect(callbacks.onDrawingCreate).not.toHaveBeenCalled()
  })
})

describe('direction-sensitive measurement', () => {
  const render = (drawings: DrawingController) => {
    const context = {
      save: vi.fn(), restore: vi.fn(), setLineDash: vi.fn(), beginPath: vi.fn(), moveTo: vi.fn(), lineTo: vi.fn(),
      stroke: vi.fn(), fill: vi.fn(), arc: vi.fn(), fillRect: vi.fn(), strokeRect: vi.fn(), fillText: vi.fn(),
      measureText: (text: string) => ({ width: text.length * 6 }),
    }
    drawings.render(context as unknown as CanvasRenderingContext2D, {
      line: '#8fb8ff', text: '#ffffff', muted: '#aaaaaa', background: '#111111',
      positive: '#00ff00', negative: '#ff0000', font: 'sans-serif',
    })
    return context
  }

  it.each([
    ['right', 520, 500, '2 bars · 2h'],
    ['left', 280, 500, '2 bars · 2h'],
    ['up', 400, 400, '+100.00 (+20.00%)'],
    ['down', 400, 600, '−100.00 (−20.00%)'],
  ] as const)('measures %s, then keeps an editable drawing on release', (_, x, y, text) => {
    const { callbacks, drawings } = controller()
    expect(drawings.pointerDown(400, 500, true)).toBe(true)
    drawings.pointerMove(x, y)
    expect(drawings.measuring).toBe(true)
    expect(drawings.cursor(x, y)).toBe('crosshair')
    const context = render(drawings)
    expect(context.fillText).toHaveBeenCalledWith(text, expect.any(Number), expect.any(Number))
    expect(context.arc).not.toHaveBeenCalled() // Editing handles appear after release.
    const [, tagX, tagY] = context.fillText.mock.lastCall!
    expect(tagX).toBeGreaterThanOrEqual(text.length * 3 + 7)
    expect(tagX).toBeLessThanOrEqual(space.width() - text.length * 3 - 7)
    expect(tagY).toBeGreaterThanOrEqual(12)
    expect(tagY).toBeLessThanOrEqual(space.height() - 12)
    drawings.pointerUp(x, y)
    expect(drawings.busy).toBe(false)
    expect(callbacks.onDrawingCreate).toHaveBeenCalledOnce()
    const created = callbacks.onDrawingCreate.mock.lastCall![0] as ChartDrawing
    expect(created).toMatchObject({
      kind: x === 400 ? 'price-range' : 'date-range',
      points: [{ time: 400 * 60_000, price: 500 }, { time: x * 60_000, price: 1000 - y }],
    })
    expect(isChartDrawing(created)).toBe(true)
    drawings.drawings = [created]
    drawings.selectedId = created.id
    const kept = render(drawings)
    expect(kept.fillText).toHaveBeenCalledWith(text, expect.any(Number), expect.any(Number))
    expect(kept.arc).toHaveBeenCalledTimes(2)
    expect(callbacks.onDrawingChange).not.toHaveBeenCalled()
    expect(callbacks.onDrawingSelect).not.toHaveBeenCalled()
  })

  it('uses the release coordinates and gives each measurement its own ID', () => {
    const { callbacks, drawings } = controller()
    drawings.setTool('text')
    drawings.pointerDown(400, 500, true)
    drawings.pointerMove(520, 500)
    drawings.pointerUp(405, 400)
    const first = callbacks.onDrawingCreate.mock.lastCall![0] as ChartDrawing
    expect(first).toMatchObject({ kind: 'price-range', points: [{ time: 24_000_000, price: 500 }, { time: 24_300_000, price: 600 }] })
    expect(first.text).toBeUndefined()
    drawings.drawings = [first]
    drawings.pointerDown(400, 500, true)
    drawings.pointerUp(520, 500)
    const second = callbacks.onDrawingCreate.mock.lastCall![0] as ChartDrawing
    expect(second.kind).toBe('date-range')
    expect(second.id).not.toBe(first.id)
    expect(callbacks.onDrawingCreate).toHaveBeenCalledTimes(2)
  })

  it('respects the existing drawing limit when keeping a measurement', () => {
    const { callbacks, drawings } = controller()
    drawings.drawings = Array.from({ length: 200 }, (_, index) => ({
      id: `drawing-${index}`, schemaVersion: 1, kind: 'vertical-line', points: [{ time: 1, price: 500 }],
    }))
    drawings.pointerDown(400, 500, true)
    drawings.pointerMove(520, 500)
    drawings.pointerUp(520, 500)
    expect(drawings.busy).toBe(false)
    expect(callbacks.onDrawingCreate).not.toHaveBeenCalled()
  })

  it('switches axes with the dominant displacement and hides the readout back at the start', () => {
    const { drawings } = controller()
    drawings.pointerDown(400, 500, true)
    drawings.pointerMove(520, 490)
    expect(render(drawings).fillText).toHaveBeenCalledWith('2 bars · 2h', expect.any(Number), expect.any(Number))
    drawings.pointerMove(405, 400)
    expect(render(drawings).fillText).toHaveBeenCalledWith('+100.00 (+20.00%)', expect.any(Number), expect.any(Number))
    drawings.pointerMove(400, 500)
    expect(render(drawings).fillText).not.toHaveBeenCalled()
  })

  it('temporarily overrides the active tool and magnet without changing selection or cancelling an edit', () => {
    const events = { onDrawingCreate: vi.fn(), onDrawingChange: vi.fn(), onDrawingSelect: vi.fn(), onDrawingCancel: vi.fn() }
    const point = vi.fn(space.point)
    const drawings = new DrawingController({ ...space, point }, events, vi.fn())
    drawings.setTool('text')
    drawings.magnet = true
    drawings.drawings = [{ id: 'selected', schemaVersion: 1, kind: 'vertical-line', points: [{ time: 6_000_000, price: 500 }] }]
    drawings.selectedId = 'selected'
    drawings.pointerDown(130, 500, true)
    drawings.pointerMove(135, 400)
    expect(point.mock.calls.every(([, , snap]) => snap === false)).toBe(true)
    drawings.cancel()
    expect(drawings.tool).toBe('text')
    expect(drawings.selectedId).toBe('selected')
    expect(drawings.drawings).toHaveLength(1)
    for (const callback of Object.values(events)) expect(callback).not.toHaveBeenCalled()
    drawings.pointerDown(130, 500)
    expect(events.onDrawingCreate).toHaveBeenCalledWith(expect.objectContaining({ kind: 'text' }))
  })

  it('does not turn a Shift-click into a pending two-click drawing', () => {
    const { callbacks, drawings } = controller()
    drawings.setTool('trend-line')
    drawings.pointerDown(400, 500, true)
    drawings.pointerUp(400, 500)
    expect(drawings.busy).toBe(false)
    drawings.pointerMove(500, 400)
    expect(render(drawings).fillText).not.toHaveBeenCalled()
    expect(callbacks.onDrawingCreate).not.toHaveBeenCalled()
  })
})

describe('drawing hit areas', () => {
  it('keeps drawings inside a Fibonacci reachable and selects the Fibonacci elsewhere in it', () => {
    const { callbacks, drawings } = controller()
    const fib: ChartDrawing = { id: 'f', schemaVersion: 1, kind: 'fibonacci', points: [{ time: 6_000_000, price: 400 }, { time: 18_000_000, price: 600 }] }
    // Between the 0.382 (523.6) and 0.5 (500) levels.
    const line: ChartDrawing = { id: 't', schemaVersion: 1, kind: 'trend-line', points: [{ time: 9_000_000, price: 512 }, { time: 15_000_000, price: 512 }] }
    drawings.drawings = [line, fib]
    drawings.pointerDown(200, 488)
    expect(callbacks.onDrawingSelect).toHaveBeenLastCalledWith('t')
    drawings.pointerUp(200, 488)
    drawings.pointerDown(250, 500)
    expect(callbacks.onDrawingSelect).toHaveBeenLastCalledWith('f')
  })

  it('prefers the nearest stroke when a click is close to several drawings', () => {
    const { callbacks, drawings } = controller()
    const fib: ChartDrawing = { id: 'f', schemaVersion: 1, kind: 'fibonacci', points: [{ time: 6_000_000, price: 400 }, { time: 18_000_000, price: 600 }] }
    const line: ChartDrawing = { id: 't', schemaVersion: 1, kind: 'trend-line', points: [{ time: 9_000_000, price: 504 }, { time: 15_000_000, price: 504 }] }
    drawings.drawings = [line, fib]
    drawings.pointerDown(200, 496)
    expect(callbacks.onDrawingSelect).toHaveBeenLastCalledWith('t')
  })
})

describe('locked drawings', () => {
  it('select without moving or exposing handles', () => {
    const { callbacks, drawings } = controller()
    const line: ChartDrawing = { id: 't', schemaVersion: 1, kind: 'trend-line', locked: true, points: [{ time: 6_000_000, price: 500 }, { time: 12_000_000, price: 600 }] }
    drawings.drawings = [line]
    drawings.selectedId = 't'
    expect(drawings.pointerDown(200, 400)).toBe(true)
    expect(drawings.busy).toBe(false)
    drawings.pointerMove(260, 380)
    drawings.pointerUp(260, 380)
    expect(callbacks.onDrawingChange).not.toHaveBeenCalled()
    expect(drawings.cursor(150, 450)).toBe('pointer')
  })
  it('accepts optional style and lock fields in stored drawings', () => {
    const base: ChartDrawing = { id: 'a', schemaVersion: 1, kind: 'zone', points: [{ time: 1, price: 2 }, { time: 3, price: 4 }] }
    expect(isChartDrawing({ ...base, locked: true, style: { color: '#8fb8ff', line: 'dotted', width: 2 } })).toBe(true)
    expect(isChartDrawing({ ...base, style: { color: 'red' } })).toBe(false)
    expect(isChartDrawing({ ...base, style: { width: 5 } })).toBe(false)
  })
})
