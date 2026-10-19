/*
 * One grammar for plan numbers (phase 0, 0.5a). The server stores a plan number only as blank or an unsigned plain
 * decimal (`ExactDecimal.IsPlanText`); the shared list in `test/contracts/plan-numbers.json` is checked by both
 * runtimes. The browser trims surrounding whitespace before saving and reads plan fields with the same rule, so
 * estimates, the chart and the server agree on what a field holds. Stored text from before the rule still loads and
 * saves unchanged; see `planTextState`.
 */

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

/** A plan field read as the browser saves it: trimmed plan text. Blank and anything else are unknown. */
export function planNumber(value: string): number | null {
  const text = value.trim()
  return text !== '' && isPlanText(text) ? Number(text) : null
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
  if (isPlanText(value.trim())) return { status: 'valid' }
  if (stored != null && (value === stored || value.trim() === stored)) return { status: 'kept' }
  const number = Number(value.trim())
  if (percent && value.trim() !== '' && Number.isFinite(number) && number < 0) return { status: 'invalid', reason: percent === 'stop'
    ? `${label}: use a price for a stop beyond the entry; a percentage cannot be negative.`
    : `${label}: a percentage cannot be negative; use a price instead.` }
  return { status: 'invalid', reason: `${label}: ${plainDecimalRule}` }
}

/** The title of a field whose stored text predates the plain-decimal rule. */
export const keptPlanTextTitle = (value: string) =>
  `Saved earlier as “${value}”, which is not a plain decimal. It saves unchanged, but estimates here ignore it; enter a plain decimal to use it.`
