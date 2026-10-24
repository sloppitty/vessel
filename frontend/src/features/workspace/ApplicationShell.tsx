import type { TradingMode } from '@/api/paper'
import { createRef, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type RefObject } from 'react'
import { Tooltip } from 'radix-ui'
import type { BrokerAccount, Overview, WorkspaceApi } from '@/api/workspace'
import { errorMessage } from '@/api/errors'
import { onApiEvent } from '@/api/events'
import type { SystemInfo } from '@/api/system'
import { Button } from '@/components/ui/button'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { useNotifications } from '@/components/notifications/notifications'
import { createPlaysSession, hasUnsavedPlayWork, startNewPlay, type PlaysSession } from '@/features/plays/saved'
import type { PlayLogChange } from '@/features/plays/log-draft'
import { initialPlaysList } from '@/features/plays/plays-list'
import type { PlayView } from '@/api/plays'
import { useMonitorEvents } from './useMonitorEvents'
import { countIncidents, incidentsBeyondCap, monitorIncidents } from '@/features/monitor/incidents'
import { IncidentCount } from '@/features/monitor/IncidentBadge'
import { useInAppNotifications } from '@/features/notifications/useInAppNotifications'
import { initialNotificationList } from '@/features/notifications/notification-list'
import { NotificationBadge } from '@/features/notifications/NotificationBadge'
import { notificationsLabel, useCountAnnouncement } from '@/features/notifications/unread'
import type { NotificationLink } from '@/api/notifications'
import { AccountDetail } from './AccountDetail'
import { ActivityOperations } from './ActivityOperations'
import { CreateAccountDialog, CreatePortfolioDialog } from './CreateDialogs'
import { ManageAccountDialog, ManagePortfolioDialog } from './ManageDialogs'
import type { OverviewQuery } from '@/api/overview-summary'
import { resolveOverviewScope } from './overview-scope'
import { AccountsPanel } from './AccountsPanel'
import { emptyAccountFilters } from './account-filters'
import { initialActivityState } from './activity-filters'
import { OverviewActions } from './OverviewActions'
import { VenuesProvider, useVenues } from '@/api/venues'
import {
  Activity, BarChart3, Bell, CircleHelp, Compass, Folder, LayoutDashboard,
  LogOut, Menu, PanelLeftClose, PanelLeftOpen, Plus, RefreshCw, Settings2, Wallet, BookOpen, X,
} from 'lucide-react'
import { PageErrorBoundary } from '@/components/PageErrorBoundary'
import { OverviewPage } from './OverviewPage'
import { PortfoliosPage } from './PortfoliosPage'
import { useHashPage, type Page } from './useHashPage'
import { useAccountRefresh } from './useAccountRefresh'
import { lazyPage } from './lazy-page'
import type { ManagementEvent } from './management-events'
import type { ShellIdentity } from '@/features/identity/types'
import { AccountMenu } from '@/features/identity/AccountMenu'
import type { SettingsTab } from './SettingsPage'
import { ShellBreadcrumb } from './ShellBreadcrumb'
import { ShellNotices, type ShellNotice } from './ShellNotices'
import { ShellMonogram, TradingModeConfirmDialog, TradingModeHeaderMark, TradingModeRailSwitch, TradingModeSubtitle } from './TradingModeSwitch'
import { otherTradingMode, tradingModeNames, type TradingModeArrival } from './trading-mode'
import { locationKey, playLocation, settingsLocation, visitLocations, type ShellLocation } from './shell-history'
import './application-shell.css'
import './skip-content.css'
import { Time } from '@/components/ui/time'

const PlaysPage = lazyPage(async () => ({ default: (await import('@/features/plays/PlaysPage')).PlaysPage }))
const ReviewPage = lazyPage(async () => ({ default: (await import('@/features/review/ReviewPage')).ReviewPage }))
const SettingsPage = lazyPage(async () => ({ default: (await import('./SettingsPage')).SettingsPage }))
const NotificationsPage = lazyPage(async () => ({ default: (await import('@/features/notifications/NotificationsPage')).NotificationsPage }))

function PageLoading() {
  return <div className="workspace-loading" role="status">Loading your workspace…</div>
}

const pages = [
  { id: 'overview', label: 'Overview', icon: LayoutDashboard },
  { id: 'plays', label: 'Plays', icon: BookOpen },
  { id: 'review', label: 'Review', icon: BarChart3 },
  { id: 'portfolios', label: 'Portfolios', icon: Folder },
  { id: 'accounts', label: 'Accounts', icon: Wallet },
  { id: 'activity', label: 'Activity', icon: Activity },
  { id: 'notifications', label: 'Notifications', icon: Bell },
  { id: 'settings', label: 'Settings', icon: Settings2 },
] as const
const pageLocation = (page: Page): ShellLocation => ({ kind: 'page', page, label: pages.find(item => item.id === page)!.label })
const paperNotice: ShellNotice = {
  id: 'paper', title: 'Paper mode', tone: 'paper',
  message: 'Simulated capital and executions only. Real accounts and results are excluded. Zero fees and slippage; candle touches can be ambiguous.',
}
/** `identity` is present in Wallet mode only; Token mode keeps the owner card that disconnects at once. */
type ShellProps = { system: SystemInfo; disconnect: () => void; api: WorkspaceApi; identity?: ShellIdentity }

/** Focuses an element of a page that may still be loading, for up to a second. */
function focusWhenShown(id: string, tries = 20) {
  window.setTimeout(() => {
    const element = document.getElementById(id)
    if (element) element.focus()
    else if (tries > 1) focusWhenShown(id, tries - 1)
  }, 50)
}

/** The log line above each page title: today's date, with a greeting on the Overview. */
function logLine(page: Page, owner: string, now = new Date()) {
  const date = new Intl.DateTimeFormat(undefined, { weekday: 'long', day: 'numeric', month: 'long' }).format(now)
  if (page !== 'overview') return date
  const hour = now.getHours()
  const greeting = hour >= 5 && hour < 12 ? 'Good morning' : hour >= 12 && hour < 18 ? 'Good afternoon' : 'Good evening'
  const name = owner.trim().split(/\s+/)[0]
  return `${greeting}${name ? `, ${name}` : ''} · ${date}`
}

export function ApplicationShell(props: ShellProps) {
  return <Tooltip.Provider delayDuration={250} skipDelayDuration={400}><NotificationProvider><TradingWorkspace {...props} /></NotificationProvider></Tooltip.Provider>
}

type ModeProps = {
  tradingMode: TradingMode; paperAvailable: boolean; onTradingModeChange: (mode: TradingMode) => void; onPaperSettingsChange: () => void
  /** Kept above the per-mode shell so a switch keeps the rail and its control. */
  navigationCollapsed: boolean; onNavigationCollapsedChange: (collapsed: boolean) => void
  modeArrival: TradingModeArrival | null; onModeArrivalSeen: () => void
}

/** Desktop widths, where the collapsed rail applies; narrower screens use the off-canvas sheet. */
function useWideNavigation() {
  const [wide, setWide] = useState(() => window.innerWidth > 800)
  useEffect(() => {
    const resize = () => setWide(window.innerWidth > 800)
    window.addEventListener('resize', resize)
    return () => window.removeEventListener('resize', resize)
  }, [])
  return wide
}

function TradingWorkspace(props: ShellProps) {
  const [mode, setMode] = useState<TradingMode>('real')
  const [paperApi, setPaperApi] = useState<WorkspaceApi | null>(null)
  const [settingsGeneration, setSettingsGeneration] = useState(0)
  useEffect(() => {
    if (!props.api.paperSettings || !props.api.forTradingMode) return
    const controller = new AbortController()
    props.api.paperSettings(controller.signal).then(() => { if (!controller.signal.aborted) setPaperApi(props.api) })
      .catch(() => { /* Old/unsupported APIs stay real-only. Settings shows its own actionable error. */ })
    return () => controller.abort()
  }, [props.api, settingsGeneration])
  const paperClient = useMemo(() => {
    try { const client = props.api.forTradingMode?.('paper'); return client?.tradingMode === 'paper' ? client : null }
    catch { return null }
  }, [props.api])
  const paperAvailable = paperApi === props.api && paperClient !== null && props.system.venues.some(venue => venue.capabilities.paperExecution)
  const tradingMode = paperAvailable ? mode : 'real'
  const api = tradingMode === 'paper' ? paperClient! : props.api
  const [navigationCollapsed, setNavigationCollapsed] = useState(false)
  // The shell is keyed by mode, so a switch remounts it: what it needs afterwards lives here.
  const [modeArrival, setModeArrival] = useState<TradingModeArrival | null>(null)
  const [modeAnnouncement, setModeAnnouncement] = useState('')
  const changeTradingMode = useCallback((next: TradingMode) => {
    setModeArrival({ from: tradingMode, to: next, focusControl: true })
    setModeAnnouncement(tradingModeNames[next])
    setMode(next)
  }, [tradingMode])
  const modeArrivalSeen = useCallback(() => setModeArrival(null), [])
  // A paper account shares public chart/catalogue support only, never real account actions or streaming.
  const venues = useMemo(() => tradingMode === 'real' ? props.system.venues : props.system.venues.map(venue => ({ ...venue,
    tradeUrlTemplate: null, capabilities: { ...venue.capabilities, sync: false, orders: false, stablecoinWallet: false,
      accountDiscovery: false, readOnlyCredential: false, manualExecutions: false, marketContext: false, stream: false } })), [props.system.venues, tradingMode])
  return <VenuesProvider venues={venues}><Shell key={tradingMode} {...props} api={api} tradingMode={tradingMode} paperAvailable={paperAvailable}
    onTradingModeChange={changeTradingMode} onPaperSettingsChange={() => setSettingsGeneration(value => value + 1)}
    navigationCollapsed={navigationCollapsed} onNavigationCollapsedChange={setNavigationCollapsed} modeArrival={modeArrival} onModeArrivalSeen={modeArrivalSeen} />
    {/* Outlives the remount, so the new mode is announced. */}
    {paperAvailable && <span className="sr-only" role="status" aria-live="polite">{modeAnnouncement}</span>}</VenuesProvider>
}

function Shell({ system, disconnect, api, identity, tradingMode, paperAvailable, onTradingModeChange, onPaperSettingsChange,
  navigationCollapsed, onNavigationCollapsedChange, modeArrival, onModeArrivalSeen }: ShellProps & ModeProps) {
  const { notify } = useNotifications()
  const venues = useVenues()
  const [menuOpen, setMenuOpen] = useState(false)
  const wide = useWideNavigation()
  const rail = wide && navigationCollapsed
  // The trading mode control: the brand subtitle, or its rail form. A switch asks first while Play work could be lost.
  const modeControl = useRef<HTMLButtonElement>(null)
  const switchFocus = useRef<HTMLElement | null>(null)
  const [pendingSwitch, setPendingSwitch] = useState<{ target: TradingMode; api: WorkspaceApi; work: RefObject<symbol | null>; control: HTMLButtonElement } | null>(null)
  // Read once: this shell was mounted by a switch made in this browser session, not by a load or reload.
  const [arrival] = useState(() => modeArrival?.to === tradingMode ? modeArrival : null)
  const arrivedFrom = arrival?.from ?? null
  // Runs once (both dependencies are stable): later layout changes never move focus.
  useEffect(() => {
    if (!arrival) return
    onModeArrivalSeen()
    // Focus follows a switch made from the control: the new control, or the menu button because the sheet has closed.
    if (arrival.focusControl) (window.innerWidth > 800 ? modeControl.current : document.getElementById('workspace-menu'))?.focus({ preventScroll: true })
  }, [arrival, onModeArrivalSeen])
  const [selectedAccountId, setSelectedAccountId] = useState<string | null>(null)
  const [trail, setTrail] = useState<ShellLocation[]>(() => {
    const initial = pages.find(item => `#${item.id}` === window.location.hash)?.id ?? 'overview'
    return [pageLocation(initial), ...(initial === 'settings' && identity ? [settingsLocation('appearance')] : [])]
  })
  const [playsSession, setPlaysSession] = useState(createPlaysSession)
  const [settingsTab, setSettingsTab] = useState<SettingsTab>('appearance')
  const [requestedPlay, setRequestedPlay] = useState<{ api: WorkspaceApi; id: string; origin: ShellLocation[]; restore: boolean } | null>(null)
  const onHashNavigation = useCallback((next: Page) => {
    setMenuOpen(false)
    if (next !== 'accounts') setSelectedAccountId(null)
  }, [])
  const { page, navigate: navigateHash } = useHashPage(onHashNavigation, next => {
    setSelectedAccountId(null)
    setRequestedPlay(null)
    setTrail(history => {
      const parent = history.some(location => location.kind === 'page' && location.page === next)
        ? visitLocations(history, pageLocation(next)) : [pageLocation(next)]
      if (next === 'plays' && playsSession.view === 'editor') return visitLocations(parent, playLocation(playsSession))
      return next === 'settings' && identity ? visitLocations(parent, settingsLocation(settingsTab)) : parent
    })
  })
  const [playsList, setPlaysList] = useState(initialPlaysList)
  const [playsLifetime, setPlaysLifetime] = useState(() => ({ api, visited: page === 'plays', generation: 0, work: createRef<symbol>() }))
  // Shell navigation hides the Plays UI without destroying the owner of an in-flight mutation.
  // A new API identity starts a new private session and invalidates all old callbacks.
  if (playsLifetime.api !== api) {
    setPlaysLifetime({ api, visited: page === 'plays', generation: playsLifetime.generation + 1, work: createRef<symbol>() })
    setPlaysSession(createPlaysSession())
    setPlaysList(initialPlaysList)
    setTrail([pageLocation(page)])
  } else if (page === 'plays' && !playsLifetime.visited) {
    setPlaysLifetime({ ...playsLifetime, visited: true })
  }
  const [disconnectedApi, setDisconnectedApi] = useState<WorkspaceApi | null>(null)
  const exitState = useRef<{ api: WorkspaceApi; session: PlaysSession; work: typeof playsLifetime.work; page: Page; playRequested: boolean } | null>(null)
  useLayoutEffect(() => {
    if (disconnectedApi === api) return
    const current = { api, session: playsSession, work: playsLifetime.work, page, playRequested: requestedPlay !== null }
    exitState.current = current
    return () => { if (exitState.current === current) exitState.current = null }
  }, [api, disconnectedApi, playsSession, playsLifetime.work, page, requestedPlay])
  // A forced sign-out drops the exit state first, so the reload never asks about unsaved work.
  const registerExitState = identity?.registerExitState
  useLayoutEffect(() => registerExitState?.(() => { exitState.current = null; setDisconnectedApi(api) }), [api, registerExitState])
  useEffect(() => {
    if (disconnectedApi === api) return
    const work = playsLifetime.work
    // Keep the listener for this connection: acquiring the work ref alone does not render the shell.
    // Only serialize a draft at exit time, never on chart frames or ordinary navigation.
    const beforeUnload = (event: BeforeUnloadEvent) => {
      const current = exitState.current
      if (current?.api !== api || current.work !== work || (!work.current && !hasUnsavedPlayWork(current.session))) return
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', beforeUnload)
    return () => window.removeEventListener('beforeunload', beforeUnload)
  }, [api, disconnectedApi, playsLifetime.work])
  const acceptPlaysSession = useCallback((next: PlaysSession) => {
    const current = exitState.current
    if (current?.api !== api || current.work !== playsLifetime.work) return
    current.session = next
    setPlaysSession(next)
    // A mutation may finish after its editor was hidden: use the live location,
    // not the page captured by the callback that started the request.
    if (current.page === 'plays' && !current.playRequested) setTrail(history => next.view === 'editor'
      ? visitLocations(history, playLocation(next)) : visitLocations(history, pageLocation('plays')))
  }, [api, playsLifetime.work])
  // A log write that completes after its Plays page has gone changes the session as it is now, never a discarded one.
  // A mounted Plays page applies it to its own current session, so its edits and the acknowledgement never race.
  const logApplier = useRef<((change: PlayLogChange) => void) | null>(null)
  const registerLogApplier = useCallback((apply: (change: PlayLogChange) => void) => {
    logApplier.current = apply
    return () => { if (logApplier.current === apply) logApplier.current = null }
  }, [])
  const changePlaysLog = useCallback((change: PlayLogChange) => {
    const current = exitState.current
    if (current?.api !== api || current.work !== playsLifetime.work) return
    if (logApplier.current) { logApplier.current(change); return }
    const next = { ...current.session, log: change(current.session.log) }
    current.session = next
    setPlaysSession(next)
  }, [api, playsLifetime.work])
  const [data, setData] = useState<Overview | null>(null)
  const [loaded, setLoaded] = useState<{ api: WorkspaceApi; key: number } | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [portfolioFilter, setPortfolioFilter] = useState('')
  const [accountFilters, setAccountFilters] = useState(emptyAccountFilters)
  const [overviewQuery, setOverviewQuery] = useState<OverviewQuery>({ groupBy: 'portfolio', period: '30', scope: 'all' })
  const [activityState, setActivityState] = useState(initialActivityState)
  const [notificationList, setNotificationList] = useState(initialNotificationList)
  const [portfolioDialog, setPortfolioDialog] = useState(false)
  const [accountDialog, setAccountDialog] = useState(false)
  const [focusPortfolioAccounts, setFocusPortfolioAccounts] = useState(false)
  const [managedPortfolioId, setManagedPortfolioId] = useState<string | null>(null)
  const [managedAccountId, setManagedAccountId] = useState<string | null>(null)
  const setNotice = useCallback((message: string) => { notify({ tone: 'success', message }) }, [notify])
  const [reloadKey, setReloadKey] = useState(0)
  const reload = useCallback(() => setReloadKey(key => key + 1), [])
  // A write answered under the session another tab replaced may still have applied: say so and read everything again.
  useEffect(() => onApiEvent(api, event => {
    if (event.type !== 'session-changed') return
    notify({ tone: 'info', key: 'session-changed', message: 'Your session changed in another tab. Vessel reloaded your workspace; check your last change before trying again.' })
    reload()
  }), [api, notify, reload])
  const startupNotice = identity?.notice ?? null
  const noticePresented = identity?.noticePresented
  useEffect(() => {
    if (!startupNotice) return
    notify({ tone: 'info', key: 'session-notice', message: startupNotice, duration: null })
    noticePresented?.()
  }, [startupNotice, noticePresented, notify])
  // Links from notifications go through the shell's own navigation; a missing account gets a short notice.
  const notificationLink = useRef<(link: NotificationLink) => void>(() => {})
  const inApp = useInAppNotifications({ api, active: disconnectedApi !== api && tradingMode === 'real', onLink: link => notificationLink.current(link),
    onOpenPage: () => navigate('notifications', true) })
  const monitor = useMonitorEvents(api, disconnectedApi !== api && tradingMode === 'real', reload, reloadKey, inApp.handlers)
  // The persistent incident count: stored monitor records only, never notifications. Shared by the Plays entry and Overview.
  const incidents = useMemo(() => monitorIncidents(monitor.summary?.accounts ?? []), [monitor.summary])
  const incidentCounts = countIncidents(incidents, incidentsBeyondCap(monitor.summary?.accounts ?? []))
  // Derived from the request generation so Reload shows progress and a repeated
  // failure visibly clears and returns, without a synchronous effect render.
  const loading = loaded === null || loaded.api !== api || loaded.key !== reloadKey
  // Stored reads wait for the event head cursor, so the stream covers every change after them.
  const pending = (loading && data === null) || (tradingMode === 'real' && !monitor.cursorReady)
  const error = loading ? null : loadError
  const cursorReady = tradingMode === 'paper' || monitor.cursorReady
  useEffect(() => {
    if (disconnectedApi === api || !cursorReady) return
    const controller = new AbortController()
    let active = true
    api.overview(controller.signal).then(next => { if (active) { setData(next); setOverviewQuery(query => { const scope = resolveOverviewScope(query.scope, next.accounts, next.portfolios); return scope === query.scope ? query : { ...query, scope } }); setLoadError(null); setLoaded({ api, key: reloadKey }) } })
      .catch(cause => { if (active) { setLoadError(errorMessage(cause, 'The workspace could not be loaded.')); setLoaded({ api, key: reloadKey }) } })
    return () => { active = false; controller.abort() }
  }, [api, reloadKey, disconnectedApi, cursorReady])
  useEffect(() => {
    if (!menuOpen) return
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const sidebar = document.getElementById('workspace-nav')!
    const controls = () => [...sidebar.querySelectorAll<HTMLAnchorElement | HTMLButtonElement>('a[href],button:not(:disabled)')]
    controls()[0]?.focus()
    const handle = (event: KeyboardEvent) => {
      // A dialog opened from the sheet (the trading mode confirmation) has already handled its own Escape.
      if (event.defaultPrevented) return
      if (event.key === 'Escape') {
        event.preventDefault(); setMenuOpen(false)
      } else if (event.key === 'Tab') {
        const list = controls(), first = list[0], last = list.at(-1)
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
      }
    }
    const resize = () => { if (window.innerWidth > 800) setMenuOpen(false) }
    window.addEventListener('keydown', handle)
    window.addEventListener('resize', resize)
    return () => {
      document.body.style.overflow = previousOverflow
      window.removeEventListener('keydown', handle)
      window.removeEventListener('resize', resize)
      document.getElementById('workspace-menu')?.focus({ preventScroll: true })
    }
  }, [menuOpen])
  function navigate(next: Page, drillDown = false) {
    navigateHash(next); setSelectedAccountId(null)
    setRequestedPlay(null)
    const locations = [pageLocation(next)]
    if (next === 'plays' && playsSession.view === 'editor') locations.push(playLocation(playsSession))
    if (next === 'settings' && identity) locations.push(settingsLocation(settingsTab))
    setTrail(history => drillDown ? visitLocations(history, ...locations) : locations)
  }
  function viewAccount(id: string) {
    if (page !== 'accounts') navigateHash('accounts')
    setSelectedAccountId(id); setMenuOpen(false)
    setRequestedPlay(null)
    const account = data?.accounts.find(item => item.id === id)
    setTrail(history => visitLocations(history, ...(page === 'accounts' ? [] : [pageLocation('accounts')]),
      { kind: 'account', page: 'accounts', id, label: account?.name || 'Account' }))
    window.scrollTo({ top: 0, left: 0, behavior: 'auto' })
  }
  function browsePlays(view: PlayView) {
    setRequestedPlay(null)
    setPlaysSession(session => ({ ...session, view: 'list' }))
    setPlaysList(list => ({ ...list, view, q: '', page: 1 }))
    navigateHash('plays')
    setTrail(history => visitLocations(history, pageLocation('plays')))
  }
  async function viewPlay(id: string, signal: AbortSignal, restore = false) {
    if (signal.aborted) return
    // PlaysPage owns draft protection and loading. Navigation must not replace the editor session.
    setRequestedPlay({ api, id, origin: trail, restore })
    if (!restore) {
      navigateHash('plays')
      setTrail(history => visitLocations(history, pageLocation('plays'),
        ...(playsSession.view === 'editor' ? [playLocation(playsSession)] : [])))
    }
  }
  function requestedPlayHandled(opened = true) {
    if (!requestedPlay || requestedPlay.api !== api) return
    if (opened) {
      const session = exitState.current?.session
      if (session?.view === 'editor') {
        navigateHash('plays')
        setTrail(visitLocations(requestedPlay.origin, ...(requestedPlay.origin.at(-1)?.page === 'plays' ? [] : [pageLocation('plays')]), playLocation(session)))
      }
    }
    setRequestedPlay(null)
  }
  function backToAccounts() {
    setSelectedAccountId(null)
    setTrail(history => visitLocations(history, pageLocation('accounts')))
    window.scrollTo({ top: 0, left: 0, behavior: 'auto' })
  }
  function changeSettingsTab(tab: SettingsTab, topLevel = false) {
    setSettingsTab(tab)
    setTrail(history => topLevel ? [pageLocation('settings'), settingsLocation(tab)] : visitLocations(history, settingsLocation(tab)))
  }
  function visitCrumb(location: ShellLocation) {
    if (location.kind === 'account') { viewAccount(location.id); return }
    if (location.kind === 'play') {
      if (location.id) void viewPlay(location.id, new AbortController().signal, true)
      return
    }
    if (location.kind === 'settings-tab') setSettingsTab(location.tab)
    if (location.kind === 'page' && location.page === 'settings') setSettingsTab('appearance')
    if (location.page === 'plays') setPlaysSession(session => ({ ...session, view: 'list' }))
    navigateHash(location.page)
    setSelectedAccountId(null)
    setRequestedPlay(null)
    setTrail(history => visitLocations(history, location,
      ...(location.kind === 'page' && location.page === 'settings' && identity ? [settingsLocation('appearance')] : [])))
  }
  const portfolios = data?.portfolios ?? [], accounts = data?.accounts ?? []
  useEffect(() => {
    notificationLink.current = link => {
      if (link.kind === 'play' || link.kind === 'play-execution') {
        // The Plays page opens it (protecting unsaved work) and reports a Play that no longer exists.
        if (link.playId) void viewPlay(link.playId, new AbortController().signal)
      } else if (link.accountId && accounts.some(account => account.id === link.accountId)) viewAccount(link.accountId)
      else notify({ tone: 'info', message: 'This account is no longer available.' })
    }
  })
  const enabledAccounts = accounts.filter(account => account.isEnabled !== false)
  const activePortfolioFilter = portfolioFilter === 'unassigned' || portfolios.some(p => p.id === portfolioFilter) ? portfolioFilter : ''
  const managedPortfolio = portfolios.find(p => p.id === managedPortfolioId) ?? null
  const managedAccount = accounts.find(a => a.id === managedAccountId) ?? null
  const manageAccount = (account: BrokerAccount) => setManagedAccountId(account.id)
  const refreshable = enabledAccounts.filter(account => venues.can(account.venueId, 'sync'))
  const selectedAccount = accounts.find(a => a.id === selectedAccountId)
  const currentPage = pages.find(p => p.id === page)!
  const unread = inApp.status.known && inApp.status.enabled ? inApp.status.unreadCount : 0
  const announcement = useCountAnnouncement(unread, inApp.status.known)
  const notificationsName = notificationsLabel(unread)
  // Plays are tracked: the condition that also shows the monitor's offline banner.
  const tracked = !!monitor.summary?.accounts.some(item => item.state !== 'idle' || item.manualState !== 'idle' || item.discrepancies.some(issue => issue.state === 'open'))
  const created = () => { if (exitState.current?.api !== api) return; reload(); setNotice('Saved to your workspace.') }
  const managementChanged = (event: ManagementEvent) => {
    if (exitState.current?.api !== api) return
    if (event.kind === 'portfolio-deleted' && portfolioFilter === event.id) setPortfolioFilter('')
    if (event.kind === 'account-deleted' && selectedAccountId === event.id) {
      setSelectedAccountId(null)
      setTrail(history => visitLocations(history, pageLocation('accounts')))
    }
    reload(); setNotice(event.message)
  }

  function newPlay() {
    // Read the owner's guard, not render-time busy/dirty state: either may still be stale.
    if (playsLifetime.work.current) {
      notify({ tone: 'info', message: 'A Play change is still pending. Your current work is kept until it finishes.' })
      navigate('plays', true)
      return
    }
    const next = startNewPlay(playsSession)
    if (next.draft === playsSession.draft)
      notify({ tone: 'info', message: 'Your unsaved changes are still open. Save or discard them before starting a new play.' })
    setPlaysSession(next)
    setRequestedPlay(null)
    navigateHash('plays')
    setTrail(history => visitLocations(history, ...(page === 'plays' ? [] : [pageLocation('plays')]), playLocation(next)))
  }
  const { refreshing, refreshProgress, sync, refreshAll, enqueue, cancel: cancelRefresh } = useAccountRefresh({ api, refreshable, reload, notify })
  const accountCreated = (account?: BrokerAccount) => {
    if (exitState.current?.api !== api) return
    reload()
    if (account && account.tradingMode !== 'paper' && account.isEnabled !== false && venues.can(account.venueId, 'sync')) enqueue(account)
  }
  const disconnectSession = () => {
    const current = exitState.current
    if (current?.api !== api || current.work !== playsLifetime.work) return
    if (current.work.current || hasUnsavedPlayWork(current.session)) {
      const verb = identity ? 'Sign out' : 'Disconnect'
      const message = current.work.current
        ? `A Play request is still in progress. ${verb} and discard unsaved work? Already-sent changes may still complete.`
        : `${verb} and discard unsaved Play work?`
      if (!window.confirm(message)) return
    }
    if (exitState.current?.api !== api || exitState.current.work !== current.work) return
    exitState.current = null
    cancelRefresh(); setDisconnectedApi(api); setRequestedPlay(null); setPlaysSession(createPlaysSession()); disconnect()
  }
  const refreshAllAction = tradingMode === 'paper' ? null : <Button variant="outline" size="sm" onClick={() => { void refreshAll() }}
    disabled={refreshing !== null || loading || !refreshable.length} aria-busy={refreshProgress !== null}>
    <RefreshCw size={14} className={refreshProgress ? 'is-spinning' : ''} />
    <span role={refreshProgress ? 'status' : undefined}>{refreshProgress ? `Refreshing ${refreshProgress.current} of ${refreshProgress.total}` : 'Refresh all'}</span>
  </Button>

  function switchTradingMode(control: HTMLButtonElement) {
    if (pendingSwitch) return
    const next = otherTradingMode(tradingMode)
    const current = exitState.current
    if (current?.work.current || current && hasUnsavedPlayWork(current.session)) {
      setPendingSwitch({ target: next, api, work: current.work, control })
      return
    }
    commitTradingMode(next)
  }
  // Only a mode the shell can enter remounts it; clearing the guards without that remount would leave live work unprotected.
  const canEnter = (next: TradingMode) => next !== tradingMode && (next === 'real' || paperAvailable)
  function commitTradingMode(next: TradingMode) {
    if (!canEnter(next)) return
    cancelRefresh()
    exitState.current = null
    onTradingModeChange(next)
  }
  function keepTradingMode() {
    switchFocus.current = pendingSwitch?.control ?? null
    setPendingSwitch(null)
  }
  function confirmTradingMode() {
    if (!pendingSwitch) return
    // Re-check now: work may have finished or started while the dialog was open, and only the connection that asked may
    // switch. Discarding was confirmed either way; a replaced or ended connection changes nothing.
    const current = exitState.current
    if (current?.api !== pendingSwitch.api || current.work !== pendingSwitch.work || !canEnter(pendingSwitch.target)) { keepTradingMode(); return }
    switchFocus.current = null
    setPendingSwitch(null)
    commitTradingMode(pendingSwitch.target)
  }
  // Cancelling returns focus to the control that asked, or to the current one if a resize swapped the rail and subtitle forms.
  function tradingModeDialogClosed() {
    const target = switchFocus.current?.isConnected ? switchFocus.current : modeControl.current
    switchFocus.current = null
    target?.focus({ preventScroll: true })
  }
  if (disconnectedApi === api) return null
  const modeSwitch = { mode: tradingMode, pending: pendingSwitch !== null, onSwitch: switchTradingMode, ref: modeControl }
  return <div className={`journal-shell ${navigationCollapsed ? 'navigation-collapsed' : ''}`} data-trading-mode={tradingMode}>
    <a className="skip-content" href="#workspace-content" onClick={event => { event.preventDefault(); document.getElementById('workspace-content')?.focus() }}>Skip to content</a>
    <aside id="workspace-nav" className={`shell-sidebar ${menuOpen ? 'is-open' : ''}`} aria-label="Main navigation" role={menuOpen ? 'dialog' : undefined} aria-modal={menuOpen ? true : undefined}>
      <div className={`shell-brand${paperAvailable ? ' has-mode' : ''}`}>
        <a className="shell-brand-link" href="#overview" aria-label="Vessel overview" title="Vessel overview" onClick={event => { event.preventDefault(); navigate('overview') }}>
          <ShellMonogram mode={tradingMode} arrivedFrom={arrivedFrom} /><span className="sidebar-copy">vessel{!paperAvailable && <small>The trading journal</small>}</span></a>
        {paperAvailable && !rail && <TradingModeSubtitle {...modeSwitch} />}
      </div>
      {menuOpen && <button type="button" className="icon-button sidebar-close" aria-label="Close navigation" title="Close navigation" onClick={() => setMenuOpen(false)}><X size={19} aria-hidden="true" /></button>}
      <button type="button" className="icon-button sidebar-toggle" aria-label={navigationCollapsed ? 'Expand navigation' : 'Collapse navigation'} title={navigationCollapsed ? 'Expand navigation' : 'Collapse navigation'} aria-controls="workspace-nav" aria-expanded={!navigationCollapsed} onClick={() => onNavigationCollapsedChange(!navigationCollapsed)}>
        {navigationCollapsed ? <PanelLeftOpen size={18} aria-hidden="true" /> : <PanelLeftClose size={18} aria-hidden="true" />}
      </button>
      {paperAvailable && rail && <TradingModeRailSwitch {...modeSwitch} />}
      <div className="nav-section-label">Workspace</div>
      <nav>{pages.filter(p => p.id !== 'settings' && p.id !== 'notifications').map(({ id, label, icon: Icon }) => {
        // Plays keeps its name; open monitor incidents describe it, beside a count in their severity tone.
        const incidentsShown = id === 'plays' && incidentCounts.total > 0
        return <a key={id} href={`#${id}`} className={`shell-nav-item ${page === id ? 'active' : ''}`} aria-label={label} title={label} onClick={event => { event.preventDefault(); navigate(id) }} aria-current={page === id ? 'page' : undefined}
          aria-describedby={incidentsShown ? 'plays-nav-incidents' : undefined}><Icon size={18} aria-hidden="true" /><span className="sidebar-copy">{label}</span>
          {id === 'accounts' && accounts.length > 0 && <small>{accounts.length}</small>}
          {incidentsShown && <IncidentCount counts={incidentCounts} id="plays-nav-incidents" />}</a>
      })}</nav>
      <div className="nav-section-label later-section">Later</div>
      <div className="shell-nav-later" role="link" aria-label="Strategies" aria-disabled="true" title="Strategies are not available yet"><Compass size={18} aria-hidden="true" /><span className="sidebar-copy">Strategies</span><small className="sidebar-copy" aria-hidden="true">Soon</small></div>
      <div className="shell-sidebar-bottom">
        {tradingMode === 'real' && <a href="#notifications" className={`shell-nav-item ${page === 'notifications' ? 'active' : ''}`} aria-label={notificationsName} title={notificationsName} aria-current={page === 'notifications' ? 'page' : undefined} onClick={event => { event.preventDefault(); navigate('notifications') }}><Bell size={18} aria-hidden="true" /><span className="sidebar-copy">Notifications</span><NotificationBadge count={unread} /></a>}
        <a href="#settings" className={`shell-nav-item ${page === 'settings' ? 'active' : ''}`} aria-label="Settings" title="Settings" aria-current={page === 'settings' ? 'page' : undefined} onClick={event => { event.preventDefault(); navigate('settings') }}><Settings2 size={18} aria-hidden="true" /><span className="sidebar-copy">Settings</span></a>
        {identity ? <AccountMenu identity={identity} onSignOut={disconnectSession}
          onProfile={() => { navigate('settings'); changeSettingsTab('profile', true) }}
          onSessions={() => { navigate('settings'); changeSettingsTab('profile', true); focusWhenShown('sessions-heading') }} />
          : <button className="owner-card" onClick={disconnectSession} aria-label={`Disconnect ${system.owner.displayName}'s session`} title={`Disconnect ${system.owner.displayName}'s browser session`}><span className="owner-avatar" aria-hidden="true">{system.owner.displayName.slice(0, 1).toUpperCase()}</span><span className="sidebar-copy"><strong>{system.owner.displayName}</strong></span><LogOut size={15} aria-hidden="true" /></button>}
      </div>
    </aside>
    <div className="shell-main" inert={menuOpen}>
      <header className="shell-header"><div className="shell-header-row"><button id="workspace-menu" className="icon-button mobile-menu" aria-label="Open navigation" title="Open navigation" aria-expanded={menuOpen} aria-controls="workspace-nav" onClick={() => setMenuOpen(true)}><Menu size={19} aria-hidden="true" />{unread > 0 && <span className="menu-unread-dot" aria-hidden="true" />}</button>
        {tradingMode === 'paper' && <TradingModeHeaderMark />}
        <ShellBreadcrumb key={locationKey(trail.at(-1)!)} trail={trail.map(location => location.kind === 'account'
          ? { ...location, label: accounts.find(account => account.id === location.id)?.name || location.label } : location)} onVisit={visitCrumb} />
        {tradingMode === 'real' && <a href="#notifications" className="icon-button header-notifications" aria-label={notificationsName} title={notificationsName} onClick={event => { event.preventDefault(); navigate('notifications') }}><Bell size={18} aria-hidden="true" /><NotificationBadge count={unread} /></a>}</div>
        <ShellNotices notices={tradingMode === 'paper' ? [paperNotice] : []} />
      </header>
      <span className="sr-only" role="status" aria-live="polite">{announcement}</span>
      <main id="workspace-content" className="shell-content" tabIndex={-1}>
        {page !== 'plays' && page !== 'review' && page !== 'notifications' && <section className="shell-page-heading" key={`heading-${page}`}><div><div className="eyebrow">{logLine(page, system.owner.displayName)}</div><h1>{selectedAccount ? selectedAccount.name : currentPage.label}</h1></div><div className="shell-heading-actions">
          {page !== 'settings' && <Button variant="outline" onClick={reload} disabled={loading || refreshing !== null} aria-busy={loading}><RefreshCw size={14} className={loading ? 'is-spinning' : ''} />Reload</Button>}
          {page === 'overview' ? <OverviewActions disabled={pending || !data} onNewPlay={newPlay} onPortfolio={() => setPortfolioDialog(true)} onAccount={() => setAccountDialog(true)} /> : page === 'portfolios' ? <Button onClick={() => setPortfolioDialog(true)}><Plus size={15} />New portfolio</Button> : page !== 'settings' && page !== 'activity' && <Button onClick={() => setAccountDialog(true)} disabled={pending || !data}><Plus size={15} />Add account</Button>}
        </div></section>}
        {error && <div className="workspace-alert" role="alert"><CircleHelp size={17} /><span>{error}</span><Button variant="ghost" size="sm" onClick={reload}>Try again</Button></div>}
        {monitor.error && <div className="workspace-alert" role="status">{monitor.error} Stored data remains available; no venue check was requested.</div>}
        {monitor.notice && <div className="workspace-alert" role="status">{monitor.notice}</div>}
        {monitor.summary && !monitor.summary.workerOnline && monitor.summary.accounts.some(item => item.state !== 'idle' || item.manualState !== 'idle' || item.discrepancies.some(issue => issue.state === 'open')) && <div className="workspace-alert monitor-offline" role="status">
          Worker offline. Background Play checks are delayed. Last heartbeat <Time at={monitor.summary.heartbeatAtUtc} fallback="not recorded" />. Stored executions may be out of date.
        </div>}
        {inApp.status.known && inApp.status.enabled && !inApp.status.workerOnline && tracked && <div className="workspace-alert notifications-offline" role="status">
          Notifications offline: they will arrive when it returns
        </div>}
        {pending && <div className="workspace-loading" role="status">Loading your workspace…</div>}
        {!pending && !data && !error && <div className="workspace-alert">No workspace data is available.</div>}

        {playsLifetime.visited && cursorReady && <div hidden={page !== 'plays'}>
          <PageErrorBoundary resetKey={page} key={playsLifetime.generation}>
            <Suspense fallback={<PageLoading />}>
              <PlaysPage workRef={playsLifetime.work} active={page === 'plays' && disconnectedApi !== api} accounts={accounts} portfolios={portfolios}
                reloadGeneration={reloadKey} accountMonitors={monitor.summary?.accounts ?? []} onManageAccount={id => {
                  const account = accounts.find(item => item.id === id)
                  if (account) manageAccount(account)
                }}
                api={api} session={playsSession} onSession={acceptPlaysSession} onLogChange={changePlaysLog} onLogApplier={registerLogApplier} onReload={reload} loading={loading}
                requestedPlayId={requestedPlay?.api === api ? requestedPlay.id : null} requestedPlayRestore={requestedPlay?.restore ?? false}
                onRequestedPlayHandled={requestedPlayHandled}
                list={playsList} onList={setPlaysList} />
            </Suspense>
          </PageErrorBoundary>
        </div>}
        <PageErrorBoundary key={page}><div className="page-settle">
        {page === 'review' && cursorReady && <Suspense fallback={<PageLoading />}><ReviewPage api={api} reloadGeneration={reloadKey} onOpenPlay={viewPlay} workspaceAccounts={accounts} /></Suspense>}

        {page === 'overview' && data && <OverviewPage data={data} api={api} monitors={monitor.summary?.accounts ?? []} incidents={incidents} overviewQuery={overviewQuery} setOverviewQuery={setOverviewQuery}
          reloadKey={reloadKey} refreshAllAction={refreshAllAction} refreshing={refreshing} onAddAccount={() => setAccountDialog(true)}
          onCreatePortfolio={() => setPortfolioDialog(true)} onAccounts={() => navigate('accounts', true)} onActivity={() => navigate('activity', true)}
          viewAccount={viewAccount} sync={sync} manageAccount={manageAccount} browsePlays={browsePlays} viewPlay={viewPlay} reload={reload} />}

        {page === 'portfolios' && data && <PortfoliosPage portfolios={portfolios} accounts={accounts} onAddAccount={() => setAccountDialog(true)}
          onCreatePortfolio={() => setPortfolioDialog(true)} onOpen={id => { setPortfolioFilter(id); navigate('accounts', true) }}
          onManage={(id, focusAccounts) => { setFocusPortfolioAccounts(focusAccounts); setManagedPortfolioId(id) }} />}

        {page === 'accounts' && data && (selectedAccount ? <AccountDetail key={selectedAccount.id} account={selectedAccount} api={api} monitor={monitor.summary?.accounts.find(item => item.accountId === selectedAccount.id) ?? null} refreshing={refreshing === selectedAccount.id} busy={refreshing !== null} reloadGeneration={reloadKey} onOpenPlay={viewPlay} onCreated={reload} onSync={id => { void sync(id) }} onManage={() => manageAccount(selectedAccount)} onBack={backToAccounts} /> :
          <AccountsPanel monitors={monitor.summary?.accounts ?? []} api={api} onChanged={managementChanged} onReload={reload} filters={accountFilters} onFiltersChange={setAccountFilters} accounts={accounts} portfolios={portfolios} portfolioFilter={activePortfolioFilter} onPortfolioFilter={setPortfolioFilter}
            refreshing={refreshing} refreshAction={refreshAllAction} onDetail={viewAccount} onSync={id => { void sync(id) }} onManage={manageAccount} onAdd={() => setAccountDialog(true)} />)}


        {page === 'activity' && data && <section className="shell-panel"><header className="shell-panel-heading"><h2>Position operations</h2></header><ActivityOperations api={api} accounts={accounts} portfolios={portfolios} filters state={activityState} onStateChange={setActivityState} reloadGeneration={reloadKey} onOpenPlay={viewPlay} onCreated={reload} /></section>}

        {page === 'notifications' && tradingMode === 'paper' && <p className="workspace-scope-note">Real-account notifications are unavailable in Paper mode.</p>}
        {page === 'notifications' && tradingMode === 'real' && cursorReady && <Suspense fallback={<PageLoading />}><NotificationsPage api={api} inApp={inApp} accounts={accounts}
          state={notificationList} onStateChange={setNotificationList} eyebrow={logLine(page, system.owner.displayName)} onPreferences={() => navigate('settings', true)} /></Suspense>}

        {page === 'settings' && <Suspense fallback={<PageLoading />}><SettingsPage system={system} api={api} onPaperSettingsChange={onPaperSettingsChange} inApp={inApp} disconnectSession={disconnectSession} onAccounts={() => navigate('accounts', true)}
          {...identity ? { identity, tab: settingsTab, onTab: changeSettingsTab } : {}} /></Suspense>}
        </div></PageErrorBoundary>
      </main>
    </div>
    <CreatePortfolioDialog open={portfolioDialog} onOpenChange={setPortfolioDialog} api={api} accounts={accounts} portfolios={portfolios} onCreated={created} />
    <CreateAccountDialog open={accountDialog} onOpenChange={setAccountDialog} api={api} portfolios={portfolios} accounts={accounts} onCreated={accountCreated} />
    {managedPortfolio && <ManagePortfolioDialog key={managedPortfolio.id} portfolio={managedPortfolio} accounts={accounts} portfolios={portfolios} focusAccounts={focusPortfolioAccounts} onStale={reload} api={api} onClose={() => setManagedPortfolioId(null)} onChanged={managementChanged} />}
    {managedAccount && <ManageAccountDialog key={managedAccount.id} account={managedAccount} portfolios={portfolios} api={api} onClose={() => setManagedAccountId(null)} onChanged={managementChanged} onStale={reload} />}
    {pendingSwitch && <TradingModeConfirmDialog target={pendingSwitch.target} onKeep={keepTradingMode} onConfirm={confirmTradingMode} onClosed={tradingModeDialogClosed} />}
  </div>
}
