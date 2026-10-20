import { describe, expect, it } from 'vitest'
import type { SizingDocument } from '@/api/sizing'
import { createDraft } from './draft'
import { preflightChecks, withIfThen } from './preflight'

const base = createDraft()
const entry = { ...base.entries[0]!, price: '100', share: '100',
  stops: [{ ...base.entries[0]!.stops[0]!, value: '95', share: '100' }], targets: [{ ...base.entries[0]!.targets[0]!, value: '110', share: '100' }] }
const ready = { ...base, direction: 'long' as const, leverage: '5', entries: [entry],
  notes: { ...base.notes, thesis: 'Breakout retest', invalidation: 'Close below 94' } }
const sizing = { settings: { riskPercent: '1.25' }, exposure: { multiplier: '0.5' } } as unknown as SizingDocument
const outcomes = (checks: ReturnType<typeof preflightChecks>) => Object.fromEntries(checks.map(check => [check.code, check.outcome]))

describe('pre-flight at Plan it (phase 1, 1.5)', () => {
  it('passes a complete plan within the setting at the ladder level, with the liquidation beyond the stop', () => {
    const checks = preflightChecks({ draft: ready, risk: { riskSharePercent: 0.6 }, sizing, liquidation: 80 })
    expect(checks.map(check => check.code)).toEqual(['priced-stops', 'allocations', 'thesis', 'risk', 'liquidation'])
    expect(checks.every(check => check.outcome === 'passed')).toBe(true)
  })

  it('marks what the plan does not say yet, and never guesses missing inputs', () => {
    const unpriced = { ...ready, entries: [{ ...entry, stops: [{ ...entry.stops[0]!, value: '' }] }] }
    expect(outcomes(preflightChecks({ draft: unpriced, risk: null, sizing: null, liquidation: 80 }))).toEqual({
      'priced-stops': 'skipped', allocations: 'passed', thesis: 'passed', risk: 'unknown', liquidation: 'unknown',
    })
    // 1.25% at the half-size level allows 0.625%; 0.7% is beyond it.
    expect(outcomes(preflightChecks({ draft: ready, risk: { riskSharePercent: 0.7 }, sizing, liquidation: 80 })).risk).toBe('skipped')
    expect(outcomes(preflightChecks({ draft: ready, risk: { riskSharePercent: null }, sizing, liquidation: 80 })).risk).toBe('unknown')
    expect(outcomes(preflightChecks({ draft: ready, risk: { riskSharePercent: 0.6 }, sizing, liquidation: 96 })).liquidation).toBe('skipped')
    expect(outcomes(preflightChecks({ draft: ready, risk: { riskSharePercent: 0.6 }, sizing, liquidation: null })).liquidation).toBe('unknown')
    const shares = { ...ready, entries: [{ ...entry, share: '60' }] }
    expect(outcomes(preflightChecks({ draft: shares, risk: null, sizing, liquidation: 80 })).allocations).toBe('skipped')
    const targets = { ...ready, entries: [{ ...entry, targets: [{ ...entry.targets[0]!, share: '50' }] }] }
    expect(outcomes(preflightChecks({ draft: targets, risk: null, sizing, liquidation: 80 })).allocations).toBe('skipped')
    // No targets, or no stops, total 0%, not 100%.
    expect(outcomes(preflightChecks({ draft: { ...ready, entries: [{ ...entry, targets: [] }] }, risk: null, sizing, liquidation: 80 })).allocations).toBe('skipped')
    expect(outcomes(preflightChecks({ draft: { ...ready, entries: [{ ...entry, stops: [] }] }, risk: null, sizing, liquidation: 80 })).allocations).toBe('skipped')
    const thesis = { ...ready, notes: { ...ready.notes, invalidation: '  ' } }
    expect(outcomes(preflightChecks({ draft: thesis, risk: null, sizing, liquidation: 80 })).thesis).toBe('skipped')
  })

  it('judges priced stops per priced entry, leaving shares to the allocation check', () => {
    // Half the position at 100, with its stop priced: the stop is there, the shares are what is missing.
    const half = { ...ready, entries: [{ ...entry, share: '50' }] }
    expect(outcomes(preflightChecks({ draft: half, risk: null, sizing, liquidation: 80 }))).toMatchObject({ 'priced-stops': 'passed', allocations: 'skipped' })
    const unpricedEntry = { ...ready, entries: [{ ...entry, price: '' }] }
    expect(outcomes(preflightChecks({ draft: unpricedEntry, risk: null, sizing, liquidation: 80 }))['priced-stops']).toBe('unknown')
    const second = { ...ready, entries: [{ ...entry, share: '50' }, { ...entry, id: 'entry-2', share: '50', stops: [] }] }
    expect(outcomes(preflightChecks({ draft: second, risk: null, sizing, liquidation: 80 }))['priced-stops']).toBe('skipped')
  })

  it('meets the liquidation check only when every stop is priced, and never hides a stop past it', () => {
    const halves = (second: string) => ({ ...ready, entries: [{ ...entry, stops: [{ ...entry.stops[0]!, share: '50' },
      { ...entry.stops[0]!, id: 'second', value: second, share: '50' }] }] })
    expect(outcomes(preflightChecks({ draft: halves('90'), risk: null, sizing, liquidation: 80 })).liquidation).toBe('passed')
    expect(outcomes(preflightChecks({ draft: halves(''), risk: null, sizing, liquidation: 80 })).liquidation).toBe('unknown')
    expect(outcomes(preflightChecks({ draft: halves(''), risk: null, sizing, liquidation: 96 })).liquidation).toBe('skipped')
    expect(outcomes(preflightChecks({ draft: { ...ready, entries: [{ ...entry, stops: [] }] }, risk: null, sizing, liquidation: 80 })).liquidation).toBe('unknown')
  })

  it('appends a next action as an if-then the owner completes, naming its source', () => {
    expect(withIfThen('', { nextAction: ' Wait for the retest ', title: 'BTC breakout' })).toBe('If …, then Wait for the retest (from BTC breakout)')
    expect(withIfThen('Breakout retest\n', { nextAction: 'Size down after two losses', title: '' }))
      .toBe('Breakout retest\n\nIf …, then Size down after two losses (from an earlier Play)')
  })
})
