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

Not started.

## 0.5 Managed stops

Not started.

## 0.6 Journal opens without every image

October 19, 2026, branch `t3/phase0-journal-images`:
- A saved Play opens from its metadata alone. Originals load afterwards, at
  most three at a time, through `EvidenceImageLoader`, outside the draft.
- Each image shows one of three states:
  - loading;
  - unavailable, with Retry, for 503, 500, a timeout or a transport failure;
  - missing from storage, for a 404.
- Save never deletes or uploads an image that has not loaded. Its note still
  saves.
- Regression tests port the review probe `f6.mjs`. Before the fix, 7 of the
  new frontend tests failed, including "Unable to find role=textbox name
  Thesis" when one original returned 503.
- A backend test now covers `GET /api/evidence/{id}/content` during an S3
  outage: 503 and 500 from storage both give 503, AccessDenied gives 500 and
  NoSuchKey gives 404.
- `scripts/check.sh` passed after the rebase onto `main` with #8 and #10,
  with PostgreSQL 18 and RabbitMQ 4.3.6 in disposable containers:
  - Python: 78 OK.
  - Prototype tests: pass.
  - Release build: 0 warnings.
  - Backend: 3,436 passed, 5 skipped (live venue and S3).
  - Frontend: typecheck and lint clean, 2,463 tests, build OK.
- An independent Sol (Codex, GPT-6.1-Sol) review found no actionable defect.
  It checked metadata-only opening, Save with unloaded evidence, the
  three-request limit, cancellation, stale responses, object-URL cleanup and
  the error states, and its probes passed.
- Still to do: a rendered browser check of the placeholder card and viewer.
  The isolated e2e harness does not visit evidence.
