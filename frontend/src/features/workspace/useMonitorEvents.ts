import { useEffect, useRef, useState } from 'react'
import type { WorkspaceApi } from '@/api/workspace'
import type { MonitorSummary } from '@/api/monitor'
import type { NotificationFrame, NotificationHead, NotificationsStatus } from '@/api/notifications'
import { ApiError } from '@/api/errors'
import {
  isAfter, isNotificationAfter, isStaleNotificationCursor, isUnknownCursor, readHeadCursor, unknownCursor,
  type EventCursor, type NotificationCursor,
} from '@/api/event-cursor'

/** Reconnect delays grow from 2 s to 60 s, with jitter so several tabs do not retry together. */
export function reconnectDelay(attempt: number, random = Math.random()) {
  const ceiling = Math.min(60_000, 2_000 * 2 ** Math.max(0, attempt - 1))
  return Math.round(ceiling * (0.5 + random / 2))
}
/** A stream that stays open this long, or delivers an event, ends the backoff. A ready-then-close loop does not. */
export const stableStream = 30_000
/** The server refuses a fifth stream per owner. After repeated refusals, retry rarely and say why. */
const refusalLimit = 3
const refusedRetry = 300_000

/** Receives the owner's notification feed from the shared stream. None of it reloads stored workspace state. */
export interface NotificationStreamHandlers {
  /** A head read that is not older than the stream's cursor: the first one, after a reset and on the fallback timer. */
  onHead: (head: NotificationHead) => void
  /** The feed's `ready` or `reset` status; on a reset the head is read again. */
  onStatus: (status: NotificationsStatus) => void
  /** A notification's current state, once per position. */
  onFrame: (frame: NotificationFrame) => void
}

/**
 * One memory-only owner stream. All refreshes read stored state, never a venue.
 *
 * The head cursor is read first and `cursorReady` holds the shell's stored reads until then, so an event
 * committed after those reads is streamed from that cursor rather than skipped, without a second load.
 *
 * With `notifications`, the notification head is read after the event head and the stream also carries the
 * notification feed from it. Its cursor and resets are independent of the event cursor. A failed notification head
 * never holds stored reads: the stream opens without notifications and the head is retried on the fallback timer.
 */
export function useMonitorEvents(api: WorkspaceApi, enabled: boolean, reload: () => void, generation: number,
  notifications?: NotificationStreamHandlers) {
  const [head, setHead] = useState<{ api: WorkspaceApi; cursor: EventCursor | null } | null>(null)
  const [result, setResult] = useState<{ api: WorkspaceApi; summary: MonitorSummary } | null>(null)
  const [error, setError] = useState<{ api: WorkspaceApi; message: string } | null>(null)
  const [notice, setNotice] = useState<{ api: WorkspaceApi; message: string } | null>(null)
  const [healthGeneration, setHealthGeneration] = useState(0)
  // A new reload callback must not reopen the stream (and reset its cursor or backoff).
  const latestReload = useRef(reload)
  const handlers = useRef(notifications)
  useEffect(() => { latestReload.current = reload; handlers.current = notifications })
  const withNotifications = notifications !== undefined
  // The first notification head for this API, or null when it could not be read.
  const [notificationHead, setNotificationHead] = useState<{ api: WorkspaceApi; cursor: NotificationCursor | null } | null>(null)
  const currentHead = enabled && head?.api === api ? head : null
  const headKnown = currentHead !== null
  // Null when the head read failed: the stream then learns the head itself and reloads once.
  const start = currentHead ? currentHead.cursor ?? unknownCursor : null
  // The stream waits for the notification head read to settle; the stored reads do not.
  const notificationsSettled = !withNotifications || notificationHead?.api === api
  const notificationStart = withNotifications && notificationHead?.api === api ? notificationHead.cursor : null

  useEffect(() => {
    if (!enabled) return
    const controller = new AbortController()
    Promise.resolve().then(() => readHeadCursor(api, controller.signal)).then(cursor => {
      if (!controller.signal.aborted) setHead({ api, cursor })
    }).catch(() => { if (!controller.signal.aborted) setHead({ api, cursor: null }) })
    return () => controller.abort()
  }, [api, enabled])

  useEffect(() => {
    if (!enabled || !headKnown || !withNotifications) return
    const controller = new AbortController()
    Promise.resolve().then(() => api.notificationHead(controller.signal)).then(head => {
      if (controller.signal.aborted) return
      setNotificationHead({ api, cursor: head.nextCursor })
      handlers.current?.onHead(head)
    }).catch(() => { if (!controller.signal.aborted) setNotificationHead({ api, cursor: null }) })
    return () => controller.abort()
  }, [api, enabled, headKnown, withNotifications])

  useEffect(() => {
    if (!enabled || !headKnown) return
    const controller = new AbortController()
    Promise.resolve().then(() => api.monitor(controller.signal)).then(summary => {
      if (!controller.signal.aborted) { setResult({ api, summary }); setError(null) }
    }).catch(cause => {
      if (!controller.signal.aborted) setError({ api, message: cause instanceof ApiError ? cause.message : 'Monitor status is unavailable.' })
    })
    return () => controller.abort()
  }, [api, enabled, headKnown, generation, healthGeneration])

  useEffect(() => {
    if (!enabled || start === null || !notificationsSettled) return
    const controller = new AbortController()
    let cursor = start
    // Null while the notification head is unknown: the stream then carries events only.
    let notificationCursor = notificationStart
    let connection: AbortController | null = null
    let headRequests = 0
    let ready = false
    // A fallback reload ran while the stream was down; it may have failed, so the next clean connection reloads once.
    let reloadOnReady = false
    let failures = 0
    let refusals = 0
    let retry: ReturnType<typeof setTimeout> | undefined
    let stable: ReturnType<typeof setTimeout> | undefined
    let debounce: ReturnType<typeof setTimeout> | undefined
    const refresh = () => {
      if (controller.signal.aborted || debounce !== undefined) return
      debounce = setTimeout(() => {
        debounce = undefined
        if (!controller.signal.aborted) latestReload.current()
      }, 250)
    }
    const readNotificationHead = () => {
      if (!handlers.current) return
      // Only the latest read may answer, so a read from before a reset never lands after the one it started.
      const request = ++headRequests
      api.notificationHead(controller.signal).then(head => {
        if (controller.signal.aborted || request !== headRequests) return
        if (notificationCursor === null) {
          // The head is known at last: reconnect at once so the open stream carries notifications too.
          notificationCursor = head.nextCursor
          handlers.current?.onHead(head)
          connection?.abort()
          return
        }
        // An answer from before a reset (or another epoch), or behind a frame the stream delivered while it was in
        // flight, must not undo what the stream reported since: its count would be older than the badge.
        if (!isStaleNotificationCursor(head.nextCursor, notificationCursor) && !isNotificationAfter(notificationCursor, head.nextCursor)) {
          handlers.current?.onHead(head)
        }
      }, () => undefined)
    }
    const connect = async () => {
      let opened = false
      let refused = false
      let retryAt = 0
      const current = new AbortController()
      connection = current
      try {
        await api.monitorStream(cursor, AbortSignal.any([controller.signal, current.signal]), frame => {
          if (controller.signal.aborted) return
          if (!opened) {
            opened = true
            if (refusals) { refusals = 0; setNotice(null) }
            stable = setTimeout(() => { failures = 0 }, stableStream)
          }
          if (frame.type === 'status') {
            ready = true
            const missed = reloadOnReady
            reloadOnReady = false
            // Without a known head, stored reads may predate this stream: take its head and reload once.
            if (isUnknownCursor(cursor)) { cursor = frame.status.nextCursor; refresh(); return }
            // Ready only confirms the cursor; its catch-up events follow. A reset means stored state must reload.
            if (frame.status.state === 'reset') { cursor = frame.status.nextCursor; refresh() }
            // After an outage with no events to catch up on, nothing else would clear a failed fallback reload.
            else if (missed) refresh()
          } else if (frame.type === 'event') {
            failures = 0
            // Each event frame names its own resume cursor; a reconnect resumes after the last one handled.
            if (isAfter(frame.cursor, cursor)) { cursor = frame.cursor; refresh() }
          } else if (notificationCursor !== null) {
            // Notification frames never reload stored state; their cursor travels in the frame data.
            if (frame.type === 'notifications') {
              if (frame.status.state === 'reset') { notificationCursor = frame.status.nextCursor; readNotificationHead() }
              handlers.current?.onStatus(frame.status)
            } else if (isNotificationAfter(frame.frame.cursor, notificationCursor)) {
              notificationCursor = frame.frame.cursor
              handlers.current?.onFrame(frame.frame)
            }
          }
        }, ...(notificationCursor !== null ? [notificationCursor] : []))
      } catch (cause) {
        // Stored reads remain available while the stream reconnects.
        refused = cause instanceof ApiError && cause.status === 429
        const at = cause instanceof ApiError && cause.retryAtUtc ? Date.parse(cause.retryAtUtc) : 0
        retryAt = Number.isFinite(at) ? at : 0
      }
      clearTimeout(stable)
      connection = null
      if (controller.signal.aborted) return
      ready = false
      // Reopened for notifications, not failed: no backoff.
      if (current.signal.aborted) { void connect(); return }
      failures++
      refusals = refused ? refusals + 1 : 0
      if (refusals >= refusalLimit) {
        setNotice({ api, message: 'Live monitor updates are paused because too many Vessel tabs or windows are open. Close the others; stored data still reloads every minute.' })
      }
      const delay = refusals >= refusalLimit ? refusedRetry : Math.max(reconnectDelay(failures), retryAt - Date.now())
      retry = setTimeout(() => { void connect() }, Math.min(delay, refusedRetry))
    }
    void connect()
    const fallback = setInterval(() => {
      if (ready) setHealthGeneration(value => value + 1)
      else { reloadOnReady = true; refresh() }
      // Keeps the notifications worker status current, and retries a head that could not be read.
      readNotificationHead()
    }, 60_000)
    return () => {
      controller.abort()
      clearTimeout(retry)
      clearTimeout(stable)
      clearTimeout(debounce)
      clearInterval(fallback)
    }
  }, [api, enabled, start, notificationsSettled, notificationStart])
  return {
    /** False until the head cursor is known (or could not be read); hold stored reads until then. */
    cursorReady: !enabled || headKnown,
    summary: enabled && result?.api === api ? result.summary : null,
    error: enabled && error?.api === api ? error.message : null,
    notice: enabled && notice?.api === api ? notice.message : null,
  }
}
