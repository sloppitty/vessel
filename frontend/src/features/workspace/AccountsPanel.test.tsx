import { useState } from 'react'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { accountFixture, portfolioFixture } from '@/test/workspace-fixture'
import { createWorkspaceApi, type BrokerAccount, type WorkspaceApi } from '@/api/workspace'
import { AccountsPanel } from './AccountsPanel'
import { emptyAccountFilters, type AccountFilters } from './account-filters'
import { credentialsFixture } from '@/test/identity-fixture'

const accounts = [
  { ...accountFixture, name: 'Manual savings' },
  { ...accountFixture, id: 'failed', name: 'Alpha trading', venueId: 'hyperliquid', sourceId: '0xabc123',
    syncStatus: 'error' as const, portfolioId: null },
  { ...accountFixture, id: 'new', name: 'Beta trading', venueId: 'hyperliquid', address: '0xdef456',
    syncStatus: 'not-synced' as const },
  { ...accountFixture, id: 'disabled', name: 'Old account', venueId: 'hyperliquid', isEnabled: false, syncStatus: 'synced' as const },
]
function Harness() {
  const [portfolio, setPortfolio] = useState('')
  const [filters, setFilters] = useState(emptyAccountFilters)
  return <AccountsPanel api={createWorkspaceApi(credentialsFixture)} onChanged={vi.fn()} onReload={vi.fn()} accounts={accounts} portfolios={[portfolioFixture]} portfolioFilter={portfolio} onPortfolioFilter={setPortfolio}
    filters={filters} onFiltersChange={setFilters}
    refreshing={null} refreshAction={null} onDetail={vi.fn()} onSync={vi.fn()} onManage={vi.fn()} onAdd={vi.fn()} />
}
const names = () => within(screen.getByRole('table')).getAllByRole('button', { name: /^View / }).map(button => button.textContent)
describe('Account filters', () => {
  it('combines case-insensitive name/source search with portfolio, venue and status, and resets them', async () => {
    render(<Harness />)
    expect(screen.getByText('4 of 4 accounts')).toBeInTheDocument()
    await userEvent.type(screen.getByRole('textbox', { name: 'Search accounts' }), 'ABC123')
    expect(names()).toEqual(['View'])
    expect(screen.getByRole('button', { name: 'View Alpha trading' })).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by portfolio' }), 'unassigned')
    await userEvent.click(within(screen.getByRole('group', { name: 'Filter by venue' })).getByRole('button', { name: 'Hyperliquid' }))
    expect(within(screen.getByRole('group', { name: 'Filter by venue' })).getByRole('button', { name: 'Hyperliquid' })).toHaveAttribute('aria-pressed', 'true')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by status' }), 'error')
    expect(screen.getByText('1 of 4 accounts')).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by portfolio' }), portfolioFixture.id)
    expect(screen.getByText('0 of 4 accounts')).toBeInTheDocument()
    expect(screen.getByText('No accounts in this view')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reset filters' }))
    expect(screen.getByText('4 of 4 accounts')).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Search accounts' })).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Reset filters' })).toBeDisabled()
  })
  it.each([
    ['enabled', 3], ['disabled', 1], ['error', 1], ['not-synced', 1], ['manual', 1],
  ])('filters status %s', async (status, count) => {
    render(<Harness />)
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by status' }), status as string)
    expect(screen.getByText(`${count} of 4 accounts`)).toBeInTheDocument()
    expect(names()).toHaveLength(count as number)
  })
  it('shows venue logo chips with counts and an accessible venue name, falling back to the generic icon', async () => {
    const mixed = [...accounts, { ...accountFixture, id: 'rh', name: 'Robinhood main', venueId: 'lighter-rh', sourceId: '281474976710655', address: null, portfolioId: null },
      { ...accountFixture, id: 'other', name: 'Other', venueId: 'quantfury' }]
    render(<AccountsPanel api={createWorkspaceApi(credentialsFixture)} onChanged={vi.fn()} onReload={vi.fn()} accounts={mixed} portfolios={[portfolioFixture]}
      portfolioFilter="" onPortfolioFilter={vi.fn()} filters={emptyAccountFilters} onFiltersChange={vi.fn()}
      refreshing={null} refreshAction={null} onDetail={vi.fn()} onSync={vi.fn()} onManage={vi.fn()} onAdd={vi.fn()} />)
    const chips = within(screen.getByRole('group', { name: 'Filter by venue' }))
    expect(chips.getByRole('button', { name: 'All venues' })).toHaveAttribute('aria-pressed', 'true')
    expect(chips.getByRole('button', { name: 'Lighter · Robinhood' })).toHaveTextContent('1')
    expect(chips.getByRole('button', { name: 'Lighter · Robinhood' }).querySelector('[data-logo="lighter-rh"] img')).not.toBeNull()
    expect(chips.getByRole('button', { name: 'Quantfury' }).querySelector('[data-logo="generic"] svg')).not.toBeNull()
    const row = screen.getByRole('button', { name: 'View Robinhood main' }).closest('tr')!
    expect(row).toHaveTextContent('Lighter · Robinhood')
    expect(within(row).getByTitle('281474976710655')).toHaveTextContent('#2814…0655')
    expect(row).toHaveTextContent('Unassigned')
  })
  it('labels venue values in the venue quote stablecoin and manual values in USD', () => {
    const mixed = [{ ...accountFixture, name: 'Manual savings' },
      { ...accountFixture, id: 'rh', name: 'Robinhood main', venueId: 'lighter-rh', sourceId: '7', address: null, syncStatus: 'synced' as const }]
    render(<AccountsPanel api={createWorkspaceApi(credentialsFixture)} onChanged={vi.fn()} onReload={vi.fn()} accounts={mixed} portfolios={[portfolioFixture]}
      portfolioFilter="" onPortfolioFilter={vi.fn()} filters={emptyAccountFilters} onFiltersChange={vi.fn()}
      refreshing={null} refreshAction={null} onDetail={vi.fn()} onSync={vi.fn()} onManage={vi.fn()} onAdd={vi.fn()} />)
    const row = (name: string) => screen.getByRole('button', { name: `View ${name}` }).closest('tr')!
    // One figure with its unit; the unit's title says what the venue reports.
    expect(within(row('Robinhood main')).getByTitle('Venue-reported account value · nominal USDG')).toHaveTextContent(/^USDG$/)
    expect(within(row('Robinhood main')).getByText('$1,250.12')).toBeInTheDocument()
    expect(row('Robinhood main')).not.toHaveTextContent('USD ·')
    expect(within(row('Manual savings')).getByTitle('Manual value in USD')).toHaveTextContent(/^USD$/)
  })
  it('finds a source address as well as an account name', async () => {
    render(<Harness />)
    const input = screen.getByRole('textbox', { name: 'Search accounts' })
    await userEvent.type(input, 'DEF456')
    expect(screen.getByRole('button', { name: 'View Beta trading' })).toBeInTheDocument()
    expect(names()).toHaveLength(1)
    await userEvent.clear(input); await userEvent.type(input, 'SAVINGS')
    expect(screen.getByRole('button', { name: 'View Manual savings' })).toBeInTheDocument()
    expect(names()).toHaveLength(1)
  })
})

const SHARED = '0xAbCdEf0123456789abcdef0123456789ABCDEF01'
const OTHER = `0x${'2'.repeat(40)}`
const mk = (id: string, name: string, patch: Partial<BrokerAccount>): BrokerAccount => ({ ...accountFixture, id, name, ...patch })
const grouped = [
  mk('alpha', 'Alpha HL', { venueId: 'hyperliquid', address: SHARED, sourceId: SHARED, balanceUsd: '100.10' }),
  // The same wallet on another venue, spelled in lowercase and with no balance reported.
  mk('beta', 'Beta QF', { venueId: 'quantfury', address: SHARED.toLowerCase(), sourceId: SHARED.toLowerCase(), balanceUsd: null }),
  mk('gamma', 'Gamma HL', { venueId: 'hyperliquid', address: OTHER, sourceId: OTHER, balanceUsd: '10' }),
  mk('old', 'Old HL', { venueId: 'hyperliquid', address: OTHER, sourceId: OTHER, balanceUsd: '7', isEnabled: false }),
  mk('manual', 'Manual savings', { balanceUsd: '1000' }),
  mk('rh', 'Robinhood main', { venueId: 'lighter-rh', address: null, sourceId: '281474976710655', balanceUsd: '5', portfolioId: null }),
]
function GroupHarness({ initial = {}, api }: { initial?: Partial<AccountFilters>; api?: WorkspaceApi }) {
  const [portfolio, setPortfolio] = useState('')
  const [filters, setFilters] = useState<AccountFilters>({ ...emptyAccountFilters, ...initial })
  return <AccountsPanel api={api ?? createWorkspaceApi(credentialsFixture)} onChanged={vi.fn()} onReload={vi.fn()} accounts={grouped} portfolios={[portfolioFixture]}
    portfolioFilter={portfolio} onPortfolioFilter={setPortfolio} filters={filters} onFiltersChange={setFilters}
    refreshing={null} refreshAction={null} onDetail={vi.fn()} onSync={vi.fn()} onManage={vi.fn()} onAdd={vi.fn()} />
}
const groupControl = () => screen.getByRole('group', { name: 'Group by' })
const chooseGroup = (name: 'None' | 'Venue' | 'Address') => userEvent.click(within(groupControl()).getByRole('button', { name }))
/** Group sections are the named row groups: the table head is unnamed. */
const sections = () => within(screen.getByRole('table')).getAllByRole('rowgroup').filter(group => group.tagName === 'TBODY')
const sectionNames = () => sections().map(section => section.getAttribute('aria-labelledby') ? document.getElementById(section.getAttribute('aria-labelledby')!)!.textContent : null)
const section = (name: string) => screen.getByRole('rowgroup', { name })
const rows = (container: HTMLElement) => within(container).getAllByRole('button', { name: /^View / }).map(button => button.getAttribute('aria-label'))

describe('Account grouping', () => {
  it('offers None, Venue and Address as a pressed-state group, starting flat', () => {
    render(<GroupHarness />)
    const buttons = within(groupControl()).getAllByRole('button')
    expect(buttons.map(b => b.textContent)).toEqual(['None', 'Venue', 'Address'])
    expect(buttons.map(b => b.getAttribute('aria-pressed'))).toEqual(['true', 'false', 'false'])
    expect(sections()).toHaveLength(1)
    expect(sectionNames()).toEqual([null])
    expect(rows(screen.getByRole('table'))).toHaveLength(6)
  })

  it('groups by venue with logo, name, count and an exact balance, in order of first appearance', async () => {
    render(<GroupHarness />)
    await chooseGroup('Venue')
    expect(within(groupControl()).getByRole('button', { name: 'Venue' })).toHaveAttribute('aria-pressed', 'true')
    expect(sectionNames()).toEqual(['Hyperliquid', 'Quantfury', 'Manual', 'Lighter · Robinhood'])
    const hyperliquid = section('Hyperliquid')
    expect(rows(hyperliquid)).toEqual(['View Alpha HL', 'View Gamma HL', 'View Old HL'])
    expect(hyperliquid.querySelector('.account-group-row [data-logo="hyperliquid"] img')).not.toBeNull()
    // The disabled account is left out of the total and the header says so.
    expect(within(hyperliquid).getByText('3 accounts')).toBeInTheDocument()
    expect(within(hyperliquid).getByText('$110.10')).toBeInTheDocument()
    expect(within(hyperliquid).getByText('Nominal balance · 2 of 3 enabled')).toBeInTheDocument()
    expect(within(section('Manual')).getByText('$1,000.00')).toBeInTheDocument()
    expect(within(section('Manual')).getByText('1 account')).toBeInTheDocument()
    expect(rows(section('Lighter · Robinhood'))).toEqual(['View Robinhood main'])
  })

  it('shows a partial marker instead of a total when an enabled account reports no balance', async () => {
    render(<GroupHarness />)
    await chooseGroup('Venue')
    const quantfury = section('Quantfury')
    expect(within(quantfury).getByText('Partial')).toBeInTheDocument()
    expect(within(quantfury).getByText('No balances reported')).toBeInTheDocument()
    expect(quantfury.querySelector('.account-group-balance')).not.toHaveTextContent('$')
  })

  it('groups by address across venues, ignoring EVM case, and puts accounts without an address last', async () => {
    render(<GroupHarness />)
    await chooseGroup('Address')
    expect(sectionNames()).toEqual(['0xabcd…ef01', '0x2222…2222', 'No address recorded'])
    const shared = section('0xabcd…ef01')
    expect(rows(shared)).toEqual(['View Alpha HL', 'View Beta QF'])
    // Full address on hover, and one logo per venue the address spans.
    expect(within(shared.querySelector<HTMLElement>('.account-group-row')!).getByTitle(SHARED.toLowerCase())).toHaveTextContent('0xabcd…ef01')
    expect(within(shared).getByRole('img', { name: 'Hyperliquid' })).toBeInTheDocument()
    expect(within(shared).getByRole('img', { name: 'Quantfury' })).toBeInTheDocument()
    expect(within(shared).getByText('2 accounts')).toBeInTheDocument()
    expect(within(shared).getByText('Partial')).toBeInTheDocument()
    expect(within(shared).getByText('1 of 2 balances reported')).toBeInTheDocument()
    const other = section('0x2222…2222')
    expect(within(other).getByText('$10.00')).toBeInTheDocument()
    expect(within(other).getByText('Nominal balance · 1 of 2 enabled')).toBeInTheDocument()
    // Manual records and account-index venues (Lighter) store no owner address.
    const none = section('No address recorded')
    expect(rows(none)).toEqual(['View Manual savings', 'View Robinhood main'])
    expect(within(none).getByText('2 accounts · manual or account-index venues')).toBeInTheDocument()
    expect(within(none).getByText('$1,005.00')).toBeInTheDocument()
  })

  it('applies the existing filters first and leaves no empty groups', async () => {
    render(<GroupHarness initial={{ groupBy: 'address' }} />)
    expect(screen.getByText('6 of 6 accounts')).toBeInTheDocument()
    await userEvent.type(screen.getByRole('textbox', { name: 'Search accounts' }), 'beta')
    expect(sectionNames()).toEqual(['0xabcd…ef01'])
    expect(rows(section('0xabcd…ef01'))).toEqual(['View Beta QF'])
    expect(within(section('0xabcd…ef01')).getByText('1 account')).toBeInTheDocument()
    expect(screen.getByText('1 of 6 accounts')).toBeInTheDocument()
    await userEvent.clear(screen.getByRole('textbox', { name: 'Search accounts' }))
    await userEvent.click(within(screen.getByRole('group', { name: 'Filter by venue' })).getByRole('button', { name: 'Hyperliquid' }))
    expect(sectionNames()).toEqual(['0xabcd…ef01', '0x2222…2222'])
    expect(rows(section('0xabcd…ef01'))).toEqual(['View Alpha HL'])
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by status' }), 'disabled')
    expect(sectionNames()).toEqual(['0x2222…2222'])
    expect(within(section('0x2222…2222')).getByText('All accounts disabled')).toBeInTheDocument()
  })

  it('returns to the flat list and keeps the venue filter chips in every mode', async () => {
    render(<GroupHarness />)
    await chooseGroup('Address')
    expect(within(screen.getByRole('group', { name: 'Filter by venue' })).getByRole('button', { name: 'Quantfury' })).toBeInTheDocument()
    await chooseGroup('None')
    expect(sections()).toHaveLength(1)
    expect(screen.queryByRole('rowgroup', { name: /./ })).toBeNull()
    expect(rows(screen.getByRole('table'))).toEqual(grouped.map(a => `View ${a.name}`))
  })

  it('starts grouped when the shell remembers a choice, and reset leaves grouping alone', async () => {
    render(<GroupHarness initial={{ groupBy: 'venue' }} />)
    expect(within(groupControl()).getByRole('button', { name: 'Venue' })).toHaveAttribute('aria-pressed', 'true')
    // Grouping is a presentation choice, not a filter, so it does not enable the reset button.
    expect(screen.getByRole('button', { name: 'Reset filters' })).toBeDisabled()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by status' }), 'enabled')
    expect(screen.getByText('5 of 6 accounts')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reset filters' }))
    expect(screen.getByText('6 of 6 accounts')).toBeInTheDocument()
    expect(within(groupControl()).getByRole('button', { name: 'Venue' })).toHaveAttribute('aria-pressed', 'true')
    expect(sectionNames()).toEqual(['Hyperliquid', 'Quantfury', 'Manual', 'Lighter · Robinhood'])
  })

  describe('bulk selection', () => {
    const selectAll = () => screen.getByRole('checkbox', { name: 'Select all visible accounts' }) as HTMLInputElement
    const box = (name: string) => screen.getByRole('checkbox', { name }) as HTMLInputElement

    it('selects every visible row across groups with select-all', async () => {
      render(<GroupHarness initial={{ groupBy: 'address' }} />)
      await userEvent.click(selectAll())
      expect(screen.getByText('6 selected')).toBeInTheDocument()
      expect(screen.getAllByRole('checkbox').every(input => (input as HTMLInputElement).checked)).toBe(true)
      await userEvent.click(selectAll())
      expect(screen.queryByText(/ selected$/)).toBeNull()
    })

    it('selects and clears one group, with a mixed state when only some members are chosen', async () => {
      render(<GroupHarness initial={{ groupBy: 'address' }} />)
      const group = box('Select all 0xabcd…ef01 accounts')
      await userEvent.click(box('Select Alpha HL'))
      expect(group.indeterminate).toBe(true)
      expect(group.checked).toBe(false)
      await userEvent.click(group)
      expect(group.checked).toBe(true)
      expect(group.indeterminate).toBe(false)
      expect(screen.getByText('2 selected')).toBeInTheDocument()
      expect(box('Select Beta QF').checked).toBe(true)
      expect(box('Select Gamma HL').checked).toBe(false)
      expect(selectAll().indeterminate).toBe(true)
      await userEvent.click(box('Select Manual savings'))
      expect(screen.getByText('3 selected')).toBeInTheDocument()
      // Clearing the group removes only its members.
      await userEvent.click(group)
      expect(screen.getByText('1 selected')).toBeInTheDocument()
      expect(box('Select Manual savings').checked).toBe(true)
      expect(group.checked).toBe(false)
    })

    it('labels the account-less group checkbox and selects exactly its members', async () => {
      render(<GroupHarness initial={{ groupBy: 'address' }} />)
      await userEvent.click(box('Select all accounts without an address'))
      expect(screen.getByText('2 selected')).toBeInTheDocument()
      expect(box('Select Manual savings').checked).toBe(true)
      expect(box('Select Robinhood main').checked).toBe(true)
    })

    it('moves exactly the selected group through the existing bulk bar', async () => {
      const api = { ...createWorkspaceApi(credentialsFixture), moveAccounts: vi.fn().mockResolvedValue(grouped) }
      render(<GroupHarness api={api} />)
      await chooseGroup('Venue')
      await userEvent.click(box('Select all Hyperliquid accounts'))
      expect(screen.getByText('3 selected')).toBeInTheDocument()
      await userEvent.click(screen.getByRole('button', { name: 'Move' }))
      await waitFor(() => expect(api.moveAccounts).toHaveBeenCalledTimes(1))
      expect(api.moveAccounts).toHaveBeenCalledWith(['alpha', 'gamma', 'old'], null,
        ['alpha', 'gamma', 'old'].map(accountId => ({ accountId, settingsRevision: 1 })))
    })

    it('drops a selected account that a filter hides, and keeps the rest selected', async () => {
      render(<GroupHarness initial={{ groupBy: 'venue' }} />)
      await userEvent.click(box('Select all Hyperliquid accounts'))
      await userEvent.click(box('Select Manual savings'))
      expect(screen.getByText('4 selected')).toBeInTheDocument()
      await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by status' }), 'enabled')
      expect(screen.getByText('3 selected')).toBeInTheDocument()
      expect(box('Select all Hyperliquid accounts').checked).toBe(true)
    })
  })
})
