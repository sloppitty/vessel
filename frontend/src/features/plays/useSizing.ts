import { useCallback, useEffect, useRef, useState } from 'react'
import type { SizingDocument } from '@/api/sizing'
import type { WorkspaceApi } from '@/api/workspace'

/**
 * How current the sizing record is. `loading`: a read is under way (the last record, if any, stays
 * usable). `ready`: the latest read succeeded. `stale`: the latest read failed after an earlier
 * success, so the last record is shown but is no longer current. `failed`: no record could be read.
 */
export type SizingStatus = 'loading' | 'ready' | 'stale' | 'failed'

const aborted = (cause: unknown) => cause instanceof DOMException && cause.name === 'AbortError'

/**
 * The sizing document, loaded when the Plays page is first shown, after each accepted stored-state
 * reload and on `retry`. A failed read is reported, never swallowed: the suggestions stop offering
 * Accept until a read succeeds. Aborted reads (a newer read, a hidden page or a replaced API) are not
 * failures. `updateRisk` saves the risk setting and takes the document the server returns; it
 * rejects so the caller can show why.
 */
export function useSizing(api: WorkspaceApi, generation = 0, enabled = true) {
  const [attempt, setAttempt] = useState(0)
  // The outcome of the latest finished read for this API, and the last document read from it.
  const [result, setResult] = useState<{ api: WorkspaceApi; generation: number; attempt: number; ok: boolean } | null>(null)
  const [last, setLast] = useState<{ api: WorkspaceApi; document: SizingDocument } | null>(null)
  // A hidden page neither reads nor rereads; it catches up once with the latest generation when shown.
  const loaded = useRef<{ api: WorkspaceApi; generation: number; attempt: number } | null>(null)
  useEffect(() => {
    const current = loaded.current
    if (!enabled || current?.api === api && current.generation === generation && current.attempt === attempt) return
    const controller = new AbortController()
    // Older API stubs may lack the endpoint; treat that like a failed read.
    Promise.resolve().then(() => api.sizing(controller.signal))
      .then(document => {
        if (controller.signal.aborted) return
        loaded.current = { api, generation, attempt }
        setLast({ api, document })
        setResult({ api, generation, attempt, ok: true })
      })
      .catch((cause: unknown) => {
        // A failed read is retried when the page is shown again, as well as on Retry.
        if (controller.signal.aborted || aborted(cause)) return
        setResult({ api, generation, attempt, ok: false })
      })
    return () => controller.abort()
  }, [api, generation, enabled, attempt])

  const updateRisk = useCallback(async (riskPercent: string) => {
    const document = await api.updateSizingSettings({ riskPercent })
    setLast({ api, document })
    setResult(current => ({ api, generation: current?.api === api ? current.generation : generation, attempt: current?.api === api ? current.attempt : attempt, ok: true }))
  }, [api, generation, attempt])
  const retry = useCallback(() => setAttempt(value => value + 1), [])

  const sizing = last?.api === api ? last.document : null
  const finished = result?.api === api && result.generation === generation && result.attempt === attempt ? result : null
  const status: SizingStatus = finished === null ? 'loading' : finished.ok ? 'ready' : sizing ? 'stale' : 'failed'
  return { sizing, status, retry, updateRisk }
}
