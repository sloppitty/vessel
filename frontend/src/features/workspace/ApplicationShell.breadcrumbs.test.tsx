import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import type { SavedPlay } from '@/api/plays'
import { accountFixture, overviewFixture, playApiStubs, playCountsFrom, playsFrom, savedPlayFixture } from '@/test/workspace-fixture'
import { activityPage, operationFixture } from '@/test/activity-fixture'
import { reviewFixture } from '@/test/review-fixture'
import { systemFixture } from '@/test/system-fixture'
import { notificationWith } from '@/test/notification-fixture'
import { ApplicationShell } from './ApplicationShell'
import { ShellBreadcrumb } from './ShellBreadcrumb'
import type { ShellLocation } from './shell-history'
import { credentialsFixture, shellIdentityFixture } from '@/test/identity-fixture'

const play = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, title: 'HYPE Long · 24 Oct 2026' } }
function client(overrides: Partial<WorkspaceApi> = {}): WorkspaceApi {
  return {
    ...createWorkspaceApi(credentialsFixture), ...playApiStubs(),
    overview: vi.fn().mockResolvedValue(overviewFixture), accounts: vi.fn().mockResolvedValue([accountFixture]),
    snapshot: vi.fn().mockResolvedValue(null), fills: vi.fn().mockResolvedValue([]),
    activity: vi.fn().mockResolvedValue(activityPage([])), play: vi.fn().mockResolvedValue(play),
    plays: vi.fn(playsFrom([play.summary])), playCounts: vi.fn(playCountsFrom([play.summary])),
    manualExecutions: vi.fn().mockResolvedValue({ executions: [], corrections: [] }),
    review: vi.fn().mockResolvedValue(reviewFixture), ...overrides,
  }
}
const breadcrumb = () => screen.getByRole('navigation', { name: 'Breadcrumb' })
const labels = () => within(breadcrumb()).getAllByRole('listitem').map(item => item.textContent)
const backLink = (name: string) => within(breadcrumb()).getByRole('link', { name: `${name} — previous location` })
const mount = (api = client()) => render(<ApplicationShell identity={shellIdentityFixture()} api={api} system={systemFixture} disconnect={vi.fn()} />)

beforeAll(async () => {
  await Promise.all([import('@/features/plays/PlaysPage'), import('@/features/review/ReviewPage'), import('./SettingsPage')])
}, 60_000)
beforeEach(() => {
  window.history.replaceState(null, '', '/#overview')
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1440, writable: true })
})
afterEach(() => vi.restoreAllMocks())

describe('shell route history', () => {
  it('resets on top-level navigation, including navigating to a page already in the trail', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: 'View Main account' }))
    expect(labels()).toEqual(['Overview', 'Accounts', 'Main account'])
    await user.click(screen.getByRole('link', { name: 'Accounts' }))
    expect(labels()).toEqual(['Accounts'])
    await user.click(await screen.findByRole('button', { name: 'View Main account' }))
    await user.click(screen.getByRole('link', { name: 'Settings' }))
    expect(labels()).toEqual(['Settings', 'Appearance'])
    await user.click(screen.getByRole('link', { name: 'Vessel overview' }))
    expect(labels()).toEqual(['Overview'])
  })

  it('keeps account filters when Overview → account detail returns through the Accounts crumb', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(screen.getByRole('link', { name: 'Accounts' }))
    await user.type(await screen.findByRole('textbox', { name: 'Search accounts' }), 'Main')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Filter by status' }), 'enabled')
    await user.click(screen.getByRole('link', { name: 'Overview' }))
    await user.click(await screen.findByRole('button', { name: 'View Main account' }))
    expect(labels()).toEqual(['Overview', 'Accounts', 'Main account'])
    expect(within(breadcrumb()).getByText('Main account')).toHaveAttribute('aria-current', 'page')
    expect(within(breadcrumb()).queryByRole('link', { name: /^Main account/ })).not.toBeInTheDocument()
    await user.click(backLink('Accounts'))
    expect(labels()).toEqual(['Overview', 'Accounts'])
    expect(await screen.findByRole('textbox', { name: 'Search accounts' })).toHaveValue('Main')
    expect(screen.getByRole('combobox', { name: 'Filter by status' })).toHaveValue('enabled')
  })

  it('pops Accounts → detail through the existing All accounts button', async () => {
    const user = userEvent.setup()
    window.history.replaceState(null, '', '/#accounts')
    mount()
    await user.click(await screen.findByRole('button', { name: 'View Main account' }))
    expect(labels()).toEqual(['Accounts', 'Main account'])
    await user.click(screen.getByRole('button', { name: 'All accounts' }))
    expect(labels()).toEqual(['Accounts'])
  })

  it('opens a Review Play with its target page and actual saved title, then pops through All plays', async () => {
    const user = userEvent.setup()
    window.history.replaceState(null, '', '/#review')
    mount()
    await user.click(await screen.findByRole('button', { name: 'Inspect Swing trading' }))
    await user.click(await screen.findByRole('button', { name: 'Open Play BTC breakout' }))
    await screen.findByRole('textbox', { name: 'Play title' })
    await waitFor(() => expect(labels()).toEqual(['Review', 'Plays', play.summary.title]))
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    expect(labels()).toEqual(['Review', 'Plays'])
    await user.click(backLink('Review'))
    expect(labels()).toEqual(['Review'])
  })

  it('includes account detail as the source of an opened Play and reveals hidden locations without loops', async () => {
    const user = userEvent.setup()
    mount(client({ activity: vi.fn().mockResolvedValue(activityPage([
      { ...operationFixture, playId: play.summary.id, playAssociation: 'linked', playTitle: play.summary.title },
    ])) }))
    await user.click(await screen.findByRole('button', { name: 'View Main account' }))
    await user.click(await screen.findByRole('button', { name: `${play.summary.title}, open Play` }))
    await screen.findByRole('textbox', { name: 'Play title' })
    expect(labels()).toEqual(['Overview', '…', 'Plays', play.summary.title])
    const more = within(breadcrumb()).getByRole('button', { name: 'Show 2 more locations' })
    expect(more).toHaveAttribute('aria-expanded', 'false')
    await user.click(more)
    expect(more).toHaveAttribute('aria-expanded', 'true')
    await user.click(screen.getByRole('link', { name: 'Main account — previous location' }))
    await screen.findByRole('button', { name: 'All accounts' })
    expect(labels()).toEqual(['Overview', 'Accounts', 'Main account'])
    await user.click(backLink('Overview'))
    expect(labels()).toEqual(['Overview'])
  })

  it.each([false, true])('retains the trail, hash, page and edits on cancelled Play restoration (editor hidden=%s), then allows retry', async hidden => {
    const user = userEvent.setup()
    const second = { ...play, summary: { ...play.summary, id: '33333333-4444-5555-6666-777777777777', title: 'Second saved Play' } }
    const popup = notificationWith(1, { content: { ...notificationWith(1).content,
      links: [{ kind: 'play', label: 'Open second Play', playId: second.summary.id, accountId: null }] } })
    const accountPopup = notificationWith(2, { content: { ...notificationWith(2).content,
      links: [{ kind: 'account', label: 'Open account detail', playId: null, accountId: accountFixture.id }] } })
    const api = client({
      play: vi.fn(id => Promise.resolve(id === second.summary.id ? second : play)),
      plays: vi.fn(playsFrom([play.summary, second.summary])),
      playCounts: vi.fn(playCountsFrom([play.summary, second.summary])),
      claimNotifications: vi.fn().mockResolvedValue({ popups: [popup, accountPopup], more: 0 }),
      readNotification: vi.fn(id => Promise.resolve({ ...(id === popup.id ? popup : accountPopup), state: 'read' as const })),
    })
    window.history.replaceState(null, '', '/#review')
    mount(api)
    await user.click(await screen.findByRole('button', { name: 'Inspect Swing trading' }))
    await user.click(await screen.findByRole('button', { name: 'Open Play BTC breakout' }))
    await screen.findByRole('textbox', { name: 'Play title' })
    await user.click(await screen.findByRole('button', { name: 'Open second Play' }))
    const title = await screen.findByRole('textbox', { name: 'Play title' })
    await waitFor(() => expect(title).toHaveValue(second.summary.title))
    await user.type(title, ' with unsaved edits')
    expect(labels()).toEqual(['Review', 'Plays', play.summary.title, second.summary.title])
    if (hidden) {
      await user.click(screen.getByRole('button', { name: 'Open account detail' }))
      await screen.findByRole('button', { name: 'All accounts' })
    }
    const before = labels(), hash = window.location.hash
    const openEarlierPlay = async () => {
      if (!screen.queryByRole('link', { name: `${play.summary.title} — previous location` })) {
        await user.click(within(breadcrumb()).getByRole('button', { name: 'Show 3 more locations' }))
      }
      await user.click(screen.getByRole('link', { name: `${play.summary.title} — previous location` }))
    }
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await openEarlierPlay()
    await waitFor(() => expect(confirm).toHaveBeenCalledWith('Discard unsaved Play work and open this Play?'))
    expect(labels()).toEqual(before)
    expect(window.location.hash).toBe(hash)
    if (!hidden) expect(title).toHaveValue(`${second.summary.title} with unsaved edits`)
    else expect(screen.queryByRole('textbox', { name: 'Play title' })).not.toBeInTheDocument()
    expect(api.play).toHaveBeenCalledTimes(2)
    confirm.mockReturnValue(true)
    await openEarlierPlay()
    await waitFor(() => expect(labels()).toEqual(['Review', 'Plays', play.summary.title]))
    expect(confirm).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue(play.summary.title)
  })

  it('keeps draft titles current and never offers an unsaved draft as a history link', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    const title = await screen.findByRole('textbox', { name: 'Play title' })
    expect(labels()).toEqual(['Overview', 'Plays', 'New play'])
    await user.type(title, 'Draft idea')
    expect(labels()).toEqual(['Overview', 'Plays', 'Draft idea'])
    expect(within(breadcrumb()).queryByRole('link', { name: /^Draft idea/ })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    expect(labels()).toEqual(['Overview', 'Plays'])
  })

  it('does not add a hidden editor to the current trail when an earlier Play save completes', async () => {
    let finish!: (play: SavedPlay) => void
    const api = client({ updatePlay: vi.fn(() => new Promise<SavedPlay>(resolve => { finish = resolve })) })
    const user = userEvent.setup()
    window.history.replaceState(null, '', '/#review')
    mount(api)
    await user.click(await screen.findByRole('button', { name: 'Inspect Swing trading' }))
    await user.click(await screen.findByRole('button', { name: 'Open Play BTC breakout' }))
    const title = await screen.findByRole('textbox', { name: 'Play title' })
    await user.clear(title)
    await user.type(title, 'Renamed saved Play')
    await user.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(api.updatePlay).toHaveBeenCalledOnce())
    await user.click(screen.getByRole('link', { name: 'Settings' }))
    expect(labels()).toEqual(['Settings', 'Appearance'])
    await act(async () => finish({ ...play, summary: { ...play.summary, title: 'Renamed saved Play', version: 2 } }))
    expect(labels()).toEqual(['Settings', 'Appearance'])
    await user.click(screen.getByRole('link', { name: 'Plays' }))
    expect(await screen.findByRole('textbox', { name: 'Play title' })).toHaveValue('Renamed saved Play')
    expect(labels()).toEqual(['Plays', 'Renamed saved Play'])
  })

  it('truncates for external hash navigation to a parent and resets for a new hash page; remount starts fresh', async () => {
    const user = userEvent.setup()
    const view = mount()
    await user.click(await screen.findByRole('button', { name: 'View Main account' }))
    act(() => { window.history.replaceState(null, '', '/#overview'); fireEvent(window, new HashChangeEvent('hashchange')) })
    expect(labels()).toEqual(['Overview'])
    act(() => { window.history.replaceState(null, '', '/#activity'); fireEvent(window, new HashChangeEvent('hashchange')) })
    expect(labels()).toEqual(['Activity'])
    view.unmount()
    mount()
    expect(labels()).toEqual(['Activity'])
  })
})

describe('compact breadcrumb accessibility', () => {
  const trail: ShellLocation[] = [
    { kind: 'page', page: 'overview', label: 'Overview' },
    { kind: 'page', page: 'accounts', label: 'Accounts' },
    { kind: 'account', page: 'accounts', id: accountFixture.id, label: accountFixture.name },
    { kind: 'page', page: 'plays', label: 'Plays' },
    { kind: 'play', page: 'plays', id: play.summary.id, label: play.summary.title.repeat(10) },
  ]
  it.each([390, 320])('keeps a collapsed, keyboard-accessible route at %i px', async width => {
    window.innerWidth = width
    const user = userEvent.setup(), onVisit = vi.fn()
    render(<ShellBreadcrumb trail={trail} onVisit={onVisit} />)
    expect(labels()).toEqual(width === 320 ? ['…', trail[4]!.label] : ['…', 'Plays', trail[4]!.label])
    const current = within(breadcrumb()).getByText(trail[4]!.label)
    expect(current).toHaveAttribute('aria-current', 'page')
    expect(current).toHaveAttribute('title', trail[4]!.label)
    const more = within(breadcrumb()).getByRole('button', { name: `Show ${width === 320 ? 4 : 3} more locations` })
    more.focus()
    await user.keyboard('{Enter}')
    expect(more).toHaveAttribute('aria-expanded', 'true')
    const link = screen.getByRole('link', { name: 'Overview — previous location' })
    expect(link).toHaveAttribute('href', '#overview')
    await user.click(link)
    expect(onVisit).toHaveBeenCalledWith(trail[0])
  })
})
