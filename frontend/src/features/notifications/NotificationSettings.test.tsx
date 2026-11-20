import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { notificationPreferencesConflict } from '@/api/notifications'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { notificationPreferencesFixture } from '@/test/notification-fixture'
import { sessionFixture } from '@/test/identity-fixture'
import { NotificationSettings } from './NotificationSettings'
import type { InAppChange, InAppNotifications } from './useInAppNotifications'

afterEach(() => vi.useRealTimers())

function hub(status: Partial<InAppNotifications['status']> = {}) {
  const listeners = new Set<(change: InAppChange) => void>()
  const inApp = {
    status: { known: false, unreadCount: 0, enabled: true, workerOnline: true, heartbeatAtUtc: null, ...status },
    handlers: { onHead: vi.fn(), onStatus: vi.fn(), onFrame: vi.fn() }, cursor: () => null, acceptHead: vi.fn(),
    subscribe: (listener: (change: InAppChange) => void) => { listeners.add(listener); return () => { listeners.delete(listener) } },
    markRead: vi.fn(), openLink: vi.fn(),
  } as InAppNotifications
  return { inApp, emit: (change: InAppChange) => act(() => { for (const listener of listeners) listener(change) }) }
}
const show = (api: WorkspaceApi, inApp = hub().inApp) => render(<NotificationProvider><NotificationSettings api={api} inApp={inApp} /></NotificationProvider>)

describe('Notification settings', () => {
  it('saves the channel, minimum severity and categories with the expected revision', async () => {
    const api = {
      notificationPreferences: vi.fn().mockResolvedValue(notificationPreferencesFixture),
      saveNotificationPreferences: vi.fn().mockResolvedValue({ ...notificationPreferencesFixture, minimumSeverity: 'warning', categories: ['executions', 'links', 'discrepancies', 'monitor'], revision: 4 }),
    } as unknown as WorkspaceApi
    show(api)
    expect(await screen.findByText('Delivering')).toBeInTheDocument()
    const save = screen.getByRole('button', { name: 'Save notifications' })
    expect(save).toBeDisabled()
    await userEvent.selectOptions(screen.getByLabelText('Minimum severity'), 'warning')
    await userEvent.click(screen.getByRole('checkbox', { name: 'Credentials' }))
    await userEvent.click(save)
    expect(api.saveNotificationPreferences).toHaveBeenCalledWith({ enabled: true, minimumSeverity: 'warning',
      categories: ['executions', 'links', 'discrepancies', 'monitor'], expectedRevision: 3 })
    expect(await screen.findByText('Notification preferences saved.')).toBeInTheDocument()
    expect(save).toBeDisabled()
  })

  it('disables the filters when in-app notifications are off', async () => {
    const api = { notificationPreferences: vi.fn().mockResolvedValue(notificationPreferencesFixture) } as unknown as WorkspaceApi
    show(api)
    await userEvent.click(await screen.findByRole('switch', { name: 'In-app notifications' }))
    expect(screen.getByLabelText('Minimum severity')).toBeDisabled()
    expect(screen.getByRole('checkbox', { name: 'Executions' })).toBeDisabled()
  })

  it('reloads and tells the owner when the revision is stale', async () => {
    const api = {
      notificationPreferences: vi.fn().mockResolvedValueOnce(notificationPreferencesFixture)
        .mockResolvedValueOnce({ ...notificationPreferencesFixture, enabled: false, revision: 5 }),
      saveNotificationPreferences: vi.fn().mockRejectedValue(new ApiError('http', notificationPreferencesConflict, 409)),
    } as unknown as WorkspaceApi
    show(api)
    await userEvent.selectOptions(await screen.findByLabelText('Minimum severity'), 'critical')
    await userEvent.click(screen.getByRole('button', { name: 'Save notifications' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(notificationPreferencesConflict)
    await waitFor(() => expect(screen.getByRole('switch', { name: 'In-app notifications' })).not.toBeChecked())
    expect(api.notificationPreferences).toHaveBeenCalledTimes(2)
  })

  // #115: Retry used to leave the old load error on screen until the new read answered.
  it('drops the load error while Retry reads', async () => {
    let answer!: (value: typeof notificationPreferencesFixture) => void
    const api = { notificationPreferences: vi.fn().mockRejectedValueOnce(new Error('down'))
      .mockImplementationOnce(() => new Promise(resolve => { answer = resolve })) } as unknown as WorkspaceApi
    show(api)
    expect(await screen.findByRole('alert')).toHaveTextContent('Notification preferences could not be loaded.')
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Loading notification preferences…')
    await act(async () => answer(notificationPreferencesFixture))
    expect(await screen.findByText('Delivering')).toBeInTheDocument()
  })

  it('rereads after a feed reset unless edits are pending', async () => {
    const api = { notificationPreferences: vi.fn().mockResolvedValue(notificationPreferencesFixture) } as unknown as WorkspaceApi
    const { inApp, emit } = hub()
    show(api, inApp)
    await screen.findByText('Delivering')
    emit({ type: 'reset' })
    await waitFor(() => expect(api.notificationPreferences).toHaveBeenCalledTimes(2))
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Order links' }))
    emit({ type: 'reset' })
    await act(async () => { await Promise.resolve() })
    expect(api.notificationPreferences).toHaveBeenCalledTimes(2)
  })

  it('shows the worker offline time from the stream head', async () => {
    const api = { notificationPreferences: vi.fn().mockResolvedValue(notificationPreferencesFixture) } as unknown as WorkspaceApi
    show(api, hub({ known: true, workerOnline: false, heartbeatAtUtc: '2026-10-17T14:02:00Z' }).inApp)
    expect(await screen.findByText(/^Notification worker offline since /)).toBeInTheDocument()
  })

  it('requests a test notification and shows the cooldown from Retry-After', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true, now: Date.parse('2026-10-17T10:00:00Z') })
    const api = {
      notificationPreferences: vi.fn().mockResolvedValue(notificationPreferencesFixture),
      requestTestNotification: vi.fn().mockResolvedValueOnce(undefined)
        .mockRejectedValueOnce(new ApiError('http', 'A test notification was requested moments ago.', 429, '2026-10-17T10:00:07.000Z')),
    } as unknown as WorkspaceApi
    show(api)
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    await user.click(await screen.findByRole('button', { name: 'Send test notification' }))
    expect(await screen.findByText(/Test notification requested/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Send test notification' }))
    const waiting = await screen.findByRole('button', { name: /^Send test notification \(wait \d s\)$/ })
    expect(waiting).toBeDisabled()
    await act(async () => { await vi.advanceTimersByTimeAsync(8000) })
    expect(screen.getByRole('button', { name: 'Send test notification' })).toBeEnabled()
  })

  it.each(['7', 'Sat, 17 Oct 2026 10:00:07 GMT'])('shows a Wallet-mode wait notice and cooldown from Retry-After: %s', async retryAfter => {
    vi.useFakeTimers({ shouldAdvanceTime: true, now: Date.parse('2026-10-17T10:00:00Z') })
    const fetcher = vi.fn((path: string) => Promise.resolve(new Response(JSON.stringify(
      path.endsWith('/test') ? { code: 'rate-limited', detail: 'Do not show this body.' } : notificationPreferencesFixture,
    ), { status: path.endsWith('/test') ? 429 : 200, headers: { 'Retry-After': retryAfter } })))
    vi.stubGlobal('fetch', fetcher)
    show(createWorkspaceApi({ kind: 'session', userId: sessionFixture.user.id, sessionId: sessionFixture.session.id }))
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    await user.click(await screen.findByRole('button', { name: 'Send test notification' }))
    const notice = await screen.findByText('Too many requests. Wait 7 seconds before trying again.')
    expect(notice.closest('[role="status"]')).toHaveClass('is-warning')
    expect(screen.queryByText('Do not show this body.')).toBeNull()
    expect(screen.getByRole('button', { name: 'Send test notification (wait 7 s)' })).toBeDisabled()
    await act(async () => { await vi.advanceTimersByTimeAsync(8000) })
    expect(screen.getByRole('button', { name: 'Send test notification' })).toBeEnabled()
    expect(fetcher.mock.calls.filter(([path]) => path.endsWith('/test'))).toHaveLength(1)
  })

  it('reports a lagging subscription and failed deliveries', async () => {
    const api = {
      notificationPreferences: vi.fn().mockResolvedValue({ ...notificationPreferencesFixture, subscriptionStatus: 'lagging', failedDeliveries: 2 }),
    } as unknown as WorkspaceApi
    show(api)
    expect(await screen.findByText('Some events aged out before they could become notifications.')).toBeInTheDocument()
    expect(screen.getByText('2 events could not become notifications.')).toBeInTheDocument()
  })

  it('keeps a replaced API\'s late save out of the new API\'s preferences', async () => {
    let answer!: (saved: typeof notificationPreferencesFixture) => void
    const first = {
      notificationPreferences: vi.fn().mockResolvedValue(notificationPreferencesFixture),
      saveNotificationPreferences: vi.fn(() => new Promise(resolve => { answer = resolve })),
    } as unknown as WorkspaceApi
    const second = { notificationPreferences: vi.fn().mockResolvedValue({ ...notificationPreferencesFixture, minimumSeverity: 'critical' }) } as unknown as WorkspaceApi
    const { inApp } = hub()
    const tree = (api: WorkspaceApi) => <NotificationProvider><NotificationSettings api={api} inApp={inApp} /></NotificationProvider>
    const view = render(tree(first))
    await userEvent.selectOptions(await screen.findByLabelText('Minimum severity'), 'warning')
    await userEvent.click(screen.getByRole('button', { name: 'Save notifications' }))
    view.rerender(tree(second))
    await waitFor(() => expect(screen.getByLabelText('Minimum severity')).toHaveValue('critical'))
    expect(screen.getByRole('button', { name: 'Save notifications' })).not.toHaveAttribute('aria-busy', 'true')
    await act(async () => { answer({ ...notificationPreferencesFixture, minimumSeverity: 'warning', revision: 4 }) })
    expect(screen.queryByText('Loading notification preferences…')).toBeNull()
    expect(screen.getByLabelText('Minimum severity')).toHaveValue('critical')
    expect(screen.queryByText('Notification preferences saved.')).toBeNull()
  })
})
