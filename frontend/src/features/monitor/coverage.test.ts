import { afterEach, describe, expect, it } from 'vitest'
import type { PlayExecution } from '@/api/plays'
import type { MonitorCoverage, MonitorStopEvidence } from '@/api/monitor'
import { coverageAs, coverageFixture, monitorFixture } from '@/test/monitor-fixture'
import { savedPlayFixture } from '@/test/workspace-fixture'
import { formatTime } from '@/lib/time'
import { coverageHeading, coverageLines, stopEvidenceFor } from './coverage'
import { restoreZone } from '@/test/zone'

const execution = (coverage: MonitorCoverage[] | undefined, overrides: Partial<PlayExecution> = {}): PlayExecution => ({
  playId: savedPlayFixture.summary.id, status: 'open', tracked: true, reason: null, checkedAtUtc: null,
  totals: { enteredQuantity: '1', exitedQuantity: '0', openQuantity: '1', closedPnlUsd: '0', closedPnlBasis: 'gross', fees: [] },
  entries: [], links: [], suggestions: [], unlinkedOrders: [], notice: '', issues: [],
  monitor: coverage === undefined ? { ...monitorFixture } : { ...monitorFixture, coverage }, discrepancies: [], watches: [], ...overrides,
})
const stamp = (iso: string) => `T(${iso})`
// The fixture's planned stop is saved at 60000; the chart draws it there unless the draft moved it.
const saved = 60000
const level = (evidence: MonitorStopEvidence['evidence'], extra: Partial<MonitorCoverage> = {}, stop: Partial<MonitorStopEvidence> = {}): MonitorCoverage => ({
  ...coverageFixture, ...extra,
  stops: [{ ...coverageFixture.stops[0]!, evidence, ...evidence.startsWith('resting') ? {} : { orderId: null, price: null }, ...stop }],
})
const of = (coverage: MonitorCoverage[] | undefined, price = saved, overrides: Partial<PlayExecution> = {}) =>
  stopEvidenceFor(execution(coverage, overrides), stamp)!('entry-1', 'stop-1', price)
const originalZone = process.env.TZ
afterEach(() => { restoreZone(originalZone) })

describe('stop evidence for the chart (Phase 0, 0.4c)', () => {
  it('confirms only a stop the last check saw resting at its saved level, dated, and words everything else as the evidence does', () => {
    expect(of([level('resting')])).toEqual({ text: 'resting as of T(2026-10-14T10:00:00Z)', confirmed: true })
    expect(of([level('not-seen')])).toEqual({ text: 'not seen at the venue', confirmed: false })
    expect(of([level('unknown')])).toEqual({ text: 'unknown', confirmed: false })
    expect(of([coverageAs('unsupported', 'current-book-not-read')])).toEqual({ text: 'not read', confirmed: false })
  })
  it('says where a stop rests when it is not at the planned price, or when the draft moved the line', () => {
    expect(of([level('resting-elsewhere', {}, { price: '59000' })])).toEqual({ text: 'resting at 59000 as of T(2026-10-14T10:00:00Z)', confirmed: false })
    // The unsaved draft dragged the stop to 58000: the order still rests at its saved level, not on the moved line.
    expect(of([level('resting')], 58000)).toEqual({ text: 'resting at 60000 as of T(2026-10-14T10:00:00Z)', confirmed: false })
    // A level without a saved price cannot be confirmed either.
    expect(of([level('resting', {}, { plannedPrice: null })])).toEqual({ text: 'resting at 60000 as of T(2026-10-14T10:00:00Z)', confirmed: false })
  })
  it('never confirms a stop it has no evidence for', () => {
    // A level the check did not record (e.g. added by a later revision), no recorded check yet, or an older API.
    expect(stopEvidenceFor(execution([level('resting')]), stamp)!('entry-1', 'stop-2', saved)).toEqual({ text: 'unknown', confirmed: false })
    expect(of([])).toEqual({ text: 'unknown', confirmed: false })
    expect(of(undefined)).toEqual({ text: 'unknown', confirmed: false })
    // Tracked but not monitored: nothing reads the venue.
    expect(of([], saved, { monitor: null })).toEqual({ text: 'not read', confirmed: false })
    // Stale evidence keeps its date but no longer confirms; an old absence is no longer known.
    expect(of([level('resting', { state: 'stale' })])).toEqual({ text: 'resting as of T(2026-10-14T10:00:00Z)', confirmed: false })
    expect(of([level('not-seen', { state: 'stale' })])).toEqual({ text: 'unknown', confirmed: false })
  })
  it('leaves the plan unlabelled where it claims nothing about the venue', () => {
    const time = (value: string) => value
    expect(stopEvidenceFor(null, time)).toBeUndefined()
    expect(stopEvidenceFor(execution([level('resting')], { status: 'planned' }), time)).toBeUndefined()
    expect(stopEvidenceFor(execution([level('resting')], { tracked: false }), time)).toBeUndefined()
  })
  it('stamps the canvas label in the display zone and names it (phase 1, 1.6e)', () => {
    process.env.TZ = 'Europe/Madrid'
    // The zone took effect: 14:02 UTC is 16:02 in Madrid on this date.
    expect(new Date('2026-10-14T14:02:00Z').getHours()).toBe(16)
    const resting = execution([level('resting', {}, { observedAtUtc: '2026-10-14T14:02:00Z' })])
    expect(stopEvidenceFor(resting, value => formatTime(value, 'utc', 'day-time'))!('entry-1', 'stop-1', saved))
      .toEqual({ text: 'resting as of 14 Oct, 14:02 UTC', confirmed: true })
    expect(stopEvidenceFor(resting, value => formatTime(value, 'local', 'day-time'))!('entry-1', 'stop-1', saved))
      .toEqual({ text: 'resting as of 14 Oct, 16:02 CEST', confirmed: true })
  })
})

describe('stop coverage heading (Phase 0, 0.4c)', () => {
  const uncovered = (reason: string) => ({ ...coverageAs('known', reason), covered: false })
  it.each([
    ['no-stop', 'No stop at the venue'],
    ['short', 'Stop short of the position'],
    ['cancelled', 'Stop cancelled at the venue'],
    ['some-future-reason', 'Stop does not cover the position'],
  ])('names a known uncovered Play by its reason: %s', (reason, heading) => {
    expect(coverageHeading([uncovered(reason)])).toBe(heading)
  })
  it('heads with the most serious uncovered reason, then the least certain coverage, and stays silent only when every Play is covered', () => {
    expect(coverageHeading([uncovered('short'), uncovered('cancelled'), uncovered('no-stop')])).toBe('Stop cancelled at the venue')
    expect(coverageHeading([uncovered('short'), uncovered('no-stop')])).toBe('No stop at the venue')
    expect(coverageHeading([coverageFixture])).toBeNull()
    expect(coverageHeading([])).toBeNull()
    expect(coverageHeading([coverageFixture, coverageAs('shared', 'shared-whole-position-stop')])).toBe('Stop shared with other Plays')
    expect(coverageHeading([coverageAs('shared', 'shared-whole-position-stop'), coverageAs('unsupported', 'current-book-not-read')])).toBe('Current book not read')
    expect(coverageHeading([coverageAs('unsupported', 'current-book-not-read'), { ...coverageFixture, state: 'stale' }])).toBe('Stop coverage stale')
    expect(coverageHeading([{ ...coverageFixture, state: 'stale' }, coverageAs('unknown', 'history-gap')])).toBe('Stop coverage unknown')
    expect(coverageHeading([coverageAs('unknown', 'history-gap'), uncovered('short')])).toBe('Stop short of the position')
  })
  it('never lets an Open Play without a recorded check read as covered', () => {
    expect(coverageHeading([], 1)).toBe('Stop coverage not checked yet')
    expect(coverageLines([], String, true, 1)).toEqual(['An Open Play\'s stop coverage has not been checked yet.'])
    expect(coverageLines([coverageFixture], String, true, 2)).toEqual(['2 Open Plays\' stop coverage has not been checked yet.'])
    // Recorded coverage of other Plays does not make the unchecked ones covered.
    expect(coverageHeading([coverageFixture], 1)).toBe('Stop coverage not checked yet')
  })
  it('names every order-book check that does not run without the current book, and the new order-read reasons', () => {
    const [line] = coverageLines([coverageAs('unsupported', 'current-book-not-read')], String)
    for (const check of ['missing', 'short', 'cancelled', 'excess', 'orphaned exits and entries', 'level drift']) expect(line).toContain(check)
    expect(coverageLines([coverageAs('unknown', 'stale-order-read')], String)).toEqual(['Stop coverage is unknown. The open-order read was too old to rely on.'])
    expect(coverageLines([coverageAs('unknown', 'order-read-unreliable')], String)[0]).toMatch(/^Stop coverage is unknown\. The open-order read was inconsistent/)
  })
})
