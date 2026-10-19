# Play monitor API implementation contract

October 14, 2026, revised October 16 for the final stack. Shared contract for the authorized backend/frontend workers. Implements the [monitor plan](play-monitor-plan.md), not notification delivery. Existing Play/execution DTO fields and approved layout remain intact.

All routes are authenticated and owner-scoped. Responses use `Cache-Control: no-store`. Times are ISO UTC or null. Decimal values in event/discrepancy details are strings. Event sequences are decimal strings, avoiding JavaScript integer precision limits. Browser cursors are opaque strings `{epoch}.{sequence}` (or `0` for a stream not yet seen); the epoch is rotated by the restore procedure. Only `frontend/src/api/event-cursor.ts` parses, validates or orders them.

## Monitor state

`GET /api/monitor` returns:

```ts
interface MonitorSummary {
  workerOnline: boolean
  heartbeatAtUtc: string | null
  accounts: AccountMonitor[] // bounded to 200, enabled accounts eligible for monitoring
}
interface AccountMonitor {
  accountId: string
  state: 'live' | 'stale' | 'degraded' | 'credential-error' | 'idle'
  lastAttemptAtUtc: string | null
  lastSuccessAtUtc: string | null
  nextRunAtUtc: string | null
  manualState: 'idle' | 'pending' | 'running'
  runTrigger: 'owner' | 'refresh' | 'scheduled' | null // who started the pending/running work; null when idle
  nextManualAllowedAtUtc: string | null
  error: string | null // safe server-authored text, never venue payloads or credentials
  workerOnline: boolean
  discrepancies: MonitorDiscrepancy[]
}
```

`GET /api/accounts/{id}/monitor` returns `AccountMonitor`; foreign/missing accounts return 404. The summary endpoint avoids a separate state request per account row. Offline is a system condition rather than a mutually exclusive account state.

`POST /api/accounts/{id}/monitor/run`, with no body, returns 202:

```ts
interface MonitorRunAccepted {
  requestId: string
  monitor: AccountMonitor
}
```

The 202 response reports `manualState: 'pending'` and the unchanged state; it is accepted, not done. The browser leaves pending/running when a stream event arrives, including `monitor.run-completed` (info, dedup key `monitor-run:{accountId}:{requestId}`, data `{ trigger, outcome: 'succeeded' | 'failed' | 'superseded', state }`), which every owner Check emits on completion even when the account state did not change (a deferred Check stays pending and emits nothing; scheduled runs never emit it; a Check left pending on an account that was then disabled or lost its source is settled by discovery as `superseded`, and an owner Check left unclaimed for `Lease` + `HeartbeatTimeout` completes as `superseded` when a later Check or Refresh is admitted, its check still due for a returning worker); while a displayed account is not idle it also re-reads the stored account monitor every few seconds. Scheduled runs share admission and show as scheduled, not as an owner Check.

Busy/cooling requests return 429 with `Retry-After`; ineligible accounts (no eligible venue Plays, pending linked orders or discrepancy work) return 409. With the worker heartbeat stale (`workerOnline: false`) an otherwise admissible Check returns 503 "The monitor is offline; try again later." and records nothing (October 16). The button counts down from server timestamps, disables during pending/running and does not create runs on page load. Scheduled and manual runs share admission. A failed attempt also starts the configured cooldown.

## Events

`GET /api/events?after={cursor}&limit={1..200}` returns:

```ts
interface MonitorEventPage {
  events: MonitorEvent[]
  nextCursor: string // {epoch}.{sequence}; resume from it
  resetRequired: boolean
}
interface MonitorEvent {
  id: string
  type: string
  version: number
  ownerId: string
  sequence: string
  runId: string
  dedupKey: string
  severity: 'info' | 'warning' | 'critical'
  occurredAt: string
  detectedAt: string
  timeliness: 'timely' | 'late'
  relevance: 'current' | 'superseded'
  expiresAt: string | null
  // Every optional field is present as an explicit null, never omitted.
  subject: { accountId: string | null; venueId: string | null; contractId: string | null; playId: string | null;
             entryId: string | null; levelId: string | null; discrepancyId: string | null }
  source: { trigger: string; orderIds: string[] | null; fillIds: string[] | null }
  data: Record<string, unknown>
}
```

A cursor from another epoch (a restored database), below the retention floor, ahead of the database or a bare pre-epoch sequence returns `resetRequired: true`, no events and the `nextCursor` to resume from after reloading stored state. `0` starts at the beginning of retained history.

`GET /api/events/stream?after={cursor}` uses authenticated fetch SSE through the existing decoder. `event` frames contain `MonitorEvent` and an SSE `id:` line holding that event's resume cursor. `status` frames contain `{ state: 'ready' | 'reset', nextCursor: string }`; `ready` echoes the accepted cursor normalized with the epoch, and reset requires reloading stored state, not replaying historical popups. Keepalive comments need no UI action. Limit to four streams per owner and cancel when the API connection changes/disconnects. One shell subscription serves the browser; it is a database-cursor reader, not a broker subscription.

Before its first stored-state read the shell reads the current head and opens the stream from it, so a change committed during the initial load is delivered rather than skipped. There is no dedicated head read: `event-cursor.ts` sends a bare pre-epoch sequence beyond any retained one (`after=9223372036854775807&limit=1`), which always returns `resetRequired` with the head as `nextCursor`; that probe never leaves the module. The browser resumes after each event from its SSE `id:` cursor; an event in another epoch is new whatever its sequence. Reconnects back off from 2 s to 60 s with jitter, honour `Retry-After`, reset only after a stable connection or an event, and reload stored state only on `reset`.

The UI reloads stored state on relevant events and polls stored state every 60 seconds when disconnected from the stream. These reads do not contact venues. No event/financial-data browser persistence is introduced.

## Execution and discrepancies

`GET /api/plays/{id}/execution` adds `monitor: AccountMonitor | null`, `discrepancies: MonitorDiscrepancy[]` and `watches: MonitorWatch[]`. Keep all existing fields, including P1 execution issues. An untracked/manual/retrospective Play may have null monitor. `POST /api/plays/{id}/execution/check` no longer reads venues: it records the same manual request as `monitor/run` for the Play's account and answers like it (202 `MonitorRunAccepted`, 429 with `Retry-After`, 409 when the Play cannot be monitored); the browser uses `monitor/run`.

```ts
// A linked order (exit or entry) of a Closed or Cancelled Play, watched until the venue reports it filled, cancelled
// or rejected, a complete open-order read no longer lists it, or Vessel:Monitor:WatchLifetime (7 days) passes.
interface MonitorWatch {
  linkId: string
  playId: string
  orderId: string
  role: 'entry' | 'stop' | 'target' | 'exit'
  state: 'watching' | 'resolved' | 'unverifiable' // unverifiable is persistent and never claims the order is gone
  startedAtUtc: string
  endedAtUtc: string | null
}
```

`POST /api/accounts/{id}/sync` (account Refresh) stays synchronous: 200 with the refreshed account, 429 with `Retry-After` while the account is checking or cooling down or the venue deadline applies, 502 when the read failed or changed settings superseded it. Its outcome never changes the monitor state, backoff or scheduled check, and a refused Refresh is never replayed later.

```ts
interface MonitorDiscrepancy {
  id: string
  accountId: string
  playId: string | null
  contractId: string
  kind: string
  severity: 'info' | 'warning' | 'critical'
  state: 'candidate' | 'open' | 'resolved' | 'dismissed' // a candidate is visible only once announced unverifiable
  openedAtUtc: string
  confirmedAtUtc: string | null
  resolvedAtUtc: string | null
  dismissedAtUtc: string | null
  note: string | null
  details: Record<string, unknown>
  // 'unverifiable': the subject stayed unobservable beyond Vessel:Monitor:UnobservableLifetime (7 days). Neither resolved
  // nor still checked; dismissable; cleared by a later fresh decision. Both fields are null otherwise, never omitted.
  outcome: 'unverifiable' | null
  unverifiableAtUtc: string | null
  // Source time of the last decisive (fresh present or absent) observation of the subject; the unobservable lifetime
  // counts from it. Incomplete or stale reads do not move it. Null only when none is recorded, never omitted.
  lastObservedAtUtc: string | null
}
```

`POST /api/plays/{id}/discrepancies/{discrepancyId}/dismiss` accepts `{ note?: string | null }` and returns 204; only records with an outstanding announcement (open, or announced `unverifiable`) can be dismissed; the dismissal publishes `discrepancy.resolved` (`dismissed`) and stores the note. Dismissing an already dismissed record that has no new outstanding announcement is a no-op: 204, no event, the original note kept. A dismissed record whose changed evidence later got an outstanding `unverifiable` announcement is closed by dismissing it again, which publishes the dismissal and replaces the note. Any other state (a candidate, a resolved record) returns 409. An instrument-level discrepancy may be dismissed from an involved Play after server validation. `POST /api/accounts/{id}/discrepancies/{discrepancyId}/dismiss` supports the account-detail view and returns 204. Notes are bounded plain text. Dismissal does not stop monitoring or change executions.

Render monitor state within the existing Execution area, exceptional account badges and an offline shell banner. Preserve component theme tokens and accessible controls. No toast/notification system or new layout is included.

**Incidents (October 19, Phase 0 item 0.4a).** The browser derives one persistent incident count from `GET /api/monitor` with a shared helper (`frontend/src/features/monitor/incidents.ts`), never from notifications (preferences, read state and withdrawal make them incomplete). An incident is an `open` record, or an `unverifiable` record whose announcement has not been dismissed since (no `dismissedAtUtc` at or after `unverifiableAtUtc`); this is the same set the dismiss route accepts. An open record keeps its severity; an outstanding unverifiable record that is not open counts as a warning. Incidents map to Plays through `playId`, or `details.playIds` for instrument kinds (at most 50 are listed; the account still counts the rest). The count appears on the Plays navigation entry, on Plays list rows and in the Play header (which brings the Execution tab forward), and as a conditional row above the Overview metric tiles. The Execution tab and account monitor headings follow open incidents ("Needs attention · 1 critical") and never say "Monitored" beside one. Records show a human title per kind, a fact table (open quantity, stop quantity, uncovered quantity only when `details.stopCoverage` is `orders`, otherwise whole-position or unknown coverage, last observed) and one next action; the recorded JSON stays behind a "Recorded details" disclosure. Unknown values read "Unknown", never 0. The summary covers enabled sourced accounts with active work (at most 200) and at most 500 records per account, so the count is bounded by the same limits.
