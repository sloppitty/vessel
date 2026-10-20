import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from 'react'
import type { WorkspaceApi } from '@/api/workspace'
import type { LinkOrder, PlayExecution, RebindLevel, SavedPlay } from '@/api/plays'
import { errorMessage } from '@/api/errors'

type Context = { api: WorkspaceApi; playId: string | null }

/**
 * The shown execution and how current it is. One counter orders the facts: a read takes its number when it starts (its
 * answer is at least as new as that moment), a mutation takes its number when it completes (its effect is committed by
 * then, whether it succeeded or failed). A mutation's own answer was built when it committed, at an unknown moment
 * before it arrived, so it cannot be ordered against a read that overlapped it: it may be shown, but it never makes the
 * execution fresh. Only a read started after the mutation completed does.
 */
interface Shown {
  context: Context | null
  execution: PlayExecution | null
  /**
   * The display order of the facts shown: the start number of the read they came from, or the completion number of a
   * mutation whose own answer is shown. A read replaces them only if it started later.
   */
  applied: number
  /** The completion number of the last mutation. */
  mutated: number
  /** The newest start number of a read that failed. */
  failed: number
  /** Mutations sent and not yet answered. */
  mutating: number
}

type ShownEvent = { context: Context } & (
  | { type: 'read'; sequence: number; execution: PlayExecution }
  | { type: 'read-failed'; sequence: number }
  | { type: 'mutation-started' }
  | { type: 'mutation-done'; started: number; sequence: number; execution: PlayExecution | null })

const nothingShown: Shown = { context: null, execution: null, applied: 0, mutated: 0, failed: 0, mutating: 0 }

function shown(state: Shown, event: ShownEvent): Shown {
  const current = state.context === event.context ? state : { ...nothingShown, context: event.context }
  switch (event.type) {
    // A read applies only if it started after the facts shown: an earlier one, even if it answers later, cannot replace
    // a mutation's own answer or a newer read.
    case 'read': return event.sequence > current.applied ? { ...current, execution: event.execution, applied: event.sequence } : current
    case 'read-failed': return { ...current, failed: Math.max(current.failed, event.sequence) }
    case 'mutation-started': return { ...current, mutating: current.mutating + 1 }
    // A mutation's own answer is shown only if no read started after the mutation has been shown: such a read may have
    // seen later facts (an import after the commit) than the answer, which may have waited on the way.
    case 'mutation-done': return { ...current, mutating: Math.max(0, current.mutating - 1), mutated: Math.max(current.mutated, event.sequence),
      ...event.execution && current.applied <= event.started ? { execution: event.execution, applied: event.sequence } : {} }
  }
}

/**
 * Fresh when no mutation is in flight, the facts shown come from a read started after the last mutation completed (a
 * mutation's own answer never counts; the follow-up read is always issued), and no read started after them has failed.
 * A read that is only pending does not count, so routine refreshes never turn known facts into unknown ones.
 */
const isFresh = (state: Shown) => state.mutating === 0 && state.applied > state.mutated && state.failed <= state.applied

/**
 * Stored execution reads only. The shell owns event-driven and disconnected fallback refreshes.
 * Writes belong to the API and Play, not its status: a status change (often caused by the write's own
 * fills) re-reads the execution but never aborts a link or unlink already sent.
 */
export function usePlayExecution(api: WorkspaceApi, playId: string | null, status?: string, generation = 0) {
  const context = useMemo<Context>(() => ({ api, playId }), [api, playId])
  const [state, dispatch] = useReducer(shown, nothingShown)
  const [error, setError] = useState<{ context: Context; message: string; source: 'read' | 'action' } | null>(null)
  const [workState, setWorkState] = useState<{ context: Context; signal: AbortSignal; busy: boolean } | null>(null)
  const [revision, setRevision] = useState(0)
  const session = useRef<{ context: Context; controller: AbortController; busy: boolean } | null>(null)
  // One counter for read starts and mutation completions.
  const sequence = useRef(0)
  useEffect(() => {
    const work = { context, controller: new AbortController(), busy: false }
    session.current = work
    return () => work.controller.abort()
  }, [context])
  const reload = useCallback(() => setRevision(value => value + 1), [])
  useEffect(() => {
    if (!playId) return
    // A newer read aborts this one, so at most one read is live.
    const controller = new AbortController()
    const started = ++sequence.current
    api.playExecution(playId, controller.signal).then(execution => {
      if (controller.signal.aborted) return
      dispatch({ context, type: 'read', sequence: started, execution })
      setError(current => current?.context === context && current.source === 'action' ? current : null)
    }).catch(cause => {
      if (controller.signal.aborted) return
      dispatch({ context, type: 'read-failed', sequence: started })
      // Its message only stands while nothing newer has been applied since it started.
      if (sequence.current === started) setError({ context, message: errorMessage(cause, 'The change could not be completed. Try again.'), source: 'read' })
    })
    return () => controller.abort()
  }, [api, playId, context, status, generation, revision])

  const run = useCallback(async (action: (id: string, signal: AbortSignal) => Promise<PlayExecution | void>) => {
    const work = session.current
    if (!work || !context.playId || work.context !== context || work.busy || work.controller.signal.aborted) return
    work.busy = true
    // Reads numbered above this started after the mutation did.
    const started = sequence.current
    dispatch({ context, type: 'mutation-started' })
    setWorkState({ context, signal: work.controller.signal, busy: true })
    try {
      const execution = await action(context.playId, work.controller.signal)
      if (!work.controller.signal.aborted) {
        dispatch({ context, type: 'mutation-done', started, sequence: ++sequence.current, execution: execution ?? null })
        setError(null)
      }
    } catch (cause) {
      if (!work.controller.signal.aborted) {
        // A failed mutation may still have changed something: only a read started after it is current again.
        dispatch({ context, type: 'mutation-done', started, sequence: ++sequence.current, execution: null })
        setError({ context, message: errorMessage(cause, 'The change could not be completed. Try again.'), source: 'action' })
        throw cause
      }
    } finally {
      work.busy = false
      if (!work.controller.signal.aborted) { setWorkState({ context, signal: work.controller.signal, busy: false }); reload() }
    }
  }, [context, reload])
  // The error state above shows link/unlink failures; the rejection itself needs no other handler.
  const link = useCallback((request: LinkOrder) => run((id, signal) => api.linkOrder(id, request, signal)).catch(() => undefined), [api, run])
  const unlink = useCallback((linkId: string) => run((id, signal) => api.unlinkOrder(id, linkId, signal)).catch(() => undefined), [api, run])
  /** Turns a whole venue order down for this Play in one request (1.6d), so no check can link it between two steps. */
  const reject = useCallback((orderId: string) => run((id, signal) => api.rejectOrder(id, orderId, signal)).catch(() => undefined), [api, run])
  const dismiss = useCallback((id: string, note?: string) => run((play, signal) => api.dismissPlayDiscrepancy(play, id, note, signal)), [api, run])
  /** The new plan revision belongs to the Play, so the saved Play goes to `onPlay`; the execution is read again after. */
  const rebind = useCallback((linkId: string, request: RebindLevel, onPlay: (play: SavedPlay) => void) =>
    run(async (play, signal) => { onPlay(await api.rebindLevel(play, linkId, request, signal)) }), [api, run])
  const current = state.context === context ? state : nothingShown
  return { execution: current.execution,
    error: error?.context === context ? error.message : null,
    busy: workState?.context === context && !workState.signal.aborted && workState.busy,
    /**
     * The shown execution may be out of date: a mutation is in flight, no read started after the last mutation completed
     * has been shown yet (a link's own answer does not count), or a read started after the facts shown has failed. Facts
     * derived from it (fills, averages) are unknown until it is fresh.
     */
    stale: !isFresh(current), reload, link, unlink, reject, dismiss, rebind }
}
