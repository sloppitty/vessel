import { decimalSign } from '@/lib/decimal'
import { multiple } from '@/features/plays/receipt-format'

export const tone = (value: string | null) => {
  const sign = decimalSign(value)
  return sign === 0 ? '' : sign < 0 ? 'negative' : 'positive'
}

/** Review loss and drawdown magnitudes arrive unsigned; genuine zero has no minus sign. */
export const negative = (value: string | null) => value === null ? null : decimalSign(value) === 0 ? '0' : `-${value}`

/** R as the Play receipt prints it ("+1.98R"), never as a "×" multiple. */
export const reviewR = (value: string | null) => value === null ? 'N/A' : multiple(value)
