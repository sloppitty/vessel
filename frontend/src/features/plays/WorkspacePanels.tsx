import { useId, useRef, useState, type FocusEvent, type FormEvent, type ReactNode } from 'react'
import { useVenues } from '@/api/venues'
import { Maximize2, Minimize2, NotebookPen, Pencil, Plus, RotateCcw } from 'lucide-react'
import type { BrokerAccount, Portfolio } from '@/api/workspace'
import { emptyStructuredReview, type PlayStatus, type StructuredReview } from '@/api/plays'
import { ChartIconButton } from '@/components/chart/ChartToolbar'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { money } from '@/lib/format'
import type { DraftEvidence, PlayDraft } from './draft'
import { EvidencePanel } from './EvidencePanel'
import { journalReviewVisible } from './journal'
import { JournalStateContext, useJournalStore } from './journal-state'
import { PositionRiskSummary } from './PositionRiskSummary'
import type { RiskReference } from './position-risk-summary'
import { averageEntryPrice, formatDraggedPrice, leverageOf } from './levels'
import { budgetSourceLabel, defaultSizeUnits, formatMoney, formatQuantity, playBudget, positionSize, type AvailableBalance, type SizeUnits } from './sizing'
import './plays-workspace.css'
import { NativeSelect } from '@/components/ui/native-select'

const displayMoney = (value: string | null | undefined) => value == null ? 'Unavailable' : money(value)
// Placeholder values stay readable but recede, so real figures carry the visual weight.
const isPlaceholder = (value: string) => ['Unavailable', 'Not calculated', 'Not chosen', 'Not set', 'Needs a size', 'Needs an entry price'].includes(value)

/**
 * The play's budget: a manual amount when set, otherwise the account's available balance, named by
 * its source (wallet, reported margin or reported withdrawal). A known zero is a budget of zero; only
 * without either does a "Set budget" action show.
 */
export function AvailableBudget({ draft, onChange, available = null }: {
  draft: PlayDraft
  onChange: (draft: PlayDraft) => void
  /** The account's normalized available balance in nominal USD and its source, or null when unknown. */
  available?: AvailableBalance | null
}) {
  const [editing, setEditing] = useState(false)
  const [budget, setBudget] = useState('')
  const [error, setError] = useState('')
  const id = useId()
  const current = playBudget(draft, available)

  function saveBudget(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const text = budget.trim()
    if (text && !/^\d+(\.\d+)?$/.test(text)) {
      setError('Enter a non-negative USD amount, or leave it blank to use the available amount.')
      return
    }
    onChange({ ...draft, budgetOverride: text || null })
    setEditing(false)
    setError('')
  }
  const edit = () => { setBudget(draft.budgetOverride ?? ''); setError(''); setEditing(true) }

  if (editing) return <form className="available-budget available-budget-editing" onSubmit={saveBudget} noValidate>
    <label htmlFor={`${id}-budget`}>Budget <small>$</small></label>
    <Input id={`${id}-budget`} inputMode="decimal" autoFocus value={budget} maxLength={100} placeholder={available?.value ?? '0'}
      aria-invalid={!!error} aria-describedby={error ? `${id}-budget-error` : undefined}
      onChange={(event) => { setBudget(event.target.value); setError('') }} />
    {error && <p id={`${id}-budget-error`} className="error" role="alert">{error}</p>}
    <div className="budget-actions">
      <Button type="submit" size="sm">Save budget</Button>
      <Button type="button" variant="ghost" size="sm" onClick={() => { setEditing(false); setError('') }}>Cancel</Button>
    </div>
  </form>

  if (current === null) return <div className="available-budget available-budget-empty">
    <Button type="button" variant="ghost" size="sm" className="set-budget" onClick={edit}><Plus size={13} aria-hidden="true" />Set budget</Button>
  </div>

  const source = budgetSourceLabel(current)
  return <div className="available-budget" role="group" aria-label="Budget">
    <span className="budget-label">Budget</span>
    <strong title={current.value}>{displayMoney(current.value)}</strong>
    <small title={current.source === 'manual' ? 'Set for this play' : 'The account\'s available balance, as the server reports it'}>{source}</small>
    <Button type="button" variant="ghost" size="icon-sm" aria-label="Edit budget" title="Set a budget for this play" onClick={edit}><Pencil aria-hidden="true" size={13} /></Button>
    {current.source === 'manual' && available !== null && <Button type="button" variant="ghost" size="icon-sm" aria-label="Use the available amount"
      title={`Use the available amount${available.source ? ` (${budgetSourceLabel({ source: available.source })})` : ''}`}
      onClick={() => onChange({ ...draft, budgetOverride: null })}><RotateCcw aria-hidden="true" size={13} /></Button>}
  </div>
}

export function CapitalContext({ accounts, portfolios, draft, retrospective = false }: {
  accounts: BrokerAccount[]
  portfolios: Portfolio[]
  draft: PlayDraft
  retrospective?: boolean
}) {
  const account = accounts.find(item => item.id === draft.accountId && item.isEnabled !== false)
  const portfolio = portfolios.find(item => item.id === account?.portfolioId)
  const venues = useVenues()
  const wallet = !!account && venues.can(account.venueId, 'stablecoinWallet')
  // Unified and portfolio-margin accounts keep all balances in the wallet; their perps state is not meaningful.
  const unified = account?.accountMode === 'unifiedAccount' || account?.accountMode === 'portfolioMargin'
  const sized = positionSize(draft, leverageOf(draft.leverage))
  // A portfolio sums its accounts' balances; an unassigned account stands on its own.
  const balance = portfolio ? (portfolio.balanceUsd === undefined ? portfolio.totalValueUsd : portfolio.balanceUsd)
    : account?.balanceUsd === undefined ? account?.accountValueUsd ?? null : account.balanceUsd
  const balanceValue = balance == null ? null : Number(balance)
  // Shares of the balance treat the quote asset as USD, like the nominal account values.
  const share = (value: number | null) => value === null || !balanceValue ? '—' : `${(value / balanceValue * 100).toFixed(1)}%`
  const coverage = portfolio?.balanceCoverage ?? portfolio?.valueCoverage
  // Each ratio names the balance it divides by: the portfolio's, or an unassigned account's own.
  const reference = portfolio ? `${portfolio.name} balance` : account ? `${account.name} balance` : 'balance'
  const metric = (label: string, value: string | null | undefined, title?: string) =>
    <div title={title}><dt>{label}</dt><dd data-placeholder={value == null}>{displayMoney(value)}</dd></div>
  return <section className="panel capital-context" aria-label="Capital context" data-testid="capital-context">
    <div className="capital-values">
      <h2 className="capital-title sr-only">Capital context</h2>
      <dl className="capital-metrics">
        {metric(portfolio ? `${portfolio.name} balance` : 'Account balance', balance,
          `Nominal USD${coverage ? `; coverage ${coverage}` : ''}. Perps equity plus supported stablecoins, or the wallet alone in unified accounts.`)}
        {wallet && metric('Wallet total', account.totalStablecoinNominalUsd,
          `${account.stablecoinScope ?? 'Supported stablecoin wallet'}${account.accountMode ? ` · ${account.accountMode}` : ''}. Nominal 1 token = 1 USD.`)}
        {wallet && metric('Available', account.availableStablecoinNominalUsd, 'Wallet total minus amounts held by open orders.')}
        {wallet && !unified && metric('Perps equity', account.accountValueUsd, 'Perpetual account value at the venue.')}
        {!wallet && account && metric('Account value', account.accountValueUsd)}
        {!retrospective && <><div title={`Committed margin as a share of the ${reference}`}><dt>Margin / {reference}</dt><dd data-placeholder={share(sized.margin) === '—'}>{share(sized.margin)}</dd></div>
          <div title={`Position size (notional) as a share of the ${reference}`}><dt>Exposure / {reference}</dt><dd data-placeholder={share(sized.notional) === '—'}>{share(sized.notional)}</dd></div></>}
      </dl>
    </div>
  </section>
}

const journalSections = [
  ['thesis', 'Thesis', 'Why this trade?'],
  ['invalidation', 'Invalidation', 'What would prove it wrong?'],
  ['strategy', 'Strategy', 'Which setup or rules apply?'],
  ['evidence', 'Evidence', 'Notes on the evidence'],
  ['review', 'Review', 'What happened, and what did you learn?'],
] as const

export function PlayJournal({ notes, onChange, evidence = [], onEvidenceChange, evidenceRequest = 0, readOnly = false, execution,
  structuredReview, onStructuredReviewChange, status = 'draft', retrospective = false }: {
  notes: PlayDraft['notes']
  onChange: (notes: PlayDraft['notes']) => void
  evidence?: DraftEvidence[]
  onEvidenceChange?: (update: (evidence: DraftEvidence[]) => DraftEvidence[]) => void
  /** Incremented to bring the Evidence tab forward, e.g. after a chart capture. */
  evidenceRequest?: number
  /** Keeps the pre-trade notes fixed; the review stays editable. */
  readOnly?: boolean
  /** Linked venue orders and fills of a saved play, shown as an Execution tab before the review. */
  execution?: ReactNode
  structuredReview?: StructuredReview | undefined
  onStructuredReviewChange?: (review: StructuredReview) => void
  /** Decides whether the Review tab is offered and whether structured answers can be given. */
  status?: PlayStatus
  retrospective?: boolean
}) {
  const id = useId()
  const state = useJournalStore()
  const [tab, setTab] = useState(retrospective ? 'review' : 'thesis')
  const [expanded, setExpanded] = useState(false)
  const expandButton = useRef<HTMLButtonElement>(null)
  const [shownRequest, setShownRequest] = useState(evidenceRequest)
  // While the owner is writing in the Review tab it stays, even if they clear the note to rewrite it.
  const [writingReview, setWritingReview] = useState(false)
  if (shownRequest !== evidenceRequest) {
    setShownRequest(evidenceRequest)
    setTab('evidence')
    setWritingReview(false)
  }
  const terminal = status === 'closed' || status === 'cancelled'
  const showReview = writingReview || journalReviewVisible({ status, retrospective, review: notes.review, structuredReview })
  const sections = journalSections.filter(([key]) => (key !== 'review' || showReview) && (!retrospective || key === 'evidence' || key === 'review'))
  const fallback = retrospective ? 'review' : 'thesis'
  const currentTab = sections.some(([key]) => key === tab) || execution && tab === 'execution' ? tab : fallback
  // A Review tab that is no longer offered falls back for good, so it never reappears selected later.
  if (tab === 'review' && !showReview) setTab(fallback)
  const review = structuredReview ?? emptyStructuredReview()
  const changeReview = (patch: Partial<StructuredReview>) => onStructuredReviewChange?.({ ...review, ...patch })

  const heading = (inDialog: boolean) => <div className="journal-heading">
    <span className="journal-icon" aria-hidden="true"><NotebookPen size={15} /></span>
    {inDialog ? <DialogTitle className="journal-title">Play journal</DialogTitle> : <h2 className="journal-title">Play journal</h2>}
  </div>
  // The inline and expanded journals are the same tabs over the same notes; only one is mounted at a time.
  // Tabs switch on pointer down, which removes the Review panel before any blur, so leaving it ends the writing too.
  const chooseTab = (next: string) => { setTab(next); if (next !== 'review') setWritingReview(false) }
  const journal = (inDialog: boolean) => <Tabs value={currentTab} onValueChange={chooseTab} className="journal-tabs">
    <header className="journal-header">
      {heading(inDialog)}
      <TabsList variant="line" className="journal-tab-list" aria-label="Journal sections">
        {sections.map(([key, label]) => <TabsTrigger key={key} value={key}>{label}
          {key === 'evidence' && evidence.length > 0 && <span className="journal-tab-count" aria-hidden="true">{evidence.length}</span>}
        </TabsTrigger>).flatMap((trigger, index) => execution && sections[index]![0] === 'evidence'
          ? [trigger, <TabsTrigger key="execution" value="execution">Execution</TabsTrigger>] : [trigger])}
      </TabsList>
      <div className="journal-actions">
        {inDialog
          ? <ChartIconButton label="Close expanded journal" icon={<Minimize2 size={15} aria-hidden="true" />} onClick={() => setExpanded(false)} />
          : <ChartIconButton ref={expandButton} label="Expand journal" icon={<Maximize2 size={15} aria-hidden="true" />} onClick={() => setExpanded(true)} />}
      </div>
    </header>
    {sections.map(([key, label, help]) => <TabsContent key={key} value={key} className="journal-tab-content" data-section={key}
      {...key === 'review' ? {
        onFocus: () => setWritingReview(true),
        onBlur: (event: FocusEvent<HTMLDivElement>) => { if (!event.currentTarget.contains(event.relatedTarget)) setWritingReview(false) },
      } : {}}>
      {key === 'evidence' && onEvidenceChange && <EvidencePanel evidence={evidence} onChange={onEvidenceChange} />}
      <label className="sr-only" htmlFor={`${id}-${key}`}>{label}</label>
      <textarea id={`${id}-${key}`} className="journal-note" value={notes[key]} placeholder={help} readOnly={readOnly && key !== 'review'}
        onChange={(event) => onChange({ ...notes, [key]: event.target.value })} />
      {key === 'review' && onStructuredReviewChange && <fieldset className="structured-review" disabled={!terminal}>
        <legend><span>Structured review</span>{terminal ? <small>optional</small>
          : <small title="Structured answers are available after Closed or Cancelled. Your free-text note stays readable.">After close or cancel</small>}</legend>
        <div className="structured-review-fields">
          <div className="structured-review-field">
            <label htmlFor={`${id}-adherence`}>Plan adherence</label>
            <NativeSelect id={`${id}-adherence`} value={review.adherence ?? ''}
              onChange={event => changeReview({ adherence: (event.target.value || null) as StructuredReview['adherence'] })}>
              <option value="">Unanswered</option>
              {!retrospective && <><option value="followed">Followed</option><option value="deviated">Deviated</option></>}
              <option value="not-applicable">Not applicable</option>
            </NativeSelect>
          </div>
          <div className="structured-review-field">
            <label htmlFor={`${id}-mistake`}>Mistake tag</label>
            <input id={`${id}-mistake`} maxLength={100} value={review.mistakeTag ?? ''} placeholder="e.g. Chased the entry"
              onChange={event => changeReview({ mistakeTag: event.target.value || null })} />
          </div>
          <div className="structured-review-field">
            <label htmlFor={`${id}-lesson`}>Lesson</label>
            <textarea id={`${id}-lesson`} maxLength={4000} value={review.lesson}
              onChange={event => changeReview({ lesson: event.target.value })} />
          </div>
          <div className="structured-review-field">
            <label htmlFor={`${id}-next`}>Next action</label>
            <textarea id={`${id}-next`} maxLength={4000} value={review.nextAction}
              onChange={event => changeReview({ nextAction: event.target.value })} />
          </div>
        </div>
      </fieldset>}
    </TabsContent>)}
    {execution && <TabsContent value="execution" className="journal-tab-content" data-section="execution">{execution}</TabsContent>}
  </Tabs>

  // The dialog is portaled but still inside this provider, so both views share the tabs' working state.
  return <JournalStateContext.Provider value={state}><section className="panel journal-panel" aria-label="Play journal" data-testid="journal-panel">
    {expanded ? <>
      <header className="journal-header">{heading(false)}</header>
      <p className="journal-parked">Journal open in the expanded view.</p>
    </> : journal(false)}
    <Dialog open={expanded} onOpenChange={setExpanded}>
      <DialogContent className="plays-journal-dialog" showCloseButton={false}
        onCloseAutoFocus={event => { event.preventDefault(); expandButton.current?.focus({ preventScroll: true }) }}>
        <DialogDescription className="sr-only">The same journal with more room. Edits apply to this play. Escape returns to the workspace.</DialogDescription>
        {journal(true)}
      </DialogContent>
    </Dialog>
  </section></JournalStateContext.Provider>
}

export function PositionSummary({ draft, units = defaultSizeUnits, instrumentName, riskReference }: { draft: PlayDraft; units?: SizeUnits; instrumentName?: string; riskReference?: RiskReference | null | undefined }) {
  const count = draft.entries.length
  // Same quantity-weighted planned average as the chart's AVG line, for one or more priced entries.
  const average = averageEntryPrice(draft.entries, 1)
  const sized = positionSize(draft, leverageOf(draft.leverage))
  const missing = draft.size ? 'Needs an entry price' : 'Needs a size'
  const values: Array<[string, string]> = [
    ['Margin', sized.margin === null ? missing : formatMoney(sized.margin, units)],
    ['Position size', sized.notional === null ? missing : formatMoney(sized.notional, units)],
    ['Quantity', sized.quantity === null ? missing : formatQuantity(sized.quantity, units)],
    ['Average entry', average === null ? 'Not set' : formatDraggedPrice(average)],
  ]
  return <section className="panel summary" aria-label="Full-position summary" data-testid="summary-panel">
    <div className="summary-intro">
      <h2 title="Planned position before fees and funding">Summary</h2>
      <strong>{count} {count === 1 ? 'entry' : 'entries'} · <span data-direction={draft.direction}>{draft.direction === 'long' ? 'Long' : 'Short'}</span></strong>
      <small>{instrumentName || draft.instrument || 'No instrument'} · Leverage {draft.leverage || '–'}×</small>
    </div>
    <dl>
      {values.map(([label, value]) => <div key={label}><dt>{label}</dt><dd data-placeholder={isPlaceholder(value)}>{value}</dd></div>)}
    </dl>
    <PositionRiskSummary draft={draft} units={units} riskReference={riskReference ?? null} />
  </section>
}
