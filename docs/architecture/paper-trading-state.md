# Paper trading delivery state

October 20, 2026. **PR remains draft: subsequent Claude Opus reviews found
confirmed simulation/recovery blockers. Earlier integration approval is
superseded pending corrections and fresh review.**
Authority: [delivery contract](paper-trading-delivery-contract.md) and
[owner-refined plan](paper-trading-plan.md).

## Work

- Shared wire contract recorded before parallel implementation.
- Backend implementation delegated to Sol/xhigh (`paper-backend-implementation-20261020-r1`):
  foundation, engine and reporting subtasks, with saved-Play integration owned
  by that backend implementer. Shared write scope: `backend/` only.
- Frontend implementation delegated to Sol/xhigh (`paper-frontend-implementation-20261020-r1`):
  new paper API/settings/execution components in a disjoint child task. Shared
  write scope: `frontend/` only.
- Parent owns docs, scripts/deployment integration, integration checks and
  independent review coordination.
- Independent Sol backend reviews completed three rounds. The first round's
  three findings are independently verified fixed; two new round-two findings
  are independently verified fixed in round three (below).
- Independent Sol frontend re-review gives bounded approval of the frozen
  implementation, including both original findings and history provenance.
- Frontend complete-tree tests and parent synthetic browser verification passed.
  Packaging re-review gives bounded approval. Final backend complete-tree tests
  and independent backend round three passed. Parent integration sign-off is
  recorded below, with explicit verification limits.
- Disable semantics coordinated: UI-confirmed settings suspension, no
  finish-every-Play 409, held reservations, atomic settings/evaluation fencing,
  retained history and a disabled-period gap on fresh-boundary resumption.
  Backend and frontend implementations and focused regressions are present.

The initial implementation handoff performed no owner's database migration,
deployment, release, commit, push or pull request. The subsequent owner-authorized
PR and Claude review follow-up is tracked below.

## Isolated environment

- Local SDK `/home/ops/.dotnet/dotnet`, version 10.0.302, matches `global.json`.
  The system CLI only has 10.0.112 and does not satisfy the SDK pin.
- Dedicated disposable PostgreSQL 18 container `vessel-paper-test-pg-20261020`,
  loopback-only, separate from existing containers and the owner's `.env`.
  Tests create/drop unique schemas in this synthetic database.
- Dedicated disposable RabbitMQ `vessel-paper-test-rmq-20261020`, loopback-only,
  bounds Erlang schedulers/memory and is used only for regression tests of the
  unchanged transport-backed modules. Paper itself has no broker dependency.
- No baseline or application test pass is claimed by environment preparation.

## Parent integration checks

- `python3 -m unittest discover -s scripts -p 'test_*.py'`: **82 passed** at the
  latest integration checkpoint (optional paper dev runner, independent module
  environment, standalone heartbeat and Compose-model tests included).
- These script checks do not establish a backend/frontend feature pass.
- Optional `Vessel_DEV_PAPER=1` development worker and additive
  `deploy/compose.paper.yaml --profile paper` keep existing deployment module
  sets unchanged. New worker has no broker/credential/evidence dependency.
- Paper execution facts retain immutable simulation `ModelVersion = 1`.
  The same unshipped foundation migration includes its default and database
  edit refusal; no extra DTO field is required.

## Independent review findings, early round

Sol review `paper-boundary-review-20261020-r1`, snapshot October 20 at
16:03:22 UTC, found three actionable issues in the changing implementation:

1. **P1, mixed-case route bypass:** the HTTP router is case-insensitive but the
   ID/mode middleware compared path segments case-sensitively. The review's
   disposable ASP.NET host reproduced cross-mode Play access/write handlers
   through mixed-case paths. Owner filtering was not bypassed.
2. **P2, torn paper balance read:** separate execution/reservation/ledger reads
   could report an impossible available balance across a concurrent entry
   commit. Reporting needs a single snapshot, not merely an existing
   read-committed transaction.
3. **P2, paper lifecycle event contamination:** owner planning/pause/cancel
   changes entered the existing event mutation capture without paper provenance.
   Deferred paper-event delivery requires skipping all non-real Plays, not
   just changes whose source is Paper. No popup leak was claimed.

All three are **independently verified fixed** in round two:
case-normalized resource filters protect mixed-case paths, paper balances use
one owner-filtered SQL statement, and event capture skips every non-real Play.
The real Activity wire version also stays unchanged across the tested paper
account mutation. Artifacts: `/tmp/vessel-paper-review-AYyW5m/` and
`/tmp/vessel-paper-review-r2-w7pi3D/`.

## Backend review round two: corrected integration blockers

Independent review `paper-backend-independent-review-20261020-r2` reproduced:

1. **P1, actual retrospective mode bypass:** paper-mode retrospective preview
   and creation accepted a real account, and creation wrote a real Closed Play
   plus two actual order links. Both retrospective routes require normalized
   actual-only rejection before diary reads/writes. A disposable PostgreSQL
   regression must assert unchanged real mutation counts and preserve Real use.
2. **P2, cross-Play account chronology:** evaluating each Play's entire window
   let one Play's later loss reject a sibling's earlier reserved entry. With
   cash 4,000, both minute-zero entries were valid; whole-Play iteration order
   changed whether the second entry happened. The corrective work must evaluate
   a complete account timeline in candle chronology, including other symbols
   and not-yet-due active siblings, with common pre-minute capital and atomic
   account-level validation/commit. Sorting Play IDs is not a fix.

The corrected engine is frozen: **122/122 focused tests passed, zero skips**,
including the exact original order probe in both wrapper orders, cross-symbol
chronology, genuinely earlier losses, fresh staggered arming, stale account
sets/settings/revisions, incomplete windows, retained future closed facts and
whole-account progress beyond the scheduler's soft batch cap. Both retrospective
guards and their mixed-case/unchanged-real-state/Real-control regressions are
implemented. Independent backend round three verified both original repros
against its isolated snapshot and approved the bounded backend scope.
Round-two artifacts include the executable adversarial probes at
`/tmp/vessel-paper-review-r2-w7pi3D/`.

The broader baseline run also caught an actual compatibility regression:
including mutable `Play.AccountId` in an EF principal alternate key prevented
moving an existing real Draft. Foundation corrected EF key mapping to use
only immutable owner/Play ID/mode while retaining the stronger database-only
account/mode uniqueness and foreign-key constraints. Existing real Draft-move
concurrency tests and the new pre-arm paper move/direct-SQL tuple-refusal tests
pass in the corrected focused suite; no new table or column was needed.

### Backend regression evidence before the final corrections

- Unified build: zero warnings/errors.
- Focused compatibility and feature matrix: **371 passed, zero failed/skipped**,
  including body limits, evidence, diagnostic errors, mixed-case boundaries,
  owner SQL inventory, event suppression and the then-current paper tests.
- An earlier unbounded full run crashed after 1,478 passes, 53 failures and
  three skips: **not a pass**. Pre-binding boundary regressions and historical
  schema fixtures were corrected and rerun in the focused matrix.
- First complete bounded baseline: **3,693 passed, eight failed, 17 skipped**
  (3,718 expanded cases), no host crash. The eight failures identified two
  Draft-move mapping regressions and six historical schema/wire/inventory
  assertions. These were corrected, not waived.
- Final corrected concentrated suite: **246 passed, zero failed/skipped**,
  including all paper tests, those baseline failures, Token/Wallet owner-route
  isolation and pending-model checks. Build has zero warnings/errors.
  Log: `/tmp/vessel-paper-frozen-focused-final.log`.
- Final full discovery: 226 classes / 3,629 displayed entries. All four
  class-disjoint batches finished at two xUnit threads against frozen source and
  dedicated PostgreSQL/broker: **3,733 passed, zero failed, five skipped**,
  3,738 unique expanded test IDs. Parent independently parsed the TRX outcomes,
  checked no cross-batch duplicates and verified unchanged production hashes.
  All 12 broker-backed facts passed, including real-monitor/host transport E2E.
  This final result supersedes the earlier failing baseline.
- Production-source manifest:
  `/tmp/vessel-paper-frozen-production.sha256`, aggregate SHA-256
  `9cf976a72a2f6062bfe31da64ab20e780c9460c13e5806dd911ce37af5bbaf43`.

| Final batch | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| 1 | 921 | 0 | 0 |
| 2 | 961 | 0 | 3 |
| 3 | 934 | 0 | 1 |
| 4 | 917 | 0 | 1 |
| Total | **3,733** | **0** | **5** |

The five skips are not passes:

- `LighterLiveTests.Public_catalogue_empty_system_account_and_three_candles_parse`
- `LighterLiveTests.Robinhood_public_catalogue_system_account_and_candles_parse`
- `RisexLiveTests.Live_account_orders_and_market_data_parse_under_the_strict_rules`
- `S3EvidenceCopyTests.Actual_cli_copies_database_originals_without_changing_sources_or_metadata`
- `S3IntegrationTests.Rustfs_round_trip_create_only_races_private_reads_and_deletes`

The live venue checks and isolated RustFS/S3 integrations were not configured.
Logs: `/tmp/vessel-paper-frozen-batch-{1..4}.log`; TRX:
`/tmp/vessel-paper-frozen-batch-N-results/batch-N.trx`.
Parent reconciliation: `/tmp/vessel-paper-final-backend-reconciliation.json`.
Skip counts come from `UnitTestResult` outcome rows; this runner's TRX
`Counters.notExecuted` misleadingly reports zero despite five skipped rows.
Implementation summary: `/tmp/vessel-paper-frozen-summary.txt`.
Full backend path inventory: `/tmp/vessel-paper-backend-paths.txt`
(84 paths: 64 production/migration and 20 tests).

## Independent backend round three

**Bounded backend approval: no remaining P1/P2 findings identified.** All prior
objections were independently closed, including the exact retrospective and
whole-Play-order reproductions, real/paper Draft moves, strong database tuple
guards and immutable model version. Ten additional independent probes passed:
cross-symbol protective exits/gaps, same-minute capital, two concurrent account
evaluations committing once, stale sibling frontiers and foreign-owner
candidates, and all 203 siblings across the 200-row keyset boundary.

The review retains its non-clean invocations rather than replacing their history:

| Independent invocation | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Original captured snapshot | 273 | 1 | 5 |
| Expanded matrix with test-only setup correction/probes | 296 | 1 | 0 |
| Unchanged worker rerun plus additional regressions | 128 | 0 | 0 |

The original failure was the disclosed synthetic owner's early-startup fixture
configuration, corrected only in `/tmp` to match the checked-in fixture fix.
The expanded run expected two worker public reads but saw zero; it passed
originally and passed unchanged on rerun. Shared database-wide advisory lock
contention is consistent with the failure but was **not directly traced**.
The last two TRXs contain 418 distinct test IDs with latest outcomes PASS,
including that disclosed rerun; this is not an independently clean full-suite
run. The separate frozen shared-tree full suite passed 3,733 with five skips.

Both isolated builds had zero warnings/errors. All 366 production files still
matched the shared checkout exactly at review end and parent final reconciliation.
Report, commands, original failures, probes and TRXs:
`/tmp/vessel-paper-review-r3-94S9Uu/REVIEW.md`.
Original 606-file manifest SHA-256:
`2439af1353fe961f55ed15d0e22adad4b5266dfa28532b06a80866f3f64a4738`;
final verification manifest with fixture/probes:
`ffccb1f5f962a6068c5c8783e871ebdf3d8e24e828857968bd55b36cae746799`.

## Packaging review

Independent packaging round one found one **P2**: the Compose paper service
passed an empty Identity origin in Token mode, which the host correctly rejects
even outside Wallet mode. Parent fixed the overlay to leave an unset/blank
origin unassigned rather than pass an empty value; explicit Wallet origins and
the identity directory remain intact. The new unset/blank Token regression
failed before the fix and passes afterward; all 82 root tests pass.

Compose's resolved JSON model uses null for the absent pass-through variable,
and Docker's `Config.Env` can retain a no-equals unset sentinel. An isolated
inert shell probe confirms the **actual process environment** has no origin;
no PostgreSQL service was started. Probe/container/volume/network were removed.
Artifact: `/tmp/vessel-paper-token-container-env-check.json`.
Independent packaging round two gives **bounded approval: no remaining P1/P2
packaging findings**. The Token-origin finding is independently resolved.
Checks passed: 11 focused unit tests, 11 synthetic Compose configuration cases
and 18 heartbeat cases. Both Wallet overlay orders, direct-origin inheritance/
explicit-alias precedence and unchanged existing service definitions were checked.
Review artifacts: `/tmp/vessel-paper-packaging-round2-4k6sVUFH/`; final manifest
SHA-256 `93677177b15df20dd1776bb86df53da932c97fe01e5aff62332f66a6ce8b64cd`.
Only this state document changed during review; manifested implementation files
stayed stable. This is packaging approval, not deployment authorization.

## Standalone host smoke

The parent explicitly applied journal migrations through
`20261020155750_PaperTradingFoundation` to separate synthetic database
`paper_smoke` in the disposable container, using the pinned EF tool 10.0.10.
The Debug worker ran in Production with only the paper module, owner and DB,
without a bearer or broker configuration:

- Productive heartbeat appeared; `deploy/worker-health` accepted it.
- Renaming the empty synthetic `paper_settings` table stopped heartbeat
  refreshes, rather than claim a healthy failed scan.
- Restoring the table resumed the heartbeat without restarting the process.

This proves standalone host startup/health/failure/recovery, **not** candle
execution or end-to-end UI behavior. No active paper accounts existed in this
smoke database. Local logs: `/tmp/vessel-paper-host-smoke.log` and
`/tmp/vessel-paper-smoke-migrate.log`.
This first smoke preceded the final `ModelVersion` migration addition and has
now been superseded by the final-schema repeat below.

### Final schema and Compose-derived Token host repeat

After the engine/mapping freeze, the parent recreated only its disposable
`paper_smoke` database and explicitly applied the compiled migration with
`dotnet ef database update --project src/Vessel.Persistence --no-build`.
The final migration includes the immutable EF/DB key correction and ModelVersion.

The compiled Production standalone host ran from a private temporary content
root with the corrected Compose-derived Token environment, without any Identity
origin, broker, bearer, credentials or evidence settings. Four checks passed:

1. The host starts and its productive heartbeat passes `worker-health`.
2. Renaming the empty synthetic settings table stops heartbeat refreshes; health
   becomes unhealthy while the host remains running.
3. Restoring the table recovers the productive heartbeat without restarting.
4. The final migration is recorded, ModelVersion defaults to 1, and no messaging
   provisioning/envelope schema exists in the database.

The host was stopped and its temporary files removed. This remains host/schema/
health verification, **not** execution or API/browser E2E. Logs/results:
`/tmp/vessel-paper-final-smoke-migrate.log`,
`/tmp/vessel-paper-final-host-smoke.log`,
`/tmp/vessel-paper-final-host-smoke-results.json`.

## Frontend verification

Independent Sol review `paper-frontend-independent-review-20261020-r1` found
two P2 issues: paper portfolio removal attempted forbidden unassignment after
partial writes, and the paper API deny-list missed `/market-stream`.
Both were fixed and independently verified in round two:

- Existing paper members are locked with a paper-to-paper move hint. Retained
  removal intent is rejected before any write, including removal plus addition.
  Real revision-checked unassignment is unchanged.
- Direct paper `marketStream()` refuses locally with zero fetches.
- The five-file history follow-up preserves explicit `source: "paper"`.
  Real/default rejects it; Paper permits Owner/Paper but rejects Venue/Manual.
  History labels the source “from simulated paper executions.”

Round-two verdict: **bounded frontend approval, no remaining P1/P2 findings**
in its final frozen snapshot. Independent checks: history/API **42/42**,
temporary adversarial probes **8/8**, focused paper tests **62/62**, and
typecheck PASS. These overlapping counts are not a full-suite total.
One initial one-second async query timed out; its unchanged file then passed
8/8 with one worker and normal checked-in timeouts.

Implementation checks: typecheck, lint, production build and whitespace PASS.
**Parent final complete-tree suite: 154 files / 3,831 tests PASS**, normal
checked-in timeouts, two workers, 204.17 seconds, after both review fixes and the
history follow-up:

```sh
pnpm --filter vessel-frontend test --maxWorkers=2
```

Log: `/tmp/vessel-paper-frontend-final.log`, exit status zero. Earlier evidence:
3,818 tests passed before the narrow corrections; an optional repeated run hit
five load/timing failures, whose unchanged files then passed 255 tests at two
workers. The complete corrected-tree result above is the final frontend suite.

Independent artifacts:
`/tmp/vessel-paper-frontend-review-round2-20261020T162935Z/`.
All 438 final frontend file hashes matched after review, including 46 dirty
paths; final manifest SHA-256:
`a9c6be5f738c248b74ad19be9d570160451a454c051e1cbbe82fd118af4e5e35`.

## Accepted bounded v1 limits

- No gap/ambiguity resolution API or controls. Unresolved holds can persist
  indefinitely and affected results remain unscored.
- No manual simulated-close route; planned exits are the closing mechanism.
- Flat, resolved account equity is known cash. Open/unresolved virtual equity
  remains null/unknown, never zero or a stale candle-close “live” valuation.
- The existing owner risk preference is read-only in Paper. Paper outcome
  records and the ladder are isolated; no new per-mode risk setting is added.
- No paper events, notification fanout, actual snapshots/imports/orders or
  continuous market stream. The execution panel reads stored state; Reload
  does not run a simulation check.

## Parent synthetic browser verification

The collaborative preview could not open: its final error was
`Preview automation open timed out after 660000ms`. Verification used the
repository's externally supplied Playwright workflow with an isolated browser,
loopback Vite and an in-memory synthetic API. Actual production React components,
styles and chart renderer were mounted; this is **UI-only, not backend E2E**.
All non-loopback requests were blocked. No owner browser profile, credentials,
data or venue API was used.

Nine checks passed, with zero page errors, external requests or paper-mode
actual-monitor/sync/credential/order/market-stream calls:

1. Disabled paper history stays accessible; real accounts and Refresh all stay
   out of the Paper workspace.
2. Settings defaults are false/60 and save an independent 120-minute interval.
3. Cancelling the disable confirmation does not save; confirming suspends it.
4. Account creation defaults to 4,000; a second account accepts 6,000 without
   a wallet field or initial real refresh.
5. Account management has no editable starting-capital input.
6. A saved paper Play uses the existing editor/chart and stored execution panel,
   including the next scheduled check.
7. The 390-pixel Play layout has no horizontal document overflow.
8. Paper Settings renders under the built-in light theme.
9. No forbidden actual API calls, page errors or external requests occur.

The desktop/mobile and dark/light captures were visually inspected. The chart
uses explicitly labelled synthetic verification candles, never production sample
data or a fallback. Harness, results and captures:
`/tmp/vessel-paper-ui/{fixture.tsx,smoke.cjs,results.json,paper-account-create.png,paper-play-desktop.png,paper-play-mobile.png,paper-settings-light.png}`.
Earlier harness failures (missing Tailwind source discovery and wrong dialog/
tab locators) are not counted as application passes or
production defects. The final complete harness run passed all nine checks.

## Initial parent integration sign-off (superseded by Opus findings)

The bounded first delivery is verified against the recorded contract and
accepted limits: independent Sol backend/frontend/packaging approvals, complete
frozen-tree backend and frontend regressions, root checks, final explicit
synthetic migration/standalone host health and synthetic production-component
browser checks. Production/backend and reviewed frontend manifests still match.

This is **not** a real-venue execution test, a combined browser-to-API E2E pass,
or live installation/deployment approval. Five external/S3 backend tests remain
skipped and the independent review's rerun history is retained above.
The feature remains off by default. An operator must deliberately apply the
new migration and run the standalone paper worker in the target installation;
this work did neither on the owner's system.

At the initial handoff, no commit, push, pull request, release/version change,
publication or owner-data migration had been performed.
Both task-owned disposable PostgreSQL/RabbitMQ containers and their anonymous
volumes were removed after all implementations/reviews completed; no pre-existing
container was changed. UI/worker smoke hosts were stopped. Logs, manifests,
TRXs, runnable review snapshots and synthetic captures remain under `/tmp`.
Cleanup record: `/tmp/vessel-paper-disposable-cleanup.json`.

## Owner-authorized PR and Claude review follow-up

October 20, 2026: the owner requested a pull request and an independent Claude
Opus evaluation of the implementation. The verified feature was committed as
`54be38ea5b0db0d6bff70e3e76a226733da8b9b7` and pushed on
`t3/paper-trading-portfolio-concept`. Draft **`sloppitty/vessel#32`** targets
`main` and is registered with this T3 thread.

Two separate app-owned review requests selected provider `claudeAgent`, model
`claude-opus-5-5`, effort `max`. Both failed before code evaluation with
`Claude API rate limit reached. Try again later.` No Claude findings or approval
were obtained; the service failure is not a completed code review. No further
automatic retries are running.

Keep the PR draft until the requested Opus evaluation can complete and any
actionable findings are addressed. The recorded Sol approvals and frozen-tree
test results above remain the implementation baseline. PR creation does not
authorize merging, deployment, release or owner-data migration.

### Delayed Opus completion: changes requested

The initially rate-limited task resumed and completed an independent review of
`54be38e`. The duplicate retry was stopped; a fresh review will follow fixes.
The previous statement that no evaluation occurred describes the earlier
handoff, not the current state. Report and runnable probes:
`/tmp/vessel-paper-opus-eYeSX3/REPORT.md`.

**Not ready to leave draft.** The review reproduced:

- **P1:** ordinary non-terminating sizing/margin/P&L precision can overflow
  decimal conversions, break paper financial reads/arming, freeze siblings and
  poison the shared scheduler across owners. A final-exit rounding residue can
  also leave a phantom position/zero-margin fact rejected by PostgreSQL.
- **Conditional P1:** an absent minute is treated as a permanent gap. Whether
  native Hyperliquid omits zero-trade minutes needs recorded primary-source
  verification; do not guess that absence proves either coverage or a touch.
- **P2:** declining an entry due to capital/ambiguity freezes already-open
  tranches' protective exits and cascades to siblings.
- **P2:** global/account disable has no genuine re-enable transition, making
  ordinary suspension an irreversible account-wide hold.
- **P3:** unchanged protective levels lose jump detection across a revision;
  just-closed candles lack a settlement delay; terminal state keeps polling.

Existing paper tests passed 162/162 as the review's control, demonstrating that
the earlier tests did not cover these cases. No owner or real-account data leak
was found. Corrections are in progress with dedicated disposable PostgreSQL and
new regression cases; earlier full-suite counts are not proof of those fixes.

### Corrective work and native candle evidence

- Core corrections are now frozen: explicit 12-place fact quantization,
  exact account sums/comparisons, exact final-exit remainders, per-entry decline
  receipts and reversible prospective suspension with retained unscored history.
  The unshipped foundation schema gains decline/suspension JSON records and an
  internal previous-close timestamp; no live database has been changed.
  Arm/revision preflight rejects unsupported tiny allocations, including
  retained-position exit revisions, before changing facts. Database constraints
  also reject unsupported fact precision, bounds and nonrepresentable prices.
  Exact aggregate-capital tests exceed `decimal.MaxValue` without converting
  back to `decimal`.
- Additive state wire: `blocked` / `suspended`, `declinedEntries`, `suspensions`
  and `hasUnscoredHistory`. The frontend correction passed 107 focused tests,
  typecheck/lint/build. Parent complete corrected frontend suite: **154 files /
  3,850 tests passed**, normal timeouts, two workers, 159.92 seconds
  (`/tmp/vessel-paper-opus-frontend-full.log`).
  Parent synthetic production-component UI repeat: **12 checks passed**,
  including distinct blocked/suspended labels, decline receipts, resumed
  unscored history, 390-pixel layout and light theme; zero external requests,
  page errors or forbidden actual API calls. This remains UI-only, not backend
  E2E. Captures were visually inspected; its isolated server was stopped.
  Harness/results: `/home/ops/.cache/vessel-verification/paper-opus-ui-20261020/`.
  The frozen integrated Paper run passed **217/217, zero failed/skipped**,
  including the parent's final 25 worker cases. A clean `Rebuild` passed with
  zero warnings/errors. Artifacts:
  `/home/ops/.cache/vessel-verification/core-clean-build.log`,
  `core-paper-tests-final.log` and
  `core-test-results/paper-core-integration-final.trx`.
  The complete backend suite, final-schema host repeat and fresh independent
  approval remain separate pending gates.
- Parent worker tests reproduced both starvation/heartbeat and just-closed
  candle bugs before fixes (two failures), then passed 17 worker checks in an
  isolated baseline-plus-worker snapshot. Explicit trade-count validation
  reproduced three more failures, then all 21 worker checks passed there.
  The interrupted duplicate Opus review also exposed a shared-availability
  defect: ordinary candle read failures set the real venue's server-wide
  deadline and stopped other symbol reads. Three regression cases failed before
  the correction; an actual rate-limit control already passed. Only a genuine
  venue rate limit now sets that shared deadline. Other read failures retain
  account-local retry state and do not stop unrelated symbols.
  The final isolated worker run passed **25/25, zero skips**, including the
  genuine rate-limit control. This is not a full integrated-core pass; that run
  must follow the core freeze.
  Logs: `/tmp/vessel-paper-worker-{red,green,native-green}.log`.
  Additional evidence: `/tmp/vessel-paper-worker-deadline-red.log`,
  `/tmp/vessel-paper-worker-final-green.log` and corresponding TRXs.
- Native public evidence did **not** establish ordinary zero-trade omissions:
  two quiet Hyperliquid markets returned all 360 requested minute slots,
  including 343 explicit zero-trade candles. Thus Opus F2 remains conditional,
  not a confirmed quiet-market outage. The source does not guarantee universal
  completeness; an absent slot is still unknown and is never silently skipped.
  Native zero-trade carried prices must not fabricate fresh touches.
  See [the research note](paper-candle-coverage-research.md) and exact seven-read
  artifacts under `/tmp/vessel-paper-candle-research-N1XmIP/`.
- Worker correction uses a ten-second candle-settlement lag and contains
  account-local arithmetic/validation failures with a fenced retry in a fresh
  scope. Shared schema/connection failures remain unhealthy, not silently
  swallowed. Native trade counts are retained; null/negative counts or
  contradictory zero-trade rows are not treated as complete empty observations.
  The caught-up scheduling predicate shares the settled frontier, so the delay
  does not accidentally turn hourly checks into perpetual one-minute catch-up.
- Parent root checks remain **82/82 passed**. No corrective code commit or
  readiness claim preceded the stable core snapshot. Corrective changes will
  be pushed to the draft PR for a fresh fixed-commit Opus review; earlier Sol
  approvals and pre-correction complete-suite counts do not approve this tree.
- Additional interrupted-review probes reproduced a protective-revision
  admission defect: unrelated sibling gaps or an own transient read issue
  prevented tightening an already-open position's stop despite requiring no
  additional capital. The correction must preserve known protective edits
  without allowing new arming, pending quantity/margin increases, a raised
  reservation or bypass of the edited Play's terminal gap/ambiguity. Persisted
  revision/effective-time fencing and transient issues must remain intact.
  Both sibling-gap and own-read-issue cases failed before correction and pass
  in the frozen integrated run, including the later tightened-stop execution
  and refusal controls. That review's probes are retained under
  `/tmp/vessel-paper-opus-r2b-8gMuNE/`.
- Suspension admission is exposure-specific: only still-open facts predating a
  suspension retain that unknown-exposure hold. A new, known entry after an
  unfilled prospective resume does not poison the account. Historical
  suspension still excludes the affected outcome from Review and sizing.

The parent is running the complete frozen backend inventory in four disjoint
class batches (at most two testhosts concurrently, two xUnit threads each),
with a new task-owned PostgreSQL/RabbitMQ pair. Separate synthetic databases
isolate standalone-schema smoke and the next Opus reviewer from those tests.
Evidence root:
`/home/ops/.cache/vessel-verification/paper-corrective-final/`.
The frozen combined backend/frontend production manifest has SHA-256
`e0ad24699b2cac87edcd79f5dfda800c1338874e388ad14e7704867a33744b3b`.
No corrected-tree complete-suite or independent review pass is claimed yet.

The first complete corrective run exposed a stale SQL-audit inventory and one
worker case expecting two reads/executions but observing zero during parallel
tests. The new exact-capital/suspension SQL was reviewed for explicit
owner/account parameters and correlated owner/Play facts, and its three
statement hashes were updated rather than exempting a file. Worker tests now
use a nonparallel collection because real monitor tests share their
database-wide venue advisory key even across synthetic schemas; that specific
failed invocation's cause was not traced. Its failure is retained, not counted
as a pass. These are test-only follow-ups; production remains byte-identical to
the frozen manifest. A rebuilt audit/worker run and the affected complete batch
must pass before handoff; final batches use separate synthetic databases to
avoid cross-testhost advisory-lock contention.

### Corrective full-suite and upstream integration checkpoints

The final rebuilt `31e565c` run passed **3,776 backend tests, zero failed,
five skipped**, across 226 classes in four disjoint batches. All 12 explicit
broker-backed cases passed; no test IDs occur across batches. Production
matched its frozen manifest throughout. The initial invocation remains under
`paper-corrective-final/initial-run/`, including both test failures and the
parent's shell-wrapper error after two completed testhosts. It is not
substituted for the complete successful rerun.

Meanwhile, `main` advanced to `cda3a4b` with imported-position Review (PR33).
A Sol implementation sidecar resolved seven conflicts in private merge
`e4495a09ead0fc2d074e8616514798baa1006789`, preserving real position eligibility,
weighting and browser preferences while forcing Paper Review to `exclude`
before any actual-position source read. Paper codecs reject position rows
and nonzero position aggregates; the imported-position controls/preferences
are hidden in Paper. The sidecar's red-before-green regressions and bounded
handoff are in
`/home/ops/.cache/vessel-verification/paper-main-merge-20261020-4bd14067/REPORT.md`.

Parent full verification of that exact merge passed **3,799 backend tests,
zero failed, five skipped** across 228 classes, including all 12 broker facts.
Frontend passed **157 files / 3,889 tests**, typecheck, lint and production
build. The backend clean rebuild had zero warnings/errors; source manifests
remained unchanged and TRXs have no cross-batch duplicate test IDs. The five
skips are still two Lighter live, one RISEx live and two isolated S3 checks.
Evidence: `/home/ops/.cache/vessel-verification/paper-integrated-final/`.
The parent fast-forwarded to the tested merge only after Opus finished its
older fixed-commit review; these results do not close the new findings below.

### Opus corrective round three: additional changes requested

Fresh Claude Opus/max reviewed `31e565c` read-only. Its exact export and the
shared checkout matched all 2,469 tracked files and the 790-file production
manifest at both ends. **No outright P1 or owner/real-data leak was confirmed,
but readiness remains withheld for three reproduced P2s:**

1. A transient candle-read issue survives pause/cancel on an unfilled Play and
   permanently poisons account admission/balances, even after other work
   recovers. Temporary issues must not become unresolved historical exposure.
2. A persistently failing multi-symbol account anchors the global shared
   candle window. Once sufficiently behind, healthy owners are silently
   skipped because the shared window ends before their checkpoint. Deterministic
   source failures also retry indefinitely without an explicit terminal state.
3. A missing newest candle becomes a permanent gap immediately. Recent
   not-yet-published trailing coverage needs bounded recovery without crossing
   missing evidence; genuine old/interior holes remain conservative gaps.
   Quiet-minute publication latency is unverified, so the conditional P1
   premise is not presented as established venue behavior.

Related P3s: edited already-executed target shares incorrectly alter later
exact-remainder allocation; a Closed `ready` Play without a next check keeps
polling; disable copy omits the post-resume open-exposure admission hold; and
explicit rearming after pausing a suspension leaves an open suspension receipt.
Backend corrections and frontend polling/disclosure regressions are in progress
with disjoint write scopes. No new reset, gap-resolution or actual execution
capability is authorized.

Earlier precision/entry-decline/suspension/protective-revision/settlement
objections are independently closed in their named paths. Opus also ran 4,000
random plans (2,763 passed preflight) without precision violations. Its own
failed hypotheses/assertion and generator errors remain recorded. Independent
runs included 217 control tests, 21 final probes and 399 real-mode regressions;
one frontend one-second timeout passed unchanged on repeated one-worker runs.
These bounded results are not a clean full-suite claim or overall approval.
Report, original iterations and runnable probes:
`/home/ops/.cache/vessel-verification/opus-r3-31e565c/REPORT.md`.

Parent frontend follow-up is frozen: automatic observations stop for every
state without a next check, including Closed `ready` and `blocked` states;
manual stored-state Reload still works. Global/account disable copy explicitly
discloses the continuing pre-suspension exposure admission hold after re-enable,
declined hold-period entries and retained unscored history. Three polling cases
and both disclosure checks failed before their fixes. The focused set passes
**71/71**, and the complete frontend suite passes **157 files / 3,892 tests**
with normal timeouts and two workers (170.45 seconds). Typecheck, lint, build
and scoped whitespace checks pass. Evidence:
`/home/ops/.cache/vessel-verification/paper-r3-ui/`.
Backend recovery/windowing corrections and another fresh review are still
pending; frontend verification does not close those P2s.

The accepted bounded B/C corrective policy is recorded in the delivery contract:
per-complete-account read windows, exact tick-local window sharing, five-minute
clock-based grace for a genuinely recent trailing missing suffix, and a
separate five-minute first-deterministic-failure recovery timer. Old/interior
missing history stays a gap. Transport/timeouts/429 are not deterministic source
rejections. Only affected-symbol simulations terminalize after deterministic
expiry, preserving known sibling protective eligibility and unresolved capital.
One internal nullable `SourceFailureSinceUtc` field is allowed in the same
unshipped foundation schema; no state DTO or endpoint is added. Implementation
and boundary regressions are in progress. These five-minute policies do not
assert venue publication/finality guarantees and do not authorize live sampling
or migration.

### Round-three corrections frozen for final verification

The backend implementer has handed the shared compiler back after a clean build
(zero warnings/errors), **35 focused tests**, **254 Paper/owner-audit tests**
and **1,049 selected compatibility tests**, all with zero failures/skips. These
sets overlap and are not a complete-suite total. Actual Hyperliquid adapter
classification, real Review/Play/isolation, migration and source-failure fences
are included in the compatibility evidence.

Implemented: safe nonterminal unexposed pause/cancel clears transient issues;
read windows are account-relative; deterministic source recovery has its
accepted persisted timer; recent trailing missing coverage can retry without
crossing evidence; target remainder decisions use retained execution activation
provenance rather than current edited shares; and explicit eligible rearm closes
the suspension receipt without erasing its unscored history. The source-failure
timer is internal only. No runtime SQL changed; the owner audit passes unchanged.

All red runs remain available, including two test-only assertion mistakes and
the older protective-gap fixture that used a recent trailing omission. That
fixture now uses an explicitly old missing minute, retaining its protective
revision/admission assertions; it did not require a production exception.
Handoff, exact 15 backend paths, commands, logs, TRXs and frozen source hashes:
`/home/ops/.cache/vessel-verification/paper-r3-backend-5jDuaKJ0/HANDOFF.md`.
The parent is now running the complete disjoint backend batches and the
final explicit synthetic schema/standalone-worker smoke under
`/home/ops/.cache/vessel-verification/paper-recovery-final/`.
No final complete-suite or fresh independent approval is claimed yet.

### Final recovery snapshot complete-suite checkpoint

Corrections were committed and pushed as
`9c66d65c7dfa1231d3c1b9201ce0cb3f5d7ba329`. The complete frozen backend run
passed **3,832 tests, zero failed, five skipped** (3,837 total), across all
231 discovered classes in four disjoint bounded batches:

| Batch | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| 1 | 963 | 0 | 1 |
| 2 | 790 | 0 | 2 |
| 3 | 841 | 0 | 1 |
| 4 | 1,238 | 0 | 1 |

All 12 explicit broker-backed facts passed. TRX reconciliation confirms no
cross-batch duplicate test IDs, all discovered classes covered, and unchanged
production source. The five skips remain two Lighter live, one RISEx live
and two isolated S3 checks; they are not passes. The frozen combined
backend/frontend production manifest SHA-256 is
`935f02bb72720fba88dad4c5489b4a9e7dd29f891b1b721b227621b3c7e27ad5`.
Exact totals, logs, TRXs and manifests:
`/home/ops/.cache/vessel-verification/paper-recovery-final/summary.json`.

The explicit final migration was applied only to separate synthetic database
`paper_recovery_smoke`. EF reports no pending model changes. Four Production
Token standalone-host checks passed: productive healthy heartbeat without
broker/bearer/origin, heartbeat expiry on missing schema, recovery after schema
restore without restarting, and final audit columns/ModelVersion 1 with no
messaging schema. The temporary host was stopped. This is schema/health smoke,
not active-candle execution or combined browser/API E2E.

Root checks pass **82/82**. The unchanged latest frontend remains **3,892/3,892**
with typecheck/lint/build passing. A fresh app-owned **Claude Opus/max round
four** is evaluating the exact `9c66d65` code, including the upstream Review
integration and all prior findings. **PR remains draft; no new independent
approval or final readiness is claimed while that review is running.**
