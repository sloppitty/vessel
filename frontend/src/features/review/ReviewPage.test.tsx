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
    await waitFor(() => expect(review).toHaveBeenLastCalledWith({ period: 'month', portfolio: 'unassigned', positions: 'auto', minPlays: 5 }, expect.any(AbortSignal)))
    await user.click(await screen.findByRole('button', { name: 'Last 7 days' }))
    await waitFor(() => expect(review).toHaveBeenLastCalledWith({ period: 'week', portfolio: 'unassigned', positions: 'auto', minPlays: 5 }, expect.any(AbortSignal)))
    await user.click(screen.getByRole('button', { name: 'Reset' }))
    await waitFor(() => expect(review).toHaveBeenLastCalledWith({ period: 'week', portfolio: 'all', positions: 'auto', minPlays: 5 }, expect.any(AbortSignal)))
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
    expect(review).toHaveBeenLastCalledWith({ ...defaultReviewQuery, positions: 'auto', minPlays: 5, portfolio: reviewFixture.options.portfolios[0]!.id, offset: 0 }, expect.any(AbortSignal))
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

  it('names the R estimate with its denominator and counts sign differences and fee-only losses', async () => {
    setup(vi.fn().mockResolvedValue(report))
    const card = (await screen.findByRole('heading', { name: 'Average outcome' })).closest('section')!
    expect(within(card).queryByText('Avg. realized R')).not.toBeInTheDocument()
    expect(within(card).getByText('Estimated R (planned-stop model)')).toBeVisible()
    expect(within(card).getByText('2 of 3 with a complete initial stop')).toBeVisible()
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

describe('Review imported positions', () => {
  const position = { ...reviewFixture.plays[0]!, id: null, operationId: 'fill-1:long', title: 'SOL Long', instrument: 'SOL',
    origin: 'position' as const, executionSource: 'imported' as const, rMultiple: null, reviewed: false, local: null }
  const included: ReviewDocument = {
    ...reviewFixture,
    metrics: { ...reviewFixture.metrics, positions: 1 },
    positions: { mode: 'auto', included: true, minPlays: 5, plays: 2, positions: 1, omitted: 2, playWeight: '2' },
    plays: [...reviewFixture.plays, position],
  }

  it('shows the automatic decision, lets the owner override it and return to automatic', async () => {
    const { user, review } = setup(vi.fn().mockResolvedValue(included))
    const toggle = await screen.findByRole('switch', { name: 'Include imported positions' })
    expect(toggle).toBeChecked()
    expect(screen.getByText('Automatic')).toBeVisible()
    expect(screen.getByText('Plays weigh 2× a position in rates')).toBeVisible()
    expect(screen.getByRole('button', { name: 'About imported positions' })).toHaveAccessibleDescription(/fewer than 5 scored Plays/)
    await user.click(toggle)
    await waitFor(() => expect(review).toHaveBeenLastCalledWith(expect.objectContaining({ positions: 'exclude', minPlays: 5 }), expect.any(AbortSignal)))
    await user.click(await screen.findByRole('button', { name: 'Use automatic' }))
    await waitFor(() => expect(review).toHaveBeenLastCalledWith(expect.objectContaining({ positions: 'auto' }), expect.any(AbortSignal)))
  })

  it('lists positions as results that cannot open a Play and pins the decision in drill-downs', async () => {
    const { user, review } = setup(vi.fn().mockResolvedValue(included))
    await user.click(await screen.findByRole('button', { name: /Plays · 1 position/ }))
    expect(review).toHaveBeenLastCalledWith(expect.objectContaining({ positions: 'include', offset: 0 }), expect.any(AbortSignal))
    const dialog = await screen.findByRole('dialog')
    const row = (await within(dialog).findByText('SOL Long')).closest('tr')!
    expect(within(row).queryByRole('button')).not.toBeInTheDocument()
    expect(within(row).getByText('Imported position · no plan')).toBeVisible()
    expect(within(row).getAllByText('Not a Play')).toHaveLength(2)
  })

  it('discloses positions that could not be scored', async () => {
    const { user } = setup(vi.fn().mockResolvedValue(included))
    await user.click(await screen.findByText(/scored · 2 excluded/))
    expect(screen.getByText('1 imported position included; 2 closed positions could not be scored.')).toBeVisible()
  })
})
