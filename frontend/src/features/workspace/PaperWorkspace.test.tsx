import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Overview, WorkspaceApi } from '@/api/workspace'
import { VenuesProvider } from '@/api/venues'
import { systemFixture } from '@/test/system-fixture'
import { accountFixture, emptyOverview, playApiStubs, playCountsFrom, playsFrom, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { ApplicationShell } from './ApplicationShell'
import { CreateAccountDialog } from './CreateDialogs'
import { ManageAccountDialog, ManagePortfolioDialog } from './ManageDialogs'
import { activityPage, operationFixture } from '@/test/activity-fixture'
import { reviewFixture } from '@/test/review-fixture'
import { pickerAccounts } from './account-picker-options'
import { accountStatus } from '@/lib/format'

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

// The trading mode control is the brand subtitle (or its rail form); only these helpers know how to find it.
const modeNames = { real: 'Real journal', paper: 'Paper playground' } as const
type Mode = keyof typeof modeNames
const switchName = (from: Mode) => `${modeNames[from]}, switch to ${modeNames[from === 'real' ? 'paper' : 'real']}`
const tradingModeControl = () => screen.queryByRole('button', { name: /^(Real journal|Paper playground), switch to / })
async function switchTradingMode(user: { click: (element: Element) => Promise<void> }, mode: Mode) {
  await user.click(await screen.findByRole('button', { name: switchName(mode === 'paper' ? 'real' : 'paper') }))
}

describe('isolated Paper workspace', () => {
  it('shows mode context once in the header, not on accounts, portfolios, operations or Play rows, and resets history on a mode switch', async () => {
    const user = userEvent.setup()
    const summary = { ...savedPlayFixture.summary, tradingMode: 'paper' as const, accountId: paperAccount.id }
    const paper = api({ tradingMode: 'paper', overview: vi.fn().mockResolvedValue(paperOverview),
      activity: vi.fn().mockResolvedValue(activityPage([{ ...operationFixture, tradingMode: 'paper' }])),
      plays: vi.fn(playsFrom([summary])), playCounts: vi.fn(playCountsFrom([summary])) })
    const real = api()
    const root = api({ ...real, forTradingMode: vi.fn(mode => mode === 'paper' ? paper : real) })
    render(<ApplicationShell system={system} api={root} disconnect={vi.fn()} />)
    expect(screen.queryByRole('note')).not.toBeInTheDocument()
    await switchTradingMode(user, 'paper')
    const note = await screen.findByRole('note')
    expect(note.closest('.shell-header')).not.toBeNull()
    expect(note.querySelector('strong')).toHaveTextContent('Paper mode')
    expect(note).toHaveTextContent('Simulated capital and executions only. Real accounts and results are excluded. Zero fees and slippage; candle touches can be ambiguous.')
    expect(note.querySelector('.workspace-badge')).toBeNull()
    const row = await screen.findByRole('row', { name: /Paper hypothesis 1/ })
    expect(within(row).getByText('Enabled')).toBeInTheDocument()
    expect(within(row).queryByText('Paper')).not.toBeInTheDocument()
    expect(within(row).queryByText('Not refreshed yet')).not.toBeInTheDocument()
    expect(accountStatus({ ...paperAccount, isEnabled: false })).toBe('Disabled')
    expect(pickerAccounts([paperAccount], [paperPortfolio])[0]?.detail).toBe(paperPortfolio.name)
    await user.click(screen.getByRole('link', { name: 'Portfolios' }))
    const portfolio = await screen.findByRole('button', { name: `Open ${paperPortfolio.name}` })
    expect(within(portfolio).queryByText('Paper')).not.toBeInTheDocument()
    await user.click(screen.getByRole('link', { name: 'Activity' }))
    expect(await screen.findByRole('button', { name: 'Expand BTC operation' })).toBeInTheDocument()
    expect(within(screen.getByRole('main')).queryByText('Paper · simulated')).not.toBeInTheDocument()
    await user.click(screen.getByRole('link', { name: 'Plays' }))
    await user.click(await screen.findByRole('tab', { name: /^Drafts/ }))
    const playRow = await screen.findByRole('button', { name: `Open ${summary.title}` })
    expect(within(playRow).queryByText('Paper')).not.toBeInTheDocument()
    await switchTradingMode(user, 'real')
    expect(screen.queryByRole('note')).not.toBeInTheDocument()
    expect(within(screen.getByRole('navigation', { name: 'Breadcrumb' })).getAllByRole('listitem')).toHaveLength(1)
  })
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
    await switchTradingMode(user, 'paper')
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
    await switchTradingMode(user, 'real')
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
    await switchTradingMode(userEvent, 'paper')
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
    expect(tradingModeControl()).not.toBeInTheDocument()
    expect(within(screen.getByRole('link', { name: 'Vessel overview' })).getByText('The trading journal')).toBeInTheDocument()
  })
  it('protects unsaved Play work before remounting another mode', async () => {
    window.history.replaceState(null, '', '/#plays')
    const real = api(), paper = api({ tradingMode: 'paper' })
    const root = api({ ...real, forTradingMode: vi.fn(mode => mode === 'paper' ? paper : real) })
    render(<ApplicationShell system={system} api={root} disconnect={vi.fn()} />)
    await userEvent.click(await screen.findByRole('button', { name: 'New play' }))
    await userEvent.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Unsaved real thesis')
    const confirm = vi.spyOn(window, 'confirm')
    await switchTradingMode(userEvent, 'paper')
    const dialog = screen.getByRole('alertdialog', { name: 'Switch to Paper playground?' })
    expect(dialog).toHaveAccessibleDescription('Switch trading mode and discard unsaved Play work? Already-sent changes may still complete in their original mode.')
    expect(within(dialog).getByRole('button', { name: 'Keep editing' })).toHaveFocus()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Keep editing' }))
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Unsaved real thesis')
    await switchTradingMode(userEvent, 'paper')
    await userEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Discard and switch' }))
    expect(await screen.findByRole('heading', { name: 'Plays' })).toBeInTheDocument()
    expect(screen.queryByDisplayValue('Unsaved real thesis')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: switchName('paper') })).toBeInTheDocument()
    expect(confirm).not.toHaveBeenCalled()
  })
})

describe('trading mode lockup', () => {
  function modeApis(realOverrides: Partial<WorkspaceApi> = {}) {
    const real = api(realOverrides), paper = api({ tradingMode: 'paper', overview: vi.fn().mockResolvedValue(paperOverview) })
    const root = api({ ...real, forTradingMode: vi.fn(mode => mode === 'paper' ? paper : real) })
    return { real, paper, root }
  }
  const shellMode = () => document.querySelector('.journal-shell')!.getAttribute('data-trading-mode')
  const monogram = () => document.querySelector('.shell-monogram')!
  // The mode announcement lives outside the per-mode shell so it survives the remount.
  const modeAnnouncer = () => screen.getAllByRole('status').find(region => !region.closest('.journal-shell'))!

  it('names the action both ways in a button beside, not inside, the Overview link', async () => {
    const user = userEvent.setup()
    render(<ApplicationShell system={system} api={modeApis().root} disconnect={vi.fn()} />)
    const control = await screen.findByRole('button', { name: 'Real journal, switch to Paper playground' })
    const link = screen.getByRole('link', { name: 'Vessel overview' })
    expect(control).toHaveTextContent('Real journal')
    expect(control).toHaveAttribute('title', 'Real journal, switch to Paper playground')
    expect(control).not.toHaveAttribute('aria-pressed')
    expect(link).not.toContainElement(control)
    expect(link.parentElement).toContainElement(control)
    expect(within(link).queryByText('The trading journal')).not.toBeInTheDocument()
    await user.click(control)
    const back = await screen.findByRole('button', { name: 'Paper playground, switch to Real journal' })
    expect(back).toHaveTextContent('Paper playground')
    expect(back).not.toHaveAttribute('aria-pressed')
    expect(screen.getByRole('link', { name: 'Vessel overview' })).not.toContainElement(back)
    expect(shellMode()).toBe('paper')
  })

  it('keeps the plain subtitle and no control, even in the rail, when no venue simulates executions', async () => {
    const { root } = modeApis()
    render(<ApplicationShell system={systemFixture} api={root} disconnect={vi.fn()} />)
    await waitFor(() => expect(root.paperSettings).toHaveBeenCalled())
    await screen.findByRole('button', { name: 'Collapse navigation' })
    expect(tradingModeControl()).not.toBeInTheDocument()
    expect(within(screen.getByRole('link', { name: 'Vessel overview' })).getByText('The trading journal')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }))
    expect(tradingModeControl()).not.toBeInTheDocument()
    expect(shellMode()).toBe('real')
  })

  it('becomes a round swap button in the collapsed rail and keeps the rail across a switch', async () => {
    const user = userEvent.setup()
    render(<ApplicationShell system={system} api={modeApis().root} disconnect={vi.fn()} />)
    await screen.findByRole('button', { name: switchName('real') })
    await user.click(screen.getByRole('button', { name: 'Collapse navigation' }))
    const swap = screen.getByRole('button', { name: switchName('real') })
    expect(screen.getAllByRole('button', { name: switchName('real') })).toHaveLength(1)
    expect(swap).toHaveClass('shell-mode-swap')
    expect(swap).toHaveAttribute('title', switchName('real'))
    await user.click(swap)
    const next = await screen.findByRole('button', { name: switchName('paper') })
    expect(next).toHaveClass('shell-mode-swap')
    await waitFor(() => expect(next).toHaveFocus())
    expect(screen.getByRole('button', { name: 'Expand navigation' })).toHaveAttribute('aria-expanded', 'false')
    // The rail only applies to desktop widths: the narrow sheet keeps the subtitle.
    vi.stubGlobal('innerWidth', 800)
    fireEvent(window, new Event('resize'))
    expect(screen.getByRole('button', { name: switchName('paper') })).toHaveClass('shell-mode-switch')
  })

  it('focuses the new control and announces the mode in a live region that survives the remount', async () => {
    const user = userEvent.setup()
    render(<ApplicationShell system={system} api={modeApis().root} disconnect={vi.fn()} />)
    await screen.findByRole('button', { name: switchName('real') })
    const live = modeAnnouncer()
    expect(live).toHaveAttribute('aria-live', 'polite')
    expect(live).toBeEmptyDOMElement()
    await switchTradingMode(user, 'paper')
    await waitFor(() => expect(screen.getByRole('button', { name: switchName('paper') })).toHaveFocus())
    expect(live).toBeInTheDocument()
    expect(live).toHaveTextContent('Paper playground')
    await switchTradingMode(user, 'real')
    await waitFor(() => expect(screen.getByRole('button', { name: switchName('real') })).toHaveFocus())
    expect(modeAnnouncer()).toBe(live)
    expect(live).toHaveTextContent('Real journal')
  })

  it('animates the mark only after a switch made in this session, never on load or reload', async () => {
    const user = userEvent.setup()
    const session = render(<ApplicationShell system={system} api={modeApis().root} disconnect={vi.fn()} />)
    await screen.findByRole('button', { name: switchName('real') })
    expect(monogram()).not.toHaveClass('is-arriving')
    expect(monogram().querySelector('.is-leaving')).toBeNull()
    await switchTradingMode(user, 'paper')
    await screen.findByRole('button', { name: switchName('paper') })
    expect(monogram()).toHaveClass('is-arriving')
    expect(monogram().querySelectorAll('.mode-mark')).toHaveLength(2)
    expect(monogram().querySelector('.mode-mark.is-leaving')).not.toBeNull()
    session.unmount()
    render(<ApplicationShell system={system} api={modeApis().root} disconnect={vi.fn()} />)
    await screen.findByRole('button', { name: switchName('real') })
    expect(monogram()).not.toHaveClass('is-arriving')
    expect(monogram().querySelector('.is-leaving')).toBeNull()
  })

  it('asks before discarding Play work; Keep editing, Escape and outside clicks change nothing and return focus', async () => {
    window.history.replaceState(null, '', '/#plays')
    const user = userEvent.setup()
    const { root, paper } = modeApis()
    render(<ApplicationShell system={system} api={root} disconnect={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await user.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Unsaved real thesis')
    const control = screen.getByRole('button', { name: switchName('real') })
    await user.click(control)
    let dialog = screen.getByRole('alertdialog', { name: 'Switch to Paper playground?' })
    expect(within(dialog).getByRole('button', { name: 'Keep editing' })).toHaveFocus()
    expect(within(dialog).getAllByRole('button').map(button => button.textContent)).toEqual(['Keep editing', 'Discard and switch'])
    expect(control).toHaveAttribute('data-pending')
    await user.click(document.querySelector('[data-slot="dialog-overlay"]')!)
    expect(screen.getByRole('alertdialog')).toBe(dialog)
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() => expect(control).toHaveFocus())
    expect(control).not.toHaveAttribute('data-pending')
    await user.click(control)
    dialog = screen.getByRole('alertdialog', { name: 'Switch to Paper playground?' })
    await user.click(within(dialog).getByRole('button', { name: 'Keep editing' }))
    await waitFor(() => expect(control).toHaveFocus())
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Unsaved real thesis')
    expect(shellMode()).toBe('real')
    expect(modeAnnouncer()).toBeEmptyDOMElement()
    expect(paper.overview).not.toHaveBeenCalled()
  })

  it('switches after Discard and switch, then focuses the new control and announces it', async () => {
    window.history.replaceState(null, '', '/#plays')
    const user = userEvent.setup()
    const { root, paper } = modeApis()
    render(<ApplicationShell system={system} api={root} disconnect={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await user.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Unsaved real thesis')
    await switchTradingMode(user, 'paper')
    await user.click(within(screen.getByRole('alertdialog', { name: 'Switch to Paper playground?' })).getByRole('button', { name: 'Discard and switch' }))
    await waitFor(() => expect(screen.getByRole('button', { name: switchName('paper') })).toHaveFocus())
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(shellMode()).toBe('paper')
    expect(modeAnnouncer()).toHaveTextContent('Paper playground')
    expect(screen.queryByDisplayValue('Unsaved real thesis')).not.toBeInTheDocument()
    await waitFor(() => expect(paper.overview).toHaveBeenCalled())
  })

  it('re-checks the guard at confirm time: work that finished meanwhile still switches', async () => {
    window.history.replaceState(null, '', '/#plays')
    const user = userEvent.setup()
    let finish!: (play: typeof savedPlayFixture) => void
    const { root } = modeApis({ plays: vi.fn(playsFrom([savedPlayFixture.summary])), playCounts: vi.fn(playCountsFrom([savedPlayFixture.summary])),
      play: vi.fn(() => new Promise<typeof savedPlayFixture>(resolve => { finish = resolve })) })
    render(<ApplicationShell system={system} api={root} disconnect={vi.fn()} />)
    await user.click(await screen.findByRole('tab', { name: /^Drafts/ }))
    await user.click(await screen.findByRole('button', { name: `Open ${savedPlayFixture.summary.title}` }))
    // Opening is in flight, so the switch asks first.
    await switchTradingMode(user, 'paper')
    const dialog = screen.getByRole('alertdialog', { name: 'Switch to Paper playground?' })
    await act(async () => finish(savedPlayFixture))
    await user.click(within(dialog).getByRole('button', { name: 'Discard and switch' }))
    await waitFor(() => expect(shellMode()).toBe('paper'))
    expect(screen.queryByRole('textbox', { name: 'Play title' })).not.toBeInTheDocument()
  })

  it('re-checks the guard at confirm time: a confirmation left from a replaced connection switches nothing', async () => {
    window.history.replaceState(null, '', '/#plays')
    const user = userEvent.setup()
    const first = modeApis(), second = modeApis()
    const view = render(<ApplicationShell system={system} api={first.root} disconnect={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await user.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Unsaved real thesis')
    await switchTradingMode(user, 'paper')
    const dialog = screen.getByRole('alertdialog', { name: 'Switch to Paper playground?' })
    view.rerender(<ApplicationShell system={system} api={second.root} disconnect={vi.fn()} />)
    await waitFor(() => expect(second.root.paperSettings).toHaveBeenCalled())
    await user.click(within(dialog).getByRole('button', { name: 'Discard and switch' }))
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(await screen.findByRole('button', { name: switchName('real') })).toBeInTheDocument()
    expect(shellMode()).toBe('real')
    expect(first.paper.overview).not.toHaveBeenCalled()
    expect(second.paper.overview).not.toHaveBeenCalled()
  })

  it('lives in the narrow sheet: Escape in the dialog keeps the sheet, and a switch focuses the menu button', async () => {
    vi.stubGlobal('innerWidth', 390)
    window.history.replaceState(null, '', '/#plays')
    const user = userEvent.setup()
    render(<ApplicationShell system={system} api={modeApis().root} disconnect={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await user.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Unsaved real thesis')
    expect(document.querySelector('.header-mode')).toBeNull()
    await user.click(screen.getByRole('button', { name: 'Open navigation' }))
    const control = within(screen.getByRole('dialog', { name: 'Main navigation' })).getByRole('button', { name: switchName('real') })
    expect(control).toHaveClass('shell-mode-switch')
    await user.click(control)
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Main navigation' })).toBeInTheDocument()
    await waitFor(() => expect(control).toHaveFocus())
    await user.click(control)
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Discard and switch' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Open navigation' })).toHaveFocus())
    expect(screen.queryByRole('dialog', { name: 'Main navigation' })).not.toBeInTheDocument()
    const mark = document.querySelector('.header-mode')!
    expect(mark).toHaveAttribute('aria-hidden', 'true')
    expect(mark).toHaveTextContent('Paper')
    expect(mark.closest('.shell-header-row')).not.toBeNull()
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
    expect(screen.queryByText('Paper · simulated')).not.toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Hyperliquid' })).toBeInTheDocument()
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
    expect(screen.queryByText('Paper')).not.toBeInTheDocument()
    expect(document.querySelector('.account-source')).toHaveTextContent('Simulated executions')
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
  it('discloses the post-resume entry hold before disabling a paper account', async () => {
    render(<VenuesProvider venues={system.venues}><ManageAccountDialog api={api({ tradingMode: 'paper' })}
      account={paperAccount} portfolios={[paperPortfolio]} onClose={vi.fn()} onChanged={vi.fn()} /></VenuesProvider>)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Account enabled' }))
    expect(screen.getByText(/Disabling suspends this account/)).toHaveTextContent('re-enabling resumes at a fresh minute boundary')
    expect(screen.getByText(/Disabling suspends this account/)).toHaveTextContent('Any position open at suspension keeps new entries blocked after re-enabling until that exposure closes')
    expect(screen.getByText(/Disabling suspends this account/)).toHaveTextContent('Touches during the hold are declined, not replayed')
    expect(screen.getByText(/Disabling suspends this account/)).toHaveTextContent('unscored')
    expect(screen.getByText(/Disabling suspends this account/)).toHaveTextContent('Unevaluated time before disabling is also skipped and remains unscored.')
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
