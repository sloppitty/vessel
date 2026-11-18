import { useId } from 'react'
import { Inbox } from 'lucide-react'
import type { BrokerAccount, Portfolio } from '@/api/workspace'
import { AccountPicker, type PickerChoice } from '@/components/accounts/AccountPicker'
import { Button } from '@/components/ui/button'
import { allAccountsChoice, pickerAccounts } from './account-picker-options'
import { PortfolioIcon } from './PortfolioIcon'

const accountsText = (n: number) => `${n} ${n === 1 ? 'account' : 'accounts'}`

export function OverviewScope({ scope, onScope, accounts, portfolios }: {
  scope: string; onScope: (scope: string) => void; accounts: BrokerAccount[]; portfolios: Portfolio[]
}) {
  const enabled = accounts.filter(a => a.isEnabled)
  const id = useId()
  const unassigned = enabled.filter(a => a.portfolioId === null).length
  const choices: PickerChoice[] = [
    allAccountsChoice('All accounts', 'all', accountsText(enabled.length)),
    ...portfolios.map(p => ({ value: `portfolio:${p.id}`, label: p.name, group: 'Portfolios', icon: <PortfolioIcon icon={p.icon} size={15} />,
      detail: accountsText(enabled.filter(a => a.portfolioId === p.id).length) })),
    ...(unassigned ? [{ value: 'unassigned', label: 'Unassigned', group: 'Portfolios', icon: <Inbox size={15} />, detail: accountsText(unassigned) }] : []),
  ]
  return <div className="overview-scope">
    <span id={`${id}-label`}>Overview scope</span>
    <AccountPicker id={id} labelledBy={`${id}-label`} value={scope} onChange={onScope} choices={choices} accountsHeading="Accounts"
      accounts={pickerAccounts(enabled, portfolios, a => `account:${a.id}`)} placeholder="All accounts" searchLabel="Search portfolios and accounts" />
    {/* Not rendered at all while everything is shown. It leaves the page when used, so focus moves to the selector first. */}
    {scope !== 'all' && <Button variant="outline" size="sm" className="text-foreground" aria-label="View all accounts" onClick={() => { onScope('all'); document.getElementById(id)?.focus() }}>View all</Button>}
  </div>
}
