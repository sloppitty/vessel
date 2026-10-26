import {
  distanceToSegment, drawingSchemaVersion, fibonacciLevels, fibonacciPrice, goldenPocket, formatDuration, isPositionKind, pointCount, positionPoints, positionStats,
  sidedPositionPoints,
  priceRangeStats, type ChartDrawing, type DrawingKind, type DrawingPoint,
} from './drawings'
import { FrameEdit } from './frameEdit'

/** Pane coordinate conversions supplied by the renderer adapter. */
export interface DrawingSpace {
  x(time: number): number | null
  y(price: number): number | null
  /** Inverse conversion; `snap` applies the magnet when enabled. */
  point(x: number, y: number, snap: boolean): DrawingPoint | null
  width(): number
  height(): number
  formatPrice(price: number): string
  formatTime(time: number): string
  /** Bars between two times at the loaded interval, or null without candles. */
  bars(from: number, to: number): number | null
}

export interface DrawingCallbacks {
  onDrawingCreate(drawing: ChartDrawing): void
  onDrawingChange(drawing: ChartDrawing, phase: 'move' | 'end'): void
  onDrawingSelect(id: string | null): void
  onDrawingCancel?(): void
}

export interface DrawingTheme {
  line: string
  text: string
  muted: string
  background: string
  positive: string
  negative: string
  font: string
}

type Gesture =
  | { type: 'create'; start: DrawingPoint; startX: number; startY: number; moved: boolean; awaiting: boolean }
  | { type: 'measure'; start: DrawingPoint; startX: number; startY: number }
  | { type: 'move'; drawing: ChartDrawing; x: number; y: number; moved: boolean }
  | { type: 'handle'; drawing: ChartDrawing; index: number; moved: boolean }

/** Default level colors, from the swing end (0) to its start (1); the golden pocket levels are gold. */
const fibonacciColors = ['#9aa4b2', '#ef5f6b', '#f59a3c', '#4cbf7f', '#e3b341', '#e3b341', '#4f9cf0', '#b07fe0'] as const
const gold = '#e3b341'

const hitTolerance = 6
const handleRadius = 5
const maxDrawings = 200
const textFont = 12

let nextId = 0
// Session-local drawing IDs also work on HTTP LAN previews and remain stable when saved.
const drawingId = () => `drawing-${Date.now().toString(36)}-${++nextId}`

export function buildPoints(kind: DrawingKind, start: DrawingPoint, end: DrawingPoint): DrawingPoint[] {
  if (pointCount[kind] === 1) return [start]
  if (kind === 'long-position' || kind === 'short-position') return sidedPositionPoints(kind === 'long-position' ? 'long' : 'short', start, end)
  if (kind === 'position') {
    const points = positionPoints(start, end)
    // Keep the mirrored stop above zero for very wide long boxes.
    points[2] = { ...points[2]!, price: Math.max(points[2]!.price, start.price * 0.0001) }
    return points
  }
  return [start, end]
}

/** Creates, selects, moves and resizes drawings from pane pointer events, and renders them. */
export class DrawingController {
  drawings: readonly ChartDrawing[] = []
  selectedId: string | null = null
  tool: DrawingKind | null = null
  magnet = false
  private gesture: Gesture | null = null
  private preview: ChartDrawing | null = null
  private readonly edit: FrameEdit<ChartDrawing>
  /** Pane x of the free vertical guide shown while a tool is active, or null. */
  private guide: number | null = null

  constructor(private readonly space: DrawingSpace, private readonly callbacks: DrawingCallbacks, private readonly requestUpdate: () => void) {
    this.edit = new FrameEdit((drawing, phase) => this.callbacks.onDrawingChange(drawing, phase))
  }

  get busy() { return this.gesture !== null }
  get measuring() { return this.gesture?.type === 'measure' }

  setGuide(x: number | null) {
    if (x === this.guide) return
    this.guide = x
    this.requestUpdate()
  }

  setTool(tool: DrawingKind | null) {
    this.tool = tool
    this.cancel()
  }

  cancel() {
    if (this.gesture?.type === 'move' || this.gesture?.type === 'handle') this.callbacks.onDrawingCancel?.()
    this.edit.cancel()
    this.gesture = null
    this.preview = null
    this.requestUpdate()
  }

  /** Returns true when the event belongs to drawings and must not pan the chart. */
  pointerDown(x: number, y: number, measure = false) {
    if (measure) {
      this.cancel()
      this.setGuide(null)
      const start = this.space.point(x, y, false)
      if (start) this.gesture = { type: 'measure', start, startX: x, startY: y }
      return true
    }
    const gesture = this.gesture
    if (gesture?.type === 'create' && gesture.awaiting) return this.finishCreate(x, y)
    if (this.tool) {
      const start = this.space.point(x, y, this.magnet)
      if (!start) return true
      if (pointCount[this.tool] === 1) {
        this.create(buildPoints(this.tool, start, start))
        return true
      }
      this.gesture = { type: 'create', start, startX: x, startY: y, moved: false, awaiting: false }
      this.preview = this.draft(buildPoints(this.tool, start, start))
      this.requestUpdate()
      return true
    }
    const selected = this.drawings.find(drawing => drawing.id === this.selectedId)
    const handle = selected && !selected.locked ? this.handleAt(selected, x, y) : null
    if (selected && handle !== null) {
      this.gesture = { type: 'handle', drawing: selected, index: handle, moved: false }
      return true
    }
    const hit = this.drawingAt(x, y)
    if (hit) {
      if (hit.id !== this.selectedId) this.callbacks.onDrawingSelect(hit.id)
      // Locked drawings select but never move, so a stray drag cannot shift them.
      if (!hit.locked) this.gesture = { type: 'move', drawing: hit, x, y, moved: false }
      return true
    }
    if (this.selectedId) this.callbacks.onDrawingSelect(null)
    return false
  }

  pointerMove(x: number, y: number) {
    const gesture = this.gesture
    if (!gesture) return false
    if (gesture.type === 'measure') {
      const end = this.space.point(x, y, false)
      if (end && Math.hypot(x - gesture.startX, y - gesture.startY) > 4) {
        this.preview = {
          id: 'measurement', schemaVersion: drawingSchemaVersion,
          kind: Math.abs(x - gesture.startX) >= Math.abs(y - gesture.startY) ? 'date-range' : 'price-range',
          points: [gesture.start, end],
        }
      } else this.preview = null
      this.requestUpdate()
      return true
    }
    if (gesture.type === 'create') {
      if (Math.hypot(x - gesture.startX, y - gesture.startY) > 4) gesture.moved = true
      const end = this.space.point(x, y, this.magnet)
      if (end && this.tool) this.preview = this.draft(buildPoints(this.tool, gesture.start, end))
      this.requestUpdate()
      return true
    }
    if (gesture.type === 'handle') {
      const point = this.space.point(x, y, this.magnet)
      if (!point) return true
      gesture.moved = true
      this.move(this.resize(gesture.drawing, gesture.index, point))
      return true
    }
    if (!gesture.moved && Math.hypot(x - gesture.x, y - gesture.y) < 3) return true
    gesture.moved = true
    const moved = this.translate(gesture.drawing, x - gesture.x, y - gesture.y)
    if (moved) this.move(moved)
    return true
  }

  pointerUp(x: number, y: number) {
    const gesture = this.gesture
    if (!gesture) return false
    if (gesture.type === 'measure') {
      this.pointerMove(x, y)
      const measurement = this.preview
      this.cancel()
      if (measurement) this.create(measurement.points, measurement.kind)
      return true
    }
    if (gesture.type === 'create') {
      if (gesture.moved) return this.finishCreate(x, y)
      // A click places a long or short box of a default size, ready to adjust by its handles.
      if (this.tool === 'long-position' || this.tool === 'short-position') {
        const points = this.defaultPosition(this.tool, gesture.startX, gesture.startY)
        this.gesture = null
        this.preview = null
        if (points) this.create(points)
        else this.requestUpdate()
        return true
      }
      // A click without dragging waits for a second click.
      gesture.awaiting = true
      return true
    }
    this.gesture = null
    if (!gesture.moved) return true
    const finished = gesture.type === 'handle'
      ? (() => { const point = this.space.point(x, y, this.magnet); return point ? this.resize(gesture.drawing, gesture.index, point) : null })()
      : this.translate(gesture.drawing, x - gesture.x, y - gesture.y)
    if (finished) this.edit.end(finished)
    else {
      // A release off the price scale ends the gesture with whatever was already published.
      this.edit.cancel()
      this.callbacks.onDrawingCancel?.()
    }
    this.preview = null
    this.requestUpdate()
    return true
  }

  private move(drawing: ChartDrawing) {
    this.preview = drawing
    this.requestUpdate()
    this.edit.move(drawing)
  }

  cursor(x: number, y: number) {
    if (this.tool || this.gesture?.type === 'create' || this.measuring) return 'crosshair'
    const selected = this.drawings.find(drawing => drawing.id === this.selectedId)
    if (selected && !selected.locked && this.handleAt(selected, x, y) !== null) return 'grab'
    const hit = this.drawingAt(x, y)
    return hit ? hit.locked ? 'pointer' : 'move' : null
  }

  private finishCreate(x: number, y: number) {
    const gesture = this.gesture
    if (gesture?.type !== 'create' || !this.tool) return true
    const end = this.space.point(x, y, this.magnet)
    this.gesture = null
    this.preview = null
    if (end && Math.hypot(x - gesture.startX, y - gesture.startY) >= 3) this.create(buildPoints(this.tool, gesture.start, end))
    else this.requestUpdate()
    return true
  }

  private create(points: DrawingPoint[], kind: DrawingKind | null = this.tool) {
    if (!kind || this.drawings.length >= maxDrawings) return
    const drawing = this.draft(points, kind)
    this.callbacks.onDrawingCreate(drawing)
    this.requestUpdate()
  }

  /** A box 140 px wide with the target 60 px and the stop 40 px from the entry, on the tool's side. */
  private defaultPosition(kind: 'long-position' | 'short-position', x: number, y: number): DrawingPoint[] | null {
    const sign = kind === 'long-position' ? -1 : 1
    const entry = this.space.point(x, y, this.magnet)
    const target = this.space.point(x + 140, y + sign * 60, false)
    const stop = this.space.point(x + 140, y - sign * 40, false)
    if (!entry || !target || !stop) return null
    return [entry, { time: target.time, price: target.price }, { time: target.time, price: stop.price }]
  }

  private draft(points: DrawingPoint[], kind: DrawingKind = this.tool!): ChartDrawing {
    return { id: drawingId(), schemaVersion: drawingSchemaVersion, kind, points, ...(kind === 'text' ? { text: 'Note' } : {}) }
  }

  private resize(drawing: ChartDrawing, index: number, point: DrawingPoint): ChartDrawing {
    const points = drawing.points.map(existing => ({ ...existing }))
    if (isPositionKind(drawing.kind)) {
      if (index === 0) points[0] = point
      else {
        // Target and stop share the box's end time; each handle moves its own price.
        points[1]!.time = point.time
        points[2]!.time = point.time
        points[index]!.price = point.price
      }
    } else points[index] = point
    return { ...drawing, points }
  }

  private translate(drawing: ChartDrawing, dx: number, dy: number): ChartDrawing | null {
    const points: DrawingPoint[] = []
    for (const point of drawing.points) {
      const x = this.space.x(point.time)
      const y = this.space.y(point.price)
      if (x === null || y === null) return null
      const moved = this.space.point(x + (drawing.kind === 'horizontal-line' ? 0 : dx), y + (drawing.kind === 'vertical-line' ? 0 : dy), false)
      if (!moved) return null
      points.push(drawing.kind === 'horizontal-line' ? { time: point.time, price: moved.price }
        : drawing.kind === 'vertical-line' ? { time: moved.time, price: point.price } : moved)
    }
    return { ...drawing, points }
  }

  private handles(drawing: ChartDrawing) {
    if (pointCount[drawing.kind] === 1) return []
    return drawing.points.map(point => ({ x: this.space.x(point.time), y: this.space.y(point.price) }))
  }

  private handleAt(drawing: ChartDrawing, x: number, y: number) {
    const index = this.handles(drawing).findIndex(handle =>
      handle.x !== null && handle.y !== null && Math.hypot(handle.x - x, handle.y - y) <= handleRadius + 3)
    return index >= 0 ? index : null
  }

  /** Nearest drawing under the pointer; filled areas rank behind strokes, later drawings win ties. */
  drawingAt(x: number, y: number) {
    let best: { drawing: ChartDrawing; distance: number } | null = null
    for (const drawing of this.drawings) {
      const distance = this.distance(drawing, x, y)
      if (distance !== null && (!best || distance <= best.distance)) best = { drawing, distance }
    }
    return best?.drawing ?? null
  }

  private distance(drawing: ChartDrawing, x: number, y: number): number | null {
    const xs = drawing.points.map(point => this.space.x(point.time))
    const ys = drawing.points.map(point => this.space.y(point.price))
    if (xs.some(value => value === null) || ys.some(value => value === null)) return null
    const [x0, x1 = x0] = xs as number[]
    const [y0, y1 = y0] = ys as number[]
    const stroke = (distance: number) => distance <= hitTolerance ? distance : null
    const area = (inside: boolean) => inside ? hitTolerance : null
    switch (drawing.kind) {
      case 'trend-line': return stroke(distanceToSegment(x, y, x0!, y0!, x1!, y1!))
      case 'horizontal-line': return stroke(Math.abs(y - y0!))
      case 'vertical-line': return stroke(Math.abs(x - x0!))
      case 'text': {
        const width = (drawing.text ?? '').length * textFont * 0.6 + 14
        return area(x >= x0! - 2 && x <= x0! + width && y >= y0! - 20 && y <= y0! + 4)
      }
      case 'long-position':
      case 'short-position':
      case 'position': {
        const y2 = (ys as number[])[2]!
        return area(within(x, x0!, x1!) && within(y, Math.min(y1!, y2), Math.max(y1!, y2)))
      }
      case 'fibonacci': {
        if (!within(x, x0!, x1!)) return null
        const [start, end] = drawing.points as [DrawingPoint, DrawingPoint]
        const distances = [distanceToSegment(x, y, x0!, y0!, x1!, y1!), ...fibonacciLevels.map(level => {
          const levelY = this.space.y(fibonacciPrice(start, end, level))
          return levelY === null ? Infinity : Math.abs(levelY - y)
        })]
        // A level line wins; anywhere between levels 0 and 1 still grabs the drawing.
        return stroke(Math.min(...distances)) ?? area(within(y, y0!, y1!))
      }
      default: return area(within(x, x0!, x1!) && within(y, y0!, y1!))
    }
  }

  render(context: CanvasRenderingContext2D, theme: DrawingTheme) {
    const all = this.preview ? [...this.drawings.filter(drawing => drawing.id !== this.preview!.id), this.preview] : this.drawings
    context.save()
    context.lineCap = 'round'
    context.font = `500 ${textFont - 1}px ${theme.font}`
    context.textBaseline = 'middle'
    for (const drawing of all) this.renderOne(context, theme, drawing, drawing.id === this.selectedId || drawing === this.preview && !this.measuring)
    if (this.guide !== null) {
      context.strokeStyle = theme.muted
      context.lineWidth = 1
      context.setLineDash([4, 4])
      line(context, this.guide, 0, this.guide, this.space.height())
      context.setLineDash([])
      const point = this.space.point(this.guide, 0, false)
      if (point) tag(context, theme, this.space.formatTime(point.time), this.guide, this.space.height() - 12, theme.muted)
    }
    context.restore()
  }

  private renderOne(context: CanvasRenderingContext2D, theme: DrawingTheme, drawing: ChartDrawing, selected: boolean) {
    const xs = drawing.points.map(point => this.space.x(point.time))
    const ys = drawing.points.map(point => this.space.y(point.price))
    if (xs.some(value => value === null) || ys.some(value => value === null)) return
    const [x0, x1 = x0] = xs as number[]
    const [y0, y1 = y0] = ys as number[]
    const width = this.space.width()
    const color = drawing.style?.color ?? theme.line
    const baseWidth = drawing.style?.width ?? 1
    const dash = drawing.style?.line === 'dashed' ? [7, 4] : drawing.style?.line === 'dotted' ? [1.5, 3.5] : []
    context.strokeStyle = color
    context.fillStyle = color
    context.lineWidth = baseWidth + (selected ? 0.75 : 0.25)
    context.setLineDash(dash)
    switch (drawing.kind) {
      case 'trend-line':
        line(context, x0!, y0!, x1!, y1!)
        break
      case 'horizontal-line':
        line(context, 0, y0!, width, y0!)
        label(context, theme, this.space.formatPrice(drawing.points[0]!.price), width - 8, y0! - 9, 'right')
        break
      case 'vertical-line':
        line(context, x0!, 0, x0!, this.space.height())
        label(context, theme, this.space.formatTime(drawing.points[0]!.time), x0! + 6, this.space.height() - 12, 'left')
        break
      case 'date-range':
      case 'price-range': {
        const [start, end] = drawing.points as [DrawingPoint, DrawingPoint]
        const left = Math.min(x0!, x1!)
        const top = Math.min(y0!, y1!)
        context.globalAlpha = 0.12
        context.fillRect(left, top, Math.abs(x1! - x0!), Math.abs(y1! - y0!))
        context.globalAlpha = 1
        let text: string
        if (drawing.kind === 'date-range') {
          const middle = (y0! + y1!) / 2
          line(context, x0!, top, x0!, top + Math.abs(y1! - y0!))
          line(context, x1!, top, x1!, top + Math.abs(y1! - y0!))
          arrow(context, x0!, middle, x1!, middle)
          const bars = this.space.bars(start.time, end.time)
          text = `${bars === null ? '' : `${Math.round(Math.abs(bars))} bars · `}${formatDuration(end.time - start.time)}`
        } else {
          const middle = (x0! + x1!) / 2
          line(context, left, y0!, left + Math.abs(x1! - x0!), y0!)
          line(context, left, y1!, left + Math.abs(x1! - x0!), y1!)
          arrow(context, middle, y0!, middle, y1!)
          const stats = priceRangeStats(start, end)
          text = `${stats.change >= 0 ? '+' : '−'}${this.space.formatPrice(Math.abs(stats.change))} (${stats.percent >= 0 ? '+' : '−'}${Math.abs(stats.percent).toFixed(2)}%)`
        }
        const below = y1! >= y0!
        let tagX = (x0! + x1!) / 2
        let tagY = below ? Math.max(y0!, y1!) + 14 : top - 14
        if (this.measuring && drawing === this.preview) {
          const half = context.measureText(text).width / 2 + 7
          tagX = Math.max(half, Math.min(tagX, width - half))
          tagY = Math.max(12, Math.min(tagY, this.space.height() - 12))
        }
        tag(context, theme, text, tagX, tagY, color)
        break
      }
      case 'zone':
        context.globalAlpha = 0.14
        context.fillRect(Math.min(x0!, x1!), Math.min(y0!, y1!), Math.abs(x1! - x0!), Math.abs(y1! - y0!))
        context.globalAlpha = 1
        context.strokeRect(Math.min(x0!, x1!), Math.min(y0!, y1!), Math.abs(x1! - x0!), Math.abs(y1! - y0!))
        break
      case 'fibonacci': {
        const [start, end] = drawing.points as [DrawingPoint, DrawingPoint]
        const left = Math.min(x0!, x1!)
        const right = Math.max(x0!, x1!)
        const levels = fibonacciLevels.map((level, index) => ({
          level, price: fibonacciPrice(start, end, level), y: this.space.y(fibonacciPrice(start, end, level)),
          // A chosen color applies to every level; otherwise each level has its own.
          color: drawing.style?.color ?? fibonacciColors[index]!,
        }))
        // Light bands between consecutive levels with the golden pocket in gold, then the levels and the swing line on top.
        levels.slice(1).forEach((band, index) => {
          const previous = levels[index]!
          if (band.y === null || previous.y === null) return
          const pocket = previous.level === goldenPocket[0] && band.level === goldenPocket[1]
          context.globalAlpha = pocket ? 0.3 : 0.08
          context.fillStyle = pocket ? gold : band.color
          context.fillRect(left, Math.min(previous.y, band.y), right - left, Math.abs(band.y - previous.y))
        })
        context.globalAlpha = 1
        context.strokeStyle = color
        context.setLineDash([4, 3])
        line(context, x0!, y0!, x1!, y1!)
        context.setLineDash(dash)
        context.lineWidth = baseWidth
        // Labels sit outside the drawing, on the left when there is room, so they never cover candles inside it.
        const outsideLeft = left > 96
        const placed = levels.filter(item => item.y !== null) as Array<typeof levels[number] & { y: number }>
        for (const { y, color: levelColor } of placed) {
          context.strokeStyle = levelColor
          line(context, left, y, right, y)
        }
        // Close levels (such as the golden pocket) get their labels stacked so they never cover each other.
        let previous = -Infinity
        for (const { level, price, y, color: levelColor } of [...placed].sort((a, b) => a.y - b.y)) {
          const labelY = Math.max(y, previous + 21)
          previous = labelY
          tag(context, theme, `${level} (${this.space.formatPrice(price)})`, outsideLeft ? left - 6 : right + 6, labelY, levelColor, outsideLeft ? 'right' : 'left')
        }
        break
      }
      case 'long-position':
      case 'short-position':
      case 'position': {
        const y2 = (ys as number[])[2]!
        const left = Math.min(x0!, x1!)
        const boxWidth = Math.abs(x1! - x0!)
        context.globalAlpha = 0.2
        context.fillStyle = theme.positive
        context.fillRect(left, Math.min(y0!, y1!), boxWidth, Math.abs(y1! - y0!))
        context.fillStyle = theme.negative
        context.fillRect(left, Math.min(y0!, y2), boxWidth, Math.abs(y2 - y0!))
        context.globalAlpha = 1
        context.strokeStyle = drawing.style?.color ?? theme.text
        line(context, left, y0!, left + boxWidth, y0!)
        const stats = positionStats(drawing.points)
        if (stats) {
          const [, target, stop] = drawing.points as [DrawingPoint, DrawingPoint, DrawingPoint]
          const middle = left + boxWidth / 2
          tag(context, theme, `Target ${this.space.formatPrice(target.price)} (${stats.targetPercent.toFixed(2)}%)`, middle, y1! + (y1! < y0! ? -14 : 14), theme.positive)
          tag(context, theme, `Stop ${this.space.formatPrice(stop.price)} (${stats.stopPercent.toFixed(2)}%)`, middle, y2 + (y2 > y0! ? 14 : -14), theme.negative)
          if (stats.ratio !== null) tag(context, theme, `R ${stats.ratio.toFixed(2)}`, middle, y0!, drawing.style?.color ?? theme.text)
        }
        break
      }
      case 'text': {
        const text = drawing.text ?? ''
        const textWidth = context.measureText(text).width
        context.fillStyle = theme.background
        context.globalAlpha = 0.9
        context.fillRect(x0! - 2, y0! - 20, textWidth + 14, 22)
        context.globalAlpha = 1
        context.strokeRect(x0! - 2, y0! - 20, textWidth + 14, 22)
        context.fillStyle = theme.text
        context.fillText(text, x0! + 5, y0! - 9)
        break
      }
    }
    context.setLineDash([])
    if (selected && !drawing.locked) for (const handle of this.handles(drawing)) {
      if (handle.x === null || handle.y === null) continue
      context.beginPath()
      context.arc(handle.x, handle.y, handleRadius, 0, Math.PI * 2)
      context.fillStyle = theme.background
      context.fill()
      context.strokeStyle = color
      context.lineWidth = 2
      context.stroke()
    }
  }
}

const within = (value: number, a: number, b: number) => value >= Math.min(a, b) - 4 && value <= Math.max(a, b) + 4

function line(context: CanvasRenderingContext2D, x0: number, y0: number, x1: number, y1: number) {
  context.beginPath()
  context.moveTo(x0, y0)
  context.lineTo(x1, y1)
  context.stroke()
}

/** Line from (x0, y0) with an arrowhead at (x1, y1). */
function arrow(context: CanvasRenderingContext2D, x0: number, y0: number, x1: number, y1: number) {
  line(context, x0, y0, x1, y1)
  const angle = Math.atan2(y1 - y0, x1 - x0)
  if (Math.hypot(x1 - x0, y1 - y0) < 8) return
  context.beginPath()
  context.moveTo(x1, y1)
  context.lineTo(x1 - 7 * Math.cos(angle - 0.45), y1 - 7 * Math.sin(angle - 0.45))
  context.moveTo(x1, y1)
  context.lineTo(x1 - 7 * Math.cos(angle + 0.45), y1 - 7 * Math.sin(angle + 0.45))
  context.stroke()
}

/** Measurement tag on a filled background, centered on `x` or with that edge at `x`. */
function tag(context: CanvasRenderingContext2D, theme: DrawingTheme, text: string, x: number, y: number, color: string,
  align: 'center' | 'left' | 'right' = 'center') {
  const width = context.measureText(text).width + 14
  const left = align === 'center' ? x - width / 2 : align === 'left' ? x : x - width
  const lineWidth = context.lineWidth
  context.setLineDash([])
  context.fillStyle = theme.background
  context.globalAlpha = 0.92
  context.fillRect(left, y - 10, width, 20)
  context.globalAlpha = 1
  context.strokeStyle = color
  context.lineWidth = 1
  context.strokeRect(left, y - 10, width, 20)
  context.fillStyle = theme.text
  context.textAlign = 'center'
  context.fillText(text, left + width / 2, y)
  context.textAlign = 'left'
  context.lineWidth = lineWidth
}

function label(context: CanvasRenderingContext2D, theme: DrawingTheme, text: string, x: number, y: number, align: 'left' | 'right', muted = false) {
  context.textAlign = align
  context.fillStyle = muted ? theme.muted : theme.text
  context.fillText(text, x, y)
  context.textAlign = 'left'
}
