import { useEffect, useMemo, useSyncExternalStore } from 'react'
import { ApiError, errorMessage } from '@/api/errors'
import type { WorkspaceApi } from '@/api/workspace'
import type { DraftEvidence } from './draft'

/**
 * What a saved original shows while the Play is open. Unavailable covers 503, 500, timeouts and transport failures and
 * can be retried; missing is a 404 (the object is gone from storage). Neither ever removes the image from the draft.
 */
export type EvidenceImageState =
  | { status: 'loading' }
  | { status: 'loaded'; image: Blob }
  | { status: 'unavailable'; message: string }
  | { status: 'missing' }

/** Saved originals of the open Play by server evidence ID. */
export interface EvidenceImages {
  state(serverId: string): EvidenceImageState
  retry(serverId: string): void
}

/** Originals read at the same time; each is up to 10 MiB. */
export const evidenceImageLimit = 3
const loading: EvidenceImageState = { status: 'loading' }

const failure = (cause: unknown): EvidenceImageState => cause instanceof ApiError && cause.kind === 'http' && cause.status === 404
  ? { status: 'missing' } : { status: 'unavailable', message: errorMessage(cause, 'The image could not be loaded. Try again.') }

/** The image an evidence item shows: its own Blob (captures, uploads) or the saved original's load state. */
export function evidenceImage(item: DraftEvidence, images: EvidenceImages | undefined): EvidenceImageState {
  if (item.image) return { status: 'loaded', image: item.image }
  return item.serverId && images ? images.state(item.serverId) : loading
}

/**
 * Reads saved originals after a Play opens, each on its own and at most `limit` at a time, so one failure never blocks
 * the rest or the Play. Loaded Blobs stay here, keyed by server ID, rather than in the draft: their arrival is not an
 * edit and never changes the draft's identity. Nothing retries by itself, and nothing is stored beyond this memory.
 */
export class EvidenceImageLoader {
  /** Replaced on every change, so it can be a `useSyncExternalStore` snapshot. */
  snapshot: ReadonlyMap<string, EvidenceImageState> = new Map()
  private wanted = new Set<string>()
  private queue: string[] = []
  private readonly running = new Map<string, AbortController>()
  private readonly listeners = new Set<() => void>()

  constructor(private readonly read: (id: string, signal: AbortSignal) => Promise<Blob>, private readonly limit = evidenceImageLimit) {}

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }

  state(id: string): EvidenceImageState { return this.snapshot.get(id) ?? loading }

  /** The originals the open Play needs. Others are aborted and forgotten; new ones queue in order. */
  want(ids: readonly string[]) {
    this.wanted = new Set(ids)
    for (const [id, controller] of this.running) if (!this.wanted.has(id)) { controller.abort(); this.running.delete(id) }
    const next = new Map([...this.snapshot].filter(([id]) => this.wanted.has(id)))
    for (const id of this.wanted) if (!next.has(id)) { next.set(id, loading); this.queue.push(id) }
    this.queue = this.queue.filter(id => this.wanted.has(id))
    if (next.size !== this.snapshot.size || [...next.keys()].some(id => !this.snapshot.has(id))) this.publish(next)
    this.pump()
  }

  /** Reads an unavailable original again, ahead of the queue. */
  retry(id: string) {
    if (this.snapshot.get(id)?.status !== 'unavailable') return
    this.queue.unshift(id)
    this.publish(new Map(this.snapshot).set(id, loading))
    this.pump()
  }

  /** Aborts every read and forgets every original, e.g. when the API session is replaced or the page unmounts. */
  reset() {
    for (const controller of this.running.values()) controller.abort()
    this.running.clear()
    this.queue = []
    this.wanted = new Set()
    if (this.snapshot.size) this.publish(new Map())
  }

  private pump() {
    while (this.running.size < this.limit && this.queue.length) {
      const id = this.queue.shift()!
      if (this.wanted.has(id) && !this.running.has(id) && this.snapshot.get(id)?.status === 'loading') this.start(id)
    }
  }

  private start(id: string) {
    const controller = new AbortController()
    this.running.set(id, controller)
    let request: Promise<Blob>
    // The API client refuses an invalid identifier synchronously.
    try { request = this.read(id, controller.signal) } catch (cause) { request = Promise.reject(cause) }
    void request.then((image): EvidenceImageState => ({ status: 'loaded', image }), failure).then(state => {
      if (controller.signal.aborted || this.running.get(id) !== controller) return
      this.running.delete(id)
      this.publish(new Map(this.snapshot).set(id, state))
      this.pump()
    })
  }

  private publish(next: ReadonlyMap<string, EvidenceImageState>) {
    this.snapshot = next
    for (const listener of this.listeners) listener()
  }
}

/**
 * Loads the saved originals the draft does not hold for the current API session. Items leaving the draft (another
 * Play opened, removed, discarded) are aborted and forgotten; a new API identity or unmount aborts everything.
 */
export function useEvidenceImages(api: WorkspaceApi, evidence: readonly DraftEvidence[]): EvidenceImages {
  const loader = useMemo(() => new EvidenceImageLoader((id, signal) => api.evidenceImage(id, signal)), [api])
  // Server IDs are GUIDs, so a space-joined key is unambiguous and keeps the effect from rerunning on every edit.
  const key = evidence.flatMap(item => item.serverId && !item.image ? [item.serverId] : []).join(' ')
  useEffect(() => () => loader.reset(), [loader])
  useEffect(() => { loader.want(key ? key.split(' ') : []) }, [loader, key])
  const snapshot = useSyncExternalStore(loader.subscribe, () => loader.snapshot)
  return useMemo(() => ({ state: id => snapshot.get(id) ?? loading, retry: id => loader.retry(id) }), [loader, snapshot])
}
