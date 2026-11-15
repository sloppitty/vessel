import { useEffect } from 'react'
import { act, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import type { InAppNotification, NotificationLink } from '@/api/notifications'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { notificationCursor, notificationFixture, notificationHeadFixture, notificationWith } from '@/test/notification-fixture'
import { sessionFixture } from '@/test/identity-fixture'
import { useInAppNotifications, type InAppNotifications } from './useInAppNotifications'

let visibility: DocumentVisibilityState = 'visible'
let focused = true
function setActive(next: { visible?: boolean; focused?: boolean }) {
  if (next.visible !== undefined) visibility = next.visible ? 'visible' : 'hidden'
  if (next.focused !== undefined) focused = next.focused
  act(() => { document.dispatchEvent(new Event('visibilitychange')); window.dispatchEvent(new Event(focused ? 'focus' : 'blur')) })
}
beforeEach(() => {
  vi.useFakeTimers()
  visibility = 'visible'; focused = true
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => visibility })
  vi.spyOn(document, 'hasFocus').mockImplementation(() => focused)
})
afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
  Reflect.deleteProperty(document, 'visibilityState')
})
const flush = () => act(async () => { await vi.advanceTimersByTimeAsync(0) })

function client(claims: { popups: InAppNotification[]; more: number }[] = []) {
  return {
    claimNotifications: vi.fn(() => Promise.resolve(claims.shift() ?? { popups: [], more: 0 })),
    readNotification: vi.fn((id: string) => Promise.resolve({ ...notificationFixture, id, state: 'read', readAt: '2026-10-17T11:00:00Z' })),
    notifications: vi.fn().mockResolvedValue({ items: [], page: 1, pageSize: 100, total: 0, unreadTotal: 0, allTotal: 0 }),
  } as unknown as WorkspaceApi
}

function setup(api: WorkspaceApi) {
  const onLink = vi.fn<(link: NotificationLink) => void>(), onOpenPage = vi.fn()
  const result: { current: InAppNotifications | null } = { current: null }
  function Harness() {
    result.current = useInAppNotifications({ api, active: true, onLink, onOpenPage })
    return <span data-testid="count">{result.current.status.unreadCount}</span>
  }
  render(<NotificationProvider><main id="workspace-content" tabIndex={-1}><Harness /></main></NotificationProvider>)
  const hub = () => result.current!
  return { hub, onLink, onOpenPage }
}
const frame = (notification: InAppNotification, sequence: number, unreadCount = 1) =>
  ({ cursor: notificationCursor(sequence), unreadCount, enabled: true, notification })

it.each(['read', 'reset'] as const)('ignores a pending claim after a newer %s frame', async transition => {
  const api = client()
  let answer!: (claim: { popups: InAppNotification[]; more: number }) => void
  vi.mocked(api.claimNotifications).mockImplementationOnce(() => new Promise(resolve => { answer = resolve }))
  const { hub } = setup(api)
  act(() => hub().handlers.onHead(notificationHeadFixture))
  await flush()
  act(() => {
    if (transition === 'reset') hub().handlers.onStatus({ state: 'reset', nextCursor: notificationCursor(1, 1), unreadCount: 0, enabled: true })
    else hub().handlers.onFrame(frame(notificationWith(1, { state: 'read', readAt: '2026-10-17T11:00:00Z' }), 11, 0))
  })
  await act(async () => { answer({ popups: [notificationWith(1)], more: 0 }) })
  expect(screen.queryByText('Notification 1')).toBeNull()
})

it('withdraws old-session popups when the API is replaced', async () => {
  const first = client([{ popups: [notificationWith(1)], more: 0 }]), second = client()
  let hub!: InAppNotifications
  function Harness({ api }: { api: WorkspaceApi }) {
    hub = useInAppNotifications({ api, active: true, onLink: vi.fn(), onOpenPage: vi.fn() })
    return null
  }
  const tree = (api: WorkspaceApi) => <NotificationProvider><main id="workspace-content" tabIndex={-1}><Harness api={api} /></main></NotificationProvider>
  const view = render(tree(first))
  act(() => hub.handlers.onHead(notificationHeadFixture))
  await flush()
  expect(screen.getByText('Notification 1')).toBeInTheDocument()
  view.rerender(tree(second))
  expect(screen.queryByText('Notification 1')).toBeNull()
})

describe('in-app notification popups', () => {
  it('uses owner-scoped server claims across Wallet tabs and reloads, with no browser marker or read on timeout', async () => {
    // Stand-in for the server's atomic, owner-scoped shown_at claim; the browser must not invent its own marker.
    const shown = new Set<string>()
    const fetcher = vi.fn((_path: string, init: RequestInit) => {
      const owner = new Headers(init.headers).get('X-Vessel-User')!
      const popups = shown.has(owner) ? [] : [notificationWith(1)]
      shown.add(owner)
      return Promise.resolve(new Response(JSON.stringify({ popups, more: 0 })))
    })
    vi.stubGlobal('fetch', fetcher)
    const clientFor = (userId: string) => createWorkspaceApi({ kind: 'session', userId, sessionId: sessionFixture.session.id })
    function Harness({ api, paper }: { api: WorkspaceApi; paper: boolean }) {
      const hub = useInAppNotifications({ api, active: true, realJournalNotice: paper, onLink: vi.fn(), onOpenPage: vi.fn() })
      useEffect(() => { hub.handlers.onHead({ ...notificationHeadFixture, unreadCount: 1 }) }, [hub.handlers])
      return <span>{hub.status.unreadCount} unread</span>
    }
    const tree = (userId: string, paper: boolean) => <NotificationProvider><Harness api={clientFor(userId)} paper={paper} /></NotificationProvider>
    const first = render(tree(sessionFixture.user.id, true))
    const otherTab = render(tree(sessionFixture.user.id, false))
    await flush()
    expect(screen.getAllByText('Notification 1')).toHaveLength(1)
    expect(screen.getByText('Real journal.')).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(6000) })
    expect(screen.queryByText('Notification 1')).toBeNull()
    expect(screen.getAllByText('1 unread')).toHaveLength(2)
    first.unmount(); otherTab.unmount()
    const reloaded = render(tree(sessionFixture.user.id, true))
    await flush()
    expect(screen.queryByText('Notification 1')).toBeNull()
    reloaded.unmount()
    render(tree('7d2e4f61-3a7b-4c85-b0e1-6f8a2d5c7b34', true))
    await flush()
    expect(screen.getByText('Notification 1')).toBeInTheDocument()
    expect(fetcher).toHaveBeenCalledTimes(4)
    for (const [path, init] of fetcher.mock.calls) {
      expect(path).toBe('/api/notifications/claim') // Timers never mark anything read.
      expect(init).toMatchObject({ method: 'POST', credentials: 'same-origin' })
    }
    expect(localStorage.length).toBe(0)
    expect(sessionStorage.length).toBe(0)
  })

  it('claims at start-up only while visible and focused, then shows the claimed popups once', async () => {
    const api = client([{ popups: [notificationWith(1, { severity: 'critical' })], more: 0 }])
    const { hub } = setup(api)
    setActive({ focused: false })
    act(() => hub().handlers.onHead({ ...notificationHeadFixture, unreadCount: 1 }))
    await flush()
    expect(api.claimNotifications).not.toHaveBeenCalled()
    expect(screen.getByTestId('count')).toHaveTextContent('1')
    setActive({ focused: true })
    await flush()
    expect(api.claimNotifications).toHaveBeenCalledOnce()
    const popup = screen.getByRole('alert')
    expect(popup).toHaveTextContent('Notification 1')
    expect(popup.closest('[aria-live]')).toHaveAttribute('aria-live', 'assertive')
    // A frame for a notification this tab already saw does not claim again.
    act(() => hub().handlers.onFrame(frame(notificationWith(1, { severity: 'critical' }), 11)))
    await flush()
    expect(api.claimNotifications).toHaveBeenCalledOnce()
  })

  it('claims when a frame brings an unseen unread notification and shows only what the claim returns', async () => {
    const api = client([{ popups: [], more: 0 }, { popups: [], more: 0 }])
    const { hub } = setup(api)
    act(() => hub().handlers.onHead(notificationHeadFixture))
    await flush()
    act(() => hub().handlers.onFrame(frame(notificationWith(2), 11)))
    await flush()
    // Another tab won the claim: nothing pops up here.
    expect(api.claimNotifications).toHaveBeenCalledTimes(2)
    expect(screen.queryByRole('status')).toBeNull()
  })

  it('times popups by severity without marking them read on timeout', async () => {
    const api = client([{ popups: [notificationWith(1, { severity: 'critical' }), notificationWith(2, { severity: 'warning' }), notificationWith(3)], more: 0 }])
    const { hub } = setup(api)
    act(() => hub().handlers.onHead({ ...notificationHeadFixture, unreadCount: 3 }))
    await flush()
    expect(screen.getByText('Notification 2').closest('.notification')).toHaveClass('is-warning')
    expect(screen.getByText('Notification 2').closest('[aria-live]')).toHaveAttribute('aria-live', 'polite')
    await act(async () => { await vi.advanceTimersByTimeAsync(6000) })
    expect(screen.queryByText('Notification 3')).toBeNull()
    expect(screen.getByText('Notification 2')).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(6000) })
    expect(screen.queryByText('Notification 2')).toBeNull()
    expect(screen.getByText('Notification 1')).toBeInTheDocument()
    // Hidden tabs hold the timer.
    setActive({ visible: false })
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(screen.getByText('Notification 1')).toBeInTheDocument()
    setActive({ visible: true })
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000) })
    expect(screen.queryByText('Notification 1')).toBeNull()
    expect(api.readNotification).not.toHaveBeenCalled()
  })

  it('marks read from the close button and from a link, which navigates through the shell', async () => {
    const link: NotificationLink = { kind: 'account', label: 'Open account', playId: null, accountId: notificationFixture.content.subject.accountId }
    const api = client([{ popups: [notificationWith(1), notificationWith(2, { content: { ...notificationFixture.content, links: [link] } })], more: 0 }])
    const { hub, onLink } = setup(api)
    act(() => hub().handlers.onHead({ ...notificationHeadFixture, unreadCount: 2 }))
    await flush()
    act(() => screen.getAllByRole('button', { name: 'Mark as read and close' })[0]!.click())
    await flush()
    expect(api.readNotification).toHaveBeenCalledWith(notificationWith(1).id)
    act(() => screen.getByRole('button', { name: 'Open account' }).click())
    await flush()
    expect(onLink).toHaveBeenCalledWith(link)
    expect(api.readNotification).toHaveBeenLastCalledWith(notificationWith(2).id)
    expect(screen.queryByRole('status')).toBeNull()
  })

  it('closes a popup when another tab reads it or it is withdrawn, moving focus to the next popup', async () => {
    const api = client([{ popups: [notificationWith(1), notificationWith(2), notificationWith(3)], more: 0 }])
    const { hub } = setup(api)
    act(() => hub().handlers.onHead({ ...notificationHeadFixture, unreadCount: 3 }))
    await flush()
    const first = screen.getByText('Notification 1').closest<HTMLElement>('.notification')!
    act(() => first.focus())
    act(() => hub().handlers.onFrame(frame({ ...notificationWith(1), state: 'read', readAt: '2026-10-17T11:00:00Z' }, 12, 2)))
    expect(screen.queryByText('Notification 1')).toBeNull()
    expect(document.activeElement).toBe(screen.getByText('Notification 2').closest('.notification'))
    act(() => hub().handlers.onFrame(frame({ ...notificationWith(2), state: 'withdrawn' }, 13, 1)))
    expect(screen.queryByText('Notification 2')).toBeNull()
    expect(screen.getByText('Notification 3')).toBeInTheDocument()
    expect(api.readNotification).not.toHaveBeenCalled()
    expect(screen.getByTestId('count')).toHaveTextContent('1')
  })

  it('adds one backlog summary that opens the Notifications page', async () => {
    const api = client([{ popups: [notificationWith(1)], more: 7 }])
    const { hub, onOpenPage } = setup(api)
    act(() => hub().handlers.onHead({ ...notificationHeadFixture, unreadCount: 8 }))
    await flush()
    expect(screen.getByText('7 more unread notifications')).toBeInTheDocument()
    act(() => screen.getByRole('button', { name: 'Open notifications' }).click())
    expect(onOpenPage).toHaveBeenCalledOnce()
  })

  it('drops popups no longer unread after a reset and tells listeners to reload', async () => {
    const api = client([{ popups: [notificationWith(1), notificationWith(2)], more: 0 }])
    vi.mocked(api.notifications).mockResolvedValue({ items: [notificationWith(2)], page: 1, pageSize: 100, total: 1, unreadTotal: 1, allTotal: 2 })
    const { hub } = setup(api)
    const listener = vi.fn()
    act(() => { hub().subscribe(listener); hub().handlers.onHead({ ...notificationHeadFixture, unreadCount: 2 }) })
    await flush()
    act(() => hub().handlers.onStatus({ state: 'reset', nextCursor: notificationCursor(1, 1), unreadCount: 1, enabled: true }))
    await flush()
    expect(listener).toHaveBeenCalledWith({ type: 'reset' })
    expect(screen.queryByText('Notification 1')).toBeNull()
    expect(screen.getByText('Notification 2')).toBeInTheDocument()
    expect(hub().cursor()).toBe(notificationCursor(1, 1))
    // Switching the channel off closes the rest.
    act(() => hub().handlers.onStatus({ state: 'reset', nextCursor: notificationCursor(1, 2), unreadCount: 1, enabled: false }))
    expect(screen.queryByText('Notification 2')).toBeNull()
  })

  it('keeps the newest cursor and ignores a head from an older reset', () => {
    const { hub } = setup(client())
    act(() => hub().handlers.onHead(notificationHeadFixture))
    act(() => hub().handlers.onFrame(frame(notificationWith(4, { state: 'read', readAt: '2026-10-17T11:00:00Z' }), 15, 0)))
    expect(hub().cursor()).toBe(notificationCursor(15))
    act(() => hub().handlers.onStatus({ state: 'reset', nextCursor: notificationCursor(2, 1), unreadCount: 0, enabled: true }))
    act(() => hub().acceptHead({ ...notificationHeadFixture, unreadCount: 40 }))
    expect(hub().cursor()).toBe(notificationCursor(2, 1))
    expect(screen.getByTestId('count')).toHaveTextContent('0')
  })

  it('ignores a head that a newer frame of the same reset has passed', async () => {
    const { hub } = setup(client())
    act(() => hub().handlers.onHead(notificationHeadFixture))
    await flush()
    act(() => hub().handlers.onFrame(frame(notificationWith(1), 11, 1)))
    act(() => hub().acceptHead({ ...notificationHeadFixture, unreadCount: 0 }))
    expect(hub().status.unreadCount).toBe(1)
  })

  it('reconciles after a reset only the popups shown when the unread rows were requested', async () => {
    const api = client([{ popups: [notificationWith(1)], more: 0 }, { popups: [], more: 0 }, { popups: [notificationWith(2)], more: 0 }])
    let answer!: (page: Awaited<ReturnType<WorkspaceApi['notifications']>>) => void
    vi.mocked(api.notifications).mockImplementationOnce(() => new Promise(resolve => { answer = resolve }))
    const { hub } = setup(api)
    act(() => hub().handlers.onHead({ ...notificationHeadFixture, unreadCount: 1 }))
    await flush()
    act(() => hub().handlers.onStatus({ state: 'reset', nextCursor: notificationCursor(11, 1), unreadCount: 1, enabled: true }))
    await flush()
    act(() => hub().handlers.onFrame({ ...frame(notificationWith(2), 12, 2), cursor: notificationCursor(12, 1) }))
    await flush()
    expect(screen.getByText('Notification 2')).toBeInTheDocument()
    await act(async () => { answer({ items: [notificationWith(1)], page: 1, pageSize: 100, total: 1, unreadTotal: 1, allTotal: 1 }) })
    expect(screen.getByText('Notification 2')).toBeInTheDocument()
  })
})
