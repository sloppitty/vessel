import { useEffect, useId, useState } from 'react'
import { Download, FileDown } from 'lucide-react'
import type { BrokerAccount, WorkspaceApi } from '@/api/workspace'
import { errorMessage } from '@/api/errors'
import { AccountSelect } from '@/components/accounts/AccountSelect'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { saveFile } from '@/lib/download'
import './data-export-settings.css'

type ExportApi = Pick<WorkspaceApi, 'accounts' | 'accountFacts'>
type Kind = 'executions' | 'funding'

/**
 * Raw retained facts of one enabled real account as CSV (Phase 2, item 5b): its executions (imported or owner-entered)
 * and, as a separate file, its funding settlements, with native IDs and exact amounts. Not a tax report. Dates are UTC
 * days; leaving them blank exports everything retained.
 */
export function DataExportSettings({ api }: { api: ExportApi }) {
  const id = useId()
  const [accounts, setAccounts] = useState<{ api: ExportApi; list: BrokerAccount[] } | null>(null)
  const [accountId, setAccountId] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [busy, setBusy] = useState<Kind | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    // Partial clients (older shells, test doubles) may not list accounts; the panel then has none to offer.
    if (typeof api.accounts !== 'function') return () => controller.abort()
    api.accounts(controller.signal).then(list => {
      if (!controller.signal.aborted) setAccounts({ api, list: list.filter(a => a.isEnabled !== false && (a.tradingMode ?? 'real') === 'real') })
    }).catch(() => { if (!controller.signal.aborted) setAccounts({ api, list: [] }) })
    return () => controller.abort()
  }, [api])
  const list = accounts?.api === api ? accounts.list : []
  const chosen = list.some(a => a.id === accountId) ? accountId : ''
  const range = from !== '' && to !== '' && from > to ? 'The start date is after the end date.' : null

  async function download(kind: Kind) {
    setBusy(kind)
    setError(null)
    try { saveFile(await api.accountFacts(chosen, kind, from || null, to || null)) }
    catch (cause) { setError(errorMessage(cause, 'The file could not be downloaded. Try again.')) }
    finally { setBusy(null) }
  }

  return <section className="shell-panel settings-panel data-export-settings" aria-labelledby={`${id}-heading`}>
    <FileDown size={23} aria-hidden="true" /><h2 id={`${id}-heading`}>Data export</h2>
    <p>An account's retained executions and funding as CSV, with native IDs and exact amounts, for your records or other tools.
      Not a tax report. Vessel keeps only what it retained; the venue may hold more history.</p>
    <div className="data-export-fields">
      <div className="data-export-field"><span id={`${id}-account`}>Account</span>
        <AccountSelect labelledBy={`${id}-account`} value={chosen} accounts={list} onChange={setAccountId}
          placeholder={accounts && !list.length ? 'No enabled accounts' : 'Choose an account'} /></div>
      <label className="data-export-field">From (UTC day)<Input type="date" value={from} onChange={event => setFrom(event.target.value)} /></label>
      <label className="data-export-field">To (UTC day)<Input type="date" value={to} aria-invalid={range !== null} onChange={event => setTo(event.target.value)} /></label>
    </div>
    {range && <p className="data-export-error">{range}</p>}
    <div className="data-export-actions">
      {(['executions', 'funding'] as const).map(kind => <Button key={kind} variant="outline" disabled={!chosen || range !== null || busy !== null}
        aria-busy={busy === kind} onClick={() => { void download(kind) }}><Download size={15} />{kind === 'executions' ? 'Executions CSV' : 'Funding CSV'}</Button>)}
    </div>
    {error && <p className="data-export-error" role="alert">{error}</p>}
  </section>
}
