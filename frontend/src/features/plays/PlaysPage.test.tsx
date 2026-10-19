import { monitorFixture } from '@/test/monitor-fixture'
import { useEffect, useState, type Dispatch, type SetStateAction } from 'react'
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { pickInstrument } from '@/test/instrument'
import { createWorkspaceApi, type BrokerAccount, type WorkspaceApi } from '@/api/workspace'
import type { PlayExecution, PlayFields, PlayHistory, PlayListQuery, PlayStatus, PlaySummary, SavedEvidence, SavedPlay, StatusRequest } from '@/api/plays'
import { ApiError } from '@/api/errors'
import { accountFixture, idleMarketStream, instrumentCatalogFixture, playCountsFrom, playsFrom, portfolioFixture, savedPlayFixture, sizingFixture } from '@/test/workspace-fixture'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { PlaysPage } from './PlaysPage'
import { createDraft } from './draft'
import { plainDecimalRule } from './numbers'
import { createEvidence } from './evidence'
import type { ImageMarkup } from '@/api/markup'
import { createPlaysSession, describePlanChanges, draftFromSaved, planFromDraft, savedState, syncEvidence, type PlaysSession } from './saved'
import { accountPicker, chooseInPicker } from '@/test/account-picker'

const ids = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`

/** An in-memory stand-in for the saved-play API with the server's version and revision rules. */
function fakeServer() {
  let next = 1
  const plays = new Map<string, SavedPlay>()
  const history = new Map<string, PlayHistory>()
  const evidence: SavedEvidence[] = []
  const now = () => '2026-10-03T10:00:00.000Z'
  const bump = (play: SavedPlay, changes: Partial<PlaySummary> = {}) =>
    ({ ...play, summary: { ...play.summary, ...changes, version: play.summary.version + 1, updatedAtUtc: now() } })
  const find = (id: string) => {
    const play = plays.get(id)
    if (!play) throw new ApiError('http', 'This play or image is no longer available.', 404)
    return play
  }
  const execution = (play: SavedPlay): PlayExecution => ({
    playId: play.summary.id, status: play.summary.status, tracked: play.summary.instrumentSource === 'venue', checkedAtUtc: null,
    reason: play.summary.instrumentSource === 'venue' ? null : 'Manual instruments are not tracked at a venue.',
    totals: { enteredQuantity: '0', exitedQuantity: '0', openQuantity: '0', closedPnlUsd: '0', closedPnlBasis: 'gross', fees: [] },
    entries: [], links: [], suggestions: [], unlinkedOrders: [], notice: 'Venue facts.', issues: [], monitor: play.summary.instrumentSource === 'venue' ? monitorFixture : null, discrepancies: [], watches: [],
  })
  const api = {
    plays: vi.fn((query: PlayListQuery) => playsFrom([...plays.values()].map(play => play.summary))(query)),
    playCounts: vi.fn((query: { q?: string }) => playCountsFrom([...plays.values()].map(play => play.summary))(query)),
    play: vi.fn((id: string) => Promise.resolve(find(id))),
    createPlay: vi.fn((fields: PlayFields) => {
      const id = ids(next++)
      const play: SavedPlay = {
        summary: { id, title: fields.title.trim(), status: 'draft', accountId: fields.accountId, venueId: 'manual', instrument: fields.instrument,
          instrumentSource: fields.instrumentSource, direction: fields.plan.direction, planRevision: 0, version: 1, cancelReason: null,
          createdAtUtc: now(), updatedAtUtc: now(), plannedAtUtc: null, endedAtUtc: null, hasReview: !!fields.review.trim() },
        plan: fields.plan, drawings: fields.drawings, review: fields.review,
      }
      plays.set(id, play)
      history.set(id, { revisions: [], statusChanges: [] })
      return Promise.resolve(play)
    }),
    updatePlay: vi.fn((id: string, fields: PlayFields & { expectedVersion: number; revisionReason?: string }) => {
      const play = find(id)
      if (play.summary.version !== fields.expectedVersion) return Promise.reject(new ApiError('http', 'This Play changed elsewhere.', 409))
      const planChanged = JSON.stringify(play.plan) !== JSON.stringify(fields.plan)
      let revision = play.summary.planRevision
      if (planChanged && play.summary.status !== 'draft') {
        if (!fields.revisionReason) return Promise.reject(new ApiError('http', 'Say why the plan changed.', 409))
        revision += 1
        history.get(id)!.revisions.push({ number: revision, status: play.summary.status, reason: fields.revisionReason, createdAtUtc: now(), plan: fields.plan })
      }
      const updated = { ...bump(play, { title: fields.title.trim(), instrument: fields.instrument, planRevision: revision, hasReview: !!fields.review.trim() }),
        plan: fields.plan, drawings: fields.drawings, review: fields.review }
      plays.set(id, updated)
      return Promise.resolve(updated)
    }),
    changePlayStatus: vi.fn((id: string, version: number, request: StatusRequest) => {
      const play = find(id)
      if (play.summary.version !== version) return Promise.reject(new ApiError('http', 'This Play changed elsewhere.', 409))
      const to: PlayStatus = request.status
      const record = history.get(id)!
      record.statusChanges.push({ from: play.summary.status, to, reason: request.status === 'cancelled' ? request.reason : null,
        note: request.status === 'cancelled' ? request.note ?? null : null, occurredAtUtc: now() })
      const planned = play.summary.status === 'draft' && to === 'planned'
      if (planned) record.revisions.push({ number: 1, status: 'planned', reason: 'Planned', createdAtUtc: now(), plan: play.plan })
      const updated = bump(play, { status: to, ...(planned ? { planRevision: 1, plannedAtUtc: now() } : {}),
        ...(request.status === 'cancelled' ? { cancelReason: request.reason, endedAtUtc: now() } : {}) })
      plays.set(id, updated)
      return Promise.resolve(updated)
    }),
    playHistory: vi.fn((id: string) => Promise.resolve(history.get(id)!)),
    deletePlay: vi.fn((id: string) => { plays.delete(id); return Promise.resolve() }),
    evidence: vi.fn((playId: string) => Promise.resolve(evidence.filter(item => item.playId === playId))),
    evidenceImage: vi.fn(() => Promise.resolve(new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' }))),
    uploadEvidence: vi.fn((playId: string, _image: Blob, fields: { source: 'capture' | 'upload'; note: string }) => {
      const item: SavedEvidence = { id: ids(1000 + next++), playId, source: fields.source, contentType: 'image/png', sizeBytes: 4,
        sha256: 'a'.repeat(64), note: fields.note, createdAtUtc: now(), updatedAtUtc: now(), markup: null }
      evidence.push(item)
      return Promise.resolve(item)
    }),
    updateEvidenceNote: vi.fn((id: string, note: string) => {
      const item = evidence.find(other => other.id === id)!
      item.note = note
      return Promise.resolve({ ...item })
    }),
    updateEvidenceMarkup: vi.fn(),
    playExecution: vi.fn((id: string) => Promise.resolve(execution(find(id)))),
    runAccountMonitor: vi.fn(() => Promise.resolve({ requestId: ids(5000), monitor: monitorFixture })),
    accountMonitor: vi.fn(() => Promise.resolve(monitorFixture)),
    deleteEvidence: vi.fn((id: string) => { evidence.splice(evidence.findIndex(item => item.id === id), 1); return Promise.resolve() }),
  }
  return { api, plays, history, evidence, execution }
}

function Page({ api, accounts = [accountFixture], initial = createPlaysSession(), onControl, onManageAccount }: { api: WorkspaceApi; accounts?: BrokerAccount[];
  initial?: PlaysSession; onControl?: (set: Dispatch<SetStateAction<PlaysSession>>) => void; onManageAccount?: (id: string) => void }) {
  const [session, setSession] = useState(initial)
  useEffect(() => { onControl?.(setSession) }, [onControl])
  return <NotificationProvider><PlaysPage accounts={accounts} portfolios={[portfolioFixture]} api={api} session={session} onSession={setSession} onManageAccount={onManageAccount} /></NotificationProvider>
}

const client = (server: ReturnType<typeof fakeServer>, overrides: Partial<WorkspaceApi> = {}) =>
  ({ ...createWorkspaceApi('test-only'), ...server.api, marketStream: idleMarketStream, ...overrides }) as unknown as WorkspaceApi

async function newPlannedPlay(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'New play' }))
  await user.type(screen.getByRole('textbox', { name: 'Play title' }), '  Range reclaim')
  await chooseInPicker('Account', accountFixture.name, user)
  expect(screen.getByRole('button', { name: 'Plan it' })).toBeDisabled()
  await user.type(screen.getByRole('textbox', { name: 'Perpetual instrument' }), 'BTC-PERP')
  await user.type(screen.getByRole('spinbutton', { name: 'Entry 1 planned entry price (quote units)' }), '100')
  await user.type(screen.getByRole('spinbutton', { name: 'Entry 1 planned stop price (quote units)' }), '95')
  await user.click(screen.getByRole('button', { name: 'Plan it' }))
  await waitFor(() => expect(within(screen.getByRole('region', { name: 'Play draft workspace' })).getByText('Planned', { selector: '.badge' })).toBeInTheDocument())
}

describe('saved plays', () => {
  it('saves a new draft, plans it and fixes the account and instrument', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await screen.findByText(/No saved plays yet/)
    await newPlannedPlay(user)
    expect(server.api.createPlay).toHaveBeenCalledOnce()
    expect(server.api.createPlay.mock.calls[0]![0]).toMatchObject({ accountId: accountFixture.id, instrument: 'BTC-PERP', instrumentSource: 'manual' })
    expect(server.api.changePlayStatus).toHaveBeenCalledWith(ids(1), 1, { status: 'planned' })
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Range reclaim')
    expect(accountPicker('Account')).toBeDisabled()
    expect(screen.getByRole('textbox', { name: 'Perpetual instrument' })).toHaveAttribute('readonly')
    expect(screen.getByRole('button', { name: /Saved/ })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
  })

  it('asks why a planned plan changed and keeps each revision', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await newPlannedPlay(user)
    const stop = screen.getByRole('spinbutton', { name: 'Entry 1 planned stop price (quote units)' })
    await user.clear(stop)
    await user.type(stop, '97')
    expect(screen.getByRole('button', { name: 'Pause' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    const dialog = screen.getByRole('dialog', { name: 'Why did the plan change?' })
    expect(within(dialog).getByRole('button', { name: 'Save revision' })).toBeDisabled()
    await user.type(within(dialog).getByRole('textbox', { name: 'Reason' }), 'Stop under the new swing low')
    await user.click(within(dialog).getByRole('button', { name: 'Save revision' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(server.api.updatePlay.mock.lastCall![1]).toMatchObject({ revisionReason: 'Stop under the new swing low', expectedVersion: 2 })
    // The revision is a pill beside the status, not a banner.
    expect(screen.getByText('Revision 2', { selector: '.badge' })).toHaveAttribute('title', 'Saved plan changes ask for a reason')

    // A title change alone is saved without a reason.
    await user.type(screen.getByRole('textbox', { name: 'Play title' }), ' v2')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await waitFor(() => expect(server.api.updatePlay).toHaveBeenCalledTimes(2))
    expect(server.api.updatePlay.mock.lastCall![1]).not.toHaveProperty('revisionReason')

    await user.click(screen.getByRole('button', { name: 'History' }))
    const history = await screen.findByRole('dialog', { name: 'Play history' })
    expect(await within(history).findByText('Plan revision 2 · Stop under the new swing low')).toBeInTheDocument()
    expect(within(history).getByText('Entry 1 stop: 95 → 97')).toBeInTheDocument()
    expect(within(history).getByText('Draft → Planned')).toBeInTheDocument()
  })

  it('pauses, resumes and cancels with a reason, leaving only the review editable', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await newPlannedPlay(user)
    await user.click(screen.getByRole('button', { name: 'Pause' }))
    await user.click(await screen.findByRole('button', { name: 'Resume' }))
    await user.click(await screen.findByRole('button', { name: 'Cancel play' }))
    const dialog = screen.getByRole('dialog', { name: 'Cancel this play?' })
    await user.selectOptions(within(dialog).getByRole('combobox', { name: 'Reason' }), 'missed')
    await user.type(within(dialog).getByRole('textbox', { name: /Note/ }), 'Price ran away')
    await user.click(within(dialog).getByRole('button', { name: 'Cancel play' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(server.api.changePlayStatus.mock.lastCall![2]).toEqual({ status: 'cancelled', reason: 'missed', note: 'Price ran away' })
    expect(within(screen.getByRole('region', { name: 'Play draft workspace' })).getByText('Cancelled', { selector: '.badge' })).toBeInTheDocument()
    expect(screen.getByRole('spinbutton', { name: 'Entry 1 planned entry price (quote units)' })).toBeDisabled()
    expect(screen.getByRole('textbox', { name: 'Play title' })).toBeDisabled()
    await user.click(screen.getByRole('tab', { name: 'Thesis' }))
    expect(screen.getByRole('textbox', { name: 'Thesis' })).toHaveAttribute('readonly')
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByRole('textbox', { name: 'Review' }), 'Waited too long.')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await waitFor(() => expect(server.api.updatePlay.mock.lastCall![1]).toMatchObject({ review: 'Waited too long.' }))
  })

  it('lists saved plays, keeps unsaved edits through navigation and blocks opening another play until resolved', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', accountFixture.name, user)
    await user.type(screen.getByRole('textbox', { name: 'Play title' }), 'First')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await waitFor(() => expect(server.api.createPlay).toHaveBeenCalledOnce())
    await user.type(screen.getByRole('textbox', { name: 'Play title' }), ' edited')
    await user.click(screen.getByRole('button', { name: 'All plays' }))

    expect(await screen.findByText(/Unsaved changes in/)).toHaveTextContent('First edited')
    await user.click(screen.getByRole('tab', { name: /^Drafts/ }))
    const open = await screen.findByRole('button', { name: 'Open First' })
    expect(open).toBeDisabled()
    expect(screen.getByRole('button', { name: 'New play' })).toBeDisabled()
    expect(within(open).getByText('Draft')).toBeInTheDocument()
    await user.click(screen.getByRole('tab', { name: /^Closed/ }))
    expect(await screen.findByText('No plays in this view.')).toBeInTheDocument()
    await user.click(screen.getByRole('tab', { name: /^Drafts/ }))

    await user.click(screen.getByRole('button', { name: 'Continue editing' }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('First edited')
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    await user.click(await screen.findByRole('button', { name: 'Discard changes' }))
    await user.click(await screen.findByRole('button', { name: 'Open First' }))
    expect(await screen.findByRole('textbox', { name: 'Play title' })).toHaveValue('First')
    expect(screen.getByRole('button', { name: /Saved/ })).toBeDisabled()
  })

  it('shows each saved play with a ticker chip, the venue logo and the account, described together for assistive technology', async () => {
    const server = fakeServer()
    const account = { ...accountFixture, venueId: 'hyperliquid' }
    const venuePlay: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, status: 'planned', title: 'Reclaim', venueId: 'hyperliquid', instrument: 'ETH', instrumentSource: 'venue' } }
    const manualPlay: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, status: 'planned', id: ids(77), title: 'Manual idea', venueId: 'manual', instrument: 'GDX', instrumentSource: 'manual' } }
    const orphan: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, status: 'planned', id: ids(78), title: 'Orphan', accountId: ids(99), instrument: '' } }
    for (const play of [venuePlay, manualPlay, orphan]) server.plays.set(play.summary.id, play)
    render(<Page api={client(server)} accounts={[account]} />)
    const row = await screen.findByRole('button', { name: 'Open Reclaim' })
    expect(row.querySelector('.play-asset-base')).toHaveTextContent('ETH')
    expect(row.querySelector('.play-asset-quote')).toHaveTextContent('/USDC')
    expect(within(row).getByRole('img', { name: 'Hyperliquid' })).toHaveAttribute('title', 'Hyperliquid')
    expect(within(row).getByText('Main account')).toBeInTheDocument()
    expect(within(row).queryByText('Hyperliquid')).not.toBeInTheDocument()
    expect(row).toHaveAccessibleDescription(/^ETH\/USDC.*Main account$/)
    const manual = screen.getByRole('button', { name: 'Open Manual idea' })
    expect(manual.querySelector('.play-asset')).toHaveTextContent(/^GDX$/)
    expect(within(manual).getByRole('img', { name: 'Manual' })).toBeInTheDocument()
    const unknown = screen.getByRole('button', { name: 'Open Orphan' })
    expect(within(unknown).getByText('No instrument')).toBeInTheDocument()
    expect(within(unknown).getByText('Unknown account')).toBeInTheDocument()
  })

  it('deletes only drafts', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', accountFixture.name, user)
    // Delete keeps its place but stays disabled until the draft is saved.
    expect(screen.getByRole('button', { name: 'Delete' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Delete' })).toBeEnabled())
    await user.click(screen.getByRole('button', { name: 'Delete' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete draft' }))
    await screen.findByText(/No saved plays yet/)
    expect(server.api.deletePlay).toHaveBeenCalledWith(ids(1))
  })

  it('shows save failures without losing the draft', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server, { createPlay: vi.fn().mockRejectedValue(new ApiError('http', 'Enable the account before using it for a Play.', 409)) })} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', accountFixture.name, user)
    await user.type(screen.getByRole('textbox', { name: 'Play title' }), 'Kept')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enable the account before using it for a Play.')
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Kept')
  })

  it('blocks saving stops and targets on the wrong side and offers the corrections', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', accountFixture.name, user)
    await user.type(screen.getByRole('spinbutton', { name: 'Entry 1 planned entry price (quote units)' }), '100')
    const stop = screen.getByRole('spinbutton', { name: 'Entry 1 planned stop price (quote units)' })
    await user.type(stop, '105')
    await user.type(screen.getByRole('spinbutton', { name: 'Entry 1 planned target 1 price (quote units)' }), '90')
    expect(stop).toHaveAttribute('aria-invalid', 'true')
    const notices = within(screen.getByRole('region', { name: 'Plan notifications' }))
    expect(notices.getByText('Entry 1 stop is above the entry price; a long stop goes below it.')).toBeInTheDocument()
    expect(notices.getByText('Entry 1 target 1 is below the entry price; a long target goes above it.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(server.api.createPlay).not.toHaveBeenCalled()
    expect(within(screen.getByRole('region', { name: 'Notifications' })).getByRole('alert')).toHaveTextContent(/Not saved.*Fix the plan first\. 2 stops and targets/)

    await user.click(notices.getByRole('button', { name: 'Switch to Short' }))
    expect(screen.getByRole('button', { name: /Short/, pressed: true })).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Plan notifications' })).toBeNull()
    expect(screen.queryByText(/Not saved/)).toBeNull()
    await user.click(screen.getByRole('button', { name: /Long/ }))
    await user.click(screen.getByRole('button', { name: 'Swap stops and targets' }))
    expect(screen.getByRole('spinbutton', { name: 'Entry 1 planned stop price (quote units)' })).toHaveValue(90)
    expect(screen.getByRole('spinbutton', { name: 'Entry 1 planned target 1 price (quote units)' })).toHaveValue(105)
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await waitFor(() => expect(server.api.createPlay).toHaveBeenCalledOnce())
  })

  it('refuses to save plan numbers the server would refuse, and says why', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', accountFixture.name, user)
    const price = screen.getByRole('spinbutton', { name: 'Entry 1 planned entry price (quote units)' })
    fireEvent.change(price, { target: { value: '1e2' } })
    expect(price).toHaveAttribute('aria-invalid', 'true')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(server.api.createPlay).not.toHaveBeenCalled()
    expect(within(screen.getByRole('region', { name: 'Notifications' })).getByRole('alert'))
      .toHaveTextContent(`Fix the plan first. Entry 1 price: ${plainDecimalRule}`)

    await user.click(within(screen.getByRole('group', { name: 'Entry 1 stop units' })).getByRole('button', { name: '% return at leverage' }))
    fireEvent.change(price, { target: { value: '100' } })
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Entry 1 planned stop return at 1× leverage (%)' }), { target: { value: '-5' } })
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(server.api.createPlay).not.toHaveBeenCalled()
    expect(within(screen.getByRole('region', { name: 'Notifications' })).getByRole('alert'))
      .toHaveTextContent('Entry 1 stop 1: use a price for a stop beyond the entry; a percentage cannot be negative.')

    // 0% is a break-even stop and saves.
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Entry 1 planned stop return at 1× leverage (%)' }), { target: { value: '0' } })
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await waitFor(() => expect(server.api.createPlay).toHaveBeenCalledOnce())
    expect(server.api.createPlay.mock.lastCall![0].plan.entries[0]).toMatchObject({ price: '100', stops: [{ unit: 'percent', value: '0' }] })
  })

  it('saves a stored plan whose numbers predate the plain-decimal rule without rewriting them', async () => {
    const server = fakeServer()
    const entry = { ...savedPlayFixture.plan.entries[0]!, price: '1e2', stops: [{ id: 'stop-1', unit: 'percent' as const, value: '-5', share: '100' }] }
    const legacy: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, title: 'Older plan' },
      plan: { ...savedPlayFixture.plan, size: '.5', entries: [entry] } }
    server.plays.set(legacy.summary.id, legacy)
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await user.click(await screen.findByRole('tab', { name: /^Drafts/ }))
    await user.click(await screen.findByRole('button', { name: 'Open Older plan' }))
    const stop = await screen.findByRole('spinbutton', { name: 'Entry 1 planned stop return at 1× leverage (%)' })
    // Kept text is flagged as a warning, not an error: it saves unchanged, but nothing here estimates from it.
    expect(stop).toHaveAttribute('data-warning', 'true')
    expect(stop).not.toHaveAttribute('aria-invalid')
    expect(stop.getAttribute('title')).toMatch(/^Saved earlier as “-5”/)
    await user.type(screen.getByRole('textbox', { name: 'Play title' }), ' again')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await waitFor(() => expect(server.api.updatePlay).toHaveBeenCalledOnce())
    expect(server.api.updatePlay.mock.lastCall![1].plan).toEqual(legacy.plan)

    // Another invalid value in a kept field is new text, and is refused.
    fireEvent.change(stop, { target: { value: '-6' } })
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(server.api.updatePlay).toHaveBeenCalledOnce()
  })

  it('keeps notes, marks and removals made while a save is pending, and saves them next', async () => {
    const server = fakeServer()
    let release!: () => void
    const held = new Promise<void>(resolve => { release = resolve })
    // The first upload's response arrives late, as on a slow connection.
    const uploadEvidence = vi.fn(async (...args: Parameters<typeof server.api.uploadEvidence>) => {
      const item = await server.api.uploadEvidence(...args)
      await held
      return item
    })
    const updateEvidenceMarkup = vi.fn((id: string, markup: ImageMarkup | null) => {
      const item = server.evidence.find(other => other.id === id)!
      item.markup = markup
      return Promise.resolve({ ...item })
    })
    const image = new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' })
    const draft = { ...createDraft(), title: 'Evidence', accountId: accountFixture.id,
      evidence: [{ ...createEvidence('capture', image, 'a.png'), note: 'Before' }, createEvidence('upload', image, 'b.png')] }
    let setSession!: Dispatch<SetStateAction<PlaysSession>>
    const user = userEvent.setup()
    render(<Page api={client(server, { uploadEvidence, updateEvidenceMarkup })} initial={{ view: 'editor', draft, saved: null }}
      onControl={set => { setSession = set }} />)
    await user.click(screen.getByRole('tab', { name: 'Evidence' }))
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await waitFor(() => expect(uploadEvidence).toHaveBeenCalledOnce())
    expect(screen.getByRole('button', { name: 'Saving…' })).toBeDisabled()

    const note = screen.getByRole('textbox', { name: 'Note for image 1' })
    await user.clear(note)
    await user.type(note, 'During save')
    const markup: ImageMarkup = { width: 100, height: 50, shapes: [{ id: 'b', kind: 'box', color: '#ff5c5c', width: 2, from: { x: 1, y: 1 }, to: { x: 50, y: 25 } }] }
    act(() => setSession(session => ({ ...session, draft: { ...session.draft,
      evidence: session.draft.evidence.map((item, index) => index === 0 ? { ...item, markup } : item) } })))
    await user.click(screen.getByRole('button', { name: 'Remove image 2' }))
    await user.click(within(screen.getByRole('group', { name: 'Remove image 2?' })).getByRole('button', { name: 'Remove' }))

    release()
    const save = await screen.findByRole('button', { name: /^Save$/ })
    expect(save).toBeEnabled()
    expect(screen.getByRole('textbox', { name: 'Note for image 1' })).toHaveValue('During save')
    expect(within(screen.getByRole('list', { name: 'Evidence images' })).getAllByRole('listitem')).toHaveLength(1)
    // The removed image finished uploading with the save; it is now a saved image to delete.
    expect(uploadEvidence).toHaveBeenCalledTimes(2)
    const [first, second] = [...server.evidence]
    expect(first!.note).toBe('Before')

    await user.click(save)
    await waitFor(() => expect(screen.getByRole('button', { name: /Saved/ })).toBeDisabled())
    expect(server.api.updateEvidenceNote).toHaveBeenCalledWith(first!.id, 'During save')
    expect(updateEvidenceMarkup).toHaveBeenCalledWith(first!.id, markup)
    expect(server.api.deleteEvidence).toHaveBeenCalledWith(second!.id)
    expect(uploadEvidence).toHaveBeenCalledTimes(2)
    expect(server.evidence).toEqual([expect.objectContaining({ id: first!.id, note: 'During save', markup })])
  })

  it('links a venue instrument to its trading page on the venue', async () => {
    const venue = { ...accountFixture, venueId: 'hyperliquid', address: `0x${'a'.repeat(40)}` }
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server, { instruments: () => Promise.resolve(instrumentCatalogFixture), candles: () => new Promise(() => {}),
      marketContext: () => new Promise(() => {}) })} accounts={[venue]} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', venue.name, user)
    expect(screen.queryByRole('link', { name: /on Hyperliquid/ })).not.toBeInTheDocument()
    await pickInstrument(user, 'BTC')
    const link = screen.getByRole('link', { name: 'Open BTC/USDC on Hyperliquid' })
    expect(link).toHaveAttribute('href', 'https://app.hyperliquid.xyz/trade/BTC')
    expect(link).toHaveAttribute('rel', 'noopener noreferrer')
  })
})

describe('default play title', () => {
  // Only the clock is faked, so the page and user-event keep their real timers.
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 9, 15, 12)) })
  afterEach(() => { vi.useRealTimers() })

  const startDraft = async (user: ReturnType<typeof userEvent.setup>) => {
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', accountFixture.name, user)
    await user.type(screen.getByRole('textbox', { name: 'Perpetual instrument' }), 'BTC-PERP')
  }
  const titleBox = () => screen.getByRole('textbox', { name: 'Play title' })
  const save = () => screen.getByRole('button', { name: /^Save$/ })
  /** A create whose response arrives only when released, so edits can happen while the save is pending. */
  function heldCreate(server: ReturnType<typeof fakeServer>) {
    let release!: () => void
    const gate = new Promise<void>(resolve => { release = resolve })
    const createPlay = vi.fn(async (...args: Parameters<typeof server.api.createPlay>) => {
      const play = await server.api.createPlay(...args)
      await gate
      return play
    })
    return { createPlay, release }
  }

  it('names a play saved without a title by its asset, direction and date, then shows it everywhere', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await startDraft(user)
    // The input previews what an empty title will become, and follows the direction.
    expect(titleBox()).toHaveAttribute('placeholder', 'BTC-PERP Long · 15 Oct 2026')
    await user.click(within(screen.getByRole('group', { name: 'Direction' })).getByRole('button', { name: /Short/ }))
    expect(titleBox()).toHaveAttribute('placeholder', 'BTC-PERP Short · 15 Oct 2026')
    await user.click(save())
    await waitFor(() => expect(server.api.createPlay).toHaveBeenCalledOnce())
    expect(server.api.createPlay.mock.calls[0]![0]).toMatchObject({ title: 'BTC-PERP Short · 15 Oct 2026' })
    await waitFor(() => expect(titleBox()).toHaveValue('BTC-PERP Short · 15 Oct 2026'))
    // The generated name is the saved name, so nothing is left to save.
    expect(screen.getByRole('button', { name: /Saved/ })).toBeDisabled()

    // A later save of a cleared title is named again, with the day of that save.
    vi.setSystemTime(new Date(2026, 9, 16, 12))
    await user.clear(titleBox())
    await user.click(save())
    await waitFor(() => expect(server.api.updatePlay).toHaveBeenCalledOnce())
    expect(server.api.updatePlay.mock.lastCall![1]).toMatchObject({ title: 'BTC-PERP Short · 16 Oct 2026' })
    await waitFor(() => expect(titleBox()).toHaveValue('BTC-PERP Short · 16 Oct 2026'))

    await user.click(screen.getByRole('button', { name: 'All plays' }))
    await user.click(await screen.findByRole('tab', { name: /^Drafts/ }))
    expect(await screen.findByRole('button', { name: 'Open BTC-PERP Short · 16 Oct 2026' })).toBeInTheDocument()
    expect(screen.queryByText('Untitled play')).not.toBeInTheDocument()
  })

  it('never replaces a name the owner typed, on the first or a later save', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await startDraft(user)
    await user.type(titleBox(), 'Range reclaim')
    await user.click(save())
    await waitFor(() => expect(server.api.createPlay).toHaveBeenCalledOnce())
    expect(server.api.createPlay.mock.calls[0]![0]).toMatchObject({ title: 'Range reclaim' })
    await user.type(titleBox(), ' v2')
    await user.click(save())
    await waitFor(() => expect(server.api.updatePlay).toHaveBeenCalledOnce())
    expect(server.api.updatePlay.mock.lastCall![1]).toMatchObject({ title: 'Range reclaim v2' })
    expect(titleBox()).toHaveValue('Range reclaim v2')
  })

  it('names a title that was only spaces, and shows the name after other edits made while saving', async () => {
    const server = fakeServer()
    const { createPlay, release } = heldCreate(server)
    const user = userEvent.setup()
    render(<Page api={client(server, { createPlay })} />)
    await startDraft(user)
    await user.type(titleBox(), '   ')
    await user.click(save())
    await waitFor(() => expect(createPlay).toHaveBeenCalledOnce())
    expect(createPlay.mock.calls[0]![0]).toMatchObject({ title: 'BTC-PERP Long · 15 Oct 2026' })
    // The owner changes the plan, not the title, before the response arrives.
    await user.type(screen.getByRole('spinbutton', { name: 'Entry 1 planned entry price (quote units)' }), '100')
    release()
    await waitFor(() => expect(titleBox()).toHaveValue('BTC-PERP Long · 15 Oct 2026'))
    // The price edit is still unsaved; the title is not what makes the play dirty.
    await waitFor(() => expect(save()).toBeEnabled())
    await user.click(save())
    await waitFor(() => expect(server.api.updatePlay).toHaveBeenCalledOnce())
    expect(server.api.updatePlay.mock.lastCall![1]).toMatchObject({ title: 'BTC-PERP Long · 15 Oct 2026' })
    await waitFor(() => expect(screen.getByRole('button', { name: /Saved/ })).toBeDisabled())
  })

  it('keeps a name typed while a nameless save is pending, and saves it next', async () => {
    const server = fakeServer()
    const { createPlay, release } = heldCreate(server)
    const user = userEvent.setup()
    render(<Page api={client(server, { createPlay })} />)
    await startDraft(user)
    await user.click(save())
    await waitFor(() => expect(createPlay).toHaveBeenCalledOnce())
    await user.type(titleBox(), 'Mine')
    release()
    await waitFor(() => expect(save()).toBeEnabled())
    expect(titleBox()).toHaveValue('Mine')
    await user.click(save())
    await waitFor(() => expect(server.api.updatePlay).toHaveBeenCalledOnce())
    expect(server.api.updatePlay.mock.lastCall![1]).toMatchObject({ title: 'Mine' })
  })

  it('keeps the blank title of a cancelled play, which the API would reject changing, and its Untitled label', async () => {
    const server = fakeServer()
    const legacy: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, title: '', status: 'cancelled', planRevision: 1,
      cancelReason: 'missed', plannedAtUtc: '2026-10-05T12:00:00Z', endedAtUtc: '2026-10-05T13:00:00Z' } }
    server.plays.set(legacy.summary.id, legacy)
    const user = userEvent.setup()
    render(<Page api={client(server)} />)
    await user.click(await screen.findByRole('tab', { name: /^Cancelled/ }))
    await user.click(await screen.findByRole('button', { name: 'Open Untitled play' }))
    await screen.findByRole('textbox', { name: 'Play title' })
    expect(titleBox()).toHaveAttribute('placeholder', 'Name this play')
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByRole('textbox', { name: 'Review' }), 'Late note')
    await user.click(save())
    await waitFor(() => expect(server.api.updatePlay).toHaveBeenCalledOnce())
    expect(server.api.updatePlay.mock.lastCall![1]).toMatchObject({ title: '', review: 'Late note' })
    expect(titleBox()).toHaveValue('')
  })
})

describe('venue tracking', () => {
  it('checks a tracked play when opened and takes the status that linked fills produced', async () => {
    const server = fakeServer()
    const venue = { ...accountFixture, venueId: 'hyperliquid', address: `0x${'a'.repeat(40)}` }
    const api = client(server, { instruments: () => Promise.resolve(instrumentCatalogFixture), candles: () => new Promise(() => {}),
      marketContext: () => new Promise(() => {}) })
    const user = userEvent.setup()
    render(<Page api={api} accounts={[venue]} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', venue.name, user)
    await pickInstrument(user, 'BTC')
    await user.type(screen.getByRole('spinbutton', { name: 'Entry 1 planned entry price (quote units)' }), '100')
    const direction = () => within(screen.getByRole('group', { name: 'Direction' }))
    expect(direction().getByRole('button', { name: /Short/ })).toBeEnabled()
    // The venue reports the entry filled: the server moves the play to Open on the next check.
    server.api.runAccountMonitor.mockImplementation(() => {
      const id = [...server.plays.keys()][0]!
      const play = server.plays.get(id)!
      const opened = { ...play, summary: { ...play.summary, status: 'open' as const, version: play.summary.version + 1 } }
      server.plays.set(id, opened)
      return Promise.resolve({ requestId: ids(5000), monitor: monitorFixture })
    })
    await user.click(screen.getByRole('button', { name: 'Plan it' }))
    expect(server.api.runAccountMonitor).not.toHaveBeenCalled()
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await user.click(await screen.findByRole('button', { name: 'Check' }))
    await waitFor(() => expect(server.api.runAccountMonitor).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(within(screen.getByRole('region', { name: 'Play draft workspace' })).getByText('Open', { selector: '.badge' })).toBeInTheDocument())
    // A filled entry fixes the direction: it decides which side closes the position.
    expect(direction().getByRole('button', { name: /Short/ })).toBeDisabled()
    expect(direction().getByRole('button', { name: /Long/ })).toBeDisabled()
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    expect(screen.getByRole('button', { name: 'Check' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Pause' })).not.toBeInTheDocument()
  })
})

describe('monitor credential', () => {
  it('opens the Play account credential management from the Execution monitor', async () => {
    const server = fakeServer()
    const venue = { ...accountFixture, venueId: 'hyperliquid', address: `0x${'a'.repeat(40)}` }
    const api = client(server, { instruments: () => Promise.resolve(instrumentCatalogFixture), candles: () => new Promise(() => {}),
      marketContext: () => new Promise(() => {}),
      playExecution: vi.fn((id: string) => Promise.resolve({ ...server.execution(server.plays.get(id)!),
        monitor: { ...monitorFixture, state: 'credential-error' as const } })) })
    const manage = vi.fn()
    const user = userEvent.setup()
    render(<Page api={api} accounts={[venue]} onManageAccount={manage} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', venue.name, user)
    await pickInstrument(user, 'BTC')
    await user.type(screen.getByRole('spinbutton', { name: 'Entry 1 planned entry price (quote units)' }), '100')
    await user.click(screen.getByRole('button', { name: 'Plan it' }))
    await user.click(await screen.findByRole('tab', { name: 'Execution' }))
    await user.click(await screen.findByRole('button', { name: 'Manage account credential' }))
    expect(manage).toHaveBeenCalledWith(venue.id)
    expect(screen.queryByRole('link', { name: 'Manage account credential' })).not.toBeInTheDocument()
  })
})

describe('saved play helpers', () => {
  it('describes plan revisions by entry and target identity', () => {
    const draft = createDraft()
    const before = planFromDraft({ ...draft, entries: [{ ...draft.entries[0]!, price: '100', stops: [{ ...draft.entries[0]!.stops[0]!, value: '95' }] }] })
    const entry = before.entries[0]!
    const after = { ...before, leverage: '5', entries: [{ ...entry, stops: [{ ...entry.stops[0]!, unit: 'percent' as const, value: '3' }, { id: 's2', unit: 'price' as const, value: '90', share: '' }],
      targets: [{ ...entry.targets[0]!, value: '110' }, { id: 't2', unit: 'price' as const, value: '120', share: '50' }] }],
      notes: { ...before.notes, thesis: 'Changed' } }
    expect(describePlanChanges(before, after)).toEqual([
      'Leverage: 1× → 5×', 'Entry 1 stop 1: 95 → 3%', 'Entry 1 stop 2 added at 90', 'Entry 1 target 1: blank → 110', 'Entry 1 target 2 added at 120', 'Thesis edited',
    ])
    expect(describePlanChanges(after, { ...after, entries: [] })).toEqual(['Entry 1 removed'])
  })

  it('syncs evidence: deletes removed images, uploads new ones and saves edited notes, then stops at a failure', async () => {
    const server = fakeServer()
    const api = client(server)
    const image = new Blob([new Uint8Array([1])], { type: 'image/png' })
    const kept = { id: 'k', serverId: ids(1), savedState: { note: 'old', markup: null }, source: 'upload' as const, image, name: 'a.png', context: '', note: 'new', markup: null, addedAt: '' }
    const fresh = { id: 'f', source: 'capture' as const, image, name: 'b.png', context: '', note: 'capture', markup: null, addedAt: '' }
    server.evidence.push({ id: ids(1), playId: ids(9), source: 'upload', contentType: 'image/png', sizeBytes: 1, sha256: 'a'.repeat(64), note: 'old', createdAtUtc: '', updatedAtUtc: '', markup: null },
      { id: ids(2), playId: ids(9), source: 'upload', contentType: 'image/png', sizeBytes: 1, sha256: 'a'.repeat(64), note: '', createdAtUtc: '', updatedAtUtc: '', markup: null })
    const result = await syncEvidence(api, ids(9), [kept, fresh], [ids(1), ids(2)])
    expect(result.error).toBeNull()
    expect(server.api.deleteEvidence).toHaveBeenCalledWith(ids(2))
    expect(server.api.updateEvidenceNote).toHaveBeenCalledWith(ids(1), 'new')
    expect(result.evidence[1]).toMatchObject({ id: 'f', savedState: { note: 'capture' } })
    expect(result.evidence[1]!.serverId).toBeTruthy()

    const failing = client(server, { deleteEvidence: vi.fn().mockRejectedValue(new ApiError('unavailable', 'Down')) })
    const failed = await syncEvidence(failing, ids(9), [fresh], [ids(1)])
    expect(failed.error).toBeInstanceOf(ApiError)
    expect(failed.pendingDeletes).toEqual([ids(1)])
    expect(failed.evidence[0]!.serverId).toBeUndefined()
  })
})

// Phase 0, 0.1 sizing truth: wiring between the page, the execution and the workspace.
describe('sizing truth on the page', () => {
  const venue: BrokerAccount = { ...accountFixture, venueId: 'hyperliquid', address: `0x${'a'.repeat(40)}`, balanceUsd: '10000' }
  const venueApi = (server: ReturnType<typeof fakeServer>, overrides: Partial<WorkspaceApi> = {}) => client(server, {
    instruments: () => Promise.resolve(instrumentCatalogFixture), candles: () => new Promise(() => {}), marketContext: () => new Promise(() => {}),
    sizing: vi.fn().mockResolvedValue(sizingFixture), ...overrides })

  it('blocks a save above the reported available margin in the quote unit of the instrument (F-16)', async () => {
    const server = fakeServer()
    const user = userEvent.setup()
    render(<Page api={venueApi(server)} accounts={[{ ...venue, availableBalanceUsd: '100', availableBalanceSource: 'margin' }]} />)
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    await chooseInPicker('Account', venue.name, user)
    await pickInstrument(user, 'BTC')
    await user.type(screen.getByRole('spinbutton', { name: 'Entry 1 planned entry price (quote units)' }), '100')
    await user.type(screen.getByRole('spinbutton', { name: 'Whole-position margin (USDC)' }), '500')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(server.api.createPlay).not.toHaveBeenCalled()
    expect(within(screen.getByRole('region', { name: 'Notifications' })).getByRole('alert'))
      .toHaveTextContent('Fix the plan first. Margin 500 USDC is above the 100 USDC budget (reported margin).')
  })

  it('offers no size or leverage once an entry has a linked fill, and level suggestions only for the unfilled entry (R3b)', async () => {
    const server = fakeServer()
    const exit = (id: string, value: string) => ({ id, unit: 'price' as const, value, share: '100' })
    const open: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, status: 'open', planRevision: 1, venueId: 'hyperliquid',
      instrument: 'BTC', instrumentSource: 'venue', plannedAtUtc: '2026-10-03T10:00:00.000Z' },
      plan: { ...savedPlayFixture.plan, size: '100', entries: [
        { id: 'entry-1', name: 'Entry 1', color: '#b9c9e4', share: '50', price: '100', stops: [exit('stop-1', '80')], targets: [exit('target-1', '105')] },
        { id: 'entry-2', name: 'Entry 2', color: '#e4c9b9', share: '50', price: '90', stops: [exit('stop-2', '70')], targets: [exit('target-2', '95')] },
      ] } }
    server.plays.set(open.summary.id, open)
    const playExecution = vi.fn(() => Promise.resolve({ ...server.execution(open), entries: [
      { entryId: 'entry-1', filledQuantity: '1', averageFillPrice: '100', restingOrders: 0 },
      { entryId: 'entry-2', filledQuantity: '0', averageFillPrice: null, restingOrders: 1 },
    ] }))
    const draft = draftFromSaved(open, [])
    const user = userEvent.setup()
    render(<Page api={venueApi(server, { playExecution })} accounts={[venue]} initial={{ view: 'editor', draft, saved: savedState(open, draft) }} />)
    await waitFor(() => expect(playExecution).toHaveBeenCalled())
    await user.click(within(await screen.findByTestId('suggestions-panel')).getByRole('button', { name: /^Suggestions/ }))
    await waitFor(() => expect(screen.getAllByTestId('max-stop-suggestion').map(row => row.querySelector('.suggestion-row-title')?.textContent))
      .toEqual(['Entry 2 · stop']))
    expect(screen.queryByTestId('size-suggestion')).not.toBeInTheDocument()
    expect(screen.queryByTestId('leverage-suggestion')).not.toBeInTheDocument()
    expect(screen.getByRole('spinbutton', { name: 'Whole-position margin (USDC)' })).not.toHaveAttribute('data-suggested')
  })
})
