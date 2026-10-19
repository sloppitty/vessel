import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BrokerAccount, Overview, WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { systemFixture } from '@/test/system-fixture'
import { reviewFixture } from '@/test/review-fixture'
import { accountFixture, emptyOverview, candleSeriesFixture, idleMarketStream, instrumentCatalogFixture, marketContextFixture, overviewFixture, playApiStubs, playCountsFrom, playsFrom, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import type { ActivityQuery } from '@/api/activity'
import { ApplicationShell } from './ApplicationShell'
import { activityFillsFixture, activityFixture, activityPage, operationFixture } from '@/test/activity-fixture'
import { retrospectiveFixture, retrospectiveSavedFixture } from '@/test/retrospective.fixtures'
import { discoveryFixture, indexAccount, indexVenue } from '@/test/account-access-fixture'
import { discrepancyFixture, eventCursor, eventFrame, missingStopFixture, monitorFixture, monitorSummaryFixture, shortStopFixture } from '@/test/monitor-fixture'
import type { MonitorEventPage, MonitorStreamEvent } from '@/api/monitor'
import type { PlayExecution } from '@/api/plays'
import { chooseInPicker } from '@/test/account-picker'

function api(overrides: Partial<WorkspaceApi> = {}): WorkspaceApi {
  return {
    discoverAccounts: vi.fn().mockRejectedValue(new Error('Unexpected discovery')),
    accountCredential: vi.fn().mockResolvedValue({ storageConfigured: true, credential: null }),
    saveAccountCredential: vi.fn().mockRejectedValue(new Error('Unexpected credential write')),
    deleteAccountCredential: vi.fn().mockResolvedValue(undefined),
    activity: vi.fn().mockResolvedValue(activityPage([], { scopeNote: 'Fixture history' })),
    activityFills: vi.fn().mockResolvedValue(activityFillsFixture),
    overview: vi.fn().mockResolvedValue(emptyOverview),
    portfolios: vi.fn().mockResolvedValue([]), accounts: vi.fn().mockResolvedValue([]),
    account: vi.fn().mockResolvedValue(accountFixture),
    moveAccounts: vi.fn().mockResolvedValue([accountFixture]),
    createPortfolio: vi.fn().mockResolvedValue(portfolioFixture), createAccount: vi.fn().mockResolvedValue(accountFixture),
    updatePortfolio: vi.fn().mockResolvedValue(portfolioFixture), deletePortfolio: vi.fn().mockResolvedValue(undefined),
    updateAccount: vi.fn().mockResolvedValue(accountFixture), deleteAccount: vi.fn().mockResolvedValue(undefined),
    snapshot: vi.fn().mockResolvedValue(null), fills: vi.fn().mockResolvedValue([]),
    sync: vi.fn().mockResolvedValue(accountFixture), instruments: vi.fn().mockResolvedValue(instrumentCatalogFixture),
    candles: vi.fn().mockResolvedValue(candleSeriesFixture), marketContext: vi.fn().mockResolvedValue(marketContextFixture),
    marketStream: vi.fn(idleMarketStream), ...playApiStubs(), ...overrides,
  }
}
// The shell loads Plays, Review and Settings lazily. A cold import transforms a large module graph, which under a
// loaded full-suite run can outlast a query's default wait in whichever test opens the page first; warm them once.
beforeAll(async () => {
  await Promise.all([import('@/features/plays/PlaysPage'), import('@/features/review/ReviewPage'), import('./SettingsPage')])
}, 60_000)
beforeEach(() => { window.history.replaceState(null, '', '/') })
describe('Main application shell', () => {
  it('keeps one owner stream across navigation, displays exceptional health, and aborts it on disconnect', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue(overviewFixture),
      monitor: vi.fn().mockResolvedValue({ ...monitorSummaryFixture, workerOnline: false, accounts: [{ ...monitorFixture, state: 'stale' }] }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText(/Worker offline. Background Play checks are delayed/)
    expect(screen.getByText('Monitor stale')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }))
    await userEvent.click(screen.getByRole('link', { name: 'Plays' }))
    expect(client.monitorStream).toHaveBeenCalledOnce()
    expect(client.runAccountMonitor).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: /^Disconnect .*session$/ }))
    expect(vi.mocked(client.monitorStream).mock.calls[0]![1].aborted).toBe(true)
    expect(client.runAccountMonitor).not.toHaveBeenCalled()
  })
  it('reads the event head before any stored workspace read and streams from it', async () => {
    let head!: (page: MonitorEventPage) => void
    const client = api({ overview: vi.fn().mockResolvedValue(overviewFixture),
      monitorEvents: vi.fn(() => new Promise<MonitorEventPage>(resolve => { head = resolve })) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText('Loading your workspace…')
    expect(client.overview).not.toHaveBeenCalled()
    expect(client.monitor).not.toHaveBeenCalled()
    expect(client.monitorStream).not.toHaveBeenCalled()
    await act(async () => head({ events: [], nextCursor: eventCursor('12'), resetRequired: true }))
    await screen.findByRole('button', { name: 'View Main account' })
    expect(client.overview).toHaveBeenCalledOnce()
    expect(vi.mocked(client.monitorStream).mock.calls[0]![0]).toBe(eventCursor('12'))
  })
  it('refreshes stored saved state on owner events while keeping dirty Play work and avoiding notifications', async () => {
    let emit!: (event: MonitorStreamEvent) => void
    const play = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, instrumentSource: 'venue' as const, status: 'planned' as const } }
    const stored = { ...play, summary: { ...play.summary, version: play.summary.version + 1, title: 'Server revision' } }
    const client = api({
      overview: vi.fn().mockResolvedValue(overviewFixture), plays: vi.fn(playsFrom([play.summary])), playCounts: vi.fn(playCountsFrom([play.summary])),
      play: vi.fn().mockResolvedValue(play), evidence: vi.fn().mockResolvedValue([]),
      monitorStream: vi.fn((_after, signal, onEvent) => new Promise<void>(resolve => {
        emit = onEvent; signal.addEventListener('abort', () => resolve(), { once: true })
      })),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Open Saved idea' }))
    const title = await screen.findByRole('textbox', { name: 'Play title' })
    await userEvent.clear(title)
    await userEvent.type(title, 'Keep local revision')
    vi.mocked(client.play).mockResolvedValue(stored)
    // The server opens every stream with a status naming the head; only later events reload.
    act(() => {
      emit({ type: 'status', status: { state: 'reset', nextCursor: eventCursor('1') } })
      emit(eventFrame())
    })
    await screen.findByText(/The saved Play changed elsewhere/)
    expect(title).toHaveValue('Keep local revision')
    expect(client.overview).toHaveBeenCalledTimes(2)
    expect(client.runAccountMonitor).not.toHaveBeenCalled()
    expect(client.sync).not.toHaveBeenCalled()
    expect(document.querySelector('.notification')).toBeNull()
  })
  it('dismisses account-level discrepancies with a stored reload and no venue request', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue(overviewFixture),
      monitor: vi.fn().mockResolvedValue({ ...monitorSummaryFixture, accounts: [{ ...monitorFixture, discrepancies: [discrepancyFixture] }] }),
      dismissAccountDiscrepancy: vi.fn().mockResolvedValue(undefined) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'View Main account' }))
    await userEvent.type(await screen.findByRole('textbox', { name: /Dismissal note/ }), 'Checked separately.')
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss' }))
    await waitFor(() => expect(client.dismissAccountDiscrepancy).toHaveBeenCalledWith(accountFixture.id, discrepancyFixture.id, 'Checked separately.', expect.any(AbortSignal)))
    await waitFor(() => expect(client.overview).toHaveBeenCalledTimes(2))
    expect(client.sync).not.toHaveBeenCalled()
    expect(client.runAccountMonitor).not.toHaveBeenCalled()
  })
  it.each(['disconnect', 'unmount', 'API replacement'])('cancels Refresh all on %s while the first sync is pending', async action => {
    const first = { ...accountFixture, venueId: 'hyperliquid' }
    const second = { ...first, id: 'second', name: 'Second' }
    let finish!: (account: BrokerAccount) => void
    const sync = vi.fn().mockImplementationOnce(() => new Promise<BrokerAccount>(resolve => { finish = resolve })).mockResolvedValue(second)
    const client = api({ overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [first, second] }), sync })
    const disconnect = vi.fn()
    const view = render(<ApplicationShell system={systemFixture} disconnect={disconnect} api={client} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('button', { name: 'Refresh all' }))
    if (action === 'disconnect') await userEvent.click(screen.getByRole('button', { name: /^Disconnect .*session$/ }))
    else if (action === 'unmount') view.unmount()
    else view.rerender(<ApplicationShell system={systemFixture} disconnect={disconnect} api={api()} />)
    await act(async () => finish(first))
    expect(sync).toHaveBeenCalledTimes(1)
    expect((sync.mock.calls[0]?.[1] as AbortSignal)?.aborted).toBe(true)
    expect(client.overview).toHaveBeenCalledTimes(1)
    expect(screen.queryByText(/accounts refreshed/)).not.toBeInTheDocument()
  })
  it('disables account detail refresh during another account refresh without showing its spinner', async () => {
    const first = { ...accountFixture, venueId: 'hyperliquid' }
    const second = { ...first, id: 'second', name: 'Second' }
    let finish!: (account: BrokerAccount) => void
    const client = api({ overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [first, second] }),
      sync: vi.fn().mockImplementation(() => new Promise<BrokerAccount>(resolve => { finish = resolve })) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('button', { name: 'Refresh Main account' }))
    await userEvent.click(screen.getByRole('button', { name: 'View Second' }))
    const refresh = screen.getByRole('button', { name: 'Refresh account' })
    expect(refresh).toBeDisabled()
    expect(refresh.querySelector('.is-spinning')).toBeNull()
    await act(async () => finish(first))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Refresh account' })).toBeEnabled())
  })
  it('retains account search, venue and status filters across detail and page navigation', async () => {
    const account = { ...accountFixture, venueId: 'hyperliquid', syncStatus: 'error' as const }
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api({
      overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [account] }),
    })} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Search accounts' }), 'Main')
    await userEvent.click(within(screen.getByRole('group', { name: 'Filter by venue' })).getByRole('button', { name: 'Hyperliquid' }))
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by status' }), 'error')
    await userEvent.click(screen.getByRole('button', { name: 'View Main account' }))
    await userEvent.click(screen.getByRole('button', { name: 'All accounts' }))
    expect(screen.getByRole('textbox', { name: 'Search accounts' })).toHaveValue('Main')
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }))
    expect(screen.getByRole('textbox', { name: 'Search accounts' })).toHaveValue('Main')
    expect(within(screen.getByRole('group', { name: 'Filter by venue' })).getByRole('button', { name: 'Hyperliquid' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('combobox', { name: 'Filter by status' })).toHaveValue('error')
  })
  it('retains the Accounts grouping across detail and page navigation', async () => {
    const account = { ...accountFixture, venueId: 'hyperliquid', address: `0x${'1'.repeat(40)}`, sourceId: `0x${'1'.repeat(40)}` }
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api({
      overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [account] }),
    })} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }))
    await userEvent.click(within(screen.getByRole('group', { name: 'Group by' })).getByRole('button', { name: 'Address' }))
    expect(screen.getByRole('rowgroup', { name: '0x1111…1111' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'View Main account' }))
    await userEvent.click(screen.getByRole('button', { name: 'All accounts' }))
    expect(screen.getByRole('rowgroup', { name: '0x1111…1111' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }))
    expect(within(screen.getByRole('group', { name: 'Group by' })).getByRole('button', { name: 'Address' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('rowgroup', { name: '0x1111…1111' })).toBeInTheDocument()
  })
  it('retains Activity filters across page navigation', async () => {
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api({
      overview: vi.fn().mockResolvedValue(overviewFixture),
      activity: vi.fn().mockResolvedValue({ ...activityFixture, operations: [operationFixture] }),
    })} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('link', { name: 'Activity' }))
    await screen.findByRole('combobox', { name: 'Instrument' })
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Instrument' }), 'BTC')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Direction' }), 'long')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Period' }), '30')
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    await userEvent.click(screen.getByRole('link', { name: 'Activity' }))
    expect(await screen.findByRole('combobox', { name: 'Instrument' })).toHaveValue('BTC')
    expect(screen.getByRole('combobox', { name: 'Direction' })).toHaveValue('long')
    expect(screen.getByRole('combobox', { name: 'Period' })).toHaveValue('30')
  })
  it('retains the Activity page and size across page navigation and asks Overview for five scoped rows', async () => {
    const operations = Array.from({ length: 60 }, (_, n) => ({ ...operationFixture, id: `${String(n).padStart(8, '0')}-1234-1234-1234-123456789abc:long`, contractId: `C-${n}` }))
    const activity = vi.fn((query: ActivityQuery) => Promise.resolve(activityPage(operations.slice((query.page - 1) * query.pageSize, query.page * query.pageSize),
      { page: query.page, pageSize: query.pageSize, totalCount: 60, facets: { accountIds: [accountFixture.id], contracts: operations.map(op => op.contractId).sort() } })))
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api({ overview: vi.fn().mockResolvedValue(overviewFixture), activity })} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await waitFor(() => expect(activity).toHaveBeenCalledWith({ page: 1, pageSize: 5 }, expect.any(AbortSignal)))
    await userEvent.click(screen.getByRole('link', { name: 'Activity' }))
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Rows per page' }), '50')
    await userEvent.click(within(await screen.findByRole('navigation', { name: 'Activity pages' })).getByRole('button', { name: 'Next' }))
    expect(await screen.findByText('51–60 of 60')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    await userEvent.click(screen.getByRole('link', { name: 'Activity' }))
    expect(await screen.findByText('51–60 of 60')).toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Rows per page' })).toHaveValue('50')
    expect(activity).toHaveBeenLastCalledWith({ page: 2, pageSize: 50 }, expect.any(AbortSignal))
  })
  it('opens Plays from Overview on the same tab, without an earlier search', async () => {
    const plays = [{ ...savedPlayFixture.summary, title: 'Waiting review', status: 'closed' as const, endedAtUtc: '2026-10-06T12:00:00Z' }]
    const client = api({ overview: vi.fn().mockResolvedValue(overviewFixture), plays: vi.fn(playsFrom(plays)), playCounts: vi.fn(playCountsFrom(plays)) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('link', { name: 'Plays' }))
    await userEvent.type(await screen.findByRole('searchbox', { name: 'Search plays' }, { timeout: 5_000 }), 'zzz')
    // The search is debounced; a busy run may need longer than the default wait to send it.
    await waitFor(() => expect(client.plays).toHaveBeenLastCalledWith({ view: 'progress', q: 'zzz' }, expect.any(AbortSignal)), { timeout: 5_000 })
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    const overview = await screen.findByRole('region', { name: 'Plays overview' })
    await userEvent.click(within(overview).getByRole('tab', { name: /^Needs review/ }))
    await within(overview).findByRole('button', { name: 'Open Waiting review' })
    await userEvent.click(within(overview).getByRole('button', { name: 'View all' }))
    expect(await screen.findByRole('tab', { name: /^Needs review/ })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('searchbox', { name: 'Search plays' })).toHaveValue('')
    expect(await screen.findByRole('button', { name: 'Open Waiting review' })).toBeInTheDocument()
    expect(client.plays).toHaveBeenLastCalledWith({ view: 'review', page: 1, pageSize: 25 }, expect.any(AbortSignal))
  })
  it.each([
    ['lighter', 'margin', '131.89123456789012345678901234', '$131.89', 'reported margin', 'Venue-reported available margin · not guaranteed withdrawable'],
    ['risex', 'margin', '0', '$0.00', 'reported margin', 'Venue-reported available margin · not guaranteed withdrawable'],
    ['hyperliquid', 'withdrawable', '35', '$35.00', 'reported withdrawal', 'Venue-reported perps withdrawable'],
    ['risex', null, null, '—', 'not reported', 'Not reported by this venue'],
  ] as const)('shows the selected available source in %s account rows and detail', async (venueId, source, value, display, pill, note) => {
    const account = { ...accountFixture, venueId, availableBalanceUsd: value, availableBalanceSource: source }
    const client = api({
      overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [account] }),
      snapshot: vi.fn().mockResolvedValue({ observedAtUtc: '2026-10-12T10:00:00Z', valueScope: 'fixture',
        accountValueUsd: '1000', withdrawableUsd: source === 'withdrawable' ? value : null,
        availableMarginUsd: source === 'margin' ? value : null, marginUsedUsd: '20', positions: [] }),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    const row = (await screen.findByRole('button', { name: 'View Main account' })).closest('tr')!
    expect(row).toHaveTextContent(`Available balance ${display}`)
    await userEvent.click(screen.getByRole('button', { name: 'View Main account' }))
    await screen.findByText('No positions in the latest snapshot')
    const available = screen.getByText('Available balance').closest('.metric')!
    expect(available).toHaveTextContent(display)
    // The source pill is named by its visible text; the explanation is its description.
    const sourcePill = within(available as HTMLElement).getByRole('button', { name: pill })
    expect(sourcePill).toHaveTextContent(pill)
    expect(sourcePill).toHaveAccessibleDescription(note)
    if (source === 'margin') expect(available).toHaveTextContent('Perps withdrawable —')
  })
  it('shows portfolio available balance with enabled-account coverage, not the equity subtotal', async () => {
    const portfolio = { ...portfolioFixture, availableBalanceUsd: '131.89', availableBalanceAccountCount: 1,
      enabledAccountCount: 2, availableBalanceCoverage: 'partial' as const }
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api({ overview: vi.fn().mockResolvedValue({ ...overviewFixture, portfolios: [portfolio] }) })} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('link', { name: 'Portfolios' }))
    const tile = (await screen.findByRole('button', { name: 'Open Swing trading' })).closest('article')!
    expect(within(tile).getByRole('button', { name: 'Open Swing trading' })).toHaveTextContent('Available balance $131.89')
    const partial = within(tile).getByRole('button', { name: '1/2 Availability coverage' })
    expect(partial).toHaveTextContent('1/2')
    expect(partial).toHaveAccessibleDescription(/^1 of 2 enabled accounts\./)
  })
  it('shows combined total and available balances with coverage, and rereads after refresh', async () => {
    const refreshed = { ...overviewFixture, totals: { ...overviewFixture.totals, totalBalanceUsd: '240', availableBalanceUsd: '95',
      balanceAccountCount: 3, availableBalanceAccountCount: 2, balanceCoverage: 'complete' as const, availableBalanceCoverage: 'partial' as const } }
    const client = api({
      overview: vi.fn().mockResolvedValueOnce({ ...refreshed, totals: { ...refreshed.totals, totalBalanceUsd: '210', availableBalanceUsd: '80' },
        accounts: [{ ...accountFixture, venueId: 'hyperliquid' }, { ...accountFixture, id: 'second', name: 'Other portfolio' }, { ...accountFixture, id: 'third', name: 'Unassigned', portfolioId: null }] })
        .mockResolvedValue({ ...refreshed, accounts: [{ ...accountFixture, venueId: 'hyperliquid' }, { ...accountFixture, id: 'second', name: 'Other portfolio' }, { ...accountFixture, id: 'third', name: 'Unassigned', portfolioId: null }] }),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    const total = (await screen.findByText('Total balance')).closest('.metric')!
    const available = screen.getByText('Available balance').closest('.metric')!
    expect(total).toHaveTextContent('$210.00')
    expect(within(total as HTMLElement).queryByRole('button', { name: /coverage/ })).not.toBeInTheDocument()
    expect(available).toHaveTextContent('$80.00')
    const partial = within(available as HTMLElement).getByRole('button', { name: '2/3 Availability coverage' })
    expect(partial).toHaveTextContent('2/3')
    expect(partial).toHaveAccessibleDescription(/^2 of 3 enabled accounts\./)
    await userEvent.click(screen.getByRole('button', { name: 'Refresh Main account' }))
    await waitFor(() => expect(total).toHaveTextContent('$240.00'))
    expect(available).toHaveTextContent('$95.00')
    expect(client.overview).toHaveBeenCalledTimes(2)
  })
  it('refreshes all eligible accounts sequentially, even outside the filters, and reloads once after failures', async () => {
    const first = { ...accountFixture, venueId: 'hyperliquid', name: 'First' }
    const second = { ...first, id: 'second', name: 'Failed account', portfolioId: null }
    const third = { ...first, id: 'third', name: 'Last', venueId: 'another-sync-venue' }
    let completeFirst!: (account: BrokerAccount) => void
    let completeThird!: (account: BrokerAccount) => void
    const sync = vi.fn().mockImplementationOnce(() => new Promise<BrokerAccount>(resolve => { completeFirst = resolve }))
      .mockRejectedValueOnce(new ApiError('http', 'Fixture failure', 502))
      .mockImplementationOnce(() => new Promise<BrokerAccount>(resolve => { completeThird = resolve }))
    const client = api({ sync, overview: vi.fn().mockResolvedValue({ ...overviewFixture,
      accounts: [first, second, third, accountFixture, { ...first, id: 'disabled', name: 'Disabled', isEnabled: false },
        { ...first, id: 'unsupported', name: 'Unsupported', venueId: 'lighter' }] }) })
    const system = { ...systemFixture, venues: [...systemFixture.venues,
      { ...systemFixture.venues[0]!, id: third.venueId, name: 'Another sync venue' }] }
    render(<ApplicationShell system={system} disconnect={vi.fn()} api={client} />)
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }))
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Filter by portfolio' }), portfolioFixture.id)
    await userEvent.click(screen.getByRole('button', { name: 'Refresh all' }))
    expect(screen.getByText('Refreshing 1 of 3')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Refresh First' })).toHaveAttribute('aria-busy', 'true')
    expect(screen.getByRole('button', { name: 'Refresh Last' })).toBeDisabled()
    expect(sync).toHaveBeenCalledTimes(1)
    expect(client.overview).toHaveBeenCalledTimes(1)
    await act(async () => completeFirst(first))
    await waitFor(() => expect(sync).toHaveBeenCalledTimes(3))
    expect(sync.mock.calls.map(call => call[0])).toEqual([first.id, second.id, third.id])
    expect(screen.getByText('Refreshing 3 of 3')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Refresh Last' })).toHaveAttribute('aria-busy', 'true')
    expect(client.overview).toHaveBeenCalledTimes(1)
    await act(async () => completeThird(third))
    await waitFor(() => expect(client.overview).toHaveBeenCalledTimes(2))
    expect(await screen.findByRole('alert')).toHaveTextContent('2 of 3 accounts refreshed. Failed: Failed account.')
    expect(screen.getAllByRole('alert')).toHaveLength(1)
    expect(await screen.findByRole('button', { name: 'Refresh all' })).toBeEnabled()
  })
  it('disables Refresh all when no account is refreshable or an individual refresh is running', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue(overviewFixture) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    expect(await screen.findByRole('button', { name: 'Refresh all' })).toBeDisabled()
    client.overview = vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [{ ...accountFixture, venueId: 'hyperliquid' }] })
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Refresh all' })).toBeEnabled())
    let finish!: (account: BrokerAccount) => void
    client.sync = vi.fn().mockImplementation(() => new Promise<BrokerAccount>(resolve => { finish = resolve }))
    await userEvent.click(screen.getByRole('button', { name: 'Refresh Main account' }))
    expect(screen.getByRole('button', { name: 'Refresh all' })).toBeDisabled()
    await act(async () => finish(accountFixture))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Refresh all' })).toBeEnabled())
  })
  it('starts a fresh play from Overview but keeps a plan-only unsaved draft', async () => {
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api()} />)
    await screen.findByText('Start with your accounts.')
    await userEvent.click(screen.getByRole('button', { name: 'New play' }))
    expect(await screen.findByRole('textbox', { name: 'Play title' })).toHaveValue('')
    await userEvent.click(screen.getByRole('button', { name: 'Add entry' }))
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    await userEvent.click(screen.getByRole('button', { name: 'New play' }))
    expect(screen.getByRole('article', { name: 'Entry 2 editor' })).toBeInTheDocument()
    expect(screen.getByText('Your unsaved changes are still open. Save or discard them before starting a new play.')).toBeInTheDocument()
  })
  it('offers both existing create dialogs in the keyboard-accessible Overview Add menu', async () => {
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api()} />)
    await screen.findByText('Start with your accounts.')
    const trigger = screen.getByRole('button', { name: 'Add' })
    trigger.focus(); await userEvent.keyboard('{Enter}')
    expect(trigger).toHaveAttribute('aria-expanded', 'true')
    const first = screen.getByRole('menuitem', { name: 'Add account' })
    await waitFor(() => expect(first).toHaveFocus())
    await userEvent.keyboard('{Enter}')
    expect(screen.getByRole('dialog')).toHaveTextContent('Add an account')
    await userEvent.keyboard('{Escape}')
    await userEvent.click(trigger)
    await userEvent.click(screen.getByRole('menuitem', { name: 'New portfolio' }))
    expect(screen.getByRole('dialog')).toHaveTextContent('New portfolio')
  })
  it('refreshes portfolio grouping, counts and Plays capital context after move, disable and enable', async () => {
    const second = { ...portfolioFixture, id: '22222222-3333-4444-5555-666666666666', name: 'Second', accountCount: 0, balanceUsd: null }
    let current: Overview = { ...overviewFixture, portfolios: [portfolioFixture, second] }
    const client = api({
      overview: vi.fn().mockImplementation(() => Promise.resolve(current)),
      updateAccount: vi.fn().mockImplementation((id, settings) => {
        const account = { ...current.accounts[0]!, ...settings, id, settingsRevision: current.accounts[0]!.settingsRevision! + 1 }
        current = { ...current, accounts: [account], portfolios: current.portfolios.map(p => ({
          ...p, accountCount: p.id === account.portfolioId ? 1 : 0,
          balanceUsd: p.id === account.portfolioId && account.isEnabled ? '1250.123456' : null,
          balanceCoverage: p.id === account.portfolioId && account.isEnabled ? 'complete' : 'unavailable',
        })) }
        return Promise.resolve(account)
      }),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Manage Main account' }))
    await userEvent.selectOptions(screen.getByLabelText('Portfolio'), second.id)
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(client.overview).toHaveBeenCalledTimes(2))
    await userEvent.click(screen.getByRole('link', { name: 'Portfolios' }))
    expect(await screen.findByRole('button', { name: `Open ${portfolioFixture.name}` })).toHaveTextContent('0 account records')
    expect(screen.getByRole('button', { name: 'Open Second' })).toHaveTextContent('1 account record')
    expect(screen.getByRole('button', { name: 'Open Second' })).toHaveTextContent('$1,250.12')
    await userEvent.click(screen.getByRole('button', { name: 'Open Second' }))
    expect(screen.getByRole('combobox', { name: 'Filter by portfolio' })).toHaveValue(second.id)
    await userEvent.click(screen.getByRole('button', { name: 'Manage Main account' }))
    expect(screen.getByLabelText('Portfolio')).toHaveValue(second.id)
    await userEvent.click(screen.getByLabelText('Account enabled'))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(client.overview).toHaveBeenCalledTimes(3))
    expect(screen.getByRole('row', { name: /Main account/ })).toHaveTextContent('Disabled')
    await userEvent.click(screen.getByRole('link', { name: 'Portfolios' }))
    expect(within(screen.getByRole('button', { name: 'Open Second' }).closest('article')!).getByRole('button', { name: 'unavailable Balance coverage' }))
      .toHaveAccessibleDescription(/Enabled account balances are unavailable\./)
    await userEvent.click(screen.getByRole('button', { name: 'Open Second' }))
    await userEvent.click(screen.getByRole('button', { name: 'Manage Main account' }))
    await userEvent.click(screen.getByLabelText('Account enabled'))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(client.overview).toHaveBeenCalledTimes(4))
    expect(client.updateAccount).toHaveBeenLastCalledWith(accountFixture.id, expect.objectContaining({ expectedRevision: 3, isEnabled: true }))
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    await userEvent.click(screen.getByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', accountFixture.name)
    expect(screen.getByTestId('capital-context')).toHaveTextContent('Second balance$1,250.12')
  })
  it('resets a portfolio filter whose portfolio disappeared on reload', async () => {
    const client = api({ overview: vi.fn().mockResolvedValueOnce(overviewFixture)
      .mockResolvedValue({ ...overviewFixture, portfolios: [], accounts: [{ ...accountFixture, portfolioId: null }] }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(screen.getByRole('link', { name: 'Portfolios' }))
    await userEvent.click(await screen.findByRole('button', { name: `Open ${portfolioFixture.name}` }))
    expect(screen.getByRole('combobox', { name: 'Filter by portfolio' })).toHaveValue(portfolioFixture.id)
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }))
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Filter by portfolio' })).toHaveValue(''))
    expect(screen.getByRole('button', { name: 'View Main account' })).toBeInTheDocument()
  })
  it('shows portfolio balance rather than the perps-only value for a unified wallet', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue({
      ...overviewFixture,
      portfolios: [{ ...portfolioFixture, totalValueUsd: '0', balanceUsd: '750', balanceCoverage: 'complete' }],
    }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(screen.getByRole('link', { name: 'Portfolios' }))
    const tile = await screen.findByRole('button', { name: `Open ${portfolioFixture.name}` })
    expect(within(tile).getByText('$750.00')).toBeInTheDocument()
    // Complete coverage shows no pill, and the perps-only subtotal is not shown beside the balance.
    expect(within(tile.closest('article')!).queryByRole('button', { name: /Balance coverage$/ })).not.toBeInTheDocument()
    expect(within(tile).queryByText(/\$0\.00/)).not.toBeInTheDocument()
  })
  it('opens contributing Plays from Review through the existing saved editor', async () => {
    const client = api({ review: vi.fn().mockResolvedValue(reviewFixture), play: vi.fn().mockResolvedValue(savedPlayFixture) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(screen.getByRole('link', { name: 'Review' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Inspect Swing trading' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Open Play BTC breakout' }))
    expect(await screen.findByRole('textbox', { name: 'Play title' })).toHaveValue(savedPlayFixture.summary.title)
    expect(client.play).toHaveBeenCalledWith(reviewFixture.plays[0]!.id, expect.any(AbortSignal))
  })
  it('protects plan-only draft edits when opening a contributing Play from Review', async () => {
    const client = api({ review: vi.fn().mockResolvedValue(reviewFixture) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText('Start with your accounts.')
    await userEvent.click(screen.getByRole('link', { name: 'Plays' }))
    await userEvent.click(screen.getByRole('button', { name: 'New play' }))
    await userEvent.click(screen.getByRole('button', { name: 'Add entry' }))
    await userEvent.click(screen.getByRole('link', { name: 'Review' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Inspect Swing trading' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Open Play BTC breakout' }))
    expect(await screen.findByText(/A Play was requested from another screen/)).toBeInTheDocument()
    await userEvent.click(await screen.findByRole('button', { name: 'Keep current work' }))
    expect(screen.queryByText(/A Play was requested from another screen/)).not.toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('')
    expect(screen.getByRole('article', { name: 'Entry 2 editor' })).toBeInTheDocument()
    expect(client.play).not.toHaveBeenCalled()
  })
  it('opens the precise excluded Play through the requested-play loader', async () => {
    const play = reviewFixture.plays[0]!
    const client = api({ review: vi.fn().mockResolvedValue(reviewFixture), play: vi.fn().mockResolvedValue(savedPlayFixture),
      reviewExclusions: vi.fn().mockResolvedValue({ items: [{ playId: play.id, title: play.title, accountId: play.accountId,
        accountName: play.accountName, instrument: play.instrument, reasons: ['Order 932 is missing execution rows.'] }],
        total: 1, offset: 0, nextOffset: null, notice: 'Retained facts in this period.' }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(screen.getByRole('link', { name: 'Review' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Inspect exclusions (2)' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Open excluded Play BTC breakout' }))
    expect(await screen.findByRole('textbox', { name: 'Play title' })).toHaveValue(savedPlayFixture.summary.title)
    expect(client.play).toHaveBeenCalledWith(play.id, expect.any(AbortSignal))
    expect(screen.queryByText(/Opening the requested Play/)).not.toBeInTheDocument()
  })
  it.each(['keep', 'discard'])('preserves an unsaved editor after retrospective success until the owner chooses to %s', async choice => {
    const client = api({ overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [{ ...accountFixture, venueId: 'hyperliquid' }] }),
      activity: vi.fn().mockResolvedValue({ ...activityFixture, operations: [retrospectiveFixture.operation], retainedFillCount: 2 }),
      retrospectivePreview: vi.fn().mockResolvedValue(retrospectiveFixture),
      createRetrospectivePlay: vi.fn().mockResolvedValue(retrospectiveSavedFixture), play: vi.fn().mockResolvedValue(retrospectiveSavedFixture) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('link', { name: 'Plays' }))
    await userEvent.click(screen.getByRole('button', { name: 'New play' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Play title' }), 'Unsaved plan stays')
    await userEvent.click(screen.getByRole('button', { name: 'Add entry' }))
    await userEvent.click(screen.getByRole('link', { name: 'Activity' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Expand BTC operation' }))
    await userEvent.click(screen.getByRole('button', { name: 'Inspect BTC retrospective eligibility' }))
    await userEvent.type(await screen.findByLabelText('Retrospective title'), 'Confirmed history')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm retrospective Play' }))
    expect(await screen.findByText(/A Play was requested from another screen/)).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Unsaved plan stays')
    expect(screen.getByRole('article', { name: 'Entry 2 editor' })).toBeInTheDocument()
    expect(client.play).not.toHaveBeenCalled()
    expect(client.createRetrospectivePlay).toHaveBeenCalledOnce()
    if (choice === 'keep') {
      await userEvent.click(screen.getByRole('button', { name: 'Keep current work' }))
      expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Unsaved plan stays')
      expect(screen.queryByText(/A Play was requested from another screen/)).not.toBeInTheDocument()
      expect(client.play).not.toHaveBeenCalled()
    } else {
      await userEvent.click(screen.getByRole('button', { name: 'All plays' }))
      await userEvent.click(screen.getByRole('button', { name: 'Discard changes' }))
      await waitFor(() => expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue(retrospectiveSavedFixture.summary.title))
      expect(client.play).toHaveBeenCalledWith(retrospectiveSavedFixture.summary.id, expect.any(AbortSignal))
      expect(screen.getByRole('region', { name: 'No original plan' })).toBeInTheDocument()
      expect(screen.queryByText(/A Play was requested from another screen/)).not.toBeInTheDocument()
    }
  })
  it('cancels retrospective creation on disconnect and never requests its late result', async () => {
    let finish!: (saved: typeof retrospectiveSavedFixture) => void
    const create = vi.fn().mockImplementation(() => new Promise<typeof retrospectiveSavedFixture>(resolve => { finish = resolve }))
    const client = api({ overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [{ ...accountFixture, venueId: 'hyperliquid' }] }),
      activity: vi.fn().mockResolvedValue({ ...activityFixture, operations: [retrospectiveFixture.operation], retainedFillCount: 2 }),
      retrospectivePreview: vi.fn().mockResolvedValue(retrospectiveFixture), createRetrospectivePlay: create })
    const disconnect = vi.fn()
    render(<ApplicationShell system={systemFixture} disconnect={disconnect} api={client} />)
    await userEvent.click(screen.getByRole('link', { name: 'Activity' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Expand BTC operation' }))
    await userEvent.click(screen.getByRole('button', { name: 'Inspect BTC retrospective eligibility' }))
    await userEvent.type(await screen.findByLabelText('Retrospective title'), 'Pending confirmation')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm retrospective Play' }))
    // Close the disclosure while its request is pending, then disconnect the actual shell.
    await userEvent.keyboard('{Escape}')
    await userEvent.click(screen.getByRole('button', { name: /^Disconnect .*session$/ }))
    expect(disconnect).toHaveBeenCalledOnce()
    expect((create.mock.calls[0]![1] as AbortSignal).aborted).toBe(true)
    await act(async () => finish(retrospectiveSavedFixture))
    expect(client.play).not.toHaveBeenCalled()
    expect(client.overview).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('heading', { name: 'Plays' })).not.toBeInTheDocument()
  })
  it('opens a saved play from Overview and preserves edits when reopening that same play', async () => {
    const client = api({
      overview: vi.fn().mockResolvedValue(overviewFixture),
      plays: vi.fn(playsFrom([{ ...savedPlayFixture.summary, status: 'planned' }])),
      play: vi.fn().mockResolvedValue(savedPlayFixture), evidence: vi.fn().mockResolvedValue([]),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Open Saved idea' }))
    const title = await screen.findByRole('textbox', { name: 'Play title' })
    expect(title).toHaveValue('Saved idea')
    await userEvent.clear(title)
    await userEvent.type(title, 'Unsaved revision')
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Open Saved idea' }))
    expect(await screen.findByRole('textbox', { name: 'Play title' })).toHaveValue('Unsaved revision')
    expect(client.play).toHaveBeenCalledOnce()
    expect(client.createPlay).not.toHaveBeenCalled()
    expect(client.updatePlay).not.toHaveBeenCalled()
  })
  it('does not overwrite a local draft when a different saved play is opened from Overview', async () => {
    const client = api({
      overview: vi.fn().mockResolvedValue(overviewFixture),
      plays: vi.fn(playsFrom([{ ...savedPlayFixture.summary, status: 'planned' }])),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByRole('button', { name: 'Open Saved idea' })
    await userEvent.click(screen.getByRole('link', { name: 'Plays' }))
    await userEvent.click(screen.getByRole('button', { name: 'New play' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Play title' }), 'Keep this draft')
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Open Saved idea' }))
    expect(await screen.findByText(/A Play was requested from another screen/)).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Keep this draft')
    expect(client.play).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Keep current work' }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Keep this draft')
    expect(client.play).not.toHaveBeenCalled()
  })
  it('reloads the Overview play list independently and keeps the rest of Overview visible on failure', async () => {
    const client = api({
      overview: vi.fn().mockResolvedValue(overviewFixture),
      plays: vi.fn(playsFrom([{ ...savedPlayFixture.summary, status: 'planned' }])).mockRejectedValueOnce(new ApiError('unavailable', 'Plays temporarily unavailable.')),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Plays temporarily unavailable.')
    expect(screen.getByRole('button', { name: 'View Main account' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }))
    expect(await screen.findByRole('button', { name: 'Open Saved idea' })).toBeInTheDocument()
    expect(client.plays).toHaveBeenCalledTimes(2)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
  it('opens Overview, not the sample Play page, and has honest empty states', async () => {
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api()} />)
    await screen.findByText('Start with your accounts.')
    expect(screen.getByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument()
    expect(screen.queryByText('BTC reclaim at support')).not.toBeInTheDocument()
    expect(await screen.findByText('No operations in this view')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Plays' })).toHaveAttribute('href', '#plays')
    expect(screen.queryByRole('region', { name: 'Play draft workspace' })).not.toBeInTheDocument()
    expect(screen.queryByText('Read-only by design')).not.toBeInTheDocument()
    expect(screen.queryByText('Private session')).not.toBeInTheDocument()
    expect(screen.queryByText('Perpetuals', { exact: true })).not.toBeInTheDocument()
    expect(screen.queryByRole('contentinfo')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Open navigation' })).toHaveAttribute('aria-controls', 'workspace-nav')
  })
  it('collapses and expands navigation with an accessible native control', async () => {
    const client = api()
    const { container } = render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText('Start with your accounts.')
    const collapse = screen.getByRole('button', { name: 'Collapse navigation' })
    expect(collapse.tagName).toBe('BUTTON')
    expect(collapse).toHaveAttribute('type', 'button')
    expect(collapse).toHaveAttribute('title', 'Collapse navigation')
    expect(collapse).toHaveAttribute('aria-controls', 'workspace-nav')
    expect(collapse).toHaveAttribute('aria-expanded', 'true')
    expect(container.firstElementChild).not.toHaveClass('navigation-collapsed')
    await userEvent.click(collapse)
    const expand = screen.getByRole('button', { name: 'Expand navigation' })
    expect(expand).toHaveAttribute('title', 'Expand navigation')
    expect(expand).toHaveAttribute('aria-controls', 'workspace-nav')
    expect(expand).toHaveAttribute('aria-expanded', 'false')
    expect(container.firstElementChild).toHaveClass('navigation-collapsed')
    expect(screen.getByRole('link', { name: 'Overview' })).toHaveAttribute('aria-current', 'page')
    await userEvent.keyboard(' ')
    expect(screen.getByRole('button', { name: 'Collapse navigation' })).toHaveFocus()
    expect(container.firstElementChild).not.toHaveClass('navigation-collapsed')
    expect(client.overview).toHaveBeenCalledOnce()
    expect(client.sync).not.toHaveBeenCalled()
  })
  it('keeps named rail links, tooltips, active markers and owner disconnect across pages', async () => {
    const disconnect = vi.fn()
    render(<ApplicationShell system={systemFixture} disconnect={disconnect} api={api({ overview: vi.fn().mockResolvedValue(overviewFixture) })} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }))
    expect(screen.getByRole('link', { name: 'Vessel overview' })).toHaveAttribute('title', 'Vessel overview')
    let previous = screen.getByRole('link', { name: 'Overview' })
    for (const name of ['Plays', 'Portfolios', 'Accounts', 'Activity', 'Settings', 'Overview']) {
      const link = screen.getByRole('link', { name })
      expect(link).toHaveAttribute('aria-label', name)
      expect(link).toHaveAttribute('title', name)
      expect(link).toHaveAttribute('href', `#${name.toLowerCase()}`)
      await userEvent.click(link)
      expect(link).toHaveAttribute('aria-current', 'page')
      expect(link).toHaveClass('active')
      expect(previous).not.toHaveAttribute('aria-current')
      expect(previous).not.toHaveClass('active')
      expect(screen.getByRole('button', { name: 'Expand navigation' })).toHaveAttribute('aria-expanded', 'false')
      previous = link
    }
    const strategies = screen.getByRole('link', { name: 'Strategies' })
    expect(strategies).toHaveAttribute('aria-disabled', 'true')
    expect(strategies).toHaveAttribute('title', 'Strategies are not available yet')
    expect(strategies).not.toHaveAttribute('href')
    expect(strategies).not.toHaveAttribute('tabindex')
    await userEvent.click(strategies)
    expect(previous).toHaveAttribute('aria-current', 'page')
    const owner = screen.getByRole('button', { name: "Disconnect Owner's session" })
    expect(owner).toHaveAttribute('title', "Disconnect Owner's browser session")
    await userEvent.click(owner)
    expect(disconnect).toHaveBeenCalledOnce()
  })
  it('starts expanded in a new shell session rather than persisting the rail preference', async () => {
    const client = api()
    const session = render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText('Start with your accounts.')
    await userEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }))
    session.unmount()
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText('Start with your accounts.')
    expect(screen.getByRole('button', { name: 'Collapse navigation' })).toHaveAttribute('aria-expanded', 'true')
    expect(screen.queryByRole('button', { name: 'Expand navigation' })).not.toBeInTheDocument()
  })
  it('opens Plays from navigation and retains its local draft through collapse and navigation', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue(overviewFixture) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByRole('heading', { level: 1, name: 'Overview' })
    await userEvent.click(screen.getByRole('link', { name: 'Plays' }))
    expect(screen.getByRole('heading', { level: 1, name: 'Plays' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Plays' })).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByRole('button', { name: 'Add account' })).not.toBeInTheDocument()
    await screen.findByText(/No saved plays yet/)
    await userEvent.click(screen.getByRole('button', { name: 'New play' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Play title' }), 'Local idea')
    await userEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Local idea')
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    expect(screen.queryByRole('textbox', { name: 'Play title' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Plays' }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Local idea')
    expect(screen.getByRole('button', { name: 'Expand navigation' })).toHaveAttribute('aria-expanded', 'false')
    await userEvent.click(screen.getByRole('button', { name: 'Expand navigation' }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Local idea')
    await userEvent.click(screen.getByRole('button', { name: 'Reload accounts' }))
    await waitFor(() => expect(client.overview).toHaveBeenCalledTimes(2))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Local idea')
    expect(client.sync).not.toHaveBeenCalled()
    expect(client.createAccount).not.toHaveBeenCalled()
    expect(client.updateAccount).not.toHaveBeenCalled()
  })
  it('supports a direct Plays hash without a sample record or saved-play claim', async () => {
    window.history.replaceState(null, '', '/#plays')
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api()} />)
    await screen.findByText(/No saved plays yet/)
    expect(screen.queryByText('BTC reclaim at support')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'New play' }))
    await screen.findByRole('button', { name: 'Account No enabled accounts' })
    expect(screen.getByRole('heading', { level: 1, name: 'Plays' })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('')
    expect(within(screen.getByRole('region', { name: 'Play draft workspace' })).getByText('Draft', { selector: '.badge' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /^Save$/ })).toBeDisabled()
  })
  it('creates an owner-scoped portfolio and reloads the workspace', async () => {
    const client = api()
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Create an optional portfolio' }))
    const dialog = screen.getByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText('Portfolio name'), 'Long-term perps')
    expect(within(dialog).getByRole('radio', { name: 'Folder' })).toBeChecked()
    await userEvent.click(within(dialog).getByRole('radio', { name: 'Mountain' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create portfolio' }))
    await waitFor(() => expect(client.createPortfolio).toHaveBeenCalledWith('Long-term perps', 'mountain'))
    await waitFor(() => expect(client.overview).toHaveBeenCalledTimes(2))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
  it('allows an unassigned account before any portfolio exists', async () => {
    const client = api()
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText('Start with your accounts.')
    expect(screen.getByRole('button', { name: 'Create an optional portfolio' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Add your first account' }))
    await userEvent.type(screen.getByLabelText('Account name'), 'Unassigned manual')
    await userEvent.click(screen.getByRole('radio', { name: 'Manual account' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add account' }))
    await waitFor(() => expect(client.createAccount).toHaveBeenCalledWith({ portfolioId: null, name: 'Unassigned manual', venueId: 'manual' }))
    expect(client.createPortfolio).not.toHaveBeenCalled()
    expect(client.sync).not.toHaveBeenCalled()
  })
  it('queues an initial venue sync after adding an account and keeps it running after the dialog closes', async () => {
    let finish!: () => void
    const client = api({
      createAccount: vi.fn().mockResolvedValue({ ...accountFixture, venueId: 'hyperliquid' }),
      sync: vi.fn().mockImplementation(() => new Promise<void>(resolve => { finish = resolve })),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Add your first account' }))
    await userEvent.type(screen.getByLabelText('Account name'), 'New wallet')
    await userEvent.type(screen.getByLabelText('Public wallet address'), `0x${'ab'.repeat(20)}`)
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add account' }))
    await waitFor(() => expect(client.sync).toHaveBeenCalledExactlyOnceWith(accountFixture.id, expect.any(AbortSignal)))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await act(async () => finish())
    await screen.findByText('1 of 1 accounts refreshed.')
  })
  it.each(['lighter', 'lighter-rh'])('automatically refreshes a newly imported %s account', async venueId => {
    const venue = { ...indexVenue, id: venueId }
    const client = api({
      discoverAccounts: vi.fn().mockResolvedValue({ ...discoveryFixture, venueId }),
      createAccount: vi.fn().mockResolvedValue({ ...indexAccount, venueId }),
    })
    const system = { ...systemFixture, venues: [venue, ...systemFixture.venues.filter(v => v.id !== venueId)] }
    render(<ApplicationShell system={system} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Add your first account' }))
    await userEvent.type(screen.getByLabelText('Public wallet address'), discoveryFixture.address)
    await userEvent.click(screen.getByRole('button', { name: 'Find wallet' }))
    await userEvent.click(await screen.findByRole('checkbox', { name: /Main account.*9007199254740993/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    await waitFor(() => expect(client.sync).toHaveBeenCalledExactlyOnceWith(indexAccount.id, expect.any(AbortSignal)))
    expect(client.saveAccountCredential).not.toHaveBeenCalled()
    expect(client.createAccount).toHaveBeenCalledTimes(1)
  })
  it('creates a manual account without fabricating an unavailable balance', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue({ ...emptyOverview, portfolios: [portfolioFixture] }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText('Start with your accounts.')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))
    await userEvent.click(screen.getByRole('menuitem', { name: 'Add account' }))
    const dialog = screen.getByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText('Account name'), 'Manual broker')
    await userEvent.click(within(dialog).getByRole('radio', { name: 'Manual account' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add account' }))
    await waitFor(() => expect(client.createAccount).toHaveBeenCalledWith({
      portfolioId: null, name: 'Manual broker', venueId: 'manual',
    }))
  })
  it('checks public-address syntax before attempting a venue connection record', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue({ ...emptyOverview, portfolios: [portfolioFixture] }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText('Start with your accounts.')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))
    await userEvent.click(screen.getByRole('menuitem', { name: 'Add account' }))
    await userEvent.type(screen.getByLabelText('Account name'), 'Wallet')
    await userEvent.type(screen.getByLabelText('Public wallet address'), 'not-a-private-key')
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('valid public wallet address')
    expect(client.createAccount).not.toHaveBeenCalled()
  })
  it('keeps the create dialog open with guidance when the venue address already exists', async () => {
    const duplicate = 'An account for this venue and address already exists. Manage or re-enable that account.'
    const client = api({
      overview: vi.fn().mockResolvedValue({ ...emptyOverview, portfolios: [portfolioFixture] }),
      createAccount: vi.fn().mockRejectedValue(new ApiError('http', duplicate, 409)),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText('Start with your accounts.')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))
    await userEvent.click(screen.getByRole('menuitem', { name: 'Add account' }))
    await userEvent.type(screen.getByLabelText('Account name'), 'Wallet copy')
    await userEvent.type(screen.getByLabelText('Public wallet address'), `0x${'ab'.repeat(20)}`)
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add account' }))
    expect(await within(screen.getByRole('dialog')).findByRole('alert')).toHaveTextContent('Manage or re-enable that account')
    expect(client.overview).toHaveBeenCalledTimes(1)
  })
  it('shows Reload progress and returns a repeated overview failure with its retry action', async () => {
    let fail: (cause: unknown) => void = () => {}
    const client = api({
      overview: vi.fn().mockRejectedValueOnce(new ApiError('unavailable', 'Overview is unavailable.'))
        .mockImplementationOnce(() => new Promise((_, reject) => { fail = reject })),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Overview is unavailable.')
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }))
    expect(screen.getByRole('button', { name: 'Reload' })).toHaveAttribute('aria-busy', 'true')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    fail(new ApiError('unavailable', 'Overview is still unavailable.'))
    expect(await screen.findByRole('alert')).toHaveTextContent('Overview is still unavailable.')
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })
  it('does not truncate a private-key-shaped value into a valid public address', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue({ ...emptyOverview, portfolios: [portfolioFixture] }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByText('Start with your accounts.')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))
    await userEvent.click(screen.getByRole('menuitem', { name: 'Add account' }))
    await userEvent.type(screen.getByLabelText('Account name'), 'Wallet')
    const longValue = `0x${'1'.repeat(64)}`
    await userEvent.type(screen.getByLabelText('Public wallet address'), longValue)
    expect(screen.getByLabelText('Public wallet address')).toHaveValue(longValue)
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('valid public wallet address')
    expect(client.createAccount).not.toHaveBeenCalled()
  })
  it('shows real account data and opens its detail even when the hash changes', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue(overviewFixture) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('button', { name: 'View Main account' }))
    fireEvent(window, new HashChangeEvent('hashchange'))
    await screen.findByRole('heading', { level: 1, name: 'Main account' })
    expect(await screen.findByText('No positions in the latest snapshot')).toBeInTheDocument()
    expect(client.snapshot).toHaveBeenCalledWith(accountFixture.id, expect.any(AbortSignal))
    expect(client.activity).toHaveBeenCalledWith({ accountId: accountFixture.id, page: 1, pageSize: 25 }, expect.any(AbortSignal))
  })
  it('retains previous data when a refresh fails and does not invent fills', async () => {
    const wallet = { ...accountFixture, venueId: 'hyperliquid', syncStatus: 'synced' as const, address: '0x' + '1'.repeat(40) }
    const client = api({ overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [wallet] }), sync: vi.fn().mockRejectedValue(new ApiError('http', 'Previous account data is still available.', 502)) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Refresh Main account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Previous account data')
    expect(screen.getByRole('button', { name: 'View Main account' })).toBeInTheDocument()
    expect(client.sync).toHaveBeenCalledWith(wallet.id, expect.any(AbortSignal))
  })
  it('keeps settings and disconnect available while core data is unavailable', async () => {
    const disconnect = vi.fn()
    render(<ApplicationShell system={systemFixture} disconnect={disconnect} api={api({ overview: vi.fn().mockRejectedValue(new ApiError('http', 'Database unavailable.', 503)) })} />)
    await screen.findByRole('alert')
    await userEvent.click(screen.getByRole('link', { name: 'Settings' }))
    expect(await screen.findByRole('heading', { name: 'Private session' })).toBeInTheDocument()
    const capabilities = screen.getByRole('heading', { name: 'Venue capabilities' }).closest('section')!
    expect(within(capabilities).getByText('Lighter · Robinhood')).toBeInTheDocument()
    expect(capabilities.querySelector('[data-logo="lighter-rh"] img')).not.toBeNull()
    expect(capabilities.querySelector('[data-logo="manual"] svg')).not.toBeNull()
    expect(capabilities.querySelector('[data-logo="generic"] svg')).not.toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Disconnect session' }))
    expect(disconnect).toHaveBeenCalledOnce()
  })
  it.each([false, true])('traps mobile navigation focus and restores it on Escape with desktop collapsed=%s', async collapsed => {
    vi.stubGlobal('innerWidth', 800)
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api()} />)
    await screen.findByText('Start with your accounts.')
    if (collapsed) await userEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }))
    const trigger = screen.getByRole('button', { name: 'Open navigation' })
    await userEvent.click(trigger)
    expect(screen.getByRole('dialog', { name: 'Main navigation' })).toBeInTheDocument()
    expect(trigger).toHaveAttribute('aria-expanded', 'true')
    expect(document.querySelector('.shell-main')).toHaveAttribute('inert')
    expect(screen.getByRole('link', { name: 'Vessel overview' })).toHaveFocus()
    await userEvent.tab({ shift: true })
    expect(screen.getByRole('button', { name: "Disconnect Owner's session" })).toHaveFocus()
    await userEvent.tab()
    expect(screen.getByRole('link', { name: 'Vessel overview' })).toHaveFocus()
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog', { name: 'Main navigation' })).not.toBeInTheDocument()
    expect(trigger).toHaveAttribute('aria-expanded', 'false')
    expect(trigger).toHaveFocus()
    expect(document.querySelector('.shell-main')).not.toHaveAttribute('inert')
    expect(screen.getByRole('button', { name: collapsed ? 'Expand navigation' : 'Collapse navigation' })).toHaveAttribute('aria-expanded', String(!collapsed))
  })
  it('closes the full-screen mobile drawer with its own button and restores scrolling and focus', async () => {
    vi.stubGlobal('innerWidth', 390)
    const previousOverflow = document.body.style.overflow
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api()} />)
    await screen.findByText('Start with your accounts.')
    const trigger = screen.getByRole('button', { name: 'Open navigation' })
    await userEvent.click(trigger)
    const drawer = screen.getByRole('dialog', { name: 'Main navigation' })
    expect(drawer).toHaveAttribute('aria-modal', 'true')
    expect(document.body.style.overflow).toBe('hidden')
    await userEvent.click(within(drawer).getByRole('button', { name: 'Close navigation' }))
    expect(screen.queryByRole('dialog', { name: 'Main navigation' })).not.toBeInTheDocument()
    expect(document.body.style.overflow).toBe(previousOverflow)
    expect(document.querySelector('.shell-main')).not.toHaveAttribute('inert')
    expect(trigger).toHaveFocus()
  })
  it('closes the mobile drawer on navigation and desktop resize without resetting the rail preference', async () => {
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={api()} />)
    await screen.findByText('Start with your accounts.')
    await userEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }))
    vi.stubGlobal('innerWidth', 800)
    const trigger = screen.getByRole('button', { name: 'Open navigation' })
    await userEvent.click(trigger)
    await userEvent.click(screen.getByRole('link', { name: 'Settings' }))
    expect(screen.queryByRole('dialog', { name: 'Main navigation' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Settings' })).toHaveAttribute('aria-current', 'page')
    expect(document.querySelector('.shell-main')).not.toHaveAttribute('inert')
    await userEvent.click(trigger)
    vi.stubGlobal('innerWidth', 801)
    fireEvent(window, new Event('resize'))
    expect(screen.queryByRole('dialog', { name: 'Main navigation' })).not.toBeInTheDocument()
    expect(document.querySelector('.shell-main')).not.toHaveAttribute('inert')
    expect(screen.getByRole('button', { name: 'Expand navigation' })).toHaveAttribute('aria-expanded', 'false')
  })
  it('keeps disabled accounts manageable but out of the enabled overview', async () => {
    const disabled = { ...accountFixture, isEnabled: false, venueId: 'hyperliquid', syncStatus: 'synced' as const }
    const fixture = { ...overviewFixture, accounts: [disabled],
      totals: { ...overviewFixture.totals, totalAccountValueUsd: null, valuedAccountCount: 0 } }
    const client = api({ overview: vi.fn().mockResolvedValue(fixture) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    expect(await screen.findByText('All accounts are disabled')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Manage accounts' }))
    expect(await screen.findByRole('button', { name: 'Manage Main account' })).toBeEnabled()
    expect(screen.getByRole('row', { name: /Main account/ })).toHaveTextContent('Disabled')
    expect(screen.getByRole('button', { name: 'Refresh Main account' })).toBeDisabled()
    await userEvent.click(screen.getByRole('button', { name: 'View Main account' }))
    expect(await screen.findByText('Imported movements hidden while disabled')).toBeInTheDocument()
    expect(screen.queryByText('No imported executions yet')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Manage account' }))
    expect(screen.getByLabelText('Account enabled')).not.toBeChecked()
  })
  it('does not create an unassigned portfolio tile and filters unassigned accounts out of portfolios', async () => {
    const unassigned = { ...accountFixture, id: 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff', name: 'Unassigned account', portfolioId: null }
    const client = api({ overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [accountFixture, unassigned] }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByRole('button', { name: 'View Unassigned account' })
    await userEvent.click(screen.getByRole('link', { name: 'Portfolios' }))
    expect(await screen.findByRole('button', { name: 'Open Swing trading' })).toBeInTheDocument()
    expect(document.querySelectorAll('.portfolio-tile')).toHaveLength(1)
    await userEvent.click(screen.getByRole('button', { name: 'Open Swing trading' }))
    expect(await screen.findByRole('button', { name: 'View Main account' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'View Unassigned account' })).not.toBeInTheDocument()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by portfolio' }), '')
    expect(await screen.findByRole('button', { name: 'View Unassigned account' })).toBeInTheDocument()
  })
  it('opens the portfolio manager and preserves grouping until the owner confirms a change', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue(overviewFixture) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('link', { name: 'Portfolios' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Manage portfolio Swing trading' }))
    expect(screen.getByLabelText('Portfolio name')).toHaveValue('Swing trading')
    expect(client.deletePortfolio).not.toHaveBeenCalled()
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
  it('shows available stablecoins rather than misleading zero perp equity for a unified wallet', async () => {
    const wallet = { ...accountFixture, venueId: 'hyperliquid', accountValueUsd: '0',
      availableBalanceUsd: '10.123456', availableBalanceSource: 'wallet' as const,
      availableStablecoinNominalUsd: '10.123456', accountMode: 'unifiedAccount', stablecoinScope: 'hypercore-spot-stablecoins' }
    const client = api({
      overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [wallet],
        totals: { ...overviewFixture.totals, totalAccountValueUsd: '0', availableStablecoinNominalUsd: '10.123456', stablecoinAccountCount: 1 } }),
      snapshot: vi.fn().mockResolvedValue({
        observedAtUtc: '2026-10-01T12:00:00Z', valueScope: 'primary-perpetual-dex',
        accountValueUsd: '0', withdrawableUsd: '0', marginUsedUsd: '0', positions: [],
        stablecoinWallet: { observedAtUtc: '2026-10-01T12:00:01Z', accountMode: 'unifiedAccount',
          scope: 'hypercore-spot-stablecoins', totalNominalUsd: '12.123456', availableNominalUsd: '10.123456',
          balances: [{ symbol: 'USDC', tokenIndex: 0, tokenId: 'known-token', total: '12.123456', held: '2', available: '10.123456' }],
          notice: 'Wallet funds, not guaranteed trading margin.' },
      }),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    expect(await screen.findByText('Available balance')).toBeInTheDocument()
    expect(screen.getByText('Perps equity $0.00')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'View Main account' }))
    expect(await screen.findByRole('heading', { name: 'Stablecoin wallet' })).toBeInTheDocument()
    expect(await screen.findByText('USDC')).toBeInTheDocument()
    expect(screen.getByText('Available token units')).toBeInTheDocument()
    expect(screen.getByText('10.123456')).toBeInTheDocument()
    expect(screen.getByText('Wallet funds, not guaranteed trading margin.')).toBeInTheDocument()
    // Perps equity stays a separate labelled figure, never summed with the wallet.
    expect(screen.getByText('Wallet stablecoins available').closest('.metric')).toHaveTextContent('Perps equity $0.00')
    // Both figures carry a pill named by its visible text, "wallet", with its own explanation as the description.
    const pill = (title: string) => within(screen.getByText(title).closest('.metric') as HTMLElement).getByRole('button', { name: 'wallet' })
    expect(pill('Wallet stablecoins available')).toHaveAccessibleDescription(/nominal total minus held$/)
    expect(pill('Available balance')).toHaveAccessibleDescription('Wallet total minus held · nominal')
    expect(screen.getByText('Perps withdrawable $0.00')).toBeInTheDocument()
  })
  it('names an unreported withdrawable amount and shows no wallet note for a venue without a wallet', async () => {
    const venue = { ...systemFixture.venues[0]!, id: 'risex', name: 'RISEx', tradeUrlTemplate: 'https://www.rise.trade/trade/{instrument}',
      priceRule: 'tick-size' as const, capabilities: { ...systemFixture.venues[0]!.capabilities, stream: false, stablecoinWallet: false } }
    const system = { ...systemFixture, venues: [venue, ...systemFixture.venues] }
    const risex = { ...accountFixture, venueId: 'risex', address: '0x1111111111111111111111111111111111111111', accountValueUsd: '12480.55',
      syncStatus: 'synced' as const, lastSyncedAtUtc: '2026-10-06T09:12:00Z' }
    const client = api({
      overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [risex] }),
      snapshot: vi.fn().mockResolvedValue({ observedAtUtc: '2026-10-06T09:12:00Z', valueScope: 'risex-perps-account',
        accountValueUsd: '12480.55', withdrawableUsd: null, marginUsedUsd: '1830.2', positions: [] }),
      fills: vi.fn().mockResolvedValue([]),
    })
    render(<ApplicationShell system={system} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'View Main account' }))
    expect(await screen.findByRole('button', { name: 'not reported' })).toHaveAccessibleDescription('Not reported by this venue')
    expect(screen.getByText('Account value · reported')).toBeInTheDocument()
    expect(screen.queryByText(/perpetuals equity:/)).not.toBeInTheDocument()
    expect(screen.queryByText(/risex-perps-account/)).not.toBeInTheDocument()
  })
  it('Reload retries failed detail reads when Overview metadata remains unchanged', async () => {
    const client = api({
      overview: vi.fn().mockImplementation(() => Promise.resolve(structuredClone(overviewFixture))),
      snapshot: vi.fn().mockRejectedValueOnce(new ApiError('unavailable', 'Temporary detail outage.')).mockResolvedValue(null),
      fills: vi.fn().mockResolvedValue([]),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'View Main account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Temporary detail outage')
    expect(client.snapshot).toHaveBeenCalledTimes(1)
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }))
    await waitFor(() => expect(client.snapshot).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(client.activity).toHaveBeenCalledTimes(3))
    expect(await screen.findByText('No positions in the latest snapshot')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
  it('Reload refreshes detail content even when account metadata is identical', async () => {
    const observation = {
      observedAtUtc: '2026-10-01T12:00:00Z', valueScope: 'primary-perpetual-dex',
      accountValueUsd: '4', withdrawableUsd: '3', marginUsedUsd: '1',
      positions: [{ contractId: 'Fresh-contract', signedQuantity: '1', entryPrice: '4', unrealizedPnlUsd: '0', marginUsedUsd: '1', leverage: 1 }],
    }
    const client = api({
      overview: vi.fn().mockImplementation(() => Promise.resolve(structuredClone(overviewFixture))),
      snapshot: vi.fn().mockResolvedValueOnce(null).mockResolvedValue(observation),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await userEvent.click(await screen.findByRole('button', { name: 'View Main account' }))
    await screen.findByText('No positions in the latest snapshot')
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }))
    expect(await screen.findByText('Fresh-contract')).toBeInTheDocument()
    expect(client.snapshot).toHaveBeenCalledTimes(2)
  })
  it('opens membership directly from an empty tile and reloads counts after a bulk move', async () => {
    const second = { ...portfolioFixture, id: '22222222-3333-4444-5555-666666666666', name: 'Second', accountCount: 0, balanceUsd: null }
    let current: Overview = { ...overviewFixture, portfolios: [portfolioFixture, second] }
    const client = api({
      overview: vi.fn().mockImplementation(() => Promise.resolve(current)),
      moveAccounts: vi.fn().mockImplementation((ids, portfolioId) => {
        const moved = current.accounts.map(account => ids.includes(account.id) ? { ...account, portfolioId, settingsRevision: account.settingsRevision! + 1 } : account)
        current = { ...current, accounts: moved, portfolios: current.portfolios.map(p => ({ ...p,
          accountCount: moved.filter(a => a.portfolioId === p.id).length,
          balanceUsd: moved.some(a => a.portfolioId === p.id) ? '1250.123456' : null,
        })) }
        return Promise.resolve(moved.filter(a => ids.includes(a.id)))
      }),
    })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(screen.getByRole('link', { name: 'Portfolios' }))
    await userEvent.click(screen.getByRole('button', { name: 'Add accounts to Second' }))
    expect(screen.getByRole('textbox', { name: 'Search portfolio accounts' })).toHaveFocus()
    await userEvent.click(screen.getByRole('button', { name: 'Close' }))
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }))
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Main account' }))
    await userEvent.selectOptions(screen.getByLabelText('Move to'), second.id)
    await userEvent.click(screen.getByRole('button', { name: 'Move' }))
    await waitFor(() => expect(client.overview).toHaveBeenCalledTimes(2))
    expect(await screen.findByText('1 account moved to the portfolio.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Portfolios' }))
    expect(screen.getByRole('button', { name: 'Open Second' })).toHaveTextContent('1 account record')
    expect(screen.getByRole('button', { name: 'Open Second' })).toHaveTextContent('$1,250.12')
    expect(screen.getByRole('button', { name: `Open ${portfolioFixture.name}` })).toHaveTextContent('0 account records')
    await userEvent.click(screen.getByRole('button', { name: 'Open Second' }))
    expect(screen.getByRole('combobox', { name: 'Filter by portfolio' })).toHaveValue(second.id)
    await userEvent.click(screen.getByRole('button', { name: 'Manage Main account' }))
    expect(screen.getByLabelText('Portfolio')).toHaveValue(second.id)
  })

})

describe('monitor incidents in the shell (Phase 0, 0.4a)', () => {
  const venueAccount = { ...accountFixture, venueId: 'hyperliquid', balanceUsd: '1250.123456' }
  const precedes = (a: Element, b: Element) => !!(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING)
  it('badges the Plays entry and adds an Overview row above the metric tiles, linking the affected Play and account', async () => {
    const play = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, venueId: 'hyperliquid', instrumentSource: 'venue' as const, status: 'open' as const } }
    const instrumentLevel = { ...discrepancyFixture, id: 'instrument', kind: 'position.excess', playId: null,
      details: { playIds: [], expectedQuantity: '0', venueQuantity: '2' } }
    const client = api({ overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [venueAccount] }),
      play: vi.fn().mockResolvedValue(play), evidence: vi.fn().mockResolvedValue([]),
      playExecution: vi.fn(() => new Promise<PlayExecution>(() => {})),
      monitor: vi.fn().mockResolvedValue({ ...monitorSummaryFixture, accounts: [{ ...monitorFixture, discrepancies: [
        missingStopFixture, instrumentLevel, { ...shortStopFixture, state: 'resolved' }] }] }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    const row = await screen.findByRole('region', { name: 'Monitor incidents' })
    expect(row).toHaveTextContent('2 incidents need attention')
    expect(within(row).getByText('1 critical · 1 warning')).toBeInTheDocument()
    // Above the metric tiles; Allocation stays between the tiles and Summary.
    const tiles = screen.getByRole('region', { name: 'Scoped overview totals' })
    expect(precedes(row, tiles)).toBe(true)
    expect(precedes(tiles, screen.getByRole('region', { name: 'Balance allocation' }))).toBe(true)
    expect(precedes(screen.getByRole('region', { name: 'Balance allocation' }), screen.getByRole('region', { name: 'Overview summary' }))).toBe(true)
    // The persistent count on the Plays entry keeps its name and describes the incidents.
    const plays = screen.getByRole('link', { name: 'Plays' })
    expect(within(plays).getByText('2')).toBeInTheDocument()
    expect(plays).toHaveAccessibleDescription('Needs attention: 1 critical · 1 warning')
    // An instrument-level record opens its account; a Play's record opens the Play.
    await userEvent.click(within(row).getByRole('button', { name: /^The venue position is larger than expected · BTC/ }))
    expect(await screen.findByRole('region', { name: 'Background monitor' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Overview' }))
    await userEvent.click(within(await screen.findByRole('region', { name: 'Monitor incidents' })).getByRole('button', { name: /^No stop protects the open position · BTC/ }))
    await waitFor(() => expect(client.play).toHaveBeenCalledWith(play.summary.id, expect.any(AbortSignal)))
    // The Play header points to the Execution tab with the same count.
    expect(await screen.findByRole('button', { name: 'Needs attention · 1 critical' })).toBeInTheDocument()
  })
  it('keeps the Overview row compact: the four most severe incidents, then how many more', async () => {
    const records = Array.from({ length: 6 }, (_, index) => ({ ...shortStopFixture, id: `w-${index}`, playId: `00000000-0000-4000-8000-00000000010${index}` }))
    const client = api({ overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [venueAccount] }),
      monitor: vi.fn().mockResolvedValue({ ...monitorSummaryFixture, accounts: [{ ...monitorFixture, discrepancies: [...records, missingStopFixture] }] }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    const row = await screen.findByRole('region', { name: 'Monitor incidents' })
    const items = within(row).getAllByRole('listitem')
    expect(items).toHaveLength(4)
    expect(items[0]).toHaveTextContent('No stop protects the open position · BTC')
    expect(items[0]).toHaveTextContent(venueAccount.name)
    expect(row).toHaveTextContent('and 3 more on the affected accounts')
    expect(within(screen.getByRole('link', { name: 'Plays' })).getByText('7')).toBeInTheDocument()
  })
  it('shows no incident row and no Plays badge when every record is resolved or dismissed', async () => {
    const client = api({ overview: vi.fn().mockResolvedValue({ ...overviewFixture, accounts: [venueAccount] }),
      monitor: vi.fn().mockResolvedValue({ ...monitorSummaryFixture, accounts: [{ ...monitorFixture, discrepancies: [
        { ...missingStopFixture, state: 'resolved' }, { ...shortStopFixture, state: 'dismissed' }] }] }) })
    render(<ApplicationShell system={systemFixture} disconnect={vi.fn()} api={client} />)
    await screen.findByRole('region', { name: 'Scoped overview totals' })
    await waitFor(() => expect(client.monitor).toHaveBeenCalled())
    await act(async () => {})
    expect(screen.queryByRole('region', { name: 'Monitor incidents' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Plays' })).not.toHaveAccessibleDescription(/Needs attention/)
  })
})
