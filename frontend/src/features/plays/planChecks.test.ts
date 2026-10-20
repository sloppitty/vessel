import { describe, expect, it } from 'vitest'
import { createDraft, createEntry, type DraftEntry } from './draft'
import { keptStopWarnings, planFixes, planIssues, stopsPastLiquidation } from './planChecks'
import { storedPlanText } from './stored-plan-text'
import { planFromDraft } from './saved'
import { planTextState } from './numbers'

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

// Phase 0, 0.5b (decision 2): the review probe verify-sol-frontend/f1f2.mjs F1, an Open long at 100 with a revised stop.
describe('stops of a filled entry', () => {
  const filled = new Set(['e1'])
  const open = (unit: 'price' | 'percent', stop: string) => ({ ...entry('100', stop, '120', 'price'), id: 'e1',
    stops: [{ id: 's1', unit, value: stop, share: '100' }] })

  it('may sit at or beyond the entry once it has filled; an unfilled entry keeps the wrong-side rule', () => {
    for (const [unit, value] of [['price', '95'], ['price', '100'], ['price', '105'], ['percent', '0'], ['percent', '5']] as const) {
      expect(planIssues([open(unit, value)], 'long', filled)).toEqual([])
    }
    // Before the fix, the browser refused these with "a long stop goes below the entry price".
    expect(planIssues([open('price', '100')], 'long').map(issue => issue.message))
      .toEqual(['Entry 1 stop is at the entry price; a long stop goes below it.'])
    expect(planIssues([open('price', '105')], 'long', new Set(['other'])).map(issue => issue.kind)).toEqual(['stop'])
    // Shorts mirror it; targets keep their side whether or not the entry filled.
    expect(planIssues([{ ...entry('100', '95', '90'), id: 'e1' }], 'short', filled)).toEqual([])
    expect(planIssues([{ ...entry('100', '105', '95'), id: 'e1' }], 'long', filled).map(issue => issue.message))
      .toEqual(['Entry 1 target 1 is below the entry price; a long target goes above it.'])
    expect(planFixes({ direction: 'long', entries: [open('price', '105')] }, filled)).toEqual({ swapped: null, reversed: null })
  })

  it('keeps a stop saved beyond an entry that no longer has a fill as a warning, not an error', () => {
    // The stop at 105 was saved while the entry had a fill; the fill was unlinked since.
    const saved = open('price', '105')
    const stored = storedPlanText(planFromDraft({ ...createDraft(), entries: [saved] }))
    expect(planIssues([saved], 'long', new Set(), stored)).toEqual([])
    expect(keptStopWarnings([saved], 'long', new Set(), stored).map(issue => issue.message))
      .toEqual(['Entry 1 stop is above the entry price, but its entry has no fill. It was saved there and still saves unchanged.'])
    // Changed text, unit or entry price is checked again; with a fill there is nothing to warn about.
    for (const changed of [open('price', '106'), { ...saved, price: '101' }, open('percent', '105')])
      expect(planIssues([changed], 'long', new Set(), stored).map(issue => issue.kind)).toEqual(changed.stops[0]!.unit === 'percent' ? [] : ['stop'])
    expect(keptStopWarnings([saved], 'long', filled, stored)).toEqual([])
    expect(planIssues([saved], 'long', new Set())).toHaveLength(1)
  })

  it('checks every stop again when the direction changes (Sol review)', () => {
    // A long saved with entry 100, stop 95 and no target, switched to short with the same numbers: the stop is below a short entry.
    const saved = { ...open('price', '95'), targets: [] }
    const stored = storedPlanText(planFromDraft({ ...createDraft(), direction: 'long', entries: [saved] }))
    expect(planIssues([saved], 'short', new Set(), stored).map(issue => issue.message))
      .toEqual(['Entry 1 stop is below the entry price; a short stop goes above it.'])
    expect(keptStopWarnings([saved], 'short', new Set(), stored)).toEqual([])
    expect(planFixes({ direction: 'long', entries: [saved] }, new Set(), stored)).toEqual({ swapped: null, reversed: null })
  })

  it('refuses a negative percentage with a pointer to a price stop (-5% once saved as no stop)', () => {
    expect(planTextState('-5', 'Entry 1 stop 1', null, 'stop')).toEqual({ status: 'invalid',
      reason: 'Entry 1 stop 1: use a price for a stop beyond the entry; a percentage cannot be negative.' })
    expect(planTextState('0', 'Entry 1 stop 1', null, 'stop')).toEqual({ status: 'valid' })
  })
})

// The owner's decision of October 20, 2026: a 0 % stop is break-even, which needs a fill on the entry.
describe('a 0 % stop', () => {
  const zero = (price: string, stop: string, target = '10', unit: 'price' | 'percent' = 'percent') =>
    ({ ...entry(price, stop, target, 'percent'), id: 'e1', stops: [{ id: 's1', unit, value: stop, share: '100' }] })

  it.each([['long', 'below'], ['short', 'above']] as const)('is refused before a fill like a %s price stop at the entry', (direction, side) => {
    for (const [price, value] of [['100', '0'], ['100', '0.00'], ['100', '00'], ['', '0']] as const) {
      expect(planIssues([zero(price, value)], direction).map(issue => issue.message), `${price} ${value}`)
        .toEqual([`Entry 1 stop is at the entry price; a ${direction} stop goes ${side} it. 0 % (break-even) needs a fill on the entry.`])
    }
    // The same as a price stop at the entry: no swap or direction switch is offered.
    expect(planFixes({ direction, entries: [zero('100', '0')] })).toEqual({ swapped: null, reversed: null })
    expect(planFixes({ direction, entries: [{ ...entry('100', '100', direction === 'long' ? '110' : '90'), id: 'e1' }] }))
      .toEqual({ swapped: null, reversed: null })
    // Any fill on the entry allows it; a fill on another entry does not.
    expect(planIssues([zero('100', '0')], direction, new Set(['e1']))).toEqual([])
    expect(planIssues([zero('100', '0')], direction, new Set(['e2']))).toHaveLength(1)
  })

  it('keeps a stored 0 % stop saveable as a warning, and leaves blank stops and 0 % targets alone', () => {
    const saved = zero('100', '0')
    const stored = storedPlanText(planFromDraft({ ...createDraft(), direction: 'long', entries: [saved] }))
    expect(planIssues([saved], 'long', new Set(), stored)).toEqual([])
    expect(keptStopWarnings([saved], 'long', new Set(), stored).map(issue => issue.message))
      .toEqual(['Entry 1 stop is at the entry price, but its entry has no fill. It was saved there and still saves unchanged.'])
    // In the other direction it is checked again.
    expect(planIssues([saved], 'short', new Set(), stored)).toHaveLength(1)
    // A stop that is not set is no stop, not break-even; 0 % targets are outside this rule.
    expect(planIssues([zero('100', '')], 'long')).toEqual([])
    expect(planIssues([zero('100', '5', '0')], 'long')).toEqual([])
    expect(planIssues([zero('100', '5'), zero('100', '0', '10', 'price')].map((item, index) => ({ ...item, id: `e${index}` })), 'long'))
      .toEqual([])
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
