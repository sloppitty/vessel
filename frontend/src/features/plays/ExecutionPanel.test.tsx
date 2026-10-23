import { coverageAs, coverageFixture, missingStopFixture, monitorFixture } from '@/test/monitor-fixture'
import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { ExecutionOrder, PlayExecution } from '@/api/plays'
import type { PlayFunding } from '@/api/play-funding'
import { ExecutionPanel } from './ExecutionPanel'
import { createDraft } from './draft'
import { PlayJournal } from './WorkspacePanels'
import { fillSummary, suggestionGroups } from './execution'
import { restoreZone } from '@/test/zone'

const entry = { ...createDraft().entries[0]!, id: 'e1', name: 'Entry 1', price: '100' }
const entries = [{ ...entry, stops: [{ ...entry.stops[0]!, id: 's1', value: '95' }], targets: [{ ...entry.targets[0]!, id: 't1', value: '110' }] }]
const order = (orderId: string, overrides: Partial<ExecutionOrder> = {}): ExecutionOrder => ({
  orderId, side: 'buy', orderType: 'Limit', limitPrice: '100', triggerPrice: null, reduceOnly: false, isPositionTpsl: false,
  originalSize: '0.5', remainingSize: '0.5', placedAtUtc: '2026-10-03T10:00:00Z', status: 'open', venueStatus: 'open',
  statusAtUtc: '2026-10-03T10:00:00Z', ...overrides,
})
const execution: PlayExecution = {
  playId: '00000000-0000-4000-8000-000000000001', status: 'open', tracked: true, reason: null, checkedAtUtc: '2026-10-03T10:05:00Z',
  totals: { enteredQuantity: '0.5', exitedQuantity: '0', openQuantity: '0.5', closedPnlUsd: '0', closedPnlBasis: 'gross', fees: [{ token: 'USDC', amount: '0.02' }] },
  entries: [{ entryId: 'e1', filledQuantity: '0.5', averageFillPrice: '100', restingOrders: 0 }],
  links: [{ id: '00000000-0000-4000-8000-0000000000a1', role: 'entry', entryId: 'e1', levelId: null, state: 'linked', source: 'automatic',
    order: order('11', { status: 'filled' }), filledQuantity: '0.5',
    fills: [{ sourceFillId: 'f1', direction: 'Open Long', price: '100', quantity: '0.5', fee: '0.02', feeToken: 'USDC', closedPnlUsd: '0', side: 'buy', positionEffect: 'open', pnlBasis: 'gross', occurredAtUtc: '2026-10-03T10:01:00Z' }] }],
  suggestions: [{ id: '00000000-0000-4000-8000-0000000000a2', role: 'stop', entryId: 'e1', levelId: 's1', state: 'suggested', source: 'automatic',
    order: order('12', { side: 'sell', orderType: 'Stop Market', triggerPrice: '95', reduceOnly: true }), filledQuantity: '0', fills: [] }],
  unlinkedOrders: [order('13', { side: 'sell', limitPrice: '110', reduceOnly: true })],
  notice: 'Orders and fills come from the venue.',
  issues: [], monitor: monitorFixture, discrepancies: [], watches: [],
}

describe('execution panel', () => {
  it('marks figures kept from the last read and holds confirmations until a fresh one (phase 1, 1.1a)', () => {
    const props = { execution, error: null, busy: false, entries, direction: 'long' as const, onCheck: vi.fn(), onLink: vi.fn(), onUnlink: vi.fn() }
    const view = render(<ExecutionPanel {...props} stale />)
    expect(screen.getByText(/These figures are from the last read/)).toBeInTheDocument()
    const confirm = screen.getByRole('region', { name: 'Orders to confirm' })
    expect(within(confirm).getByRole('button', { name: /^Link as/ })).toBeDisabled()
    expect(within(confirm).getByRole('button', { name: 'Not this play' })).toBeDisabled()
    expect(within(screen.getByRole('region', { name: 'Other orders on this instrument' })).getByRole('button', { name: 'Link' })).toBeDisabled()
    // An existing link can still be undone.
    expect(screen.getByRole('button', { name: 'Unlink Entry 1 order' })).toBeEnabled()
    view.rerender(<ExecutionPanel {...props} />)
    expect(screen.queryByText(/These figures are from the last read/)).not.toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Orders to confirm' })).getByRole('button', { name: /^Link as/ })).toBeEnabled()
  })

  it('heads the monitor with this Play\'s own stop coverage, never Monitored beside an unknown (Phase 0, 0.4c)', () => {
    const props = { error: null, busy: false, entries, direction: 'long' as const, onCheck: vi.fn(), onLink: vi.fn(), onUnlink: vi.fn() }
    const monitor = { ...monitorFixture, coverage: [coverageAs('unknown', 'opposing-plays', execution.playId),
      { ...coverageFixture, playId: '00000000-0000-4000-8000-0000000000f2' }] }
    const view = render(<ExecutionPanel {...props} execution={{ ...execution, monitor }} />)
    const panel = () => screen.getByRole('region', { name: 'Background monitor' })
    expect(within(panel()).queryByText('Monitored')).not.toBeInTheDocument()
    expect(within(panel()).getByText('Stop coverage unknown')).toBeInTheDocument()
    // Another Play's coverage on the account does not speak for this one: an Open Play without its own record is not
    // checked yet, never "Monitored" (after the migration, or once reconciliation opens it until the next check).
    view.rerender(<ExecutionPanel {...props} execution={{ ...execution, monitor: { ...monitor, coverage: [monitor.coverage[1]!] } }} />)
    expect(within(panel()).queryByText('Monitored')).not.toBeInTheDocument()
    expect(within(panel()).getByText('Stop coverage not checked yet')).toBeInTheDocument()
    view.rerender(<ExecutionPanel {...props} execution={{ ...execution, monitor: { ...monitor, coverage: [{ ...coverageFixture, playId: execution.playId }] } }} />)
    expect(within(panel()).getByText('Monitored')).toBeInTheDocument()
    // A Planned Play has no position to cover yet.
    view.rerender(<ExecutionPanel {...props} execution={{ ...execution, status: 'planned', monitor: monitorFixture }} />)
    expect(within(panel()).getByText('Monitored')).toBeInTheDocument()
    // The venue capability: without a current book the stop checks do not run, which the panel says.
    view.rerender(<ExecutionPanel {...props} execution={{ ...execution, status: 'planned', monitor: monitorFixture }} currentBookRead={false} />)
    expect(within(panel()).getByText('Current book not read')).toBeInTheDocument()
    expect(within(panel()).getByText(/Order-book checks do not run here/)).toBeInTheDocument()
  })

  it('shows the check timestamp only once, retaining the fallback and untracked reason', () => {
    const props = { execution, error: null, busy: false, entries, direction: 'long' as const, onCheck: vi.fn(), onLink: vi.fn(), onUnlink: vi.fn() }
    const view = render(<ExecutionPanel {...props} />)
    expect(screen.queryByText(/^Checked/)).not.toBeInTheDocument()
    expect(screen.getByText(/^Last success /)).toBeVisible()
    view.rerender(<ExecutionPanel {...props} monitor={null} />)
    expect(screen.getByText(/^Checked/)).toBeVisible()
    view.rerender(<ExecutionPanel {...props} execution={{ ...execution, tracked: false, reason: 'This venue does not track orders.' }} />)
    expect(screen.getByText('This venue does not track orders.')).toBeVisible()
  })

  it('says when the venue already took fees off the closed PnL', () => {
    const net = { ...execution, totals: { ...execution.totals, closedPnlBasis: 'net-of-fee' as const } }
    const { unmount } = render(<ExecutionPanel execution={execution} error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />)
    expect(screen.getByText('Venue closed PnL')).toBeInTheDocument()
    unmount()
    render(<ExecutionPanel execution={net} error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />)
    expect(screen.getByText('Venue closed PnL · after fees').parentElement).toHaveAttribute('title', expect.stringMatching(/fees already taken off/))
  })
  it('shows the scored venue net beside the Play-local result and flags a sign difference', () => {
    const closed: PlayExecution = { ...execution, status: 'closed', totals: { ...execution.totals, enteredQuantity: '1', exitedQuantity: '1', openQuantity: '0',
      closedPnlUsd: '3', venueNetUsd: '2.9', local: { netUsd: '-2.1', basis: 'executions', reason: null, signDiffers: true } } }
    const props = { error: null, busy: false, entries, direction: 'long' as const, onCheck: vi.fn(), onLink: vi.fn(), onUnlink: vi.fn() }
    const view = render(<ExecutionPanel execution={closed} {...props} />)
    const value = (term: string) => screen.getByText(term).closest('div')!.querySelector('dd')
    expect(value('Venue net')).toHaveTextContent(/^2\.9 USD$/)
    // Only what is true: a running figure while open, scored only once closed and settled, never for retrospective sizing.
    const venueTip = screen.getByRole('button', { name: 'About venue net' })
    expect(venueTip).toHaveAccessibleDescription(/so far while the play is open/)
    expect(venueTip).toHaveAccessibleDescription(/closed play whose fills settle it/)
    expect(venueTip).toHaveAccessibleDescription(/retrospective plays are not in sizing/)
    expect(venueTip).not.toHaveAccessibleDescription(/score this result/)
    expect(value('Play-local result')).toHaveTextContent(/^-2\.1 USD/)
    expect(screen.getByRole('button', { name: 'About the Play-local result' })).toHaveAccessibleDescription(/sell proceeds minus buy costs, less every trading fee/)
    expect(screen.getByRole('button', { name: 'Sign differs by basis' })).toBeInTheDocument()

    const reason = 'The play is not closed.'
    view.rerender(<ExecutionPanel execution={{ ...execution, totals: { ...execution.totals, venueNetUsd: null,
      local: { netUsd: null, basis: 'executions', reason, signDiffers: false } } }} {...props} />)
    expect(screen.getByRole('button', { name: 'Unknown Play-local result' })).toHaveAccessibleDescription(reason)
    expect(screen.getByRole('button', { name: 'Unknown venue net' })).toHaveAccessibleDescription(/listed stablecoins/)
    expect(screen.queryByRole('button', { name: 'Sign differs by basis' })).not.toBeInTheDocument()

    // Tiny results keep their exact strings: never rounded into a zero that reads as a scratch.
    view.rerender(<ExecutionPanel execution={{ ...closed, totals: { ...closed.totals, venueNetUsd: '-0.00001',
      local: { netUsd: '0.00001', basis: 'executions', reason: null, signDiffers: true } } }} {...props} />)
    expect(value('Venue net')).toHaveTextContent(/^-0\.00001 USD$/)
    expect(value('Play-local result')).toHaveTextContent(/^0\.00001 USD/)

    // Servers before October 19, 2026 send neither figure.
    view.rerender(<ExecutionPanel execution={execution} {...props} />)
    expect(screen.queryByText('Venue net')).not.toBeInTheDocument()
    expect(screen.queryByText('Play-local result')).not.toBeInTheDocument()
  })

  it('shows settled funding beside the results in each state and changes neither result (phase 2, 9b)', () => {
    const closed: PlayExecution = { ...execution, status: 'closed', totals: { ...execution.totals, enteredQuantity: '1', exitedQuantity: '1', openQuantity: '0',
      closedPnlUsd: '3', venueNetUsd: '2.9', local: { netUsd: '2.8', basis: 'executions', reason: null, signDiffers: false } } }
    const props = { error: null, busy: false, entries, direction: 'long' as const, onCheck: vi.fn(), onLink: vi.fn(), onUnlink: vi.fn() }
    const value = (term: string) => screen.getByText(term).closest('div')!.querySelector('dd')!
    const results = () => {
      expect(value('Venue net')).toHaveTextContent(/^2\.9 USD$/)
      expect(value('Play-local result')).toHaveTextContent(/^2\.8 USD$/)
      expect(value('Venue closed PnL')).toHaveTextContent(/^3 USD$/)
    }
    const none = { amountUsd: null, asset: null, paymentCount: null, fromUtc: null, toUtc: null }
    const sole: PlayFunding = { state: 'sole', reason: null, amountUsd: '-0.1234567890123456789012345678', asset: 'USDC', paymentCount: 2,
      fromUtc: '2026-10-03T10:01:00Z', toUtc: '2026-10-03T13:01:00Z' }
    const view = render(<ExecutionPanel execution={{ ...closed, funding: sole }} {...props} />)
    // The exact sum, its count and its window, never rounded.
    expect(value('Settled funding')).toHaveTextContent(/^-0\.1234567890123456789012345678 USDC 2 settlements · .+ – .+$/)
    expect(value('Settled funding').querySelectorAll('time')).toHaveLength(2)
    expect(screen.getByRole('button', { name: 'About settled funding' })).toHaveAccessibleDescription(/this play alone held the position.*never added to either\. Whether venue-reported P&L includes funding is unverified\./)
    results()

    view.rerender(<ExecutionPanel execution={{ ...closed, funding: { state: 'shared', reason: 'other-play', ...none } }} {...props} />)
    expect(screen.getByRole('button', { name: 'Shared, not allocated settled funding' }))
      .toHaveAccessibleDescription('Another play traded this position during the holding window.')
    expect(within(value('Settled funding')).getByRole('button')).toHaveTextContent(/^Shared, not allocated$/)
    results()

    view.rerender(<ExecutionPanel execution={{ ...closed, funding: { state: 'incomplete', reason: 'flat-start-unproven', ...none } }} {...props} />)
    expect(screen.getByRole('button', { name: 'Coverage incomplete settled funding' }))
      .toHaveAccessibleDescription('Nothing on record proves the position was flat before the first entry. Missing funding is not zero.')
    expect(within(value('Settled funding')).getByRole('button')).toHaveTextContent(/^Coverage incomplete$/)
    results()

    view.rerender(<ExecutionPanel execution={{ ...closed, funding: { state: 'unavailable', reason: 'no-funding-read', ...none } }} {...props} />)
    expect(screen.getByRole('button', { name: 'Not available settled funding' })).toHaveAccessibleDescription('Settled funding is not read for this venue.')
    results()

    // No holding window (or an older server): no line at all.
    view.rerender(<ExecutionPanel execution={closed} {...props} />)
    expect(screen.queryByText('Settled funding')).not.toBeInTheDocument()
    results()
  })

  it('shows linked fills, asks about ambiguous orders and links others by hand', async () => {
    const onLink = vi.fn()
    const onUnlink = vi.fn()
    const onCheck = vi.fn()
    const user = userEvent.setup()
    render(<ExecutionPanel execution={execution} error={null} busy={false} entries={entries} direction="long" onCheck={onCheck} onLink={onLink} onUnlink={onUnlink} />)
    expect(screen.getByText('Still open').nextSibling).toHaveTextContent('0.5')
    expect(screen.getByText('0.02 USDC')).toBeInTheDocument()
    const linked = screen.getByRole('region', { name: 'Linked orders' })
    expect(within(linked).getByText('Entry 1')).toBeInTheDocument()
    expect(within(linked).getByText(/Buy limit at 100 · 0.5 · filled · filled 0.5 · linked automatically/)).toBeInTheDocument()
    expect(within(linked).getByText('Open Long 0.5 at 100')).toBeInTheDocument()
    expect(fillSummary([{ ...execution.links[0]!.fills[0]!, quantity: '1', price: '100' }, { ...execution.links[0]!.fills[0]!, quantity: '3', price: '104' }]))
      .toBe('Open Long · 2 fills, average 103')

    const confirm = screen.getByRole('region', { name: 'Orders to confirm' })
    // An older server says nothing about other Plays, so neither does the row.
    expect(within(confirm).queryByText(/another play/)).not.toBeInTheDocument()
    await user.click(within(confirm).getByRole('button', { name: 'Link as Entry 1 stop' }))
    expect(onLink).toHaveBeenCalledWith({ orderId: '12', role: 'stop', entryId: 'e1', levelId: 's1' })
    await user.click(within(confirm).getByRole('button', { name: 'Not this play' }))
    expect(onUnlink).toHaveBeenCalledWith('00000000-0000-4000-8000-0000000000a2')

    const other = screen.getByRole('region', { name: 'Other orders on this instrument' })
    const link = within(other).getByRole('button', { name: /Link/ })
    expect(link).toBeDisabled()
    const select = within(other).getByRole('combobox', { name: 'Level for order 13' })
    expect(within(select).getByRole('option', { name: 'Unplanned exit' })).toBeInTheDocument()
    // A sell cannot enter a long play, so only its exits are offered.
    expect(within(select).queryByRole('option', { name: 'Entry 1' })).not.toBeInTheDocument()
    await user.selectOptions(select, 'target|e1|t1')
    await user.click(link)
    expect(onLink).toHaveBeenLastCalledWith({ orderId: '13', role: 'target', entryId: 'e1', levelId: 't1' })

    await user.click(within(linked).getByRole('button', { name: 'Unlink Entry 1 order' }))
    expect(onUnlink).toHaveBeenLastCalledWith('00000000-0000-4000-8000-0000000000a1')
    await user.click(screen.getByRole('button', { name: 'Check' }))
    expect(onCheck).toHaveBeenCalledOnce()
  })

  it('offers only the roles an order can take and says why linked fills do not settle the play', () => {
    const issue = 'Order 51 is filled at the venue, but not all of its executions are retained yet.'
    render(<ExecutionPanel execution={{ ...execution, issues: [issue], unlinkedOrders: [order('21'), order('22', { reduceOnly: true }), order('23', { side: 'sell' })] }}
      error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />)
    expect(within(screen.getByRole('region', { name: 'Not reconciled' })).getByText(issue)).toBeInTheDocument()
    const options = (id: string) => within(screen.getByRole('combobox', { name: `Level for order ${id}` })).getAllByRole('option').map(option => option.textContent)
    expect(options('21')).toEqual(['Link as…', 'Entry 1'])
    expect(options('22')).toEqual(['Link as…'])
    expect(options('23')).toEqual(['Link as…', 'Entry 1 stop', 'Entry 1 target 1', 'Unplanned exit'])
  })

  it('lists orders placed before the Play was saved apart, for the owner to link (phase 2, item 4)', async () => {
    const user = userEvent.setup()
    const onLink = vi.fn().mockResolvedValue(undefined)
    render(<ExecutionPanel execution={{ ...execution, status: 'planned', links: [], suggestions: [], unlinkedOrders: [],
      earlierOrders: [order('31', { status: 'filled', placedAtUtc: '2026-10-03T09:58:00Z' })] }}
      error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={onLink} onUnlink={vi.fn()} />)
    const earlier = screen.getByRole('region', { name: 'Placed before this Play was saved' })
    expect(screen.queryByRole('region', { name: 'Other orders on this instrument' })).not.toBeInTheDocument()
    await user.selectOptions(within(earlier).getByRole('combobox', { name: 'Level for order 31' }), 'Entry 1')
    await user.click(within(earlier).getByRole('button', { name: 'Link' }))
    expect(onLink).toHaveBeenLastCalledWith({ orderId: '31', role: 'entry', entryId: 'e1' })
  })

  it('shows the venue liquidation price and mode, now and at the first fill (phase 2, 8a)', () => {
    render(<ExecutionPanel execution={{ ...execution,
      venuePosition: { signedQuantity: '0.5', entryPrice: '100', liquidationPrice: '82.5', marginMode: 'cross', observedAtUtc: '2026-10-03T10:05:00Z' },
      riskAtFirstFill: { signedQuantity: '0.5', entryPrice: '100', liquidationPrice: null, marginMode: 'isolated', observedAtUtc: '2026-10-03T10:01:30Z' } }}
      error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />)
    const row = screen.getByText(/^Venue liquidation/).closest('div')!
    expect(row).toHaveTextContent(/82\.5 \(cross\) read .+ · first read after entry not reported \(isolated\), .+/)
    // Sol round 1 on #41: each value says when it was read.
    expect(row.querySelectorAll('time')).toHaveLength(2)
  })

  it('keeps the first-fill context once the position is flat, and shows nothing without either', () => {
    const view = render(<ExecutionPanel execution={{ ...execution, venuePosition: null,
      riskAtFirstFill: { signedQuantity: '0.5', entryPrice: '100', liquidationPrice: '70', marginMode: 'isolated', observedAtUtc: '2026-10-03T10:01:30Z' } }}
      error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />)
    expect(screen.getByText(/^Venue liquidation/).closest('div')!).toHaveTextContent(/Flat now · first read after entry 70 \(isolated\)/)
    view.rerender(<ExecutionPanel execution={{ ...execution, venuePosition: null, riskAtFirstFill: null }} error={null} busy={false} entries={entries}
      direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />)
    expect(screen.queryByText(/^Venue liquidation/)).not.toBeInTheDocument()
  })

  it('offers a resting unlinked stop to answer a missing stop, never linking it by itself (phase 2, 8c)', async () => {
    const user = userEvent.setup()
    const onLink = vi.fn().mockResolvedValue(undefined)
    const stop = order('41', { side: 'sell', orderType: 'Stop Market', triggerPrice: '95', reduceOnly: true })
    const takeProfit = order('42', { side: 'sell', orderType: 'Take Profit Market', triggerPrice: '120', reduceOnly: true })
    render(<ExecutionPanel execution={{ ...execution, unlinkedOrders: [stop, takeProfit], discrepancies: [missingStopFixture] }}
      error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={onLink} onUnlink={vi.fn()} onDismiss={vi.fn()} />)
    const buttons = screen.getAllByRole('button', { name: /^Link this stop/ })
    // Only the stop mechanism on the exit side is offered; a take-profit never is.
    expect(buttons).toHaveLength(1)
    expect(buttons[0]).toHaveTextContent('Link this stop to Entry 1 stop')
    expect(onLink).not.toHaveBeenCalled()
    await user.click(buttons[0]!)
    expect(onLink).toHaveBeenLastCalledWith({ orderId: '41', role: 'stop', entryId: 'e1', levelId: 's1' })
  })

  it('offers it only for an open incident, for an order the server says can be a stop, and waits for unsaved edits (Sol round 1 on #43)', () => {
    const stop = order('41', { side: 'sell', orderType: 'Stop Market', triggerPrice: '95', reduceOnly: true })
    const contradicted = { ...order('43', { side: 'sell', orderType: 'Stop Market', triggerPrice: '94', reduceOnly: true }), roles: ['target' as const] }
    const props = { error: null, busy: false, entries, direction: 'long' as const, onCheck: vi.fn(), onLink: vi.fn(), onUnlink: vi.fn(), onDismiss: vi.fn() }
    const view = render(<ExecutionPanel execution={{ ...execution, unlinkedOrders: [stop],
      discrepancies: [{ ...missingStopFixture, state: 'resolved' }] }} {...props} />)
    expect(screen.queryByRole('button', { name: /^Link this stop/ })).toBeNull()
    view.rerender(<ExecutionPanel execution={{ ...execution, unlinkedOrders: [stop],
      discrepancies: [{ ...missingStopFixture, state: 'dismissed' }] }} {...props} />)
    expect(screen.queryByRole('button', { name: /^Link this stop/ })).toBeNull()
    // A retained fill the server says contradicts a stop: not offered.
    view.rerender(<ExecutionPanel execution={{ ...execution, unlinkedOrders: [contradicted], discrepancies: [missingStopFixture] }} {...props} />)
    expect(screen.queryByRole('button', { name: /^Link this stop/ })).toBeNull()
    // Unsaved plan edits: the server does not know their levels yet.
    view.rerender(<ExecutionPanel execution={{ ...execution, unlinkedOrders: [stop], discrepancies: [missingStopFixture] }} {...props}
      rebindBlocked="Save or discard your plan changes first." />)
    expect(screen.getByRole('button', { name: /^Link this stop/ })).toBeDisabled()
  })

  it('explains untracked and closed plays without edit controls', () => {
    const view = render(<ExecutionPanel execution={{ ...execution, tracked: false, monitor: null, status: 'planned', reason: 'Manual instruments are not tracked at a venue.',
      links: [], suggestions: [], unlinkedOrders: [] }} error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />)
    expect(screen.getByText('Manual instruments are not tracked at a venue.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Check' })).not.toBeInTheDocument()
    view.rerender(<ExecutionPanel execution={{ ...execution, status: 'closed' }} error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />)
    expect(screen.queryByRole('button', { name: /Unlink/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Other orders on this instrument' })).not.toBeInTheDocument()
  })

  it('keeps order roles chosen by hand when the journal expands, collapses or changes tab', async () => {
    const onLink = vi.fn()
    const user = userEvent.setup()
    render(<PlayJournal notes={createDraft().notes} onChange={vi.fn()} status="open"
      execution={<ExecutionPanel execution={execution} error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={onLink} onUnlink={vi.fn()} />} />)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    const level = (scope: HTMLElement = document.body) => within(scope).getByRole('combobox', { name: 'Level for order 13' })
    await user.selectOptions(level(), 'exit')
    expect(level()).toHaveValue('exit')

    // Expanding mounts a new Execution tab in the dialog; the choice comes along.
    await user.click(screen.getByRole('button', { name: 'Expand journal' }))
    const dialog = screen.getByRole('dialog', { name: 'Play journal' })
    expect(level(dialog)).toHaveValue('exit')
    expect(within(dialog).getByRole('button', { name: 'Link' })).toBeEnabled()
    await user.selectOptions(level(dialog), 'target|e1|t1')
    await user.click(within(dialog).getByRole('button', { name: 'Close expanded journal' }))

    // Closing returns the choice made in the dialog, and another tab does not drop it either.
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(level()).toHaveValue('target|e1|t1')
    await user.click(screen.getByRole('tab', { name: 'Thesis' }))
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    expect(level()).toHaveValue('target|e1|t1')
    await user.click(within(screen.getByRole('region', { name: 'Other orders on this instrument' })).getByRole('button', { name: 'Link' }))
    expect(onLink).toHaveBeenCalledWith({ orderId: '13', role: 'target', entryId: 'e1', levelId: 't1' })
  })

  it('keeps the choices of one play apart from another play in the same journal', async () => {
    const user = userEvent.setup()
    const panel = (playId: string) => <PlayJournal notes={createDraft().notes} onChange={vi.fn()} status="open"
      execution={<ExecutionPanel execution={{ ...execution, playId }} error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />} />
    const view = render(panel('00000000-0000-4000-8000-000000000001'))
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await user.selectOptions(screen.getByRole('combobox', { name: 'Level for order 13' }), 'exit')
    view.rerender(panel('00000000-0000-4000-8000-000000000002'))
    expect(screen.getByRole('combobox', { name: 'Level for order 13' })).toHaveValue('')
  })
})

describe('confirm rows (phase 1, 1.6d)', () => {
  const twoTargets = [{ ...entries[0]!, targets: [{ ...entries[0]!.targets[0]!, id: 't1', value: '110', share: '50' },
    { ...entries[0]!.targets[0]!, id: 't2', value: '110', share: '50' }] }]
  const takeProfit = (orderId: string) => order(orderId, { side: 'sell', orderType: 'Take Profit Market', triggerPrice: '110', reduceOnly: true,
    isPositionTpsl: true })
  const suggestion = (n: number, orderId: string, levelId: string, otherPlay = false) => ({ id: `00000000-0000-4000-8000-0000000000b${n}`,
    role: 'target' as const, entryId: 'e1', levelId, state: 'suggested' as const, source: 'automatic' as const, order: takeProfit(orderId),
    filledQuantity: '0', fills: [], otherPlay })

  it('lists two position take-profits as two rows, each with both levels and no other play (U7)', async () => {
    const onLink = vi.fn()
    const onReject = vi.fn()
    const user = userEvent.setup()
    render(<ExecutionPanel execution={{ ...execution, suggestions: [suggestion(1, '21', 't1'), suggestion(2, '21', 't2'), suggestion(3, '22', 't1'),
      suggestion(4, '22', 't2')] }} error={null} busy={false} entries={twoTargets} direction="long" onCheck={vi.fn()} onLink={onLink} onUnlink={vi.fn()}
      onReject={onReject} />)
    const confirm = screen.getByRole('region', { name: 'Orders to confirm' })
    const rows = within(confirm).getAllByRole('listitem')
    expect(rows).toHaveLength(2)
    expect(within(rows[0]!).getByText(/Matches several levels of this play$/)).toBeInTheDocument()
    expect(confirm).not.toHaveTextContent(/another play/)
    await user.click(within(rows[1]!).getByRole('button', { name: 'Link as Entry 1 target 2' }))
    expect(onLink).toHaveBeenCalledWith({ orderId: '22', role: 'target', entryId: 'e1', levelId: 't2' })
    await user.click(within(rows[0]!).getByRole('button', { name: 'Not this play' }))
    expect(onReject).toHaveBeenCalledWith('21')
  })

  it('says "another play" only when the server reports one', () => {
    render(<ExecutionPanel execution={{ ...execution, suggestions: [suggestion(1, '21', 't1', true), suggestion(3, '22', 't1'), suggestion(4, '22', 't2', true)] }}
      error={null} busy={false} entries={twoTargets} direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />)
    const rows = within(screen.getByRole('region', { name: 'Orders to confirm' })).getAllByRole('listitem')
    expect(rows[0]).toHaveTextContent('Also matches another play')
    expect(rows[1]).toHaveTextContent('Matches several levels of this play and another play')
    expect(suggestionGroups([{ ...suggestion(1, '21', 't1'), order: null }, { ...suggestion(2, '21', 't2'), order: null }])).toHaveLength(2)
  })
})

describe('fill times in the display zone (phase 1, 1.6e)', () => {
  it('lists each linked fill with its time in the display zone (U17)', () => {
    const zone = process.env.TZ
    process.env.TZ = 'Europe/Madrid'
    try {
      render(<ExecutionPanel execution={execution} error={null} busy={false} entries={entries} direction="long" onCheck={vi.fn()} onLink={vi.fn()} onUnlink={vi.fn()} />)
      expect(within(screen.getByRole('region', { name: 'Linked orders' })).getByText('Open Long 0.5 at 100'))
        .toHaveAttribute('title', 'Open Long 0.5 at 100 · 3 Oct 2026, 12:01:00 CEST')
    } finally { restoreZone(zone) }
  })
})
