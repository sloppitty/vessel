import type { BrokerAccount, Portfolio } from '@/api/workspace'
import { balanceShare } from './overview-share'

/** Categorical slots validated for adjacent slices on the card surface; a seventh entity folds into Other. */
export const allocationSlots = 6

export interface AllocationSlice {
  id: string; name: string; balanceUsd: string; share: string
  /** 0–1, for drawing only; labels use the exact share. */
  fraction: number
  /** 1–6 for a categorical slot, null for Other. */
  slot: number | null
  /** Scope to open when the slice is chosen; Other has none. */
  scope: string | null
  venueId?: string | undefined; icon?: string | undefined
  /** An account's public address or index (null for manual records); absent for portfolio and Other slices. */
  source?: string | null | undefined
  /** Accounts the slice stands for: 1 for an account, the members of a portfolio, or the parts folded into Other. */
  accountCount: number
  /** IDs folded into Other. */
  members?: string[] | undefined
}
export interface Allocation {
  id: string; name: string; icon?: string | undefined; scope: string
  totalUsd: string | null; accountCount: number
  slices: AllocationSlice[]
  /** Enabled accounts without a reported balance, and groups with zero or negative balance. Neither is drawn. */
  missingCount: number; notDrawn: { name: string; balanceUsd: string }[]
  /** Display share of every drawn part by ID, including those folded into Other. */
  shares: Record<string, string>
}

const scale = 28
const units = (value: string) => {
  const negative = value.startsWith('-')
  const [whole, fraction = ''] = (negative ? value.slice(1) : value).split('.')
  const magnitude = BigInt(whole + fraction.slice(0, scale).padEnd(scale, '0'))
  return negative ? -magnitude : magnitude
}
const text = (value: bigint) => {
  const negative = value < 0n
  const digits = (negative ? -value : value).toString().padStart(scale + 1, '0')
  const fraction = digits.slice(-scale).replace(/0+$/, '')
  return `${negative ? '-' : ''}${digits.slice(0, -scale)}${fraction ? `.${fraction}` : ''}`
}
/** Exact sum of validated plain decimal strings. */
export const sumDecimals = (values: string[]) => text(values.reduce((sum, v) => sum + units(v), 0n))

interface Part { id: string; name: string; balanceUsd: string | null; accountCount: number; scope: string; venueId?: string | undefined; icon?: string | undefined; source?: string | null | undefined }

/**
 * Folds parts into a pie. A part's colour comes from its position in the stable part order, never its size, so balance
 * changes do not repaint it. Only when more than six parts exist can a part beyond the sixth take a slot left free by
 * a folded or undrawn one.
 */
function allocate(id: string, name: string, scope: string, parts: Part[], icon?: string | undefined): Allocation {
  const valued = parts.filter((p): p is Part & { balanceUsd: string } => p.balanceUsd !== null)
  const missingCount = parts.filter(p => p.balanceUsd === null).reduce((n, p) => n + p.accountCount, 0)
  const positive = valued.filter(p => units(p.balanceUsd) > 0n)
  const notDrawn = valued.filter(p => units(p.balanceUsd) <= 0n).map(p => ({ name: p.name, balanceUsd: p.balanceUsd }))
  const totalUsd = valued.length ? sumDecimals(valued.map(p => p.balanceUsd)) : null
  const accountCount = parts.reduce((n, p) => n + p.accountCount, 0)
  const drawnTotal = positive.reduce((sum, p) => sum + units(p.balanceUsd), 0n)
  if (!positive.length || drawnTotal <= 0n) return { id, name, icon, scope, totalUsd, accountCount, slices: [], missingCount, notDrawn, shares: {} }
  const bySize = [...positive].sort((a, b) => units(b.balanceUsd) > units(a.balanceUsd) ? 1 : units(b.balanceUsd) < units(a.balanceUsd) ? -1 : 0)
  const kept = positive.length > allocationSlots ? new Set(bySize.slice(0, allocationSlots - 1)) : new Set(positive)
  const rest = bySize.filter(p => !kept.has(p))
  const denominator = text(drawnTotal)
  const slice = (p: { id: string; name: string; balanceUsd: string; accountCount: number }, slot: number | null, scope: string | null, extra: Partial<AllocationSlice> = {}): AllocationSlice => ({
    id: p.id, name: p.name, balanceUsd: p.balanceUsd, accountCount: p.accountCount, slot, scope, ...extra,
    share: balanceShare({ numerator: p.balanceUsd, denominator }).label,
    fraction: Number(units(p.balanceUsd) * 1_000_000n / drawnTotal) / 1_000_000,
  })
  const order = new Map(parts.map((p, i) => [p, i]))
  const slots = new Map<Part, number>()
  for (const p of kept) if (order.get(p)! < allocationSlots) slots.set(p, order.get(p)! + 1)
  const free = Array.from({ length: allocationSlots }, (_, i) => i + 1).filter(n => ![...slots.values()].includes(n))
  for (const p of positive) if (kept.has(p) && !slots.has(p)) slots.set(p, free.shift()!)
  const shares = Object.fromEntries(positive.map(p => [p.id, balanceShare({ numerator: p.balanceUsd, denominator }).label]))
  const slices = bySize.filter(p => kept.has(p)).map(p => slice(p, slots.get(p)!, p.scope, { venueId: p.venueId, icon: p.icon, source: p.source }))
  if (rest.length) slices.push(slice({ id: `${id}:other`, name: `Other (${rest.length})`, balanceUsd: sumDecimals(rest.map(p => p.balanceUsd)), accountCount: rest.reduce((n, p) => n + p.accountCount, 0) }, null, null, { members: rest.map(p => p.id) }))
  return { id, name, icon, scope, totalUsd, accountCount, slices, missingCount, notDrawn, shares }
}

const accountPart = (a: BrokerAccount): Part => ({ id: a.id, name: a.name, balanceUsd: a.balanceUsd ?? null, accountCount: 1, scope: `account:${a.id}`, venueId: a.venueId, source: a.tradingMode === 'paper' ? undefined : a.sourceId ?? a.address })

/** One pie per portfolio (plus Unassigned) of each enabled account's share of that portfolio's balance. */
export function portfolioAllocations(accounts: BrokerAccount[], portfolios: Portfolio[]): Allocation[] {
  const enabled = accounts.filter(a => a.isEnabled !== false)
  const groups = portfolios.map(p => ({ id: p.id, name: p.name, icon: p.icon ?? 'folder', scope: `portfolio:${p.id}`, members: enabled.filter(a => a.portfolioId === p.id) }))
  const unassigned = enabled.filter(a => a.portfolioId === null)
  if (unassigned.length) groups.push({ id: 'unassigned', name: 'Unassigned', icon: 'folder', scope: 'unassigned', members: unassigned })
  return groups.filter(g => g.members.length).map(g => allocate(g.id, g.name, g.scope, g.members.map(accountPart), g.icon))
}

/** Each portfolio's share of every enabled account's balance; parts are the portfolio pies' totals. */
export function totalAllocation(groups: Allocation[]): Allocation {
  const total = allocate('all', 'All accounts', 'all', groups.map(g => ({
    id: g.id, name: g.name, icon: g.icon, scope: g.scope, accountCount: g.accountCount, balanceUsd: g.totalUsd,
  })))
  // A portfolio total already leaves out its accounts without a balance; count those accounts, not whole portfolios.
  return { ...total, missingCount: groups.reduce((n, g) => n + g.missingCount, 0) }
}
