# Backend review, October 14, 2026

A review of the backend architecture and code after PR #37, looking for defects, simplifications and cleanups. The companion [backend guide](backend-guide.md) explains how the backend works.

**Original review method.** I read all of `backend/src` (about 10,700 lines, excluding migrations), skimmed the tests (about 18,500 lines) and cross-checked the docs, CI and the frontend's polling. The solution built with 0 warnings (`TreatWarningsAsErrors`). At that checkpoint, 1,316 tests passed and 124 were skipped because the PostgreSQL and live-venue environment variables were unset. No code changed during the initial review. The findings below retain that baseline; subsequent implementation is tracked here.

## October 16–17, 2026 improvement pass

A second pass on the `t3/backend-health` branch, based on `main` at `6d2e55a`. It records the work on that branch, not a release, deployment or migration of the owner's database.

**Method.** Four independent audits (three Opus: performance, reliability and code health; one Sol: combined) produced a draft plan. A challenge round by Sol and Opus cut it to behaviour-preserving or clearly corrective changes; locking semantics, admission design, migrations, wire-contract changes and cross-request caches were deferred unless the owner approved them. Opus agents implemented waves A–F in separate worktrees with disjoint write scopes. A Sol verification round found three issues (a slow client could hold the event stream open for a whole page past its lifetime, an overflowing track-record mean was rounded twice, and one settlement path reparsed the plan); all three were fixed in `0a8ce60` with regression tests.

| Item | What changed |
| --- | --- |
| A1–A5 | Execution matching uses hash indexes of the loaded links instead of scanning them per order. Matching, transitions, `GetAsync`, `LinkAsync`, `UnlinkAsync` and the retrospective preview read only what can matter (candidate orders on active Plays' contracts since their earliest creation, the Play's own links, one order or one link); orders are read without tracking. Results are unchanged. |
| B1/B2 | Event fill capture loads the baseline once per unit, skips unlinked orders and claims fill identities in one `INSERT … SELECT unnest … ON CONFLICT DO NOTHING RETURNING`. Event identities, order and payloads are unchanged; payload amounts use the shared `ExactDecimal` formatter. |
| C1–C4 | The monitor scheduler keeps one wake waiter across ticks (it leaked one per tick). A caller-cancelled Refresh releases its lease without recording a venue sync failure. An unreadable stored event settles its receipt as failed with zero handler attempts, and a running subscription without its handler stops host start. The browser event stream ends after `Vessel:Events:StreamMaxLifetime` (30 minutes), checked before every frame, and the browser resumes from its cursor. |
| D1, D2, D4 | Track-record averages and the exposure ladder no longer overflow: the exact sum over the count is rounded once, and an unrepresentable ratio is null with a notice. Concurrent first writes of sizing settings both succeed (`ON CONFLICT DO NOTHING` then `FOR UPDATE`). Each closed Play's plan is parsed once. |
| D3, E1, E2 | Transient S3 failures (transport, timeout, throttling, 5xx) answer 503 through `EvidenceStorageUnavailableException`; 4xx, missing keys and the 412 create-only conflict are unchanged. Each venue adapter reuses one `BoundedJsonHttp` and parses the pre-sized buffer without a copy. `VenueCredentialPurpose.ReadToken` replaces seven copies of the AES-GCM purpose string, byte for byte. |
| P-a (owner-approved) | A Check with a stale worker heartbeat answers 503 and records nothing. Pending check work that no worker claimed within `Lease + HeartbeatTimeout` returns to idle at Check or Refresh admission, still due, so it no longer blocks Refresh. |
| E3 (owner-approved) | Credential verification runs with no transaction or lock open; a short locked recheck saves, or answers 409 if the account settings, source, venue or stored credential changed. The absent-created-deleted ABA case is an accepted limitation. |
| F1 | `AddVesselApplication()` and `AddVesselMonitoring()` live in `Vessel.Infrastructure/ApplicationServices.cs`, shared by the API and worker; Persistence registers only the context and stores. `HostCompositionTests` builds both containers. |
| F2 | Leftovers removed (`BoundedMarketDataCache.Set` private, unused `VenueOrder.IsTrigger`, stale `global::` qualifiers). |
| F5 | Read-only API rejection matrices share one host per class (`DiaryRequestLimitTests` 24 s to 0.6 s). |
| R3 | `backend/README.md` rewritten around running, testing, migrating and configuring. |
| Analyzers | Migrations are generated code. CA1806, CA1725, CA2016, CA1826, CA1859, CA1869, CA1870, CA2263, CA1068 and CA1036 are warnings (errors under `TreatWarningsAsErrors`) for `backend/src`; tests keep the SDK defaults. 28 findings: 27 fixed, one CA1859 suppressed in place with its reason. CA2016 and CA1826 had no findings. |

**Verification.** The integrated branch passed the full suite with 2,763 tests passed, 5 opt-in skips (S3 and live venues) and 0 build warnings before the analyzer change. After the analyzer change the Release build had 0 warnings and the full suite again passed 2,763 with 5 opt-in skips. Tests used disposable PostgreSQL schemas and broker prefixes only.

**Backlog status after this pass.** R1 is done through the monitor: Refresh and Check read the venue under a lease with no transaction or lock, then apply in short fenced units. R3 is done. S3 is done again (the monitor work had moved registrations back into Persistence). S9 is partly done: matching and execution reads are narrowed; Activity, Overview and Review still recompute from all retained facts. N6 is partly done: the purpose helper exists; the credential `GET` still decrypts to report status. N9 is done for `BoundedJsonHttp`; the Hyperliquid `StrictJson` wrappers remain. N11 is partly done: selected analyzers are on; test folders and traits are not.

### Deferred proposals

Each needs measurement or owner agreement first; none is implemented.

- Session `lock_timeout` with a 55P03 mapping, so lock waits fail fast and clearly. Done in issue #106 (November 2026), with `statement_timeout` too; see [deploy/README.md](../../deploy/README.md#database-timeouts).
- S2: one lock coordinator for the advisory-lock sites and the account-row lock implementations.
- An `imported_fills (OwnerId, AccountId, OrderId)` index, after `EXPLAIN` on a 100k-fill dataset.
- A read model for Activity, Overview and Review (P6), after measurement.
- D5: Overview summary from one snapshot (one repeatable-read reporting transaction for membership, balances and facts).
- Splitting the drawings column from `plays` so closed-Play loads skip it.
- N14 HTTP status consistency (retrospective creation 201, reader-unavailable 502 versus 503); a frontend contract change.
- Automatic decompression for venue HTTP, after checking each venue's size-limit semantics.
- Worker readiness that reports broker and outbox age.
- S8 typed statuses, and splitting `MonitorDiscrepancyService.Observe` and `MonitorCoordinator.RunAsync`.
- N12 evidence orphan sweep, and handling an S3 412 on a retried create.
- CA1848 (`LoggerMessage`) and CA1711; not enabled in this pass.
- Activity and Diary join a Play's links to fills by account and order without checking the Play's contract (noted by Sol). Investigate whether a cross-contract attribution can occur; do not change it without a reproduction.
- A Check on an account whose run is still `running` with an expired lease answers 429 with `Retry-After: 1` until a worker reclaims it.

## Implementation and verification

October 14, 2026, on `t3/backend-architecture-review`. This records the work in PR #38, not a release or deployment.

| Work | Implemented | Independent verification |
| --- | --- | --- |
| Wave 1 | S3 composition root, S7 error types, S11 namespace, N1/N3/N4 cleanup and the manual venue ID part of S8. | Sol found no runtime regression. The stale guide references it found are corrected. |
| Wave 2a | R2 `IOwned` filters/save guard and S12 entity configurations/decimal convention. | Sol passed 75 targeted tests, checked all 18 mapped entities and compared the baseline, current model and migration snapshot. No introduced finding; both schema diffs were empty. |
| Wave 2b | S1 named exact-number profiles and shared arithmetic/formatting, plus N2 drawing-document disposal. | Sol passed 1,013 targeted tests and 868,510 independent differential/exactness checks. No introduced finding. |
| Wave 2c | S5 endpoint transport metadata, S10 venue composition, plus the authorized groups/claims portion of S4. | Sol found two transport regressions. Fix `d17f56a` moves server body limits after authorization and preserves unmatched-route errors. Sol re-verification closed both: 50/50 immediate authenticated retries succeeded and 870 differential HTTP cases matched the baseline. |
| Arithmetic follow-up | R4 derived-price overflow guard and safe tiny-price tolerances in `728a13c`. | Sol closed both reproductions with no actionable finding: 61,140 tolerance checks, 576 level-resolution checks and six Review/Sizing service cases passed. |

The corrected combined code at `d17f56a` passed 1,689 backend tests against a dedicated synthetic PostgreSQL container; only the two opt-in live-venue tests skipped. All 24 Python checks passed. The transport regression tests failed before the correction and the corrected focused suite passed 132 tests. Independent re-verification found no remaining introduced issue in its scope.

**Final integrated checks.** After the arithmetic fix and integration of the latest `main`, `scripts/check.sh` passed at `3ea2e26`: 1,708 backend tests with two opt-in live-venue skips, 24 Python tests, 76 prototype tests and 1,256 frontend tests across 68 files. Locked restores, the Release build, frontend type checking, lint and production build also passed. The Release build reported zero warnings and errors. Sol confirmed that the reviewed arithmetic code and regressions were unchanged by the `main` integration.

The [current-state guide](../app-status.md) and [four diagrams](../diagrams/app-status.md) cover the whole app. All 38 relative links and anchors resolve. The four Mermaid diagrams were rendered and inspected; the HTML overview was checked at desktop and mobile widths without horizontal overflow.

Remaining implementation includes R1/S2 refresh and lock work, S9 execution performance, N7/N13 reporting consistency, S6 workspace separation, the rest of S8, N5/N6/N10, and N8/N11/N12/R3 documentation and maintenance work. S4 refresh orchestration is still in the endpoint. N9 adapter allocation/wrapper cleanup and N14 retrospective HTTP status consistency are **not implemented**. P1–P8 remain proposals needing owner agreement. The owner subsequently authorized fixing R4 and merging the implemented cleanup; the remaining waves are separate follow-up work, not part of this merge scope.

The owner-isolation review also reproduced two preexisting boundaries, neither reached by current production code:

- Attaching a fabricated entity with a foreign row ID but local current/original `OwnerId` can bypass the save guard on an ID-only key. Current stores load owner-filtered rows rather than attaching caller-supplied entities. Before introducing detached writes, add an owner concurrency predicate or equivalent storage check and a regression test.
- An EF customizer that adds entities after `OnModelCreating` can miss owner-filter installation. No such customizer is registered. Mapped inheritance currently fails closed; current entities are sealed. Revisit model-finalization checks if either extension is introduced.

Verification used disposable schemas only. It did not read the owner's `.env`, call live venues, deploy the app or migrate the owner's database.

### Final code-smell and architecture pass

A fresh GPT-6.1-Sol review inspected `356a6ba` against PR base `6acd5fe`. It covered composition and venue registration, authorization and transport, ownership and EF configuration, numeric consumers, and adjacent Play, diary, reconciliation, evidence, refresh and reporting code.

The review found **one new P2 issue, R4 below**, in preexisting execution arithmetic. The parent reviewer independently reran the service-level reproductions against both revisions and confirmed both failures. No introduced regression was found in the inspected paths. The earlier deferred items remain open; this pass does not certify the entire backend as defect-free.

A fresh Release test run passed **1,689 backend tests**, with only the two opt-in live-venue tests skipped. All **24 Python tests** passed, and all **38 current-state guide and diagram links/anchors** resolved. CI on `356a6ba` also passed. The new arithmetic probes use actual application services and in-memory stores; they are separate from the passing regression suite and were not HTTP or live-venue tests.

The review-only pass changed documentation. The subsequent owner-authorized arithmetic fix is recorded under R4; it does not implement the remaining architecture backlog.

## What is already in good shape

- **Layering is real and enforced.** Domain and Application have no package references, and `ArchitectureTests` fails the build if a project reference points the wrong way.
- **Owner isolation is defended in depth:** query filters, composite `(OwnerId, Id)` foreign keys, a save-time guard and owner conditions repeated in raw SQL.
- **Venue boundaries are strict.** Reads are bounded, JSON is parsed strictly with duplicate-key rejection, vocabulary is normalized and checked before storage, and failures are safe and fixed messages.
- **Exact numbers are taken seriously.** Input is never silently rounded, sums cannot overflow, and unknown stays null.
- **Concurrency is explicit.** Row and advisory locks have a documented order, and real PostgreSQL tests observe lock waits.
- **Operations are safe.** Migrations are explicit, diagnostics are safe, production refuses to start with bad configuration, and credentials are sealed with a key ring kept outside the database.
- **Capabilities, not names.** The venue registry lets use cases ask what a venue can do.
- **Decisions are documented.** Most decisions have a contract or state document.

The findings below are mostly about **duplication that grew feature by feature**, **plumbing that is spread across classes**, and **a few design choices that will not survive the next phase** (background monitoring, longer history).

## Categories

| Category | Meaning |
| --- | --- |
| **Required** | Fix before the next feature phase (background monitor, history backfill). Either the failure can already be triggered, or a core invariant depends on a convention nobody enforces. |
| **Should fix** | A real maintenance cost, inconsistency or latent risk. Plan within the next few PRs. |
| **Nice to fix** | Small cleanups. Do them when touching the code nearby. |
| **Possible refactor** | Larger structural change worth discussing. Each needs owner agreement before work starts. |

Effort: **S** less than half a day, **M** one to two days, **L** several days or needs a design first.

---

## Required

### R1. Venue I/O runs inside database transactions and row locks — M/L

**Where.** `WorkspaceService.SyncAsync` (`Workspace/WorkspaceService.cs:334`) calls the venue at line 346 inside `IWorkspaceStore.WithAccountLockAsync` (`Persistence/WorkspaceStore.cs:100`). `PlayExecutionService.CheckAsync` (`Plays/Execution/PlayExecutionService.cs:70`) then takes the lock again and reads orders and the instrument catalogue (`TicksAsync`, line 228) inside it. Lighter's adapter also opens the credential under the same lock.

**Problem.** A transaction stays open, with the account row locked and a pooled connection held, while the server waits for venue HTTP calls. Each read has a deadline of up to 20 s; Hyperliquid alone makes six sequential calls. While that happens, any settings change, disable, delete or credential change for the account waits on `SELECT … FOR UPDATE`. That wait counts against Npgsql's default 30-second command timeout, so a rename during a slow refresh or execution check can fail with a 503. The browser already runs the execution check every 60 seconds while an active Play is open, so this overlap is easy to hit. A background monitor (play-lifecycle step 4) would hold these locks continuously.

**Suggestion.** Read first, then commit in a short transaction:

1. Read the account outside a transaction, and the Lighter token in its own short read.
2. Call the venue with no transaction open.
3. In a short transaction: lock the row, re-check that the account exists, is enabled and has the same source identity, then save.

The save is already idempotent: a snapshot is replaced only by a strictly newer observation, fills are de-duplicated (with a unique index behind it), and an order update older than the stored one is ignored. So two overlapping refreshes are harmless, and "a disable cannot be overwritten by an in-flight refresh" still holds through the re-check. If the owner wants only one venue read per account at a time, use a process-level keyed gate or a non-blocking session advisory lock, not the row lock.

This choice shapes the monitor design, so agree it with the owner before implementing it (see P1).

**Status (October 17, 2026).** Done through the monitor: Refresh and Check read the venue under a monitor lease with no transaction or lock open, then apply in short fenced units; credential verification also runs outside the lock (E3).

### R2. The owner write guard is fail-open for new entity types — S

**Where.** `VesselDbContext.ValidateOwnership` (`Persistence/VesselDbContext.cs:303`) switches over 18 entity types and falls through to `_ => (Guid?)null` (line 331). Each query filter is also added by hand in `OnModelCreating`.

**Problem.** All 18 tables are covered today. But adding a table without updating both places silently drops the save-time guard and the read filter for it, and owner isolation is a core invariant of the product.

**Suggestion.**

- Add a Domain marker interface `IOwned { Guid OwnerId { get; } }` and implement it on every entity.
- In `OnModelCreating`, loop over entity types, apply the owner query filter to each `IOwned`, and throw during model building for any entity type that does not implement it.
- In `ValidateOwnership`, use `entry.Entity is IOwned owned ? owned.OwnerId : throw`.
- Add a test that enumerates `db.Model.GetEntityTypes()` and asserts each one has a filter.

Combine this with S12.

### R3. `backend/README.md` contradicts the code — S

**Where.** `backend/README.md`.

**Problem.** A newcomer who reads it first learns things that are no longer true:

- "Active and Closed are minimal head states" (there are six statuses).
- "No … Play CRUD, inferred execution matching" (both exist).
- "Venues are `manual` or `hyperliquid`" and "synchronous Hyperliquid refresh only" (RISEx and Lighter exist).
- "the forty PostgreSQL tests" (124 tests are skipped without a database).
- The Infrastructure description mentions only the Hyperliquid reader.
- The route list leaves out plays, review, sizing, activity, overview summary, market data, credentials and discovery.

**Suggestion.** Cut the README down to running, testing, migrating and configuring, and link the [backend guide](backend-guide.md) for structure. Move the per-phase historical detail (review safeguards, stablecoin update, package provenance) into the existing state documents. This review only added a pointer at the top.

**Status (October 17, 2026).** Done: the README now covers running, testing, migrating and configuring and links the guide.

### R4. Accepted plan numbers can overflow execution matching

**Status.** P2, found during the final review of `356a6ba`; also reproduced at PR base `6acd5fe`. This was a preexisting defect, not a regression from the numeric-helper cleanup. Fixed in `728a13c`.

**Where.** `ExecutionMatcher.Near` and `ExecutionMatcher.Resolve` in [ExecutionMatcher.cs](../../backend/src/Vessel.Application/Plays/Execution/ExecutionMatcher.cs).

**Reproduction.** With a synthetic venue account and a one-entry long plan, both of these pass `PlayService.CreateAsync` and the transition to Planned:

- Entry price `"100000"`, leverage `"1"` and a percentage target of `"1e28"`. `PlayExecutionService.GetAsync` throws `OverflowException` while resolving the target, even with no orders, fills or links.
- Entry price `"1e-25"`. Matching a subsequent ordinary buy limit order throws because the fifth-significant-figure tolerance calculates `10^29` before taking its reciprocal. The calculation runs before a coarser instrument tick can replace it.

**Impact.** The first case repeatedly breaks the saved Play's execution read. The API's existing unexpected-error handler maps the exception to HTTP 500. The second can abort account-wide reconciliation from another Play because `MatchAsync` considers all active Plays on the account. No venue failure or fabricated detached entity is needed.

**Fix.** `Resolve` catches derived-price overflow locally and leaves the level's price null. Its key and the stored plan text remain unchanged, and it cannot automatically match an order. `Near` computes fractional powers directly rather than constructing an overflowing denominator. Below decimal precision, the step is zero, requiring exact equality unless a venue tick provides a coarser tolerance. Supported calculations and the global exception handler are unchanged.

**Regression evidence.** Two real PostgreSQL service tests failed with the original overflow errors and passed after their respective fixes. They cover accepted-plan execution reads, continued linking of valid entry orders, refusal to match an unresolved target, and reconciliation of both an ordinary Play and a tiny-price Play when checking the ordinary one. Thirteen exact-decimal/tick cases cover the tolerance boundary and decimal maximum; two further cases cover overflowing stops and targets in both directions. All 254 focused execution, market-data, sizing and review tests passed before integrating the latest `main`.

---

## Should fix

### S1. Exact-number logic is duplicated, with different acceptance rules — M

**Where.** At least eight implementations:

- `Activity/ExactAmount.cs`
- `Workspace/StablecoinTotals.cs`, which repeats `ExactAmount.From` and `ToString`
- `StrictJson.ParseDecimal` and `StrictJson.DecimalUnits`
- `ManualLedger.Number`, `Add`, `Multiply` and `Text`
- `WorkspaceService.TryExactNonnegativeDecimal`
- `AccountDiscoveryService.ExactAmount`
- `PlayDocuments.NumberPattern`
- five copies of the `"0.############################"` formatter (`WorkspaceService:22`, `PlayExecutionService:384`, `ManualLedger:149`, `ReviewService:272`, plus `SizingService:71`, which rounds to 8 places)

**Problem.** Exactness is the product's core promise, yet each entry point accepts different text:

| Entry point | Sign | Exponent | Other |
| --- | --- | --- | --- |
| Plan fields | yes | yes | |
| Manual executions | `+` only | yes | |
| Manual account value | no | no | digits and dot only |
| Venue JSON | `-` only | no | |

The BigInteger code is copied three times. A fix to one copy does not reach the others.

**Suggestion.** Create one Domain `ExactDecimal` module:

- parsing with named profiles (for example `PlanText`, `OwnerAmount`, `VenueAmount`);
- one invariant formatter;
- one 28-place `BigInteger` accumulator (today's `ExactAmount`, made public in Domain);
- checked `Add` and `Multiply`.

Keep the per-contract rules, but give them names in one place, and add table-driven tests. `StablecoinTotals.Sum` becomes `ExactDecimal.Sum(...)`.

### S2. Locking is spread across stores, and services reach into each other — M

**Where.**

- `pg_advisory_xact_lock(hashtextextended(…))` appears five times (`WorkspaceStore:47`, `PlayStore:55`, `ExecutionStore:86`, `DiaryStore:15`, `EvidenceStore:20`).
- There are two account-row lock implementations (`WorkspaceStore.WithAccountLockAsync` and `ExecutionStore.WithAccountLockAsync`).
- `PlayStore` constructs other stores to borrow their locks (`new WorkspaceStore(db)` at line 56, `new ExecutionStore(db)` at line 59).
- `PlayExecutionService` depends on the concrete `WorkspaceService` to call its `internal` `WithAccountLockAsync` and `RecordOrderHistoryNoticeAsync`.

**Problem.** The lock order (management, then account row, then execution, then Play) is a convention spread over five classes. A new path can take the locks in another order and deadlock. The begin-transaction-or-join and reload-after-lock code is copied each time.

**Suggestion.** Add one Persistence `PostgresLocks` (or `LockCoordinator`) with:

- `InTransactionAsync`
- `ManagementAsync(owner)`
- `AccountRowAsync(id)`, which reloads the row
- `ExecutionAsync(account)`
- `PlayRowAsync(id)`
- `KeyedAsync(name)`

Make its methods follow the documented order, and assert it in debug builds. Expose what Application needs as one small port (`IAccountLock`). That removes the `PlayExecutionService` → `WorkspaceService` coupling.

### S3. Application services are registered by Persistence — S

**Where.** `Persistence/DependencyInjection.cs:28-55` registers `WorkspaceService`, `PlayService`, `ReviewService`, `CandleService` and the others.

**Problem.** The storage project decides which use cases exist. Swapping or testing persistence means re-registering the application, and a newcomer looks for the services in the wrong place. Application cannot do the registration itself, because the architecture test forbids package references there.

**Suggestion.** Register application services in the composition root, for example a `Vessel.Api/ApplicationServices.cs` with `AddVesselApplication()`. Persistence then registers only the `DbContext` and stores.

**Status (October 17, 2026).** Done again: the monitor work had moved its use cases back into Persistence. `AddVesselApplication()` and `AddVesselMonitoring()` now live in `Vessel.Infrastructure/ApplicationServices.cs`, shared by the API and worker (F1).

### S4. Endpoints contain orchestration — S

**Where.**

- `POST /api/accounts/{id}/sync` (`Api/WorkspaceEndpoints.cs:87`) runs `WorkspaceService.SyncAsync`, resolves `PlayExecutionService` from `RequestServices`, retains orders and then reads the account again. The `AccountDto` that `SyncAsync` returns is thrown away.
- `/api/system` (`Program.cs:127`) parses claims itself.
- `/api/activity/retrospective` is mapped in `PlayEndpoints` with its own `RequireAuthorization`.
- Review endpoints are not grouped.

**Suggestion.**

- Add `AccountRefreshService.RefreshAsync(id)` returning the final DTO. It is also the natural home for R1.
- Give the owner's display name to `IJournalOwnerContext`, or use a small `IJournalOwner` reader.
- Map every route through `MapGroup("/api/…").RequireAuthorization()`.

### S5. Transport rules depend on matching path strings — S

**Where.** `Program.cs:72` and `85-108`: `Path.EndsWith("/credential")` and `Path.Contains("/manual-executions")`. `Cache-Control: no-store` is set in up to four places for the same response (global middleware, error handler, credential middleware and endpoint filter), plus explicit sets in `WorkspaceEndpoints:96,102` and `ReviewEndpoints:15` that the global middleware already covers.

**Problem.** A new token-bearing route whose path does not end in `/credential` silently loses the HTTPS guard and the 4 KiB body limit.

**Suggestion.** Use endpoint metadata, as `CompressMarketData` already does:

- `.WithMetadata(new RequestSizeLimitAttribute(4096))`. Routing applies `IRequestSizeLimitMetadata` before model binding; this is confirmed in ASP.NET Core 10.
- A `CredentialEndpoint` marker that a small middleware checks for HTTPS and `no-store`.

Remove the redundant `no-store` sets.

### S6. `WorkspaceService` and `IWorkspaceStore` do too much — M

**Where.**

- `WorkspaceService` (437 lines) mixes portfolio CRUD, account CRUD and settings, Overview, snapshot DTO mapping, the instrument catalogue, sync orchestration and the balance and availability rules.
- `IWorkspaceStore` has 20 members and six consumers. Market data needs only `MarketDataAccountAsync`; credentials need only the account lock.
- `OverviewSummaryService` consumes `WorkspaceService`'s string DTOs and parses them back with `ExactAmount.Parse`.
- The Hyperliquid account-mode names `"unifiedAccount"` and `"portfolioMargin"` are matched in shared code (`WorkspaceService.cs:393,402`).

**Suggestion.**

- Split by responsibility: `PortfolioService`, `AccountService`, `AccountRefreshService`, `OverviewService`, `InstrumentCatalogService`.
- Extract a pure `AccountBalances` calculator (balance, availability source, coverage) that works on decimals and formats once at the edge.
- Let the adapter state the wallet semantics (for example `VenueStablecoinWallet.IncludesPerpetualEquity`) instead of shared code knowing Hyperliquid's mode names.
- Split the store port to match: `IPortfolioStore`, `IAccountStore`, `IAccountFactsStore`, `IAccountLock`.

### S7. `WorkspaceException` is the error type for every module, and domain errors are translated by hand — S/M

**Where.**

- `WorkspaceException` (`Workspace/Contracts.cs`) is thrown from 25 files, including Plays, Evidence, Review, Sizing and the Persistence stores.
- Domain throws `ArgumentException` and `PlayRuleException`.
- `PlayService.Apply` and `SizingService` translate them to 400 and 409.
- Other services validate the same rules first, so they never reach the domain (for example `ValidateName` against `Account.UpdateSettings`).

**Problem.** The name misleads. Validation is duplicated between services and entities. An `ArgumentException` that escapes from framework code would be reported to the owner as their own input error if a service translated it, or as a 500 if not.

**Suggestion.**

- Rename the exception to a neutral `RequestProblemException` (or similar) in a shared namespace.
- Add Domain `DomainValidationException` (400) and `DomainRuleException` (409; `PlayRuleException` becomes one).
- Map both centrally in the error middleware.
- Remove `Apply` and the duplicated service-side checks where the entity already enforces the rule.

### S8. Statuses, roles and directions are string literals — M

**Where.**

- `ImportedOrder.Status` is a string. The `VenueOrderStatus` enum exists, but Persistence lowercases it, and services compare against `"open"`, `"filled"`, `"canceled"`, `"rejected"` and `"triggered"`.
- `Account.SyncStatus` (`"manual"`, `"not-synced"`, `"synced"`, `"error"`).
- Plan direction `"long"` and `"short"`: more than a dozen comparisons.
- Manual and operation roles `"entry"` and `"exit"`.
- Operation status and association.
- The `"manual"` venue literal appears 8 times even though `VenueDescriptor.ManualId` exists, including in Domain `Account`, which cannot see that constant.
- `ManualLedger` sets `FeeBasis = "unknown"`, a value outside the `ExecutionFacts` vocabulary, on purpose.

**Suggestion.**

- Use enums persisted with `HasConversion<string>()`, as `Play.Status` already is, and keep the wire format through JSON converters.
- Move the manual venue ID into Domain.
- Give the "unknown fee" state a named member instead of an out-of-vocabulary string.

### S9. Reads grow with all retained history, and matching is quadratic — M

**Where.**

- `ActivityStore.ReadAsync` loads every fill of every enabled account on each Activity and Overview-summary request.
- `DiaryStore.ActivityAsync` loads all fills of the account to preview one operation.
- `SizingStore.ClosedPlaysAsync` loads every closed Play with all its fills for Review and Sizing. The Plays editor loads Sizing.
- `PlayExecutionService.GetAsync` loads every order and link of the account to show one Play, and runs every 60 s through the check.
- `MatchAsync` (line 263) scans every retained order with `links.Any(...)` inside the loop (lines 277 and 282), so O(orders × links). It reloads orders, links and fills on each of up to three passes.

**Problem.** Fine at today's volumes. Venue imports are capped (for example 2,000 Hyperliquid fills per refresh), but retained facts are never pruned. The planned backfill or archive would multiply them.

**Suggestion.**

- Before backfill, seed a large synthetic dataset (100k fills, 10k orders) and measure.
- Cheap wins: hash lookups for links by order, matching only orders placed after the earliest active Play and not yet linked, and filtering `GetAsync` to the Play's contract and creation time.
- Larger options are in P6.

**Status (October 17, 2026).** Partly done: matching uses hash lookups and matching and execution reads are narrowed (A1–A5). Activity, Overview and Review still recompute from all retained facts.

### S10. Venues are registered in three different ways — S

**Where.** `Infrastructure/DependencyInjection.cs`:

- Hyperliquid registers four typed clients with the same configuration copied four times (lines 28-47).
- RISEx registers four named typed clients.
- Lighter registers one typed client and forwards each port in scope.
- `IVenueAccountDiscovery` and `IVenueCredentialVerifier` are not in `IVenueRegistry`. Services resolve them with `IEnumerable<>.SingleOrDefault()`, which throws on a duplicate registration, while the registry lets the last one win.

**Suggestion.** Add one `AddVenueModule<TAdapter>(descriptor, configureClient)` helper that registers one named client and forwards whichever ports the adapter implements. Add `Discovery(venueId)` and `CredentialVerifier(venueId)` to the registry. Adding a venue then means one line in composition.

### S11. The `Vessel.Application.System` namespace shadows `System` — S

**Where.** `System/SystemMetadata.cs:3`. Inside Application, `System.X` now resolves to this namespace first, which forces `global::System…` in `OverviewSummaryService.cs:23` and `OperationAggregator.cs:305`. `SystemMetadata.Upcoming` still lists Lighter as `planned` (line 19); it is hidden only because the real descriptor exists.

**Suggestion.** Rename the namespace to `Vessel.Application.Metadata`. Remove the stale Lighter entry, and decide whether Quantfury should still be shown.

### S12. EF model configuration is long and depends on order — S

**Where.** `OnModelCreating` is about 270 lines. Every decimal is mapped to `numeric` by a loop in the middle (`VesselDbContext.cs:141-144`). The loop only reaches entity types already in the model at that point; today all of them are, because every entity has a `DbSet`.

**Suggestion.**

- Map decimals with `ConfigureConventions(b => b.Properties<decimal>().HaveColumnType("numeric"))`, keeping the explicit `(5,2)` on `SizingSettings.RiskPercent`.
- Split the configuration into one `IEntityTypeConfiguration<T>` per aggregate and use `ApplyConfigurationsFromAssembly`.
- Apply the owner filter and the composite owner foreign key in one loop (R2).
- Check that `dotnet ef migrations add NoOp` produces an empty migration.

---

## Nice to fix

- **N1. Dead code and leftovers.**
  - The `HexValidation` file class at the bottom of `WorkspaceService.cs` is unused.
  - `SizingService.Outcome` and `StopDistance` pass-throughs are used only by tests; tests can call `PlayOutcomeCalculator`.
  - `CandleCache.Set` is unused.
  - Blank-line runs remain in `HyperliquidPerpetualReader.cs` from splitting the partial classes.
- **N2.** `PlayDocuments.ReadDrawings` (`PlayPlanDocument.cs:112`) never disposes its `JsonDocument`, so pooled buffers are not returned. Use `JsonSerializer.Deserialize<JsonElement>(stored)`.
- **N3.** `[assembly: InternalsVisibleTo("Vessel.Tests")]` is hidden in `LighterReader.cs:11` but applies to the whole Infrastructure assembly. Declare it in the `.csproj`.
- **N4. Duplicated literals and checks.** `HyperliquidMarketStream.VenueId => "hyperliquid"` should use `HyperliquidPerpetualReader.Id`. `EvmAddress.Require` (Infrastructure) duplicates `AccountSource.IsEvmAddress` (Domain).
- **N5.** `Account.HistoryNotice` packs two notices behind a magic `"\nOrder history: "` separator in one 1,000-character column. A budget test keeps the combined length legal. Two columns (`HistoryNotice`, `OrderHistoryNotice`) would remove the parsing and the budget; the API can still return them joined.
- **N6. Credentials.**
  - The purpose string `VenueId + "-read-token"` is built in four places; use one helper.
  - `AccountCredentialService.Metadata` decrypts the token on every `GET` to report its status, while `AccountCredentialReader.IsUsableAsync` deliberately avoids decrypting. Pick one policy.
  - *Status (October 17, 2026):* the purpose helper is done (`VenueCredentialPurpose`); the `GET` decrypt is still open.
- **N7. Review and Overview.**
  - `ReviewService.GetAsync` and `ExclusionsAsync` repeat the filter validation and account scoping.
  - The fee-incomplete predicate is written twice (lines 135 and 139).
  - Period windows are exclusive at the start in Review (`> from`) but inclusive in the Overview summary (`>= from`).
- **N8.** Long user-facing notice texts are spread inline across services. A `Notices` class per module would make them easy to find and review together; keep them server-owned.
- **N9. Adapter boilerplate.** Adapters build a new `BoundedJsonHttp` on every property access (`Http => new(...)`); make it a field. `HyperliquidPerpetualReader` has a dozen one-line wrappers around `StrictJson`; call `Json.X` directly, as RISEx does. *Status (October 17, 2026):* the `BoundedJsonHttp` part is done; the wrappers remain.
- **N10.** The enum-to-wire helpers (`StatusName`, `ReasonName`, `Name<T>`, `Source(LimitSource)`) could become `JsonStringEnumConverter` with `[JsonStringEnumMemberName]` on the enums.
- **N11. Tests and analyzers.**
  - Group the 74 test files into folders (Domain, Application, Api, Persistence, Venues) and add `[Trait]` categories, so a fast unit subset can run with `--filter`.
  - Consider `<AnalysisLevel>latest-recommended</AnalysisLevel>` and `EnforceCodeStyleInBuild`; `.editorconfig` has no C# rules yet.
  - *Status (October 17, 2026):* partly done. Ten selected CA rules are warnings for production code and migrations are generated code; test folders, traits and a broader analysis level are not done.
- **N12. Evidence cleanup.** A failed delete or upload leaves an unreferenced file, which is harmless by design, but nothing ever removes them. Add a maintenance command that compares the storage keys with `play_evidence.ObjectKey`.
- **N13. Overview consistency.** `OverviewSummaryService` reads accounts and snapshots through `WorkspaceService`, then reads facts again in a separate repeatable-read transaction, so one response is not one snapshot. `FillDto.PlayId` is always null in Overview and account fills even though links exist; the contract states this, so revisit it if the UI would benefit.
- **N14. HTTP status consistency.** `POST /api/plays/retrospective` returns 200 where `POST /api/plays` returns 201. "Venue reader is unavailable" is a 503 in `SyncAsync` but a 502 in `InstrumentsAsync` and `CandleService`.

---

## Possible refactors

Each of these needs a short design note and owner agreement first; the monitor, notification and broker designs in particular are to be shaped before any code.

- **P1. One refresh pipeline for both browser and monitor.**
  - Build R1's read-then-commit refresh as one application service: sync, retain orders, reconcile.
  - A future `IHostedService` monitor (play-lifecycle step 4) calls it on a schedule, and the 60-second browser polling can be retired.
  - This is the natural place to decide per-account scheduling, backoff and the "only one read in flight" policy.
- **P2. One execution-fact model.**
  - `ManualLedger` projects owner facts into fake `ImportedFill` objects (`OrderId = "manual:{id}"`, `FeeBasis = "unknown"`) to reuse the outcome calculation.
  - An `ExecutionFact` value (role, side, effect, quantity, price, fee, PnL, bases, source) consumed by `PlayOutcomeCalculator`, `ExecutionIntegrity` and Review would make both sources honest inputs.
- **P3. A typed, cached plan.**
  - The plan is `jsonb` text parsed by `PlayDocuments.Read` in 16 places, some inside loops. Its numbers are strings that are parsed again in the matcher, outcome calculation and Review.
  - Parse once per Play into a validated `ResolvedPlan` with decimal levels, using a lazy property or EF's JSON column mapping, and keep the exact text only for display and revisions.
- **P4. Richer fact and diary entities.**
  - `ManualExecution`, `ManualExecutionHistory`, `ImportedOrder`, `AccountCredential` and `DiaryRequest` have public setters; `DiaryService` edits fields directly when correcting a record.
  - Give them constructors and methods (`ManualExecution.Correct(...)`) with private setters, as `Play` and `Account` already have.
- **P5. Feature-sliced persistence.**
  - Mirror the Application folders in Persistence: per feature, its entity configurations and its store.
  - Together with S6, split "Workspace" into Accounts, Portfolios, Refresh and Overview.
- **P6. Derived reporting read models.**
  - Activity operations, Overview summaries and Review results are recomputed from all facts on every request.
  - When S9's measurements require it, cache them keyed by a data version (latest sync time, link and Play versions), or materialize operations at refresh time.
- **P7. A generated API contract for the frontend.**
  - The frontend hand-writes runtime guards (`isPlayExecution` and others) that mirror the C# DTOs.
  - Generating TypeScript types or validators from the OpenAPI document in CI would catch contract drift. This needs the frontend owner's agreement.
- **P8. Basic operational telemetry.**
  - Only unexpected failures are logged today.
  - Metrics for venue latency and failures, lock-wait time and refresh duration (OpenTelemetry, opt-in) would make R1 and S9 measurable on a real installation.

---

## Suggested order

| Step | Content | Why first |
| --- | --- | --- |
| 1 | R2 + S12 + N3 | Cheap, protects owner isolation, no behaviour change. |
| 2 | R3 (README rewrite) | Onboarding; documentation only. |
| 3 | S3, S4, S5, S10, S11, N1, N4 | Composition and transport cleanup, mostly mechanical, covered by the existing API tests. |
| 4 | Design note, then R1 + S2 (with P1 in mind) | Changes locking semantics; agree with the owner, then rely on the PostgreSQL concurrency tests. |
| 5 | S1 (+ N2) | One place for exact numbers, with table-driven tests. |
| 6 | S6, S7, S8, incrementally | Splitting the workspace module and typing statuses, a module at a time. |
| 7 | S9 measurements | Before any history backfill or archive work. |
