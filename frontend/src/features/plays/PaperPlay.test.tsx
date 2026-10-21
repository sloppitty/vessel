import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { useState } from 'react'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Tooltip } from 'radix-ui'
import { describe, expect, it, vi } from 'vitest'
import { VenuesProvider } from '@/api/venues'
import type { PaperPlayState } from '@/api/paper'
import type { SavedPlay } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { systemFixture } from '@/test/system-fixture'
import { accountFixture, instrumentCatalogFixture, playApiStubs, portfolioFixture, savedPlayFixture, sizingFixture } from '@/test/workspace-fixture'
import { draftFromSaved, savedState, type PlaysSession } from './saved'
import { PlaysPage } from './PlaysPage'
import { paperEntryFills } from './entry-fills'
import { exactFixed } from '@/lib/decimal'

const account = { ...accountFixture, tradingMode: 'paper' as const, syncStatus: 'paper' as const, venueId: 'hyperliquid', balanceUsd: '4000', availableBalanceUsd: '4000', availableBalanceSource: 'paper' as const }
const venues = systemFixture.venues.map(v => v.id === 'hyperliquid' ? { ...v, capabilities: { ...v.capabilities, paperExecution: true } } : v)
const play: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, tradingMode: 'paper', venueId: 'hyperliquid', instrumentSource: 'venue', status: 'planned' },
  plan: { ...savedPlayFixture.plan, size: '10', entries: [{ ...savedPlayFixture.plan.entries[0]!, price: '100' }] } }
const paperState: PaperPlayState = { playId: play.summary.id, status: 'ambiguous', checkedAtUtc: '2026-10-20T12:01:00Z', nextCheckAtUtc: null,
  declinedEntries: [], suspensions: [], hasUnscoredHistory: true,
  enteredQuantity: '1', exitedQuantity: '0', openQuantity: '1', realizedPnlUsd: '0', issues: ['Opposing exit touches; order unknown.'], executions: [
    { id: '12345678-1111-2222-3333-444444444444', entryId: 'entry-1', levelId: null, role: 'entry', price: '100', quantity: '1', pnlUsd: '0',
      occurredAtUtc: '2026-10-20T12:01:00Z', candleOpenAtUtc: '2026-10-20T12:00:00Z', candleCloseAtUtc: '2026-10-20T12:01:00Z' },
  ] }
function makeApi(enabled = true) {
  return { ...playApiStubs(), tradingMode: 'paper', paperSettings: vi.fn().mockResolvedValue({ enabled, checkIntervalMinutes: 60 }),
    paperState: vi.fn().mockResolvedValue(paperState), instruments: vi.fn().mockResolvedValue(instrumentCatalogFixture),
    sizing: vi.fn().mockResolvedValue({ ...sizingFixture, tradingMode: 'paper', record: { ...sizingFixture.record, source: 'paper' } }),
    candles: vi.fn().mockImplementation(() => new Promise(() => {})) } as unknown as WorkspaceApi
}
function Workspace({ api, initial = play }: { api: WorkspaceApi; initial?: SavedPlay }) {
  const [session, onSession] = useState<PlaysSession>(() => ({ view: 'editor', saved: savedState(initial, draftFromSaved(initial, [])), draft: draftFromSaved(initial, []) }))
  return <Tooltip.Provider><NotificationProvider><VenuesProvider venues={venues}><PlaysPage api={api} accounts={[account]} portfolios={[portfolioFixture]} session={session} onSession={onSession} /></VenuesProvider></NotificationProvider></Tooltip.Provider>
}
describe('Paper Play integration in the existing editor', () => {
  it('confirms paper pause with a skipped-window disclosure before any lifecycle write', async () => {
    const user = userEvent.setup()
    const api = makeApi()
    vi.mocked(api.changePlayStatus).mockResolvedValue({ ...play, summary: { ...play.summary, status: 'paused', version: 2 } })
    render(<Workspace api={api} />)
    await user.click(screen.getByRole('button', { name: 'Pause' }))
    expect(api.changePlayStatus).not.toHaveBeenCalled()
    const dialog = screen.getByRole('dialog', { name: 'Pause paper play?' })
    expect(dialog).toHaveTextContent('Unevaluated time before this action is skipped, not replayed.')
    expect(dialog).toHaveTextContent('A retained receipt keeps this Play unscored, even after resuming.')
    await user.click(within(dialog).getByRole('button', { name: 'Keep running' }))
    expect(api.changePlayStatus).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Pause' }))
    await user.click(within(screen.getByRole('dialog', { name: 'Pause paper play?' })).getByRole('button', { name: 'Pause paper play' }))
    expect(api.changePlayStatus).toHaveBeenCalledExactlyOnceWith(play.summary.id, play.summary.version, { status: 'paused' })
    expect(await screen.findByRole('button', { name: 'Resume' })).toBeInTheDocument()
  })

  it('discloses unchecked pre-cancel time without referring to actual venue orders', async () => {
    const user = userEvent.setup()
    const api = makeApi()
    render(<Workspace api={api} />)
    await user.click(screen.getByRole('button', { name: 'Cancel play' }))
    const dialog = screen.getByRole('dialog', { name: 'Cancel this play?' })
    expect(dialog).toHaveTextContent('Unevaluated time before this action is skipped, not replayed.')
    expect(dialog).toHaveTextContent('A retained receipt keeps this Play unscored')
    expect(dialog).not.toHaveTextContent('Remove resting venue orders yourself')
    await user.click(within(dialog).getByRole('button', { name: 'Keep the play' }))
    expect(api.changePlayStatus).not.toHaveBeenCalled()
  })

  it('observes paper facts even before opening Execution and never reads actual/manual executions or monitor health', async () => {
    const api = makeApi()
    render(<Workspace api={api} />)
    await waitFor(() => expect(api.paperState).toHaveBeenCalledWith(play.summary.id, expect.any(AbortSignal)))
    expect(api.playExecution).not.toHaveBeenCalled()
    expect(api.manualExecutions).not.toHaveBeenCalled()
    expect(api.accountMonitor).not.toHaveBeenCalled()
    expect(api.runAccountMonitor).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('tab', { name: 'Execution' }))
    expect(await screen.findByText('Ambiguous candle')).toBeVisible()
    expect(screen.getByText(/No intrabar winner is assumed/)).toBeVisible()
    expect(screen.queryByRole('button', { name: /Link order|Add actual|Check now/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Trade|Hyperliquid/ })).not.toBeInTheDocument()
  })
  it('disables arming when settings are disabled and offers no manual instrument fallback or risk mutation', async () => {
    const api = makeApi(false)
    const draft: SavedPlay = { ...play, summary: { ...play.summary, status: 'draft' } }
    render(<Workspace api={api} initial={draft} />)
    await waitFor(() => expect(api.paperSettings).toHaveBeenCalled())
    expect(screen.getByRole('button', { name: 'Plan it' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Enter manually' })).not.toBeInTheDocument()
    await userEvent.click(await screen.findByRole('button', { name: /^Suggestions/ }))
    expect(await screen.findByText(/shared preference, read-only in Paper/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Risk per trade.*Edit/ })).not.toBeInTheDocument()
    expect(api.updateSizingSettings).not.toHaveBeenCalled()
    expect(screen.getByText('Paper cash balance')).toBeInTheDocument()
    expect(screen.getByText('Virtual equity')).toBeInTheDocument()
  })
  it('uses exact averages from typed simulated entries without fabricated venue-fill DTOs', () => {
    const facts = { ...paperState, executions: [...paperState.executions, { ...paperState.executions[0]!, id: '12345678-1111-2222-3333-444444444445',
      price: '102', quantity: '3' }, { ...paperState.executions[0]!, id: '12345678-1111-2222-3333-444444444446', role: 'target' as const,
      price: '99999', quantity: '1', pnlUsd: '5' }] }
    const fills = paperEntryFills(facts)
    expect([...fills.filled]).toEqual(['entry-1'])
    expect(exactFixed(fills.averages.get('entry-1')!, 1)).toBe('101.5')
  })
})
