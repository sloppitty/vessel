import { useState } from 'react'
import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { BrokerAccount, Portfolio } from '@/api/workspace'
import { emptyStructuredReview } from '@/api/plays'
import { accountFixture, portfolioFixture } from '@/test/workspace-fixture'
import { AvailableBudget, CapitalContext, PlayJournal, PositionSummary } from './WorkspacePanels'
import { journalReviewVisible } from './journal'
import { createDraft, type PlayDraft } from './draft'
import type { AvailableBalance } from './sizing'

function BudgetHarness({ initial = createDraft(), accounts = [], portfolios = [], onChange = vi.fn() }: {
  initial?: PlayDraft
  accounts?: BrokerAccount[]
  portfolios?: Portfolio[]
  onChange?: (draft: PlayDraft) => void
}) {
  const [draft, setDraft] = useState(initial)
  return <><CapitalContext accounts={accounts} portfolios={portfolios} draft={draft} />
    <AvailableBudget key={draft.accountId} draft={draft} onChange={next => { setDraft(next); onChange(next) }} /></>
}

describe('Capital context', () => {
  const metric = (label: string) => within(screen.getByTestId('capital-context')).getByText(label).closest('div')!

  it('does not replace an explicitly unknown portfolio or account balance with perps equity', () => {
    const account = { ...accountFixture, balanceUsd: null, portfolioId: null }
    const { rerender } = render(<BudgetHarness initial={{ ...createDraft(), accountId: account.id }} accounts={[account]} />)
    expect(metric('Account balance')).toHaveTextContent('Unavailable')
    rerender(<BudgetHarness initial={{ ...createDraft(), accountId: account.id }}
      accounts={[{ ...account, portfolioId: portfolioFixture.id }]} portfolios={[{ ...portfolioFixture, balanceUsd: null }]} />)
    expect(metric(`${portfolioFixture.name} balance`)).toHaveTextContent('Unavailable')
  })
  it('leaves unknown values and budget unavailable without seeding accounts', () => {
    render(<BudgetHarness />)
    const capital = screen.getByTestId('capital-context')
    expect(metric('Account balance')).toHaveTextContent('Unavailable')
    expect(within(capital).queryByText('Wallet total')).toBeNull()
    expect(metric('Margin / balance')).toHaveTextContent('—')
    expect(screen.getByRole('button', { name: 'Set budget' })).toBeInTheDocument()
    expect(within(capital).queryByRole('combobox')).not.toBeInTheDocument()
  })

  it('shows the portfolio balance, wallet total and available amount of a unified account without perps equity', () => {
    const account: BrokerAccount = { ...accountFixture, venueId: 'hyperliquid', accountValueUsd: '0',
      availableStablecoinNominalUsd: '150', totalStablecoinNominalUsd: '151.77', balanceUsd: '151.77',
      stablecoinScope: 'hypercore-spot-stablecoins', accountMode: 'unifiedAccount' }
    const portfolio: Portfolio = { ...portfolioFixture, totalValueUsd: '0', balanceUsd: '1151.77', balanceCoverage: 'complete' }
    render(<BudgetHarness initial={{ ...createDraft(), accountId: account.id }} accounts={[account]} portfolios={[portfolio]} />)
    expect(metric(`${portfolio.name} balance`)).toHaveTextContent('$1,151.77')
    expect(metric(`${portfolio.name} balance`)).toHaveAttribute('title', expect.stringContaining('coverage complete'))
    expect(metric('Wallet total')).toHaveTextContent('$151.77')
    expect(metric('Wallet total')).toHaveAttribute('title', 'hypercore-spot-stablecoins · unifiedAccount. Nominal 1 token = 1 USD.')
    expect(metric('Available')).toHaveTextContent('$150.00')
    expect(within(screen.getByTestId('capital-context')).queryByText('Perps equity')).toBeNull()
  })

  it('shows perps equity beside the wallet for a standard account', () => {
    const account: BrokerAccount = { ...accountFixture, venueId: 'hyperliquid', portfolioId: null, accountValueUsd: '1000',
      availableStablecoinNominalUsd: '10', totalStablecoinNominalUsd: '10', balanceUsd: '1010', accountMode: 'default' }
    render(<BudgetHarness initial={{ ...createDraft(), accountId: account.id }} accounts={[account]} portfolios={[portfolioFixture]} />)
    expect(metric('Account balance')).toHaveTextContent('$1,010.00')
    expect(metric('Perps equity')).toHaveTextContent('$1,000.00')
  })

  it('shows margin and exposure as shares of the balance once the position is sized', () => {
    const portfolio: Portfolio = { ...portfolioFixture, totalValueUsd: '10000', balanceUsd: '10000' }
    const draft = { ...createDraft(), accountId: accountFixture.id, size: '500', leverage: '4' }
    draft.entries[0]!.price = '100'
    render(<BudgetHarness initial={draft} accounts={[accountFixture]} portfolios={[portfolio]} />)
    // Phase 0, 0.1 (D-12): each ratio names the balance it divides by.
    expect(metric('Margin / Swing trading balance')).toHaveTextContent('5.0%')
    expect(metric('Exposure / Swing trading balance')).toHaveTextContent('20.0%')
  })

  it('names an unassigned account in its ratios, since its own balance is the reference', () => {
    const account = { ...accountFixture, portfolioId: null, balanceUsd: '1000' }
    const draft = { ...createDraft(), accountId: account.id, size: '50', leverage: '2' }
    draft.entries[0]!.price = '100'
    render(<BudgetHarness initial={draft} accounts={[account]} portfolios={[portfolioFixture]} />)
    expect(metric('Margin / Main account balance')).toHaveTextContent('5.0%')
    expect(metric('Exposure / Main account balance')).toHaveTextContent('10.0%')
  })

  it('keeps a reported zero distinct from missing values and does not create a default portfolio', () => {
    const account = { ...accountFixture, portfolioId: null, accountValueUsd: '0' }
    render(<BudgetHarness initial={{ ...createDraft(), accountId: account.id }} accounts={[account]} portfolios={[portfolioFixture]} />)
    expect(metric('Account balance')).toHaveTextContent('$0.00')
    expect(screen.queryByText(/Swing trading/)).not.toBeInTheDocument()
  })

  it('does not reveal a disabled account value or its portfolio', () => {
    render(<BudgetHarness initial={{ ...createDraft(), accountId: accountFixture.id }}
      accounts={[{ ...accountFixture, isEnabled: false }]} portfolios={[portfolioFixture]} />)
    expect(screen.queryByText('$1,250.12')).not.toBeInTheDocument()
    expect(metric('Account balance')).toHaveTextContent('Unavailable')
  })

})

describe('Budget', () => {
  function Budget({ initial = createDraft(), available = null as AvailableBalance | null, onChange = vi.fn() }) {
    const [draft, setDraft] = useState(initial)
    return <AvailableBudget key={draft.accountId} draft={draft} available={available} onChange={next => { setDraft(next); onChange(next) }} />
  }
  const budget = () => screen.getByRole('group', { name: 'Budget' })

  it('uses the available amount until a manual budget is set, and can go back to it', async () => {
    const onChange = vi.fn()
    render(<Budget available={{ value: '151.77', source: 'wallet' }} onChange={onChange} />)
    expect(budget()).toHaveTextContent('Budget$151.77wallet')
    await userEvent.click(screen.getByRole('button', { name: 'Edit budget' }))
    const input = screen.getByRole('textbox', { name: 'Budget $' })
    expect(input).toHaveAttribute('placeholder', '151.77')
    await userEvent.type(input, '9007199254740990.99')
    expect(onChange).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Save budget' }))
    expect(onChange.mock.lastCall![0].budgetOverride).toBe('9007199254740990.99')
    expect(budget()).toHaveTextContent('$9,007,199,254,740,990.99manual')
    await userEvent.click(screen.getByRole('button', { name: 'Use the available amount' }))
    expect(onChange.mock.lastCall![0].budgetOverride).toBeNull()
    expect(budget()).toHaveTextContent('$151.77wallet')
  })

  // Phase 0, 0.1 (D-07, decision 6): only null is unknown, and the budget names where it comes from.
  it('keeps a known zero available balance as a zero budget', () => {
    render(<Budget available={{ value: '0', source: 'wallet' }} />)
    expect(budget()).toHaveTextContent('Budget$0.00wallet')
    expect(screen.queryByRole('button', { name: 'Set budget' })).toBeNull()
  })

  it.each([
    ['margin', 'reported margin'], ['withdrawable', 'reported withdrawal'], ['wallet', 'wallet'],
  ] as const)('names a %s budget source', (source, label) => {
    render(<Budget available={{ value: '25', source }} />)
    expect(budget()).toHaveTextContent(`Budget$25.00${label}`)
  })

  it('offers only a set action without an available amount, and accepts an explicit zero', async () => {
    const onChange = vi.fn()
    render(<Budget onChange={onChange} />)
    expect(screen.queryByRole('group', { name: 'Budget' })).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Set budget' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Budget $' }), '100')
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(onChange).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Set budget' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Set budget' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Budget $' }), '0')
    await userEvent.click(screen.getByRole('button', { name: 'Save budget' }))
    expect(onChange.mock.calls[0]![0].budgetOverride).toBe('0')
    expect(budget()).toHaveTextContent('$0.00manual')
    expect(screen.queryByRole('button', { name: 'Use the available amount' })).toBeNull()
  })

  it('restores the saved value when an edit is cancelled', async () => {
    const onChange = vi.fn()
    render(<Budget initial={{ ...createDraft(), budgetOverride: '250' }} onChange={onChange} />)
    await userEvent.click(screen.getByRole('button', { name: 'Edit budget' }))
    const input = screen.getByRole('textbox', { name: 'Budget $' })
    expect(input).toHaveValue('250')
    await userEvent.clear(input)
    await userEvent.type(input, '500')
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(onChange).not.toHaveBeenCalled()
    expect(budget()).toHaveTextContent('$250.00')
  })

  it.each(['-10', 'NaN', '1e6', '1,000', '1.2.3'])('rejects invalid budget %s without applying it', async (value) => {
    const onChange = vi.fn()
    render(<Budget onChange={onChange} />)
    await userEvent.click(screen.getByRole('button', { name: 'Set budget' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Budget $' }), value)
    await userEvent.click(screen.getByRole('button', { name: 'Save budget' }))
    expect(onChange).not.toHaveBeenCalled()
    expect(screen.getByRole('alert')).toHaveTextContent('Enter a non-negative USD amount')
    expect(screen.getByRole('textbox', { name: 'Budget $' })).toHaveAttribute('aria-invalid', 'true')
  })

  it('discards an open unsaved budget when the selected account changes', async () => {
    const onChange = vi.fn()
    const initial = { ...createDraft(), accountId: accountFixture.id }
    const { rerender } = render(<AvailableBudget key={initial.accountId} draft={initial} onChange={onChange} />)
    await userEvent.click(screen.getByRole('button', { name: 'Set budget' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Budget $' }), '100')
    rerender(<AvailableBudget key="" draft={{ ...initial, accountId: '' }} onChange={onChange} />)
    expect(screen.queryByRole('button', { name: 'Save budget' })).not.toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
  })
})

describe('Play journal', () => {
  it('keeps thesis, invalidation, strategy, evidence and review separate when switching tabs', async () => {
    const onChange = vi.fn()
    function JournalHarness() {
      const [notes, setNotes] = useState(createDraft().notes)
      return <PlayJournal notes={notes} status="closed" onChange={(next) => { setNotes(next); onChange(next) }} />
    }
    render(<JournalHarness />)
    // Tabs share the heading row so the note field keeps most of the journal height.
    expect(screen.getByRole('heading', { name: 'Play journal' }).closest('header'))
      .toContainElement(screen.getByRole('tablist', { name: 'Journal sections' }))
    const sections = ['Thesis', 'Invalidation', 'Strategy', 'Evidence', 'Review']
    for (const label of sections) {
      await userEvent.click(screen.getByRole('tab', { name: label }))
      await userEvent.type(screen.getByRole('textbox', { name: label }), `${label} text`)
    }
    expect(onChange.mock.lastCall![0]).toEqual({
      thesis: 'Thesis text', invalidation: 'Invalidation text', strategy: 'Strategy text',
      evidence: 'Evidence text', review: 'Review text',
    })
    for (const label of sections) {
      await userEvent.click(screen.getByRole('tab', { name: label }))
      expect(screen.getByRole('textbox', { name: label })).toHaveValue(`${label} text`)
      expect(screen.getAllByRole('textbox')).toHaveLength(1)
    }
    await userEvent.click(screen.getByRole('tab', { name: 'Evidence' }))
    expect(screen.getByRole('textbox', { name: 'Evidence' })).toHaveAttribute('placeholder', 'Notes on the evidence')
    expect(screen.getByTestId('journal-panel').querySelector('input[type="file"]')).toBeNull()
  })

  it('offers the review once a play is Open, Closed or Cancelled, never for Draft, Planned or Paused', () => {
    const notes = createDraft().notes
    const { rerender } = render(<PlayJournal notes={notes} onChange={vi.fn()} />)
    const tabs = () => screen.getAllByRole('tab').map(tab => tab.textContent)
    expect(tabs()).toEqual(['Thesis', 'Invalidation', 'Strategy', 'Evidence'])
    for (const status of ['draft', 'planned', 'paused'] as const) {
      rerender(<PlayJournal notes={notes} onChange={vi.fn()} status={status} />)
      expect(screen.queryByRole('tab', { name: 'Review' })).not.toBeInTheDocument()
    }
    for (const status of ['open', 'closed', 'cancelled'] as const) {
      rerender(<PlayJournal notes={notes} onChange={vi.fn()} status={status} />)
      expect(screen.getByRole('tab', { name: 'Review' })).toBeInTheDocument()
    }
    // Retrospective plays exist to be reviewed and open on their review.
    rerender(<PlayJournal key="retrospective" notes={notes} onChange={vi.fn()} status="closed" retrospective />)
    expect(tabs()).toEqual(['Evidence', 'Review'])
    expect(screen.getByRole('tab', { name: 'Review' })).toHaveAttribute('aria-selected', 'true')
  })

  it('never hides a review that was already written, whatever the status', () => {
    const notes = { ...createDraft().notes, review: 'Written before planning' }
    const { rerender } = render(<PlayJournal notes={notes} onChange={vi.fn()} status="planned" />)
    expect(screen.getByRole('tab', { name: 'Review' })).toBeInTheDocument()
    rerender(<PlayJournal notes={createDraft().notes} onChange={vi.fn()} status="paused" onStructuredReviewChange={vi.fn()}
      structuredReview={{ ...emptyStructuredReview(), lesson: 'Kept answer' }} />)
    expect(screen.getByRole('tab', { name: 'Review' })).toBeInTheDocument()
    rerender(<PlayJournal notes={{ ...createDraft().notes, review: '   ' }} onChange={vi.fn()} status="draft" />)
    expect(screen.queryByRole('tab', { name: 'Review' })).not.toBeInTheDocument()
  })

  it('keeps the Review tab while an earlier note is rewritten, and drops it once left empty before the play opens', async () => {
    function Harness() {
      const [notes, setNotes] = useState({ ...createDraft().notes, review: 'Written before planning' })
      return <PlayJournal notes={notes} status="planned" onChange={setNotes} />
    }
    render(<Harness />)
    await userEvent.click(screen.getByRole('tab', { name: 'Review' }))
    await userEvent.clear(screen.getByRole('textbox', { name: 'Review' }))
    expect(screen.getByRole('tab', { name: 'Review' })).toHaveAttribute('aria-selected', 'true')
    await userEvent.type(screen.getByRole('textbox', { name: 'Review' }), 'Rewritten')
    await userEvent.clear(screen.getByRole('textbox', { name: 'Review' }))
    await userEvent.click(screen.getByRole('tab', { name: 'Strategy' }))
    expect(screen.queryByRole('tab', { name: 'Review' })).not.toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Strategy' })).toHaveAttribute('aria-selected', 'true')
  })

  it('falls back to Thesis when the selected Review tab is no longer offered', async () => {
    const notes = createDraft().notes
    const { rerender } = render(<PlayJournal notes={notes} onChange={vi.fn()} status="open" />)
    await userEvent.click(screen.getByRole('tab', { name: 'Review' }))
    rerender(<PlayJournal notes={notes} onChange={vi.fn()} status="planned" />)
    expect(screen.queryByRole('tab', { name: 'Review' })).not.toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Thesis' })).toHaveAttribute('aria-selected', 'true')
    // The fallback sticks: offering the review again does not jump back to it.
    rerender(<PlayJournal notes={notes} onChange={vi.fn()} status="open" />)
    expect(screen.getByRole('tab', { name: 'Thesis' })).toHaveAttribute('aria-selected', 'true')
  })

  it('decides review visibility from status, origin and existing answers', () => {
    const blank = { review: '', structuredReview: emptyStructuredReview() }
    expect(journalReviewVisible({ status: 'draft', ...blank })).toBe(false)
    expect(journalReviewVisible({ status: 'planned', ...blank })).toBe(false)
    expect(journalReviewVisible({ status: 'paused', ...blank })).toBe(false)
    expect(journalReviewVisible({ status: 'open', ...blank })).toBe(true)
    expect(journalReviewVisible({ status: 'closed', ...blank })).toBe(true)
    expect(journalReviewVisible({ status: 'cancelled', ...blank })).toBe(true)
    expect(journalReviewVisible({ status: 'draft', retrospective: true, ...blank })).toBe(true)
    expect(journalReviewVisible({ status: 'draft', review: 'Note', structuredReview: undefined })).toBe(true)
    expect(journalReviewVisible({ status: 'planned', review: '', structuredReview: { ...emptyStructuredReview(), adherence: 'followed' } })).toBe(true)
  })

  it('expands into an in-page dialog on the same tab and notes, and returns focus on Escape', async () => {
    const onChange = vi.fn()
    function JournalHarness() {
      const [notes, setNotes] = useState({ ...createDraft().notes, strategy: 'Range reclaim' })
      return <PlayJournal notes={notes} status="open" onChange={(next) => { setNotes(next); onChange(next) }} />
    }
    render(<JournalHarness />)
    await userEvent.click(screen.getByRole('tab', { name: 'Strategy' }))
    const expand = screen.getByRole('button', { name: 'Expand journal' })
    await userEvent.click(expand)
    const dialog = screen.getByRole('dialog', { name: 'Play journal' })
    // The panel keeps its place; the one live journal moves into the dialog, so ids stay unique.
    expect(within(screen.getByTestId('journal-panel')).queryByRole('tablist')).toBeNull()
    expect(within(dialog).getByRole('tab', { name: 'Strategy' })).toHaveAttribute('aria-selected', 'true')
    const field = within(dialog).getByRole('textbox', { name: 'Strategy' })
    expect(field).toHaveValue('Range reclaim')
    await userEvent.type(field, ' at the weekly low')
    expect(onChange.mock.lastCall![0]).toMatchObject({ strategy: 'Range reclaim at the weekly low' })
    await userEvent.click(within(dialog).getByRole('tab', { name: 'Review' }))
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.getByRole('button', { name: 'Expand journal' })).toHaveFocus()
    // The tab chosen in the dialog stays selected inline, and the edit made there is kept.
    expect(screen.getByRole('tab', { name: 'Review' })).toHaveAttribute('aria-selected', 'true')
    await userEvent.click(screen.getByRole('tab', { name: 'Strategy' }))
    expect(screen.getByRole('textbox', { name: 'Strategy' })).toHaveValue('Range reclaim at the weekly low')
  })

  it('closes the expanded journal from its own header button', async () => {
    render(<PlayJournal notes={createDraft().notes} onChange={vi.fn()} />)
    await userEvent.click(screen.getByRole('button', { name: 'Expand journal' }))
    const dialog = screen.getByRole('dialog', { name: 'Play journal' })
    expect(within(dialog).queryByRole('tab', { name: 'Review' })).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Close expanded journal' }))
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.getByRole('button', { name: 'Expand journal' })).toHaveFocus()
    expect(screen.getByRole('tablist', { name: 'Journal sections' })).toBeInTheDocument()
  })
})

describe('Position summary', () => {
  it('derives margin, position size and quantity from the margin and leverage, but no payoff', () => {
    const draft = { ...createDraft(), size: '1000', leverage: '5' }
    draft.entries[0]!.price = '2000'
    draft.entries[0]!.stops[0]!.value = '1900'
    draft.entries[0]!.targets[0]!.value = '2200'
    render(<PositionSummary draft={draft} units={{ quote: 'USDC', base: 'ETH', quantityDecimals: 4 }} instrumentName="ETH/USDC" />)
    const summary = screen.getByTestId('summary-panel')
    const value = (label: string) => within(summary).getByText(label).nextElementSibling
    expect(value('Margin')).toHaveTextContent('1,000 USDC')
    expect(value('Position size')).toHaveTextContent('5,000 USDC')
    expect(value('Quantity')).toHaveTextContent('2.5 ETH')
    expect(value('Average entry')).toHaveTextContent('2000')
    expect(within(summary).queryByText(/Not calculated|1900|2200/)).not.toBeInTheDocument()
    expect(within(summary).getByText('Long')).toHaveAttribute('data-direction', 'long')
    expect(within(summary).getByText('ETH/USDC · Leverage 5×')).toBeInTheDocument()
    expect(value('Position size')).toHaveAttribute('data-placeholder', 'false')
  })

  it('derives margin from a quantity once an entry is priced', () => {
    const draft = { ...createDraft(), sizingMode: 'quantity' as const, size: '0.125', leverage: '4' }
    const view = render(<PositionSummary draft={draft} />)
    expect(screen.getByText('Margin').nextElementSibling).toHaveTextContent('Needs an entry price')
    expect(screen.getByText('Quantity').nextElementSibling).toHaveTextContent('0.125 units')
    draft.entries[0]!.price = '80000'
    view.rerender(<PositionSummary draft={{ ...draft }} />)
    expect(screen.getByText('Position size').nextElementSibling).toHaveTextContent('10,000 quote units')
    expect(screen.getByText('Margin').nextElementSibling).toHaveTextContent('2,500 quote units')
  })

  it('does not insert a sample size for an empty draft', () => {
    render(<PositionSummary draft={createDraft()} />)
    expect(screen.getByText('Not set')).toHaveAttribute('data-placeholder', 'true')
    expect(screen.getByText('No instrument · Leverage 1×')).toBeInTheDocument()
    expect(screen.getAllByText('Needs a size')).toHaveLength(3)
  })
})
