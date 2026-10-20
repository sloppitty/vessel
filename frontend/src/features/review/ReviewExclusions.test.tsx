import { act, render, screen, within, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { WorkspaceApi } from '@/api/workspace'
import type { ReviewExclusions as Exclusions } from '@/api/review'
import { defaultReviewQuery } from '@/api/review'
import { reviewFixture } from '@/test/review-fixture'
import { ReviewPage } from './ReviewPage'

const source = reviewFixture.plays[0]!
const items = Array.from({ length: 51 }, (_, i) => ({
  playId: `22222222-3333-4444-5555-${(i + 1).toString(16).padStart(12, '0')}`, title: `Excluded BTC ${i + 1}`,
  accountId: source.accountId, accountName: source.accountName, instrument: source.instrument,
  reasons: [`Order ${9000 + i} is missing retained executions; fill ${source.id} has unknown fee currency.`],
}))
const page: Exclusions = { items: items.slice(0, 50), total: 51, offset: 0, nextOffset: 50, notice: 'Retained history only. Same evaluation as Review counts.' }
function setup(exclusions = vi.fn().mockResolvedValue(page)) {
  const open = vi.fn().mockResolvedValue(undefined)
  const api = { review: vi.fn().mockResolvedValue({ ...reviewFixture, coverage: { ...reviewFixture.coverage, incompleteExecutions: 50 } }),
    reviewExclusions: exclusions } as unknown as WorkspaceApi
  const view = render(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={open} />)
  return { api, view, open, exclusions, user: userEvent.setup() }
}
describe('Review actionable exclusions', () => {
  it('reaches exact exclusion records from counts, preserves filters, paginates and opens the precise Play', async () => {
    const { exclusions, open, user } = setup()
    await screen.findByRole('button', { name: 'Inspect exclusions (51)' })
    await user.selectOptions(screen.getByLabelText('Portfolio'), 'unassigned')
    await user.click(await screen.findByRole('button', { name: 'Inspect exclusions (51)' }))
    const dialog = screen.getByRole('dialog')
    expect(await within(dialog).findByText(items[0]!.reasons[0]!)).toBeInTheDocument()
    expect(exclusions).toHaveBeenLastCalledWith({ ...defaultReviewQuery, portfolio: 'unassigned', offset: 0 }, expect.any(AbortSignal))
    expect(within(dialog).getByText(`Play ${items[0]!.playId} · Account ${items[0]!.accountId}`)).toBeInTheDocument()
    // Exclusion records carry no venue, so only the asset chip and the wallet account are shown.
    const identity = within(dialog).getAllByRole('button', { name: /^Open excluded Play / })[0]!.closest('li')!.querySelector('.play-identity')!
    expect(identity.querySelector('.play-asset')).toHaveTextContent(items[0]!.instrument)
    expect(identity.querySelector('.play-account-name')).toHaveTextContent(items[0]!.accountName)
    expect(within(identity as HTMLElement).queryByRole('img')).not.toBeInTheDocument()
    exclusions.mockResolvedValue({ ...page, items: [items[50]], offset: 50, nextOffset: null })
    await user.click(within(dialog).getByRole('button', { name: 'Next exclusions' }))
    await within(dialog).findByRole('button', { name: 'Open excluded Play Excluded BTC 51' })
    expect(exclusions).toHaveBeenLastCalledWith(expect.objectContaining({ offset: 50 }), expect.any(AbortSignal))
    expect(within(dialog).queryByText(items[0]!.reasons[0]!)).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Next exclusions' })).toBeDisabled()
    await user.click(within(dialog).getByRole('button', { name: 'Open excluded Play Excluded BTC 51' }))
    expect(open).toHaveBeenCalledWith(items[50]!.playId, expect.any(AbortSignal))
  })
  it('does not show stale records on failures and retries without exposing transport details', async () => {
    const exclusions = vi.fn().mockRejectedValueOnce(new Error('private transport')).mockResolvedValue(page)
    const { user } = setup(exclusions)
    await user.click(await screen.findByRole('button', { name: 'Inspect exclusions (51)' }))
    const dialog = screen.getByRole('dialog')
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Excluded Plays could not be loaded.')
    expect(screen.queryByText('private transport')).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Retry exclusions' }))
    expect(await within(dialog).findByText(items[0]!.reasons[0]!)).toBeInTheDocument()
  })
  it('aborts on close and restores focus to the count button', async () => {
    let finish!: (page: Exclusions) => void
    const exclusions = vi.fn().mockImplementation(() => new Promise<Exclusions>(resolve => { finish = resolve }))
    const { user } = setup(exclusions)
    const trigger = await screen.findByRole('button', { name: 'Inspect exclusions (51)' })
    await user.click(trigger)
    await within(screen.getByRole('dialog')).findByRole('status')
    await user.keyboard('{Escape}')
    expect((exclusions.mock.calls[0]![1] as AbortSignal).aborted).toBe(true)
    await waitFor(() => expect(trigger).toHaveFocus())
    await act(async () => finish(page))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
  it('labels retrospective and manual sources while using notional returns and no invented retrospective R', async () => {
    const api = { review: vi.fn().mockResolvedValue({ ...reviewFixture, plays: [
      { ...source, origin: 'retrospective', executionSource: 'imported', rMultiple: null },
      { ...source, id: items[1]!.playId, origin: 'planned', executionSource: 'manual' },
    ] }) } as unknown as WorkspaceApi
    render(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Inspect Swing trading' }))
    const dialog = screen.getByRole('dialog')
    expect(await within(dialog).findByText('Retrospective · imported executions')).toBeInTheDocument()
    expect(within(dialog).getByText('Planned · manual executions')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'About return' })).toHaveAccessibleDescription(/Return is net P&L \/ executed entry notional/)
    expect(within(dialog).getAllByText('2.0%')).toHaveLength(2)
    expect(within(dialog).getByText('N/A')).toBeInTheDocument()
  })
})

describe('Unresolved Plays in Review (phase 1, 1.3a)', () => {
  it('counts them among the excluded and lists each with its reason, note and execution facts', async () => {
    const declared: Exclusions = { total: 1, offset: 0, nextOffset: null, notice: 'Retained history only.', items: [{
      ...items[0]!, title: 'Stuck BTC', reasons: ['Declared unresolved by the owner because of a shared close: its executions cannot settle it, so it has no result.'],
      unresolved: { reason: 'shared-close', note: 'Closed with another position.', declaredAtUtc: '2026-10-20T10:00:00Z',
        evidence: { entered: '1', exited: '0', open: '1', orderCount: 1,
          orders: [{ orderId: '21', role: 'entry', entryId: 'entry-1', levelId: null, status: 'filled', filled: '1' }],
          problems: ['Order 22 is missing retained executions.'] } },
    }] }
    const api = { review: vi.fn().mockResolvedValue({ ...reviewFixture, coverage: { ...reviewFixture.coverage, incompleteFees: 0, incompleteExecutions: 0, unresolved: 1 } }),
      reviewExclusions: vi.fn().mockResolvedValue(declared) } as unknown as WorkspaceApi
    const user = userEvent.setup()
    render(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    expect(await screen.findByText('1 unresolved')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Inspect exclusions (1)' }))
    const facts = within(await within(screen.getByRole('dialog')).findByLabelText('Unresolved declaration'))
    expect(facts.getByText('Shared close')).toBeInTheDocument()
    expect(facts.getByText('Closed with another position.')).toBeInTheDocument()
    expect(facts.getByText('When declared: entered 1, exited 0, entered minus exited 1.')).toBeInTheDocument()
    expect(facts.getByText('Linked orders: entry order 21 (filled, filled 1).')).toBeInTheDocument()
    expect(facts.getByText('Order 22 is missing retained executions.')).toBeInTheDocument()
  })
})
