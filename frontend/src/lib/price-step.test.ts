import { describe, expect, it } from 'vitest'
import { roundedLevelPrice } from '@/components/chart/frameEdit'
import { exactDecimal } from '@/lib/decimal'
import { formatDraggedPrice, pricesMatch } from '@/features/plays/levels'
import { roundPrice } from '@/features/plays/suggestions'
import { significantDecimals } from './price-step'

// Phase 2, item 14b (F-11, probe C9): Hyperliquid takes five significant figures, any integer, and 6 − szDecimals decimals.
const szDecimals1 = { maxDecimals: 5 } as const

describe('Venue price rule', () => {
  it('caps significant-figure decimals at the venue limit and always allows integers', () => {
    expect(significantDecimals(0.051234, null)).toBe(6)
    expect(significantDecimals(0.051234, szDecimals1)).toBe(5)
    expect(significantDecimals(123456.7, szDecimals1)).toBe(0)
    expect(formatDraggedPrice(0.0512345, szDecimals1)).toBe('0.05123')
    expect(formatDraggedPrice(0.0512345)).toBe('0.051235')
    expect(formatDraggedPrice(123456.7, szDecimals1)).toBe('123457')
    expect(roundedLevelPrice(0.0512345, szDecimals1)).toBe(0.05123)
    expect(roundPrice(0.0512341, 'up', szDecimals1)).toBe('0.05124')
    expect(roundPrice(0.0512349, 'down', szDecimals1)).toBe('0.05123')
    // A tick still wins over significant figures.
    expect(formatDraggedPrice(0.0912345, 0.00001)).toBe('0.09123')
  })

  it('matches an order at the only price the venue accepts', () => {
    const order = exactDecimal('0.05123')!
    const planned = exactDecimal('0.051234')!
    expect(pricesMatch(order, planned)).toBe(false)
    expect(pricesMatch(order, planned, szDecimals1)).toBe(true)
    expect(pricesMatch(exactDecimal('0.05125')!, planned, szDecimals1)).toBe(false)
    // Nothing becomes stricter at coarse prices.
    expect(pricesMatch(exactDecimal('84542')!, exactDecimal('84541')!, { maxDecimals: 1 })).toBe(true)
  })
})

describe('priceStepOf', () => {
  it('prefers the tick and keeps one object per cap', async () => {
    const { priceStepOf } = await import('./price-step')
    expect(priceStepOf(0.5, 3)).toBe(0.5)
    expect(priceStepOf(null, null)).toBeNull()
    expect(priceStepOf(undefined, 5)).toBe(priceStepOf(null, 5))
    expect(priceStepOf(undefined, 5)).toEqual({ maxDecimals: 5 })
  })
})
