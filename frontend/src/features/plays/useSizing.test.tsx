import { act, renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { SizingDocument } from '@/api/sizing'
import { ApiError } from '@/api/errors'
import type { WorkspaceApi } from '@/api/workspace'
import { sizingFixture } from '@/test/workspace-fixture'
import { useSizing } from './useSizing'

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (cause: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

describe('sizing document reads', () => {
  it('does not reread when the page is hidden, and catches up once with the latest generation when shown', async () => {
    const api = { sizing: vi.fn().mockResolvedValue({}) } as unknown as WorkspaceApi
    const view = renderHook(({ generation, enabled }) => useSizing(api, generation, enabled), { initialProps: { generation: 3, enabled: true } })
    await act(async () => {})
    expect(api.sizing).toHaveBeenCalledOnce()
    view.rerender({ generation: 3, enabled: false })
    view.rerender({ generation: 4, enabled: false })
    view.rerender({ generation: 5, enabled: false })
    await act(async () => {})
    expect(api.sizing).toHaveBeenCalledOnce()
    view.rerender({ generation: 5, enabled: true })
    await act(async () => {})
    expect(api.sizing).toHaveBeenCalledTimes(2)
    view.rerender({ generation: 5, enabled: false })
    view.rerender({ generation: 5, enabled: true })
    await act(async () => {})
    expect(api.sizing).toHaveBeenCalledTimes(2)
  })

  // Phase 0, 0.1 (D-13): a failed reload no longer leaves the old record looking current.
  it('is loading, then ready; a failed reload keeps the last record as stale until Retry succeeds', async () => {
    const updated: SizingDocument = { ...sizingFixture, settings: { riskPercent: '1.5' } }
    const sizing = vi.fn().mockResolvedValueOnce(sizingFixture).mockRejectedValueOnce(new ApiError('unavailable', 'Down.')).mockResolvedValueOnce(updated)
    const api = { sizing } as unknown as WorkspaceApi
    const view = renderHook(({ generation }) => useSizing(api, generation), { initialProps: { generation: 0 } })
    expect(view.result.current.status).toBe('loading')
    await waitFor(() => expect(view.result.current.status).toBe('ready'))
    view.rerender({ generation: 1 })
    await waitFor(() => expect(view.result.current.status).toBe('stale'))
    expect(view.result.current.sizing).toEqual(sizingFixture)
    act(() => view.result.current.retry())
    await waitFor(() => expect(view.result.current.status).toBe('ready'))
    expect(view.result.current.sizing).toEqual(updated)
    expect(sizing).toHaveBeenCalledTimes(3)
  })

  it('fails without a record, and Retry reads again', async () => {
    const sizing = vi.fn().mockRejectedValueOnce(new ApiError('unavailable', 'Down.')).mockResolvedValueOnce(sizingFixture)
    const api = { sizing } as unknown as WorkspaceApi
    const { result } = renderHook(() => useSizing(api))
    await waitFor(() => expect(result.current.status).toBe('failed'))
    expect(result.current.sizing).toBeNull()
    act(() => result.current.retry())
    // Still failed while the retry reads: only a successful read makes the record current.
    expect(result.current.status).toBe('failed')
    await waitFor(() => expect(result.current.status).toBe('ready'))
    expect(result.current.sizing).toEqual(sizingFixture)
  })

  // Review of 0.1: a retry or reload after a failure must not make the old record current before it succeeds.
  it('keeps a stale record stale while Retry or a new generation reads, until a read succeeds', async () => {
    const retried = deferred<SizingDocument>()
    const reloaded = deferred<SizingDocument>()
    const updated: SizingDocument = { ...sizingFixture, settings: { riskPercent: '2' } }
    const sizing = vi.fn().mockResolvedValueOnce(sizingFixture).mockRejectedValueOnce(new ApiError('unavailable', 'Down.'))
      .mockImplementationOnce(() => retried.promise).mockImplementationOnce(() => reloaded.promise)
    const api = { sizing } as unknown as WorkspaceApi
    const view = renderHook(({ generation }) => useSizing(api, generation), { initialProps: { generation: 0 } })
    await waitFor(() => expect(view.result.current.status).toBe('ready'))
    view.rerender({ generation: 1 })
    await waitFor(() => expect(view.result.current.status).toBe('stale'))
    act(() => view.result.current.retry())
    await act(async () => {})
    expect(sizing).toHaveBeenCalledTimes(3)
    expect(view.result.current.status).toBe('stale')
    view.rerender({ generation: 2 })
    await act(async () => {})
    expect(view.result.current.status).toBe('stale')
    await act(async () => { retried.resolve(updated) })
    expect(view.result.current.status).toBe('stale')
    await act(async () => { reloaded.resolve(updated) })
    expect(view.result.current).toMatchObject({ status: 'ready', sizing: updated })
  })

  it('ignores a read that started before an accepted risk change', async () => {
    const reading = deferred<SizingDocument>()
    const changed: SizingDocument = { ...sizingFixture, settings: { riskPercent: '1.5' } }
    const api = { sizing: vi.fn().mockResolvedValueOnce(sizingFixture).mockImplementationOnce(() => reading.promise),
      updateSizingSettings: vi.fn().mockResolvedValue(changed) } as unknown as WorkspaceApi
    const view = renderHook(({ generation }) => useSizing(api, generation), { initialProps: { generation: 0 } })
    await waitFor(() => expect(view.result.current.status).toBe('ready'))
    view.rerender({ generation: 1 })
    await act(async () => { await view.result.current.updateRisk('1.5') })
    expect(view.result.current).toMatchObject({ status: 'ready', sizing: changed })
    // The older read resolves with the old risk afterwards and is ignored.
    await act(async () => { reading.resolve(sizingFixture) })
    expect(view.result.current).toMatchObject({ status: 'ready', sizing: changed })
  })

  it('settles on the new API when a risk change for the replaced one finishes late', async () => {
    const saving = deferred<SizingDocument>()
    const first = { sizing: vi.fn().mockResolvedValue(sizingFixture), updateSizingSettings: vi.fn(() => saving.promise) } as unknown as WorkspaceApi
    const replacement: SizingDocument = { ...sizingFixture, settings: { riskPercent: '0.5' } }
    const second = { sizing: vi.fn().mockResolvedValue(replacement) } as unknown as WorkspaceApi
    const view = renderHook(({ api }) => useSizing(api), { initialProps: { api: first } })
    await waitFor(() => expect(view.result.current.status).toBe('ready'))
    let change!: Promise<void>
    act(() => { change = view.result.current.updateRisk('1.5') })
    view.rerender({ api: second })
    await waitFor(() => expect(view.result.current).toMatchObject({ status: 'ready', sizing: replacement }))
    await act(async () => { saving.resolve({ ...sizingFixture, settings: { riskPercent: '1.5' } }); await change })
    expect(view.result.current).toMatchObject({ status: 'ready', sizing: replacement })
  })

  it('does not treat an aborted read as a failure', async () => {
    const first = deferred<SizingDocument>()
    const second = deferred<SizingDocument>()
    const sizing = vi.fn().mockResolvedValueOnce(sizingFixture)
      .mockImplementationOnce((signal: AbortSignal) => { signal.addEventListener('abort', () => first.reject(new DOMException('Aborted', 'AbortError'))); return first.promise })
      .mockImplementationOnce(() => second.promise)
    const api = { sizing } as unknown as WorkspaceApi
    const view = renderHook(({ generation }) => useSizing(api, generation), { initialProps: { generation: 0 } })
    await waitFor(() => expect(view.result.current.status).toBe('ready'))
    view.rerender({ generation: 1 })
    view.rerender({ generation: 2 })
    await act(async () => {})
    expect(view.result.current.status).toBe('loading')
    expect(view.result.current.sizing).toEqual(sizingFixture)
    await act(async () => { second.resolve(sizingFixture) })
    expect(view.result.current.status).toBe('ready')
  })

  it('takes the document a risk change returns as current', async () => {
    const updated: SizingDocument = { ...sizingFixture, settings: { riskPercent: '1.5' } }
    const api = { sizing: vi.fn().mockRejectedValue(new ApiError('unavailable', 'Down.')), updateSizingSettings: vi.fn().mockResolvedValue(updated) } as unknown as WorkspaceApi
    const { result } = renderHook(() => useSizing(api))
    await waitFor(() => expect(result.current.status).toBe('failed'))
    await act(async () => { await result.current.updateRisk('1.5') })
    expect(result.current).toMatchObject({ status: 'ready', sizing: updated })
  })
})
