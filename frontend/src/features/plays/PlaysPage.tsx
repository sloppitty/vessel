import { PlayBundleButton } from './PlayBundleButton'
import type { PaperPlayState, PaperSettings as PaperSettingsDocument } from '@/api/paper'
import { PaperExecutionPanel } from './PaperExecutionPanel'
import { errorMessage } from '@/api/errors'
import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react'
import type { BrokerAccount, Portfolio, WorkspaceApi } from '@/api/workspace'
import { isEndedStatus, isReviewed, pagedPlayViews, playSearchLength, statusLabels, type NextAction, type PlayCounts, type PlayList as PlayListPage, type PreflightCheck, type PlayListQuery, type PlayStatus, type UnresolvedReason, type PlayView, type SavedPlay, type StatusRequest } from '@/api/plays'
import type { AccountMonitor } from '@/api/monitor'
import { Button } from '@/components/ui/button'
import { Pager } from '@/components/ui/pager'
import { isPageSize } from '@/components/ui/paging'
import { Skeleton, SkeletonStatus } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useDelayedFlag } from '@/lib/use-delayed-flag'
import { useNotifications } from '@/components/notifications/notifications'
import { preloadLightweightRenderer } from '@/components/chart/lazy'
import { useVenues } from '@/api/venues'
import { ArrowLeft, CircleHelp, Copy, History, Pause, Play, Plus, RotateCcw, Save, Search, Trash2, X } from 'lucide-react'
import type { PlayDraft } from './draft'
import { PlayWorkspace } from './PlayWorkspace'
import { PlayIdentity } from './PlayIdentity'
import { ExecutionPanel } from './ExecutionPanel'
import { ManualExecutionPanel } from './ManualExecutionPanel'
import { emptyManualDraft, hasManualDraft } from './manual'
import { useMonitorRun } from '@/features/monitor/useMonitorRun'
import { incidentsByPlay, monitorIncidents, type IncidentCounts } from '@/features/monitor/incidents'
import { IncidentPill } from '@/features/monitor/IncidentBadge'
import { stopEvidenceFor } from '@/features/monitor/coverage'
import { usePlayExecution } from './usePlayExecution'
import { useEvidenceImages } from './evidence-images'
import { useSizing } from './useSizing'
import { noFilledEntries, planIssues, planIssueSummary } from './planChecks'
import { executionFills, manualLedgerFills, paperEntryFills, useManualLedger } from './entry-fills'
import { RetrospectiveReceipt, ReviewReceipt } from './Receipt'
import { PlayLog, ReviewCompletionBar, type ReviewAction } from './ReviewCompletion'
import type { PlayLogChange } from './log-draft'
import { noEntryFills, type EntryFills } from './position-risk-summary'
import { planNumberIssues, planNumberSummary, storedPlanText, StoredPlanTextContext, type StoredPlanText } from './stored-plan-text'
import { availableOf, defaultSizeUnits, marginOverBudget, overBudgetMessage, type SizeUnits } from './sizing'
import { leverageOf } from './levels'
import type { ChartFill } from './chart-fills'
import { CancelDialog, DeleteDialog, HistoryDialog, PauseDialog, PreflightDialog, ReturnToOpenDialog, RevisionDialog, UnresolvedDialog } from './PlayDialogs'
import { initialPlaysList, playSearchDelay, playViewTabs, type PlaysListState } from './plays-list'
import { useDisplayZone } from '@/lib/display-zone'
import { formatTime } from '@/lib/time'
import { Time } from '@/components/ui/time'
import {
  createPlaysSession, draftFromSaved, draftSession, duplicateDraft, fieldsToSave, hasDraftContent, hasPlayLogDraft, hasUnsavedPlayWork, isDirty, loadSavedPlay, mergeSavedEvidence, planChanged, savedState,
  observedPlayVersion, startNewPlay, syncEvidence,
  type PlaysSession, type SavedState,
} from './saved'


const ended: PlayStatus[] = ['closed', 'cancelled', 'unresolved']
type DialogKind = null | 'revision' | 'pause' | 'cancel' | 'delete' | 'history' | 'preflight' | 'unresolved' | 'return-open'
type WorkKind = 'save' | 'status' | 'open' | 'delete' | 'manual'

export function PlaysPage({ accounts, portfolios, api, session, onSession, onReload, loading = false, requestedPlayId, requestedPlayRestore = false, requestedPlayKey, onRequestedPlayHandled, active: visible = true, workRef, list, onList, reloadGeneration = 0, accountMonitors = [], onManageAccount, onLogChange, onLogApplier }: {
  accounts: BrokerAccount[]
  portfolios: Portfolio[]
  api: WorkspaceApi
  session: PlaysSession
  onSession: (session: PlaysSession) => void
  /**
   * Applies a log change to the owner's current session (phase 1, 1.2). A log write that completes after this page has
   * gone reaches the session through it; the owner refuses it once the session was discarded.
   */
  onLogChange?: ((change: PlayLogChange) => void) | undefined
  /**
   * Registers this page as the one that applies log changes while it is mounted, so the owner's late acknowledgements
   * go through the page's own current session and never race its edits; returns the unregistration.
   */
  onLogApplier?: ((apply: (change: PlayLogChange) => void) => () => void) | undefined
  onReload?: (() => void) | undefined
  reloadGeneration?: number
  accountMonitors?: AccountMonitor[]
  /** Opens the account's management dialog, where its read-only credential is replaced. */
  onManageAccount?: ((accountId: string) => void) | undefined
  loading?: boolean
  requestedPlayId?: string | null
  /** Breadcrumb restoration waits for this owner to confirm any discard before the shell changes location. */
  requestedPlayRestore?: boolean
  /** The shell's identity for this request: asking for the same Play again is a new request. Returned when handled. */
  requestedPlayKey?: unknown
  onRequestedPlayHandled?: (opened?: boolean, key?: unknown) => void
  /** The shell retains this mutation owner while another page is visible, not its UI or polling. */
  active?: boolean
  /** Shared with shell actions that could replace the session, before disabled controls render. */
  workRef?: RefObject<symbol | null>
  /** The list's view, search, page and size; the shell keeps them while a Play is open. */
  list?: PlaysListState
  onList?: (list: PlaysListState) => void
}) {
  const [localList, setLocalList] = useState(initialPlaysList)
  const listState = list ?? localList
  const setListState = onList ?? setLocalList
  useEffect(() => { void preloadLightweightRenderer().catch(() => { /* The adapter shows a load error when mounted. */ }) }, [])
  const latest = useRef(session)
  useEffect(() => { latest.current = session })
  const update = useCallback((next: Partial<PlaysSession>) => {
    latest.current = { ...latest.current, ...next }
    onSession(latest.current)
  }, [onSession])
  // While mounted, every log change, a departed page's included, applies to this page's current session (1.2).
  const applyLog = useCallback((change: PlayLogChange) => update({ log: change(latest.current.log) }), [update])
  useEffect(() => onLogApplier?.(applyLog), [onLogApplier, applyLog])
  const [busy, setBusy] = useState<WorkKind | null>(null)
  const activeWork = useRef(workRef ?? { current: null as symbol | null })
  const [navigationError, setNavigationError] = useState<string | null>(null)
  const requestAttempt = useRef<number | null>(null)
  // Every shell request is its own generation, a repeat of the same Play or a new connection included: a response, the
  // discard confirmed for it and its acknowledgement belong to the request that started them, never to a later one.
  // Declared before the request effect below.
  const requestGeneration = useRef(0)
  useEffect(() => { requestGeneration.current++ }, [requestedPlayId, requestedPlayRestore, requestedPlayKey, api])
  const openController = useRef<AbortController | null>(null)
  const epoch = useRef(0)
  const mounted = useRef(false)
  const currentApi = useRef(api)
  useEffect(() => { currentApi.current = api })
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  useEffect(() => {
    const currentEpoch = epoch
    const controllerRef = openController
    const workRef = activeWork.current
    currentEpoch.current++
    workRef.current = null
    requestAttempt.current = null
    let active = true
    queueMicrotask(() => { if (active && !workRef.current) setBusy(null) })
    return () => { active = false; currentEpoch.current++; workRef.current = null; controllerRef.current?.abort() }
  }, [api, activeWork])
  // Acquire synchronously, before React renders disabled controls. Only the owner may release.
  const beginWork = useCallback((kind: WorkKind) => {
    if (!mounted.current || currentApi.current !== api || activeWork.current.current) return null
    const owner = Symbol(kind)
    activeWork.current.current = owner
    setBusy(kind)
    return () => {
      if (activeWork.current.current !== owner) return
      activeWork.current.current = null
      if (mounted.current && currentApi.current === api) setBusy(null)
    }
  }, [api, activeWork])
  // Errors appear as popup notifications; a newer one replaces the last, and starting another action clears it.
  const { notify, dismiss } = useNotifications()
  const setError = useCallback((message: string | null) => {
    if (message) notify({ tone: 'error', key: 'plays-error', message })
    else dismiss('plays-error')
  }, [notify, dismiss])
  // A version identifies the whole saved document, not just its lifecycle.
  const acceptStoredPlay = useCallback((play: SavedPlay) => {
    if (!mounted.current || currentApi.current !== api) return false
    const { draft: currentDraft, saved: current } = latest.current
    if (!current || current.summary.id !== play.summary.id || current.summary.accountId !== play.summary.accountId ||
      play.summary.version < observedPlayVersion(latest.current, api)) return false
    const observedPlay = { api, id: play.summary.id, accountId: play.summary.accountId, version: play.summary.version, conflict: false }
    const stored = draftFromSaved(play, currentDraft.evidence)
    const next = { ...savedState(play, stored), evidenceIds: current.evidenceIds }
    if (next.fieldsKey !== current.fieldsKey) {
      if (isDirty(currentDraft, current)) {
        update({ observedPlay: { ...observedPlay, conflict: true } })
        return true
      }
      update({ saved: next, draft: stored, observedPlay })
    } else {
      update({ saved: next, observedPlay })
    }
    return true
  }, [api, update])
  const [dialog, setDialogState] = useState<DialogKind>(null)
  // An async action may outlive the render, or the dialog that started it.
  const currentDialog = useRef({ kind: null as DialogKind })
  const setDialog = (kind: DialogKind) => {
    currentDialog.current = { kind }
    setDialogState(kind)
    setDialogError(null)
  }
  const closeDialog = (intended: { kind: DialogKind }) => {
    if (currentDialog.current === intended) setDialog(null)
  }
  const actionError = (cause: unknown, intended: { kind: DialogKind }) => {
    const message = errorMessage(cause, 'The change could not be completed. Try again.')
    if (currentDialog.current === intended && intended.kind && intended.kind !== 'history') setDialogError(message)
    else setError(message)
  }
  const [dialogError, setDialogError] = useState<string | null>(null)

  const { draft, saved } = session
  // Saved originals load after the Play opens, outside the draft, and stay while the session holds it.
  const evidenceImages = useEvidenceImages(api, draft.evidence)
  const venues = useVenues()
  const account = accounts.find(item => item.id === (saved?.summary.accountId ?? draft.accountId))
  const paper = api.tradingMode === 'paper'
  const [paperSettings, setPaperSettings] = useState<{ api: WorkspaceApi; settings: PaperSettingsDocument } | null>(null)
  useEffect(() => {
    if (!paper) return
    const controller = new AbortController()
    api.paperSettings(controller.signal).then(settings => { if (!controller.signal.aborted) setPaperSettings({ api, settings }) }).catch(() => { /* Unknown settings cannot arm. */ })
    return () => controller.abort()
  }, [api, paper, reloadGeneration, visible])
  const paperArming = !paper || paperSettings?.api === api && paperSettings.settings.enabled && !!account && venues.can(account.venueId, 'paperExecution')
  const [paperResult, setPaperResult] = useState<{ api: WorkspaceApi; id: string; version: number; state: PaperPlayState } | null>(null)
  const paperState = paperResult?.api === api && paperResult.id === saved?.summary.id && paperResult.version === saved.summary.version ? paperResult.state : null
  const lastPaperCheck = useRef<{ api: WorkspaceApi; id: string; signature: string } | null>(null)
  const onPaperState = useCallback((state: PaperPlayState | null) => {
    setPaperResult(state && saved ? { api, id: saved.summary.id, version: saved.summary.version, state } : null)
    if (state && saved) {
      const previous = lastPaperCheck.current
      const signature = JSON.stringify([state.status, state.checkedAtUtc, state.enteredQuantity, state.exitedQuantity, state.openQuantity, state.realizedPnlUsd, state.issues])
      if (previous?.api !== api || previous.id !== state.playId || previous.signature !== signature) {
        lastPaperCheck.current = { api, id: state.playId, signature }
        onReload?.()
      }
    }
  }, [api, saved, onReload])
  const paperFills = useMemo(() => paperState ? paperEntryFills(paperState) : null, [paperState])
  const manual = !paper && !!account && venues.can(account.venueId, 'manualExecutions')
  const currentBookRead = !account || venues.readsCurrentBook(account.venueId)
  const retrospective = saved?.summary.origin === 'retrospective'
  // Record-based suggestions also follow accepted stored-state reloads.
  const sizing = useSizing(api, reloadGeneration, visible)
  const tracking = usePlayExecution(api, visible && session.view === 'editor' && saved && !manual && !paper ? saved.summary.id : null, saved?.summary.status, reloadGeneration, saved?.summary.planRevision)
  const { reload: reloadExecution } = tracking
  // A shell reload already rereads the execution through its generation; reload it directly only without one.
  const monitorChanged = useCallback(() => { if (onReload) onReload(); else reloadExecution() }, [reloadExecution, onReload])
  // One count per Play from the shell's monitor summary, shared by the list rows and the Play header.
  const playIncidents = useMemo(() => incidentsByPlay(monitorIncidents(accountMonitors)), [accountMonitors])
  const executionMonitor = visible && account?.isEnabled !== false ? tracking.execution?.monitor ?? null : null
  const monitorRun = useMonitorRun(api, executionMonitor ? accountMonitors.find(item => item.accountId === executionMonitor.accountId) ?? executionMonitor : null, monitorChanged)
  // The chart labels this Play's planned stops with the venue evidence of the newest stored monitor read.
  const trackedExecution = tracking.execution?.playId === saved?.summary.id ? tracking.execution : null
  const zone = useDisplayZone()
  const stopEvidence = useMemo(() => stopEvidenceFor(trackedExecution && monitorRun.monitor?.accountId === trackedExecution.monitor?.accountId
    ? { ...trackedExecution, monitor: monitorRun.monitor } : trackedExecution, value => formatTime(value, zone, 'day-time')),
  [trackedExecution, monitorRun.monitor, zone])

  // Linked fills move a play to Open or Closed on the server; take the new status and version.
  const venueStatus = tracking.execution?.playId === saved?.summary.id ? tracking.execution?.status : undefined
  const storedGeneration = useRef<{ api: WorkspaceApi; id: string; generation: number; status: string | undefined } | null>(null)
  const storedAccept = useRef(acceptStoredPlay)
  useEffect(() => { storedAccept.current = acceptStoredPlay }, [acceptStoredPlay])
  const [storedReadError, setStoredReadError] = useState<string | null>(null)
  // A failed reread describes the Play that was shown; switching Plays or leaving the page drops it.
  useEffect(() => () => setStoredReadError(null), [visible, saved?.summary.id])
  useEffect(() => {
    const state = latest.current.saved
    if (!visible || !state) return
    const previous = storedGeneration.current
    storedGeneration.current = { api, id: state.summary.id, generation: reloadGeneration, status: venueStatus }
    const reloaded = previous?.api === api && previous.id === state.summary.id && previous.generation !== reloadGeneration
    if (previous?.api === api && previous.id === state.summary.id && previous.generation === reloadGeneration && previous.status === venueStatus) return
    if (!reloaded && (!venueStatus || venueStatus === state.summary.status)) return
    const controller = new AbortController()
    api.play(state.summary.id, controller.signal).then(play => {
      if (!controller.signal.aborted) { storedAccept.current(play); setStoredReadError(null) }
    }).catch(cause => { if (!controller.signal.aborted) setStoredReadError(errorMessage(cause, 'The change could not be completed. Try again.')) })
    return () => controller.abort()
  }, [visible, api, venueStatus, reloadGeneration, saved?.summary.id])
  // The server keeps the direction once an entry has filled: it decides which side closes the position.
  const filled = paperFills ? paperFills.filled.size > 0 : venueStatus !== undefined && !!tracking.execution?.links.some(link => link.role === 'entry' && link.fills.length > 0)
  const status = saved?.summary.status ?? 'draft'
  // Which entries have filled, per entry, as the server decides it: the execution's linked entry fills for a venue Play,
  // the manual ledger for a manual one. Whatever facts are loaded count, whatever the saved status says (a reread of the
  // Play can fail after the execution moved it to Open). Until they load, or after a failed reread, they are unknown.
  const playExecution = venueStatus !== undefined ? tracking.execution : null
  const fillsDirection = saved?.plan.direction ?? draft.direction
  const manualLedger = useManualLedger(api, visible && manual && saved && status !== 'draft' ? saved.summary.id : null, observedPlayVersion(session, api))
  const ledger = useMemo(() => manualLedgerFills(manualLedger), [manualLedger])
  // The Play's fills on the chart (phase 1, 1.1b): its linked venue fills, or its manual ledger's executions.
  const playId = saved?.summary.id
  const chartFills = useMemo<ChartFill[] | undefined>(() => {
    if (manual) return manualLedger && manualLedger !== 'unknown' ? manualLedger.records.map(record => ({ id: record.id, atUtc: record.occurredAtUtc,
      price: record.price, quantity: record.quantity, side: record.side, role: record.role })) : undefined
    const execution = tracking.execution
    if (!execution || execution.playId !== playId) return undefined
    return execution.links.flatMap(link => link.fills.map(fill => ({ id: `${link.id}:${fill.sourceFillId}`, atUtc: fill.occurredAtUtc,
      price: fill.price, quantity: fill.quantity, side: fill.side, role: link.role === 'entry' ? 'entry' as const : 'exit' as const })))
  }, [manual, manualLedger, tracking.execution, playId])
  const venueFills = useMemo(() => !manual && playExecution ? executionFills(playExecution, fillsDirection) : null,
    [manual, playExecution, fillsDirection])
  const fills: EntryFills | 'unknown' = status === 'draft' ? noEntryFills
    : paper ? paperFills ?? 'unknown' : manual ? ledger ?? 'unknown' : venueFills && !tracking.stale ? venueFills : 'unknown'
  // Entries with a fill get no size, leverage, stop or target suggestions (D-11). An Open Play has a fill somewhere, so
  // until the facts say which entry, none gets one.
  const opened = status === 'open' || venueStatus === 'open'
  const filledEntries: ReadonlySet<string> | 'all' = fills === 'unknown' ? opened ? 'all' : noFilledEntries : fills.filled
  const [priceStep, setPriceStep] = useState<number | null>(null)
  // The Draft's pre-flight as the workspace last evaluated it; Plan it records it with revision 1 (phase 1, 1.5).
  const [preflight, setPreflight] = useState<PreflightCheck[] | null>(null)
  // The owner's recent next actions, offered while a Play is not yet under way. They are context, so a failed read shows none.
  const [nextActions, setNextActions] = useState<{ api: WorkspaceApi; actions: NextAction[] } | null>(null)
  const offersNextActions = visible && session.view === 'editor' && (saved?.summary.status ?? 'draft') !== 'open' &&
    !(saved && isEndedStatus(saved.summary.status)) && saved?.summary.origin !== 'retrospective'
  useEffect(() => {
    if (!offersNextActions) return
    const controller = new AbortController()
    api.nextActions(controller.signal).then(actions => { if (!controller.signal.aborted) setNextActions({ api, actions }) })
      .catch(() => { /* No suggestions on failure; planning does not depend on them. */ })
    return () => controller.abort()
  }, [api, offersNextActions])
  // The owner's earlier mistake tags, offered while reviewing an ended Play (phase 1, 1.4b). A failed read offers none.
  const [mistakeTags, setMistakeTags] = useState<{ api: WorkspaceApi; tags: string[] } | null>(null)
  const offersMistakeTags = visible && session.view === 'editor' && !!saved && isEndedStatus(saved.summary.status)
  useEffect(() => {
    if (!offersMistakeTags) return
    const controller = new AbortController()
    api.mistakeTags(controller.signal).then(tags => { if (!controller.signal.aborted) setMistakeTags({ api, tags }) })
      .catch(() => { /* Autocomplete only; the field still takes any tag. */ })
    return () => controller.abort()
  }, [api, offersMistakeTags])
  // The workspace knows the instrument's units; page messages such as the blocked-save reason use them.
  const [units, setUnits] = useState<SizeUnits>(defaultSizeUnits)
  const onUnits = useCallback((next: SizeUnits) => setUnits(current => current.quote === next.quote && current.base === next.base &&
    current.quantityDecimals === next.quantityDecimals ? current : next), [])
  const dirty = useMemo(() => saved ? isDirty(draft, saved) : hasDraftContent(draft), [draft, saved])
  // Number text unchanged from the saved plan still saves, even where it predates the plain-decimal rule.
  const storedText = useMemo(() => storedPlanText(saved?.plan), [saved?.plan])
  const changedPlan = useMemo(() => !!saved && planChanged(draft, saved), [draft, saved])
  const unsaved = dirty || hasManualDraft(session.manual) || hasPlayLogDraft(session)
  // A blocked save says why until the plan is fixed.
  const fixed = !planProblem(draft, status, storedText)
  useEffect(() => { if (fixed) dismiss('plan-blocked') }, [fixed, dismiss])

  /**
   * Plan numbers the server would refuse, stops and targets on the wrong side of their entry, and a
   * draft's margin above its budget block saving and planning until they are fixed. Once orders may
   * rest the venue counts their margin as used, so the budget is only held while the play is a draft.
   */
  function planProblem(current: PlayDraft, currentStatus: PlayStatus, stored: StoredPlanText) {
    if (retrospective) return null
    const numbers = planNumberIssues(current, stored)
    if (numbers.length) return planNumberSummary(numbers)
    // A filled entry's stops may sit at or beyond it, and a stop kept unchanged from the saved plan still saves.
    const issues = planIssues(current.entries, current.direction, fills === 'unknown' ? noFilledEntries : fills.filled, stored)
    if (issues.length) return planIssueSummary(issues)
    const account = accounts.find(item => item.id === current.accountId)
    const over = currentStatus === 'draft' ? marginOverBudget(current, leverageOf(current.leverage), availableOf(account)) : null
    return over && overBudgetMessage(over, units)
  }

  function planBlocked() {
    const { draft: current, saved: state } = latest.current
    const problem = planProblem(current, state?.summary.status ?? 'draft', storedPlanText(state?.plan))
    if (problem) notify({ tone: 'error', key: 'plan-blocked', title: 'Not saved', message: `Fix the plan first. ${problem}` })
    return !!problem
  }

  /** Saves the draft and its evidence. Returns the saved state, or null when the save failed. */
  async function save(reason?: string): Promise<SavedState | null> {
    const finish = beginWork('save')
    if (!finish) return null
    try { return await saveDraft(reason) } finally { finish() }
  }

  /** The caller owns the shared work guard, including a save followed by planning. */
  async function saveDraft(reason?: string): Promise<SavedState | null> {
    const started = epoch.current
    const intendedDialog = currentDialog.current
    const { draft: sent, saved: before } = latest.current
    if (!sent.accountId) { setError('Choose an account before saving.'); return null }
    if (paper && sent.instrumentSource !== 'venue') { setError('Paper plays require a supported venue instrument. Manual labels cannot be simulated.'); return null }
    if (planBlocked()) return null
    setError(null); setDialogError(null)
    try {
      const fields = fieldsToSave(sent, before?.summary.status, new Date(), before?.plan ?? null)
      const play = before
        ? await api.updatePlay(before.summary.id, { ...fields, expectedVersion: before.summary.version, ...(reason ? { revisionReason: reason } : {}) })
        : await api.createPlay(fields)
      if (started !== epoch.current) return null
      if (before && play.summary.version < observedPlayVersion(latest.current, api)) {
        setError('The Play was saved, but this response is older than the latest known Play. Reload the current saved Play before saving again.')
        return null
      }
      update({ observedPlay: { api, id: play.summary.id, accountId: play.summary.accountId, version: play.summary.version,
        conflict: latest.current.observedPlay?.conflict ?? false } })
      const synced = await syncEvidence(api, play.summary.id, sent.evidence, before?.evidenceIds ?? [])
      if (started !== epoch.current) return null
      if (play.summary.version < observedPlayVersion(latest.current, api)) {
        setError('The Play changed again while saving. Reload the current saved Play before saving again.')
        return null
      }
      const stored = draftFromSaved(play, synced.evidence)
      const state = savedState(play, stored)
      const next = { ...state, evidenceIds: [...state.evidenceIds, ...synced.pendingDeletes] }
      // Edits made while saving stay; only their evidence learns its server identity.
      // A name the save generated reaches the heading too, unless the owner started typing one meanwhile.
      const current = latest.current.draft
      const named = !sent.title.trim() && !current.title.trim() && play.summary.title ? { title: play.summary.title } : {}
      update({
        saved: next,
        observedPlay: { api, id: play.summary.id, accountId: play.summary.accountId, version: play.summary.version, conflict: false },
        draft: current === sent ? stored : { ...current, ...named, evidence: mergeSavedEvidence(current.evidence, synced.evidence) },
      })
      if (synced.error) setError(`The play was saved, but an image did not sync: ${errorMessage(synced.error, 'The change could not be completed. Try again.')} Save again to retry.`)
      closeDialog(intendedDialog)
      return next
    } catch (cause) {
      if (started !== epoch.current) return null
      actionError(cause, intendedDialog)
      return null
    }
  }

  async function changeStatus(request: StatusRequest) {
    const intendedDialog = currentDialog.current
    const finish = beginWork('status')
    if (!finish) return
    const started = epoch.current
    try {
      let state = latest.current.saved
      if (request.status === 'planned' && planBlocked()) return
      if (request.status === 'planned' && status === 'draft' && (!state || isDirty(latest.current.draft, state))) {
        state = await saveDraft()
        if (!state) return
        if (isDirty(latest.current.draft, state)) { setError('Changes made during saving are still unsaved. Save them before planning.'); return }
      }
      if (!state) return
      setError(null); setDialogError(null)
      const play = await api.changePlayStatus(state.summary.id, state.summary.version, request)
      if (started !== epoch.current) return
      acceptStoredPlay(play)
      closeDialog(intendedDialog)
    } catch (cause) {
      if (started !== epoch.current) return
      actionError(cause, intendedDialog)
    } finally {
      finish()
    }
  }

  /** Complete review, Review again or No review needed, on the version the owner sees (phase 1, 1.2). */
  async function reviewAction(action: ReviewAction) {
    const state = latest.current.saved
    if (!state) return
    const finish = beginWork('status')
    if (!finish) return
    const started = epoch.current
    try {
      setError(null)
      const send = action === 'complete' ? api.completeReview : action === 'reopen' ? api.reopenReview : api.noReviewNeeded
      const play = await send(state.summary.id, state.summary.version)
      if (started !== epoch.current) return
      acceptStoredPlay(play)
    } catch (cause) {
      if (started !== epoch.current) return
      setError(errorMessage(cause, 'The review could not be updated. Try again.'))
    } finally {
      finish()
    }
  }

  /**
   * Declare an Open Play unresolved, or return an Unresolved one to Open (phase 1, 1.3a), on the version the owner sees.
   * Only the owner does this; nothing here reads account flatness or the monitor.
   */
  async function resolution(request: { kind: 'declare'; reason: UnresolvedReason; note: string } | { kind: 'return'; note: string }) {
    const intendedDialog = currentDialog.current
    const state = latest.current.saved
    if (!state) return
    const finish = beginWork('status')
    if (!finish) return
    const started = epoch.current
    try {
      setError(null); setDialogError(null)
      const play = request.kind === 'declare'
        ? await api.declareUnresolved(state.summary.id, state.summary.version, request.reason, request.note)
        : await api.returnToOpen(state.summary.id, state.summary.version, request.note)
      if (started !== epoch.current) return
      acceptStoredPlay(play)
      closeDialog(intendedDialog)
    } catch (cause) {
      if (started !== epoch.current) return
      actionError(cause, intendedDialog)
    } finally {
      finish()
    }
  }

  async function remove() {
    const started = epoch.current
    if (!saved) return
    const finish = beginWork('delete')
    if (!finish) return
    setDialogError(null)
    try {
      await api.deletePlay(saved.summary.id)
      if (started !== epoch.current) return
      setDialog(null)
      update({ ...createPlaysSession(), manual: emptyManualDraft() })
    } catch (cause) {
      if (started !== epoch.current) return
      setDialogError(errorMessage(cause, 'The change could not be completed. Try again.'))
    } finally {
      finish()
    }
  }

  /** `wanted` is checked after loading: a request the shell cleared or superseded meanwhile never replaces the editor. */
  async function open(id: string, restore = false, wanted: () => boolean = () => true): Promise<boolean> {
    const before = latest.current
    const discarding = hasUnsavedPlayWork(before)
    if (discarding) {
      if (!restore) {
        setNavigationError('Keep your current work. Save or explicitly discard it before opening the requested Play.')
        return false
      }
      if (activeWork.current.current || !window.confirm('Discard unsaved Play work and open this Play?')) return false
    }
    const finish = beginWork('open')
    if (!finish) return false
    const started = epoch.current
    openController.current?.abort()
    const controller = new AbortController()
    openController.current = controller
    setError(null)
    try {
      const loaded = await loadSavedPlay(api, id, controller.signal)
      if (started !== epoch.current || controller.signal.aborted || !wanted()) return false
      if (latest.current.draft !== before.draft || latest.current.manual !== before.manual || latest.current.log !== before.log) {
        setNavigationError('Your edits made while loading were kept. Save them before trying the requested Play again.')
        return false
      }
      if (before.saved?.summary.id === id && loaded.saved.summary.version < observedPlayVersion(latest.current, api)) {
        setNavigationError('This response is older than the latest known Play. Retry opening the current saved Play.')
        return false
      }
      // A confirmed discard covers the previous Play's log entry too, so returning to it never resurrects that entry.
      update({ view: 'editor', ...loaded, manual: emptyManualDraft(), ...(discarding ? { log: undefined } : {}), observedPlay: {
        api, id, accountId: loaded.saved.summary.accountId, version: loaded.saved.summary.version, conflict: false,
      } })
      setNavigationError(null)
      return true
    } catch (cause) {
      if (started === epoch.current && !controller.signal.aborted) { setError(errorMessage(cause, 'The change could not be completed. Try again.')); setNavigationError(errorMessage(cause, 'The change could not be completed. Try again.')) }
      return false
    } finally {
      controller.abort()
      finish()
    }
  }

  useEffect(() => {
    if (!requestedPlayId) { requestAttempt.current = null; return }
    if (!visible && !requestedPlayRestore) return
    const generation = requestGeneration.current, key = requestedPlayKey
    const current = () => mounted.current && requestGeneration.current === generation
    if (requestAttempt.current === generation || busy) return
    if (saved?.summary.id === requestedPlayId) {
      update({ view: 'editor' })
      requestAttempt.current = generation
      onRequestedPlayHandled?.(true, key)
      return
    }
    if (unsaved && !requestedPlayRestore) return
    let active = true
    // Claimed when it starts, not when queued: a rerun that cancels the queued start must be able to queue it again.
    queueMicrotask(() => {
      if (!active || requestAttempt.current === generation) return
      requestAttempt.current = generation
      void open(requestedPlayId, requestedPlayRestore, current).then(success => {
        if ((success || requestedPlayRestore) && current()) onRequestedPlayHandled?.(success, key)
      })
    })
    return () => { active = false }
    // open reads the live session and guards edits made during loading.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible, requestedPlayId, requestedPlayRestore, requestedPlayKey, busy, unsaved, saved?.summary.id, onRequestedPlayHandled, api, update])

  const requested = requestedPlayId && requestedPlayId !== saved?.summary.id ? <div className="workspace-alert plays-request" role="status">
    <span>{unsaved ? 'A Play was requested from another screen. Your current work is kept; save it or explicitly discard it before opening.'
      : navigationError ?? 'Opening the requested Play…'}</span>
    <Button variant="outline" size="sm" disabled={busy !== null} onClick={() => { update({ view: 'editor' }); onRequestedPlayHandled?.(undefined, requestedPlayKey) }}>Keep current work</Button>
    {!unsaved && navigationError && <Button variant="outline" size="sm" disabled={busy !== null}
      onClick={() => {
        const generation = requestGeneration.current, key = requestedPlayKey
        const current = () => mounted.current && requestGeneration.current === generation
        void open(requestedPlayId, false, current).then(success => { if (success && current()) onRequestedPlayHandled?.(undefined, key) })
      }}>Retry requested Play</Button>}
  </div> : null

  if (!visible) return null
  const conflict = session.observedPlay?.api === api && session.observedPlay.id === saved?.summary.id &&
    session.observedPlay.accountId === saved.summary.accountId && session.observedPlay.conflict
    ? <div className="workspace-alert" role="alert">The saved Play changed elsewhere. Your edits and their original version were kept. Copy any edits you want to keep, then discard the local copy from All plays and reopen the latest saved Play before saving.</div> : null
  if (session.view === 'list') {
    return <>{requested}{conflict}{paper && !paperArming && <p className="workspace-scope-note" role="status">Paper evaluation is disabled or settings are unavailable. History and editing remain accessible; enable Paper trading in Settings to arm or resume.</p>}{storedReadError && <p className="error" role="status">{storedReadError}</p>}<PlayList reloadGeneration={reloadGeneration} api={api} accounts={accounts} busy={busy !== null} list={listState} onList={setListState}
      incidents={playIncidents}
      unsaved={unsaved ? draft.title.trim() || 'Untitled play' : null}
      onOpen={id => { void open(id) }}
      onNew={() => { if (activeWork.current.current) return; setError(null); update(startNewPlay(session)) }}
      onContinue={() => update({ view: 'editor' })}
      onDiscard={() => { if (activeWork.current.current) return; latest.current = createPlaysSession(); onSession(latest.current) }} /></>
  }

  const saving = busy === 'save'
  const locked = status !== 'draft'
  const readOnly = ended.includes(status)
  const needsReason = !!saved && locked && !readOnly && changedPlan
  const statusBlocked = dirty ? 'Save your changes first' : undefined
  const pending = busy !== null

  // Plan it always shows the checklist first; it never blocks, and what the owner saw is recorded with revision 1.
  const requestPlan = () => {
    if (planBlocked()) return
    if (preflight) { setDialogError(null); setDialog('preflight') } else void changeStatus({ status: 'planned' })
  }

  // Imported executions on the Play's own linked orders; only they make an Open Play declarable as unresolved (1.3a).
  const importedFills = !manual && tracking.execution?.playId === saved?.summary.id && !!tracking.execution?.links.some(link => link.fills.length > 0)
  // The same buttons stay in the same places whether or not the play is saved; unavailable ones are disabled.
  const statusAction = status === 'draft'
    ? <Button size="sm" className="play-primary" disabled={pending || !draft.accountId || !draft.instrument || !paperArming}
      title={!paperArming ? 'Enable Paper trading in Settings before arming a play' : !draft.instrument ? 'Choose an instrument and price an entry to plan' : 'Commit to this plan. Later changes become revisions.'}
      onClick={requestPlan}><Play size={14} />Plan it</Button>
    : status === 'planned' ? <Button size="sm" variant="outline" disabled={pending || dirty} title={statusBlocked ?? (paper ? 'Pause simulation; unchecked time remains unscored' : 'Orders withdrawn, idea still valid')}
      onClick={() => { if (paper) { setDialogError(null); setDialog('pause') } else { void changeStatus({ status: 'paused' }) } }}><Pause size={14} />Pause</Button>
    : status === 'paused' ? <Button size="sm" className="play-primary" disabled={pending || dirty || !paperArming} title={statusBlocked}
      onClick={() => { void changeStatus({ status: 'planned' }) }}><Play size={14} />Resume</Button>
    // An Open venue Play with imported executions can be declared unresolved; an Unresolved one can return to Open (1.3a).
    : status === 'open' && importedFills ? <Button size="sm" variant="outline" disabled={pending || dirty}
      title={statusBlocked ?? 'Its executions can never settle it'} onClick={() => { setDialogError(null); setDialog('unresolved') }}>
      <CircleHelp size={14} />Declare unresolved</Button>
    : status === 'unresolved' ? <Button size="sm" variant="outline" disabled={pending || dirty} title={statusBlocked}
      onClick={() => { setDialogError(null); setDialog('return-open') }}><RotateCcw size={14} />Return to Open</Button>
    : null
  const cancellable = status === 'planned' || status === 'paused'
  const requestSave = () => { if (planBlocked()) return; if (needsReason) { setDialogError(null); setDialog('revision') } else void save() }
  const actions = <div className="play-actions" role="group" aria-label="Play actions">
    <Button variant="outline" size="sm" disabled={!saved} title={saved ? 'Plan revisions and status changes' : 'Save the play first'}
      onClick={() => setDialog('history')}><History size={14} />History</Button>
    {saved && typeof api.playBundle === 'function' && <PlayBundleButton api={api} playId={saved.summary.id} dirty={dirty} />}
    {/* A planned Play's plan as a new Draft (1.6c); a retrospective Play has no plan to copy. */}
    {saved && !retrospective && <Button variant="outline" size="sm" disabled={pending || hasUnsavedPlayWork(session)}
      title={hasUnsavedPlayWork(session) ? 'Save or discard your changes first' : 'Open a copy of this plan as a new Draft'}
      onClick={() => {
        const current = latest.current
        if (activeWork.current.current || !current.saved || hasUnsavedPlayWork(current)) return
        setError(null)
        update(draftSession(duplicateDraft(current.saved)))
      }}><Copy size={14} />Duplicate</Button>}
    <Button size="sm" variant="outline" className="play-save" disabled={!dirty || pending || !draft.accountId}
      title={!draft.accountId ? 'Choose an account to save' : undefined}
      onClick={requestSave}>
      <Save size={14} />{saving ? 'Saving…' : !dirty && saved ? 'Saved' : 'Save'}</Button>
    {statusAction}
    {cancellable
      ? <Button size="sm" variant="outline" className="danger-action" disabled={pending || dirty}
        title={statusBlocked} onClick={() => { setDialogError(null); setDialog('cancel') }}><X size={14} />Cancel play</Button>
      : status === 'draft' && <Button size="sm" variant="outline" className="danger-action" disabled={pending || !saved}
        title={saved ? 'Delete this draft' : 'Nothing saved yet'} onClick={() => { setDialogError(null); setDialog('delete') }}><Trash2 size={14} />Delete</Button>}
  </div>

  // The same Save inside the journal, so a review is not saved from the top of the page; shown only with something to save.
  const journalSave = dirty || saving ? <Button size="sm" variant="outline" className="play-save" disabled={!dirty || pending || !draft.accountId}
    title={!draft.accountId ? 'Choose an account to save' : undefined} onClick={requestSave}>
    <Save size={14} />{saving ? 'Saving play…' : 'Save play'}</Button> : undefined
  // The outcome beside the review: a venue Play's two bases or a manual Play's ledger, with R and time held from the server.
  const shownExecution = tracking.execution?.playId === saved?.summary.id ? tracking.execution : null
  // Like the outcome, the header's post-hoc badge waits for a fresh read rather than keeping an earlier one's flag.
  const playReceipt = manual ? manualLedger && manualLedger !== 'unknown' ? manualLedger.receipt ?? null : null
    : tracking.stale ? null : shownExecution?.receipt ?? null
  // Figures kept from an earlier read are not shown as current: after a failed or pending reread the receipt waits for it.
  const staleOutcome = tracking.stale ? tracking.error
    ? <p className="error" role="alert">The outcome could not be read again. {tracking.error}</p>
    : <p className="muted" role="status">Reading the outcome…</p> : undefined
  const reviewReceipt = manual
    ? manualLedger && manualLedger !== 'unknown' && manualLedger.receipt
      ? <ReviewReceipt receipt={manualLedger.receipt} result={{ kind: 'manual', totals: manualLedger.totals, fees: manualLedger }}
        status={manualLedger.play.summary.status} /> : undefined
    : shownExecution?.receipt ? staleOutcome ?? <ReviewReceipt receipt={shownExecution.receipt} result={{ kind: 'venue', totals: shownExecution.totals }} status={shownExecution.status} />
      : undefined
  const retrospectiveReceipt = retrospective && shownExecution ? staleOutcome ?? <RetrospectiveReceipt execution={shownExecution} direction={draft.direction} /> : undefined

  // The status badge, a revision pill and the buttons say what the owner can do; no banner restates them.
  const revision = saved && locked && !retrospective && saved.summary.planRevision > 1 ? saved.summary.planRevision : null

  return <>{requested}{conflict}{paper && !paperArming && <p className="workspace-scope-note" role="status">Paper evaluation is disabled or settings are unavailable. History and editing remain accessible; enable Paper trading in Settings to arm or resume.</p>}{storedReadError && <p className="error" role="status">{storedReadError}</p>}
    <Button variant="ghost" size="sm" className="plays-back" onClick={() => update({ view: 'list' })}><ArrowLeft size={14} />All plays</Button>
    <StoredPlanTextContext.Provider value={storedText}>
    <PlayWorkspace accounts={accounts} portfolios={portfolios} api={api} draft={draft} onChange={next => update({ draft: next })}
      {...(onReload ? { onReload } : {})} loading={loading} status={status} actions={actions} revision={revision} lockInstrument={locked}
      lockDirection={status === 'open' || filled} readOnly={readOnly} retrospective={retrospective}
      sizing={sizing.sizing} sizingStatus={sizing.status} onRetrySizing={sizing.retry} onRiskChange={sizing.updateRisk}
      filledEntries={filledEntries} fills={fills} onUnits={onUnits} onPriceStep={setPriceStep}
      incidents={saved ? playIncidents.get(saved.summary.id) : undefined} stopEvidence={manual ? undefined : stopEvidence}
      evidenceImages={evidenceImages} reviewReceipt={reviewReceipt} journalSave={journalSave} retrospectiveReceipt={retrospectiveReceipt}
      onPreflight={setPreflight} nextActions={nextActions?.api === api ? nextActions.actions : null} onOpenPlay={id => { void open(id) }}
      log={saved ? <PlayLog key={saved.summary.id} api={api} playId={saved.summary.id} draft={session.log}
        onDraft={change => {
          // A living page publishes its own session; once it has gone, the owner applies the change to its current one.
          // An outdated page session is never published.
          if (mounted.current && currentApi.current === api) update({ log: change(latest.current.log) })
          else onLogChange?.(change)
        }} /> : undefined}
      mistakeTags={offersMistakeTags && mistakeTags?.api === api ? mistakeTags.tags : undefined}
      chartFills={chartFills}
      playId={saved?.summary.id ?? null} openRiskKey={[saved?.summary.version ?? 0, reloadGeneration]}
      reviewCompletion={saved ? <ReviewCompletionBar summary={saved.summary} dirty={dirty} busy={pending}
        filled={(playReceipt?.fillCount ?? 0) > 0} onAction={action => { void reviewAction(action) }} /> : undefined}
      plannedAfterExecution={playReceipt?.plannedAfterExecution === true}
      execution={paper ? saved ? <PaperExecutionPanel key={saved.summary.id} api={api} playId={saved.summary.id} reloadGeneration={reloadGeneration + saved.summary.version} onState={onPaperState} /> : <p className="workspace-scope-note">Save this Paper play to see simulated execution progress. Planning arms evaluation; a draft never monitors.</p> : manual ? <ManualExecutionPanel key={saved?.summary.id ?? 'unsaved'} api={api} saved={saved} entries={draft.entries} zone={zone}
        enabled={account?.isEnabled !== false} dirty={dirty} form={session.manual ?? emptyManualDraft()}
        working={busy !== null}
        observedVersion={observedPlayVersion(session, api)}
        onChange={form => { if (mounted.current && currentApi.current === api && latest.current.saved?.summary.id === saved?.summary.id) update({ manual: form }) }}
        onStored={acceptStoredPlay} onBegin={() => latest.current.saved?.summary.id === saved?.summary.id ? beginWork('manual') : null} />
        : saved && status !== 'draft' ? <ExecutionPanel execution={tracking.execution} error={tracking.error} busy={tracking.busy} stale={tracking.stale}
        entries={draft.entries} direction={draft.direction} origin={saved.summary.origin} monitor={monitorRun.monitor} monitorBusy={monitorRun.busy} monitorError={monitorRun.error} retryAtUtc={monitorRun.retryAtUtc} monitorUnavailable={monitorRun.statusUnavailable}
        currentBookRead={currentBookRead}
        {...(onManageAccount && account ? { onManageCredential: () => onManageAccount(account.id) } : {})}
        onDismiss={tracking.dismiss} onCheck={() => { void monitorRun.check() }} onLink={request => { void tracking.link(request) }}
        onUnlink={linkId => { void tracking.unlink(linkId) }} onReject={orderId => { void tracking.reject(orderId) }}
        // A rebind writes a plan revision, so it waits for unsaved edits and other Play work, and its answer is the saved Play.
        version={saved.summary.version} leverage={leverageOf(draft.leverage)} priceStep={priceStep} rebindBlocked={dirty ? 'Save or discard your plan changes first.' : busy !== null ? 'Wait for the current change to finish.' : undefined}
        onRebind={(linkId, request) => tracking.rebind(linkId, request, acceptStoredPlay)} /> : undefined} />
    </StoredPlanTextContext.Provider>
    {dialog === 'revision' && saved && <RevisionDialog revision={saved.summary.planRevision} pending={saving} error={dialogError}
      onSave={reason => { void save(reason) }} onClose={() => setDialog(null)} />}
    {dialog === 'pause' && <PauseDialog pending={busy === 'status'} error={dialogError}
      onPause={() => { void changeStatus({ status: 'paused' }) }} onClose={() => setDialog(null)} />}
    {dialog === 'cancel' && <CancelDialog paper={paper} pending={busy === 'status'} error={dialogError}
      onCancel={(reason, note) => { void changeStatus({ status: 'cancelled', reason, ...(note ? { note } : {}) }) }} onClose={() => setDialog(null)} />}
    {dialog === 'preflight' && preflight && <PreflightDialog checks={preflight} pending={busy === 'status'} error={dialogError}
      onPlan={() => { void changeStatus({ status: 'planned', preflight }) }} onClose={() => setDialog(null)} />}
    {dialog === 'unresolved' && <UnresolvedDialog pending={busy === 'status'} error={dialogError}
      onDeclare={(reason, note) => { void resolution({ kind: 'declare', reason, note }) }} onClose={() => setDialog(null)} />}
    {dialog === 'return-open' && <ReturnToOpenDialog pending={busy === 'status'} error={dialogError}
      onReturn={note => { void resolution({ kind: 'return', note }) }} onClose={() => setDialog(null)} />}
    {dialog === 'delete' && <DeleteDialog pending={busy === 'delete'} error={dialogError} onDelete={() => { void remove() }} onClose={() => setDialog(null)} />}
    {dialog === 'history' && saved && <HistoryDialog api={api} playId={saved.summary.id} onClose={() => setDialog(null)} />}
  </>
}

const count = new Intl.NumberFormat('en-US')

function PlayList({ api, accounts, busy, reloadGeneration, unsaved, list, onList, onOpen, onNew, onContinue, onDiscard, incidents }: {
  api: WorkspaceApi
  accounts: BrokerAccount[]
  /** Open monitor incidents per Play, from the shell's monitor summary. */
  incidents: ReadonlyMap<string, IncidentCounts>
  busy: boolean
  reloadGeneration: number
  /** Title of the play with unsaved changes, if any. */
  unsaved: string | null
  list: PlaysListState
  onList: (list: PlaysListState) => void
  onOpen: (id: string) => void
  onNew: () => void
  onContinue: () => void
  onDiscard: () => void
}) {
  const [generation, setGeneration] = useState(0)
  // The first mount loads the list, even if a retained save is running. Subsequent work
  // reloads only on completion, including lost responses that leave the version unchanged.
  const [previousBusy, setPreviousBusy] = useState(busy)
  if (previousBusy !== busy) {
    setPreviousBusy(busy)
    if (!busy) setGeneration(current => current + 1)
  }
  // Typing waits briefly before it becomes the search; a search set elsewhere replaces the box.
  const [search, setSearch] = useState(list.q)
  const [searched, setSearched] = useState(list.q)
  if (searched !== list.q) {
    setSearched(list.q)
    if (search.trim() !== list.q) setSearch(list.q)
  }
  const latestList = useRef(list)
  useEffect(() => { latestList.current = list })
  useEffect(() => {
    const q = search.trim()
    if (q === latestList.current.q) return
    const timer = setTimeout(() => onList({ ...latestList.current, q, page: 1 }), playSearchDelay)
    return () => clearTimeout(timer)
  }, [search, onList])

  const paged = pagedPlayViews.includes(list.view)
  const query: PlayListQuery = { view: list.view, ...(list.q ? { q: list.q } : {}), ...(paged ? { page: list.page, pageSize: list.pageSize } : {}) }
  // Accepted stored-state reloads (owner events) reread the page like a finished owner action.
  const rowsKey = JSON.stringify([query, generation, reloadGeneration])
  // `requested` produced the rows; `key` is the request they answer, which differs when the server clamped the page.
  const [rows, setRows] = useState<{ api: WorkspaceApi; key: string; requested: string; page?: PlayListPage; error?: string } | null>(null)
  const adopted = useRef<string | null>(null)
  useEffect(() => {
    const skip = adopted.current === rowsKey
    adopted.current = null
    if (skip) return
    const controller = new AbortController()
    const [request, at, reloaded] = JSON.parse(rowsKey) as [PlayListQuery, number, number]
    api.plays(request, controller.signal).then(page => {
      if (controller.signal.aborted) return
      const answered = request.page === undefined || page.page === request.page ? rowsKey : JSON.stringify([{ ...request, page: page.page }, at, reloaded])
      setRows({ api, key: answered, requested: rowsKey, page })
    }).catch(cause => { if (!controller.signal.aborted) setRows({ api, key: rowsKey, requested: rowsKey, error: errorMessage(cause, 'The change could not be completed. Try again.') }) })
    return () => controller.abort()
  }, [api, rowsKey])
  // A page past the end comes back clamped: keep the page actually shown, so later growth does not jump back.
  // Only the response to the current request may write, and the adopted page is not requested again.
  useEffect(() => {
    if (rows === null || rows.api !== api || rows.requested !== rowsKey || rows.key === rowsKey || !rows.page) return
    adopted.current = rows.key
    onList({ ...list, page: rows.page.page })
  }, [api, list, onList, rows, rowsKey])
  const countsKey = JSON.stringify([list.q, generation, reloadGeneration])
  const [counts, setCounts] = useState<{ api: WorkspaceApi; key: string; counts: PlayCounts | null } | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    const q = (JSON.parse(countsKey) as [string, number, number])[0]
    api.playCounts(q ? { q } : {}, controller.signal).then(next => { if (!controller.signal.aborted) setCounts({ api, key: countsKey, counts: next }) })
      .catch(() => { if (!controller.signal.aborted) setCounts({ api, key: countsKey, counts: null }) })
    return () => controller.abort()
  }, [api, countsKey])

  const pending = rows === null || rows.api !== api || rows.key !== rowsKey && rows.requested !== rowsKey
  const previous = rows?.api === api ? rows.page ?? null : null
  const shown = pending ? previous : rows.page ?? null
  const error = pending ? null : rows.error ?? null
  const skeleton = useDelayedFlag(pending && previous === null)
  const tabCounts = counts?.api === api && counts.key === countsKey ? counts.counts : null
  const top = useRef<HTMLDivElement | null>(null)
  const accountName = (id: string) => accounts.find(account => account.id === id)?.name ?? 'Unknown account'

  return <section className="plays-page plays-list-page" aria-label="Saved plays">
    <div className="plays-draft-heading">
      <div className="page-heading-copy"><h1>Plays</h1></div>
      <div className="plays-heading-actions"><Button onClick={onNew} disabled={!!unsaved || busy}><Plus size={15} />New play</Button></div>
    </div>
    {unsaved && <div className="workspace-alert plays-unsaved" role="status"><span>Unsaved changes in <strong>{unsaved}</strong>. Save or discard them before opening another play.</span>
      <Button variant="outline" size="sm" onClick={onContinue}>Continue editing</Button>
      <Button variant="ghost" size="sm" className="danger-action" disabled={busy} onClick={onDiscard}>Discard changes</Button></div>}
    <Tabs value={list.view} onValueChange={value => onList({ ...list, view: value as PlayView, page: 1 })} className="plays-list-tabs">
      <div className="plays-list-toolbar">
        <TabsList aria-label="Show plays">
          {playViewTabs.map(tab => <TabsTrigger key={tab.view} value={tab.view} title={tab.description}>
            {tab.label}{' '}<span className="plays-tab-count">{tabCounts ? count.format(tabCounts[tab.view]) : '—'}</span>
          </TabsTrigger>)}
        </TabsList>
        <label className="plays-search"><Search size={14} aria-hidden="true" />
          <input type="search" value={search} maxLength={playSearchLength} placeholder="Title or instrument" aria-label="Search plays"
            autoComplete="off" onChange={event => setSearch(event.target.value)} /></label>
      </div>
      <TabsContent value={list.view} ref={top}>
        {error && <div className="workspace-alert" role="alert"><span>{error}</span>
          <Button variant="ghost" size="sm" onClick={() => { setRows(null); setGeneration(current => current + 1) }}>Try again</Button></div>}
        {!error && shown === null && (skeleton ? <SkeletonStatus label="Loading plays…"><ul className="plays-list">
          {[0, 1, 2, 3].map(row => <li key={row}><span className="plays-list-item plays-list-skeleton">
            <span className="plays-list-title"><Skeleton className="skeleton-title" /><Skeleton className="skeleton-identity" /></span>
            <Skeleton className="skeleton-chip" /><Skeleton className="skeleton-chip" /><Skeleton className="skeleton-meta" />
          </span></li>)}
        </ul></SkeletonStatus> : <div className="plays-list-pending" aria-busy="true" />)}
        {!error && shown && <>
          {!paged && shown.totalCount > shown.plays.length && <p className="workspace-scope-note plays-cap" role="status">
            Showing the {count.format(shown.plays.length)} most recently updated of {count.format(shown.totalCount)}. Search to narrow the list.</p>}
          {shown.plays.length ? <ul className={pending ? 'plays-list list-refetching' : 'plays-list'} aria-busy={pending || undefined}>
            {shown.plays.map(play => <li key={play.id}>
              <button type="button" className="plays-list-item" disabled={!!unsaved || busy} onClick={() => onOpen(play.id)} aria-label={`Open ${play.title || 'Untitled play'}`}
                aria-describedby={incidents.has(play.id) ? `plays-list-identity-${play.id} plays-list-incidents-${play.id}` : `plays-list-identity-${play.id}`}>
                <span className="plays-list-title"><span className="plays-list-heading"><strong>{play.title || 'Untitled play'}</strong>{play.origin === 'retrospective' && <span className="badge play-retrospective">Retrospective</span>}
                  <IncidentPill id={`plays-list-incidents-${play.id}`} counts={incidents.get(play.id)} /></span>
                  <PlayIdentity id={`plays-list-identity-${play.id}`} instrument={play.instrument} source={play.instrumentSource} venueId={play.venueId} accountName={accountName(play.accountId)} /></span>
                <span className={`direction-chip ${play.direction}`}>{play.direction === 'long' ? 'Long' : 'Short'}</span>
                <span className={`badge play-status-${play.status}`}>{statusLabels[play.status]}</span>
                <small className="plays-list-meta">{ended.includes(play.status) && (isReviewed(play) ? 'Reviewed · ' : 'Pending review · ')}{play.origin !== 'retrospective' && play.planRevision > 1 ? `${play.planRevision} revisions · ` : ''}Updated <Time at={play.updatedAtUtc} style="date" /></small>
              </button>
            </li>)}
          </ul> : <div className={pending ? 'plays-list-empty list-refetching' : 'plays-list-empty'} aria-busy={pending || undefined}>
            <p>{list.q ? `No plays match “${list.q}”.` : tabCounts?.all === 0 ? 'No saved plays yet.' : 'No plays in this view.'}</p></div>}
          {paged && <Pager label="Plays pages" page={shown.page} pageSize={shown.pageSize} totalCount={shown.totalCount} disabled={pending}
            onPage={page => { onList({ ...list, page }); top.current?.scrollIntoView?.({ block: 'start' }) }}
            onPageSize={size => { if (isPageSize(size)) onList({ ...list, page: 1, pageSize: size }) }} />}
        </>}
      </TabsContent>
    </Tabs>
  </section>
}
