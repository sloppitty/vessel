/*
 * Manual execution times as the owner types them (phase 1, 1.6b): in the display time zone, sent as UTC. The browser's
 * own zone is the display zone until the time-zone setting (1.6e) lets the owner choose UTC. Fractions of a second are
 * kept digit for digit, so a stored microsecond time shown and sent back unchanged is the same instant.
 */

export type EntryZone = 'local' | 'utc'

const pad = (value: number) => String(value).padStart(2, '0')
const stored = /^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(\.\d+)?(?:Z|\+00:00)$/
const typed = /^(\d{4})-(\d\d)-(\d\d)[T ](\d\d):(\d\d)(?::(\d\d)(\.\d+)?)?\s*(Z|[+-]\d\d:\d\d)?$/i

/** The zone's name as a label shows it: the browser's IANA zone, or UTC. */
export function zoneName(zone: EntryZone) {
  if (zone === 'utc') return 'UTC'
  return Intl.DateTimeFormat().resolvedOptions().timeZone || 'local time'
}

function text(date: Date, zone: EntryZone, fraction: string) {
  const [year, month, day, hour, minute, second] = zone === 'utc'
    ? [date.getUTCFullYear(), date.getUTCMonth() + 1, date.getUTCDate(), date.getUTCHours(), date.getUTCMinutes(), date.getUTCSeconds()]
    : [date.getFullYear(), date.getMonth() + 1, date.getDate(), date.getHours(), date.getMinutes(), date.getSeconds()]
  return `${String(year).padStart(4, '0')}-${pad(month)}-${pad(day)} ${pad(hour)}:${pad(minute)}:${pad(second)}${fraction}`
}

/** A stored UTC timestamp as the owner reads and types it in `zone`; anything else is returned unchanged. */
export function zoneText(utc: string, zone: EntryZone) {
  const match = stored.exec(utc)
  if (!match) return utc
  const date = new Date(`${match[1]}Z`)
  return Number.isFinite(date.getTime()) ? text(date, zone, match[2] ?? '') : utc
}

/** Now, to the whole second, as the owner types it in `zone`. */
export const nowText = (zone: EntryZone, now = Date.now()) => text(new Date(Math.floor(now / 1000) * 1000), zone, '')

export type ParsedTime = { utc: string; fraction: string } | { problem: 'format' | 'missing' }

/**
 * What the owner typed, read in `zone` unless it names its own (Z or ±HH:MM), as the UTC timestamp the server reads. A
 * time that does not exist, such as one in a daylight-saving gap or 30 February, is refused rather than moved.
 */
export function parseZoneText(value: string, zone: EntryZone): ParsedTime {
  const input = value.trim()
  const match = typed.exec(input)
  if (!match) return { problem: 'format' }
  const [, y, mo, d, h, mi, s = '00', fraction = '', offset] = match
  const parts = [y, mo, d, h, mi, s].map(Number) as [number, number, number, number, number, number]
  const [year, month, day, hour, minute, second] = parts
  const date = new Date(0)
  if (offset || zone === 'utc') {
    date.setUTCFullYear(year, month - 1, day)
    date.setUTCHours(hour, minute, second, 0)
    if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day ||
      date.getUTCHours() !== hour || date.getUTCMinutes() !== minute || date.getUTCSeconds() !== second) return { problem: 'missing' }
    if (offset && offset.toUpperCase() !== 'Z') {
      const sign = offset.startsWith('-') ? -1 : 1
      const [oh, om] = offset.slice(1).split(':').map(Number) as [number, number]
      if (oh > 23 || om > 59) return { problem: 'format' }
      date.setTime(date.getTime() - sign * (oh * 60 + om) * 60_000)
    }
  } else {
    date.setFullYear(year, month - 1, day)
    date.setHours(hour, minute, second, 0)
    if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day ||
      date.getHours() !== hour || date.getMinutes() !== minute || date.getSeconds() !== second) return { problem: 'missing' }
  }
  if (!Number.isFinite(date.getTime())) return { problem: 'format' }
  return { utc: `${date.toISOString().slice(0, 19)}${fraction}Z`, fraction }
}
