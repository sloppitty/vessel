import { useCallback, useEffect, useEffectEvent, useRef, useState } from 'react'
import { errorMessage } from '@/api/errors'

export interface ApiReadOptions<T = unknown> {
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
  /**
   * The key an answer belongs to, given the key it was read for, when the server may answer another question than the one
   * asked (a page past the end comes back clamped). The answer is current for both keys, and moving to the key it answers
   * next shows it without reading it again.
   */
  answers?: (data: T, key: unknown) => unknown
  /** Milliseconds to wait before reading, so a key that keeps changing (typing) reads once it settles. */
  delay?: number
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
  /** The key the answer belongs to (see `answers`); the read key unless the server answered another one. */
  answered: unknown
  data: T | null; error: string | null; cause: unknown; latest: T | null
  /** Set when the request that produced this ended its turn: a later request with the same parameters is not this one. */
  superseded?: boolean
}

type Request = { api: unknown; key: unknown; reloadKey: unknown; count: number }
const sameRequest = (a: Request, b: Request) =>
  a.api === b.api && Object.is(a.key, b.key) && Object.is(a.reloadKey, b.reloadKey) && a.count === b.count

/**
 * One read of `api` for `key`, compared with `Object.is` like an effect dependency. A new API, key, `reloadKey` or
 * `reload()` aborts the read in flight, as does unmounting, and an answer that belongs to a superseded request or a
 * replaced API is never applied. `read` may change on every render; the read uses the one from the render that started it.
 */
export function useApiRead<A, T>(api: A, key: unknown, read: (api: A, signal: AbortSignal) => Promise<T>,
  { fallback, enabled = true, reloadKey, keepPrevious, answers, delay }: ApiReadOptions<T>): ApiRead<T> {
  const [count, setCount] = useState(0)
  const [settled, setSettled] = useState<Settled<T> | null>(null)
  // The same as `settled`, for the effect and its cleanup.
  const last = useRef<Settled<T> | null>(null)
  // The request an earlier answer already answers (see `answers`): the next turn shows it without reading.
  const adopted = useRef<Request | null>(null)
  const start = useEffectEvent((signal: AbortSignal) => read(api, signal))
  const failed = useEffectEvent((cause: unknown) => errorMessage(cause, fallback))
  const answeredKey = useEffectEvent((data: T, asked: unknown) => answers ? answers(data, asked) : asked)
  // The parameters of the render that ends a turn, read as its cleanup runs.
  const next = useEffectEvent(() => enabled ? { api, key, reloadKey, count } : null)
  useEffect(() => {
    if (!enabled) return
    const settle = (next: Settled<T>) => { last.current = next; setSettled(next) }
    const request = { api, key, reloadKey, count }
    const adopt = adopted.current
    adopted.current = null
    const end = () => {
      const previous = last.current, upcoming = next()
      if (!previous || previous.superseded) return
      // Moving straight from the key asked to the key its answer belongs to keeps that answer current.
      if (sameRequest(previous, request) && !Object.is(previous.answered, previous.key) && upcoming !== null &&
        sameRequest(upcoming, { ...request, key: previous.answered })) adopted.current = upcoming
      else settle({ ...previous, superseded: true })
    }
    if (adopt && sameRequest(adopt, request)) return end
    const controller = new AbortController()
    const done = (data: T | null, error: string | null, cause: unknown) => settle({ ...request,
      answered: error === null ? answeredKey(data as T, key) : key, data, error, cause,
      latest: error === null ? data : last.current?.api === api ? last.current.latest : null })
    const begin = () => {
      // A client that refuses its arguments synchronously fails like any other read.
      let pending: Promise<T>
      try { pending = start(controller.signal) } catch (cause) { pending = Promise.reject(cause) }
      pending.then(data => {
        if (!controller.signal.aborted) done(data, null, undefined)
      }, (cause: unknown) => { if (!controller.signal.aborted) done(null, failed(cause), cause) })
    }
    // The read starts here, as the effect runs, unless it waits for `delay`.
    const timer = delay ? setTimeout(begin, delay) : undefined
    if (!delay) begin()
    return () => {
      clearTimeout(timer)
      controller.abort()
      // Matching parameters do not identify a request: returning to an earlier key, or enabling again, starts a new one.
      end()
    }
  }, [api, key, reloadKey, count, enabled, delay])
  const reload = useCallback(() => setCount(value => value + 1), [])
  if (!enabled) return { data: null, error: null, cause: undefined, loading: false, latest: null, reload }
  const own = settled?.api === api ? settled : null
  const current = own && !own.superseded && (Object.is(own.key, key) || Object.is(own.answered, key)) &&
    Object.is(own.reloadKey, reloadKey) && own.count === count ? own : null
  const kept = !current && own && own.error === null && (keepPrevious === 'always' || keepPrevious === 'reload' && Object.is(own.key, key))
    ? own.data : null
  return {
    data: current ? current.data : kept, error: current?.error ?? null, cause: current?.cause, loading: current === null,
    latest: own?.latest ?? null, reload,
  }
}
