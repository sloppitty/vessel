import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import type { EntryProgress, PlayExecution, PlayReceipt } from '@/api/plays'
import { createDraft } from './draft'
import { EntryReceipts, RetrospectiveReceipt, ReviewReceipt } from './Receipt'
import { multiple, timeHeld } from './receipt-format'

const entries = [{ ...createDraft().entries[0]!, id: 'e1', name: 'Breakout', price: '100' }]
const progress: EntryProgress = { entryId: 'e1', filledQuantity: '0.5', averageFillPrice: '100.4', restingOrders: 0,
  plannedPrice: '100', plannedQuantity: '1', planRevision: 1 }
const closed: PlayReceipt = { firstEntryAtUtc: '2026-10-03T10:00:00Z', lastExitAtUtc: '2026-10-03T12:05:00Z', averageEntryPrice: '100.4',
  averageExitPrice: '110', fillCount: 3, estimatedR: '1.9800796812749003984063745020', initialRisk: '2.51', estimatedRReason: null }
const totals: PlayExecution['totals'] = { enteredQuantity: '0.5', exitedQuantity: '0.5', openQuantity: '0', closedPnlUsd: '5', closedPnlBasis: 'gross',
  fees: [{ token: 'USDC', amount: '0.03' }], venueNetUsd: '4.97', local: { netUsd: '-0.2', basis: 'executions', reason: null, signDiffers: true } }

describe('actual versus plan (phase 1, 1.1a)', () => {
  it('sets each entry beside the plan at its first fill: average fill against price, filled against quantity', () => {
    render(<EntryReceipts progress={[progress, { ...progress, entryId: 'e2', filledQuantity: '0', averageFillPrice: null,
      plannedPrice: null, plannedQuantity: '7.1428571428571428571428571429', planRevision: null }]} entries={entries} />)
    const [first, second] = within(screen.getByRole('region', { name: 'Entries against the plan' })).getAllByRole('listitem')
    expect(first).toHaveTextContent('Breakout')
    expect(first).toHaveTextContent('Plan revision 1')
    expect(first).toHaveTextContent('Average fill 100.4 vs plan 100 (+0.4)')
    expect(first).toHaveTextContent('Filled 0.5 of 1')
    // Unknown stays unknown, and a long planned quantity is shortened for display with the exact value as its title.
    expect(second).toHaveTextContent('Average fill none yet vs plan unknown')
    expect(within(second!).getByTitle('7.1428571428571428571428571429')).toHaveTextContent('Filled 0 of 7.14285714')
    expect(second).not.toHaveTextContent('Plan revision')
  })

  it('shows nothing for an older server that sends no planned values, and signs a fill below plan', () => {
    const { container, rerender } = render(<EntryReceipts progress={[{ entryId: 'e1', filledQuantity: '0.5', averageFillPrice: '100', restingOrders: 0 }]} entries={entries} />)
    expect(container).toBeEmptyDOMElement()
    rerender(<EntryReceipts progress={[{ ...progress, averageFillPrice: '99.75' }]} entries={entries} />)
    expect(screen.getByText('Average fill 99.75 vs plan 100 (-0.25)')).toBeInTheDocument()
  })

  it('never shows a small non-zero R as zero', () => {
    expect(['-0.0001', '0.0001', '-0.0049', '0', '-0', '0.000', '-0.005', '1.98', '-1.25'].map(multiple))
      .toEqual(['−<0.01R', '+<0.01R', '−<0.01R', '0.00R', '0.00R', '0.00R', '-0.01R', '+1.98R', '-1.25R'])
  })

  it('lists a manual ledger\'s known fees by currency and counts unknown costs', () => {
    render(<ReviewReceipt receipt={closed} status="closed" result={{ kind: 'manual', totals: { enteredQuantity: '1', exitedQuantity: '1',
      openQuantity: '0', grossResultUsd: '10', netResultUsd: null, feesComplete: false }, fees: { fees: [{ token: 'USDC', amount: '0.4' }], unknownFees: 1 } }} />)
    const fees = screen.getByText('Fees').closest('div')!
    expect(fees).toHaveTextContent('0.4 USDC · 1 unknown')
  })

  it('measures time held to the last exit once closed and so far while open', () => {
    expect(timeHeld(closed, 'closed')).toBe('2h 5m')
    expect(timeHeld({ ...closed, lastExitAtUtc: null }, 'open', '2026-10-04T11:00:00Z')).toBe('1d 1h so far')
    expect(timeHeld({ ...closed, firstEntryAtUtc: null }, 'open')).toBeNull()
    expect(timeHeld(closed, 'cancelled')).toBeNull()
  })

  it('heads the Review tab with both result bases, fees, estimated R over its initial risk, and time held', async () => {
    const view = render(<ReviewReceipt receipt={closed} result={{ kind: 'venue', totals }} status="closed" />)
    const outcome = screen.getByLabelText('Outcome')
    expect(outcome).toHaveTextContent('4.97 USD')
    expect(outcome).toHaveTextContent('-0.2 USD')
    expect(within(outcome).getByText('Sign differs by basis')).toBeInTheDocument()
    expect(outcome).toHaveTextContent('0.03 USDC')
    expect(outcome).toHaveTextContent('+1.98R ÷ 2.51 initial risk')
    expect(outcome).toHaveTextContent('2h 5m')
    // Unknown R names Review's reason instead of a number.
    view.rerender(<ReviewReceipt receipt={{ ...closed, estimatedR: null, initialRisk: null, estimatedRReason: 'The plan was written after the first fill.' }}
      result={{ kind: 'manual', totals: { enteredQuantity: '1', exitedQuantity: '1', openQuantity: '0', grossResultUsd: '2', netResultUsd: null, feesComplete: false }, fees: {} }} status="closed" />)
    const manual = screen.getByLabelText('Outcome')
    expect(manual).toHaveTextContent('Manual ledger net')
    expect(manual).not.toHaveTextContent('Venue net')
    await userEvent.click(within(manual).getByRole('button', { name: /^Unknown estimated R/ }))
    expect(await screen.findAllByText('The plan was written after the first fill.')).not.toHaveLength(0)
  })

  it('lists a retrospective trade\'s facts without any plan comparison or R', () => {
    const execution = { totals, receipt: closed } as PlayExecution
    render(<RetrospectiveReceipt execution={execution} direction="short" />)
    const facts = screen.getByLabelText('Trade facts')
    for (const text of ['Short', '100.4', '110', '4.97 USD', '0.03 USDC', '2h 5m']) expect(facts).toHaveTextContent(text)
    expect(facts).not.toHaveTextContent('Estimated R')
  })
})
