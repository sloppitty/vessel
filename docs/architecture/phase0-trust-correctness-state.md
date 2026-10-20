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

Not started.

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
