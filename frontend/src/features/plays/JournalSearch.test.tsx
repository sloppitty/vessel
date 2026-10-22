import { useState } from 'react'
import { screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import type { JournalSearchPage, PlayOutcome, PlaySummary } from '@/api/plays'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { accountFixture, idleMarketStream, playApiStubs, playCountsFrom, playsFrom, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { PlaysPage } from './PlaysPage'
import { createPlaysSession } from './saved'
import { initialPlaysList, type PlaysListState } from './plays-list'

// Phase 2, item 13: journal-wide search on the Plays page, and each ended Play's outcome on its list row (F-07).
const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`
const outcome = (fields: Partial<PlayOutcome>): PlayOutcome => ({ state: 'active', exitAtUtc: null, resultUsd: null, estimatedR: null, reason: null, ...fields })
const play = (n: number, fields: Partial<PlaySummary> = {}): PlaySummary => ({ ...savedPlayFixture.summary, id: id(n), title: `Play ${n}`,
  updatedAtUtc: new Date(Date.parse('2026-10-10T00:00:00Z') - n * 60_000).toISOString(), outcome: outcome({}), ...fields })
const closed = (n: number, title: string, result: Partial<PlayOutcome>) => play(n, { title, status: 'closed', planRevision: 1,
  endedAtUtc: '2026-10-09T00:00:00Z', outcome: outcome(result) })

function client(plays: PlaySummary[], overrides: Partial<WorkspaceApi> = {}) {
  return { ...createWorkspaceApi('test-only'), ...playApiStubs(), marketStream: idleMarketStream,
    plays: vi.fn(playsFrom(plays)), playCounts: vi.fn(playCountsFrom(plays)), ...overrides } as WorkspaceApi
}
function Shell({ api, initial = initialPlaysList, onList }: { api: WorkspaceApi; initial?: PlaysListState; onList?: (list: PlaysListState) => void }) {
  const [session, setSession] = useState(createPlaysSession)
  const [list, setList] = useState(initial)
  return <NotificationProvider><PlaysPage accounts={[accountFixture]} portfolios={[portfolioFixture]} api={api} session={session} onSession={setSession}
    list={list} onList={next => { setList(next); onList?.(next) }} /></NotificationProvider>
}
const page = (items: JournalSearchPage['items'], fields: Partial<JournalSearchPage> = {}): JournalSearchPage =>
  ({ query: 'wombat', items, total: items.length, offset: 0, nextOffset: null, notIndexed: 0, ...fields })
const hit = (summary: PlaySummary, excerpt = '…moved the stop after the wombat candle…') =>
  ({ play: summary, field: 'log' as const, excerpt, matchStart: excerpt.indexOf('wombat'), matchLength: 6 })

describe('Plays list outcomes (F-07)', () => {
  it('shows an ended Play’s exit, result and estimated R from the projection, and unknown as unknown', async () => {
    const plays = [
      closed(1, 'Scored win', { state: 'scored', exitAtUtc: '2026-10-08T14:30:00Z', resultUsd: '12.5', estimatedR: '1.25' }),
      closed(2, 'Scored without R', { state: 'scored', exitAtUtc: '2026-10-08T14:30:00Z', resultUsd: '-3', estimatedR: null,
        reason: 'The plan in force at the first entry fill lacks a complete stop on the loss side of every entry.' }),
      closed(3, 'Missing exit', { state: 'executions', exitAtUtc: null,
        reason: 'Review leaves this Play out because its executions are incomplete or do not settle it.' }),
      closed(4, 'Before indexing', { state: 'not-indexed', reason: 'Not indexed yet.' }),
      play(5, { title: 'Cancelled idea', status: 'cancelled', endedAtUtc: '2026-10-09T00:00:00Z', outcome: outcome({ state: 'cancelled' }) }),
      play(6, { title: 'Still open', status: 'open', planRevision: 1 }),
    ]
    render(<Shell api={client(plays)} initial={{ ...initialPlaysList, view: 'all' }} />)
    const win = await screen.findByRole('button', { name: 'Open Scored win' })
    expect(win).toHaveTextContent(/Exit .*\+\$12\.5.*Est\. \+1\.25R/)
    expect(win).toHaveAccessibleDescription(expect.stringContaining('+$12.5'))
    const noR = screen.getByRole('button', { name: 'Open Scored without R' })
    expect(noR).toHaveTextContent(/-\$3.*Est\. R unknown/)
    expect(noR).toHaveAccessibleDescription(expect.stringContaining('lacks a complete stop'))
    const missing = screen.getByRole('button', { name: 'Open Missing exit' })
    expect(missing).toHaveTextContent(/Exit unknown.*Result unknown/)
    expect(missing).not.toHaveTextContent('$0')
    expect(missing).toHaveAccessibleDescription(expect.stringContaining('executions are incomplete'))
    expect(screen.getByRole('button', { name: 'Open Before indexing' })).toHaveTextContent('Result not indexed yet')
    expect(screen.getByRole('button', { name: 'Open Cancelled idea' })).toHaveTextContent('No result')
    expect(screen.getByRole('button', { name: 'Open Still open' })).not.toHaveTextContent(/Exit|Result/)
  })
})

describe('Journal search', () => {
  it('searches after typing pauses, shows the field and the marked excerpt in place of the list, and opens the Play', async () => {
    const found = closed(3, 'Wombat trade', { state: 'scored', exitAtUtc: '2026-10-08T14:30:00Z', resultUsd: '4', estimatedR: '2' })
    const searchJournal = vi.fn<WorkspaceApi['searchJournal']>().mockResolvedValue(page([hit(found)], { notIndexed: 2 }))
    const openPlay = vi.fn<WorkspaceApi['play']>(() => new Promise(() => {}))
    const states: PlaysListState[] = []
    render(<Shell api={client([play(1, { title: 'Listed draft' }), found], { searchJournal, play: openPlay })}
      initial={{ ...initialPlaysList, view: 'all' }} onList={state => states.push(state)} />)
    await screen.findByRole('button', { name: 'Open Listed draft' })
    const box = screen.getByRole('searchbox', { name: 'Search the journal' })
    await userEvent.type(box, 'w')
    await new Promise(resolve => setTimeout(resolve, 400))
    // One character is not a query: the list stays.
    expect(searchJournal).not.toHaveBeenCalled()
    await userEvent.type(box, 'ombat')
    const results = await screen.findByRole('region', { name: 'Journal search results' })
    await waitFor(() => expect(searchJournal).toHaveBeenLastCalledWith({ q: 'wombat' }, expect.any(AbortSignal)))
    expect(searchJournal).toHaveBeenCalledTimes(1)
    expect(await within(results).findByRole('status')).toHaveTextContent('1 play matches “wombat”')
    expect(within(results).getByRole('note')).toHaveTextContent('2 plays are not indexed yet, so search cannot find them.')
    expect(screen.queryByRole('tab', { name: /All/ })).not.toBeInTheDocument()
    const row = within(results).getByRole('button', { name: 'Open Wombat trade' })
    expect(row).toHaveAccessibleDescription(expect.stringContaining('Log entry: …moved the stop after the wombat candle…'))
    expect(within(row).getByText('wombat', { selector: 'mark' })).toBeInTheDocument()
    expect(row).toHaveTextContent(/Est\. \+2\.00R/)
    expect(states.at(-1)).toMatchObject({ view: 'all', journal: 'wombat', journalOffset: 0 })
    await userEvent.click(row)
    await waitFor(() => expect(openPlay).toHaveBeenCalledWith(found.id, expect.any(AbortSignal)))
  })

  it('pages fifty at a time, retries a failed read and returns to the unchanged list when cleared', async () => {
    const many = Array.from({ length: 50 }, (_, n) => hit(play(n + 10, { title: `Match ${n}` })))
    const searchJournal = vi.fn<WorkspaceApi['searchJournal']>()
      .mockResolvedValueOnce(page(many, { total: 51, nextOffset: 50 }))
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce(page([hit(play(99, { title: 'Last match' }))], { total: 51, offset: 50 }))
    const api = client([play(1, { title: 'Listed draft' })], { searchJournal })
    const states: PlaysListState[] = []
    render(<Shell api={api} initial={{ ...initialPlaysList, view: 'all', journal: 'wombat', journalOffset: 0 }} onList={state => states.push(state)} />)
    const pages = await screen.findByRole('navigation', { name: 'Journal search pages' })
    expect(pages).toHaveTextContent('1–50 of 51')
    await userEvent.click(within(pages).getByRole('button', { name: 'Next' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/searched|offline/)
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByRole('button', { name: 'Open Last match' })).toBeInTheDocument()
    expect(searchJournal.mock.calls.map(([query]) => query)).toEqual([{ q: 'wombat' }, { q: 'wombat', offset: 50 }, { q: 'wombat', offset: 50 }])
    expect(screen.getByRole('navigation', { name: 'Journal search pages' })).toHaveTextContent('51–51 of 51')
    await userEvent.click(screen.getByRole('button', { name: 'Clear search' }))
    expect(await screen.findByRole('button', { name: 'Open Listed draft' })).toBeInTheDocument()
    expect(screen.getByRole('searchbox', { name: 'Search the journal' })).toHaveValue('')
    expect(states.at(-1)).toMatchObject({ view: 'all', journal: '', journalOffset: 0 })
  })
})
