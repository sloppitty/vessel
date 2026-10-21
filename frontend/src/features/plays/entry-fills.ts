import type { PaperPlayState } from '@/api/paper'
import { useEffect, useState } from 'react'
import type { ManualExecutionRecord } from '@/api/diary'
import type { PlayExecution } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { exactAdd, exactCompare, exactDecimal, exactDiv, exactMul, exactScientific, exactSignOf, exactZero, type Exact } from '@/lib/decimal'
import type { EntryFills } from './position-risk-summary'

/*
 * Which entries have filled, and at what average price, as the server decides it per entry (phase 0, 0.5b): a filled
 * entry's stops may sit at or beyond it, and the Summary measures them from its average fill.
 */

/**
 * A venue Play: the fills of its linked entry orders that do not contradict the entry side, as the server counts them (an
 * entry buys for a long and opens; a close or flip contradicts it). Each entry's average fill is their exact quantity ×
 * price over their quantity, not the server's average rounded to 28 places, so a stop at that rounded figure keeps its
 * true sign. Only when the listed fills do not add up to the entry's reported filled quantity (an older server) is the
 * rounded average used, and the entry is marked so the Summary does not claim a sign within that rounding.
 */
export function executionFills(execution: PlayExecution, direction: 'long' | 'short'): EntryFills {
  const entrySide = direction === 'long' ? 'buy' : 'sell'
  const sums = new Map<string, { quantity: Exact; notional: Exact }>()
  for (const link of execution.links) {
    if (link.role !== 'entry' || link.state !== 'linked' || !link.entryId) continue
    for (const fill of link.fills) {
      if (fill.side !== entrySide || fill.positionEffect === 'close' || fill.positionEffect === 'flip') continue
      const [quantity, price] = [exactDecimal(fill.quantity), exactDecimal(fill.price)]
      if (quantity === null || price === null) continue
      const sum = sums.get(link.entryId) ?? { quantity: exactZero, notional: exactZero }
      sums.set(link.entryId, { quantity: exactAdd(sum.quantity, quantity), notional: exactAdd(sum.notional, exactMul(quantity, price)) })
    }
  }
  const filled = new Set<string>()
  const averages = new Map<string, Exact>()
  const rounded = new Set<string>()
  for (const entry of execution.entries) {
    const reported = exactDecimal(entry.filledQuantity)
    if (reported === null || exactSignOf(reported) <= 0) continue
    filled.add(entry.entryId)
    const sum = sums.get(entry.entryId)
    const exact = sum && exactCompare(sum.quantity, reported) === 0 ? exactDiv(sum.notional, sum.quantity) : null
    const average = exact ?? (entry.averageFillPrice === null ? null : exactDecimal(entry.averageFillPrice))
    if (average === null || exactSignOf(average) <= 0) continue
    averages.set(entry.entryId, average)
    if (exact === null) rounded.add(entry.entryId)
  }
  return { filled, averages, rounded }
}

/**
 * A manual Play: each entry execution of the ledger counts for its entry, as on the server, and the entry's average fill
 * is its quantity-weighted price, exactly. An entry whose facts cannot be read has no average.
 */
export function ledgerFills(records: readonly ManualExecutionRecord[]): EntryFills {
  const sums = new Map<string, { quantity: Exact; notional: Exact; readable: boolean }>()
  for (const record of records) {
    if (record.role !== 'entry' || !record.entryId) continue
    const sum = sums.get(record.entryId) ?? { quantity: exactZero, notional: exactZero, readable: true }
    const quantity = exactScientific(record.quantity)
    const price = exactScientific(record.price)
    sums.set(record.entryId, quantity === null || price === null ? { ...sum, readable: false }
      : { quantity: exactAdd(sum.quantity, quantity), notional: exactAdd(sum.notional, exactMul(quantity, price)), readable: sum.readable })
  }
  const averages = new Map<string, Exact>()
  for (const [entryId, sum] of sums) {
    const average = sum.readable ? exactDiv(sum.notional, sum.quantity) : null
    if (average !== null && exactSignOf(average) > 0) averages.set(entryId, average)
  }
  return { filled: new Set(sums.keys()), averages }
}

/**
 * The manual ledger's entry fills for a saved Play, read again whenever its known version moves (a manual write, a
 * correction or a rebind) and whenever the page shows the Play again. Only the answer of the current read attempt counts,
 * and only for the version it was read at: every attempt drops the previous answer as it starts, so while it is pending
 * or after it fails the fills are `'unknown'`, never earlier figures shown as current (another tab may have corrected an
 * entry at the same version). Null without a Play to read.
 */
export function useLedgerFills(api: WorkspaceApi, playId: string | null, version: number): EntryFills | 'unknown' | null {
  const [state, setState] = useState<{ api: WorkspaceApi; playId: string; version: number; fills: EntryFills } | null>(null)
  useEffect(() => {
    if (!playId) return
    const controller = new AbortController()
    api.manualExecutions(playId, controller.signal).then(ledger => {
      // An answer older than the version that asked for it does not describe that version.
      if (!controller.signal.aborted && ledger.play.summary.id === playId && ledger.play.summary.version >= version)
        setState({ api, playId, version, fills: ledgerFills(ledger.records) })
    }).catch(() => { /* Unknown stays unknown; the Execution tab shows the ledger's own error. */ })
    // The next attempt, for any version, starts from unknown: this answer is not reused.
    return () => { controller.abort(); setState(null) }
  }, [api, playId, version])
  if (!playId) return null
  return state?.api === api && state.playId === playId && state.version === version ? state.fills : 'unknown'
}

/** Explicit paper entry facts share only the weighted-average math, never imported venue execution DTOs. */
export function paperEntryFills(state: PaperPlayState): EntryFills {
  const sums = new Map<string, { quantity: Exact; notional: Exact }>()
  for (const execution of state.executions) {
    if (execution.role !== 'entry') continue
    const quantity = exactDecimal(execution.quantity), price = exactDecimal(execution.price)
    if (quantity === null || price === null || exactSignOf(quantity) <= 0) continue
    const sum = sums.get(execution.entryId) ?? { quantity: exactZero, notional: exactZero }
    sums.set(execution.entryId, { quantity: exactAdd(sum.quantity, quantity), notional: exactAdd(sum.notional, exactMul(quantity, price)) })
  }
  const averages = new Map<string, Exact>()
  for (const [id, sum] of sums) { const average = exactDiv(sum.notional, sum.quantity); if (average !== null) averages.set(id, average) }
  return { filled: new Set(sums.keys()), averages }
}
