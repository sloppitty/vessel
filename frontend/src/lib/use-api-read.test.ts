import { act, renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/errors'
import { useApiRead, type ApiReadOptions } from './use-api-read'

interface Call { key: string; signal: AbortSignal; resolve: (value: string) => void; reject: (cause: unknown) => void }

/** An API whose reads settle only when the test says so. */
function deferredApi() {
  const calls: Call[] = []
  const api = {
    read: vi.fn((key: string, signal: AbortSignal) => new Promise<string>((resolve, reject) => { calls.push({ key, signal, resolve, reject }) })),
  }
  return { api, calls }
}
type Api = ReturnType<typeof deferredApi>['api']
type Props = { api: Api; keyValue: string; options?: Partial<ApiReadOptions<string>> }

function setup(initial: Props) {
  return renderHook(({ api, keyValue, options }: Props) =>
    useApiRead(api, keyValue, (client, signal) => client.read(keyValue, signal), { fallback: 'Could not load.', ...options }), { initialProps: initial })
}

const flush = () => act(async () => { await Promise.resolve(); await Promise.resolve() })

describe('useApiRead', () => {
  it('loads, then shows the answer', async () => {
    const { api, calls } = deferredApi()
    const { result } = setup({ api, keyValue: 'a' })
    expect(result.current).toMatchObject({ data: null, error: null, loading: true })
    await flush()
    await act(async () => calls[0]!.resolve('A'))
    expect(result.current).toMatchObject({ data: 'A', error: null, loading: false, latest: 'A' })
  })

  it('starts the read as the effect runs', () => {
    const { api, calls } = deferredApi()
    setup({ api, keyValue: 'a' })
    expect(calls).toHaveLength(1)
  })

  it('shows only API-safe failure text', async () => {
    const { api, calls } = deferredApi()
    const { result } = setup({ api, keyValue: 'a' })
    await flush()
    await act(async () => calls[0]!.reject(new Error('raw body <script>')))
    expect(result.current).toMatchObject({ data: null, error: 'Could not load.', loading: false })
    act(() => result.current.reload())
    await flush()
    const cause = new ApiError('http', 'Vessel refused this request.', 403)
    await act(async () => calls[1]!.reject(cause))
    expect(result.current).toMatchObject({ error: 'Vessel refused this request.', cause })
  })

  it('treats a client that throws synchronously as a failed read', async () => {
    const api: { read: (key: string, signal: AbortSignal) => Promise<string> } = { read: () => { throw new ApiError('http', 'Invalid identifier.') } }
    const { result } = renderHook(() => useApiRead(api, 'x', (client, signal) => client.read('x', signal), { fallback: 'Could not load.' }))
    await waitFor(() => expect(result.current.error).toBe('Invalid identifier.'))
  })

  it('aborts and discards the answer to a superseded key', async () => {
    const { api, calls } = deferredApi()
    const { result, rerender } = setup({ api, keyValue: 'a' })
    await flush()
    rerender({ api, keyValue: 'b' })
    await flush()
    expect(calls[0]!.signal.aborted).toBe(true)
    await act(async () => calls[0]!.resolve('stale A'))
    expect(result.current).toMatchObject({ data: null, loading: true })
    await act(async () => calls[1]!.resolve('B'))
    expect(result.current).toMatchObject({ data: 'B', loading: false })
  })

  it('never applies an answer for a replaced API', async () => {
    const first = deferredApi(), second = deferredApi()
    const { result, rerender } = setup({ api: first.api, keyValue: 'a' })
    await flush()
    await act(async () => first.calls[0]!.resolve('old'))
    rerender({ api: second.api, keyValue: 'a', options: { keepPrevious: 'always' } })
    // Even "always" keeps answers of the same API only.
    expect(result.current).toMatchObject({ data: null, loading: true, latest: null })
    await flush()
    expect(second.calls).toHaveLength(1)
    await act(async () => second.calls[0]!.resolve('new'))
    expect(result.current.data).toBe('new')
  })

  it('aborts on unmount', async () => {
    const { api, calls } = deferredApi()
    const { unmount } = setup({ api, keyValue: 'a' })
    await flush()
    unmount()
    expect(calls[0]!.signal.aborted).toBe(true)
  })

  it('reads again on reload and on a new reload key, blanking without keepPrevious', async () => {
    const { api, calls } = deferredApi()
    const { result, rerender } = setup({ api, keyValue: 'a', options: { reloadKey: 0 } })
    await flush()
    await act(async () => calls[0]!.resolve('A1'))
    act(() => result.current.reload())
    expect(result.current).toMatchObject({ data: null, loading: true, latest: 'A1' })
    await flush()
    await act(async () => calls[1]!.resolve('A2'))
    rerender({ api, keyValue: 'a', options: { reloadKey: 1 } })
    expect(result.current).toMatchObject({ data: null, loading: true })
    await flush()
    expect(calls).toHaveLength(3)
  })

  it('keeps the last answer for the same key while a reload runs, and only then', async () => {
    const { api, calls } = deferredApi()
    const options = { keepPrevious: 'reload' as const }
    const { result, rerender } = setup({ api, keyValue: 'a', options: { ...options, reloadKey: 0 } })
    await flush()
    await act(async () => calls[0]!.resolve('A1'))
    rerender({ api, keyValue: 'a', options: { ...options, reloadKey: 1 } })
    expect(result.current).toMatchObject({ data: 'A1', loading: true })
    await flush()
    await act(async () => calls[1]!.resolve('A2'))
    expect(result.current).toMatchObject({ data: 'A2', loading: false })
    // Another key is another question: its old answer is not shown as current.
    rerender({ api, keyValue: 'b', options: { ...options, reloadKey: 1 } })
    expect(result.current).toMatchObject({ data: null, loading: true, latest: 'A2' })
  })

  it('never keeps a failed answer, and keeps the last success as latest', async () => {
    const { api, calls } = deferredApi()
    const { result } = setup({ api, keyValue: 'a', options: { keepPrevious: 'reload' } })
    await flush()
    await act(async () => calls[0]!.resolve('A1'))
    act(() => result.current.reload())
    await flush()
    await act(async () => calls[1]!.reject(new Error('down')))
    expect(result.current).toMatchObject({ data: null, error: 'Could not load.', latest: 'A1' })
    act(() => result.current.reload())
    expect(result.current).toMatchObject({ data: null, error: null, loading: true, latest: 'A1' })
  })

  it('keeps any earlier answer of the same API with keepPrevious always', async () => {
    const { api, calls } = deferredApi()
    const { result, rerender } = setup({ api, keyValue: 'a', options: { keepPrevious: 'always' } })
    await flush()
    await act(async () => calls[0]!.resolve('A'))
    rerender({ api, keyValue: 'b', options: { keepPrevious: 'always' } })
    expect(result.current).toMatchObject({ data: 'A', loading: true })
  })

  it('loads again when the key returns to an earlier one before the other settles', async () => {
    const { api, calls } = deferredApi()
    const { result, rerender } = setup({ api, keyValue: 'a' })
    await flush()
    await act(async () => calls[0]!.resolve('A'))
    rerender({ api, keyValue: 'b' })
    await flush()
    rerender({ api, keyValue: 'a' })
    await flush()
    expect(calls).toHaveLength(3)
    // A fresh read of "a" is in flight: the earlier answer is not this request's.
    expect(result.current).toMatchObject({ data: null, error: null, loading: true })
    await act(async () => calls[2]!.resolve('A2'))
    expect(result.current).toMatchObject({ data: 'A2', loading: false })
  })

  it('keeps the earlier answer for a returning key only when asked to', async () => {
    const { api, calls } = deferredApi()
    const { result, rerender } = setup({ api, keyValue: 'a', options: { keepPrevious: 'reload' } })
    await flush()
    await act(async () => calls[0]!.resolve('A'))
    rerender({ api, keyValue: 'b', options: { keepPrevious: 'reload' } })
    await flush()
    rerender({ api, keyValue: 'a', options: { keepPrevious: 'reload' } })
    await flush()
    expect(result.current).toMatchObject({ data: 'A', loading: true })
  })

  it('loads again after being disabled and enabled, without showing the old answer or failure', async () => {
    const { api, calls } = deferredApi()
    const { result, rerender } = setup({ api, keyValue: 'a' })
    await flush()
    await act(async () => calls[0]!.reject(new Error('boom')))
    expect(result.current).toMatchObject({ error: 'Could not load.', loading: false })
    rerender({ api, keyValue: 'a', options: { enabled: false } })
    await flush()
    rerender({ api, keyValue: 'a', options: { enabled: true } })
    await flush()
    expect(calls).toHaveLength(2)
    expect(result.current).toMatchObject({ data: null, error: null, loading: true })
  })

  it('reads nothing while disabled', async () => {
    const { api } = deferredApi()
    const { result } = setup({ api, keyValue: 'a', options: { enabled: false } })
    await flush()
    expect(api.read).not.toHaveBeenCalled()
    expect(result.current).toMatchObject({ data: null, error: null, loading: false })
  })

  describe('answers (a clamped page)', () => {
    // A read of "page5" answers "page3", as a server clamps a page past the end.
    const answers = (data: string, key: unknown) => data === 'P3' ? 'page3' : key

    it('is current for the key asked, and adopting the key it answers reads nothing', async () => {
      const { api, calls } = deferredApi()
      const { result, rerender } = setup({ api, keyValue: 'page5', options: { answers } })
      await flush()
      await act(async () => calls[0]!.resolve('P3'))
      expect(result.current).toMatchObject({ data: 'P3', loading: false })
      rerender({ api, keyValue: 'page3', options: { answers } })
      await flush()
      expect(calls).toHaveLength(1)
      expect(result.current).toMatchObject({ data: 'P3', loading: false })
      act(() => result.current.reload())
      await flush()
      expect(calls).toHaveLength(2)
      expect(result.current).toMatchObject({ data: null, loading: true })
    })

    it('reads the answered key again once another key was read in between', async () => {
      const { api, calls } = deferredApi()
      const { result, rerender } = setup({ api, keyValue: 'page5', options: { answers } })
      await flush()
      await act(async () => calls[0]!.resolve('P3'))
      rerender({ api, keyValue: 'page1', options: { answers } })
      await flush()
      expect(result.current).toMatchObject({ data: null, loading: true })
      rerender({ api, keyValue: 'page3', options: { answers } })
      await flush()
      expect(calls.map(call => call.key)).toEqual(['page5', 'page1', 'page3'])
      expect(result.current).toMatchObject({ data: null, loading: true })
    })

    it('reads again when the answered key comes back after being disabled', async () => {
      const { api, calls } = deferredApi()
      const { result, rerender } = setup({ api, keyValue: 'page5', options: { answers } })
      await flush()
      await act(async () => calls[0]!.resolve('P3'))
      rerender({ api, keyValue: 'page5', options: { answers, enabled: false } })
      await flush()
      rerender({ api, keyValue: 'page3', options: { answers } })
      await flush()
      expect(calls).toHaveLength(2)
      expect(result.current).toMatchObject({ data: null, loading: true })
    })
  })

  it('waits for delay before reading, and a key that changes meanwhile reads only once', async () => {
    vi.useFakeTimers()
    try {
      const { api, calls } = deferredApi()
      const { result, rerender } = setup({ api, keyValue: 'a', options: { delay: 300 } })
      expect(result.current).toMatchObject({ data: null, loading: true })
      act(() => { vi.advanceTimersByTime(200) })
      rerender({ api, keyValue: 'b', options: { delay: 300 } })
      act(() => { vi.advanceTimersByTime(299) })
      expect(calls).toHaveLength(0)
      act(() => { vi.advanceTimersByTime(1) })
      expect(calls.map(call => call.key)).toEqual(['b'])
      await act(async () => calls[0]!.resolve('B'))
      expect(result.current).toMatchObject({ data: 'B', loading: false })
    } finally { vi.useRealTimers() }
  })

  it('uses the read of the render that started it, without reading again when only the function changes', async () => {
    const { api, calls } = deferredApi()
    const { rerender } = setup({ api, keyValue: 'a' })
    await flush()
    rerender({ api, keyValue: 'a' })
    rerender({ api, keyValue: 'a' })
    await flush()
    expect(calls).toHaveLength(1)
  })
})
