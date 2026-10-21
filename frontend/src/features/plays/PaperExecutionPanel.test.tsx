import { act, fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/errors'
import type { PaperPlayState } from '@/api/paper'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import { formatTime } from '@/lib/time'
import { PaperExecutionPanel } from './PaperExecutionPanel'

const playId = '30000000-0000-4000-8000-000000000001'
const otherPlayId = '30000000-0000-4000-8000-000000000002'
const entry: PaperPlayState['executions'][number] = {
  id: '10000000-0000-4000-8000-000000000001', entryId: 'd-entry-1', levelId: null, role: 'entry',
  price: '12345.67890123456789', quantity: '0.3000000000000000001', pnlUsd: '0',
  occurredAtUtc: '2026-10-20T12:01:00Z', candleOpenAtUtc: '2026-10-20T12:00:00Z', candleCloseAtUtc: '2026-10-20T12:01:00Z',
}
const state: PaperPlayState = {
  playId, status: 'waiting', checkedAtUtc: '2026-10-20T12:02:00Z', nextCheckAtUtc: '2026-10-20T13:02:00Z',
  enteredQuantity: '0.3000000000000000001', exitedQuantity: '0.1', openQuantity: '0.2000000000000000001',
  realizedPnlUsd: '-1.234567890123456789', issues: [], declinedEntries: [], suspensions: [], hasUnscoredHistory: false,
  executions: [entry, {
    ...entry, id: '10000000-0000-4000-8000-000000000002', levelId: 'd-target-1', role: 'target',
    quantity: '0.1', pnlUsd: '-1.234567890123456789', occurredAtUtc: '2026-10-20T12:02:00Z',
    candleOpenAtUtc: '2026-10-20T12:01:00Z', candleCloseAtUtc: '2026-10-20T12:02:00Z',
  }],
}
const makeApi = (value = state) => ({ paperState: vi.fn<WorkspaceApi['paperState']>().mockResolvedValue(value) })
function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
afterEach(() => vi.useRealTimers())

describe('PaperExecutionPanel', () => {
  it('shows only explicit simulation progress and rows, preserving exact strings, with candle times in the display zone', async () => {
    const api = makeApi()
    const onState = vi.fn()
    render(<PaperExecutionPanel api={api} playId={playId} onState={onState} />)
    expect(await screen.findByRole('heading', { name: 'Simulated executions' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Paper execution · simulation' })).toBeInTheDocument()
    expect(screen.getByText(/No real orders or imported fills/)).toBeInTheDocument()
    expect(screen.getByText('Open quantity').nextElementSibling).toHaveTextContent(state.openQuantity)
    expect(screen.getByText('Entered quantity').nextElementSibling).toHaveTextContent(state.enteredQuantity)
    expect(screen.getByText('Exited quantity').nextElementSibling).toHaveTextContent(state.exitedQuantity)
    expect(screen.getByText('Realized simulated P&L').nextElementSibling).toHaveTextContent(`${state.realizedPnlUsd} USD`)
    const rows = within(screen.getByRole('region', { name: 'Simulated executions' })).getAllByRole('listitem')
    expect(rows).toHaveLength(2)
    expect(rows[0]).toHaveTextContent(`Quantity ${entry.quantity} at ${entry.price}`)
    expect(rows[0]).toHaveTextContent(`Entry ID ${entry.entryId}`)
    expect(rows[1]).toHaveTextContent('Level ID d-target-1')
    expect(rows[1]).toHaveTextContent('Simulated target')
    const candleTimes = rows[0]!.querySelectorAll('time')
    expect(Array.from(candleTimes, element => element.dateTime)).toEqual([entry.occurredAtUtc, entry.candleOpenAtUtc, entry.candleCloseAtUtc])
    expect(rows[0]).toHaveTextContent(`Candle ${formatTime(entry.candleOpenAtUtc, 'local')} – ${formatTime(entry.candleCloseAtUtc, 'local')}`)
    expect(onState.mock.calls).toEqual([[null], [state]])
    expect(api.paperState).toHaveBeenCalledWith(playId, expect.any(AbortSignal))
    expect(screen.queryByRole('button', { name: /Link|Check|Record|Refresh/ })).not.toBeInTheDocument()
  })

  it.each([
    ['not-armed', 'Not armed'], ['waiting', 'Waiting'], ['ready', 'Ready'],
    ['ambiguous', 'Ambiguous candle'], ['gap', 'Candle history gap'], ['disabled', 'Paper execution disabled'],
    ['blocked', 'Capital admission blocked'], ['suspended', 'Paper execution suspended'],
  ] as const)('shows %s without inventing executions or a scored result', async (status, label) => {
    render(<PaperExecutionPanel api={makeApi({ ...state, status, checkedAtUtc: null, nextCheckAtUtc: null, executions: [],
      issues: ['The server has not resolved this window.'] })} playId={playId} />)
    expect(await screen.findByText(label)).toBeInTheDocument()
    expect(screen.getByText('Not checked yet')).toBeInTheDocument()
    expect(screen.getByText('Not scheduled')).toBeInTheDocument()
    expect(screen.getByText('No simulated executions recorded.')).toBeInTheDocument()
    expect(screen.getByText('The server has not resolved this window.')).toBeInTheDocument()
    if (status === 'ambiguous') expect(screen.getByText(/No intrabar winner is assumed/)).toBeInTheDocument()
    if (status === 'ambiguous' || status === 'gap') expect(screen.getByText(/not scored/)).toBeInTheDocument()
  })

  it('separates a declined capital admission from candle gaps while protective exits continue', async () => {
    const declinedEntry = { entryId: 'local-entry-2', reason: 'Insufficient paper margin for this entry.', declinedAtUtc: '2026-10-20T12:03:00Z' }
    render(<PaperExecutionPanel api={makeApi({ ...state, status: 'blocked', declinedEntries: [declinedEntry] })} playId={playId} />)
    expect(await screen.findByText('Capital admission blocked')).toBeInTheDocument()
    expect(screen.getByText(/Protective stops and targets for open tranches continue/)).toBeInTheDocument()
    const section = screen.getByRole('region', { name: 'Declined entries' })
    expect(section).toHaveTextContent('Entry ID local-entry-2')
    expect(section).toHaveTextContent(declinedEntry.reason)
    expect(section.querySelector('time')?.dateTime).toBe(declinedEntry.declinedAtUtc)
    expect(section).toHaveTextContent('never retroactively simulated')
    expect(screen.queryByText('Candle history gap')).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Paper execution issues' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Undo|Reset|Resolve|Close simulated/i })).not.toBeInTheDocument()
  })

  it('shows the instants a suspension reason names in the display zone', async () => {
    // The server writes the skipped interval of a paused or cancelled paper Play as round-trip UTC instants.
    const [from, to] = ['2026-10-20T12:00:00.0000000+00:00', '2026-10-20T12:03:12.5000000+00:00']
    const suspension = { reason: `The owner paused this paper Play; the unprocessed pre-action interval [${from}, ${to}) was skipped and is not simulated.`,
      suspendedAtUtc: '2026-10-20T12:03:12Z', resumedAtUtc: null }
    render(<PaperExecutionPanel api={makeApi({ ...state, status: 'suspended', suspensions: [suspension], hasUnscoredHistory: true,
      nextCheckAtUtc: null })} playId={playId} />)
    const section = await screen.findByRole('region', { name: 'Suspension history' })
    expect(section).toHaveTextContent(`interval [${formatTime(from, 'local', 'datetime-seconds')}, ${formatTime(to, 'local', 'datetime-seconds')}) was skipped`)
    expect(section).not.toHaveTextContent(from)
    expect(Array.from(section.querySelectorAll('strong time'), element => element.getAttribute('datetime'))).toEqual([from, to])
  })

  it.each([null, '2026-10-20T12:06:00Z'])('shows suspension receipts with retained unscored history when resumed at %s', async resumedAtUtc => {
    const suspension = { reason: 'Paper account disabled.', suspendedAtUtc: '2026-10-20T12:03:12Z', resumedAtUtc }
    render(<PaperExecutionPanel api={makeApi({ ...state, status: resumedAtUtc ? 'waiting' : 'suspended', suspensions: [suspension],
      hasUnscoredHistory: true, nextCheckAtUtc: resumedAtUtc ? state.nextCheckAtUtc : null })} playId={playId} />)
    const section = await screen.findByRole('region', { name: 'Suspension history' })
    expect(section).toHaveTextContent(suspension.reason)
    expect(section).toHaveTextContent('Suspended at')
    expect(section.querySelector('time')?.dateTime).toBe(suspension.suspendedAtUtc)
    expect(screen.getByText(/Unscored history/)).toHaveTextContent('excluded from scoring')
    expect(section).toHaveTextContent('Disabled-period price touches are never replayed')
    if (resumedAtUtc) {
      expect(section).toHaveTextContent('Resumed at')
      expect(section.querySelectorAll('time')[1]?.dateTime).toBe(resumedAtUtc)
      expect(section).toHaveTextContent('fresh minute boundary')
      expect(screen.queryByText('Paper execution suspended')).not.toBeInTheDocument()
    } else {
      expect(section).toHaveTextContent('Not resumed')
      expect(screen.getByText('Paper execution suspended')).toBeInTheDocument()
      expect(screen.getByText(/Reservations remain held while open exposure is unknown/)).toBeInTheDocument()
    }
    expect(screen.queryByText('Candle history gap')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Undo|Reset|Resolve|Resume/i })).not.toBeInTheDocument()
  })

  it('keeps historical decline and unscored notices visible after evaluation becomes ready with no transient issues', async () => {
    render(<PaperExecutionPanel api={makeApi({ ...state, status: 'ready', hasUnscoredHistory: true,
      declinedEntries: [{ entryId: 'entry-2', reason: 'Capital unavailable at this touch.', declinedAtUtc: '2026-10-20T12:03:00Z' }] })} playId={playId} />)
    expect(await screen.findByText('Ready')).toBeInTheDocument()
    expect(screen.getByText(/Unscored history/)).toHaveTextContent('excluded from scoring')
    expect(screen.getByRole('region', { name: 'Declined entries' })).toHaveTextContent('Capital unavailable at this touch.')
    expect(screen.queryByRole('region', { name: 'Paper execution issues' })).not.toBeInTheDocument()
  })

  it('discloses the version-one rounding model without changing defined execution prices', async () => {
    render(<PaperExecutionPanel api={makeApi()} playId={playId} />)
    await screen.findByText('Simulated target')
    expect(screen.getByText(/Model v1/)).toHaveTextContent('12 decimal places')
    expect(screen.getByText(/Model v1/)).toHaveTextContent('quantity rounds down, USD margin rounds up, and P&L rounds toward negative infinity')
    expect(screen.getByText(/Model v1/)).toHaveTextContent('Defined plan prices are unchanged')
  })

  it('clears facts at a manual reload, reads only paper state, then publishes the new state', async () => {
    const pending = deferred<PaperPlayState>()
    const api = makeApi()
    api.paperState.mockResolvedValueOnce(state).mockReturnValueOnce(pending.promise)
    const onState = vi.fn()
    render(<PaperExecutionPanel api={api} playId={playId} onState={onState} />)
    await screen.findByText('Simulated target')
    await userEvent.click(screen.getByRole('button', { name: 'Reload paper execution' }))
    expect(screen.getByText('Loading paper execution…')).toBeInTheDocument()
    expect(screen.queryByText('Simulated target')).not.toBeInTheDocument()
    expect(onState).toHaveBeenLastCalledWith(null)
    expect(screen.getByRole('button')).toBeDisabled()
    const updated = { ...state, realizedPnlUsd: '0' }
    await act(async () => pending.resolve(updated))
    expect(screen.getByText('Realized simulated P&L').nextElementSibling).toHaveTextContent('0 USD')
    expect(onState).toHaveBeenLastCalledWith(updated)
    expect(api.paperState).toHaveBeenCalledTimes(2)
  })

  it.each(['error', 'malformed', 'foreign'] as const)('fails closed on a %s response and only shows safe error text', async kind => {
    const api = makeApi()
    api.paperState.mockResolvedValueOnce(state)
    if (kind === 'error') api.paperState.mockRejectedValueOnce(new Error('Private backend detail'))
    if (kind === 'malformed') api.paperState.mockResolvedValueOnce({ ...state, openQuantity: 3 } as unknown as PaperPlayState)
    if (kind === 'foreign') api.paperState.mockResolvedValueOnce({ ...state, playId: otherPlayId })
    const onState = vi.fn()
    render(<PaperExecutionPanel api={api} playId={playId} onState={onState} />)
    await screen.findByText('Simulated target')
    await userEvent.click(screen.getByRole('button', { name: 'Reload paper execution' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Paper execution could not be loaded. No simulated result is available.')
    expect(screen.queryByText('Simulated target')).not.toBeInTheDocument()
    expect(screen.queryByText('Private backend detail')).not.toBeInTheDocument()
    expect(onState).toHaveBeenLastCalledWith(null)
    expect(screen.getByRole('button', { name: 'Reload paper execution' })).toBeEnabled()
  })

  it('shows a safe API error and permits a manual retry', async () => {
    const api = makeApi()
    api.paperState.mockRejectedValueOnce(new ApiError('http', 'Paper execution is unavailable.', 503)).mockResolvedValue(state)
    render(<PaperExecutionPanel api={api} playId={playId} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Paper execution is unavailable.')
    await userEvent.click(screen.getByRole('button', { name: 'Reload paper execution' }))
    expect(await screen.findByText('Simulated target')).toBeInTheDocument()
  })

  it('handles an older API fixture without a paper method', async () => {
    render(<PaperExecutionPanel api={{} as Pick<WorkspaceApi, 'paperState'>} playId={playId} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Paper execution could not be loaded.')
  })

  it('clears on generation changes and ignores an aborted generation response', async () => {
    const first = deferred<PaperPlayState>()
    const second = deferred<PaperPlayState>()
    const api = makeApi()
    api.paperState.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const onState = vi.fn()
    const view = render(<PaperExecutionPanel api={api} playId={playId} reloadGeneration={1} onState={onState} />)
    const oldSignal = api.paperState.mock.calls[0]?.[1]
    view.rerender(<PaperExecutionPanel api={api} playId={playId} reloadGeneration={2} onState={onState} />)
    expect(oldSignal?.aborted).toBe(true)
    expect(onState).toHaveBeenCalledTimes(2)
    await act(async () => first.resolve(state))
    expect(screen.getByText('Loading paper execution…')).toBeInTheDocument()
    expect(onState).toHaveBeenLastCalledWith(null)
    await act(async () => second.resolve({ ...state, realizedPnlUsd: '8' }))
    expect(screen.getByText('Realized simulated P&L').nextElementSibling).toHaveTextContent('8 USD')
    expect(onState).toHaveBeenLastCalledWith({ ...state, realizedPnlUsd: '8' })
  })

  it('uses a fresh request after an API/Play round trip instead of restoring earlier facts', async () => {
    const pendingOther = deferred<PaperPlayState>()
    const pendingAgain = deferred<PaperPlayState>()
    const api = makeApi()
    api.paperState.mockResolvedValueOnce(state).mockReturnValueOnce(pendingOther.promise).mockReturnValueOnce(pendingAgain.promise)
    const onState = vi.fn()
    const view = render(<PaperExecutionPanel api={api} playId={playId} onState={onState} />)
    await screen.findByText('Simulated target')
    view.rerender(<PaperExecutionPanel api={api} playId={otherPlayId} onState={onState} />)
    view.rerender(<PaperExecutionPanel api={api} playId={playId} onState={onState} />)
    expect(screen.queryByText('Simulated target')).not.toBeInTheDocument()
    await act(async () => pendingOther.resolve({ ...state, playId: otherPlayId }))
    expect(screen.queryByText('Simulated target')).not.toBeInTheDocument()
    expect(onState).toHaveBeenLastCalledWith(null)
    await act(async () => pendingAgain.resolve(state))
    expect(screen.getByText('Simulated target')).toBeInTheDocument()
  })

  it('does not restart a read when the callback identity changes, and publishes to the newest callback', async () => {
    const pending = deferred<PaperPlayState>()
    const api = makeApi()
    api.paperState.mockReturnValue(pending.promise)
    const first = vi.fn()
    const second = vi.fn()
    const view = render(<PaperExecutionPanel api={api} playId={playId} onState={first} />)
    view.rerender(<PaperExecutionPanel api={api} playId={playId} onState={second} />)
    await act(async () => pending.resolve(state))
    expect(api.paperState).toHaveBeenCalledTimes(1)
    expect(first.mock.calls).toEqual([[null]])
    expect(second).toHaveBeenCalledExactlyOnceWith(state)
  })

  it.each(['resolve', 'reject'] as const)('ignores a late %s after unmount, aborting the read and leaving callbacks alone', async result => {
    const pending = deferred<PaperPlayState>()
    const api = makeApi()
    api.paperState.mockReturnValue(pending.promise)
    const onState = vi.fn()
    const view = render(<PaperExecutionPanel api={api} playId={playId} onState={onState} />)
    const signal = api.paperState.mock.calls[0]?.[1]
    view.unmount()
    expect(signal?.aborted).toBe(true)
    await act(async () => {
      if (result === 'resolve') pending.resolve(state)
      else pending.reject(new Error('Late private error'))
    })
    expect(onState.mock.calls).toEqual([[null]])
  })

  it.each([
    ['next-check-plus-two-seconds', '2026-10-20T12:00:10Z', 12_000],
    ['minimum', '2026-10-20T12:00:01Z', 5000],
    ['maximum', '2026-10-20T13:00:00Z', 300_000],
    ['past', '2026-10-20T11:59:00Z', 60_000],
  ] as const)('observes local paper state using the bounded %s delay', async (_label, nextCheckAtUtc, delay) => {
    vi.useFakeTimers({ now: Date.parse('2026-10-20T12:00:00Z') })
    const api = makeApi({ ...state, nextCheckAtUtc })
    const onState = vi.fn()
    render(<PaperExecutionPanel api={api} playId={playId} onState={onState} />)
    await act(async () => {})
    expect(api.paperState).toHaveBeenCalledTimes(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(delay - 1) })
    expect(api.paperState).toHaveBeenCalledTimes(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(api.paperState).toHaveBeenCalledTimes(2)
    expect(onState.mock.calls.at(-2)).toEqual([null])
    expect(onState).toHaveBeenLastCalledWith({ ...state, nextCheckAtUtc })
  })

  it.each(['not-armed', 'waiting', 'ready', 'blocked', 'disabled', 'suspended', 'gap', 'ambiguous'] as const)('stops automatic reads for %s without a next check, but permits a stored-state reload', async status => {
    vi.useFakeTimers({ now: Date.parse('2026-10-20T12:00:00Z') })
    const api = makeApi({ ...state, status, nextCheckAtUtc: null })
    render(<PaperExecutionPanel api={api} playId={playId} />)
    await act(async () => {})
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000) })
    expect(api.paperState).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Reload paper execution' }))
    await act(async () => {})
    expect(api.paperState).toHaveBeenCalledTimes(2)
  })

  it('keeps blocked observations aligned to the next check using only GET paper-state, never actual checks or venue calls', async () => {
    vi.useFakeTimers({ now: Date.parse('2026-10-20T12:00:00Z') })
    const blocked: PaperPlayState = { ...state, status: 'blocked', nextCheckAtUtc: '2026-10-20T12:00:10Z',
      declinedEntries: [{ entryId: 'entry-2', reason: 'Insufficient capital.', declinedAtUtc: '2026-10-20T11:59:00Z' }] }
    const fetcher = vi.fn().mockImplementation(async () => new Response(JSON.stringify(blocked)))
    vi.stubGlobal('fetch', fetcher)
    render(<PaperExecutionPanel api={createWorkspaceApi('synthetic-token', undefined, 'paper')} playId={playId} />)
    await act(async () => {})
    expect(fetcher).toHaveBeenCalledTimes(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(11_999) })
    expect(fetcher).toHaveBeenCalledTimes(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(fetcher).toHaveBeenCalledTimes(2)
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(fetcher).toHaveBeenCalledTimes(3)
    fireEvent.click(screen.getByRole('button', { name: 'Reload paper execution' }))
    await act(async () => {})
    expect(fetcher).toHaveBeenCalledTimes(4)
    for (const [path, init] of fetcher.mock.calls) {
      expect(path).toBe(`/api/plays/${playId}/paper-state?tradingMode=paper`)
      expect(init.method).toBe('GET')
    }
  })

  it.each(['gap', 'ambiguous'] as const)('cancels future observations when active blocked evaluation becomes terminal %s', async status => {
    vi.useFakeTimers({ now: Date.parse('2026-10-20T12:00:00Z') })
    const api = makeApi({ ...state, status, nextCheckAtUtc: null })
    api.paperState.mockResolvedValueOnce({ ...state, status: 'blocked', nextCheckAtUtc: '2026-10-20T12:00:58Z' })
    render(<PaperExecutionPanel api={api} playId={playId} />)
    await act(async () => {})
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(api.paperState).toHaveBeenCalledTimes(2)
    await act(async () => { await vi.advanceTimersByTimeAsync(3_600_000) })
    expect(api.paperState).toHaveBeenCalledTimes(2)
  })

  it('rereads suspended state after a generation change, retaining its resumption receipt and unscored history', async () => {
    vi.useFakeTimers({ now: Date.parse('2026-10-20T12:00:00Z') })
    const suspension = { reason: 'Paper settings disabled.', suspendedAtUtc: '2026-10-20T11:50:00Z', resumedAtUtc: null }
    const api = makeApi({ ...state, status: 'suspended', nextCheckAtUtc: null, suspensions: [suspension], hasUnscoredHistory: true })
    const view = render(<PaperExecutionPanel api={api} playId={playId} reloadGeneration={1} />)
    await act(async () => {})
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000) })
    expect(api.paperState).toHaveBeenCalledTimes(1)
    api.paperState.mockResolvedValue({ ...state, status: 'waiting', nextCheckAtUtc: '2026-10-20T12:10:10Z',
      suspensions: [{ ...suspension, resumedAtUtc: '2026-10-20T12:10:00Z' }], hasUnscoredHistory: true })
    view.rerender(<PaperExecutionPanel api={api} playId={playId} reloadGeneration={2} />)
    await act(async () => {})
    expect(api.paperState).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('region', { name: 'Suspension history' })).toHaveTextContent('Resumed at')
    expect(screen.getByText(/Unscored history/)).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(12_000) })
    expect(api.paperState).toHaveBeenCalledTimes(3)
  })

  it('does not overlap timed reads, and keeps facts cleared after a failed observation until manual retry', async () => {
    vi.useFakeTimers({ now: Date.parse('2026-10-20T12:00:00Z') })
    const pending = deferred<PaperPlayState>()
    const api = makeApi({ ...state, nextCheckAtUtc: '2026-10-20T11:59:00Z' })
    api.paperState.mockResolvedValueOnce({ ...state, nextCheckAtUtc: '2026-10-20T11:59:00Z' }).mockReturnValueOnce(pending.promise)
    const onState = vi.fn()
    render(<PaperExecutionPanel api={api} playId={playId} onState={onState} />)
    await act(async () => {})
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(api.paperState).toHaveBeenCalledTimes(2)
    expect(onState).toHaveBeenLastCalledWith(null)
    expect(screen.getByRole('button', { name: 'Reload paper execution' })).toBeDisabled()
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000) })
    expect(api.paperState).toHaveBeenCalledTimes(2)
    await act(async () => pending.reject(new Error('Private local error')))
    expect(screen.getByRole('alert')).toHaveTextContent('Paper execution could not be loaded.')
    expect(screen.queryByText('Simulated target')).not.toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000) })
    expect(api.paperState).toHaveBeenCalledTimes(2)
    fireEvent.click(screen.getByRole('button', { name: 'Reload paper execution' }))
    await act(async () => {})
    expect(api.paperState).toHaveBeenCalledTimes(3)
    expect(screen.getByText('Simulated target')).toBeInTheDocument()
  })

  it('pauses timed observation in a hidden tab and clears the timer on unmount', async () => {
    vi.useFakeTimers({ now: Date.parse('2026-10-20T12:00:00Z') })
    let visibility: DocumentVisibilityState = 'visible'
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
    const api = makeApi({ ...state, nextCheckAtUtc: '2026-10-20T11:59:00Z' })
    const view = render(<PaperExecutionPanel api={api} playId={playId} />)
    await act(async () => {})
    visibility = 'hidden'
    fireEvent(document, new Event('visibilitychange'))
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000) })
    expect(api.paperState).toHaveBeenCalledTimes(1)
    visibility = 'visible'
    fireEvent(document, new Event('visibilitychange'))
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(api.paperState).toHaveBeenCalledTimes(2)
    view.unmount()
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000) })
    expect(api.paperState).toHaveBeenCalledTimes(2)
  })
})
