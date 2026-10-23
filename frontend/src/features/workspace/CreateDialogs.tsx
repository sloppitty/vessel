import { manualVenueId } from '@/api/system'
import { useState, type FormEvent } from 'react'
import type { BrokerAccount, Portfolio, WorkspaceApi } from '@/api/workspace'
import { accountIndex } from '@/api/account-access'
import { isSolanaAddress } from '@/api/source-identity'
import { ApiError, errorMessage } from '@/api/errors'
import { useVenues } from '@/api/venues'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { VenueLogo } from '@/components/venues/VenueBadge'
import './accounts.css'
import { WalletAccountImport } from './WalletAccountImport'
import { PortfolioAccounts } from './PortfolioAccounts'
import { PortfolioDestination } from './PortfolioDestination'
import { usePortfolioDestination } from './usePortfolioDestination'
import { PortfolioIconPicker } from './PortfolioIcon'
import { defaultPortfolioIcon, type PortfolioIconKey } from '@/api/portfolio-icons'

export function CreatePortfolioDialog({ open, onOpenChange, api, accounts = [], portfolios = [], onCreated }: {
  open: boolean; onOpenChange: (open: boolean) => void; api: WorkspaceApi; accounts?: BrokerAccount[]; portfolios?: Portfolio[]; onCreated: () => void
}) {
  const [selected, setSelected] = useState<BrokerAccount[]>([])
  const [name, setName] = useState('')
  const [icon, setIcon] = useState<PortfolioIconKey>(defaultPortfolioIcon)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [reloaded, setReloaded] = useState<{ source: BrokerAccount[]; portfolioSource: Portfolio[]; accounts: BrokerAccount[]; portfolios: Portfolio[] } | null>(null)
  const currentAccounts = reloaded?.source === accounts ? reloaded.accounts : accounts
  const currentPortfolios = reloaded?.portfolioSource === portfolios ? reloaded.portfolios : portfolios
  async function submit(event: FormEvent) {
    event.preventDefault(); setError(null)
    if (!name.trim()) { setError('Give the portfolio a name.'); return }
    setPending(true)
    try {
      const members = selected.filter(a => currentAccounts.some(current => current.id === a.id))
      if (members.length) await api.createPortfolio(name.trim(), icon, members.map(a => a.id),
        members.map(a => ({ accountId: a.id, settingsRevision: a.settingsRevision! })))
      else await api.createPortfolio(name.trim(), icon)
      setSelected([]); setName(''); setIcon(defaultPortfolioIcon); onOpenChange(false); onCreated()
    } catch (cause) {
      setError(errorMessage(cause, 'Could not create this portfolio.'))
      if (cause instanceof ApiError && (cause.status === 409 || cause.status === 412)) {
        setSelected([]); onCreated()
        setError('Account settings changed elsewhere. No portfolio was created or accounts moved. Review the reloaded accounts and select them again before retrying.')
        try {
          const [latestAccounts, latestPortfolios] = await Promise.all([api.accounts(), api.portfolios()])
          setReloaded({ source: accounts, portfolioSource: portfolios, accounts: latestAccounts, portfolios: latestPortfolios })
        } catch { setError('Account settings changed elsewhere. Reloading failed. Close this dialog and reload accounts before retrying.') }
      }
    }
    finally { setPending(false) }
  }
  return <Dialog open={open} onOpenChange={next => { if (!pending) { onOpenChange(next); setError(null) } }}>
    <DialogContent className="workspace-dialog">
      <DialogHeader><DialogTitle>New portfolio</DialogTitle><DialogDescription className="sr-only">Group accounts by the way you trade.</DialogDescription></DialogHeader>
      <form onSubmit={event => { void submit(event) }} className="workspace-form" aria-busy={pending}>
        <label htmlFor="portfolio-name">Portfolio name</label><Input id="portfolio-name" value={name} onChange={event => setName(event.target.value)} maxLength={200} placeholder="e.g. Swing trading" disabled={pending} autoFocus />
        <PortfolioIconPicker name="new-portfolio-icon" value={icon} onChange={setIcon} disabled={pending} />
        <PortfolioAccounts accounts={currentAccounts} portfolios={currentPortfolios}
          selected={selected.filter(a => currentAccounts.some(current => current.id === a.id)).map(a => a.id)}
          onChange={ids => setSelected(ids.map(id => selected.find(a => a.id === id) ?? currentAccounts.find(a => a.id === id)!))} disabled={pending} />
        {error && <p className="error" role="alert">{error}</p>}
        <Button type="submit" disabled={pending}>{pending ? 'Creating…' : 'Create portfolio'}</Button>
      </form>
    </DialogContent>
  </Dialog>
}

export function CreateAccountDialog({ open, onOpenChange, api, portfolios, accounts = [], onCreated }: {
  open: boolean; onOpenChange: (open: boolean) => void; api: WorkspaceApi; portfolios: Portfolio[]
  accounts?: BrokerAccount[]; onCreated: (account?: BrokerAccount) => void
}) {
  const [name, setName] = useState('')
  const destination = usePortfolioDestination(portfolios, api, onCreated)
  const venues = useVenues()
  const paper = api.tradingMode === 'paper'
  const choices = paper ? venues.list.filter(venue => venue.capabilities.paperExecution) : venues.creatable()
  const [venueChoice, setVenue] = useState('')
  // The first venue that can read an account, until the owner chooses.
  const venue = choices.some(v => v.id === venueChoice) ? venueChoice : (choices.find(v => !venues.isManual(v.id)) ?? choices[0])?.id ?? manualVenueId
  const venueInfo = venues.find(venue)
  const needsAddress = !paper && venueInfo?.source === 'evm-address'
  const needsSolana = !paper && venueInfo?.source === 'solana-address'
  const needsIndex = !paper && venueInfo?.source === 'account-index'
  const canDiscover = needsIndex && venues.can(venue, 'accountDiscovery')
  const [address, setAddress] = useState('')
  const [manualValue, setManualValue] = useState('')
  const [startingCapital, setStartingCapital] = useState('4000')
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function submit(event: FormEvent) {
    event.preventDefault(); setError(null)
    if (!name.trim()) { setError('Name the account.'); return }
    if (paper && !choices.some(choice => choice.id === venue)) { setError('Paper execution is not available for this venue.'); return }
    if (paper && !/^\d+(\.\d+)?$/.test(startingCapital.trim())) { setError('Use an exact nonnegative starting capital amount.'); return }
    if (needsAddress && !/^0x[\da-f]{40}$/i.test(address.trim())) { setError('Use a valid public wallet address, starting with 0x.'); return }
    if (needsSolana && !isSolanaAddress(address.trim())) { setError('Use a case-sensitive Solana public address: base58 decoding to 32 bytes.'); return }
    if (needsIndex && !accountIndex(address.trim())) { setError('Use the exact nonnegative account index.'); return }
    if ((needsIndex || needsSolana) && accounts.some(a => a.venueId === venue && (a.sourceId ?? a.address) === address.trim())) {
      setError('This account is already imported. Manage or re-enable the existing account.'); return
    }
    if (venues.isManual(venue) && manualValue.trim() && !/^\d+(\.\d+)?$/.test(manualValue.trim())) { setError('Use a nonnegative balance, or leave it unavailable.'); return }
    setPending(true)
    let portfolioCreated = false
    try {
      const target = await destination.resolve(); portfolioCreated = target.created
      const account = await api.createAccount({
        portfolioId: target.portfolioId, name: name.trim(), venueId: venue,
        ...(paper ? { tradingMode: 'paper' as const, startingCapitalUsd: startingCapital.trim() } : needsAddress ? { address: address.trim().toLowerCase() } : needsIndex || needsSolana ? { sourceId: address.trim() } : manualValue.trim() ? { manualAccountValueUsd: manualValue.trim() } : {}),
      })
      setName(''); setAddress(''); setManualValue(''); setStartingCapital('4000'); onOpenChange(false); onCreated(account)
    } catch (cause) { setError((portfolioCreated ? 'The new portfolio was created and kept. Retry adding the account into it; a duplicate portfolio will not be created. ' : '') + (errorMessage(cause, 'Could not create this account.'))) }
    finally { setPending(false) }
  }
  return <Dialog open={open} onOpenChange={next => { if (!pending) { onOpenChange(next); setError(null) } }}>
    <DialogContent className={`workspace-dialog${canDiscover ? ' account-import-dialog' : ''}`}>
      <DialogHeader><DialogTitle>{paper ? 'Add a paper account' : 'Add an account'}</DialogTitle><DialogDescription className="sr-only">{paper ? 'Simulated capital only. Starting capital is fixed immediately after creation.' : 'Import read-only venue accounts or keep a manual record.'}</DialogDescription></DialogHeader>
      <fieldset className="venue-picker" disabled={pending}>
        <legend>Venue</legend>
        <div className="venue-picker-grid">{choices.map(v => <label key={v.id} className="venue-card">
          <input type="radio" className="sr-only" name="account-venue" value={v.id} checked={venue === v.id}
            aria-label={paper ? `${v.name} · Paper simulated` : venues.isManual(v.id) ? 'Manual account' : `${v.name} · read-only perps`}
            onChange={() => { setVenue(v.id); setAddress(''); setError(null) }} />
          <VenueLogo venueId={v.id} size="lg" />
          <span><strong>{venues.isManual(v.id) ? 'Manual account' : v.name}</strong>
            {!venues.isManual(v.id) && <small>{paper ? 'Paper · simulated' : `Read-only perps${v.quoteAsset ? ` · ${v.quoteAsset}` : ''}`}</small>}</span>
        </label>)}</div>
      </fieldset>
      {canDiscover ? <WalletAccountImport key={venue} venueId={venue} api={api} portfolios={portfolios} accounts={accounts}
        onCreated={onCreated} onBusy={setPending} onClose={() => onOpenChange(false)} /> :
        <form onSubmit={event => { void submit(event) }} className="workspace-form" aria-busy={pending}>
          <PortfolioDestination destination={destination} inputId="account-portfolio" label={paper ? 'Paper portfolio · required' : 'Portfolio · optional'} required={paper} disabled={pending} />
          <label htmlFor="account-name">Account name</label><Input id="account-name" value={name} onChange={event => setName(event.target.value)} placeholder="e.g. Main account" maxLength={200} disabled={pending} />
          {paper ? <><label htmlFor="paper-starting-capital">Starting capital · virtual USD</label><Input id="paper-starting-capital" value={startingCapital} onChange={event => setStartingCapital(event.target.value)} inputMode="decimal" disabled={pending} required />
            <p className="field-help">Default 4000 nominal virtual USD. Fixed immediately after creation: no later edits, deposits, withdrawals or reset. Zero capital cannot fund entries. No real wallet, credentials or history import.</p></> : needsAddress || needsSolana ? <><label htmlFor="account-address">{needsSolana ? 'Solana public address' : 'Public wallet address'}</label><Input id="account-address" value={address} onChange={event => setAddress(event.target.value)} placeholder={needsSolana ? 'Case-sensitive base58 address' : '0x…'} maxLength={needsSolana ? 44 : 128} spellCheck={false} autoComplete="off" autoCapitalize="none" disabled={pending} /><p className="field-help">Never enter a private key or seed phrase.</p></> :
            needsIndex ? <><label htmlFor="account-source">Account index</label><Input id="account-source" value={address} onChange={event => setAddress(event.target.value)} inputMode="numeric" autoComplete="off" disabled={pending} /></> :
            <><label htmlFor="manual-balance">Known account value · USD <span className="optional">optional</span></label><Input id="manual-balance" value={manualValue} onChange={event => setManualValue(event.target.value)} placeholder="Leave blank if unavailable" inputMode="decimal" disabled={pending} /></>}
          {error && <p role="alert" className="error">{error}</p>}
          <Button type="submit" disabled={pending || paper && !choices.length}>{pending ? 'Adding…' : paper ? 'Create paper account' : 'Add account'}</Button>
        </form>}
    </DialogContent>
  </Dialog>
}
