import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react'
import type { CandleInterval } from '@/api/candles'
import type { VenueCandle, WorkspaceApi } from '@/api/workspace'
import { candleCache, type CandleCache, type CandleEntry } from './candleCache'
export { mergeCandles, upsertCandle, toChartCandle } from './candleCache'
export type { CandleState } from './candleCache'

export interface CandleSource {
  api: WorkspaceApi; accountId: string; instrument: string; interval: CandleInterval
  /** Load candles ending at this time (UTC ms) instead of now, e.g. after a closed trade; no live tail joins them. */
  anchor?: number | null
}

/** Cached series appear synchronously; each activation revalidates, never persisting across connections. */
export function useCandles({ api, accountId, instrument, interval, anchor = null }: CandleSource) {
  const cache = useMemo(() => candleCache(api), [api])
  const keyed = useMemo(() => cache.entry(accountId, instrument, interval, anchor), [cache, accountId, instrument, interval, anchor])
  // A dropped entry (404/409/eviction) keeps its error until the owner retries, which takes a fresh entry.
  const [retried, setRetried] = useState<{ cache: CandleCache; entry: CandleEntry } | null>(null)
  // The entry whose refresh a retry already started; the next activation consumes or discards it.
  const retrying = useRef<CandleEntry | null>(null)
  const entry = cache.entries.get(keyed.key) !== keyed && retried?.cache === cache && retried.entry.key === keyed.key
    ? retried.entry : keyed
  const subscribe = useCallback((listener: () => void) => cache.subscribe(entry, listener), [cache, entry])
  const snapshot = useSyncExternalStore(subscribe, () => entry.snapshot)
  useEffect(() => {
    const started = retrying.current === entry
    retrying.current = null
    cache.activate(entry, !started)
  }, [cache, entry])
  const refresh = useCallback(() => {
    if (cache.entries.get(entry.key) === entry) void cache.load(entry, 'refresh')
    else {
      const fresh = cache.entry(accountId, instrument, interval, anchor)
      retrying.current = fresh
      void cache.load(fresh, 'refresh')
      setRetried({ cache, entry: fresh })
    }
  }, [cache, entry, accountId, instrument, interval, anchor])
  const revalidate = useCallback(() => { void cache.load(entry, 'latest') }, [cache, entry])
  const upsert = useCallback((candle: VenueCandle, receivedAt: string) => cache.upsert(entry, candle, receivedAt), [cache, entry])
  const loadOlder = useCallback(() => {
    if (!entry.snapshot.loadingOlder && !entry.snapshot.olderError) void cache.load(entry, 'older')
  }, [cache, entry])
  const retryOlder = useCallback(() => { void cache.load(entry, 'older') }, [cache, entry])
  return { ...snapshot, refresh, revalidate, upsert, loadOlder, retryOlder }
}
