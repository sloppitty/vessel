import { describe, expect, it } from 'vitest'
import { isManualExecutions, type ManualExecutions } from './diary'
import { isPlayExecution, isPlayReceipt, type PlayReceipt } from './plays'
import { savedPlayFixture } from '@/test/workspace-fixture'
import recorded from '@/test/contracts/monitor-api.json?raw'

const execution = (JSON.parse(recorded) as { playExecution: Record<string, unknown> & { entries: Record<string, unknown>[] } }).playExecution
const known: PlayReceipt = { firstEntryAtUtc: '2026-10-03T10:00:00Z', lastExitAtUtc: '2026-10-03T12:00:00Z', averageEntryPrice: '100.4',
  averageExitPrice: '110', fillCount: 3, estimatedR: '-1.25', initialRisk: '2.51', estimatedRReason: null }
const unknown: PlayReceipt = { ...known, estimatedR: null, initialRisk: null, estimatedRReason: 'Known once the Play is closed and its fills settle it.' }

describe('Play receipt (phase 1, 1.1a)', () => {
  it('accepts the recorded server execution with planned entries and its receipt, and an older one without them', () => {
    expect(isPlayExecution(execution)).toBe(true)
    expect(execution.entries[0]).toMatchObject({ plannedPrice: '100', plannedQuantity: '7.5', planRevision: 1 })
    const older: Record<string, unknown> = { ...execution, entries: execution.entries.map(entry => {
      const copy = { ...entry }
      delete copy.plannedPrice; delete copy.plannedQuantity; delete copy.planRevision
      return copy
    }) }
    delete older.receipt
    expect(isPlayExecution(older)).toBe(true)
    expect(isPlayExecution({ ...execution, receipt: known })).toBe(true)
    expect(isPlayExecution({ ...execution, entries: [{ ...execution.entries[0], plannedQuantity: '1e2' }] })).toBe(false)
    expect(isPlayExecution({ ...execution, entries: [{ ...execution.entries[0], planRevision: -1 }] })).toBe(false)
  })

  it.each([
    { ...known, estimatedRReason: 'Known R has no reason.' },
    { ...unknown, estimatedRReason: null },
    { ...known, initialRisk: null },
    { ...unknown, initialRisk: '2.51' },
    { ...known, estimatedR: 1.25 },
    { ...known, fillCount: -1 },
    { ...known, firstEntryAtUtc: 'yesterday' },
  ])('rejects an inconsistent receipt %#', value => {
    expect(isPlayReceipt(value)).toBe(false)
  })

  it('accepts a manual ledger with entries and a receipt, and one from an older server', () => {
    const ledger: ManualExecutions = { play: savedPlayFixture, records: [], history: [], notice: 'Manual facts only.',
      totals: { enteredQuantity: '0', exitedQuantity: '0', openQuantity: '0', grossResultUsd: '0', netResultUsd: null, feesComplete: false } }
    expect(isManualExecutions(ledger)).toBe(true)
    expect(isManualExecutions({ ...ledger, entries: [{ entryId: 'entry-1', filledQuantity: '0', averageFillPrice: null, restingOrders: 0,
      plannedPrice: '100', plannedQuantity: '7.5', planRevision: 1 }], receipt: unknown })).toBe(true)
    expect(isManualExecutions({ ...ledger, receipt: { ...unknown, estimatedRReason: null } })).toBe(false)
    expect(isManualExecutions({ ...ledger, entries: 'none' })).toBe(false)
  })
})
