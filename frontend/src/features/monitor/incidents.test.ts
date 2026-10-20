import { describe, expect, it } from 'vitest'
import type { AccountMonitor, MonitorDiscrepancy } from '@/api/monitor'
import { discrepancyFixture, missingStopFixture, monitorFixture, shortStopFixture } from '@/test/monitor-fixture'
import {
  incidentsOf, countIncidents, incidentPlayIds, incidentSummary, incidentTone, incidentsBeyondCap, incidentsByPlay, isIncident, monitorIncidents, topTone,
} from './incidents'

const unverifiable = (fields: Partial<MonitorDiscrepancy> = {}): MonitorDiscrepancy =>
  ({ ...shortStopFixture, state: 'candidate', outcome: 'unverifiable', unverifiableAtUtc: '2026-10-21T10:00:00Z', ...fields })

describe('monitor incidents beyond the record cap (review round 2)', () => {
  const account = (discrepancies: MonitorDiscrepancy[], discrepanciesTruncated?: boolean): AccountMonitor =>
    ({ ...monitorFixture, discrepancies, ...discrepanciesTruncated === undefined ? {} : { discrepanciesTruncated } })
  it('knows further incidents can be missing only when a truncated account lists nothing but incidents', () => {
    // The server lists incidents first: a historical record in the list means every incident made it.
    expect(incidentsBeyondCap([account([missingStopFixture, { ...shortStopFixture, state: 'resolved' }], true)])).toBe(false)
    expect(incidentsBeyondCap([account([missingStopFixture, unverifiable()], true)])).toBe(true)
    // Not truncated, or an older server that does not say.
    expect(incidentsBeyondCap([account([missingStopFixture, unverifiable()], false)])).toBe(false)
    expect(incidentsBeyondCap([account([missingStopFixture, unverifiable()])])).toBe(false)
    expect(incidentsBeyondCap([account([], true)])).toBe(false)
  })
  it('shows a lower bound, "2+ incidents", whose severities are not all known', () => {
    const counts = countIncidents(incidentsOf([missingStopFixture, unverifiable()]), true)
    expect(counts).toMatchObject({ total: 2, critical: 1, warning: 1, more: true })
    expect(incidentSummary(counts)).toBe('2+ incidents')
    expect(topTone(counts)).toBe('critical')
    expect(countIncidents([], true).more).toBeUndefined()
    expect(incidentSummary(countIncidents(incidentsOf([missingStopFixture])))).toBe('1 critical')
  })
})

describe('monitor incidents', () => {
  it('counts open records and unverifiable records not dismissed since, never resolved or dismissed history', () => {
    expect(isIncident(missingStopFixture)).toBe(true)
    expect(isIncident({ ...missingStopFixture, state: 'resolved' })).toBe(false)
    expect(isIncident({ ...missingStopFixture, state: 'dismissed', dismissedAtUtc: '2026-10-15T10:00:00Z' })).toBe(false)
    expect(isIncident(unverifiable())).toBe(true)
    expect(isIncident(unverifiable({ state: 'resolved' }))).toBe(true)
    expect(isIncident(unverifiable({ state: 'open' }))).toBe(true)
    // Dismissing an unverifiable record closes it; the outcome stays until a fresh decision.
    expect(isIncident(unverifiable({ state: 'dismissed', dismissedAtUtc: '2026-10-21T10:00:00Z' }))).toBe(false)
    expect(isIncident(unverifiable({ state: 'dismissed', dismissedAtUtc: '2026-10-22T09:00:00.5+00:00' }))).toBe(false)
    // A dismissed record whose changed evidence later became unverifiable is outstanding again.
    expect(isIncident(unverifiable({ state: 'dismissed', dismissedAtUtc: '2026-10-20T10:00:00Z' }))).toBe(true)
    // Seven fractional digits order exactly, beyond millisecond parsing.
    expect(isIncident(unverifiable({ state: 'dismissed', unverifiableAtUtc: '2026-10-21T10:00:00.0000002Z',
      dismissedAtUtc: '2026-10-21T10:00:00.0000001+00:00' }))).toBe(true)
  })
  it('keeps an open record\'s severity and counts an outstanding unverifiable record that is not open as a warning', () => {
    expect(incidentTone(missingStopFixture)).toBe('critical')
    expect(incidentTone({ ...discrepancyFixture, severity: 'info' })).toBe('info')
    expect(incidentTone(unverifiable({ state: 'open', severity: 'critical' }))).toBe('critical')
    expect(incidentTone(unverifiable({ severity: 'critical' }))).toBe('warning')
  })
  it('maps incidents to Plays by playId or the instrument\'s listed Plays, ignoring malformed entries', () => {
    expect(incidentPlayIds(missingStopFixture)).toEqual([missingStopFixture.playId])
    expect(incidentPlayIds({ ...discrepancyFixture, playId: null, details: { playIds: ['a', 'b', 'a', 7, ''] } })).toEqual(['a', 'b'])
    expect(incidentPlayIds({ ...discrepancyFixture, playId: null, details: { playIds: 'a' } })).toEqual([])
    expect(incidentPlayIds({ ...discrepancyFixture, playId: null, details: {} })).toEqual([])
  })
  it('collects incidents across accounts, most severe first, and groups their counts by Play', () => {
    const accounts: AccountMonitor[] = [
      { ...monitorFixture, discrepancies: [{ ...shortStopFixture, playId: 'p-1' }, { ...missingStopFixture, state: 'resolved' }] },
      { ...monitorFixture, accountId: 'other', discrepancies: [
        { ...discrepancyFixture, id: 'i', accountId: 'other', kind: 'position.excess', playId: null, details: { playIds: ['p-1', 'p-2'] } },
        { ...missingStopFixture, id: 'm', accountId: 'other', playId: 'p-2' },
        { ...discrepancyFixture, id: 'drift', accountId: 'other', kind: 'level.drift', severity: 'info', playId: 'p-3' },
      ] },
    ]
    const incidents = monitorIncidents(accounts)
    expect(incidents.map(item => item.record.id)).toEqual(['m', shortStopFixture.id, 'i', 'drift'])
    expect(incidents[2]).toMatchObject({ accountId: 'other', tone: 'warning', playIds: ['p-1', 'p-2'] })
    expect(countIncidents(incidents)).toEqual({ total: 4, critical: 1, warning: 2, info: 1 })
    const byPlay = incidentsByPlay(incidents)
    expect(byPlay.get('p-1')).toEqual({ total: 2, critical: 0, warning: 2, info: 0 })
    expect(byPlay.get('p-2')).toEqual({ total: 2, critical: 1, warning: 1, info: 0 })
    expect(byPlay.get('p-3')).toEqual({ total: 1, critical: 0, warning: 0, info: 1 })
    expect(byPlay.has('p-4')).toBe(false)
    expect(monitorIncidents([])).toEqual([])
  })
  it('summarizes counts in severity order with plain plurals', () => {
    expect(incidentSummary({ total: 1, critical: 1, warning: 0, info: 0 })).toBe('1 critical')
    expect(incidentSummary({ total: 4, critical: 1, warning: 2, info: 1 })).toBe('1 critical · 2 warnings · 1 notice')
    expect(incidentSummary({ total: 2, critical: 0, warning: 0, info: 2 })).toBe('2 notices')
    expect(topTone({ total: 3, critical: 0, warning: 2, info: 1 })).toBe('warning')
    expect(topTone({ total: 1, critical: 0, warning: 0, info: 1 })).toBe('info')
    expect(topTone({ total: 0, critical: 0, warning: 0, info: 0 })).toBeNull()
  })
})
