import { describe, expect, it } from 'vitest'
import { createEntry, type DraftEntry } from './draft'
import { planFixes, planIssues, stopsPastLiquidation } from './planChecks'

const entry = (price: string, stop: string, target: string, unit: 'price' | 'percent' = 'price'): DraftEntry => {
  const base = createEntry(0)
  return { ...base, price, stops: [{ ...base.stops[0]!, unit, value: stop }], targets: [{ ...base.targets[0]!, unit, value: target }] }
}

describe('plan checks', () => {
  it('flags price levels on the wrong side or at the entry, never percent levels or unpriced entries', () => {
    expect(planIssues([entry('100', '95', '110')], 'long')).toEqual([])
    expect(planIssues([entry('100', '100', '110')], 'long').map(issue => issue.message))
      .toEqual(['Entry 1 stop is at the entry price; a long stop goes below it.'])
    expect(planIssues([entry('100', '95', '110')], 'short').map(issue => issue.kind)).toEqual(['stop', 'target'])
    expect(planIssues([entry('100', '5', '10', 'percent')], 'short')).toEqual([])
    expect(planIssues([entry('', '105', '90')], 'long')).toEqual([])
  })

  it('reads stored entry and level text as the server\'s side check does', () => {
    // A stored 1e2 entry is 100 to the server, which refuses a long stop at 105; the browser says so before saving.
    expect(planIssues([entry('1e2', '105', '110')], 'long').map(issue => issue.message))
      .toEqual(['Entry 1 stop is above the entry price; a long stop goes below it.'])
    expect(planIssues([entry('+100', '95', '100.')], 'long').map(issue => issue.message))
      .toEqual(['Entry 1 target 1 is at the entry price; a long target goes above it.'])
    expect(planIssues([entry('1e2', '95', '110')], 'long')).toEqual([])
    // Text the server cannot read at all stays unpriced.
    expect(planIssues([entry('1,5', '105', '90')], 'long')).toEqual([])
  })

  it('offers only the corrections that clear every issue', () => {
    const reversed = entry('100', '105', '90')
    const fixes = planFixes({ direction: 'long', entries: [reversed] })
    expect(fixes.reversed).toBe('short')
    expect(fixes.swapped?.[0]).toMatchObject({ stops: reversed.targets, targets: reversed.stops })
    // A stop above the entry with a target also above it: neither swapping nor switching fixes both.
    expect(planFixes({ direction: 'long', entries: [entry('100', '105', '110')] })).toEqual({ swapped: null, reversed: null })
    expect(planFixes({ direction: 'long', entries: [entry('100', '95', '110')] })).toEqual({ swapped: null, reversed: null })
  })
})

describe('stops past liquidation', () => {
  it('warns about stops that would not trigger before the estimated liquidation', () => {
    expect(stopsPastLiquidation([entry('100', '85', '120')], 'long', 10, 90)).toEqual([expect.objectContaining({ message: 'Entry 1 stop at 85 is past the estimated liquidation price of 90.' })])
    expect(stopsPastLiquidation([entry('100', '95', '120')], 'long', 10, 90)).toEqual([])
    expect(stopsPastLiquidation([entry('100', '112', '80')], 'short', 10, 110)).toHaveLength(1)
    expect(stopsPastLiquidation([entry('100', '85', '120')], 'long', 10, null)).toEqual([])
  })
})
