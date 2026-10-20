import { StrictMode } from 'react'
import { act, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { PlayExecution } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { discrepancyFixture, monitorFixture } from '@/test/monitor-fixture'
import { savedPlayFixture } from '@/test/workspace-fixture'
import { usePlayExecution } from './usePlayExecution'

afterEach(() => vi.useRealTimers())
const execution: PlayExecution = {
  playId: savedPlayFixture.summary.id, status: 'open', tracked: true, checkedAtUtc: null, reason: null,
  totals: { enteredQuantity: '1', exitedQuantity: '0', openQuantity: '1', closedPnlUsd: '0', closedPnlBasis: 'gross', fees: [] },
  entries: [], links: [], suggestions: [], unlinkedOrders: [], notice: 'Stored facts only.', issues: ['Missing exit execution.'],
  monitor: monitorFixture, discrepancies: [discrepancyFixture], watches: [],
}
const client = () => ({ playExecution: vi.fn().mockResolvedValue(execution), runAccountMonitor: vi.fn(),
  sync: vi.fn(), dismissPlayDiscrepancy: vi.fn().mockResolvedValue(undefined),
}) as unknown as WorkspaceApi

function deferred() {
  let resolve!: (value: PlayExecution) => void
  let reject!: (cause: unknown) => void
  const promise = new Promise<PlayExecution>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

describe('stored Play execution', () => {
  it('does not automatically check on opening, resuming or elapsed time; shell generations only reload stored facts', async () => {
    vi.useFakeTimers()
    const api = client()
    const view = renderHook(({ status, generation }) => usePlayExecution(api, execution.playId, status, generation),
      { initialProps: { status: 'planned', generation: 0 } })
    await act(async () => { await vi.advanceTimersByTimeAsync(180_000) })
    expect(api.playExecution).toHaveBeenCalledOnce()
    view.rerender({ status: 'open', generation: 1 })
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(api.playExecution).toHaveBeenCalledTimes(2)
    expect(api.runAccountMonitor).not.toHaveBeenCalled()
    expect(api.sync).not.toHaveBeenCalled()
    expect(view.result.current.execution?.issues).toEqual(execution.issues)
  })
  it('dismisses a Play discrepancy, reloads stored facts and keeps execution issues', async () => {
    const api = client()
    const view = renderHook(() => usePlayExecution(api, execution.playId, 'open'))
    await act(async () => {})
    await act(async () => { await view.result.current.dismiss(discrepancyFixture.id, 'Owner note.') })
    expect(api.dismissPlayDiscrepancy).toHaveBeenCalledWith(execution.playId, discrepancyFixture.id, 'Owner note.', expect.any(AbortSignal))
    expect(api.playExecution).toHaveBeenCalledTimes(2)
    expect(view.result.current.execution?.issues).toEqual(['Missing exit execution.'])
    expect(api.runAccountMonitor).not.toHaveBeenCalled()
  })
  it('keeps a rejected owner-link error visible after the stored refresh', async () => {
    const api = client()
    api.linkOrder = vi.fn().mockRejectedValue(new ApiError('http', 'This order is already linked elsewhere.', 409))
    const view = renderHook(() => usePlayExecution(api, execution.playId, 'open'))
    await act(async () => {})
    await act(async () => { await view.result.current.link({ orderId: 'order-1', role: 'entry' }) })
    expect(api.playExecution).toHaveBeenCalledTimes(2)
    expect(view.result.current.error).toBe('This order is already linked elsewhere.')
    expect(view.result.current.execution?.issues).toEqual(execution.issues)
  })
  describe.each(['link', 'unlink', 'dismiss'] as const)('%s navigation', action => {
    it.each([
      ['resolve', 'before return'], ['reject', 'before return'],
      ['resolve', 'during retry'], ['reject', 'during retry'],
      ['resolve', 'after retry'], ['reject', 'after retry'],
    ] as const)('forgets aborted work on return to the same Play: %s %s', async (outcome, timing) => {
      let settle!: () => void
      let rejectRetry!: (cause: Error) => void
      const oldRequest = new Promise<PlayExecution | void>((resolve, reject) => {
        settle = () => outcome === 'resolve'
          ? resolve(action === 'dismiss' ? undefined : { ...execution, status: 'closed' })
          : reject(new ApiError('http', 'Old action failed.', 409))
      })
      const request = vi.fn().mockReturnValueOnce(oldRequest)
        .mockImplementationOnce(() => new Promise((_, reject) => { rejectRetry = reject }))
      const api = client()
      api[action === 'link' ? 'linkOrder' : action === 'unlink' ? 'unlinkOrder' : 'dismissPlayDiscrepancy'] = request
      const view = renderHook(({ id }: { id: string | null }) => usePlayExecution(api, id, 'open'),
        { initialProps: { id: execution.playId as string | null } })
      const invoke = () => {
        const current = view.result.current
        return (action === 'link' ? current.link({ orderId: 'order-1', role: 'entry' })
          : action === 'unlink' ? current.unlink('link-1') : current.dismiss(discrepancyFixture.id)).catch(() => undefined)
      }
      await act(async () => {})
      let oldWork!: Promise<void>, retry!: Promise<void>
      act(() => { oldWork = invoke() })
      expect(view.result.current.busy).toBe(true)
      view.rerender({ id: null })
      expect((request.mock.calls[0]!.at(-1) as AbortSignal).aborted).toBe(true)
      if (timing === 'before return') await act(async () => { settle(); await oldWork })
      view.rerender({ id: execution.playId })
      await act(async () => {})
      expect(view.result.current.busy).toBe(false)
      expect(view.result.current.execution).toEqual(execution)
      expect(view.result.current.error).toBeNull()
      act(() => { retry = invoke() })
      expect(request).toHaveBeenCalledTimes(2)
      if (timing === 'during retry') await act(async () => { settle(); await oldWork })
      expect(view.result.current.busy).toBe(true)
      expect(view.result.current.execution).toEqual(execution)
      expect(view.result.current.error).toBeNull()
      expect(api.playExecution).toHaveBeenCalledTimes(2)
      const refreshed = { ...execution, notice: 'Refreshed stored facts.' }
      vi.mocked(api.playExecution).mockResolvedValue(refreshed)
      await act(async () => { rejectRetry(new ApiError('http', 'Current action failed.', 409)); await retry })
      if (timing === 'after retry') await act(async () => { settle(); await oldWork })
      expect(view.result.current.busy).toBe(false)
      expect(view.result.current.execution).toEqual(refreshed)
      expect(view.result.current.error).toBe('Current action failed.')
      expect(api.playExecution).toHaveBeenCalledTimes(3)
    })
  })
  it('cancels stale stored reads on API replacement and unmount', async () => {
    let finish!: (value: PlayExecution) => void
    const first = client(), second = client()
    vi.mocked(first.playExecution).mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const view = renderHook(({ api }) => usePlayExecution(api, execution.playId, 'open'), { initialProps: { api: first } })
    view.rerender({ api: second })
    expect(vi.mocked(first.playExecution).mock.calls[0]![1]!.aborted).toBe(true)
    await act(async () => { finish({ ...execution, status: 'closed' }) })
    expect(view.result.current.execution?.status).toBe('open')
    view.unmount()
    expect(vi.mocked(second.playExecution).mock.calls[0]![1]!.aborted).toBe(true)
  })

  it.each(['resolve', 'reject'] as const)('does not let an older initial read %s over an applied link', async outcome => {
    const initial = deferred()
    const api = client()
    const linked = { ...execution, notice: 'Linked facts.' }
    vi.mocked(api.playExecution).mockReturnValueOnce(initial.promise).mockResolvedValue(linked)
    api.linkOrder = vi.fn().mockResolvedValue(linked)
    const view = renderHook(() => usePlayExecution(api, execution.playId, 'open'))
    await act(async () => { await view.result.current.link({ orderId: 'order-1', role: 'entry' }) })
    expect(view.result.current.execution).toBe(linked)
    await act(async () => {
      if (outcome === 'resolve') initial.resolve({ ...execution, status: 'closed' })
      else initial.reject(new ApiError('http', 'Old read failed.', 502))
    })
    expect(view.result.current.execution).toBe(linked)
    expect(view.result.current.error).toBeNull()
    expect(view.result.current.busy).toBe(false)
    expect(api.runAccountMonitor).not.toHaveBeenCalled()
  })

  it.each(['resolve', 'reject'] as const)('ignores an older stored refresh that %ss after a newer response', async outcome => {
    const older = deferred()
    const api = client()
    vi.mocked(api.playExecution).mockReturnValueOnce(older.promise)
    const view = renderHook(({ generation }) => usePlayExecution(api, execution.playId, 'open', generation),
      { initialProps: { generation: 0 } })
    view.rerender({ generation: 1 })
    await act(async () => {})
    expect(vi.mocked(api.playExecution).mock.calls[0]![1]!.aborted).toBe(true)
    await act(async () => {
      if (outcome === 'resolve') older.resolve({ ...execution, status: 'closed' })
      else older.reject(new ApiError('http', 'Old read failed.', 502))
    })
    expect(view.result.current.execution).toBe(execution)
    expect(view.result.current.error).toBeNull()
  })

  it.each(['play', 'api', 'disconnect', 'return'] as const)('retires requests and old action callbacks on %s replacement', async replacement => {
    const initial = deferred(), mutation = deferred()
    const first = client(), second = client()
    vi.mocked(first.playExecution).mockReturnValueOnce(initial.promise)
    first.linkOrder = vi.fn().mockReturnValueOnce(mutation.promise)
    second.linkOrder = vi.fn()
    const view = renderHook(({ api, id, status }) => usePlayExecution(api, id, status),
      { initialProps: { api: first, id: execution.playId as string | null, status: 'open' } })
    const oldLink = view.result.current.link
    act(() => { void oldLink({ orderId: 'order-1', role: 'entry' }) })
    view.rerender({ api: replacement === 'api' ? second : first,
      id: replacement === 'disconnect' || replacement === 'return' ? null : replacement === 'play' ? 'other-play' : execution.playId,
      status: 'open' })
    if (replacement === 'return') view.rerender({ api: first, id: execution.playId, status: 'open' })
    expect(vi.mocked(first.playExecution).mock.calls[0]![1]!.aborted).toBe(true)
    expect(vi.mocked(first.linkOrder).mock.calls[0]![2]!.aborted).toBe(true)
    await act(async () => {})
    const current = view.result.current.execution
    await act(async () => {
      initial.resolve({ ...execution, status: 'closed', notice: 'Old read.' })
      mutation.resolve({ ...execution, status: 'closed', notice: 'Old link.' })
      await oldLink({ orderId: 'order-2', role: 'entry' })
    })
    expect(view.result.current.execution).toBe(current)
    expect(view.result.current.error).toBeNull()
    expect(view.result.current.busy).toBe(false)
    expect(first.linkOrder).toHaveBeenCalledOnce()
    expect(second.linkOrder).not.toHaveBeenCalled()
  })

  it('rereads on a status change without aborting a link already sent, and shows its failure', async () => {
    const mutation = deferred()
    const api = client()
    api.linkOrder = vi.fn().mockReturnValueOnce(mutation.promise)
    const view = renderHook(({ status }) => usePlayExecution(api, execution.playId, status), { initialProps: { status: 'planned' } })
    await act(async () => {})
    let pending!: Promise<void>
    act(() => { pending = view.result.current.link({ orderId: 'order-1', role: 'entry' }) })
    view.rerender({ status: 'open' })
    expect(vi.mocked(api.linkOrder).mock.calls[0]![2]!.aborted).toBe(false)
    expect(vi.mocked(api.playExecution).mock.calls[0]![1]!.aborted).toBe(true)
    expect(api.playExecution).toHaveBeenCalledTimes(2)
    expect(view.result.current.busy).toBe(true)
    await act(async () => { mutation.reject(new ApiError('http', 'The order belongs to another Play.', 409)); await pending })
    expect(view.result.current.error).toBe('The order belongs to another Play.')
    expect(view.result.current.busy).toBe(false)
  })

  it('handles StrictMode replay and aborts a pending mutation on unmount', async () => {
    const old = deferred(), current = deferred(), mutation = deferred()
    const api = client()
    vi.mocked(api.playExecution).mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise)
    api.unlinkOrder = vi.fn().mockReturnValueOnce(mutation.promise)
    const view = renderHook(() => usePlayExecution(api, execution.playId, 'open'), { wrapper: StrictMode })
    expect(api.playExecution).toHaveBeenCalledTimes(2)
    expect(vi.mocked(api.playExecution).mock.calls[0]![1]!.aborted).toBe(true)
    await act(async () => { current.resolve(execution); old.resolve({ ...execution, status: 'closed' }) })
    expect(view.result.current.execution).toBe(execution)
    expect(view.result.current.busy).toBe(false)
    act(() => { void view.result.current.unlink('link-1') })
    view.unmount()
    expect(vi.mocked(api.unlinkOrder).mock.calls[0]![2]!.aborted).toBe(true)
    await act(async () => { mutation.resolve({ ...execution, status: 'closed' }) })
    expect(api.playExecution).toHaveBeenCalledTimes(2)
    expect(api.runAccountMonitor).not.toHaveBeenCalled()
  })

  it('keeps a failed read stale through action errors and through actions that return no execution (second Sol review)', async () => {
    const api = client()
    const reads = [deferred(), deferred(), deferred()]
    api.playExecution = vi.fn().mockResolvedValueOnce(execution)
      .mockImplementationOnce(() => reads[0]!.promise).mockImplementationOnce(() => reads[1]!.promise).mockImplementationOnce(() => reads[2]!.promise)
    api.dismissPlayDiscrepancy = vi.fn().mockRejectedValueOnce(new ApiError('http', 'Dismissal was refused.', 409)).mockResolvedValueOnce(undefined)
    const view = renderHook(() => usePlayExecution(api, execution.playId, 'open'))
    await act(async () => {})
    expect(view.result.current.stale).toBe(false)
    // The next read fails: the execution shown is an earlier answer.
    act(() => view.result.current.reload())
    await act(async () => reads[0]!.reject(new ApiError('unavailable', 'Execution unavailable.')))
    expect(view.result.current.stale).toBe(true)
    // A failed action replaces the message, not the staleness, also while its reload is pending.
    await act(async () => { await view.result.current.dismiss(discrepancyFixture.id).catch(() => undefined) })
    expect(view.result.current.error).toBe('Dismissal was refused.')
    expect(view.result.current.stale).toBe(true)
    // A successful action that returns no execution leaves it stale until a read succeeds.
    await act(async () => { await view.result.current.dismiss(discrepancyFixture.id) })
    expect(view.result.current.error).toBeNull()
    expect(view.result.current.stale).toBe(true)
    await act(async () => reads[2]!.resolve(execution))
    expect(view.result.current.stale).toBe(false)
  })

  it('keeps a failed read stale when an overlapping action superseded it (third Sol review)', async () => {
    const api = client()
    const reread = deferred()
    const followUp = deferred()
    let failDismissal!: (cause: unknown) => void
    api.playExecution = vi.fn().mockResolvedValueOnce(execution).mockImplementationOnce(() => reread.promise).mockImplementationOnce(() => followUp.promise)
    api.dismissPlayDiscrepancy = vi.fn(() => new Promise<void>((_resolve, reject) => { failDismissal = reject }))
    const view = renderHook(() => usePlayExecution(api, execution.playId, 'open'))
    await act(async () => {})
    expect(view.result.current.stale).toBe(false)
    // A reread starts, then a dismissal starts before it answers.
    act(() => view.result.current.reload())
    let dismissal!: Promise<void>
    act(() => { dismissal = view.result.current.dismiss(discrepancyFixture.id).catch(() => undefined) })
    // The reread fails: superseded by the dismissal or not, the shown execution is now uncertain.
    await act(async () => reread.reject(new ApiError('unavailable', 'Execution unavailable.')))
    expect(view.result.current.stale).toBe(true)
    // The dismissal fails too, and its follow-up reload stays pending: still uncertain.
    await act(async () => { failDismissal(new ApiError('http', 'Dismissal was refused.', 409)); await dismissal })
    expect(api.playExecution).toHaveBeenCalledTimes(3)
    expect(view.result.current.stale).toBe(true)
    await act(async () => followUp.resolve(execution))
    expect(view.result.current.stale).toBe(false)
  })

  it('does not mark known facts uncertain while a routine refresh is only pending', async () => {
    const api = client()
    const refresh = deferred()
    api.playExecution = vi.fn().mockResolvedValueOnce(execution).mockImplementationOnce(() => refresh.promise)
    const view = renderHook(({ generation }) => usePlayExecution(api, execution.playId, 'open', generation), { initialProps: { generation: 0 } })
    await act(async () => {})
    view.rerender({ generation: 1 })
    expect(api.playExecution).toHaveBeenCalledTimes(2)
    expect(view.result.current.stale).toBe(false)
    await act(async () => refresh.resolve(execution))
    expect(view.result.current.stale).toBe(false)
  })
})


describe('turning down an order (phase 1, 1.6d)', () => {
  it('turns a whole order down in one request, as one action', async () => {
    const api = client()
    const answer = deferred()
    api.rejectOrder = vi.fn().mockReturnValueOnce(answer.promise)
    api.unlinkOrder = vi.fn()
    const view = renderHook(() => usePlayExecution(api, execution.playId, 'open', 0))
    await act(async () => {})
    let done!: Promise<unknown>
    act(() => { done = view.result.current.reject('21') })
    expect(view.result.current.busy).toBe(true)
    await act(async () => { answer.resolve(execution); await done })
    expect(api.rejectOrder).toHaveBeenCalledExactlyOnceWith(execution.playId, '21', expect.any(AbortSignal))
    expect(api.unlinkOrder).not.toHaveBeenCalled()
    expect(view.result.current.busy).toBe(false)
  })
})
