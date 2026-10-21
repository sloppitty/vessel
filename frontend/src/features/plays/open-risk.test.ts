import { describe, expect, it } from 'vitest'
import { exactDecimal, exactFixed } from '@/lib/decimal'
import { emptyOpenRisk } from '@/test/workspace-fixture'
import type { OpenRisk, OpenRiskPreview } from '@/api/risk'
import { riskPlanOf, riskShare, withThisPlay } from './open-risk'
import { createDraft } from './draft'

const id = (n: number) => `0000000${n}-0000-0000-0000-000000000000`
const play = (n: number, direction: 'long' | 'short', riskUsd: string | null) => ({ playId: id(n), title: `P${n}`, direction, status: 'planned' as const,
  accountId: id(9), riskUsd, reason: riskUsd === null ? 'An entry has no stop.' : null, rounded: false, protection: null })
// G-06: three 12.5 longs on a 1,000 balance and one 10 short, plus one unknown long.
const risk: OpenRisk = { ...emptyOpenRisk('all'), balance: { amountUsd: '1000', contributingAccountCount: 1, totalAccountCount: 1 },
  long: { knownUsd: '37.5', knownCount: 3, unknownCount: 1, rounded: false }, short: { knownUsd: '10', knownCount: 1, unknownCount: 0, rounded: false },
  plays: [play(1, 'long', '12.5'), play(2, 'long', '12.5'), play(3, 'long', '12.5'), play(4, 'short', '10'), play(5, 'long', null)] }
const preview = (riskUsd: string | null, direction: 'long' | 'short' = 'long'): OpenRiskPreview =>
  ({ playId: null, direction, riskUsd, reason: riskUsd === null ? 'A stop has no price.' : null, rounded: false })
const fixed = (value: ReturnType<typeof exactDecimal>) => exactFixed(value!, 28)

describe('open risk in capital context', () => {
  it('adds a new Draft to its own direction and never nets long against short', () => {
    const { totals, own } = withThisPlay(risk, null, preview('20'))
    expect([fixed(totals.long), fixed(totals.short), totals.unknown, fixed(own)]).toEqual(['57.5', '10', 1, '20'])
    // A short does not reduce the longs.
    const short = withThisPlay(risk, null, preview('20', 'short'))
    expect([fixed(short.totals.long), fixed(short.totals.short)]).toEqual(['37.5', '30'])
  })

  it('replaces the saved Play with the plan being edited', () => {
    // Play 1 is saved at 12.5; its edited stop risks 0 (managed to break-even), so it no longer counts at all.
    const { totals } = withThisPlay(risk, id(1), preview('0'))
    expect(fixed(totals.long)).toBe('25')
    // The saved unknown Play now measures: one fewer unknown.
    const measured = withThisPlay(risk, id(5), preview('5'))
    expect([fixed(measured.totals.long), measured.totals.unknown]).toEqual(['42.5', 0])
    // An edit that makes a known Play unknown moves it to the unknown count, never to zero risk.
    const unknown = withThisPlay(risk, id(2), preview(null))
    expect([fixed(unknown.totals.long), unknown.totals.unknown, unknown.own]).toEqual(['25', 2, null])
  })

  it('shows shares exactly, with two decimals', () => {
    expect(riskShare(exactDecimal('37.5')!, exactDecimal('1000'))).toBe('3.75%')
    expect(riskShare(exactDecimal('0')!, exactDecimal('1000'))).toBe('0%')
    expect(riskShare(exactDecimal('0.0001')!, exactDecimal('1000'))).toBe('<0.01%')
    expect(riskShare(exactDecimal('1')!, exactDecimal('3'))).toBe('33.33%')
    // No balance, or none above zero: no share.
    expect(riskShare(exactDecimal('1')!, null)).toBeNull()
    expect(riskShare(exactDecimal('1')!, exactDecimal('0'))).toBeNull()
  })

  it('sends only the plan fields open risk reads', () => {
    const draft = createDraft()
    const plan = riskPlanOf(draft)
    expect(Object.keys(plan).sort()).toEqual(['direction', 'entries', 'leverage', 'size', 'sizingMode'])
    expect(plan.entries[0]).toEqual({ id: draft.entries[0]!.id, share: draft.entries[0]!.share, price: draft.entries[0]!.price,
      stops: draft.entries[0]!.stops.map(stop => ({ id: stop.id, unit: stop.unit, value: stop.value, share: stop.share })) })
  })
})
