import type { AccountMonitor, MonitorDiscrepancy, MonitorEvent, MonitorSummary } from '@/api/monitor'
import { accountFixture, savedPlayFixture } from './workspace-fixture'

export const discrepancyFixture: MonitorDiscrepancy = {
  id: '00000000-0000-4000-8000-0000000000d1', accountId: accountFixture.id, playId: savedPlayFixture.summary.id,
  contractId: 'BTC', kind: 'position-mismatch', severity: 'warning', state: 'open', openedAtUtc: '2026-10-14T10:00:00Z',
  confirmedAtUtc: null, resolvedAtUtc: null, dismissedAtUtc: null, note: null, details: { venueQuantity: '0.100000000000000001', playQuantity: '0.1' },
  outcome: null, unverifiableAtUtc: null, lastObservedAtUtc: '2026-10-14T10:00:00Z',
}
/** A confirmed critical record: an open Long without a stop, as the monitor details it (`stopCoverage` by orders). */
export const missingStopFixture: MonitorDiscrepancy = {
  ...discrepancyFixture, id: '00000000-0000-4000-8000-0000000000d3', kind: 'protection.missing', severity: 'critical', contractId: 'BTC',
  confirmedAtUtc: '2026-10-14T10:01:00Z', lastObservedAtUtc: '2026-10-14T10:05:00Z',
  details: { playIds: [savedPlayFixture.summary.id], nativeContractId: 'BTC', quantity: '0.5', stopQuantity: '0', targetQuantity: '0.5',
    stopCoverage: 'orders', orders: [{ orderId: '900000028', role: 'Target', level: 'target|entry-1|target-1', status: 'Open', resting: true,
      remainingQuantity: '0.5', wholePosition: false, price: '70000' }] },
}
/** A stop smaller than the position, covered by sized orders. */
export const shortStopFixture: MonitorDiscrepancy = {
  ...missingStopFixture, id: '00000000-0000-4000-8000-0000000000d4', kind: 'protection.short', severity: 'warning',
  details: { playIds: [savedPlayFixture.summary.id], nativeContractId: 'BTC', quantity: '1.25', stopQuantity: '0.5', targetQuantity: '0',
    stopCoverage: 'orders', orders: [{ orderId: '900000029', role: 'Stop', level: 'stop|entry-1|stop-1', status: 'Open', resting: true,
      remainingQuantity: '0.5', wholePosition: false, price: '60000' }] },
}
export const monitorFixture: AccountMonitor = {
  accountId: accountFixture.id, state: 'live', lastAttemptAtUtc: '2026-10-14T10:00:00Z', lastSuccessAtUtc: '2026-10-14T10:00:00Z',
  nextRunAtUtc: '2026-10-14T10:10:00Z', manualState: 'idle', runTrigger: null, nextManualAllowedAtUtc: null, error: null, workerOnline: true, discrepancies: [],
}
export const monitorSummaryFixture: MonitorSummary = { workerOnline: true, heartbeatAtUtc: '2026-10-14T10:00:00Z', accounts: [monitorFixture] }
export const monitorEventFixture: MonitorEvent = {
  id: '00000000-0000-4000-8000-0000000000e1', type: 'play.opened', version: 1, ownerId: 'fixture-owner', sequence: '9007199254740993',
  runId: '00000000-0000-4000-8000-0000000000e2', dedupKey: 'fixture-dedup', severity: 'info',
  occurredAt: '2026-10-14T10:00:00Z', detectedAt: '2026-10-14T10:00:10Z', timeliness: 'timely', relevance: 'current', expiresAt: null,
  subject: { accountId: accountFixture.id, venueId: null, contractId: null, playId: savedPlayFixture.summary.id, entryId: null, levelId: null, discrepancyId: null },
  source: { trigger: 'scheduled', orderIds: null, fillIds: null }, data: {},
}
/** Test-only cursor construction; production code leaves cursor syntax to `@/api/event-cursor`. */
export const fixtureEpoch = '0123456789abcdef0123456789abcdef'
export const eventCursor = (sequence: string, epoch = fixtureEpoch) => `${epoch}.${sequence}`
export const eventFrame = (event: MonitorEvent = monitorEventFixture) => ({ type: 'event', event, cursor: eventCursor(event.sequence) }) as const
