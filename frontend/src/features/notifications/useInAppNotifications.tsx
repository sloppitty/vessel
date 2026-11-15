import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { WorkspaceApi } from '@/api/workspace'
import type { InAppNotification, NotificationClaim, NotificationHead, NotificationLink, NotificationSeverity } from '@/api/notifications'
import { isNotificationAfter, isStaleNotificationCursor, type NotificationCursor } from '@/api/event-cursor'
import { errorMessage } from '@/api/errors'
import { useNotifications, type NotificationTone } from '@/components/notifications/notifications'
import { isDocumentActive, useDocumentActive } from '@/lib/use-document-active'
import type { NotificationStreamHandlers } from '@/features/workspace/useMonitorEvents'

/** Popup tone and lifetime per severity. The timer runs only while the tab is visible and focused. Tune freely. */
export const popupTiming: Record<NotificationSeverity, { tone: NotificationTone; duration: number }> = {
  critical: { tone: 'error', duration: 20_000 },
  warning: { tone: 'warning', duration: 12_000 },
  info: { tone: 'info', duration: 6_000 },
}
/** The unread rows read to check which popups are still unread after a reset. */
const reconcileRows = 100
const popupKey = (id: string) => `notification:${id}`
const backlogKey = 'notification-backlog'

export interface InAppStatus {
  /** False until the first head or stream status arrives. */
  known: boolean
  unreadCount: number
  /** The in-app channel switch: off stops popups and the badge. */
  enabled: boolean
  workerOnline: boolean
  heartbeatAtUtc: string | null
}
const unknownStatus: InAppStatus = { known: false, unreadCount: 0, enabled: true, workerOnline: true, heartbeatAtUtc: null }

/** What an open Notifications page hears: a notification's current state, or that the feed reset and lists must reload. */
export type InAppChange = { type: 'notification'; notification: InAppNotification } | { type: 'reset' }

export interface InAppNotifications {
  status: InAppStatus
  /** Pass to `useMonitorEvents`. */
  handlers: NotificationStreamHandlers
  /** The newest notification cursor this tab knows, for Mark all as read. */
  cursor: () => NotificationCursor | null
  /** Adopts a head read outside the stream (after a 409) unless it is older than what the stream reported. */
  acceptHead: (head: NotificationHead) => void
  subscribe: (listener: (change: InAppChange) => void) => () => void
  /** Marks one notification read and announces its new state; rejects with an API-safe error. */
  markRead: (id: string) => Promise<InAppNotification>
  /** Follows a link through shell navigation, then marks its notification read. */
  openLink: (link: NotificationLink, notificationId: string) => void
}

/**
 * The shell's in-app notifications: the unread count, channel switch and worker status from the shared stream,
 * and popups. Each tab claims unshown notifications only while visible and focused, so one tab shows each popup
 * once. A popup closing on its timer leaves its notification unread; its close button or a link marks it read.
 * The durable shown marker is the server's owner-scoped delivery claim, never browser storage.
 */
export function useInAppNotifications({ api, active, onLink, onOpenPage, realJournalNotice = false }: {
  api: WorkspaceApi
  /** False once the session is disconnected. */
  active: boolean
  /** Shell navigation for a link; it reports a missing target itself. */
  onLink: (link: NotificationLink) => void
  onOpenPage: () => void
  /** Paper pages still receive real warnings; make their journal explicit. */
  realJournalNotice?: boolean
}): InAppNotifications {
  const { notify, dismiss } = useNotifications()
  const [state, setState] = useState<{ api: WorkspaceApi; status: InAppStatus }>({ api, status: unknownStatus })
  const status = state.api === api && active ? state.status : unknownStatus
  const live = useRef({ api, active, onLink, onOpenPage, notify, dismiss, realJournalNotice })
  useEffect(() => { live.current = { api, active, onLink, onOpenPage, notify, dismiss, realJournalNotice } })
  // Per-API memory: never storage. Seen IDs decide when a frame warrants a claim; popups are those shown here.
  const session = useRef(newSession(api))
  // Runs before the stream's effects (this hook is called first), so a new API never sees the old session. The old
  // session's popups close with it: their actions belong to the API that claimed them.
  useEffect(() => {
    if (session.current.api === api) return
    closeAll(session.current, live.current.dismiss)
    session.current = newSession(api)
  }, [api])
  useEffect(() => { if (!active) closeAll(session.current, live.current.dismiss) }, [active])
  useEffect(() => () => {
    // Mode switches unmount this shell while the provider survives. Invalidate claims before their replies
    // can create popups whose actions belong to the discarded shell or connection.
    session.current.generation++
    live.current.active = false
    closeAll(session.current, live.current.dismiss)
  }, [])
  const listeners = useRef(new Set<(change: InAppChange) => void>())
  const documentActive = useDocumentActive()

  const emit = useCallback((change: InAppChange) => { for (const listener of [...listeners.current]) listener(change) }, [])
  const apply = useCallback((target: WorkspaceApi, patch: Partial<InAppStatus>) => {
    const current = session.current
    if (current.api !== target) return
    current.enabled = patch.enabled ?? current.enabled
    setState(previous => ({ api: target, status: { ...previous.api === target ? previous.status : unknownStatus, ...patch, known: true } }))
    if (!current.enabled) closeAll(current, live.current.dismiss)
  }, [])
  const advance = useCallback((cursor: NotificationCursor) => {
    const known = session.current.cursor
    if (known === null || isNotificationAfter(cursor, known) || !isStaleNotificationCursor(cursor, known) && cursor !== known && !isNotificationAfter(known, cursor)) {
      session.current.cursor = cursor
    }
  }, [])

  const markRead = useCallback(async (id: string) => {
    const target = live.current.api
    session.current.settled.add(id)
    const notification = await target.readNotification(id)
    if (session.current.api === target) {
      live.current.dismiss(popupKey(id), 'withdrawn')
      emit({ type: 'notification', notification })
    }
    return notification
  }, [emit])
  const markReadQuietly = useCallback((id: string) => {
    markRead(id).catch(cause => live.current.notify({ tone: 'error', message: errorMessage(cause, 'The notification could not be marked as read.') }))
  }, [markRead])
  const openLink = useCallback((link: NotificationLink, notificationId: string) => {
    live.current.onLink(link)
    markReadQuietly(notificationId)
  }, [markReadQuietly])

  const show = useCallback((current: Session, generation: number, claim: NotificationClaim) => {
    // A claim that answers after a reset, an API replacement or a disconnect is stale: its snapshot may show
    // notifications that are no longer unread. Claimed rows stay unread on the page, so nothing is lost.
    if (session.current !== current || current.generation !== generation || !current.enabled || !live.current.active) return
    const { notify: popup } = live.current
    for (const notification of claim.popups) {
      current.seen.add(notification.id)
      // A read or withdrawal frame may have arrived while the claim was in flight.
      if (notification.state !== 'unread' || current.settled.has(notification.id)) continue
      const { tone, duration } = popupTiming[notification.severity]
      const { content } = notification
      current.popups.set(notification.id, notification)
      popup({
        key: popupKey(notification.id), tone, duration, timer: 'visible-focused', title: content.title, closeLabel: 'Mark as read and close',
        message: <>{live.current.realJournalNotice && <span>Real journal. </span>}{content.summary}{content.late && <span className="notification-late">Late</span>}</>,
        actions: content.links.map(link => ({ label: link.label, onClick: () => { if (session.current === current) openLink(link, notification.id) } })),
        onClose: reason => {
          if (current.popups.get(notification.id) === notification) current.popups.delete(notification.id)
          // Closing on the timer, or being pushed out or replaced, leaves it unread on the page.
          if (reason === 'dismissed' && session.current === current) markReadQuietly(notification.id)
        },
      })
    }
    if (claim.more > 0) popup({
      key: backlogKey, tone: 'info', duration: popupTiming.info.duration, timer: 'visible-focused',
      message: `${live.current.realJournalNotice ? 'Real journal. ' : ''}${claim.more} more unread notification${claim.more === 1 ? '' : 's'}`,
      action: { label: 'Open notifications', onClick: () => live.current.onOpenPage() },
    })
  }, [markReadQuietly, openLink])

  const claimAgain = useRef(() => {})
  const claim = useCallback(() => {
    const current = session.current
    if (!current.claimWanted || current.claiming || !current.enabled || !current.headKnown || !live.current.active || !isDocumentActive()) return
    current.claimWanted = false
    current.claiming = true
    const generation = current.generation
    current.api.claimNotifications().then(result => show(current, generation, result), () => undefined).finally(() => {
      current.claiming = false
      if (session.current === current) claimAgain.current()
    })
  }, [show])
  useEffect(() => { claimAgain.current = claim })
  const requestClaim = useCallback(() => { session.current.claimWanted = true; claim() }, [claim])
  // A hidden or unfocused tab claims once it is looked at again.
  useEffect(() => { if (documentActive && active) claim() }, [documentActive, active, api, claim])

  // After a reset, close popups whose notifications are no longer unread. Only popups shown when the rows were
  // requested are judged: one shown later may be newer than the answer.
  const reconcile = useCallback((target: WorkspaceApi) => {
    const current = session.current
    if (current.api !== target || current.popups.size === 0) return
    const { generation } = current
    const candidates = [...current.popups]
    target.notifications({ tab: 'unread', page: 1, pageSize: reconcileRows }).then(page => {
      // A later reset reconciles again.
      if (session.current !== current || current.generation !== generation) return
      const unread = new Set(page.items.map(item => item.id))
      const complete = page.items.length >= page.total
      const oldest = Math.min(...page.items.map(item => Date.parse(item.createdAt)))
      for (const [id, notification] of candidates) {
        if (current.popups.get(id) !== notification || unread.has(id)) continue
        // Beyond the rows read, a popup older than the oldest of them may still be unread.
        if (complete || Date.parse(notification.createdAt) >= oldest) live.current.dismiss(popupKey(id), 'withdrawn')
      }
    }, () => undefined)
  }, [])

  const handlers = useMemo<NotificationStreamHandlers>(() => ({
    onHead: head => {
      const target = live.current.api
      if (session.current.api !== target) return
      advance(head.nextCursor)
      apply(target, { unreadCount: head.unreadCount, enabled: head.enabled, workerOnline: head.workerOnline, heartbeatAtUtc: head.heartbeatAtUtc })
      // The first head opens the start-up claim.
      if (!session.current.headKnown) { session.current.headKnown = true; requestClaim() }
    },
    onStatus: next => {
      const target = live.current.api
      if (session.current.api !== target) return
      if (next.state === 'reset') { session.current.cursor = next.nextCursor; session.current.generation++ }
      else advance(next.nextCursor)
      apply(target, { unreadCount: next.unreadCount, enabled: next.enabled })
      if (next.state !== 'reset') return
      emit({ type: 'reset' })
      if (next.unreadCount === 0) closeAll(session.current, live.current.dismiss)
      else { reconcile(target); requestClaim() }
    },
    onFrame: frame => {
      const target = live.current.api
      const current = session.current
      if (current.api !== target) return
      advance(frame.cursor)
      apply(target, { unreadCount: frame.unreadCount, enabled: frame.enabled })
      const { notification } = frame
      emit({ type: 'notification', notification })
      if (notification.state !== 'unread') {
        // Read in another tab or withdrawn: every tab closes its popup, and a claim still in flight skips it.
        current.settled.add(notification.id)
        if (current.popups.has(notification.id)) live.current.dismiss(popupKey(notification.id), 'withdrawn')
      } else if (!current.seen.has(notification.id)) {
        current.seen.add(notification.id)
        requestClaim()
      }
    },
  }), [advance, apply, emit, reconcile, requestClaim])

  const subscribe = useCallback((listener: (change: InAppChange) => void) => {
    listeners.current.add(listener)
    return () => { listeners.current.delete(listener) }
  }, [])
  const cursor = useCallback(() => session.current.cursor, [])
  const acceptHead = useCallback((head: NotificationHead) => {
    const known = session.current.cursor
    // An older reset, or a head the stream has already passed, would lower the count.
    if (known !== null && (isStaleNotificationCursor(head.nextCursor, known) || isNotificationAfter(known, head.nextCursor))) return
    handlers.onHead(head)
  }, [handlers])
  return { status, handlers, cursor, acceptHead, subscribe, markRead, openLink }
}

interface Session {
  api: WorkspaceApi
  /** Advances on every notification reset; a claim answer from an older generation is discarded. */
  generation: number
  cursor: NotificationCursor | null
  enabled: boolean
  headKnown: boolean
  claimWanted: boolean
  claiming: boolean
  seen: Set<string>
  /** Notifications this tab knows are read or withdrawn. */
  settled: Set<string>
  popups: Map<string, InAppNotification>
}
const newSession = (api: WorkspaceApi): Session => ({ api, generation: 0, cursor: null, enabled: true, headKnown: false,
  claimWanted: false, claiming: false, seen: new Set(), settled: new Set(), popups: new Map() })

function closeAll(session: Session, dismiss: (key: string, reason: 'withdrawn') => void) {
  for (const id of [...session.popups.keys()]) dismiss(popupKey(id), 'withdrawn')
  dismiss(backlogKey, 'withdrawn')
}
