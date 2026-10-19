import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { WorkspaceApi } from '@/api/workspace'
import type { LinkOrder, PlayExecution, RebindLevel, SavedPlay } from '@/api/plays'
import { errorMessage } from '@/api/errors'

/**
 * Stored execution reads only. The shell owns event-driven and disconnected fallback refreshes.
 * Writes belong to the API and Play, not its status: a status change (often caused by the write's own
 * fills) re-reads the execution but never aborts a link or unlink already sent.
 */
export function usePlayExecution(api: WorkspaceApi, playId: string | null, status?: string, generation = 0) {
  const context = useMemo(() => ({ api, playId }), [api, playId])
  const [result, setResult] = useState<{ context: typeof context; execution: PlayExecution } | null>(null)
  const [error, setError] = useState<{ context: typeof context; message: string; source: 'read' | 'action' } | null>(null)
  // Whether the shown execution is fresh, kept apart from the error message: an action's error or success never makes a
  // failed read fresh. Only a successful read, or an action that returns the execution itself, does.
  const [freshness, setFreshness] = useState<{ context: typeof context; stale: boolean } | null>(null)
  const [workState, setWorkState] = useState<{ context: typeof context; signal: AbortSignal; busy: boolean } | null>(null)
  const [revision, setRevision] = useState(0)
  const session = useRef<{ context: typeof context; controller: AbortController; busy: boolean } | null>(null)
  const read = useRef(0)
  useEffect(() => {
    const work = { context, controller: new AbortController(), busy: false }
    session.current = work
    return () => work.controller.abort()
  }, [context])
  const reload = useCallback(() => setRevision(value => value + 1), [])
  useEffect(() => {
    if (!playId) return
    const controller = new AbortController()
    const version = ++read.current
    api.playExecution(playId, controller.signal).then(execution => {
      if (!controller.signal.aborted && read.current === version) {
        setResult({ context, execution })
        setFreshness({ context, stale: false })
        setError(current => current?.context === context && current.source === 'action' ? current : null)
      }
    }).catch(cause => {
      if (!controller.signal.aborted && read.current === version) {
        setFreshness({ context, stale: true })
        setError({ context, message: errorMessage(cause, 'The change could not be completed. Try again.'), source: 'read' })
      }
    })
    return () => controller.abort()
  }, [api, playId, context, status, generation, revision])

  const run = useCallback(async (action: (id: string, signal: AbortSignal) => Promise<PlayExecution | void>) => {
    const work = session.current
    if (!work || !context.playId || work.context !== context || work.busy || work.controller.signal.aborted) return
    work.busy = true
    const version = ++read.current
    setWorkState({ context, signal: work.controller.signal, busy: true })
    try {
      const execution = await action(context.playId, work.controller.signal)
      if (!work.controller.signal.aborted) {
        if (execution && read.current === version) { setResult({ context, execution }); setFreshness({ context, stale: false }) }
        setError(null)
      }
    } catch (cause) {
      if (!work.controller.signal.aborted) { setError({ context, message: errorMessage(cause, 'The change could not be completed. Try again.'), source: 'action' }); throw cause }
    } finally {
      work.busy = false
      if (!work.controller.signal.aborted) { setWorkState({ context, signal: work.controller.signal, busy: false }); reload() }
    }
  }, [context, reload])
  // The error state above shows link/unlink failures; the rejection itself needs no other handler.
  const link = useCallback((request: LinkOrder) => run((id, signal) => api.linkOrder(id, request, signal)).catch(() => undefined), [api, run])
  const unlink = useCallback((linkId: string) => run((id, signal) => api.unlinkOrder(id, linkId, signal)).catch(() => undefined), [api, run])
  const dismiss = useCallback((id: string, note?: string) => run((play, signal) => api.dismissPlayDiscrepancy(play, id, note, signal)), [api, run])
  /** The new plan revision belongs to the Play, so the saved Play goes to `onPlay`; the execution is read again after. */
  const rebind = useCallback((linkId: string, request: RebindLevel, onPlay: (play: SavedPlay) => void) =>
    run(async (play, signal) => { onPlay(await api.rebindLevel(play, linkId, request, signal)) }), [api, run])
  return { execution: result?.context === context ? result.execution : null,
    error: error?.context === context ? error.message : null,
    busy: workState?.context === context && !workState.signal.aborted && workState.busy,
    /** A read failed and no read (or action returning the execution) has succeeded since: `execution` may be out of date. */
    readFailed: freshness?.context === context && freshness.stale, reload, link, unlink, dismiss, rebind }
}
