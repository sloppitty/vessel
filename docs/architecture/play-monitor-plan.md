# Play monitor: development plan

October 14, 2026, revised October 15 and 16, 2026. Status: **implementation authorized on October 14, 2026, with Sol implementation and independent verification, final isolated end-to-end checks and stacked pull requests. On October 15 the owner replaced the delivery design with topics and subscriptions and accepted the review corrections below; the stack (#43–#47) was amended and, on October 16, restacked into one linear chain before merge.**

The owner asked to shape a background monitor before building it. The monitor watches active Plays at their venues, detects fills, checks stops and targets against the real venue position and moves Play status automatically. Every change raises an event. A later, platform-independent notification system consumes those events: Telegram first, Discord later, and in-app popups. This is step 4 of the [Play lifecycle](play-lifecycle.md#monitoring-and-notifications). Building notifications is out of scope; the event contract is designed for them.

## What the owner has settled

- **Periodic, not real time.** Each monitored account is checked every 10 minutes by default; 20 or 30 minutes are acceptable.
- **Outbox plus broker.** Events are written to PostgreSQL in the same transaction as the Play change, then relayed to RabbitMQ (or a similar broker) so the system can scale later. W0 recommends Wolverine 6.50.0 with the integration conditions below; the owner authorized implementing this direction after the unit-of-work refactor.
- **A separate worker from the start.** The monitor runs in its own process and container, not inside the API.
- **Positions are compared.** The account sync already stores the venue's positions, so each run compares them with the open Plays.
- **Late events are delivered while still relevant**, and are marked so subscribers can treat them differently.
- **Alerts on a Play's drawings** (price crossing a drawn line or level) are wanted, but they need near-real-time prices and a different path. Deferred; see [Price and drawing alerts](#price-and-drawing-alerts-deferred).
- **Tracking lives in the repository.** No Plane or Outline.

Settled on October 14 after the first draft:

- **Interval:** configurable, starting at 10 minutes.
- **Protection grace:** configurable, starting at 10 minutes. After it, an open position without protection is shown as an issue.
- **Worker packaging:** the worker ships in the API image with its own entrypoint, like `migrate`.
- **Event retention:** configurable, starting at 90 days for every event type. Play history, imported facts and deduplication records remain separate; pending delivery work is not silently deleted. (How subscriptions hold retention was settled on October 15, below.)
- **Browser checks on demand only.** The worker still checks automatically at the configured interval. Opening a Play does not request an extra run. The Check button is blocked while a run is pending or running and then for a configurable cooldown, shown as a countdown. The server enforces the same rule, so repeated presses or several tabs cannot queue extra venue reads.
- **Check cooldown:** 60 seconds by default, configurable, measured from completion including failed attempts.
- **Closed-Play follow-up:** keep checking linked exits (and, since the #45 review, linked entries) after closure or cancellation at the normal interval until fresh venue evidence confirms they are no longer resting. Dismissing a warning does not stop those checks. Account-disable and credential guards still apply.
- **RabbitMQ management page:** exposed for now, for troubleshooting.
- **Wolverine fit is checked first** (W0 spike) before any product work. W0 is complete with a conditional-go recommendation. Subsequent implementation authorization is recorded above.
- **Unit of work first.** After W0, the owner chose a bounded unit-of-work refactor to centralize saves and transaction completion before integrating Wolverine. The top-level use case owns the boundary; nested stores do not commit or dispatch. Necessary intermediate saves remain inside the same transaction.

Settled on October 15, after review of the stack and before any of it merged:

- **Topics and subscriptions.** Delivery follows the Azure Service Bus model, implemented on RabbitMQ through Wolverine; the broker choice is unchanged. Publishers write each event once to a topic and know nothing about who listens. Each subscriber owns a subscription: its queue, filter, limits, dead-letter queue and its own progress in PostgreSQL. Recovery is the subscriber's job, from the database. This replaces the earlier configured durable consumers, publisher-written delivery rows and the producer-side republish sweep. See [Topics, subscriptions and delivery](#topics-subscriptions-and-delivery).
- **No production subscription yet.** The placeholder `monitor` event consumer is removed. Tests and the release check use a verification subscription. The Notification Hub will be the first real one.
- **Notification Hub boundary.** A future single `notifications` subscription owns per-owner rules and hands work to per-provider queues. Only the boundary is fixed here; it has its own plan. See [Notification Hub](#notification-hub-future).
- **Messaging is one adapter, features are modules.** Wolverine and RabbitMQ code leaves `Vessel.Persistence` for one messaging infrastructure project. Feature modules (monitor now, notifications later) register their own topology, handlers and hosted services. The worker image runs the modules named in `Vessel:Worker:Modules`, so modules can scale as separate services. See [Modules and hosts](#modules-and-hosts).
- **Neutral event names.** Events already cover monitor and credential changes, so `PlayEvent` becomes `VesselEvent` and the tables lose their `play_` prefix. Type strings do not change.
- **Contract corrections.** Optional fields serialize as explicit `null`; events within one transaction are sequenced in causal order; account deletion removes that account's event identities. The catalogue's dedup keys now match the code, except that monitor state moves to a persisted `StateRevision`; `monitor.run-completed` is added. See [Event contract](#event-contract).
- **Delivery contract tightened** after an independent review: inline listeners with receipt-driven settlement, one dispatcher for live delivery and catch-up, an explicit watermark and retention protocol, immutable filters, a mandatory epoch rotation on restore, and starting defaults. See [Topics, subscriptions and delivery](#topics-subscriptions-and-delivery).
- **Review corrections** to the unit of work, worker and discrepancy rules are recorded in [Review corrections, October 15](#review-corrections-october-15).
- **Migrations change in place.** Nothing in the stack is merged or deployed, so its migrations are edited in place instead of adding rename migrations. Any development database that applied the branch migrations must be recreated.

Unchanged rules:
- Venues are read-only.
- A price touch is never a fill.
- Linking is automatic only when unambiguous.
- Discrepancies are flagged, never silently corrected.
- Code checks capabilities, never venue names.
- Owner scope applies everywhere.
- Values stay exact decimal strings.
- Secrets never appear in logs, URLs or messages.
- Migrations are explicit and never run at startup.
- Tests never touch the owner's database.

## Where things stand

This section describes `main` before the monitor stack. The stack (#43 unit of work, #44 events and outbox, #45 worker, #46 discrepancies, #47 monitor UI) implements the plan but is not merged, deployed or published; it is being amended to the October 15 design.

- **Transitions are browser-driven.** `PlayExecutionService.CheckAsync` runs only when the browser calls it: when a Play is opened, and every 60 s while a tracked Play is visible. It syncs the account (snapshot and fills), reads orders, links them and applies transitions under the `vessel-execution:{account}` advisory lock. `LinkAsync` (owner confirmation) also applies transitions. With no tab open, nothing is detected.
- **Venue reads hold a database lock.** `WorkspaceService.SyncAsync` makes its venue calls inside the account row lock (`SELECT … FOR UPDATE`).
- **Coverage is not machine-readable enough yet.** `VenueOrderReadResult` has orders, one observation time and a notice, but no complete-open-orders flag. Add explicit adapter coverage before interpreting an absent order as missing protection. A human-readable notice is not a safe completeness contract.
- **No background jobs exist** apart from the Hyperliquid market stream relay. There is no scheduler, job runner or broker.
- **Owner scope comes from the HTTP request.** `VesselDbContext` filters every query by `IJournalOwnerContext`, and the only implementation, `HttpJournalOwnerContext`, reads the request.
- **Release layout.** The release image runs the API and carries the `/app/migrate` bundle. Compose runs postgres, migrate, api and web.

## Architecture

```text
 browser ──REST──► API ── owner actions, Check ──┐
    ▲               │                            │ unit of work: change + event + outbox envelope
    └──SSE── cursor reader (database only)       ▼
                                      PostgreSQL (record)
                          events, cursors, subscriptions, receipts, monitor_accounts
                                 ▲           │ relay, after commit
            catch-up sweep ──────┘           ▼
            (per subscription)     RabbitMQ (transport)
                                   ├─ topic exchange vessel.events ── routing key = event type
                                   │    ├─ vessel.events.sub.<name>      (+ .dlq)  one per subscription
                                   │    └─ later: vessel.events.sub.notifications ──► vessel.notify.<provider>
                                   └─ queue vessel.commands.monitor (+ .dlq)
                                                 │
        ┌──────── worker image, modules from Vessel:Worker:Modules ────────┐
        │ worker-monitor: scheduler, RunAccountMonitor ──► venue REST (read-only)
        │ later worker-notifications: hub subscription, provider senders │
        │ every worker: catch-up and retention sweeps (replica-safe)     │
        └──────────────────────────────────────────────────────────────────┘
```

- **`Vessel.Worker`** (new project) is a generic host with no HTTP ingress. It runs the modules its configuration names: the monitor now, the Notification Hub later. It uses the same Domain, Application, Persistence, Infrastructure and Messaging projects as the API.
- **The API** stops reading venue orders for Plays. It appends events for owner actions (link, confirm, unlink, dismiss), sends `RunAccountMonitor` commands and streams committed events to the browser from the database.
- **RabbitMQ is transport, not the record.** Everything needed to rebuild state, including each subscription's progress, lives in PostgreSQL.

### Dependency rule

Application defines the ports. One messaging adapter integrates with Persistence's transaction boundary; Domain and Application never reference Wolverine or RabbitMQ. The ports are:
- `IUnitOfWork`: execute a database write operation on the shared owner-scoped context, save and finish its transaction.
- `IVesselEventSink` (was `IPlayEventSink`): append an event to the current unit of work.
- `IVesselEventReader` (was `IPlayEventReader`): read committed events after a cursor.
- `IMonitorCommandSink`: request a run for an account.
- A subscriber port for event handlers and a command-handler port, so feature handlers never see broker types.
- Later, `INotificationChannel` for notification providers.
- `TimeProvider`.

W0 recommends keeping Wolverine behind these ports rather than writing a replacement outbox. See [the fit investigation](wolverine-fit-research.md) for tested versions and limitations.

### Modules and hosts

- **One messaging adapter.** Wolverine and RabbitMQ code moves out of `Vessel.Persistence` into one infrastructure project, `Vessel.Messaging`. It references Application and Persistence and plugs into the unit of work through Persistence's participant hook. Persistence keeps the event tables, cursor, dedup and receipts, and references no Wolverine or RabbitMQ package. The architecture tests encode this direction with new rows for Messaging and Worker: Domain → nothing; Application → Domain; Persistence → Application, Domain; Messaging → Application, Persistence; API and Worker → the projects they compose, including Messaging. Domain and Application keep no package references.
- **Feature modules.** A small Vessel-owned module contract lets each feature declare its services, subscriptions (name, filter, limits, handler), command queues and handlers, and hosted services. Shared transport and module descriptors live in `Vessel.Messaging` (for example `Modules/Monitor`), business ports in Application, and worker-only hosted-service registration in `Vessel.Worker`. A small explicit registry lists the compiled-in modules; there is no plugin discovery. No central function hard-codes API or worker roles. A module has two sides: what any host needs in order to publish to it (for example the `RunAccountMonitor` route), and what a host that runs it adds (listeners, handlers, schedulers).
- **Worker modules by configuration.** `Vessel:Worker:Modules` lists the modules a worker runs, default `["monitor"]`. The monitor worker (Compose service `worker`) and, later, `worker-notifications` are separate services from the same image and scale independently. An unknown module name stops startup. Changing a deployment's module set needs an explicit drain of its envelope schema first.
- **Replica-safe maintenance.** Catch-up and retention sweeps run in workers and are safe with several replicas: claims use `FOR UPDATE SKIP LOCKED` or advisory locks. A subscription's catch-up runs in the deployment that hosts that subscription. Settled for #45: maintenance is not a module; every worker host registers it, running catch-up for the subscriptions it hosts and the owner's retention sweep (serialized per owner), so the monitor worker covers retention today and later workers overlap safely. The API never runs it.
- **Deployments.** A worker's logical deployment is `Vessel:Worker:Deployment` (default: the first module in `Vessel:Worker:Modules`); it names the envelope schema (`wolverine_{deployment}`, or `Vessel:Messaging:StorageSchemas:{deployment}`) and the Wolverine service name. Provisioning prepares the deployments in `Vessel:Messaging:Deployments:{name}` (default: `api` with no modules and one deployment per compiled-in module) and records each schema's deployment and module set in `messaging_deployments`, insert-only; a changed module set is rejected. Every messaging host checks its row before Wolverine starts.
- **Provisioning covers every module.** The explicit provision step declares the topology of every known module and registers their subscriptions, whichever modules a given deployment runs. Runtime never declares topology.

### Unit of work and transaction boundary

W0 reproduced Vessel's real nested account-row/advisory-lock transaction and multiple inner saves. It proved that saving and outbox dispatch must remain separate from transaction completion. The owner then chose a unit of work to own that boundary instead of adding commit hooks to individual stores.

- Application declares `IUnitOfWork` with an `ExecuteAsync` boundary and no EF or Wolverine types. Its implementation reuses the scoped `VesselDbContext`; it does not add a second change tracker or a generic repository framework.
- The top-level write use case starts one unit of work. Stores acquire the existing owner-scoped account/management/advisory locks and change entities inside that transaction. Nested services participate without starting or completing another unit of work.
- Centralize `SaveChangesAsync`, commit and rollback in the implementation. Stores no longer independently commit or send messages. Preserve write-owner validation, concurrency conflict handling and lock ordering.
- One transaction may still need intermediate database saves. `WorkspaceStore.SaveRefreshAsync` currently removes position/wallet rows before inserting replacements with the same composite keys; reconciliation saves imported orders before querying them for matching. Keep those necessary saves behind the unit-of-work persistence implementation. They do not commit, dispatch or expose partially committed state.
- The final monitor path reads venues before opening the database transaction, then reloads and validates account/source/credential revisions under the locks. A transaction retry must not repeat network calls. The first refactor preserves the existing import/reconcile commit boundaries; moving network reads and changing admission happens in the worker step with its revision checks.

Once outbox integration is added, the completion sequence is:

```text
Begin database transaction
  Acquire locks and revalidate state
  Apply changes and collect events
  Persist business events and outgoing envelopes on the same transaction
  Save remaining changes
Commit once
Attempt outgoing-message dispatch
```

Do not call `SaveChangesAndFlushMessagesAsync()` inside an existing store callback. In the tested version it commits the current transaction, and the spike reproduced a business change surviving an intended rollback. Calling `FlushOutgoingMessagesAsync()` before outer commit is also unsafe: the consumer acted on a change that subsequently rolled back.

The scoped event sink requires an active unit of work. Rollback discards pending in-memory sends and ends the failed work; it must not leave tracked mutations available for a later accidental save. Dispatch happens only after successful commit. A post-commit broker error does not roll back the business operation or justify repeating it; its durable envelope remains for recovery. Do not mix Wolverine-owned transaction middleware with Vessel's unit-of-work boundary.

Clarified on October 15:
- **Nested calls save before returning.** A nested `ExecuteAsync` saves its tracked changes before it returns to its caller, so the caller's reads see them. It still does not commit.
- **Commit is not cancellable.** Cancellation is honored up to the commit; the commit itself uses no cancellation token, so a cancelled request cannot leave the outcome unknown halfway through.
- **Rollback failure keeps the cause.** If rollback also fails, the original exception is the one reported; the rollback failure is attached to it, and the scope becomes unusable.
- **Failures cannot be swallowed.** Once a participant has thrown, the unit of work rolls back even if a caller catches the exception and carries on. This is deliberate: callers must not use catch-and-continue around participants, and must start a new unit of work instead.
- **All write paths use it.** Evidence and sizing writes go through the unit of work, like Play, execution and account-import writes.
- **No object storage inside a unit.** `IUnitOfWork.IsActive` is true while a unit runs in the scope, including inside a dispatched handler's savepoint, and also for any EF, explicitly enlisted or ambient `System.Transactions` transaction. Evidence upload/deletion and Play deletion call `RequireOutermost` and refuse before any storage call, because a rollback could not undo it.
- **Savepoint isolation is the dispatcher's.** `UnitOfWork.IsolateAsync` (internal) runs a subscription handler and saves its effects in a savepoint; on failure it rolls back to the savepoint, clears tracked entities and every participant's buffered state, and keeps the unit and its receipt lock. Call it before any other buffered participant write in the unit, since that state is discarded too.

### Records and transport

| Store | What it holds | Written |
|---|---|---|
| `events` | The event history: what the browser stream and subscription catch-up read | Same transaction as the change |
| `event_cursors` | Per owner: the last allocated sequence, the retention floor and the stream epoch | Same transaction (row lock held to commit) |
| `event_dedup` | Dedup key → event ID, with the subject account; survives pruning | Same transaction |
| `event_baselines`, `event_fills` | Per account watch start and known fill identities; survive pruning | Same transaction |
| Outbox envelope (Wolverine, PostgreSQL) | One copy per event or command waiting to be sent to RabbitMQ; removed once the broker confirms | Same transaction |
| `event_subscriptions` | Per owner and subscription: owning module, filter fingerprint, the sequence it became responsible from, the low-water mark, lag status and any explicit gap | Inserted by the provision step; then advanced by the subscriber |
| `event_receipts` | Per subscription and event: `pending`, `delivered`, `skipped`, `expired` or `failed`, attempts, next attempt, safe error | By the subscriber only |
| `monitor_accounts` | The record of pending manual monitor requests (see [Scheduling](#scheduling-and-the-unit-of-work)) | Same transaction as the admission |
| RabbitMQ exchange, queues and dead-letter queues | Messages in transit, and diagnostic copies of dead-lettered deliveries | By the relay and the broker |

Publishers write no per-subscriber rows. The earlier `event_deliveries` and `play_event_envelopes` tables, the `Vessel:Events:DurableConsumers` setting and the republish sweep are removed.

- **Broker recovery.** PostgreSQL holds retained events and each subscription's progress. An empty or lost broker delays delivery for a registered subscription: its catch-up sweep finds unsettled events still in retained history. Pending manual monitor requests also live in PostgreSQL, so command recovery does not depend on the broker. Queues and bindings are still provisioned before publishers start, for timeliness; a dropped or unroutable publish is no longer a loss. Broker volume backups stay optional, provided this path passes the release test. Lost broker diagnostics and dead-letter contents are not reconstructed.
- **At-least-once delivery** within each subscription's retained responsibility window, with explicit terminal failures and gaps. A receipt is unique per owner, subscription and event. Handlers are idempotent on the event ID. Database-only effects and the receipt commit together. An external send followed by a crash can still repeat after retry; restoring an older database can also lose receipts for messages already sent. The Notification Hub must handle those limits rather than promise exactly-once delivery.
- **Catch-up cursor.** A fixed overlap does not make an ordinary database sequence safe against a transaction that commits late. Event numbers are allocated through one owner cursor row, held until the event transaction commits. Allocate near the end of the transaction, in a consistent lock order, with no network calls. A sequence below the cursor can therefore never appear later, so readers and low-water marks can advance safely, and a missing sequence (removed with a deleted account) counts as settled. A cursor outside retained history, from another stream epoch or ahead of the database returns an explicit reset requiring a state reload.
- **Retention:** `Vessel:Events:RetentionDays`, default 90 for every event type, confirmed by the owner. See [Retention](#retention) for how subscriptions hold it. Event deduplication/baseline records survive history pruning so an old fill cannot become a new event. A Play's history does not depend on events: status changes, plan revisions, links, fills and discrepancies keep it.

## Deployment

- **Compose services:** postgres, **rabbitmq** (new), migrate, api, **worker** (new, running the `monitor` module), web. A later `worker-notifications` service runs the same image with `Vessel:Worker:Modules` set to `["notifications"]`.
- **Worker image:** the worker ships in the API image with its own entrypoint (`/app/worker`), as `migrate` does today. Releases keep the `api-x.y.z` and `web-x.y.z` tags.
- **RabbitMQ:**
  - official `rabbitmq` 4.x management image, pinned;
  - AMQP not published;
  - the management page published for troubleshooting, bound to loopback by default (`VESSEL_RABBITMQ_MANAGEMENT_BIND`, default `127.0.0.1:15672`, like `VESSEL_WEB_BIND`). Reach it through an SSH tunnel or behind the TLS proxy; never plain HTTP to the internet;
  - credentials from `.env`, with the default `guest` user unusable remotely;
  - healthcheck `rabbitmq-diagnostics -q ping`;
  - a named volume for durable queues, excluded from backups;
  - quorum queues on a single node;
  - both publisher-confirmation switches enabled explicitly;
  - durable exchanges, queues and topic bindings, with each queue's dead-letter exchange, routing and dead-letter queue, provisioned before producers start, by the explicit provision step only. W0 confirmed that an unbound topic can be acknowledged and removed from the outbox without delivery; with subscriber-side catch-up that costs latency within the retention window, not data. Confirmations do not replace topology validation or subscriber receipts;
  - plain AMQP inside the private Compose network, the same trust boundary as API → PostgreSQL today. TLS is required if the broker ever leaves the host.
  - Upgrades follow RabbitMQ's rule of enabling all feature flags before the next version.
- **Migrations:** keep `AutoBuildMessageStorageOnStartup = AutoCreate.None`. The one-shot `migrate` path applies the existing EF bundle, then the explicit provision step: `IHost.SetupResources()` for every deployment's envelope storage, the broker topology of every module, and subscription registrations. W0 proved this setup path and missing-storage startup rejection in 6.50.0. Envelope mappings are excluded from EF migrations; they do not provision the full Wolverine schema. Keep the default storage-error policy and durability agents enabled. Missing-table checks are not a complete schema-drift check, so upgrades also require storage validation.
- **Envelope isolation:** one schema per logical deployment, with explicit configured names and service names in the same database: `wolverine_api`, `wolverine_monitor` and later `wolverine_notifications`. A service name alone does not isolate recovery. Replicas of one deployment share its schema; startup rejects reuse of a schema by a deployment with a different module set, so no deployment recovers envelopes it has no route for. Do not confuse Wolverine's envelope node-owner number with Vessel's journal owner ID.
- **Runtime packaging:** the tested option is `WolverineFx.RuntimeCompilation` with dynamic handlers in the runtime-only .NET 10 image, non-root and with a read-only application directory. No SDK is required in that tested image. Pregenerated handlers remain an optional, untested alternative. Pin the tested package family and reconcile EF Design/tooling versions during authorized integration; do not silently upgrade Vessel's EF runtime.
- **Credentials overlay:** mounts the key ring read-only into the worker as well, since it opens Lighter read tokens. The API still seals them. The worker never logs or forwards a token.
- **Heartbeat:** the worker writes one heartbeat row per owner (owner ID, worker ID and heartbeat time; a newer beat replaces an older one), which the API reports so the UI can say "Monitor offline". It also writes a heartbeat file that its Docker healthcheck reads. Both advance only after a productive scheduling pass (see [Health and visibility](#health-and-visibility)), not on liveness alone.
- **Upgrades and backups:** stop web, api and every worker service before a backup or upgrade, then run migrate (EF bundle and provisioning) and start them again. A worker stopped during a run loses nothing: runs are idempotent, so the next run repeats the work.
- **Restore:** stop every host → restore the database → `rotate-event-epoch` → provision → start. The rotation is mandatory; see [Browser live updates](#browser-live-updates).

### Owner scope in the worker

- **Messages:** every message carries `ownerId`. The worker validates it against the configured journal owner, then opens an immutable owner scope before resolving the database context. It loads the account through the normal owner-filtered queries. A mismatch is rejected to the queue's dead-letter queue and logged with IDs only; it is never silently acknowledged. The browser never chooses the command's owner.
- **Scheduler:** use the configured journal owner ID, matching the existing single-owner authentication setting. Do not enumerate unscoped accounts or introduce a multi-owner directory for this step.
- **Query filters** are never disabled.

## Scheduling and the unit of work

- **Unit:** one account. All its Planned, Paused and Open venue Plays share one run.
- **Eligible accounts:** enabled; venue with the `Sync` and `Orders` capabilities; a source identity; and at least one Planned, Paused or Open venue Play, or a Closed or Cancelled Play with linked exits or entries that may still be resting. Keep checking those orders at the normal interval until fresh evidence confirms they are no longer resting, even if the owner dismisses their warning. This lets `protection.orphaned` and `entry.orphaned` be detected and cleared after the last active Play closes. A required credential that is missing or unusable suspends venue reads and leaves a visible `credential-error`, rather than making the account disappear from monitoring.
- **Exit and entry-orphan watches:** a watch resolves when a read reports that specific order in a terminal state (filled, cancelled, rejected), whether or not the read covers the whole open-order book. Concluding that the order is gone from its absence still needs a complete open-order read. A watch lasts at most 7 days (`Vessel:Monitor:WatchLifetime`), measured from the later of the Play's end and the link change; a relinked order starts a new watch. October 20 (phase 1, 1.3a): an Unresolved Play is watched by the same rule from its declaration, leaves the open-Play position comparison, and a later declaration starts a new watch. Linked entries are watched like exits, so a Cancelled Play whose only linked order is a resting entry keeps the account monitored. After that it ends with a persistent, visible `unverifiable` outcome: it never resolves the orphan discrepancy or claims the order is gone.
- **`monitor_accounts`:** one row per account holding:
  - state, its `StateRevision`, interval and `next_run_at`;
  - lease holder and `lease_until`;
  - last attempt, last success and consecutive failures;
  - a safe error text;
  - the continuity watermark: the newest fill time any import has read (for gap detection).
- **Scheduler:** every 30 s, the worker claims due rows with `FOR UPDATE SKIP LOCKED` and sets a renewable 5-minute lease with a generation number. It then runs `RunAccountMonitor` on an in-process queue, at most 2 accounts at a time. Each commit checks the current lease generation; a worker that lost its lease cannot apply late results. Lease expiry recovers a crashed run. The existing account locks still serialize writes against the API.
- **Cadence:** `Vessel:Monitor:Interval`, default 10 minutes, allowed from 1 to 60 minutes, with ±10% jitter.
- **Failures:** retry after 1 and then 3 minutes, then return to the normal interval. Failures count towards `stale`. A run superseded by changed settings or a newer observation is not a failure: it neither advances the backoff nor counts towards `stale`.
- **Check on demand:** the Check button records a request ID and sends `RunAccountMonitor` through the API outbox in the same transaction; opening a Play does not. Per account, scheduled, confirmation and manual runs share one admission gate:
  - while one is pending or running, further requests are refused;
  - after it finishes, including a failed attempt, manual requests are refused until the cooldown ends (`Vessel:Monitor:ManualCooldown`, default 60 s, never longer than the interval). The cooldown gates manual requests only; it never delays a scheduled claim;
  - a refusal returns 429 with `Retry-After`. Venue `Retry-After` deadlines are clamped to at most 1 hour;
  - `monitor_accounts` records the request ID, pending/running state and `next_manual_allowed_at`, so the rule holds across tabs and API restarts.

  A scheduled run that starts before an accepted manual command covers that request. Duplicate or delayed commands carrying the same request ID cannot start another run. Completion resets the next scheduled run, so Check does not cause an immediate duplicate poll. The command travels on `vessel.commands.monitor`; its listener validates the owner, finds the matching pending request and wakes the scheduler for an early pass, then acknowledges. If the command is lost, the scheduler finds the still-pending request in `monitor_accounts` on its next tick. No new request ID is created.
- **Confirmation run:** when a run finds a new discrepancy candidate, it schedules one account-wide follow-up, no earlier than 60 s later or the protection grace deadline, whichever is later. Only a fresh, sufficiently complete read can confirm or clear it. A failed or incomplete confirmation leaves the candidate unknown and the monitor degraded. Confirmation work shares the lease and venue request budget.
- **Venue reads:** the run reuses the existing sync adapters and order reader, once per account rather than once per Play. Average interval cost does not control bursts from scheduled runs, Check, account Refresh all, retries and the chart. Bound concurrency and retries by venue capabilities and limits; honor rate-limit responses. Incremental fill cursors stay a later optimization. Exact request budgets are adapter implementation details, not a guarantee of headroom in this plan.
- **Account refresh:** shares the admission gate and read budget, but its outcome does not change the check backoff, the `stale` state or the scheduled due time. A refresh refused with 429 is answered to the owner and is never kept pending for the worker to replay later. Settled for #45: Refresh stays synchronous in the API on its own lease (the answer is the refreshed account or 502); it leaves the monitor state, last attempt and success, consecutive failures and credential revision alone, and a refresh lease left by a crashed API is released, not replayed. October 19 (owner decision 10, Phase 0 item 0.4b), replacing the #45 rule that Refresh also left history-gap evidence alone: a Refresh records the fill continuity of its own import, in the unit that retains its fills, by the history-gap rule below. It can set the gap and advances the continuity watermark, but never clears a gap; with unknown truncation it records nothing. Everything else listed here stays check-only.
- **Lock scope:** venue reads move out of the database transaction. Before reading, capture the account's source/settings and credential revision. Under the account lock, reload and revalidate owner, enabled state, source and revisions, along with the run's lease generation, before applying results. Use the existing "newer observation wins" guard as well. A changed or disabled account discards stale results. Manual account refresh must use the same read/admission path; otherwise it can bypass the monitor's throttling and restore the network-held lock.
- **Credentials:** a refused or expired token sets `credential-error` and stops retries until the credential changes; saving a token reschedules the account. A token expiring within 14 days raises `credential.expiring` once.
- **Known omissions:** adapters report `OmittedExecutions`, in-scope executions they read but left out because a fact was missing or unverifiable (Lighter: unreported realized PnL, unverified or nonzero fees). This is separate from lifetime completeness and raw truncation, never parsed from notices, and makes the run `degraded`.
- **Discovery** pages candidate accounts by ID after filtering, so every account is reached on each pass.
- **History gaps:** sometimes a response hits the venue's cap (2,000 fills on Hyperliquid) and its oldest raw fill is newer than the newest fill known before that import, or no oldest fill is reported. The account then records a gap, and checks are `degraded` ("fills may be missing"). Vessel never concludes that no fill happened.

  October 19 (owner decision 10, Phase 0 item 0.4b): every import, Refresh included, checks continuity against the newest fill known before it, the later of the continuity watermark and the newest retained fill. Fills retained through Refresh therefore no longer look like a gap to the next check, and a first import, with nothing known before it, never records one. Each import advances the watermark to the newest fill it read; a Refresh whose truncation is unknown records nothing, while a check keeps degrading on unknown truncation as before.

  The gap is sticky: a successful read alone never clears it. A Refresh never clears it, and a check clears it only when an adapter reports complete fill history, which none does today. Gaps stored before October 19, false ones included, stay until a recovery rule exists.

## Detecting fills and changes

- **Polling only.** At this cadence REST is the only path. Venue user streams (Hyperliquid `userFills`, `orderUpdates`) are not needed. They remain a possible later capability that triggers a reconcile, never a replacement for it.
- **Deduplication** uses the existing unique indexes on fills (owner, account, contract, source fill ID) and orders (owner, account, order ID).
- **Events come from Vessel's own state changes** inside the transaction: a link created, a status moved, a discrepancy opened or resolved. They never come from "rows seen". Re-reads, restarts and overlapping runs therefore emit nothing new, and the first worker run after deployment re-emits nothing already linked or transitioned.
- **Watch start and matching are separate.** The existing matcher uses `CreatedAtUtc`, not `PlannedAtUtc`, as its earliest order time. This step must not silently change that matching rule. Persist an event baseline for already-linked fills when monitoring is enabled. Pre-watch imports and baseline rows do not become fresh fill alerts; newly discovered relevant changes after the watch boundary can be late events. Relinking a known fill emits a link change, not another claim that the venue just executed it.
- **The trigger doesn't matter.** The same transition, link and discrepancy code runs for the worker and for owner actions in the API, and both append events through `IVesselEventSink`.

## Positions, stops and targets

- **Open quantity of a Play:** linked entry fills minus linked exit fills.
- **Expected position** for an account and instrument: the signed sum of its Open Plays' open quantities. Opposite Plays net out in one-way accounts. Venues that report separate long and short legs are compared per leg.
- **Venue position:** the snapshot's `SignedQuantity` for that contract, read in the same run. Keep observation times and read completeness for positions, fills and orders separately. The current sync reads the snapshot before the separate order read, so reusing it unchanged cannot satisfy a promise that the snapshot is newer than every order. A later position read helps but is not an atomic venue snapshot. Comparisons use the instrument's quantity precision, require adequate coverage and must agree on a fresh confirmation run before opening a discrepancy.

A position cannot assign fills to a Play, because it carries no order IDs. Linking stays order-based. An exit that takes the only Open Play on an instrument to flat already links automatically as an unplanned exit. With several Open Plays, it becomes a suggestion that states the venue position. What the position adds is corroboration of closes and the instrument-level discrepancies below.

### Discrepancies

A discrepancy is a flagged record. It never changes a status or a link. It opens only after the confirmation run, resolves by itself when fresh evidence shows the condition cleared, or is dismissed by the owner with an optional note. Missing rows in a partial read do not prove a cancelled stop or a flat position. A dismissed discrepancy stays dismissed until the quantities or orders involved change.

**Per Play:**

| Kind | Condition | Severity |
|---|---|---|
| `protection.missing` | Open quantity, no resting stop covering it, and the first entry fill older than the grace period (`Vessel:Monitor:ProtectionGrace`, default 10 min). Since October 23, 2026 (Phase 2, D-19 follow-up) it confirms on this condition, so an entry that keeps filling after the grace still confirms. Its fingerprint and details keep the quantities. | critical |
| `protection.cancelled` | A linked stop was cancelled without filling while the Play is Open | critical |
| `protection.short` | Resting stop quantity covering the Play is below its open quantity. Since October 22, 2026 (Phase 2, D-19) it confirms on this condition, not on its fingerprint, so a position or stop whose quantity keeps changing still confirms. Its fingerprint and details keep the quantities. | warning |
| `protection.excess` | Resting linked stop or target quantity is above the open quantity (see reduce-only below). Since October 23, 2026 it confirms on this condition, so a stop or target placed at full size while the entries fill still confirms. When a whole-position order counts the open quantity because the snapshot agrees, it rests on the snapshot and the book, and confirms only when both reads are 60 s after the first observation. | warning |
| `protection.beyond-liquidation` | A resting linked stop sits at or beyond the venue's own reported liquidation price on the loss side, so it cannot act before liquidation (Phase 2, item 8b; owner: critical). Only a fresh snapshot's venue-reported price for the position in the Play's direction counts; an opposite position leaves it undecidable (`position.opposite` reports that), and with no reported price it is undecidable by design. A stop beyond liquidation opens it from any known order read, but only the complete book, or a fresh snapshot without the Play's position, clears it. Its fingerprint is which stops are beyond, so a cross-margin price that drifts with the account does not restart confirmation. | critical |
| `protection.orphaned` | A Closed or Cancelled Play still has a resting linked stop or target, which could close another Play's position on the same instrument | warning |
| `entry.orphaned` | A Closed or Cancelled Play still has a resting linked entry order, which could open untracked exposure | critical |
| `level.drift` | A linked order's price no longer matches its plan level, for example after an edit at the venue. Suggests a plan revision, which the record offers as a rebind (October 19, below). | info |

Targets smaller than the position are not flagged, because scaling out is normal.

Whole-position orders and coverage (October 15):
- **Stops and targets are separate.** Stop coverage and target coverage are evaluated separately; a target never establishes stop protection.
- **Shared whole-position stop.** A resting whole-position stop (for example a Hyperliquid position SL) linked to an Open Play covers its sibling Open Plays only when it is on the same account and native contract, is an actual stop, and closes on the side compatible with their exposure. Then, when the venue position equals the expected total, `protection.missing` is false for the siblings. Otherwise their coverage is unknown, never a critical missing stop. One account-level order is never counted independently for several Plays.
- **Opposing Plays.** When opposite Plays net out on a one-way account, per-Play protection is unknown unless the allocation of protection to each Play is demonstrable. Equality of the net position alone never establishes per-Play protection.
- **Single Play.** With exactly one Open Play on the instrument and the venue position equal to its open quantity, a whole-position stop establishes its stop coverage, so `protection.short`, `protection.excess` and `protection.cancelled` are decidable for it.
- **Unknown is not degraded.** Coverage that is structurally unknown (shared stops while the position differs, netted opposite Plays) is shown as unknown and does not degrade the monitor. Only missing or stale evidence (an incomplete or failed read, a history gap) does. Since October 19 it is recorded as unknown [stop coverage](#stop-coverage) with its reason, so the UI no longer says "Monitored" beside it.
- **Reduce-only orders** cannot flip a position. They do not count towards `protection.excess` when the Play is the only Open Play on the instrument; the check stays when siblings exist.
- **One record per cause.** For the same orders, `protection.cancelled` supersedes `protection.missing` and `protection.short`: one record, not two or three.
- **`level.drift` is decided per order.** It is raised if any priced linked order drifts; it is unknown only for unpriced orders.
- **Rebind (October 19, 2026, Phase 0 0.5b).** The drift record in the Execution tab lists each drifted linked stop or target with the price its order rests at. With a reason, the owner can move the level to that price: `POST /api/plays/{id}/execution/links/{linkId}/rebind` writes one plan revision (the level becomes a price) and, when the observed order is another order, replaces the link, in one unit of work under the account and Play locks. The call is checked against the Play's version and the retained order's acting price, and a stale one returns 409 and changes nothing. The browser offers it only for orders the monitor found away from their level, matched with the same tolerance, and names a percentage level that would become a price. The rebind refuses a level that another linked order still references, and the replacement of a link whose order has fills. The record itself is not touched: like every discrepancy it resolves when the next fresh check finds the order at its level. Entries and unplanned exits are not rebound, and an order's price is never treated as a fill.

**Per account and instrument** (each record lists every Open Play involved and closes none):

| Kind | Condition | Severity |
|---|---|---|
| `position.missing` | The expected position is non-zero and the venue is flat | critical |
| `position.opposite` | The venue position has the opposite sign | critical |
| `position.short` | The venue position is smaller than expected, same sign. Since October 23, 2026 it confirms on this condition, so a position that keeps changing still confirms; a switch to `position.excess` is another record with its own sequence. | warning |
| `position.excess` | The venue position is larger than expected: untracked exposure. Since October 23, 2026 it confirms on this condition, as `position.short` does. | warning |
| `position.unprotected` | On an account that keeps its history: an open position with no Open Play and no resting stop on the closing side (Phase 2, item 6c) | warning |
| `fill.unlinked` | A fill on the instrument after an Open Play's first entry, whose order is neither linked nor suggested | warning |

`fill.unlinked` details are bounded: a count, the total quantity, a capped list of fills and a fingerprint over the sorted fill IDs.

`play_discrepancies` holds:
- owner, account, Play (null for instrument-level kinds), contract and kind;
- a fingerprint of the quantities and orders involved;
- state, plus opened, confirmed, resolved and dismissed times;
- the note;
- details with exact decimal strings.

One open record exists per account, kind and subject. When an open record's details change (a partial fill, a quantity change), its details are updated in place and no event is emitted; its identity and `OpenedAt` are kept. Only a resolved or dismissed record can re-open; re-opening increments its revision, so it raises a new `discrepancy.opened`.

**Lifecycle (October 15–16).** `DiscrepancyLifecycle` is the one deterministic state machine, free of I/O, that applies an input (fresh present, fresh absent, incomplete, expiry tick, dismissal) to a record:
- **States** are `candidate`, `open`, `resolved` and `dismissed`. A condition opens only after an uninterrupted fresh confirmation (a second independent observation at least 60 s later). An open warning follows its evidence silently. A changed recurrence of a resolved or dismissed record is durable pending work (`PendingFingerprint`) whatever the public state, until a fresh observation confirms or clears it; an incomplete observation restarts the sequence but does not erase the work.
- **Confirmation on the condition (October 22, 2026, Phase 2, D-19; extended October 23).** Changed evidence restarts confirmation, except for `protection.short`, `protection.missing`, `protection.excess`, `position.short`, `position.excess` and item 6c's `position.unprotected`, where a changing size is normal while a trade is built. These confirm on their kind and subject with the condition still present (for example, the stop still covering less than the open quantity), and keep following the latest evidence. Interruptions, a fresh absence and the dismissed evidence returning still restart the sequence. A dismissed record still reopens only on changed evidence, after the same 60 s. Severity and announcements are unchanged. See the [lifecycle](messaging-and-workers.md#lifecycle).
- **Announcements.** `PublishedTransition` and `PublishedRevision` record the last announced transition (`opened`, `resolved`, `dismissed`, `unverifiable`) and its revision. Anything ever announced stays visible, and every announcement (`opened` or `unverifiable`) gets exactly one matching close (`resolved` or `dismissed`). Dedup keys are revision-based: `discrepancy:{id}:{revision}:{transition}`.
- **Unverifiable.** Work whose subject stays unobservable for `Vessel:Monitor:UnobservableLifetime` (1 hour to 90 days, default 7 days) after its last decisive observation gets the `unverifiable` outcome (`UnverifiableAtUtc`, DTO `outcome: "unverifiable"`) and one `discrepancy.unverifiable`, including for an unconfirmed candidate, which then becomes public. It is neither resolved nor still checked; it no longer holds eligibility or marks runs incomplete; the owner can dismiss it; a later fresh decision clears it. The option follows the same convention as the exit watch's `Vessel:Monitor:WatchLifetime`, but the two lifetimes are separate concepts.
- **Eligibility versus evaluation membership.** Outstanding verifiable work (a candidate, an open warning or pending re-confirmation, not unverifiable) keeps the account eligible for scheduled checks, in `EligibleAsync`, set-based discovery and the summary, including rows changed but not yet saved in the unit. Evaluation membership is wider: every tracked record's subject, unverifiable or publicly open ones included, is evaluated whenever the account is checked.
- **Housekeeping on every lease-owned outcome.** An evaluated check settles inside the evaluator. Every other lease-owned outcome settles discrepancy housekeeping exactly once through the coordinator's `SettleDiscrepanciesAsync`: a failed, discarded (timestamp-fenced or settings-changed) or interrupted check interrupts pending confirmations and ages work; a venue deferral (check or refused refresh), a check superseded before its read and an owner refresh only age work. A refresh never reconciles or confirms. Eligibility is recomputed afterwards, so a check without remaining work is not kept pending.
- **Overdue checks are starvation-free.** Scheduled and confirmation claims are never gated by the manual cooldown, and a refresh never moves the check due time, so repeated owner refreshes cannot postpone a due check.
- **Omitted executions.** When an adapter reports `OmittedExecutions` (in-scope fills knowingly left out), fill-derived position, protection-quantity and unlinked-fill checks are unknown, like a fill-window gap: they neither open nor resolve, and the evaluation is incomplete.

**Order mechanics.** `OrderMechanics` is the single venue-neutral classification (plain, stop, take-profit, unrecognized trigger) from the adapters' normalized order type and trigger facts, shared by the automatic matcher, manual link validation and the monitor. Only stop mechanics establish own or shared stop protection; a take-profit or plain limit linked as a stop never does, and an unrecognized trigger leaves coverage unknown. Manual linking refuses a take-profit order for a stop level, and the matcher no longer auto-matches a take-profit (for example a Lighter take-profit) to a stop level. `protection.orphaned` still considers every resting linked stop or target.

**Evaluation loads only what it needs:** Open Plays, terminal Plays still under exit or entry-orphan watch, and fills on the relevant contracts and orders since the earliest open entry. There are no growth-based caps that disable every check once an account has a long history.

### Stop coverage

October 19, 2026 (Phase 0 item 0.4c; review defects D-09, D-18 and the chart part of D-08). Structural unknowns used to be invisible: the checks stayed silent, the run stayed live and the Execution tab said "Monitored" while, for example, opposite Plays shared one whole-position stop. Each evaluated check now records every Open Play's stop coverage in `monitor_coverage` (migration `20261019132931_MonitorCoverage`), one row per Play on its account and instrument, written in the check's unit of work under the account lock and owner scope through `IMonitorStore.SaveCoverageAsync`. The check replaces the account's rows; Plays it no longer evaluates lose theirs, and reads return only currently Open Plays. A check whose facts are too incomplete to name the Plays leaves the rows to go stale.

| State | Meaning | Reasons |
|---|---|---|
| `known` | Decided: `covered` true or false | `stop-orders`, `whole-position-stop` (covered); `no-stop`, `short`, `cancelled` (not covered) |
| `shared` | A whole-position stop covers this Play together with same-direction siblings, and the venue position equals their total | `shared-whole-position-stop` |
| `unknown` | Missing evidence first, then a structural unknown | missing evidence: `history-gap`, `omitted-executions`, `quantity-unknown`, `orders-unavailable`, `stale-order-read`, `order-read-unreliable` (repeated orders, another native market or a status newer than the read), `incomplete-book`, `fills-after-read`, `mechanics`, `position-unknown`, `instrument-unknown`; structural: `opposing-plays`, `position-mismatch` |
| `unsupported` | The venue's order reader does not return the current book | `current-book-not-read` |
| `stale` | Projected when read, never stored: the check is older than twice `Vessel:Monitor:Interval`, the account's own staleness rule | the recorded reason stays |

The outcome follows the same evidence as the protection checks above, missing evidence first. Structural unknowns (opposing Plays, a shared stop the position cannot allocate) are `unknown`, never a missing stop, and never degrade the run.

Each row also keeps evidence per planned stop level, from the check's own order read, counting only what coverage counts as a stop (a linked order on the closing side with stop mechanics): `resting` (it rested in the current book at the saved level price, within the matcher's step, with the observation time), `resting-elsewhere` (the same at another price, which protects but does not confirm the planned price; `level.drift` covers the difference), `not-seen` (a complete, fresh book had none), `unknown` (the book could not tell, or the resting linked order is not a recognized closing stop) or `not-read`. Each carries the order's exact acting price and the saved level price it was compared with. Only a current-book row counts as resting; historical `Open` statuses never do.

**Venues without the current book.** The `CurrentOrderBook` venue capability ([venue registry](venue-registry.md#current-order-book-october-19-2026)) says whether the order reader returns the current open book. RISEx says it does not (owner decision, October 19: label it rather than build `/v1/orders/open` now). There, coverage is `unsupported`, the book-dependent per-Play kinds (`protection.missing`, `protection.cancelled`, `protection.short`, `protection.excess`, `protection.beyond-liquidation`, `protection.orphaned`, `entry.orphaned`, `level.drift`) do not run and are structural, and an incomplete order read alone no longer makes the run `degraded`. Position checks, `fill.unlinked`, watches and every other degradation reason still apply. The check uses the capability, never the venue name.

**Display.** The API returns the rows on `AccountMonitor.coverage` ([API contract](play-monitor-api-contract.md#stop-coverage)). The monitor heading follows open incidents first, then coverage, so it never says "Monitored" beside unknown, stale, shared or unsupported coverage, or a known uncovered Play. An Open, tracked Play without a recorded row reads "not checked yet", never "Monitored"; the account monitor's `uncheckedPlays` counts them, so account detail says so too. The chart labels each planned stop of an Open, tracked Play with the evidence's words, in UTC like the chart axis ("SL · resting as of Oct 14, 14:02 UTC", "resting at 95 as of …" for a stop elsewhere or a line the unsaved draft moved, "not seen at the venue", "unknown", "not read") and draws a stop without a confirmed resting order hollow and fainter, with theme tokens and in the same place. Coverage is a record, not a discrepancy: it never opens one, changes a status or corrects anything.

## Event contract

```json
{
  "id": "0192f0c4-…", "type": "execution.filled", "version": 1,
  "ownerId": "…", "sequence": "1234", "runId": "…",
  "dedupKey": "order:{accountId}:{orderId}:filled:0.5",
  "severity": "warning", "occurredAt": "2026-10-14T03:12:08Z", "detectedAt": "2026-10-14T03:20:41Z",
  "timeliness": "timely", "relevance": "current", "expiresAt": null,
  "subject": { "accountId": "…", "venueId": "hyperliquid", "contractId": "BTC", "playId": "…", "entryId": "…", "levelId": "…",
               "discrepancyId": null },
  "source": { "trigger": "monitor", "orderIds": ["…"], "fillIds": ["…"] },
  "data": { "role": "stop", "side": "sell", "filledQuantity": "0.5", "cumulativeQuantity": "0.5",
            "remainingQuantity": "0", "averagePrice": "83900", "complete": true }
}
```

| Field | Meaning |
|---|---|
| `id` | UUIDv7, stable; subscribers dedupe on it |
| `type`, `version` | The event kind and its payload version |
| `sequence` | Owner-scoped, commit-ordered cursor for catch-up, as a decimal string; independent of broker arrival order |
| `runId` | Groups the events of one monitor run or owner action, so a subscriber can send one message for them |
| `dedupKey` | Unique; a repeated insert is ignored |
| `occurredAt` / `detectedAt` | The source time when known, and when Vessel recorded the change. Owner actions use their action time; discrepancies use first qualifying observation time, not an invented venue cancellation time. |
| `timeliness` | `timely`, or `late` when `detectedAt − occurredAt` exceeds twice the interval: an outage or backlog, not the normal cadence delay |
| `relevance` | `current`, or `superseded` when a later change found in the same run overtakes it. Example: an entry fill and a stop fill both happened between two runs, so `play.closed` tells the whole story. |
| `expiresAt` | Set for time-sensitive types. A subscriber that processes the event later records `expired` instead of sending it. |
| `severity` | `info`, `warning` or `critical` |
| `subject` | Account, venue, contract, and when relevant the Play, entry, level and discrepancy |
| `source` | `monitor` or `owner`, with the order and fill IDs that caused it |
| `data` | Type-specific, exact decimal strings |

### Late events

New watched state changes become events; re-read rows and pre-watch history do not. Imported facts remain in their own tables even when they cause no event. Every event says whether it is timely or late, and whether it was still current when detected. Subscribers recheck current relevance at delivery, because a queued missing-stop warning may already be resolved. For example, Telegram can:
- send current events individually;
- fold superseded and late ones from the same run into one message ("While the monitor was offline: entry filled 03:12 at 84,541, stop filled 05:40 at 83,900; Play closed");
- drop expired ones.

Discrepancies are evaluated on the current state, so a problem that resolved itself during an outage never raises one.

### Catalogue, version 1

| Type | When | Severity | Dedup key |
|---|---|---|---|
| `play.opened` | Planned or Paused → Open | info; warning when filled while paused | `play:{playId}:opened` |
| `play.closed` | Open → Closed; data carries entered, exited, closed PnL, PnL basis and fees by token | info | `play:{playId}:closed` |
| `play.status-changed` | Other saved status changes, including planning, pause, resume and cancellation; data carries previous/current status and source | info | `status-change:{statusChangeId}` |
| `execution.filled` | New linked fill quantity on an entry, stop, target or exit order; `complete` says whether the order is fully filled | info; warning for stops | `order:{accountId}:{orderId}:filled:{cumulativeQuantity}` |
| `link.needs-confirmation` | An ambiguous order became a suggestion | warning | `link:{linkId}:suggested` |
| `execution.link-changed` | An owner or the matcher links, confirms, unlinks or dismisses a link; data carries previous/current state | info | Stable link-change identity allocated with the mutation |
| `discrepancy.opened` | A confirmed discrepancy opens or re-opens; data carries its kind and details. Updates to an open record emit nothing. | per kind | `discrepancy:{id}:{revision}:opened` |
| `discrepancy.resolved` | Cleared or dismissed; data carries the resolution (`resolved` or `dismissed`) | info | `discrepancy:{id}:{revision}:resolved` or `discrepancy:{id}:{revision}:dismissed` |
| `discrepancy.unverifiable` | Verification ended without a decision: the subject stayed unobservable beyond `UnobservableLifetime`. Data carries kind, state, `outcome`, last decisive observation and `unverifiableAtUtc`. | warning | `discrepancy:{id}:{revision}:unverifiable` |
| `monitor.state-changed` | An account moves between persisted monitor states | warning when leaving `live`, info when returning | `monitor:{accountId}:{stateRevision}:{state}` |
| `monitor.run-completed` | An owner-requested Check completes, even without a state change; data `{ trigger, outcome: succeeded \| failed \| superseded, state }`. A deferred Check is still pending and emits nothing; scheduled runs emit nothing. An owner Check stranded on an account that was disabled or lost its source is settled by discovery and completes as `superseded`. An owner Check left unclaimed for `Lease` + `HeartbeatTimeout` completes as `superseded` when a later Check or Refresh is admitted (October 16). | info | `monitor-run:{accountId}:{requestId}` |
| `credential.expiring` | A read-only token expires within 14 days | warning | `credential:{accountId}:{credentialRevision}:expiring` |

`monitor_accounts.StateRevision` is the persisted identity of monitor state. It is incremented under the account and monitor lock whenever the state changes (run outcomes, account disable, credential changes), and is separate from the lease `Generation`. `runId` keeps the request or action identity. No identity is generated while serving reads: the API-derived `offline` and `stale` are projections, not events, unless a writer persists the transition.

Versioning:
- Adding optional fields keeps the version.
- Removing or renaming a field, or changing its meaning, creates the next version. A breaking version needs a coordinated deployment or a subscriber upcaster; producers never inspect subscription registrations or supported versions.
- Each type has a golden JSON contract test, driven by the real producer code rather than hand-built samples.

Payload and ordering rules (October 15):
- **Explicit null.** One serialization policy for every event: an optional field that has no value is written as `null`, never omitted. Unknown stays `null`.
- **Causal order.** Within one transaction, events are sequenced in causal order: fills and link changes before the status change they cause, and a discrepancy after the facts it describes.
- **Account deletion.** Deleting an account removes its event baselines, fill identities, and the events and dedup rows whose subject is that account, together with any receipts for them. Dedup rows record their subject account for this purpose.

Fill accounting cannot rely on the 90-day event history. Keep a durable source-identity baseline for each owner's account, contract and fill, and canonicalize cumulative quantities used in dedup keys. A repeated observation, retry, relink or event-retention sweep must not recreate an execution notification. Event ordering and fill baselines need concurrency and retention tests before Step 2 is complete.

## Topics, subscriptions and delivery

Settled by the owner on October 15. The model is Azure Service Bus topics and subscriptions, built on RabbitMQ through Wolverine. Delivery is at least once within each subscription's retained responsibility window, with explicit terminal failures and gaps.

### Topic

- **One topic:** the durable topic exchange `vessel.events`. The routing key is the event type, for example `play.closed`.
- **Publishers** (the API and worker modules) append the event and its outbox envelope in the unit of work and publish it once after commit, through Wolverine's durable outbox. They know nothing about subscriptions, write no per-subscriber rows and never inspect which versions subscribers support.

### Subscriptions

- **One durable quorum queue per subscriber:** `vessel.events.sub.<name>`. Its bindings are its filter rules, for example `play.#` or `discrepancy.*`.
- **Immutable filter.** A subscription name's filter never changes. Its registration stores a filter fingerprint, and provisioning rejects a changed filter; a different filter needs a new name.
- **Dead-lettering:** provisioning declares the queue's dead-letter exchange, routing and bound queue `vessel.events.sub.<name>.dlq`, and a quorum delivery limit (`x-delivery-limit`, default 20). The delivery limit is only a transport guard; receipt attempts are the business retry limit.
- **Ownership:** the subscribing module declares the subscription. Only the explicit provision step creates the topology and registers the subscription in PostgreSQL. Nothing is declared at runtime.
- **Unordered in this stack.** For a later ordered subscription: it serializes advancement in PostgreSQL and drains in sequence order, with broker delivery only waking it. Single-active-consumer is an optimization there, not the source of correctness.
- **Versions.** A subscriber that receives an event version it does not support records a visible subscription failure. Compatibility comes from upgrading the subscriber, an upcaster or a coordinated release, never from producers checking.

### Subscriber state in PostgreSQL

- **Registration** (`event_subscriptions`): name, owner, owning module, filter fingerprint, the sequence it became responsible from, a low-water mark, lag status and any explicit gap. A new subscription starts at the owner's current sequence unless an explicit replay is requested. Provisioning is insert-only for an existing registration: it never resets its start or progress.
- **Receipts** (`event_receipts`): per owner, subscription and event, `pending`, `delivered`, `skipped`, `expired` or `failed`, with attempts, next attempt time and a safe error.

### One dispatcher

Live delivery and catch-up use the same dispatcher. In one transaction it:
1. validates the owner, then looks the event up by (owner, event ID). It never trusts the sequence or payload in the message: a retained event's database sequence and payload are used, including the floor check. An absent event with an owner-scoped `event_dedup` row is known history that was pruned; with neither it is unknown;
2. acquires and locks the receipt for (owner, subscription, event), creating it if absent;
3. checks the subscription's responsibility, terminal state, expiry, retry deadline and remaining attempts;
4. runs the handler's database-only effects;
5. settles the receipt.

Invariant: an event and its dedup identity are created atomically. Ordinary event deletion is only retention pruning, which keeps the dedup identity; account deletion removes both.

Subscription handlers in this stack are database-only. The Notification Hub later enqueues provider work transactionally; nothing sends externally while holding the receipt lock.

The handler runs in a savepoint of the dispatcher's unit (`IsolateAsync`), so a failing effect rolls back while the receipt stays locked and the attempt is recorded in the same transaction. The attempt cap bounds *recorded* attempts, not every rolled-back handler invocation: when the whole transaction fails at commit after the handler ran, the attempt is recorded in a new transaction, and if that also fails or the process dies the invocation is not counted (accepted in review). Handlers must therefore be database-only and idempotent.

### Settlement

Event-subscription and command listeners use Wolverine's inline listener mode, not the durable inbox: a 6.50 durable-inbox listener persists and settles the broker delivery before the handler runs, and on RabbitMQ 4.3 a nack does not count towards `x-delivery-limit`. The durable outbox stays. A pinned-version integration test proves this behavior.

Wolverine models a RabbitMQ queue as one endpoint with one mode for both directions, so a host never publishes to a command queue it listens to: the API publishes `MonitorAccountCommand` through the durable outbox, and a worker running the monitor module only listens inline and refuses to stage that command. Registering both sides on one host made Wolverine warn about conflicting modes and keep whichever it applied last. A broker test asserts each host's mode and that startup logs no conflict.

| Outcome | Database | Broker delivery |
|---|---|---|
| Success | Effects and terminal receipt commit together | Ack after commit |
| Retryable failure | Effects roll back; the receipt durably records the attempt, `pending` and `next_attempt_at` | Ack: catch-up owns the retry |
| Terminal failure (attempt cap, unsupported version) | Receipt commits `failed` | Dead-letter, for diagnosis |
| Invalid owner | No effects, no receipt | Dead-letter, logged by IDs |
| Unknown event: no retained event and no dedup identity | No effects, no receipt | Dead-letter for diagnosis, whatever sequence the message claims |
| Known historical: pruned, or retained below the floor | No effects; not recorded as handled | Ack as non-deliverable |
| Already settled, or not responsible | No effects | Ack |

Commands have no event receipts. For `RunAccountMonitor`, the pending request in `monitor_accounts` and its request ID are the durable hand-off and dedup: the listener acks once the command is validated against that work. Venue polling belongs to the scheduler and run executor, not the listener.

### Dead-letter queues

Dead-letter queues are diagnostic transport artifacts, not authoritative work. Receipt state controls retries. Broker redrive from a dead-letter queue never reopens a terminal receipt. An explicit, bounded admin command returns retained `failed` receipts to `pending` with a bounded additional retry budget. Historical attempts are preserved, and the low-water mark is never rewound.

### Catch-up

Recovery is subscriber-side; there is no producer-side republish loop.
- Discovery and retry work are separate. Each bounded pass per subscription (every 30 s, batch 100) does both through the same dispatcher:
  - **discovery:** committed events after its low-water mark that match its filter, were detected more than 30 s ago and have no receipt yet;
  - **retry work:** due `pending` receipts at any sequence, including admin retries behind the low-water mark, always joined to their retained events.
- Retries share one attempt cap (12) across live delivery and catch-up, backing off from 1 minute, doubling to 1 hour, with ±20% jitter. An admin retry adds its own bounded budget on top. An event past its `expiresAt` settles `expired`.
- The sweep covers a lost broker, an unroutable publish, a dead-lettered or acked-for-retry message and a subscriber that was down.

### Watermarks and retention

- **Low-water mark.** Advancement captures a committed high-water sequence, then moves the mark monotonically (under the subscription lock or compare-and-set) across a bounded prefix in which every sequence is settled: an event outside the filter, a terminal receipt, or a legitimate hole such as an account deletion. Sequences are commit-ordered, so a hole below the high-water mark never fills later.
- **Retention decisions** are serialized per owner. Retention deletes only a contiguous prefix of events, all older than `RetentionDays` and none above the lowest low-water mark of the active subscriptions, and none with a `pending` receipt (pending delivery work is not silently deleted).
- **Floor publication** is a short transaction: it revalidates registrations and watermarks, marks a subscription whose mark falls behind the retention window `lagging` and records its explicit gap, and raises the floor atomically. It never holds the owner cursor lock while scanning. Physical deletion below the floor follows in repeatable set-based batches, receipts included. Every delivery path respects the logical floor.
- A lagging subscription stops holding retention; its gap is visible in health. Deregistering a subscription releases it.
- Dedup and baseline identities survive pruning.

### Commands

- Commands are point-to-point queues owned by the receiving module: `RunAccountMonitor` goes to `vessel.commands.monitor`, with its own dead-letter queue and transport delivery limit.
- PostgreSQL stays the record: pending manual requests live in `monitor_accounts`, and the request ID makes a duplicate command harmless.

### Browser live updates

- The API's event stream is a non-durable database-cursor reader. It streams committed `events` rows in cursor order and polls briefly between them; it is not a subscription and keeps no receipts.
- The cursor carries the stream epoch with the sequence. A cursor from another epoch, ahead of the database or below the retention floor gets a reset requiring a state reload.
- **Epoch rotation.** The explicit `rotate-event-epoch` command is a mandatory step of the restore procedure: stop every host → restore → `rotate-event-epoch` → provision → start. `migrate` does not rotate it. A restore is detected only when this procedure rotates the epoch before any host starts.
- Popups, their acknowledgement and channel-specific settings belong to the Notification Hub.

### In this stack

No durable production subscription is required yet. The no-op `monitor` event consumer is removed. Tests and the release check register a verification subscription, prove delivery, broker loss and catch-up, then deregister it. The release check provisions it with `Vessel:Messaging:Verification` on `migrate` and runs it in a separate `verifier` worker (deployment `verification`), so the default monitor worker keeps its production configuration; during a broker outage it checks that the API outbox holds the publication. The Notification Hub will be the first real subscription.

### Notification Hub (future)

A separate plan: [Notification Hub and in-app popups](notification-hub-plan.md) (draft, October 17). Only its boundary is fixed here:
- **One subscription,** `notifications`. It owns per-owner rules (channels, minimum severity, event types), grouping by `runId`, the relevance and expiry rechecks at delivery, and a delivery record per notification and channel.
- **Atomic hand-off.** In the transaction that settles its subscription receipt, the hub creates the durable pending work for each channel and its outbox message.
- **Providers** consume `vessel.notify.<provider>` queues (`telegram`, `discord`, `in-app`), which are transport only: a provider recovers unsettled work from the hub's records, using stable delivery identities. Each implements an Application port such as `INotificationChannel`, with its own rate limits, retries and dead-letter queue.
- **Adding a provider** never changes publishers or the event contract.
- It runs as the `notifications` worker module.

## Health and visibility

| State | Meaning |
|---|---|
| `live` | The last run succeeded within twice the interval |
| `stale` | No successful run for longer than twice the interval |
| `degraded` | Missing or stale evidence: a partial or failed read, or a history gap. Structurally unknown coverage (for example shared whole-position stops) is shown as unknown stop coverage, not as a degraded monitor. An incomplete order read on a venue without the current-book capability is not missing evidence either (October 19, [stop coverage](#stop-coverage)). |
| `credential-error` | The token is missing, refused or expired |
| `idle` | Nothing to watch |
| `offline` | The worker heartbeat is missing (system-wide) |

`offline` is derived by the API when the worker cannot report its own failure. Staleness also advances from stored timestamps without waiting for a healthy worker to publish an event.

- **Productive heartbeat.** The heartbeat row and file advance only after a scheduling pass completes (discovery and claims), not merely because the process is alive. A worker that cannot schedule shows as `offline`. The file holds the time and the configured `HeartbeatTimeout`, which the container healthcheck reads; when the file cannot be written, the row is not recorded either.
- **Safe logs.** Failure logs include the exception type name and IDs, never payloads, messages from venue responses or secrets.
- **Exit and entry-orphan watches** that reach their lifetime without proof show a persistent `unverifiable` on the Play.
- **Subscriptions** that are `lagging`, have a gap or hold `failed` receipts are reported with worker health and logged by name.

The UI shows this inside existing areas; the approved layout does not change:
- **Execution tab:** "Monitored · checked 4 min ago · next in about 6 min" with Check. Since October 19 the heading names open incidents first, then any stop coverage that is not plainly covered ("Stop coverage unknown", "Current book not read"), and says "Monitored" only otherwise. The button shows "Checking…" while the run is pending or running, then a cooldown countdown before it can be used again. Stale: "Monitor stale since 14:02: fills may be missing". Credential error: a link to manage the token. Discrepancies are listed with Dismiss.
- **Account rows and detail:** a badge only when the state is not `live` or `idle`. Instrument-level discrepancies also appear on the account detail.
- **Shell:** a banner when the worker is offline while Plays are tracked.

## API and frontend

| Route | Purpose |
|---|---|
| `GET /api/events?after={cursor}&limit=` | The owner's events, bounded, oldest first; the cursor carries the stream epoch and sequence |
| `GET /api/events/stream?after={cursor}` | SSE: `event`, `status`, keepalive. Read with `fetch` and the bearer header through `api/sse.ts`; at most 4 streams per owner |
| `POST /api/accounts/{id}/monitor/run` | 202 with `{ requestId, monitor }` (`MonitorRunAccepted`: the pending request and the account monitor); 429 with `Retry-After` while a manual run is pending, running or cooling down; 409 when the account cannot be monitored |
| `GET /api/accounts/{id}/monitor` | State, last success, next run, safe error, manual run state and when Check is available again |
| `GET /api/plays/{id}/execution` | Adds `monitor`, `discrepancies` and `watches` (linked exit and entry orders of an ended Play, with role and state) |
| `POST /api/plays/{id}/discrepancies/{discrepancyId}/dismiss` | With an optional note |

- **Check endpoint:** `POST /api/plays/{id}/execution/check` no longer reads venues: it records the same manual request as `monitor/run` for the Play's account (202 `MonitorRunAccepted`, 429 or 409). It stays as an API alias; the browser uses `monitor/run`. The browser-driven `CheckAsync`, refresh-time order retention and the lock-holding `SyncAsync` are removed. Behaviour changes from that removal: opening a Play or the browser timer never reads a venue or links orders; reconciliation and status changes happen only in worker checks; an account Refresh retains facts but performs no linking or lifecycle change; a Check answers accepted, not done.
- **Browser:** the 60 s venue polling in `usePlayExecution` goes away. The shell keeps one event stream. On an event for the open Play's account, the browser re-reads the stored execution, which touches the database only. If the stream is down, it polls that read every 60 s.
- **Manual refresh** of an account stays available on the Accounts page. Its server path shares admission, source-revision validation and import locking with monitor runs. Settled for #45: it stays synchronous in the API on its own refresh lease and read budget; a refused or abandoned refresh is released, never replayed by the worker.
- **Browser stream (#47).** Before its first stored read the shell reads the event head and streams from it. The API has no dedicated head read: `frontend/src/api/event-cursor.ts`, the only code that knows cursor syntax, sends a bare legacy sequence beyond any retained one, which always answers with a reset naming the head. Status frames and pages carry `nextCursor`; event frames carry their resume cursor in the SSE `id:` line.

## Review corrections, October 15

Review of the stack found these defects; the owner accepted the corrections. Each fixed finding needs a regression test that failed before the fix. The rules themselves are written into the sections above.

**#43 unit of work** ([Unit of work](#unit-of-work-and-transaction-boundary)):
- a nested `ExecuteAsync` saves before returning;
- commit uses no cancellation token;
- a rollback failure preserves the original exception;
- a participant's exception cannot be caught and continued past (documented);
- evidence and sizing writes go through the unit of work.

**#44 events and outbox** ([Topics, subscriptions and delivery](#topics-subscriptions-and-delivery), [Event contract](#event-contract)):
- replace configured durable consumers, publisher-written `event_deliveries` and the republish sweep with topics, subscriptions, receipts and subscriber-side catch-up;
- move Wolverine and RabbitMQ code into `Vessel.Messaging`; rename `PlayEvent` types and tables to neutral names;
- explicit `null`, causal order within a transaction, account-deletion cleanup;
- the watermark and retention protocol, without holding the cursor lock while scanning;
- a stream epoch on the browser cursor, rotated by `rotate-event-epoch` in the restore procedure;
- inline listeners and the settlement table, one dispatcher, immutable filters, insert-only provisioning;
- owner-mismatched messages dead-lettered and logged, not silently acknowledged;
- catalogue dedup keys aligned with the code (`discrepancy:{id}:{revision}:…`, `credential:{accountId}:{credentialRevision}:expiring`); monitor state keys are covered under #45;
- the no-op `monitor` event consumer removed; tests use a verification subscription.

**#45 worker** ([Scheduling](#scheduling-and-the-unit-of-work), [Health and visibility](#health-and-visibility)):
- an exit watch resolves on a reported terminal order regardless of book completeness; absence still needs a complete read; a 7-day lifetime ending in a persistent `unverifiable` outcome;
- venue `Retry-After` clamped to 1 hour;
- heartbeat and health reflect a productive scheduling pass; logs include exception type names only;
- refresh outcomes leave check backoff, `stale` and the due time alone; a refresh refused with 429 is never replayed by the worker; superseded runs are not failures; the manual cooldown does not gate scheduled claims and is at most the interval;
- modules selected by `Vessel:Worker:Modules`;
- a persisted `StateRevision` on `monitor_accounts`, incremented on every state change under the account and monitor lock, keys `monitor.state-changed` (`monitor:{accountId}:{stateRevision}:{state}`) instead of the lease generation; reads never create identities;
- upgrades and backups stop web, api and worker.

**#46 discrepancies** ([Discrepancies](#discrepancies)):
- stop and target coverage are separate; a shared whole-position stop (same account and native contract, a real stop, compatible closing side) covers sibling Plays when the venue position equals the expected total, otherwise unknown, never critical missing;
- netted opposite Plays on a one-way account have unknown per-Play protection unless allocation is demonstrable;
- a single Play with a matching venue position gets decidable whole-position stop coverage; structural unknowns do not degrade the monitor;
- reduce-only orders do not count towards `protection.excess` for a lone Play;
- new `entry.orphaned` (critical) for a resting linked entry on a Closed or Cancelled Play;
- `protection.cancelled` supersedes `protection.missing` and `protection.short` for the same orders;
- open records update their details in place without an event; a re-opened record gets a new revision and event;
- bounded `fill.unlinked` details;
- `level.drift` decided per order;
- evaluation loads only relevant Plays, orders and fills, with no growth-based caps.

**#46 follow-up (October 16):** the lifecycle state machine, revisioned announcements, `discrepancy.unverifiable`, `UnobservableLifetime`, eligibility versus evaluation membership, settlement on every lease-owned outcome, refresh expiry housekeeping, starvation-free overdue checks, `OrderMechanics`, and omitted executions as unknown evidence; see [Discrepancies](#discrepancies).

**#47 monitor UI:** adds `monitor.run-completed` for owner Checks, follows the opaque `{epoch}.{sequence}` cursor, resets and SSE resume ids, reads explicit nulls, labels runs by `runTrigger`, and shows unknown coverage, `unverifiable` discrepancies and watches (with their role) and `entry.orphaned` within the existing layout.

## Delivery steps

### W0: Wolverine spike, complete

**Verdict: conditional go for Wolverine 6.50.0.** Not a drop-in substitution for inner `SaveChangesAsync` calls. The [research report](wolverine-fit-research.md) records primary sources, exact versions, the evidence matrix and reproduction instructions. Disposable code and recorded outputs live in `spikes/wolverine/`.

Proved in the isolated spike:
- real sealed `VesselDbContext`, real nested store locks and atomic rollback/commit when persistence, outer commit and flush stay separate;
- recovery after commit/process exit and after broker outage followed by broker/producer restart;
- business EventId dedupe with a PostgreSQL effect, plus separate same-envelope inbox dedupe;
- explicit schema setup, missing-storage rejection with automatic schema creation disabled, and separate API/worker recovery schemas;
- durable RabbitMQ topic/quorum topology and publisher confirms, including the unsafe unbound-topic case;
- scoped owner validation and isolation under concurrent message handling;
- dynamic handler execution and explicit setup in the runtime-only .NET 10 container.

Not proved by W0: continuous reconnect without producer restart, full schema-drift rejection, broker-volume-loss/database-restore recovery, the eventual product event mapping/retention/scheduler, bidirectional product commands/events, pregenerated code, local-only transport tests, or external notification delivery. These are not implied by a successful spike. Carry the applicable cases into integration/release tests.

The earlier Claude attempt failed at a provider limit. The replacement Sol investigation completed, and the parent took over evidence review after the owner requested stopping the agent. No product files, migrations, deployment or owner database were changed. The recommendation does not authorize product implementation.

### Step 1: Bounded unit-of-work refactor

- Application-owned `IUnitOfWork` and one scoped implementation coordinating the existing DbContext.
- Move transaction completion and saves out of the relevant stores into the unit of work, preserving necessary intermediate saves.
- Start with Play/execution writes and their shared account-import path. Adapt callers that share those store methods, including management flows, without broadening into unrelated read-only reports or a repository rewrite.
- Preserve existing commit boundaries, ownership checks, account/management/advisory lock ordering, settings revisions and conflict responses.
- Prove rollback after intermediate saves, one outer commit, nested-service participation, replacement-row correctness and reconciliation read-after-save behavior.
- No event tables, Wolverine packages, broker, worker, scheduler or frontend behavior changes in this step. No schema migration should be needed.
- Gate: existing behavior and transaction regression tests pass before adding messaging.

### Step 2: Event log, topic and subscriptions

- `events`, `event_cursors` (with the stream epoch), `event_dedup`, `event_baselines`, `event_fills`, `IVesselEventSink` and `IVesselEventReader`.
- Commit-ordered owner cursors, causal order within a transaction, pre-watch fill baselines and retention-safe event identity, including removal on account deletion.
- Events from the existing transitions and links in `PlayExecutionService`, serialized with explicit `null`.
- `Vessel.Messaging`: Wolverine persistence and post-commit dispatch at the tested unit-of-work boundary, with regression tests for premature commit and pre-commit dispatch. Persistence keeps no Wolverine reference.
- The module contract and registry, the `vessel.events` topic and the subscription machinery: inline listeners, the shared dispatcher and settlement, registrations with filter fingerprints, receipts, low-water marks, catch-up, dead-letter queues, the bounded admin retry and subscription-aware retention. No producer-side republish or per-subscriber rows written by publishers.
- Explicit, insert-only provisioning of envelope storage, topology (including dead-letter exchanges) and subscription registrations, and the `rotate-event-epoch` command.
- RabbitMQ in Compose and the relay.
- The `play.opened`, `play.closed`, `execution.filled` and `link.needs-confirmation` contracts, with golden tests from the real producers.
- Migration, contract and verification-subscription tests.

### Step 3: Worker and scheduler

- `Vessel.Worker` selecting modules from `Vessel:Worker:Modules` (default `["monitor"]`).
- The monitor module: `monitor_accounts`, the scheduler, the `vessel.commands.monitor` queue and `RunAccountMonitor` with owner scope; owner-mismatched commands dead-lettered.
- Venue reads outside the lock; clamped venue `Retry-After`; refresh outcomes kept separate from check scheduling.
- Exit and entry-orphan watches that resolve on a reported terminal order and end as `unverifiable` after 7 days.
- Health states, `monitor.state-changed` and `credential.expiring`.
- The productive heartbeat, and replica-safe catch-up and retention sweeps.
- `monitor/run`, and retiring `execution/check` and the browser's venue polling.
- Compose worker service, image entrypoint, credentials overlay, release verification and README, with web, api and worker stopped for upgrades and backups.

### Step 4: Discrepancies

- Normalized read-coverage fields for positions, open orders and fill history, supplied by each adapter; unknown coverage stays unknown.
- The rules above, including the October 15 coverage, precedence and in-place update corrections, and the confirmation run.
- `play_discrepancies`, `entry.orphaned` and the discrepancy events.
- Execution tab list with Dismiss; account detail.

### Step 5: In-app live updates

- The events read and stream routes, with epoch-carrying cursors.
- The shell subscription and the database-only fallback.
- Monitor status texts and the offline banner.

### Later (separate plans)

- The Notification Hub: the `notifications` subscription and Telegram, Discord and in-app providers, with popups, acknowledgement and per-owner settings.
- Price and drawing alerts.
- Venue user streams as a capability.
- Incremental fill cursors.
- A normalized liquidation fact (RISEx `is_liquidation`, Hyperliquid liquidation events) for a `position.liquidated` event.

## Price and drawing alerts (deferred)

The owner wants alerts when price reaches a Play's drawings (lines, levels, ranges). They need prices close to real time, so they take a different path from the periodic monitor:

- **Price watcher:** an in-memory component in the worker that uses the venues' market streams. The shared stream relay would move so the worker can use it. RISEx would build prices from its public `trades`/`oracle` channels, and Lighter from `market_stats`. A venue without a stream would fall back to short REST polling of the mark price.
- **Definitions:** alerts are armed from a Play's drawings and plan levels and evaluated on the mark price with hysteresis.
- **Events:** only the crossing becomes an event (`price.crossed`, `level.approached`), with a short `expiresAt` and an explicit observation flag. Ticks never enter the outbox. A crossing is never a fill and never changes a status.
- **Restarts:** after a restart, alerts re-arm. Whether a crossing missed while offline is reported once as late is to be decided with that plan.

## Tests

- **Application:**
  - one event per transition, with stable dedup keys;
  - timeliness and relevance;
  - every discrepancy kind, including the confirmation run, several Plays sharing an instrument, opposite Plays netting out and lot rounding;
  - shared whole-position stops (including a target that must not count as a stop, a mismatched contract or side, and netted opposite Plays), the single-Play coverage case, reduce-only orders, `entry.orphaned`, cancelled-over-missing precedence and silent detail updates;
  - owner scope in handlers.
- **PostgreSQL** (disposable database, as today):
  - the unit of work commits once, rolls back intermediate saves and cannot be completed by nested stores;
  - replacement rows, reconciliation reads, management locking and concurrency conflicts retain existing behavior;
  - the event and the Play change commit or roll back together;
  - a repeated dedup key is ignored, including after event history has been pruned;
  - a delayed commit cannot be skipped by the cursor reader or a subscription's low-water mark; an expired cursor, a cursor ahead of the database or one from another epoch requests a reset;
  - events within a transaction follow causal order, and optional fields serialize as `null`;
  - a subscription registered later is not responsible for earlier events; re-provisioning never resets its start or progress, and a changed filter is rejected;
  - live delivery and catch-up race on one receipt without double effects; catch-up processes only matching, unsettled events past the grace period, backs off, ends `failed` at the shared cap and `expired` past `expiresAt`; an admin retry after the low-water mark has advanced is processed as retry work without rewinding the mark, preserves attempt history and survives a crash before dispatch;
  - a message with an unknown event ID dead-letters even when it claims a below-floor sequence; a known pruned ID acks without trusting its claimed sequence; a retained event is dispatched with its database sequence;
  - repeated monitor state cycles without a lease claim produce distinct `monitor.state-changed` events; a retry or re-read of an unchanged state produces none;
  - the low-water mark advances monotonically over non-matching events, terminal receipts and account-deletion holes, never past an unsettled event;
  - retention deletes only a contiguous prefix below the lowest active low-water mark, records gaps for lagging subscriptions in the floor transaction, repeats physical deletion safely, never serves events below the floor and does not hold the cursor lock while scanning;
  - account deletion removes that account's baselines, fill identities, events, dedup rows and receipts;
  - two schedulers do not own a current lease together; a stale lease holder cannot commit;
  - scheduled, manual and confirmation requests coalesce, including redelivered commands;
  - account disable/source/credential changes during network reads reject stale results;
  - migrations have no pending model changes, and Wolverine storage is applied only by `migrate`.
- **Worker:** fake venue adapters take a Play from Planned to Open to Closed with no browser. Also covered: a refused credential, a history gap, incomplete reads that neither confirm nor clear a discrepancy, closed-Play orphan exits and entries, exit watches resolved by a terminal order on an incomplete read and ending `unverifiable`, clamped `Retry-After`, refresh outcomes that leave check scheduling alone, superseded runs, the productive heartbeat, grace deadlines, and late or superseded events after simulated downtime. Two worker replicas run catch-up and retention without double processing.
- **Broker** (opt-in, disposable RabbitMQ container, pinned Wolverine and RabbitMQ versions): inline listeners settle each row of the settlement table as specified, including ack after a retryable failure and dead-lettering of a terminal one into the provisioned dead-letter queue; an owner link in the API reaches a verification subscription; a broker restart; duplicate delivery is ignored; catch-up recovers a lost queue and an unroutable publish from PostgreSQL; redriving a dead-lettered message does not reopen its receipt; an owner-mismatched message is dead-lettered; a lost command is recovered from `monitor_accounts`.
- **Frontend:** event handling, fallback polling, monitor status texts and Dismiss.
- **Release:** `scripts/verify-release.py` covers the worker entrypoint and healthcheck, RabbitMQ not being published, explicit provisioning with a verification subscription, and backup and restore without the broker volume through the documented procedure. After the restored database has advanced beyond the browser's former sequence, an old-epoch browser cursor reconnects and gets a reset.

## Authorization and delivery tracking

On October 14, the owner authorized implementation by Sol agents, a separate Sol verification, a final isolated end-to-end verification and stacked pull requests. The five delivery steps above define the stack, each based on the preceding layer. The parent integrates changes, verifies the final application and opens the PRs. Notifications and drawing alerts remain excluded.

Track the verified state, test evidence and PR links in `play-monitor-state.md`. W0 integration constraints remain mandatory; authorization is not evidence that an untested gate has passed.

## Risks

- **Wolverine is a large framework.** It brings runtime code generation, its own conventions and its own tables. Mitigation: the W0 spike, keeping it behind Application ports, and explicit storage setup.
- **Two stateful services.** Treating RabbitMQ as replaceable transport depends on tested subscriber catch-up from retained PostgreSQL events and pending commands. External delivery duplicates remain possible.
- **A slow subscription can miss history.** One that lags beyond the retention window stops holding retention and gets an explicit, visible gap rather than unbounded event growth.
- **Reported late by design.** A filled stop is reported up to one interval later; the owner accepted this.
- **Venue history limits.** Very active accounts can lose fills after a long outage (Hyperliquid's latest 2,000). This is shown as `degraded`, never as "no fill".
- **Snapshot and fills read moments apart.** Coverage checks and confirmation reduce false positives; they do not turn independent venue responses into an atomic snapshot.

## Primary references

1. [Wolverine durable messaging](https://wolverinefx.net/guide/durability/): the outbox's guarantee, duplicate rejection by message ID in the inbox, and no two-phase commit with brokers.
2. [Wolverine message storage management](https://wolverinefx.net/guide/durability/managing): `AutoCreate.None`, `resources setup`, `db-apply` and `db-dump`.
3. [Wolverine EF Core outbox](https://wolverinefx.net/guide/durability/efcore/outbox-and-inbox.html) and [EF Core migrations](https://wolverinefx.net/guide/durability/efcore/migrations).
4. [Wolverine RabbitMQ transport](https://wolverinefx.net/guide/messaging/transports/rabbitmq/): `WolverineFX.RabbitMQ`, provisioning and publisher confirms.
5. [Hyperliquid rate limits](https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/rate-limits-and-user-limits) and [WebSocket subscriptions](https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/websocket/subscriptions).
