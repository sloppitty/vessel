import { useState } from 'react'
import { act, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import type { PlayList, PlayListQuery, PlaySummary } from '@/api/plays'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { accountFixture, idleMarketStream, playApiStubs, playCountsFrom, playsFrom, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { PlaysPage } from './PlaysPage'
import { createPlaysSession } from './saved'
import { initialPlaysList, type PlaysListState } from './plays-list'
import type { AccountMonitor } from '@/api/monitor'
import { discrepancyFixture, missingStopFixture, monitorFixture, shortStopFixture } from '@/test/monitor-fixture'

const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`
const play = (n: number, fields: Partial<PlaySummary> = {}): PlaySummary => ({ ...savedPlayFixture.summary, id: id(n), title: `Play ${n}`,
  updatedAtUtc: new Date(Date.parse('2026-10-10T00:00:00Z') - n * 60_000).toISOString(), ...fields })
const ended = (n: number, status: 'closed' | 'cancelled', fields: Partial<PlaySummary> = {}) =>
  play(n, { status, endedAtUtc: new Date(Date.parse('2026-10-09T00:00:00Z') - n * 60_000).toISOString(), ...fields })
const library = [
  play(1, { title: 'Draft breakout' }), play(2, { title: 'Planned BTC', status: 'planned' }), play(3, { title: 'Open ETH', status: 'open', instrument: 'ETH' }),
  ended(4, 'closed', { title: 'Closed loser' }), ended(5, 'closed', { title: 'Closed reviewed', hasReview: true }), ended(6, 'cancelled', { title: 'Cancelled idea' }),
]
function client(plays: PlaySummary[] = library, overrides: Partial<WorkspaceApi> = {}) {
  return { ...createWorkspaceApi('test-only'), ...playApiStubs(), marketStream: idleMarketStream,
    plays: vi.fn(playsFrom(plays)), playCounts: vi.fn(playCountsFrom(plays)), ...overrides } as WorkspaceApi & {
    plays: ReturnType<typeof vi.fn<WorkspaceApi['plays']>>; playCounts: ReturnType<typeof vi.fn<WorkspaceApi['playCounts']>> }
}
/** The shell's memory: the Play session and the list state live outside the page. */
function Shell({ api, initial = initialPlaysList, onList, monitors }: { api: WorkspaceApi; initial?: PlaysListState; onList?: (list: PlaysListState) => void
  monitors?: AccountMonitor[] }) {
  const [session, setSession] = useState(createPlaysSession)
  const [list, setList] = useState(initial)
  return <NotificationProvider><PlaysPage accounts={[accountFixture]} portfolios={[portfolioFixture]} api={api} session={session} onSession={setSession}
    list={list} onList={next => { setList(next); onList?.(next) }} {...monitors ? { accountMonitors: monitors } : {}} /></NotificationProvider>
}
const lastQuery = (api: ReturnType<typeof client>) => api.plays.mock.calls.at(-1)![0]

describe('Plays list views', () => {
  it('opens on In progress with counts for every view and splits drafts from in progress', async () => {
    const api = client()
    render(<Shell api={api} />)
    expect(await screen.findByRole('button', { name: 'Open Planned BTC' })).toBeInTheDocument()
    expect(await screen.findByRole('tab', { name: 'In progress 2' })).toHaveAttribute('aria-selected', 'true')
    for (const name of ['Drafts 1', 'Needs review 2', 'Reviewed 1', 'Closed 2', 'Cancelled 1', 'All 6']) expect(screen.getByRole('tab', { name })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Open Open ETH' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Open Draft breakout' })).not.toBeInTheDocument()
    expect(lastQuery(api)).toEqual({ view: 'progress' })
    expect(screen.queryByRole('navigation', { name: 'Plays pages' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Drafts 1' }))
    expect(await screen.findByRole('button', { name: 'Open Draft breakout' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Needs review 2' }))
    expect(await screen.findByRole('button', { name: 'Open Closed loser' })).toHaveTextContent('Pending review')
    expect(screen.getByRole('button', { name: 'Open Cancelled idea' })).toBeInTheDocument()
    expect(lastQuery(api)).toEqual({ view: 'review', page: 1, pageSize: 25 })
    await userEvent.click(screen.getByRole('tab', { name: 'Cancelled 1' }))
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Open Closed loser' })).not.toBeInTheDocument())
    expect(screen.getByRole('navigation', { name: 'Plays pages' })).toHaveTextContent('1–1 of 1')
  })
  it('searches title and instrument after typing pauses, with counts for the same search', async () => {
    const api = client()
    render(<Shell api={api} />)
    await screen.findByRole('button', { name: 'Open Planned BTC' })
    const search = screen.getByRole('searchbox', { name: 'Search plays' })
    await userEvent.type(search, 'eth')
    expect(api.plays.mock.calls.some(([query]) => query.q)).toBe(false)
    await waitFor(() => expect(lastQuery(api)).toEqual({ view: 'progress', q: 'eth' }))
    expect(api.plays.mock.calls.filter(([query]) => query.q).map(([query]) => query.q)).toEqual(['eth'])
    await waitFor(() => expect(api.playCounts).toHaveBeenLastCalledWith({ q: 'eth' }, expect.any(AbortSignal)))
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Open Planned BTC' })).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Open Open ETH' })).toBeInTheDocument()
    expect(await screen.findByRole('tab', { name: 'In progress 1' })).toBeInTheDocument()
    await userEvent.clear(search)
    await userEvent.type(search, 'nothing')
    expect(await screen.findByText('No plays match “nothing”.')).toBeInTheDocument()
  })
  it('pages ended views, returns to page 1 on a size change and scrolls to the list', async () => {
    const many = Array.from({ length: 60 }, (_, n) => ended(n + 1, 'closed'))
    const api = client(many)
    const states: PlaysListState[] = []
    const scroll = vi.fn()
    Element.prototype.scrollIntoView = scroll
    try {
      render(<Shell api={api} initial={{ ...initialPlaysList, view: 'closed' }} onList={state => states.push(state)} />)
      const nav = await screen.findByRole('navigation', { name: 'Plays pages' })
      expect(nav).toHaveTextContent('1–25 of 60')
      await userEvent.click(within(nav).getByRole('button', { name: '3' }))
      expect(scroll).toHaveBeenCalled()
      await waitFor(() => expect(lastQuery(api)).toEqual({ view: 'closed', page: 3, pageSize: 25 }))
      expect(await screen.findByRole('button', { name: 'Open Play 60' })).toBeInTheDocument()
      expect(screen.getByRole('navigation', { name: 'Plays pages' })).toHaveTextContent('51–60 of 60')
      await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Rows per page' }), '100')
      await waitFor(() => expect(lastQuery(api)).toEqual({ view: 'closed', page: 1, pageSize: 100 }))
      expect(states.at(-1)).toEqual({ ...initialPlaysList, view: 'closed', page: 1, pageSize: 100 })
      await userEvent.click(screen.getByRole('tab', { name: /^All/ }))
      expect(states.at(-1)).toEqual({ ...initialPlaysList, view: 'all', page: 1, pageSize: 100 })
    } finally { delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView }
  })
  it('keeps the page the server clamped to, without reading it again, so a later return after growth stays there', async () => {
    const many = Array.from({ length: 30 }, (_, n) => ended(n + 1, 'closed'))
    const api = client(many)
    const states: PlaysListState[] = []
    const view = render(<Shell api={api} initial={{ ...initialPlaysList, view: 'closed', page: 5 }} onList={state => states.push(state)} />)
    expect(await screen.findByRole('navigation', { name: 'Plays pages' })).toHaveTextContent('26–30 of 30')
    await waitFor(() => expect(states.at(-1)).toEqual({ ...initialPlaysList, view: 'closed', page: 2 }))
    expect(api.plays.mock.calls.map(([query]) => query.page)).toEqual([5])
    view.unmount()
    // More Plays end while the owner is elsewhere; returning keeps the page that was shown, not the stale request.
    many.push(...Array.from({ length: 170 }, (_, n) => ended(n + 31, 'closed')))
    render(<Shell api={api} initial={states.at(-1)!} />)
    expect(await screen.findByRole('navigation', { name: 'Plays pages' })).toHaveTextContent('26–50 of 200')
    expect(lastQuery(api)).toEqual({ view: 'closed', page: 2, pageSize: 25 })
  })
  it('ignores a clamped answer to a request that is no longer current', async () => {
    let late!: (list: PlayList) => void
    const many = Array.from({ length: 60 }, (_, n) => ended(n + 1, 'closed'))
    const plays = vi.fn((query: PlayListQuery) => query.page === 5 ? new Promise<PlayList>(resolve => { late = resolve }) : playsFrom(many)(query))
    const states: PlaysListState[] = []
    render(<Shell api={client(many, { plays })} initial={{ ...initialPlaysList, view: 'closed', page: 5 }} onList={state => states.push(state)} />)
    await waitFor(() => expect(plays).toHaveBeenCalledTimes(1))
    await userEvent.click(await screen.findByRole('tab', { name: /^All/ }))
    expect(await screen.findByRole('navigation', { name: 'Plays pages' })).toHaveTextContent('1–25 of 60')
    await act(async () => late({ plays: many.slice(50, 60), view: 'closed', page: 3, pageSize: 25, totalCount: 60 }))
    expect(states.every(state => state.page === 1)).toBe(true)
    expect(plays).toHaveBeenCalledTimes(2)
  })
  it('says when an unpaged view was cut at 200', async () => {
    const many = Array.from({ length: 230 }, (_, n) => play(n + 1, { status: 'planned' }))
    render(<Shell api={client(many)} />)
    expect(await screen.findByRole('status')).toHaveTextContent('Showing the 200 most recently updated of 230. Search to narrow the list.')
    expect(screen.getAllByRole('button', { name: /^Open Play/ })).toHaveLength(200)
    expect(screen.queryByRole('navigation', { name: 'Plays pages' })).not.toBeInTheDocument()
  })
  it('shows a delayed skeleton on first load and dims the previous rows while another view loads', async () => {
    let finish!: (list: PlayList) => void
    const read = vi.fn(playsFrom(library))
    read.mockImplementationOnce(() => new Promise<PlayList>(resolve => { finish = resolve }))
    render(<Shell api={client(library, { plays: read })} />)
    expect(screen.queryByText('Loading plays…')).not.toBeInTheDocument()
    expect(await screen.findByText('Loading plays…')).toBeInTheDocument()
    await act(async () => finish(await playsFrom(library)({ view: 'progress' })))
    const row = await screen.findByRole('button', { name: 'Open Planned BTC' })
    let next!: (list: PlayList) => void
    read.mockImplementationOnce(() => new Promise<PlayList>(resolve => { next = resolve }))
    await userEvent.click(screen.getByRole('tab', { name: /^All/ }))
    expect(row.closest('ul')).toHaveAttribute('aria-busy', 'true')
    expect(row.closest('ul')).toHaveClass('list-refetching')
    expect(screen.queryByText('Loading plays…')).not.toBeInTheDocument()
    await act(async () => next(await playsFrom(library)({ view: 'all', page: 1, pageSize: 25 } as PlayListQuery)))
    expect(await screen.findByRole('button', { name: 'Open Draft breakout' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Open Draft breakout' }).closest('ul')).not.toHaveAttribute('aria-busy')
  })
  it('keeps the view, search, page and size after opening a Play and returning', async () => {
    const many = Array.from({ length: 30 }, (_, n) => ended(n + 1, 'closed', { title: `BTC idea ${n + 1}` }))
    const target = many[27]!
    const api = client(many, { play: vi.fn().mockResolvedValue({ ...savedPlayFixture, summary: target }) })
    render(<Shell api={api} />)
    await userEvent.click(await screen.findByRole('tab', { name: /^Closed/ }))
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search plays' }), 'btc')
    await waitFor(() => expect(lastQuery(api)).toEqual({ view: 'closed', q: 'btc', page: 1, pageSize: 25 }))
    await userEvent.click(within(await screen.findByRole('navigation', { name: 'Plays pages' })).getByRole('button', { name: 'Next' }))
    await userEvent.click(await screen.findByRole('button', { name: `Open ${target.title}` }))
    await userEvent.click(await screen.findByRole('button', { name: 'All plays' }))
    expect(await screen.findByRole('tab', { name: /^Closed/ })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('searchbox', { name: 'Search plays' })).toHaveValue('btc')
    expect(await screen.findByRole('navigation', { name: 'Plays pages' })).toHaveTextContent('26–30 of 30')
    expect(lastQuery(api)).toEqual({ view: 'closed', q: 'btc', page: 2, pageSize: 25 })
  })
  it('replaces the search box when the shell opens another view without a search', async () => {
    const api = client()
    const { rerender } = render(<PlaysPageWith api={api} list={{ ...initialPlaysList, q: 'eth' }} />)
    expect(await screen.findByRole('searchbox', { name: 'Search plays' })).toHaveValue('eth')
    rerender(<PlaysPageWith api={api} list={{ ...initialPlaysList, view: 'review' }} />)
    expect(screen.getByRole('searchbox', { name: 'Search plays' })).toHaveValue('')
    expect(screen.getByRole('tab', { name: /^Needs review/ })).toHaveAttribute('aria-selected', 'true')
    await waitFor(() => expect(lastQuery(api)).toEqual({ view: 'review', page: 1, pageSize: 25 }))
  })
})

function PlaysPageWith({ api, list }: { api: WorkspaceApi; list: PlaysListState }) {
  const [session, setSession] = useState(createPlaysSession)
  return <NotificationProvider><PlaysPage accounts={[accountFixture]} portfolios={[portfolioFixture]} api={api} session={session} onSession={setSession}
    list={list} onList={vi.fn()} /></NotificationProvider>
}

describe('Plays list monitor incidents (Phase 0, 0.4a)', () => {
  it('marks each row with its open incidents from the monitor summary, by Play or by the instrument\'s listed Plays', async () => {
    const api = client()
    const monitors: AccountMonitor[] = [{ ...monitorFixture, discrepancies: [
      { ...missingStopFixture, playId: id(3) },
      // An instrument-level record names its Plays in details.
      { ...discrepancyFixture, id: 'position', kind: 'position.excess', playId: null, details: { playIds: [id(2), id(3)], expectedQuantity: '1', venueQuantity: '2' } },
      // Resolved and dismissed records are history, not incidents.
      { ...shortStopFixture, playId: id(2), state: 'resolved' },
      { ...shortStopFixture, id: 'dismissed', playId: id(2), state: 'dismissed' },
    ] }]
    render(<Shell api={api} monitors={monitors} />)
    const open = await screen.findByRole('button', { name: 'Open Open ETH' })
    expect(within(open).getByText('1 critical · 1 warning')).toBeInTheDocument()
    expect(open).toHaveAccessibleDescription(expect.stringContaining('Needs attention: 1 critical · 1 warning'))
    expect(within(screen.getByRole('button', { name: 'Open Planned BTC' })).getByText('1 warning')).toBeInTheDocument()
  })
  it('shows nothing on rows without an open incident', async () => {
    const api = client()
    render(<Shell api={api} monitors={[{ ...monitorFixture, discrepancies: [{ ...shortStopFixture, playId: id(3), state: 'resolved' }] }]} />)
    const open = await screen.findByRole('button', { name: 'Open Open ETH' })
    expect(within(open).queryByText(/critical|warning/)).not.toBeInTheDocument()
  })
})
