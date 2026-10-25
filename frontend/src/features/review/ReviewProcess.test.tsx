import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { ReviewProcess, ReviewProcessPage, ReviewRule } from '@/api/review'
import { isReviewProcessPage, isReviewRule, reviewProcessPath } from '@/api/review'
import type { WorkspaceApi } from '@/api/workspace'
import { createWorkspaceApi } from '@/api/workspace'
import { reviewFixture } from '@/test/review-fixture'
import { render as renderWithVenues } from '@/test/render'
import { ProcessSection, RuleSection } from './ReviewProcess'
import { ReviewPage } from './ReviewPage'
import { credentialsFixture } from '@/test/identity-fixture'

const process: ReviewProcess = {
  followed: { wins: 7, losses: 3, scratches: 2 }, deviated: { wins: 2, losses: 4, scratches: 0 },
  retrospective: 1, unscored: 2, unanswered: 5, notApplicable: 0,
  mistakeTags: [{ tag: 'Chased the entry', wins: 0, losses: 3, scratches: 1, unscored: 1 }], mistakeTagCount: 1,
}
const play = reviewFixture.plays[0]!
const page: ReviewProcessPage = { total: 1, offset: 0, nextOffset: null, items: [{
  id: play.id!, title: 'Late plan', accountId: play.accountId, accountName: play.accountName, venueId: play.venueId, instrument: play.instrument,
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
    const api = { ...createWorkspaceApi(credentialsFixture), review: vi.fn().mockResolvedValue({ ...reviewFixture, process }), reviewProcess }
    const user = userEvent.setup()
    renderWithVenues(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: '7 followed wins' }))
    const dialog = await screen.findByRole('dialog', { name: 'Followed · wins' })
    expect(reviewProcess).toHaveBeenCalledWith(expect.objectContaining({ period: 'month', offset: 0 }), { cell: 'followed-win' }, expect.any(AbortSignal))
    expect(await within(dialog).findByText('Plan written after first execution')).toBeInTheDocument()
    expect(within(dialog).getByText(/Followed · Review not complete/)).toBeInTheDocument()
    // The link is named by its visible title, and the row names its venue.
    expect(within(dialog).getByRole('button', { name: 'Late plan' })).toBeInTheDocument()
    expect(within(dialog).getByRole('img', { name: /Hyperliquid/ })).toBeInTheDocument()
  })

  it('breaks results down by origin and direction in a fixed order, without inspecting their rows', async () => {
    const group = (id: string, name: string) => ({ id, name, detail: '1 scored', metrics: reviewFixture.metrics })
    const api = { ...createWorkspaceApi(credentialsFixture), review: vi.fn().mockResolvedValue({ ...reviewFixture,
      groups: { ...reviewFixture.groups, origins: [group('planned', 'Planned'), group('retrospective', 'Retrospective')], directions: [group('long', 'Long')] } }) }
    const user = userEvent.setup()
    renderWithVenues(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: 'Origin' }))
    const table = screen.getByRole('table', { name: 'origins in a fixed order' })
    expect(within(table).getAllByRole('rowheader').map(cell => cell.textContent)).toEqual(['Planned1 scored', 'Retrospective1 scored', 'Combined result'])
    expect(within(table).queryByRole('button', { name: /^Inspect/ })).not.toBeInTheDocument()
    // One win and one loss decided: no batting average, payoff or profit factor, in the rows or the combined result.
    const [, planned] = within(table).getAllByRole('row')
    expect(within(planned!).getAllByRole('cell').slice(1, 4).map(cell => cell.textContent)).toEqual(['—n = 2', '—n = 1', '—n = 2'])
    expect(within(table).getByRole('rowheader', { name: 'Combined result' }).parentElement).not.toHaveTextContent('%')
    expect(table).not.toHaveTextContent('50.0%')
    // The ranked views keep their rates, marked as small samples.
    await user.click(screen.getByRole('button', { name: 'Assets' }))
    expect(screen.getByRole('table', { name: 'assets ranked by net closed-play P&L' })).toHaveTextContent('50.0%Small sample')
  })

  it('lists the period\'s cancelled Plays with the exclusions and counts them as excluded', async () => {
    const api = { ...createWorkspaceApi(credentialsFixture), review: vi.fn().mockResolvedValue({ ...reviewFixture,
      coverage: { ...reviewFixture.coverage, unresolved: 1, cancelled: 3 } }) }
    renderWithVenues(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn()} />)
    // 1 incomplete fees + 1 incomplete executions + 1 unresolved + 3 cancelled.
    expect(await screen.findByRole('button', { name: 'Inspect exclusions (6)' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '3 cancelled' })).toBeInTheDocument()
  })

  it('builds the drill-down path and accepts only pages whose counts add up', () => {
    expect(reviewProcessPath({ period: 'all', portfolio: 'all', offset: 50 }, { mistakeTag: 'Chased the entry' }))
      .toBe('/api/review/process?period=all&portfolio=all&offset=50&mistakeTag=Chased+the+entry')
    expect(isReviewProcessPage(page)).toBe(true)
    expect(isReviewProcessPage({ ...page, total: 2 })).toBe(false)
    expect(isReviewProcessPage({ ...page, items: [{ ...page.items[0]!, status: 'open' }] })).toBe(false)
    expect(isReviewProcessPage({ ...page, items: [{ ...page.items[0]!, net: null, returnPercent: null }] })).toBe(true)
    expect(isReviewProcessPage({ ...page, items: [{ ...page.items[0]!, status: 'unresolved', net: null, returnPercent: null }] })).toBe(true)
    // Unscored Plays have no result, a result has its return, retrospective Plays have no R or post-hoc badge, and a
    // direction is long, short or none.
    for (const change of [{ status: 'unresolved' }, { status: 'cancelled' }, { returnPercent: null }, { net: null },
      { net: null, returnPercent: null, rMultiple: '1' }, { origin: 'retrospective', plannedAfterExecution: false, rMultiple: '1' },
      { origin: 'retrospective' }, { direction: 'sideways' }])
      expect(isReviewProcessPage({ ...page, items: [{ ...page.items[0]!, ...change }] })).toBe(false)
  })
})

describe('pre-flight drill-down (phase 1, 1.5 and 1.4b)', () => {
  it('opens the Plays planned without a check, and those with no pre-flight recorded', async () => {
    const reviewProcess = vi.fn<WorkspaceApi['reviewProcess']>().mockResolvedValue({ items: [], total: 0, offset: 0, nextOffset: null })
    const api = { ...createWorkspaceApi(credentialsFixture), reviewProcess, review: vi.fn().mockResolvedValue({ ...reviewFixture, process,
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

describe('the if-then rule in Review (Phase 2, item 3)', () => {
  const row = (wins: number, losses: number, scratches: number, unscored: number) => ({ wins, losses, scratches, unscored })
  const rule: ReviewRule = { carried: 6, acknowledged: 4, exceptions: 1, notAcknowledged: 1,
    followed: row(2, 1, 0, 0), deviated: row(1, 0, 0, 1), notApplicable: row(0, 0, 0, 0), unanswered: row(0, 0, 1, 0) }

  it('counts the answers at Plan it and in the review, with no share, each opening its Plays', async () => {
    const onInspect = vi.fn()
    const view = render(<RuleSection rule={rule} onInspect={onInspect} />)
    const section = screen.getByRole('region', { name: 'If-then rule' })
    expect(section).toHaveTextContent('6 Plays planned under a rule. At Plan it: 4 acknowledged · 1 exception · 1 not answered.')
    const rows = within(section).getAllByRole('row')
    expect(rows.map(r => within(r).queryByRole('rowheader')?.textContent)).toEqual([undefined, 'Followed', 'Deviated', 'Not applicable', 'Not answered'])
    // A winning deviation stays a deviation.
    expect(rows[2]).toHaveTextContent('Deviated21001')
    expect(section).not.toHaveTextContent('%')
    await userEvent.click(within(rows[2]!).getByRole('button', { name: '2 deviated the rule' }))
    expect(onInspect).toHaveBeenLastCalledWith('Rule · deviated', { rule: 'deviated' })
    await userEvent.click(within(section).getByRole('button', { name: '1 exception at Plan it' }))
    expect(onInspect).toHaveBeenLastCalledWith('Rule at Plan it · exception', { rule: 'exception' })
    view.rerender(<RuleSection rule={{ ...rule, carried: 0, acknowledged: 0, exceptions: 0, notAcknowledged: 0,
      followed: row(0, 0, 0, 0), deviated: row(0, 0, 0, 0), unanswered: row(0, 0, 0, 0) }} onInspect={onInspect} />)
    expect(screen.queryByRole('region', { name: 'If-then rule' })).toBeNull()
  })

  it('accepts only counts that add up, and builds the drill-down path', () => {
    expect(isReviewRule(rule)).toBe(true)
    expect(isReviewRule({ ...rule, carried: 5 })).toBe(false)
    expect(reviewProcessPath({ period: 'all', portfolio: 'all', offset: 0 }, { rule: 'not-acknowledged' }))
      .toBe('/api/review/process?period=all&portfolio=all&offset=0&rule=not-acknowledged')
  })
})
