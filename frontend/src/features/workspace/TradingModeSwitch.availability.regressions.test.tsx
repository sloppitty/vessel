import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import { emptyOverview, playApiStubs } from '@/test/workspace-fixture'
import { systemFixture } from '@/test/system-fixture'
import { ApplicationShell } from './ApplicationShell'

const system = { ...systemFixture, venues: systemFixture.venues.map(venue => ({
  ...venue, capabilities: { ...venue.capabilities, paperExecution: venue.id === 'hyperliquid' },
})) }
const realName = 'Journal, switch to Paper playground'
const paperName = 'Paper playground, switch to Journal'
function client() {
  const base = { ...createWorkspaceApi('synthetic-only'), ...playApiStubs(), overview: vi.fn().mockResolvedValue(emptyOverview),
    accounts: vi.fn().mockResolvedValue([]), portfolios: vi.fn().mockResolvedValue([]) }
  const real: WorkspaceApi = { ...base, tradingMode: 'real' }
  const paper: WorkspaceApi = { ...base, tradingMode: 'paper' }
  const root = { ...real, forTradingMode: vi.fn(mode => mode === 'paper' ? paper : real) } as WorkspaceApi
  return { real, paper, root }
}
beforeEach(() => window.history.replaceState(null, '', '/#plays'))

it('keeps the exit guard if Paper becomes unavailable while its switch confirmation is open', async () => {
  const user = userEvent.setup()
  const { root } = client()
  const disconnect = vi.fn()
  const view = render(<ApplicationShell system={system} api={root} disconnect={disconnect} />)
  await user.click(await screen.findByRole('button', { name: 'New play' }))
  await user.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Keep this real work')
  const before = new Event('beforeunload', { cancelable: true })
  fireEvent(window, before)
  expect(before.defaultPrevented).toBe(true)
  await user.click(await screen.findByRole('button', { name: realName }))
  const dialog = screen.getByRole('alertdialog')
  view.rerender(<ApplicationShell system={systemFixture} api={root} disconnect={disconnect} />)
  expect(screen.queryByRole('button', { name: realName })).not.toBeInTheDocument()
  await user.click(within(dialog).getByRole('button', { name: 'Discard and switch' }))
  expect(document.querySelector('.journal-shell')).toHaveAttribute('data-trading-mode', 'real')
  expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Keep this real work')
  const after = new Event('beforeunload', { cancelable: true })
  fireEvent(window, after)
  fireEvent.change(screen.getByRole('textbox', { name: 'Play title' }), { target: { value: 'Continue real work' } })
  const title = (screen.getByRole('textbox', { name: 'Play title' }) as HTMLInputElement).value
  vi.spyOn(window, 'confirm').mockReturnValue(true)
  await user.click(screen.getByRole('button', { name: "Disconnect Owner's session" }))
  expect({ exitProtected: after.defaultPrevented, title, disconnects: disconnect.mock.calls.length })
    .toEqual({ exitProtected: true, title: 'Continue real work', disconnects: 1 })
})

it('keeps beforeunload protection on cancel, but removes it on an actual mode switch', async () => {
  const user = userEvent.setup()
  render(<ApplicationShell system={system} api={client().root} disconnect={vi.fn()} />)
  await user.click(await screen.findByRole('button', { name: 'New play' }))
  await user.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Unsaved work')
  await user.click(await screen.findByRole('button', { name: realName }))
  await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Keep editing' }))
  const before = new Event('beforeunload', { cancelable: true })
  fireEvent(window, before)
  expect(before.defaultPrevented).toBe(true)
  await user.click(screen.getByRole('button', { name: realName }))
  const confirm = within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Discard and switch' })
  // Both activations use the same handler before a React render can disable or replace it.
  act(() => { fireEvent.click(confirm); fireEvent.click(confirm) })
  await waitFor(() => expect(screen.getByRole('button', { name: paperName })).toHaveFocus())
  expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  const after = new Event('beforeunload', { cancelable: true })
  fireEvent(window, after)
  expect(after.defaultPrevented).toBe(false)
})

it('does not leave Paper or discard its hidden Play draft when monitoring is disabled in Settings', async () => {
  const user = userEvent.setup()
  const { root, paper } = client()
  let settings = { enabled: true, checkIntervalMinutes: 60 }
  vi.mocked(root.paperSettings).mockImplementation(async () => settings)
  vi.mocked(paper.savePaperSettings).mockImplementation(async next => { settings = next; return settings })
  render(<ApplicationShell system={system} api={root} disconnect={vi.fn()} />)
  await user.click(await screen.findByRole('button', { name: realName }))
  await user.click(await screen.findByRole('button', { name: 'New play' }))
  await user.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Keep this Paper work')
  await user.click(screen.getByRole('link', { name: 'Settings' }))
  await user.click(await screen.findByRole('switch', { name: 'Enable paper trading' }))
  await user.click(screen.getByRole('button', { name: 'Save paper settings' }))
  await user.click(within(screen.getByRole('dialog', { name: 'Disable paper monitoring?' }))
    .getByRole('button', { name: 'Disable paper monitoring' }))
  await waitFor(() => expect(settings.enabled).toBe(false))
  expect(document.querySelector('.journal-shell')).toHaveAttribute('data-trading-mode', 'paper')
  expect(screen.getByRole('button', { name: paperName })).toBeInTheDocument()
  await user.click(screen.getByRole('link', { name: 'Plays' }))
  expect(await screen.findByRole('textbox', { name: 'Play title' })).toHaveValue('Keep this Paper work')
})

it('returns focus to a surviving shell control when Paper disappears during confirmation', async () => {
  const user = userEvent.setup()
  const { root } = client()
  const disconnect = vi.fn()
  const view = render(<ApplicationShell system={system} api={root} disconnect={disconnect} />)
  await user.click(await screen.findByRole('button', { name: 'New play' }))
  await user.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Keep this real work')
  await user.click(await screen.findByRole('button', { name: realName }))
  const dialog = screen.getByRole('alertdialog')
  view.rerender(<ApplicationShell system={systemFixture} api={root} disconnect={disconnect} />)
  expect(screen.queryByRole('button', { name: realName })).not.toBeInTheDocument()
  await user.click(within(dialog).getByRole('button', { name: 'Discard and switch' }))
  await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument())
  expect(document.querySelector('.journal-shell')).toHaveAttribute('data-trading-mode', 'real')
  expect(document.querySelector('[data-pending]')).toBeNull()
  expect(document.querySelector('.is-arriving')).toBeNull()
  expect(screen.queryByText('Paper playground', { selector: '[role="status"]' })).not.toBeInTheDocument()
  await waitFor(() => expect(document.activeElement).not.toBe(document.body))
  expect(document.activeElement?.closest('.journal-shell')).not.toBeNull()
})
