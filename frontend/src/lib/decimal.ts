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

const plainDecimal = /^-?\d+(\.\d+)?$/

/** `a − b` for plain decimal strings, exactly, without trailing zeros. Null when either is not a plain decimal. */
export function decimalDifference(a: string, b: string): string | null {
  if (!plainDecimal.test(a) || !plainDecimal.test(b)) return null
  const scale = Math.max(a.split('.')[1]?.length ?? 0, b.split('.')[1]?.length ?? 0)
  const units = (value: string) => {
    const [whole, fraction = ''] = value.replace('-', '').split('.')
    const magnitude = BigInt(`${whole}${fraction.padEnd(scale, '0')}`)
    return value.startsWith('-') ? -magnitude : magnitude
  }
  const difference = units(a) - units(b)
  const digits = (difference < 0n ? -difference : difference).toString().padStart(scale + 1, '0')
  const whole = digits.slice(0, digits.length - scale)
  const fraction = digits.slice(digits.length - scale).replace(/0+$/, '')
  return `${difference < 0n ? '-' : ''}${whole}${fraction ? `.${fraction}` : ''}`
}
