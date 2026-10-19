import type { ComponentProps } from 'react'
import { AccountPicker } from './AccountPicker'

type AccountSelectProps = Omit<ComponentProps<typeof AccountPicker>, 'choices' | 'accountsHeading' | 'showVenue'>

/**
 * Chooses one account for a form, such as the Play editor. It is the account picker without aggregate choices like
 * All accounts or a portfolio, and its trigger also names the venue, because the account decides the instruments.
 * With no accounts to list it is disabled and shows the placeholder.
 */
export function AccountSelect({ accounts, disabled = false, ...props }: AccountSelectProps) {
  return <AccountPicker {...props} accounts={accounts} showVenue disabled={disabled || !accounts.length} />
}
