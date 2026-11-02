import { useState } from 'react'
import { Download } from 'lucide-react'
import type { WorkspaceApi } from '@/api/workspace'
import { errorMessage } from '@/api/errors'
import { Button } from '@/components/ui/button'
import { saveFile } from '@/lib/download'

/**
 * Downloads one saved Play as a zip (Phase 2, item 5c): its record as JSON and its evidence originals unchanged. Saved
 * state only; unsaved edits are not in it. Not a tax report.
 */
export function PlayBundleButton({ api, playId, dirty }: { api: Pick<WorkspaceApi, 'playBundle'>; playId: string; dirty: boolean }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function download() {
    setBusy(true)
    setError(null)
    try { saveFile(await api.playBundle(playId)) }
    catch (cause) { setError(errorMessage(cause, 'The Play could not be downloaded. Try again.')) }
    finally { setBusy(false) }
  }
  return <>
    <Button variant="outline" size="sm" disabled={busy} aria-busy={busy} onClick={() => { void download() }}
      title={dirty ? 'Downloads the saved Play; unsaved changes are not included' : 'The saved Play, its history, executions, review, log and evidence as a zip'}>
      <Download size={14} />Download</Button>
    {error && <span className="play-bundle-error" role="alert">{error}</span>}
  </>
}
