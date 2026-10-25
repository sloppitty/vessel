import { useMemo, type CSSProperties } from 'react'
import type { BrokerAccount, Portfolio } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { Folder, Pencil } from 'lucide-react'
import { money } from '@/lib/format'
import { PortfolioIcon } from './PortfolioIcon'
import { portfolioAllocations, totalAllocation } from './overview-allocation'
import { CoverageBadge } from './CoverageBadge'

export function PortfoliosPage({ accounts, portfolios, onAddAccount, onCreatePortfolio, onOpen, onManage }: {
  accounts: BrokerAccount[]; portfolios: Portfolio[]; onAddAccount: () => void; onCreatePortfolio: () => void
  onOpen: (id: string) => void; onManage: (id: string, focusAccounts: boolean) => void
}) {
  // A portfolio keeps the colour of its Overview allocation slice; undrawn or folded ones keep the accent.
  const slots = useMemo(() => new Map(totalAllocation(portfolioAllocations(accounts, portfolios)).slices
    .flatMap(slice => slice.slot === null ? [] : [[slice.id, slice.slot] as const])), [accounts, portfolios])
  return <section className="workspace-page-content">
    {!portfolios.length ? <div className="shell-panel workspace-empty">
      <Folder size={30} /><strong>No portfolios yet</strong>
      {!accounts.length && <Button onClick={onAddAccount}>Add your first account</Button>}
      <Button variant="outline" onClick={onCreatePortfolio}>Create a portfolio</Button>
    </div> : <div className="portfolio-grid">
      {portfolios.map(portfolio => <article className="shell-panel portfolio-tile" key={portfolio.id}
        style={slots.has(portfolio.id) ? { '--portfolio-color': `var(--series-${slots.get(portfolio.id)})` } as CSSProperties : undefined}>
        <button className="portfolio-open" aria-label={`Open ${portfolio.name}`} onClick={() => onOpen(portfolio.id)}>
          <span className="portfolio-icon"><PortfolioIcon icon={portfolio.icon} /></span>
          <h2>{portfolio.name}</h2><p>{portfolio.accountCount} {portfolio.accountCount === 1 ? 'account record' : 'account records'}</p>
          <strong>{money(portfolio.balanceUsd === undefined ? portfolio.totalValueUsd : portfolio.balanceUsd)}</strong>
          <small>Available balance {money(portfolio.availableBalanceUsd ?? null)}</small>
        </button>
        {/* Coverage pills sit outside the open button: they are their own controls. Complete coverage shows nothing. */}
        <div className="portfolio-coverage">
          <CoverageBadge label="Balance" status={portfolio.balanceCoverage ?? portfolio.valueCoverage}
            reasons={[(portfolio.balanceCoverage ?? portfolio.valueCoverage) === 'partial' ? 'Some enabled account balances are unavailable.' : 'Enabled account balances are unavailable.']} />
          <CoverageBadge label="Availability" noun="enabled account" covered={portfolio.availableBalanceAccountCount ?? 0} total={portfolio.enabledAccountCount ?? 0}
            status={portfolio.availableBalanceCoverage ?? 'unavailable'} />
        </div>
        <button className="icon-button portfolio-manage" aria-label={`Manage portfolio ${portfolio.name}`}
          title="Rename, change icon, manage accounts or delete portfolio" onClick={() => onManage(portfolio.id, false)}><Pencil size={14} /></button>
        <Button variant="outline" size="sm" aria-label={`Add accounts to ${portfolio.name}`} onClick={() => onManage(portfolio.id, true)}>Add accounts</Button>
      </article>)}
    </div>}
  </section>
}
