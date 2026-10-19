import { useEffect, useState } from 'react'
import type { ManualExecutionRecord } from '@/api/diary'
import type { PlayExecution } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { exactAdd, exactDecimal, exactDiv, exactMul, exactScientific, exactSignOf, exactZero, type Exact } from '@/lib/decimal'
import type { EntryFills } from './position-risk-summary'

/*
 * Which entries have filled, and at what average price, as the server decides it per entry (phase 0, 0.5b): a filled
 * entry's stops may sit at or beyond it, and the Summary measures them from its average fill.
 */

/**
 * A venue Play: the execution's per-entry filled quantity and average fill, which count only the linked fills that do not
 * contradict the entry side, as the server's rule does. Links with fills are not counted again.
 */
export function executionFills(execution: PlayExecution): EntryFills {
  const filled = new Set<string>()
  const averages = new Map<string, Exact>()
  for (const entry of execution.entries) {
    if (!/[1-9]/.test(entry.filledQuantity)) continue
    filled.add(entry.entryId)
    const average = entry.averageFillPrice === null ? null : exactDecimal(entry.averageFillPrice)
    if (average !== null && exactSignOf(average) > 0) averages.set(entry.entryId, average)
  }
  return { filled, averages }
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
 * The manual ledger's entry fills for a Play that can have fills, read again whenever its saved version moves (a manual
 * write or a rebind); the last answer for the same Play stays until the new one arrives. `'unknown'` before the first
 * answer or when the ledger cannot be read; null without a Play to read.
 */
export function useLedgerFills(api: WorkspaceApi, playId: string | null, version: number): EntryFills | 'unknown' | null {
  const [state, setState] = useState<{ api: WorkspaceApi; playId: string; fills: EntryFills } | null>(null)
  useEffect(() => {
    if (!playId) return
    const controller = new AbortController()
    api.manualExecutions(playId, controller.signal).then(ledger => {
      if (!controller.signal.aborted && ledger.play.summary.id === playId) setState({ api, playId, fills: ledgerFills(ledger.records) })
    }).catch(() => { /* Unknown stays unknown; the Execution tab shows the ledger's own error. */ })
    return () => controller.abort()
  }, [api, playId, version])
  if (!playId) return null
  return state?.api === api && state.playId === playId ? state.fills : 'unknown'
}
