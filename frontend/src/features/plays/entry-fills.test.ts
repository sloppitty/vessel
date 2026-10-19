import { describe, expect, it } from 'vitest'
import type { ManualExecutionRecord } from '@/api/diary'
import type { PlayExecution } from '@/api/plays'
import { exactDecimal, exactFixed } from '@/lib/decimal'
import { executionFills, ledgerFills } from './entry-fills'

const at = '2026-10-19T10:00:00Z'
const record = (entryId: string | null, quantity: string, price: string, role: 'entry' | 'exit' = 'entry'): ManualExecutionRecord => ({
  id: `${entryId}-${quantity}-${price}`, role, entryId, occurredAtUtc: at, side: role === 'entry' ? 'buy' : 'sell', quantity, price,
  fee: null, feeCurrency: null, source: 'Statement', createdAtUtc: at, updatedAtUtc: at })

describe('entry fills', () => {
  it('takes the execution\'s per-entry filled quantity, which leaves out contradicting fills', () => {
    const entries: PlayExecution['entries'] = [
      { entryId: 'e1', filledQuantity: '0.5', averageFillPrice: '100.25', restingOrders: 0 },
      { entryId: 'e2', filledQuantity: '0', averageFillPrice: null, restingOrders: 1 },
      { entryId: 'e3', filledQuantity: '1', averageFillPrice: null, restingOrders: 0 },
    ]
    const fills = executionFills({ entries } as PlayExecution)
    expect([...fills.filled]).toEqual(['e1', 'e3'])
    expect([...fills.averages]).toEqual([['e1', exactDecimal('100.25')]])
  })

  it('averages each entry\'s manual executions exactly and ignores exits', () => {
    const fills = ledgerFills([record('e1', '1', '101'), record('e1', '2', '1.03e2'), record('e2', '+0.5', '99'), record(null, '3', '120', 'exit')])
    expect([...fills.filled]).toEqual(['e1', 'e2'])
    expect(exactFixed(fills.averages.get('e1')!, 28)).toBe('102.3333333333333333333333333333')
    expect(exactFixed(fills.averages.get('e2')!, 28)).toBe('99')
  })
})
