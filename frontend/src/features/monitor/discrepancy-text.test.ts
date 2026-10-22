import { describe, expect, it } from 'vitest'
import type { MonitorDiscrepancy } from '@/api/monitor'
import { discrepancyAction, discrepancyFacts, discrepancyTitle } from './discrepancy-text'

const record = (details: Record<string, unknown>) => ({ kind: 'protection.beyond-liquidation', details, lastObservedAtUtc: null }) as unknown as MonitorDiscrepancy

describe('beyond-liquidation wording (phase 2, 8b)', () => {
  it('names the venue liquidation price, mode and the stops beyond it', () => {
    expect(discrepancyTitle('protection.beyond-liquidation')).toBe("The stop sits beyond the venue's liquidation price")
    expect(discrepancyAction('protection.beyond-liquidation')).toContain('Under cross margin the price moves with the account')
    const facts = discrepancyFacts(record({ liquidationPrice: '95.5', marginMode: 'cross',
      stops: [{ orderId: '1', price: '90', beyond: true }, { orderId: '2', price: '97', beyond: false }] }), value => value)
    expect(facts.slice(0, 3)).toEqual([{ label: 'Venue liquidation', value: '95.5' }, { label: 'Margin mode', value: 'cross' },
      { label: 'Stops beyond it', value: '90' }])
  })

  it('says unknown rather than inventing a figure', () => {
    const facts = discrepancyFacts(record({}), value => value)
    expect(facts.slice(0, 3).map(fact => fact.value)).toEqual(['Unknown', 'Unknown', 'Unknown'])
  })
})

describe('no resting stop wording (phase 2, 6c)', () => {
  const unprotected = (details: Record<string, unknown>) =>
    ({ kind: 'position.unprotected', details, lastObservedAtUtc: null }) as unknown as MonitorDiscrepancy

  it('names the venue position and its side, and says unknown for anything missing', () => {
    expect(discrepancyTitle('position.unprotected')).toBe('An open position has no resting stop and no Play')
    expect(discrepancyAction('position.unprotected')).toContain('Place a stop at the venue')
    expect(discrepancyFacts(unprotected({ venueQuantity: '-0.5', direction: 'short' }), value => value).slice(0, 2))
      .toEqual([{ label: 'Venue position', value: '-0.5' }, { label: 'Direction', value: 'Short' }])
    expect(discrepancyFacts(unprotected({}), value => value).slice(0, 2).map(fact => fact.value)).toEqual(['Unknown', 'Unknown'])
  })
})
