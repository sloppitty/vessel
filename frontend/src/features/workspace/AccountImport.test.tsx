import { useState } from 'react'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type BrokerAccount, type WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { VenuesProvider } from '@/api/venues'
import { credentialFixture, discoveryFixture, indexAccount, indexVenue } from '@/test/account-access-fixture'
import { portfolioFixture } from '@/test/workspace-fixture'
import { systemFixture } from '@/test/system-fixture'
import { CreateAccountDialog } from './CreateDialogs'
import { credentialsFixture } from '@/test/identity-fixture'

beforeEach(() => vi.stubEnv('DEV', false))

/**
 * A Retry-After deadline `seconds` from now, with the clock held there: the notice counts down from the moment it
 * renders, and a slow run must not turn 60 into 59.
 */
function frozenRetryAt(seconds: number) {
  const now = Date.now()
  vi.spyOn(Date, 'now').mockReturnValue(now)
  return new Date(now + seconds * 1000).toISOString()
}

function client(overrides: Partial<WorkspaceApi> = {}): WorkspaceApi {
  return {
    ...createWorkspaceApi(credentialsFixture),
    discoverAccounts: vi.fn().mockResolvedValue(discoveryFixture),
    createAccount: vi.fn().mockImplementation(body => Promise.resolve({
      ...indexAccount, ...body, id: body.sourceId === indexAccount.sourceId ? indexAccount.id : portfolioFixture.id,
    })),
    saveAccountCredential: vi.fn().mockResolvedValue(credentialFixture),
    sync: vi.fn(),
    ...overrides,
  }
}
function Fixture({ api, accounts = [], onCreated = vi.fn() }: {
  api: WorkspaceApi; accounts?: BrokerAccount[]; onCreated?: (account?: BrokerAccount) => void
}) {
  const [open, setOpen] = useState(true)
  return <VenuesProvider venues={[indexVenue, ...systemFixture.venues.filter(v => v.id !== indexVenue.id)]}>
    <button onClick={() => setOpen(true)}>Open import</button>
    <CreateAccountDialog open={open} onOpenChange={setOpen} api={api} portfolios={[portfolioFixture]} accounts={accounts} onCreated={onCreated} />
  </VenuesProvider>
}
async function discover() {
  await userEvent.type(screen.getByLabelText('Public wallet address'), discoveryFixture.address)
  await userEvent.click(screen.getByRole('button', { name: 'Find wallet' }))
  await screen.findByRole('heading', { name: 'Choose accounts' })
}
async function selectMain() {
  await userEvent.click(screen.getByRole('checkbox', { name: /Main account.*9007199254740993/ }))
}

describe('Wallet account import', () => {
  it('shows the public discovery wait instead of a generic failure', async () => {
    const api = client({ discoverAccounts: vi.fn().mockRejectedValue(new ApiError('http', 'Generic failure.', 429,
      frozenRetryAt(60))) })
    render(<Fixture api={api} />)
    await userEvent.type(screen.getByLabelText('Public wallet address'), discoveryFixture.address)
    await userEvent.click(screen.getByRole('button', { name: 'Find wallet' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Wait 60 seconds before trying again.')
    expect(api.discoverAccounts).toHaveBeenCalledOnce()
  })

  it('shows the token-backed name-discovery wait without losing selections', async () => {
    const api = client({ discoverAccounts: vi.fn().mockResolvedValueOnce(discoveryFixture)
      .mockRejectedValueOnce(new ApiError('http', 'Private credential detail.', 429, frozenRetryAt(60))) })
    render(<Fixture api={api} />)
    await discover()
    await selectMain()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:test-only')
    await userEvent.click(screen.getByRole('button', { name: 'Load Lighter names' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Wait 60 seconds before trying again.')
    expect(screen.getByRole('checkbox', { name: /Main account.*9007199254740993/ })).toBeChecked()
    expect(screen.queryByText(/Private credential detail/)).not.toBeInTheDocument()
    expect(api.discoverAccounts).toHaveBeenCalledTimes(2)
  })

  it('keeps an imported account when token verification is rate limited and shows the wait', async () => {
    const api = client({ saveAccountCredential: vi.fn().mockRejectedValue(new ApiError('http', 'Private credential detail.', 429,
      frozenRetryAt(60))) })
    const onCreated = vi.fn()
    render(<Fixture api={api} onCreated={onCreated} />)
    await discover()
    await selectMain()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:test-only')
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    expect(await screen.findByText(/Wait 60 seconds before trying again/)).toHaveTextContent('Imported.')
    expect(onCreated).toHaveBeenCalledWith(expect.objectContaining({ id: indexAccount.id }))
    expect(api.saveAccountCredential).toHaveBeenCalledOnce()
    expect(screen.queryByText(/Private credential detail/)).not.toBeInTheDocument()
  })

  it.each(['close', 'unmount', 'API replacement'] as const)('aborts wallet discovery on %s and ignores a late result', async action => {
    let resolve!: (value: typeof discoveryFixture) => void
    const api = client({ discoverAccounts: vi.fn().mockImplementation(() => new Promise(accept => { resolve = accept })) })
    const view = render(<Fixture api={api} />)
    await userEvent.type(screen.getByLabelText('Public wallet address'), discoveryFixture.address)
    await userEvent.click(screen.getByRole('button', { name: 'Find wallet' }))
    const signal = vi.mocked(api.discoverAccounts).mock.calls[0]![2]!
    expect(signal.aborted).toBe(false)
    if (action === 'close') {
      await userEvent.click(screen.getByRole('button', { name: 'Close' }))
      await userEvent.click(screen.getByRole('button', { name: 'Open import' }))
    } else if (action === 'unmount') view.unmount()
    else view.rerender(<Fixture api={client()} />)
    expect(signal.aborted).toBe(true)
    await act(async () => resolve(discoveryFixture))
    expect(screen.queryByRole('heading', { name: 'Choose accounts' })).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    if (action !== 'unmount') expect(screen.getByLabelText('Public wallet address')).toHaveValue('')
  })

  it.each(['close', 'unmount', 'API replacement'] as const)('aborts token-backed name discovery on %s and ignores a late error', async action => {
    let reject!: (reason: Error) => void
    const api = client({ discoverAccounts: vi.fn().mockResolvedValueOnce(discoveryFixture)
      .mockImplementationOnce(() => new Promise((_accept, fail) => { reject = fail })) })
    const view = render(<Fixture api={api} />)
    await discover()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:test-only')
    await userEvent.click(screen.getByRole('button', { name: 'Load Lighter names' }))
    const signal = vi.mocked(api.discoverAccounts).mock.calls[1]![2]!
    expect(screen.getByLabelText('Read-only token')).toHaveValue('')
    if (action === 'close') {
      await userEvent.click(screen.getByRole('button', { name: 'Close' }))
      await userEvent.click(screen.getByRole('button', { name: 'Open import' }))
    } else if (action === 'unmount') view.unmount()
    else view.rerender(<Fixture api={client()} />)
    expect(signal.aborted).toBe(true)
    await act(async () => reject(new Error('private upstream credential')))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByText(/private upstream|Could not load venue names/)).not.toBeInTheDocument()
  })

  it('shows collateral and available balance separately, including zero and exact signed values', async () => {
    const api = client({ discoverAccounts: vi.fn().mockResolvedValue({ ...discoveryFixture, accounts: [
      { ...discoveryFixture.accounts[0], collateralUsd: '400.123456789', availableBalanceUsd: '-12.5000' },
      { ...discoveryFixture.accounts[1], collateralUsd: '0.00', availableBalanceUsd: null },
    ] }) })
    render(<Fixture api={api} />)
    await discover()
    expect(screen.getByText('Collateral · $400.12')).toHaveAttribute('title', '400.123456789')
    expect(screen.getByText('Available · -$12.50')).toHaveAttribute('title', '-12.5000')
    expect(screen.getByText('Collateral · $0.00')).toBeInTheDocument()
    expect(screen.getByText('Available · Unavailable')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'About these balances' })).toHaveAccessibleDescription(/not total equity/)
  })
  it('loads venue names before selection, clears the token and imports with the venue name', async () => {
    const api = client({ discoverAccounts: vi.fn().mockResolvedValueOnce(discoveryFixture).mockResolvedValueOnce({
      ...discoveryFixture, accounts: discoveryFixture.accounts.map(a => ({ ...a, name: a.accountType === 'main' ? 'Core holdings' : 'Swing / ETH' })),
    }) })
    render(<Fixture api={api} />)
    await discover()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:test-only')
    await userEvent.click(screen.getByRole('button', { name: 'Load Lighter names' }))
    await screen.findByText('Swing / ETH')
    expect(api.discoverAccounts).toHaveBeenLastCalledWith('lighter', discoveryFixture.address, expect.any(AbortSignal), 'ro:test-only')
    expect(screen.getByLabelText('Read-only token')).toHaveValue('')
    expect(api.saveAccountCredential).not.toHaveBeenCalled()
    expect(api.createAccount).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('checkbox', { name: /Swing \/ ETH/ }))
    expect(screen.getByLabelText('Account name · 9223372036854775807')).toHaveValue('Swing / ETH')
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    await screen.findByText('Imported.')
    expect(api.createAccount).toHaveBeenCalledWith(expect.objectContaining({ sourceId: '9223372036854775807', name: 'Swing / ETH' }))
    expect(api.sync).not.toHaveBeenCalled()
  })
  it('preserves edited names and selections across name lookup, and keeps them on failure', async () => {
    const api = client({ discoverAccounts: vi.fn().mockResolvedValueOnce(discoveryFixture).mockResolvedValueOnce({
      ...discoveryFixture, accounts: discoveryFixture.accounts.map(a => ({ ...a, name: 'Venue label ' + a.sourceId })),
    }).mockRejectedValueOnce(new Error('ro:must-not-echo')) })
    render(<Fixture api={api} />)
    await discover(); await selectMain()
    const input = screen.getByLabelText('Account name · 9007199254740993')
    await userEvent.clear(input); await userEvent.type(input, 'My custom name')
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:test-only')
    await userEvent.click(screen.getByRole('button', { name: 'Load Lighter names' }))
    await screen.findByText(/Venue names loaded/)
    expect(input).toHaveValue('My custom name')
    expect(screen.getAllByRole('checkbox')[0]).toBeChecked()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:must-not-echo')
    await userEvent.click(screen.getByRole('button', { name: 'Load Lighter names' }))
    await screen.findByRole('alert')
    expect(input).toHaveValue('My custom name')
    expect(screen.getByLabelText('Read-only token')).toHaveValue('')
    expect(screen.queryByText(/ro:must-not-echo/)).not.toBeInTheDocument()
    expect(screen.getAllByRole('checkbox')[0]).toBeChecked()
  })
  it('discovers concrete indices, names selected accounts and hands the created account to the shell for its initial refresh', async () => {
    const api = client()
    const onCreated = vi.fn()
    render(<Fixture api={api} onCreated={onCreated} />)
    await discover()
    expect(api.discoverAccounts).toHaveBeenCalledWith('lighter', discoveryFixture.address, expect.any(AbortSignal))
    expect(screen.getByText('Balance unavailable')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Import selected' })).toBeDisabled()
    await selectMain()
    const name = screen.getByLabelText(`Account name · ${indexAccount.sourceId}`)
    await userEvent.clear(name); await userEvent.type(name, 'Long-term')
    await userEvent.selectOptions(screen.getByLabelText(/Portfolio/), portfolioFixture.id)
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    await screen.findByText('Imported.')
    expect(api.createAccount).toHaveBeenCalledExactlyOnceWith({
      venueId: 'lighter', sourceId: '9007199254740993', name: 'Long-term', portfolioId: portfolioFixture.id,
    })
    expect(api.saveAccountCredential).not.toHaveBeenCalled()
    expect(onCreated).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ id: indexAccount.id }))
    // The shell owns scheduling, so closing the dialog cannot abandon the initial refresh.
    expect(api.sync).not.toHaveBeenCalled()
    expect(screen.getByRole('checkbox', { name: /Main account.*9007199254740993/ })).toBeDisabled()
  })
  it.each(['saved', 'failed'] as const)('waits for the optional token attempt before queueing a refresh when it is %s', async outcome => {
    let finish!: () => void
    const api = client({ saveAccountCredential: vi.fn().mockImplementation(() => new Promise((resolve, reject) => {
      finish = () => outcome === 'saved' ? resolve(credentialFixture) : reject(new Error('synthetic failure'))
    })) })
    const onCreated = vi.fn()
    render(<Fixture api={api} onCreated={onCreated} />)
    await discover(); await selectMain()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:synthetic-only')
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    await waitFor(() => expect(api.saveAccountCredential).toHaveBeenCalledTimes(1))
    expect(onCreated).not.toHaveBeenCalled()
    await act(async () => finish())
    await waitFor(() => expect(onCreated).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ id: indexAccount.id })))
  })
  it('shows imported disabled accounts from discovery, and detects duplicates in the current workspace', async () => {
    const api = client({
      discoverAccounts: vi.fn().mockResolvedValue({ ...discoveryFixture, accounts: [
        { ...discoveryFixture.accounts[0], existingAccountId: indexAccount.id, isEnabled: false },
        discoveryFixture.accounts[1],
      ] }),
    })
    render(<Fixture api={api} accounts={[{ ...indexAccount, sourceId: discoveryFixture.accounts[1]!.sourceId, isEnabled: false }]} />)
    await discover()
    expect(screen.getAllByRole('checkbox')).toHaveLength(2)
    screen.getAllByRole('checkbox').forEach(box => expect(box).toBeDisabled())
    // Already-imported rows carry a badge instead of a sentence.
    expect(screen.getAllByText('Already imported · Disabled', { selector: '.workspace-badge' })).toHaveLength(2)
    expect(screen.getByRole('button', { name: 'Import selected' })).toBeDisabled()
    expect(api.createAccount).not.toHaveBeenCalled()
  })
  it('shows each partial result, keeps a created account after token failure, and continues with the next account', async () => {
    const secret = 'ro:never-echo-this'
    const api = client({ saveAccountCredential: vi.fn().mockRejectedValueOnce(new ApiError('http', secret)).mockResolvedValueOnce(credentialFixture) })
    render(<Fixture api={api} />)
    await discover(); await selectMain()
    await userEvent.click(screen.getByRole('checkbox', { name: /Subaccount 1/ }))
    expect(screen.getByRole('link', { name: 'Create a read-only token in Lighter' })).toHaveAttribute('href', indexVenue.credentialSetupUrl)
    expect(screen.getByText(/For multiple selected accounts, create an all-scope token from the main account/)).toBeInTheDocument()
    const token = screen.getByLabelText('Read-only token')
    expect(token).toHaveAttribute('type', 'password')
    expect(token).toHaveAttribute('autocomplete', 'new-password')
    await userEvent.type(token, secret)
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (2)' }))
    await screen.findByText(/Imported. Token save was not confirmed/)
    await screen.findByText('Imported. Read-only token verified and saved.')
    expect(api.saveAccountCredential).toHaveBeenNthCalledWith(1, indexAccount.id, secret)
    expect(api.saveAccountCredential).toHaveBeenNthCalledWith(2, portfolioFixture.id, secret)
    expect(document.body).not.toHaveTextContent(secret)
    expect(screen.queryByLabelText('Read-only token')).not.toBeInTheDocument()
    expect(screen.getAllByRole('checkbox').every(box => (box as HTMLInputElement).disabled)).toBe(true)
    expect(api.createAccount).toHaveBeenCalledTimes(2)
  })
  it('explains server HTTPS rejection during name lookup and retains the public selection', async () => {
    const api = client({ discoverAccounts: vi.fn().mockResolvedValueOnce(discoveryFixture)
      .mockRejectedValueOnce(new ApiError('http', 'ro:never-display', 403)) })
    render(<Fixture api={api} />)
    await discover(); await selectMain()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:fixture-only')
    await userEvent.click(screen.getByRole('button', { name: 'Load Lighter names' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/trusted reverse proxy/)
    expect(screen.getByRole('checkbox', { name: /Main account.*9007199254740993/ })).toBeChecked()
    expect(screen.getByLabelText('Read-only token')).toHaveValue('')
    expect(document.body).not.toHaveTextContent('ro:never-display')
    expect(api.createAccount).not.toHaveBeenCalled()
  })
  it('explains unavailable credential storage without losing or reimporting the account', async () => {
    const api = client({ saveAccountCredential: vi.fn().mockRejectedValue(new ApiError('http', 'ro:never-display', 503)) })
    render(<Fixture api={api} />)
    await discover(); await selectMain()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:fixture-only')
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    await screen.findByText(/Imported. Token save was not confirmed.*server credential vault/)
    expect(screen.getByRole('checkbox', { name: /Main account.*9007199254740993/ })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Import selected' })).toBeDisabled()
    expect(api.createAccount).toHaveBeenCalledOnce()
    expect(api.sync).not.toHaveBeenCalled()
    expect(document.body).not.toHaveTextContent('ro:never-display')
  })
  it('reports creation failure per account without hiding successful imports or blindly retrying', async () => {
    const api = client({ createAccount: vi.fn().mockRejectedValueOnce(new Error('unknown outcome')).mockResolvedValueOnce({
      ...indexAccount, sourceId: discoveryFixture.accounts[1]!.sourceId, id: portfolioFixture.id,
    }) })
    render(<Fixture api={api} />)
    await discover(); await selectMain()
    await userEvent.click(screen.getByRole('checkbox', { name: /Subaccount 1/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (2)' }))
    await screen.findByText(/Import not confirmed. Find wallet again/)
    await screen.findByText('Imported.')
    expect(screen.getAllByRole('checkbox').every(box => (box as HTMLInputElement).disabled)).toBe(true)
    expect(api.createAccount).toHaveBeenCalledTimes(2)
  })
  it('handles a duplicate created since discovery without attempting credential storage', async () => {
    const api = client({ createAccount: vi.fn().mockRejectedValue(new ApiError('http', 'duplicate', 409)) })
    render(<Fixture api={api} />)
    await discover(); await selectMain()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:test-secret')
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    await screen.findByText(/Already added. Close this dialog/)
    expect(api.saveAccountCredential).not.toHaveBeenCalled()
    expect(screen.queryByLabelText('Read-only token')).not.toBeInTheDocument()
  })
  it('clears the token on a failed name validation and when closing via Escape', async () => {
    const api = client()
    render(<Fixture api={api} />)
    await discover(); await selectMain()
    await userEvent.clear(screen.getByLabelText(`Account name · ${indexAccount.sourceId}`))
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:test-secret')
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Name each selected account')
    expect(screen.getByLabelText('Read-only token')).toHaveValue('')
    expect(api.createAccount).not.toHaveBeenCalled()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:second-secret')
    await userEvent.keyboard('{Escape}')
    await userEvent.click(screen.getByRole('button', { name: 'Open import' }))
    await discover(); await selectMain()
    expect(screen.getByLabelText('Read-only token')).toHaveValue('')
  })
  it('clears the token when changing venues or wallet and never stores it', async () => {
    const local = vi.spyOn(Storage.prototype, 'setItem')
    const api = client()
    render(<Fixture api={api} />)
    await discover(); await selectMain()
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:secret')
    await userEvent.click(screen.getByRole('radio', { name: 'Manual account' }))
    await userEvent.click(screen.getByRole('radio', { name: 'Lighter · read-only perps' }))
    await discover(); await selectMain()
    expect(screen.getByLabelText('Read-only token')).toHaveValue('')
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:secret')
    fireEvent.change(screen.getByLabelText('Public wallet address'), { target: { value: discoveryFixture.address.toUpperCase() } })
    expect(screen.queryByLabelText('Read-only token')).not.toBeInTheDocument()
    expect(local).not.toHaveBeenCalled()
    expect(api.saveAccountCredential).not.toHaveBeenCalled()
  })
  it('still discovers and imports on LAN HTTP, but cannot enter or send a token', async () => {
    vi.stubGlobal('location', { protocol: 'http:', hostname: '192.168.1.5' })
    const api = client()
    render(<Fixture api={api} />)
    await discover(); await selectMain()
    expect(screen.getByLabelText('Read-only token')).toBeDisabled()
    expect(screen.getByText(/Use HTTPS before entering/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    await screen.findByText('Imported.')
    expect(api.saveAccountCredential).not.toHaveBeenCalled()
  })
  it('allows importing with a read-only token on LAN HTTP in development', async () => {
    vi.stubEnv('DEV', true)
    vi.stubGlobal('location', { protocol: 'http:', hostname: '192.168.1.5' })
    const api = client()
    render(<Fixture api={api} />)
    await discover(); await selectMain()
    expect(screen.getByLabelText('Read-only token')).toBeEnabled()
    expect(screen.getByRole('note')).toHaveTextContent('Development only')
    await userEvent.type(screen.getByLabelText('Read-only token'), 'ro:test')
    await userEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }))
    await waitFor(() => expect(api.saveAccountCredential).toHaveBeenCalled())
  })
  it('requires a valid wallet and lets the owner retry a discovery failure', async () => {
    const api = client({ discoverAccounts: vi.fn().mockRejectedValueOnce(new Error('upstream')).mockResolvedValueOnce(discoveryFixture) })
    render(<Fixture api={api} />)
    await userEvent.click(screen.getByRole('button', { name: 'Find wallet' }))
    expect(screen.getByRole('alert')).toHaveTextContent('valid public wallet address')
    expect(api.discoverAccounts).not.toHaveBeenCalled()
    await userEvent.type(screen.getByLabelText('Public wallet address'), discoveryFixture.address)
    await userEvent.click(screen.getByRole('button', { name: 'Find wallet' }))
    await screen.findByText(/Could not find accounts/)
    await userEvent.click(screen.getByRole('button', { name: 'Find wallet' }))
    await screen.findByRole('heading', { name: 'Choose accounts' })
  })
  it('supports a direct index for a descriptor without discovery and does not treat it as a manual balance', async () => {
    const api = client()
    render(<VenuesProvider venues={[{ ...indexVenue, capabilities: { ...indexVenue.capabilities, accountDiscovery: false } }]}>
      <CreateAccountDialog open onOpenChange={vi.fn()} api={api} portfolios={[]} onCreated={vi.fn()} />
    </VenuesProvider>)
    await userEvent.type(screen.getByLabelText('Account name'), 'Indexed')
    await userEvent.type(screen.getByLabelText('Account index'), indexAccount.sourceId)
    expect(screen.queryByLabelText(/Known account value/)).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Add account' }))
    await waitFor(() => expect(api.createAccount).toHaveBeenCalledExactlyOnceWith({
      venueId: 'lighter', name: 'Indexed', sourceId: indexAccount.sourceId, portfolioId: null,
    }))
  })
})
