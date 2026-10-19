# Play monitor API implementation contract

October 14, 2026, revised October 16 for the final stack. Shared contract for the authorized backend/frontend workers. Implements the [monitor plan](play-monitor-plan.md), not notification delivery. Existing Play/execution DTO fields and approved layout remain intact.

All routes are authenticated and owner-scoped. Responses use `Cache-Control: no-store`. Times are ISO UTC or null. Decimal values in event/discrepancy details are strings. Event sequences are decimal strings, avoiding JavaScript integer precision limits. Browser cursors are opaque strings `{epoch}.{sequence}` (or `0` for a stream not yet seen); the epoch is rotated by the restore procedure. Only `frontend/src/api/event-cursor.ts` parses, validates or orders them.

## Monitor state

`GET /api/monitor` returns:

```ts
interface MonitorSummary {
  workerOnline: boolean
  heartbeatAtUtc: string | null
  accounts: AccountMonitor[] // bounded to 200: enabled accounts eligible for monitoring, or holding an outstanding incident
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
  discrepancies: MonitorDiscrepancy[] // at most 500 visible records, outstanding incidents first
  discrepanciesTruncated?: boolean    // October 19: more records exist than listed; always sent, optional for older producers
  coverage?: MonitorCoverage[] // October 19; always sent by this API, optional for older producers (see Stop coverage)
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
  // counts from it. Incomplete or stale reads do not move it. Null only when none is recorded, never omitted by this API.
  // The browser reads its absence (an older API) as unknown rather than rejecting the record (October 19).
  lastObservedAtUtc: string | null
}
```

`POST /api/plays/{id}/discrepancies/{discrepancyId}/dismiss` accepts `{ note?: string | null }` and returns 204; only records with an outstanding announcement (open, or announced `unverifiable`) can be dismissed; the dismissal publishes `discrepancy.resolved` (`dismissed`) and stores the note. Dismissing an already dismissed record that has no new outstanding announcement is a no-op: 204, no event, the original note kept. A dismissed record whose changed evidence later got an outstanding `unverifiable` announcement is closed by dismissing it again, which publishes the dismissal and replaces the note. Any other state (a candidate, a resolved record) returns 409. An instrument-level discrepancy may be dismissed from an involved Play after server validation. `POST /api/accounts/{id}/discrepancies/{discrepancyId}/dismiss` supports the account-detail view and returns 204. Notes are bounded plain text. Dismissal does not stop monitoring or change executions.

Render monitor state within the existing Execution area, exceptional account badges and an offline shell banner. Preserve component theme tokens and accessible controls. No toast/notification system or new layout is included.

**Incidents (October 19, Phase 0 item 0.4a).** The browser derives one persistent incident count from `GET /api/monitor` with a shared helper (`frontend/src/features/monitor/incidents.ts`), never from notifications (preferences, read state and withdrawal make them incomplete). An incident is an `open` record, or an `unverifiable` record whose announcement has not been dismissed since (no `dismissedAtUtc` at or after `unverifiableAtUtc`); this is the same set the dismiss route accepts. An open record keeps its severity; an outstanding unverifiable record that is not open counts as a warning. Incidents map to Plays through `playId`, or `details.playIds` for instrument kinds (at most 50 are listed; the account still counts the rest). The count appears on the Plays navigation entry, on Plays list rows and in the Play header (which brings the Execution tab forward), and as a conditional row above the Overview metric tiles. The Execution tab and account monitor headings follow open incidents ("Needs attention · 1 critical") and never say "Monitored" beside one. Records show a human title per kind, a fact table (open quantity, stop quantity, uncovered quantity only when `details.stopCoverage` is `orders`, otherwise whole-position or unknown coverage, last observed) and one next action; the recorded JSON stays behind a "Recorded details" disclosure. Unknown values read "Unknown", never 0. The summary covers enabled sourced accounts with active work or an outstanding incident (at most 200) and at most 500 records per account. Each account lists its outstanding incidents (open, or unverifiable and not dismissed since) ahead of historical records, each group in the previous order (newest confirmation first), so history never pushes an incident past the cap; `discrepanciesTruncated` says the cap left records out, and the browser then notes "Older records not shown." under the records. Only when a truncated account lists nothing but incidents can more exist: the Plays entry, the Overview row and the account heading then show a lower bound ("500+ incidents"), and per-Play counts stay what is listed. An incident keeps its account listed after scheduling has nothing left to check (for example, the last Play closed, its watches ended and its record became unverifiable), until the owner dismisses it; it never makes the account eligible for checks. Disabled and foreign accounts stay out. The browser reads a record without `lastObservedAtUtc` (an older API) as unknown instead of rejecting the record, the summary or the Play's execution.

## Stop coverage

October 19, Phase 0 item 0.4c (review defects D-09, D-18 and the chart part of D-08). `AccountMonitor.coverage` lists the last stop-coverage outcome the monitor recorded for each currently Open Play of the account (at most 500), in the summary, the account monitor and the Play execution's `monitor` alike. It is an added optional field: this API always sends it (an empty array when nothing is recorded), and the browser guard accepts its absence from an older producer.

```ts
interface MonitorCoverage {
  playId: string
  contractId: string
  // Projected at read time: 'stale' once checkedAtUtc is older than twice Vessel:Monitor:Interval (the account's own
  // staleness rule), otherwise recordedState. 'stale' is never stored.
  state: 'known' | 'shared' | 'unknown' | 'unsupported' | 'stale'
  recordedState: 'known' | 'shared' | 'unknown' | 'unsupported'
  covered: boolean | null // only for recordedState 'known'; null otherwise, never omitted
  reason: string          // e.g. 'stop-orders', 'no-stop', 'short', 'opposing-plays', 'stale-order-read', 'current-book-not-read'
  checkedAtUtc: string
  stops: MonitorStopEvidence[]
}
interface MonitorStopEvidence {
  entryId: string
  levelId: string
  // resting: a linked closing stop (exit side, stop mechanics) rested in the current book at observedAtUtc, at the saved
  // level price within the matcher's step; resting-elsewhere: the same at another price; not-seen: a complete, fresh book had none;
  // unknown: the book could not tell; not-read: the venue's order reader does not return the current book.
  evidence: 'resting' | 'resting-elsewhere' | 'not-seen' | 'unknown' | 'not-read'
  orderId: string | null
  observedAtUtc: string | null // always set for both resting kinds
  price: string | null          // the resting order's exact acting price; always set for both resting kinds
  plannedPrice: string | null   // the saved level price the check compared it with; null for an unpriced level
}
```

States and reasons are listed in the [monitor plan](play-monitor-plan.md#stop-coverage). `unsupported` comes from the `currentOrderBook` venue capability ([venue registry](venue-registry.md#current-order-book-october-19-2026)), never a venue name. Coverage is a record, not a discrepancy, and never counts as an incident.

The browser:
- heads the monitor panel with open incidents first (0.4a), then with coverage that is not plainly covered. A known uncovered Play is named by its reason, most serious first: "Stop cancelled at the venue", "No stop at the venue", "Stop short of the position" (any other reason: "Stop does not cover the position"). Then, least certain first: "Stop coverage unknown", "Stop coverage not checked yet" (an Open, tracked Play with no recorded row, e.g. after the migration or once reconciliation opens it, until the next check), "Stop coverage stale", "Current book not read", "Stop shared with other Plays". It says "Monitored" only when the run is live and every Play in scope is recorded and known covered. The Execution tab scopes this to its own Play; account detail to the account's recorded rows;
- adds one line per distinct outcome that is not plainly covered (the reason in words, with a count when several Plays share it), and, where the venue states `currentOrderBook: false`, even before a Play is open: "Order-book checks do not run here: missing, short, cancelled and excess stops, orphaned exits and entries, and level drift. This venue does not return its current order book.";
- labels each planned stop of an Open, tracked Play on the chart with the evidence's words, times in UTC like the chart axis and captures: "resting as of Oct 14, 14:02 UTC" (confirmed only for `resting` while the record is not stale and the drawn line still stands at `plannedPrice`), "resting at <price> as of …" (`resting-elsewhere`, or a line the unsaved draft moved away from the saved level), "not seen at the venue", "unknown" (no recorded check, a level the check did not record, stale absence, or a resting linked order that is not a recognized closing stop) or "not read" (not monitored, or no current book). A stop without a confirmed resting order is drawn with a hollow tag and a fainter, sparser line in its theme colour; positions and layout do not change. Drafts and plans without an Open position keep their plain tags.

The contract fixture (`frontend/src/test/contracts/monitor-api.json`) records real coverage from the end-to-end flow (an Open ETH Play with a resting linked stop) plus serialized known and stale unsupported samples.
