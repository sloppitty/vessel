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
- Not run: `scripts/monitor-browser-smoke.cjs`, `scripts/e2e/browser-e2e.cjs`
  and a rendered browser check.

## 0.5 Managed stops

Not started.

## 0.6 Journal opens without every image

Not started.
