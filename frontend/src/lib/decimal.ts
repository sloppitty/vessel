/** Sign of a validated plain decimal string, without floating-point rounding. Null is unknown. */
export function decimalSign(value: string | null): -1 | 0 | 1 {
  if (value === null || !/[1-9]/.test(value)) return 0
  return value.startsWith('-') ? -1 : 1
}

/** Compare validated plain decimals without rounding an authoritative value. */
export function decimalLess(a: string, b: string): boolean {
  const scale = Math.max(a.split('.')[1]?.length ?? 0, b.split('.')[1]?.length ?? 0)
  const integer = (value: string) => {
    const [whole, fraction = ''] = value.split('.')
    return BigInt(`${whole}${fraction.padEnd(scale, '0')}`)
  }
  return integer(a) < integer(b)
}

/**
 * An exact value as a fraction of integers, for money derived from plain decimal text: products, sums and the divisions a
 * plan needs (a percentage over the leverage, a margin over the average entry) stay exact until one rounding for display.
 * The denominator is positive and the fraction is in lowest terms, so equal values compare equal.
 */
export interface Exact { readonly n: bigint; readonly d: bigint }

const gcd = (a: bigint, b: bigint): bigint => {
  let [x, y] = [a < 0n ? -a : a, b < 0n ? -b : b]
  while (y !== 0n) [x, y] = [y, x % y]
  return x
}

function reduce(n: bigint, d: bigint): Exact {
  if (d === 0n) throw new RangeError('Division by zero')
  const sign = d < 0n ? -1n : 1n
  const divisor = gcd(n, d) || 1n
  return { n: sign * n / divisor, d: sign * d / divisor }
}

export const exactZero: Exact = { n: 0n, d: 1n }
export const exactInteger = (value: number | bigint): Exact => ({ n: BigInt(value), d: 1n })

/** A plain decimal string (`-?digits[.digits]`), exactly; anything else is null. */
export function exactDecimal(text: string): Exact | null {
  const match = /^(-?)(\d+)(?:\.(\d+))?$/.exec(text)
  if (!match) return null
  const [, sign, whole, fraction = ''] = match
  const n = BigInt(`${whole}${fraction}`) * (sign ? -1n : 1n)
  return reduce(n, 10n ** BigInt(fraction.length))
}

/** A decimal with an optional sign and exponent, as manual execution facts allow (`+1.5`, `2e3`), exactly; else null. */
export function exactScientific(text: string): Exact | null {
  const match = /^([+-]?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(text)
  if (!match) return null
  const [, sign, whole, fraction = '', exponent = '0'] = match
  const shift = Number(exponent) - fraction.length
  if (!Number.isSafeInteger(shift) || Math.abs(shift) > 400) return null
  const n = BigInt(`${whole}${fraction}`) * (sign === '-' ? -1n : 1n)
  return shift >= 0 ? reduce(n * 10n ** BigInt(shift), 1n) : reduce(n, 10n ** BigInt(-shift))
}

export const exactAdd = (a: Exact, b: Exact) => reduce(a.n * b.d + b.n * a.d, a.d * b.d)
export const exactSub = (a: Exact, b: Exact) => reduce(a.n * b.d - b.n * a.d, a.d * b.d)
export const exactMul = (a: Exact, b: Exact) => reduce(a.n * b.n, a.d * b.d)
/** Null when dividing by zero. */
export const exactDiv = (a: Exact, b: Exact): Exact | null => b.n === 0n ? null : reduce(a.n * b.d, a.d * b.n)
export const exactSignOf = (value: Exact): -1 | 0 | 1 => value.n > 0n ? 1 : value.n < 0n ? -1 : 0
export const exactCompare = (a: Exact, b: Exact) => exactSignOf(exactSub(a, b))
export const exactAbs = (value: Exact): Exact => value.n < 0n ? { n: -value.n, d: value.d } : value

/**
 * The value as a plain decimal string with at most `places` decimals, rounded half away from zero once, trailing zeros
 * dropped. A non-zero value that rounds to zero prints as `0`; read its sign with `exactSignOf`, never from this text.
 */
export function exactFixed(value: Exact, places: number): string {
  const scale = 10n ** BigInt(places)
  const magnitude = value.n < 0n ? -value.n : value.n
  const scaled = magnitude * scale
  let units = scaled / value.d
  if ((scaled % value.d) * 2n >= value.d) units += 1n
  const digits = units.toString().padStart(places + 1, '0')
  const whole = digits.slice(0, digits.length - places)
  const fraction = places ? digits.slice(digits.length - places).replace(/0+$/, '') : ''
  const text = fraction ? `${whole}.${fraction}` : whole
  return value.n < 0n && units !== 0n ? `-${text}` : text
}

/** A display estimate of the exact value, for ratios and geometry only; never for a sign or an amount shown as exact. */
export function exactNumber(value: Exact) {
  const [n, d] = [Number(value.n), Number(value.d)]
  return Number.isFinite(n) && Number.isFinite(d) ? n / d : Number(exactFixed(value, 20))
}
