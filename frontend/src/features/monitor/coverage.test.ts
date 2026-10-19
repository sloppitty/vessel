import { describe, expect, it } from 'vitest'
import type { PlayExecution } from '@/api/plays'
import type { MonitorCoverage } from '@/api/monitor'
import { coverageAs, coverageFixture, monitorFixture } from '@/test/monitor-fixture'
import { savedPlayFixture } from '@/test/workspace-fixture'
import { coverageHeading, stopEvidenceFor } from './coverage'

const execution = (coverage: MonitorCoverage[] | undefined, overrides: Partial<PlayExecution> = {}): PlayExecution => ({
  playId: savedPlayFixture.summary.id, status: 'open', tracked: true, reason: null, checkedAtUtc: null,
  totals: { enteredQuantity: '1', exitedQuantity: '0', openQuantity: '1', closedPnlUsd: '0', closedPnlBasis: 'gross', fees: [] },
  entries: [], links: [], suggestions: [], unlinkedOrders: [], notice: '', issues: [],
  monitor: coverage === undefined ? { ...monitorFixture } : { ...monitorFixture, coverage }, discrepancies: [], watches: [], ...overrides,
})
const stamp = (iso: string) => `T(${iso})`
const level = (evidence: MonitorCoverage['stops'][number]['evidence'], extra: Partial<MonitorCoverage> = {}): MonitorCoverage => ({
  ...coverageFixture, ...extra,
  stops: [{ ...coverageFixture.stops[0]!, evidence, ...evidence === 'resting' ? {} : { orderId: null } }],
})

describe('stop evidence for the chart (Phase 0, 0.4c)', () => {
  it('confirms only a stop the last check saw resting, dated, and words everything else as the evidence does', () => {
    expect(stopEvidenceFor(execution([level('resting')]), stamp)!('entry-1', 'stop-1'))
      .toEqual({ text: 'resting as of T(2026-10-14T10:00:00Z)', confirmed: true })
    expect(stopEvidenceFor(execution([level('not-seen')]), stamp)!('entry-1', 'stop-1')).toEqual({ text: 'not seen at the venue', confirmed: false })
    expect(stopEvidenceFor(execution([level('unknown')]), stamp)!('entry-1', 'stop-1')).toEqual({ text: 'unknown', confirmed: false })
    expect(stopEvidenceFor(execution([coverageAs('unsupported', 'current-book-not-read')]), stamp)!('entry-1', 'stop-1'))
      .toEqual({ text: 'not read', confirmed: false })
  })
  it('never confirms a stop it has no evidence for', () => {
    // A level the check did not record (e.g. added by a later revision), no recorded check yet, or an older API.
    expect(stopEvidenceFor(execution([level('resting')]), stamp)!('entry-1', 'stop-2')).toEqual({ text: 'unknown', confirmed: false })
    expect(stopEvidenceFor(execution([]), stamp)!('entry-1', 'stop-1')).toEqual({ text: 'unknown', confirmed: false })
    expect(stopEvidenceFor(execution(undefined), stamp)!('entry-1', 'stop-1')).toEqual({ text: 'unknown', confirmed: false })
    // Tracked but not monitored: nothing reads the venue.
    expect(stopEvidenceFor(execution([], { monitor: null }), stamp)!('entry-1', 'stop-1')).toEqual({ text: 'not read', confirmed: false })
    // Stale evidence keeps its date but no longer confirms; an old absence is no longer known.
    expect(stopEvidenceFor(execution([level('resting', { state: 'stale' })]), stamp)!('entry-1', 'stop-1'))
      .toEqual({ text: 'resting as of T(2026-10-14T10:00:00Z)', confirmed: false })
    expect(stopEvidenceFor(execution([level('not-seen', { state: 'stale' })]), stamp)!('entry-1', 'stop-1')).toEqual({ text: 'unknown', confirmed: false })
  })
  it('leaves the plan unlabelled where it claims nothing about the venue', () => {
    expect(stopEvidenceFor(null)).toBeUndefined()
    expect(stopEvidenceFor(execution([level('resting')], { status: 'planned' }))).toBeUndefined()
    expect(stopEvidenceFor(execution([level('resting')], { tracked: false }))).toBeUndefined()
  })
  it('heads with the least certain coverage and stays silent only when every Play is covered', () => {
    expect(coverageHeading([coverageFixture])).toBeNull()
    expect(coverageHeading([])).toBeNull()
    expect(coverageHeading([coverageFixture, coverageAs('shared', 'shared-whole-position-stop')])).toBe('Stop shared with other Plays')
    expect(coverageHeading([coverageAs('shared', 'shared-whole-position-stop'), coverageAs('unsupported', 'current-book-not-read')])).toBe('Current book not read')
    expect(coverageHeading([coverageAs('unsupported', 'current-book-not-read'), { ...coverageFixture, state: 'stale' }])).toBe('Stop coverage stale')
    expect(coverageHeading([{ ...coverageFixture, state: 'stale' }, coverageAs('unknown', 'history-gap')])).toBe('Stop coverage unknown')
    expect(coverageHeading([coverageAs('unknown', 'history-gap'), { ...coverageAs('known', 'short'), covered: false }])).toBe('Stop not seen at the venue')
  })
})
