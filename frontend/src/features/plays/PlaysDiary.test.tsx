import { useCallback, useRef, useState } from 'react'
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import type { ManualExecutions } from '@/api/diary'
import { emptyStructuredReview, preflightCodes, type PlayExecution, type PlayFields, type PlayList, type PlayNote, type PlaySummary, type PlayView, type SavedPlay } from '@/api/plays'
import { VenuesProvider } from '@/api/venues'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { accountFixture, idleMarketStream, instrumentCatalogFixture, overviewFixture, playApiStubs, playCountsFrom, playListPage, playsFrom, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { systemFixture } from '@/test/system-fixture'
import { retrospectiveSavedFixture } from '@/test/retrospective.fixtures'
import { PlaysPage } from './PlaysPage'
import { PlayWorkspace } from './PlayWorkspace'
import { createDraft } from './draft'
import { emptyManualDraft } from './manual'
import { createPlaysSession, draftFromSaved, hasUnsavedPlayWork, savedState, type PlaysSession } from './saved'
import type { PlayLogChange } from './log-draft'
import { ApplicationShell } from '@/features/workspace/ApplicationShell'
import * as overviewActions from '@/features/workspace/OverviewActions'
import * as savedChecks from './saved'
import * as preflightChecks from './preflight'

const at = '2026-10-13T10:00:00Z'
const otherId = '00000000-0000-4000-8000-000000000002'
const terminal: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, status: 'cancelled', version: 2,
  endedAtUtc: at, cancelReason: 'missed', origin: 'planned', reviewedAtUtc: null }, review: 'Existing free text', structuredReview: emptyStructuredReview() }
const sessionFor = (play: SavedPlay): PlaysSession => {
  const draft = draftFromSaved(play, [])
  return { view: 'editor', draft, saved: savedState(play, draft) }
}
const execution = (play: SavedPlay): PlayExecution => ({
  playId: play.summary.id, status: play.summary.status, tracked: false, reason: 'No imported tracking.', checkedAtUtc: null,
  totals: { enteredQuantity: '0', exitedQuantity: '0', openQuantity: '0', closedPnlUsd: '0', closedPnlBasis: 'gross', fees: [] },
  entries: [], links: [], suggestions: [], unlinkedOrders: [], notice: 'Facts only.', issues: [], monitor: null, discrepancies: [], watches: [],
})

const manualLedger = (play: SavedPlay): ManualExecutions => ({
  play, records: [], history: [], totals: { enteredQuantity: '0', exitedQuantity: '0', openQuantity: '0',
    grossResultUsd: '0', netResultUsd: null, feesComplete: false }, notice: 'Manual facts only.',
})
function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (cause: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
const planned: SavedPlay = { ...terminal, summary: { ...terminal.summary,
  status: 'planned', planRevision: 1, plannedAtUtc: at, endedAtUtc: null, cancelReason: null } }
const readyManualSession = (): PlaysSession => ({ ...sessionFor(planned), manual: { ...emptyManualDraft(),
  entryId: 'entry-1', occurredAtUtc: '2026-10-13T10:00:00', side: 'buy', quantity: '1', price: '100', source: 'Statement' } })
function client(play = terminal, more: SavedPlay[] = []) {
  let current = play
  const methods = {
    plays: vi.fn<WorkspaceApi['plays']>(query => playsFrom([current.summary, ...more.map(item => item.summary)])(query)),
    playCounts: vi.fn<WorkspaceApi['playCounts']>(query => playCountsFrom([current.summary, ...more.map(item => item.summary)])(query)),
    play: vi.fn<WorkspaceApi['play']>(() => Promise.resolve(current)),
    playExecution: vi.fn(() => Promise.resolve(execution(current))),
    sizing: vi.fn(() => Promise.reject(new ApiError('unavailable', 'No sizing.'))),
    evidence: vi.fn(() => Promise.resolve([])),
    nextActions: vi.fn<WorkspaceApi['nextActions']>(() => Promise.resolve([])),
    playNotes: vi.fn<WorkspaceApi['playNotes']>(() => Promise.resolve([])),
    addPlayNote: vi.fn<WorkspaceApi['addPlayNote']>(() => Promise.resolve([])),
    completeReview: vi.fn<WorkspaceApi['completeReview']>(() => {
      current = { ...current, summary: { ...current.summary, version: current.summary.version + 1, reviewCompletion: 'completed', reviewCompletedAtUtc: at } }
      return Promise.resolve(current)
    }),
    reopenReview: vi.fn<WorkspaceApi['reopenReview']>(() => {
      current = { ...current, summary: { ...current.summary, version: current.summary.version + 1, reviewCompletion: null, reviewCompletedAtUtc: null } }
      return Promise.resolve(current)
    }),
    updatePlay: vi.fn((_id: string, fields: PlayFields & { expectedVersion: number }) => {
      current = { ...current, review: fields.review, structuredReview: fields.structuredReview ?? emptyStructuredReview(),
        summary: { ...current.summary, version: current.summary.version + 1, hasReview: true, reviewedAtUtc: at } }
      return Promise.resolve(current)
    }),
  }
  return { methods, api: { ...createWorkspaceApi('test-only'), ...methods } as WorkspaceApi }
}
/** Replies to every later list and count read with these plays. */
function serve(methods: ReturnType<typeof client>['methods'], plays: PlaySummary[]) {
  methods.plays.mockImplementation(playsFrom(plays))
  methods.playCounts.mockImplementation(playCountsFrom(plays))
}
/** List reads, optionally of one view. */
const reads = (methods: ReturnType<typeof client>['methods'], view?: PlayView) =>
  methods.plays.mock.calls.filter(([query]) => view === undefined || query.view === view).length
function Harness({ api, initial = createPlaysSession(), request = null, onHandled = vi.fn(), onSession = vi.fn(), manual = false, generation = 0 }: {
  api: WorkspaceApi; initial?: PlaysSession; request?: string | null; onHandled?: () => void;
  onSession?: (session: PlaysSession) => void; manual?: boolean; generation?: number
}) {
  const [session, setSession] = useState(initial)
  const [requested, setRequested] = useState(request)
  const [shown, setShown] = useState(true)
  // As the shell does: a mounted page applies log changes; without one, they go to the current session.
  const applier = useRef<((change: PlayLogChange) => void) | null>(null)
  const onLogApplier = useCallback((apply: (change: PlayLogChange) => void) => {
    applier.current = apply
    return () => { if (applier.current === apply) applier.current = null }
  }, [])
  const venues = systemFixture.venues.map(venue => venue.id === accountFixture.venueId
    ? { ...venue, capabilities: { ...venue.capabilities, manualExecutions: manual } } : venue)
  return <VenuesProvider venues={venues}><NotificationProvider>
    <button onClick={() => setRequested(otherId)}>Request another Play</button>
    <button onClick={() => setShown(value => !value)}>Toggle Plays page</button>
    {shown && <PlaysPage accounts={[accountFixture]} portfolios={[portfolioFixture]} api={api} session={session}
      onSession={next => { setSession(next); onSession(next) }} onLogApplier={onLogApplier}
      onLogChange={change => { if (applier.current) applier.current(change); else setSession(current => ({ ...current, log: change(current.log) })) }}
      requestedPlayId={requested}
      onRequestedPlayHandled={() => { onHandled(); setRequested(null) }} reloadGeneration={generation} />}
  </NotificationProvider></VenuesProvider>
}

describe('diary workspace integration', () => {
  it.each(['tab', 'page'] as const)('keeps the v6 high-water mark across a %s remount when dirty review kept the v3 baseline', async mode => {
    const closed: SavedPlay = { ...terminal, summary: { ...terminal.summary, status: 'closed', version: 3 } }
    const { api } = client(closed)
    const record: ManualExecutions['records'][number] = { id: otherId, role: 'entry', entryId: 'entry-1',
      occurredAtUtc: at, side: 'buy', quantity: '1', price: '100', fee: '0', feeCurrency: 'USD',
      source: 'Statement', createdAtUtc: at, updatedAtUtc: at }
    const response = (version: number, result: string, review = closed.review): ManualExecutions => {
      const entered = { ...record, price: version === 3 ? '100' : version === 4 ? '94' : '92' }
      const exit = { ...record, id: '00000000-0000-4000-8000-000000000003', role: 'exit' as const,
        entryId: null, side: 'sell' as const, occurredAtUtc: '2026-10-13T10:01:00Z' }
      const history: ManualExecutions['history'] = [record, exit].map((after, index) => ({
        id: `00000000-0000-4000-8000-00000000000${index + 4}`, executionId: after.id, reason: null,
        occurredAtUtc: '2026-10-13T10:02:00Z', before: null, after,
      }))
      if (version >= 4) history.push({ id: '00000000-0000-4000-8000-000000000006', executionId: record.id,
        reason: 'Statement amended', occurredAtUtc: '2026-10-13T10:03:00Z', before: record, after: { ...record, price: '94' } })
      if (version >= 5) history.push({ id: '00000000-0000-4000-8000-000000000007', executionId: record.id,
        reason: 'Second amendment', occurredAtUtc: '2026-10-13T10:04:00Z', before: { ...record, price: '94' }, after: entered })
      return { ...manualLedger({ ...closed, review, summary: { ...closed.summary, version } }), records: [entered, exit], history,
        totals: { enteredQuantity: '1', exitedQuantity: '1', openQuantity: '0', grossResultUsd: result, netResultUsd: result, feesComplete: true } }
    }
    const read = vi.fn(async () => response(3, '0'))
    const correct = vi.fn<NonNullable<WorkspaceApi['correctManualExecution']>>()
      .mockRejectedValueOnce(new ApiError('unavailable', 'Response lost.'))
      .mockResolvedValue(response(4, '6'))
    api.manualExecutions = read
    api.correctManualExecution = correct
    let current = sessionFor(closed)
    const user = userEvent.setup()
    render(<Harness api={api} initial={current} onSession={next => { current = next }} manual />)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await user.click(await screen.findByRole('button', { name: `Correct ${otherId}` }))
    await user.clear(screen.getByLabelText('Execution price'))
    await user.type(screen.getByLabelText('Execution price'), '94')
    await user.type(screen.getByLabelText('Correction reason'), 'Statement amended')
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    await screen.findByText(/Response lost\./)
    const attempted = correct.mock.calls[0]
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByLabelText('Lesson'), 'Local unsaved lesson')
    // The other tab saved review at v5, then corrected the entry price to 92 at v6.
    read.mockResolvedValue(response(6, '8', 'Review changed in another tab'))
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await screen.findByText(/The saved Play changed elsewhere/)
    expect(screen.getByText('Net realized USD').nextElementSibling).toHaveTextContent('8')
    expect(screen.getByText('Execution audit history (4)')).toBeInTheDocument()
    expect(current.saved?.summary.version).toBe(3)
    expect(current.observedPlay).toMatchObject({ version: 6, conflict: true })
    expect(current.manual?.attempt).toEqual(attempted?.[2])
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.clear(screen.getByLabelText('Lesson'))
    read.mockRejectedValue(new ApiError('unavailable', 'Current ledger unavailable.'))
    if (mode === 'page') {
      await user.click(screen.getByRole('button', { name: 'Toggle Plays page' }))
      await user.click(screen.getByRole('button', { name: 'Toggle Plays page' }))
    }
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await screen.findByText(/Current ledger unavailable\./)
    expect(screen.queryByLabelText('Manual execution totals')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    await waitFor(() => expect(current.manual?.executionId).toBeNull())
    expect(correct.mock.calls[1]).toEqual(attempted)
    expect(current.saved?.summary.version).toBe(3)
    expect(current.observedPlay?.version).toBe(6)
    expect(screen.getByText(/The saved Play changed elsewhere/)).toBeInTheDocument()
    expect(screen.getByText(/execution was already saved.*older than the latest known Play/i)).toBeInTheDocument()
    expect(screen.queryByLabelText('Manual execution totals')).not.toBeInTheDocument()
    read.mockResolvedValue(response(6, '8', 'Review changed in another tab'))
    await user.click(screen.getByRole('button', { name: 'Reload manual ledger' }))
    await waitFor(() => expect(current.saved?.summary.version).toBe(6))
    expect(screen.getByText('Net realized USD').nextElementSibling).toHaveTextContent('8')
    expect(screen.getByText('Execution audit history (4)')).toBeInTheDocument()
    expect(screen.queryByText(/The saved Play changed elsewhere/)).not.toBeInTheDocument()
    expect(correct).toHaveBeenCalledTimes(2)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    expect(screen.getByRole('textbox', { name: 'Review' })).toHaveValue('Review changed in another tab')
  })
  it('excludes Pause and manual writes, blocks discard while recording, and retains an uncertain retry', async () => {
    const { api, methods } = client(planned)
    const pausing = deferred<SavedPlay>()
    const recording = deferred<ManualExecutions>()
    api.changePlayStatus = vi.fn(() => pausing.promise)
    const add = vi.fn<NonNullable<WorkspaceApi['addManualExecution']>>().mockImplementationOnce(() => recording.promise)
    api.addManualExecution = add
    const read = vi.fn(async () => manualLedger(planned))
    api.manualExecutions = read
    let current = readyManualSession()
    const user = userEvent.setup()
    render(<Harness api={api} initial={current} onSession={next => { current = next }} manual />)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await screen.findByLabelText('Manual execution totals')
    await user.click(screen.getByRole('button', { name: 'Pause' }))
    expect(screen.getByRole('button', { name: 'Record execution' })).toBeDisabled()
    fireEvent.submit(screen.getByRole('form', { name: 'Record manual execution' }))
    expect(add).not.toHaveBeenCalled()
    await act(async () => pausing.reject(new ApiError('http', 'Pause rejected.', 409)))
    await user.click(screen.getByRole('button', { name: 'Record execution' }))
    expect(add).toHaveBeenCalledOnce()
    const attempted = add.mock.calls[0]
    expect(screen.getByRole('button', { name: 'Pause' })).toBeDisabled()
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByRole('textbox', { name: 'Review' }), ' local edit')
    expect(screen.getByRole('button', { name: /^Save$/ })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(methods.updatePlay).not.toHaveBeenCalled()
    await user.clear(screen.getByRole('textbox', { name: 'Review' }))
    await user.type(screen.getByRole('textbox', { name: 'Review' }), planned.review)
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    expect(await screen.findByRole('button', { name: 'Discard changes' })).toBeDisabled()
    await act(async () => recording.reject(new ApiError('unavailable', 'Response lost.')))
    expect(screen.getByRole('button', { name: 'Discard changes' })).toBeEnabled()
    expect(current.manual?.attempt).toEqual(attempted?.[1])
    await user.click(screen.getByRole('button', { name: 'Continue editing' }))
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    const opened: SavedPlay = { ...planned, summary: { ...planned.summary, status: 'open', version: 3 } }
    read.mockResolvedValue(manualLedger(opened))
    add.mockResolvedValue(manualLedger(opened))
    await user.click(screen.getByRole('button', { name: 'Retry same execution' }))
    expect(add.mock.calls[1]).toEqual(attempted)
    expect(api.changePlayStatus).toHaveBeenCalledOnce()
    await waitFor(() => expect(current.manual?.attempt).toBeUndefined())
  })
  it('blocks manual and lifecycle writes while saving even if in-flight edits make the draft clean again', async () => {
    const { api, methods } = client(planned)
    const saving = deferred<SavedPlay>()
    methods.updatePlay.mockImplementation(() => saving.promise)
    const add = vi.fn<NonNullable<WorkspaceApi['addManualExecution']>>()
    api.addManualExecution = add
    api.manualExecutions = vi.fn(async () => manualLedger(planned))
    api.changePlayStatus = vi.fn()
    const user = userEvent.setup()
    render(<Harness api={api} initial={readyManualSession()} manual />)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByRole('textbox', { name: 'Review' }), ' saving')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await user.clear(screen.getByRole('textbox', { name: 'Review' }))
    await user.type(screen.getByRole('textbox', { name: 'Review' }), planned.review)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await screen.findByLabelText('Manual execution totals')
    expect(screen.queryByText('Save the Play changes before changing executions.')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Record execution' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Pause' })).toBeDisabled()
    fireEvent.submit(screen.getByRole('form', { name: 'Record manual execution' }))
    fireEvent.click(screen.getByRole('button', { name: 'Pause' }))
    expect(add).not.toHaveBeenCalled()
    expect(api.changePlayStatus).not.toHaveBeenCalled()
    await act(async () => saving.reject(new ApiError('http', 'Save rejected.', 409)))
    expect(screen.getByRole('button', { name: 'Record execution' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Pause' })).toBeEnabled()
  })
  it.each([
    ['pause', 'resolve'], ['pause', 'reject'], ['save', 'resolve'], ['save', 'reject'], ['manual', 'resolve'], ['manual', 'reject'],
  ] as const)('does not let old %s cleanup %s unlock a newer manual request after API replacement', async (kind, outcome) => {
    const { api, methods } = client(planned)
    const previous = deferred<SavedPlay>()
    const previousManual = deferred<ManualExecutions>()
    const recording = deferred<ManualExecutions>()
    api.changePlayStatus = vi.fn(() => previous.promise)
    methods.updatePlay.mockImplementation(() => previous.promise)
    api.addManualExecution = vi.fn(() => previousManual.promise)
    api.manualExecutions = vi.fn(async () => manualLedger(planned))
    const nextAdd = vi.fn(() => recording.promise)
    const nextApi = { ...api, addManualExecution: nextAdd }
    let current = readyManualSession()
    const user = userEvent.setup()
    const view = render(<Harness api={api} initial={current} onSession={next => { current = next }} manual />)
    if (kind === 'save') {
      await user.click(screen.getByRole('tab', { name: 'Review' }))
      await user.type(screen.getByRole('textbox', { name: 'Review' }), ' saving')
      await user.click(screen.getByRole('button', { name: /^Save$/ }))
      await user.clear(screen.getByRole('textbox', { name: 'Review' }))
      await user.type(screen.getByRole('textbox', { name: 'Review' }), planned.review)
    } else {
      await user.click(screen.getByRole('tab', { name: 'Execution' }))
      await screen.findByLabelText('Manual execution totals')
      await user.click(screen.getByRole('button', { name: kind === 'pause' ? 'Pause' : 'Record execution' }))
    }
    // Remount the journal panel under the new API while the old request is still unresolved.
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    view.rerender(<Harness api={nextApi} initial={current} onSession={next => { current = next }} manual />)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    const record = screen.getByRole('button', { name: /^(Record execution|Retry same execution)$/ })
    await waitFor(() => expect(record).toBeEnabled())
    await user.click(record)
    expect(nextAdd).toHaveBeenCalledOnce()
    const attempted = current.manual?.attempt
    await act(async () => {
      if (outcome === 'reject') (kind === 'manual' ? previousManual : previous).reject(new ApiError('unavailable', 'Old response failed.'))
      else {
        const returned: SavedPlay = { ...planned, summary: { ...planned.summary, version: 3, status: kind === 'pause' ? 'paused' : 'open' } }
        if (kind === 'manual') previousManual.resolve(manualLedger(returned))
        else previous.resolve(returned)
      }
    })
    expect(current.saved?.summary.version).toBe(2)
    expect(current.manual?.attempt).toEqual(attempted)
    expect(screen.getByRole('button', { name: 'Pause' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    expect(await screen.findByRole('button', { name: 'Discard changes' })).toBeDisabled()
    await act(async () => recording.reject(new ApiError('unavailable', 'New response lost.')))
    expect(screen.getByRole('button', { name: 'Discard changes' })).toBeEnabled()
    expect(current.manual?.attempt).toEqual(attempted)
  })
  it.each(['status first', 'manual first', 'manual twice', 'manual then discard'] as const)('admits only one write when handlers run in the same render: %s', async order => {
    const { api } = client(planned)
    const pausing = deferred<SavedPlay>()
    const recording = deferred<ManualExecutions>()
    api.changePlayStatus = vi.fn(() => pausing.promise)
    api.addManualExecution = vi.fn(() => recording.promise)
    api.manualExecutions = vi.fn(async () => manualLedger(planned))
    let current = readyManualSession()
    render(<Harness api={api} initial={current} onSession={next => { current = next }} manual />)
    await userEvent.click(screen.getByRole('tab', { name: 'Execution' }))
    await screen.findByLabelText('Manual execution totals')
    const pause = screen.getByRole('button', { name: 'Pause' })
    const discard = screen.getByRole('button', { name: 'Discard execution edit' })
    const form = screen.getByRole('form', { name: 'Record manual execution' })
    const submit = () => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    const status = () => pause.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    act(() => {
      if (order === 'status first') { status(); submit() }
      else {
        submit()
        if (order === 'manual twice') submit()
        else if (order === 'manual then discard') discard.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
        else status()
      }
    })
    expect(api.changePlayStatus).toHaveBeenCalledTimes(order === 'status first' ? 1 : 0)
    expect(api.addManualExecution).toHaveBeenCalledTimes(order === 'status first' ? 0 : 1)
    if (order !== 'status first') expect(current.manual?.attempt).toBeDefined()
    await act(async () => {
      if (order === 'status first') pausing.reject(new ApiError('http', 'Pause rejected.', 409))
      else recording.reject(new ApiError('unavailable', 'Manual response lost.'))
    })
  })
  it('shows the pre-flight at Plan it and goes back to the plan without planning (phase 1, 1.5)', async () => {
    const draftPlay: SavedPlay = { ...planned, plan: { ...planned.plan, entries: planned.plan.entries.map(entry => ({ ...entry, price: '100' })) },
      summary: { ...planned.summary, status: 'draft', version: 1, planRevision: 0, plannedAtUtc: null } }
    const { api } = client(draftPlay)
    api.changePlayStatus = vi.fn()
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(draftPlay)} />)
    await user.click(screen.getByRole('button', { name: 'Plan it' }))
    const checks = within(await screen.findByRole('list', { name: 'Pre-flight checks' })).getAllByRole('listitem')
    expect(checks.map(item => item.textContent)).toEqual([
      'A priced stop for every priced entryNot met', 'Entry, stop and target shares each total 100%Not met', 'Thesis and invalidation are writtenNot met',
      'Risk within the sizing setting at the current ladder levelUnknown', 'The estimated liquidation lies beyond every stopUnknown',
    ])
    await user.click(screen.getByRole('button', { name: 'Back to the plan' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(api.changePlayStatus).not.toHaveBeenCalled()
  })
  it('shows the pre-flight even when every check is met (phase 1, 1.5)', async () => {
    const draftPlay: SavedPlay = { ...planned, plan: { ...planned.plan, entries: planned.plan.entries.map(entry => ({ ...entry, price: '100' })) },
      summary: { ...planned.summary, status: 'draft', version: 1, planRevision: 0, plannedAtUtc: null } }
    const met = vi.spyOn(preflightChecks, 'preflightChecks').mockReturnValue(preflightCodes.map(code => ({ code, outcome: 'passed' as const })))
    try {
      const { api } = client(draftPlay)
      const plan = vi.fn<WorkspaceApi['changePlayStatus']>()
      api.changePlayStatus = plan
      const user = userEvent.setup()
      render(<Harness api={api} initial={sessionFor(draftPlay)} />)
      await user.click(screen.getByRole('button', { name: 'Plan it' }))
      const checks = within(await screen.findByRole('list', { name: 'Pre-flight checks' })).getAllByRole('listitem')
      expect(checks.every(item => item.textContent?.endsWith('Met'))).toBe(true)
      expect(plan).not.toHaveBeenCalled()
    } finally { met.mockRestore() }
  })
  it('declares an open Play with imported fills unresolved, then returns it to Open (phase 1, 1.3a)', async () => {
    const open: SavedPlay = { ...planned, summary: { ...planned.summary, status: 'open' } }
    const fill = { sourceFillId: 'f1', direction: 'Open Long', price: '100', quantity: '1', fee: '0', feeToken: 'USDC', closedPnlUsd: '0',
      side: 'buy' as const, positionEffect: 'open' as const, pnlBasis: 'gross' as const, occurredAtUtc: at }
    const filled = (play: SavedPlay): PlayExecution => ({ ...execution(play), tracked: true, reason: null,
      links: [{ id: ids(161), role: 'entry', entryId: 'entry-1', levelId: null, state: 'linked', source: 'automatic', order: null, filledQuantity: '1', fills: [fill] }] })
    let current = open
    const { api, methods } = client(open)
    methods.play.mockImplementation(() => Promise.resolve(current))
    methods.playExecution.mockImplementation(() => Promise.resolve(filled(current)))
    const declare = vi.fn<WorkspaceApi['declareUnresolved']>((_id, version) => {
      current = { ...current, summary: { ...current.summary, status: 'unresolved', version: version + 1, endedAtUtc: at } }
      return Promise.resolve(current)
    })
    const back = vi.fn<WorkspaceApi['returnToOpen']>((_id, version) => {
      current = { ...current, summary: { ...current.summary, status: 'open', version: version + 1, endedAtUtc: null } }
      return Promise.resolve(current)
    })
    api.declareUnresolved = declare
    api.returnToOpen = back
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(open)} />)
    await user.click(await screen.findByRole('button', { name: 'Declare unresolved' }))
    const dialog = await screen.findByRole('dialog')
    const submit = within(dialog).getByRole('button', { name: 'Declare unresolved' })
    expect(submit).toBeDisabled()
    await user.selectOptions(within(dialog).getByRole('combobox', { name: 'Reason' }), 'shared-close')
    expect(submit).toBeDisabled()
    await user.type(within(dialog).getByRole('textbox', { name: 'What happened' }), 'Closed with another position.')
    await user.click(submit)
    await waitFor(() => expect(declare).toHaveBeenCalledWith(open.summary.id, open.summary.version, 'shared-close', 'Closed with another position.'))
    const status = screen.getByText('Status').closest('div')!
    expect(await within(status).findByText('Unresolved')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Declare unresolved' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Return to Open' }))
    const returning = await screen.findByRole('dialog')
    await user.type(within(returning).getByRole('textbox', { name: 'Reason' }), 'The close was another play\'s.')
    await user.click(within(returning).getByRole('button', { name: 'Return to Open' }))
    await waitFor(() => expect(back).toHaveBeenCalledWith(open.summary.id, open.summary.version + 1, 'The close was another play\'s.'))
    expect(await within(status).findByText('Open')).toBeInTheDocument()
  })
  it('offers Declare unresolved only once the Play holds imported fills (phase 1, 1.3a)', async () => {
    const open: SavedPlay = { ...planned, summary: { ...planned.summary, status: 'open' } }
    const { api, methods } = client(open)
    render(<Harness api={api} initial={sessionFor(open)} />)
    await waitFor(() => expect(methods.playExecution).toHaveBeenCalled())
    expect(screen.queryByRole('button', { name: 'Declare unresolved' })).not.toBeInTheDocument()
  })
  it('offers recent next actions at planning and copies one into the thesis only on request (phase 1, 1.5)', async () => {
    const draftPlay: SavedPlay = { ...planned, summary: { ...planned.summary, status: 'draft', version: 1, planRevision: 0, plannedAtUtc: null } }
    const { api, methods } = client(draftPlay)
    methods.nextActions.mockResolvedValue([{ playId: otherId, title: 'BTC breakout', nextAction: 'Wait for the retest', reviewedAtUtc: at }])
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(draftPlay)} />)
    const thesis = screen.getByRole('textbox', { name: 'Thesis' })
    const before = (thesis as HTMLTextAreaElement).value
    await user.click(await screen.findByText('Your recent next actions (1)'))
    expect(screen.getByText('Wait for the retest')).toBeInTheDocument()
    expect(thesis).toHaveValue(before)
    await user.click(screen.getByRole('button', { name: 'Copy into plan' }))
    expect(screen.getByRole('textbox', { name: 'Thesis' })).toHaveValue(
      `${before.trim() ? `${before.trimEnd()}\n\n` : ''}If …, then Wait for the retest (from BTC breakout)`)
  })
  it('asks for a reason when a next action is copied into a planned Play, writing one revision (phase 1, 1.5)', async () => {
    const { api, methods } = client(planned)
    methods.nextActions.mockResolvedValue([{ playId: otherId, title: 'BTC breakout', nextAction: 'Wait for the retest', reviewedAtUtc: at }])
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(planned)} />)
    await user.click(await screen.findByText('Your recent next actions (1)'))
    await user.click(screen.getByRole('button', { name: 'Copy into plan' }))
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByRole('textbox'), 'Adding my rule from the BTC breakout')
    await user.click(within(dialog).getByRole('button', { name: 'Save revision' }))
    await waitFor(() => expect(methods.updatePlay).toHaveBeenCalledOnce())
    expect(methods.updatePlay.mock.calls[0]![1]).toMatchObject({ revisionReason: 'Adding my rule from the BTC breakout' })
    expect(methods.updatePlay.mock.calls[0]![1].plan.notes.thesis).toContain('If …, then Wait for the retest (from BTC breakout)')
  })
  it('holds one guard through the save prerequisite and the planning request', async () => {
    const draftPlay: SavedPlay = { ...planned, plan: { ...planned.plan, size: '100',
      entries: planned.plan.entries.map(entry => ({ ...entry, price: '100' })) },
      summary: { ...planned.summary, status: 'draft', version: 1, planRevision: 0, plannedAtUtc: null } }
    const { api, methods } = client(draftPlay)
    const saving = deferred<SavedPlay>()
    const planning = deferred<SavedPlay>()
    methods.updatePlay.mockImplementation(() => saving.promise)
    api.changePlayStatus = vi.fn(() => planning.promise)
    const initial = sessionFor(draftPlay)
    initial.draft.notes.review = 'Saved before planning'
    render(<Harness api={api} initial={initial} manual />)
    await userEvent.click(screen.getByRole('button', { name: 'Plan it' }))
    // The plan has no thesis, so the pre-flight shows first; nothing is saved or planned until the owner goes ahead.
    expect(methods.updatePlay).not.toHaveBeenCalled()
    await userEvent.click(await screen.findByRole('button', { name: 'Plan it anyway' }))
    expect(methods.updatePlay).toHaveBeenCalledOnce()
    expect(api.changePlayStatus).not.toHaveBeenCalled()
    // The checklist stays open while the work runs, so the page behind it is hidden from the accessibility tree.
    expect(screen.getByRole('button', { name: 'Planning…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Plan it', hidden: true })).toBeDisabled()
    expect(screen.getByRole('button', { name: /^Save$/, hidden: true })).toBeDisabled()
    const stored = { ...draftPlay, review: initial.draft.notes.review, summary: { ...draftPlay.summary, version: 2 } }
    await act(async () => saving.resolve(stored))
    expect(api.changePlayStatus).toHaveBeenCalledWith(planned.summary.id, 2, { status: 'planned', preflight: expect.arrayContaining([
      { code: 'thesis', outcome: 'skipped' }]) })
    expect(screen.getByRole('button', { name: 'Plan it', hidden: true })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Saved', hidden: true })).toBeDisabled()
    await act(async () => planning.resolve({ ...stored, summary: { ...planned.summary, version: 3 } }))
    expect(screen.getByRole('button', { name: 'Pause' })).toBeEnabled()
    expect(methods.updatePlay).toHaveBeenCalledOnce()
    expect(api.changePlayStatus).toHaveBeenCalledOnce()
  })
  it.each(['initial read', 'reload'] as const)('adopts the corresponding saved fields with a newer manual %s before saving a lesson', async mode => {
    const newer = { ...terminal, review: 'Review saved in another tab', summary: { ...terminal.summary, version: 3 } }
    const { api, methods } = client(newer)
    const read = vi.fn(async () => manualLedger(mode === 'initial read' ? newer : terminal))
    api.manualExecutions = read
    const seen = vi.fn()
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(terminal)} onSession={seen} manual />)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await screen.findByLabelText('Manual execution totals')
    if (mode === 'reload') {
      read.mockResolvedValue(manualLedger(newer))
      await user.click(screen.getByRole('button', { name: 'Reload manual ledger' }))
    }
    await waitFor(() => expect(seen.mock.lastCall?.[0].saved.summary.version).toBe(3))
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    expect(screen.getByRole('textbox', { name: 'Review' })).toHaveValue(newer.review)
    await user.type(screen.getByLabelText('Lesson'), 'A new lesson')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(methods.updatePlay).toHaveBeenCalledWith(terminal.summary.id, expect.objectContaining({
      expectedVersion: 3, review: newer.review, structuredReview: expect.objectContaining({ lesson: 'A new lesson' }),
    }))
  })
  it.each(['initial read', 'reload'] as const)('keeps dirty review and its original version when a manual %s finds a changed document', async mode => {
    const newer = { ...terminal, review: 'Review saved in another tab', summary: { ...terminal.summary, version: 3 } }
    const { api, methods } = client(newer)
    methods.updatePlay.mockRejectedValue(new ApiError('http', 'Reload: this Play changed.', 409))
    const read = vi.fn(async () => manualLedger(mode === 'initial read' ? newer : terminal))
    api.manualExecutions = read
    const initial = sessionFor(terminal)
    initial.draft.structuredReview = { ...emptyStructuredReview(), lesson: 'Local lesson' }
    let current = initial
    const user = userEvent.setup()
    render(<Harness api={api} initial={initial} onSession={next => { current = next }} manual />)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await screen.findByLabelText('Manual execution totals')
    if (mode === 'reload') {
      read.mockResolvedValue(manualLedger(newer))
      await user.click(screen.getByRole('button', { name: 'Reload manual ledger' }))
    }
    await screen.findByText(/The saved Play changed elsewhere/)
    expect(current.saved).toEqual(initial.saved)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    expect(screen.getByRole('textbox', { name: 'Review' })).toHaveValue(terminal.review)
    expect(screen.getByLabelText('Lesson')).toHaveValue('Local lesson')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(methods.updatePlay).toHaveBeenCalledWith(terminal.summary.id, expect.objectContaining({
      expectedVersion: 2, review: terminal.review, structuredReview: expect.objectContaining({ lesson: 'Local lesson' }),
    }))
    await screen.findByText('Reload: this Play changed.')
    expect(current.saved?.summary.version).toBe(2)
    expect(screen.getByLabelText('Lesson')).toHaveValue('Local lesson')
  })
  it.each([false, true])('preserves in-flight review edits after a correction, changed returned document=%s', async changed => {
    const closed: SavedPlay = { ...terminal, summary: { ...terminal.summary, status: 'closed' } }
    const returned = { ...closed, review: changed ? 'Newer server review' : closed.review,
      summary: { ...closed.summary, version: changed ? 4 : 3 } }
    const { api, methods } = client(returned)
    if (changed) methods.updatePlay.mockRejectedValue(new ApiError('http', 'Reload: this Play changed.', 409))
    const record: ManualExecutions['records'][number] = { id: otherId, role: 'entry', entryId: 'entry-1',
      occurredAtUtc: at, side: 'buy', quantity: '1', price: '100', fee: null, feeCurrency: null,
      source: 'Statement', createdAtUtc: at, updatedAtUtc: at }
    api.manualExecutions = vi.fn(async () => ({ ...manualLedger(closed), records: [record] }))
    let finish!: (result: ManualExecutions) => void
    api.correctManualExecution = vi.fn(() => new Promise<ManualExecutions>(resolve => { finish = resolve }))
    const initial = sessionFor(closed)
    let current = initial
    const user = userEvent.setup()
    render(<Harness api={api} initial={initial} onSession={next => { current = next }} manual />)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await user.click(await screen.findByRole('button', { name: `Correct ${otherId}` }))
    await user.type(screen.getByLabelText('Correction reason'), 'Statement amended')
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByLabelText('Lesson'), 'Kept while correcting')
    await act(async () => finish({ ...manualLedger(returned), records: [record] }))
    expect(screen.getByRole('textbox', { name: 'Review' })).toHaveValue(closed.review)
    expect(screen.getByLabelText('Lesson')).toHaveValue('Kept while correcting')
    expect(current.saved?.summary.version).toBe(changed ? 2 : 3)
    expect(current.saved?.fieldsKey).toBe(initial.saved?.fieldsKey)
    expect(current.manual?.executionId).toBeNull()
    if (changed) expect(screen.getByText(/The saved Play changed elsewhere/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(methods.updatePlay).toHaveBeenCalledWith(closed.summary.id, expect.objectContaining({
      expectedVersion: changed ? 2 : 3, review: closed.review,
      structuredReview: expect.objectContaining({ lesson: 'Kept while correcting' }),
    }))
  })
  it.each(['same panel', 'remount with failed GET', 'API replacement with failed GET'] as const)('rejects an older correction replay after a newer ledger: %s', async mode => {
    const closed: SavedPlay = { ...terminal, summary: { ...terminal.summary, status: 'closed', version: 3 } }
    const { api } = client(closed)
    const record: ManualExecutions['records'][number] = { id: otherId, role: 'entry', entryId: 'entry-1',
      occurredAtUtc: at, side: 'buy', quantity: '1', price: '100', fee: '0', feeCurrency: 'USD',
      source: 'Statement', createdAtUtc: at, updatedAtUtc: at }
    const response = (version: number, result: string): ManualExecutions => ({ ...manualLedger({
      ...closed, summary: { ...closed.summary, version } }), records: [record],
      totals: { enteredQuantity: '1', exitedQuantity: '1', openQuantity: '0', grossResultUsd: result, netResultUsd: result, feesComplete: true } })
    const read = vi.fn(async () => response(3, '0'))
    const correct = vi.fn<NonNullable<WorkspaceApi['correctManualExecution']>>()
      .mockRejectedValueOnce(new ApiError('unavailable', 'Response lost.'))
      .mockResolvedValue(response(4, '6'))
    api.manualExecutions = read
    api.correctManualExecution = correct
    let current = sessionFor(closed)
    const user = userEvent.setup()
    const view = render(<Harness api={api} initial={current} onSession={next => { current = next }} manual />)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await user.click(await screen.findByRole('button', { name: `Correct ${otherId}` }))
    await user.type(screen.getByLabelText('Correction reason'), 'Statement amended')
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    await screen.findByText(/Response lost\./)
    const attempted = correct.mock.calls[0]
    read.mockResolvedValue(response(5, '8'))
    await user.click(screen.getByRole('button', { name: 'Reload manual ledger' }))
    await waitFor(() => expect(current.saved?.summary.version).toBe(5))
    expect(screen.getByText('Net realized USD').nextElementSibling).toHaveTextContent('8')
    if (mode !== 'same panel') {
      read.mockRejectedValue(new ApiError('unavailable', 'Current ledger unavailable.'))
      if (mode === 'remount with failed GET') {
        await user.click(screen.getByRole('tab', { name: 'Review' }))
        await user.click(screen.getByRole('tab', { name: 'Execution' }))
      } else {
        view.rerender(<Harness api={{ ...api }} initial={current} onSession={next => { current = next }} manual />)
      }
      await screen.findByText(/Current ledger unavailable\./)
    }
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    await waitFor(() => expect(current.manual?.executionId).toBeNull())
    expect(correct.mock.calls[1]).toEqual(attempted)
    expect(current.saved?.summary.version).toBe(5)
    if (mode === 'same panel') {
      expect(screen.getByText('Net realized USD').nextElementSibling).toHaveTextContent('8')
    } else {
      expect(screen.queryByLabelText('Manual execution totals')).not.toBeInTheDocument()
      expect(screen.getByRole('alert')).toHaveTextContent(/execution was already saved.*older than the latest known Play/i)
      read.mockResolvedValue(response(5, '8'))
      await user.click(screen.getByRole('button', { name: 'Reload manual ledger' }))
      await waitFor(() => expect(screen.getByText('Net realized USD').nextElementSibling).toHaveTextContent('8'))
      expect(correct).toHaveBeenCalledTimes(2)
    }
  })
  it('blocks discard during a pending save so the result cannot attach a saved ID to a blank draft', async () => {
    const { api } = client(savedPlayFixture)
    let finish!: (play: SavedPlay) => void
    api.createPlay = vi.fn(() => new Promise<SavedPlay>(resolve => { finish = resolve }))
    let current: PlaysSession = { ...createPlaysSession(), view: 'editor', draft: {
      ...createDraft(), accountId: accountFixture.id, title: 'Play A',
    } }
    const user = userEvent.setup()
    render(<Harness api={api} initial={current} onSession={next => { current = next }} />)
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    const discard = await screen.findByRole('button', { name: 'Discard changes' })
    expect(discard).toBeDisabled()
    await user.click(discard)
    expect(current.draft.title).toBe('Play A')
    await act(async () => finish({ ...savedPlayFixture, summary: { ...savedPlayFixture.summary, title: 'Play A' } }))
    expect(current.saved?.summary.id).toBe(savedPlayFixture.summary.id)
    expect(current.draft.accountId).toBe(accountFixture.id)
    expect(current.draft.title).toBe('Play A')
  })
  it('labels retrospective execution links as actual entries and exits, without an invented plan', async () => {
    const retrospective: SavedPlay = { ...terminal, plan: { ...terminal.plan, entries: [], leverage: '0' },
      summary: { ...terminal.summary, origin: 'retrospective', status: 'closed' } }
    const { api, methods } = client(retrospective)
    methods.playExecution.mockResolvedValue({ ...execution(retrospective), tracked: true, links: [
      { id: 'entry-link', role: 'entry', entryId: null, levelId: null, state: 'linked', source: 'owner', order: null,
        filledQuantity: '1', fills: [{ sourceFillId: 'entry-fill', direction: 'Open Long', price: '100', quantity: '1',
          fee: '0', feeToken: 'USDC', closedPnlUsd: '0', side: 'buy', positionEffect: 'open', pnlBasis: 'gross', occurredAtUtc: at }] },
      { id: 'exit-link', role: 'exit', entryId: null, levelId: null, state: 'linked', source: 'owner', order: null,
        filledQuantity: '1', fills: [{ sourceFillId: 'exit-fill', direction: 'Close Long', price: '108', quantity: '1',
          fee: '0', feeToken: 'USDC', closedPnlUsd: '8', side: 'sell', positionEffect: 'close', pnlBasis: 'gross', occurredAtUtc: at }] },
    ] })
    render(<Harness api={api} initial={sessionFor(retrospective)} />)
    await userEvent.click(screen.getByRole('tab', { name: 'Execution' }))
    const links = within(await screen.findByRole('region', { name: 'Linked orders' }))
    expect(links.getByText('Actual entry')).toBeInTheDocument()
    expect(links.getByText('Actual exit')).toBeInTheDocument()
    expect(links.getByText('Open Long 1 at 100')).toBeInTheDocument()
    expect(links.getByText('Close Long 1 at 108')).toBeInTheDocument()
    expect(screen.queryByText('Removed entry')).not.toBeInTheDocument()
    expect(screen.queryByText('Unplanned exit')).not.toBeInTheDocument()
    expect(screen.queryByText(/Orders link automatically by price/)).not.toBeInTheDocument()
  })

  // Phase 0, 0.1 (D-12): the Summary divides by the account balance the size uses, with a labelled manual-budget fallback.
  it.each([
    { quote: 'USD', manual: true, balance: '1000', label: 'Risk / Main account balance', share: '2%' },
    { quote: 'USDC', manual: false, balance: '1000', label: 'Risk / Main account balance · nominal USD/USDC at 1:1', share: '2%' },
    { quote: 'USDG', manual: false, balance: '1000', label: 'Risk / Main account balance · nominal USD/USDG at 1:1', share: '2%' },
    { quote: 'USDT0', manual: false, balance: '1000', label: 'Risk / Main account balance · nominal USD/USDT0 at 1:1', share: '2%' },
    { quote: 'EUR', manual: false, balance: '1000', label: 'Risk share', share: 'Unknown' },
    { quote: 'usdg', manual: false, balance: '1000', label: 'Risk share', share: 'Unknown' },
    { quote: 'USD', manual: true, balance: null, label: 'Risk / Explicit manual budget', share: '4%' },
  ])('uses an honest named risk reference for $quote, manual=$manual, balance=$balance', async ({ quote, manual, balance, label, share }) => {
    const venueId = manual ? 'manual' : 'hyperliquid'
    // Perps equity (account value) differs from the balance and is never the reference.
    const account = { ...accountFixture, venueId, accountValueUsd: '5000', balanceUsd: balance }
    const draft = { ...createDraft(), accountId: account.id, instrument: 'BTC',
      instrumentSource: manual ? 'manual' as const : 'venue' as const, sizingMode: 'quantity' as const,
      size: '2', leverage: '1', budgetOverride: '500' }
    draft.entries[0]!.price = '100'
    draft.entries[0]!.stops[0]!.value = '90'
    const api = { instruments: vi.fn(async () => ({ ...instrumentCatalogFixture,
      instruments: [{ ...instrumentCatalogFixture.instruments[0]!, quoteAsset: quote }] })),
      candles: () => new Promise(() => {}), marketContext: () => new Promise(() => {}), marketStream: idleMarketStream,
    } as unknown as WorkspaceApi
    const venues = systemFixture.venues.map(venue => ({ ...venue,
      capabilities: { ...venue.capabilities, manualExecutions: manual && venue.id === venueId } }))
    render(<VenuesProvider venues={venues}><PlayWorkspace accounts={[account]} portfolios={[]} api={api}
      draft={draft} onChange={vi.fn()} /></VenuesProvider>)
    const summary = within(screen.getByRole('group', { name: 'Planned position money summary' }))
    await waitFor(() => expect(summary.getByText(label).nextElementSibling).toHaveTextContent(share))
    if (manual) {
      expect(screen.getByRole('button', { name: 'About manual instruments' })).toHaveAccessibleDescription(/Manual plans assume a linear USD quote and one base unit/)
      expect(api.instruments).not.toHaveBeenCalled()
    } else {
      expect(api.instruments).toHaveBeenCalled()
    }
    if (quote === 'EUR' || quote === 'usdg') expect(summary.queryByText(/nominal USD/)).not.toBeInTheDocument()
  })

  it('writes the review beside the outcome and saves it from inside the journal (phase 1, 1.1a)', async () => {
    const closed: SavedPlay = { ...terminal, summary: { ...terminal.summary, status: 'closed', cancelReason: null } }
    const { api, methods } = client(closed)
    methods.playExecution.mockImplementation(() => Promise.resolve({ ...execution(closed), tracked: true, reason: null,
      totals: { ...execution(closed).totals, enteredQuantity: '0.5', exitedQuantity: '0.5', closedPnlUsd: '5', venueNetUsd: '4.97',
        fees: [{ token: 'USDC', amount: '0.03' }] },
      receipt: { firstEntryAtUtc: '2026-10-13T08:00:00Z', lastExitAtUtc: '2026-10-13T09:30:00Z', averageEntryPrice: '100.4', averageExitPrice: '110',
        fillCount: 3, estimatedR: '1.98', initialRisk: '2.51', estimatedRReason: null } }))
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(closed)} />)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    const outcome = await screen.findByLabelText('Outcome')
    for (const text of ['4.97 USD', '0.03 USDC', '+1.98R', '2.51 initial risk', '1h 30m']) expect(outcome).toHaveTextContent(text)
    // Nothing to save yet: the journal offers no Save of its own.
    expect(screen.queryByRole('button', { name: 'Save play' })).not.toBeInTheDocument()
    await user.type(screen.getByLabelText('Lesson'), 'Sized too small')
    await user.click(screen.getByRole('button', { name: 'Save play' }))
    await waitFor(() => expect(methods.updatePlay).toHaveBeenCalledOnce())
    expect(methods.updatePlay.mock.calls[0]![1].structuredReview?.lesson).toBe('Sized too small')
    // Saved: the journal's Save leaves, and focus stays in the journal instead of falling to the page.
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Save play' })).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Expand journal' })).toHaveFocus()
  })
  it('withholds the outcome when a reread fails instead of showing the earlier figures as current (phase 1, 1.1a)', async () => {
    const closed: SavedPlay = { ...terminal, summary: { ...terminal.summary, status: 'closed', cancelReason: null } }
    const { api, methods } = client(closed)
    methods.playExecution.mockImplementation(() => Promise.resolve({ ...execution(closed), tracked: true, reason: null,
      totals: { ...execution(closed).totals, venueNetUsd: '4.97' },
      receipt: { firstEntryAtUtc: '2026-10-13T08:00:00Z', lastExitAtUtc: '2026-10-13T09:30:00Z', averageEntryPrice: '100.4', averageExitPrice: '110',
        fillCount: 3, estimatedR: '1.98', initialRisk: '2.51', estimatedRReason: null } }))
    const user = userEvent.setup()
    const view = render(<Harness api={api} initial={sessionFor(closed)} />)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    expect(await screen.findByLabelText('Outcome')).toHaveTextContent('4.97 USD')
    // Newer facts arrived and their reread failed: the earlier net is no longer shown as the outcome.
    methods.playExecution.mockRejectedValue(new ApiError('unavailable', 'Execution unavailable.'))
    view.rerender(<Harness api={api} initial={sessionFor(closed)} generation={1} />)
    expect(await screen.findByText('The outcome could not be read again. Execution unavailable.')).toBeInTheDocument()
    expect(screen.queryByLabelText('Outcome')).not.toBeInTheDocument()
    expect(screen.queryByText('4.97 USD')).not.toBeInTheDocument()
  })
  it('compares entries with a newly saved plan revision, not the one before it (phase 1, 1.1a)', async () => {
    const { api, methods } = client(planned)
    const progress = (plannedQuantity: string) => ({ ...execution(planned), tracked: true, reason: null,
      entries: [{ entryId: 'entry-1', filledQuantity: '0', averageFillPrice: null, restingOrders: 0, plannedPrice: '100', plannedQuantity, planRevision: null }] })
    methods.playExecution.mockImplementation(() => Promise.resolve(progress('1')))
    methods.updatePlay.mockImplementation((_id, fields) => {
      methods.playExecution.mockImplementation(() => Promise.resolve(progress('2')))
      return Promise.resolve({ ...planned, plan: fields.plan, summary: { ...planned.summary, version: planned.summary.version + 1, planRevision: 2 } })
    })
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(planned)} />)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    expect(await screen.findByText('Filled 0 of 1')).toBeInTheDocument()
    await user.click(screen.getByRole('tab', { name: 'Thesis' }))
    await user.type(screen.getByRole('textbox', { name: 'Thesis' }), ' Doubled the size.')
    await user.click(screen.getByRole('button', { name: 'Save play' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByRole('textbox'), 'Doubled the size')
    await user.click(within(dialog).getByRole('button', { name: 'Save revision' }))
    await waitFor(() => expect(methods.updatePlay).toHaveBeenCalledOnce())
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    expect(await screen.findByText('Filled 0 of 2')).toBeInTheDocument()
    expect(screen.queryByText('Filled 0 of 1')).not.toBeInTheDocument()
  })
  it('keeps focus in the journal after a save that asked for a revision reason', async () => {
    const { api, methods } = client(planned)
    methods.updatePlay.mockImplementation((_id, fields) => Promise.resolve({ ...planned, plan: fields.plan,
      summary: { ...planned.summary, version: planned.summary.version + 1, planRevision: 2 } }))
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(planned)} />)
    await user.type(screen.getByRole('textbox', { name: 'Thesis' }), ' Retest held.')
    await user.click(screen.getByRole('button', { name: 'Save play' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByRole('textbox'), 'Clarified the thesis')
    await user.click(within(dialog).getByRole('button', { name: 'Save revision' }))
    await waitFor(() => expect(methods.updatePlay).toHaveBeenCalledOnce())
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Save play' })).not.toBeInTheDocument())
    await waitFor(() => expect(screen.getByRole('button', { name: 'Expand journal' })).toHaveFocus())
  })
  it('leaves focus alone when the journal save was dismissed and the header saved instead', async () => {
    const { api, methods } = client(planned)
    methods.updatePlay.mockImplementation((_id, fields) => Promise.resolve({ ...planned, plan: fields.plan,
      summary: { ...planned.summary, version: planned.summary.version + 1, planRevision: 2 } }))
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(planned)} />)
    await user.type(screen.getByRole('textbox', { name: 'Thesis' }), ' Retest held.')
    await user.click(screen.getByRole('button', { name: 'Save play' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Keep editing' }))
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByRole('textbox'), 'Clarified the thesis')
    await user.click(within(dialog).getByRole('button', { name: 'Save revision' }))
    await waitFor(() => expect(methods.updatePlay).toHaveBeenCalledOnce())
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Save play' })).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Expand journal' })).not.toHaveFocus()
  })
  it('marks a plan written after the first execution in the header and above the review (D-17)', async () => {
    const closed: SavedPlay = { ...terminal, summary: { ...terminal.summary, status: 'closed', cancelReason: null } }
    const { api, methods } = client(closed)
    const receipt = { firstEntryAtUtc: '2026-10-10T08:00:00Z', lastExitAtUtc: '2026-10-10T09:00:00Z', averageEntryPrice: '100',
      averageExitPrice: '110', fillCount: 2, estimatedR: null, initialRisk: null,
      estimatedRReason: 'No plan revision was in force at the first entry fill: the plan was written after it.' }
    methods.playExecution.mockImplementation(() => Promise.resolve({ ...execution(closed), receipt: { ...receipt, plannedAfterExecution: true } }))
    const user = userEvent.setup()
    const view = render(<Harness api={api} initial={sessionFor(closed)} />)
    const status = screen.getByText('Status').closest('div')!
    expect(await within(status).findByText('Plan written after first execution')).toBeInTheDocument()
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    expect(within(screen.getByRole('tabpanel')).getByText('Plan written after first execution')).toBeInTheDocument()
    view.unmount()
    methods.playExecution.mockImplementation(() => Promise.resolve({ ...execution(closed), receipt }))
    render(<Harness api={api} initial={sessionFor(closed)} />)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    // The receipt has arrived, without the mark.
    expect(await screen.findByLabelText('Outcome')).toBeInTheDocument()
    expect(screen.queryByText('Plan written after first execution')).not.toBeInTheDocument()
  })
  it('completes a review explicitly, with unchanged text, and reopens it (phase 1, 1.2)', async () => {
    const closed: SavedPlay = { ...terminal, summary: { ...terminal.summary, status: 'closed', cancelReason: null, reviewCompletion: null, reviewCompletedAtUtc: null } }
    const { api, methods } = client(closed)
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(closed)} />)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    expect(screen.getByRole('textbox', { name: 'Review' })).toHaveValue('Existing free text')
    await user.click(screen.getByRole('button', { name: 'Complete review' }))
    expect(methods.completeReview).toHaveBeenCalledWith(closed.summary.id, closed.summary.version)
    expect(await screen.findByRole('button', { name: 'Review again' })).toBeInTheDocument()
    expect(methods.updatePlay).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Review again' }))
    expect(methods.reopenReview).toHaveBeenCalledWith(closed.summary.id, closed.summary.version + 1)
    expect(await screen.findByRole('button', { name: 'Complete review' })).toBeInTheDocument()
    // The in-trade log is its own tab.
    await user.click(screen.getByRole('tab', { name: 'Log' }))
    expect(await screen.findByText('No entries yet.')).toBeInTheDocument()
    expect(methods.playNotes).toHaveBeenCalledWith(closed.summary.id, expect.any(AbortSignal))
  })
  it('keeps an unfinished log entry across journal tabs and the expanded journal (phase 1, 1.2)', async () => {
    const open: SavedPlay = { ...planned, summary: { ...planned.summary, status: 'open' } }
    const { api } = client(open)
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(open)} />)
    await user.click(screen.getByRole('tab', { name: 'Log' }))
    await user.type(await screen.findByLabelText('Log entry'), 'Adding into strength')
    await user.selectOptions(screen.getByLabelText(/^State/), 'fomo')
    await user.click(screen.getByRole('tab', { name: 'Thesis' }))
    await user.click(screen.getByRole('tab', { name: 'Log' }))
    expect(screen.getByLabelText('Log entry')).toHaveValue('Adding into strength')
    expect(screen.getByLabelText(/^State/)).toHaveValue('fomo')
    await user.click(screen.getByRole('button', { name: 'Expand journal' }))
    expect(within(await screen.findByRole('dialog')).getByLabelText('Log entry')).toHaveValue('Adding into strength')
  })
  it('keeps an unfinished log entry when another Play is requested (phase 1, 1.2)', async () => {
    const open: SavedPlay = { ...planned, summary: { ...planned.summary, status: 'open' } }
    const { api, methods } = client(open)
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(open)} />)
    await user.click(screen.getByRole('tab', { name: 'Log' }))
    await user.type(await screen.findByLabelText('Log entry'), 'Adding into strength')
    await user.click(screen.getByRole('button', { name: 'Request another Play' }))
    expect(await screen.findByText(/Your current work is kept/)).toBeInTheDocument()
    expect(methods.play).not.toHaveBeenCalledWith(otherId, expect.anything())
    expect(screen.getByLabelText('Log entry')).toHaveValue('Adding into strength')
  })
  it('applies a write from a page that has gone to the current session, keeping newer text and showing the entry (phase 1, 1.2)', async () => {
    const open: SavedPlay = { ...planned, summary: { ...planned.summary, status: 'open' } }
    const { api, methods } = client(open)
    let finish!: (notes: PlayNote[]) => void
    let stored: PlayNote[] = []
    methods.playNotes.mockImplementation(() => Promise.resolve(stored))
    methods.addPlayNote.mockImplementation(() => new Promise<PlayNote[]>(resolve => { finish = resolve }))
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(open)} />)
    await user.click(screen.getByRole('tab', { name: 'Log' }))
    await user.type(await screen.findByLabelText('Log entry'), 'Adding into strength')
    await user.click(screen.getByRole('button', { name: 'Add to log' }))
    // The whole page goes and comes back while the write is out; the owner keeps typing in the new one.
    await user.click(screen.getByRole('button', { name: 'Toggle Plays page' }))
    await user.click(screen.getByRole('button', { name: 'Toggle Plays page' }))
    await user.click(await screen.findByRole('tab', { name: 'Log' }))
    const entry = await screen.findByLabelText('Log entry')
    await user.clear(entry)
    await user.type(entry, 'A newer thought')
    const written: PlayNote = { id: '00000000-0000-4000-8000-0000000000c1', text: 'Adding into strength', state: null, createdAtUtc: at }
    stored = [written]
    await act(async () => finish([written]))
    expect(screen.getByLabelText('Log entry')).toHaveValue('A newer thought')
    // The current page reads the list again and shows the entry the gone page wrote.
    expect(await screen.findByText('Adding into strength')).toBeInTheDocument()
  })
  it('applies a gone page\'s acknowledgement through the mounted page, so an edit before it renders keeps it (phase 1, 1.2)', async () => {
    const open: SavedPlay = { ...planned, summary: { ...planned.summary, status: 'open' } }
    const { api, methods } = client(open)
    let finish!: (notes: PlayNote[]) => void
    methods.addPlayNote.mockImplementation(() => new Promise<PlayNote[]>(resolve => { finish = resolve }))
    const sessions = vi.fn()
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(open)} onSession={sessions} />)
    await user.click(screen.getByRole('tab', { name: 'Log' }))
    await user.type(await screen.findByLabelText('Log entry'), 'Adding into strength')
    await user.click(screen.getByRole('button', { name: 'Add to log' }))
    await user.click(screen.getByRole('button', { name: 'Toggle Plays page' }))
    await user.click(screen.getByRole('button', { name: 'Toggle Plays page' }))
    await user.click(await screen.findByRole('tab', { name: 'Log' }))
    const entry = await screen.findByLabelText('Log entry')
    const written: PlayNote = { id: '00000000-0000-4000-8000-0000000000c1', text: 'Adding into strength', state: null, createdAtUtc: at }
    // The write lands, and the owner clears the field before the page renders the acknowledgement.
    await act(async () => {
      finish([written])
      await new Promise(resolve => setTimeout(resolve, 0))
      fireEvent.change(entry, { target: { value: '' } })
    })
    const last = sessions.mock.lastCall![0] as PlaysSession
    expect(last.log?.attempt).toBeUndefined()
    expect(hasUnsavedPlayWork(last)).toBe(false)
  })
  it('offers the log on a retrospective Play too (phase 1, 1.2)', async () => {
    const { api } = client(retrospectiveSavedFixture)
    render(<Harness api={api} initial={sessionFor(retrospectiveSavedFixture)} />)
    expect(await screen.findByRole('tab', { name: 'Log' })).toBeInTheDocument()
  })
  it('saves from the expanded journal too, keeping focus in the dialog', async () => {
    const closed: SavedPlay = { ...terminal, summary: { ...terminal.summary, status: 'closed', cancelReason: null } }
    const { api, methods } = client(closed)
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(closed)} />)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByLabelText('Lesson'), 'Wait for the retest')
    await user.click(screen.getByRole('button', { name: 'Expand journal' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Save play' }))
    await waitFor(() => expect(methods.updatePlay).toHaveBeenCalledOnce())
    await waitFor(() => expect(within(dialog).queryByRole('button', { name: 'Save play' })).not.toBeInTheDocument())
    expect(within(dialog).getByRole('button', { name: 'Close expanded journal' })).toHaveFocus()
  })
  it('preserves free text, structured edits during save, and reports a stale rejected save without clearing dirty work', async () => {
    const { api, methods } = client()
    let complete!: (play: SavedPlay) => void
    methods.updatePlay.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
      .mockRejectedValueOnce(new ApiError('http', 'Reload: this Play changed.', 409))
    const seen = vi.fn()
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(terminal)} onSession={seen} />)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    expect(screen.getByRole('textbox', { name: 'Review' })).toHaveValue('Existing free text')
    await user.type(screen.getByLabelText('Lesson'), 'First lesson')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await user.type(screen.getByLabelText('Lesson'), ' extended')
    const sent = methods.updatePlay.mock.calls[0]![1]
    expect(sent.review).toBe('Existing free text')
    await act(async () => complete({ ...terminal, structuredReview: sent.structuredReview,
      summary: { ...terminal.summary, hasReview: true, reviewedAtUtc: at, version: 3 } } as SavedPlay))
    expect(screen.getByLabelText('Lesson')).toHaveValue('First lesson extended')
    expect(screen.getByRole('button', { name: /^Save$/ })).toBeEnabled()
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await screen.findByText('Reload: this Play changed.')
    expect(screen.getByLabelText('Lesson')).toHaveValue('First lesson extended')
    expect(seen.mock.lastCall![0].saved.summary.version).toBe(3)
  })
  it('shows a retrospective review without compatibility entries, leverage, thesis or sizing and saves it without plan checks', async () => {
    const retro: SavedPlay = { ...terminal, plan: { ...terminal.plan, entries: [], leverage: '0', size: '' },
      summary: { ...terminal.summary, origin: 'retrospective', status: 'closed', planRevision: 0 } }
    const { api, methods } = client(retro)
    const user = userEvent.setup()
    render(<Harness api={api} initial={sessionFor(retro)} />)
    expect(screen.getByRole('tab', { name: 'Review' })).toHaveAttribute('data-state', 'active')
    expect(screen.queryByRole('tab', { name: 'Thesis' })).not.toBeInTheDocument()
    expect(screen.queryByRole('spinbutton', { name: /Leverage/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Planned position money summary' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Plan adherence')).not.toHaveTextContent('Followed')
    await user.type(screen.getByLabelText('Lesson'), 'After the trade')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await waitFor(() => expect(methods.updatePlay).toHaveBeenCalledOnce())
    expect(methods.updatePlay.mock.calls[0]![1].plan.entries).toEqual([])
  })
  it('filters terminal Cancelled and retrospective Plays by saved review state, including reviewedAtUtc', async () => {
    const pending = { ...terminal, review: '' }
    const reviewed: SavedPlay = { ...terminal, summary: { ...terminal.summary, id: otherId, title: 'Imported review', origin: 'retrospective',
      status: 'closed', hasReview: false, reviewedAtUtc: at } }
    const { api } = client(pending, [reviewed])
    render(<Harness api={api} />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('tab', { name: /^Needs review/ }))
    expect(await screen.findByRole('button', { name: `Open ${pending.summary.title}` })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Open Imported review' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('tab', { name: /^Reviewed/ }))
    expect(await screen.findByRole('button', { name: 'Open Imported review' })).toHaveTextContent('Retrospective')
  })
  it.each([false, true])('refreshes the mounted review queue after saving, with later dirty edits=%s', async edited => {
    const pending = { ...terminal, review: '' }
    const { api, methods } = client(pending)
    const saving = deferred<SavedPlay>()
    methods.updatePlay.mockImplementationOnce(() => saving.promise)
    const seen = vi.fn()
    render(<Harness api={api} initial={sessionFor(pending)} onSession={seen} />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByLabelText('Lesson'), 'First lesson')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    if (edited) await user.type(screen.getByLabelText('Lesson'), ' extended')
    const sent = methods.updatePlay.mock.calls[0]![1]
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    await user.click(screen.getByRole('tab', { name: /^Needs review/ }))
    expect(await screen.findByRole('button', { name: `Open ${pending.summary.title}` })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Discard changes' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'New play' })).toBeDisabled()
    expect(reads(methods, 'review')).toBe(1)
    const reviewed = { ...pending, structuredReview: sent.structuredReview ?? emptyStructuredReview(),
      summary: { ...pending.summary, version: 3, hasReview: true, reviewedAtUtc: at } }
    serve(methods, [reviewed.summary])
    await act(async () => saving.resolve(reviewed))
    await waitFor(() => expect(reads(methods, 'review')).toBe(2))
    await waitFor(() => expect(screen.queryByRole('button', { name: `Open ${pending.summary.title}` })).not.toBeInTheDocument())
    await user.click(screen.getByRole('tab', { name: /^Reviewed/ }))
    expect(await screen.findByRole('button', { name: `Open ${pending.summary.title}` })).toHaveTextContent('Reviewed')
    expect(seen.mock.lastCall![0].saved.summary.version).toBe(3)
    expect(methods.updatePlay).toHaveBeenCalledOnce()
    if (edited) {
      expect(screen.getByRole('button', { name: 'Discard changes' })).toBeEnabled()
      expect(screen.getByRole('button', { name: `Open ${pending.summary.title}` })).toBeDisabled()
      await user.click(screen.getByRole('button', { name: 'Continue editing' }))
      await user.click(screen.getByRole('tab', { name: 'Review' }))
      expect(screen.getByLabelText('Lesson')).toHaveValue('First lesson extended')
      expect(screen.getByRole('button', { name: /^Save$/ })).toBeEnabled()
    } else {
      expect(screen.getByRole('button', { name: `Open ${pending.summary.title}` })).toBeEnabled()
      expect(screen.queryByRole('button', { name: 'Discard changes' })).not.toBeInTheDocument()
    }
  })
  it.each(['resolve', 'reject'] as const)('ignores an aborted list read that later %ss after a review save', async outcome => {
    const pending = { ...terminal, review: '' }
    const { api, methods } = client(pending)
    const saving = deferred<SavedPlay>()
    const previous = deferred<PlayList>()
    methods.updatePlay.mockImplementationOnce(() => saving.promise)
    methods.plays.mockImplementationOnce(() => previous.promise)
    render(<Harness api={api} initial={sessionFor(pending)} />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByLabelText('Lesson'), 'First lesson')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    const sent = methods.updatePlay.mock.calls[0]![1]
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    await waitFor(() => expect(reads(methods)).toBe(1))
    const signal = methods.plays.mock.calls[0]![1]!
    const reviewed = { ...pending, structuredReview: sent.structuredReview ?? emptyStructuredReview(),
      summary: { ...pending.summary, version: 3, hasReview: true, reviewedAtUtc: at } }
    serve(methods, [reviewed.summary])
    await act(async () => saving.resolve(reviewed))
    await user.click(screen.getByRole('tab', { name: /^Reviewed/ }))
    expect(await screen.findByRole('button', { name: `Open ${pending.summary.title}` })).toHaveTextContent('Reviewed')
    expect(signal.aborted).toBe(true)
    await act(async () => {
      if (outcome === 'resolve') previous.resolve(playListPage([pending.summary], { view: 'progress' }))
      else previous.reject(new ApiError('unavailable', 'Superseded read failed.'))
    })
    expect(screen.getByRole('button', { name: `Open ${pending.summary.title}` })).toHaveTextContent('Reviewed')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    await user.click(screen.getByRole('tab', { name: /^Needs review/ }))
    await waitFor(() => expect(screen.queryByRole('button', { name: `Open ${pending.summary.title}` })).not.toBeInTheDocument())
  })
  it('refreshes the mounted queue after an older correction replay without advancing the observed version', async () => {
    const closed: SavedPlay = { ...terminal, review: '', summary: { ...terminal.summary, status: 'closed', version: 3 } }
    const record: ManualExecutions['records'][number] = { id: otherId, role: 'entry', entryId: 'entry-1',
      occurredAtUtc: at, side: 'buy', quantity: '1', price: '100', fee: '0', feeCurrency: 'USD',
      source: 'Statement', createdAtUtc: at, updatedAtUtc: at }
    const exit = { ...record, id: '00000000-0000-4000-8000-000000000003', role: 'exit' as const, entryId: null,
      occurredAtUtc: '2026-10-13T10:01:00Z', side: 'sell' as const, price: '101' }
    const ledger = (version: number): ManualExecutions => ({ ...manualLedger({ ...closed, summary: { ...closed.summary, version } }),
      records: [record, exit], totals: { enteredQuantity: '1', exitedQuantity: '1', openQuantity: '0',
        grossResultUsd: '1', netResultUsd: '1', feesComplete: true } })
    const { api, methods } = client(closed)
    const read = vi.fn(async () => ledger(3))
    const replay = deferred<ManualExecutions>()
    const correct = vi.fn<NonNullable<WorkspaceApi['correctManualExecution']>>()
      .mockRejectedValueOnce(new ApiError('unavailable', 'Response lost.'))
      .mockImplementationOnce(() => replay.promise)
    api.manualExecutions = read
    api.correctManualExecution = correct
    let current = sessionFor(closed)
    render(<Harness api={api} initial={current} onSession={next => { current = next }} manual />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await user.click(await screen.findByRole('button', { name: `Correct ${otherId}` }))
    await user.type(screen.getByLabelText('Correction reason'), 'Statement amended')
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    await screen.findByText(/Response lost\./)
    read.mockResolvedValue(ledger(5))
    await user.click(screen.getByRole('button', { name: 'Reload manual ledger' }))
    await waitFor(() => expect(current.observedPlay?.version).toBe(5))
    serve(methods, [ledger(5).play.summary])
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    expect(correct.mock.calls[1]).toEqual(correct.mock.calls[0])
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    await user.click(screen.getByRole('tab', { name: /^Needs review/ }))
    expect(await screen.findByRole('button', { name: `Open ${closed.summary.title}` })).toBeDisabled()
    // A concurrent review completes after the queue read; the retry still returns its cached v4 document.
    const reviewed = { ...closed.summary, version: 6, hasReview: true, reviewedAtUtc: at }
    serve(methods, [reviewed])
    await act(async () => replay.resolve(ledger(4)))
    expect(current.saved?.summary.version).toBe(5)
    expect(current.observedPlay?.version).toBe(5)
    expect(current.manual?.executionId).toBeNull()
    await waitFor(() => expect(reads(methods, 'review')).toBe(2))
    await waitFor(() => expect(screen.queryByRole('button', { name: `Open ${closed.summary.title}` })).not.toBeInTheDocument())
    await user.click(screen.getByRole('tab', { name: /^Reviewed/ }))
    expect(await screen.findByRole('button', { name: `Open ${closed.summary.title}` })).toBeEnabled()
  })
  it('refetches after a lost review response without inferring success or clearing dirty work', async () => {
    const pending = { ...terminal, review: '' }
    const { api, methods } = client(pending)
    const saving = deferred<SavedPlay>()
    methods.updatePlay.mockImplementationOnce(() => saving.promise)
    let current = sessionFor(pending)
    render(<Harness api={api} initial={current} onSession={next => { current = next }} />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByLabelText('Lesson'), 'Unconfirmed lesson')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    await user.click(screen.getByRole('tab', { name: /^Needs review/ }))
    await screen.findByRole('button', { name: `Open ${pending.summary.title}` })
    // The server committed, but no saved document reaches the editor.
    serve(methods, [{ ...pending.summary, version: 3, hasReview: true, reviewedAtUtc: at }])
    await act(async () => saving.reject(new ApiError('unavailable', 'Review response lost.')))
    await waitFor(() => expect(reads(methods, 'review')).toBe(2))
    expect(current.saved?.summary.version).toBe(2)
    expect(current.draft.structuredReview?.lesson).toBe('Unconfirmed lesson')
    await waitFor(() => expect(screen.queryByRole('button', { name: `Open ${pending.summary.title}` })).not.toBeInTheDocument())
    await user.click(screen.getByRole('tab', { name: /^Reviewed/ }))
    expect(await screen.findByRole('button', { name: `Open ${pending.summary.title}` })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Discard changes' })).toBeEnabled()
  })
  it('keeps a failed review save in the mounted pending queue with its dirty edits', async () => {
    const pending = { ...terminal, review: '' }
    const { api, methods } = client(pending)
    const saving = deferred<SavedPlay>()
    methods.updatePlay.mockImplementationOnce(() => saving.promise)
    render(<Harness api={api} initial={sessionFor(pending)} />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByLabelText('Lesson'), 'Unsaved lesson')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    await user.click(screen.getByRole('tab', { name: /^Needs review/ }))
    await screen.findByRole('button', { name: `Open ${pending.summary.title}` })
    await act(async () => saving.reject(new ApiError('http', 'Review save failed.', 409)))
    expect(screen.getByRole('button', { name: `Open ${pending.summary.title}` })).toHaveTextContent('Pending review')
    await waitFor(() => expect(reads(methods, 'review')).toBe(2))
    await user.click(screen.getByRole('tab', { name: /^Reviewed/ }))
    await waitFor(() => expect(reads(methods, 'reviewed')).toBe(1))
    await waitFor(() => expect(screen.queryByRole('button', { name: `Open ${pending.summary.title}` })).not.toBeInTheDocument())
    await user.click(screen.getByRole('button', { name: 'Continue editing' }))
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    expect(screen.getByLabelText('Lesson')).toHaveValue('Unsaved lesson')
    expect(screen.getByRole('button', { name: /^Save$/ })).toBeEnabled()
  })
  it.each(['saved', 'unsaved'] as const)('retains dirty %s work when another screen requests a Play', async kind => {
    const { api, methods } = client()
    const initial = kind === 'saved' ? sessionFor(terminal) : { ...createPlaysSession(), view: 'editor' as const,
      draft: { ...createDraft(), title: 'Unsaved idea' } }
    if (kind === 'saved') initial.draft.notes.review += ' local edit'
    const handled = vi.fn()
    render(<Harness api={api} initial={initial} onHandled={handled} />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Request another Play' }))
    expect(screen.getByText(/A Play was requested/)).toBeInTheDocument()
    expect(methods.play).not.toHaveBeenCalled()
    expect(handled).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Keep current work' }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue(initial.draft.title)
  })
  it('passes an AbortSignal and consumes a request only after a successful load', async () => {
    const { api, methods } = client()
    let finish!: (play: SavedPlay) => void
    methods.play.mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const handled = vi.fn()
    const view = render(<Harness api={api} request={otherId} onHandled={handled} />)
    await waitFor(() => expect(methods.play).toHaveBeenCalledWith(otherId, expect.any(AbortSignal)))
    expect(handled).not.toHaveBeenCalled()
    const signal = methods.play.mock.calls[0]![1]!
    view.unmount()
    expect(signal.aborted).toBe(true)
    await act(async () => finish(terminal))
    expect(handled).not.toHaveBeenCalled()
  })
  it('leaves a failed requested load visible and consumes it only after a successful retry', async () => {
    const { api, methods } = client()
    const target = { ...terminal, summary: { ...terminal.summary, id: otherId, title: 'Requested completed Play' } }
    methods.play.mockRejectedValueOnce(new ApiError('http', 'Requested Play unavailable.', 404)).mockResolvedValue(target)
    const handled = vi.fn()
    render(<Harness api={api} request={otherId} onHandled={handled} />)
    await screen.findByRole('button', { name: 'Retry requested Play' })
    expect(handled).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Retry requested Play' }))
    await waitFor(() => expect(handled).toHaveBeenCalledOnce())
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Requested completed Play')
  })
  it('shows review revision history independently of frozen plan revisions', async () => {
    const { api } = client()
    api.playHistory = vi.fn(async () => ({
      revisions: [], statusChanges: [], reviews: [{ number: 1, occurredAtUtc: at, review: 'Old free text',
        structuredReview: { adherence: 'not-applicable' as const, mistakeTag: null, lesson: 'Kept evidence', nextAction: 'Wait' } }],
    }))
    render(<Harness api={api} initial={sessionFor(terminal)} />)
    await userEvent.click(screen.getByRole('button', { name: 'History' }))
    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText('Review revision 1')).toBeInTheDocument()
    expect(within(dialog).getByText('Lesson: Kept evidence')).toBeInTheDocument()
    expect(within(dialog).queryByText(/Plan revision 1/)).not.toBeInTheDocument()
  })
  it('keeps edits made during requested loading and exposes a retry instead of replacing them', async () => {
    const { api, methods } = client()
    let finish!: (play: SavedPlay) => void
    methods.play.mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const initial = sessionFor(savedPlayFixture)
    render(<Harness api={api} initial={initial} />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Request another Play' }))
    await user.type(screen.getByRole('textbox', { name: 'Play title' }), ' changed while loading')
    await act(async () => finish({ ...terminal, summary: { ...terminal.summary, id: otherId } }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue(`${savedPlayFixture.summary.title} changed while loading`)
    expect(screen.getByText(/Your current work is kept/)).toBeInTheDocument()
  })
  it('gates manual recording by explicit capability and requires save then plan', async () => {
    const { api } = client()
    const user = userEvent.setup()
    const view = render(<Harness api={api} initial={sessionFor(savedPlayFixture)} />)
    expect(screen.queryByRole('tab', { name: 'Execution' })).not.toBeInTheDocument()
    view.unmount()
    render(<Harness api={api} initial={sessionFor(savedPlayFixture)} manual />)
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    expect(within(screen.getByRole('tabpanel')).getByText(/Save the Play, then plan/)).toBeInTheDocument()
  })
})


const shellPlay: SavedPlay = { ...planned, summary: { ...planned.summary, venueId: 'manual', instrumentSource: 'manual' } }
const shellAccount = { ...accountFixture, venueId: 'manual' }
const shellSystem = { ...systemFixture, venues: systemFixture.venues.map(venue => venue.id === 'manual'
  ? { ...venue, capabilities: { ...venue.capabilities, manualExecutions: true } } : venue) }
function shellClient() {
  const { api, methods } = client(shellPlay)
  return { ...api, ...playApiStubs(), ...methods,
    overview: vi.fn(async () => ({ ...overviewFixture, accounts: [shellAccount] })),
    manualExecutions: vi.fn(async () => manualLedger(shellPlay)),
    addManualExecution: vi.fn<NonNullable<WorkspaceApi['addManualExecution']>>(),
    correctManualExecution: vi.fn<NonNullable<WorkspaceApi['correctManualExecution']>>(),
    changePlayStatus: vi.fn<WorkspaceApi['changePlayStatus']>(),
  }
}
async function enterShellManual(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: `Open ${shellPlay.summary.title}` }))
  await user.click(screen.getByRole('tab', { name: 'Execution' }))
  await screen.findByLabelText('Manual execution totals')
  await user.selectOptions(screen.getByLabelText('Saved plan entry'), 'entry-1')
  fireEvent.change(screen.getByLabelText('Execution time (UTC)'), { target: { value: '2026-10-13T10:00:00' } })
  await user.selectOptions(screen.getByLabelText('Side'), 'buy')
  await user.type(screen.getByLabelText('Execution quantity'), '1')
  await user.type(screen.getByLabelText('Execution price'), '100')
  await user.type(screen.getByLabelText('Source label'), 'Statement')
}
const openedLedger = (): ManualExecutions => {
  const after: ManualExecutions['records'][number] = { id: otherId, role: 'entry', entryId: 'entry-1',
    occurredAtUtc: at, side: 'buy', quantity: '1', price: '100', fee: null, feeCurrency: null,
    source: 'Statement', createdAtUtc: at, updatedAtUtc: at }
  return { ...manualLedger({ ...shellPlay, summary: { ...shellPlay.summary, status: 'open', version: 3 } }),
    records: [after], history: [{ id: otherId, executionId: after.id, before: null, after, reason: null, occurredAtUtc: at }],
    totals: { enteredQuantity: '1', exitedQuantity: '0', openQuantity: '1', grossResultUsd: '0', netResultUsd: null, feesComplete: false } }
}

describe('diary mutation ownership in the actual shell', () => {
  function warnsOnUnload() {
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    return event.defaultPrevented
  }
  it('guards a retained Overview action synchronously before the pending Save render', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('Unexpected network request'))))
    window.history.replaceState(null, '', '/#plays')
    let newPlayFromOverview!: () => void
    const OverviewActions = overviewActions.OverviewActions
    // Retain the public action while rendering the real controls, without accessing React internals.
    vi.spyOn(overviewActions, 'OverviewActions').mockImplementation(props => {
      newPlayFromOverview = props.onNewPlay
      return <OverviewActions {...props} />
    })
    const api = shellClient()
    const saving = deferred<SavedPlay>()
    const user = userEvent.setup()
    render(<ApplicationShell system={shellSystem} disconnect={vi.fn()} api={api} />)
    await enterShellManual(user)
    await user.click(screen.getByRole('button', { name: 'Discard execution edit' }))
    await user.click(screen.getByRole('link', { name: 'Overview' }))
    await screen.findByRole('button', { name: 'New play' })
    await user.click(screen.getByRole('link', { name: 'Plays' }))
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByRole('textbox', { name: 'Review' }), ' sent while saving')
    api.updatePlay.mockImplementation(() => {
      expect(screen.getByRole('button', { name: /^Save$/ })).toBeEnabled()
      expect(warnsOnUnload()).toBe(true)
      newPlayFromOverview()
      return saving.promise
    })
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    expect(api.updatePlay).toHaveBeenCalledOnce()
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue(shellPlay.summary.title)
    expect(screen.getByRole('button', { name: 'Saving…' })).toBeDisabled()
    expect(screen.getByText(/A Play change is still pending/)).toBeInTheDocument()
    const sent = api.updatePlay.mock.calls[0]![1]
    await act(async () => saving.resolve({ ...shellPlay, review: sent.review, summary: { ...shellPlay.summary, version: 3 } }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue(shellPlay.summary.title)
    expect(screen.getByRole('button', { name: 'Saved' })).toBeDisabled()
    expect(warnsOnUnload()).toBe(false)
    await user.click(screen.getByRole('link', { name: 'Overview' }))
    await user.click(screen.getByRole('button', { name: 'New play' }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('')
    expect(screen.queryByRole('button', { name: 'Pause' })).not.toBeInTheDocument()
  })
  it('preserves a clean-again Play when Overview New play is used during its pending save', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('Unexpected network request'))))
    window.history.replaceState(null, '', '/#plays')
    const api = shellClient()
    const saving = deferred<SavedPlay>()
    api.updatePlay.mockImplementation(() => saving.promise)
    const user = userEvent.setup()
    render(<ApplicationShell system={shellSystem} disconnect={vi.fn()} api={api} />)
    await enterShellManual(user)
    await user.click(screen.getByRole('button', { name: 'Discard execution edit' }))
    expect(warnsOnUnload()).toBe(false)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    await user.type(screen.getByRole('textbox', { name: 'Review' }), ' sent while saving')
    await user.click(screen.getByRole('button', { name: /^Save$/ }))
    const sent = api.updatePlay.mock.calls[0]![1]
    expect(sent.expectedVersion).toBe(2)
    await user.clear(screen.getByRole('textbox', { name: 'Review' }))
    await user.type(screen.getByRole('textbox', { name: 'Review' }), shellPlay.review)
    // The draft now matches its saved baseline, but the real save still owns the work ref.
    expect(warnsOnUnload()).toBe(true)
    await user.click(screen.getByRole('link', { name: 'Overview' }))
    await user.click(await screen.findByRole('button', { name: 'New play' }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue(shellPlay.summary.title)
    await act(async () => saving.resolve({ ...shellPlay, review: sent.review, summary: { ...shellPlay.summary, version: 3 } }))
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue(shellPlay.summary.title)
    await user.click(screen.getByRole('tab', { name: 'Review' }))
    expect(screen.getByRole('textbox', { name: 'Review' })).toHaveValue(shellPlay.review)
    expect(screen.getByRole('button', { name: /^Save$/ })).toBeEnabled()
    expect(warnsOnUnload()).toBe(true)
    // The page reads the ledger for the Summary's fills as well; the shown tab reads it again for its own records.
    const reads = api.manualExecutions.mock.calls.length
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await waitFor(() => expect(api.manualExecutions.mock.calls.length).toBeGreaterThan(reads))
    expect(api.manualExecutions).toHaveBeenLastCalledWith(shellPlay.summary.id, expect.any(AbortSignal))
    expect(screen.getByRole('button', { name: 'Pause' })).toBeDisabled()
    expect(api.updatePlay).toHaveBeenCalledOnce()
  })
  it.each(['links', 'hash'] as const)('retains pending manual ownership, blocks discard, and accepts its result across %s navigation', async navigation => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('Unexpected network request'))))
    window.history.replaceState(null, '', '/#plays')
    const api = shellClient()
    const recording = deferred<ManualExecutions>()
    api.addManualExecution.mockImplementation(() => recording.promise)
    const user = userEvent.setup()
    render(<ApplicationShell system={shellSystem} disconnect={vi.fn()} api={api} />)
    await enterShellManual(user)
    await user.click(screen.getByRole('button', { name: 'Record execution' }))
    const attempted = api.addManualExecution.mock.calls[0]
    const navigate = async (page: 'Settings' | 'Plays') => {
      if (navigation === 'links') await user.click(screen.getByRole('link', { name: page }))
      else act(() => {
        window.history.replaceState(null, '', `/#${page.toLowerCase()}`)
        window.dispatchEvent(new HashChangeEvent('hashchange'))
      })
    }
    await navigate('Settings')
    expect(screen.queryByRole('tab', { name: 'Execution' })).not.toBeInTheDocument()
    await navigate('Plays')
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    expect(screen.getByRole('button', { name: 'Retry same execution' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Pause' })).toBeDisabled()
    expect(screen.getByLabelText('Execution quantity')).toHaveValue('1')
    fireEvent.submit(screen.getByRole('form', { name: 'Record manual execution' }))
    await user.click(screen.getByRole('button', { name: 'All plays' }))
    expect(await screen.findByRole('button', { name: 'Discard changes' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }))
    expect(api.addManualExecution).toHaveBeenCalledOnce()
    expect(attempted?.[1]).toMatchObject({ expectedVersion: 2, quantity: '1', price: '100' })
    api.manualExecutions.mockResolvedValue(openedLedger())
    api.play.mockResolvedValue(openedLedger().play)
    await act(async () => recording.resolve(openedLedger()))
    await user.click(screen.getByRole('button', { name: `Open ${shellPlay.summary.title}` }))
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await screen.findByText('Entry · buy 1 at 100')
    expect(screen.getByLabelText('Execution quantity')).toHaveValue('')
    expect(screen.queryByRole('button', { name: 'Retry same execution' })).not.toBeInTheDocument()
    expect(api.changePlayStatus).not.toHaveBeenCalled()
  })
  it('keeps an uncertain payload and acknowledges a manual success while Settings is visible', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('Unexpected network request'))))
    window.history.replaceState(null, '', '/#plays')
    const api = shellClient()
    const recording = deferred<ManualExecutions>()
    api.addManualExecution.mockImplementationOnce(() => recording.promise)
    render(<ApplicationShell system={shellSystem} disconnect={vi.fn()} api={api} />)
    const user = userEvent.setup()
    await enterShellManual(user)
    await user.click(screen.getByRole('button', { name: 'Record execution' }))
    const attempted = api.addManualExecution.mock.calls[0]
    await user.click(screen.getByRole('link', { name: 'Settings' }))
    await act(async () => recording.reject(new ApiError('unavailable', 'Response lost.')))
    expect(warnsOnUnload()).toBe(true)
    await user.click(screen.getByRole('link', { name: 'Plays' }))
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    const retrying = deferred<ManualExecutions>()
    api.addManualExecution.mockImplementationOnce(() => retrying.promise)
    await user.click(screen.getByRole('button', { name: 'Retry same execution' }))
    expect(api.addManualExecution.mock.calls[1]).toEqual(attempted)
    await user.click(screen.getByRole('link', { name: 'Settings' }))
    api.manualExecutions.mockResolvedValue(openedLedger())
    await act(async () => retrying.resolve(openedLedger()))
    expect(warnsOnUnload()).toBe(false)
    await user.click(screen.getByRole('link', { name: 'Plays' }))
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    await screen.findByText('Entry · buy 1 at 100')
    expect(screen.getByLabelText('Execution quantity')).toHaveValue('')
    expect(api.addManualExecution).toHaveBeenCalledTimes(2)
  })
  it('keeps a held Pause exclusive across Settings navigation, then releases only its own guard', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('Unexpected network request'))))
    window.history.replaceState(null, '', '/#plays')
    const api = shellClient()
    const pausing = deferred<SavedPlay>()
    const recording = deferred<ManualExecutions>()
    api.changePlayStatus.mockImplementation(() => pausing.promise)
    api.addManualExecution.mockImplementation(() => recording.promise)
    render(<ApplicationShell system={shellSystem} disconnect={vi.fn()} api={api} />)
    const user = userEvent.setup()
    await enterShellManual(user)
    await user.click(screen.getByRole('button', { name: 'Pause' }))
    await user.click(screen.getByRole('link', { name: 'Settings' }))
    await user.click(screen.getByRole('link', { name: 'Plays' }))
    await user.click(screen.getByRole('tab', { name: 'Execution' }))
    expect(screen.getByRole('button', { name: 'Record execution' })).toBeDisabled()
    fireEvent.submit(screen.getByRole('form', { name: 'Record manual execution' }))
    expect(api.addManualExecution).not.toHaveBeenCalled()
    await act(async () => pausing.reject(new ApiError('unavailable', 'Pause failed.')))
    await user.click(screen.getByRole('button', { name: 'Record execution' }))
    expect(api.addManualExecution).toHaveBeenCalledOnce()
    expect(screen.getByRole('button', { name: 'Pause' })).toBeDisabled()
    await act(async () => recording.reject(new ApiError('unavailable', 'Manual response lost.')))
  })
  it.each(['disconnect', 'unmount', 'API replacement'] as const)('invalidates pending ownership and private draft on %s', async action => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('Unexpected network request'))))
    window.history.replaceState(null, '', '/#plays')
    const api = shellClient()
    const previous = deferred<ManualExecutions>()
    api.addManualExecution.mockImplementation(() => previous.promise)
    const disconnect = vi.fn()
    const view = render(<ApplicationShell system={shellSystem} disconnect={disconnect} api={api} />)
    const user = userEvent.setup()
    await enterShellManual(user)
    await user.click(screen.getByRole('button', { name: 'Record execution' }))
    await user.click(screen.getByRole('link', { name: 'Settings' }))
    if (action === 'disconnect') {
      const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
      await user.click(screen.getByRole('button', { name: 'Disconnect session' }))
      expect(disconnect).not.toHaveBeenCalled()
      expect(warnsOnUnload()).toBe(true)
      await user.click(screen.getByRole('button', { name: 'Disconnect session' }))
      expect(confirm).toHaveBeenCalledTimes(2)
      expect(confirm).toHaveBeenLastCalledWith(expect.stringContaining('Already-sent changes may still complete.'))
    } else if (action === 'unmount') view.unmount()
    else {
      const nextApi = shellClient()
      const nextRequest = deferred<ManualExecutions>()
      nextApi.addManualExecution.mockImplementation(() => nextRequest.promise)
      view.rerender(<ApplicationShell system={shellSystem} disconnect={disconnect} api={nextApi} />)
      await user.click(screen.getByRole('link', { name: 'Plays' }))
      expect(screen.queryByRole('button', { name: 'Continue editing' })).not.toBeInTheDocument()
      await enterShellManual(user)
      await user.click(screen.getByRole('button', { name: 'Record execution' }))
      await act(async () => previous.resolve(openedLedger()))
      expect(screen.getByLabelText('Execution quantity')).toHaveValue('1')
      expect(screen.getByRole('button', { name: 'Recording…' })).toBeDisabled()
      expect(screen.getByRole('button', { name: 'Pause' })).toBeDisabled()
      await act(async () => nextRequest.reject(new ApiError('unavailable', 'New response lost.')))
      expect(screen.getByRole('button', { name: 'Retry same execution' })).toBeEnabled()
      return
    }
    await act(async () => previous.resolve(openedLedger()))
    expect(warnsOnUnload()).toBe(false)
    expect(screen.queryByText('Entry · buy 1 at 100')).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Plays' })).not.toBeInTheDocument()
    if (action === 'disconnect') expect(disconnect).toHaveBeenCalledOnce()
  })
})


describe('Plays review wave request regressions', () => {
  it('memoizes draft checks across unrelated renders and recomputes them for real edits', async () => {
    const { api } = client(planned)
    api.playHistory = vi.fn().mockResolvedValue({ revisions: [], statusChanges: [] })
    const dirty = vi.spyOn(savedChecks, 'isDirty')
    const changed = vi.spyOn(savedChecks, 'planChanged')
    try {
      render(<Harness api={api} initial={sessionFor(planned)} />)
      const user = userEvent.setup()
      await act(async () => {})
      const initialDirtyCalls = dirty.mock.calls.length
      const initialChangedCalls = changed.mock.calls.length
      await user.click(screen.getByRole('button', { name: 'History' }))
      await screen.findByText(/History starts when the play is planned/)
      await user.keyboard('{Escape}')
      expect(dirty).toHaveBeenCalledTimes(initialDirtyCalls)
      expect(changed).toHaveBeenCalledTimes(initialChangedCalls)
      await user.type(screen.getByRole('textbox', { name: 'Play title' }), ' revised')
      expect(dirty.mock.calls.length).toBeGreaterThan(initialDirtyCalls)
      expect(changed.mock.calls.length).toBeGreaterThan(initialChangedCalls)
      expect(screen.getByRole('button', { name: /^Save$/ })).toBeEnabled()
      expect(screen.getByRole('button', { name: 'Pause' })).toBeDisabled()
      // A plan edit must still request a revision reason; memoization cannot hide it.
      await user.type(screen.getByRole('spinbutton', { name: /Entry 1 planned entry price/ }), '100')
      await user.click(screen.getByRole('button', { name: /^Save$/ }))
      expect(screen.getByRole('dialog', { name: 'Why did the plan change?' })).toBeInTheDocument()
    } finally { dirty.mockRestore(); changed.mockRestore() }
  })

  it('does not reload an already mounted list when work starts, but reloads once after a failed saved-Play read', async () => {
    const { api, methods } = client()
    const read = deferred<SavedPlay>()
    methods.play.mockReturnValueOnce(read.promise)
    render(<Harness api={api} />)
    await waitFor(() => expect(methods.plays).toHaveBeenCalledOnce())
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Request another Play' }))
    await waitFor(() => expect(methods.play).toHaveBeenCalledOnce())
    expect(methods.plays).toHaveBeenCalledOnce()
    expect(screen.getByRole('button', { name: 'New play' })).toBeDisabled()
    await act(async () => { read.reject(new ApiError('unavailable', 'Synthetic read failed.')) })
    await waitFor(() => expect(methods.plays).toHaveBeenCalledTimes(2))
    expect(methods.play.mock.calls[0]![1]!.aborted).toBe(true)
    expect(screen.getByRole('button', { name: 'New play' })).toBeEnabled()
  })

  it('starts no extra list request at successful open start or when the editor replaces the list', async () => {
    const { api, methods } = client()
    const read = deferred<SavedPlay>()
    const target = { ...terminal, summary: { ...terminal.summary, title: 'Opened Play' } }
    methods.play.mockReturnValueOnce(read.promise)
    render(<Harness api={api} />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('tab', { name: /^All/ }))
    const row = await screen.findByRole('button', { name: `Open ${terminal.summary.title}` })
    expect(reads(methods, 'all')).toBe(1)
    await user.click(row)
    await waitFor(() => expect(methods.play).toHaveBeenCalledOnce())
    expect(row).toBeDisabled()
    expect(reads(methods, 'all')).toBe(1)
    await act(async () => { read.resolve(target) })
    expect(screen.getByRole('textbox', { name: 'Play title' })).toHaveValue('Opened Play')
    expect(reads(methods, 'all')).toBe(1)
  })

  it.each(['revision', 'cancel'] as const)('shows a %s failure in its current intended dialog after intervening renders', async kind => {
    const { api, methods } = client(planned)
    const pending = deferred<SavedPlay>()
    methods.updatePlay.mockReturnValueOnce(pending.promise)
    api.changePlayStatus = vi.fn().mockReturnValue(pending.promise)
    render(<Harness api={api} initial={sessionFor(planned)} />)
    const user = userEvent.setup()
    if (kind === 'revision') {
      await user.type(screen.getByRole('spinbutton', { name: /Entry 1 planned entry price/ }), '100')
      await user.click(screen.getByRole('button', { name: /^Save$/ }))
      await user.type(screen.getByRole('textbox', { name: 'Reason' }), 'Synthetic revision')
      await user.click(screen.getByRole('button', { name: 'Save revision' }))
    } else {
      await user.click(screen.getByRole('button', { name: 'Cancel play' }))
      await user.selectOptions(screen.getByRole('combobox', { name: 'Reason' }), 'missed')
      await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel play' }))
    }
    const dialog = screen.getByRole('dialog')
    expect(within(dialog).getByRole('button', { name: kind === 'revision' ? 'Saving…' : 'Cancelling…' })).toBeDisabled()
    await act(async () => { pending.reject(new ApiError('unavailable', 'Current dialog failure.')) })
    expect(within(dialog).getByRole('alert')).toHaveTextContent('Current dialog failure.')
    expect(document.querySelector('[aria-label="Notifications"]')).not.toHaveTextContent('Current dialog failure.')
  })

  it.each(['revision', 'cancel'] as const)('does not send a %s error to a dialog from an old render after History replaces it', async kind => {
    const { api, methods } = client(planned)
    const pending = deferred<SavedPlay>()
    methods.updatePlay.mockReturnValueOnce(pending.promise)
    api.changePlayStatus = vi.fn().mockReturnValue(pending.promise)
    api.playHistory = vi.fn().mockResolvedValue({ revisions: [], statusChanges: [] })
    render(<Harness api={api} initial={sessionFor(planned)} />)
    const user = userEvent.setup()
    const historyButton = screen.getByRole('button', { name: 'History' })
    if (kind === 'revision') {
      await user.type(screen.getByRole('spinbutton', { name: /Entry 1 planned entry price/ }), '100')
      await user.click(screen.getByRole('button', { name: /^Save$/ }))
      await user.type(screen.getByRole('textbox', { name: 'Reason' }), 'Synthetic revision')
      await user.click(screen.getByRole('button', { name: 'Save revision' }))
    } else {
      await user.click(screen.getByRole('button', { name: 'Cancel play' }))
      await user.selectOptions(screen.getByRole('combobox', { name: 'Reason' }), 'missed')
      await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel play' }))
    }
    // Exercise replacement directly, without bypassing pending mutation ownership.
    fireEvent.click(historyButton)
    expect(screen.getByRole('dialog', { name: 'Play history' })).toBeInTheDocument()
    await act(async () => { pending.reject(new ApiError('unavailable', 'Previous dialog failed.')) })
    expect(document.querySelector('[aria-label="Notifications"]')).toHaveTextContent('Previous dialog failed.')
    expect(within(screen.getByRole('dialog', { name: 'Play history' })).queryByRole('alert')).not.toBeInTheDocument()
  })

  it.each(['replace', 'unmount'] as const)('aborts the saved-Play read triggered by a venue status change on %s and ignores its late result', async mode => {
    const { api, methods } = client(planned)
    const read = deferred<SavedPlay>()
    const other = { ...planned, summary: { ...planned.summary, id: otherId, title: 'Replacement Play' } }
    methods.playExecution.mockImplementation(async () => ({ ...execution(planned), status: 'closed' }))
    methods.play.mockReturnValueOnce(read.promise)
    const nextSession = vi.fn()
    const view = render(<Harness api={api} initial={sessionFor(planned)} onSession={nextSession} />)
    await waitFor(() => expect(methods.play).toHaveBeenCalledWith(planned.summary.id, expect.any(AbortSignal)))
    const signal = methods.play.mock.calls[0]![1]!
    if (mode === 'replace') {
      const replacement = client(other)
      replacement.methods.playExecution.mockResolvedValue(execution(other))
      view.rerender(<Harness api={replacement.api} initial={sessionFor(other)} onSession={nextSession} />)
    } else view.unmount()
    expect(signal.aborted).toBe(true)
    await act(async () => { read.resolve({ ...planned, summary: { ...planned.summary, status: 'closed', version: 5 } }) })
    expect(nextSession).not.toHaveBeenCalled()
  })
})

// Phase 0, 0.5b review: a manual Play measures its stops from the ledger's average fill per entry, as the server decides.
describe('manual fills in the Summary', () => {
  const level = (id: string, value: string) => ({ id, unit: 'price' as const, value, share: '100' })
  const open: SavedPlay = { ...planned, summary: { ...planned.summary, status: 'open', version: 3 },
    plan: { ...planned.plan, sizingMode: 'quantity', size: '1', leverage: '1', entries: [
      { id: 'entry-1', name: 'Entry 1', color: '#b9c9e4', share: '50', price: '100', stops: [level('stop-1', '101')], targets: [level('target-1', '110')] },
      { id: 'entry-2', name: 'Entry 2', color: '#c9b9e4', share: '50', price: '100', stops: [level('stop-2', '101')], targets: [level('target-2', '110')] },
    ] } }
  const fill = (id: string, entryId: string, quantity: string, price: string): ManualExecutions['records'][number] => ({ id, role: 'entry', entryId,
    occurredAtUtc: at, side: 'buy', quantity, price, fee: '0', feeCurrency: 'USD', source: 'Statement', createdAtUtc: at, updatedAtUtc: at })
  const summaryValue = (label: string) =>
    within(screen.getByRole('group', { name: 'Planned position money summary' })).getByText(label, { selector: 'dt' }).nextElementSibling!

  it('measures each filled entry from its ledger average, and waits for the ledger instead of using the planned entry', async () => {
    const { api } = client(open)
    const ledger = deferred<ManualExecutions>()
    api.manualExecutions = vi.fn(() => ledger.promise)
    render(<Harness api={api} initial={sessionFor(open)} manual />)
    expect(summaryValue('All stops')).toHaveTextContent(/^UnknownWaiting for the entries' fills$/)
    // Entry 1 filled 1 at 101 and 1 at 103 (average 102); entry 2 has no fill, so its saved stop above 100 is only a warning.
    await act(async () => ledger.resolve({ ...manualLedger(open), records: [fill(ids(1), 'entry-1', '1', '101'), fill(ids(2), 'entry-1', '1e0', '103')] }))
    expect(api.manualExecutions).toHaveBeenCalledWith(open.summary.id, expect.any(AbortSignal))
    // Only the selected entry shows its form; entry 1's stop above its planned price is fine, since entry 1 filled.
    const stop = screen.getByRole('spinbutton', { name: 'Entry 1 planned stop price (quote units)' })
    expect(stop).not.toHaveAttribute('data-warning')
    expect(stop).not.toHaveAttribute('aria-invalid')
    // Entry 2's stop is not covered, so only entry 1's half is known: 0.5 × (101 − 102), not +0.5 from the planned 100.
    expect(summaryValue('All stops')).toHaveTextContent(/^Known \u22120.5 USD at riskTotal unknown · from the average fill$/)
    expect(within(screen.getByRole('region', { name: 'Plan notifications' })).getByRole('status'))
      .toHaveTextContent('Entry 2 stop is above the entry price, but its entry has no fill.')
  })
})
const ids = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`
