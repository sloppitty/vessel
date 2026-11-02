import { afterEach, describe, expect, it } from 'vitest'
import { exactDecimal, exactFixed } from '@/lib/decimal'
import { preferenceKey } from '@/lib/preference-scope'
import { assumedCosts, costPercentText, plannedNotional, readCostAssumptions } from './cost-assumptions'
import { createDraft } from './draft'

afterEach(() => localStorage.clear())

describe('Cost assumptions (Phase 2, item 10a)', () => {
  it('accepts 0 to 10 percent with up to four decimals, and blank as not set', () => {
    expect(costPercentText('')).toBeNull()
    expect(costPercentText(' 0.09 ')).toBe('0.09')
    expect(costPercentText('0')).toBe('0')
    expect(costPercentText('10')).toBe('10')
    for (const bad of ['10.0001', '-0.1', '0.12345', '1e-2', '.5', 'abc']) expect(costPercentText(bad)).toBeUndefined()
  })

  it('reads stored assumptions and drops malformed ones rather than reading them as zero', () => {
    localStorage.setItem(preferenceKey('costs.assumptions.v1'), JSON.stringify({
      hyperliquid: { feePercent: '0.09', slippagePercent: '0.05' },
      lighter: { feePercent: null, slippagePercent: '0.1' },
      risex: { feePercent: 'zero', slippagePercent: '0.1' },
      manual: { feePercent: null, slippagePercent: null },
    }))
    expect(readCostAssumptions()).toEqual({
      hyperliquid: { feePercent: '0.09', slippagePercent: '0.05' },
      lighter: { feePercent: null, slippagePercent: '0.1' },
    })
    localStorage.setItem(preferenceKey('costs.assumptions.v1'), '[1]')
    expect(readCostAssumptions()).toEqual({})
  })

  it('measures the planned position size exactly in both sizing modes', () => {
    const margin = { ...createDraft(), size: '1000', leverage: '5' }
    expect(exactFixed(plannedNotional(margin)!, 8)).toBe('5000')
    const quantity = { ...createDraft(), sizingMode: 'quantity' as const, size: '0.3', leverage: '5' }
    quantity.entries[0]!.price = '2000.1'
    expect(exactFixed(plannedNotional(quantity)!, 8)).toBe('600.03')
    quantity.entries[0]!.price = ''
    expect(plannedNotional(quantity)).toBeNull()
    expect(plannedNotional({ ...margin, size: '' })).toBeNull()
  })

  it('states the cost in R only against a known loss at the stops, and an unset part adds nothing', () => {
    const draft = { ...createDraft(), size: '1000', leverage: '5' }
    const loss = exactDecimal('-250')
    const both = assumedCosts(draft, { feePercent: '0.09', slippagePercent: '0.05' }, loss)!
    expect(exactFixed(both.cost!, 8)).toBe('7')
    expect(exactFixed(both.r!, 8)).toBe('0.028')
    const feeOnly = assumedCosts(draft, { feePercent: '0.09', slippagePercent: null }, loss)!
    expect(exactFixed(feeOnly.cost!, 8)).toBe('4.5')
    // No loss at the stops (locked in or unknown): money only.
    expect(assumedCosts(draft, { feePercent: '0.09', slippagePercent: null }, exactDecimal('10'))!.r).toBeNull()
    expect(assumedCosts(draft, { feePercent: '0.09', slippagePercent: null }, null)!.r).toBeNull()
    expect(assumedCosts(draft, null, loss)).toBeNull()
    expect(assumedCosts(draft, { feePercent: null, slippagePercent: null }, loss)).toBeNull()
  })
})
