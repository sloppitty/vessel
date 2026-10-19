import { renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { ManualExecutionRecord, ManualExecutions } from '@/api/diary'
import type { ExecutionFill, OrderLink, PlayExecution } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { exactDecimal, exactFixed } from '@/lib/decimal'
import { savedPlayFixture } from '@/test/workspace-fixture'
import { executionFills, ledgerFills, useLedgerFills } from './entry-fills'

const at = '2026-10-19T10:00:00Z'
const record = (entryId: string | null, quantity: string, price: string, role: 'entry' | 'exit' = 'entry'): ManualExecutionRecord => ({
  id: `${entryId}-${quantity}-${price}`, role, entryId, occurredAtUtc: at, side: role === 'entry' ? 'buy' : 'sell', quantity, price,
  fee: null, feeCurrency: null, source: 'Statement', createdAtUtc: at, updatedAtUtc: at })
const fill = (quantity: string, price: string, side: 'buy' | 'sell' = 'buy', positionEffect: ExecutionFill['positionEffect'] = 'open'): ExecutionFill =>
  ({ sourceFillId: `${quantity}@${price}`, direction: 'Open Long', price, quantity, fee: '0', feeToken: 'USDC', closedPnlUsd: '0',
    occurredAtUtc: at, side, positionEffect, pnlBasis: 'gross' })
const entryLink = (entryId: string, fills: ExecutionFill[]): OrderLink => ({ id: `00000000-0000-4000-8000-0000000000${entryId === 'e1' ? 'a1' : 'a2'}`,
  role: 'entry', entryId, levelId: null, state: 'linked', source: 'automatic', order: null, filledQuantity: '0', fills })

describe('entry fills', () => {
  it('averages a venue entry exactly from its linked fills, leaving out contradicting ones', () => {
    // The server reports the 28-place average 100; the exact average is a hair above it.
    const execution = { entries: [
      { entryId: 'e1', filledQuantity: '3', averageFillPrice: '100', restingOrders: 0 },
      { entryId: 'e2', filledQuantity: '0', averageFillPrice: null, restingOrders: 1 },
      { entryId: 'e3', filledQuantity: '1', averageFillPrice: '101', restingOrders: 0 },
    ], links: [
      entryLink('e1', [fill('2', '100'), fill('1', '100.0000000000000000000000000001'), fill('5', '90', 'sell', 'close')]),
      entryLink('e2', [fill('1', '99', 'buy', 'flip')]),
    ] } as unknown as PlayExecution
    const fills = executionFills(execution, 'long')
    expect([...fills.filled]).toEqual(['e1', 'e3'])
    expect(exactFixed(fills.averages.get('e1')!, 40)).toBe('100.0000000000000000000000000000333333333333')
    // Without listed fills that add up to the reported quantity, the reported average is used and marked as rounded.
    expect(fills.averages.get('e3')).toEqual(exactDecimal('101'))
    expect([...fills.rounded!]).toEqual(['e3'])
    // A short enters by selling, so the same buy fills do not count.
    expect(executionFills(execution, 'short').rounded).toEqual(new Set(['e1', 'e3']))
  })

  it('averages each entry\'s manual executions exactly and ignores exits', () => {
    const fills = ledgerFills([record('e1', '1', '101'), record('e1', '2', '1.03e2'), record('e2', '+0.5', '99'), record(null, '3', '120', 'exit')])
    expect([...fills.filled]).toEqual(['e1', 'e2'])
    expect(exactFixed(fills.averages.get('e1')!, 28)).toBe('102.3333333333333333333333333333')
    expect(exactFixed(fills.averages.get('e2')!, 28)).toBe('99')
  })

  it('counts a ledger answer only for the version it was read at, and never keeps it through a failed reread', async () => {
    const ledger = (version: number, price: string): ManualExecutions => ({ play: { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, version } },
      records: [record('entry-1', '1', price)], history: [], totals: { enteredQuantity: '1', exitedQuantity: '0', openQuantity: '1',
        grossResultUsd: null, netResultUsd: null, feesComplete: true }, notice: 'Manual facts only.' })
    const manualExecutions = vi.fn<WorkspaceApi['manualExecutions']>().mockResolvedValueOnce(ledger(3, '100'))
      .mockRejectedValueOnce(new Error('Ledger unavailable'))
    const api = { manualExecutions } as unknown as WorkspaceApi
    const { result, rerender } = renderHook(({ version }) => useLedgerFills(api, savedPlayFixture.summary.id, version), { initialProps: { version: 3 } })
    expect(result.current).toBe('unknown')
    await waitFor(() => expect(result.current).not.toBe('unknown'))
    expect((result.current as Exclude<typeof result.current, 'unknown' | null>).averages.get('entry-1')).toEqual(exactDecimal('100'))
    // The entry price was corrected (version 4) and the reread failed: unknown, not the old average.
    rerender({ version: 4 })
    await waitFor(() => expect(manualExecutions).toHaveBeenCalledTimes(2))
    expect(result.current).toBe('unknown')
  })
})
