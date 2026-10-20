import { describe, expect, it } from 'vitest'
import { createEntry, createExit, type DraftEntry, type DraftExit } from './draft'
import {
  addChartExit, applyEntryEdit, applyLevelDrag, averageEntryPrice, averageOverlayId, formatDraggedPrice, keepPercentLevelPrices, levelPrice,
  overlayPrice, parseOverlayId, placeLevel, planOverlays, removeExit, setExitShare,
} from './levels'

function entry(index: number, patch: Partial<DraftEntry> = {}): DraftEntry {
  return { ...createEntry(index), ...patch }
}
const stop = (id: string, unit: DraftExit['unit'], value: string, share = '100'): DraftExit => ({ id, unit, value, share })

describe('planned level overlays', () => {
  it('plots entry, stop and targets, resolving percentage distances by direction', () => {
    const first = entry(0, {
      price: '100', stops: [stop('s', 'percent', '5')],
      targets: [{ ...createExit('50'), unit: 'price', value: '120' }, { ...createExit('50'), unit: 'percent', value: '10' }],
    })
    // A single entry needs no entry prefix on its tags.
    const long = planOverlays([first], first.id, 'aggregate', 'long')
    expect(long.map(o => [o.label, o.kind, o.price])).toEqual([
      ['Entry', 'entry', 100], ['SL', 'stop', 95], ['TP1', 'target', 120], ['TP2', 'target', 110.00000000000001],
    ])
    const short = planOverlays([first], first.id, 'aggregate', 'short')
    expect(short.find(o => o.kind === 'stop')!.price).toBeCloseTo(105)
    expect(long.every(o => o.draggable && o.emphasis === 'selected' && o.accent === first.color)).toBe(true)
    expect(long.map(o => o.color)).toEqual([first.color, 'var(--negative)', 'var(--positive)', 'var(--positive)'])
  })

  it('omits blank or invalid values and percentages without an entry price', () => {
    const blank = entry(0, { price: '', stops: [stop('s', 'percent', '5')] })
    const invalid = entry(1, { price: '-3', stops: [stop('t', 'price', 'abc')] })
    expect(planOverlays([blank, invalid], blank.id, 'aggregate', 'long')).toEqual([])
    expect(levelPrice(100, stop('x', 'percent', '150'), 'stop', 'long')).toBeNull()
  })

  it('treats percentages as returns on margin at the leverage', () => {
    const first = entry(0, { price: '100', stops: [stop('s', 'percent', '10')], targets: [{ ...createExit('100'), unit: 'percent', value: '50' }] })
    const at10 = planOverlays([first], first.id, 'aggregate', 'long', 10)
    expect(at10.find(o => o.kind === 'stop')!.price).toBeCloseTo(99)
    expect(at10.find(o => o.kind === 'target')!.price).toBeCloseTo(105)
    expect(levelPrice(100, stop('x', 'percent', '150'), 'stop', 'long', 10)).toBeCloseTo(85)
  })

  it('numbers several stops and keeps one tag per entry when there are several entries', () => {
    const first = entry(0, { price: '100', stops: [stop('a', 'price', '95', '50'), stop('b', 'price', '90', '50')], targets: [] })
    const second = entry(1, { price: '98', stops: [stop('c', 'price', '93')], targets: [] })
    expect(planOverlays([first, second], first.id, 'aggregate', 'long').map(o => o.label))
      .toEqual(['E1', 'E1 SL1', 'E1 SL2', 'E2', 'E2 SL'])
  })

  it('keeps other entries in aggregate and hides them in the selected view', () => {
    const first = entry(0, { price: '100' })
    const second = entry(1, { price: '90' })
    const aggregate = planOverlays([first, second], second.id, 'aggregate', 'long')
    expect(aggregate.map(o => [o.label, o.emphasis, o.draggable])).toEqual([['E1', 'normal', true], ['E2', 'selected', true]])
    expect(planOverlays([first, second], second.id, 'selected', 'long').map(o => o.label)).toEqual(['E2'])
  })
})

describe('dragging planned levels', () => {
  it('writes prices with five significant figures and no exponent', () => {
    expect(formatDraggedPrice(65432.123)).toBe('65432')
    expect(formatDraggedPrice(123456.7)).toBe('123457')
    expect(formatDraggedPrice(1.234567)).toBe('1.2346')
    expect(formatDraggedPrice(0.0000123456)).toBe('0.00001235')
  })
  it('rounds chart prices to the instrument tick when the venue has one', () => {
    expect(formatDraggedPrice(0.094607, 0.00001)).toBe('0.09461')
    expect(formatDraggedPrice(83.3423, 0.01)).toBe('83.34')
    expect(formatDraggedPrice(101.26, 0.5)).toBe('101.5')
    expect(formatDraggedPrice(86331.27, 0.1)).toBe('86331.3')
    expect(formatDraggedPrice(65432.123, null)).toBe('65432')
  })

  it('stores dragged stops and targets as prices and leaves other entries untouched', () => {
    const first = entry(0, {
      price: '100', stops: [stop('s', 'percent', '5')],
      targets: [{ ...createExit('100'), id: 't1', unit: 'percent', value: '20' }],
    })
    const other = entry(1, { price: '80' })
    const overlays = planOverlays([first, other], first.id, 'aggregate', 'long')
    const stopId = overlays.find(o => o.kind === 'stop' && o.label === 'E1 SL')!.id
    const targetId = overlays.find(o => o.kind === 'target')!.id
    const entryId = overlays.find(o => o.label === 'E1')!.id

    let entries = applyLevelDrag([first, other], stopId, 92.5)
    expect(entries[0]!.stops[0]).toEqual(stop('s', 'price', '92.5'))
    expect(entries[1]).toBe(other)
    expect(entries[0]!.targets).toEqual(first.targets)
    entries = applyLevelDrag(entries, targetId, 131.234)
    expect(entries[0]!.targets[0]).toMatchObject({ unit: 'price', value: '131.23', share: '100' })
    entries = applyLevelDrag(entries, entryId, 101.11)
    expect(entries[0]!.price).toBe('101.11')
    expect(entries[0]!.stops[0]!.value).toBe('92.5')
    // Later entry-price or leverage changes must not move a chart-chosen price.
    expect(planOverlays(entries, first.id, 'aggregate', 'long', 5).find(o => o.id === stopId)!.price).toBe(92.5)
    expect(planOverlays(entries, first.id, 'aggregate', 'long', 5).find(o => o.id === targetId)!.price).toBe(131.23)
  })

  it('stores the actual dragged price across the entry, leaving wrong-side checks to plan validation', () => {
    const first = entry(0, { price: '100', stops: [stop('s', 'percent', '5')] })
    const stopId = planOverlays([first], first.id, 'aggregate', 'short').find(o => o.kind === 'stop')!.id
    expect(applyLevelDrag([first], stopId, 98)[0]!.stops[0]).toEqual(stop('s', 'price', '98'))
    expect(applyLevelDrag([first], stopId, 110)[0]!.stops[0]).toEqual(stop('s', 'price', '110'))
  })

  it('ignores unknown ids and invalid prices', () => {
    const first = entry(0, { price: '100' })
    expect(parseOverlayId('nonsense')).toBeNull()
    expect(applyLevelDrag([first], 'nonsense', 10)[0]).toBe(first)
    for (const price of [Number.NaN, Infinity, 0, -1])
      expect(applyLevelDrag([first], `${first.id}|entry`, price)[0]).toBe(first)
  })
})

describe('average planned entry', () => {
  it('weights entry prices by quantity share in the aggregate view only', () => {
    const first = entry(0, { price: '100', share: '60' })
    const second = entry(1, { price: '90', share: '40' })
    expect(averageEntryPrice([first, second])).toBeCloseTo(96)
    const average = planOverlays([first, second], first.id, 'aggregate', 'long').find(o => o.id === averageOverlayId)!
    expect(average).toMatchObject({ label: 'AVG', kind: 'reference', draggable: false })
    expect(average.price).toBeCloseTo(96)
    expect(parseOverlayId(average.id)).toBeNull()
    expect(planOverlays([first, second], first.id, 'selected', 'long').some(o => o.id === averageOverlayId)).toBe(false)
  })

  it('normalizes over contributing entries and needs at least two', () => {
    const priced = entry(0, { price: '100', share: '30' })
    const other = entry(1, { price: '80', share: '10' })
    const unpriced = entry(2, { price: '', share: '60' })
    const unshared = entry(3, { price: '50', share: '' })
    expect(averageEntryPrice([priced, other, unpriced, unshared])).toBeCloseTo(95)
    expect(averageEntryPrice([priced, unpriced])).toBeNull()
    expect(averageEntryPrice([priced, entry(1, { price: '90', share: '0' })])).toBeNull()
  })
})

describe('chart level editing helpers', () => {
  it('adds targets beyond the entry and stops on the risk side, as prices, filling blank ones first', () => {
    const base = entry(0, { price: '100', stops: [stop('s', 'percent', '')], targets: [] })
    expect(addChartExit(base, 'target', 'long')!.targets[0]).toMatchObject({ unit: 'price', value: '102', share: '100' })
    expect(addChartExit(base, 'target', 'short')!.targets[0]!.value).toBe('98')
    const withStop = addChartExit(base, 'stop', 'long')!
    expect(withStop.stops).toEqual([stop('s', 'price', '98')])
    expect(addChartExit(base, 'stop', 'short')!.stops[0]!.value).toBe('102')
    // A further stop goes a step beyond and does not take a share by itself.
    expect(addChartExit(withStop, 'stop', 'long')!.stops.map(s => [s.value, s.share])).toEqual([['98', '100'], ['96', '']])
    expect(addChartExit({ ...base, price: '' }, 'target', 'long')).toBeNull()
  })

  it('places a level picked on the chart for the entry, a stop or a target', () => {
    const base = entry(0, { price: '', stops: [stop('s', 'price', '')], targets: [] })
    expect(placeLevel(base, 'entry', 84541.37).price).toBe('84541')
    expect(placeLevel(base, 'stop', 83000).stops).toEqual([stop('s', 'price', '83000')])
    const twice = placeLevel(placeLevel(base, 'target', 86000), 'target', 87000)
    expect(twice.targets.map(t => [t.value, t.share])).toEqual([['86000', '100'], ['87000', '']])
    expect(placeLevel(base, 'stop', Number.NaN)).toBe(base)
  })

  it('removes and re-shares stops and targets and reports plotted prices', () => {
    const first = entry(0, { price: '100', stops: [stop('s1', 'price', '95', '50'), stop('s2', 'price', '90', '50')],
      targets: [{ ...createExit('50'), id: 'a', value: '110' }, { ...createExit('50'), id: 'b', unit: 'percent', value: '5' }] })
    expect(removeExit(first, 'target', 'a').targets.map(t => t.id)).toEqual(['b'])
    expect(removeExit(first, 'stop', 's1').stops.map(t => t.id)).toEqual(['s2'])
    expect(setExitShare(first, 'target', 'b', '70').targets[1]!.share).toBe('70')
    expect(setExitShare(first, 'stop', 's2', '30').stops[1]!.share).toBe('30')
    expect(overlayPrice(first, `${first.id}|stop|s2`, 'long')).toBe(90)
    expect(overlayPrice(first, `${first.id}|target|b`, 'long')).toBeCloseTo(105)
    expect(overlayPrice(first, `${first.id}|entry`, 'long')).toBe(100)
    expect(overlayPrice(first, 'other|entry', 'long')).toBeNull()
  })

  it('reapplies only the fields an edit changed', () => {
    const before = entry(0, { price: '100', share: '50' })
    const after = { ...before, price: '101' }
    const sideEdited = { ...after, share: '70' }
    expect(applyEntryEdit(sideEdited, after, before)).toEqual({ ...sideEdited, price: '100' })
    expect(applyEntryEdit({ ...before, share: '70' }, before, after)).toEqual({ ...before, share: '70', price: '101' })
  })

  it('reverts one chart target edit without touching sibling targets edited later', () => {
    const tp1 = { ...createExit('50'), id: 'tp1', value: '90000' }
    const tp2 = { ...createExit('50'), id: 'tp2', value: '92000' }
    const before = entry(0, { price: '85000', targets: [tp1, tp2] })
    const chartEdit = { ...before, targets: [{ ...tp1, value: '91000' }, tp2] }
    const sidebar = { ...chartEdit, targets: [chartEdit.targets[0]!, { ...tp2, value: '93000' }] }
    const undone = applyEntryEdit(sidebar, chartEdit, before)
    expect(undone.targets.map(t => t.value)).toEqual(['90000', '93000'])
    const redone = applyEntryEdit(undone, before, chartEdit)
    expect(redone.targets.map(t => t.value)).toEqual(['91000', '93000'])
  })

  it('undoes target additions and removals by ID and keeps targets added elsewhere', () => {
    const tp1 = { ...createExit('100'), id: 'tp1', value: '110' }
    const before = entry(0, { price: '100', targets: [tp1] })
    const added = { ...before, targets: [tp1, { ...createExit(), id: 'tp2', value: '104' }] }
    const sideAdded = { ...added, targets: [...added.targets, { ...createExit(), id: 'tp3', value: '120' }] }
    expect(applyEntryEdit(sideAdded, added, before).targets.map(t => t.id)).toEqual(['tp1', 'tp3'])
    const removed = { ...before, targets: [] }
    const sideAfterRemove = { ...removed, targets: [{ ...createExit(), id: 'tp9', value: '130' }] }
    expect(applyEntryEdit(sideAfterRemove, removed, before).targets.map(t => t.id)).toEqual(['tp1', 'tp9'])
  })

  it('reverts only the changed stop sub-field and keeps stops added elsewhere', () => {
    const before = entry(0, { price: '100', stops: [stop('s', 'price', '95')] })
    const chartEdit = { ...before, stops: [{ ...before.stops[0]!, value: '94' }] }
    const sideEdit = { ...chartEdit, share: '40', stops: [...chartEdit.stops, stop('s2', 'price', '90', '')] }
    expect(applyEntryEdit(sideEdit, chartEdit, before)).toEqual({ ...sideEdit, stops: [stop('s', 'price', '95'), stop('s2', 'price', '90', '')] })
  })
})

describe('plan text written by level helpers', () => {
  it('rescales percentage levels for a leverage change without writing an exponent', () => {
    const before = entry(0, { price: '100', stops: [stop('s', 'percent', '5')], targets: [stop('t', 'percent', '10000000000000000000000')] })
    const [after] = keepPercentLevelPrices([before], 1, 10)
    expect(after!.stops[0]!.value).toBe('50')
    // (1e22 × 10).toFixed(4) is "1e+23".
    expect(after!.targets[0]!.value).toBe('100000000000000000000000')
  })

  it('places dragged prices as plain decimals at any magnitude', () => {
    expect(formatDraggedPrice(0.000012345)).toBe('0.00001234')
    expect(formatDraggedPrice(2e21)).toBe('2000000000000000000000')
  })
})
