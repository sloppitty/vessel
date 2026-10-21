import { isEndedStatus, type PlayStatus } from '@/api/plays'

/** The API and the title input both limit a Play title to this many characters. */
export const maxPlayTitleLength = 200

const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'] as const

/**
 * Day, short English month and year, e.g. `15 Oct 2026`, in local time unless `zone` is UTC. Fixed names keep it
 * independent of the browser locale.
 */
export const titleDate = (date: Date, zone: 'local' | 'utc' = 'local') => zone === 'utc'
  ? `${date.getUTCDate()} ${months[date.getUTCMonth()]} ${date.getUTCFullYear()}`
  : `${date.getDate()} ${months[date.getMonth()]} ${date.getFullYear()}`

const directionLabel = (direction: string | null | undefined) =>
  direction === 'long' ? 'Long' : direction === 'short' ? 'Short' : ''

/** Cuts text to a number of UTF-16 units without leaving half of a surrogate pair behind. */
function clip(text: string, max: number) {
  if (text.length <= max) return text
  const cut = text.slice(0, Math.max(0, max))
  return /[\uD800-\uDBFF]$/.test(cut) ? cut.slice(0, -1) : cut
}

/**
 * Name given to a Play saved without one: the asset, the direction and the local date, e.g.
 * `BTC Long · 15 Oct 2026`. The asset is the instrument as the editor shows it, the venue contract key
 * such as `BTC` or a manual label. Missing parts are left out without stray separators, and an overlong
 * manual label is shortened so the whole title stays within the limit.
 */
export function defaultPlayTitle(play: { instrument?: string | null; direction?: string | null }, now: Date = new Date(), zone: 'local' | 'utc' = 'local') {
  const date = titleDate(now, zone)
  const direction = directionLabel(play.direction)
  const asset = (play.instrument ?? '').replace(/\s+/g, ' ').trim()
  const reserved = (direction ? direction.length + 1 : 0) + ' · '.length + date.length
  const head = [clip(asset, maxPlayTitleLength - reserved).trimEnd(), direction].filter(Boolean).join(' ')
  return [head, date].filter(Boolean).join(' · ')
}

/** Ended Plays (Closed, Cancelled, Unresolved) keep their title; the API rejects a changed one. */
const keepsTitle = (status: PlayStatus | undefined) => status !== undefined && isEndedStatus(status)

/**
 * The title to send when saving: what the owner typed (the API trims it), or the default when it is
 * blank and the Play can still change its title.
 */
export function titleForSave(draft: { title: string; instrument: string; direction: string }, status: PlayStatus | undefined, now: Date = new Date()) {
  return draft.title.trim() !== '' || keepsTitle(status) ? draft.title : defaultPlayTitle(draft, now)
}
