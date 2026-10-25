import type { ManagementEvent } from './management-events'
import { useState } from 'react'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type BrokerAccount, type WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { VenuesProvider } from '@/api/venues'
import { render } from '@/test/render'
import { accountFixture, portfolioFixture } from '@/test/workspace-fixture'
import { discoveryFixture, indexAccount, indexVenue } from '@/test/account-access-fixture'
import { CreateAccountDialog, CreatePortfolioDialog } from './CreateDialogs'
import { ManageAccountDialog, ManagePortfolioDialog } from './ManageDialogs'
import { WalletAccountImport } from './WalletAccountImport'
import { AccountsPanel } from './AccountsPanel'
import { emptyAccountFilters } from './account-filters'
import { credentialsFixture } from '@/test/identity-fixture'

const otherPortfolio = { ...portfolioFixture, id: '22222222-3333-4444-5555-666666666666', name: 'Other', accountCount: 0 }
const otherAccount = { ...accountFixture, id: 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff', name: 'Other account', portfolioId: otherPortfolio.id, isEnabled: false }
const portfolios = [portfolioFixture, otherPortfolio]
const accounts = [accountFixture, otherAccount]
const expected = (records: BrokerAccount[]) => records.map(a => ({ accountId: a.id, settingsRevision: a.settingsRevision }))
function client(overrides: Partial<WorkspaceApi> = {}): WorkspaceApi {
  return { ...createWorkspaceApi(credentialsFixture),
    createPortfolio: vi.fn().mockResolvedValue(otherPortfolio), createAccount: vi.fn().mockResolvedValue(accountFixture),
    updatePortfolio: vi.fn().mockResolvedValue(portfolioFixture), moveAccounts: vi.fn().mockResolvedValue(accounts),
    accounts: vi.fn().mockResolvedValue(accounts), portfolios: vi.fn().mockResolvedValue(portfolios),
    discoverAccounts: vi.fn().mockResolvedValue(discoveryFixture), ...overrides }
}
function Panel({ api, records = accounts, changed = vi.fn(), reload = vi.fn() }: {
  api: WorkspaceApi; records?: BrokerAccount[]; changed?: (event: ManagementEvent) => void; reload?: () => void
}) {
  const [filters, setFilters] = useState(emptyAccountFilters)
  const [portfolioFilter, setPortfolioFilter] = useState('')
  return <AccountsPanel api={api} onChanged={changed} onReload={reload} accounts={records} portfolios={portfolios}
    filters={filters} onFiltersChange={setFilters} portfolioFilter={portfolioFilter} onPortfolioFilter={setPortfolioFilter}
    refreshing={null} refreshAction={null} onDetail={vi.fn()} onSync={vi.fn()} onManage={vi.fn()} onAdd={vi.fn()} />
}
async function newTarget() {
  await userEvent.selectOptions(screen.getByRole('combobox', { name: /^(Portfolio|Move to)/ }), 'new-portfolio')
  await userEvent.type(screen.getByRole('textbox', { name: 'New portfolio name' }), 'Other')
}

describe('Portfolio membership', () => {
  it('creates a portfolio with checked existing accounts and shows venue, current group and disabled status', async () => {
    const api = client(), created = vi.fn()
    render(<CreatePortfolioDialog open onOpenChange={vi.fn()} api={api} accounts={accounts} portfolios={portfolios} onCreated={created} />)
    expect(screen.getByText('Manual · Other · Disabled')).toBeInTheDocument()
    // A row says what checking it does only when it moves an account from another portfolio.
    expect(screen.queryByText('Moves from Other')).not.toBeInTheDocument()
    expect(screen.queryByText('History stays with each account.')).not.toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Portfolio name'), 'Together')
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Other account' }))
    expect(screen.getByText('Moves from Other')).toBeInTheDocument()
    expect(screen.getByText('History stays with each account.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Create portfolio' }))
    await waitFor(() => expect(api.createPortfolio).toHaveBeenCalledWith('Together', 'folder', [otherAccount.id], expected([otherAccount])))
    expect(created).toHaveBeenCalledOnce()
  })
  it('searches a long checklist without losing selections and drops disappeared accounts before create', async () => {
    const api = client(), records = Array.from({ length: 12 }, (_, i) => ({ ...otherAccount, id: String(i), name: `Record ${i}` }))
    const props = { open: true, onOpenChange: vi.fn(), api, portfolios, onCreated: vi.fn() }
    const view = render(<CreatePortfolioDialog {...props} accounts={records} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Record 0' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Search portfolio accounts' }), 'Record 11')
    expect(screen.getAllByRole('checkbox')).toHaveLength(1)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Record 11' }))
    view.rerender(<CreatePortfolioDialog {...props} accounts={records.slice(1)} />)
    await userEvent.type(screen.getByLabelText('Portfolio name'), 'Together')
    await userEvent.click(screen.getByRole('button', { name: 'Create portfolio' }))
    await waitFor(() => expect(api.createPortfolio).toHaveBeenCalledWith('Together', 'folder', ['11'], expected([records[11]!])))
  })
  it('saves additions as moves and removals as unassignment, retaining the rename/icon flow', async () => {
    const api = client(), changed = vi.fn()
    render(<ManagePortfolioDialog portfolio={portfolioFixture} api={api} accounts={accounts} portfolios={portfolios} onClose={vi.fn()} onChanged={changed} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Main account' }))
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Other account' }))
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(api.moveAccounts).toHaveBeenCalledTimes(2))
    expect(api.moveAccounts).toHaveBeenNthCalledWith(1, [otherAccount.id], portfolioFixture.id, expected([otherAccount]))
    expect(api.moveAccounts).toHaveBeenNthCalledWith(2, [accountFixture.id], null, expected([accountFixture]))
    expect(changed).toHaveBeenCalledWith({ kind: 'portfolio-updated', id: portfolioFixture.id, message: 'Portfolio updated.' })
  })
  it('rejects tab A removal after tab B moves the displayed A rev1 account to B rev2', async () => {
    let current = { ...accountFixture }
    let finishRename!: () => void
    const changed = vi.fn(), close = vi.fn(), stale = vi.fn()
    const api = client({
      updatePortfolio: vi.fn().mockResolvedValue(portfolioFixture).mockImplementationOnce(() => new Promise<void>(resolve => { finishRename = resolve })),
      moveAccounts: vi.fn().mockImplementation((_ids, _portfolioId, revisions) => {
        if (revisions[0].settingsRevision !== current.settingsRevision) throw new ApiError('http', 'Account settings changed elsewhere.', 409)
        throw new Error('Tab A must not overwrite tab B')
      }),
      accounts: vi.fn().mockImplementation(async () => [current, otherAccount]),
    })
    render(<ManagePortfolioDialog portfolio={portfolioFixture} api={api} accounts={accounts} portfolios={portfolios} onClose={close} onChanged={changed} onStale={stale} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Main account' }))
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    current = { ...accountFixture, portfolioId: otherPortfolio.id, settingsRevision: 2 }; finishRename()
    expect(await screen.findByRole('alert')).toHaveTextContent('Account settings changed elsewhere.')
    await waitFor(() => expect(api.accounts).toHaveBeenCalledOnce())
    expect(api.moveAccounts).toHaveBeenCalledExactlyOnceWith([accountFixture.id], null, expected([accountFixture]))
    expect(current.portfolioId).toBe(otherPortfolio.id); expect(current.settingsRevision).toBe(2)
    expect(screen.getByRole('checkbox', { name: 'Include Main account' })).not.toBeChecked()
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled()
    expect(stale).toHaveBeenCalledOnce(); expect(changed).not.toHaveBeenCalled(); expect(close).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Reload accounts' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(changed).toHaveBeenCalledOnce())
    expect(api.moveAccounts).toHaveBeenCalledOnce()
  })
  it('reports partially saved membership and requires a current read before retrying', async () => {
    const api = client({ moveAccounts: vi.fn().mockRejectedValueOnce(new ApiError('http', 'Target missing', 404)) }), stale = vi.fn()
    render(<ManagePortfolioDialog portfolio={portfolioFixture} api={api} accounts={accounts} portfolios={portfolios} onClose={vi.fn()} onChanged={vi.fn()} onStale={stale} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Other account' }))
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Some changes may have saved. Reload accounts before retrying.')
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled()
    await userEvent.click(screen.getByRole('button', { name: 'Reload accounts' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled())
    expect(api.portfolios).toHaveBeenCalledOnce(); expect(stale).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('checkbox', { name: 'Include Other account' })).not.toBeChecked()
  })
  it('keeps the displayed revision for a pending addition across shell reloads and refreshes lists on conflict', async () => {
    const latest = { ...otherAccount, portfolioId: null, settingsRevision: 2 }
    const stale = vi.fn(), changed = vi.fn()
    const api = client({ moveAccounts: vi.fn().mockRejectedValue(new ApiError('http', 'Conflict', 409)),
      accounts: vi.fn().mockResolvedValue([accountFixture, latest]) })
    const props = { portfolio: portfolioFixture, api, portfolios, onClose: vi.fn(), onChanged: changed, onStale: stale }
    const view = render(<ManagePortfolioDialog {...props} accounts={accounts} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Other account' }))
    view.rerender(<ManagePortfolioDialog {...props} accounts={[accountFixture, latest]} />)
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Account settings changed elsewhere.')
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Include Other account' })).not.toBeChecked())
    expect(api.moveAccounts).toHaveBeenCalledExactlyOnceWith([otherAccount.id], portfolioFixture.id, expected([otherAccount]))
    expect(api.accounts).toHaveBeenCalledOnce(); expect(api.portfolios).toHaveBeenCalledOnce()
    expect(stale).toHaveBeenCalledOnce(); expect(changed).not.toHaveBeenCalled()
  })
  it('keeps the selected row revision in the bulk bar and clears stale selections on conflict', async () => {
    const api = client({ moveAccounts: vi.fn().mockRejectedValue(new ApiError('http', 'Conflict', 409)) })
    const reload = vi.fn(), changed = vi.fn()
    const view = render(<Panel api={api} reload={reload} changed={changed} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Main account' }))
    view.rerender(<Panel api={api} reload={reload} changed={changed} records={[{ ...accountFixture, settingsRevision: 2 }, otherAccount]} />)
    await userEvent.click(screen.getByRole('button', { name: 'Move' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Account settings changed elsewhere. No accounts were moved.')
    expect(api.moveAccounts).toHaveBeenCalledExactlyOnceWith([accountFixture.id], null, expected([accountFixture]))
    expect(screen.queryByRole('form', { name: 'Move selected accounts' })).not.toBeInTheDocument()
    expect(reload).toHaveBeenCalledOnce(); expect(changed).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Main account' }))
    await userEvent.click(screen.getByRole('button', { name: 'Move' }))
    await waitFor(() => expect(api.moveAccounts).toHaveBeenCalledTimes(2))
    expect(api.moveAccounts).toHaveBeenLastCalledWith([accountFixture.id], null, [{ accountId: accountFixture.id, settingsRevision: 2 }])
  })
  it('does not create a portfolio from stale selected accounts and refreshes the checklist without replaying selection', async () => {
    const latest = { ...otherAccount, portfolioId: null, settingsRevision: 2 }
    const created = vi.fn(), close = vi.fn()
    const api = client({ createPortfolio: vi.fn().mockRejectedValue(new ApiError('http', 'Conflict', 409)),
      accounts: vi.fn().mockResolvedValue([accountFixture, latest]) })
    const props = { open: true, onOpenChange: close, api, portfolios, onCreated: created }
    const view = render(<CreatePortfolioDialog {...props} accounts={accounts} />)
    await userEvent.type(screen.getByLabelText('Portfolio name'), 'Together')
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Other account' }))
    view.rerender(<CreatePortfolioDialog {...props} accounts={[accountFixture, latest]} />)
    await userEvent.click(screen.getByRole('button', { name: 'Create portfolio' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('No portfolio was created or accounts moved.')
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Include Other account' })).not.toBeChecked())
    expect(api.createPortfolio).toHaveBeenCalledExactlyOnceWith('Together', 'folder', [otherAccount.id], expected([otherAccount]))
    expect(api.accounts).toHaveBeenCalledOnce(); expect(api.portfolios).toHaveBeenCalledOnce()
    expect(created).toHaveBeenCalledOnce(); expect(close).not.toHaveBeenCalled()
  })
  it('takes new portfolio lists while an account settings dialog is open', () => {
    const api = client(), props = { api, account: accountFixture, onClose: vi.fn(), onChanged: vi.fn() }
    const view = render(<ManageAccountDialog {...props} portfolios={[portfolioFixture]} />)
    view.rerender(<ManageAccountDialog {...props} portfolios={portfolios} />)
    expect(screen.getByRole('option', { name: 'Other' })).toBeInTheDocument()
  })
  it('creates a portfolio first for a new account and passes its id', async () => {
    const api = client(), created = vi.fn()
    render(<CreateAccountDialog open onOpenChange={vi.fn()} api={api} portfolios={portfolios} onCreated={created} />)
    await userEvent.click(screen.getByRole('radio', { name: 'Manual account' }))
    await userEvent.type(screen.getByLabelText('Account name'), 'Added')
    await newTarget(); await userEvent.click(screen.getByRole('button', { name: 'Add account' }))
    await waitFor(() => expect(api.createAccount).toHaveBeenCalledWith({ portfolioId: otherPortfolio.id, name: 'Added', venueId: 'manual' }))
    expect(api.createPortfolio).toHaveBeenCalledExactlyOnceWith('Other'); expect(created).toHaveBeenCalledTimes(2)
  })
  it('retains a portfolio on failed account creation and retries without duplicating it before shell reload completes', async () => {
    const api = client({ createAccount: vi.fn().mockRejectedValueOnce(new ApiError('http', 'Account unavailable', 503)).mockResolvedValueOnce(accountFixture) }), created = vi.fn()
    render(<CreateAccountDialog open onOpenChange={vi.fn()} api={api} portfolios={[]} onCreated={created} />)
    await userEvent.click(screen.getByRole('radio', { name: 'Manual account' }))
    await userEvent.type(screen.getByLabelText('Account name'), 'Added')
    await newTarget(); await userEvent.click(screen.getByRole('button', { name: 'Add account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The new portfolio was created and kept.')
    expect(screen.getByRole('combobox', { name: /Portfolio/ })).toHaveValue(otherPortfolio.id)
    expect(created).toHaveBeenCalledOnce()
    await userEvent.click(screen.getByRole('button', { name: 'Add account' }))
    await waitFor(() => expect(api.createAccount).toHaveBeenCalledTimes(2)); expect(api.createPortfolio).toHaveBeenCalledOnce()
    expect(api.createAccount).toHaveBeenLastCalledWith({ portfolioId: otherPortfolio.id, name: 'Added', venueId: 'manual' })
  })
  it('does not create an account when the new portfolio request fails', async () => {
    const api = client({ createPortfolio: vi.fn().mockRejectedValue(new ApiError('http', 'Cannot create', 503)) })
    render(<CreateAccountDialog open onOpenChange={vi.fn()} api={api} portfolios={[]} onCreated={vi.fn()} />)
    await userEvent.click(screen.getByRole('radio', { name: 'Manual account' })); await userEvent.type(screen.getByLabelText('Account name'), 'Added')
    await newTarget(); await userEvent.click(screen.getByRole('button', { name: 'Add account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot create'); expect(api.createAccount).not.toHaveBeenCalled()
  })
  it('selects only visible rows and drops hidden selections permanently', async () => {
    render(<Panel api={client()} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select all visible accounts' }))
    expect(screen.getByText('2 selected')).toBeInTheDocument()
    await userEvent.type(screen.getByRole('textbox', { name: 'Search accounts' }), 'Other')
    expect(screen.getByText('1 selected')).toBeInTheDocument()
    await userEvent.clear(screen.getByRole('textbox', { name: 'Search accounts' }))
    expect(screen.getByRole('checkbox', { name: 'Select Main account' })).not.toBeChecked()
    expect(screen.getByRole('checkbox', { name: 'Select Other account' })).toBeChecked()
    await userEvent.click(screen.getByRole('button', { name: 'Clear' })); expect(screen.queryByRole('form', { name: 'Move selected accounts' })).not.toBeInTheDocument()
  })
  it('drops accounts that disappear on reload', async () => {
    const api = client(), view = render(<Panel api={api} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select all visible accounts' }))
    view.rerender(<Panel api={api} records={[otherAccount]} />)
    expect(screen.getByText('1 selected')).toBeInTheDocument()
  })
  it('bulk moves disabled and enabled accounts, notifies and clears selection', async () => {
    const api = client(), changed = vi.fn()
    render(<Panel api={api} changed={changed} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select all visible accounts' }))
    await userEvent.selectOptions(screen.getByLabelText('Move to'), otherPortfolio.id)
    await userEvent.click(screen.getByRole('button', { name: 'Move' }))
    await waitFor(() => expect(api.moveAccounts).toHaveBeenCalledExactlyOnceWith([accountFixture.id, otherAccount.id], otherPortfolio.id, expected(accounts)))
    expect(changed).toHaveBeenCalledWith({ kind: 'accounts-moved', ids: [accountFixture.id, otherAccount.id], message: '2 accounts moved to the portfolio.' })
    expect(screen.queryByRole('form', { name: 'Move selected accounts' })).not.toBeInTheDocument()
  })
  it('bulk unlinks to a literal null target', async () => {
    const api = client(); render(<Panel api={api} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Main account' }))
    await userEvent.click(screen.getByRole('button', { name: 'Move' }))
    await waitFor(() => expect(api.moveAccounts).toHaveBeenCalledExactlyOnceWith([accountFixture.id], null, expected([accountFixture])))
  })
  it('keeps a newly created bulk destination on failure, disables controls while moving, and retries safely', async () => {
    let reject!: (cause: Error) => void
    const api = client({ moveAccounts: vi.fn().mockImplementationOnce(() => new Promise((_resolve, no) => { reject = no })).mockResolvedValueOnce(accounts) }), reload = vi.fn()
    render(<Panel api={api} reload={reload} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Main account' })); await newTarget()
    await userEvent.click(screen.getByRole('button', { name: 'Move' }))
    await waitFor(() => expect(api.moveAccounts).toHaveBeenCalledOnce())
    expect(screen.getByRole('checkbox', { name: 'Select Main account' })).toBeDisabled()
    expect(screen.getByRole('combobox', { name: 'Move to' })).toBeDisabled()
    reject(new ApiError('http', 'Move failed', 503))
    expect(await screen.findByRole('alert')).toHaveTextContent('The new portfolio was created and kept.')
    expect(screen.getByLabelText('Move to')).toHaveValue(otherPortfolio.id); expect(reload).toHaveBeenCalledTimes(2)
    await userEvent.click(screen.getByRole('button', { name: 'Move' }))
    await waitFor(() => expect(api.moveAccounts).toHaveBeenCalledTimes(2)); expect(api.createPortfolio).toHaveBeenCalledOnce()
  })
  it('creates one portfolio for all selected Lighter imports', async () => {
    const api = client({ createAccount: vi.fn().mockResolvedValue(indexAccount) }), created = vi.fn()
    render(<VenuesProvider venues={[indexVenue]}><WalletAccountImport venueId="lighter" api={api} portfolios={[]} accounts={[]}
      onCreated={created} onBusy={vi.fn()} onClose={vi.fn()} /></VenuesProvider>)
    await userEvent.type(screen.getByLabelText('Public wallet address'), discoveryFixture.address)
    await userEvent.click(screen.getByRole('button', { name: 'Find wallet' })); await screen.findByRole('heading', { name: 'Choose accounts' })
    for (const checkbox of screen.getAllByRole('checkbox')) await userEvent.click(checkbox)
    await newTarget(); await userEvent.click(screen.getByRole('button', { name: 'Import selected (2)' }))
    await waitFor(() => expect(api.createAccount).toHaveBeenCalledTimes(2))
    expect(api.createPortfolio).toHaveBeenCalledExactlyOnceWith('Other')
    for (const [body] of vi.mocked(api.createAccount).mock.calls) expect(body.portfolioId).toBe(otherPortfolio.id)
    expect(created).toHaveBeenCalledTimes(3)
  })
  it('keeps the new Lighter portfolio after import failure and reuses it after rediscovery', async () => {
    const api = client({ createAccount: vi.fn().mockRejectedValueOnce(new Error('Import failed')).mockResolvedValueOnce(indexAccount) }), created = vi.fn()
    render(<VenuesProvider venues={[indexVenue]}><WalletAccountImport venueId="lighter" api={api} portfolios={[]} accounts={[]}
      onCreated={created} onBusy={vi.fn()} onClose={vi.fn()} /></VenuesProvider>)
    await userEvent.type(screen.getByLabelText('Public wallet address'), discoveryFixture.address)
    await userEvent.click(screen.getByRole('button', { name: 'Find wallet' })); await screen.findByRole('heading', { name: 'Choose accounts' })
    await userEvent.click(screen.getAllByRole('checkbox')[0]!); await newTarget()
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The new portfolio was created and kept.')
    expect(screen.getByRole('combobox', { name: /Portfolio/ })).toHaveValue(otherPortfolio.id); expect(created).toHaveBeenCalledTimes(2)
    await userEvent.click(screen.getByRole('button', { name: 'Find wallet' })); await screen.findByRole('heading', { name: 'Choose accounts' })
    await userEvent.click(screen.getAllByRole('checkbox')[0]!); await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    await waitFor(() => expect(api.createAccount).toHaveBeenCalledTimes(2)); expect(api.createPortfolio).toHaveBeenCalledOnce()
    expect(api.createAccount).toHaveBeenLastCalledWith(expect.objectContaining({ portfolioId: otherPortfolio.id }))
  })
  it('reconciles current memberships on shell reload without discarding checklist edits', async () => {
    const api = client(), props = { portfolio: portfolioFixture, api, portfolios, onClose: vi.fn(), onChanged: vi.fn() }
    const newcomer = { ...otherAccount, id: 'cccccccc-dddd-eeee-ffff-aaaaaaaaaaaa', name: 'New member', portfolioId: portfolioFixture.id }
    const view = render(<ManagePortfolioDialog {...props} accounts={accounts} />)
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Main account' }))
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include Other account' }))
    view.rerender(<ManagePortfolioDialog {...props} accounts={[...accounts, newcomer]} />)
    expect(screen.getByRole('checkbox', { name: 'Include Main account' })).not.toBeChecked()
    expect(screen.getByRole('checkbox', { name: 'Include Other account' })).toBeChecked()
    expect(screen.getByRole('checkbox', { name: 'Include New member' })).toBeChecked()
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include New member' }))
    api.accounts = vi.fn().mockResolvedValue([...accounts, newcomer])
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(api.moveAccounts).toHaveBeenCalledTimes(2))
    expect(api.moveAccounts).toHaveBeenNthCalledWith(2, [accountFixture.id, newcomer.id], null, expected([accountFixture, newcomer]))
  })
  it('never silently substitutes Unassigned when a selected portfolio disappears', async () => {
    const api = client(), props = { open: true, onOpenChange: vi.fn(), api, onCreated: vi.fn() }
    const view = render(<CreateAccountDialog {...props} portfolios={portfolios} />)
    await userEvent.click(screen.getByRole('radio', { name: 'Manual account' }))
    await userEvent.type(screen.getByLabelText('Account name'), 'Added')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: /Portfolio/ }), otherPortfolio.id)
    view.rerender(<CreateAccountDialog {...props} portfolios={[portfolioFixture]} />)
    await userEvent.click(screen.getByRole('button', { name: 'Add account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The selected portfolio is no longer available.')
    expect(api.createAccount).not.toHaveBeenCalled()
  })

  it('stops retaining a confirmed inline portfolio once it appeared in a reload and was later deleted', async () => {
    const api = client({ createAccount: vi.fn().mockRejectedValue(new ApiError('http', 'Account unavailable', 503)) })
    const props = { open: true, onOpenChange: vi.fn(), api, onCreated: vi.fn() }
    const view = render(<CreateAccountDialog {...props} portfolios={[]} />)
    await userEvent.click(screen.getByRole('radio', { name: 'Manual account' }))
    await userEvent.type(screen.getByLabelText('Account name'), 'Added'); await newTarget()
    await userEvent.click(screen.getByRole('button', { name: 'Add account' }))
    await screen.findByRole('alert')
    view.rerender(<CreateAccountDialog {...props} portfolios={[otherPortfolio]} />)
    view.rerender(<CreateAccountDialog {...props} portfolios={[]} />)
    await userEvent.click(screen.getByRole('button', { name: 'Add account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The selected portfolio is no longer available.')
    expect(api.createAccount).toHaveBeenCalledOnce(); expect(api.createPortfolio).toHaveBeenCalledOnce()
  })

})
