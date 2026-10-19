import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import type { PickerAccount } from './AccountPicker'
import { AccountSelect } from './AccountSelect'

const hl = { id: 'hl', name: 'Swing', venueId: 'hyperliquid', source: '0x6d1e9c42aa1b3c4d5e6f708192a3b4c5d6e7f389', detail: 'Core' }
const lighter = { id: 'lt', name: 'Scalps', venueId: 'lighter-rh', source: '48213', detail: 'Unassigned' }

function setup({ value = '', accounts = [hl, lighter], missingLabel }: { value?: string; accounts?: PickerAccount[]; missingLabel?: string } = {}) {
  const onChange = vi.fn()
  render(<><span id="label">Account</span><AccountSelect labelledBy="label" value={value} onChange={onChange} accounts={accounts} missingLabel={missingLabel} /></>)
  return { onChange, trigger: screen.getByRole('button', { name: /^Account / }) }
}

describe('AccountSelect', () => {
  it('lists only accounts and chooses one', async () => {
    const { onChange, trigger } = setup()
    expect(trigger).toHaveAccessibleName('Account Choose an account')
    await userEvent.click(trigger)
    const options = within(screen.getByRole('listbox', { name: 'Account' })).getAllByRole('option')
    expect(options.map(option => option.getAttribute('aria-label')?.split(',')[0])).toEqual(['Swing', 'Scalps'])
    await userEvent.click(screen.getByRole('option', { name: /^Scalps,/ }))
    expect(onChange).toHaveBeenCalledWith('lt')
  })
  it('shows the venue tag beside the chosen account', () => {
    const { trigger } = setup({ value: 'lt' })
    expect(trigger).toHaveAccessibleName('Account Scalps, Lighter · Robinhood')
    expect(trigger.querySelector('.venue-tag')).toHaveTextContent('Lighter · Robinhood')
  })
  it('names a value that matches no listed account, and otherwise falls back to the placeholder', () => {
    setup({ value: 'gone', missingLabel: 'Unavailable or disabled account' })
    expect(screen.getByRole('button', { name: 'Account Unavailable or disabled account' })).toBeInTheDocument()
  })
  it('is disabled with nothing to choose', () => {
    const { trigger } = setup({ accounts: [] })
    expect(trigger).toBeDisabled()
  })
})
