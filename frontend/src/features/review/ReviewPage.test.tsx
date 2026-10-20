import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import { defaultReviewQuery, type ReviewDocument } from '@/api/review'
import { emptyReview, reviewFixture } from '@/test/review-fixture'
import { render as renderWithVenues } from '@/test/render'
import { ReviewPage } from './ReviewPage'
import { chooseInPicker } from '@/test/account-picker'

function setup(review = vi.fn().mockResolvedValue(reviewFixture)) {
  const api = { ...createWorkspaceApi('test'), review }
  const open = vi.fn().mockResolvedValue(undefined)
  const view = render(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={open} />)
  return { review, open, user: userEvent.setup(), ...view }
}

describe('Production portfolio review', () => {
  it('names all three toggle groups and keeps pressed state and keyboard operation', async () => {
    const { user } = setup()
    const periods = await screen.findByRole('group', { name: 'Performance period' })
    const grouping = screen.getByRole('group', { name: 'Break down results by' })
    expect(within(periods).getAllByRole('button', { pressed: true })).toHaveLength(1)
    const accounts = within(grouping).getByRole('button', { name: 'Accounts' })
    accounts.focus()
    await user.keyboard('{Enter}')
    expect(accounts).toHaveAttribute('aria-pressed', 'true')
    expect(within(grouping).getAllByRole('button', { pressed: true })).toEqual([accounts])
    const chart = screen.getByRole('group', { name: 'Chart view' })
    expect(within(chart).getByRole('button', { name: 'P&L' })).toHaveAttribute('aria-pressed', 'true')
    const drawdown = within(chart).getByRole('button', { name: 'Drawdown' })
    drawdown.focus()
    await user.keyboard('{Enter}')
    expect(within(chart).getAllByRole('button', { pressed: true })).toEqual([drawdown])
    expect(screen.getByRole('img', { name: /drawdown closed-play/ })).toBeInTheDocument()
  })
  it('renders server statistics and discloses coverage without proposal-only labels', async () => {
    const { user } = setup()
    expect(await screen.findByRole('region', { name: 'Performance scorecard' })).toBeVisible()
    expect(screen.getByText('Batting average')).toBeVisible()
    expect(screen.queryByLabelText('Play origin')).not.toBeInTheDocument()
    await user.click(screen.getByText('3 scored · 2 excluded · 1 reviewed'))
    expect(screen.getByText(/12 unassigned fills/)).toBeVisible()
    expect(screen.getByText('Recent history only.')).toBeVisible()
    await user.click(screen.getByText('Chart data'))
    expect(screen.getByRole('table', { name: /Chart values/ })).toBeVisible()
  })
  it('shows honest empty results without falling back to demo performance', async () => {
    setup(vi.fn().mockResolvedValue(emptyReview))
    expect(await screen.findByText('No scored closed Plays')).toBeVisible()
    expect(screen.getAllByText('N/A').length).toBeGreaterThan(5)
    expect(screen.queryByText('BTC')).not.toBeInTheDocument()
  })
  it('supports filters, clears account on portfolio change and keeps period on reset', async () => {
    const { review, user } = setup()
    await screen.findByRole('button', { name: 'Last 7 days' })
    await chooseInPicker('Account', reviewFixture.options.accounts[0]!.name, user)
    await waitFor(() => expect(review).toHaveBeenLastCalledWith(expect.objectContaining({ accountId: reviewFixture.options.accounts[0]!.id }), expect.any(AbortSignal)))
    await user.selectOptions(screen.getByLabelText('Portfolio'), 'unassigned')
    await waitFor(() => expect(review).toHaveBeenLastCalledWith({ period: 'month', portfolio: 'unassigned' }, expect.any(AbortSignal)))
    await user.click(await screen.findByRole('button', { name: 'Last 7 days' }))
    await waitFor(() => expect(review).toHaveBeenLastCalledWith({ period: 'week', portfolio: 'unassigned' }, expect.any(AbortSignal)))
    await user.click(screen.getByRole('button', { name: 'Reset' }))
    await waitFor(() => expect(review).toHaveBeenLastCalledWith({ period: 'week', portfolio: 'all' }, expect.any(AbortSignal)))
  })
  it('aborts superseded requests and never displays a late old report as current', async () => {
    let oldResolve!: (report: ReviewDocument) => void
    const review = vi.fn().mockImplementationOnce(() => new Promise<ReviewDocument>(resolve => { oldResolve = resolve }))
      .mockResolvedValue(emptyReview)
    const { user } = setup(review)
    expect(screen.getByRole('status')).toHaveTextContent('Loading review')
    await user.selectOptions(screen.getByLabelText('Portfolio'), 'unassigned')
    expect(await screen.findByText('No scored closed Plays')).toBeVisible()
    expect((review.mock.calls[0]![1] as AbortSignal).aborted).toBe(true)
    await act(async () => { oldResolve(reviewFixture) })
    expect(screen.queryByText('BTC')).not.toBeInTheDocument()
  })
  it('retains the displayed asset scope when its option disappears on reload', async () => {
    const { user, review } = setup()
    await screen.findByRole('button', { name: 'Last 30 days' })
    await user.selectOptions(screen.getByLabelText('Asset'), 'BTC')
    await screen.findByRole('button', { name: 'Last 30 days' })
    review.mockResolvedValue({ ...emptyReview, options: { ...reviewFixture.options, instruments: [] } })
    await user.click(screen.getByRole('button', { name: 'Reload review' }))
    expect(await screen.findByRole('option', { name: 'BTC · unavailable' })).toBeInTheDocument()
    expect(screen.getByLabelText('Asset')).toHaveValue('BTC')
    expect(review).toHaveBeenLastCalledWith(expect.objectContaining({ instrument: 'BTC' }), expect.any(AbortSignal))
  })
  it('retries errors and restores focus after definitions', async () => {
    const review = vi.fn().mockRejectedValueOnce(new Error('private transport detail')).mockResolvedValue(reviewFixture)
    const { user } = setup(review)
    expect(await screen.findByRole('alert')).toHaveTextContent('The review could not be loaded.')
    expect(screen.queryByText('private transport detail')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Retry review' }))
    await screen.findByRole('button', { name: 'Last 30 days' })
    const trigger = screen.getByRole('button', { name: 'Definitions' })
    await user.click(trigger)
    expect(screen.getByRole('dialog')).toHaveTextContent('Unassigned fills are not scored Plays')
    await user.keyboard('{Escape}')
    expect(trigger).toHaveFocus()
  })
  it('narrows drill-downs, paginates from server offsets and opens a saved Play', async () => {
    const review = vi.fn().mockResolvedValue({ ...reviewFixture, nextOffset: 50 })
    const { user, open } = setup(review)
    await screen.findByRole('button', { name: 'Inspect Swing trading' })
    await user.click(screen.getByRole('button', { name: 'Inspect Swing trading' }))
    const dialog = screen.getByRole('dialog')
    await within(dialog).findByRole('button', { name: 'Open Play BTC breakout' })
    expect(review).toHaveBeenLastCalledWith({ ...defaultReviewQuery, portfolio: reviewFixture.options.portfolios[0]!.id, offset: 0 }, expect.any(AbortSignal))
    await user.click(within(dialog).getByRole('button', { name: 'Next' }))
    await waitFor(() => expect(review).toHaveBeenLastCalledWith(expect.objectContaining({ offset: 50 }), expect.any(AbortSignal)))
    await user.click(await within(dialog).findByRole('button', { name: 'Open Play BTC breakout' }))
    expect(open).toHaveBeenCalledWith(reviewFixture.plays[0]!.id, expect.any(AbortSignal))
  })
})

describe('Review result bases', () => {
  const local = { netUsd: '-2', basis: 'executions' as const, reason: null, signDiffers: true }
  const reason = 'A fill\'s fee is unverified or not in a listed USD stablecoin.'
  const report: ReviewDocument = {
    ...reviewFixture,
    metrics: { ...reviewFixture.metrics, signDiffers: 1, localUnknown: 1, feeOnlyLosses: 1, unknownGrossLosses: 0 },
    plays: [
      { ...reviewFixture.plays[0]!, net: '3', executionSource: 'imported', local },
      { ...reviewFixture.plays[0]!, id: '22222222-3333-4444-5555-777777777777', title: 'ETH fade', net: '-1.1', executionSource: 'imported',
        local: { netUsd: null, basis: 'executions', reason, signDiffers: false } },
      { ...reviewFixture.plays[0]!, id: '22222222-3333-4444-5555-888888888888', title: 'Paper trade', net: '5.8', executionSource: 'manual',
        local: { netUsd: '5.8', basis: 'manual-ledger', reason: null, signDiffers: false } },
    ],
  }

  it('gives each metric its own n, so 20 scratches and one win is a small sample of 1 decided (A5, F-08)', async () => {
    const a5 = { ...reviewFixture.metrics, count: 21, wins: 1, losses: 0, scratches: 20, batting: '1', riskCount: 21 }
    setup(vi.fn().mockResolvedValue({ ...reviewFixture, metrics: a5,
      groups: { ...reviewFixture.groups, assets: [{ ...reviewFixture.groups.assets[0]!, metrics: a5 },
        { id: 'ETH', name: 'ETH', detail: 'hyperliquid', metrics: { ...a5, count: 3, scratches: 2, net: '-3' } }] } }))
    const scorecard = await screen.findByRole('region', { name: 'Performance scorecard' })
    const card = (name: string) => within(scorecard).getByText(name).closest('article')!
    // Before, the 21-Play count hid that the 100% batting average rests on one decided Play.
    expect(card('Batting average')).toHaveTextContent('n = 1 decided · Small sample')
    expect(card('Profit factor')).toHaveTextContent('n = 1 decided · Small sample')
    expect(card('Avg. gain / loss')).toHaveTextContent('n = 1 win · 0 losses · Small sample')
    expect(card('Expectancy per Play')).toHaveTextContent('n = 21 Plays')
    expect(card('Expectancy per Play')).not.toHaveTextContent('Small sample')
    // The highest and lowest asset cards (net P&L over their Plays) and the breakdown rows are marked by their own counts.
    expect(screen.getByText('HIGHEST ASSET P&L').closest('button')).not.toHaveTextContent('Small sample')
    expect(screen.getByText('LOWEST ASSET P&L').closest('button')).toHaveTextContent('3 PlaysSmall sample')
    const breakdown = screen.getByRole('table', { name: /ranked by net/ })
    expect(within(breakdown).getAllByText(/Small sample/)).not.toHaveLength(0)
    // Each n is its own line, never run into the text before it.
    expect(within(card('Batting average')).getByText('n = 1 decided · Small sample')).toHaveClass('pr-sample')
  })
  it('marks each breakdown cell by its own denominator: 20 wins and one loss leave only gain / loss small', async () => {
    const lopsided = { ...reviewFixture.metrics, count: 21, wins: 20, losses: 1, scratches: 0, batting: '0.9523809524' }
    setup(vi.fn().mockResolvedValue({ ...reviewFixture, metrics: lopsided,
      groups: { ...reviewFixture.groups, portfolios: [{ ...reviewFixture.groups.portfolios[0]!, metrics: lopsided }] } }))
    const breakdown = await screen.findByRole('table', { name: /ranked by net/ })
    const [batting, payoff, profitFactor] = within(breakdown).getAllByRole('row')[1]!.querySelectorAll('td:nth-child(3), td:nth-child(4), td:nth-child(5)')
    expect(batting).not.toHaveTextContent('Small sample')
    expect(payoff).toHaveTextContent('Small sample')
    expect(profitFactor).not.toHaveTextContent('Small sample')
    const footer = within(breakdown).getAllByRole('row').at(-1)!
    expect(footer.querySelectorAll('td')[2]).toHaveTextContent('Small sample')
  })
  it('marks a Play whose plan was written after its first execution, and only that one (D-17)', async () => {
    const { user } = setup(vi.fn().mockResolvedValue({ ...reviewFixture, plays: [
      { ...reviewFixture.plays[0]!, plannedAfterExecution: true },
      { ...reviewFixture.plays[0]!, id: '22222222-3333-4444-5555-888888888888', title: 'ETH fade', plannedAfterExecution: false }] }))
    await user.click(await screen.findByRole('button', { name: '3 Plays' }))
    const rows = within(await screen.findByRole('table', { name: 'Contributing closed Plays' })).getAllByRole('row').slice(1)
    expect(within(rows[0]!).getByText('Plan written after first execution')).toBeInTheDocument()
    expect(within(rows[1]!).queryByText('Plan written after first execution')).not.toBeInTheDocument()
  })
  it('names the R estimate with its denominator and counts sign differences and fee-only losses', async () => {
    setup(vi.fn().mockResolvedValue(report))
    const card = (await screen.findByRole('heading', { name: 'Average outcome' })).closest('section')!
    expect(within(card).queryByText('Avg. realized R')).not.toBeInTheDocument()
    expect(within(card).getByText('Estimated R expectancy (planned-stop model)')).toBeVisible()
    expect(within(card).getByText('2 of 3 with a complete initial stop · Small sample')).toBeVisible()
    const estimate = within(card).getByRole('button', { name: 'About estimated R' })
    expect(estimate).toHaveAccessibleDescription(/entry notional × share-weighted stop distance/)
    expect(estimate).toHaveAccessibleDescription(/at or before the first entry fill/)
    const value = (term: string) => within(card).getByText(term).closest('div')!.querySelector('dd')
    expect(value('Sign differs by basis')).toHaveTextContent(/^1 of 31 unknown$/)
    expect(value('Fee-only losses')).toHaveTextContent(/^1 of 1 loss$/)
    expect(within(card).getByRole('button', { name: 'About result bases' })).toHaveAccessibleDescription(/own fills/)
    // Funding inside venue-reported P&L is unverified, so both counts can reflect it.
    expect(within(card).getByRole('button', { name: 'About result bases' })).toHaveAccessibleDescription(/includes funding is unverified/)
    expect(within(card).getByRole('button', { name: 'About fee-only losses' })).toHaveAccessibleDescription(/includes funding is unverified/)
  })

  it('defines both bases, the unverified funding and the estimated R revision rule', async () => {
    const { user } = setup(vi.fn().mockResolvedValue(report))
    await user.click(await screen.findByRole('button', { name: 'Definitions' }))
    const dialog = screen.getByRole('dialog')
    expect(dialog).toHaveTextContent(/Result bases.*Play-local result.*includes funding is unverified, so a sign difference or a fee-only loss can reflect funding/)
    expect(dialog).toHaveTextContent(/Estimated R \(planned-stop model\).*plan revision at or before the first entry fill/)
    // The formula holds for shorts too: a short sold at 100 and bought back at 90 gains 10.
    expect(dialog).toHaveTextContent(/its own fills' sell proceeds minus buy costs, less every trading fee/)
    expect(dialog).not.toHaveTextContent(/exits minus entries/)
  })

  it('shows each Play-local result beside the venue result, with the flag and unknown reasons', async () => {
    const { user } = setup(vi.fn().mockResolvedValue(report))
    await user.click(await screen.findByRole('button', { name: `${report.metrics.count} Plays` }))
    const dialog = await screen.findByRole('dialog')
    const table = await within(dialog).findByRole('table', { name: 'Contributing closed Plays' })
    expect(within(table).getByRole('columnheader', { name: /Play-local/ })).toBeInTheDocument()
    // The per-Play R column uses the same name as the metric, with the same tip.
    const estimated = within(table).getByRole('columnheader', { name: /^Est\. R/ })
    expect(within(estimated).getByRole('button', { name: 'About estimated R' })).toHaveAccessibleDescription(/planned-stop model/)
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows[0]).toHaveTextContent('+$3Venue result')
    expect(rows[0]).toHaveTextContent('-$2')
    expect(within(rows[0]!).getByRole('button', { name: 'Sign differs by basis' })).toHaveAccessibleDescription(/still follow the venue result/)
    const unknown = within(rows[1]!).getByRole('button', { name: 'Unknown Play-local result' })
    expect(unknown).toHaveTextContent('Unknown')
    expect(unknown).toHaveAccessibleDescription(reason)
    expect(within(rows[1]!).queryByRole('button', { name: 'Sign differs by basis' })).not.toBeInTheDocument()
    expect(rows[2]).toHaveTextContent('Manual ledger')
    expect(within(rows[2]!).queryByRole('button', { name: 'Sign differs by basis' })).not.toBeInTheDocument()
  })

  it('never shows a tiny non-zero result as an exact-looking zero', async () => {
    const tiny: ReviewDocument = { ...report, plays: [
      { ...report.plays[0]!, net: '-0.00001', local: { netUsd: '0.00001', basis: 'executions', reason: null, signDiffers: true } },
      { ...report.plays[2]!, net: '0', local: { netUsd: '0', basis: 'manual-ledger', reason: null, signDiffers: false } },
    ] }
    const { user } = setup(vi.fn().mockResolvedValue(tiny))
    await user.click(await screen.findByRole('button', { name: `${tiny.metrics.count} Plays` }))
    const table = await within(await screen.findByRole('dialog')).findByRole('table', { name: 'Contributing closed Plays' })
    const [first, second] = within(table).getAllByRole('row').slice(1).map(row => within(row).getAllByRole('cell'))
    expect(first![1]).toHaveTextContent(/^-\$0\.00001Venue result$/)
    expect(first![2]).toHaveTextContent(/^\+\$0\.00001Sign differs by basis/)
    expect(second![1]).toHaveTextContent(/^\$0Manual ledger$/)
    expect(second![2]).toHaveTextContent(/^\$0$/)
  })

  it('omits the result-basis figures for reports from servers before them', async () => {
    const { user } = setup()
    await user.click(await screen.findByRole('button', { name: `${reviewFixture.metrics.count} Plays` }))
    expect(screen.queryByText('Sign differs by basis')).not.toBeInTheDocument()
    expect(screen.queryByText('Fee-only losses')).not.toBeInTheDocument()
    const table = await within(await screen.findByRole('dialog')).findByRole('table', { name: 'Contributing closed Plays' })
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('Not reported')
  })
})

describe('Review venue names', () => {
  it('shows venue descriptor names instead of raw venue IDs in breakdowns and the Play list', async () => {
    const api = { ...createWorkspaceApi('test'), review: vi.fn().mockResolvedValue(reviewFixture) }
    const user = userEvent.setup()
    renderWithVenues(<ReviewPage api={api} reloadGeneration={0} onOpenPlay={vi.fn().mockResolvedValue(undefined)} />)
    const breakdown = await screen.findByRole('table', { name: /ranked by net closed-play P&L/ })
    await user.click(screen.getByRole('button', { name: 'Accounts' }))
    expect(within(breakdown).getByText(/^Hyperliquid( · Small sample)?$/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Assets' }))
    expect(within(breakdown).getByText(/^Hyperliquid( · Small sample)?$/)).toBeInTheDocument()
    expect(within(breakdown).queryByText(/hyperliquid/)).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: `${reviewFixture.metrics.count} Plays` }))
    const dialog = await screen.findByRole('dialog')
    await within(dialog).findByRole('table', { name: 'Contributing closed Plays' })
    // The venue is a named logo beside the account, not repeated text.
    const logos = within(dialog).getAllByRole('img', { name: 'Hyperliquid' })
    expect(logos.length).toBeGreaterThan(0)
    expect(logos[0]).toHaveAttribute('title', 'Hyperliquid')
    expect(within(dialog).queryByText('Hyperliquid')).not.toBeInTheDocument()
    expect(within(dialog).queryByText('hyperliquid')).not.toBeInTheDocument()
    const first = reviewFixture.plays[0]!
    const row = within(dialog).getByRole('button', { name: `Open Play ${first.title || first.instrument}` }).closest('th')!
    expect(row.querySelector('.play-asset')).toHaveTextContent(first.instrument)
    expect(row.querySelector('.play-account-name')).toHaveTextContent(first.accountName)
  })
})
