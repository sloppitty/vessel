import { describe, expect, it } from 'vitest'
import type { SizingDocument } from '@/api/sizing'
import { sizingFixture } from '@/test/workspace-fixture'
import { createEntry, createExit, type DraftEntry, type DraftExit, type PlayDraft } from './draft'
import { rewardToRisk, type SizeUnits } from './sizing'
import { positionRiskSummary } from './position-risk-summary'
import { lossPerUnit, planSuggestions, riskSize, roundPrice, suggestLeverage, suggestMaxStop, suggestMinTarget, suggestSize, suggestionCount } from './suggestions'

type Level = [value: string, share?: string, unit?: DraftExit['unit']]
const level = ([value, share = '100', unit = 'price']: Level): DraftExit => ({ ...createExit(share), value, unit })
const entry = (price: string, share: string, stops: Level[], targets: Level[] = []): DraftEntry =>
  ({ ...createEntry(0), price, share, stops: stops.map(level), targets: targets.map(level) })
const draft = (entries: DraftEntry[], changes: Partial<PlayDraft> = {}) =>
  ({ entries, direction: 'long' as const, size: '', sizingMode: 'margin' as const, leverage: '1', ...changes })
const units = (quantityDecimals: number | null): SizeUnits => ({ quote: 'USDC', base: 'BTC', quantityDecimals })
const sizing = (changes: { multiplier?: string; effective?: string } = {}): SizingDocument => ({
  ...sizingFixture,
  exposure: { ...sizingFixture.exposure, ...(changes.multiplier ? { level: changes.multiplier === '0.5' ? 'half' : 'quarter', multiplier: changes.multiplier } : {}) },
  limits: { ...sizingFixture.limits, ...(changes.effective ? { effectiveRiskPercent: changes.effective } : {}) },
})

// 60% at 100 with stops at 95 and 90 (7.5 average), 40% at 90 with a stop at 85 (5): 6.5 per unit, average entry 96.
const twoEntries = () => [entry('100', '60', [['95', '50'], ['90', '50']]), entry('90', '40', [['85']])]

describe('loss per unit', () => {
  it('weights stop distances by stop share and entries by quantity share', () => {
    expect(lossPerUnit(twoEntries(), 'long', 1)).toBeCloseTo(6.5, 9)
  })

  it('resolves percent stops at the leverage, on the direction side', () => {
    // A 20% loss at 10× is a 2% move: 204 for a short at 200.
    expect(lossPerUnit([entry('200', '100', [['20', '100', 'percent']])], 'short', 10)).toBeCloseTo(4, 9)
  })

  it('is unknown while an entry has no stop or a stop sits on the wrong side', () => {
    expect(lossPerUnit([entry('100', '50', [['95']]), entry('90', '50', [['']])], 'long', 1)).toBeNull()
    expect(lossPerUnit([entry('100', '100', [['105']])], 'long', 1)).toBeNull()
    expect(lossPerUnit([entry('', '100', [['95']])], 'long', 1)).toBeNull()
  })
})

describe('size suggestion', () => {
  it('sizes margin from balance × risk ÷ loss per unit at the leverage', () => {
    // 13,000 × 1.25% = 162.5 at risk; ÷ 6.5 = 25 units; × 96 = 2,400 notional; at 2× a 1,200 margin.
    const sized = suggestSize(draft(twoEntries()), 2, 13_000, sizing(), units(5), null)
    expect(sized).toMatchObject({ value: '1200', margin: 1200, overBudget: null, effectiveRiskPercent: 1.25 })
    expect(sized!.risk).toBeCloseTo(162.5, 9)
    expect(sized!.quantity).toBeCloseTo(25, 9)
    expect(sized!.notional).toBeCloseTo(2400, 9)
  })

  it('sizes quantity rounded down to the venue precision', () => {
    // 125 at risk over a 3 stop distance: 41.67 units.
    const plan = draft([entry('100', '100', [['97']])], { sizingMode: 'quantity' })
    expect(suggestSize(plan, 3, 10_000, sizing(), units(0), null)?.value).toBe('41')
    expect(suggestSize(plan, 3, 10_000, sizing(), units(2), null)?.value).toBe('41.66')
    expect(suggestSize(plan, 3, 10_000, sizing(), units(null), null)?.value).toBe('41.666666')
    expect(suggestSize(draft(plan.entries), 3, 10_000, sizing(), units(2), null)?.value).toBe('1388.88')
  })

  it('uses the effective risk while exposure is reduced, with percent stops on a short', () => {
    const plan = draft([entry('200', '100', [['20', '100', 'percent']])], { direction: 'short', sizingMode: 'quantity' })
    // 5,000 × 0.625% = 31.25 at risk over a 4 distance: 7.8125 units.
    expect(suggestSize(plan, 10, 5_000, sizing({ multiplier: '0.5', effective: '0.625' }), units(3), null)).toMatchObject({ value: '7.812', effectiveRiskPercent: 0.625 })
  })

  it('needs a balance, an entry price and stops', () => {
    expect(suggestSize(draft(twoEntries()), 2, null, sizing(), units(5), null)).toBeNull()
    expect(suggestSize(draft(twoEntries()), 2, 0, sizing(), units(5), null)).toBeNull()
    expect(suggestSize(draft([entry('', '100', [['95']])]), 2, 10_000, sizing(), units(5), null)).toBeNull()
    expect(suggestSize(draft([entry('100', '100', [['']])]), 2, 10_000, sizing(), units(5), null)).toBeNull()
  })

  it('hides while the entered size is within 2% of the suggestion', () => {
    expect(suggestSize(draft(twoEntries(), { size: '1190' }), 2, 13_000, sizing(), units(5), null)).toBeNull()
    expect(suggestSize(draft(twoEntries(), { size: '1224' }), 2, 13_000, sizing(), units(5), null)).toBeNull()
    expect(suggestSize(draft(twoEntries(), { size: '1170' }), 2, 13_000, sizing(), units(5), null)?.value).toBe('1200')
  })

  it('reports the budget when the margin would not fit it', () => {
    expect(suggestSize(draft(twoEntries()), 2, 13_000, sizing(), units(5), 1000)).toMatchObject({ value: '1200', overBudget: 1000 })
    expect(suggestSize(draft(twoEntries()), 2, 13_000, sizing(), units(5), 1200)?.overBudget).toBeNull()
    expect(riskSize(draft(twoEntries(), { sizingMode: 'quantity' }), 2, 13_000, sizing(), units(5))?.margin).toBeCloseTo(1200, 9)
  })
})

describe('maximum stop', () => {
  const stopOf = (item: DraftEntry) => item.stops[0]!

  it('moves a price stop beyond the limit to the limit, long and short', () => {
    const long = entry('100', '100', [['85']])
    expect(suggestMaxStop(long, stopOf(long), 'long', 1, 10)).toEqual({ value: '90', price: 90 })
    const short = entry('200', '100', [['230']])
    expect(suggestMaxStop(short, stopOf(short), 'short', 1, 10)).toEqual({ value: '220', price: 220 })
    expect(suggestMaxStop(long, stopOf(long), 'long', 1, 15)).toBeNull()
  })

  it('keeps a percent stop in percent at the leverage', () => {
    // 150% at 10× is a 15% move; the 10% limit is 100% at 10×.
    const percent = entry('100', '100', [['150', '100', 'percent']])
    const suggestion = suggestMaxStop(percent, stopOf(percent), 'long', 10, 10)
    expect(suggestion?.value).toBe('100')
    expect(suggestion?.price).toBeCloseTo(90, 9)
  })

  it('rounds toward the entry so the accepted stop stays within the limit', () => {
    const odd = entry('123.457', '100', [['100']])
    const suggestion = suggestMaxStop(odd, stopOf(odd), 'long', 1, 10)
    expect(suggestion?.value).toBe('111.12')
    const accepted = { ...odd, stops: [{ ...stopOf(odd), value: suggestion!.value }] }
    expect(suggestMaxStop(accepted, stopOf(accepted), 'long', 1, 10)).toBeNull()
  })

  it('ignores stops within the limit, blank, on the wrong side or without an entry price', () => {
    for (const [price, stop] of [['100', '95'], ['100', ''], ['100', '120'], ['', '50']] as const) {
      const item = entry(price, '100', [[stop]])
      expect(suggestMaxStop(item, stopOf(item), 'long', 1, 10)).toBeNull()
    }
  })
})

describe('minimum target', () => {
  it('moves the only target out to the minimum reward to risk', () => {
    const item = entry('100', '100', [['95']], [['105']])
    expect(suggestMinTarget(item, 'long', 1, 2)).toEqual({ levelId: item.targets[0]!.id, value: '110', price: 110 })
  })

  it('moves only the farthest target and counts the shares', () => {
    // Targets at 4 and 6 with a 5 stop: reward 5. Reaching 10 moves the 6 target by 5 ÷ 50%: to 116.
    const item = entry('100', '100', [['95']], [['104', '50'], ['106', '50']])
    const suggestion = suggestMinTarget(item, 'long', 1, 2)
    expect(suggestion).toEqual({ levelId: item.targets[1]!.id, value: '116', price: 116 })
    const accepted = { ...item, targets: [item.targets[0]!, { ...item.targets[1]!, value: suggestion!.value }] }
    expect(rewardToRisk(accepted, 'long', 1)).toBeCloseTo(2, 9)
    expect(suggestMinTarget(accepted, 'long', 1, 2)).toBeNull()
  })

  it('keeps a percent target in percent at the leverage on a short', () => {
    // Stop 10 above 200, a 10% target at 5× is a 4 move: R:R 0.4. A 20 move at 5× is 50%.
    const item = entry('200', '100', [['210']], [['10', '100', 'percent']])
    const suggestion = suggestMinTarget(item, 'short', 5, 2)
    expect(suggestion?.value).toBe('50')
    expect(suggestion?.price).toBeCloseTo(180, 9)
  })

  it('rounds away from the entry so the accepted target reaches the minimum', () => {
    const item = entry('3.0007', '100', [['2.9']], [['3.1']])
    const suggestion = suggestMinTarget(item, 'long', 1, 2.5)!
    const accepted = { ...item, targets: [{ ...item.targets[0]!, value: suggestion.value }] }
    expect(rewardToRisk(accepted, 'long', 1)!).toBeGreaterThanOrEqual(2.5)
    expect(suggestMinTarget(accepted, 'long', 1, 2.5)).toBeNull()
  })

  it('stays quiet when the R:R is enough or unknown, or a short target would go below zero', () => {
    expect(suggestMinTarget(entry('100', '100', [['95']], [['110']]), 'long', 1, 2)).toBeNull()
    expect(suggestMinTarget(entry('100', '100', [['95']], [['']]), 'long', 1, 2)).toBeNull()
    expect(suggestMinTarget(entry('100', '100', [['105']], [['110']]), 'long', 1, 2)).toBeNull()
    expect(suggestMinTarget(entry('10', '100', [['15']], [['9']]), 'short', 1, 3)).toBeNull()
  })
})

describe('leverage suggestion', () => {
  const plan = (changes: Partial<PlayDraft> = {}) => ({ ...draft(twoEntries(), changes), leverage: '2' })

  it('is the lowest leverage that fits the risk-based position in the budget', () => {
    // 2,400 notional in a 1,000 budget needs 3×.
    expect(suggestLeverage(plan(), 2, 50, 1000, sizing(), 2400)).toMatchObject({ leverage: 3 })
    // At 10× the liquidation is inside the stops, so it comes down to the lowest that fits.
    expect(suggestLeverage(plan(), 10, 50, 1000, sizing(), 2400)).toMatchObject({ leverage: 3 })
    expect(suggestLeverage(plan(), 3, 50, 1000, sizing(), 2400)).toBeNull()
  })

  it('leaves a higher leverage alone while it fits and its liquidation clears the stops', () => {
    // A long at 100 with a stop at 98: 5× liquidates near 80.8, beyond the stop, and the margin fits.
    const tight = { ...draft([entry('100', '100', [['98']])]), leverage: '5' }
    expect(suggestLeverage(tight, 5, 50, 1000, sizing(), 2400)).toBeNull()
    // While exposure is reduced it still suggests coming down.
    expect(suggestLeverage(tight, 5, 50, 1000, sizing({ multiplier: '0.5' }), 2400)).toMatchObject({ leverage: 3 })
  })

  it('falls back to an entered quantity, but not an entered margin', () => {
    expect(suggestLeverage(plan({ sizingMode: 'quantity', size: '25' }), 1, 50, 1000, sizing(), null)).toMatchObject({ leverage: 3 })
    expect(suggestLeverage(plan({ size: '1200' }), 1, 50, 1000, sizing(), null)).toBeNull()
  })

  it('without a budget only brings a liquidation inside the stops back out', () => {
    expect(suggestLeverage(plan(), 2, 50, null, sizing(), 2400)).toBeNull()
    expect(suggestLeverage(plan(), 2, 50, 0, sizing(), 2400)).toBeNull()
    // A long at 120.5 with a stop at 114 on a 20× contract: 20× liquidates near 117.4, inside the stop;
    // 13× (about 114.08) is still inside; 12× (about 113.3) is the highest that clears it.
    const sol = { ...draft([entry('120.5', '100', [['114']])]), leverage: '20' }
    expect(suggestLeverage(sol, 20, 20, null, sizing(), null)).toMatchObject({ leverage: 12 })
    expect(suggestLeverage(sol, 12, 20, null, sizing(), null)).toBeNull()
  })

  it('keeps the estimated liquidation beyond the farthest stop', () => {
    // At 3× on a 50× contract a long at 100 liquidates near 67.3, above a stop at 60.
    const deep = { ...draft([entry('100', '100', [['60']])]), leverage: '1' }
    expect(suggestLeverage(deep, 1, 50, 1000, sizing(), 2400)).toBeNull()
    expect(suggestLeverage(deep, 1, 50, 1500, sizing(), 2400)).toMatchObject({ leverage: 2 })
    // Short: at 3× liquidation is near 132; a stop at 140 is past it.
    const short = { ...draft([entry('100', '100', [['140']])], { direction: 'short' }), leverage: '1' }
    expect(suggestLeverage(short, 1, 50, 1000, sizing(), 2400)).toBeNull()
    // Without the venue maximum the liquidation is unknown, so only the budget decides.
    expect(suggestLeverage(deep, 1, null, 1000, sizing(), 2400)).toMatchObject({ leverage: 3 })
  })

  it('is capped at the venue maximum and, while exposure is reduced, at the current leverage', () => {
    expect(suggestLeverage(plan(), 1, 2, 1000, sizing(), 2400)).toBeNull()
    expect(suggestLeverage(plan(), 2, 50, 1000, sizing({ multiplier: '0.5' }), 2400)).toBeNull()
    expect(suggestLeverage(plan(), 5, 50, 1000, sizing({ multiplier: '0.25' }), 2400)).toMatchObject({ leverage: 3 })
  })
})

describe('price rounding', () => {
  it('keeps five significant figures in the asked direction', () => {
    expect(roundPrice(111.1113, 'up')).toBe('111.12')
    expect(roundPrice(111.1113, 'down')).toBe('111.11')
    expect(roundPrice(90, 'up')).toBe('90')
    expect(roundPrice(0.000123456, 'down')).toBe('0.00012345')
    expect(roundPrice(123456.7, 'up')).toBe('123457')
  })
  it("rounds to the instrument's tick when the venue has one", () => {
    expect(roundPrice(0.094607, 'up', 0.00001)).toBe('0.09461')
    expect(roundPrice(0.094607, 'down', 0.00001)).toBe('0.0946')
    expect(roundPrice(101.26, 'up', 0.5)).toBe('101.5')
    expect(roundPrice(101.26, 'down', 0.5)).toBe('101')
    expect(roundPrice(101.5, 'up', 0.5)).toBe('101.5')
  })
  it('keeps suggested stops on the tick and within the limit', () => {
    const stop = suggestMaxStop(entry('100', '100', [['90']]), level(['90']), 'long', 1, 3.33, 0.5)
    expect(stop).toMatchObject({ value: '97', price: 97 })
  })
})

// Phase 0, 0.1 (D-04): the review probe `verify-sol-frontend/f1f2.mjs` F2 and its variants.
describe('stop coverage gate', () => {
  const inputs = (entries: DraftEntry[]) => ({
    draft: { ...draft(entries, { sizingMode: 'quantity', size: '1' }), leverage: '5' }, leverage: 5, maxLeverage: 50,
    sizing: sizing(), units: units(5), balance: 10_000, budget: 100_000,
  })
  const stopEntry = (stops: Level[], share = '100', price = '100') => entry(price, share, stops, [['120']])

  it.each([
    ['one stop of two is blank', [stopEntry([['95', '50'], ['', '50']])]],
    ['the only stop closes half the entry', [stopEntry([['95', '50']])]],
    ['half the position has no entry price', [stopEntry([['95']], '50'), stopEntry([['90']], '50', '')]],
  ])('offers no size or size-driven leverage while %s', (_case, entries) => {
    const plan = planSuggestions(inputs(entries))
    expect(riskSize(inputs(entries).draft, 5, 10_000, sizing(), units(5))).toBeNull()
    expect(plan.size).toBeNull()
    expect(plan.lossPerUnit).toBeNull()
    expect(plan.leverage).toBeNull()
    expect(plan.stopCoverage).toMatch(/^50% of the position has no (priced stop|entry price)$/)
    expect(suggestionCount(plan)).toBe(0)
  })

  it('still sizes a plan whose stops cover the whole position (control: stops at 95 and 90)', () => {
    const plan = planSuggestions(inputs([stopEntry([['95', '50'], ['90', '50']])]))
    // 125 at risk over a 7.5 average distance: 16.66666 units at five decimals.
    expect(plan.size?.value).toBe('16.66666')
    expect(plan.stopCoverage).toBeNull()
  })

  it('names stops on the wrong side and incomplete entry shares', () => {
    expect(planSuggestions(inputs([stopEntry([['105']])])).stopCoverage).toBe('A stop sits on the wrong side of its entry')
    expect(planSuggestions(inputs([stopEntry([['95']], '60')])).stopCoverage).toBe('Entry shares total 60%, not 100%')
  })

  // Review of 0.1: like the summary, a blank leverage is unknown, not 1×.
  it('treats a blank leverage as unknown, as the summary does', () => {
    const blank = { ...inputs([stopEntry([['10', '100', 'percent']])]), leverage: 1 }
    blank.draft = { ...blank.draft, leverage: '' }
    const plan = planSuggestions(blank)
    expect(plan.size).toBeNull()
    expect(plan.stopCoverage).toBe('The leverage is not set')
    expect(riskSize(blank.draft, 1, 10_000, sizing(), units(5))).toBeNull()
    expect(positionRiskSummary({ ...blank.draft, size: '1' }).stops.coverage.status).not.toBe('complete')
  })

  it('keeps only a liquidation leverage line, against the priced stops', () => {
    // A long at 120.5 with one priced stop at 114 for half the entry, at 20× on a 20× contract: the estimate sits inside it.
    const partial = { ...inputs([entry('120.5', '100', [['114', '50'], ['', '50']])]), leverage: 20, maxLeverage: 20 }
    partial.draft = { ...partial.draft, leverage: '20' }
    expect(planSuggestions(partial).leverage).toEqual({ leverage: 12, reason: 'liquidation' })
    // Without the gate the entered quantity's margin would be held to a tiny budget instead.
    expect(planSuggestions({ ...partial, budget: 1 }).leverage).toEqual({ leverage: 12, reason: 'liquidation' })
  })
})

// Phase 0, 0.1 (D-11): the review probe `round3-dotnet/R3b.cs`.
describe('after a fill', () => {
  const entries = [{ ...entry('100', '50', [['80']], [['105']]), id: 'entry-1', name: 'Entry 1' },
    { ...entry('90', '50', [['70']], [['95']]), id: 'entry-2', name: 'Entry 2' }]
  const inputs = (filledEntries?: ReadonlySet<string> | 'all') => ({
    // Without a venue maximum the liquidation is unknown, so the 100 budget decides the leverage (6×).
    draft: { ...draft(entries), leverage: '5' }, leverage: 5, maxLeverage: null, sizing: sizing(), units: units(5), balance: 10_000,
    budget: 100, ...(filledEntries ? { filledEntries } : {}),
  })

  it('suggests size, leverage, stops and targets before any fill', () => {
    const plan = planSuggestions(inputs())
    expect(plan.size).not.toBeNull()
    expect(plan.leverage).not.toBeNull()
    expect(plan.stops.map(stop => stop.entryName)).toEqual(['Entry 1', 'Entry 2'])
    expect(plan.filled).toBe(false)
  })

  it('hides size and leverage once an entry has filled and keeps level suggestions only for unfilled entries', () => {
    const plan = planSuggestions(inputs(new Set(['entry-1'])))
    expect(plan.size).toBeNull()
    expect(plan.leverage).toBeNull()
    expect(plan.filled).toBe(true)
    expect(plan.stops.map(stop => stop.entryName)).toEqual(['Entry 2'])
    expect(plan.targets.map(target => target.entryName)).toEqual(['Entry 2'])
    const unknown = planSuggestions(inputs('all'))
    expect([unknown.size, unknown.leverage, unknown.stops.length, unknown.targets.length]).toEqual([null, null, 0, 0])
  })
})

// Phase 0, 0.1 (D-07): a known zero budget never asks for more leverage to fit it.
describe('zero budget', () => {
  it('reports the size above a zero budget without a leverage to fit it', () => {
    const plan = planSuggestions({ draft: { ...draft(twoEntries()), leverage: '2' }, leverage: 2, maxLeverage: 50, sizing: sizing(),
      units: units(5), balance: 13_000, budget: 0 })
    expect(plan.size).toMatchObject({ value: '1200', overBudget: 0 })
    expect(plan.leverage).toBeNull()
  })
})
