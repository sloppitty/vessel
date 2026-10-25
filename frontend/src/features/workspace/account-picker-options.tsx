import { Layers } from 'lucide-react'
import type { BrokerAccount, Portfolio } from '@/api/workspace'
import type { PickerAccount, PickerChoice } from '@/components/accounts/AccountPicker'
import { money } from '@/lib/format'

/** Workspace accounts as picker entries: source for identity, portfolio as the secondary line, balance when reported. */
export function pickerAccounts(accounts: readonly BrokerAccount[], portfolios: readonly Portfolio[], value?: (a: BrokerAccount) => string): PickerAccount[] {
  const names = new Map(portfolios.map(p => [p.id, p.name]))
  return accounts.map(a => ({
    id: a.id, name: a.name, venueId: a.venueId, source: a.tradingMode === 'paper' ? undefined : a.sourceId ?? a.address,
    detail: a.portfolioId ? names.get(a.portfolioId) ?? 'Portfolio' : 'Unassigned',
    balance: a.balanceUsd ? money(a.balanceUsd) : null,
    ...(value ? { value: value(a) } : {}),
  }))
}

/** The "every account" choice at the top of an account filter. */
export const allAccountsChoice = (label: string, value = '', detail?: string): PickerChoice => ({ value, label, detail, icon: <Layers size={15} /> })
