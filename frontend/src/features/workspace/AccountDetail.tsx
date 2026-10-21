import { useEffect, useRef, useState } from 'react'
import type { AccountMonitor } from '@/api/monitor'
import { MonitorBadge, MonitorDiscrepancies, MonitorPanel } from '@/features/monitor/MonitorPanel'
import { useMonitorRun } from '@/features/monitor/useMonitorRun'
import type { AccountSnapshot, BrokerAccount, WorkspaceApi } from '@/api/workspace'
import { errorMessage } from '@/api/errors'
import { Button } from '@/components/ui/button'
import { ActivityOperations } from './ActivityOperations'
import { accountStatus, accountStatusTone, amount, money, time } from '@/lib/format'
import { useVenues } from '@/api/venues'
import { ArrowLeft, RefreshCw } from 'lucide-react'
import { VenueLogo } from '@/components/venues/VenueBadge'
import { InfoTip } from '@/components/ui/info-tip'

/** A short source tag on a figure; the tag opens the full description. */
function SourcePill({ text, detail }: { text: string; detail: string }) {
  return <InfoTip className="value-tag" trigger={text}>{detail}</InfoTip>
}

export function AccountDetail({ account, api, refreshing, busy = refreshing, onSync, onBack, onManage, reloadGeneration = 0, onOpenPlay, onCreated, monitor = null }: {
  account: BrokerAccount; api: WorkspaceApi; refreshing: boolean; busy?: boolean; onSync: (id: string) => void; onBack: () => void; onManage: () => void; reloadGeneration?: number
  monitor?: AccountMonitor | null
  onOpenPlay?: (id: string, signal: AbortSignal) => Promise<void>; onCreated?: () => void
}) {
  const paper = account.tradingMode === 'paper'
  const monitorRun = useMonitorRun(api, !paper && account.isEnabled !== false ? monitor : null, onCreated)
  const dismissal = useRef<AbortController | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    dismissal.current = controller
    return () => controller.abort()
  }, [api, account.id])
  async function dismissDiscrepancy(id: string, note?: string) {
    const controller = dismissal.current
    if (!controller || controller.signal.aborted) return
    try { await api.dismissAccountDiscrepancy(account.id, id, note, controller.signal) }
    finally { if (!controller.signal.aborted) onCreated?.() }
  }
  const venues = useVenues()
  const synced = !paper && venues.can(account.venueId, 'sync')
  const wallet = !paper && venues.can(account.venueId, 'stablecoinWallet')
  const [snapshot, setSnapshot] = useState<AccountSnapshot | null>(null)
  const [completed, setCompleted] = useState<{ api: WorkspaceApi; key: string } | null>(null)
  const [detailError, setDetailError] = useState<string | null>(null)
  // A refresh ends with a shell reload, so an in-flight refresh does not start a racing read.
  const requestKey = JSON.stringify([account.id, account.lastSyncedAtUtc, account.isEnabled, reloadGeneration])
  const pending = !paper && (completed === null || completed.api !== api || completed.key !== requestKey)
  const error = pending ? null : detailError
  useEffect(() => {
    if (paper) return
    const controller = new AbortController()
    let active = true
    // Loading/error visibility derives from the request generation, avoiding a
    // synchronous effect render while keeping prior observations during retries.
    api.snapshot(account.id, controller.signal).then(nextSnapshot => {
      if (active) { setSnapshot(nextSnapshot); setDetailError(null); setCompleted({ api, key: requestKey }) }
    }).catch(cause => {
      if (active) { setDetailError(errorMessage(cause, 'Unable to read the account.')); setCompleted({ api, key: requestKey }) }
    })
    return () => { active = false; controller.abort() }
  }, [api, account.id, requestKey, paper])
  return <div className="workspace-page-content">
    <div className="detail-actions"><Button variant="ghost" size="sm" onClick={onBack}><ArrowLeft size={15} />All accounts</Button><div className="detail-management"><Button variant="outline" onClick={onManage}
      title={venues.can(account.venueId, 'readOnlyCredential') ? 'Rename, move or disable; inspect or replace the read-only token if order access is unavailable or expired' : undefined}>Manage account</Button>{synced && <Button variant="outline" onClick={() => onSync(account.id)} disabled={busy || account.isEnabled === false}
      title="Rereads current state and bounded recent executions, not complete lifetime history"><RefreshCw size={15} className={refreshing ? 'is-spinning' : ''} />{refreshing ? 'Refreshing…' : 'Refresh account'}</Button>}</div></div>
    {/* The page title already names the account, so this card carries only the venue and freshness. */}
    <section className="shell-panel account-detail-heading" aria-label="Account summary"><VenueLogo venueId={account.venueId} size="lg" /><div><p>{venues.name(account.venueId)}{paper ? ' · Paper · simulated' : synced ? ` · ${account.lastSyncedAtUtc ? `refreshed ${time(account.lastSyncedAtUtc)}` : 'not refreshed yet'}` : venues.isManual(account.venueId) ? '' : ' · reader not enabled'}</p></div><span className={`workspace-badge ${accountStatusTone(account)}`}>{accountStatus(account)}</span>{account.isEnabled !== false && <MonitorBadge monitor={monitorRun.monitor} />}</section>
    {(account.sourceId ?? account.address) && <p className="account-source"><span>Source</span> {account.sourceId ?? account.address}</p>}
    {error && <div className="workspace-alert" role="alert">{error}</div>}
    {account.isEnabled === false && <div className="workspace-alert" role="status">Disabled: imports and positions are hidden and excluded from totals.</div>}
    {/* Account identity and source come first; monitor state precedes the balances. */}
    {monitorRun.monitor && <div>
      <MonitorPanel monitor={monitorRun.monitor} busy={monitorRun.busy} error={monitorRun.error} retryAtUtc={monitorRun.retryAtUtc} statusUnavailable={monitorRun.statusUnavailable}
        currentBookRead={venues.readsCurrentBook(account.venueId)} onCheck={() => { void monitorRun.check() }} onManageCredential={onManage} />
      <MonitorDiscrepancies discrepancies={monitorRun.monitor.discrepancies} truncated={monitorRun.monitor.discrepanciesTruncated === true}
        busy={monitorRun.busy} onDismiss={dismissDiscrepancy} />
    </div>}
    <div className="workspace-stat-grid three">
      <div className="shell-panel metric"><span>{paper ? 'Paper cash balance · virtual USD' : wallet ? 'Wallet stablecoins available' : synced ? 'Account value · reported' : 'Known manual value'}</span><strong>{money(paper ? account.balanceUsd ?? null : wallet ? account.availableStablecoinNominalUsd ?? null : account.accountValueUsd)}</strong>
        <div className="metric-tags">{account.isEnabled === false ? <SourcePill text="hidden" detail="Hidden while disabled" />
          : paper ? <SourcePill text="Paper" detail="Simulated cash, not a real stablecoin wallet" /> : !wallet ? (synced ? <SourcePill text={venues.valueUnit(account.venueId)} detail={`Perpetuals equity in ${venues.valueUnit(account.venueId)} · nominal`} /> : <SourcePill text="manual" detail="Entered manually" />)
          : snapshot?.stablecoinWallet ? <SourcePill text="wallet" detail={`${snapshot.stablecoinWallet.accountMode} · nominal total minus held`} /> : <SourcePill text="not read" detail="Refresh to read the stablecoin wallet" />}</div>
        {/* Perps equity is a separate figure, never summed with the wallet. */}
        {wallet && snapshot && account.isEnabled !== false && <small>Perps equity {money(snapshot.accountValueUsd)}</small>}</div>
      <div className="shell-panel metric"><span>Available balance</span><strong>{money(account.isEnabled === false ? null : account.availableBalanceUsd ?? null)}</strong>
        <div className="metric-tags">{account.isEnabled === false ? <SourcePill text="hidden" detail="Hidden while disabled" />
          : paper ? <SourcePill text="paper" detail="Virtual available margin, not real funds" /> : account.availableBalanceSource === 'wallet' ? <SourcePill text="wallet" detail="Wallet total minus held · nominal" />
          : account.availableBalanceSource === 'margin' ? <SourcePill text="reported margin" detail="Venue-reported available margin · not guaranteed withdrawable" />
          : account.availableBalanceSource === 'withdrawable' ? <SourcePill text="reported withdrawal" detail="Venue-reported perps withdrawable" />
          : <SourcePill text="not reported" detail="Not reported by this venue" />}</div>
        {!paper && <small>Perps withdrawable {money(account.isEnabled === false ? null : snapshot?.withdrawableUsd ?? null)}</small>}</div>
      <div className="shell-panel metric"><span>{paper ? 'Virtual equity' : 'Margin used · reported'}</span><strong>{money(paper ? account.accountValueUsd : snapshot?.marginUsedUsd ?? null)}</strong>{paper && <small>Starting capital {account.startingCapitalUsd ?? 'Unknown'} virtual USD · fixed at creation</small>}</div>
    </div>
    {wallet && <section className="shell-panel"><header className="shell-panel-heading"><div className="heading-with-tip"><h2>Stablecoin wallet</h2><InfoTip label="About the stablecoin wallet">Supported stablecoins in the venue wallet. Other assets are excluded.</InfoTip></div><span className="workspace-badge">{snapshot?.stablecoinWallet?.accountMode ?? 'Not observed'}</span></header>
      {account.isEnabled === false ? <div className="workspace-empty small-empty"><strong>Wallet balances hidden while disabled</strong></div> :
        !snapshot?.stablecoinWallet ? <div className="workspace-empty small-empty"><strong>Stablecoin wallet not retrieved yet</strong></div> :
        <><div className="workspace-table-wrap"><table className="workspace-table"><thead><tr><th>Stablecoin</th><th>Total token units</th><th>Held token units</th><th>Available token units</th></tr></thead><tbody>{snapshot.stablecoinWallet.balances.map(b => <tr key={b.tokenId}><td><strong>{b.symbol}</strong></td><td>{amount(b.total)}</td><td>{amount(b.held)}</td><td><strong>{amount(b.available)}</strong></td></tr>)}</tbody></table></div><p className="workspace-scope-note in-panel">{snapshot.stablecoinWallet.notice}</p></>}
    </section>}
    {!paper && <section className="shell-panel"><header className="shell-panel-heading"><h2>Perpetual positions</h2></header>
      {account.isEnabled === false ? <div className="workspace-empty small-empty"><strong>Positions hidden while disabled</strong></div> : !snapshot?.positions.length ? <div className="workspace-empty small-empty"><strong>{pending ? 'Reading the snapshot…' : 'No positions in the latest snapshot'}</strong>{!pending && !snapshot && <p>No snapshot yet; that is not proof of zero exposure.</p>}</div> :
        <div className="workspace-table-wrap"><table className="workspace-table"><thead><tr><th>Contract</th><th>Signed quantity</th><th>Entry price</th><th>Unrealized P&amp;L</th><th>Leverage</th></tr></thead><tbody>{snapshot.positions.map(p => <tr key={p.contractId}><td><strong>{p.contractId}</strong></td><td>{amount(p.signedQuantity)}</td><td>{money(p.entryPrice)}</td><td>{money(p.unrealizedPnlUsd)}</td><td>{p.leverage === null ? '—' : `${p.leverage}×`}</td></tr>)}</tbody></table></div>}
    </section>}
    <section className="shell-panel"><header className="shell-panel-heading"><h2>{paper ? 'Simulated operations' : 'Recent executions'}</h2></header>{account.isEnabled === false ? <div className="workspace-empty small-empty"><strong>Imported movements hidden while disabled</strong></div> : <ActivityOperations key={requestKey} api={api} accountId={account.id} accounts={[account]} reloadGeneration={reloadGeneration} {...(onOpenPlay ? { onOpenPlay } : {})} {...(onCreated ? { onCreated } : {})} />}</section>
  </div>
}
