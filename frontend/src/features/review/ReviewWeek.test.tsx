import { credentialsFixture } from '@/test/identity-fixture'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { ReviewDocument, ReviewQuery, ReviewWeek as Week } from '@/api/review'
import type { WorkspaceApi } from '@/api/workspace'
import { createWorkspaceApi } from '@/api/workspace'
import { emptyReviewMetrics, reviewFixture } from '@/test/review-fixture'
import { render } from '@/test/render'
import { ReviewPage } from './ReviewPage'

const week: Week = { start: '2026-10-05', fromUtc: '2026-10-04T22:00:00Z', toUtc: '2026-10-11T22:00:00Z', timeZone: 'Europe/Madrid',
  weekStart: 'monday', data: 'some' }
const process = { followed: { wins: 1, losses: 1, scratches: 0 }, deviated: { wins: 0, losses: 1, scratches: 0 }, retrospective: 0,
  unscored: 0, unanswered: 0, notApplicable: 0, mistakeTagCount: 2,
  mistakeTags: [{ tag: 'Late entry', wins: 0, losses: 2, scratches: 0, unscored: 0 }, { tag: 'Once', wins: 1, losses: 0, scratches: 0, unscored: 0 }] }
const weekly = (overrides: Partial<Week> = {}, document: Partial<ReviewDocument> = {}): ReviewDocument =>
  ({ ...reviewFixture, process, ...document, week: { ...week, ...overrides } })

function client(review: (query: ReviewQuery) => ReviewDocument) {
  const reviewProcess = vi.fn<WorkspaceApi['reviewProcess']>().mockResolvedValue({ items: [], total: 0, offset: 0, nextOffset: null })
  const api: WorkspaceApi = {
    ...createWorkspaceApi(credentialsFixture),
    review: vi.fn((query: ReviewQuery) => Promise.resolve(query.week ? review(query) : reviewFixture)),
    reviewProcess,
    nextActions: vi.fn().mockResolvedValue([{ playId: reviewFixture.plays[0]!.id!, title: 'BTC breakout', nextAction: 'Wait for the retest.',
      reviewedAtUtc: '2026-10-08T10:00:00Z' }]),
    playCounts: vi.fn().mockResolvedValue({ drafts: 0, progress: 1, review: 2, reviewed: 3, closed: 4, cancelled: 1, all: 6 }),
  }
  return { api, reviewProcess }
}

async function openWeek(api: WorkspaceApi) {
  const user = userEvent.setup()
  render(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn()} />)
  await user.click(await screen.findByRole('button', { name: 'Week' }))
  return { user, dialog: await screen.findByRole('dialog', { name: 'Week review' }) }
}

describe('the week page (Phase 2, item 2)', () => {
  it('opens on this reporting week and moves by whole weeks from its first day', async () => {
    const { api } = client(query => weekly(query.week === 'current' ? {} : { start: '2026-09-28', fromUtc: '2026-09-27T22:00:00Z', toUtc: '2026-10-04T22:00:00Z' }))
    const { user, dialog } = await openWeek(api)
    expect(api.review).toHaveBeenLastCalledWith(expect.objectContaining({ week: 'current', offset: 0 }), expect.any(AbortSignal))
    expect(await within(dialog).findByText(/5 Oct 2026 to 11 Oct 2026 · Europe\/Madrid · weeks start on Monday/)).toBeInTheDocument()
    // This week: nothing later to open.
    expect(within(dialog).getByRole('button', { name: 'Next week' })).toBeDisabled()
    await user.click(within(dialog).getByRole('button', { name: 'Previous week' }))
    expect(api.review).toHaveBeenLastCalledWith(expect.objectContaining({ week: '2026-09-28' }), expect.any(AbortSignal))
    expect(await within(dialog).findByText(/28 Sept 2026 to 4 Oct 2026/)).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Next week' })).toBeEnabled()
  })

  it('shows the scorecard without a rate below n = 10, repeated tags only, and drills into the same week', async () => {
    const { api, reviewProcess } = client(() => weekly())
    const { user, dialog } = await openWeek(api)
    const card = await within(dialog).findByRole('region', { name: 'Week scorecard' })
    expect(card).toHaveTextContent('n = 2 decided · no rate below 10')
    const tags = within(dialog).getByRole('region', { name: 'Repeated mistake tags' })
    expect(within(tags).getByRole('button', { name: 'Late entry: 2 Plays' })).toBeInTheDocument()
    expect(within(tags).queryByText(/Once/)).not.toBeInTheDocument()
    await user.click(within(tags).getByRole('button', { name: 'Late entry: 2 Plays' }))
    await waitFor(() => expect(reviewProcess).toHaveBeenCalledWith(expect.objectContaining({ week: '2026-10-05' }), { mistakeTag: 'Late entry' },
      expect.any(AbortSignal)))
    await user.click(within(dialog).getByRole('button', { name: 'Back to the week' }))
    expect(await within(dialog).findByRole('region', { name: 'Week scorecard' })).toBeInTheDocument()
  })

  it('names positions apart from Plays, with their weighting, and discloses the week\'s coverage (Sol round 1)', async () => {
    const mixed = weekly({}, {
      metrics: { ...reviewFixture.metrics, count: 10, wins: 1, losses: 9, scratches: 0, positions: 9 },
      positions: { mode: 'include', included: true, minPlays: 5, plays: 1, positions: 9, omitted: 1, playWeight: '2' },
    })
    const { api } = client(() => mixed)
    const { dialog } = await openWeek(api)
    const card = await within(dialog).findByRole('region', { name: 'Week scorecard' })
    expect(card).toHaveTextContent('1 Play · 9 positions · 1 W · 9 L · 0 S')
    expect(card).toHaveTextContent('Excluded Plays2Inspect exclusions1 imported position not scored')
    expect(within(dialog).getByText(/Plays weigh 2× a position in batting average and expectancy/)).toBeInTheDocument()
    const coverage = within(dialog).getByRole('region', { name: 'Week coverage' })
    expect(coverage).toHaveTextContent('1 Play · 9 positions scored · 2 excluded Plays · 1 position not scored')
    expect(coverage).toHaveTextContent('unassigned fills in this week')
    expect(coverage).toHaveTextContent(reviewFixture.coverage.notice)
  })

  it('lists the latest next actions and the Needs review count in Review\'s scope', async () => {
    const { api } = client(() => weekly())
    const { dialog } = await openWeek(api)
    expect(await within(dialog).findByRole('button', { name: 'BTC breakout' })).toBeInTheDocument()
    expect(within(dialog).getByText('Wait for the retest.')).toBeInTheDocument()
    expect(within(dialog).getByText('2 Plays need review now in this scope.')).toBeInTheDocument()
    expect(api.playCounts).toHaveBeenCalledWith({ scope: 'all' }, expect.any(AbortSignal))
  })

  it('says a week has no data apart from a week without trades', async () => {
    const empty = { metrics: emptyReviewMetrics, process: { ...process, mistakeTags: [], mistakeTagCount: 0 } }
    const { api } = client(query => weekly({ data: query.week === 'current' ? 'empty' : 'none' }, empty))
    const { user, dialog } = await openWeek(api)
    expect(await within(dialog).findByRole('note')).toHaveTextContent('No Play closed, was cancelled or was declared Unresolved this week.')
    expect(within(dialog).queryByRole('region', { name: 'Week scorecard' })).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Previous week' }))
    expect(await within(dialog).findByText('Vessel has no data for this week in this scope.')).toBeInTheDocument()
  })
})
