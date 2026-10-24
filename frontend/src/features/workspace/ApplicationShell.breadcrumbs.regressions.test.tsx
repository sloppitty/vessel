import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import type { SavedPlay } from '@/api/plays'
import { accountFixture, overviewFixture, playApiStubs, playCountsFrom, playsFrom, savedPlayFixture } from '@/test/workspace-fixture'
import { activityPage } from '@/test/activity-fixture'
import { reviewFixture } from '@/test/review-fixture'
import { systemFixture } from '@/test/system-fixture'
import { notificationWith } from '@/test/notification-fixture'
import { ApplicationShell } from './ApplicationShell'

const first = savedPlayFixture
const second = { ...first, summary: { ...first.summary, id: '33333333-4444-5555-6666-777777777777', title: 'Second saved Play' } }
const labels = () => within(screen.getByRole('navigation', { name: 'Breadcrumb' }))
  .getAllByRole('listitem').map(item => item.textContent)
const crumb = (name: string) => within(screen.getByRole('navigation', { name: 'Breadcrumb' }))
  .getByRole('link', { name: `${name} — previous location` })

beforeAll(async () => {
  await Promise.all([import('@/features/plays/PlaysPage'), import('@/features/review/ReviewPage')])
}, 60_000)
beforeEach(() => {
  window.history.replaceState(null, '', '/#review')
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1440, writable: true })
})

function client(overrides: Partial<WorkspaceApi> = {}): WorkspaceApi {
  const popup = notificationWith(1, { content: { ...notificationWith(1).content,
    links: [{ kind: 'play', label: 'Open second Play', playId: second.summary.id, accountId: null }] } })
  return {
    ...createWorkspaceApi('synthetic-only'), ...playApiStubs(),
    overview: vi.fn().mockResolvedValue(overviewFixture), accounts: vi.fn().mockResolvedValue([accountFixture]),
    snapshot: vi.fn().mockResolvedValue(null), fills: vi.fn().mockResolvedValue([]),
    activity: vi.fn().mockResolvedValue(activityPage([])),
    play: vi.fn(id => Promise.resolve(id === second.summary.id ? second : first)),
    plays: vi.fn(playsFrom([first.summary, second.summary])),
    playCounts: vi.fn(playCountsFrom([first.summary, second.summary])),
    manualExecutions: vi.fn().mockResolvedValue({ executions: [], corrections: [] }),
    review: vi.fn().mockResolvedValue(reviewFixture),
    claimNotifications: vi.fn().mockResolvedValue({ popups: [popup], more: 0 }),
    readNotification: vi.fn().mockResolvedValue({ ...popup, state: 'read' }),
    ...overrides,
  }
}

async function openBoth(api: WorkspaceApi) {
  const user = userEvent.setup()
  render(<ApplicationShell api={api} system={systemFixture} disconnect={vi.fn()} />)
  await user.click(await screen.findByRole('button', { name: 'Inspect Swing trading' }))
  await user.click(await screen.findByRole('button', { name: 'Open Play BTC breakout' }))
  await screen.findByRole('textbox', { name: 'Play title' })
  await user.click(await screen.findByRole('button', { name: 'Open second Play' }))
  await waitFor(() => expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue(second.summary.title))
  expect(labels()).toEqual(['Review', 'Plays', first.summary.title, second.summary.title])
  return user
}

describe('pending breadcrumb restoration', () => {
  it('does not reopen an editor after the owner cancels restoration by going to the Plays list', async () => {
    let finish!: (play: SavedPlay) => void
    const api = client()
    const user = await openBoth(api)
    vi.mocked(api.play).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    await user.click(crumb(first.summary.title))
    await waitFor(() => expect(api.play).toHaveBeenCalledTimes(3))
    await user.click(crumb('Plays'))
    expect(labels()).toEqual(['Review', 'Plays'])
    expect(screen.queryByRole('textbox', { name: 'Play title' })).not.toBeInTheDocument()
    await act(async () => finish(first))
    expect(labels()).toEqual(['Review', 'Plays'])
    expect(screen.queryByRole('textbox', { name: 'Play title' })).not.toBeInTheDocument()
  })
})
