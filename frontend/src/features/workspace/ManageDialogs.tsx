import type { ManagementEvent } from './management-events'
import { useState, type FormEvent } from 'react'
import type { BrokerAccount, Portfolio, WorkspaceApi } from '@/api/workspace'
import { ApiError, errorMessage } from '@/api/errors'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Trash2 } from 'lucide-react'
import { useVenues } from '@/api/venues'
import { VenueBadge } from '@/components/venues/VenueBadge'
import { AccountCredential } from './AccountCredential'
import { PortfolioAccounts } from './PortfolioAccounts'
import { PortfolioIconPicker } from './PortfolioIcon'
import { defaultPortfolioIcon, type PortfolioIconKey } from '@/api/portfolio-icons'
import { NativeSelect } from '@/components/ui/native-select'

const noAccounts: BrokerAccount[] = []
const noPortfolios: Portfolio[] = []


export function ManagePortfolioDialog({ portfolio, accounts = noAccounts, portfolios = noPortfolios, focusAccounts = false, api, onClose, onChanged, onStale }: {
  portfolio: Portfolio; accounts?: BrokerAccount[]; portfolios?: Portfolio[]; focusAccounts?: boolean; api: WorkspaceApi
  onClose: () => void; onChanged: (event: ManagementEvent) => void; onStale?: () => void
}) {
  const [baseline, setBaseline] = useState(accounts.filter(a => a.portfolioId === portfolio.id).map(a => a.id))
  const [selected, setSelected] = useState(baseline)
  const [needsReload, setNeedsReload] = useState(false)
  const [reloaded, setReloaded] = useState<{ source: BrokerAccount[]; portfolioSource: Portfolio[]; accounts: BrokerAccount[]; portfolios: Portfolio[] } | null>(null)
  const currentAccounts = reloaded?.source === accounts ? reloaded.accounts : accounts
  const currentPortfolios = reloaded?.portfolioSource === portfolios ? reloaded.portfolios : portfolios
  const [membershipSource, setMembershipSource] = useState(currentAccounts)
  const [membershipRevisions, setMembershipRevisions] = useState(() => Object.fromEntries(accounts.map(a => [a.id, a.settingsRevision])))
  if (membershipSource !== currentAccounts) {
    const added = selected.filter(id => !baseline.includes(id) && currentAccounts.some(a => a.id === id))
    const removed = baseline.filter(id => !selected.includes(id))
    const members = currentAccounts.filter(a => a.portfolioId === portfolio.id).map(a => a.id)
    const changed = new Set([...added, ...removed])
    setMembershipRevisions(Object.fromEntries(currentAccounts.map(a =>
      [a.id, changed.has(a.id) ? membershipRevisions[a.id] : a.settingsRevision])))
    setMembershipSource(currentAccounts); setBaseline(members)
    setSelected([...new Set([...members.filter(id => !removed.includes(id)), ...added])])
  }
  const [name, setName] = useState(portfolio.name)
  // Undefined until the owner picks one, so a rename never overwrites a stored icon this browser does not know.
  const [icon, setIcon] = useState<PortfolioIconKey>()
  const [deleting, setDeleting] = useState(false)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function save(event: FormEvent) {
    event.preventDefault(); setError(null)
    if (!name.trim()) { setError('Give the portfolio a name.'); return }
    const added = selected.filter(id => !baseline.includes(id) && currentAccounts.some(a => a.id === id))
    const removed = baseline.filter(id => !selected.includes(id))
    if ((portfolio.tradingMode === 'paper' || api.tradingMode === 'paper') && removed.length) { setError('Paper accounts must remain in a paper portfolio. Move existing members to another paper portfolio through Manage account or the Accounts bulk move. No changes were saved.'); return }
    if (added.length > 200 || removed.length > 200) { setError('Add or remove up to 200 accounts at a time.'); return }
    setPending(true)
    try {
      await api.updatePortfolio(portfolio.id, { name: name.trim(), ...(icon && { icon }) })
      const expected = (ids: string[]) => ids.map(accountId => ({ accountId, settingsRevision: membershipRevisions[accountId]! }))
      if (added.length) await api.moveAccounts(added, portfolio.id, expected(added))
      if (removed.length) await api.moveAccounts(removed, null, expected(removed))
      onChanged({ kind: 'portfolio-updated', id: portfolio.id, message: 'Portfolio updated.' }); onClose()
    } catch (cause) {
      setError('Some changes may have saved. Reload accounts before retrying. ' + errorMessage(cause, 'The change could not be confirmed. Reload current settings before retrying.')); setNeedsReload(true); onStale?.()
      if (cause instanceof ApiError && (cause.status === 409 || cause.status === 412)) {
        setError('Account settings changed elsewhere. The conflicting membership request moved no accounts. Earlier changes may have saved. Reload accounts and review before retrying.')
        try { await readAccounts() }
        catch { setError('Account settings changed elsewhere. Reloading failed. Reload accounts before retrying; earlier changes may have saved.') }
      }
    }
    finally { setPending(false) }
  }
  async function readAccounts() {
    const [latestAccounts, latestPortfolios] = await Promise.all([api.accounts(), api.portfolios()])
    const members = latestAccounts.filter(a => a.portfolioId === portfolio.id).map(a => a.id)
    setReloaded({ source: accounts, portfolioSource: portfolios, accounts: latestAccounts, portfolios: latestPortfolios })
    setBaseline(members); setSelected(members)
    setMembershipRevisions(Object.fromEntries(latestAccounts.map(a => [a.id, a.settingsRevision])))
  }
  async function reloadAccounts() {
    setPending(true); setError(null)
    try {
      await readAccounts(); setNeedsReload(false); onStale?.()
    } catch (cause) { setError(errorMessage(cause, 'The change could not be confirmed. Reload current settings before retrying.')) }
    finally { setPending(false) }
  }
  async function remove() {
    setPending(true); setError(null)
    try {
      await api.deletePortfolio(portfolio.id)
      onChanged({ kind: 'portfolio-deleted', id: portfolio.id, message: 'Portfolio deleted. Its accounts are now available in All accounts.' }); onClose()
    } catch (cause) { setError(errorMessage(cause, 'The change could not be confirmed. Reload current settings before retrying.')) }
    finally { setPending(false) }
  }
  return <Dialog open onOpenChange={next => { if (!next && !pending) onClose() }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>{deleting ? 'Delete portfolio?' : 'Manage portfolio'}</DialogTitle><DialogDescription className={deleting ? undefined : 'sr-only'}>{deleting ?
        portfolio.tradingMode === 'paper' ? `Delete ${portfolio.name}? Move all accounts into another paper portfolio first. Paper history is kept.` : `Delete ${portfolio.name}? Its accounts, executions, snapshots and plays are kept; the accounts become Unassigned.` :
        'Change the name, icon and accounts, or remove the grouping without deleting its history.'}</DialogDescription></DialogHeader>
      {deleting ? <div className="workspace-form">
        {error && <p className="error" role="alert">{error}</p>}
        <div className="management-actions"><Button variant="outline" onClick={() => { setDeleting(false); setError(null) }} disabled={pending}>Keep portfolio</Button><Button variant="destructive" onClick={() => { void remove() }} disabled={pending}>{pending ? 'Deleting…' : 'Delete portfolio'}</Button></div>
      </div> : <form className="workspace-form" onSubmit={event => { void save(event) }} aria-busy={pending}>
        <label htmlFor="rename-portfolio">Portfolio name</label><Input id="rename-portfolio" value={name} onChange={event => setName(event.target.value)} maxLength={200} disabled={pending} autoFocus={!focusAccounts} />
        <PortfolioIconPicker name="manage-portfolio-icon" value={icon ?? portfolio.icon ?? defaultPortfolioIcon} onChange={setIcon} disabled={pending} />
        <PortfolioAccounts accounts={currentAccounts} portfolios={currentPortfolios} selected={selected.filter(id => currentAccounts.some(a => a.id === id))}
          onChange={setSelected} disabled={pending || needsReload} autoFocus={focusAccounts} maxSelected={currentAccounts.length} portfolioId={portfolio.id} allowUnassign={portfolio.tradingMode !== 'paper' && api.tradingMode !== 'paper'} />
        {needsReload && <Button type="button" variant="outline" disabled={pending} onClick={() => { void reloadAccounts() }}>Reload accounts</Button>}
        {error && <p className="error" role="alert">{error}</p>}
        <div className="management-actions"><Button type="button" variant="ghost" className="danger-action" onClick={() => { setDeleting(true); setError(null) }} disabled={pending}><Trash2 size={14} />Delete</Button><Button type="submit" disabled={pending || needsReload}>{pending ? 'Saving…' : 'Save changes'}</Button></div>
      </form>}
    </DialogContent>
  </Dialog>
}

export function ManageAccountDialog({ account, portfolios, api, onClose, onChanged, onStale }: {
  account: BrokerAccount; portfolios: Portfolio[]; api: WorkspaceApi
  onClose: () => void; onChanged: (event: ManagementEvent) => void; onStale?: () => void
}) {
  const venues = useVenues()
  const [credentialPending, setCredentialPending] = useState(false)
  const [name, setName] = useState(account.name)
  const [portfolioId, setPortfolioId] = useState(account.portfolioId ?? '')
  const [enabled, setEnabled] = useState(account.isEnabled !== false)
  const [deleting, setDeleting] = useState(false)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [revision, setRevision] = useState(account.settingsRevision)
  const [conflict, setConflict] = useState(account.settingsRevision === undefined)
  const [reloadedPortfolios, setReloadedPortfolios] = useState<{ source: Portfolio[]; options: Portfolio[] } | null>(null)
  const portfolioOptions = reloadedPortfolios?.source === portfolios ? reloadedPortfolios.options : portfolios
  async function reloadSettings() {
    setPending(true); setError(null)
    try {
      // Another tab may also have created the portfolio the account now belongs to.
      const [current, currentPortfolios] = await Promise.all([api.account(account.id), api.portfolios()])
      if (current.settingsRevision === undefined) throw new ApiError('invalid-response', 'Current settings version is unavailable. Reload after the API update.')
      setReloadedPortfolios({ source: portfolios, options: currentPortfolios })
      setName(current.name); setPortfolioId(current.portfolioId ?? '')
      setEnabled(current.isEnabled !== false); setRevision(current.settingsRevision); setConflict(false)
    } catch (cause) { setError(errorMessage(cause, 'The change could not be confirmed. Reload current settings before retrying.')) }
    finally { setPending(false) }
  }
  async function save(event: FormEvent) {
    event.preventDefault(); setError(null)
    if (credentialPending) return
    if (!name.trim()) { setError('Name the account.'); return }
    if (conflict || revision === undefined) { setError('Reload current settings before saving.'); return }
    setPending(true)
    try {
      await api.updateAccount(account.id, { name: name.trim(), portfolioId: portfolioId || null, isEnabled: enabled, expectedRevision: revision })
      onChanged({ kind: 'account-updated', id: account.id, message: enabled ? 'Account settings saved.' : 'Account disabled. Its retained imports are hidden until enabled again.' })
      onClose()
    } catch (cause) {
      setError(errorMessage(cause, 'The change could not be confirmed. Reload current settings before retrying.'))
      // Refresh the shell too, so reopening this dialog does not reuse the stale revision.
      if (cause instanceof ApiError && (cause.status === 409 || cause.status === 412)) { setConflict(true); onStale?.() }
    }
    finally { setPending(false) }
  }
  async function remove() {
    setPending(true); setError(null)
    try {
      await api.deleteAccount(account.id); onChanged({ kind: 'account-deleted', id: account.id, message: 'Account and its retained imported records deleted.' }); onClose()
    } catch (cause) { setError(errorMessage(cause, 'The change could not be confirmed. Reload current settings before retrying.')) }
    finally { setPending(false) }
  }
  return <Dialog open onOpenChange={next => { if (!next && !pending && !credentialPending) onClose() }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>{deleting ? 'Delete account permanently?' : 'Manage account'}</DialogTitle><DialogDescription className={deleting ? undefined : 'sr-only'}>{deleting ?
        `Delete ${account.name} with its imported executions, snapshots and positions? This cannot be undone here. Accounts linked to Plays cannot be deleted; disable them instead.` :
        'Rename, move or disable this record. The venue and source identity do not change.'}</DialogDescription></DialogHeader>
      {deleting ? <div className="workspace-form">
        {error && <p className="error" role="alert">{error}</p>}
        <div className="management-actions"><Button variant="outline" onClick={() => { setDeleting(false); setError(null) }} disabled={pending}>Keep account</Button><Button variant="destructive" onClick={() => { void remove() }} disabled={pending}>{pending ? 'Deleting…' : 'Delete account'}</Button></div>
      </div> : <><p className="account-source"><span>Source</span> <VenueBadge venueId={account.venueId} size="xs" /> · {account.tradingMode === 'paper' ? 'Paper · simulated' : account.sourceId ?? account.address ?? 'Manual record'}</p>
      {account.tradingMode === 'paper' && <p className="field-help"><span className="workspace-badge">Paper</span> Starting capital: {account.startingCapitalUsd ?? 'Unknown'} virtual USD · fixed at creation, not editable.</p>}
      <form className="workspace-form" onSubmit={event => { void save(event) }} aria-busy={pending}>
        <label htmlFor="rename-account">Account name</label><Input id="rename-account" value={name} onChange={event => setName(event.target.value)} maxLength={200} disabled={pending} autoFocus />
        <label htmlFor="move-account">Portfolio</label><NativeSelect id="move-account" value={portfolioId} onChange={event => setPortfolioId(event.target.value)} disabled={pending}>
          {account.tradingMode !== 'paper' && <option value="">No portfolio</option>}{portfolioOptions.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
          {portfolioId && !portfolioOptions.some(p => p.id === portfolioId) && <option value={portfolioId}>Current portfolio</option>}
        </NativeSelect>
        <label className="account-enabled-control"><input type="checkbox" checked={enabled} onChange={event => setEnabled(event.target.checked)} disabled={pending} />Account enabled</label>
        {!enabled && <p className="field-help">{account.tradingMode === 'paper' ?
          'Disabling suspends this account’s paper checks; re-enabling resumes at a fresh minute boundary without replaying disabled touches. Unevaluated time before disabling is also skipped and remains unscored. Any position open at suspension keeps new entries blocked after re-enabling until that exposure closes. Touches during the hold are declined, not replayed. The affected history remains unscored. Records are kept but hidden and excluded from totals while disabled.' :
          'Records are kept; history and positions are hidden and excluded from totals, and refresh is blocked.'}</p>}
        {conflict && <div className="workspace-form"><p className="field-help">These settings may be outdated. Reload the latest saved values and review them; your changes are not replayed.</p><Button type="button" variant="outline" onClick={() => { void reloadSettings() }} disabled={pending}>Reload current settings</Button></div>}
        {error && <p className="error" role="alert">{error}</p>}
        <div className="management-actions"><Button type="button" variant="ghost" className="danger-action" onClick={() => { setDeleting(true); setError(null) }} disabled={pending || credentialPending}><Trash2 size={14} />Delete</Button><Button type="submit" disabled={pending || credentialPending || conflict}>{pending ? 'Saving…' : 'Save settings'}</Button></div>
      </form>
      {account.tradingMode !== 'paper' && venues.can(account.venueId, 'readOnlyCredential') && <fieldset className="credential-fieldset" disabled={pending}>
        <AccountCredential accountId={account.id} venueId={account.venueId} enabled={account.isEnabled !== false} api={api} onBusy={setCredentialPending} />
      </fieldset>}</>}
    </DialogContent>
  </Dialog>
}
