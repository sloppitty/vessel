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
  // Whether the shown execution is fresh, kept apart from the error message. Reads and actions are numbered as they start
  // (`read`). `fresh` is the newest one whose execution was applied; `uncertain` the newest read that ended without being
  // applied, because it failed or a later action superseded it. The execution is stale while an uncertain read is newer
  // than the fresh facts: an action's error, or an action that returns nothing, never changes that. A read that is only
  // pending does not count, so routine refreshes do not flicker known facts to unknown.
  const [freshness, setFreshness] = useState<{ context: typeof context; fresh: number; uncertain: number } | null>(null)
  const mark = useCallback((kind: 'fresh' | 'uncertain', sequence: number) => setFreshness(current => {
    const base = current?.context === context ? current : { context, fresh: 0, uncertain: 0 }
    return kind === 'fresh' ? { ...base, fresh: Math.max(base.fresh, sequence) } : { ...base, uncertain: Math.max(base.uncertain, sequence) }
  }), [context])
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
      if (controller.signal.aborted) return
      if (read.current === version) {
        setResult({ context, execution })
        mark('fresh', version)
        setError(current => current?.context === context && current.source === 'action' ? current : null)
      // An action started meanwhile supersedes this answer: until newer facts arrive, the shown execution may be behind.
      } else mark('uncertain', version)
    }).catch(cause => {
      if (controller.signal.aborted) return
      // Failed, superseded or not, it leaves the shown execution uncertain until fresh facts arrive.
      mark('uncertain', version)
      if (read.current === version) setError({ context, message: errorMessage(cause, 'The change could not be completed. Try again.'), source: 'read' })
    })
    return () => controller.abort()
  }, [api, playId, context, status, generation, revision, mark])

  const run = useCallback(async (action: (id: string, signal: AbortSignal) => Promise<PlayExecution | void>) => {
    const work = session.current
    if (!work || !context.playId || work.context !== context || work.busy || work.controller.signal.aborted) return
    work.busy = true
    const version = ++read.current
    setWorkState({ context, signal: work.controller.signal, busy: true })
    try {
      const execution = await action(context.playId, work.controller.signal)
      if (!work.controller.signal.aborted) {
        if (execution && read.current === version) { setResult({ context, execution }); mark('fresh', version) }
        setError(null)
      }
    } catch (cause) {
      if (!work.controller.signal.aborted) { setError({ context, message: errorMessage(cause, 'The change could not be completed. Try again.'), source: 'action' }); throw cause }
    } finally {
      work.busy = false
      if (!work.controller.signal.aborted) { setWorkState({ context, signal: work.controller.signal, busy: false }); reload() }
    }
  }, [context, reload, mark])
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
    /**
     * A read failed, or was superseded by an action, and no newer read (or action returning the execution) has been
     * applied since: `execution` may be out of date.
     */
    readFailed: freshness?.context === context && freshness.uncertain > freshness.fresh, reload, link, unlink, dismiss, rebind }
}
