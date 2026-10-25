import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { OverviewQuery, OverviewSummaryDocument } from '@/api/overview-summary'
import { createWorkspaceApi, type Overview } from '@/api/workspace'
import { accountFixture, overviewFixture, playListPage, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import type { ActivityQuery } from '@/api/activity'
import { playViews, type PlayCounts, type PlayListQuery } from '@/api/plays'
import { overviewSummaryFixture, summaryTotals } from '@/test/overview-summary-fixture'
import { activityPage, operationFixture } from '@/test/activity-fixture'
import { systemFixture } from '@/test/system-fixture'
import { ApplicationShell } from './ApplicationShell'
import { overviewAccounts, resolveOverviewScope } from './overview-scope'
import { OverviewScope } from './OverviewScope'
import { accountPicker, chooseInPicker } from '@/test/account-picker'
import { render as renderWithVenues } from '@/test/render'
import { credentialsFixture, shellIdentityFixture } from '@/test/identity-fixture'

const other = { ...accountFixture, id: 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff', name: 'Other account', portfolioId: null, balanceUsd: '20' }
const main = { ...accountFixture, balanceUsd: '10' }
const second = { ...main, id: 'cccccccc-cccc-dddd-eeee-ffffffffffff', name: 'Second main', balanceUsd: '5' }
const overview: Overview = { ...overviewFixture, accounts: [main, other] }
/** Chooses a scope in the Overview scope picker by the option that stands for it. */
const pickScope = (value: string) => chooseInPicker('Overview scope', value === 'all' ? 'All accounts' : value === 'unassigned' ? 'Unassigned'
  : value.startsWith('portfolio:') ? portfolioFixture.name : [main, other, second].find(a => `account:${a.id}` === value)!.name)
function scopedSummary(query: OverviewQuery): OverviewSummaryDocument {
  const selected = overviewAccounts(query.scope, overview.accounts)
  const balance = selected.length === 2 ? '30' : selected[0]?.id === main.id ? '10' : '20'
  const document = overviewSummaryFixture(query); document.totals = summaryTotals(balance, selected.length)
  document.rows = selected.map(a => ({ id: query.groupBy === 'account' ? `account:${a.id}` : a.portfolioId ? `portfolio:${a.portfolioId}` : 'unassigned',
    name: query.groupBy === 'account' ? a.name : a.portfolioId ? portfolioFixture.name : 'Unassigned', icon: query.groupBy === 'portfolio' ? 'folder' : null,
    venueId: query.groupBy === 'account' ? a.venueId : null, totals: summaryTotals(a.balanceUsd!), shareOfBalance: { numerator: a.balanceUsd!, denominator: balance } }))
  document.totalGroupCount = document.rows.length
  return document
}
const operations = [operationFixture, { ...operationFixture, id: 'other-op', accountId: other.id, contractId: 'ETH' }]
const plays = [{ ...savedPlayFixture.summary, status: 'planned' as const, title: 'Main play' }, { ...savedPlayFixture.summary, status: 'planned' as const, id: 'other-play', title: 'Other play', accountId: other.id }]
/** The server's scope: only enabled accounts in the selected portfolio, Unassigned or account. */
const inScope = (scope: string | undefined, accountId: string) => overviewAccounts(scope ?? 'all', overview.accounts).some(a => a.id === accountId)
function client() {
  const playPage = (query: PlayListQuery) => playListPage(plays.filter(p => inScope(query.scope, p.accountId)), { ...query, scope: 'all' })
  return { ...createWorkspaceApi(credentialsFixture), overview: vi.fn().mockResolvedValue(overview), overviewSummary: vi.fn(async (q: OverviewQuery) => scopedSummary(q)),
    activity: vi.fn(async (q: ActivityQuery) => activityPage(operations.filter(op => inScope(q.scope, op.accountId)), { pageSize: q.pageSize })),
    plays: vi.fn(async (q: PlayListQuery) => playPage(q)),
    playCounts: vi.fn(async (q: { scope?: string }) => Object.fromEntries(playViews.map(view => [view, playPage({ ...q, view }).totalCount])) as PlayCounts) }
}
beforeEach(() => { window.history.replaceState(null, '', '/') })
describe('Overview scope', () => {
  it('selects portfolio, unassigned and individual-account scope for totals, activity and Plays', async () => {
    const api = client()
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
    await screen.findByRole('table', { name: 'Overview summary by portfolio' })
    const metrics = screen.getByRole('region', { name: 'Scoped overview totals' })
    expect(within(metrics).getByText('$30.00')).toBeInTheDocument()
    const plays = screen.getByRole('region', { name: 'Plays overview' })
    await within(plays).findByRole('button', { name: 'Open Other play' })
    const recent = screen.getByRole('heading', { name: 'Recent activity' }).closest('section')!
    await within(recent).findByText('ETH')
    await pickScope(`portfolio:${portfolioFixture.id}`)
    await waitFor(() => expect(within(metrics).getByText('$10.00')).toBeInTheDocument())
    expect(within(recent).queryByText('ETH')).not.toBeInTheDocument()
    expect(within(plays).queryByRole('button', { name: 'Open Other play' })).not.toBeInTheDocument()
    await pickScope('unassigned')
    await waitFor(() => expect(within(metrics).getByText('$20.00')).toBeInTheDocument())
    expect(within(recent).queryByText('BTC')).not.toBeInTheDocument()
    expect(within(plays).queryByRole('button', { name: 'Open Main play' })).not.toBeInTheDocument()
    await pickScope(`account:${main.id}`)
    await waitFor(() => expect(api.activity).toHaveBeenCalledWith({ scope: `account:${main.id}`, page: 1, pageSize: 5 }, expect.any(AbortSignal)))
    await waitFor(() => expect(within(metrics).getByText('$10.00')).toBeInTheDocument())
    expect(within(recent).queryByText('ETH')).not.toBeInTheDocument()
  })
  it('reloads the summary once per common generation when scope remains valid', async () => {
    const api = client()
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
    await screen.findByRole('table', { name: 'Overview summary by portfolio' })
    expect(api.overviewSummary).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Reload' }))
    await waitFor(() => expect(api.overview).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Reload' })).toBeEnabled())
    await screen.findByRole('table', { name: 'Overview summary by portfolio' })
    expect(api.overviewSummary).toHaveBeenCalledTimes(2)
  })
  it('keeps grouping, period and row-selected scope in shell memory across navigation', async () => {
    const api = client()
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Scope overview to Unassigned' }))
    fireEvent.click(screen.getByRole('button', { name: 'By account' }))
    fireEvent.change(screen.getByLabelText('Result period'), { target: { value: '7' } })
    await waitFor(() => expect(api.overviewSummary).toHaveBeenLastCalledWith({ groupBy: 'account', period: '7', scope: 'unassigned' }, expect.any(AbortSignal)))
    fireEvent.click(screen.getByRole('link', { name: 'Settings' }))
    await screen.findByRole('heading', { name: 'Settings', level: 1 })
    fireEvent.click(screen.getByRole('link', { name: 'Overview' }))
    await screen.findByRole('table', { name: 'Overview summary by account' })
    expect(screen.getByLabelText('Overview scope')).toHaveAttribute('data-value', 'unassigned')
    expect(screen.getByLabelText('Result period')).toHaveValue('7')
    expect(screen.getByRole('button', { name: 'By account' })).toHaveAttribute('aria-pressed', 'true')
    fireEvent.click(screen.getByRole('button', { name: 'Scope overview to Other account' }))
    await waitFor(() => expect(screen.getByLabelText('Overview scope')).toHaveAttribute('data-value', `account:${other.id}`))
  })
  it.each(['account', 'portfolio', 'unassigned'])('falls back permanently to All when %s scope disappears after reload', async kind => {
    const api = client()
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
    const control = await screen.findByLabelText('Overview scope')
    const scope = kind === 'account' ? `account:${main.id}` : kind === 'portfolio' ? `portfolio:${portfolioFixture.id}` : 'unassigned'
    await pickScope(scope)
    await waitFor(() => expect(api.overviewSummary).toHaveBeenLastCalledWith(expect.objectContaining({ scope }), expect.any(AbortSignal)))
    api.overview.mockResolvedValue(kind === 'account' ? { ...overview, accounts: [{ ...main, isEnabled: false }, other] } :
      kind === 'portfolio' ? { ...overview, portfolios: [], accounts: [other] } : { ...overview, accounts: [main] })
    fireEvent.click(screen.getByRole('button', { name: 'Reload' }))
    await waitFor(() => expect(control).toHaveAttribute('data-value', 'all'))
    api.overview.mockResolvedValue(overview)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Reload' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Reload' }))
    await waitFor(() => expect(api.overview).toHaveBeenCalledTimes(3))
    expect(control).toHaveAttribute('data-value', 'all')
  })
  it('labels the selector, excludes disabled accounts and only shows Unassigned when enabled members exist', () => {
    const disabled = { ...other, isEnabled: false }
    render(<OverviewScope scope="all" onScope={vi.fn()} accounts={[main, disabled]} portfolios={[portfolioFixture]} />)
    expect(accountPicker('Overview scope')).toHaveAttribute('data-value', 'all')
    expect(accountPicker('Overview scope')).toHaveAccessibleName('Overview scope All accounts')
    fireEvent.click(accountPicker('Overview scope'))
    expect(screen.getByRole('option', { name: /^All accounts, 1 account$/ })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('option', { name: /^Swing trading,/ })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: /^Unassigned/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('option', { name: /^Other account/ })).not.toBeInTheDocument()
    expect(resolveOverviewScope(`account:${other.id}`, [main, disabled], [portfolioFixture])).toBe('all')
    expect(resolveOverviewScope(`portfolio:${portfolioFixture.id}`, [], [portfolioFixture])).toBe(`portfolio:${portfolioFixture.id}`)
    expect(overviewAccounts('unassigned', [main, other, disabled])).toEqual([other])
  })
  describe('View all', () => {
    const portfolioScope = `portfolio:${portfolioFixture.id}`
    it('is absent from the page while all accounts are shown and offered for every narrower scope', () => {
      const { rerender } = render(<OverviewScope scope="all" onScope={vi.fn()} accounts={[main, other]} portfolios={[portfolioFixture]} />)
      expect(screen.queryByRole('button', { name: /^View all/ })).not.toBeInTheDocument()
      for (const scope of [portfolioScope, 'unassigned', `account:${main.id}`]) {
        rerender(<OverviewScope scope={scope} onScope={vi.fn()} accounts={[main, other]} portfolios={[portfolioFixture]} />)
        const button = screen.getByRole('button', { name: 'View all accounts' })
        // The accessible name starts with the visible text.
        expect(button).toHaveTextContent('View all')
        expect(button).toHaveAttribute('data-size', 'sm')
      }
    })
    it('sets the scope to all and leaves focus on the select it replaces', () => {
      const onScope = vi.fn()
      render(<OverviewScope scope={portfolioScope} onScope={onScope} accounts={[main, other]} portfolios={[portfolioFixture]} />)
      fireEvent.click(screen.getByRole('button', { name: 'View all accounts' }))
      expect(onScope).toHaveBeenCalledExactlyOnceWith('all')
      expect(accountPicker('Overview scope')).toHaveFocus()
    })
    it('keeps the selector labelled and unchanged beside the button', () => {
      render(<OverviewScope scope="unassigned" onScope={vi.fn()} accounts={[main, other]} portfolios={[portfolioFixture]} />)
      const picker = screen.getByLabelText('Overview scope')
      expect(picker).toBe(accountPicker('Overview scope'))
      expect(picker).toHaveAttribute('data-value', 'unassigned')
      expect(picker).toHaveAccessibleName('Overview scope Unassigned')
    })
    it('names a chosen account with its venue and opens on arrow keys', async () => {
      const onScope = vi.fn()
      renderWithVenues(<OverviewScope scope={`account:${main.id}`} onScope={onScope} accounts={[main, other]} portfolios={[portfolioFixture]} />)
      expect(accountPicker('Overview scope')).toHaveAccessibleName('Overview scope Main account, Manual')
      accountPicker('Overview scope').focus()
      fireEvent.keyDown(accountPicker('Overview scope'), { key: 'ArrowDown' })
      const search = await screen.findByRole('combobox', { name: 'Search portfolios and accounts' })
      await waitFor(() => expect(search).toHaveFocus())
      fireEvent.change(search, { target: { value: 'other' } })
      fireEvent.keyDown(search, { key: 'Enter' })
      expect(onScope).toHaveBeenCalledExactlyOnceWith(`account:${other.id}`)
    })
    it('returns the whole Overview to all accounts and keeps select, metrics and Allocation together', async () => {
      const api = client()
      render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
      await screen.findByRole('table', { name: 'Overview summary by portfolio' })
      const metrics = screen.getByRole('region', { name: 'Scoped overview totals' })
      const scope = screen.getByLabelText('Overview scope')
      expect(screen.queryByRole('button', { name: 'View all accounts' })).not.toBeInTheDocument()
      await pickScope(portfolioScope)
      await waitFor(() => expect(within(metrics).getByText('$10.00')).toBeInTheDocument())
      expect(screen.getByRole('button', { name: 'Back to all accounts' })).toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: 'View all accounts' }))
      expect(scope).toHaveAttribute('data-value', 'all')
      expect(scope).toHaveFocus()
      await waitFor(() => expect(within(metrics).getByText('$30.00')).toBeInTheDocument())
      expect(screen.queryByRole('button', { name: 'View all accounts' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /^Back to/ })).not.toBeInTheDocument()
      expect(screen.getByRole('img', { name: /^All accounts balance shares/ })).toBeInTheDocument()
      await waitFor(() => expect(api.overviewSummary).toHaveBeenLastCalledWith(expect.objectContaining({ scope: 'all' }), expect.any(AbortSignal)))
    })
  })
  describe('Allocation back arrow with the scope select', () => {
    const portfolioScope = `portfolio:${portfolioFixture.id}`
    const backName = 'Back to all accounts'
    const metricTotal = (text: string) => waitFor(() => expect(within(screen.getByRole('region', { name: 'Scoped overview totals' })).getByText(text)).toBeInTheDocument())
    it('shows all accounts when the Overview first loads, with no back arrow or View all', async () => {
      render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={client()} />)
      await screen.findByRole('table', { name: 'Overview summary by portfolio' })
      expect(screen.getByLabelText('Overview scope')).toHaveAttribute('data-value', 'all')
      expect(screen.queryByRole('button', { name: backName })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'View all accounts' })).not.toBeInTheDocument()
      expect(screen.getByRole('img', { name: /^All accounts balance shares/ })).toBeInTheDocument()
    })
    it('stops the browser restoring the period select, which would show a stale option without telling React', async () => {
      render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={client()} />)
      await screen.findByRole('table', { name: 'Overview summary by portfolio' })
      expect(screen.getByLabelText('Result period')).toHaveAttribute('autocomplete', 'off')
    })
    it.each([
      ['portfolio', portfolioScope, '$10.00'],
      ['unassigned', 'unassigned', '$20.00'],
      ['account', `account:${main.id}`, '$10.00'],
    ])('a %s scope goes back to all accounts, always named the same, with every section following the select', async (_name, from, fromTotal) => {
      const api = client()
      render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
      await screen.findByRole('table', { name: 'Overview summary by portfolio' })
      const scope = screen.getByLabelText('Overview scope')
      await pickScope(from)
      await metricTotal(fromTotal)
      const back = screen.getByRole('button', { name: backName })
      expect(back).toHaveAttribute('title', backName)
      fireEvent.click(back)
      expect(scope).toHaveAttribute('data-value', 'all')
      await metricTotal('$30.00')
      await waitFor(() => expect(api.overviewSummary).toHaveBeenLastCalledWith(expect.objectContaining({ scope: 'all' }), expect.any(AbortSignal)))
      expect(screen.queryByRole('button', { name: /^Back to/ })).not.toBeInTheDocument()
    })
    it('returns an account opened from a one-account donut on All to All by slice again or by arrow, with the select following', async () => {
      const api = client()
      render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
      await screen.findByRole('table', { name: 'Overview summary by portfolio' })
      const scope = screen.getByLabelText('Overview scope')
      const slice = () => screen.getByText('Other account: $20.00 · 100.00%')
      fireEvent.click(slice())
      expect(scope).toHaveAttribute('data-value', `account:${other.id}`)
      await metricTotal('$20.00')
      expect(screen.getByRole('button', { name: backName })).toBeInTheDocument()
      fireEvent.click(slice())
      expect(scope).toHaveAttribute('data-value', 'all')
      await metricTotal('$30.00')
      fireEvent.click(slice())
      fireEvent.click(screen.getByRole('button', { name: backName }))
      expect(scope).toHaveAttribute('data-value', 'all')
      await waitFor(() => expect(api.overviewSummary).toHaveBeenLastCalledWith(expect.objectContaining({ scope: 'all' }), expect.any(AbortSignal)))
    })
    it('returns a lone Unassigned account to All when Unassigned came from the select or its card', async () => {
      const api = client()
      render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
      await screen.findByRole('table', { name: 'Overview summary by portfolio' })
      const scope = screen.getByLabelText('Overview scope')
      const slice = () => screen.getByText('Other account: $20.00 · 100.00%')
      await pickScope('unassigned')
      await metricTotal('$20.00')
      fireEvent.click(slice())
      expect(scope).toHaveAttribute('data-value', `account:${other.id}`)
      fireEvent.click(slice())
      expect(scope).toHaveAttribute('data-value', 'all')
      await metricTotal('$30.00')
      fireEvent.click(within(screen.getByRole('article', { name: 'Unassigned' })).getByRole('button', { name: 'Show allocation for Unassigned' }))
      expect(scope).toHaveAttribute('data-value', 'unassigned')
      fireEvent.click(slice())
      fireEvent.click(screen.getByRole('button', { name: backName }))
      expect(scope).toHaveAttribute('data-value', 'all')
      await waitFor(() => expect(api.overviewSummary).toHaveBeenLastCalledWith(expect.objectContaining({ scope: 'all' }), expect.any(AbortSignal)))
    })
    it('goes back to all after choosing one account and then another of a portfolio picked in the select', async () => {
      const api = client()
      api.overview.mockResolvedValue({ ...overview, accounts: [main, second, other] })
      render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
      await screen.findByRole('table', { name: 'Overview summary by portfolio' })
      const scope = screen.getByLabelText('Overview scope')
      await pickScope(portfolioScope)
      fireEvent.click(await screen.findByText('Main account: $10.00 · 66.67%'))
      expect(scope).toHaveAttribute('data-value', `account:${main.id}`)
      fireEvent.click(screen.getByRole('button', { name: 'Show allocation for Second main' }))
      expect(scope).toHaveAttribute('data-value', `account:${second.id}`)
      await waitFor(() => expect(api.overviewSummary).toHaveBeenLastCalledWith(expect.objectContaining({ scope: `account:${second.id}` }), expect.any(AbortSignal)))
      fireEvent.click(screen.getByRole('button', { name: backName }))
      expect(scope).toHaveAttribute('data-value', 'all')
      await waitFor(() => expect(api.overviewSummary).toHaveBeenLastCalledWith(expect.objectContaining({ scope: 'all' }), expect.any(AbortSignal)))
    })
    it('deselects an account of a multi-account portfolio to all, not to the portfolio, by slice or legend item', async () => {
      const api = client()
      api.overview.mockResolvedValue({ ...overview, accounts: [main, second, other] })
      render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
      await screen.findByRole('table', { name: 'Overview summary by portfolio' })
      const scope = screen.getByLabelText('Overview scope')
      await pickScope(`account:${main.id}`)
      fireEvent.click(screen.getByText('Main account: $10.00 · 66.67%'))
      expect(scope).toHaveAttribute('data-value', 'all')
      await pickScope(`account:${second.id}`)
      fireEvent.click(screen.getByRole('button', { name: 'Show allocation for Second main' }))
      expect(scope).toHaveAttribute('data-value', 'all')
      await waitFor(() => expect(api.overviewSummary).toHaveBeenLastCalledWith(expect.objectContaining({ scope: 'all' }), expect.any(AbortSignal)))
    })
  })
})
