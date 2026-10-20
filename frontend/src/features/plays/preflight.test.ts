import { describe, expect, it } from 'vitest'
import type { SizingDocument } from '@/api/sizing'
import { createDraft } from './draft'
import { preflightChecks, preflightClear, withIfThen } from './preflight'

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
    expect(preflightClear(checks)).toBe(true)
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
    const thesis = { ...ready, notes: { ...ready.notes, invalidation: '  ' } }
    expect(outcomes(preflightChecks({ draft: thesis, risk: null, sizing, liquidation: 80 })).thesis).toBe('skipped')
  })

  it('appends a next action as an if-then the owner completes, naming its source', () => {
    expect(withIfThen('', { nextAction: ' Wait for the retest ', title: 'BTC breakout' })).toBe('If …, then Wait for the retest (from BTC breakout)')
    expect(withIfThen('Breakout retest\n', { nextAction: 'Size down after two losses', title: '' }))
      .toBe('Breakout retest\n\nIf …, then Size down after two losses (from an earlier Play)')
  })
})
