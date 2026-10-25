import { StrictMode } from 'react'
import { act, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { isPlaySummary, type PlayList, type PlayListQuery, type PlaySummary } from '@/api/plays'
import { createWorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { accountFixture, playCountsFrom, playsFrom, savedPlayFixture } from '@/test/workspace-fixture'
import { OverviewPlays } from './OverviewPlays'
import { credentialsFixture } from '@/test/identity-fixture'

const summary = (status: PlaySummary['status'], hasReview = false): PlaySummary => ({
  ...savedPlayFixture.summary, id: status, title: `${status} idea`, status, hasReview,
})
const plays = ['draft', 'planned', 'paused', 'open', 'closed', 'cancelled'].map(status => summary(status as PlaySummary['status']))
const api = (list: PlaySummary[] = plays, read = vi.fn(playsFrom(list))) =>
  ({ ...createWorkspaceApi(credentialsFixture), plays: read, playCounts: vi.fn(playCountsFrom(list)) })
const props = { accounts: [accountFixture], reloadGeneration: 0, onBrowse: vi.fn(), onOpen: vi.fn().mockResolvedValue(undefined) }

describe('Overview plays', () => {
  it('shows Drafts, In progress, Needs review and All with server counts', async () => {
    const reviewed = { ...summary('closed', true), id: 'reviewed', title: 'Reviewed idea', updatedAtUtc: '2026-10-06T12:00:00Z' }
    const client = api([...plays, reviewed])
    render(<OverviewPlays {...props} api={client} />)
    await screen.findByRole('button', { name: 'Open open idea' })
    expect(await screen.findByRole('tab', { name: 'In progress 3' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tab', { name: 'Drafts 1' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Needs review 2' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'All 7' })).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /^Open .* idea$/ })).toHaveLength(3)
    expect(client.plays).toHaveBeenLastCalledWith({ view: 'progress', scope: 'all', pageSize: 5 }, expect.any(AbortSignal))
    await userEvent.click(screen.getByRole('tab', { name: 'Drafts 1' }))
    expect(await screen.findByRole('button', { name: 'Open draft idea' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Needs review 2' }))
    expect(await screen.findByRole('button', { name: 'Open cancelled idea' })).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /^Open .* idea$/ })).toHaveLength(2)
    expect(screen.queryByRole('button', { name: 'Open Reviewed idea' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'All 7' }))
    expect(await screen.findByRole('button', { name: 'Open Reviewed idea' })).toBeInTheDocument()
    expect(screen.getAllByRole('listitem')).toHaveLength(5)
    expect(client.plays).toHaveBeenLastCalledWith({ view: 'all', scope: 'all', pageSize: 5 }, expect.any(AbortSignal))
  })
  it('asks for the Overview scope and opens Plays on the same tab', async () => {
    const browse = vi.fn()
    const client = api()
    const scope = `account:${accountFixture.id}`
    render(<OverviewPlays {...props} scope={scope} onBrowse={browse} api={client} />)
    await screen.findByRole('button', { name: 'Open open idea' })
    expect(client.playCounts).toHaveBeenCalledWith({ scope }, expect.any(AbortSignal))
    expect(client.plays).toHaveBeenCalledWith({ view: 'progress', scope, pageSize: 5 }, expect.any(AbortSignal))
    await userEvent.click(screen.getByRole('button', { name: 'View all' }))
    expect(browse).toHaveBeenLastCalledWith('progress')
    await userEvent.click(screen.getByRole('tab', { name: /Needs review/ }))
    await userEvent.click(screen.getByRole('button', { name: 'View all' }))
    expect(browse).toHaveBeenLastCalledWith('review')
    await userEvent.click(screen.getByRole('tab', { name: /^All/ }))
    await userEvent.click(screen.getByRole('button', { name: 'View all' }))
    expect(browse).toHaveBeenLastCalledWith('all')
  })
  it('shows at most five rows even when an unpaged view returns more', async () => {
    const many = Array.from({ length: 8 }, (_, n) => ({ ...summary('planned'), id: `planned-${n}`, title: `planned ${n}` }))
    const read = vi.fn((query: PlayListQuery): Promise<PlayList> => Promise.resolve({ plays: many, view: query.view, page: 1, pageSize: 200, totalCount: 8 }))
    render(<OverviewPlays {...props} api={api(many, read)} />)
    await screen.findByRole('button', { name: 'Open planned 0' })
    expect(screen.getAllByRole('listitem')).toHaveLength(5)
    expect(screen.getByRole('tab', { name: 'In progress 8' })).toBeInTheDocument()
  })
  it('labels venue instruments with the venue quote asset and keeps manual labels', async () => {
    const venue = { ...summary('open'), venueId: 'hyperliquid', instrumentSource: 'venue' as const }
    render(<OverviewPlays {...props} api={api([venue, summary('planned')])} />)
    const venueRow = await screen.findByRole('button', { name: 'Open open idea' })
    expect(venueRow.querySelector('.play-asset')).toHaveTextContent(/^BTC\/USDC$/)
    expect(venueRow.querySelector('.play-asset-quote')).toHaveTextContent('/USDC')
    const manualRow = screen.getByRole('button', { name: 'Open planned idea' })
    expect(manualRow.querySelector('.play-asset')).toHaveTextContent(/^BTC$/)
    expect(manualRow.querySelector('.play-asset-quote')).toBeNull()
  })
  it('separates the asset, the venue logo and the account, and describes the row with all three', async () => {
    const venue = { ...summary('open'), venueId: 'hyperliquid', instrumentSource: 'venue' as const }
    render(<OverviewPlays {...props} api={api([venue])} />)
    const row = await screen.findByRole('button', { name: 'Open open idea' })
    expect(within(row).getByRole('img', { name: 'Hyperliquid' })).toHaveAttribute('title', 'Hyperliquid')
    expect(within(row).getByText('Main account')).toBeInTheDocument()
    expect(within(row).queryByText('Hyperliquid')).not.toBeInTheDocument()
    expect(row).toHaveAccessibleDescription(/^BTC\/USDC.*Main account$/)
  })
  it('names an account that is no longer listed', async () => {
    const venue = { ...summary('open'), accountId: '99999999-9999-4999-8999-999999999999' }
    render(<OverviewPlays {...props} api={api([venue])} />)
    expect(within(await screen.findByRole('button', { name: 'Open open idea' })).getByText('Unknown account')).toBeInTheDocument()
  })
  it('shows rows in server order and supports keyboard tab navigation', async () => {
    const newer = { ...summary('open'), updatedAtUtc: '2026-10-05T12:00:00Z' }
    const older = { ...summary('planned'), updatedAtUtc: '2026-10-05T13:00:00+02:00' }
    render(<OverviewPlays {...props} api={api([older, newer])} />)
    await screen.findByRole('button', { name: 'Open open idea' })
    expect(within(screen.getAllByRole('listitem')[0]!).getByText('open idea')).toBeInTheDocument()
    ;(await screen.findByRole('tab', { name: 'In progress 2' })).focus()
    await userEvent.keyboard('{ArrowRight}')
    expect(screen.getByRole('tab', { name: 'Needs review 0' })).toHaveAttribute('aria-selected', 'true')
    expect(await screen.findByText('No plays waiting for review')).toBeInTheDocument()
  })
  it('uses a delayed skeleton, failure, retry and empty states without affecting accounts', async () => {
    let reject!: (error: Error) => void
    const read = vi.fn(playsFrom([])).mockImplementationOnce(() => new Promise((_, no) => { reject = no }))
    const browse = vi.fn()
    render(<OverviewPlays {...props} onBrowse={browse} api={api([], read)} />)
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
    expect(await screen.findByRole('status')).toHaveTextContent('Loading plays')
    expect(screen.queryByText('No plays in progress')).not.toBeInTheDocument()
    await act(async () => reject(new ApiError('unavailable', 'Play list unavailable.')))
    expect(await screen.findByRole('alert')).toHaveTextContent('Play list unavailable.')
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByText('No plays in progress')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Open Plays' }))
    expect(browse).toHaveBeenCalledExactlyOnceWith('progress')
    expect(read).toHaveBeenCalledTimes(2)
  })
  it('refreshes on Reload and ignores stale or aborted reads', async () => {
    let resolve!: (value: PlayList) => void
    const read = vi.fn(playsFrom([summary('open')])).mockImplementationOnce(() => new Promise(yes => { resolve = yes }))
    const client = api([summary('open')], read)
    const { rerender, unmount } = render(<OverviewPlays {...props} api={client} />)
    const oldSignal = read.mock.calls[0]![1] as AbortSignal
    rerender(<OverviewPlays {...props} reloadGeneration={1} api={client} />)
    expect(oldSignal.aborted).toBe(true)
    await screen.findByRole('button', { name: 'Open open idea' })
    resolve({ plays: [summary('paused')], view: 'progress', page: 1, pageSize: 5, totalCount: 1 })
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Open paused idea' })).not.toBeInTheDocument())
    unmount()
    expect((read.mock.calls[1]![1] as AbortSignal).aborted).toBe(true)
  })
  it('opens a play safely under StrictMode and reports opening failures', async () => {
    const open = vi.fn().mockRejectedValueOnce(new ApiError('http', 'Play is no longer available.', 404)).mockResolvedValue(undefined)
    render(<StrictMode><OverviewPlays {...props} onOpen={open} api={api()} /></StrictMode>)
    await userEvent.click(await screen.findByRole('button', { name: 'Open open idea' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Play is no longer available.')
    expect(open.mock.calls[0]![1].aborted).toBe(false)
    await userEvent.click(screen.getByRole('button', { name: 'Open open idea' }))
    expect(open).toHaveBeenLastCalledWith('open', expect.any(AbortSignal))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
  it('cancels an in-flight opening when the panel is unmounted', async () => {
    const open = vi.fn<(id: string, signal: AbortSignal) => Promise<void>>(() => new Promise(() => {}))
    const { unmount } = render(<OverviewPlays {...props} onOpen={open} api={api()} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Open open idea' }))
    const signal = open.mock.calls[0]![1] as AbortSignal
    expect(signal.aborted).toBe(false)
    expect(screen.getByRole('button', { name: 'Open planned idea' })).toBeDisabled()
    unmount()
    expect(signal.aborted).toBe(true)
  })
  it('requires a boolean review-presence field at the API boundary', () => {
    const valid = savedPlayFixture.summary
    expect(isPlaySummary(valid)).toBe(true)
    expect(isPlaySummary({ ...valid, hasReview: undefined })).toBe(false)
    expect(isPlaySummary({ ...valid, hasReview: 'false' })).toBe(false)
  })
})
