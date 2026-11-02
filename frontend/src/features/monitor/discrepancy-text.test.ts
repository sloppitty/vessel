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
