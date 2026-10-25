import { credentialsFixture } from '@/test/fixtures'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { createWorkspaceApi, isHistoryCoverage, type BrokerAccount, type HistoryCoverage as Coverage, type WorkspaceApi } from '@/api/workspace'
import { accountFixture, portfolioFixture } from '@/test/workspace-fixture'
import { HistoryCoverage, backfillPollMs } from './HistoryCoverage'
import { ManageAccountDialog } from './ManageDialogs'

const gap = { id: '00000000-0000-4000-8000-000000000001', fromUtc: '2026-10-20T10:00:00Z', toUtc: '2026-10-21T09:00:00Z',
  openedAtUtc: '2026-10-21T10:00:00Z', closedAtUtc: null }
const coverage: Coverage = {
  keepExecutionHistory: true, available: true, openGapCount: 2, recordCount: 3, notice: 'Each import records what it asked for.',
  openGaps: [gap, { ...gap, id: '00000000-0000-4000-8000-000000000002', fromUtc: null, toUtc: null }], closedGaps: [],
  recent: [
    { source: 'check', firstObservedAtUtc: '2026-10-21T10:00:00Z', lastObservedAtUtc: '2026-10-21T12:00:00Z', importCount: 13,
      requestedFromUtc: null, requestedToUtc: '2026-10-21T12:00:00Z', truncated: true, complete: false,
      coveredFromUtc: '2026-10-21T09:00:00Z', coveredToUtc: '2026-10-21T12:00:00Z', gapFromUtc: gap.fromUtc, gapToUtc: gap.toUtc },
    { source: 'refresh', firstObservedAtUtc: '2026-10-20T10:00:00Z', lastObservedAtUtc: '2026-10-20T10:00:00Z', importCount: 1,
      requestedFromUtc: null, requestedToUtc: '2026-10-20T10:00:00Z', truncated: null, complete: false,
      coveredFromUtc: null, coveredToUtc: null, gapFromUtc: null, gapToUtc: null },
  ],
}

describe('history coverage (Phase 2, item 6)', () => {
  it('reads only ledgers whose ranges hold together', () => {
    expect(isHistoryCoverage(coverage)).toBe(true)
    const [first, second] = coverage.recent as [Coverage['recent'][number], Coverage['recent'][number]]
    // Unknown truncation proves nothing; a gap has both ends; an open list holds open gaps only.
    expect(isHistoryCoverage({ ...coverage, recent: [{ ...second, coveredToUtc: second.lastObservedAtUtc }] })).toBe(false)
    expect(isHistoryCoverage({ ...coverage, recent: [{ ...first, gapToUtc: null }] })).toBe(true)
    expect(isHistoryCoverage({ ...coverage, recent: [{ ...first, gapFromUtc: null }] })).toBe(false)
    expect(isHistoryCoverage({ ...coverage, recent: [{ ...first, coveredFromUtc: first.coveredToUtc, coveredToUtc: first.gapFromUtc }] })).toBe(false)
    expect(isHistoryCoverage({ ...coverage, openGaps: [{ ...gap, closedAtUtc: '2026-10-22T10:00:00Z' }] })).toBe(false)
    expect(isHistoryCoverage({ ...coverage, openGapCount: 1 })).toBe(false)
  })

  it('shows the opt-in, the open gaps with unknown ends, and what each import covered', async () => {
    const historyCoverage = vi.fn().mockResolvedValue(coverage)
    render(<HistoryCoverage api={{ historyCoverage }} accountId="a" reloadKey="1" />)
    const panel = await screen.findByRole('region', { name: 'History coverage' })
    expect(panel).toHaveTextContent('Keeping execution history')
    const gaps = within(panel).getByRole('group', { name: 'Possible gaps' })
    expect(gaps).toHaveTextContent('2 possible gaps')
    expect(gaps).toHaveTextContent('start unknown – end unknown')
    const rows = within(panel).getAllByRole('row')
    expect(rows[1]).toHaveTextContent('Scheduled or owner check13')
    expect(rows[2]).toHaveTextContent('Refresh')
    expect(rows[2]).toHaveTextContent('Not known')
  })

  it('shows nothing for a venue whose history cannot be kept and that recorded nothing', async () => {
    const historyCoverage = vi.fn().mockResolvedValue({ ...coverage, keepExecutionHistory: false, available: false, openGapCount: 0,
      openGaps: [], recordCount: 0, recent: [] })
    render(<HistoryCoverage api={{ historyCoverage }} accountId="a" reloadKey="1" />)
    await waitFor(() => expect(historyCoverage).toHaveBeenCalled())
    expect(screen.queryByRole('region', { name: 'History coverage' })).toBeNull()
  })

  it('offers Keep execution history for a venue account and saves it with the settings', async () => {
    const venueAccount: BrokerAccount = { ...accountFixture, venueId: 'hyperliquid', address: '0x' + '1'.repeat(40),
      sourceId: '0x' + '1'.repeat(40), syncStatus: 'synced' }
    const updateAccount = vi.fn().mockResolvedValue({ ...venueAccount, keepExecutionHistory: true })
    const api: WorkspaceApi = { ...createWorkspaceApi(credentialsFixture), updateAccount }
    render(<ManageAccountDialog account={venueAccount} portfolios={[portfolioFixture]} api={api} onClose={vi.fn()} onChanged={vi.fn()} />)
    const keep = screen.getByRole('checkbox', { name: 'Keep execution history' })
    expect(keep).not.toBeChecked()
    expect(keep).toHaveAccessibleDescription(/after protection checks/)
    await userEvent.click(keep)
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(updateAccount).toHaveBeenCalledWith(venueAccount.id, expect.objectContaining({ keepExecutionHistory: true })))
  })

  it('offers Backfill on a gap the venue may still serve, starts it and shows its progress (Phase 2, item 6b)', async () => {
    const beyond = { ...gap, id: '00000000-0000-4000-8000-000000000003', beyondVenueReach: true }
    const queued = { id: '00000000-0000-4000-8000-000000000009', gapId: gap.id, state: 'queued' as const, fromUtc: gap.fromUtc!,
      toUtc: gap.toUtc!, cursorUtc: gap.fromUtc!, pages: 1, rawRows: 2, addedFills: 1, omittedExecutions: 0, error: null,
      createdAtUtc: '2026-10-22T10:00:00Z', finishedAtUtc: null }
    const offered: Coverage = { ...coverage, backfillAvailable: true, openGapCount: 2, openGaps: [gap, beyond], backfills: [] }
    expect(isHistoryCoverage(offered)).toBe(true)
    expect(isHistoryCoverage({ ...offered, backfills: [queued, { ...queued, id: '00000000-0000-4000-8000-00000000000a' }] })).toBe(false)
    const historyCoverage = vi.fn().mockResolvedValueOnce(offered).mockResolvedValue({ ...offered, backfills: [queued] })
    const startBackfill = vi.fn().mockResolvedValue(queued)
    render(<HistoryCoverage api={{ historyCoverage, startBackfill }} accountId="a" reloadKey="1" />)
    const panel = await screen.findByRole('region', { name: 'History coverage' })
    const gaps = within(panel).getByRole('group', { name: 'Possible gaps' })
    // Only the gap the venue may still serve offers it.
    const backfill = within(gaps).getByRole('button', { name: 'Backfill' })
    expect(backfill).toHaveAccessibleDescription(/never creates a Play or a notification/)
    expect(gaps).toHaveTextContent("beyond the venue's reach")
    await userEvent.click(backfill)
    expect(startBackfill).toHaveBeenCalledWith('a', gap.id)
    expect(await within(panel).findByRole('group', { name: 'Backfills' })).toHaveTextContent('Reading behind protection checks · 1 page, 1 execution added')
    // One backfill at a time: no other gap offers it while this one reads.
    expect(within(panel).queryByRole('button', { name: 'Backfill' })).toBeNull()
  })

  it('accepts a backfill record and a first page anchored before the gap, and keeps polling after a failed read (6b, Sol round 1)', async () => {
    const queued = { id: '00000000-0000-4000-8000-000000000009', gapId: gap.id, state: 'queued' as const, fromUtc: gap.fromUtc!,
      toUtc: '2026-10-22T10:00:00Z', cursorUtc: '2026-10-01T00:00:00Z', pages: 1, rawRows: 2, addedFills: 1, omittedExecutions: 0,
      error: null, createdAtUtc: '2026-10-22T10:00:00Z', finishedAtUtc: null }
    const record = { source: 'backfill' as const, firstObservedAtUtc: '2026-10-22T10:00:00Z', lastObservedAtUtc: '2026-10-22T10:00:00Z',
      importCount: 1, requestedFromUtc: gap.fromUtc, requestedToUtc: '2026-10-22T10:00:00Z', truncated: false, complete: false,
      coveredFromUtc: gap.fromUtc, coveredToUtc: '2026-10-22T10:00:00Z', gapFromUtc: null, gapToUtc: null }
    const reading: Coverage = { ...coverage, backfillAvailable: true, recordCount: 1, recent: [record], backfills: [queued] }
    expect(isHistoryCoverage(reading)).toBe(true)
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      const historyCoverage = vi.fn().mockResolvedValueOnce(reading).mockRejectedValueOnce(new Error('offline')).mockResolvedValue(reading)
      render(<HistoryCoverage api={{ historyCoverage }} accountId="a" reloadKey="1" />)
      const panel = await screen.findByRole('region', { name: 'History coverage' })
      expect(panel).toHaveTextContent('Backfill · from')
      await vi.advanceTimersByTimeAsync(backfillPollMs)
      await waitFor(() => expect(historyCoverage).toHaveBeenCalledTimes(2))
      // The failed read keeps what was shown and still reads again.
      await vi.advanceTimersByTimeAsync(backfillPollMs)
      await waitFor(() => expect(historyCoverage).toHaveBeenCalledTimes(3))
      expect(panel).toHaveTextContent('Reading behind protection checks')
    } finally { vi.useRealTimers() }
  })
})

