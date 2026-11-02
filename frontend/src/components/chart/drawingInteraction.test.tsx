import { useState } from 'react'
import { act, renderHook } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { DrawingController, type DrawingSpace } from './drawingController'
import type { ChartDrawing } from './drawings'
import { FrameEdit, roundedLevelPrice } from './frameEdit'
import { testFrames } from '@/test/testFrames'
import { useChartHistory } from './useChartHistory'
import { useDrawingEditor } from './useDrawingEditor'

const line: ChartDrawing = { id: 'line', schemaVersion: 1, kind: 'vertical-line', points: [{ time: 100, price: 500 }] }
const space: DrawingSpace = {
  x: time => time, y: price => 1000 - price, point: (x, y) => ({ time: x, price: 1000 - y }),
  width: () => 800, height: () => 1000, formatPrice: String, formatTime: String, bars: (a, b) => b - a,
}
const callbacks = () => ({ onDrawingCreate: vi.fn(), onDrawingChange: vi.fn(), onDrawingSelect: vi.fn(), onDrawingCancel: vi.fn() })

it('keeps a Shift-drag measurement through the editor with undo, redo and delete', () => {
  const { result } = renderHook(() => {
    const [drawings, setDrawings] = useState<ChartDrawing[]>([])
    const history = useChartHistory('instrument')
    return { drawings, history, editor: useDrawingEditor(drawings, setDrawings, 'instrument', history) }
  })
  const controller = new DrawingController(space, {
    ...callbacks(), onDrawingCreate: drawing => result.current.editor.create(drawing),
  }, vi.fn())
  act(() => {
    controller.pointerDown(100, 500, true)
    controller.pointerMove(200, 510)
    expect(result.current.drawings).toEqual([])
    controller.pointerUp(220, 510)
  })
  const measurement = result.current.drawings[0]!
  expect(measurement).toMatchObject({
    kind: 'date-range', points: [{ time: 100, price: 500 }, { time: 220, price: 490 }],
  })
  expect(result.current.editor.selectedId).toBe(measurement.id)
  expect(result.current.history.undoLabel).toBe('Add drawing')
  act(() => result.current.history.undo())
  expect(result.current.drawings).toEqual([])
  expect(result.current.history.canUndo).toBe(false)
  act(() => result.current.history.redo())
  expect(result.current.drawings).toEqual([measurement])
  act(() => result.current.editor.remove())
  expect(result.current.drawings).toEqual([])
  expect(result.current.history.undoLabel).toBe('Delete drawing')
  act(() => result.current.history.undo())
  expect(result.current.drawings).toEqual([measurement])
})

describe('frame-batched drawing gestures', () => {
  it('publishes the latest time-only move once per frame and previews it immediately', () => {
    const frames = testFrames(); const events = callbacks(); const redraw = vi.fn()
    const controller = new DrawingController(space, events, redraw)
    controller.drawings = [line]
    controller.pointerDown(100, 200)
    controller.pointerMove(110, 200); controller.pointerMove(120, 200)
    expect(events.onDrawingChange).not.toHaveBeenCalled()
    expect(redraw).toHaveBeenCalledTimes(2)
    frames.run()
    expect(events.onDrawingChange).toHaveBeenCalledExactlyOnceWith({ ...line, points: [{ time: 120, price: 500 }] }, 'move')
    controller.pointerMove(130, 200); frames.run()
    expect(events.onDrawingChange).toHaveBeenLastCalledWith({ ...line, points: [{ time: 130, price: 500 }] }, 'move')
    controller.cancel()
  })

  it.each(['move', 'resize'] as const)('pointerup flushes %s before the scheduled frame', mode => {
    const frames = testFrames(); const events = callbacks()
    const controller = new DrawingController(space, events, vi.fn())
    controller.drawings = [line]
    controller.selectedId = mode === 'resize' ? line.id : null
    const y = mode === 'resize' ? 500 : 200
    controller.pointerDown(100, y); controller.pointerMove(120, y); controller.pointerUp(140, y)
    expect(events.onDrawingChange).toHaveBeenCalledExactlyOnceWith({ ...line, points: [{ time: 140, price: 500 }] }, 'end')
    frames.run()
    expect(events.onDrawingChange).toHaveBeenCalledTimes(1)
  })

  it('cancel drops the scheduled change without an end or undo entry', () => {
    const frames = testFrames(); const events = callbacks()
    const controller = new DrawingController(space, events, vi.fn())
    controller.drawings = [line]
    controller.pointerDown(100, 200); controller.pointerMove(140, 200); controller.cancel(); frames.run()
    expect(events.onDrawingChange).not.toHaveBeenCalled()
    expect(events.onDrawingCancel).toHaveBeenCalledTimes(1)
    expect(controller.busy).toBe(false)
  })

  it('ends the gesture when the release cannot be converted to a chart point', () => {
    const frames = testFrames(); const events = callbacks()
    // Releases below the price scale have no positive price.
    const bounded: DrawingSpace = { ...space, point: (x, y) => y > 1000 ? null : { time: x, price: 1000 - y } }
    const controller = new DrawingController(bounded, events, vi.fn())
    const trend: ChartDrawing = { id: 'trend', schemaVersion: 1, kind: 'trend-line', points: [{ time: 100, price: 500 }, { time: 300, price: 600 }] }
    controller.drawings = [trend]
    controller.selectedId = trend.id
    controller.pointerDown(100, 500); controller.pointerMove(120, 520); frames.run()
    controller.pointerUp(130, 1200); frames.run()
    expect(events.onDrawingChange).toHaveBeenCalledOnce()
    expect(events.onDrawingCancel).toHaveBeenCalledOnce()
    expect(controller.busy).toBe(false)
  })

  it('records no undo step for a gesture that ends where it started', () => {
    const { result } = renderHook(() => {
      const [drawings, setDrawings] = useState([line])
      const history = useChartHistory('instrument')
      return { history, editor: useDrawingEditor(drawings, setDrawings, 'instrument', history) }
    })
    act(() => result.current.editor.change({ ...line, points: [{ time: 120, price: 500 }] }, 'move'))
    act(() => result.current.editor.change(line, 'end'))
    expect(result.current.history.canUndo).toBe(false)
  })

  it('records one undo step for the entire gesture, and keeps a cancelled gesture as its own step', () => {
    const frames = testFrames()
    const { result } = renderHook(() => {
      const [drawings, setDrawings] = useState([line])
      const history = useChartHistory('instrument')
      return { drawings, history, editor: useDrawingEditor(drawings, setDrawings, 'instrument', history) }
    })
    const controller = new DrawingController(space, {
      ...callbacks(), onDrawingChange: (drawing, phase) => result.current.editor.change(drawing, phase),
      onDrawingCancel: () => result.current.editor.cancelGesture(),
    }, vi.fn())
    controller.drawings = result.current.drawings
    act(() => { controller.pointerDown(100, 200); controller.pointerMove(120, 200); frames.run() })
    controller.drawings = result.current.drawings
    act(() => { controller.pointerMove(130, 200); frames.run() })
    controller.drawings = result.current.drawings
    act(() => controller.pointerUp(140, 200))
    expect(result.current.history.canUndo).toBe(true)
    act(() => result.current.history.undo())
    expect(result.current.drawings).toEqual([line])
    expect(result.current.history.canUndo).toBe(false)
    act(() => result.current.history.redo())
    expect(result.current.drawings[0]!.points[0]!.time).toBe(140)

    controller.drawings = result.current.drawings
    act(() => { controller.pointerDown(140, 200); controller.pointerMove(160, 200); frames.run() })
    act(() => controller.cancel())
    controller.drawings = result.current.drawings
    act(() => { controller.pointerDown(160, 200); controller.pointerMove(180, 200); controller.pointerUp(190, 200) })
    act(() => result.current.history.undo())
    expect(result.current.drawings[0]!.points[0]!.time).toBe(160)
    act(() => result.current.history.undo())
    expect(result.current.drawings[0]!.points[0]!.time).toBe(140)
  })
})

describe('frame-batched level publications', () => {
  it.each([null, 0.25, 0.00001])('skips equal rounded prices (step %s), but always ends', step => {
    const frames = testFrames(); const publish = vi.fn()
    const price = step === 0.00001 ? 0.12345 : 100
    const epsilon = step === null ? 0.0001 : step / 10
    const edit = new FrameEdit<number>(publish, (a, b) => roundedLevelPrice(a, step) === roundedLevelPrice(b, step))
    edit.move(price); frames.run(); edit.move(price + epsilon); frames.run()
    expect(publish).toHaveBeenCalledTimes(1)
    edit.move(price + epsilon * 2); edit.end(price + epsilon * 3); frames.run()
    expect(publish).toHaveBeenCalledTimes(2)
    expect(publish).toHaveBeenLastCalledWith(price + epsilon * 3, 'end')
  })

  it('skips a first move that rounds to the starting price', () => {
    const frames = testFrames(); const publish = vi.fn()
    const edit = new FrameEdit<number>(publish, (a, b) => roundedLevelPrice(a, 0.25) === roundedLevelPrice(b, 0.25), 100)
    edit.move(100.01); frames.run()
    expect(publish).not.toHaveBeenCalled()
    edit.move(100.3); frames.run()
    expect(publish).toHaveBeenCalledExactlyOnceWith(100.3, 'move')
  })
})
