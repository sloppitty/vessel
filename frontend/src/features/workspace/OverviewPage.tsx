import type { ReactNode } from 'react'
import type { BrokerAccount, Overview, WorkspaceApi } from '@/api/workspace'
import type { OverviewQuery } from '@/api/overview-summary'
import type { PlayView } from '@/api/plays'
import type { AccountMonitor } from '@/api/monitor'
import { Button } from '@/components/ui/button'
import { ArrowRight, Wallet } from 'lucide-react'
import { AccountRows } from './AccountRows'
import { ActivityOperations } from './ActivityOperations'
import { OverviewScope } from './OverviewScope'
import { overviewAccounts } from './overview-scope'
import { OverviewSummary } from './OverviewSummary'
import { OverviewPlays } from './OverviewPlays'
import { OverviewAllocation } from './OverviewAllocation'
import { OverviewIncidents } from './OverviewIncidents'
import { incidentsBeyondCap, type Incident } from '@/features/monitor/incidents'

export function OverviewPage({ data, api, monitors = [], incidents = [], overviewQuery, setOverviewQuery, reloadKey, refreshAllAction, refreshing,
  onAddAccount, onCreatePortfolio, onAccounts, onActivity, viewAccount, sync, manageAccount, browsePlays, viewPlay, reload }: {
  data: Overview; api: WorkspaceApi; overviewQuery: OverviewQuery
  monitors?: AccountMonitor[]
  /** Open monitor incidents across every monitored account, from the shell's monitor summary. */
  incidents?: readonly Incident[]
  setOverviewQuery: (query: OverviewQuery | ((previous: OverviewQuery) => OverviewQuery)) => void
  reloadKey: number; refreshAllAction: ReactNode; refreshing: string | null
  onAddAccount: () => void; onCreatePortfolio: () => void; onAccounts: () => void; onActivity: () => void
  viewAccount: (id: string) => void; sync: (id: string) => Promise<void>; manageAccount: (account: BrokerAccount) => void
  browsePlays: (view: PlayView) => void; viewPlay: (id: string, signal: AbortSignal) => Promise<void>; reload: () => void
}) {
  const { accounts, portfolios } = data
  const enabledAccounts = accounts.filter(account => account.isEnabled !== false)
  const scopedAccounts = overviewAccounts(overviewQuery.scope, accounts)
  const portfolioNames = Object.fromEntries(portfolios.map(p => [p.id, p.name]))
  return <>
    <OverviewScope scope={overviewQuery.scope} onScope={scope => setOverviewQuery(query => ({ ...query, scope }))}
      accounts={accounts} portfolios={portfolios} />
    {/* Above the metric tiles and only while an incident is open; Allocation stays between the tiles and Summary. */}
    <OverviewIncidents incidents={incidents} more={incidentsBeyondCap(monitors)} accounts={accounts} onOpenAccount={viewAccount}
      onOpenPlay={id => { void viewPlay(id, new AbortController().signal) }} />
    <OverviewSummary api={api} query={overviewQuery} onQuery={setOverviewQuery} reloadGeneration={reloadKey}
      fallback={data} accountCount={scopedAccounts.length} workspaceNote={data.scopeNote}
      beforeSummary={<OverviewAllocation accounts={accounts} portfolios={portfolios} scope={overviewQuery.scope}
        onScope={scope => setOverviewQuery(query => ({ ...query, scope }))} />} />
    {!accounts.length ? <section className="shell-panel workspace-start">
      <span className="start-icon"><Wallet size={26} /></span>
      <h2>Start with your accounts.</h2>
      <Button onClick={onAddAccount}>Add your first account<ArrowRight size={15} /></Button>
      <Button variant="ghost" onClick={onCreatePortfolio}>Create an optional portfolio</Button>
    </section> : <section className="shell-panel">
      <header className="shell-panel-heading">
        <h2>Your enabled accounts</h2>
        <div className="panel-heading-actions">{refreshAllAction}<button className="text-action" onClick={onAccounts}>View all<ArrowRight size={14} /></button></div>
      </header>
      {enabledAccounts.length ? <AccountRows monitors={monitors} accounts={scopedAccounts.slice(0, 4)} portfolioNames={portfolioNames}
        refreshing={refreshing} onDetail={viewAccount} onSync={id => { void sync(id) }} onManage={manageAccount} /> :
        <div className="workspace-empty small-empty">
          <strong>All accounts are disabled</strong>
          <Button variant="outline" onClick={onAccounts}>Manage accounts</Button>
        </div>}
    </section>}
    <div className="overview-bottom-grid">
      <section className="shell-panel">
        <header className="shell-panel-heading">
          <h2>Recent activity</h2>
          <button className="text-action" onClick={onActivity}>View activity<ArrowRight size={14} /></button>
        </header>
        <ActivityOperations api={api} accounts={accounts} scope={overviewQuery.scope}
          compact reloadGeneration={reloadKey} onOpenPlay={viewPlay} onCreated={reload} />
      </section>
      <OverviewPlays api={api} accounts={accounts} scope={overviewQuery.scope} reloadGeneration={reloadKey}
        onBrowse={browsePlays} onOpen={viewPlay} />
    </div>
  </>
}
