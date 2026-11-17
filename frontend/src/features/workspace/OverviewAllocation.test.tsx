import { fireEvent, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import type { BrokerAccount, Portfolio } from '@/api/workspace'
import { accountFixture } from '@/test/workspace-fixture'
import { render } from '@/test/render'
import { OverviewAllocation } from './OverviewAllocation'

const portfolio = (id: string, name: string): Portfolio => ({ id, name, accountCount: 0, totalValueUsd: null, valueCoverage: 'unavailable', icon: 'folder',
  balanceUsd: null, balanceCoverage: 'unavailable', availableBalanceUsd: null, availableBalanceAccountCount: 0, availableBalanceCoverage: 'unavailable', enabledAccountCount: 0 })
const account = (id: string, portfolioId: string | null, balanceUsd: string | null, isEnabled = true): BrokerAccount => ({
  ...accountFixture, id, portfolioId, name: id, accountValueUsd: balanceUsd, isEnabled, balanceUsd,
})
const portfolios = [portfolio('p1', 'Swing'), portfolio('p2', 'Core'), portfolio('p3', 'Empty')]
const accounts = [account('Alpha', 'p1', '300'), account('Beta', 'p1', '100'), account('Gamma', 'p2', '600'),
  account('Delta', null, null), account('Hidden', 'p1', '5000', false)]

describe('Overview allocation', () => {
  it('shows each account as a capsule with its venue, its source and the full identity in the title', () => {
    const hl = { ...account('hl', 'p1', '300'), name: 'main', venueId: 'hyperliquid', address: '0x6d1e9c42aa1b3c4d5e6f708192a3b4c5d6e7f389' }
    const rh = { ...account('rh', 'p1', '100'), name: 'main', venueId: 'lighter-rh', sourceId: '48213' }
    const blank = { ...account('blank', 'p1', '50'), name: ' ', venueId: 'hyperliquid', address: '0x3f5ab2c19e0d4471a8b6e2f0c9d1aa7c3b21c0de' }
    render(<OverviewAllocation accounts={[hl, rh, blank]} portfolios={[portfolio('p1', 'Swing')]} scope="all" onScope={vi.fn()} />)
    const legend = screen.getByRole('list', { name: 'Swing shares' })
    const first = within(legend).getAllByRole('button', { name: 'Show allocation for main' })[0]!
    expect(first).toHaveAttribute('title', 'main · Hyperliquid · 0x6d1e9c42aa1b3c4d5e6f708192a3b4c5d6e7f389')
    expect(within(first).getByText('Hyperliquid')).toBeInTheDocument()
    expect(within(first).getByText('0x6d1e…f389')).toHaveAttribute('title', '0x6d1e9c42aa1b3c4d5e6f708192a3b4c5d6e7f389')
    const second = within(legend).getAllByRole('button', { name: 'Show allocation for main' })[1]!
    expect(within(second).getByText('Lighter · Robinhood')).toBeInTheDocument()
    expect(within(second).getByText('#48213')).toBeInTheDocument()
    // An unnamed account reads as its short source, tagged Unnamed.
    const unnamed = within(legend).getByRole('button', { name: 'Show allocation for 0x3f5a…c0de' })
    expect(within(unnamed).getByText('Unnamed')).toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'Swing balance shares: main 66.67%, main 22.22%, 0x3f5a…c0de 11.11%' })).toBeInTheDocument()
  })
  it('shows portfolio shares of the total and account shares within each portfolio, excluding disabled accounts', () => {
    const onScope = vi.fn()
    render(<OverviewAllocation accounts={accounts} portfolios={portfolios} scope="all" onScope={onScope} />)
    const panel = screen.getByRole('region', { name: 'Balance allocation' })
    expect(within(panel).getByRole('img', { name: 'All accounts balance shares: Core 60.00%, Swing 40.00%' })).toBeInTheDocument()
    const swing = within(panel).getByRole('article', { name: 'Swing' })
    expect(within(swing).getByText('40.00% of total')).toBeInTheDocument()
    expect(within(swing).getByRole('img', { name: 'Swing balance shares: Alpha 75.00%, Beta 25.00%' })).toBeInTheDocument()
    // Empty portfolios have no card; Unassigned keeps its account without a balance visible.
    expect(within(panel).queryByRole('article', { name: 'Empty' })).not.toBeInTheDocument()
    expect(within(within(panel).getByRole('article', { name: 'Unassigned' })).getByText('No positive balance')).toBeInTheDocument()
    expect(within(panel).getAllByText('1 account without a reported balance')).toHaveLength(2)
    expect(within(panel).queryByText('Hidden')).not.toBeInTheDocument()
    fireEvent.click(within(swing).getByRole('button', { name: 'Show allocation for Alpha' }))
    expect(onScope).toHaveBeenLastCalledWith('account:Alpha')
    fireEvent.click(within(swing).getByRole('button', { name: 'Show allocation for Swing' }))
    expect(onScope).toHaveBeenLastCalledWith('portfolio:p1')
  })

  it('narrows to a portfolio and emphasizes a selected account within it', () => {
    const { rerender } = render(<OverviewAllocation accounts={accounts} portfolios={portfolios} scope="portfolio:p1" onScope={() => {}} />)
    expect(screen.getByRole('heading', { name: /^Swing\s*·\s*40\.00% of total$/ })).toBeInTheDocument()
    expect(screen.getByText('$400.00')).toBeInTheDocument()
    rerender(<OverviewAllocation accounts={accounts} portfolios={portfolios} scope="account:Beta" onScope={() => {}} />)
    const legend = screen.getByRole('list', { name: 'Swing shares' })
    // The selected account is the pressed toggle; the other account stays selectable but dimmed.
    expect(within(legend).getByRole('button', { name: 'Show allocation for Beta' })).toHaveAttribute('aria-pressed', 'true')
    expect(within(legend).getByRole('button', { name: 'Show allocation for Alpha' })).toHaveAttribute('aria-pressed', 'false')
    expect(within(legend).getByRole('button', { name: 'Show allocation for Alpha' }).closest('li')).toHaveClass('allocation-dim')
    expect(within(legend).getByText('Beta').closest('li')).not.toHaveClass('allocation-dim')
  })

  it('drops focus emphasis when the scope changes, so the newly selected account is emphasized', () => {
    const { rerender } = render(<OverviewAllocation accounts={accounts} portfolios={portfolios} scope="portfolio:p1" onScope={() => {}} />)
    fireEvent.focus(screen.getByRole('button', { name: 'Show allocation for Alpha' }))
    rerender(<OverviewAllocation accounts={accounts} portfolios={portfolios} scope="account:Alpha" onScope={() => {}} />)
    rerender(<OverviewAllocation accounts={accounts} portfolios={portfolios} scope="account:Beta" onScope={() => {}} />)
    const legend = screen.getByRole('list', { name: 'Swing shares' })
    expect(within(legend).getByText('Beta').closest('li')).toHaveClass('allocation-active')
    expect(within(legend).getByRole('button', { name: 'Show allocation for Alpha' }).closest('li')).toHaveClass('allocation-dim')
  })

  it('folds a seventh account into Other and highlights Other for an account inside it', () => {
    const many = ['70', '60', '50', '40', '30', '20', '10'].map((v, i) => account(`a${i}`, 'p1', v))
    render(<OverviewAllocation accounts={many} portfolios={portfolios} scope="account:a6" onScope={() => {}} />)
    const legend = screen.getByRole('list', { name: 'Swing shares' })
    expect(within(legend).getAllByRole('listitem')).toHaveLength(6)
    expect(within(legend).getByText('Other (2)').closest('li')).not.toHaveClass('allocation-dim')
    expect(within(legend).getByText('a0').closest('li')).toHaveClass('allocation-dim')
  })

  it('gives every portfolio its share of the total even when the total chart folds it into Other', () => {
    const many = Array.from({ length: 7 }, (_, i) => portfolio(`q${i}`, `P${i}`))
    const list = many.map((p, i) => account(`acc${i}`, p.id, String((i + 1) * 10)))
    const { rerender } = render(<OverviewAllocation accounts={list} portfolios={many} scope="all" onScope={() => {}} />)
    expect(within(screen.getByRole('article', { name: 'P0' })).getByText('3.57% of total')).toBeInTheDocument()
    expect(screen.getByText('Other (2)')).toBeInTheDocument()
    rerender(<OverviewAllocation accounts={list} portfolios={many} scope="portfolio:q0" onScope={() => {}} />)
    expect(screen.getByRole('heading', { name: /^P0\s*·\s*3\.57% of total$/ })).toBeInTheDocument()
  })

  describe('going back to all accounts', () => {
    const withUnassigned = [...accounts.filter(a => a.id !== 'Delta'), account('Delta', null, '50'), account('Eps', null, '150')]
    // Unassigned holds only Solo (a one-slice donut, like a lone sandbox account); Core holds only Gamma.
    const solo = [...accounts.filter(a => a.id !== 'Delta'), account('Solo', null, '50')]
    const backName = 'Back to all accounts'
    /** Keeps the scope in state like the Overview page, so a click is followed by the real next render. */
    function Harness({ initial, list = accounts }: { initial: string; list?: BrokerAccount[] }) {
      const [scope, setScope] = useState(initial)
      return <><output aria-label="scope">{scope}</output>
        <OverviewAllocation accounts={list} portfolios={portfolios} scope={scope} onScope={setScope} /></>
    }
    const scopeOf = () => screen.getByLabelText('scope').textContent
    const legendButton = (name: string) => screen.getByRole('button', { name: `Show allocation for ${name}` })
    const slice = (title: string) => screen.getByText(title)

    it('offers no back arrow while all accounts are shown', () => {
      render(<Harness initial="all" />)
      expect(screen.queryByRole('button', { name: /^Back to/ })).not.toBeInTheDocument()
    })

    it.each([
      ['a portfolio', 'portfolio:p1', accounts, /^Swing/],
      ['Unassigned', 'unassigned', withUnassigned, /^Unassigned/],
      ['an account of a portfolio with several accounts', 'account:Beta', accounts, /^Swing/],
      ['the only account of a portfolio', 'account:Gamma', accounts, /^Core/],
      ['an unassigned account', 'account:Eps', withUnassigned, /^Unassigned/],
    ])('returns %s to all accounts with the back arrow beside the heading', (_name, initial, list, heading) => {
      render(<Harness initial={initial} list={list} />)
      // The heading is plain text, and the arrow before it always names the same destination.
      const title = screen.getByRole('heading', { name: heading })
      expect(within(title).queryByRole('button')).not.toBeInTheDocument()
      const back = screen.getByRole('button', { name: backName })
      expect(back).toHaveAttribute('title', backName)
      expect(back).not.toHaveAttribute('aria-pressed')
      expect(title.previousElementSibling).toBe(back)
      expect(screen.queryByRole('button', { name: /^Back to (?!all accounts$)/ })).not.toBeInTheDocument()
      fireEvent.click(back)
      expect(scopeOf()).toBe('all')
      expect(screen.getByRole('img', { name: /^All accounts balance shares/ })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /^Back to/ })).not.toBeInTheDocument()
    })

    it('deselects an account to all accounts when its slice is clicked again, and moves the selection in one click', () => {
      render(<Harness initial="account:Beta" />)
      const beta = () => slice('Beta: $100.00 · 25.00%'), alpha = () => slice('Alpha: $300.00 · 75.00%')
      expect(beta().parentElement).toHaveAttribute('data-scope', 'all')
      expect(alpha().parentElement).toHaveAttribute('data-scope', 'account:Alpha')
      fireEvent.click(beta())
      expect(scopeOf()).toBe('all')
      // Choosing an account selects it; choosing the other one while it is selected moves the selection instead of clearing it.
      fireEvent.click(alpha())
      expect(scopeOf()).toBe('account:Alpha')
      fireEvent.click(beta())
      expect(scopeOf()).toBe('account:Beta')
      fireEvent.click(beta())
      expect(scopeOf()).toBe('all')
    })

    it('deselects an unassigned account to all accounts, not to Unassigned', () => {
      render(<Harness initial="account:Eps" list={withUnassigned} />)
      fireEvent.click(slice('Eps: $150.00 · 75.00%'))
      expect(scopeOf()).toBe('all')
    })

    it('deselects through the selected legend item, which reports its state', () => {
      render(<Harness initial="portfolio:p1" />)
      expect(legendButton('Beta')).toHaveAttribute('aria-pressed', 'false')
      fireEvent.click(legendButton('Beta'))
      expect(scopeOf()).toBe('account:Beta')
      expect(legendButton('Beta')).toHaveAttribute('aria-pressed', 'true')
      expect(legendButton('Alpha')).toHaveAttribute('aria-pressed', 'false')
      fireEvent.click(legendButton('Beta'))
      expect(scopeOf()).toBe('all')
      expect(legendButton('Beta')).toHaveAttribute('aria-pressed', 'false')
      // The same item selects it again, so the toggle can be repeated.
      fireEvent.click(legendButton('Beta'))
      expect(scopeOf()).toBe('account:Beta')
    })

    it('goes back to all accounts, not to the portfolio, after choosing accounts one after another in a portfolio picked in the select', () => {
      render(<Harness initial="portfolio:p1" />)
      fireEvent.click(legendButton('Alpha'))
      expect(scopeOf()).toBe('account:Alpha')
      fireEvent.click(legendButton('Beta'))
      expect(scopeOf()).toBe('account:Beta')
      expect(screen.getByRole('button', { name: backName })).toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: backName }))
      expect(scopeOf()).toBe('all')
    })

    it('goes back to all accounts however the selection was reached from the all view', () => {
      render(<Harness initial="all" />)
      // Portfolio from the total legend, then an account in it.
      fireEvent.click(within(screen.getByRole('list', { name: 'All accounts shares' })).getByRole('button', { name: 'Show allocation for Swing' }))
      fireEvent.click(legendButton('Beta'))
      expect(scopeOf()).toBe('account:Beta')
      fireEvent.click(screen.getByRole('button', { name: backName }))
      expect(scopeOf()).toBe('all')
      // An account straight from its small donut, then a neighbour of the same portfolio.
      fireEvent.click(within(screen.getByRole('article', { name: 'Swing' })).getByText('Alpha: $300.00 · 75.00%'))
      expect(scopeOf()).toBe('account:Alpha')
      fireEvent.click(legendButton('Beta'))
      fireEvent.click(screen.getByRole('button', { name: backName }))
      expect(scopeOf()).toBe('all')
      // A portfolio from its card heading.
      fireEvent.click(within(screen.getByRole('article', { name: 'Core' })).getByRole('button', { name: 'Show allocation for Core' }))
      expect(scopeOf()).toBe('portfolio:p2')
      fireEvent.click(screen.getByRole('button', { name: backName }))
      expect(scopeOf()).toBe('all')
    })

    it('returns a lone Unassigned account to all accounts whether Unassigned came from the select or its card, by slice or arrow', () => {
      const soloSlice = () => slice('Solo: $50.00 · 100.00%')
      const { unmount } = render(<Harness initial="unassigned" list={solo} />)
      fireEvent.click(soloSlice())
      expect(scopeOf()).toBe('account:Solo')
      expect(screen.getByRole('button', { name: backName })).toBeInTheDocument()
      fireEvent.click(soloSlice())
      expect(scopeOf()).toBe('all')
      unmount()
      render(<Harness initial="all" list={solo} />)
      for (const route of ['slice', 'arrow']) {
        fireEvent.click(within(screen.getByRole('article', { name: 'Unassigned' })).getByRole('button', { name: 'Show allocation for Unassigned' }))
        expect(scopeOf()).toBe('unassigned')
        fireEvent.click(soloSlice())
        expect(scopeOf()).toBe('account:Solo')
        if (route === 'slice') fireEvent.click(soloSlice()); else fireEvent.click(screen.getByRole('button', { name: backName }))
        expect(scopeOf()).toBe('all')
      }
    })

    it('behaves the same for any portfolio with one drawn account', () => {
      render(<Harness initial="portfolio:p2" />)
      fireEvent.click(slice('Gamma: $600.00 · 100.00%'))
      expect(scopeOf()).toBe('account:Gamma')
      fireEvent.click(slice('Gamma: $600.00 · 100.00%'))
      expect(scopeOf()).toBe('all')
    })

    it('goes straight back to all accounts from an undrawn account selected beside a drawn one', () => {
      const partly = [account('Alpha', 'p1', '300'), account('Zed', 'p1', null)]
      render(<Harness initial="account:Zed" list={partly} />)
      fireEvent.click(screen.getByRole('button', { name: backName }))
      expect(scopeOf()).toBe('all')
    })

    it('keeps the back arrow when the group has nothing to draw', () => {
      render(<Harness initial="unassigned" />)
      expect(screen.getByText('No positive balance')).toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: backName }))
      expect(scopeOf()).toBe('all')
    })

    it('lets a selected account folded into Other go back to all accounts through Other', () => {
      const many = ['70', '60', '50', '40', '30', '20', '10'].map((v, i) => account(`a${i}`, 'p1', v))
      render(<Harness initial="account:a6" list={many} />)
      expect(screen.getByRole('button', { name: 'Show allocation for Other (2)' })).toHaveAttribute('aria-pressed', 'true')
      fireEvent.click(screen.getByText(/^Other \(2\):/))
      expect(scopeOf()).toBe('all')
      // Without a selection Other is not an action: it holds several accounts and has no scope of its own.
      expect(screen.queryByRole('button', { name: 'Show allocation for Other (2)' })).not.toBeInTheDocument()
    })

    it('deselects from the keyboard with Enter and Space, keeping focus on the entity\'s own control', async () => {
      const user = userEvent.setup()
      render(<Harness initial="account:Beta" />)
      legendButton('Beta').focus()
      await user.keyboard('{Enter}')
      expect(scopeOf()).toBe('all')
      // The donut is rebuilt on every scope change, but focus stays on the entity's own control.
      expect(legendButton('Beta')).toHaveFocus()
      legendButton('Alpha').focus()
      await user.keyboard(' ')
      expect(scopeOf()).toBe('account:Alpha')
      expect(legendButton('Alpha')).toHaveFocus()
      await user.keyboard(' ')
      expect(scopeOf()).toBe('all')
      expect(legendButton('Alpha')).toHaveFocus()
    })

    describe('focus after a change made from inside the panel', () => {
      it('moves from a portfolio control to the back arrow, and back to that portfolio\'s control', async () => {
        const user = userEvent.setup()
        render(<Harness initial="all" />)
        const total = screen.getByRole('list', { name: 'All accounts shares' })
        within(total).getByRole('button', { name: 'Show allocation for Swing' }).focus()
        await user.keyboard('{Enter}')
        expect(scopeOf()).toBe('portfolio:p1')
        expect(screen.getByRole('button', { name: backName })).toHaveFocus()
        await user.keyboard('{Enter}')
        expect(scopeOf()).toBe('all')
        expect(within(screen.getByRole('list', { name: 'All accounts shares' })).getByRole('button', { name: 'Show allocation for Swing' })).toHaveFocus()
      })

      it('uses the portfolio card button when the portfolio was selected from its card', async () => {
        const user = userEvent.setup()
        render(<Harness initial="all" />)
        const card = within(screen.getByRole('article', { name: 'Core' })).getByRole('button', { name: 'Show allocation for Core' })
        card.focus()
        await user.keyboard('{Enter}')
        expect(scopeOf()).toBe('portfolio:p2')
        expect(screen.getByRole('button', { name: backName })).toHaveFocus()
      })

      it('lands the arrow on the control of the account it leaves in the all view', async () => {
        const user = userEvent.setup()
        render(<Harness initial="account:Beta" />)
        screen.getByRole('button', { name: backName }).focus()
        await user.keyboard('{Enter}')
        expect(scopeOf()).toBe('all')
        expect(legendButton('Beta')).toHaveFocus()
      })

      it('keeps focus on a control that exists in the all view after deselecting the only account of Unassigned', async () => {
        const user = userEvent.setup()
        render(<Harness initial="unassigned" list={solo} />)
        within(screen.getByRole('list', { name: 'Unassigned shares' })).getByRole('button', { name: 'Show allocation for Solo' }).focus()
        await user.keyboard('{Enter}')
        expect(scopeOf()).toBe('account:Solo')
        await user.keyboard('{Enter}')
        expect(scopeOf()).toBe('all')
        expect(within(screen.getByRole('article', { name: 'Unassigned' })).getByRole('button', { name: 'Show allocation for Solo' })).toHaveFocus()
      })

      it('returns focus to the portfolio card when the portfolio has no slice in the total', async () => {
        const user = userEvent.setup()
        // Empty has no positive balance, so it is not a slice of the total and only its card offers a control.
        const none = [account('Solo', 'p3', null), account('Big', 'p1', '10')]
        render(<Harness initial="portfolio:p3" list={none} />)
        screen.getByRole('button', { name: backName }).focus()
        await user.keyboard('{Enter}')
        expect(scopeOf()).toBe('all')
        expect(within(screen.getByRole('article', { name: 'Empty' })).getByRole('button', { name: 'Show allocation for Empty' })).toHaveFocus()
      })

      it('does not take focus when the scope changes from elsewhere, such as the scope select', () => {
        const panel = (scope: string) => <><button>Elsewhere</button><OverviewAllocation accounts={accounts} portfolios={portfolios} scope={scope} onScope={() => {}} /></>
        const { rerender } = render(panel('portfolio:p1'))
        // The parent ignores this activation, leaving a pending restore; focus then moves to another control.
        fireEvent.click(screen.getByRole('button', { name: backName }))
        screen.getByRole('button', { name: 'Elsewhere' }).focus()
        rerender(panel('all'))
        expect(screen.getByRole('button', { name: 'Elsewhere' })).toHaveFocus()
      })
    })
  })

  it('renders nothing without enabled accounts', () => {
    const { container } = render(<OverviewAllocation accounts={[account('Off', 'p1', '1', false)]} portfolios={portfolios} scope="all" onScope={() => {}} />)
    expect(container).toBeEmptyDOMElement()
  })
})
