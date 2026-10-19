import { isCount, isFilledText, isPlainDecimal, isPositiveCount, isRecord as object, isText as text, isTimestamp } from './guards'
import { isEventCursor, isEventSequence, type EventCursor } from './event-cursor'
import { isNotificationFrame, isNotificationsStatus, type NotificationFrame, type NotificationsStatus } from './notifications'

export interface MonitorDiscrepancy {
  id: string
  accountId: string
  playId: string | null
  contractId: string
  kind: string
  severity: 'info' | 'warning' | 'critical'
  /** A candidate is only visible once announced as unverifiable. */
  state: 'candidate' | 'open' | 'resolved' | 'dismissed'
  openedAtUtc: string
  confirmedAtUtc: string | null
  resolvedAtUtc: string | null
  dismissedAtUtc: string | null
  note: string | null
  details: Record<string, unknown>
  /** `unverifiable`: the subject stayed unobservable beyond the monitor lifetime. Neither resolved nor still checked. */
  outcome: 'unverifiable' | null
  unverifiableAtUtc: string | null
  /**
   * Source time of the last fresh decisive observation; incomplete reads do not move it. Null when none is recorded.
   * Absent from an older API, which is accepted and shown as unknown, like null.
   */
  lastObservedAtUtc?: string | null
}

/** A terminal Play's linked exit or entry order, watched until the venue shows it is no longer resting. */
export interface MonitorWatch {
  linkId: string
  playId: string
  orderId: string
  role: 'entry' | 'stop' | 'target' | 'exit'
  /** `unverifiable` is persistent: the watch lifetime passed without proof, which never claims the order is gone. */
  state: 'watching' | 'resolved' | 'unverifiable'
  startedAtUtc: string
  endedAtUtc: string | null
}

/**
 * Evidence for one planned stop level from the last check's order read: `resting` (a linked closing stop rested in the
 * current book at `observedAtUtc`, at the saved level price within the matcher's step), `resting-elsewhere` (the same, at
 * another price), `not-seen` (a complete, fresh book had none), `unknown`, or `not-read` (the venue's order reader does
 * not return the current book).
 */
export interface MonitorStopEvidence {
  entryId: string
  levelId: string
  evidence: 'resting' | 'resting-elsewhere' | 'not-seen' | 'unknown' | 'not-read'
  orderId: string | null
  observedAtUtc: string | null
  /** The resting order's acting price, exact; set for both resting kinds. */
  price: string | null
  /** The saved level price the check compared it with, exact; null for an unpriced level. */
  plannedPrice: string | null
}

/**
 * The last stop-coverage outcome the monitor recorded for an Open Play. `state` is projected when read: `stale` once
 * the check is older than twice the monitor interval, otherwise `recordedState`. `covered` is set only for `known`.
 */
export interface MonitorCoverage {
  playId: string
  contractId: string
  state: 'known' | 'shared' | 'unknown' | 'unsupported' | 'stale'
  recordedState: 'known' | 'shared' | 'unknown' | 'unsupported'
  covered: boolean | null
  /** A short server code such as `stop-orders`, `opposing-plays` or `current-book-not-read`. */
  reason: string
  checkedAtUtc: string
  stops: MonitorStopEvidence[]
}

export interface AccountMonitor {
  accountId: string
  state: 'live' | 'stale' | 'degraded' | 'credential-error' | 'idle'
  lastAttemptAtUtc: string | null
  lastSuccessAtUtc: string | null
  nextRunAtUtc: string | null
  manualState: 'idle' | 'pending' | 'running'
  /** Who started pending or running work: an owner Check, an owner account refresh or the scheduler. Null when idle. */
  runTrigger: 'owner' | 'refresh' | 'scheduled' | null
  nextManualAllowedAtUtc: string | null
  error: string | null
  workerOnline: boolean
  /** Visible records, outstanding incidents first; at most 500. */
  discrepancies: MonitorDiscrepancy[]
  /**
   * More records exist than `discrepancies` lists. Incidents are listed first, so only older records are left out, unless
   * the incidents alone exceed the cap. Optional: an older API omits it, read as not truncated.
   */
  discrepanciesTruncated?: boolean
  /** Stored stop coverage of the account's Open Plays. Optional: an older API omits it. */
  coverage?: MonitorCoverage[]
  /**
   * Open, tracked Plays on the account with no recorded coverage yet (after the migration, or once reconciliation opens
   * one, until the next check). Optional, like `coverage`; absent reads as none.
   */
  uncheckedPlays?: number
}

export interface MonitorSummary {
  workerOnline: boolean
  heartbeatAtUtc: string | null
  accounts: AccountMonitor[]
}

export interface MonitorRunAccepted { requestId: string; monitor: AccountMonitor }
export interface MonitorEvent {
  id: string
  type: string
  version: number
  ownerId: string
  sequence: string
  runId: string
  dedupKey: string
  severity: MonitorDiscrepancy['severity']
  occurredAt: string
  detectedAt: string
  timeliness: 'timely' | 'late'
  relevance: 'current' | 'superseded'
  expiresAt: string | null
  /** Every field is present; unknown ones are explicit nulls. */
  subject: { accountId: string | null; venueId: string | null; contractId: string | null; playId: string | null
    entryId: string | null; levelId: string | null; discrepancyId: string | null }
  source: { trigger: string; orderIds: string[] | null; fillIds: string[] | null }
  data: Record<string, unknown>
}
export interface MonitorEventPage { events: MonitorEvent[]; nextCursor: EventCursor; resetRequired: boolean }
/**
 * An event frame carries its resume cursor from the SSE `id:` line. Notification frames come only when the stream
 * was opened with a notification cursor; theirs travel in the frame data, since the `id:` line stays the event's.
 */
export type MonitorStreamEvent = { type: 'event'; event: MonitorEvent; cursor: EventCursor } |
  { type: 'status'; status: { state: 'ready' | 'reset'; nextCursor: EventCursor } } |
  { type: 'notifications'; status: NotificationsStatus } |
  { type: 'notification'; frame: NotificationFrame }

const id = (v: unknown): v is string => isFilledText(v, 128)
// Monitor timestamps are bounded UTC strings with up to seven fractional digits.
const date = (v: unknown): v is string => isTimestamp(v, 64) &&
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(v)
const nullableDate = (v: unknown) => v === null || date(v)
const subjectKeys = ['accountId', 'venueId', 'contractId', 'playId', 'entryId', 'levelId', 'discrepancyId'] as const
const severity = (v: unknown) => ['info', 'warning', 'critical'].includes(v as string)

export const isMonitorDiscrepancy = (v: unknown): v is MonitorDiscrepancy => object(v) &&
  id(v.id) && id(v.accountId) && (v.playId === null || id(v.playId)) && id(v.contractId) && id(v.kind) &&
  severity(v.severity) && ['candidate', 'open', 'resolved', 'dismissed'].includes(v.state as string) &&
  date(v.openedAtUtc) && nullableDate(v.confirmedAtUtc) && nullableDate(v.resolvedAtUtc) && nullableDate(v.dismissedAtUtc) &&
  (v.note === null || text(v.note, 2000)) && object(v.details) &&
  // An older server omits it: unknown, never a reason to reject the record, its summary or the Play's execution.
  (v.lastObservedAtUtc === undefined || nullableDate(v.lastObservedAtUtc)) &&
  // The outcome and its time come together; only an unverifiable record is visible as a candidate.
  (v.outcome === null ? v.unverifiableAtUtc === null && v.state !== 'candidate' : v.outcome === 'unverifiable' && date(v.unverifiableAtUtc))

export const isMonitorWatch = (v: unknown): v is MonitorWatch => object(v) && id(v.linkId) && id(v.playId) && id(v.orderId) &&
  ['entry', 'stop', 'target', 'exit'].includes(v.role as string) && ['watching', 'resolved', 'unverifiable'].includes(v.state as string) && date(v.startedAtUtc) &&
  (v.state === 'watching' ? v.endedAtUtc === null : nullableDate(v.endedAtUtc))

const recordedStates = ['known', 'shared', 'unknown', 'unsupported']
const resting = (v: unknown) => v === 'resting' || v === 'resting-elsewhere'
export const isMonitorStopEvidence = (v: unknown): v is MonitorStopEvidence => object(v) && id(v.entryId) && id(v.levelId) &&
  ['resting', 'resting-elsewhere', 'not-seen', 'unknown', 'not-read'].includes(v.evidence as string) && (v.orderId === null || id(v.orderId)) &&
  // A resting order is always dated and priced.
  (resting(v.evidence) ? date(v.observedAtUtc) && isPlainDecimal(v.price) : nullableDate(v.observedAtUtc) && (v.price === null || isPlainDecimal(v.price))) &&
  (v.plannedPrice === null || isPlainDecimal(v.plannedPrice))
export const isMonitorCoverage = (v: unknown): v is MonitorCoverage => object(v) && id(v.playId) && id(v.contractId) &&
  recordedStates.includes(v.recordedState as string) && (v.state === 'stale' || v.state === v.recordedState) &&
  (v.recordedState === 'known' ? typeof v.covered === 'boolean' : v.covered === null) &&
  isFilledText(v.reason, 48) && date(v.checkedAtUtc) &&
  Array.isArray(v.stops) && v.stops.length <= 200 && v.stops.every(isMonitorStopEvidence)

export const isAccountMonitor = (v: unknown): v is AccountMonitor => object(v) && id(v.accountId) &&
  ['live', 'stale', 'degraded', 'credential-error', 'idle'].includes(v.state as string) &&
  nullableDate(v.lastAttemptAtUtc) && nullableDate(v.lastSuccessAtUtc) && nullableDate(v.nextRunAtUtc) &&
  ['idle', 'pending', 'running'].includes(v.manualState as string) &&
  (v.manualState === 'idle' ? v.runTrigger === null : ['owner', 'refresh', 'scheduled'].includes(v.runTrigger as string)) &&
  nullableDate(v.nextManualAllowedAtUtc) &&
  (v.error === null || text(v.error, 2000)) && typeof v.workerOnline === 'boolean' &&
  Array.isArray(v.discrepancies) && v.discrepancies.every(d => isMonitorDiscrepancy(d) && d.accountId === v.accountId) &&
  (v.discrepanciesTruncated === undefined || typeof v.discrepanciesTruncated === 'boolean') &&
  (v.coverage === undefined || Array.isArray(v.coverage) && v.coverage.length <= 500 && v.coverage.every(isMonitorCoverage) &&
    new Set(v.coverage.map(c => c.playId)).size === v.coverage.length) &&
  (v.uncheckedPlays === undefined || isCount(v.uncheckedPlays))

export const isMonitorSummary = (v: unknown): v is MonitorSummary => object(v) && typeof v.workerOnline === 'boolean' &&
  nullableDate(v.heartbeatAtUtc) && Array.isArray(v.accounts) && v.accounts.length <= 200 && v.accounts.every(isAccountMonitor) &&
  new Set(v.accounts.map(a => a.accountId)).size === v.accounts.length

export const isMonitorRunAccepted = (v: unknown): v is MonitorRunAccepted => object(v) && id(v.requestId) && isAccountMonitor(v.monitor)
export const isMonitorEvent = (v: unknown): v is MonitorEvent => object(v) &&
  ['id', 'type', 'ownerId', 'runId'].every(key => id(v[key])) && text(v.dedupKey, 1024) && v.dedupKey.length > 0 &&
  isPositiveCount(v.version) &&
  isEventSequence(v.sequence) && severity(v.severity) && date(v.occurredAt) && date(v.detectedAt) && nullableDate(v.expiresAt) &&
  ['timely', 'late'].includes(v.timeliness as string) && ['current', 'superseded'].includes(v.relevance as string) &&
  object(v.subject) && Object.keys(v.subject).length === subjectKeys.length &&
  subjectKeys.every(key => object(v.subject) && (v.subject[key] === null || id(v.subject[key]))) &&
  object(v.source) && Object.keys(v.source).length === 3 && id(v.source.trigger) && ['orderIds', 'fillIds'].every(key => v.source && object(v.source) &&
    (v.source[key] === null || Array.isArray(v.source[key]) && v.source[key].every(id))) && object(v.data)

/** `id` is the frame's SSE `id:` line: an event's resume cursor. Notification frames never read it. */
export function monitorFrame(event: string, data: string, id?: string): MonitorStreamEvent | null {
  let body: unknown
  try { body = JSON.parse(data) } catch { return null }
  if (event === 'event' && isMonitorEvent(body) && isEventCursor(id)) return { type: 'event', event: body, cursor: id }
  if (event === 'status' && object(body) && (body.state === 'ready' || body.state === 'reset') && isEventCursor(body.nextCursor)) {
    return { type: 'status', status: { state: body.state, nextCursor: body.nextCursor } }
  }
  if (event === 'notifications' && isNotificationsStatus(body)) return { type: 'notifications', status: body }
  if (event === 'notification' && isNotificationFrame(body)) return { type: 'notification', frame: body }
  return null
}

export const isMonitorEventPage = (v: unknown): v is MonitorEventPage => object(v) && Array.isArray(v.events) && v.events.length <= 200 &&
  v.events.every(isMonitorEvent) && isEventCursor(v.nextCursor) && typeof v.resetRequired === 'boolean'
