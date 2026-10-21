import type { DisplayZone } from './display-zone'

/*
 * One formatter for every displayed time (phase 1, 1.6e). Times read in the display zone and name it; dates follow the
 * same zone. English day-month order and a 24-hour clock everywhere, whatever the browser's language.
 */

export type TimeStyle = 'datetime' | 'datetime-seconds' | 'datetime-exact' | 'date' | 'day' | 'day-time' | 'time' | 'time-seconds' | 'year' | 'month' | 'day-of-month'

const styles: Record<TimeStyle, Intl.DateTimeFormatOptions> = {
  datetime: { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' },
  'datetime-seconds': { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' },
  // To the second, then the stored fraction digit for digit: an execution time where a sub-second change matters.
  'datetime-exact': { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' },
  date: { day: 'numeric', month: 'short', year: 'numeric' },
  day: { day: 'numeric', month: 'short' },
  'day-time': { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' },
  time: { hour: '2-digit', minute: '2-digit' },
  'time-seconds': { hour: '2-digit', minute: '2-digit', second: '2-digit' },
  // Chart axis ticks.
  year: { year: 'numeric' },
  month: { month: 'short' },
  'day-of-month': { day: 'numeric' },
}
const hasTime = (style: TimeStyle) => 'hour' in styles[style]

const cache = new Map<string, Intl.DateTimeFormat>()
function formatter(zone: DisplayZone, options: Intl.DateTimeFormatOptions) {
  // The browser's zone can change under a long-lived page (and does in tests), so it is part of the key.
  const local = zone === 'utc' ? 'UTC' : Intl.DateTimeFormat().resolvedOptions().timeZone
  const key = `${local}|${JSON.stringify(options)}`
  let found = cache.get(key)
  if (!found) {
    found = new Intl.DateTimeFormat('en-GB', { ...options, hourCycle: 'h23', ...(zone === 'utc' ? { timeZone: 'UTC' } : {}) })
    cache.set(key, found)
  }
  return found
}

const toDate = (at: string | number | Date) => at instanceof Date ? at : new Date(at)

/** The zone's short name at that instant: "UTC", or the browser zone's, such as "CEST" or "GMT+2". */
export function zoneLabel(at: string | number | Date, zone: DisplayZone) {
  if (zone === 'utc') return 'UTC'
  return formatter('local', { timeZoneName: 'short' }).formatToParts(toDate(at)).find(part => part.type === 'timeZoneName')?.value ?? 'local'
}

/** `at` in `zone`, without the zone's name. */
/** The fraction of a second a stored timestamp carries, such as ".123456"; none for other values. */
const fractionOf = (at: string | number | Date) => typeof at === 'string' ? /T\d\d:\d\d:\d\d(\.\d+)/.exec(at)?.[1] ?? '' : ''

export const formatIn = (at: string | number | Date, zone: DisplayZone, style: TimeStyle = 'datetime') =>
  formatter(zone, styles[style]).format(toDate(at)) + (style === 'datetime-exact' ? fractionOf(at) : '')

/** `at` in `zone`, naming the zone when it shows a time: "13 Oct 2026, 12:00 CEST". */
export function formatTime(at: string | number | Date, zone: DisplayZone, style: TimeStyle = 'datetime') {
  const text = formatIn(at, zone, style)
  return hasTime(style) ? `${text} ${zoneLabel(at, zone)}` : text
}

export const otherZone = (zone: DisplayZone): DisplayZone => zone === 'utc' ? 'local' : 'utc'

/** The browser's zone by name, such as "Europe/Madrid". */
export const zoneName = () => Intl.DateTimeFormat().resolvedOptions().timeZone || 'the browser zone'

/** The same instant in the other zone, to the second, for a tooltip. */
export const otherZoneTime = (at: string | number | Date, zone: DisplayZone, style: 'datetime-seconds' | 'datetime-exact' = 'datetime-seconds') =>
  formatTime(at, otherZone(zone), style)
