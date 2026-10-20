import { describe, expect, it } from 'vitest'
import { isPlayLocalResult } from './local-result'
import { isPlayExecution } from './plays'
import recorded from '@/test/contracts/monitor-api.json?raw'

const execution = (JSON.parse(recorded) as { playExecution: Record<string, unknown> & { totals: Record<string, unknown> } }).playExecution
const known = { netUsd: '-2', basis: 'executions', reason: null, signDiffers: true }
const unknown = { netUsd: null, basis: 'executions', reason: 'The play is not closed.', signDiffers: false }

describe('Play-local result', () => {
  it('accepts a known result with a sign difference and an unknown one with its reason', () => {
    expect(isPlayLocalResult(known)).toBe(true)
    expect(isPlayLocalResult(unknown)).toBe(true)
    expect(isPlayLocalResult({ ...known, basis: 'manual-ledger', signDiffers: false })).toBe(true)
  })

  it.each([
    { ...known, reason: 'Known results have no reason.' },
    { ...unknown, reason: null },
    { ...unknown, reason: '   ' },
    { ...unknown, signDiffers: true },
    { ...known, netUsd: -2 },
    { ...known, netUsd: '1e3' },
    { ...known, basis: 'venue' },
    { ...known, signDiffers: 'yes' },
  ])('rejects an inconsistent result %#', value => {
    expect(isPlayLocalResult(value)).toBe(false)
  })

  it('accepts execution totals with both bases, and totals from servers before them', () => {
    const { totals } = execution
    expect(isPlayExecution(execution)).toBe(true)
    const older = { ...totals }
    delete older.venueNetUsd
    delete older.local
    expect(isPlayExecution({ ...execution, totals: older })).toBe(true)
    expect(isPlayExecution({ ...execution, totals: { ...totals, venueNetUsd: '2.9', local: known } })).toBe(true)
    expect(isPlayExecution({ ...execution, totals: { ...totals, venueNetUsd: null, local: unknown } })).toBe(true)
    expect(isPlayExecution({ ...execution, totals: { ...totals, venueNetUsd: 2.9 } })).toBe(false)
    expect(isPlayExecution({ ...execution, totals: { ...totals, local: { ...known, reason: 'x' } } })).toBe(false)
  })
})
