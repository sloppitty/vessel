import { describe, expect, it } from 'vitest'
import { isSizingDocument, type RecordedPlay, type SizingDocument } from './sizing'
import { sizingFixture } from '@/test/workspace-fixture'

const play: RecordedPlay = {
  playId: '12345678-1234-1234-1234-123456789abc', title: 'BTC Long', instrument: 'BTC', closedAtUtc: '2026-10-12T10:00:00Z',
  netResultUsd: '4', returnPercent: '4', riskUsd: null, rMultiple: null, outcome: 'win', feesComplete: true,
}
const document = (exposure: Record<string, unknown>, row: Record<string, unknown>): unknown => ({
  ...sizingFixture,
  exposure: { ...sizingFixture.exposure, ...exposure },
  record: { ...sizingFixture.record, source: 'plays', closedPlays: 1, decidedPlays: 1, wins: 1, recent: [{ ...play, ...row }] },
})

describe('sizing document', () => {
  it('accepts the exit-time tie count and the detection time, and documents from earlier servers without them', () => {
    expect(isSizingDocument(document({}, {}))).toBe(true)
    expect(isSizingDocument(document({ tiedOutcomes: 0 }, { detectedAtUtc: null }))).toBe(true)
    const tied = document({ tiedOutcomes: 2 }, { detectedAtUtc: '2026-10-12T11:00:00Z' })
    expect(isSizingDocument(tied)).toBe(true)
    expect((tied as SizingDocument).exposure.tiedOutcomes).toBe(2)
  })

  it.each([-1, 1.5, '2', null])('rejects a tie count of %s', tiedOutcomes => {
    expect(isSizingDocument(document({ tiedOutcomes }, {}))).toBe(false)
  })

  it.each(['not a time', 7, ''])('rejects a detection time of %s', detectedAtUtc => {
    expect(isSizingDocument(document({}, { detectedAtUtc }))).toBe(false)
  })
})
