import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { WorkspaceApi } from '@/api/workspace'
import type { MonitorStreamEvent } from '@/api/monitor'
import { systemFixture } from '@/test/system-fixture'
import { accountFixture, emptyOverview, idleMarketStream, overviewFixture, playApiStubs } from '@/test/workspace-fixture'
import { monitorFixture, monitorSummaryFixture } from '@/test/monitor-fixture'
import { notificationCursor, notificationHeadFixture, notificationWith } from '@/test/notification-fixture'
import { activityPage } from '@/test/activity-fixture'
import { ApplicationShell } from './ApplicationShell'
import { shellIdentityFixture } from '@/test/identity-fixture'

function client(overrides: Partial<WorkspaceApi> = {}) {
  const streams: ((frame: MonitorStreamEvent) => void)[] = []
  const api = {
    overview: vi.fn().mockResolvedValue(overviewFixture), activity: vi.fn().mockResolvedValue(activityPage([], { scopeNote: 'Fixture history' })),
    snapshot: vi.fn().mockResolvedValue(null), fills: vi.fn().mockResolvedValue([]), accountCredential: vi.fn().mockResolvedValue({ storageConfigured: true, credential: null }),
    marketStream: vi.fn(idleMarketStream), ...playApiStubs(),
    monitorStream: vi.fn((_after: string, signal: AbortSignal, emit: (frame: MonitorStreamEvent) => void) => new Promise<void>(resolve => {
      streams.push(emit)
      signal.addEventListener('abort', () => resolve(), { once: true })
    })),
    ...overrides,
  } as unknown as WorkspaceApi
  return { api, emit: (frame: MonitorStreamEvent) => act(() => { streams.at(-1)!(frame) }) }
}
const unreadHead = (unreadCount: number, extra = {}) => ({ ...notificationHeadFixture, unreadCount, ...extra })

beforeAll(async () => {
  await Promise.all([import('@/features/notifications/NotificationsPage'), import('./SettingsPage')])
}, 60_000)
beforeEach(() => { window.history.replaceState(null, '', '/') })

describe('Notifications in the shell', () => {
  it('places the entry above Settings with a capped badge whose count is in the accessible name', async () => {
    const { api } = client({ notificationHead: vi.fn().mockResolvedValue(unreadHead(120)) })
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
    const sidebar = await screen.findByRole('complementary', { name: 'Main navigation' })
    const entry = await within(sidebar).findByRole('link', { name: 'Notifications, 99+ unread' })
    expect(entry).toHaveTextContent('Notifications99+')
    const bottom = entry.closest('.shell-sidebar-bottom')!
    const links = within(bottom as HTMLElement).getAllByRole('link')
    expect(links.map(link => link.getAttribute('href'))).toEqual(['#notifications', '#settings'])
    // Collapsing the sidebar keeps the badge, on the icon.
    await userEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }))
    expect(within(entry).getByText('99+')).toHaveClass('notification-badge')
    // The narrow-screen header has the same bell and the menu button shows a dot.
    const header = document.querySelector('.shell-header') as HTMLElement
    expect(within(header).getByRole('link', { name: 'Notifications, 99+ unread' })).toHaveAttribute('href', '#notifications')
    expect(header.querySelector('.menu-unread-dot')).not.toBeNull()
  })

  it('follows the unread count from stream frames and hides the badge when the channel is off', async () => {
    const { api, emit } = client({ notificationHead: vi.fn().mockResolvedValue(unreadHead(0)) })
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
    await waitFor(() => expect(screen.getAllByRole('link', { name: 'Notifications' })).toHaveLength(2))
    await waitFor(() => expect(api.monitorStream).toHaveBeenCalled())
    emit({ type: 'notification', frame: { cursor: notificationCursor(11), unreadCount: 3, enabled: true, notification: notificationWith(1) } })
    expect(screen.getAllByRole('link', { name: 'Notifications, 3 unread' })).toHaveLength(2)
    emit({ type: 'notifications', status: { state: 'reset', nextCursor: notificationCursor(1, 1), unreadCount: 3, enabled: false } })
    expect(screen.getAllByRole('link', { name: 'Notifications' })).toHaveLength(2)
    expect(document.querySelector('.notification-badge')).toBeNull()
  })

  it('opens the page from #notifications without the workspace heading actions', async () => {
    window.history.replaceState(null, '', '/#notifications')
    const { api } = client()
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
    // Both pages are lazy: a loaded parallel run can take longer than the default second to show them.
    expect(await screen.findByRole('heading', { level: 1, name: 'Notifications' }, { timeout: 5_000 })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add account' })).toBeNull()
    expect(await screen.findByText('You are all caught up. No unread notifications.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Preferences' }))
    expect(window.location.hash).toBe('#settings')
    expect(await screen.findByRole('heading', { name: 'Notifications', level: 2 }, { timeout: 5_000 })).toBeInTheDocument()
  })

  it('shows the offline banner only while the channel is on, the worker is offline and Plays are tracked', async () => {
    const tracked = { ...monitorSummaryFixture, accounts: [{ ...monitorFixture, state: 'live' as const }] }
    const idle = { ...monitorSummaryFixture, accounts: [{ ...monitorFixture, state: 'idle' as const }] }
    const cases = [
      { head: unreadHead(0, { workerOnline: false }), monitor: tracked, shown: true },
      { head: unreadHead(0, { workerOnline: false, enabled: false }), monitor: tracked, shown: false },
      { head: unreadHead(0, { workerOnline: false }), monitor: idle, shown: false },
      { head: unreadHead(0), monitor: tracked, shown: false },
    ]
    for (const { head, monitor, shown } of cases) {
      const { api } = client({ notificationHead: vi.fn().mockResolvedValue(head), monitor: vi.fn().mockResolvedValue(monitor) })
      const view = render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
      await screen.findByRole('button', { name: 'View Main account' })
      await waitFor(() => expect(api.monitor).toHaveBeenCalled())
      await act(async () => { await Promise.resolve() })
      if (shown) expect(await screen.findByText('Notifications offline: they will arrive when it returns')).toHaveClass('workspace-alert')
      else expect(screen.queryByText('Notifications offline: they will arrive when it returns')).toBeNull()
      view.unmount()
    }
  })

  it('keeps workspace reads going when the notification head fails', async () => {
    const { api } = client({ overview: vi.fn().mockResolvedValue(emptyOverview), notificationHead: vi.fn().mockRejectedValue(new Error('down')) })
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
    await waitFor(() => expect(api.overview).toHaveBeenCalled())
    await waitFor(() => expect(api.monitorStream).toHaveBeenCalled())
    expect(vi.mocked(api.monitorStream).mock.calls[0]).toHaveLength(3)
  })

  it('opens an account from a notification link and reports one that no longer exists', async () => {
    const link = { kind: 'account' as const, label: 'Open account', playId: null, accountId: accountFixture.id }
    const gone = { ...link, label: 'Open old account', accountId: '00000000-0000-4000-8000-00000000abcd' }
    const popups = [notificationWith(1, { content: { ...notificationWith(1).content, links: [link] } }),
      notificationWith(2, { content: { ...notificationWith(2).content, links: [gone] } })]
    vi.spyOn(document, 'hasFocus').mockReturnValue(true)
    const { api } = client({
      notificationHead: vi.fn().mockResolvedValue(unreadHead(2)),
      claimNotifications: vi.fn().mockResolvedValue({ popups, more: 0 }),
      readNotification: vi.fn((id: string) => Promise.resolve({ ...popups.find(item => item.id === id)!, state: 'read' as const, readAt: '2026-10-17T11:00:00Z' })),
    })
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} disconnect={vi.fn()} api={api} />)
    await screen.findByRole('button', { name: 'View Main account' })
    await userEvent.click(await screen.findByRole('button', { name: 'Open account' }))
    expect(window.location.hash).toBe('#accounts')
    expect(await screen.findByRole('heading', { level: 1, name: 'Main account' })).toBeInTheDocument()
    expect(api.readNotification).toHaveBeenCalledWith(popups[0]!.id)
    await userEvent.click(screen.getByRole('button', { name: 'Open old account' }))
    expect(await screen.findByText('This account is no longer available.')).toBeInTheDocument()
    vi.restoreAllMocks()
  })
})
