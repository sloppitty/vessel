import type { NotificationCategory, NotificationQuery, NotificationSeverity, NotificationTab } from '@/api/notifications'
import { isNotificationCategory, isNotificationSeverity } from '@/api/notifications'
import { defaultPageSize, type PageSize } from '@/components/ui/paging'
import type { DisplayZone } from '@/lib/display-zone'
import { formatTime } from '@/lib/time'

/** Tab, filters and paging of the Notifications page, kept in shell memory across navigation. Blank filters mean all. */
export interface NotificationListState { tab: NotificationTab; category: string; severity: string; page: number; pageSize: PageSize }
export const initialNotificationList: NotificationListState = { tab: 'unread', category: '', severity: '', page: 1, pageSize: defaultPageSize }

export function notificationQuery(state: NotificationListState): NotificationQuery {
  return {
    tab: state.tab, page: state.page, pageSize: state.pageSize,
    ...(isNotificationCategory(state.category) ? { category: state.category as NotificationCategory } : {}),
    ...(isNotificationSeverity(state.severity) ? { severity: state.severity as NotificationSeverity } : {}),
  }
}

export const categoryLabels: Record<NotificationCategory, string> = {
  executions: 'Executions', links: 'Order links', discrepancies: 'Discrepancies', monitor: 'Monitor', credentials: 'Credentials', test: 'Test',
}
export const severityLabels: Record<NotificationSeverity, string> = { critical: 'Critical', warning: 'Warning', info: 'Info' }

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
const steps: [Intl.RelativeTimeFormatUnit, number][] = [['second', 60], ['minute', 60], ['hour', 24], ['day', 7], ['week', 4.35], ['month', 12], ['year', Infinity]]
/** "5 minutes ago", for a time no later than `now`. */
export function relativeTime(at: string, now = Date.now()) {
  let value = Math.min(0, (Date.parse(at) - now) / 1000)
  for (const [unit, size] of steps) {
    if (Math.abs(value) < size || unit === 'year') return relative.format(Math.round(value), unit)
    value /= size
  }
  return ''
}
/** The full time of a notification, in the display zone and naming it (phase 1, 1.6e). */
export const exactTime = (at: string, zone: DisplayZone) => formatTime(at, zone, 'datetime-seconds')
