import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { ReviewProcess, ReviewProcessPage } from '@/api/review'
import { isReviewProcessPage, reviewProcessPath } from '@/api/review'
import type { WorkspaceApi } from '@/api/workspace'
import { createWorkspaceApi } from '@/api/workspace'
import { reviewFixture } from '@/test/review-fixture'
import { render as renderWithVenues } from '@/test/render'
import { ProcessSection } from './ReviewProcess'
import { ReviewPage } from './ReviewPage'

const process: ReviewProcess = {
  followed: { wins: 7, losses: 3, scratches: 2 }, deviated: { wins: 2, losses: 4, scratches: 0 },
  retrospective: 1, unscored: 2, unanswered: 5, notApplicable: 0,
  mistakeTags: [{ tag: 'Chased the entry', wins: 0, losses: 3, scratches: 1, unscored: 1 }], mistakeTagCount: 1,
}
const play = reviewFixture.plays[0]!
const page: ReviewProcessPage = { total: 1, offset: 0, nextOffset: null, items: [{
  id: play.id, title: 'Late plan', accountId: play.accountId, accountName: play.accountName, venueId: play.venueId, instrument: play.instrument,
  status: 'closed', origin: 'planned', direction: 'long', atUtc: play.closedAtUtc, net: '12.5', returnPercent: '2.5', rMultiple: null,
  adherence: 'followed', mistakeTag: null, plannedAfterExecution: true, reviewCompletion: null,
}] }

describe('process cases (phase 1, 1.4b)', () => {
  it('counts adherence by outcome in a fixed order, with a share only where its own n is at least 10', async () => {
    const onInspect = vi.fn()
    render(<ProcessSection process={process} onInspect={onInspect} />)
    const [, followed, deviated] = within(screen.getAllByRole('table')[0]!).getAllByRole('row')
    expect(within(followed!).getByRole('rowheader')).toHaveTextContent('Followed')
    // 7 wins of 10 decided: a share. 2 wins of 6 decided: none, only its n.
    expect(followed).toHaveTextContent('70.0%n = 10 decided')
    expect(deviated).toHaveTextContent('—n = 6 decided')
    await userEvent.click(within(followed!).getByRole('button', { name: '3 followed losses' }))
    expect(onInspect).toHaveBeenLastCalledWith('Followed · losses', { cell: 'followed-loss' })
    await userEvent.click(screen.getByRole('button', { name: '5 scored, no adherence answer' }))
    expect(onInspect).toHaveBeenLastCalledWith('Scored, no adherence answer', { lane: 'unanswered' })
    // A zero is not a link.
    expect(within(deviated!).queryByRole('button', { name: /scratches/ })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Chased the entry: 5 Plays' }))
    expect(onInspect).toHaveBeenLastCalledWith('Mistake tag · Chased the entry', { mistakeTag: 'Chased the entry' })
    expect(screen.queryByText(/\$/)).not.toBeInTheDocument()
  })

  it('opens a count as its Plays, unscored values as such, with the post-hoc badge', async () => {
    const reviewProcess = vi.fn<WorkspaceApi['reviewProcess']>().mockResolvedValue(page)
    const api = { ...createWorkspaceApi('test'), review: vi.fn().mockResolvedValue({ ...reviewFixture, process }), reviewProcess }
    const user = userEvent.setup()
    renderWithVenues(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: '7 followed wins' }))
    const dialog = await screen.findByRole('dialog', { name: 'Followed · wins' })
    expect(reviewProcess).toHaveBeenCalledWith(expect.objectContaining({ period: 'month', offset: 0 }), { cell: 'followed-win' }, expect.any(AbortSignal))
    expect(await within(dialog).findByText('Plan written after first execution')).toBeInTheDocument()
    expect(within(dialog).getByText(/Followed · Review not complete/)).toBeInTheDocument()
  })

  it('breaks results down by origin and direction in a fixed order, without inspecting their rows', async () => {
    const group = (id: string, name: string) => ({ id, name, detail: '1 scored', metrics: reviewFixture.metrics })
    const api = { ...createWorkspaceApi('test'), review: vi.fn().mockResolvedValue({ ...reviewFixture,
      groups: { ...reviewFixture.groups, origins: [group('planned', 'Planned'), group('retrospective', 'Retrospective')], directions: [group('long', 'Long')] } }) }
    const user = userEvent.setup()
    renderWithVenues(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: 'Origin' }))
    const table = screen.getByRole('table', { name: 'origins in a fixed order' })
    expect(within(table).getAllByRole('rowheader').map(cell => cell.textContent)).toEqual(['Planned1 scored', 'Retrospective1 scored', 'Combined result'])
    expect(within(table).queryByRole('button', { name: /^Inspect/ })).not.toBeInTheDocument()
  })

  it('builds the drill-down path and accepts only pages whose counts add up', () => {
    expect(reviewProcessPath({ period: 'all', portfolio: 'all', offset: 50 }, { mistakeTag: 'Chased the entry' }))
      .toBe('/api/review/process?period=all&portfolio=all&offset=50&mistakeTag=Chased+the+entry')
    expect(isReviewProcessPage(page)).toBe(true)
    expect(isReviewProcessPage({ ...page, total: 2 })).toBe(false)
    expect(isReviewProcessPage({ ...page, items: [{ ...page.items[0]!, status: 'open' }] })).toBe(false)
    expect(isReviewProcessPage({ ...page, items: [{ ...page.items[0]!, net: null, returnPercent: null }] })).toBe(true)
  })
})

describe('pre-flight drill-down (phase 1, 1.5 and 1.4b)', () => {
  it('opens the Plays planned without a check, and those with no pre-flight recorded', async () => {
    const reviewProcess = vi.fn<WorkspaceApi['reviewProcess']>().mockResolvedValue({ items: [], total: 0, offset: 0, nextOffset: null })
    const api = { ...createWorkspaceApi('test'), reviewProcess, review: vi.fn().mockResolvedValue({ ...reviewFixture, process,
      preflight: { recorded: 3, notRecorded: 2, skipped: [{ code: 'thesis', count: 1 }, { code: 'risk', count: 0 }] } }) }
    const user = userEvent.setup()
    renderWithVenues(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await user.click(await screen.findByText(/excluded · /))
    await user.click(screen.getByRole('button', { name: 'thesis and invalidation are written (1)' }))
    expect(reviewProcess).toHaveBeenLastCalledWith(expect.anything(), { skippedCheck: 'thesis' }, expect.any(AbortSignal))
    await user.keyboard('{Escape}')
    await user.click(screen.getByRole('button', { name: '2 not recorded' }))
    expect(reviewProcess).toHaveBeenLastCalledWith(expect.anything(), { skippedCheck: 'not-recorded' }, expect.any(AbortSignal))
  })
})
