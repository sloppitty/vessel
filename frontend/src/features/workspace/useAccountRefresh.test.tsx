import { act, renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import { accountFixture } from '@/test/workspace-fixture'
import { useAccountRefresh } from './useAccountRefresh'
import { credentialsFixture } from '@/test/identity-fixture'

const accounts = [accountFixture, { ...accountFixture, id: 'second' }]

describe('account refresh session', () => {
  it.each(['cancel', 'unmount', 'API replacement'] as const)('suppresses rejected single refresh completion after %s', async action => {
    let reject!: (reason: Error) => void
    const api = { ...createWorkspaceApi(credentialsFixture), sync: vi.fn().mockImplementation(() => new Promise((_resolve, fail) => { reject = fail })) }
    const notify = vi.fn(), reload = vi.fn()
    const view = renderHook(({ api }) => useAccountRefresh({ api, refreshable: accounts, notify, reload }), { initialProps: { api } })
    let pending!: Promise<void>
    act(() => {
      pending = view.result.current.sync(accountFixture.id)
      view.result.current.enqueue({ id: 'queued', name: 'Queued account' })
    })
    expect(view.result.current.refreshing).toBe(accountFixture.id)
    if (action === 'cancel') act(() => view.result.current.cancel())
    else if (action === 'unmount') view.unmount()
    else view.rerender({ api: { ...api, sync: vi.fn().mockResolvedValue(accountFixture) } })
    expect(api.sync.mock.calls[0]![1].aborted).toBe(true)
    await act(async () => { reject(new Error('late credential')); await pending })
    expect(api.sync).toHaveBeenCalledTimes(1)
    expect(notify).not.toHaveBeenCalled()
    expect(reload).not.toHaveBeenCalled()
  })

  it('queues initial refreshes behind current work, deduplicates them and continues after a failure', async () => {
    let finishCurrent!: () => void, failSecond!: (reason: Error) => void
    const api = { ...createWorkspaceApi(credentialsFixture), sync: vi.fn()
      .mockImplementationOnce(() => new Promise<void>(resolve => { finishCurrent = resolve }))
      .mockImplementationOnce(() => new Promise<void>((_resolve, reject) => { failSecond = reject }))
      .mockResolvedValue(accountFixture) }
    const notify = vi.fn(), reload = vi.fn()
    const { result } = renderHook(() => useAccountRefresh({ api, refreshable: accounts, notify, reload }))
    let current!: Promise<void>
    act(() => {
      current = result.current.sync(accountFixture.id)
      result.current.enqueue(accountFixture)
      result.current.enqueue({ id: 'second', name: 'Second' })
      result.current.enqueue({ id: 'second', name: 'Second' })
      result.current.enqueue({ id: 'third', name: 'Third' })
    })
    expect(api.sync).toHaveBeenCalledTimes(1)
    await act(async () => { finishCurrent(); await current })
    expect(api.sync.mock.calls.map(([id]) => id)).toEqual([accountFixture.id, 'second'])
    expect(result.current.refreshProgress).toEqual({ current: 1, total: 2 })
    await act(async () => { failSecond(new Error('synthetic failure')) })
    await waitFor(() => expect(result.current.refreshing).toBeNull())
    expect(api.sync.mock.calls.map(([id]) => id)).toEqual([accountFixture.id, 'second', 'third'])
    expect(notify).toHaveBeenLastCalledWith({
      tone: 'error', key: 'account-refresh', message: '1 of 2 accounts refreshed. Failed: Second.',
    })
    expect(reload).toHaveBeenCalledTimes(2)
  })

  it('starts an initial refresh without requiring the new account in the last overview', async () => {
    const api = { ...createWorkspaceApi(credentialsFixture), sync: vi.fn().mockResolvedValue(accountFixture) }
    const notify = vi.fn(), reload = vi.fn()
    const { result } = renderHook(() => useAccountRefresh({ api, refreshable: [], notify, reload }))
    act(() => result.current.enqueue(accountFixture))
    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1))
    expect(api.sync).toHaveBeenCalledExactlyOnceWith(accountFixture.id, expect.any(AbortSignal))
  })

  it('does not let an old batch clear or advance a new API refresh', async () => {
    let completeOld!: () => void, completeNew!: () => void
    const oldApi = { ...createWorkspaceApi(credentialsFixture), sync: vi.fn().mockImplementation(() => new Promise<void>(resolve => { completeOld = resolve })) }
    const nextApi = { ...oldApi, sync: vi.fn().mockImplementation(() => new Promise<void>(resolve => { completeNew = resolve })) }
    const notify = vi.fn(), reload = vi.fn()
    const { result, rerender } = renderHook(({ api }) => useAccountRefresh({ api, refreshable: accounts, notify, reload }), { initialProps: { api: oldApi } })
    const oldEnqueue = result.current.enqueue
    let old!: Promise<void>, next!: Promise<void>
    act(() => {
      old = result.current.refreshAll()
      result.current.enqueue({ id: 'old-queued', name: 'Old queued account' })
    })
    rerender({ api: nextApi })
    act(() => {
      oldEnqueue({ id: 'late-created', name: 'Late old creation' })
      next = result.current.sync('new-account')
    })
    await act(async () => { completeOld(); await old })
    expect(oldApi.sync).toHaveBeenCalledTimes(1)
    expect(nextApi.sync).toHaveBeenCalledTimes(1)
    expect(result.current.refreshing).toBe('new-account')
    expect(notify).not.toHaveBeenCalled()
    expect(reload).not.toHaveBeenCalled()
    await act(async () => { completeNew(); await next })
    expect(result.current.refreshing).toBeNull()
    expect(notify).toHaveBeenCalledExactlyOnceWith({ tone: 'success', message: 'Account refreshed. Recent execution facts remain unassigned to plays.' })
    expect(reload).toHaveBeenCalledTimes(1)
  })
})
