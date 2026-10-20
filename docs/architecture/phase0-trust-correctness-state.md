# Phase 0 trust and correctness: state

This records verified progress against the
[Phase 0 contract](phase0-trust-correctness-contract.md). An item is listed as
done only after its tests pass and its pull request is open, with the checks
that ran. Each item has its own section, so parallel pull requests edit
different lines.

## Contract

October 19, 2026:
- The contract was drafted from the October 18 review, its probes and the
  code.
- The owner then answered decisions 2, 6, 7 and 10 and the RISEx option.
  sloppitty/vessel PR #3 and PR #6 merged first.
- The toolchain was verified. .NET SDK 10.0.302, matching `global.json`,
  builds `backend/Vessel.slnx` in Release with 0 warnings. pnpm 12.8.1
  installs the workspace with `--frozen-lockfile`.
- No owner database was touched.

## 0.1 Sizing truth

October 19, 2026, branch `t3/phase0-sizing-truth`:
- A size line needs every part of the position under a priced stop
  (`stopCoverageGap`, the Summary's rule). Otherwise the block names the gap
  and offers no size or size-based leverage.
- `useSizing` reports loading, ready, stale or failed with Retry. Nothing
  offers Accept while stale or failed, including during a retry. A read that
  started before a risk change is ignored.
- The budget is the manual amount or the account's `availableBalanceUsd`. It
  is labelled with its source and snapshot time; a known zero stays zero. The
  capital context's "Available" shows the same figure (decision 6).
- The Summary divides risk by the account `balanceUsd`. The review's
  10,000/5,000 case shows 1.25%.
- Once an entry has a real fill, Size and Leverage are hidden. Stop and target
  suggestions stay only for unfilled entries.
- Copy reads as estimates. One over-budget message serves the editor and Save.
- Lighter liquidations, deleverages and settlements are counted by kind. The
  notice is one counted sentence that fits the 1,000-character column.
- Sizing thresholds, ladder, window and scratch rule are unchanged; the
  backend sizing code is untouched.
- Every review probe in the contract's 0.1 rows has a test that failed
  before the fix: 46 frontend and 4 backend failures. The review-fix commit
  added 11 tests that failed on the previous code.
- An independent review found no high-severity defect. Its two medium
  freshness defects and its low findings are fixed in the second commit.
- `scripts/check.sh` passed with PostgreSQL 18 and RabbitMQ 4.3.6 in
  disposable containers:
  - Python: 78 OK. Prototype tests pass.
  - Release build: 0 warnings.
  - Backend: 3,436 passed, 5 skipped (live venue and S3), 0 failed.
  - Frontend: typecheck and lint clean, 2,491 tests, build OK.
- Not yet done: a rendered browser check.

## 0.2 Exit-time ordering

Not started.

## 0.3 Result-basis receipt

Not started.

## 0.4 Monitor truth and visibility

October 19, 2026, 0.4a monitor visibility, branch
`t3/phase0-monitor-visibility`:
- One helper (`features/monitor/incidents.ts`) derives incidents from the
  stored `GET /api/monitor` summary: open records, and unverifiable records
  not dismissed since. Incidents map to Plays through `playId` or
  `details.playIds`. No new endpoint or polling.
- A severity-toned count shows on the Plays navigation entry, on Plays list
  rows and in the Play header, where it opens the Execution tab.
- Overview shows a conditional incident row above the metric tiles. It is not
  scoped by the Overview scope; the owner kept it unscoped on October 20.
- The monitor heading follows open incidents and never says "Monitored"
  beside an open record.
- Records show a human title, a fact table and one next action:
  - the fact table has open, stop and uncovered quantity (uncovered only for
    order coverage) and last observed;
  - the JSON moves behind "Recorded details".
- The discrepancy DTO adds `lastObservedAtUtc`. No migration.
- `scripts/check.sh` with PostgreSQL 18 and RabbitMQ 4.3.6 in disposable
  containers:
  - Python: 78 OK. Prototype tests pass.
  - Release build: 0 warnings.
  - Backend: 3,423 passed, 5 skipped and 10 failed, all PostgreSQL or HTTP
    timeouts while four agents ran PostgreSQL tests against the same
    server. The 10 passed when rerun alone.
  - Frontend, run separately because the script stops at the first failure:
    typecheck and lint clean, 2,481 tests, build OK.
- The rendered browser check is recorded under 0.4c below. The isolated
  full-stack e2e found one stale assertion in `browser-e2e.cjs`; it was
  fixed on this branch.

October 19, 2026, 0.4b Refresh continuity, branch `t3/phase0-refresh-continuity`:
- Every import, Refresh included, checks fill continuity against the later of
  the continuity watermark and the newest retained fill. The boundary is read
  under the account lock before the import.
- A Refresh records continuity in its snapshot-only unit:
  - a capped read whose oldest raw fill is newer than the boundary sets the
    gap;
  - otherwise the watermark advances.
- A Refresh never clears a gap, and records nothing when truncation is
  unknown. Run state, backoff, attempts, due time and credential revision
  stay check-only. No migration.
- `MonitorRefreshContinuityTests` (PostgreSQL) ports the probe's `Watermark`
  scenario and adds a capped-Refresh negative control, first-import and
  Refresh-scope cases. Before the fix, 7 of its 9 cases failed.
- Limitations recorded in the pull request:
  - Lighter's unknown-truncation reads still move the boundary through their
    retained fills.
  - Old real gaps that a Refresh imported past before the upgrade are no
    longer flagged.
- `scripts/check.sh` passed on this branch, which includes 0.4a, with
  PostgreSQL 18 and RabbitMQ 4.3.6 in disposable containers:
  - Python: 78 OK. Prototype tests pass.
  - Release build: 0 warnings.
  - Backend: 3,442 passed, 5 skipped (live venue and S3), 0 failed.
  - Frontend: typecheck and lint clean, 2,481 tests, build OK.

  This is also a clean full run of 0.4a's code.

October 19, 2026, 0.4c coverage and evidence, branch
`t3/phase0-monitor-coverage` (stacked on 0.4b):
- Every successful check persists each Open Play's coverage in
  `monitor_coverage`, in the check's unit of work and account lock. A row
  holds a state, whether the Play is covered, a reason, checked-at and
  per-stop evidence. The migration is `20261019132931_MonitorCoverage`;
  apply it explicitly.
- States:
  - known: covered or not covered;
  - shared;
  - unknown, with a reason;
  - unsupported;
  - stale, projected at read time from twice the check interval.

  Structural unknowns never become missing and never degrade the run.
- RISEx (owner's choice): a `CurrentOrderBook` capability, false for RISEx,
  gives "unsupported: current book not read". Order-book checks do not run
  there, the UI names them, and an incomplete book alone no longer degrades
  the run. `/v1/orders/open` is not built.
- The heading follows open incidents, then coverage by reason. It says
  "Monitored" only when every Play in view is covered, and "Stop coverage not
  checked yet" before a Play's first coverage row.
- Chart stops on Open Plays carry evidence labels in UTC, such as "resting as
  of …", "resting at <price>", "not seen at the venue", "unknown" or "not
  read". An unconfirmed stop is drawn hollow and faint, from theme tokens.
- Tests failed before the fix:
  - the RISEx run was degraded with no coverage;
  - the heading said "Monitored" beside unknown, shared and unsupported
    coverage;
  - evaluator tests failed, mostly by not compiling;
  - chart evidence labels were missing.
- An independent review found no blocking defect. Its findings are fixed in
  the second commit.
- `scripts/check.sh` passed with PostgreSQL 18 and RabbitMQ 4.3.6 in
  disposable containers:
  - Python: 78 OK. Prototype tests pass.
  - Release build: 0 warnings.
  - Backend: 3,456 passed, 5 skipped (live venue and S3), 0 failed.
  - Frontend: typecheck and lint clean, 2,504 tests, build OK.

  The migration was applied only to disposable test databases.
- The isolated full-stack e2e (`scripts/e2e-full-stack.py`) ran on this
  branch, which includes 0.4a and 0.4b:
  - All 177 checks across its 16 steps passed: packaged images, the fake
    venue and Playwright 1.63.
  - The first run failed one browser step. A 0.4a assertion still filtered
    on "Entry order <id>"; it was fixed on the 0.4a branch.
  - Captures are in
    `docs/product-review/verification/2026-10-19-phase0-monitor/`.
- `scripts/monitor-browser-smoke.cjs` was not run. It needs a host browser,
  which this machine cannot start, and release CI does not run it either.
- Three Sol (Codex, GPT-6.1-Sol) review rounds covered 0.4a–0.4c.
  - Round 1 found:
    - **high:** an undismissed unverifiable incident vanished from every
      badge once its account had no active work, because the summary
      listed accounts only by scheduling eligibility;
    - **medium:** the new browser rejected an older server's discrepancy
      without `lastObservedAtUtc`;
    - **medium:** account detail said "Monitored" before the first coverage
      row.
  - Round 2 confirmed those fixes and found one more medium defect: 500
    historical records could push an outstanding incident out of the
    capped list. Incidents now sort first, and `discrepanciesTruncated` is
    reported.
  - Round 3 found nothing, and confirmed the restack kept both sides.

  Every fix has a test that failed before it.
- Final `scripts/check.sh` on this branch (0.4a + 0.4b + 0.4c):
  - Backend: 3,460 passed, 5 skipped, 0 failed.
  - Frontend: 2,509 tests, build OK.
  - Release build: 0 warnings.

  0.4a alone: 3,436 backend and 2,485 frontend tests passed.
- Still open: 0.4d (`docs/app-status.md`, after jcarcaboso PR #66).

## 0.5 Managed stops

October 19, 2026, 0.5a plan-number grammar, branch
`t3/phase0-plan-number-format` (stacked on 0.1):
- Plan numbers are written as blank or an unsigned plain decimal, at most 64
  characters and exact in a C# decimal. The rule covers size, budget, entry
  share and price, and stop and target value and share. Anything else gets a
  400 naming the field.
- Unchanged stored text still saves, matched by field identity. Stored plans
  and revisions are never rewritten, and reads stay tolerant.
- Negative percentages are refused with a pointer to a price stop; `0 %` is
  break-even.
- The browser uses one parser and `formatPlanNumber`, so it never writes
  exponent text. Kept legacy text stays visible with a warning. Side
  pre-checks read stored text as the server does.
- One fixture list (`frontend/src/test/contracts/plan-numbers.json`) feeds
  both runtimes.
- Tests that failed before the fix:
  - the probe's `Grammar` cases;
  - the API test (`1e2` returned 201);
  - 43 characterization rows;
  - 23 frontend tests;
  - after review, 18 frontend and 2 backend tests.
- An independent review fuzzed 200,050 strings with 0 disagreements and found
  no legacy-bypass hole. Its low findings are fixed in the second commit.
- `scripts/check.sh` passed with PostgreSQL 18 and RabbitMQ 4.3.6 in
  disposable containers:
  - Python: 78 OK. Prototype tests pass.
  - Release build: 0 warnings.
  - Backend: 3,446 passed, 5 skipped (live venue and S3), 0 failed.
  - Frontend: typecheck and lint clean, 2,524 tests, build OK.

October 19, 2026, 0.5b managed stops, branch `t3/phase0-managed-stops`
(stacked on 0.5a):
- After an entry has a real fill (a linked entry-side fill or a manual entry
  execution, decided per entry), its price stops may sit at or beyond it,
  with a revision reason (decision 2). Unfilled entries and all targets keep
  the wrong-side rule. A stop unchanged from the stored plan is not
  re-checked, so unlinking a filled entry never makes the Play unsaveable.
- `StopDistance` is signed, so a stop that is not on the loss side adds no
  planned risk. Initial risk and R stay frozen at the revision in force at
  the first entry fill. Results for loss-side stops are unchanged.
- The Summary's "All stops" row is signed and exact, for example "+5 USDC
  locked in", "−5 USDC at risk" or "0 USDC break-even". It is measured from
  each entry's average fill (manual Plays read their ledger) or else the
  planned entry, and says which. While fills load it reads unknown.
- `POST /api/plays/{id}/execution/links/{linkId}/rebind` rebinds a linked
  stop or target to the observed order's acting price, under the account and
  Play locks. It is version-checked against the Play and the observed price,
  writes one plan revision and replaces the link in the same unit of work.
  It refuses a level with another linked order and a replacement whose old
  order has fills. The browser offers it on a `level.drift` record, within
  the matcher's tolerance.
- No migration.
- Tests:
  - the `More4.cs StopRevision` and `f1f2.mjs F1` probes are ported;
  - with the gate and signed distance reverted, 13 of 22 backend tests fail;
  - after review, 3 PostgreSQL tests and 10 frontend tests failed before
    their fixes.
- An independent review confirmed the scored result, ladder and loss-side R
  are unchanged. Its findings are fixed in the second commit: unlink
  unsaveability, manual Plays measured from the plan, size 0, rebind guards,
  drift tolerance and per-entry fills.
- `scripts/check.sh` passed with PostgreSQL 18 and RabbitMQ 4.3.6 in
  disposable containers:
  - Python: 78 OK. Prototype tests pass.
  - Release build: 0 warnings.
  - Backend: 3,471 passed, 5 skipped (live venue and S3), 0 failed.
  - Frontend: typecheck and lint clean, 2,557 tests, build OK.

- Six Sol (Codex, GPT-6.1-Sol) review rounds covered the 0.1/0.5a/0.5b chain
  and found nothing in 0.1 or 0.5a. In 0.5b:
  - **Round 1:**
    - **high:** switching an unfilled long to short slipped a wrong-side
      stop through the unchanged-stop exemption. Direction is now part
      of it, on server and browser;
    - medium: fills depended on the saved status;
    - medium: stale manual-ledger averages;
    - low: a rounded venue average.
  - **Rounds 2 to 5** found freshness races, each fixed with failing-first
    tests:
    - a same-version ledger reread;
    - an action error clearing read staleness;
    - an action overlapping a failed read;
    - a read started during a link;
    - a delayed mutation response.
  - The execution hook was rebuilt around one freshness model:
    - reads are ordered by start, mutations by completion;
    - a mutation's own answer never counts as fresh;
    - nothing is fresh while a mutation is in flight.

    An exhaustive test drives the real hook through 1,055 orderings of
    commit, snapshot, delivery and external imports.
  - **Round 6** found nothing.
- Final `scripts/check.sh` on this branch:
  - Backend: 3,472 passed, 5 skipped, 0 failed.
  - Frontend: 3,621 tests, build OK.
  - Release build: 0 warnings.

## 0.6 Journal opens without every image

Not started.
