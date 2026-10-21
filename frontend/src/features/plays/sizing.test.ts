import { zoneLabel } from '@/lib/time'
import { describe, expect, it } from 'vitest'
import { createEntry, type DraftEntry, type DraftExit } from './draft'
import type { BrokerAccount } from '@/api/workspace'
import { accountFixture } from '@/test/workspace-fixture'
import { availableLabel, availableOf, convertSize, estimatedLiquidation, exitEstimate, marginOverBudget, overBudgetMessage, playBudget, positionSize, rewardToRisk,
  sizeForBudget } from './sizing'
import { isPlanText } from './numbers'

const entry = (price: string, share = '100'): DraftEntry => ({ ...createEntry(0), price, share })

describe('exit estimates', () => {
  const planned = entry('3.75')
  const exit: DraftExit = { ...planned.stops[0]!, unit: 'percent', value: '10', share: '100' }
  const sized = positionSize({ size: '130', sizingMode: 'margin', entries: [planned] }, 3)

  it.each([
    ['long', 'stop', '10', 3.625, 13],
    ['long', 'target', '50', 4.375, 65],
    ['short', 'stop', '10', 3.875, 13],
    ['short', 'target', '50', 3.125, 65],
  ] as const)('separates a %s %s price move from its leveraged return without applying leverage twice', (direction, kind, value, price, amount) => {
    const estimate = exitEstimate(planned, { ...exit, value }, kind, direction, 3, sized.quantity)!
    expect(estimate.price).toBeCloseTo(price)
    expect(estimate.move).toBeCloseTo(Number(value) / 3)
    expect(estimate.returnPercent).toBeCloseTo(Number(value))
    expect(estimate.amount).toBeCloseTo(amount)
  })

  it('resolves price levels and respects both entry and exit quantity shares without normalizing them', () => {
    const estimate = exitEstimate({ ...planned, share: '40' }, { ...exit, unit: 'price', value: '3.375', share: '50' }, 'stop', 'long', 3, sized.quantity)!
    expect(estimate.move).toBeCloseTo(10)
    expect(estimate.returnPercent).toBeCloseTo(30)
    expect(estimate.amount).toBeCloseTo(7.8)
    expect(exitEstimate(planned, { ...exit, share: '0' }, 'stop', 'long', 3, sized.quantity)?.amount).toBe(0)
  })

  it('uses quantity sizing and 1× without adding another leverage multiplier', () => {
    const quantity = positionSize({ size: '104', sizingMode: 'quantity', entries: [planned] }, 3).quantity
    expect(exitEstimate(planned, exit, 'stop', 'long', 3, quantity)?.amount).toBeCloseTo(13)
    const unlevered = positionSize({ size: '130', sizingMode: 'margin', entries: [planned] }, 1).quantity
    expect(exitEstimate(planned, exit, 'stop', 'long', 1, unlevered)).toMatchObject({ price: 3.375, move: 10, returnPercent: 10 })
    expect(exitEstimate(planned, exit, 'stop', 'long', 1, unlevered)?.amount).toBeCloseTo(13)
  })

  it('does not invent amounts from missing or invalid size and shares, or outcomes for invalid prices', () => {
    expect(exitEstimate(planned, exit, 'stop', 'long', 3, null)?.amount).toBeNull()
    expect(exitEstimate(planned, exit, 'stop', 'long', 3, Infinity)?.amount).toBeNull()
    expect(exitEstimate(entry('10000000000'), exit, 'stop', 'long', 3, 1e308)?.amount).toBeNull()
    for (const share of ['', '-1', '101', 'invalid']) {
      expect(exitEstimate({ ...planned, share }, exit, 'stop', 'long', 3, 104)?.amount).toBeNull()
      expect(exitEstimate(planned, { ...exit, share }, 'stop', 'long', 3, 104)?.amount).toBeNull()
    }
    expect(exitEstimate(entry(''), exit, 'stop', 'long', 3, 104)).toBeNull()
    expect(exitEstimate(planned, { ...exit, unit: 'price', value: '4' }, 'stop', 'long', 3, 104)).toBeNull()
    expect(exitEstimate(planned, { ...exit, value: '400' }, 'stop', 'long', 3, 104)).toBeNull()
  })
})

describe('estimated liquidation', () => {
  it('is where the isolated margin falls to the maintenance margin', () => {
    // 10× on a 20× contract: 10% margin, 2.5% maintenance.
    expect(estimatedLiquidation([entry('121.54')], 'long', 10, 20)).toBeCloseTo(121.54 * 0.9 / 0.975, 6)
    expect(estimatedLiquidation([entry('121.54')], 'short', 10, 20)).toBeCloseTo(121.54 * 1.1 / 1.025, 6)
  })

  it('uses the maintenance margin the venue states for the contract over the assumed one', () => {
    expect(estimatedLiquidation([entry('100')], 'long', 10, 20, 0.05)).toBeCloseTo(100 * 0.9 / 0.95, 6)
    expect(estimatedLiquidation([entry('100')], 'short', 10, null, 0.05)).toBeCloseTo(100 * 1.1 / 1.05, 6)
    expect(estimatedLiquidation([entry('100')], 'long', 10, 20, 1)).toBeCloseTo(100 * 0.9 / 0.975, 6)
  })

  it('uses the share-weighted average entry of the whole plan', () => {
    const entries = [entry('100', '50'), entry('90', '50')]
    expect(estimatedLiquidation(entries, 'long', 5, 50)).toBeCloseTo(95 * 0.8 / 0.99, 6)
  })

  it('is unknown without a price or venue maximum, and absent for a 1× long', () => {
    expect(estimatedLiquidation([entry('')], 'long', 10, 20)).toBeNull()
    expect(estimatedLiquidation([entry('100')], 'long', 10, null)).toBeNull()
    expect(estimatedLiquidation([entry('100')], 'long', 1, 20)).toBeNull()
    expect(estimatedLiquidation([entry('100')], 'short', 1, 20)).toBeCloseTo(200 / 1.025, 6)
  })
})

describe('margin budget', () => {
  const draft = { size: '263', sizingMode: 'margin' as const, entries: [entry('121.54')], budgetOverride: null }
  const wallet = (value: string) => ({ value, source: 'wallet' as const })

  it('flags margin above the available amount or a manual budget', () => {
    expect(marginOverBudget(draft, 10, wallet('151.77'))).toMatchObject({ margin: 263, budget: { amount: 151.77, source: 'wallet' } })
    expect(marginOverBudget({ ...draft, budgetOverride: '300' }, 10, wallet('151.77'))).toBeNull()
    expect(marginOverBudget({ ...draft, budgetOverride: '200' }, 10, wallet('500'))).toMatchObject({ budget: { source: 'manual' } })
    expect(marginOverBudget(draft, 10, null)).toBeNull()
  })

  it('checks the margin a quantity needs at the leverage', () => {
    const quantity = { ...draft, sizingMode: 'quantity' as const, size: '20' }
    expect(marginOverBudget(quantity, 10, wallet('200'))?.margin).toBeCloseTo(243.08, 2)
    expect(marginOverBudget(quantity, 20, wallet('200'))).toBeNull()
    expect(sizeForBudget(quantity, 10, '200', { quote: 'USDC', base: 'SOL', quantityDecimals: 2 })).toBe('16.45')
  })

  // Phase 0, 0.1 (D-07, decision 6): review probes `correctness-frontend/budget.mjs` and `verify-sol-frontend-resolver/f4.mjs`.
  it('budgets from the normalized available balance with its source, and keeps a known zero', () => {
    // Lighter and RISEx report available margin and no wallet: they gain the budget check.
    expect(playBudget(draft, { value: '100', source: 'margin' })).toEqual({ value: '100', amount: 100, source: 'margin' })
    expect(marginOverBudget(draft, 10, { value: '100', source: 'margin' })).toMatchObject({ margin: 263, budget: { amount: 100, source: 'margin' } })
    // A known zero is a budget of zero, not an unknown one.
    expect(playBudget(draft, { value: '0', source: 'withdrawable' })).toEqual({ value: '0', amount: 0, source: 'withdrawable' })
    expect(marginOverBudget(draft, 10, wallet('0'))).toMatchObject({ budget: { amount: 0, source: 'wallet' } })
    // Only null is unknown; a manual amount still wins.
    expect(playBudget(draft, null)).toBeNull()
    expect(playBudget({ budgetOverride: '50' }, wallet('0'))).toEqual({ value: '50', amount: 50, source: 'manual' })
  })

  it('reads the availability the server selected, never the stablecoin wallet field of a standard account', () => {
    // A standard Hyperliquid account with $3.12 of spot dust and $4,906.67 withdrawable from perps.
    const standard: BrokerAccount = { ...accountFixture, venueId: 'hyperliquid', availableStablecoinNominalUsd: '3.12',
      availableBalanceUsd: '4906.67', availableBalanceSource: 'withdrawable' }
    expect(availableOf(standard)).toEqual({ value: '4906.67', source: 'withdrawable', asOfUtc: null, refreshFailed: false })
    expect(availableOf({ ...standard, availableBalanceUsd: '0', availableBalanceSource: 'wallet' })).toMatchObject({ value: '0', source: 'wallet' })
    expect(availableOf({ ...standard, availableBalanceUsd: null, availableBalanceSource: null })).toBeNull()
    expect(availableOf({ ...standard, isEnabled: false })).toBeNull()
    expect(availableOf(undefined)).toBeNull()
  })

  // Review of 0.1: no age limit is invented; the figure says when it was read and whether the last refresh failed.
  it('labels the available balance with its source, snapshot time and a failed refresh', () => {
    const at = new Date()
    at.setHours(14, 2, 0, 0)
    const account: BrokerAccount = { ...accountFixture, venueId: 'lighter-rh', availableBalanceUsd: '25', availableBalanceSource: 'margin',
      lastSyncedAtUtc: at.toISOString(), syncStatus: 'error', lastSyncError: 'Lighter is unavailable.' }
    expect(availableOf(account)).toEqual({ value: '25', source: 'margin', asOfUtc: at.toISOString(), refreshFailed: true })
    expect(availableLabel(availableOf(account)!, 'local')).toBe(`reported margin · as of 14:02 ${zoneLabel(at, 'local')} · last refresh failed`)
    expect(availableLabel({ value: '25', source: 'withdrawable' }, 'local')).toBe('reported withdrawal')
    const earlier = new Date(at)
    earlier.setDate(earlier.getDate() - 1)
    expect(availableLabel({ value: '25', source: 'wallet', asOfUtc: earlier.toISOString() }, 'local'))
      .toBe(`wallet · as of ${earlier.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })} 14:02 ${zoneLabel(earlier, 'local')}`)
  })

  it('names the budget and its source in the quote unit of the instrument', () => {
    const over = marginOverBudget(draft, 10, { value: '100', source: 'withdrawable' })!
    expect(overBudgetMessage(over, { quote: 'USDC', base: 'SOL', quantityDecimals: 2 })).toBe('Margin 263 USDC is above the 100 USDC budget (reported withdrawal).')
    expect(overBudgetMessage(marginOverBudget({ ...draft, budgetOverride: '200' }, 10, null)!, { quote: 'USDG', base: 'SOL', quantityDecimals: 2 }))
      .toBe('Margin 263 USDG is above the 200 USDG budget (manual).')
  })

  it('reads a manual budget with the plan number rule; text the server would not store is no budget', () => {
    expect(playBudget({ budgetOverride: '250' }, wallet('500'))).toEqual({ value: '250', amount: 250, source: 'manual' })
    expect(playBudget({ budgetOverride: '1e2' }, wallet('500'))).toBeNull()
  })
})

describe('size conversion text', () => {
  const units = { quote: 'USDC', base: 'BTC', quantityDecimals: 8 }
  it('never writes an exponent into the size field', () => {
    // 0.0001 margin at 1,000 buys 1e-7; String(Number('0.00000010')) would print "1e-7".
    const tiny = convertSize({ size: '0.0001', sizingMode: 'margin', entries: [entry('1000')] }, 1, units)
    expect(tiny).toBe('0.0000001')
    const budget = sizeForBudget({ sizingMode: 'quantity', entries: [entry('1')] }, 1, '0.0000005', units)
    expect(budget).toBe('0.0000005')
    const huge = convertSize({ size: '1000000000000000000000000', sizingMode: 'quantity', entries: [entry('1000')] }, 1, units)
    expect(huge).toBe('1000000000000000000000000000')
    for (const text of [tiny, budget ?? '', huge]) expect(isPlanText(text), text).toBe(true)
  })
})

describe('reward to risk', () => {
  it('compares percent levels by the price move they stand for', () => {
    const base = createEntry(0)
    const planned: DraftEntry = { ...base, price: '100',
      stops: [{ ...base.stops[0]!, unit: 'percent', value: '10' }], targets: [{ ...base.targets[0]!, unit: 'percent', value: '25' }] }
    expect(rewardToRisk(planned, 'short', 5)).toBeCloseTo(2.5, 6)
  })
})
