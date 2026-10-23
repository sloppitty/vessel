import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/errors'
import { isPlayExecution, type AllocationPlay, type SharedFill } from '@/api/plays'
import { allocationRemainder, feeShares } from './allocation'
import { AllocationDialog } from './AllocationDialog'
import { ExecutionPanel } from './ExecutionPanel'
import { createDraft } from './draft'

// Fixed IDs so the lowest Play ID as text is known: a < b.
const a = '10000000-0000-4000-8000-000000000000'
const b = '20000000-0000-4000-8000-000000000000'
const c = '30000000-0000-4000-8000-000000000000'
const plays: AllocationPlay[] = [
  { playId: a, title: 'First', direction: 'long', status: 'open', version: 7, entryIds: ['entry-1'] },
  { playId: b, title: 'Second', direction: 'long', status: 'unresolved', version: 4, entryIds: ['entry-1'] },
  { playId: c, title: 'Reverse', direction: 'short', status: 'planned', version: 2, entryIds: ['entry-1'] },
]
const shared = (overrides: Partial<SharedFill> = {}): SharedFill => ({
  fillId: '40000000-0000-4000-8000-000000000000', kind: 'shared-exit', orderId: '23', revision: 0, reason: null, note: null,
  updatedAtUtc: null, lines: [],
  fill: { sourceFillId: 'x1', direction: 'Close Long', price: '105', quantity: '3', fee: '0.01', feeToken: 'USDC', closedPnlUsd: '15',
    occurredAtUtc: '2026-10-23T10:00:00Z', side: 'sell', positionEffect: 'close', pnlBasis: 'gross' },
  open: [{ playId: a, openQuantity: '1' }, { playId: b, openQuantity: '2' }, { playId: c, openQuantity: '0' }],
  ...overrides,
})

describe('allocation preview', () => {
  it('keeps the remainder exact and shares the fee by the server rule', () => {
    expect(allocationRemainder('3', ['1', '1.9999999999999999999999999999'])).toBe('0.0000000000000000000000000001')
    expect(allocationRemainder('3', ['1', '2'])).toBe('0')
    expect(allocationRemainder('3', ['1', '2.5'])).toBe('-0.5')
    expect(allocationRemainder('3', ['1', '-2'])).toBeNull()
    expect(allocationRemainder('3', ['1', '0'])).toBeNull()
    // 0.01 over thirds at six places: the 0.000001 left goes to the largest line.
    expect(feeShares('0.01', '3', [{ playId: a, quantity: '1' }, { playId: b, quantity: '2' }])).toEqual([
      { playId: a, fee: '0.003333', remainder: null }, { playId: b, fee: '0.006667', remainder: '0.000001' }])
    // Equal lines tie: the lowest Play ID as text takes it, whatever the order typed.
    expect(feeShares('0.01', '3', [{ playId: c, quantity: '1' }, { playId: b, quantity: '1' }, { playId: a, quantity: '1' }])!
      .map(share => [share.playId, share.fee, share.remainder])).toEqual([[c, '0.003333', null], [b, '0.003333', null], [a, '0.003334', '0.000001']])
    // A rebate truncates toward zero, and a finer fee keeps its own precision without a remainder.
    expect(feeShares('-0.01', '3', [{ playId: a, quantity: '1' }, { playId: b, quantity: '2' }])!.map(share => share.fee)).toEqual(['-0.003333', '-0.006667'])
    expect(feeShares('0.00000004', '4', [{ playId: a, quantity: '1' }, { playId: b, quantity: '3' }])).toEqual([
      { playId: a, fee: '0.00000001', remainder: null }, { playId: b, fee: '0.00000003', remainder: null }])
  })

  it('accepts the execution fields of an allocation and refuses malformed ones', () => {
    const execution = {
      playId: a, status: 'open', tracked: true, reason: null, checkedAtUtc: null,
      totals: { enteredQuantity: '1', exitedQuantity: '3', openQuantity: '0', closedPnlUsd: '15', closedPnlBasis: 'gross', fees: [], venueNotAllocated: false },
      entries: [], links: [], suggestions: [], unlinkedOrders: [], notice: 'Facts.', issues: [], monitor: null, discrepancies: [], watches: [],
      sharedFills: [shared()], allocationPlays: plays,
    }
    expect(isPlayExecution(execution)).toBe(true)
    // A line's reason and lines come together, and an entry line names its entry.
    expect(isPlayExecution({ ...execution, sharedFills: [shared({ reason: 'shared-close' })] })).toBe(false)
    expect(isPlayExecution({ ...execution, sharedFills: [shared({ reason: 'flip', lines: [
      { playId: c, title: 'Reverse', role: 'entry', entryId: null, quantity: '1', fee: null, feeRemainder: null }] })] })).toBe(false)
    expect(isPlayExecution({ ...execution, allocationPlays: [{ ...plays[0], direction: 'sideways' }] })).toBe(false)
  })
})

describe('allocation dialog', () => {
  it('shows what is left of the fill and the fee split as the owner types, then sends every Play with its version', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined)
    const onClose = vi.fn()
    render(<AllocationDialog shared={shared()} plays={plays} playId={a} busy={false} onSubmit={onSubmit} onClose={onClose} />)
    const status = screen.getByRole('status')
    expect(status).toHaveTextContent('Left to allocate: 3')
    const save = screen.getByRole('button', { name: 'Save allocation' })
    // Only the long Plays the sell can close are offered; the short one is not.
    expect(screen.queryByLabelText('Quantity for Reverse')).not.toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Quantity for First'), '1')
    expect(status).toHaveTextContent('Left to allocate: 2')
    await userEvent.type(screen.getByLabelText('Quantity for Second'), '2.5')
    expect(status).toHaveTextContent('Left to allocate: -0.5')
    expect(screen.queryByLabelText('Fee split')).not.toBeInTheDocument()
    await userEvent.clear(screen.getByLabelText('Quantity for Second'))
    await userEvent.type(screen.getByLabelText('Quantity for Second'), '2')
    expect(status).toHaveTextContent('Lines add up to 3.')
    const fees = within(screen.getByLabelText('Fee split'))
    expect(fees.getByText('0.003333 USDC')).toBeInTheDocument()
    expect(fees.getByText('0.006667 USDC')).toBeInTheDocument()
    expect(fees.getByText('0.000001 USDC to Second')).toBeInTheDocument()
    // A note is required.
    expect(save).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Note'), 'One sell closed both.')
    await userEvent.click(save)
    expect(onSubmit).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
      fillId: shared().fillId, expectedRevision: 0, reason: 'shared-close', note: 'One sell closed both.',
      plays: [{ playId: a, expectedVersion: 7 }, { playId: b, expectedVersion: 4 }],
      lines: [{ playId: a, role: 'exit', entryId: null, quantity: '1' }, { playId: b, role: 'exit', entryId: null, quantity: '2' }],
    }))
    expect(onClose).toHaveBeenCalled()
  })

  it('keeps the dialog open with the server refusal and retries the same payload with the same request', async () => {
    const onSubmit = vi.fn().mockRejectedValueOnce(new ApiError('http', 'This Play changed elsewhere. Reload it before saving again.', 409))
      .mockResolvedValueOnce(undefined)
    const onClose = vi.fn()
    render(<AllocationDialog shared={shared()} plays={plays} playId={a} busy={false} onSubmit={onSubmit} onClose={onClose} />)
    await userEvent.type(screen.getByLabelText('Quantity for First'), '1')
    await userEvent.type(screen.getByLabelText('Quantity for Second'), '2')
    await userEvent.type(screen.getByLabelText('Note'), 'Shared.')
    await userEvent.click(screen.getByRole('button', { name: 'Save allocation' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('This Play changed elsewhere. Reload it before saving again.')
    expect(onClose).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Save allocation' }))
    expect(onSubmit).toHaveBeenCalledTimes(2)
    expect(onSubmit.mock.calls[1]![0].requestId).toBe(onSubmit.mock.calls[0]![0].requestId)
    expect(onClose).toHaveBeenCalled()
  })

  it('splits a flip into this Play\'s exact exit and another Play\'s entry, and removes an allocation with a note', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined)
    const flip = shared({ kind: 'flip', fill: { ...shared().fill, direction: 'Long > Short', quantity: '2', positionEffect: 'flip', fee: '0.02' } })
    const view = render(<AllocationDialog shared={flip} plays={plays} playId={a} busy={false} onSubmit={onSubmit} onClose={vi.fn()} />)
    // The exit starts at this Play's open quantity without the fill.
    expect(screen.getByLabelText('Exit quantity for First')).toHaveValue('1')
    await userEvent.selectOptions(screen.getByLabelText('Opens'), c)
    await userEvent.type(screen.getByLabelText('Entry quantity for Reverse'), '1')
    expect(screen.getByRole('status')).toHaveTextContent('Lines add up to 2.')
    expect(screen.getByLabelText('Reason')).toHaveValue('flip')
    await userEvent.type(screen.getByLabelText('Note'), 'Reversed in one sell.')
    await userEvent.click(screen.getByRole('button', { name: 'Save allocation' }))
    expect(onSubmit.mock.calls[0]![0]).toMatchObject({ reason: 'flip', plays: [{ playId: a, expectedVersion: 7 }, { playId: c, expectedVersion: 2 }],
      lines: [{ playId: a, role: 'exit', entryId: null, quantity: '1' }, { playId: c, role: 'entry', entryId: 'entry-1', quantity: '1' }] })
    view.unmount()

    // An existing allocation can be removed: no lines, every Play that was on them with its version.
    const saved = shared({ revision: 2, reason: 'shared-close', note: 'Shared.', lines: [
      { playId: a, title: 'First', role: 'exit', entryId: null, quantity: '1', fee: '0.003333', feeRemainder: null },
      { playId: b, title: 'Second', role: 'exit', entryId: null, quantity: '2', fee: '0.006667', feeRemainder: '0.000001' }] })
    render(<AllocationDialog shared={saved} plays={plays} playId={a} busy={false} onSubmit={onSubmit} onClose={vi.fn()} />)
    expect(screen.getByLabelText('Quantity for Second')).toHaveValue('2')
    await userEvent.click(screen.getByRole('button', { name: 'Remove allocation' }))
    expect(onSubmit.mock.calls[1]![0]).toMatchObject({ expectedRevision: 2, lines: [],
      plays: [{ playId: a, expectedVersion: 7 }, { playId: b, expectedVersion: 4 }] })
  })
})

describe('shared fills in the execution panel', () => {
  const base = {
    playId: a, status: 'open' as const, tracked: true, reason: null, checkedAtUtc: null,
    totals: { enteredQuantity: '1', exitedQuantity: '3', openQuantity: '0', closedPnlUsd: '15', closedPnlBasis: 'gross' as const, fees: [] },
    entries: [], links: [], suggestions: [], unlinkedOrders: [], notice: 'Facts.', issues: ['Linked exits exceed the entered quantity.'],
    monitor: null, discrepancies: [], watches: [], sharedFills: [shared()], allocationPlays: plays,
  }
  const props = { error: null, busy: false, entries: createDraft().entries, direction: 'long' as const, onCheck: vi.fn(), onLink: vi.fn(), onUnlink: vi.fn() }

  it('offers Allocate inside the panel and passes the server refusal to the dialog', async () => {
    const onAllocate = vi.fn().mockRejectedValue(new ApiError('http', 'A flip\u2019s exit closes First exactly.', 409))
    const view = render(<ExecutionPanel {...props} execution={base} onAllocate={onAllocate} />)
    const section = screen.getByRole('region', { name: 'Shared fills' })
    expect(within(section).getByText('Close Long 3 at 105')).toBeInTheDocument()
    await userEvent.click(within(section).getByRole('button', { name: 'Allocate' }))
    await userEvent.type(screen.getByLabelText('Quantity for First'), '1')
    await userEvent.type(screen.getByLabelText('Quantity for Second'), '2')
    await userEvent.type(screen.getByLabelText('Note'), 'Shared.')
    await userEvent.click(screen.getByRole('button', { name: 'Save allocation' }))
    expect(onAllocate).toHaveBeenCalledOnce()
    expect(await screen.findByRole('alert')).toHaveTextContent('exactly')
    view.unmount()
    // Unsaved plan edits block it, as they block a rebind.
    render(<ExecutionPanel {...props} execution={base} onAllocate={onAllocate} rebindBlocked="Save or discard your plan changes first." />)
    expect(screen.getByRole('button', { name: 'Allocate' })).toBeDisabled()
  })

  it('shows a split fill with its fee shares and says the venue result is not allocated', () => {
    const allocated = shared({ revision: 1, reason: 'shared-close', note: 'Shared.', lines: [
      { playId: a, title: 'First', role: 'exit', entryId: null, quantity: '1', fee: '0.003333', feeRemainder: null },
      { playId: b, title: 'Second', role: 'exit', entryId: null, quantity: '2', fee: '0.006667', feeRemainder: '0.000001' }] })
    render(<ExecutionPanel {...props} execution={{ ...base, status: 'closed', issues: [], sharedFills: [allocated],
      totals: { ...base.totals, exitedQuantity: '1', venueNetUsd: null, venueNotAllocated: true,
        local: { netUsd: '4.986667', basis: 'executions', reason: null, signDiffers: false } } }} onAllocate={vi.fn()} />)
    expect(screen.getAllByRole('button', { name: /Not allocated/ })).toHaveLength(2)
    expect(screen.getByText('4.986667 USD')).toBeInTheDocument()
    const section = screen.getByRole('region', { name: 'Shared fills' })
    expect(within(section).getByText(/Shared close: First closes 1 \(fee 0\.003333\) · Second closes 2 \(fee 0\.006667, incl\. remainder 0\.000001\) · revision 1/))
      .toBeInTheDocument()
    // A closed Play's split is shown, not edited from its panel.
    expect(within(section).queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument()
  })
})
