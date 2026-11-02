/** Above 0 and at most 100, a plain decimal with at most four places; blank clears the limit. */
export function riskLimitText(text: string): string | null | undefined {
  const value = text.trim()
  if (value === '') return null
  if (!/^\d+(\.\d{1,4})?$/.test(value)) return undefined
  const number = Number(value)
  return number > 0 && number <= 100 ? value : undefined
}

