import { describe, expect, it } from 'vitest'
import { isReportingSettings } from './reporting'
import { isReviewDocument, isReviewWeek, reviewPath, reviewProcessPath } from './review'
import { reviewFixture } from '@/test/review-fixture'

const week = { start: '2026-10-05', fromUtc: '2026-10-04T22:00:00Z', toUtc: '2026-10-11T22:00:00Z', timeZone: 'Europe/Madrid', weekStart: 'monday', data: 'some' }

describe('reporting setting and weeks (Phase 2, item 2)', () => {
  it('accepts the setting and a week only in their documented shapes', () => {
    expect(isReportingSettings({ timeZone: 'Europe/Madrid', weekStart: 'sunday', isDefault: false })).toBe(true)
    expect(isReportingSettings({ timeZone: '', weekStart: 'monday', isDefault: true })).toBe(false)
    expect(isReportingSettings({ timeZone: 'UTC', weekStart: 'tuesday', isDefault: true })).toBe(false)
    expect(isReviewWeek(week)).toBe(true)
    expect(isReviewWeek({ ...week, start: '2026-10-5' })).toBe(false)
    expect(isReviewWeek({ ...week, toUtc: week.fromUtc })).toBe(false)
    expect(isReviewWeek({ ...week, data: 'partial' })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, week })).toBe(true)
    expect(isReviewDocument({ ...reviewFixture, week: { ...week, weekStart: 'friday' } })).toBe(false)
  })

  it('sends the week with every read that takes Review filters', () => {
    const query = { period: 'month' as const, portfolio: 'all', week: '2026-10-07' }
    expect(reviewPath(query)).toBe('/api/review?period=month&portfolio=all&week=2026-10-07')
    expect(reviewProcessPath(query, { mistakeTag: 'Late' })).toContain('week=2026-10-07')
    expect(reviewPath({ period: 'month', portfolio: 'all' })).not.toContain('week')
  })
})
