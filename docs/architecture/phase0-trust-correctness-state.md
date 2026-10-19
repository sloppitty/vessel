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

Not started.

## 0.6 Journal opens without every image

Not started.
