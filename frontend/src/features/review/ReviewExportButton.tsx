import { useState } from 'react'
import { Download } from 'lucide-react'
import type { ReviewQuery } from '@/api/review'
import type { WorkspaceApi } from '@/api/workspace'
import { errorMessage } from '@/api/errors'
import { saveFile } from '@/lib/download'

/**
 * Downloads Review's selection as CSV (Phase 2, item 5a): the report's scored rows and its exclusions, with exact decimal
 * strings. Not a tax report. A failure says so beside the button; nothing is kept in the browser.
 */
export function ReviewExportButton({ api, query }: { api: Pick<WorkspaceApi, 'reviewExport'>; query: ReviewQuery }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function download() {
    setBusy(true)
    setError(null)
    try { saveFile(await api.reviewExport(query)) }
    catch (cause) { setError(errorMessage(cause, 'The CSV could not be downloaded. Try again.')) }
    finally { setBusy(false) }
  }
  return <>
    <button className="pr-outline-button" disabled={busy} aria-busy={busy} onClick={() => { void download() }}
      title="This selection's Plays, imported positions and exclusions as CSV, with exact amounts. Not a tax report.">
      <Download size={15} />Download CSV</button>
    {error && <span className="pr-export-error" role="alert">{error}</span>}
  </>
}
