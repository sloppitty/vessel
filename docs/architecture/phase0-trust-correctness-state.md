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

October 19, 2026, branch `t3/phase0-exit-time-ordering`:
- A venue-linked Play's outcome closes at its latest valid linked exit:
  non-entry fills of a settled Play, clamped to the detection time
  (`PlayExitTime`). Manual and retrospective Plays keep their stored exit.
- `Play.EndedAtUtc` and status changes keep the detection time. Outcome rows
  expose it as `detectedAtUtc`.
- Ties keep the Play-ID order and are disclosed through
  `exposure.tiedOutcomes` and a reason sentence, for Play and Activity
  records alike.
- The consecutive-win ladder, thresholds and window are unchanged. The docs
  now say "two consecutive winning plays".
- Before/after on synthetic data, 2,000 runs with random Play IDs:
  - W, L, L, L went from Full 977 / Half 1,023 to Half 2,000.
  - L, L, L, W went from Full 998 / Half 1,002 to Half 2,000.

  The ladder-level change on upgrade is called out in the pull request.
- `scripts/check.sh` passed with PostgreSQL 18 and RabbitMQ 4.3.6
  in disposable containers:
  - Python: 78 OK. Prototype tests pass.
  - Release build: 0 warnings.
  - Backend: 3,443 passed, 5 skipped (live venue and S3).
  - Frontend: typecheck and lint clean, 2,460 tests, build OK.

## 0.3 Result-basis receipt

October 19, 2026, branch `t3/phase0-result-basis` (stacked on 0.2):
- The scored basis stays the venue result. Classification, thresholds, the
  ladder, the window and the scratch rule are unchanged.
- `PlayLocalResult` computes a Play-local result from the Play's own fills:
  - gross = sign × (Σ exit quantity × price − Σ entry quantity × price),
    with exact products;
  - net = gross − every linked fill's fee.

  It is null with a reason unless the Play is closed and settled, the fills
  match its account and instrument, the venue quotes in a listed nominal
  stablecoin and every fee is verified. Manual Plays use the ledger.
- The Execution panel and Review rows show the venue net and the Play-local
  result with basis labels and a "Sign differs by basis" pill. Review and the
  sizing record count sign differences, unknown Play-local results and
  fee-only losses.
- "Avg. realized R" becomes "Estimated R (planned-stop model)" with its
  denominator; the column is "Est. R".
- The copy says venue P&L may include funding, which is unverified.
- Differences from the contract wording, raised in the pull request:
  - the sizing sign line shows only when a count is above zero; the owner
    kept this on October 20;
  - the quote asset is read per venue, not per instrument.
- Tests:
  - 31 backend cases port R3 and More.cs: A local +8 and B −2 against venue
    +3/+3, flag on B.
  - Before the fix, the new backend tests did not compile and 5 new frontend
    tests failed.
- An independent review found no blocking defect; its low findings are fixed
  in the second commit.
- `scripts/check.sh` passed with PostgreSQL 18 and RabbitMQ 4.3.6 in
  disposable containers:
  - Python: 78 OK. Prototype tests pass.
  - Release build: 0 warnings.
  - Backend: 3,475 passed, 5 skipped (live venue and S3), 0 failed.
  - Frontend: typecheck and lint clean, 2,480 tests, build OK.

  The broker-gated contract fixture test passed.

- A Sol (Codex, GPT-6.1-Sol) review found no defect in 0.2 and three in 0.3,
  fixed in a third commit:
  - tiny Play-local results displayed as "$0";
  - a fee-only loss turned unknown near decimal precision, so
    `GrossResult` is now exact;
  - the formula wording reversed shorts.

  The new tests failed before the fix. `scripts/check.sh` then passed:
  - Backend: 3,477 passed, 5 skipped, 0 failed.
  - Frontend: 2,482 tests, build OK.
  - Release build: 0 warnings.

## 0.4 Monitor truth and visibility

Not started.

## 0.5 Managed stops

Not started.

## 0.6 Journal opens without every image

Not started.
