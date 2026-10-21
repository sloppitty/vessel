import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import type { OverviewQuery, OverviewSummaryDocument } from '@/api/overview-summary'
import { overviewFixture } from '@/test/workspace-fixture'
import { overviewSummaryFixture, summaryAmount } from '@/test/overview-summary-fixture'
import { OverviewSummary } from './OverviewSummary'
import { balanceShare } from './overview-share'

const query: OverviewQuery = { groupBy: 'portfolio', period: '30', scope: 'all' }
function api(document = overviewSummaryFixture()) { return { ...createWorkspaceApi('test'), overviewSummary: vi.fn().mockResolvedValue(document) } }
describe('Overview summary', () => {
  it('renders nulls, per-account and excluded-operation coverage, reported signs and ratio shares', async () => {
    const document = overviewSummaryFixture()
    document.totals.balance.coverage.totalAccountCount = 2
    document.totals.balance.coverage.reasons = [{ reason: 'Balance unavailable.', count: 1 }]
    document.totals.realizedResult = { amountUsd: '-0.0000000000000000000000000001', coverage: { contributingAccountCount: 1,
      totalAccountCount: 2, contributingOperationCount: 3, excludedOperationCount: 2, reasons: [{ reason: 'Incomplete boundaries.', count: 2 }] } }
    document.totals.unrealizedPnl = summaryAmount('2')
    const onQuery = vi.fn()
    render(<OverviewSummary api={api(document)} query={query} onQuery={onQuery} reloadGeneration={0} fallback={overviewFixture} accountCount={2} />)
    const table = await screen.findByRole('table')
    expect(within(table).getByText('100.00%')).toBeInTheDocument()
    // Incomplete coverage is a pill: its name starts with the visible text and says what it covers, its description
    // states the counts; complete coverage shows nothing.
    expect(screen.getByRole('button', { name: '1/2 Balance coverage' })).toHaveTextContent('1/2')
    expect(screen.getByRole('button', { name: '1/2 Balance coverage' })).toHaveAccessibleDescription(/^1 of 2 accounts\..*1: Balance unavailable\./)
    expect(screen.getByRole('button', { name: '1/2 Realized coverage' })).toHaveAccessibleDescription(/^1 of 2 accounts · 3 closed · 2 excluded\./)
    expect(screen.queryByRole('button', { name: /Unrealized coverage$/ })).not.toBeInTheDocument()
    expect(screen.getAllByText('—').length).toBeGreaterThan(0)
    expect(screen.getAllByText('$2.00 unrealized').every(e => e.classList.contains('overview-positive'))).toBe(true)
    expect(screen.getAllByText(/\$0\.0000000000000000000000000001/).every(e => e.classList.contains('overview-negative'))).toBe(true)
    expect(screen.getByText('balance: 1 · Balance unavailable.')).toBeInTheDocument()
    // The window's ends follow the display setting (UTC here) and name it (phase 1, 1.6e).
    expect(screen.getByText('13 Oct 2026, 12:00:00 UTC')).toHaveAttribute('datetime', '2026-10-13T12:00:00.000Z')
    expect(screen.getByText('13 Sept 2026, 12:00:00 UTC')).toBeInTheDocument()
    // Pooled balances add venue quote stablecoins (USDC, USDG…) at nominal 1:1.
    expect(screen.getByText('Nominal USD')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Nominal USD' })).toHaveAccessibleDescription('Listed stablecoins at 1:1.')
    fireEvent.click(within(table).getByRole('button', { name: 'Scope overview to Swing trading' }))
    expect(onQuery).toHaveBeenLastCalledWith({ ...query, scope: document.rows[0]!.id })
    fireEvent.click(within(table).getByText('100.00%'))
    expect(onQuery).toHaveBeenCalledTimes(2)
  })
  it('changes grouping and period through labelled controls', async () => {
    const onQuery = vi.fn()
    render(<OverviewSummary api={api()} query={query} onQuery={onQuery} reloadGeneration={0} fallback={overviewFixture} accountCount={1} />)
    await screen.findByRole('table')
    expect(screen.getByRole('button', { name: 'By portfolio' })).toHaveAttribute('aria-pressed', 'true')
    fireEvent.click(screen.getByRole('button', { name: 'By account' }))
    expect(onQuery).toHaveBeenLastCalledWith({ ...query, groupBy: 'account' })
    fireEvent.change(screen.getByLabelText('Result period'), { target: { value: '90' } })
    expect(onQuery).toHaveBeenLastCalledWith({ ...query, period: '90' })
  })
  it('never shows All money while a narrower request is loading and ignores a stale completion', async () => {
    let finish!: (d: OverviewSummaryDocument) => void
    const client = api(); client.overviewSummary.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const view = render(<OverviewSummary api={client} query={query} onQuery={vi.fn()} reloadGeneration={0} fallback={overviewFixture} accountCount={1} />)
    const scope = 'unassigned'
    client.overviewSummary.mockReturnValue(new Promise(() => {}))
    view.rerender(<OverviewSummary api={client} query={{ ...query, scope }} onQuery={vi.fn()} reloadGeneration={0} fallback={overviewFixture} accountCount={0} />)
    await waitFor(() => expect(client.overviewSummary).toHaveBeenCalledTimes(2))
    expect(screen.queryByText('$1,250.12')).not.toBeInTheDocument()
    finish(overviewSummaryFixture())
    await waitFor(() => expect(screen.getByText('Loading summary…')).toBeInTheDocument())
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect((client.overviewSummary.mock.calls[0]?.[1] as AbortSignal).aborted).toBe(true)
  })
  it('retries failures and re-reads on reload generation without inventing money', async () => {
    const client = api(); client.overviewSummary.mockRejectedValueOnce(new Error('Unavailable'))
    const view = render(<OverviewSummary api={client} query={query} onQuery={vi.fn()} reloadGeneration={0} fallback={overviewFixture} accountCount={1} />)
    await screen.findByRole('alert'); expect(screen.queryByText('$1,250.12')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Retry summary' })); await screen.findByRole('table')
    view.rerender(<OverviewSummary api={client} query={query} onQuery={vi.fn()} reloadGeneration={1} fallback={overviewFixture} accountCount={1} />)
    await waitFor(() => expect(client.overviewSummary).toHaveBeenCalledTimes(3))
  })
  it('shows empty scopes and bounded group omissions', async () => {
    const document = overviewSummaryFixture(); document.rows = []; document.omittedGroupCount = 1
    render(<OverviewSummary api={api(document)} query={query} onQuery={vi.fn()} reloadGeneration={0} fallback={overviewFixture} accountCount={1} />)
    expect(await screen.findByText('No enabled accounts in this scope.')).toBeInTheDocument()
    expect(screen.getByText('+1 group, included in totals')).toBeInTheDocument()
  })
  it.each([
    [{ numerator: '1', denominator: '3' }, '33.33%', '33.33%'],
    [{ numerator: '-1', denominator: '3' }, '-33.33%', '0%'],
    [{ numerator: '10', denominator: '3' }, '333.33%', '100%'],
    [{ numerator: '79228162514264337593543950335.0000000000000000000000000001', denominator: '79228162514264337593543950335.0000000000000000000000000001' }, '100.00%', '100.00%'],
    [{ numerator: '0.0000000000000000000000000001', denominator: '0.0000000000000000000000000002' }, '50.00%', '50.00%'],
    [null, '—', '0%'], [{ numerator: '1', denominator: '0' }, '—', '0%'],
  ] as const)('rounds only the ratio display %#', (share, label, width) => expect(balanceShare(share)).toEqual({ label, width }))
})
