import { useCallback, useEffect, useRef, useState } from 'react'
import type { SizingDocument } from '@/api/sizing'
import type { WorkspaceApi } from '@/api/workspace'

/**
 * How current the sizing record is. `ready`: the latest read (or risk change) succeeded. `loading`: a
 * read is under way and none has failed since the last success, so the last record, if any, stays
 * usable. `stale`: a read failed after an earlier success; the last record is shown but is not
 * current, and stays so while a retry runs. `failed`: no record could be read. Only a successful read
 * or risk change makes a stale or failed record current again.
 */
export type SizingStatus = 'loading' | 'ready' | 'stale' | 'failed'

const aborted = (cause: unknown) => cause instanceof DOMException && cause.name === 'AbortError'

interface SizingState {
  api: WorkspaceApi
  document: SizingDocument | null
  /** A read failed and nothing has succeeded since. */
  failed: boolean
  /** The generation and attempt of the last finished read. */
  read: string | null
}

/**
 * The sizing document, loaded when the Plays page is first shown, after each accepted stored-state
 * reload and on `retry`. A failed read is reported, never swallowed: the suggestions stop offering
 * Accept until a read succeeds. Aborted reads (a newer read, a hidden page or a replaced API) are not
 * failures. `updateRisk` saves the risk setting and takes the document the server returns; reads that
 * started before it are ignored, since its document is newer. It rejects so the caller can show why.
 */
export function useSizing(api: WorkspaceApi, generation = 0, enabled = true) {
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<SizingState | null>(null)
  const key = `${generation}:${attempt}`
  const latest = useRef({ api, key })
  useEffect(() => { latest.current = { api, key } })
  // Counts accepted risk changes, so a read can tell whether one landed after it started.
  const changes = useRef(0)
  // A hidden page neither reads nor rereads; it catches up once with the latest generation when shown.
  const loaded = useRef<{ api: WorkspaceApi; key: string } | null>(null)
  useEffect(() => {
    if (!enabled || loaded.current?.api === api && loaded.current.key === key) return
    const controller = new AbortController()
    const started = changes.current
    const superseded = () => controller.signal.aborted || started !== changes.current
    // Older API stubs may lack the endpoint; treat that like a failed read.
    Promise.resolve().then(() => api.sizing(controller.signal))
      .then(document => {
        if (superseded()) return
        loaded.current = { api, key }
        setState({ api, document, failed: false, read: key })
      })
      .catch((cause: unknown) => {
        // A failed read is retried when the page is shown again, as well as on Retry.
        if (superseded() || aborted(cause)) return
        setState(current => ({ api, document: current?.api === api ? current.document : null, failed: true, read: key }))
      })
    return () => controller.abort()
  }, [api, key, enabled])

  const updateRisk = useCallback(async (riskPercent: string) => {
    const document = await api.updateSizingSettings({ riskPercent })
    // A reply for a replaced API belongs to the old connection; the new one reads its own record.
    if (latest.current.api !== api) return
    changes.current += 1
    loaded.current = { api, key: latest.current.key }
    setState({ api, document, failed: false, read: latest.current.key })
  }, [api])
  const retry = useCallback(() => setAttempt(value => value + 1), [])

  const own = state?.api === api ? state : null
  const sizing = own?.document ?? null
  const status: SizingStatus = own?.failed ? (sizing ? 'stale' : 'failed') : own?.read === key ? 'ready' : 'loading'
  return { sizing, status, retry, updateRisk }
}
