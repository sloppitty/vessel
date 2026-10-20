/*
 * One grammar for plan numbers (phase 0, 0.5a). The server stores a plan number only as blank or an unsigned plain
 * decimal (`ExactDecimal.IsPlanText`); the shared list in `test/contracts/plan-numbers.json` is checked by both
 * runtimes. The browser trims surrounding whitespace before saving and reads plan fields with the same rule, so
 * estimates, the chart and the server agree on what a field holds. Stored text from before the rule still loads and
 * saves unchanged; see `planTextState`.
 */

import { exactDecimal, type Exact } from '@/lib/decimal'

/** The longest plan number text the server stores. */
export const planTextLength = 64
const maxCoefficient = '79228162514264337593543950335'

/** The server's message after a field name, for the same refusal. */
export const plainDecimalRule =
  'use a plain decimal such as 100 or 0.5, with no sign, exponent or spaces, at most 64 characters and within decimal precision (28 decimal places).'

/**
 * Plan number text as the server accepts it on write: blank, or ASCII digits with at most one inner '.', at most 64
 * characters, that a C# decimal holds exactly (28 decimal places, a 96-bit coefficient). No sign, exponent or whitespace.
 */
export function isPlanText(text: string): boolean {
  if (text === '') return true
  if (text.length > planTextLength || !/^[0-9]+(\.[0-9]+)?$/.test(text)) return false
  const [whole = '', fraction = ''] = text.split('.')
  const places = fraction.replace(/0+$/, '')
  const coefficient = (whole + places).replace(/^0+/, '')
  return places.length <= 28 && (coefficient.length < maxCoefficient.length ||
    coefficient.length === maxCoefficient.length && coefficient <= maxCoefficient)
}

/** A minus sign before an otherwise valid, non-zero plain decimal: the one text that gets the negative-percentage reason. */
export const isNegativePlanText = (text: string) => text.startsWith('-') && isPlanText(text.slice(1)) && /[1-9]/.test(text.slice(1))

/** A plan field read as the browser saves it: trimmed plan text. Blank and anything else are unknown. */
export function planNumber(value: string): number | null {
  const text = value.trim()
  return text !== '' && isPlanText(text) ? Number(text) : null
}

/** A plan field as an exact value, read like `planNumber`: trimmed plan text, otherwise unknown. */
export function exactPlanNumber(value: string): Exact | null {
  const text = value.trim()
  return text !== '' && isPlanText(text) ? exactDecimal(text) : null
}

/** Above zero. Plan text is always finite; the finite profiles below are kept for their callers' intent. */
export function positivePlanNumber(value: string): number | null {
  const number = planNumber(value)
  return number !== null && number > 0 ? number : null
}

/** Chart levels and whole-position estimates require a finite display number. */
export function finitePlanNumber(value: string): number | null {
  const number = planNumber(value)
  return number !== null && Number.isFinite(number) ? number : null
}

export function positiveFinitePlanNumber(value: string): number | null {
  const number = finitePlanNumber(value)
  return number !== null && number > 0 ? number : null
}

/** Zero is a valid share; a blank share is unknown. Exact allocation remains the summary's job. */
export function planShare(value: string): number | null {
  const number = planNumber(value)
  return number !== null && number <= 100 ? number : null
}

const wholeDigits = new Intl.NumberFormat('en-US', { useGrouping: false, maximumFractionDigits: 0 })

/**
 * A number the browser writes into a plan field: at most `decimals` places (rounded as `toFixed` does), trailing zeros
 * dropped, never an exponent. `String(Number(x.toFixed(d)))` would print 5e-7 for a tiny quantity.
 */
export function formatPlanNumber(value: number, decimals: number): string {
  if (!Number.isFinite(value)) return ''
  // toFixed switches to an exponent from 1e21, where a double is a whole number: print its shortest digits instead.
  const text = Math.abs(value) >= 1e21 ? wholeDigits.format(value) : value.toFixed(decimals)
  const trimmed = text.includes('.') ? text.replace(/\.?0+$/, '') : text
  return trimmed === '-0' ? '0' : trimmed
}

/**
 * Whether a plan field saves. Its trimmed text is plan text (`valid`), or it is the stored text of the same field,
 * unchanged (`kept`: saved before the rule; it still saves, but estimates here ignore it), or it cannot be saved
 * (`invalid`, with the server's reason). `percent` names a stop or target in %, which cannot be negative.
 */
export type PlanTextState = { status: 'valid' } | { status: 'kept' } | { status: 'invalid'; reason: string }

export function planTextState(value: string, label: string, stored?: string | null, percent?: 'stop' | 'target'): PlanTextState {
  if (isPlanText(value)) return { status: 'valid' }
  // Stored text that is not plan text, such as 1e2 or "1\n", goes back untrimmed and unchanged.
  if (stored != null && !isPlanText(stored) && (value === stored || value.trim() === stored)) return { status: 'kept' }
  const text = value.trim()
  if (isPlanText(text)) return { status: 'valid' }
  // The grammar decides first, as on the server: -1e30 gets the plain-decimal reason, -5 the sign reason.
  if (percent && isNegativePlanText(text)) return { status: 'invalid', reason: percent === 'stop'
    ? `${label}: use a price for a stop beyond the entry; a percentage cannot be negative.`
    : `${label}: a percentage cannot be negative; use a price instead.` }
  return { status: 'invalid', reason: `${label}: ${plainDecimalRule}` }
}

/** The title of a field whose stored text predates the plain-decimal rule. Control characters are shown escaped. */
export function keptPlanTextTitle(value: string) {
  const shown = JSON.stringify(value).slice(1, -1)
  return planNumber(value) === null
    ? `Saved earlier as “${shown}”, which is not a plain decimal. It saves unchanged, but estimates here ignore it; enter a plain decimal to use it.`
    : `Saved earlier as “${shown}”, which is not a plain decimal. It saves unchanged; enter a plain decimal to replace it.`
}

/**
 * Whether an `<input type="number">` shows this text. It blanks anything else (`+100`, `100.`, `1.e1`, `1\n`) while the
 * draft keeps it, so such a field is rendered as text instead (`planInputType`).
 */
export function numberInputShows(value: string) {
  return value === '' || /^-?(?:[0-9]+|[0-9]*\.[0-9]+)(?:[eE][-+]?[0-9]+)?$/.test(value) && Number.isFinite(Number(value))
}

/** Input attributes for a plan number field: a number input unless its text would be blanked by one. */
export const planInputType = (value: string) =>
  numberInputShows(value) ? { type: 'number' as const } : { type: 'text' as const, inputMode: 'decimal' as const }

// .NET NumberStyles.Float: ASCII white space around an optional sign, digits with a point on either side, an exponent.
const serverReadPattern = /^[\t\n\v\f\r ]*[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?[\t\n\v\f\r ]*$/
const decimalMax = 7.922816251426434e28

/**
 * Whether the server's decimal parse reads the text (already in `serverReadPattern`) as zero. Decimal keeps 28 places
 * and rounds half to even on the exact digits, so a magnitude below 5e-29, or exactly 5e-29 (the midpoint between 0 and
 * 1e-28, where even 0 wins), is zero. Decided from the digits and the exponent, never through a double: `5e-29` is zero
 * there, `5.0000000000000000000001e-29` is not, and `1e-99999` needs no precision.
 */
function readsAsDecimalZero(text: string): boolean {
  const [, whole = '', fraction = '', exponent = '0'] = /^[+-]?([0-9]*)\.?([0-9]*)(?:[eE]([+-]?[0-9]+))?$/.exec(text.trim()) ?? []
  const digits = whole + fraction
  const first = digits.search(/[1-9]/)
  if (first < 0) return true
  // The power of ten of the first significant digit.
  const power = BigInt(whole.length - 1 - first) + BigInt(exponent)
  if (power !== -29n) return power < -29n
  return digits[first]! < '5' || digits[first] === '5' && !/[1-9]/.test(digits.slice(first + 1))
}

/**
 * A plan field read as the server reads stored text (`decimal.TryParse` with `NumberStyles.Float`), within decimal
 * range. Zero is decided exactly from the text, as the server rounds it; other magnitudes are doubles, which stand in
 * for decimal at its precision edge. Only the side checks use it, so a stored `1e2` entry meets a new stop at 105 here
 * as it does on the server, and a stored `5e-29` % stop is a 0 % stop. Estimates keep reading plan text only
 * (`planNumber`).
 */
export function serverPlanNumber(value: string): number | null {
  if (!serverReadPattern.test(value)) return null
  const number = Number(value.trim())
  if (!Number.isFinite(number) || Math.abs(number) > decimalMax) return null
  return readsAsDecimalZero(value) ? 0 : number
}

export function serverPositivePlanNumber(value: string): number | null {
  const number = serverPlanNumber(value)
  return number !== null && number > 0 ? number : null
}
