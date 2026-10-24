import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { createWorkspaceApi } from '@/api/workspace'
import { CreateAccountDialog } from './CreateDialogs'
import { VenuesProvider } from '@/api/venues'
import { systemFixture } from '@/test/system-fixture'
import { accountFixture } from '@/test/workspace-fixture'
import type { VenueInfo } from '@/api/system'

describe('Create account venue picker', () => {
  it('offers creatable venues as logo cards in one keyboard-operable radio group', async () => {
    render(<CreateAccountDialog open onOpenChange={vi.fn()} api={createWorkspaceApi('test-only-token')} portfolios={[]} onCreated={vi.fn()} />)
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

const solanaVenue: VenueInfo = {
  id: 'synthetic-solana', name: 'Solana venue', status: 'read-only', source: 'solana-address', logo: 'pacifica',
  quoteAsset: 'USDC', priceRule: 'tick-size', intervals: ['1m'], tradeUrlTemplate: null,
  capabilities: { sync: true, instruments: true, orders: true, candles: true, marketContext: false, stream: false, stablecoinWallet: false },
}

it('uses the source capability end to end without lowercasing or sending Solana through the legacy EVM address field', async () => {
  const api = createWorkspaceApi('test-only-token')
  api.createAccount = vi.fn().mockResolvedValue(accountFixture)
  render(<VenuesProvider venues={[solanaVenue, ...systemFixture.venues]}>
    <CreateAccountDialog open onOpenChange={vi.fn()} api={api} portfolios={[]} onCreated={vi.fn()} />
  </VenuesProvider>)
  await userEvent.type(screen.getByLabelText('Account name'), 'Solana')
  const field = screen.getByLabelText('Solana public address')
  await userEvent.type(field, 'z'.repeat(32))
  await userEvent.click(screen.getByRole('button', { name: 'Add account' }))
  expect(screen.getByRole('alert')).toHaveTextContent('decoding to 32 bytes')
  expect(api.createAccount).not.toHaveBeenCalled()
  await userEvent.clear(field)
  await userEvent.type(field, 'So11111111111111111111111111111111111111112')
  await userEvent.click(screen.getByRole('button', { name: 'Add account' }))
  expect(api.createAccount).toHaveBeenCalledWith({
    venueId: 'synthetic-solana', name: 'Solana', portfolioId: null, sourceId: 'So11111111111111111111111111111111111111112',
  })
})
