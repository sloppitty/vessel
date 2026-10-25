import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { createWorkspaceApi } from '@/api/workspace'
import { CreateAccountDialog } from './CreateDialogs'
import { credentialsFixture } from '@/test/identity-fixture'

describe('Create account venue picker', () => {
  it('offers creatable venues as logo cards in one keyboard-operable radio group', async () => {
    render(<CreateAccountDialog open onOpenChange={vi.fn()} api={createWorkspaceApi(credentialsFixture)} portfolios={[]} onCreated={vi.fn()} />)
    const group = screen.getByRole('group', { name: 'Venue' })
    const radios = within(group).getAllByRole('radio')
    expect(radios.map(radio => radio.getAttribute('aria-label'))).toEqual(['Hyperliquid · read-only perps', 'Lighter · Robinhood · read-only perps', 'Manual account'])
    expect(within(group).getByRole('radio', { name: 'Hyperliquid · read-only perps' })).toBeChecked()
    expect(group.querySelector('[data-logo="lighter-rh"] img')).not.toBeNull()
    expect(group.querySelector('[data-logo="manual"] svg')).not.toBeNull()
    expect(within(group).getByText('Read-only perps · USDG')).toBeInTheDocument()
    expect(screen.getByLabelText('Public wallet address')).toBeInTheDocument()

    await userEvent.click(within(group).getByRole('radio', { name: 'Hyperliquid · read-only perps' }))
    await userEvent.keyboard('{ArrowDown}')
    expect(within(group).getByRole('radio', { name: 'Lighter · Robinhood · read-only perps' })).toBeChecked()
    expect(screen.getByRole('button', { name: 'Find wallet' })).toBeInTheDocument()

    await userEvent.click(within(group).getByRole('radio', { name: 'Manual account' }))
    expect(screen.getByLabelText(/Known account value/)).toBeInTheDocument()
  })
})
