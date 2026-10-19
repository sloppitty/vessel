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

Not started.

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
  scoped by the Overview scope; the pull request asks the owner.
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
- Still open: 0.4d (`docs/app-status.md`, after jcarcaboso PR #66).

## 0.5 Managed stops

Not started.

## 0.6 Journal opens without every image

Not started.
