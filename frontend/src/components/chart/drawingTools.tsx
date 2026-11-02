import { Eraser, Magnet, Redo2, Undo2 } from 'lucide-react'
import type { ReactNode } from 'react'
import type { ChartTool } from './ChartToolRail'
import type { DrawingKind } from './drawings'
import { toolIcons } from './toolIcons'

const icon = { size: 16, strokeWidth: 1.6, 'aria-hidden': true } as const

export const drawingToolLabels: Record<DrawingKind, string> = {
  'trend-line': 'Trend line', 'horizontal-line': 'Horizontal line', 'vertical-line': 'Vertical line', zone: 'Rectangle zone',
  'date-range': 'Date range', 'price-range': 'Price range',
  fibonacci: 'Fibonacci retracement', 'long-position': 'Long position', 'short-position': 'Short position',
  position: 'Long/short position', text: 'Text note',
}

/** How to draw each kind, shown in the tool tooltip and while the tool is active. */
export const drawingToolHints: Record<DrawingKind, string> = {
  'trend-line': 'Drag, or click twice, to draw a trend line.',
  'horizontal-line': 'Click to place a horizontal line.',
  'vertical-line': 'Click to mark a moment in time.',
  zone: 'Drag, or click twice, to mark a zone.',
  'date-range': 'Drag across time to measure bars and duration.',
  'price-range': 'Drag up or down to measure a price change.',
  fibonacci: 'Drag from the swing start to the swing end.',
  'long-position': 'Click to place a long box, or drag to size it. Drag its corners to set target, stop and width.',
  'short-position': 'Click to place a short box, or drag to size it. Drag its corners to set target, stop and width.',
  position: 'Drag its corners to set target, stop and width.',
  text: 'Click to place a note, then edit its text.',
}

const tool = (id: DrawingKind, icon: ReactNode): ChartTool => ({ id, label: drawingToolLabels[id], description: drawingToolHints[id], icon, available: true })

export const crosshairTool: ChartTool = {
  id: 'crosshair', label: 'Crosshair',
  description: 'Select, move and resize drawings and levels. Hold Shift and drag sideways to measure time, or vertically to measure price and %. Release to keep the drawing.',
  icon: toolIcons.crosshair, available: true,
}

/** Every drawing tool by kind. */
export const drawingToolsByKind: Record<DrawingKind, ChartTool> = {
  'trend-line': tool('trend-line', toolIcons.trendLine),
  'horizontal-line': tool('horizontal-line', toolIcons.horizontalLine),
  'vertical-line': tool('vertical-line', toolIcons.verticalLine),
  zone: tool('zone', toolIcons.rectangle),
  'date-range': tool('date-range', toolIcons.dateRange),
  'price-range': tool('price-range', toolIcons.priceRange),
  fibonacci: tool('fibonacci', toolIcons.fibonacci),
  'long-position': tool('long-position', toolIcons.longPosition),
  'short-position': tool('short-position', toolIcons.shortPosition),
  position: tool('position', toolIcons.longPosition),
  text: tool('text', toolIcons.text),
}

/** Rail groups, each a button showing its last-used tool and a panel listing the rest. */
export const drawingToolGroups: readonly { id: string; label: string; kinds: readonly DrawingKind[] }[] = [
  { id: 'lines', label: 'Lines', kinds: ['trend-line', 'horizontal-line', 'vertical-line'] },
  { id: 'fibonacci', label: 'Fibonacci', kinds: ['fibonacci'] },
  { id: 'shapes', label: 'Shapes', kinds: ['zone'] },
  { id: 'positions', label: 'Positions', kinds: ['long-position', 'short-position'] },
  { id: 'measure', label: 'Measure', kinds: ['price-range', 'date-range'] },
  { id: 'text', label: 'Notes', kinds: ['text'] },
]

export const drawingUtilityIcons = {
  magnet: <Magnet {...icon} />,
  undo: <Undo2 {...icon} />,
  redo: <Redo2 {...icon} />,
  clear: <Eraser {...icon} />,
}

export const isDrawingKind = (id: string): id is DrawingKind => id in drawingToolLabels
