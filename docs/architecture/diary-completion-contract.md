# Core diary completion

October 13, 2026. The owner authorized all P1 must-haves in the current
[development roadmap](../product-review/2026-10-13-development-roadmap.md),
implementation by Sol agents, two independent adversarial Sol reviews, a pull
request and browser captures. This contract bounds that delivery. It does not
authorize P2/P3, trading, deployment or changes to the owner's database.

## Record origins and limits

- A planned Play keeps the existing plan, revisions and lifecycle. Manual
  executions are explicit owner-entered facts attached to a saved Play on a
  manual account. They are not imported venue orders or fills.
- A retrospective Play records an owner-confirmed, completed imported operation.
  Its origin is `retrospective`, with a creation/review timestamp and original
  execution times kept separately. It has no contemporaneous plan, plan
  revision, thesis, initial stop or initial-risk R. Any empty plan document
  retained for API compatibility is not displayed as a pre-trade plan.
- The first retrospective workflow accepts only a complete, closed, unassigned,
  single-direction operation. Reject split/flip fills, mixed associations,
  omitted constituent fills, unknown effects, known missing executions and
  orders whose fills cross the selected operation or require different roles.
  Show the records and reasons instead of guessing attribution.
- Use existing whole-order links only when every retained fill of each order
  belongs to this operation and one role. Do not add split-fill attribution in
  this delivery. Revalidate the full facts and associations while holding the
  account lock at creation. Never change imported fill facts.
- Retrospective net results can enter Review when fees and execution coverage
  permit it. Existing net return on executed entry notional remains valid; it
  must not be relabelled return on margin. Initial-risk R stays null. Exclude these
  records from the existing sizing ladder, whose window/arithmetic is unchanged.
- Manual facts appear in their Play and in eligible closed-Play Review results,
  not in the venue-import Activity feed or account-balance summaries. Name that
  scope. They never modify reported balances or claim to reconcile a venue.

## Manual executions

Use a small play-owned manual-execution table and append-only correction
history. Do not manufacture persisted imported orders. Reuse normalized
execution validation and closed-result arithmetic through an in-memory
projection where useful, with explicit manual provenance and fee completeness.

Required inputs are client-generated `requestId`, `expectedVersion`, role
`entry` or `exit`, entry ID for an entry, UTC timestamp, normalized `buy`/`sell`,
positive decimal-string quantity and price, and a short source label. Fee amount
and currency may both be null for unknown. Known zero is an explicit amount
with a currency. Never treat unknown as a known zero in totals or metrics.

The browser takes the time in the display time zone and sends UTC (phase 1,
1.6b; see the capture friction section of `plays-workspace-contract.md`).
Manual execution timestamps must use whole microseconds or coarser precision.
Creation and correction reject sub-microsecond values with HTTP 400 before
ledger validation; timestamps are never rounded into a different execution
ordering when persisted.

Calculate manual gross realized P&L using chronological average cost of the
open quantity, with the Play's direction. Reject over-exits and a close before
its opening facts. Opposite roles at one timestamp are ambiguous and rejected.
This is a linear, USD-quoted manual ledger, one base unit per recorded quantity.
Inverse contracts, other quote currencies and implicit contract multipliers
are unsupported; say so in the manual form and result notice.
Partial entries/exits are supported. Example: buy 1 at 100, sell 0.4 at 105,
then sell 0.6 at 95 gives gross -1 before costs. It is Open after the first
exit, Closed after the second.

Submitted amounts stay exact. Derived partial average-cost allocations and P&L
may round to decimal precision; inexact allocations leave coefficient
headroom based on the bounded ledger notionals, and residual cost carries to
the final close. Once that rounding starts, later partial allocations retain
the bounded scale. An otherwise exact finite-cost path does not start that
rounding policy.
The final exit must consume that cost exactly, and completed gross/net totals
must be exactly representable. A closure or correction that cannot satisfy
those conditions or has an unsupported derived return is rejected before any
facts, history or status change. Outcome calculation sums exact retained P&L
and fee values before converting the supported per-Play result. Review aggregate
money sums remain exact strings even when their combined value exceeds a single
decimal.

Recording requires a saved Planned/Open Play on an enabled manual account.
Expose an explicit `manualExecutions` venue capability, true only for the
manual descriptor. Do not infer manual access from a missing adapter or failed
order import.
Each mutation takes the account then Play lock, checks fresh expected version,
validates the complete resulting ledger, updates status and stores history
atomically. Retries with the same request ID and payload return the existing
result; reuse with another payload conflicts. A correction requires its reason.
Keep before/after values and timestamp. Closed corrections may correct amounts,
prices, costs and times only if the ledger stays a valid closed round trip.
No deletion, reopening, direction change after entry, or cancellation with
entered quantity is introduced. A correction that would reopen a Closed Play
is rejected without partial changes.

**Correction batch (October 20, 2026).** Phase 1.3b of the
[learning-loop contract](phase1-learning-loop-contract.md), decision 5.
- `POST /api/plays/{id}/manual-executions/corrections` takes `requestId`,
  `expectedVersion`, one `reason` and 1–200 `corrections`. Each correction is
  an existing execution's ID with its full corrected facts, as a single
  correction sends them.
- Each row is validated as a single correction is. Role, entry and side stay,
  and a Closed ledger keeps each source label.
- Only the final ledger is checked as a whole. A closed 1/1 ledger therefore
  becomes 2/2 in one batch, which no single correction can do without
  reopening it.
- The batch applies all or nothing in one unit of work. It never adds, deletes
  or reopens rows, a Closed ledger stays closed, and an Open one may close.
- Each row's before and after are kept, with the reason and a shared
  `batchId`. Single-row writes have none.
- The batch counts toward the record and history limits. Retries with the same
  `requestId` and payload return the first result; another payload conflicts.
  Its body limit is 262,144 bytes.
- The explicit `ManualCorrectionBatch` migration adds the nullable `BatchId`
  column to `manual_execution_history`.
- In the browser, a correction can take further executions through **Also
  correct**. Each has its own fields under the one reason, and with more than
  one row the correction is saved as a batch. The audit history labels batch
  entries and counts the rows corrected together.

## HTTP contract

Existing authenticated API conventions, decimal strings, owner scope, body
limits, no-store responses and error format apply. New endpoints are additive.
Manual writes have a 16,384-byte limit; retrospective writes have a
131,072-byte limit. These limits apply to every routed casing and trailing-slash
variant, including chunked requests.
Frontend work can use optional new methods on `WorkspaceApi` so older test
doubles still compile; the real API factory implements every method.

### Manual execution API

- `GET /api/plays/{id}/manual-executions`
- `POST /api/plays/{id}/manual-executions`
- `PUT /api/plays/{id}/manual-executions/{executionId}`
- `POST /api/plays/{id}/manual-executions/corrections` (phase 1.3b, above)

Writes accept the inputs above; correction also accepts `reason`. Return:

```text
{
  play: SavedPlay,
  records: [{ id, role, entryId, occurredAtUtc, side, quantity, price,
              fee, feeCurrency, source, createdAtUtc, updatedAtUtc }],
  history: [{ id, executionId, reason, occurredAtUtc, before, after, batchId }],
  totals: { enteredQuantity, exitedQuantity, openQuantity,
            grossResultUsd, netResultUsd, feesComplete },
  notice
}
```

`before` is null for creation; `after` and non-null `before` use the record
shape. History and rows are bounded with explicit limits, not silent omission.
`netResultUsd` is null when cost conversion/completeness is unavailable.

### Structured review API

Extend `PlayFields`/create/update and `SavedPlay` with optional
`structuredReview: { adherence, mistakeTag, lesson, nextAction }`.
Adherence is null, `followed`, `deviated` or `not-applicable`; mistakeTag is
null or a bounded single-line plain string. Lesson and nextAction are bounded
plain strings that preserve LF and CR line breaks; other controls are rejected.
Empty defaults mean unanswered, not failure. Existing free-text `review` stays.

Summary gains `origin: planned|retrospective` and nullable `reviewedAtUtc`.
`hasReview` includes nonempty structured answers or existing free text. Since
October 20, 2026 (Phase 1.2), having a review no longer makes a Play
"reviewed": completion is explicit (`reviewCompletion`). See the review
completion section of `play-lifecycle.md`.
Only terminal records accept nonempty structured answers. Retrospective
adherence is null or `not-applicable`, never an invented process assessment.
All changes to free-text or structured review append a review revision without
changing frozen plan revisions. `PlayHistory` gains `reviews` containing
`number`, `occurredAtUtc`, `review` and `structuredReview`.
Existing free text remains readable. Failed saves cannot remove a record from
the pending queue, and edits made during a save remain dirty.

### Retrospective API

- `GET /api/activity/retrospective?accountId={id}&operationId={id}`
  returns `{ eligible, reasons: string[], fingerprint, operation, notice }`.
  `operation` is the existing ActivityOperation DTO, including original fills
  and associations. The server recomputes it from owner-scoped retained facts;
  the browser never submits financial facts as authority.
- `POST /api/plays/retrospective` accepts
  `{ requestId, accountId, operationId, expectedFingerprint, title, review? }`
  and returns `SavedPlay`.

The fingerprint covers constituent facts, identity and associations. Creation
rechecks it under the same lock as linking/import changes; stale previews
return 409. Request retries are idempotent and different payloads with the same
key conflict. Creating the retrospective Play and all links is atomic. Do not
link a whole net position that overlaps another Play.

The fingerprint includes all retained fills of the involved orders, even those
outside the selected operation, but excludes unrelated orders and instruments.
Operations above the 1,000-fill preview limit are rejected before order/link
reads or per-order completeness scans.

Explicit account refresh retains bounded venue-reported orders even when no
active Play exists. This does not link orders or modify imported fills.
Missing order credentials and optional order-read failures leave successful
public balance/fill refreshes intact and add a safe history notice. Public and
order coverage persist through account/workspace reloads. Later order reads
replace the order section without clearing public coverage or changing sync
status, timestamps or settings revision. Only retained orders establish
completeness; missing or partial records still block creation.

Linked execution quantities accumulate exactly for chronology, missing-order
coverage, over-exit checks and lifecycle equality. Displayed entered/exited/open
strings use the same sums. A small remaining quantity cannot round into a
Closed status or a scored result, even beside a much larger execution.

### Exclusion API

`GET /api/review/exclusions` accepts the existing Review filters and `offset`.
Return `{ items, total, offset, nextOffset, notice }`, 50 records per page:
`{ playId, title, accountId, accountName, instrument, reasons: string[] }`.
Use the same evaluation and period boundary as Review counts. Reasons identify
known offending order/fill IDs where available and distinguish missing
executions, contradictory roles, missing/unsupported fees and unavailable
return/risk inputs. Do not exclude a retrospective net result merely because
initial-risk R and return-on-margin are unavailable.

Review keeps its existing executed-entry-notional return arithmetic and DTO.
No leverage is inferred. Initial-risk R remains unavailable for retrospective
records. Review continues to show funding/basis limitations and retained-history
scope.

## Frontend integration and ownership

- Plays worker owns existing `features/plays/**`, `api/plays.ts`,
  `api/diary.ts`, `api/workspace.ts` and their tests, except the new
  PositionRiskSummary and position-risk-summary files owned by Summary worker.
- Review/Activity worker owns `features/review/**`,
  `features/workspace/**`, `api/review.ts`, `api/retrospective.ts` and their
  tests. Do not edit `api/workspace.ts`; send required additions to the Plays
  worker or parent.
- Backend worker owns `backend/**`, migrations and backend tests.
- Parent owns documentation, tracking, integration fixes after handoff,
  synthetic browser fixtures/captures and PR work.

Cross-screen navigation passes `requestedPlayId?: string|null` and
`onRequestedPlayHandled?: () => void` to PlaysPage. Review/Activity callbacks
request the Play through the shell; PlaysPage offers to keep current work
instead of replacing an unsaved draft. Success consumes the request only
after loading; errors are visible. Do not silently discard on navigation.

Real WorkspaceApi additions are `manualExecutions`, `addManualExecution`,
`correctManualExecution`, `retrospectivePreview`, `createRetrospectivePlay`
and `reviewExclusions`. Put input/output types and validators in the named API
modules. Retrospective and exclusion methods consume the shapes above.

## UI and verification

Preserve the locked desktop positions and existing responsive stack. Add the
aggregate money estimates in the existing bottom Summary. Show allocation,
priced quantity, stop coverage and target coverage separately. Never normalize
partial allocations into apparent completeness or call a stop estimate a
guaranteed maximum loss. State fee, funding and slippage exclusions.

Review exclusions open the precise Play. Activity shows original facts before
retrospective confirmation. Plays offers pending/reviewed filters and the
structured Review tab. Keep optional portfolios in onboarding, explain
account-balance versus retained-operation versus saved-Play metrics, and do
not claim cross margin is always safer or evidence is durable before saving.
Use theme tokens, keyboard labels and compact mobile disclosure.

Verify owner/disabled account boundaries, exact arithmetic, partial fills,
unknown fees, correction history, idempotency, stale previews, concurrent
link/revision/import writes, terminal invariants and unsaved draft safety.
Use only disposable PostgreSQL with explicit migrations. Run repository checks,
actual browser workflows at desktop and mobile widths, then two independent
adversarial Sol reviews. Capture implemented screens using synthetic data.
No release or live database migration is part of this work.
