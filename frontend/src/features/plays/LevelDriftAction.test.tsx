import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { MonitorDiscrepancy } from '@/api/monitor'
import type { ExecutionOrder, OrderLink, PlayExecution } from '@/api/plays'
import { ApiError } from '@/api/errors'
import { monitorFixture } from '@/test/monitor-fixture'
import { createDraft } from './draft'
import { ExecutionPanel } from './ExecutionPanel'
import { levelDrifts } from './execution'
import { LevelDriftAction } from './LevelDriftAction'

const base = createDraft().entries[0]!
const entries = [{ ...base, id: 'e1', name: 'Entry 1', price: '100', stops: [{ id: 's1', unit: 'price' as const, value: '95', share: '100' }],
  targets: [{ id: 't1', unit: 'price' as const, value: '110', share: '100' }] }]
const order = (orderId: string, overrides: Partial<ExecutionOrder> = {}): ExecutionOrder => ({
  orderId, side: 'sell', orderType: 'Stop Market', limitPrice: '100', triggerPrice: '101', reduceOnly: true, isPositionTpsl: false,
  originalSize: '1', remainingSize: '1', placedAtUtc: '2026-10-19T10:00:00Z', status: 'open', venueStatus: 'open',
  statusAtUtc: '2026-10-19T10:00:00Z', ...overrides,
})
const stopLink: OrderLink = { id: '00000000-0000-4000-8000-0000000000b1', role: 'stop', entryId: 'e1', levelId: 's1', state: 'linked',
  source: 'automatic', order: order('s-order'), filledQuantity: '0', fills: [] }
const targetLink: OrderLink = { ...stopLink, id: '00000000-0000-4000-8000-0000000000b2', role: 'target', levelId: 't1',
  order: order('t-order', { orderType: 'Limit', limitPrice: '110', triggerPrice: null }) }
const drift = (levels: unknown[], overrides: Partial<MonitorDiscrepancy> = {}): MonitorDiscrepancy => ({
  id: '00000000-0000-4000-8000-0000000000c1', accountId: '00000000-0000-4000-8000-0000000000c2', playId: '00000000-0000-4000-8000-000000000001',
  contractId: 'BTC', kind: 'level.drift', severity: 'info', state: 'open', openedAtUtc: '2026-10-19T10:10:00Z', confirmedAtUtc: '2026-10-19T10:11:00Z',
  resolvedAtUtc: null, dismissedAtUtc: null, note: null, details: { coverage: {}, levels }, outcome: null, unverifiableAtUtc: null, ...overrides,
})
const context = { direction: 'long' as const, leverage: 3, tick: null }
const drifted = drift([
  { orderId: 's-order', actualPrice: '101', plannedPrice: '95', remainingQuantity: '1' },
  { orderId: 't-order', actualPrice: '110', plannedPrice: '110.0', remainingQuantity: '1' },
])

describe('level drift', () => {
  it('names the drifted linked stops and targets with the price their order rests at', () => {
    expect(levelDrifts(drifted, [stopLink, targetLink], entries, context))
      .toEqual([{ link: stopLink, observed: '101', planned: '95', applied: false, percent: null }])
    // Not drift, entries and exits, orders that no longer rest, and malformed rows are not rebound.
    expect(levelDrifts({ ...drifted, kind: 'protection.short' }, [stopLink], entries, context)).toEqual([])
    expect(levelDrifts(drifted, [{ ...stopLink, role: 'entry', levelId: null }], entries, context)).toEqual([])
    expect(levelDrifts(drifted, [{ ...stopLink, order: order('s-order', { status: 'canceled' }) }], entries, context)).toEqual([])
    expect(levelDrifts(drift([{ orderId: 's-order' }, 'row', null]), [stopLink], entries, context)).toEqual([])
    // Once the plan holds the order's price, the record only waits for the monitor's next check.
    const moved = [{ ...entries[0]!, stops: [{ ...entries[0]!.stops[0]!, value: '101.0' }] }]
    expect(levelDrifts(drifted, [stopLink], moved, context)).toEqual([{ link: stopLink, observed: '101', planned: '95', applied: true, percent: null }])
  })

  it('matches prices as the monitor does, so levels that did not drift are not offered', () => {
    // A 10% stop at 3× on an entry at 100 resolves to 96.666…; an order at 96.667 matches it within 0.001 (fifth figure).
    const percentStop = [{ ...entries[0]!, stops: [{ id: 's1', unit: 'percent' as const, value: '10', share: '100' }] }]
    const atPercent = { ...stopLink, order: order('s-order', { triggerPrice: '96.667' }) }
    const both = drift([
      { orderId: 's-order', actualPrice: '96.667', plannedPrice: '96.666666666666666666666666667', remainingQuantity: '1' },
      { orderId: 't-order', actualPrice: '112', plannedPrice: '110', remainingQuantity: '1' },
    ])
    expect(levelDrifts(both, [atPercent, { ...targetLink, order: order('t-order', { orderType: 'Limit', limitPrice: '112', triggerPrice: null }) }],
      percentStop, context).map(item => item.link.role)).toEqual(['target'])
    // A coarser tick widens the match, as on the server: 101 against 100 matches with a tick of 5, not with 0.01.
    const near = drift([{ orderId: 's-order', actualPrice: '101', plannedPrice: '100', remainingQuantity: '1' }])
    expect(levelDrifts(near, [stopLink], entries, { ...context, tick: 5 })).toEqual([])
    expect(levelDrifts(near, [stopLink], entries, context)).toHaveLength(1)
    // A drifted percentage level is marked: a rebind turns it into a price.
    const away = { ...stopLink, order: order('s-order', { triggerPrice: '95' }) }
    expect(levelDrifts(drift([{ orderId: 's-order', actualPrice: '95', plannedPrice: '96.666666666666666666666666667', remainingQuantity: '1' }]),
      [away], percentStop, context)).toEqual([{ link: away, observed: '95', planned: '96.666666666666666666666666667', applied: false, percent: '10' }])
  })

  it('says when a rebind turns a percentage into a price', async () => {
    const percentStop = [{ ...entries[0]!, stops: [{ id: 's1', unit: 'percent' as const, value: '5', share: '100' }] }]
    const away = { ...stopLink, order: order('s-order', { triggerPrice: '95' }) }
    const onRebind = vi.fn(() => Promise.resolve())
    const user = userEvent.setup()
    render(<LevelDriftAction discrepancy={drift([{ orderId: 's-order', actualPrice: '95', plannedPrice: '98.333333333333333333333333333', remainingQuantity: '1' }])}
      links={[away]} entries={percentStop} context={context} origin="planned" playId="p1" version={2} onRebind={onRebind} />)
    await user.type(screen.getByRole('textbox', { name: /Reason to move/ }), 'Moved at the venue')
    await user.click(screen.getByRole('button', { name: 'Use 95 (changes 5 % to a price) for Entry 1 stop' }))
    expect(onRebind).toHaveBeenCalledWith(stopLink.id, { expectedVersion: 2, orderId: 's-order', observedPrice: '95', reason: 'Moved at the venue' })
  })

  it('moves the level with a reason, sending the version and the observed price', async () => {
    const onRebind = vi.fn(() => Promise.resolve())
    const user = userEvent.setup()
    render(<LevelDriftAction discrepancy={drifted} links={[stopLink, targetLink]} entries={entries} context={context} origin="planned" playId="p1" version={7} onRebind={onRebind} />)
    expect(screen.getByText('Entry 1 stop: order s-order rests at 101; the plan said 95.')).toBeInTheDocument()
    const use = screen.getByRole('button', { name: 'Use 101 for Entry 1 stop' })
    expect(use).toBeDisabled()
    await user.type(screen.getByRole('textbox', { name: 'Reason to move Entry 1 stop to 101' }), 'Trailed at the venue')
    await user.click(use)
    expect(onRebind).toHaveBeenCalledWith(stopLink.id, { expectedVersion: 7, orderId: 's-order', observedPrice: '101', reason: 'Trailed at the venue' })
    expect(screen.getByRole('textbox', { name: 'Reason to move Entry 1 stop to 101' })).toHaveValue('')
  })

  it('shows the refusal and stays blocked while plan edits are unsaved', async () => {
    const onRebind = vi.fn(() => Promise.reject(new ApiError('http', 'The order\'s price changed since it was observed. Check again before rebinding.', 409)))
    const user = userEvent.setup()
    const view = render(<LevelDriftAction discrepancy={drifted} links={[stopLink]} entries={entries} context={context} origin="planned" playId="p1" version={7}
      blocked="Save or discard your plan changes first." onRebind={onRebind} />)
    await user.type(screen.getByRole('textbox', { name: /Reason to move/ }), 'Trailed')
    expect(screen.getByRole('button', { name: 'Use 101 for Entry 1 stop' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Use 101 for Entry 1 stop' })).toHaveAttribute('title', 'Save or discard your plan changes first.')
    view.rerender(<LevelDriftAction discrepancy={drifted} links={[stopLink]} entries={entries} context={context} origin="planned" playId="p1" version={7} onRebind={onRebind} />)
    await user.click(screen.getByRole('button', { name: 'Use 101 for Entry 1 stop' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The order\'s price changed since it was observed.')
    // A resolved record offers nothing.
    view.rerender(<LevelDriftAction discrepancy={{ ...drifted, state: 'resolved' }} links={[stopLink]} entries={entries} context={context} origin="planned" playId="p1"
      version={7} onRebind={onRebind} />)
    expect(screen.queryByRole('button', { name: /Use 101/ })).not.toBeInTheDocument()
  })

  it('sits in the drift record of the Execution tab, only for editable plays', () => {
    const execution: PlayExecution = {
      playId: '00000000-0000-4000-8000-000000000001', status: 'open', tracked: true, reason: null, checkedAtUtc: '2026-10-19T10:11:00Z',
      totals: { enteredQuantity: '1', exitedQuantity: '0', openQuantity: '1', closedPnlUsd: '0', closedPnlBasis: 'gross', fees: [] },
      entries: [{ entryId: 'e1', filledQuantity: '1', averageFillPrice: '100', restingOrders: 0 }], links: [stopLink], suggestions: [],
      unlinkedOrders: [], notice: 'Venue facts.', issues: [], monitor: monitorFixture, discrepancies: [drifted], watches: [],
    }
    const props = { execution, error: null, busy: false, entries, direction: 'long' as const, onCheck: vi.fn(), onLink: vi.fn(), onUnlink: vi.fn(),
      onDismiss: vi.fn(() => Promise.resolve()), onRebind: vi.fn(() => Promise.resolve()), version: 3 }
    const view = render(<ExecutionPanel {...props} />)
    const record = within(screen.getByRole('region', { name: 'Monitor discrepancies' }))
    expect(record.getByRole('button', { name: 'Use 101 for Entry 1 stop' })).toBeInTheDocument()
    expect(record.getByRole('button', { name: 'Dismiss' })).toBeInTheDocument()
    view.rerender(<ExecutionPanel {...props} execution={{ ...execution, status: 'closed' }} />)
    expect(screen.queryByRole('button', { name: /Use 101/ })).not.toBeInTheDocument()
  })
})
