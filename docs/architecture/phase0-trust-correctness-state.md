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

Not started.

## 0.4 Monitor truth and visibility

Not started.

## 0.5 Managed stops

Not started.

## 0.6 Journal opens without every image

Not started.
