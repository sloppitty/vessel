import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Overview, WorkspaceApi } from '@/api/workspace'
import { VenuesProvider } from '@/api/venues'
import { systemFixture } from '@/test/system-fixture'
import { accountFixture, emptyOverview, playApiStubs, portfolioFixture } from '@/test/workspace-fixture'
import { ApplicationShell } from './ApplicationShell'
import { CreateAccountDialog } from './CreateDialogs'
import { ManageAccountDialog, ManagePortfolioDialog } from './ManageDialogs'
import { activityPage } from '@/test/activity-fixture'
import { reviewFixture } from '@/test/review-fixture'

const paperPortfolio = { ...portfolioFixture, tradingMode: 'paper' as const, name: 'Paper hypotheses' }
const paperAccount = { ...accountFixture, tradingMode: 'paper' as const, syncStatus: 'paper' as const, venueId: 'hyperliquid', name: 'Paper hypothesis 1',
  startingCapitalUsd: '4000', balanceUsd: '4000', availableBalanceUsd: '4000', availableBalanceSource: 'paper' as const }
const paperOverview = { ...emptyOverview, accounts: [paperAccount], portfolios: [paperPortfolio],
  totals: { ...emptyOverview.totals, accountCount: 1, portfolioCount: 1, enabledAccountCount: 1 } }
const system = { ...systemFixture, venues: systemFixture.venues.map(venue => venue.id === 'hyperliquid' ? { ...venue, capabilities: { ...venue.capabilities, paperExecution: true } } : venue) }
function api(overrides: Partial<WorkspaceApi> = {}) {
  return { ...playApiStubs(), tradingMode: 'real', overview: vi.fn().mockResolvedValue(emptyOverview), accounts: vi.fn().mockResolvedValue([]),
    portfolios: vi.fn().mockResolvedValue([]), activity: vi.fn().mockResolvedValue(activityPage([])), snapshot: vi.fn().mockResolvedValue(null),
    sync: vi.fn().mockRejectedValue(new Error('No real refresh allowed')), ...overrides } as WorkspaceApi
}
beforeEach(() => { window.history.replaceState(null, '', '/#accounts') })

describe('isolated Paper workspace', () => {
  it('keeps imported-position Review controls and preferences real-only across shell mode changes', async () => {
    window.history.replaceState(null, '', '/#review')
    const user = userEvent.setup()
    const paper = api({ tradingMode: 'paper', review: vi.fn().mockResolvedValue({
      ...reviewFixture, tradingMode: 'paper',
      plays: [{ ...reviewFixture.plays[0], executionSource: 'paper',
        local: { netUsd: '200.123456789', basis: 'paper-simulation', reason: null, signDiffers: false } }],
    }) })
    const real = api()
    const root = api({ ...real, forTradingMode: vi.fn(mode => mode === 'paper' ? paper : real) })
    render(<ApplicationShell system={system} api={root} disconnect={vi.fn()} />)
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Trading mode' }), 'paper')
    await waitFor(() => expect(paper.review).toHaveBeenCalled())
    expect(paper.review).toHaveBeenLastCalledWith(expect.objectContaining({ positions: 'exclude' }), expect.any(AbortSignal))
    expect(screen.queryByRole('switch', { name: 'Include imported positions' })).not.toBeInTheDocument()
    await user.click(await screen.findByRole('button', { name: '3 Plays' }))
    const details = await screen.findByRole('dialog', { name: 'Closed Plays' })
    expect(await within(details).findByText('Paper · simulated executions')).toBeVisible()
    expect(within(details).getByText('Paper result')).toBeVisible()
    expect(within(details).queryByText('Venue result')).not.toBeInTheDocument()
    expect(paper.review).toHaveBeenLastCalledWith(expect.objectContaining({ positions: 'exclude' }), expect.any(AbortSignal))
    await user.keyboard('{Escape}')
    await user.click(screen.getByRole('button', { name: 'Definitions' }))
    expect(screen.queryByRole('heading', { name: 'Imported positions' })).not.toBeInTheDocument()
    await user.keyboard('{Escape}')
    await user.click(screen.getByRole('link', { name: 'Settings' }))
    expect(await screen.findByRole('heading', { name: 'Paper trading' })).toBeVisible()
    expect(screen.queryByRole('spinbutton', { name: /fewer Plays than/ })).not.toBeInTheDocument()
    await user.selectOptions(screen.getByRole('combobox', { name: 'Trading mode' }), 'real')
    expect(await screen.findByRole('spinbutton', { name: /fewer Plays than/ })).toBeVisible()
    await user.click(screen.getByRole('link', { name: 'Review' }))
    expect(await screen.findByRole('switch', { name: 'Include imported positions' })).toBeVisible()
    expect(real.review).toHaveBeenLastCalledWith(expect.objectContaining({ positions: 'auto' }), expect.any(AbortSignal))
    expect(paper.monitor).not.toHaveBeenCalled()
    expect(paper.sync).not.toHaveBeenCalled()
  })

  it('keeps disabled history accessible, has no real monitor/stream/refresh, and discards a late real overview', async () => {
    let finish!: (overview: Overview) => void
    const real = api({ overview: vi.fn().mockImplementation(() => new Promise<Overview>(resolve => { finish = resolve })) })
    const paper = api({ tradingMode: 'paper', overview: vi.fn().mockResolvedValue(paperOverview) })
    const root = api({ ...real, forTradingMode: vi.fn(mode => mode === 'paper' ? paper : real), paperSettings: vi.fn().mockResolvedValue({ enabled: false, checkIntervalMinutes: 60 }) })
    render(<ApplicationShell system={system} api={root} disconnect={vi.fn()} />)
    await waitFor(() => expect(real.overview).toHaveBeenCalled())
    const picker = await screen.findByRole('combobox', { name: 'Trading mode' })
    await userEvent.selectOptions(picker, 'paper')
    expect(await screen.findByRole('button', { name: 'View Paper hypothesis 1' })).toBeInTheDocument()
    expect(paper.monitor).not.toHaveBeenCalled()
    expect(paper.monitorEvents).not.toHaveBeenCalled()
    expect(paper.monitorStream).not.toHaveBeenCalled()
    expect(paper.sync).not.toHaveBeenCalled()
    expect(screen.queryByRole('button', { name: 'Refresh all' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Refresh Paper hypothesis 1' })).not.toBeInTheDocument()
    await act(async () => finish({ ...emptyOverview, accounts: [{ ...accountFixture, name: 'Late real account' }] }))
    expect(screen.queryByText('Late real account')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    expect(await screen.findByText('Simulated executions')).toBeInTheDocument()
    expect(paper.monitor).not.toHaveBeenCalled()
    expect(paper.runAccountMonitor).not.toHaveBeenCalled()
    expect(paper.monitorStream).not.toHaveBeenCalled()
    expect(paper.sync).not.toHaveBeenCalled()
  })
  it('keeps old or unsupported servers real-only without a broad venue fallback', async () => {
    const root = api({ paperSettings: vi.fn().mockRejectedValue(new Error('Not supported')), forTradingMode: vi.fn(() => api()) })
    render(<ApplicationShell system={system} api={root} disconnect={vi.fn()} />)
    await waitFor(() => expect(root.paperSettings).toHaveBeenCalled())
    expect(screen.queryByRole('combobox', { name: 'Trading mode' })).not.toBeInTheDocument()
  })
  it('protects unsaved Play work before remounting another mode', async () => {
    window.history.replaceState(null, '', '/#plays')
    const real = api(), paper = api({ tradingMode: 'paper' })
    const root = api({ ...real, forTradingMode: vi.fn(mode => mode === 'paper' ? paper : real) })
    render(<ApplicationShell system={system} api={root} disconnect={vi.fn()} />)
    await userEvent.click(await screen.findByRole('button', { name: 'New play' }))
    await userEvent.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Unsaved real thesis')
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Trading mode' }), 'paper')
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('discard unsaved Play work'))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Unsaved real thesis')
    confirm.mockReturnValue(true)
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Trading mode' }), 'paper')
    expect(await screen.findByRole('heading', { name: 'Plays' })).toBeInTheDocument()
    expect(screen.queryByDisplayValue('Unsaved real thesis')).not.toBeInTheDocument()
  })
})

describe('paper account creation and immutable capital', () => {
  it('creates another virtual account with default 4000 and no identity/discovery/import fields', async () => {
    const client = api({ tradingMode: 'paper', createAccount: vi.fn().mockResolvedValue(paperAccount), discoverAccounts: vi.fn() })
    render(<VenuesProvider venues={system.venues}><CreateAccountDialog open api={client} portfolios={[paperPortfolio]} accounts={[paperAccount]}
      onOpenChange={vi.fn()} onCreated={vi.fn()} /></VenuesProvider>)
    expect(screen.getByLabelText('Starting capital · virtual USD')).toHaveValue('4000')
    expect(screen.queryByLabelText('Public wallet address')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Account index')).not.toBeInTheDocument()
    expect(screen.queryByText('Known account value · USD')).not.toBeInTheDocument()
    expect(screen.queryByText('Main account')).not.toBeInTheDocument()
    expect(screen.getAllByRole('radio')).toHaveLength(1)
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Paper portfolio · required' }), paperPortfolio.id)
    await userEvent.type(screen.getByLabelText('Account name'), 'Paper hypothesis 2')
    await userEvent.click(screen.getByRole('button', { name: 'Create paper account' }))
    expect(client.createAccount).toHaveBeenCalledWith({ name: 'Paper hypothesis 2', portfolioId: paperPortfolio.id, venueId: 'hyperliquid', tradingMode: 'paper', startingCapitalUsd: '4000' })
    expect(client.discoverAccounts).not.toHaveBeenCalled()
    expect(client.sync).not.toHaveBeenCalled()
  })
  it('accepts known zero capital exactly and never edits starting capital after creation', async () => {
    const client = api({ tradingMode: 'paper', createAccount: vi.fn().mockResolvedValue(paperAccount), updateAccount: vi.fn().mockResolvedValue(paperAccount) })
    const created = render(<VenuesProvider venues={system.venues}><CreateAccountDialog open api={client} portfolios={[paperPortfolio]} onOpenChange={vi.fn()} onCreated={vi.fn()} /></VenuesProvider>)
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Paper portfolio · required' }), paperPortfolio.id)
    await userEvent.type(screen.getByLabelText('Account name'), 'Zero capital')
    fireEvent.change(screen.getByLabelText('Starting capital · virtual USD'), { target: { value: '0' } })
    await userEvent.click(screen.getByRole('button', { name: 'Create paper account' }))
    expect(client.createAccount).toHaveBeenCalledWith(expect.objectContaining({ startingCapitalUsd: '0' }))
    created.unmount()
    render(<VenuesProvider venues={system.venues}><ManageAccountDialog api={client} account={paperAccount} portfolios={[paperPortfolio]} onClose={vi.fn()} onChanged={vi.fn()} /></VenuesProvider>)
    expect(screen.getByText(/Starting capital: 4000 virtual USD · fixed at creation/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Starting capital · virtual USD')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /credential|reset|deposit|withdraw/i })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    const request = vi.mocked(client.updateAccount).mock.calls[0]![1]
    expect(request).not.toHaveProperty('startingCapitalUsd')
    expect(request).not.toHaveProperty('manualAccountValueUsd')
  })
  it('fails safely when no venue advertises paper execution', () => {
    render(<VenuesProvider venues={systemFixture.venues}><CreateAccountDialog open api={api({ tradingMode: 'paper' })} portfolios={[]} onOpenChange={vi.fn()} onCreated={vi.fn()} /></VenuesProvider>)
    expect(screen.getByRole('button', { name: 'Create paper account' })).toBeDisabled()
    expect(within(screen.getByRole('group', { name: 'Venue' })).queryAllByRole('radio')).toHaveLength(0)
  })
})


describe('paper portfolio membership safety', () => {
  it('keeps existing paper members checked and allows only paper-to-paper additions, never unassignment', async () => {
    const added = { ...paperAccount, id: 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff', name: 'Other paper account', portfolioId: '99999999-8888-7777-6666-555555555555' }
    const otherPortfolio = { ...paperPortfolio, id: added.portfolioId, name: 'Other paper portfolio' }
    const client = api({ tradingMode: 'paper', updatePortfolio: vi.fn().mockResolvedValue(paperPortfolio), moveAccounts: vi.fn().mockResolvedValue([added]) })
    render(<VenuesProvider venues={system.venues}><ManagePortfolioDialog api={client} portfolio={paperPortfolio} accounts={[paperAccount, added]} portfolios={[paperPortfolio, otherPortfolio]} onClose={vi.fn()} onChanged={vi.fn()} /></VenuesProvider>)
    const member = screen.getByRole('checkbox', { name: `Include ${paperAccount.name}` })
    expect(member).toBeChecked()
    expect(member).toBeDisabled()
    await userEvent.click(member)
    expect(member).toBeChecked()
    expect(screen.queryByText('Becomes Unassigned')).not.toBeInTheDocument()
    expect(screen.getByText(/Move existing members to another paper portfolio/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('checkbox', { name: `Include ${added.name}` }))
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(client.updatePortfolio).toHaveBeenCalledTimes(1)
    expect(client.moveAccounts).toHaveBeenCalledExactlyOnceWith([added.id], paperPortfolio.id, [{ accountId: added.id, settingsRevision: added.settingsRevision }])
  })
  it('rejects a retained removal before even saving portfolio fields if its mode becomes paper', async () => {
    const client = api({ updatePortfolio: vi.fn(), moveAccounts: vi.fn() })
    const props = { api: client, accounts: [paperAccount], portfolios: [paperPortfolio], onClose: vi.fn(), onChanged: vi.fn() }
    const view = render(<VenuesProvider venues={system.venues}><ManagePortfolioDialog {...props} portfolio={{ ...paperPortfolio, tradingMode: 'real' }} /></VenuesProvider>)
    await userEvent.click(screen.getByRole('checkbox', { name: `Include ${paperAccount.name}` }))
    expect(screen.getByText('Becomes Unassigned')).toBeInTheDocument()
    view.rerender(<VenuesProvider venues={system.venues}><ManagePortfolioDialog {...props} portfolio={paperPortfolio} /></VenuesProvider>)
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(screen.getByRole('alert')).toHaveTextContent('No changes were saved.')
    expect(client.updatePortfolio).not.toHaveBeenCalled()
    expect(client.moveAccounts).not.toHaveBeenCalled()
    expect(screen.queryByText('Becomes Unassigned')).not.toBeInTheDocument()
  })
})
