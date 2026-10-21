import { describe, expect, it } from 'vitest'
import { fillMarkers, tradeFocus } from './chart-fills'

describe('trade focus (phase 1, 1.1b)', () => {
  it('frames a long fill history without spreading it into one call', () => {
    const start = Date.parse('2026-01-01T00:00:00Z')
    const markers = fillMarkers(Array.from({ length: 200_000 }, (_, index) => ({ id: `f${index}`, atUtc: new Date(start + index * 60_000).toISOString(),
      price: '100', quantity: '1', side: 'buy' as const, role: 'entry' as const })), '1m')
    expect(markers).toHaveLength(200_000)
    // 200,000 minutes: a quarter of the span either side.
    const span = 199_999 * 60_000
    expect(tradeFocus(markers, '1m')).toEqual({ from: start - span / 4, to: start + span + span / 4 })
  })
})
