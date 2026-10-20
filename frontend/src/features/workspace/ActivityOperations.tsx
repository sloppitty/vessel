import { emptyFilters, filterQuery, initialActivityState, type ActivityListState, type Filters } from './activity-filters'
import { useEffect, useId, useMemo, useRef, useState } from 'react'
import type { ActivityPage, ActivityQuery } from '@/api/activity'
import type { BrokerAccount, Portfolio, WorkspaceApi } from '@/api/workspace'
import { errorMessage } from '@/api/errors'
import { AccountPicker } from '@/components/accounts/AccountPicker'
import { Button } from '@/components/ui/button'
import { NativeSelect } from '@/components/ui/native-select'
import { Pager } from '@/components/ui/pager'
import { isPageSize } from '@/components/ui/paging'
import { useDelayedFlag } from '@/lib/use-delayed-flag'
import { OperationsSkeleton, OperationsTable } from './OperationsTable'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { RetrospectivePreview } from './RetrospectivePreview'
import { FundingPaymentsTable } from './FundingPaymentsTable'
import { allAccountsChoice, pickerAccounts } from './account-picker-options'
import './activity-operations.css'

/** Overview shows this many of the latest operations in its scope, without a pager. */
const compactRows = 5
const count = new Intl.NumberFormat('en-US')

/**
 * Position operations, filtered and paged by the server. The Activity page keeps its filters, page and size
 * in shell memory (`state`); account detail keeps its own; Overview asks for the latest few in its scope.
 */
export function ActivityOperations({ api, accounts, portfolios = [], accountId, scope, compact = false, filters: showFilters = false, reloadGeneration = 0,
  state, onStateChange, onOpenPlay, onCreated }: {
  api: WorkspaceApi; accounts: BrokerAccount[]; portfolios?: Portfolio[]; accountId?: string | undefined
  /** Overview scope: `all`, `unassigned`, `portfolio:{id}` or `account:{id}`. */
  scope?: string | undefined
  compact?: boolean; filters?: boolean; reloadGeneration?: number
  state?: ActivityListState; onStateChange?: (state: ActivityListState) => void
  onOpenPlay?: (id: string, signal: AbortSignal) => Promise<void>; onCreated?: () => void
}) {
  const [localState, setLocalState] = useState(initialActivityState)
  const list = state ?? localState
  const setList = onStateChange ?? setLocalState
  const [retry, setRetry] = useState(0)
  // `requested` is the request that produced the result; `key` is the request it answers, which differs when the
  // server clamped the page. `generation` counts accepted pages so expanded rows re-read their executions.
  const [result, setResult] = useState<{ api: WorkspaceApi; key: string; requested: string; page: ActivityPage | null; error: string | null; generation: number } | null>(null)
  // The version of the first page read for these filters; a later page with another version means Activity changed.
  const [baseline, setBaseline] = useState<{ api: WorkspaceApi; key: string; version: string; changed: boolean } | null>(null)
  const [selection, setSelection] = useState<{ accountId: string; operationId: string } | null>(null)
  const [opening, setOpening] = useState<string | null>(null)
  const [openError, setOpenError] = useState<string | null>(null)
  const openingRequest = useRef<AbortController | null>(null)
  useEffect(() => () => openingRequest.current?.abort(), [api])
  async function openPlay(id: string) {
    if (!onOpenPlay) return
    openingRequest.current?.abort()
    const controller = new AbortController()
    openingRequest.current = controller
    setOpening(id); setOpenError(null)
    try { await onOpenPlay(id, controller.signal) }
    catch (cause) { if (!controller.signal.aborted) setOpenError(errorMessage(cause, 'The Play could not be opened.')) }
    finally { if (!controller.signal.aborted) setOpening(null) }
  }
  const trigger = useRef<HTMLElement | null>(null)
  const top = useRef<HTMLDivElement | null>(null)
  const enabled = accounts.filter(a => a.isEnabled !== false)
  const fieldId = useId()
  const previous = result?.api === api ? result.page : null
  // Options that disappear fall back to All; instruments come from the scoped set the server reports.
  const selected = list.filters
  const knownContracts = previous?.facets.contracts ?? null
  const filters: Filters = showFilters ? {
    ...selected,
    account: enabled.some(a => a.id === selected.account) ? selected.account : '',
    portfolio: selected.portfolio === 'unassigned' || portfolios.some(p => p.id === selected.portfolio) ? selected.portfolio : '',
    contract: knownContracts === null || knownContracts.includes(selected.contract) ? selected.contract : '',
  } : emptyFilters
  const pageSize = compact ? compactRows : list.pageSize
  const sent = filterQuery(filters)
  const requested: ActivityQuery = {
    ...(scope !== undefined && scope !== 'all' ? { scope } : {}), ...(accountId !== undefined ? { accountId } : {}),
    ...sent, page: compact ? 1 : list.page, pageSize,
  }
  const queryKey = JSON.stringify(requested)
  const query = useMemo(() => JSON.parse(queryKey) as ActivityQuery, [queryKey])
  const key = JSON.stringify([queryKey, reloadGeneration, retry])
  const listKey = JSON.stringify([{ ...query, page: undefined }, reloadGeneration, retry])
  const adopted = useRef<string | null>(null)
  useEffect(() => {
    const skip = adopted.current === key
    adopted.current = null
    if (skip) return
    const controller = new AbortController()
    api.activity(query, controller.signal).then(page => {
      if (controller.signal.aborted) return
      // The request a clamped page answers, so adopting it into the list state does not read it again.
      const answered = page.page === query.page ? key : JSON.stringify([JSON.stringify({ ...query, page: page.page }), reloadGeneration, retry])
      setResult(current => ({ api, key: answered, requested: key, page, error: null, generation: (current?.generation ?? 0) + 1 }))
      setBaseline(current => current?.api !== api || current.key !== listKey || page.page === 1 ? { api, key: listKey, version: page.version, changed: false }
        : current.version === page.version ? current : { ...current, changed: true })
    }).catch(cause => {
      if (!controller.signal.aborted) setResult(current => ({ api, key, requested: key, page: null, error: errorMessage(cause, 'Operations could not be loaded.'), generation: current?.generation ?? 0 }))
    })
    return () => controller.abort()
  }, [api, query, key, listKey, reloadGeneration, retry])
  // A page past the end comes back clamped: keep the page actually shown, so later growth does not jump back.
  // Only the response to the current request may write, and the adopted page is not requested again.
  useEffect(() => {
    if (compact || result === null || result.api !== api || result.requested !== key || result.key === key || result.page === null) return
    adopted.current = result.key
    setList({ ...list, page: result.page.page })
  }, [api, compact, key, list, result, setList])
  const pending = result === null || result.api !== api || result.key !== key && result.requested !== key
  const skeleton = useDelayedFlag(pending && previous === null)
  const changed = baseline?.api === api && baseline.key === listKey && baseline.changed
  const update = (field: keyof Filters, value: string) => setList({ ...list, filters: { ...filters, [field]: value }, page: 1 })
  const reloadActivity = () => { setList({ ...list, page: 1 }); setRetry(n => n + 1) }
  const goTo = (page: number) => {
    setList({ ...list, page })
    top.current?.scrollIntoView?.({ block: 'start' })
  }

  // A rejected filter combination keeps the filters on screen so it can be changed.
  const error = pending ? null : result.error
  const shown = pending ? previous : result.page
  const contracts = shown?.facets.contracts ?? (filters.contract ? [filters.contract] : [])
  // The pager already counts the rows; a note is only needed when filters narrow the scope (or Overview has no pager).
  // Judge the filters actually sent: retained custom dates behind "All retained activity" narrow nothing.
  const filtered = Object.keys(sent).length > 0
  const note = compact || filtered
  return <div className="activity-operations">
    {showFilters && <fieldset className="operation-filters"><legend>Filter operations</legend>
      <div className="operation-filter-field"><span id={`${fieldId}-account`}>Account</span>
        <AccountPicker labelledBy={`${fieldId}-account`} value={filters.account} onChange={value => update('account', value)}
          accounts={pickerAccounts(enabled, portfolios)} choices={[allAccountsChoice('All enabled accounts')]} /></div>
      <label>Portfolio<NativeSelect value={filters.portfolio} onChange={e => update('portfolio', e.target.value)}><option value="">All portfolios</option><option value="unassigned">Unassigned accounts</option>{portfolios.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</NativeSelect></label>
      <label>Instrument<NativeSelect value={filters.contract} onChange={e => update('contract', e.target.value)}><option value="">All instruments</option>{contracts.map(c => <option key={c}>{c}</option>)}</NativeSelect></label>
      <label>Direction<NativeSelect value={filters.direction} onChange={e => update('direction', e.target.value)}><option value="">All directions</option><option value="long">Long</option><option value="short">Short</option></NativeSelect></label>
      <label>Status<NativeSelect value={filters.status} onChange={e => update('status', e.target.value)}><option value="">All statuses</option><option value="open">Open</option><option value="partial">Partially closed</option><option value="closed">Closed</option><option value="incomplete">Incomplete</option></NativeSelect></label>
      <label title={`Dates filter the latest execution, in UTC.${shown ? ` Rolling periods count back from ${shown.asOfUtc}.` : ''}`}>Period<NativeSelect value={filters.period} onChange={e => update('period', e.target.value)}><option value="all">All retained activity</option><option value="7">Last 7 days</option><option value="30">Last 30 days</option><option value="90">Last 90 days</option><option value="custom">Date range · UTC</option></NativeSelect></label>
      {filters.period === 'custom' && <><label>From date<input type="date" value={filters.from} onChange={e => update('from', e.target.value)} /></label><label>Through date<input type="date" value={filters.to} min={filters.from || undefined} onChange={e => update('to', e.target.value)} /></label></>}
      <Button variant="outline" size="sm" onClick={() => setList({ ...list, filters: emptyFilters, page: 1 })}>Reset filters</Button>
    </fieldset>}
    {/* A page change scrolls here, so the changed notice that may follow it is in view with the rows. */}
    <div ref={top} className="activity-list">
    {changed && <div className="workspace-alert activity-changed" role="status"><span>Activity changed</span>
      <Button variant="ghost" size="sm" aria-label="Reload activity" onClick={reloadActivity}>Reload</Button></div>}
    {error ? <div className="workspace-alert" role="alert">{error}<Button variant="ghost" size="sm" onClick={() => setRetry(n => n + 1)}>Retry operations</Button></div> :
      shown === null ? skeleton ? <OperationsSkeleton compact={compact} rows={compact ? compactRows : 6} note={note} /> : <div className="operations-pending" aria-busy="true" /> : <>
      {note && <p className="workspace-scope-note in-panel" role="status">{compact
        ? `Showing ${count.format(shown.operations.length)} of ${count.format(shown.totalCount)} operations.`
        : `${count.format(shown.totalCount)} of ${count.format(shown.scopeCount)} operations match the filters.`}</p>}
      <div className={pending ? 'operations-results list-refetching' : 'operations-results'} aria-busy={pending || undefined}>
        {openError && <p className="workspace-alert" role="alert">{openError}</p>}
        <OperationsTable api={api} operations={shown.operations} accounts={accounts} compact={compact} refreshGeneration={result?.generation ?? 0} onReload={reloadActivity}
          {...(onOpenPlay ? { onOpenPlay: (id: string) => { void openPlay(id) } } : {})} opening={opening} onPreview={operation => {
          trigger.current = window.document.activeElement instanceof HTMLElement ? window.document.activeElement : null
          setSelection({ accountId: operation.accountId, operationId: operation.id })
        }} />
      </div>
      {!compact && <Pager label="Activity pages" page={shown.page} pageSize={shown.pageSize} totalCount={shown.totalCount} disabled={pending}
        onPage={goTo} onPageSize={size => { if (isPageSize(size)) setList({ ...list, page: 1, pageSize: size }) }} />}
      {!compact && shown.unassignedFundingPayments.length > 0 && <details className="operation-scope">
        <summary>Unassigned funding</summary>
        <p className="workspace-scope-note">Before operation filters. Not attributed to operation results.</p>
        <FundingPaymentsTable payments={shown.unassignedFundingPayments} accounts={accounts} />
        {shown.omittedUnassignedFundingPaymentCount > 0 && <p className="operation-incomplete">Showing the latest 100 unassigned settlements; older retained settlements are omitted.</p>}
      </details>}
      {!compact && <details className="operation-scope"><summary>Scope and coverage</summary><p className="workspace-scope-note">{shown.scopeNote}</p>{shown.coverage.filter(c => enabled.some(a => a.id === c.accountId)).map((c, index) => <p className="workspace-scope-note" key={index}><strong>{accounts.find(a => a.id === c.accountId)?.name}{c.contractId ? ` · ${c.contractId}` : ''}</strong>: {c.notice}</p>)}</details>}
    </>}
    </div>
    <Dialog open={selection !== null && enabled.some(a => a.id === selection.accountId)} onOpenChange={open => { if (!open) setSelection(null) }}>
      <DialogContent className="retrospective-dialog" onCloseAutoFocus={event => { event.preventDefault(); trigger.current?.focus() }}>
        <DialogHeader><DialogTitle>Retrospective operation preview</DialogTitle><DialogDescription className="sr-only">Inspect original facts and associations before confirming a completed Play.</DialogDescription></DialogHeader>
        {selection && enabled.some(a => a.id === selection.accountId) && <RetrospectivePreview api={api} {...selection} {...(onOpenPlay ? { onOpenPlay } : {})} {...(onCreated ? { onCreated } : {})} />}
      </DialogContent>
    </Dialog>
  </div>
}
