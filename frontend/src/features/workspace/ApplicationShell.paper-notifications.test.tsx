import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { WorkspaceApi } from '@/api/workspace'
import type { MonitorStreamEvent } from '@/api/monitor'
import type { NotificationLink } from '@/api/notifications'
import { systemFixture } from '@/test/system-fixture'
import { emptyOverview, overviewFixture, playApiStubs, playCountsFrom, playsFrom, savedPlayFixture } from '@/test/workspace-fixture'
import { eventCursor, eventFrame } from '@/test/monitor-fixture'
import { notificationCursor, notificationHeadFixture, notificationWith } from '@/test/notification-fixture'
import { shellIdentityFixture } from '@/test/identity-fixture'
import { ApplicationShell } from './ApplicationShell'

const system = { ...systemFixture, venues: systemFixture.venues.map(venue => ({
  ...venue, capabilities: { ...venue.capabilities, paperExecution: true },
})) }
const paperPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, tradingMode: 'paper' as const,
  id: '00000000-0000-4000-8000-000000000099', title: 'Paper work' } }

function clients(paperOverrides: Partial<WorkspaceApi> = {}) {
  let emit!: (frame: MonitorStreamEvent) => void
  const paper = { ...playApiStubs(), tradingMode: 'paper', overview: vi.fn().mockResolvedValue(emptyOverview),
    ...paperOverrides } as WorkspaceApi
  const real = {
    ...playApiStubs(), tradingMode: 'real', overview: vi.fn().mockResolvedValue(overviewFixture),
    snapshot: vi.fn().mockResolvedValue(null),
    notificationHead: vi.fn().mockResolvedValue(notificationHeadFixture),
    readNotification: vi.fn((id: string) => Promise.resolve({ ...notificationWith(1), id, state: 'read', readAt: '2026-10-17T11:00:00Z' })),
    play: vi.fn().mockResolvedValue(savedPlayFixture),
    forTradingMode: vi.fn((mode: string) => mode === 'paper' ? paper : real),
    monitorStream: vi.fn((_after: string, signal: AbortSignal, onEvent: typeof emit) => new Promise<void>(resolve => {
      emit = onEvent
      signal.addEventListener('abort', () => resolve(), { once: true })
    })),
  } as unknown as WorkspaceApi
  const popup = async (link: NotificationLink, n = 1, more = 0) => {
    const notification = notificationWith(n, { severity: 'critical', category: 'monitor', kind: 'monitor',
      content: { ...notificationWith(n).content, summary: 'A real position has no stop.', links: [link] } })
    vi.mocked(real.claimNotifications).mockResolvedValueOnce({ popups: [notification], more })
    act(() => emit({ type: 'notification', frame: { cursor: notificationCursor(10 + n), unreadCount: n, enabled: true, notification } }))
    await screen.findByRole('button', { name: link.label })
    return notification
  }
  return { real, paper, popup, emit: (frame: MonitorStreamEvent) => act(() => emit(frame)) }
}
type Clients = ReturnType<typeof clients>
const playLink: NotificationLink = { kind: 'play', label: 'Open real Play', playId: savedPlayFixture.summary.id, accountId: null }
const accountLink: NotificationLink = { kind: 'account', label: 'Open real account', playId: null, accountId: overviewFixture.accounts[0]!.id }
const identity = shellIdentityFixture()
const tree = (api: WorkspaceApi) => <ApplicationShell identity={identity} system={system} api={api} disconnect={vi.fn()} />

beforeAll(async () => {
  await Promise.all([import('@/features/plays/PlaysPage'), import('@/features/notifications/NotificationsPage')])
}, 60_000)
beforeEach(() => {
  window.history.replaceState(null, '', '/#plays')
  vi.spyOn(document, 'hasFocus').mockReturnValue(true)
})
afterEach(() => vi.restoreAllMocks())

async function enterPaper({ real, paper }: Clients) {
  await userEvent.click(await screen.findByRole('button', { name: 'Journal, switch to Paper playground' }))
  await waitFor(() => expect(paper.overview).toHaveBeenCalled())
  await waitFor(() => expect(real.monitorStream).toHaveBeenCalledTimes(2))
  await waitFor(() => expect(real.claimNotifications).toHaveBeenCalledTimes(2))
}
async function editPaper() {
  await userEvent.click(await screen.findByRole('button', { name: 'New play' }))
  await userEvent.type(await screen.findByRole('textbox', { name: 'Play title' }), 'Unsaved paper thesis')
}
const mode = () => screen.getByRole('button', { name: 'Paper playground, switch to Journal' })
const dialog = () => screen.getByRole('alertdialog', { name: 'Switch to Journal?' })

describe('real journal notifications in Paper mode', () => {
  it('uses real heads, streams, claims and reads, keeps both badges, and never refreshes paper pages from real events', async () => {
    const api = clients()
    render(tree(api.real))
    await enterPaper(api)
    const realReads = vi.mocked(api.real.overview).mock.calls.length
    const monitorReads = vi.mocked(api.real.monitor).mock.calls.length
    const paperReads = vi.mocked(api.paper.overview).mock.calls.length
    const notification = await api.popup(playLink, 1, 4)
    expect(api.real.notificationHead).toHaveBeenCalledTimes(2)
    expect(vi.mocked(api.real.monitorStream).mock.calls.at(-1)?.[3]).toBe(notificationHeadFixture.nextCursor)
    expect(screen.getAllByRole('link', { name: 'Notifications, 1 unread' })).toHaveLength(2)
    expect(screen.getAllByText(/Real journal\./)).toHaveLength(2)
    expect(screen.getByText(/4 more unread notifications/)).toBeInTheDocument()
    expect(screen.queryByText(overviewFixture.accounts[0]!.name)).not.toBeInTheDocument()
    api.emit({ type: 'status', status: { state: 'reset', nextCursor: eventCursor('10') } })
    api.emit(eventFrame())
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 300)) })
    expect(api.real.overview).toHaveBeenCalledTimes(realReads)
    expect(api.real.monitor).toHaveBeenCalledTimes(monitorReads)
    expect(api.paper.overview).toHaveBeenCalledTimes(paperReads)
    expect(api.paper.monitor).not.toHaveBeenCalled()
    expect(api.paper.monitorEvents).not.toHaveBeenCalled()
    expect(api.paper.monitorStream).not.toHaveBeenCalled()
    expect(api.paper.notificationHead).not.toHaveBeenCalled()
    expect(api.paper.claimNotifications).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Mark as read and close' }))
    expect(api.real.readNotification).toHaveBeenCalledWith(notification.id)
    expect(api.paper.readNotification).not.toHaveBeenCalled()
    expect(mode()).toBeInTheDocument()
  })

  it.each([playLink, accountLink])('cancels a $kind popup link without navigating, then confirms a fresh link to the real journal', async link => {
    const api = clients()
    render(tree(api.real))
    await enterPaper(api)
    await editPaper()
    await api.popup(link)
    await userEvent.click(screen.getByRole('button', { name: link.label }))
    expect(dialog()).toBeInTheDocument()
    expect(api.real.play).not.toHaveBeenCalled()
    expect(api.paper.play).not.toHaveBeenCalled()
    await userEvent.click(within(dialog()).getByRole('button', { name: 'Keep editing' }))
    expect(mode()).toBeInTheDocument()
    expect(window.location.hash).toBe('#plays')
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Unsaved paper thesis')
    await api.popup(link, 2)
    await userEvent.click(screen.getByRole('button', { name: link.label }))
    await userEvent.click(within(dialog()).getByRole('button', { name: 'Discard and switch' }))
    expect(await screen.findByRole('button', { name: 'Journal, switch to Paper playground' })).toBeInTheDocument()
    if (link.kind === 'play') {
      await waitFor(() => expect(api.real.play).toHaveBeenCalledWith(link.playId, expect.any(AbortSignal)))
      expect(await screen.findByRole('textbox', { name: 'Play title' })).toHaveValue(savedPlayFixture.summary.title)
    } else {
      expect(await screen.findByRole('heading', { level: 1, name: overviewFixture.accounts[0]!.name })).toBeInTheDocument()
    }
    expect(api.paper.play).not.toHaveBeenCalled()
    expect(screen.queryByDisplayValue('Unsaved paper thesis')).not.toBeInTheDocument()
  })

  it('guards the badge and backlog links before opening the real Notifications page', async () => {
    const api = clients()
    render(tree(api.real))
    await enterPaper(api)
    await editPaper()
    await api.popup(playLink, 1, 4)
    await userEvent.click(screen.getAllByRole('link', { name: 'Notifications, 1 unread' })[0]!)
    await userEvent.click(within(dialog()).getByRole('button', { name: 'Keep editing' }))
    expect(mode()).toBeInTheDocument()
    expect(api.real.notifications).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Open notifications' }))
    await userEvent.click(within(dialog()).getByRole('button', { name: 'Discard and switch' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Notifications' })).toBeInTheDocument()
    await waitFor(() => expect(api.real.notifications).toHaveBeenCalled())
    expect(api.paper.notifications).not.toHaveBeenCalled()
  })

  it('protects in-flight Paper work and ignores its late editor response after switching', async () => {
    let finish!: (play: typeof paperPlay) => void
    const api = clients({ plays: vi.fn(playsFrom([paperPlay.summary])), playCounts: vi.fn(playCountsFrom([paperPlay.summary])),
      play: vi.fn(() => new Promise<typeof paperPlay>(resolve => { finish = resolve })) })
    render(tree(api.real))
    await enterPaper(api)
    await userEvent.click(await screen.findByRole('tab', { name: /^Drafts/ }))
    await userEvent.click(await screen.findByRole('button', { name: `Open ${paperPlay.summary.title}` }))
    await api.popup(playLink)
    await userEvent.click(screen.getByRole('button', { name: playLink.label }))
    await userEvent.click(within(dialog()).getByRole('button', { name: 'Discard and switch' }))
    expect(await screen.findByRole('textbox', { name: 'Play title' })).toHaveValue(savedPlayFixture.summary.title)
    await act(async () => finish(paperPlay))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue(savedPlayFixture.summary.title)
  })

  it('does not follow a pending popup destination on a replaced API connection', async () => {
    const api = clients()
    const view = render(tree(api.real))
    await enterPaper(api)
    await editPaper()
    await api.popup(playLink)
    await userEvent.click(screen.getByRole('button', { name: playLink.label }))
    const pending = dialog()
    const next = clients()
    view.rerender(tree(next.real))
    await waitFor(() => expect(next.paper.overview).toHaveBeenCalled())
    await userEvent.click(within(pending).getByRole('button', { name: 'Discard and switch' }))
    expect(mode()).toBeInTheDocument()
    expect(api.real.play).not.toHaveBeenCalled()
    expect(next.real.play).not.toHaveBeenCalled()
  })
})
