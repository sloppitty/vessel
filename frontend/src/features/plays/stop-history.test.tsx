import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { render } from '@/test/render'
import type { PlanExit, PlanRevision, PlayPlan } from '@/api/plays'
import { createDraft } from './draft'
import { originalStopOverlays, stopHistory, stopsMoved, stopText } from './stop-history'
import { StopChanges } from './StopChanges'

const base = createDraft()
const stop = (id: string, value: string, unit: PlanExit['unit'] = 'price'): PlanExit => ({ id, unit, value, share: '100' })
const plan = (stops: PlanExit[], extra: Partial<PlayPlan> = {}, price = '100'): PlayPlan => ({
  direction: 'long', sizingMode: 'margin', size: '100', leverage: '5', budgetOverride: null,
  notes: { thesis: '', invalidation: '', strategy: '', evidence: '' },
  entries: [{ ...base.entries[0]!, id: 'e1', name: 'Breakout', price, stops, targets: [] }], ...extra,
})
const revision = (number: number, p: PlayPlan): PlanRevision => ({ number, status: 'open', reason: number === 1 ? 'Planned' : 'Moved', createdAtUtc: `2026-10-0${number}T00:00:00Z`, plan: p })
const label = (_: string, index: number, count: number) => `SL${count > 1 ? index + 1 : ''}`

describe('stop history of a closed Play', () => {
  it('sets the first planned stops beside the final ones and counts the revisions that moved them', () => {
    const final = plan([stop('s1', '100')])
    const history = stopHistory([revision(3, final), revision(1, plan([stop('s1', '95')])), revision(2, plan([stop('s1', '98')]))], final)
    expect(history).toHaveLength(1)
    expect(history[0]!.fromRevision).toBe(1)
    expect(history[0]!.moves).toBe(2)
    expect(history[0]!.original.map(stopText)).toEqual(['95'])
    expect(history[0]!.final.map(stopText)).toEqual(['100'])
    expect(stopsMoved(history)).toBe(true)
  })

  it('does not count revisions that changed other parts of the plan, or the same price in another unit', () => {
    // 5 % return at 5× leverage is a 1 % price move: 99 for a long entry at 100.
    const final = plan([stop('s1', '99')], { notes: { thesis: 'changed', invalidation: '', strategy: '', evidence: '' } })
    const history = stopHistory([revision(1, plan([stop('s1', '5', 'percent')])), revision(2, final)], final)
    expect(history[0]!.moves).toBe(0)
    expect(stopsMoved(history)).toBe(false)
    expect(originalStopOverlays(history, label)).toEqual([])
    expect(stopText(history[0]!.original[0]!)).toBe('5 % (99)')
  })

  it('draws an original stop the entry no longer has as a faint, fixed reference line', () => {
    const final = plan([stop('s2', '100')])
    const history = stopHistory([revision(1, plan([stop('s1', '95')])), revision(2, final)], final)
    expect(originalStopOverlays(history, label)).toEqual([{
      id: 'original-stop:e1:s1', label: 'SL orig', price: 95, color: 'var(--negative)', kind: 'reference', emphasis: 'normal',
      draggable: false, unconfirmed: true,
    }])
    // Hidden with its entry, and never for a stop the final plan still has at the same price.
    expect(originalStopOverlays(history, label, () => false)).toEqual([])
    const kept = plan([stop('s1', '95'), stop('s2', '100')])
    const both = stopHistory([revision(1, plan([stop('s1', '95')])), revision(2, kept)], kept)
    expect(both[0]!.moves).toBe(1)
    expect(originalStopOverlays(both, label)).toEqual([])
  })

  it('starts an entry added later from the revision that added it, and keeps unpriced stops as text', () => {
    const second = { ...base.entries[0]!, id: 'e2', name: 'Add', price: '', stops: [stop('x', '10', 'percent')], targets: [] }
    const added = plan([stop('s1', '95')], { entries: [plan([stop('s1', '95')]).entries[0]!, second] })
    const final = { ...added, entries: [added.entries[0]!, { ...second, stops: [stop('x', '20', 'percent')] }] }
    const history = stopHistory([revision(1, plan([stop('s1', '95')])), revision(2, added), revision(3, final)], final)
    expect(history.map(entry => [entry.entryId, entry.fromRevision, entry.moves])).toEqual([['e1', 1, 0], ['e2', 2, 1]])
    expect(stopText(history[1]!.original[0]!)).toBe('10 %')
    expect(originalStopOverlays(history, label)).toEqual([])
  })

  it('leaves out entries without any revision', () => {
    expect(stopHistory([], plan([stop('s1', '95')]))).toEqual([])
  })

  it('lists only entries whose stops moved, with original and final stops', () => {
    const final = plan([stop('s1', '101')])
    const history = stopHistory([revision(1, plan([stop('s1', '95')])), revision(2, plan([stop('s1', '100')])), revision(3, final)], final)
    const { rerender } = render(<StopChanges history={history} entries={final.entries} />)
    const item = within(screen.getByRole('region', { name: 'Stop changes' })).getByRole('listitem')
    expect(item).toHaveTextContent('Breakout')
    expect(item).toHaveTextContent('Moved 2 times')
    expect(item).toHaveTextContent('Original 95 (revision 1)')
    expect(item).toHaveTextContent('Final 101')
    rerender(<StopChanges history={stopHistory([revision(1, final)], final)} entries={final.entries} />)
    expect(screen.queryByRole('region', { name: 'Stop changes' })).toBeNull()
  })
})
