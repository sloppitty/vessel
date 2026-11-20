import { PortfolioDestination } from './PortfolioDestination'
import { usePortfolioDestination } from './usePortfolioDestination'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import type { AccountDiscovery, DiscoveredAccount } from '@/api/account-access'
import { credentialFailureMessage, credentialTransportAllowed } from '@/api/account-access'
import type { BrokerAccount, Portfolio, WorkspaceApi } from '@/api/workspace'
import { ApiError, errorMessage } from '@/api/errors'
import { useVenues } from '@/api/venues'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { InfoTip } from '@/components/ui/info-tip'
import { VenueBadge } from '@/components/venues/VenueBadge'
import { ReadOnlyTokenInput } from './AccountCredential'
import { money } from '@/lib/format'

type ImportResult = { account?: BrokerAccount; message: string }

type ImportProps = {
  venueId: string; api: WorkspaceApi; portfolios: Portfolio[]; accounts: BrokerAccount[]
  onCreated: (account?: BrokerAccount) => void; onBusy: (busy: boolean) => void; onClose: () => void
}

export function WalletAccountImport(props: ImportProps) {
  const [session, setSession] = useState({ api: props.api, generation: 0 })
  if (session.api !== props.api) setSession({ api: props.api, generation: session.generation + 1 })
  return <WalletImportSession key={session.generation} {...props} />
}

function WalletImportSession({ venueId, api, portfolios, accounts, onCreated, onBusy, onClose }: ImportProps) {
  const discoveryRequest = useRef<AbortController | null>(null)
  const alive = useRef(false)
  useEffect(() => {
    alive.current = true
    return () => { alive.current = false; discoveryRequest.current?.abort() }
  }, [])
  const startDiscovery = () => {
    discoveryRequest.current?.abort()
    const controller = new AbortController()
    discoveryRequest.current = controller
    return controller
  }
  const currentDiscovery = (controller: AbortController) => alive.current && !controller.signal.aborted && discoveryRequest.current === controller
  const venues = useVenues()
  const [address, setAddress] = useState('')
  const [discovery, setDiscovery] = useState<AccountDiscovery | null>(null)
  const [selected, setSelected] = useState<string[]>([])
  const [names, setNames] = useState<Record<string, string>>({})
  const destination = usePortfolioDestination(portfolios, api, onCreated)
  const [token, setToken] = useState('')
  const [pending, setPending] = useState(false)
  const [loadingNames, setLoadingNames] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [namesNotice, setNamesNotice] = useState<string | null>(null)
  const [results, setResults] = useState<Record<string, ImportResult>>({})
  // Keep confirmed creations visible even if a subsequent workspace reload is slow or fails.
  const [created, setCreated] = useState<BrokerAccount[]>([])
  const currentAccounts = [...created, ...accounts]
  const existing = (candidate: DiscoveredAccount) =>
    currentAccounts.find(a => a.venueId === venueId && (a.sourceId ?? a.address) === candidate.sourceId)
  const added = (candidate: DiscoveredAccount) => !!(candidate.existingAccountId || existing(candidate))
  const begin = (blockClose = true) => { setPending(true); onBusy(blockClose); setError(null) }
  const finish = () => { setToken(''); setPending(false); onBusy(false) }
  async function find(event: FormEvent) {
    event.preventDefault(); setError(null); setToken('')
    if (!/^0x[\da-f]{40}$/i.test(address.trim())) { setError('Use a valid public wallet address, starting with 0x.'); return }
    const controller = startDiscovery()
    begin(false); setDiscovery(null); setSelected([]); setResults({}); setNamesNotice(null)
    try {
      const next = await api.discoverAccounts(venueId, address.trim().toLowerCase(), controller.signal)
      if (!currentDiscovery(controller)) return
      setDiscovery(next); setNames(Object.fromEntries(next.accounts.map(a => [a.sourceId, a.name])))
    } catch (cause) {
      if (currentDiscovery(controller)) setError(cause instanceof ApiError && cause.status === 429
        ? errorMessage(cause, '') : 'Could not find accounts for this wallet. Check the address and try again.')
    }
    finally { if (currentDiscovery(controller)) finish() }
  }
  async function loadNames() {
    if (!discovery || !token || !credentialTransportAllowed()) return
    const submitted = token
    const controller = startDiscovery()
    setToken(''); setNamesNotice(null); setLoadingNames(true); begin(false)
    try {
      const next = await api.discoverAccounts(venueId, discovery.address, controller.signal, submitted)
      if (!currentDiscovery(controller)) return
      // A name the owner already edited belongs to Vessel; do not overwrite it.
      setNames(previous => Object.fromEntries(next.accounts.map(a => {
        const edited = previous[a.sourceId]
        return [a.sourceId, edited !== undefined && edited !== discovery.accounts.find(old => old.sourceId === a.sourceId)?.name
          ? edited : a.name]
      })))
      setDiscovery(next)
      setNamesNotice('Venue names loaded where available. The token was not saved; re-enter it at import for order access.')
    } catch (cause) {
      if (!currentDiscovery(controller)) return
      setError(credentialFailureMessage(cause, 'Could not load venue names. Check the read-only token’s wallet, scope and expiry.') +
        ' Public balances and your selections are unchanged.')
    } finally { if (currentDiscovery(controller)) { setLoadingNames(false); finish() } }
  }
  async function importSelected(event: FormEvent) {
    event.preventDefault()
    const candidates = discovery?.accounts.filter(a => selected.includes(a.sourceId) && !added(a) && !results[a.sourceId]) ?? []
    const submitted = token
    setToken(''); setError(null)
    if (!candidates.length) return
    if (candidates.some(a => !names[a.sourceId]?.trim())) { setError('Name each selected account before importing.'); return }
    begin()
    try {
      const target = await destination.resolve()
      if (!alive.current) return
      for (const candidate of candidates) {
        let account: BrokerAccount
        try {
          account = await api.createAccount({ venueId, sourceId: candidate.sourceId, name: names[candidate.sourceId]!.trim(),
            portfolioId: target.portfolioId })
          if (!alive.current) return
          setCreated(previous => [...previous, account])
        } catch (cause) {
          if (!alive.current) return
          if (target.created) setError('The new portfolio was created and kept. It remains selected. Find wallet again before retrying imports; a duplicate portfolio will not be created.')
          setResults(previous => ({ ...previous, [candidate.sourceId]: { message: cause instanceof ApiError && cause.status === 409
            ? 'Already added. Close this dialog to manage or re-enable the existing account.'
            : 'Import not confirmed. Find wallet again before retrying; any existing account will be shown.' } }))
          // Reconcile the workspace even when a request completed but its response was lost.
          onCreated()
          continue
        }
        let message = 'Imported.'
        if (submitted && credentialTransportAllowed()) {
          try {
            await api.saveAccountCredential(account.id, submitted)
            if (!alive.current) return
            message = 'Imported. Read-only token verified and saved.'
          } catch (cause) {
            if (!alive.current) return
            message = 'Imported. Token save was not confirmed. Open Manage account to check its status before retrying. ' + credentialFailureMessage(cause)
          }
        }
        setResults(previous => ({ ...previous, [candidate.sourceId]: { account, message } }))
        // Start the initial read only after optional credential verification/storage has settled.
        onCreated(account)
      }
      setSelected([])
    } catch (cause) { if (alive.current) setError(errorMessage(cause, 'Could not create the portfolio. No accounts were imported.')) }
    finally { if (alive.current) finish() }
  }
  const importable = discovery?.accounts.filter(a => selected.includes(a.sourceId) && !added(a) && !results[a.sourceId]) ?? []
  return <div className="wallet-import" aria-busy={pending}>
    <form className="workspace-form" onSubmit={event => { void find(event) }}>
      <label htmlFor="discovery-wallet">Public wallet address</label>
      <div className="wallet-search"><Input id="discovery-wallet" value={address} placeholder="0x…" autoComplete="off"
        spellCheck={false} disabled={pending} maxLength={128} onChange={event => {
          setAddress(event.target.value); setDiscovery(null); setSelected([]); setResults({}); setToken(''); setNamesNotice(null)
        }} />
        <Button type="submit" variant="outline" disabled={pending}>{pending && !discovery ? 'Finding…' : 'Find wallet'}</Button>
      </div>
    </form>
    {discovery && <form className="workspace-form" autoComplete="off" onSubmit={event => { void importSelected(event) }}>
      <div className="import-heading"><h3>Choose accounts</h3><VenueBadge venueId={venueId} size="xs" />
        {discovery.accounts.some(a => a.collateralUsd != null || a.availableBalanceUsd != null) && <InfoTip label="About these balances">
          Venue-reported collateral and available balance in USD, not total equity. Balances are a snapshot from discovery.</InfoTip>}
        <span className="muted">{discovery.accounts.length} found</span></div>
      {discovery.accounts.length === 0 && <p className="credential-notice" role="status">No accounts were found for this wallet.</p>}
      <div className="discovered-accounts">
        {discovery.accounts.map(candidate => {
          const record = existing(candidate)
          const alreadyAdded = added(candidate)
          const disabled = record ? !record.isEnabled : candidate.isEnabled === false
          const result = results[candidate.sourceId]
          const checked = !alreadyAdded && selected.includes(candidate.sourceId)
          return <div className={`discovered-account${checked ? ' selected' : ''}`} key={candidate.sourceId}>
            <label className="discovered-choice">
              <input type="checkbox" checked={checked} disabled={pending || alreadyAdded || !!result}
                onChange={event => setSelected(previous => event.target.checked ? [...previous, candidate.sourceId] : previous.filter(id => id !== candidate.sourceId))} />
              <span><strong>{candidate.name}</strong><small>{candidate.accountType === 'main' ? 'Main account' : 'Subaccount'} · Index {candidate.sourceId}</small></span>
              <span className="discovered-value">
                {candidate.collateralUsd != null || candidate.availableBalanceUsd != null ? <>
                  <span title={candidate.collateralUsd ?? undefined}>Collateral · {candidate.collateralUsd == null ? 'Unavailable' : money(candidate.collateralUsd)}</span>
                  <small title={candidate.availableBalanceUsd ?? undefined}>Available · {candidate.availableBalanceUsd == null ? 'Unavailable' : money(candidate.availableBalanceUsd)}</small>
                </> : candidate.accountValueUsd === null ? 'Balance unavailable' : <>Account value · {money(candidate.accountValueUsd)}</>}
              </span>
            </label>
            {alreadyAdded && <span className={`workspace-badge discovered-badge${disabled ? ' warning-badge' : ''}`}
              title={disabled ? 'Re-enable in Manage account.' : 'Manage the existing account instead.'}>Already imported{disabled ? ' · Disabled' : ''}</span>}
            {checked && !result && <div className="workspace-form">
              <label htmlFor={`import-name-${candidate.sourceId}`}>Account name · {candidate.sourceId}</label>
              <Input id={`import-name-${candidate.sourceId}`} value={names[candidate.sourceId] ?? ''} maxLength={200}
                disabled={pending} onChange={event => setNames(previous => ({ ...previous, [candidate.sourceId]: event.target.value }))} />
            </div>}
            {result && <p className="import-result" role="status">{result.message}</p>}
          </div>
        })}
      </div>
      {discovery.notice && <p className="field-help">{discovery.notice}</p>}
      {(importable.length > 0 || Object.values(results).some(result => !result.account)) && <>
        <PortfolioDestination destination={destination} inputId="import-portfolio" label="Portfolio · optional · selected accounts" disabled={pending} />
      </>}
        {discovery.accounts.length > 0 && Object.keys(results).length === 0 && venues.can(venueId, 'readOnlyCredential') && <details className="credential-panel credential-disclosure" aria-label="Optional order access">
          <summary><h3>Venue names &amp; order access <span className="optional">optional</span></h3></summary>
          <p className="field-help">Public discovery has no account names: an all-scope read-only token from the main account loads them; a single-scope token loads only its own.</p>
          {importable.length > 0 && <p className="field-help">{importable.length === 1
            ? 'Choose single scope for this account, or all scope from the main account.'
            : 'For multiple selected accounts, create an all-scope token from the main account.'}</p>}
          <ReadOnlyTokenInput venueId={venueId} value={token} onChange={setToken} disabled={pending} />
          <Button type="button" variant="outline" disabled={pending || !token || !credentialTransportAllowed()}
            onClick={() => { void loadNames() }}>{loadingNames ? 'Loading names…' : `Load ${venues.name(venueId)} names`}</Button>
          {namesNotice && <p className="field-help" role="status">{namesNotice}</p>}
        </details>}
      <div className="credential-actions">
        <Button type="button" variant="outline" disabled={pending} onClick={() => { discoveryRequest.current?.abort(); setToken(''); onClose() }}>Done</Button>
        <Button type="submit" disabled={pending || importable.length === 0} title="Adds accounts and queues one initial refresh of current state and bounded recent executions, not complete lifetime history">{pending && !loadingNames ? 'Importing…' : `Import selected${importable.length ? ` (${importable.length})` : ''}`}</Button>
      </div>
    </form>}
    {error && <p className="error" role="alert">{error}</p>}
  </div>
}
