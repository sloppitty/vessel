import { describe, expect, it } from 'vitest'
import { createDraft, createEntry, type DraftEntry, type DraftExit, type PlayDraft } from './draft'
import { exactDecimal, exactFixed, type Exact } from '@/lib/decimal'
import { positionRiskSummary, signedPayoffText, type EntryFills, type RiskReference } from './position-risk-summary'
import type { SizeUnits } from './sizing'

const units: SizeUnits = { quote: 'USDC', base: 'ETH', quantityDecimals: 4 }
const reference: RiskReference = { name: 'Trading account balance', amount: '10000', quote: 'USDC' }
const exit = (value: string, share = '100', unit: DraftExit['unit'] = 'price'): DraftExit =>
  ({ id: `${value}-${share}`, value, share, unit })
const entry = (price: string, share: string, stops: DraftExit[], targets: DraftExit[]): DraftEntry =>
  ({ ...createEntry(0), price, share, stops, targets })
const plan = (overrides: Partial<PlayDraft> = {}): PlayDraft => ({
  ...createDraft(), size: '10', sizingMode: 'quantity', leverage: '4',
  entries: [
    entry('100', '40', [exit('90', '25'), exit('80', '75')], [exit('120', '50'), exit('140', '50')]),
    entry('200', '60', [exit('190')], [exit('220', '25'), exit('240', '75')]),
  ], ...overrides,
})
const summary = (draft = plan()) => positionRiskSummary(draft, units, reference)

describe('whole-position planned money summary', () => {
  it('sums actual entry/exit quantities before deriving R:R, not entry ratios', () => {
    const result = summary()
    expect(result.quantity).toBe(10)
    expect(result.pricedQuantity).toBe(10)
    expect(result.entryAllocation).toEqual({ percent: 100, status: 'complete' })
    expect(result.pricedCoverage).toEqual({ percent: 100, status: 'complete' })
    expect(result.stops).toEqual({
      allocation: { percent: 100, status: 'complete' }, coverage: { percent: 100, status: 'complete' },
      knownAmount: 130, totalAmount: 130,
    })
    expect(result.targets.totalAmount).toBe(330)
    expect(result.rewardToRisk).toBeCloseTo(330 / 130)
    expect(result.rewardToRisk).not.toBeCloseTo((30 / 17.5 + 35 / 10) / 2)
    expect(result.reference).toEqual({ name: reference.name, amount: 10000 })
    expect(result.riskSharePercent).toBeCloseTo(1.3)
  })

  it('uses the existing whole-position margin conversion with the complete average entry', () => {
    const result = summary(plan({ sizingMode: 'margin', size: '400' }))
    expect(result.quantity).toBe(10) // 400 × 4 ÷ (100 × .4 + 200 × .6).
    expect(result.stops.totalAmount).toBe(130)
    expect(result.targets.totalAmount).toBe(330)
  })

  it('reverses price distances for shorts', () => {
    const draft = plan({ direction: 'short' })
    draft.entries = draft.entries.map(item => ({
      ...item,
      stops: item.stops.map(level => ({ ...level, value: String(2 * Number(item.price) - Number(level.value)) })),
      targets: item.targets.map(level => ({ ...level, value: String(2 * Number(item.price) - Number(level.value)) })),
    }))
    expect(summary(draft).stops.totalAmount).toBe(130)
    expect(summary(draft).targets.totalAmount).toBe(330)
  })

  it.each(['long', 'short'] as const)('resolves return percentages at leverage without applying leverage twice for %s', direction => {
    const entries = [
      entry('100', '40', [exit('10', '25', 'percent'), exit('10', '75', 'percent')], [exit('20', '100', 'percent')]),
      entry('200', '60', [exit('10', '100', 'percent')], [exit('20', '25', 'percent'), exit('20', '75', 'percent')]),
    ]
    const draft = plan({ direction, entries, sizingMode: 'margin', size: '400' })
    for (const leverage of ['4', '8']) {
      const result = summary({ ...draft, leverage })
      expect(result.stops.totalAmount).toBeCloseTo(40)
      expect(result.targets.totalAmount).toBeCloseTo(80)
      expect(result.rewardToRisk).toBeCloseTo(2)
    }
    expect(summary({ ...draft, sizingMode: 'quantity', size: '10', leverage: '8' }).stops.totalAmount).toBeCloseTo(20)
  })

  it('keeps a known subtotal and an unknown tranche when a stop is absent', () => {
    const draft = plan()
    draft.entries[1]!.stops = []
    const result = summary(draft)
    expect(result.stops.knownAmount).toBe(70)
    expect(result.stops.totalAmount).toBeNull()
    expect(result.stops.allocation).toEqual({ percent: 40, status: 'partial' })
    expect(result.stops.coverage).toEqual({ percent: 40, status: 'partial' })
    expect(result.targets.totalAmount).toBe(330)
    expect(result.rewardToRisk).toBeNull()
    expect(result.riskSharePercent).toBeNull()
  })

  it('distinguishes allocated stops from priced stop coverage', () => {
    const draft = plan()
    draft.entries[0]!.stops[1]!.value = ''
    const result = summary(draft)
    expect(result.stops.allocation).toEqual({ percent: 100, status: 'complete' })
    expect(result.stops.coverage).toEqual({ percent: 70, status: 'partial' })
    expect(result.stops.knownAmount).toBe(70)
    expect(result.stops.totalAmount).toBeNull()
  })

  it.each(['', '25'])('does not normalize missing or incomplete exit shares %s', share => {
    const draft = plan()
    draft.entries[0]!.stops[1]!.share = share
    draft.entries[0]!.targets = [exit('120', '50')]
    const result = summary(draft)
    const extra = share === '' ? 0 : 20
    expect(result.stops.knownAmount).toBe(70 + extra)
    expect(result.stops.allocation.percent).toBe(share === '' ? 70 : 80)
    expect(result.stops.totalAmount).toBeNull()
    expect(result.targets.knownAmount).toBe(250)
    expect(result.targets.allocation).toEqual({ percent: 80, status: 'partial' })
    expect(result.targets.totalAmount).toBeNull()
    expect(result.rewardToRisk).toBeNull()
  })

  it('does not assign equal shares when all exit shares are blank', () => {
    const draft = plan()
    draft.entries.forEach(item => item.stops.forEach(level => { level.share = '' }))
    const result = summary(draft)
    expect(result.stops.knownAmount).toBeNull()
    expect(result.stops.totalAmount).toBeNull()
    expect(result.stops.coverage).toEqual({ percent: 0, status: 'partial' })
  })

  it('does not normalize incomplete entry allocations', () => {
    const draft = plan()
    draft.entries[1]!.share = '20'
    const result = summary(draft)
    expect(result.entryAllocation).toEqual({ percent: 60, status: 'partial' })
    expect(result.pricedQuantity).toBe(6)
    expect(result.stops.knownAmount).toBe(90)
    expect(result.stops.coverage).toEqual({ percent: 60, status: 'partial' })
    expect(result.stops.totalAmount).toBeNull()
    expect(result.targets.totalAmount).toBeNull()
    // There is no full-position quantity to infer from an incomplete margin plan.
    expect(summary({ ...draft, sizingMode: 'margin' }).quantity).toBeNull()
  })

  it('separates priced quantity from entry allocations and withholds incomplete margin-derived amounts', () => {
    const draft = plan()
    draft.entries[1]!.price = ''
    const result = summary(draft)
    expect(result.entryAllocation.status).toBe('complete')
    expect(result.pricedCoverage).toEqual({ percent: 40, status: 'partial' })
    expect(result.pricedQuantity).toBe(4)
    expect(result.stops.allocation.status).toBe('complete')
    expect(result.stops.coverage).toEqual({ percent: 40, status: 'partial' })
    expect(result.stops.knownAmount).toBe(70)
    const margin = summary({ ...draft, sizingMode: 'margin' })
    expect(margin.quantity).toBeNull()
    expect(margin.pricedQuantity).toBeNull()
    expect(margin.stops.knownAmount).toBeNull()
    expect(margin.stops.totalAmount).toBeNull()
  })

  it('ignores unused zero-share entries and exits without requiring their prices', () => {
    const draft = plan()
    draft.entries.push(entry('', '0', [exit('NaN')], []))
    draft.entries[0]!.stops.push(exit('', '0'))
    expect(summary(draft).stops.totalAmount).toBe(130)
    expect(summary(draft).pricedCoverage.status).toBe('complete')
  })

  it.each(['margin', 'quantity'] as const)('keeps an explicit zero %s distinct from a missing size', sizingMode => {
    const result = summary(plan({ size: '0', sizingMode }))
    expect(result.quantity).toBe(0)
    expect(result.pricedQuantity).toBe(0)
    expect(result.stops.totalAmount).toBe(0)
    expect(result.targets.totalAmount).toBe(0)
    expect(result.rewardToRisk).toBeNull()
    expect(result.riskSharePercent).toBe(0)
    const partial = plan({ size: '0', sizingMode })
    partial.entries[1]!.stops = []
    expect(summary(partial).stops.knownAmount).toBe(0)
    expect(summary(partial).stops.totalAmount).toBeNull()
  })

  it.each(['long', 'short'] as const)('leaves wrong-side and entry-price exits uncovered for %s', direction => {
    const draft = plan({ direction })
    draft.entries[0]!.stops = [exit(direction === 'long' ? '110' : '90')]
    draft.entries[0]!.targets = [exit('100')]
    const result = summary(draft)
    expect(result.stops.allocation.status).toBe('complete')
    expect(result.stops.coverage.status).toBe('partial')
    expect(result.stops.totalAmount).toBeNull()
    expect(result.targets.totalAmount).toBeNull()
  })

  it('rejects entry overallocation rather than rescaling the requested quantities', () => {
    const draft = plan()
    draft.entries[0]!.share = '60'
    const result = summary(draft)
    expect(result.entryAllocation).toEqual({ percent: 120, status: 'invalid' })
    expect(result.pricedQuantity).toBeNull()
    expect(result.stops.knownAmount).toBeNull()
    expect(result.stops.totalAmount).toBeNull()
    expect(result.rewardToRisk).toBeNull()
  })

  it('rejects per-entry exit overallocation even when other underallocations offset the aggregate', () => {
    const draft = plan({ entries: [
      entry('100', '50', [exit('90', '75'), exit('80', '75')], [exit('120')]),
      entry('200', '50', [exit('190', '50')], [exit('220')]),
    ] })
    const result = summary(draft)
    expect(result.stops.allocation).toEqual({ percent: 100, status: 'invalid' })
    expect(result.stops.knownAmount).toBeNull()
    expect(result.stops.totalAmount).toBeNull()
    expect(result.targets.totalAmount).toBe(200)
  })

  it('never turns a tiny missing priced tranche or allocation gap into complete coverage', () => {
    const draft = plan({ entries: [entry('100', '100',
      [exit('90', '99.999999999'), exit('', '0.000000001')], [exit('120')])] })
    expect(summary(draft).stops.coverage.status).toBe('partial')
    expect(summary(draft).stops.totalAmount).toBeNull()
    draft.entries[0]!.stops = [exit('90', '99.999999999')]
    expect(summary(draft).stops.allocation.status).toBe('partial')
  })

  it.each(['99.99999999999999', '99.99999999999999999999999999'])(
    'keeps entry shortfall %s partial without tolerance or rounding', share => {
      const draft = plan({ entries: [entry('100', share, [exit('90')], [exit('120')])] })
      const result = summary(draft)
      expect(result.entryAllocation.status).toBe('partial')
      expect(result.pricedCoverage.status).toBe('partial')
      expect(result.quantity).toBe(10)
      expect(result.pricedQuantity).toBeCloseTo(10)
      for (const kind of ['stops', 'targets'] as const) {
        expect(result[kind].allocation.status).toBe('partial')
        expect(result[kind].coverage.status).toBe('partial')
        expect(result[kind].knownAmount).not.toBeNull()
        expect(result[kind].totalAmount).toBeNull()
      }
      expect(result.rewardToRisk).toBeNull()
      expect(result.riskSharePercent).toBeNull()
      expect(summary({ ...draft, sizingMode: 'margin' }).quantity).toBeNull()
    })

  it.each(['99.99999999999999', '99.99999999999999999999999999'])(
    'keeps exit shortfall %s partial independently for stops and targets', share => {
      for (const kind of ['stops', 'targets'] as const) {
        const draft = plan({ entries: [entry('100', '100', [exit('90')], [exit('120')])] })
        draft.entries[0]![kind][0]!.share = share
        const result = summary(draft)
        expect(result.entryAllocation.status).toBe('complete')
        expect(result.pricedCoverage.status).toBe('complete')
        expect(result[kind].allocation.status).toBe('partial')
        expect(result[kind].coverage.status).toBe('partial')
        expect(result[kind].knownAmount).not.toBeNull()
        expect(result[kind].totalAmount).toBeNull()
        expect(result[kind === 'stops' ? 'targets' : 'stops'].coverage.status).toBe('complete')
        expect(result.rewardToRisk).toBeNull()
      }
    })

  it.each([
    ['100.00000000000001'],
    ['100.00000000000000000000000001'],
    ['99.99999999999999999999999999', '0.00000000000000000000000002'],
  ])('rejects tiny overallocation in exact shares %j', (...shares) => {
    const draft = plan({ entries: shares.map(share => entry('100', share, [exit('90')], [exit('120')])) })
    const result = summary(draft)
    expect(result.entryAllocation.status).toBe('invalid')
    expect(result.pricedQuantity).toBeNull()
    expect(result.stops.totalAmount).toBeNull()
    expect(result.targets.totalAmount).toBeNull()
    for (const kind of ['stops', 'targets'] as const) {
      const exits = shares.map(share => exit(kind === 'stops' ? '90' : '120', share))
      const exitDraft = plan({ entries: [entry('100', '100', [exit('90')], [exit('120')])] })
      exitDraft.entries[0]![kind] = exits
      const exitResult = summary(exitDraft)
      expect(exitResult[kind].allocation.status).toBe('invalid')
      expect(exitResult[kind].coverage.status).toBe('invalid')
      expect(exitResult[kind].knownAmount).toBeNull()
      expect(exitResult[kind].totalAmount).toBeNull()
      expect(exitResult[kind === 'stops' ? 'targets' : 'stops'].coverage.status).toBe('complete')
    }
  })

  it.each([
    ['33.3', '33.3', '33.4'],
    ['99.99999999999999999999999999', '0.00000000000000000000000001'],
    ['0.1', '0.2', '99.7'],
  ])('accepts exactly complete decimal splits %j', (...shares) => {
    const draft = plan({ entries: shares.map(share => entry('100', share,
      shares.map(share => exit('90', share)), shares.map(share => exit('120', share)))) })
    const result = summary(draft)
    expect(result.entryAllocation.status).toBe('complete')
    expect(result.pricedCoverage.status).toBe('complete')
    expect(result.pricedQuantity).toBeCloseTo(10)
    expect(result.stops.allocation.status).toBe('complete')
    expect(result.stops.coverage.status).toBe('complete')
    expect(result.stops.totalAmount).toBeCloseTo(100)
    expect(result.targets.allocation.status).toBe('complete')
    expect(result.targets.coverage.status).toBe('complete')
    expect(result.targets.totalAmount).toBeCloseTo(200)
  })

  it('does not offset a tiny per-entry exit overage against another entry shortfall', () => {
    const result = summary(plan({ entries: [
      entry('100', '50', [exit('90', '100.00000000000000000000000001')], [exit('120')]),
      entry('100', '50', [exit('90', '99.99999999999999999999999999')], [exit('120')]),
    ] }))
    expect(result.stops.allocation).toEqual({ percent: 100, status: 'invalid' })
    expect(result.stops.coverage.status).toBe('invalid')
    expect(result.stops.knownAmount).toBeNull()
    expect(result.stops.totalAmount).toBeNull()
    expect(result.targets.totalAmount).toBe(200)
  })

  it('keeps an exactly allocated tiny unpriced exit uncovered at the plan share precision limit', () => {
    // The finest split plan text allows: 28 decimal places and a C# decimal coefficient.
    const nearFull = `99.${'9'.repeat(26)}`
    const tiny = `0.${'0'.repeat(25)}1`
    const draft = plan({ entries: [entry('100', '100', [exit('90', nearFull), exit('', tiny)], [exit('120')])] })
    const result = summary(draft)
    expect(result.stops.allocation.status).toBe('complete')
    expect(result.stops.coverage.status).toBe('partial')
    expect(result.stops.knownAmount).toBeCloseTo(100)
    expect(result.stops.totalAmount).toBeNull()
    expect(result.targets.totalAmount).toBe(200)
    draft.entries[0]!.stops[1]!.value = '90'
    expect(summary(draft).stops.totalAmount).toBeCloseTo(100)
  })

  it('rejects overlong share text instead of truncating it into a complete allocation', () => {
    const draft = plan({ entries: [entry('100', `99.${'9'.repeat(62)}`, [exit('90')], [exit('120')])] })
    const result = summary(draft)
    expect(result.entryAllocation.status).toBe('invalid')
    expect(result.pricedQuantity).toBeNull()
    expect(result.stops.totalAmount).toBeNull()
    expect(result.targets.totalAmount).toBeNull()
  })

  it('keeps a missing entry price partial even when its share is below floating-point addition precision', () => {
    const draft = plan({ entries: [
      entry('100', '99.99999999999999', [exit('90')], [exit('120')]),
      entry('', '0.00000000000001', [exit('90')], [exit('120')]),
    ] })
    expect(summary(draft).pricedCoverage.status).toBe('partial')
    expect(summary(draft).stops.totalAmount).toBeNull()
    expect(summary({ ...draft, sizingMode: 'margin' }).quantity).toBeNull()
  })

  it.each(['', 'NaN', 'Infinity', '-1', '1e309', '9'.repeat(400)])('leaves missing/nonfinite size %s unknown', size => {
    const result = summary(plan({ size }))
    expect(result.quantity).toBeNull()
    expect(result.stops.knownAmount).toBeNull()
    expect(result.stops.totalAmount).toBeNull()
    expect(result.rewardToRisk).toBeNull()
  })

  it.each(['', 'NaN', 'Infinity', '0', '2.5'])('does not substitute 1× for an invalid leverage %s', leverage => {
    const result = summary(plan({ leverage }))
    expect(result.quantity).toBeNull()
    expect(result.stops.totalAmount).toBeNull()
    expect(result.targets.totalAmount).toBeNull()
  })

  it.each(['NaN', 'Infinity', '-1', '101', '1e2', '1e-2', '9'.repeat(400)])('rejects invalid shares %s without finite-looking totals', share => {
    const draft = plan()
    draft.entries[0]!.stops[0]!.share = share
    expect(summary(draft).stops.totalAmount).toBeNull()
    expect(summary(draft).stops.knownAmount).toBeNull()
    draft.entries[0]!.share = share
    expect(summary(draft).entryAllocation.status).toBe('invalid')
    expect(summary(draft).targets.totalAmount).toBeNull()
  })

  it.each(['NaN', 'Infinity', '9'.repeat(400)])('leaves nonfinite prices %s uncovered', value => {
    const draft = plan()
    draft.entries[0]!.stops[0]!.value = value
    const result = summary(draft)
    expect(result.stops.knownAmount).toBe(120)
    expect(result.stops.coverage).toEqual({ percent: 90, status: 'partial' })
    expect(result.stops.totalAmount).toBeNull()
    draft.entries[0]!.price = value
    expect(summary(draft).pricedCoverage).toEqual({ percent: 60, status: 'partial' })
  })

  it('keeps arithmetic overflow unknown, including a nonfinite average hidden behind a zero derived quantity', () => {
    const huge = `1${'0'.repeat(308)}`
    const draft = plan({ entries: [entry(huge, '100', [exit(`9${'0'.repeat(307)}`)], [])] })
    expect(summary({ ...draft, sizingMode: 'margin', size: '100' }).quantity).toBeNull()
    expect(summary({ ...draft, size: huge }).stops.knownAmount).toBeNull()
    expect(summary({ ...draft, size: huge }).stops.totalAmount).toBeNull()
  })

  it('keeps an empty plan unknown instead of inventing a zero payoff', () => {
    expect(summary(createDraft()).stops.totalAmount).toBeNull()
    const result = summary(plan({ entries: [] }))
    expect(result.entryAllocation).toEqual({ percent: 0, status: 'partial' })
    expect(result.stops.totalAmount).toBeNull()
    expect(result.rewardToRisk).toBeNull()
  })

  it.each([
    undefined, null, { ...reference, name: ' ' }, { ...reference, amount: '0' },
    { ...reference, amount: '-1' }, { ...reference, amount: 'Infinity' },
    { ...reference, amount: '9'.repeat(400) }, { ...reference, quote: 'USD' },
  ])('requires an explicitly named positive reference in the same quote asset: %j', riskReference => {
    const result = positionRiskSummary(plan({ budgetOverride: '500' }), units, riskReference)
    expect(result.reference).toBeNull()
    expect(result.riskSharePercent).toBeNull()
    expect(result.stops.totalAmount).toBe(130)
  })
})

// Phase 0, 0.5b: the all-stops row is signed and exact, and a filled entry's stops are measured from its average fill.
describe('signed all-stops result', () => {
  const one = (stop: string, overrides: Partial<PlayDraft> = {}, unit: DraftExit['unit'] = 'price'): PlayDraft => plan({
    size: '1', leverage: '3', entries: [{ ...entry('100', '100', [exit(stop, '100', unit)], [exit('110')]), id: 'e1' }], ...overrides,
  })
  const fills = (average?: string, filled: ReadonlySet<string> = new Set(['e1'])): EntryFills =>
    ({ filled, averages: new Map(average === undefined ? [] : [['e1', exactDecimal(average)!]]) })
  const text = (value: Exact | null) => value === null ? null : exactFixed(value, 28)
  const signed = (draft: PlayDraft, entryFills?: EntryFills | 'unknown') => positionRiskSummary(draft, units, reference, entryFills)

  it('reads +5 locked in for an Open long filled at 100 with its stop at 105 (acceptance)', () => {
    const result = signed(one('105'), fills('100'))
    expect(text(result.stopPayoff.total)).toBe('5')
    expect(result.stopPayoff.basis).toBe('average fill')
    expect(result.stops.coverage).toEqual({ percent: 100, status: 'complete' })
    // Nothing of the balance is at risk, and there is no loss to measure reward against.
    expect(result.riskSharePercent).toBe(0)
    expect(result.rewardToRisk).toBeNull()
    // Before the fill the same stop is on the wrong side: not covered, so the total stays unknown.
    expect(signed(one('105')).stopPayoff.total).toBeNull()
    expect(signed(one('105')).stops.coverage.status).toBe('partial')
  })

  it('measures from the average fill, not the planned entry, and says when it falls back', () => {
    expect(text(signed(one('101'), fills('102')).stopPayoff.total)).toBe('-1')
    expect(text(signed(one('100'), fills('100')).stopPayoff.total)).toBe('0')
    // Filled, but no average is reported for the entry: from the planned entry, and it says so.
    const planned = signed(one('105'), fills(undefined))
    expect([text(planned.stopPayoff.total), planned.stopPayoff.basis]).toEqual(['5', 'planned entry'])
    // While the fills load nothing is measured, not even from the planned entry.
    const pending = signed(one('105'), 'unknown')
    expect(pending.stopPayoff).toMatchObject({ known: null, total: null, basis: null, pending: true })
    expect([pending.stops.totalAmount, pending.riskSharePercent, pending.rewardToRisk]).toEqual([null, null, null])
    // A percentage resolves from the planned entry at the leverage, as the matcher does: 0% is 100, 1 under a 101 fill.
    expect(text(signed(one('0', {}, 'percent'), fills('101')).stopPayoff.total)).toBe('-1')
    expect(text(signed(one('95')).stopPayoff.total)).toBe('-5')
  })

  it('mirrors shorts', () => {
    const short = plan({ direction: 'short', size: '2', leverage: '3', entries: [{ ...entry('100', '100', [exit('95')], [exit('90')]), id: 'e1' }] })
    expect(text(signed(short, fills('100')).stopPayoff.total)).toBe('10')
    expect(text(signed({ ...short, entries: [{ ...short.entries[0]!, stops: [exit('104')] }] }, fills('100')).stopPayoff.total)).toBe('-8')
  })

  it('keeps the sign exact where floating point would not', () => {
    // 3 × 70% × (100.3 − 100) − 3 × 30% × (100 − 99.3) is exactly zero; in floats it is −8.5e-15, a loss.
    const draft = plan({ size: '3', leverage: '1', entries: [
      { ...entry('100', '70', [exit('100.3')], [exit('110')]), id: 'e1' },
      { ...entry('100', '30', [exit('99.3')], [exit('110')]), id: 'e2' },
    ] })
    const result = signed(draft, fills('100'))
    expect(result.stopPayoff.total).toEqual({ n: 0n, d: 1n })
    expect(result.stopPayoff.basis).toBe('mixed')
    expect(result.riskSharePercent).toBe(0)
  })

  it('stays exact through margin sizing and percentages at leverage', () => {
    // 100 margin at 3× buys 3 at an average entry of 100; a 10% stop is a 3⅓% move to 96.66…, so 3 × 3.33… = 10.
    const draft = one('10', { sizingMode: 'margin', size: '100' }, 'percent')
    expect(text(signed(draft).stopPayoff.total)).toBe('-10')
    expect(signed(draft).stops.totalAmount).toBeCloseTo(10)
  })

  it('keeps unknown parts unknown', () => {
    const partial = plan({ size: '1', leverage: '3', entries: [
      { ...entry('100', '50', [exit('105')], [exit('110')]), id: 'e1' },
      { ...entry('100', '50', [], [exit('110')]), id: 'e2' },
    ] })
    const result = signed(partial, fills('100'))
    expect(text(result.stopPayoff.known)).toBe('2.5')
    expect(result.stopPayoff.total).toBeNull()
    expect(result.riskSharePercent).toBeNull()
    expect(signed({ ...one('105'), size: '' }, fills('100')).stopPayoff)
      .toEqual({ known: null, total: null, basis: 'average fill', empty: false, pending: false })
  })

  it('marks a size of zero as no position, not break-even', () => {
    const empty = signed({ ...one('95'), size: '0' })
    expect([text(empty.stopPayoff.total), empty.stopPayoff.empty]).toEqual(['0', true])
    expect(signedPayoffText(empty.stopPayoff.total!, units, true)).toBe('0 USDC')
    expect(signedPayoffText(empty.stopPayoff.total!, units)).toBe('0 USDC break-even')
  })
})
