import type { BrokerAccount } from '@/api/workspace'
// Formatting only. Authoritative amounts and aggregates arrive as decimal strings.
// Intl formats the original decimal string exactly; Number only estimates magnitude
// so tiny prices keep a few significant digits instead of rounding to zero.
const fractionDigits = (value: string, standard: number) => {
  const magnitude = Math.abs(Number(value))
  return magnitude !== 0 && magnitude < 10 ** -standard ? Math.min(100, Math.ceil(-Math.log10(magnitude)) + 4) : standard
}
const decimalString = (value: string) => value as Intl.StringNumericLiteral
export function money(value: string | null): string {
  if (value === null) return '—'
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: fractionDigits(value, 2) }).format(decimalString(value))
}
export function amount(value: string): string {
  return new Intl.NumberFormat('en-US', { maximumFractionDigits: fractionDigits(value, 8) }).format(decimalString(value))
}
export function time(value: string | null): string {
  return value === null ? 'Not refreshed yet' : new Date(value).toLocaleString(undefined, {
    year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
  })
}
export const shortAddress = (value: string | null) => value === null ? 'Manual record' : `${value.slice(0, 6)}…${value.slice(-4)}`

// Review keeps its compact money precision, N/A label and UTC date profile.
export function reviewMoney(value: string | null, signed = false): string {
  if (value === null) return 'N/A'
  const digits = Number(value) !== 0 && Math.abs(Number(value)) < 1 ? 4 : 2
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: digits,
    minimumFractionDigits: 0, signDisplay: signed ? 'exceptZero' : 'auto' }).format(decimalString(value))
}
/**
 * One Play's signed result (venue net or Play-local) in Review: compact like `reviewMoney`, but a non-zero value that would
 * round to "$0" keeps its sign and leading digits, so it never reads as an exact scratch. Only exact zero is "$0".
 */
export function reviewResult(value: string | null): string {
  if (value === null) return 'N/A'
  const standard = Number(value) !== 0 && Math.abs(Number(value)) < 1 ? 4 : 2
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: fractionDigits(value, standard),
    minimumFractionDigits: 0, signDisplay: 'exceptZero' }).format(decimalString(value))
}
export function reviewPercent(value: string | null, fraction = false): string {
  if (value === null) return 'N/A'
  const formatted = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1, minimumFractionDigits: 1,
    ...(fraction ? { style: 'percent' as const } : {}) }).format(decimalString(value))
  return fraction ? formatted : `${formatted}%`
}
export const reviewMultiple = (value: string | null) => value === null ? 'N/A'
  : `${new Intl.NumberFormat('en-US', { maximumFractionDigits: 2, minimumFractionDigits: 2 }).format(decimalString(value))}×`
export const reviewDate = (at: string, year = false) => new Date(at).toLocaleDateString('en-GB', {
  day: 'numeric', month: 'short', ...(year ? { year: 'numeric' as const } : {}), timeZone: 'UTC',
})
/** A short, recognizable form of a venue source identity (public address or account index). Keep the full value in a title. */
export function shortSource(value: string | null | undefined): string | null {
  if (!value) return null
  if (/^0x[\da-f]{40}$/i.test(value)) return `${value.slice(0, 6)}…${value.slice(-4)}`
  if (/^\d+$/.test(value)) return value.length > 10 ? `#${value.slice(0, 4)}…${value.slice(-4)}` : `#${value}`
  return value.length > 16 ? `${value.slice(0, 8)}…${value.slice(-4)}` : value
}
/** Where an account's normalized available balance comes from, in the words account pages use. */
export const availableSourceLabels = { wallet: 'wallet', margin: 'reported margin', withdrawable: 'reported withdrawal' } as const
/** When a reported figure was read: "as of 14:02", or "as of 18 Oct 14:02" on an earlier day. */
export function asOf(value: string, now = new Date()): string {
  const at = new Date(value)
  const clock = at.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })
  return `as of ${at.toDateString() === now.toDateString() ? clock : `${at.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })} ${clock}`}`
}
/** The account state shown in rows and detail. */
export const accountStatus = (account: BrokerAccount) => account.isEnabled === false ? 'Disabled' : account.syncStatus === 'manual' ? 'Manual' :
  account.syncStatus === 'synced' ? 'Read-only' : account.syncStatus === 'error' ? 'Refresh failed' : 'Not refreshed'

export const accountStatusTone = (account: BrokerAccount) => account.isEnabled === false || account.syncStatus === 'error' ? 'warning-badge' :
  account.syncStatus === 'synced' ? 'positive-badge' : ''
