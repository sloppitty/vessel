import type { CandleInterval } from '@/api/candles'
import type { CandleSeries, VenueCandle, WorkspaceApi } from '@/api/workspace'
import { ApiError, errorMessage } from '@/api/errors'
import { onApiEvent } from '@/api/events'
import type { ChartCandle } from '@/components/chart/types'

export interface CandleState {
  status: 'loading' | 'ready' | 'error'
  error: string | null
  olderError: string | null
  retrievedAt: string | null
  notice: string | null
  historyExhausted: boolean
  localHistoryLimit: boolean
  loadingOlder: boolean
  refreshing: boolean
}
interface Snapshot extends CandleState { candles: ChartCandle[]; venueCandles: VenueCandle[] }
interface Page { fetchedAt: number; times: Set<number> }
interface Tick { candle: VenueCandle; receivedAt: string }
export interface CandleEntry {
  key: string
  accountId: string
  instrument: string
  interval: CandleInterval
  /**
   * When set, the newest candle loaded is at or before this time (UTC ms), e.g. shortly after a closed trade, so the chart
   * opens on it instead of today; its tail never moves to now (phase 1, 1.1b).
   */
  anchor: number | null
  generation: number
  historyGeneration: number
  tailRequest: number
  viewers: number
  lastView: number
  snapshot: Snapshot
  pages: Map<number, Page>
  tailTimes: Set<number>
  /** End of the fetched REST tail coverage; live candles never extend it. */
  coveredTo: number | null
  buffer: Map<number, Tick>
  controllers: Set<AbortController>
  flights: Map<string, { controller: AbortController; promise: Promise<void> }>
  listeners: Set<() => void>
}
const initial = (): Snapshot => ({ status: 'loading', error: null, olderError: null, retrievedAt: null, notice: null,
  historyExhausted: false, localHistoryLimit: false, loadingOlder: false, refreshing: false, candles: [], venueCandles: [] })
export const candleLimit = 5000
export const pageLifetime = 24 * 60 * 60 * 1000
const stores = new WeakMap<WorkspaceApi, CandleCache>()
export const toChartCandle = (candle: VenueCandle): ChartCandle => ({ time: candle.openTime, open: Number(candle.open),
  high: Number(candle.high), low: Number(candle.low), close: Number(candle.close), volume: Number(candle.volume) })
const sameDisplay = (a: VenueCandle, b: VenueCandle) => a.openTime === b.openTime &&
  a.open === b.open && a.high === b.high && a.low === b.low && a.close === b.close && a.volume === b.volume
const same = (a: VenueCandle, b: VenueCandle) => sameDisplay(a, b) && a.closeTime === b.closeTime && a.trades === b.trades

/** Compare positive decimal strings without rounding venue volume to a float. */
function compareVolume(a: string, b: string) {
  const [aw = '', af = ''] = a.split('.')
  const [bw = '', bf = ''] = b.split('.')
  const wholeA = aw.replace(/^0+/, '') || '0', wholeB = bw.replace(/^0+/, '') || '0'
  if (wholeA.length !== wholeB.length) return wholeA.length - wholeB.length
  const left = `${wholeA}.${af.padEnd(Math.max(af.length, bf.length), '0')}`
  const right = `${wholeB}.${bf.padEnd(Math.max(af.length, bf.length), '0')}`
  return left === right ? 0 : left > right ? 1 : -1
}
const advances = (current: VenueCandle, incoming: VenueCandle) => current.trades !== null && incoming.trades !== null
  ? incoming.trades >= current.trades : compareVolume(incoming.volume, current.volume) >= 0
export function mergeCandles(current: readonly VenueCandle[], incoming: readonly VenueCandle[]) {
  const byTime = new Map(current.map(candle => [candle.openTime, candle]))
  for (const candle of incoming) byTime.set(candle.openTime, candle)
  return [...byTime.values()].sort((a, b) => a.openTime - b.openTime)
}
export function upsertCandle(current: readonly VenueCandle[], candle: VenueCandle) { return mergeCandles(current, [candle]) }

/** One bounded memory store per authenticated API object. No browser persistence or authorization cache. */
export class CandleCache {
  readonly entries = new Map<string, CandleEntry>()
  private clock = 0
  generation = 0
  private invalidations = new Set<{ accountId: string; callback: () => void }>()
  constructor(private readonly api: WorkspaceApi) {
    // A rejected token ends every chart; a deleted or disabled account ends its own.
    onApiEvent(api, event => {
      if (event.type === 'unauthorized') this.invalidate()
      else if (event.type === 'account-removed') this.invalidate(event.accountId)
    })
  }

  entry(accountId: string, instrument: string, interval: CandleInterval, anchor: number | null = null) {
    const key = JSON.stringify(anchor === null ? [accountId, instrument, interval] : [accountId, instrument, interval, anchor])
    let entry = this.entries.get(key)
    if (!entry) {
      entry = { key, accountId, instrument, interval, anchor, generation: 0, historyGeneration: 0, tailRequest: 0,
        viewers: 0, lastView: ++this.clock, snapshot: initial(), pages: new Map(), tailTimes: new Set(), coveredTo: null, buffer: new Map(),
        controllers: new Set(), flights: new Map(), listeners: new Set() }
      this.entries.set(key, entry)
      this.trim(entry)
    }
    return entry
  }
  subscribe(entry: CandleEntry, listener: () => void) {
    entry.listeners.add(listener)
    entry.viewers++
    entry.lastView = ++this.clock
    this.trim()
    return () => { entry.listeners.delete(listener); entry.viewers--; this.trim() }
  }
  onInvalidate(accountId: string, callback: () => void) {
    const listener = { accountId, callback }
    this.invalidations.add(listener)
    return () => { this.invalidations.delete(listener) }
  }
  private publish(entry: CandleEntry, patch: Partial<Snapshot>) {
    entry.snapshot = { ...entry.snapshot, ...patch }
    for (const listener of entry.listeners) listener()
  }
  private valid(entry: CandleEntry, generation: number) {
    return entry.generation === generation && this.entries.get(entry.key) === entry
  }
  private trim(protectedEntry?: CandleEntry) {
    while (this.entries.size > 20) {
      const oldest = [...this.entries.values()].filter(entry => !entry.viewers && entry !== protectedEntry).sort((a, b) => a.lastView - b.lastView)[0]
      if (!oldest) break // All entries are visible; trim when a viewer leaves.
      this.drop(oldest)
    }
  }
  private drop(entry: CandleEntry, error = 'Candles are no longer available for this account.') {
    entry.generation++
    for (const controller of entry.controllers) controller.abort()
    entry.controllers.clear()
    entry.flights.clear()
    entry.buffer.clear()
    entry.pages.clear()
    entry.tailTimes.clear()
    entry.coveredTo = null
    this.entries.delete(entry.key)
    this.publish(entry, { ...initial(), status: 'error', error })
  }
  invalidate(accountId?: string, error?: string) {
    if (!accountId) this.generation++
    for (const entry of [...this.entries.values()]) if (!accountId || entry.accountId === accountId) this.drop(entry, error)
    for (const listener of this.invalidations) if (!accountId || listener.accountId === accountId) listener.callback()
  }
  /** `read` is false only when the caller has already started this entry's read (a retry's refresh). */
  activate(entry: CandleEntry, read = true) {
    if (!this.valid(entry, entry.generation)) return
    entry.lastView = ++this.clock
    const expired = new Set<number>()
    for (const [window, page] of entry.pages) if (Date.now() - page.fetchedAt > pageLifetime) {
      page.times.forEach(time => expired.add(time))
      entry.pages.delete(window)
    }
    if (expired.size) {
      for (const page of entry.pages.values()) page.times.forEach(time => expired.delete(time))
      entry.tailTimes.forEach(time => expired.delete(time))
      this.replace(entry, entry.snapshot.venueCandles.filter(candle => !expired.has(candle.openTime)))
      this.publish(entry, { historyExhausted: false, localHistoryLimit: entry.snapshot.candles.length >= candleLimit })
    }
    if (read) void this.load(entry, 'latest')
  }
  /** Only REST windows reconcile the whole array; unchanged display objects retain their identity. */
  private replace(entry: CandleEntry, next: VenueCandle[]) {
    const previous = new Map(entry.snapshot.venueCandles.map((candle, index) => [candle.openTime, { candle, display: entry.snapshot.candles[index]! }]))
    const candles = next.map(candle => {
      const old = previous.get(candle.openTime)
      return old && sameDisplay(old.candle, candle) ? old.display : toChartCandle(candle)
    })
    this.prunePages(entry, next)
    this.publish(entry, { venueCandles: next, candles, localHistoryLimit: next.length >= candleLimit })
  }
  private prunePages(entry: CandleEntry, candles: VenueCandle[]) {
    const retained = new Set(candles.map(candle => candle.openTime))
    for (const time of entry.tailTimes) if (!retained.has(time)) entry.tailTimes.delete(time)
    for (const [window, page] of entry.pages) {
      for (const time of page.times) if (!retained.has(time)) page.times.delete(time)
      if (!page.times.size) entry.pages.delete(window)
    }
  }
  private applyTick(entry: CandleEntry, { candle, receivedAt }: Tick) {
    const current = entry.snapshot.venueCandles
    let low = 0, high = current.length
    while (low < high) { const middle = (low + high) >>> 1; if (current[middle]!.openTime < candle.openTime) low = middle + 1; else high = middle }
    const old = current[low]
    if (old?.openTime === candle.openTime && candle.openTime === current.at(-1)?.openTime && !advances(old, candle)) return
    if (old?.openTime === candle.openTime && same(old, candle)) return
    const replace = old?.openTime === candle.openTime ? 1 : 0
    const venueCandles = [...current.slice(0, low), candle, ...current.slice(low + replace)]
    const candles = [...entry.snapshot.candles.slice(0, low), (replace && old && sameDisplay(old, candle) ? entry.snapshot.candles[low]! : toChartCandle(candle)), ...entry.snapshot.candles.slice(low + replace)]
    if (venueCandles.length > candleLimit) {
      const removed = venueCandles.shift()!.openTime
      candles.shift()
      entry.tailTimes.delete(removed)
      for (const [window, page] of entry.pages) { page.times.delete(removed); if (!page.times.size) entry.pages.delete(window) }
    }
    this.publish(entry, { venueCandles, candles, retrievedAt: receivedAt, localHistoryLimit: candles.length >= candleLimit })
  }
  upsert(entry: CandleEntry, candle: VenueCandle, receivedAt: string) {
    if (!this.valid(entry, entry.generation) || entry.snapshot.status !== 'ready') return
    if (entry.snapshot.refreshing) {
      const previous = entry.buffer.get(candle.openTime)?.candle
      // Older corrections apply; forming replays cannot displace a more complete observation.
      const newest = Math.max(entry.snapshot.venueCandles.at(-1)?.openTime ?? 0, ...entry.buffer.keys())
      if (!previous || candle.openTime < newest || advances(previous, candle)) entry.buffer.set(candle.openTime, { candle, receivedAt })
    } else this.applyTick(entry, { candle, receivedAt })
  }
  private flush(entry: CandleEntry, rest?: VenueCandle[]) {
    const ticks = [...entry.buffer.values()].sort((a, b) => a.candle.openTime - b.candle.openTime)
    const newest = Math.max(entry.snapshot.venueCandles.at(-1)?.openTime ?? 0, ticks.at(-1)?.candle.openTime ?? 0)
    const closedRest = new Set(rest?.filter(candle => candle.openTime < newest).map(candle => candle.openTime))
    entry.buffer.clear()
    for (const tick of ticks) if (!closedRest.has(tick.candle.openTime)) this.applyTick(entry, tick)
  }
  load(entry: CandleEntry, mode: 'latest' | 'refresh' | 'older'): Promise<void> {
    if (!this.valid(entry, entry.generation)) return Promise.resolve()
    // Recovery/activation can share an explicit refresh, but never supersede its bypassed read.
    const refreshing = mode === 'latest' ? entry.flights.get('refresh') : undefined
    if (refreshing) return refreshing.promise
    const older = mode === 'older'
    if (older && (entry.snapshot.historyExhausted || entry.snapshot.localHistoryLimit || !entry.snapshot.candles.length)) return Promise.resolve()
    // An anchored entry's tail ends at the anchor, never later than now: the server takes at most a day ahead.
    const endTime = older ? entry.snapshot.venueCandles[0]!.openTime - 1
      : entry.anchor !== null ? Math.min(entry.anchor, Date.now()) : undefined
    const window = older ? `older:${endTime}` : mode
    const pending = entry.flights.get(window)
    if (pending) return pending.promise
    const generation = entry.generation, historyGeneration = entry.historyGeneration, connectionGeneration = this.generation
    const request = older ? entry.tailRequest : ++entry.tailRequest
    const controller = new AbortController()
    entry.controllers.add(controller)
    if (mode === 'refresh') {
      entry.flights.delete('latest') // Its late response is rejected by tailRequest.
      // As before the cache, an explicit refresh replaces pending older reads; their late pages are discarded.
      entry.historyGeneration++
      for (const [key, flight] of entry.flights) if (key.startsWith('older:')) { flight.controller.abort(); entry.flights.delete(key) }
    }
    this.publish(entry, older ? { loadingOlder: true, olderError: null }
      : { refreshing: true, error: null, ...(mode === 'refresh' ? { loadingOlder: false } : {}) })
    const promise = this.api.candles(entry.accountId, { instrument: entry.instrument, interval: entry.interval,
      ...(endTime !== undefined ? { endTime } : {}), ...(mode === 'refresh' ? { refresh: true } : {}) }, controller.signal).then((series: CandleSeries) => {
      if (connectionGeneration !== this.generation || !this.valid(entry, generation) || (older ? entry.historyGeneration !== historyGeneration : entry.tailRequest !== request)) return
      const current = entry.snapshot.venueCandles
      // Fetched ranges decide continuity: an empty stretch inside them is a no-trade period, not a gap.
      const gap = !older && current.length > 0 && entry.coveredTo !== null && series.requestedFrom > entry.coveredTo
      if (gap) { entry.historyGeneration++; entry.pages.clear(); entry.tailTimes.clear() }
      if (!older) entry.coveredTo = gap || entry.coveredTo === null ? series.requestedTo : Math.max(entry.coveredTo, series.requestedTo)
      const incoming = older ? series.candles.filter(candle => candle.openTime <= endTime!) : series.candles
      if (older) entry.pages.set(endTime!, { fetchedAt: Date.now(), times: new Set(incoming.map(candle => candle.openTime)) })
      else incoming.forEach(candle => entry.tailTimes.add(candle.openTime))
      const forming = Math.max(current.at(-1)?.openTime ?? 0, incoming.at(-1)?.openTime ?? 0, ...entry.buffer.keys())
      const reconciled = incoming.map(candle => {
        const old = candle.openTime === forming ? current.at(-1) : undefined
        return old?.openTime === candle.openTime && !advances(old, candle) ? old : candle
      })
      let next = gap ? reconciled : mergeCandles(current, reconciled)
      if (next.length > candleLimit) {
        if (older) {
          // Keep the entire new page and the current tail; retire previously held older bars first.
          const loaded = new Set(incoming.map(candle => candle.openTime))
          let excess = next.length - candleLimit
          next = next.filter(candle => loaded.has(candle.openTime) || excess <= 0 || (excess--, false))
        } else next = next.slice(-candleLimit)
      }
      this.replace(entry, next)
      this.publish(entry, { status: 'ready', error: null, notice: series.notice,
        ...(older || gap || !current.length ? { historyExhausted: series.historyExhausted } : {}),
        ...(older && !incoming.length && !series.historyExhausted ? { olderError: 'No older candles were returned. Retry to check again.' } : {}),
        ...(!older ? { retrievedAt: series.retrievedAt, refreshing: false } : {}) })
      if (!older) this.flush(entry, incoming)
    }, (error: unknown) => {
      if (!this.valid(entry, generation)) return
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) { this.invalidate(undefined, errorMessage(error, 'Candles could not be loaded.')); return }
      // A superseded read's failure says nothing about the newer one.
      if (connectionGeneration !== this.generation || (older ? entry.historyGeneration !== historyGeneration : entry.tailRequest !== request)) return
      // A missing account ends every chart for it; a 409 may name only this market, so other charts keep streaming.
      if (error instanceof ApiError && error.status === 404) { this.invalidate(entry.accountId, errorMessage(error, 'Candles could not be loaded.')); return }
      if (error instanceof ApiError && error.status === 409) { this.drop(entry, errorMessage(error, 'Candles could not be loaded.')); return }
      if (!older) this.flush(entry)
      this.publish(entry, older ? { olderError: errorMessage(error, 'Candles could not be loaded.') } : { status: entry.snapshot.candles.length ? 'ready' : 'error', error: errorMessage(error, 'Candles could not be loaded.'), refreshing: false })
    }).finally(() => {
      entry.controllers.delete(controller)
      if (!this.valid(entry, generation)) return
      if (entry.flights.get(window)?.controller === controller) entry.flights.delete(window)
      this.publish(entry, { loadingOlder: [...entry.flights.keys()].some(key => key.startsWith('older:')) })
      this.trim()
    })
    entry.flights.set(window, { controller, promise })
    return promise
  }
}
export function candleCache(api: WorkspaceApi) {
  let store = stores.get(api)
  if (!store) { store = new CandleCache(api); stores.set(api, store) }
  return store
}
export function clearCandleCache(api: WorkspaceApi) { stores.get(api)?.invalidate() }
export function evictAccountCandles(api: WorkspaceApi, accountId: string) { stores.get(api)?.invalidate(accountId) }
