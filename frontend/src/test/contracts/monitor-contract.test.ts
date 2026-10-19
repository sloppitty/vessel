// Real API JSON written by backend MonitorEndToEndTests.Monitor_http_json_matches_the_browser_contract_fixture.
// The backend test fails when its responses drift from this file; this test fails when the browser guards reject it.
import { describe, expect, it } from 'vitest'
import { isAccountMonitor, isMonitorCoverage, isMonitorDiscrepancy, isMonitorEvent, isMonitorRunAccepted, isMonitorSummary, isMonitorWatch } from '@/api/monitor'
import { isEventCursor } from '@/api/event-cursor'
import { isPlayExecution } from '@/api/plays'
import recorded from './monitor-api.json?raw'

const fixture = JSON.parse(recorded) as Record<string, unknown>

describe('monitor API contract fixture', () => {
  it('accepts every recorded monitor response with the browser guards', () => {
    expect(isMonitorRunAccepted(fixture.runAccepted)).toBe(true)
    expect(isAccountMonitor(fixture.accountMonitorRunning)).toBe(true)
    expect(isAccountMonitor(fixture.accountMonitorIdle)).toBe(true)
    expect(isMonitorSummary(fixture.summary)).toBe(true)
    expect(isPlayExecution(fixture.playExecution)).toBe(true)
    expect(isMonitorDiscrepancy(fixture.discrepancy)).toBe(true)
    expect(isMonitorDiscrepancy(fixture.discrepancyUnverifiable)).toBe(true)
    expect(isMonitorWatch(fixture.watchUnverifiable)).toBe(true)
    expect(isMonitorCoverage(fixture.coverage)).toBe(true)
    expect(isMonitorCoverage(fixture.coverageUnsupported)).toBe(true)
    expect(isMonitorEvent(fixture.runCompletedEvent)).toBe(true)
    expect(isEventCursor(fixture.eventPageNextCursor)).toBe(true)
  })
  it('records explicit nulls and the unverifiable outcomes', () => {
    expect(fixture.runCompletedEvent).toMatchObject({ subject: { playId: null, discrepancyId: null }, source: { orderIds: null, fillIds: null } })
    expect(fixture.discrepancy).toMatchObject({ outcome: null, unverifiableAtUtc: null, lastObservedAtUtc: expect.any(String) })
    expect(fixture.discrepancyUnverifiable).toMatchObject({ state: 'candidate', outcome: 'unverifiable' })
    expect(fixture.watchUnverifiable).toMatchObject({ state: 'unverifiable' })
  })
  it('records stop coverage on every account monitor and its states with explicit nulls (Phase 0, 0.4c)', () => {
    // The end-to-end account holds an Open ETH Play whose linked stop rests: a real check recorded its coverage.
    expect(fixture.accountMonitorIdle).toMatchObject({ coverage: [{ contractId: 'ETH', state: 'known', recordedState: 'known', covered: true,
      reason: 'stop-orders', stops: [{ evidence: 'resting', orderId: expect.any(String), observedAtUtc: expect.any(String) }] }] })
    expect(fixture.coverage).toMatchObject({ state: 'known', recordedState: 'known', covered: true, reason: 'stop-orders',
      stops: [{ evidence: 'resting', observedAtUtc: expect.any(String) }] })
    expect(fixture.coverageUnsupported).toMatchObject({ state: 'stale', recordedState: 'unsupported', covered: null,
      reason: 'current-book-not-read', stops: [{ evidence: 'not-read', orderId: null, observedAtUtc: null }] })
  })
  it('records an accepted owner Check as pending with its trigger, and its completion event', () => {
    expect(fixture.runAccepted).toMatchObject({ monitor: { manualState: 'pending', runTrigger: 'owner' } })
    expect(fixture.accountMonitorRunning).toMatchObject({ manualState: 'running', runTrigger: 'owner' })
    expect(fixture.accountMonitorIdle).toMatchObject({ manualState: 'idle', runTrigger: null, discrepanciesTruncated: false })
    expect(fixture.runCompletedEvent).toMatchObject({ type: 'monitor.run-completed', severity: 'info',
      data: { trigger: 'manual', outcome: 'succeeded' } })
  })
})
