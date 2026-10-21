import { useEffect, useRef, useState } from 'react'
import { exactTime } from './notification-list'
import type { DisplayZone } from '@/lib/display-zone'

/** The badge text: the count, or `99+` above 99. */
export const badgeText = (count: number) => count > 99 ? '99+' : String(count)

/** The accessible name of a link to the Notifications page; it starts with the visible text. */
export const notificationsLabel = (unread: number) => unread > 0 ? `Notifications, ${badgeText(unread)} unread` : 'Notifications'

/** Settle time before a changed count is announced, so a burst of frames is announced once. */
const announceDelay = 1500

/**
 * Text for a polite live region: the unread count once it changes and settles. The first known count is not
 * announced, nor is a count that returns to the one last announced.
 */
export function useCountAnnouncement(count: number, known: boolean) {
  const [announcement, setAnnouncement] = useState('')
  const last = useRef<number | null>(null)
  useEffect(() => {
    if (!known) return
    if (last.current === null) { last.current = count; return }
    if (count === last.current) return
    const timer = setTimeout(() => {
      last.current = count
      setAnnouncement(count === 0 ? 'No unread notifications' : count === 1 ? '1 unread notification' : `${badgeText(count)} unread notifications`)
    }, announceDelay)
    return () => clearTimeout(timer)
  }, [count, known])
  return announcement
}

/** Whether the notifications worker is delivering, from the newest of the stream's head and the preferences read. */
export function workerStatus(online: boolean, heartbeatAtUtc: string | null, zone: DisplayZone) {
  if (online) return 'Delivering'
  return heartbeatAtUtc ? `Notification worker offline since ${exactTime(heartbeatAtUtc, zone)}` : 'Notification worker offline; it has not reported yet'
}
