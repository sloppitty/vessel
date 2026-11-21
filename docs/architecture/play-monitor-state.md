# Play monitor implementation state

October 15, 2026, updated October 16. Implementation authorized with Sol implementation agents, independent Sol verification, a final isolated end-to-end check and stacked pull requests. No notifications or drawing alerts are included.

## Stack

| Layer | Scope | State | Pull request |
|---|---|---|---|
| 1 | Unit of work, transaction regressions, approved plan and W0 evidence | Independently verified | [#43](https://github.com/jcarcaboso/vessel/pull/43) |
| 2 | Event history, durable identity, Wolverine outbox and explicit provisioning | Independently verified, including evidence-upload fix | [#44](https://github.com/jcarcaboso/vessel/pull/44) |
| 3 | Worker, scheduling, read coverage, account admission and deployment | Upstream integration independently verified | [#45](https://github.com/jcarcaboso/vessel/pull/45) |
| 4 | Discrepancies, confirmation, dismissal and closed-Play follow-up | Upstream integration independently verified | [#46](https://github.com/jcarcaboso/vessel/pull/46) |
| 5 | Browser state updates, Check cooldown, monitor visibility and final E2E | Independently reviewed; full-stack and packaged checks passed | [#47](https://github.com/jcarcaboso/vessel/pull/47) |

The stack is based on `main` at `f80e641`, including the P1 diary changes, backend/frontend cleanup, Robinhood support, v0.1.1 packaging defaults and Overview allocation. Each PR depends on the preceding layer. No PR has been merged, no image has been published and no owner installation has been deployed or migrated.

## Safety and verification

- Use newly created disposable PostgreSQL/RabbitMQ resources and synthetic accounts only.
- Never read repository `.env`, connect tests to the owner database or apply migrations to it.
- Preserve owner filters, account and Play locking, exact execution facts, retrospective/manual workflows and theme tokens.
- Record executed commands, results, independent review findings and PR links here as each layer completes. Pending tests are not passing tests.

## Release chain on v0.1.3 main, October 16

The approved chain was rebased onto `main` at `782ecdb` (v0.1.3: paged Activity and Plays lists, the `PlayDirection` migration and the tag-driven release workflow) as `rel/43` → `rel/47`. Both sides are kept: main's server-paged lists also reread on accepted stored-state reloads and stay behind the event-head gate, and the monitor UI, cursor module and contract guards are unchanged. The stack's migrations were re-timestamped after `20261016104756_PlayDirection` with unchanged names, order and operations (`20261016110000_VesselEvents`, `20261016110100_AccountMonitor`, `20261016110200_MessagingDeployments`, `20261016110300_PlayDiscrepancies`, `20261016110400_PlayDiscrepancyUnverifiable`); their designers carry `PlayDirection`'s model and the last one matches the snapshot. An upgrade test applied main's migrations to a disposable database, seeded it through main's API, then ran the stack's EF bundle and `provision-messaging`: all five migrations applied, accounts, portfolios, the Play (with its `Direction`) and its status history were unchanged, and the new API served them. Release verification passed 166 checks in both S3 and local storage modes, and the full-stack end-to-end harness passed 171 checks.

## Final chain on main, October 16

The reviewed chain was rebased onto `main` at `f52f129` (the visual refresh, #49) as `final/43` → `final/47`. Every layer combines with the refresh rather than replacing it: AGENTS keeps main's paragraphs plus the monitor ones; account rows keep the refresh's grouping and add the monitor badge; account detail keeps the refresh's summary card (monitor badge inside it), then Source, then the monitor and discrepancy sections before the balance tiles; the Play journal's Execution tab keeps the refresh's quieter header and shows the monitor panel (with Check, the 202 run) instead of the old Check venue button; the shell keeps the page-settle wrapper and gates Review on the event head; Settings keeps the refresh's shorter copy; monitor CSS uses the refresh's panel, divider and type tokens. Two final #45 review fixes were added (stranded work on disabled or source-less accounts is settled by discovery; only a current-book Open row keeps a watched order resting), and a settled owner Check now completes with `monitor.run-completed` (`superseded`).

| Check (at `final/47`) | Result |
|---|---|
| Backend suite, disposable PostgreSQL and RabbitMQ | 2,260 passed, zero failed, five optional live/S3 skips (`final/45` 2,060; `final/46` 2,256) |
| Frontend frozen install, typecheck, lint, tests, build | Passed; 1,982 tests across 108 files (`final/45`: 1,911 across 101) |
| Operator Python, prototype, `docker compose config` | 29 and 76 passed; base and six overlays valid |
| Packaged verification (S3 mode, synthetic data) | 166 checks passed, zero failed: image 5, HTTP 44, TLS 47, worker 10, monitor 26, restore 12, security 8, browser 14 |

Synthetic captures of Overview, account detail with monitor and discrepancies, and the Play editor's monitor panel were taken in Graphite and Paper at desktop and 390 px widths for review.

## Restacked chain, October 16

After the October 15 redesign and review fixes, the five layers were restacked into one linear chain on `main` at `7335a42`: `stack/43` (unit of work) → `stack/44` (events, topics and subscriptions) → `stack/45` (worker) → `stack/46` (discrepancies) → `stack/47` (monitor UI). Semantic adaptations made while restacking:

- **#44 on the final unit of work:** the event test scope implements `IsActive`; a dispatch test proves the handler's savepoint stays inside the active unit, so object-storage I/O is refused there and counted as an ordinary attempt.
- **#46 on the reworked worker:** discrepancy housekeeping settles exactly once on every lease-owned outcome of the new coordinator, including supersedes and venue deferrals of checks and refused refreshes; the overdue-check exception was dropped because scheduled claims ignore the cooldown and refreshes never move the due time; discrepancy work is part of eligibility, set-based discovery and the summary; entry links are watched once, through the final #45 `TerminalLinks`; both discrepancy migration designers carry the final #45 model; omitted venue executions make fill-derived checks unknown.
- **#47 on the final event layer:** `monitor.run-completed` through the new sink after every completed owner Check; the browser cursor is the opaque `{epoch}.{sequence}` with `nextCursor`, SSE `id:` resume cursors and all syntax in `event-cursor.ts`; explicit nulls; the `unverifiable` discrepancy outcome and watches with their role in the UI; the interim #45 Check client is replaced by `monitor/run` while keeping its 202 validator and the rule that opening a Play or a timer never requests a check. The release check proves delivery with the verification subscription in a separate `verifier` worker and checks the outbox during broker loss.

| Check (at `stack/47`) | Result |
|---|---|
| Locked restore and Release build | Passed; zero warnings |
| Backend suite, disposable PostgreSQL and RabbitMQ | 2,256 passed, zero failed, five optional live/S3 skips |
| EF model | No pending changes; a fresh database migrates through `PlayDiscrepancyUnverifiable` |
| Frontend typecheck, lint, tests, build | Passed; 1,871 tests across 104 files |
| Operator Python tests | 29 passed |
| Prototype tests | 76 passed |
| `docker compose config` | Base file and six overlay combinations valid |
| Packaged verification (S3 mode, synthetic data) | 166 checks passed, zero failed: image 5, HTTP 44, TLS 47, worker 10, monitor 26, restore 12, security 8, browser 14 |

The packaged run built local API/web images and fresh Compose projects with private RustFS. Delivery was proven by the `verification` subscription's receipts from a separate `verifier` worker: live delivery, the API outbox holding the publication while RabbitMQ was stopped and draining after it returned, and delivery of an event pending at backup after a restore without the broker volume (after `rotate-event-epoch`), followed by deregistration. The browser smoke ran in the pinned `mcr.microsoft.com/playwright:v1.63.0-noble` image against the packaged web with UI-only monitor fixtures, including an unverifiable discrepancy; dark, light and 390 px captures were inspected.

Each lower layer was also checked on its own: `stack/45` 2,057 backend tests and 1,800 frontend tests, `stack/46` 2,253 backend tests, all passing. The October 15 verification below predates the topic/subscription rework; its delivery statements (consumer delivery rows, republish) describe the replaced design.

## Broker removal, November 20, 2026 (issue #99)

Owner decision D61: RabbitMQ and Wolverine are removed; events and monitor requests stage a `pg_notify` in their own transaction and one listening connection per worker wakes the dispatcher or the scheduler. The delivery statements above (outbox, queues, broker outage, restore without the broker volume) describe the replaced transport. Verified on branch `fix/health-99-remove-broker`:

- Integration (`Vessel_TEST_POSTGRES`, no broker): `VesselEventTransportTests` (notification committed with the event and dropped on rollback, settlement table through the listening connection, catch-up for notifications nobody heard, a killed listening connection reconnecting, live delivery of 20 events with p95 under 2 s, insert-only provisioning), `MultiOwnerMessagingTests`, `WorkerHostTests` (worker starts with only PostgreSQL and listens; monitor requests notify only on commit; unknown, suspended and unreadable commands dropped), `MonitorEndToEndTests` including `MonitorWithoutBrokerTests.cs` (API and monitor worker start with only PostgreSQL, a Check wakes the worker without a tick, a lost listener delays a Check to the next tick). These broker tests were opt-in before and now run with every database run. With the listener registration disabled, the live-delivery tests fail, so they test the notification path.
- Receipt, catch-up and retention tests (`EventSubscriptionTests`, `EventSubscriptionPoisonTests`, `NotificationOwnerTests`, `MultiOwnerEventTests`) pass with their assertions unchanged, apart from settings that no longer exist and dead-letter wording.
- Operator scripts: `deploy/vessel` leaves a 0.4.2 RabbitMQ container alone and refuses a rollback to 0.4.2 without its Compose files (`scripts/test_deploy_vessel.py`); the release check measures delivery latency, kills the verifier's listening connection, stops the Notification Hub and the verifier, and upgrades the previous release from its own broker deploy files.

## Current work

- Sol implementers completed the five layers against [the shared API contract](play-monitor-api-contract.md).
- Independent Sol reviewers reproduced and checked fixes for transaction participation, credential races, rate limits, discrepancy coverage and frontend cancellation.
- The parent integrated the reviewed layers with current upstream changes and completed final browser/E2E verification.

## Worker-offline admission, October 16

Owner-approved behaviour change: an owner Check with a stale worker heartbeat answers 503 and records nothing, and pending check work unclaimed for `Lease` + `HeartbeatTimeout` after it became due returns to idle at admission so it no longer refuses Refresh with 429 indefinitely; it stays due for a returning worker. Verified by the `Offline_*` Postgres tests in `MonitorOfflineAdmissionTests.cs`; tests about other behaviour use `MonitorWorkerOnline` to report a fresh heartbeat. See [Admission, cooldown and triggers](messaging-and-workers.md#admission-cooldown-and-triggers).

## Verified foundation

An independent Sol reviewed the exact 50-file foundation snapshot, then built it in Release and ran its tests against disposable PostgreSQL. Results: 1,453 passed, two live-venue tests skipped, zero failed; all 15 UoW regressions passed. The build had zero warnings/errors.

Adversarial probes found three admission defects: non-atomic concurrent entry, reuse of a completed nested context and entry during rollback. Each was fixed and retested. The final rollback probe rejected late entry, left no tracked mutations and preserved the original account on the next transaction. The reviewer approved the final snapshot before commit.

No migration or broker dependency is included in this layer. The later worker layer moves venue reads outside database transactions.

After reconciliation with `f738ec2`, implementation and independent review both passed the full Release suite: **1,764 passed, three optional live-venue skips, zero failed**. The suite includes 139 PostgreSQL tests and all 15 UoW regressions. Upstream `IOwned` enforcement, extracted EF configurations, numeric fixes and composition boundaries remain intact.

## Final verification scope

The final checks passed on October 15, 2026. The detailed results follow.

- Build and run the repository tests with an explicitly supplied disposable PostgreSQL connection.
- Run the API and worker against synthetic venue observations with no browser open; prove one account read serves its Plays, a partial fill opens a Play, re-reads do not duplicate events and later exits close it.
- Confirm duplicate commands, concurrent manual/scheduled requests, failed-run cooldown, lease loss, settings/credential changes and account disable cannot apply stale results.
- Confirm incomplete reads cannot prove missing protection or a flat position; verify confirmation, dismissal and continued checks for linked exits after closure.
- Exercise committed-event ordering, SSE reset/reconnect and owner isolation.
- Build the release images and run isolated Compose with explicit EF and messaging provisioning, heartbeat health, private AMQP, loopback management and worker credential-key mounts.
- Stop/restart the broker and test durable recovery. Restore the synthetic database without the old RabbitMQ volume and check that pending work survives.
- Inspect the desktop/mobile UI, Check state, worker-offline state and discrepancy dismissal without changing the approved layout.

## Integration checkpoint, October 15

These layer reviews preceded the final full-stack run.

- Events: independent review confirmed the original three late-callback probes
  now pass. Each business callback and before-commit hook owns a participation;
  event writes require the exact active UoW/DbContext. A final interceptor guard
  initially broke evidence-only uploads. The narrow fix leaves object storage
  outside transactions and requires participation only for event-producing
  mutations. Independent re-review passed the unchanged evidence probe and
  all three late-callback probes, plus 72 focused tests. The corrected old-base
  suite has 1,506 passes and two live
  skips. Before that final two-file fix, upstream integration passed 1,816 tests
  with three live skips.
- Worker: independent review approved the corrected credential replacement and
  reverification fences, typed order-only authentication failures, and durable
  owner/venue Retry-After deadlines. Independent runs passed 168 tests plus
  three custom probes; the expanded implementer suite passed 274. Equal-time
  Refresh preserves ledger values while importing new facts; genuinely newer
  observations fence old runs. Check still requires fresh observations.
  Two real API/worker/PostgreSQL/RabbitMQ host tests pass. Upstream integration
  passed 1,915 tests with three optional live skips. A further independent
  review approved `25de361` and passed 78 focused tests covering owner guards,
  credential forwarding, Robinhood coverage and the combined account-monitor
  migration. The owner/venue budget is in `20261016110100_AccountMonitor`;
  there is no separate budget migration.
- Discrepancies: independent re-review passed 63 tests, including the three
  unchanged adversarial probes. Whole-position targets no longer hide missing
  stops; entry-price changes rearm dismissed drift; account-wide assignment
  coverage includes retrospective Plays and is explicitly bounded. A further
  independent review approved `7f4ac51`: 70 tests passed, including the original
  three adversarial probes and seven upstream ownership-model tests. The
  discrepancy migration adds only its own table and preserves the monitor
  budget in the preceding migration.
- Frontend: upstream reconciliation passed independent typecheck, lint,
  production build and all 1,824 tests across 98 files. Both original
  cancellation probes and four added capability/exact-value probes pass.
  Manual/retrospective workflows, lazy pages and unsaved drafts remain intact.
- Release images started successfully in isolated Compose. Worker heartbeat,
  authenticated event history/SSE, HTTP/TLS transport checks and broker
  stop/restart recovery passed. The first build attempt ran out of disk space;
  a second attempt stopped at the browser prerequisite, before backup/restore.
  These earlier attempts were not final approval; the complete run below
  supersedes them.

Only generated task-owned test output and selected obsolete Vessel build-cache
records were reclaimed after the disk filled. No running service, source
snapshot, evidence log or data volume was deleted. The disposable test database
was restarted after its disk-full failure; Docker assigned it a new loopback
port. Tests continue to use an explicit disposable connection string and
isolated schemas, never the owner's database.

## Final assembled verification

| Check | Result |
|---|---|
| Locked NuGet restore and Release build | Passed; zero warnings and errors |
| Backend suite, disposable PostgreSQL and RabbitMQ | 1,976 passed, zero failed, three optional live-venue skips |
| EF pending-model check | No model changes |
| Frozen pnpm install, typecheck and lint | Passed |
| Frontend suite | 1,835 passed across 100 files |
| Frontend production build | Passed; existing 500 kB chunk-size warning remains |
| Operator Python tests | 25 passed |
| Prototype tests | 76 passed |
| Packaged verification matrix | 134 checks: image 5, HTTP 44, TLS 47, monitor 22, restore 10, secret-log checks 6 |
| Browser smoke | 14 checks passed; zero CSP, console or page errors |

Overview allocation landed on `main` during the final checks. The five layers
were replayed onto `f80e641`, preserving both AGENTS entries. Backend and
deployment files are byte-identical to the tested stack. Frontend checks and
the complete packaged verification were rerun.
The independently reviewed worker/discrepancy commits above correspond to
`e5afc09` and `709cdfd` after this replay; their code is unchanged.

The first packaged rerun hit a browser-harness race: it decoded a newly
mounted evidence image before React had assigned its object URL. The harness
now waits for the `blob:` source before asserting decoding and dimensions;
no product image code or assertion was removed.

The full backend suite includes the real API/worker/PostgreSQL/RabbitMQ tests:
scheduled lifecycle reconciliation without a browser, durable on-demand
commands, cooldown, owner isolation and restart recovery. Synthetic venue
observations keep these checks independent of live accounts.

The packaged run built local API/web images and used fresh Compose projects.
It verified explicit migration/provisioning, worker heartbeat, authenticated
history and SSE through nginx, one event per committed status change, no
duplicate for a stale mutation, and durable delivery after a broker outage.
It then backed up PostgreSQL with a pending event, removed the original
deployment and RabbitMQ volume, restored into fresh volumes and verified
delivery with the **same EventId**. Account/Play records, evidence bytes,
notes/markup and credential-key bytes survived restoration.

Browser monitor states use explicitly labelled UI-only fixtures; they are not
evidence of live venue integration. The backend and transport checks above
exercise the real hosts. Graphite desktop, Paper desktop and 390 px Paper
captures were inspected: Check is explicit and disabled in flight/during
cooldown, exact discrepancy details render, dismissal retains its note and
explains that checks continue, and the mobile page has no horizontal overflow.

Reproduce with `Vessel_TEST_POSTGRES` and `Vessel_TEST_EVENT_RABBIT` pointing
only at disposable services:

```sh
cd backend
dotnet restore Vessel.slnx --locked-mode
dotnet build Vessel.slnx -c Release --no-restore
dotnet test tests/Vessel.Tests -c Release --no-build --no-restore
# Supply a disposable ConnectionStrings__Vessel for design-time tooling.
dotnet ef migrations has-pending-model-changes \
  --project src/Vessel.Persistence --configuration Release --no-build
cd ..
pnpm install --frozen-lockfile
pnpm --filter vessel-frontend typecheck
pnpm --filter vessel-frontend lint
pnpm --filter vessel-frontend test
pnpm --filter vessel-frontend build
python3 -m unittest discover -s scripts -p 'test_*.py'
pnpm test:prototype
# Use an installed Chromium and Playwright module; no dependency install here.
CHROMIUM_PATH=/path/to/chromium \
VESSEL_RELEASE_CAPTURE_DIR=/path/to/captures \
python3 scripts/verify-release.py --browser-module /path/to/playwright-core
```

On the shared verification host, .NET commands ran under
`MSBUILDDISABLENODEREUSE=1 flock --close /tmp/vessel-play-monitor-dotnet.lock`.
The packaged harness removed its own containers, volumes, networks and image
tags after success. No Docker image was pushed.
