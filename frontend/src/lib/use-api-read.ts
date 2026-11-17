import { useCallback, useEffect, useEffectEvent, useState } from 'react'
import { errorMessage } from '@/api/errors'

export interface ApiReadOptions {
  /** Shown for a failure that carries no API-safe text. */
  fallback: string
  /** False reads nothing: no data, no error, not loading. */
  enabled?: boolean
  /** Another read of the same `key` whenever this changes (compared with `Object.is`), such as a shell reload generation. */
  reloadKey?: unknown
  /**
   * What stays on screen while a read runs. `reload`: the last answer for the same `key`, so a reload never blanks the
   * page. `always`: the last answer for this API, whatever its key. An answer that failed is never kept.
   */
  keepPrevious?: 'reload' | 'always'
}

export interface ApiRead<T> {
  /** The answer to the current read, or a kept earlier answer while it runs (see `keepPrevious`). */
  data: T | null
  /** API-safe text for the current read's failure. */
  error: string | null
  /** What the current read failed with, for callers that branch on its status. */
  cause: unknown
  /** True from the start of a read until it settles. */
  loading: boolean
  /** The last successful answer for this API, whatever its key, kept through failures. */
  latest: T | null
  /** Reads the current key again. */
  reload: () => void
}

interface Settled<T> {
  api: unknown; key: unknown; reloadKey: unknown; count: number
  data: T | null; error: string | null; cause: unknown; latest: T | null
}

/**
 * One read of `api` for `key`, compared with `Object.is` like an effect dependency. A new API, key, `reloadKey` or
 * `reload()` aborts the read in flight, as does unmounting, and an answer that belongs to a superseded request or a
 * replaced API is never applied. `read` may change on every render; the read uses the one from the render that started it.
 */
export function useApiRead<A, T>(api: A, key: unknown, read: (api: A, signal: AbortSignal) => Promise<T>,
  { fallback, enabled = true, reloadKey, keepPrevious }: ApiReadOptions): ApiRead<T> {
  const [count, setCount] = useState(0)
  const [settled, setSettled] = useState<Settled<T> | null>(null)
  const start = useEffectEvent((signal: AbortSignal) => read(api, signal))
  const failed = useEffectEvent((cause: unknown) => errorMessage(cause, fallback))
  useEffect(() => {
    if (!enabled) return
    const controller = new AbortController()
    const done = (data: T | null, error: string | null, cause: unknown) => setSettled(previous => ({ api, key, reloadKey, count, data, error, cause,
      latest: error === null ? data : previous?.api === api ? previous.latest : null }))
    // The read starts here, as the effect runs. A client that refuses its arguments synchronously fails like any other read.
    let pending: Promise<T>
    try { pending = start(controller.signal) } catch (cause) { pending = Promise.reject(cause) }
    pending.then(data => {
      if (!controller.signal.aborted) done(data, null, undefined)
    }, (cause: unknown) => { if (!controller.signal.aborted) done(null, failed(cause), cause) })
    return () => controller.abort()
  }, [api, key, reloadKey, count, enabled])
  const reload = useCallback(() => setCount(value => value + 1), [])
  if (!enabled) return { data: null, error: null, cause: undefined, loading: false, latest: null, reload }
  const own = settled?.api === api ? settled : null
  const current = own && Object.is(own.key, key) && Object.is(own.reloadKey, reloadKey) && own.count === count ? own : null
  const kept = !current && own && own.error === null && (keepPrevious === 'always' || keepPrevious === 'reload' && Object.is(own.key, key))
    ? own.data : null
  return {
    data: current ? current.data : kept, error: current?.error ?? null, cause: current?.cause, loading: current === null,
    latest: own?.latest ?? null, reload,
  }
}
