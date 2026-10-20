import type { AccountMonitor } from '@/api/monitor'
import { MonitorBadge } from '@/features/monitor/MonitorPanel'
import type { BrokerAccount } from '@/api/workspace'
import { useVenues } from '@/api/venues'
import { Button } from '@/components/ui/button'
import { InfoTip } from '@/components/ui/info-tip'
import { VenueLogo } from '@/components/venues/VenueBadge'
import { ArrowUpRight, Pencil, RefreshCw } from 'lucide-react'
import { useId } from 'react'
import { accountStatus, accountStatusTone, availableSourceLabels as availableSources, money, shortSource, time } from '@/lib/format'
import { AccountGroupHeader } from './AccountGroupHeader'
import type { AccountGroup } from './account-groups'
import './accounts.css'

/** One balance figure per row with its unit; what each venue reports is explained once, on the Balance header. */
function BalanceCell({ account }: { account: BrokerAccount }) {
  const venues = useVenues()
  const wallet = venues.can(account.venueId, 'stablecoinWallet')
  const value = wallet ? account.availableStablecoinNominalUsd ?? null : account.accountValueUsd
  const [unit, title] = wallet ? ['stablecoins', 'Wallet stablecoins available · nominal']
    : venues.can(account.venueId, 'sync') ? [venues.valueUnit(account.venueId), `Venue-reported account value · nominal ${venues.valueUnit(account.venueId)}`]
    : ['USD', 'Manual value in USD']
  const available = account.isEnabled === false ? null : account.availableBalanceUsd ?? null
  const source = account.availableBalanceSource ? availableSources[account.availableBalanceSource] ?? 'unknown' : 'unknown'
  return <>
    <span className="account-balance"><strong>{money(value)}</strong>{value !== null && <small className="account-balance-unit" title={title}>{unit}</small>}</span>
    {wallet && <small>Perps equity {money(account.accountValueUsd)}</small>}
    <small className="account-available">Available balance {money(available)}<span className="account-available-source">{source}</span></small>
  </>
}

/**
 * `accounts` is every row shown, in order, and drives select-all. With `groups` the rows render as one table section per
 * group, each under its own header row; the groups must partition `accounts`.
 */
export function AccountRows({ accounts, groups, portfolioNames, refreshing, selection, monitors = [], onDetail, onSync, onManage }: {
  accounts: BrokerAccount[]; groups?: AccountGroup[] | undefined; portfolioNames: Record<string, string>; refreshing: string | null
  monitors?: AccountMonitor[]
  selection?: { selected: string[]; onChange: (ids: string[]) => void; disabled: boolean }
  onDetail: (id: string) => void; onSync: (id: string) => void; onManage: (account: BrokerAccount) => void
}) {
  const venues = useVenues()
  const groupId = useId()
  const row = (account: BrokerAccount) => {
    const source = account.sourceId ?? account.address
    const portfolio = account.portfolioId ? portfolioNames[account.portfolioId] ?? 'Portfolio' : null
    return <tr key={account.id} className={account.isEnabled === false ? 'is-disabled' : undefined}>
      {selection && <td className="account-select-cell"><input type="checkbox" aria-label={`Select ${account.name}`} disabled={selection.disabled} checked={selection.selected.includes(account.id)}
        onChange={event => selection.onChange(event.target.checked ? [...selection.selected, account.id] : selection.selected.filter(id => id !== account.id))} /></td>}
      <td><button disabled={selection?.disabled} className="account-row-name" onClick={() => onDetail(account.id)}>
        <VenueLogo venueId={account.venueId} size="md" />
        <span><strong>{account.name}</strong>{account.isEnabled !== false && <MonitorBadge monitor={monitors.find(item => item.accountId === account.id) ?? null} />}<small className="account-row-meta"><span>{venues.name(account.venueId)}</span>
          {source && <span className="account-source-id" title={source}>{shortSource(source)}</span>}</small></span>
      </button></td>
      <td><span className={`account-portfolio${portfolio ? '' : ' is-unassigned'}`}>{portfolio ?? 'Unassigned'}</span></td>
      <td className="numeric"><BalanceCell account={account} /></td>
      <td><span className={`workspace-badge ${accountStatusTone(account)}`}>{accountStatus(account)}</span><small>{time(account.lastSyncedAtUtc)}</small><small>{account.positionCount} reported positions</small></td>
      <td><div className="row-actions"><Button disabled={selection?.disabled} variant="ghost" size="sm" onClick={() => onDetail(account.id)} aria-label={`View ${account.name}`}>View<ArrowUpRight size={14} /></Button>
        <button disabled={selection?.disabled} className="icon-button" onClick={() => onManage(account)} aria-label={`Manage ${account.name}`} title="Rename, move, disable or delete"><Pencil size={14} /></button>
        {venues.can(account.venueId, 'sync') && <button className="icon-button" disabled={selection?.disabled || refreshing !== null || account.isEnabled === false} aria-busy={refreshing === account.id} onClick={() => onSync(account.id)} aria-label={`Refresh ${account.name}`} title={account.isEnabled === false ? 'Enable this account before refreshing' : 'Refresh current state and recent executions'}><RefreshCw size={15} className={refreshing === account.id ? 'is-spinning' : ''} /></button>}</div></td>
    </tr>
  }
  return <div className="workspace-table-wrap"><table className="workspace-table account-table"><thead><tr>{selection && <th className="account-select-cell"><input type="checkbox" aria-label="Select all visible accounts" disabled={selection.disabled}
      checked={accounts.length > 0 && accounts.every(a => selection.selected.includes(a.id))}
      ref={input => { if (input) input.indeterminate = selection.selected.length > 0 && !accounts.every(a => selection.selected.includes(a.id)) }}
      onChange={event => selection.onChange(event.target.checked ? accounts.map(a => a.id) : [])} /></th>}<th>Account</th><th>Portfolio</th><th><span className="account-th-tip">Balance<InfoTip label="About balances">
      Nominal USD: listed stablecoins count 1:1, with no FX or depeg valuation. Venue accounts show the venue-reported account value in the venue's
      quote stablecoin; wallet venues show available wallet stablecoins with perps equity separately; manual accounts show the entered USD value.
      Available balance comes from the wallet, the venue-reported margin or the reported withdrawal, as tagged.</InfoTip></span></th><th>Sync</th><th><span className="sr-only">Actions</span></th></tr></thead>
    {groups ? groups.map((group, index) => {
      const titleId = `${groupId}-${index}`
      return <tbody key={group.key} className="account-group" aria-labelledby={titleId}>
        <AccountGroupHeader group={group} titleId={titleId} {...(selection ? { selection } : {})} />
        {group.accounts.map(row)}
      </tbody>
    }) : <tbody>{accounts.map(row)}</tbody>}
  </table></div>
}
