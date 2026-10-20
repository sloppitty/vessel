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

/** The offset from UTC at that instant in the browser's zone, as typed: "+02:00". */
function offsetText(date: Date) {
  const minutes = -date.getTimezoneOffset()
  return `${minutes < 0 ? '-' : '+'}${pad(Math.floor(Math.abs(minutes) / 60))}:${pad(Math.abs(minutes) % 60)}`
}

/** The instant as typed in `zone`, with its offset when the local time happens twice (a daylight-saving overlap). */
function exactText(date: Date, zone: EntryZone, fraction: string) {
  const shown = text(date, zone, fraction)
  if (zone === 'utc') return shown
  const back = parseZoneText(shown, zone)
  return 'utc' in back && back.utc.slice(0, 19) === date.toISOString().slice(0, 19) ? shown : `${shown} ${offsetText(date)}`
}

/**
 * A stored UTC timestamp as the owner reads and types it in `zone`, so that sending it back unchanged is the same instant;
 * anything else is returned unchanged.
 */
export function zoneText(utc: string, zone: EntryZone) {
  const match = stored.exec(utc)
  if (!match) return utc
  const date = new Date(`${match[1]}Z`)
  return Number.isFinite(date.getTime()) ? exactText(date, zone, match[2] ?? '') : utc
}

/** Now, to the whole second, as the owner types it in `zone`. */
export const nowText = (zone: EntryZone, now = Date.now()) => exactText(new Date(Math.floor(now / 1000) * 1000), zone, '')

export type ParsedTime = { utc: string; fraction: string } | { problem: 'format' | 'missing' | 'ambiguous' }

/**
 * What the owner typed, read in `zone` unless it names its own (Z or ±HH:MM), as the UTC timestamp the server reads. A
 * time that does not exist, such as one in a daylight-saving gap or 30 February, is refused rather than moved, and a
 * local time that happens twice needs its offset.
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
    const same = (other: Date) => other.getFullYear() === year && other.getMonth() === month - 1 && other.getDate() === day &&
      other.getHours() === hour && other.getMinutes() === minute && other.getSeconds() === second
    // The instants these clock fields can mean: one for each offset the zone uses in the days around them. None is a
    // time the zone skips; two are an overlap, whose choice is the owner's to say.
    const wall = new Date(0)
    wall.setUTCFullYear(year, month - 1, day)
    wall.setUTCHours(hour, minute, second, 0)
    const offsets = new Set([-2, -1, 0, 1, 2].map(days => new Date(wall.getTime() + days * 86_400_000).getTimezoneOffset()))
    const matches = [...offsets].map(offset => wall.getTime() + offset * 60_000).filter(ms => same(new Date(ms)))
    if (!matches.length) return { problem: 'missing' }
    if (new Set(matches).size > 1) return { problem: 'ambiguous' }
    date.setTime(matches[0]!)
  }
  if (!Number.isFinite(date.getTime())) return { problem: 'format' }
  return { utc: `${date.toISOString().slice(0, 19)}${fraction}Z`, fraction }
}
