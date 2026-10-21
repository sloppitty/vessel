/**
 * How a venue accepts prices on one instrument: its tick (RISEx, Lighter), or five significant figures with any integer
 * allowed and at most `maxDecimals` decimals (Hyperliquid's 6 − szDecimals; Phase 2, item 14b). Null is five
 * significant figures with at most eight decimals. Prices the owner types are never rounded by it.
 */
export type PriceStep = number | { readonly maxDecimals: number } | null

/** The instrument's tick, or null when prices follow significant figures. */
export const tickOf = (step: PriceStep | undefined): number | null => typeof step === 'number' && step > 0 ? step : null

/** Decimals a five-significant-figure price keeps: none for six digits and more, at most eight and at most the venue's cap. */
export function significantDecimals(price: number, step: PriceStep | undefined): number {
  const cap = step !== null && typeof step === 'object' ? step.maxDecimals : 8
  return Math.max(0, Math.min(8, cap, 4 - Math.floor(Math.log10(price))))
}

/** The decimal cap of a significant-figure venue, or null. */
export const decimalCapOf = (step: PriceStep | undefined): number | null =>
  step !== null && typeof step === 'object' ? step.maxDecimals : null

const caps = new Map<number, { readonly maxDecimals: number }>()

/** An instrument's rule from its catalogue fields, with one stable object per cap so React dependencies compare equal. */
export function priceStepOf(tick: number | null | undefined, maxDecimals: number | null | undefined): PriceStep {
  if (typeof tick === 'number' && tick > 0) return tick
  if (maxDecimals == null) return null
  let rule = caps.get(maxDecimals)
  if (!rule) {
    rule = Object.freeze({ maxDecimals })
    caps.set(maxDecimals, rule)
  }
  return rule
}
