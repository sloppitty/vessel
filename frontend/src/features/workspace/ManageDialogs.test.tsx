import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { accountFixture, portfolioFixture } from '@/test/workspace-fixture'
import { ManageAccountDialog, ManagePortfolioDialog } from './ManageDialogs'
import { credentialsFixture } from '@/test/identity-fixture'

function client(overrides: Partial<WorkspaceApi> = {}): WorkspaceApi {
  return { ...createWorkspaceApi(credentialsFixture),
    updatePortfolio: vi.fn().mockResolvedValue(portfolioFixture), deletePortfolio: vi.fn().mockResolvedValue(undefined),
    updateAccount: vi.fn().mockResolvedValue(accountFixture), deleteAccount: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  }
}
describe('Management dialogs', () => {
  it('renames only the portfolio without deleting its grouping', async () => {
    const api = client(), close = vi.fn(), changed = vi.fn()
    render(<ManagePortfolioDialog portfolio={portfolioFixture} api={api} onClose={close} onChanged={changed} />)
    const input = screen.getByLabelText('Portfolio name')
    await userEvent.clear(input); await userEvent.type(input, 'Intraday')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(api.updatePortfolio).toHaveBeenCalledWith(portfolioFixture.id, { name: 'Intraday' }))
    expect(api.deletePortfolio).not.toHaveBeenCalled()
    expect(close).toHaveBeenCalledOnce()
  })
  it('starts from the stored icon and saves a newly chosen one with the name', async () => {
    const api = client(), changed = vi.fn()
    render(<ManagePortfolioDialog portfolio={{ ...portfolioFixture, icon: 'gem' }} api={api} onClose={vi.fn()} onChanged={changed} />)
    const icons = screen.getByRole('group', { name: 'Icon' })
    expect(within(icons).getByRole('radio', { name: 'Gem' })).toBeChecked()
    await userEvent.click(within(icons).getByRole('radio', { name: 'Rocket' }))
    expect(within(icons).getByRole('radio', { name: 'Rocket' })).toBeChecked()
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(api.updatePortfolio).toHaveBeenCalledWith(portfolioFixture.id, { name: 'Swing trading', icon: 'rocket' }))
    expect(changed).toHaveBeenCalledWith({ kind: 'portfolio-updated', id: portfolioFixture.id, message: 'Portfolio updated.' })
  })
  it('keeps an icon this browser does not know when only the name changes', async () => {
    const api = client()
    render(<ManagePortfolioDialog portfolio={{ ...portfolioFixture, icon: 'future-icon' }} api={api} onClose={vi.fn()} onChanged={vi.fn()} />)
    const icons = screen.getByRole('group', { name: 'Icon' })
    for (const radio of within(icons).getAllByRole('radio')) expect(radio).not.toBeChecked()
    const input = screen.getByLabelText('Portfolio name')
    await userEvent.clear(input); await userEvent.type(input, 'Intraday')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(api.updatePortfolio).toHaveBeenCalledOnce())
    expect(vi.mocked(api.updatePortfolio).mock.calls[0]?.[1]).not.toHaveProperty('icon')
  })
  it('lets the owner explicitly choose the folder over an unknown icon', async () => {
    const api = client()
    render(<ManagePortfolioDialog portfolio={{ ...portfolioFixture, icon: 'future-icon' }} api={api} onClose={vi.fn()} onChanged={vi.fn()} />)
    await userEvent.click(within(screen.getByRole('group', { name: 'Icon' })).getByRole('radio', { name: 'Folder' }))
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(api.updatePortfolio).toHaveBeenCalledWith(portfolioFixture.id, { name: 'Swing trading', icon: 'folder' }))
  })
  it('requires a second explicit portfolio-delete confirmation and explains retained accounts', async () => {
    const api = client(), changed = vi.fn()
    render(<ManagePortfolioDialog portfolio={portfolioFixture} api={api} onClose={vi.fn()} onChanged={changed} />)
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }))
    expect(api.deletePortfolio).not.toHaveBeenCalled()
    expect(screen.getByText(/accounts, executions, snapshots and plays are kept; the accounts become Unassigned/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Keep portfolio' }))
    expect(api.deletePortfolio).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete portfolio' }))
    await waitFor(() => expect(api.deletePortfolio).toHaveBeenCalledWith(portfolioFixture.id))
    expect(changed).toHaveBeenCalledWith({ kind: 'portfolio-deleted', id: portfolioFixture.id, message: 'Portfolio deleted. Its accounts are now available in All accounts.' })
  })
  it('saves rename, unlink and disable in a single settings request', async () => {
    const api = client(), close = vi.fn()
    render(<ManageAccountDialog account={accountFixture} portfolios={[portfolioFixture]} api={api} onClose={close} onChanged={vi.fn()} />)
    const input = screen.getByLabelText('Account name')
    await userEvent.clear(input); await userEvent.type(input, 'Hidden account')
    await userEvent.selectOptions(screen.getByLabelText('Portfolio'), '')
    await userEvent.click(screen.getByLabelText('Account enabled'))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(api.updateAccount).toHaveBeenCalledWith(accountFixture.id, {
      name: 'Hidden account', portfolioId: null, isEnabled: false,
      expectedRevision: 1,
    }))
    expect(api.deleteAccount).not.toHaveBeenCalled()
    expect(close).toHaveBeenCalledOnce()
  })
  it('moves a previously unassigned account into a selected portfolio', async () => {
    const api = client()
    render(<ManageAccountDialog account={{ ...accountFixture, portfolioId: null }} portfolios={[portfolioFixture]} api={api} onClose={vi.fn()} onChanged={vi.fn()} />)
    expect(screen.getByLabelText('Portfolio')).toHaveValue('')
    await userEvent.selectOptions(screen.getByLabelText('Portfolio'), portfolioFixture.id)
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(api.updateAccount).toHaveBeenCalledWith(accountFixture.id, {
      name: accountFixture.name, portfolioId: portfolioFixture.id, isEnabled: true,
      expectedRevision: 1,
    }))
  })
  it('enables a disabled account without changing its source data', async () => {
    const api = client()
    render(<ManageAccountDialog account={{ ...accountFixture, isEnabled: false }} portfolios={[portfolioFixture]} api={api} onClose={vi.fn()} onChanged={vi.fn()} />)
    expect(screen.getByLabelText('Account enabled')).not.toBeChecked()
    await userEvent.click(screen.getByLabelText('Account enabled'))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(api.updateAccount).toHaveBeenCalledWith(accountFixture.id, {
      name: accountFixture.name, portfolioId: portfolioFixture.id, isEnabled: true,
      expectedRevision: 1,
    }))
  })
  it('reports permanent account deletion by kind and ID, separately from its message', async () => {
    const api = client(), changed = vi.fn()
    render(<ManageAccountDialog account={accountFixture} portfolios={[portfolioFixture]} api={api} onClose={vi.fn()} onChanged={changed} />)
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete account' }))
    await waitFor(() => expect(changed).toHaveBeenCalledWith({ kind: 'account-deleted', id: accountFixture.id, message: 'Account and its retained imported records deleted.' }))
  })

  it('requires permanent-delete confirmation and retains dialog on a linked-play conflict', async () => {
    const api = client({ deleteAccount: vi.fn().mockRejectedValue(new ApiError('http', 'Account has linked plays. Disable it instead.', 409)) })
    const close = vi.fn()
    render(<ManageAccountDialog account={accountFixture} portfolios={[portfolioFixture]} api={api} onClose={close} onChanged={vi.fn()} />)
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }))
    expect(api.deleteAccount).not.toHaveBeenCalled()
    expect(screen.getByText(/This cannot be undone/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Delete account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('linked plays')
    expect(close).not.toHaveBeenCalled()
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Keep account' })).toBeEnabled()
  })
  it('rejects an empty name before sending any update', async () => {
    const api = client()
    render(<ManageAccountDialog account={accountFixture} portfolios={[]} api={api} onClose={vi.fn()} onChanged={vi.fn()} />)
    await userEvent.clear(screen.getByLabelText('Account name'))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Name the account')
    expect(api.updateAccount).not.toHaveBeenCalled()
  })
  it('does not replay a stale settings form after conflict and reloads current values explicitly', async () => {
    const api = client({
      updateAccount: vi.fn().mockRejectedValueOnce(new ApiError('http', 'Settings changed. Reload them.', 409)).mockResolvedValue(accountFixture),
      account: vi.fn().mockResolvedValue({ ...accountFixture, name: 'Current saved name', isEnabled: false, portfolioId: null, settingsRevision: 2 }),
      portfolios: vi.fn().mockResolvedValue([portfolioFixture]),
    })
    const onStale = vi.fn()
    render(<ManageAccountDialog account={accountFixture} portfolios={[portfolioFixture]} api={api} onClose={vi.fn()} onChanged={vi.fn()} onStale={onStale} />)
    await userEvent.clear(screen.getByLabelText('Account name'))
    await userEvent.type(screen.getByLabelText('Account name'), 'Old form rename')
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Settings changed')
    expect(screen.getByRole('button', { name: 'Save settings' })).toBeDisabled()
    expect(api.updateAccount).toHaveBeenCalledTimes(1)
    expect(onStale).toHaveBeenCalledTimes(1)
    await userEvent.click(screen.getByRole('button', { name: 'Reload current settings' }))
    await waitFor(() => expect(screen.getByLabelText('Account name')).toHaveValue('Current saved name'))
    expect(screen.getByLabelText('Account enabled')).not.toBeChecked()
    expect(screen.getByLabelText('Portfolio')).toHaveValue('')
    expect(api.updateAccount).toHaveBeenCalledTimes(1)
    await userEvent.clear(screen.getByLabelText('Account name'))
    await userEvent.type(screen.getByLabelText('Account name'), 'Reviewed current rename')
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(api.updateAccount).toHaveBeenLastCalledWith(accountFixture.id, {
      name: 'Reviewed current rename', portfolioId: null, isEnabled: false, expectedRevision: 2,
    }))
  })
  it('offers a portfolio created in another tab after reloading conflicting settings', async () => {
    const elsewhere = { ...portfolioFixture, id: '7b3f6a2e-1c4d-4e5f-8a9b-0c1d2e3f4a5b', name: 'Created elsewhere' }
    const api = client({
      updateAccount: vi.fn().mockRejectedValue(new ApiError('http', 'Settings changed. Reload them.', 409)),
      account: vi.fn().mockResolvedValue({ ...accountFixture, portfolioId: elsewhere.id, settingsRevision: 3 }),
      portfolios: vi.fn().mockResolvedValue([portfolioFixture, elsewhere]),
    })
    render(<ManageAccountDialog account={accountFixture} portfolios={[portfolioFixture]} api={api} onClose={vi.fn()} onChanged={vi.fn()} />)
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Reload current settings' }))
    await waitFor(() => expect(screen.getByLabelText('Portfolio')).toHaveValue(elsewhere.id))
    expect(screen.getByRole('option', { name: 'Created elsewhere' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save settings' })).toBeEnabled()
  })
  it('keeps saving blocked when reloading conflicting settings fails', async () => {
    const api = client({
      updateAccount: vi.fn().mockRejectedValue(new ApiError('http', 'Settings changed. Reload them.', 409)),
      account: vi.fn().mockRejectedValue(new ApiError('unavailable', 'The request did not complete. Check the API and try again.')),
      portfolios: vi.fn().mockResolvedValue([portfolioFixture]),
    })
    render(<ManageAccountDialog account={accountFixture} portfolios={[portfolioFixture]} api={api} onClose={vi.fn()} onChanged={vi.fn()} />)
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Reload current settings' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('did not complete')
    expect(screen.getByRole('button', { name: 'Save settings' })).toBeDisabled()
    expect(api.updateAccount).toHaveBeenCalledTimes(1)
  })
})
