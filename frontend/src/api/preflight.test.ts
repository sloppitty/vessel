import { describe, expect, it } from 'vitest'
import { isNextAction, isPlayHistory, isPreflightCheck } from './plays'
import { isReviewDocument } from './review'
import { reviewFixture } from '@/test/review-fixture'
import { savedPlayFixture } from '@/test/workspace-fixture'

const revision = { number: 1, status: 'planned', reason: 'Planned', createdAtUtc: '2026-10-14T12:00:00Z', plan: savedPlayFixture.plan }

describe('pre-flight and next-action responses (phase 1, 1.5)', () => {
  it('accepts known checks only', () => {
    expect(isPreflightCheck({ code: 'risk', outcome: 'unknown' })).toBe(true)
    for (const bad of [{ code: 'stop-loss', outcome: 'passed' }, { code: 'risk', outcome: 'failed' }, 'risk']) expect(isPreflightCheck(bad)).toBe(false)
  })

  it('accepts history with or without a recorded pre-flight', () => {
    expect(isPlayHistory({ statusChanges: [], revisions: [revision] })).toBe(true)
    expect(isPlayHistory({ statusChanges: [], revisions: [{ ...revision, preflight: null }] })).toBe(true)
    expect(isPlayHistory({ statusChanges: [], revisions: [{ ...revision, preflight: [{ code: 'thesis', outcome: 'skipped' }] }] })).toBe(true)
    expect(isPlayHistory({ statusChanges: [], revisions: [{ ...revision, preflight: [{ code: 'thesis', outcome: 'maybe' }] }] })).toBe(false)
  })

  it('accepts a next action only with its text and source', () => {
    const action = { playId: savedPlayFixture.summary.id, title: 'BTC breakout', nextAction: 'Wait for the retest', reviewedAtUtc: null }
    expect(isNextAction(action)).toBe(true)
    expect(isNextAction({ ...action, nextAction: '   ' })).toBe(false)
    expect(isNextAction({ ...action, playId: 'not-a-guid' })).toBe(false)
  })

  it('accepts Review pre-flight counts that never exceed the recorded Plays, and older reports without them', () => {
    expect(isReviewDocument(reviewFixture)).toBe(true)
    const preflight = { recorded: 2, notRecorded: 1, skipped: [{ code: 'priced-stops', count: 2 }, { code: 'thesis', count: 0 }] }
    expect(isReviewDocument({ ...reviewFixture, preflight })).toBe(true)
    expect(isReviewDocument({ ...reviewFixture, preflight: { ...preflight, skipped: [{ code: 'priced-stops', count: 3 }] } })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, preflight: { ...preflight, skipped: [{ code: 'other', count: 1 }] } })).toBe(false)
  })
})
