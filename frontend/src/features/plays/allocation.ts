import { exactAdd, exactCompare, exactDecimal, exactFixed, exactMul, exactSignOf, exactSub, exactZero, type Exact } from '@/lib/decimal'

/**
 * Phase 2, item 11: the browser's preview of a fill allocation. The server decides and stores the split; these helpers only
 * show the owner, as they type, what is left of the fill and how its fee would be shared by the server's rule.
 */

/** A line quantity as the server accepts it: plain decimal digits, above zero. */
export const isAllocationQuantity = (text: string) => /^[0-9]+(\.[0-9]+)?$/.test(text) && /[1-9]/.test(text) && text.length <= 64

/** What is left of the fill after the typed quantities, exactly; null while any typed quantity is not a valid one. */
export function allocationRemainder(fillQuantity: string, quantities: readonly string[]): string | null {
  const total = exactDecimal(fillQuantity)
  if (!total || quantities.some(q => !isAllocationQuantity(q))) return null
  const left = quantities.reduce<Exact>((sum, q) => exactSub(sum, exactDecimal(q)!), total)
  return text(left)
}

/** Decimal places of a plain decimal, trailing zeros aside. */
const places = (value: string) => value.includes('.') ? value.split('.')[1]!.replace(/0+$/, '').length : 0

const text = (value: Exact) => exactFixed(value, decimalsOf(value))

/** The decimals an exact value needs; values here always terminate (their denominators are powers of ten). */
function decimalsOf(value: Exact) {
  let d = value.d
  let count = 0
  while (d % 10n === 0n) { d /= 10n; count++ }
  return d === 1n ? count : 28
}

export interface FeeShare { playId: string; fee: string; remainder: string | null }

/**
 * The fee split the server applies: each share is fee × quantity ÷ the fill's quantity, truncated toward zero to the fee's
 * precision (its own decimals, at least six); the remainder goes to the line with the largest quantity, ties to the lowest
 * Play ID as text. Null while the lines are not valid quantities.
 */
export function feeShares(fee: string, fillQuantity: string, lines: ReadonlyArray<{ playId: string; quantity: string }>): FeeShare[] | null {
  const total = exactDecimal(fillQuantity)
  const amount = exactDecimal(fee)
  if (!total || !amount || exactSignOf(total) <= 0 || lines.length === 0 || lines.some(l => !isAllocationQuantity(l.quantity))) return null
  const scale = 10n ** BigInt(Math.min(28, Math.max(6, places(fee))))
  const shares = lines.map(line => {
    const exact = exactMul(amount, { n: exactDecimal(line.quantity)!.n * total.d, d: exactDecimal(line.quantity)!.d * total.n })
    // BigInt division truncates toward zero, as the server's does, so a rebate is truncated toward zero too.
    return { n: exact.n * scale / exact.d, d: scale }
  }).map(share => share.n === 0n ? exactZero : { n: share.n, d: share.d })
  const remainder = shares.reduce<Exact>((left, share) => exactSub(left, share), amount)
  const target = lines.map((line, index) => ({ line, index })).sort((a, b) =>
    exactCompare(exactDecimal(b.line.quantity)!, exactDecimal(a.line.quantity)!) || (a.line.playId < b.line.playId ? -1 : a.line.playId > b.line.playId ? 1 : 0))[0]!.index
  const nonzero = exactSignOf(remainder) !== 0
  return lines.map((line, index) => ({
    playId: line.playId,
    fee: text(index === target ? exactAdd(shares[index]!, remainder) : shares[index]!),
    remainder: index === target && nonzero ? text(remainder) : null,
  }))
}
