import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { ArrowDownRight, ArrowRight, ArrowUpRight, BarChart3, ChevronRight, Info, RefreshCw, ShieldCheck, SlidersHorizontal } from 'lucide-react'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import type { BrokerAccount, WorkspaceApi } from '@/api/workspace'
import { AccountPicker } from '@/components/accounts/AccountPicker'
import { allAccountsChoice } from '@/features/workspace/account-picker-options'
import { errorMessage } from '@/api/errors'
import { defaultReviewQuery, reviewPeriods, type ReviewDocument, type ReviewGroup, type ReviewGroupBy, type ReviewQuery } from '@/api/review'
import { useReviewPreferences } from './reviewPreferences'
import { useVenues } from '@/api/venues'
import { PlayIdentity } from '@/features/plays/PlayIdentity'
import { LocalResultValue, SignDiffersFlag, fundingNote, localResultNote } from '@/features/plays/LocalResult'
import { PostHocBadge } from '@/features/plays/PostHocBadge'
import { ReviewChart } from './ReviewChart'
import { ReviewExclusions } from './ReviewExclusions'
import { reviewDate, reviewMoney, reviewMultiple, reviewPercent, reviewResult } from '@/lib/format'
import { negative, tone } from './format'
import './review.css'
import { NativeSelect } from '@/components/ui/native-select'
import { InfoTip } from '@/components/ui/info-tip'

const definitions = [
  ['Closed-play P&L', 'One saved, fully closed Play counts once, including eligible manual records and confirmed retrospective operations. The full result is attributed to its actual last exit, not the day it was refreshed or reviewed. Partial exits on open Plays are excluded. Reported execution fees are deducted once according to the venue P&L basis. Separate funding cashflows are not added. Whether RISEx reported realized P&L includes funding remains unverified.'],
  ['Batting average', 'Wins divided by wins plus losses. Positive net P&L wins, negative loses, exact zero is a scratch. Scratches do not break streaks.'],
  ['Average gain / loss', 'Mean winning return divided by the absolute mean losing return. Play return is net P&L divided by executed entry notional, not margin or account equity. Each Play has equal weight. The sizing assistant still uses its own last-20-decided-Play window.'],
  ['Profit factor and expectancy', 'Profit factor is total winning net P&L divided by absolute losing net P&L. Expectancy is the average net result of all scored Plays, including scratches, not a forecast. Missing denominators are N/A.'],
  ['Drawdown', 'The metric is the largest drop from a preceding peak in cumulative closed-play P&L, starting at zero for this period. The chart uses closing values per day, or larger labeled buckets for long histories; it can hide losses between points. Neither is account-equity drawdown.'],
  ['Result bases', `Net P&L is the venue result: reported closed P&L less fees, or the manual ledger. Venues that realize P&L at the position's average cost can give a Play another position's entry price, so each Play also shows its Play-local result: its own fills' sell proceeds minus buy costs, less every trading fee, without funding. It is shown, never scored. Sign differs by basis marks Plays where the two disagree on win, loss or scratch. Fee-only losses were zero or better before fees; they still count as losses. ${fundingNote}, so a sign difference or a fee-only loss can reflect funding.`],
  ['Risk and reviews', 'Estimated R (planned-stop model) is net P&L divided by an initial planned-risk estimate: executed entry notional × share-weighted stop distance. It uses the plan revision at or before the first entry fill, and every planned entry must have complete, valid stops. It is not actual loss risk or a retrospective estimate; incomplete risk stays unknown. Written reviews do not establish plan adherence or decision quality.'],
  ['Scope', 'All portfolios pools individual outcomes and includes unassigned enabled accounts once. Current portfolio membership applies to historical results. Disabled accounts are excluded. Asset keys retain their contract multipliers; no symbol aliases are guessed. Amounts are nominal USD: venues settling in different listed stablecoins (USDC, USDG and others) are pooled at 1:1, with no FX or depeg valuation.'],
  ['Imports and coverage', 'Linked imported fills contribute to saved Plays. Unassigned fills are not scored Plays. Activity can preview a completed operation for owner-confirmed retrospective creation when its full facts and associations permit it. Missing openings, unmatched quantities, flips, unsupported fees and incomplete links are excluded, not treated as zero. Retained history may be bounded and is not lifetime performance.'],
  ['Imported positions', 'Include imported positions adds closed Activity round trips whose fills belong to no Play, scored by the same net P&L and return rules. They have no plan, initial-risk R or review and never become Plays. Automatic adds them while the selection has fewer scored Plays than the threshold in Settings; the toggle overrides it. A Play weighs twice a position in batting average, average gain and loss, payoff and expectancy. Net P&L, profit factor, drawdown, streaks and the chart are real amounts and are never weighted. Positions that cannot be scored (missing executions, unknown fees, ambiguous identity) are counted, not treated as zero.'],
  ['Record sources', 'A retrospective Play has original execution times and a separate creation/review time, not an invented pre-trade plan or initial-risk R. Manual execution facts belong to their saved Play and eligible Review results only. They do not change venue balances or imported Activity. Account balances, retained operations and saved-Play performance are separate scopes.'],
  ['Time', '1D, 7D, Month and Year mean rolling 24-hour, 7-day, 30-day and 365-day windows ending at the displayed report time, in UTC. All means all retained eligible results. Open-play counts describe current state. Drill-down pages read a fresh snapshot; results may change after sync or link edits.'],
]

/** Below this many cases a metric is marked "Small sample", measured by its own denominator (F-08). */
const smallSample = 20

/** A breakdown cell's mark when its own denominator is below `smallSample`: batting and profit factor count decided
 * Plays, average gain / loss the smaller of wins and losses. */
function SampleMark({ n }: { n: number }) {
  return n < smallSample ? <small className="pr-sample">Small sample</small> : null
}

/** A metric's own n, marked as a small sample below `smallSample`. */
function SampleSize({ n, unit, label }: { n: number; unit?: string; label?: string }) {
  return <small className="pr-sample">{label ?? `n = ${n} ${unit}`}{n < smallSample ? ' · Small sample' : ''}</small>
}

/** "3 Plays", or "3 Plays · 2 positions" when imported positions are among `count`. */
function resultCount(count: number, positions = 0) {
  const plays = count - positions
  return `${plays} ${plays === 1 ? 'Play' : 'Plays'}${positions ? ` · ${positions} ${positions === 1 ? 'position' : 'positions'}` : ''}`
}

/** Estimated R (planned-stop model): Review's rule, as `ReviewService.HasCompleteInitialRisk` applies it. */
const estimatedRNote = 'Estimated R (planned-stop model): net P&L ÷ (executed entry notional × share-weighted stop distance), from the plan revision at or before the first entry fill. Only Plays with a complete initial stop have one. An estimate, not actual risk.'

function useReport(api: WorkspaceApi, query: ReviewQuery, generation: number) {
  const [loaded, setLoaded] = useState<{ api: WorkspaceApi; query: ReviewQuery; generation: number; value: ReviewDocument | null; error: string | null } | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    let active = true
    api.review(query, controller.signal).then(value => {
      if (active) setLoaded({ api, query, generation, value, error: null })
    }).catch(error => {
      if (active) setLoaded(previous => ({ api, query, generation, value: previous?.api === api ? previous.value : null,
        error: errorMessage(error, 'The review could not be loaded.') }))
    })
    return () => { active = false; controller.abort() }
  }, [api, query, generation])
  const loading = loaded === null || loaded.api !== api || loaded.query !== query || loaded.generation !== generation
  return {
    loading, error: loading ? null : loaded.error, report: loading || loaded.error ? null : loaded.value,
    // Previous options keep the controls usable while a new request runs; old results are never shown as current.
    options: loaded?.api === api ? loaded.value?.options : undefined,
  }
}

function ReviewPlays({ api, initialQuery, onOpenPlay }: {
  api: WorkspaceApi; initialQuery: ReviewQuery; onOpenPlay: (id: string, signal: AbortSignal) => Promise<void>
}) {
  const [query, setQuery] = useState(initialQuery)
  const [generation, setGeneration] = useState(0)
  const { report, loading, error } = useReport(api, query, generation)
  const [opening, setOpening] = useState<string | null>(null)
  const [openError, setOpenError] = useState<string | null>(null)
  const openingRequest = useRef<AbortController | null>(null)
  useEffect(() => () => openingRequest.current?.abort(), [api])
  async function open(id: string) {
    openingRequest.current?.abort()
    const controller = new AbortController()
    openingRequest.current = controller
    setOpening(id); setOpenError(null)
    try { await onOpenPlay(id, controller.signal) }
    catch (cause) { if (!controller.signal.aborted) setOpenError(errorMessage(cause, 'The Play could not be opened.')) }
    finally { if (!controller.signal.aborted) setOpening(null) }
  }
  return <>
    {loading && <p role="status">Loading Plays…</p>}
    {error && <div role="alert"><p>{error}</p><button className="pr-outline-button" onClick={() => setGeneration(n => n + 1)}>Retry</button></div>}
    {openError && <p role="alert">{openError}</p>}
    {report && <>
      <div className="pr-detail-summary"><span>Net P&L<strong className={tone(report.metrics.net)}>{reviewMoney(report.metrics.net, true)}</strong></span><span>Planned risk<strong>{report.metrics.riskCount} / {report.metrics.count - (report.metrics.positions ?? 0)}</strong></span><span>Written reviews<strong>{report.metrics.reviewed} / {report.metrics.count - (report.metrics.positions ?? 0)}</strong></span>{report.metrics.positions ? <span>Imported positions<strong>{report.metrics.positions}</strong></span> : null}</div>
      <div className="pr-table-scroll pr-play-list"><table><caption className="sr-only">Contributing closed Plays</caption><thead><tr><th>Play / account</th><th>Closed UTC</th><th><span className="pr-th-tip">Net P&L<InfoTip label="About net P&L">{report.tradingMode === 'paper' ? 'Simulated paper result only. Zero fees and slippage; unresolved ambiguity or gaps are not scored.' : 'The scored result: venue closed P&L less fees, or the manual ledger.'}</InfoTip></span></th><th><span className="pr-th-tip">Play-local<InfoTip label="About the Play-local result">{report.tradingMode === 'paper' ? 'Simulated execution result, isolated from real fills and funds.' : localResultNote}</InfoTip></span></th><th><span className="pr-th-tip">Return<InfoTip label="About return">Return is net P&amp;L / executed entry notional, not margin or account equity. Retrospective initial-risk R is unavailable.</InfoTip></span></th><th><span className="pr-th-tip">Est. R<InfoTip label="About estimated R">{estimatedRNote}</InfoTip></span></th><th>Review</th></tr></thead><tbody>
        {report.plays.map(play => { const id = play.id; return <tr key={id ?? `position-${play.operationId}`}><th scope="row">{id === null ? <span className="pr-play-link pr-position-title">{play.title || play.instrument}</span> : <button className="pr-play-link" disabled={opening !== null} onClick={() => { void open(id) }} aria-label={`Open Play ${play.title || play.instrument}`}>{play.title || play.instrument}<ArrowUpRight size={12} /></button>}<PlayIdentity instrument={play.instrument} venueId={play.venueId} accountName={play.accountName} />{id !== null && opening === id && <small>Opening…</small>}<small>{play.executionSource === 'paper' ? 'Paper · simulated executions' : play.origin === 'position' ? 'Imported position · no plan' : play.origin === 'retrospective' ? 'Retrospective · imported executions' : play.executionSource === 'manual' ? 'Planned · manual executions' : play.executionSource === 'imported' ? 'Planned · imported executions' : 'Planned · execution source not reported'}</small>{play.plannedAfterExecution && <PostHocBadge />}</th><td>{reviewDate(play.closedAtUtc, true)}</td><td className={tone(play.net)}>{reviewResult(play.net)}{play.local ? <small>{play.local.basis === 'paper-simulation' ? 'Paper result' : play.local.basis === 'manual-ledger' ? 'Manual ledger' : 'Venue result'}</small> : play.origin === 'position' && <small>Venue result</small>}</td><td className={play.local?.netUsd ? tone(play.local.netUsd) : ''}>{play.local ? <><LocalResultValue local={play.local} format={reviewResult} />{play.local.signDiffers && <SignDiffersFlag />}</> : play.origin === 'position' ? 'Not a Play' : 'Not reported'}</td><td>{reviewPercent(play.returnPercent)}</td><td>{reviewMultiple(play.rMultiple)}</td><td>{play.origin === 'position' ? 'Not a Play' : play.reviewed ? 'Written' : 'Not yet'}</td></tr> })}
        {!report.plays.length && <tr><td colSpan={7}>No scored Plays in this selection.</td></tr>}
      </tbody></table></div>
      <div className="pr-pagination"><button className="pr-outline-button" disabled={report.offset === 0} onClick={() => setQuery({ ...query, offset: Math.max(0, report.offset - 50) })}>Previous</button><span>{report.plays.length ? report.offset + 1 : 0}–{report.offset + report.plays.length} of {report.metrics.count}</span><button className="pr-outline-button" disabled={report.nextOffset === null} onClick={() => { if (report.nextOffset !== null) setQuery({ ...query, offset: report.nextOffset }) }}>Next</button></div>
    </>}
  </>
}

export function ReviewPage({ api, reloadGeneration, onOpenPlay, workspaceAccounts = [] }: {
  api: WorkspaceApi; reloadGeneration: number; onOpenPlay: (id: string, signal: AbortSignal) => Promise<void>
  /** The shell's accounts, only to show each review account's address or index beside its name. */
  workspaceAccounts?: readonly BrokerAccount[]
}) {
  const venues = useVenues()
  const fieldId = useId()
  const paper = api.tradingMode === 'paper'
  const [query, setQuery] = useState<ReviewQuery>({ ...defaultReviewQuery, positions: 'auto' })
  const [{ minPlays }] = useReviewPreferences()
  // The threshold is a browser preference; one request object per query and threshold keeps superseded loads aborted.
  const requested = useMemo<ReviewQuery>(() => paper ? { ...query, positions: 'exclude' } : { ...query, minPlays }, [query, minPlays, paper])
  const [reload, setReload] = useState(0)
  const [group, setGroup] = useState<ReviewGroupBy>('portfolios')
  const [methods, setMethods] = useState(false)
  const [detail, setDetail] = useState<{ title: string; query: ReviewQuery } | null>(null)
  const [exclusions, setExclusions] = useState<ReviewQuery | null>(null)
  const trigger = useRef<HTMLElement | null>(null)
  const { report, loading, error, options } = useReport(api, requested, reload + reloadGeneration)
  const positions = report?.positions
  // Drill-downs keep this report's decision, so a narrower selection never switches positions on by itself.
  const pinned: ReviewQuery = { ...requested, positions: positions ? positions.included ? 'include' : 'exclude' : requested.positions ?? 'auto' }
  const remember = () => { trigger.current = document.activeElement instanceof HTMLElement ? document.activeElement : null }
  const restore = (event: Event) => { event.preventDefault(); trigger.current?.focus() }
  function inspect(id?: string, title = 'Closed Plays') {
    remember()
    const filter = id === undefined ? {} : group === 'portfolios' ? { portfolio: id } : group === 'accounts' ? { accountId: id } : { instrument: id }
    setDetail({ title, query: { ...pinned, ...filter, offset: 0 } })
  }
  const inspectExclusions = () => { remember(); setExclusions({ ...requested, offset: 0 }) }
  const accounts = options?.accounts.filter(a => query.portfolio === 'all' ||
    (query.portfolio === 'unassigned' ? a.portfolioId === null : a.portfolioId === query.portfolio)) ?? []
  const stats = report?.metrics
  // Each metric's own denominator (F-08): batting and profit factor count decided Plays, not scratches.
  const decided = stats ? stats.wins + stats.losses : 0
  const groups = report?.groups[group] ?? []
  const assets = report?.groups.assets ?? []
  const best = assets[0], worst = assets.at(-1)
  const averageMax = Math.max(Number(stats?.averageWin), Number(stats?.averageLossUsd), 1)
  const filtered = query.portfolio !== 'all' || query.accountId !== undefined || query.instrument !== undefined
  const activeAccounts = accounts.filter(a => query.accountId === undefined || a.id === query.accountId)
  // Account and asset groups report venue IDs; show the venue descriptors' names.
  const groupDetail = (row: ReviewGroup) => group === 'accounts' ? venues.name(options?.accounts.find(a => a.id === row.id)?.venueId ?? row.detail) :
    group === 'assets' ? row.detail.split(', ').map(id => venues.name(id)).join(', ') : row.detail
  return <section className="portfolio-review review-workspace" aria-label="Portfolio review">
    <div className="pr-heading"><h1>Portfolio review</h1><div className="pr-heading-actions"><button className="pr-outline-button" onClick={() => { remember(); setMethods(true) }}><Info size={15} />Definitions</button><button className="pr-outline-button" disabled={loading} onClick={() => setReload(n => n + 1)} aria-label="Reload review"><RefreshCw size={15} /></button></div></div>
    <section className="pr-filters" aria-label="Review filters">
      <label><span>Portfolio</span><NativeSelect value={query.portfolio} onChange={e => setQuery(previous => { const next = { ...previous, portfolio: e.target.value }; delete next.accountId; return next })}><option value="all">All portfolios</option>{options?.portfolios.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}<option value="unassigned">Unassigned accounts</option>{query.portfolio !== 'all' && query.portfolio !== 'unassigned' && !options?.portfolios.some(p => p.id === query.portfolio) && <option value={query.portfolio}>Unavailable portfolio</option>}</NativeSelect></label>
      <div className="pr-filter-field"><span id={`${fieldId}-account`}>Account</span><AccountPicker labelledBy={`${fieldId}-account`} value={query.accountId ?? ''}
        onChange={value => setQuery(previous => { const next = { ...previous }; if (value) next.accountId = value; else delete next.accountId; return next })}
        accounts={accounts.map(a => {
          const known = workspaceAccounts.find(w => w.id === a.id)
          return { id: a.id, name: a.name, venueId: a.venueId, source: known && known.tradingMode !== 'paper' ? known.sourceId ?? known.address : undefined,
            detail: a.portfolioId ? options?.portfolios.find(p => p.id === a.portfolioId)?.name ?? 'Portfolio' : 'Unassigned' }
        })}
        choices={[allAccountsChoice('All accounts'), ...(query.accountId && !accounts.some(a => a.id === query.accountId) ? [{ value: query.accountId, label: 'Unavailable account' }] : [])]} /></div>
      <label><span>Asset</span><NativeSelect value={query.instrument ?? ''} onChange={e => setQuery(previous => { const next = { ...previous }; if (e.target.value) next.instrument = e.target.value; else delete next.instrument; return next })}><option value="">All assets</option>{options?.instruments.map(asset => <option key={asset}>{asset}</option>)}{query.instrument && !options?.instruments.includes(query.instrument) && <option value={query.instrument}>{query.instrument} · unavailable</option>}</NativeSelect></label>
      <button className="pr-reset" disabled={!filtered} onClick={() => setQuery({ ...defaultReviewQuery, period: query.period, positions: query.positions ?? 'auto' })}><SlidersHorizontal size={15} />Reset</button>
    </section>
    <div className="pr-scope"><span>Nominal USD{query.portfolio === 'all' ? ' · includes unassigned' : ''}</span>
      {!paper && <span className="pr-positions-toggle"><label><input type="checkbox" role="switch" checked={positions?.included ?? query.positions === 'include'} disabled={loading}
        onChange={event => setQuery(previous => ({ ...previous, positions: event.target.checked ? 'include' : 'exclude' }))} />Include imported positions</label>
        <small>{query.positions === 'auto' || query.positions === undefined ? 'Automatic' : 'Manual'}</small>
        <InfoTip label="About imported positions">Closed Activity positions on no Play, with no plan, R or review. Automatic includes them while this selection has fewer than {minPlays} scored {minPlays === 1 ? 'Play' : 'Plays'} (change it in Settings). A Play weighs twice a position in batting average, average gain and loss, payoff and expectancy; dollar totals are never weighted.</InfoTip>
        {query.positions !== 'auto' && query.positions !== undefined && <button className="pr-count-link" onClick={() => setQuery(previous => ({ ...previous, positions: 'auto' }))}>Use automatic</button>}</span>}</div>
    {loading && <div className="pr-loading" role="status">Loading review…</div>}
    {error && <div className="pr-empty" role="alert"><p>{error}</p><button className="pr-outline-button" onClick={() => setReload(n => n + 1)}>Retry review</button></div>}
    {report && stats && <>
      <section className="pr-periods" role="group" aria-label="Performance period">{reviewPeriods.map(item => {
        const period = report.periods.find(p => p.id === item.id)!
        return <button key={item.id} aria-pressed={query.period === item.id} aria-label={item.label} onClick={() => setQuery(previous => ({ ...previous, period: item.id }))}><span>{item.label}</span><strong className={tone(period.net)}>{reviewMoney(period.net, true)}</strong><small>{resultCount(period.count, period.positions)}</small></button>
      })}</section>
      <div className="pr-section-label"><span className="pr-date-range">{report.fromUtc ? `${reviewDate(report.fromUtc, true)} to ` : ''}{reviewDate(report.asOfUtc, true)} · {new Date(report.asOfUtc).toISOString().slice(11, 16)} UTC</span>{stats.positions ? <span className="pr-weight-note">Plays weigh {positions?.playWeight ?? '2'}× a position in rates</span> : null}<button onClick={() => inspect()}>{resultCount(stats.count, stats.positions)}<ArrowRight size={12} /></button></div>
      <section className="pr-scorecard" aria-label="Performance scorecard">
        <article><span>Batting average</span><strong>{reviewPercent(stats.batting, true)}</strong><div className="pr-outcomes" aria-hidden="true"><i style={{ flex: stats.wins, background: 'var(--pr-green)' }} /><i style={{ flex: stats.losses, background: 'var(--pr-red)' }} /><i style={{ flex: stats.scratches, background: 'var(--pr-muted)' }} /></div><small><b className="positive">{stats.wins}W</b><b className="negative">{stats.losses}L</b><b>{stats.scratches} {stats.scratches === 1 ? 'scratch' : 'scratches'}</b></small><SampleSize n={decided} unit="decided" /></article>
        <article><span>Avg. gain / loss</span><strong>{reviewMultiple(stats.payoff)}</strong><small><span className="positive">{reviewPercent(stats.averageGain)}</span> / <span className="negative">{reviewPercent(stats.averageLoss)}</span></small><SampleSize n={Math.min(stats.wins, stats.losses)} label={`n = ${stats.wins} ${stats.wins === 1 ? 'win' : 'wins'} · ${stats.losses} ${stats.losses === 1 ? 'loss' : 'losses'}`} /></article>
        <article><span>Profit factor</span><strong>{reviewMultiple(stats.profitFactor)}</strong><SampleSize n={decided} unit="decided" /></article>
        <article><span>Expectancy per Play</span><strong className={tone(stats.expectancy)}>{reviewMoney(stats.expectancy, true)}</strong><SampleSize n={stats.count} label={`n = ${resultCount(stats.count, stats.positions)}`} /></article>
      </section>
      <div className="pr-chart-grid">
        <ReviewChart key={`${report.asOfUtc}-${JSON.stringify(query)}`} report={report} />
        <section className="pr-panel pr-outcome-card"><header className="pr-panel-heading"><h2>Average outcome</h2><BarChart3 size={17} /></header>
          <div className="pr-outcome-bars"><div><span><span className="pr-dot positive" />Winning Play<SampleSize n={stats.wins} label={`n = ${stats.wins} ${stats.wins === 1 ? 'win' : 'wins'}`} /></span><strong className="positive">{reviewMoney(stats.averageWin, true)}</strong><i><b style={{ width: `${Number(stats.averageWin) / averageMax * 100}%`, background: 'var(--pr-green)' }} /></i></div><div><span><span className="pr-dot negative" />Losing Play<SampleSize n={stats.losses} label={`n = ${stats.losses} ${stats.losses === 1 ? 'loss' : 'losses'}`} /></span><strong className="negative">{reviewMoney(negative(stats.averageLossUsd))}</strong><i><b style={{ width: `${Number(stats.averageLossUsd) / averageMax * 100}%`, background: 'var(--pr-red)' }} /></i></div></div>
          <dl className="pr-outcome-details"><div><dt><span className="pr-th-tip">Estimated R expectancy (planned-stop model)<InfoTip label="About estimated R">{estimatedRNote} The expectancy is their average.</InfoTip></span></dt><dd>{reviewMultiple(stats.averageR)} <small>{stats.riskCount} of {stats.count - (stats.positions ?? 0)} with a complete initial stop{stats.riskCount < smallSample ? ' · Small sample' : ''}</small></dd></div><div><dt>Max. P&L drawdown</dt><dd className="negative">{reviewMoney(negative(stats.drawdown))}</dd></div><div><dt>Longest streaks</dt><dd><span className="positive">{stats.maxWins}W</span><span className="pr-divider">/</span><span className="negative">{stats.maxLosses}L</span></dd></div>{stats.signDiffers !== undefined && <div><dt><span className="pr-th-tip">Sign differs by basis<InfoTip label="About result bases">Plays whose own fills (sell proceeds minus buy costs, less every fee) and venue result differ in sign. Unknown ones lack a Play-local result; each row says why. Win, loss and scratch still follow the venue result. {fundingNote}, so a difference can reflect funding.</InfoTip></span></dt><dd>{stats.signDiffers} of {stats.count - (stats.positions ?? 0)}{stats.localUnknown ? <small>{stats.localUnknown} unknown</small> : null}</dd></div>}{stats.feeOnlyLosses !== undefined && <div><dt><span className="pr-th-tip">Fee-only losses<InfoTip label="About fee-only losses">Losses that were zero or better before trading fees, on the venue result. They still count as losses. {fundingNote}, so one can reflect funding.</InfoTip></span></dt><dd>{stats.feeOnlyLosses} of {stats.losses} {stats.losses === 1 ? 'loss' : 'losses'}{stats.unknownGrossLosses ? <small>{stats.unknownGrossLosses} unknown before fees</small> : null}</dd></div>}</dl>
        </section>
      </div>
      {best && worst && best.id !== worst.id && <div className="pr-insights">
        <button onClick={() => setQuery({ ...query, positions: pinned.positions ?? 'auto', instrument: best.id })}><span className="pr-insight-icon positive"><ArrowUpRight size={18} /></span><span><small>HIGHEST ASSET P&L</small><strong>{best.name}<b className={tone(best.metrics.net)}>{reviewMoney(best.metrics.net, true)}</b></strong></span><span className="pr-insight-count">{resultCount(best.metrics.count, best.metrics.positions)}{best.metrics.count < smallSample && <small>Small sample</small>}<ChevronRight size={14} /></span></button>
        <button onClick={() => setQuery({ ...query, positions: pinned.positions ?? 'auto', instrument: worst.id })}><span className="pr-insight-icon negative"><ArrowDownRight size={18} /></span><span><small>LOWEST ASSET P&L</small><strong>{worst.name}<b className={tone(worst.metrics.net)}>{reviewMoney(worst.metrics.net, true)}</b></strong></span><span className="pr-insight-count">{resultCount(worst.metrics.count, worst.metrics.positions)}{worst.metrics.count < smallSample && <small>Small sample</small>}<ChevronRight size={14} /></span></button>
      </div>}
      <section className="pr-panel pr-breakdown"><header className="pr-panel-heading"><h2>Breakdown</h2><div className="pr-segmented" role="group" aria-label="Break down results by">{(['portfolios', 'accounts', 'assets'] as const).map(item => <button key={item} aria-pressed={group === item} onClick={() => setGroup(item)}>{item[0]!.toUpperCase() + item.slice(1)}</button>)}</div></header>
        <div className="pr-table-scroll"><table className="pr-breakdown-table"><caption className="sr-only">{group} ranked by net closed-play P&L</caption><thead><tr><th>{group === 'portfolios' ? 'Portfolio' : group === 'accounts' ? 'Account' : 'Asset'}</th><th>{stats.positions ? 'Results' : 'Plays'}</th><th>Batting avg.</th><th>Avg. gain / loss</th><th>Profit factor</th><th>Net P&L</th><th><span className="sr-only">Inspect</span></th></tr></thead><tbody>
          {groups.map((row, index) => <tr key={row.id}><th scope="row"><button onClick={() => inspect(row.id, row.name)}><span className="pr-rank">{String(index + 1).padStart(2, '0')}</span><span><strong>{row.name}</strong><small>{groupDetail(row)}</small></span></button></th><td>{row.metrics.positions ? resultCount(row.metrics.count, row.metrics.positions) : row.metrics.count}<small>{row.metrics.wins}W · {row.metrics.losses}L · {row.metrics.scratches}S</small></td><td>{reviewPercent(row.metrics.batting, true)}<SampleMark n={row.metrics.wins + row.metrics.losses} /></td><td>{reviewMultiple(row.metrics.payoff)}<SampleMark n={Math.min(row.metrics.wins, row.metrics.losses)} /></td><td>{reviewMultiple(row.metrics.profitFactor)}<SampleMark n={row.metrics.wins + row.metrics.losses} /></td><td className={tone(row.metrics.net)}><strong>{reviewMoney(row.metrics.net, true)}</strong><i className="pr-pnl-bar"><b style={{ width: `${Math.abs(Number(row.metrics.net)) / Math.max(...groups.map(g => Math.abs(Number(g.metrics.net))), 1) * 100}%`, background: Number(row.metrics.net) >= 0 ? 'var(--pr-green)' : 'var(--pr-red)' }} /></i></td><td><button className="pr-row-open" aria-label={`Inspect ${row.name}`} onClick={() => inspect(row.id, row.name)}><ChevronRight size={16} /></button></td></tr>)}
          {!groups.length && <tr><td colSpan={7}><div className="pr-empty"><BarChart3 size={25} /><strong>No scored closed Plays</strong>{filtered && <button className="pr-outline-button" onClick={() => setQuery({ ...defaultReviewQuery, positions: query.positions ?? 'auto' })}>Reset view</button>}</div></td></tr>}
        </tbody>{groups.length > 0 && <tfoot><tr><th scope="row">Combined result</th><td>{stats.positions ? resultCount(stats.count, stats.positions) : stats.count}</td><td>{reviewPercent(stats.batting, true)}<SampleMark n={decided} /></td><td>{reviewMultiple(stats.payoff)}<SampleMark n={Math.min(stats.wins, stats.losses)} /></td><td>{reviewMultiple(stats.profitFactor)}<SampleMark n={decided} /></td><td className={tone(stats.net)}>{reviewMoney(stats.net, true)}</td><td /></tr></tfoot>}</table></div>
      </section>
      <section className="pr-coverage" aria-label="Data coverage"><ShieldCheck size={16} /><details><summary>{stats.count} scored · {report.coverage.incompleteFees + report.coverage.incompleteExecutions} excluded · {stats.reviewed} reviewed</summary>
        <p><button className="pr-count-link" onClick={inspectExclusions}>{report.coverage.incompleteFees} incomplete fees</button> · <button className="pr-count-link" onClick={inspectExclusions}>{report.coverage.incompleteExecutions} incomplete executions</button> · {report.coverage.openPlays} currently open</p>
        <p>{report.coverage.unassignedFills} unassigned fills in this period.</p>
        <p>Initial planned-risk inputs for {stats.riskCount} of {stats.count - (stats.positions ?? 0)} scored Plays.</p>
        {positions?.included && <p>{positions.positions ?? 0} imported {positions.positions === 1 ? 'position' : 'positions'} included{positions.omitted ? `; ${positions.omitted} closed ${positions.omitted === 1 ? 'position' : 'positions'} could not be scored` : ''}.</p>}
        <p>{report.coverage.notice}</p>
        {report.coverage.firstFillAtUtc && <p>Earliest retained fill in this scope: {reviewDate(report.coverage.firstFillAtUtc, true)}.</p>}
        <ul>{activeAccounts.map(a => <li key={a.id}><strong>{a.name}</strong> · {venues.name(a.venueId)} · {a.syncStatus} · {a.lastSyncedAtUtc ? `last refreshed ${reviewDate(a.lastSyncedAtUtc, true)}` : 'not refreshed'}{a.historyNotice && <p>{a.historyNotice}</p>}</li>)}</ul>
      </details><button className="pr-outline-button" onClick={inspectExclusions} aria-label={`Inspect exclusions (${report.coverage.incompleteFees + report.coverage.incompleteExecutions})`}>{report.coverage.incompleteFees + report.coverage.incompleteExecutions} excluded</button></section>
    </>}
    <Dialog open={methods} onOpenChange={setMethods}><DialogContent className="pr-dialog" onCloseAutoFocus={restore}><DialogHeader><DialogTitle>Definitions</DialogTitle><DialogDescription className="sr-only">Closed-Play results from retained execution facts.</DialogDescription></DialogHeader><div className="pr-methods">{definitions.filter(([title]) => !paper || title !== 'Imported positions').map(([title, text]) => <section key={title}><h3>{title}</h3><p>{api.tradingMode === 'paper' && (title === 'Closed-play P&L' || title === 'Result bases') ? 'Simulated paper executions only, isolated from real fills and funds. Zero fees and slippage. Starting capital is not profit. Unresolved ambiguity and candle-history gaps are not scored. Results use simulated exit times and the same shared metric formulas.' : text}</p></section>)}</div></DialogContent></Dialog>
    <Dialog open={detail !== null} onOpenChange={open => { if (!open) setDetail(null) }}><DialogContent className="pr-dialog pr-detail-dialog" onCloseAutoFocus={restore}><DialogHeader><DialogTitle>{detail?.title}</DialogTitle><DialogDescription className="sr-only">Contributing closed Plays. Select a Play to open its journal.</DialogDescription></DialogHeader>
      {detail && <ReviewPlays api={api} initialQuery={detail.query} onOpenPlay={onOpenPlay} />}
    </DialogContent></Dialog>
    <Dialog open={exclusions !== null} onOpenChange={open => { if (!open) setExclusions(null) }}><DialogContent className="pr-dialog pr-detail-dialog" onCloseAutoFocus={restore}><DialogHeader><DialogTitle>Excluded Plays</DialogTitle><DialogDescription className="sr-only">Exact server reasons for this Review period and scope. Open the saved Play to inspect its records.</DialogDescription></DialogHeader>
      {exclusions && <ReviewExclusions api={api} initialQuery={exclusions} onOpenPlay={onOpenPlay} />}
    </DialogContent></Dialog>
  </section>
}
