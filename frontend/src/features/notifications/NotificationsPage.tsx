import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { ChevronDown, CheckCheck, Settings2 } from 'lucide-react'
import type { WorkspaceApi, BrokerAccount } from '@/api/workspace'
import type { InAppNotification, NotificationPage as Page } from '@/api/notifications'
import { notificationCategories, notificationSeverities } from '@/api/notifications'
import { ApiError, errorMessage } from '@/api/errors'
import { AccountAvatar, AccountName, SourceChip, VenueTag } from '@/components/accounts/AccountIdentity'
import { Button } from '@/components/ui/button'
import { InfoTip } from '@/components/ui/info-tip'
import { NativeSelect } from '@/components/ui/native-select'
import { Pager } from '@/components/ui/pager'
import { isPageSize } from '@/components/ui/paging'
import { Skeleton, SkeletonStatus } from '@/components/ui/skeleton'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useNotifications } from '@/components/notifications/notifications'
import { useDelayedFlag } from '@/lib/use-delayed-flag'
import type { InAppNotifications } from './useInAppNotifications'
import { categoryLabels, exactTime, notificationQuery, relativeTime, severityLabels, type NotificationListState } from './notification-list'
import './notifications-page.css'
import { useDisplayZone } from '@/lib/display-zone'

const count = new Intl.NumberFormat('en-US')

/**
 * The owner's notifications, newest first, with Unread and All tabs, category and severity filters and the shared
 * pager. Stream changes update shown rows at once and reload the page, keeping the focused row where it is.
 */
export function NotificationsPage({ api, inApp, accounts, state, onStateChange, eyebrow, onPreferences }: {
  api: WorkspaceApi
  inApp: InAppNotifications
  accounts: BrokerAccount[]
  state: NotificationListState
  onStateChange: (state: NotificationListState) => void
  eyebrow: string
  onPreferences: () => void
}) {
  const { notify } = useNotifications()
  const query = useMemo(() => notificationQuery(state), [state])
  const key = JSON.stringify(query)
  const [reload, setReload] = useState(0)
  const [result, setResult] = useState<{ api: WorkspaceApi; key: string; reload: number; page: Page | null; error: string | null } | null>(null)
  // Stream changes to rows on screen, applied before the reload they also trigger answers.
  const [changed, setChanged] = useState<Map<string, InAppNotification>>(() => new Map())
  const [markingAll, setMarkingAll] = useState(false)
  const [conflict, setConflict] = useState<string | null>(null)
  const fieldId = useId()
  const list = useRef<HTMLOListElement>(null)
  const panel = useRef<HTMLElement>(null)
  const { subscribe, acceptHead } = inApp
  // The focused row and where it was, taken just before rows change: new rows above must not move it, and a row
  // that leaves must not drop focus to the body.
  const anchor = useRef<{ id: string; index: number; top: number } | null>(null)
  const remember = () => {
    const row = document.activeElement?.closest<HTMLElement>('[data-notification-row]')
    const id = row?.dataset.notificationRow
    const all = list.current ? [...list.current.querySelectorAll('[data-notification-row]')] : []
    anchor.current = row && id && list.current?.contains(row) ? { id, index: all.indexOf(row), top: row.getBoundingClientRect().top } : null
  }

  // The request in flight, so a notification reset can drop its answer at once (the replacement fetch is debounced).
  const inflight = useRef<AbortController | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    inflight.current = controller
    api.notifications(query, controller.signal).then(page => {
      if (controller.signal.aborted) return
      remember()
      setResult({ api, key, reload, page, error: null })
      setChanged(new Map())
      // A page past the end comes back clamped; keep the page actually shown.
      if (page.page !== query.page) onStateChange({ ...state, page: page.page })
    }).catch(cause => {
      if (!controller.signal.aborted) setResult({ api, key, reload, page: null, error: errorMessage(cause, 'Notifications could not be loaded.') })
    })
    return () => controller.abort()
    // The state is read only to adopt a clamped page for this request.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, key, query, reload])

  // Changes arrive from every tab: show them at once, then reload (debounced) for counts, order and new rows.
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined
    const later = () => { clearTimeout(timer); timer = setTimeout(() => setReload(n => n + 1), 300) }
    const unsubscribe = subscribe(change => {
      remember()
      if (change.type === 'notification') setChanged(current => new Map(current).set(change.notification.id, change.notification))
      // An answer read before a reset belongs to the old generation: never show it.
      else inflight.current?.abort()
      later()
    })
    return () => { unsubscribe(); clearTimeout(timer) }
  }, [subscribe])

  const current = result?.api === api ? result : null
  const pending = current === null || current.key !== key || current.reload !== reload
  const shown = current?.page ?? null
  const skeleton = useDelayedFlag(pending && shown === null)
  const rows = useMemo(() => shown?.items.map(item => changed.get(item.id) ?? item) ?? [], [shown, changed])
  const error = pending ? null : current.error

  useLayoutEffect(() => {
    const at = anchor.current
    anchor.current = null
    if (!at) return
    const lost = document.activeElement === document.body || !document.activeElement
    const row = list.current?.querySelector<HTMLElement>(`[data-notification-row="${at.id}"]`)
    if (row) {
      const moved = row.getBoundingClientRect().top - at.top
      if (moved) window.scrollBy?.({ top: moved, behavior: 'instant' })
      // The focused control left the row (for example Mark as read once it is read): keep focus on the row.
      if (lost) row.focus({ preventScroll: true })
    } else if (lost) {
      // The row left: focus the one now in its place, or the panel once the list is empty and gone.
      const rowsNow = list.current?.querySelectorAll<HTMLElement>('[data-notification-row]') ?? []
      const next = rowsNow[Math.min(at.index, rowsNow.length - 1)]
      ;(next ?? panel.current)?.focus({ preventScroll: true })
    }
  }, [rows])

  const update = (patch: Partial<NotificationListState>) => onStateChange({ ...state, page: 1, ...patch })
  const markRead = (notification: InAppNotification) => {
    inApp.markRead(notification.id).catch(cause => notify({ tone: 'error', message: errorMessage(cause, 'The notification could not be marked as read.') }))
  }
  const markAll = async () => {
    const upTo = inApp.cursor()
    if (upTo === null) return
    setMarkingAll(true); setConflict(null)
    try {
      const { read } = await api.readAllNotifications(upTo)
      notify({ tone: 'success', message: read === 1 ? 'Marked 1 notification as read.' : `Marked ${count.format(read)} notifications as read.` })
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 409) {
        setConflict(cause.message)
        // A newer head lets the next attempt succeed; the stream resets this tab too.
        api.notificationHead().then(acceptHead, () => undefined)
      } else notify({ tone: 'error', message: errorMessage(cause, 'Notifications could not be marked as read.') })
    } finally {
      setMarkingAll(false)
      setReload(n => n + 1)
    }
  }
  const unreadTotal = shown?.unreadTotal ?? null
  const filtered = state.category !== '' || state.severity !== ''

  return <>
    <section className="shell-page-heading"><div><div className="eyebrow">{eyebrow}</div><h1>Notifications</h1></div><div className="shell-heading-actions">
      <Button variant="outline" onClick={() => { void markAll() }} disabled={markingAll || inApp.cursor() === null || unreadTotal === 0} aria-busy={markingAll}><CheckCheck size={15} />Mark all as read</Button>
      <Button variant="ghost" asChild><a href="#settings" onClick={event => { event.preventDefault(); onPreferences() }}><Settings2 size={15} />Preferences</a></Button>
    </div></section>
    {conflict && <div className="workspace-alert" role="alert"><span>{conflict}</span></div>}
    <section ref={panel} className="shell-panel notifications-panel" tabIndex={-1} aria-label="Notification list">
      <div className="notifications-toolbar">
        <Tabs value={state.tab} onValueChange={value => update({ tab: value === 'all' ? 'all' : 'unread' })}>
          <TabsList aria-label="Notification views">
            <TabsTrigger value="unread">Unread{unreadTotal !== null && <span className="notifications-tab-count">{count.format(unreadTotal)}</span>}</TabsTrigger>
            <TabsTrigger value="all">All{shown && <span className="notifications-tab-count">{count.format(shown.allTotal)}</span>}</TabsTrigger>
          </TabsList>
        </Tabs>
        <fieldset className="notifications-filters"><legend className="sr-only">Filter notifications</legend>
          <label htmlFor={`${fieldId}-category`}>Category</label>
          <NativeSelect id={`${fieldId}-category`} density="compact" value={state.category} onChange={event => update({ category: event.target.value })}>
            <option value="">All categories</option>{notificationCategories.map(item => <option key={item} value={item}>{categoryLabels[item]}</option>)}
          </NativeSelect>
          <label htmlFor={`${fieldId}-severity`}>Severity</label>
          <NativeSelect id={`${fieldId}-severity`} density="compact" value={state.severity} onChange={event => update({ severity: event.target.value })}>
            <option value="">All severities</option>{notificationSeverities.map(item => <option key={item} value={item}>{severityLabels[item]}</option>)}
          </NativeSelect>
        </fieldset>
      </div>
      {error ? <div className="workspace-alert" role="alert">{error}<Button variant="ghost" size="sm" onClick={() => setReload(n => n + 1)}>Retry notifications</Button></div>
        : shown === null ? skeleton ? <NotificationsSkeleton /> : <div className="notifications-pending" aria-busy="true" />
        : rows.length === 0 ? <p className="notifications-empty" role="status">{filtered ? 'No notifications match the filters.' : state.tab === 'unread' ? 'You are all caught up. No unread notifications.' : 'No notifications yet.'}</p>
        : <ol ref={list} className={pending ? 'notifications-list list-refetching' : 'notifications-list'} aria-busy={pending || undefined}>
          {rows.map(notification => <NotificationRow key={notification.id} notification={notification} accounts={accounts}
            onMarkRead={() => markRead(notification)} onLink={link => inApp.openLink(link, notification.id)} />)}
        </ol>}
      {shown && <Pager label="Notification pages" page={shown.page} pageSize={shown.pageSize} totalCount={shown.total} disabled={pending}
        onPage={page => onStateChange({ ...state, page })} onPageSize={size => { if (isPageSize(size)) onStateChange({ ...state, page: 1, pageSize: size }) }} />}
    </section>
  </>
}

function NotificationRow({ notification, accounts, onMarkRead, onLink }: {
  notification: InAppNotification
  accounts: BrokerAccount[]
  onMarkRead: () => void
  onLink: (link: InAppNotification['content']['links'][number]) => void
}) {
  const zone = useDisplayZone()
  const [expanded, setExpanded] = useState(false)
  const detailId = useId()
  const { content, severity, state } = notification
  const account = accounts.find(item => item.id === content.subject.accountId)
  const unread = state === 'unread'
  return <li className={`notification-row is-${severity} is-${state}`} data-notification-row={notification.id} tabIndex={-1}>
    <span className="notification-row-mark" aria-hidden="true" />
    <div className="notification-row-main">
      <div className="notification-row-heading">
        <span className={`notification-severity is-${severity}`}>{severityLabels[severity]}</span>
        <strong className="notification-row-title">{content.title}</strong>
        {unread && <span className="notification-unread-dot" role="img" aria-label="Unread" />}
      </div>
      {content.summary && <p className="notification-row-summary">{content.summary}</p>}
      <div className="notification-row-meta">
        {account ? <span className="notification-row-account"><AccountAvatar venueId={account.venueId} size="sm" />
          <AccountName account={{ id: account.id, name: account.name, venueId: account.venueId, source: account.sourceId ?? account.address }} />
          <VenueTag venueId={account.venueId} /><SourceChip source={account.sourceId ?? account.address} /></span>
          : content.subject.accountName && <span className="notification-row-account">{content.subject.accountName}</span>}
        <InfoTip trigger={relativeTime(notification.createdAt)} className="notification-row-time">{exactTime(notification.createdAt, zone)}</InfoTip>
        {content.late && <span className="notification-pill">Late</span>}
        {state === 'withdrawn' && <span className="notification-pill is-withdrawn">No longer current</span>}
      </div>
      {expanded && content.lines.length > 0 && <ul id={detailId} className="notification-row-lines">{content.lines.map((line, index) => <li key={index}>{line}</li>)}</ul>}
      <div className="notification-row-actions">
        {content.links.map((link, index) => <Button key={index} variant="outline" size="sm" onClick={() => onLink(link)}>{link.label}</Button>)}
        {unread && <Button variant="ghost" size="sm" onClick={onMarkRead}>Mark as read</Button>}
        {content.lines.length > 0 && <Button variant="ghost" size="sm" aria-expanded={expanded} aria-controls={expanded ? detailId : undefined} onClick={() => setExpanded(open => !open)}>
          Details<ChevronDown size={14} className={expanded ? 'is-flipped' : ''} aria-hidden="true" /></Button>}
      </div>
    </div>
  </li>
}

function NotificationsSkeleton() {
  return <SkeletonStatus label="Loading notifications…">
    <ol className="notifications-list">{Array.from({ length: 5 }, (_, index) => <li key={index} className="notification-row">
      <span className="notification-row-mark" />
      <div className="notification-row-main">
        <div className="notification-row-heading"><Skeleton className="notification-skeleton-pill" /><Skeleton className="notification-skeleton-title" /></div>
        <Skeleton className="notification-skeleton-summary" />
        <div className="notification-row-meta"><Skeleton className="notification-skeleton-meta" /></div>
      </div>
    </li>)}</ol>
  </SkeletonStatus>
}
