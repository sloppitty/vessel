import { useState } from 'react'
import type { BrokerAccount, Portfolio } from '@/api/workspace'
import { useVenues } from '@/api/venues'
import { Input } from '@/components/ui/input'
import { VenueLogo } from '@/components/venues/VenueBadge'
import './accounts.css'

/** Membership checklist. Each row says what a change does only when it does something: a move from another portfolio or
 * an unchecked member becoming Unassigned. `portfolioId` is the portfolio being edited, or null for a new one. */
export function PortfolioAccounts({ accounts, portfolios, selected, onChange, disabled, autoFocus = false, maxSelected = 200, portfolioId = null, allowUnassign = true }: {
  accounts: BrokerAccount[]; portfolios: Portfolio[]; selected: string[]; onChange: (ids: string[]) => void
  disabled: boolean; autoFocus?: boolean; maxSelected?: number; portfolioId?: string | null; allowUnassign?: boolean
}) {
  const venues = useVenues()
  const [search, setSearch] = useState('')
  const names = Object.fromEntries(portfolios.map(p => [p.id, p.name]))
  const query = search.trim().toLowerCase()
  const membershipEffect = (account: BrokerAccount) => {
    const checked = selected.includes(account.id)
    return checked && account.portfolioId && account.portfolioId !== portfolioId ? `Moves from ${names[account.portfolioId] ?? 'another portfolio'}`
      : allowUnassign && !checked && portfolioId !== null && account.portfolioId === portfolioId ? 'Becomes Unassigned' : null
  }
  // Retention is explained only while a change would move or unlink an account.
  const changing = accounts.some(account => membershipEffect(account) !== null)
  const shown = accounts.filter(a => [a.name, venues.name(a.venueId), a.portfolioId ? names[a.portfolioId] ?? '' : 'Unassigned']
    .some(value => value.toLowerCase().includes(query)))
  return <fieldset className="portfolio-membership" disabled={disabled}>
    <legend>Accounts <span className="optional">optional</span></legend>
    {(accounts.length > 10 || autoFocus) && <Input aria-label="Search portfolio accounts" placeholder="Search accounts, venue or portfolio"
      autoFocus={autoFocus} value={search} onChange={event => setSearch(event.target.value)} />}
    <div className="portfolio-account-list">{shown.map(account => {
      const effect = membershipEffect(account)
      return <label className="portfolio-account-choice" key={account.id}>
      <input type="checkbox" aria-label={`Include ${account.name}`} checked={selected.includes(account.id)}
        disabled={disabled || !allowUnassign && portfolioId !== null && account.portfolioId === portfolioId || !selected.includes(account.id) && selected.length >= maxSelected}
        onChange={event => onChange(event.target.checked ? [...selected, account.id] : selected.filter(id => id !== account.id))} />
      <VenueLogo venueId={account.venueId} size="md" />
      <span><strong>{account.name}</strong><small>{venues.name(account.venueId)} · {account.portfolioId ? names[account.portfolioId] ?? 'Portfolio' : 'Unassigned'}{account.isEnabled === false ? ' · Disabled' : ''}</small></span>
      {effect && <small className="portfolio-account-effect">{effect}</small>}
    </label>})}</div>
    {!allowUnassign && portfolioId !== null && accounts.some(account => account.portfolioId === portfolioId) && <p className="field-help">Paper accounts must remain in a paper portfolio. Move existing members to another paper portfolio through Manage account or the Accounts bulk move.</p>}
    {changing && <p className="field-help">History stays with each account.</p>}
    {selected.length >= maxSelected && maxSelected < accounts.length && <p className="field-help" role="status">Up to {maxSelected} accounts per change.</p>}
    {!shown.length && <p className="field-help">{accounts.length ? 'No matching accounts.' : 'No existing accounts yet. Add accounts later.'}</p>}
  </fieldset>
}
