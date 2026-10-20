# Paper trading delivery contract

October 20, 2026. Owner-authorized implementation of
[the refined plan](paper-trading-plan.md), with delegated Sol implementation and
independent verification. No deployment, publication or owner-database migration.

## Fixed product rules

- Opt-in paper category, paper-only portfolios, no real/paper financial pooling.
- Hyperliquid-only paper accounts; no source address, credential or wallet import.
- Starting capital defaults to exact string `4000`, chosen only at creation and
  immutable thereafter. No adjustments, deposits, withdrawals or destructive reset.
- Full eligible allocation at a touched entry/TP/SL's defined price. Zero fees,
  zero slippage. No real execution claim, order submission or imported fake fills.
- Model v1 arithmetic is bounded: quantities round down at 12 fractional
  digits, committed USD margin rounds up at 12 fractional digits and USD P&L
  rounds toward negative infinity at that scale. Defined prices are unchanged.
  Final exits consume the exact remaining recorded quantity and margin, not
  rounding dust. Per-fact quantity/price ≤ `1e12`, margin/absolute P&L ≤ `1e15`;
  positive allocations/exit shares below the quantum fail arm/revision
  validation. These limits are not fees, venue lot sizes or slippage.
  Account totals and admission comparisons use exact sums, not decimal casts.
- Paper check interval defaults to 60 minutes, independently configurable;
  bounded closed one-minute candle windows since the last successful checkpoint,
  shared per native instrument. No continuous paper price stream.
  The scheduler excludes the newest ten seconds before selecting closed
  minutes, allowing final trades to settle. Account-local arithmetic/validation
  failures retain that account's frontier and retry without starving other
  owners; shared schema/connection failures still fail worker health.
  Preserve native trade counts: a validated explicit zero-trade candle advances
  coverage without creating a price touch. An absent candle is unknown, not a
  zero-trade observation; never fill missing slots with invented flat prices.
- Same-candle entry/exit or opposing exit ambiguity is flagged, not guessed,
  and does not become a scored result. No new capital admissions against
  unresolved capital. Preserve revisions/effective activation boundaries.
- Reuse current Play layout, evidence, review, metric formulas and owner scope.
  Existing real behavior, Activity fallback, counts/coverage and sizing stay real.

## Shared wire contract

Backend owns `backend/`; frontend owns `frontend/`. Parent owns docs, root,
scripts/deploy integration and final verification. Any contract change must be
reported to the parent before a dependent implementation is changed.

- Name the execution discriminator **`tradingMode`**, values `real | paper`.
  Existing `accountMode` already describes the venue wallet's accounting mode;
  do not reuse it. Existing records/default requests remain `real`.
- Financial list/report reads accept `?tradingMode=real|paper`, omitted = real.
  Scope all counts, filter options, coverage and pagination before aggregation.
  Account/Play ID writes and reads must reject a mismatched trading mode/owner.
- Add read-only `tradingMode` to account, portfolio and Play summary/list DTOs
  (frontend may default an absent field to real for old fixtures).
- Portfolio creation accepts optional `tradingMode`, default `real`.
- Paper account creation accepts `tradingMode: "paper"` and
  `startingCapitalUsd: "<exact decimal>"`, with a paper `portfolioId` and
  `venueId: "hyperliquid"`. Reject real identities/credentials/manual balance
  fields for paper accounts; preserve existing real validation.
- Account DTOs expose optional/read-only `startingCapitalUsd` for paper.
  `balanceUsd` is virtual cash, `accountValueUsd` is marked virtual equity
  (unknown when marks are missing), `availableBalanceUsd` is virtual available
  margin; source `paper`. Do not expose virtual funds as a stablecoin wallet.
- Paper account snapshots return null/unavailable rather than fabricated
  venue `AccountSnapshot` facts. Overview, Activity, Review and sizing preserve
  their existing response shapes, with optional paper provenance/mode fields,
  calculated from isolated typed paper facts. Disabled paper settings must
  not hide retained account management, Plays or reporting.
- Actual `/api/monitor`, account monitor/check/import/manual-actual/discovery
  and credential routes reject paper mode (409), before venue/credential reads.
  Actual retrospective preview and Play creation are likewise real-only;
  paper-mode requests must not read actual eligibility or create real links.
  The paper shell/Overview must not invoke those hooks/routes; paper-state is
  its execution-health source. Owner-global events/notifications retain their
  existing scope and must not be interpreted as paper monitor health.
- `GET /api/paper/settings` and `PUT /api/paper/settings` use
  `{ enabled: boolean, checkIntervalMinutes: number }`. Default false/60;
  interval is a whole number from 1 to 1440. These are per-owner paper settings,
  never a change to the real monitor. Disabled paper settings block new arming.
- Disabling is explicit suspension, not a 409 requiring all Plays to finish.
  The frontend confirms a true-to-false change, explaining that evaluation
  stops, reservations stay held and disabled-period touches will not be
  simulated later. A direct authenticated PUT false is the same owner intent;
  no additional confirmation property is required. Disable must fence in-flight
  evaluation and retain gap/ambiguity evidence atomically. Re-enable starts at
  a fresh effective candle boundary without replaying the disabled period.
  Existing history stays visible; unresolved gaps remain excluded from scoring.
  Suspension is distinct from a fatal source gap. Record start/resume times,
  resume only previously active simulations and leave owner-paused Plays paused.
  Suspension history permanently excludes that Play's result from scoring but
  does not permanently block fresh work on an otherwise flat, resolved account.
- GET sizing in paper may read the existing owner risk preference read-only.
  PUT sizing settings in paper returns 409, never modifies the real preference.
  The paper record/window/ladder/limits use only paper outcomes. No separate
  per-mode risk-preference table is needed for this first delivery.
- Advertise optional `capabilities.paperExecution: true` only for Hyperliquid.
  Check that capability in general UI/use cases rather than venue-name branches.
- `GET /api/plays/{id}/paper-state` returns the explicit simulated execution
  view below; no synthetic imported order/fill DTOs. It is paper-only.

```ts
interface PaperPlayState {
  playId: string
  status: 'not-armed' | 'waiting' | 'ready' | 'blocked' | 'suspended' | 'ambiguous' | 'gap' | 'disabled'
  checkedAtUtc: string | null
  nextCheckAtUtc: string | null
  enteredQuantity: string
  exitedQuantity: string
  openQuantity: string
  realizedPnlUsd: string
  issues: string[]
  declinedEntries: Array<{ entryId: string, reason: string, declinedAtUtc: string }>
  suspensions: Array<{ reason: string, suspendedAtUtc: string, resumedAtUtc: string | null }>
  hasUnscoredHistory: boolean
  executions: Array<{
    id: string
    entryId: string
    levelId: string | null
    role: 'entry' | 'stop' | 'target' | 'exit'
    price: string
    quantity: string
    pnlUsd: string
    occurredAtUtc: string
    candleOpenAtUtc: string
    candleCloseAtUtc: string
  }>
}
```

Declined entries are durable per-entry decisions, not candle history gaps.
Advance the frontier, never retro-execute a declined touch, and keep evaluating
already-open tranches' protective exits. `blocked` is active work; `suspended`
has no due time until prospective resumption. `issues` contains transient/fatal
source problems, not declined-entry or suspension history. Terminal gap/
ambiguity without a due time does not poll indefinitely in the browser; Reload
still observes stored state only.

Paper Plays use this view instead of the actual-order linking/manual-execution
UI and polling. Existing lifecycle save/status routes remain version-checked.
Backend implementer must report additional required endpoint/DTO decisions
promptly; the above keys may not silently diverge between workers.

Reporting preserves existing base shapes with explicit optional paper fields:
Activity rows add `tradingMode`, `executionSource: "paper"` and
`paperExecutionCount`; pages add mode/count; expanded operations add typed
`paperExecutions` and `omittedPaperExecutionCount`, with actual `fills` empty.
Review adds root mode and row `executionSource`; sizing adds root mode and
`record.source: "paper"`; Overview summary adds mode and
`totals.paperExecutionCount`. Paper imported-fill counts remain zero.
Use the backend reporting DTOs for exact simulated-execution row fields;
do not adapt these into imported fills in React.

Paper accounts use `syncStatus: "paper"`. Paper Activity operation IDs are bare
Play GUIDs, not the real `GUID:direction` operation identity. Paper result bases
are `paper-simulation`, associations are `paper` and funding is `not-applicable`.
Plan entry/level IDs remain arbitrary bounded local strings, not GUIDs.
Play history preserves `source: "paper"` for simulated lifecycle changes and
`owner` for owner actions. Paper clients reject venue/manual history; Real
clients reject paper history. Never relabel simulated facts as venue fills.

Shared-account execution decisions must follow candle chronology across every
active sibling Play and native instrument, even when a sibling is not yet due.
All decisions for one minute use the same pre-minute capital; aggregate deltas
become visible in the next minute. Account-wide state/version/checkpoint
validation and commit must be atomic. Do not partially evaluate an account
because a scheduler batch limit truncated its siblings, or apply a later loss
retroactively to an earlier entry by iterating whole Play windows.

Keep existing deployment module sets immutable. Run the optional paper module
as its own `paper` deployment; do not silently append it to a registered
`monitor` deployment. The implementation uses a standalone paper-only host path
in the existing Worker binary, with no commands/subscriptions or Wolverine/
RabbitMQ dependency. It needs an explicit journal migration and its own productive
scheduler heartbeat, but no paper messaging provisioning/envelope schema.
Preserve existing transport-backed module checks; reject unsupported mixed
paper/transport module sets. Paper notification/event delivery is deferred:
no fake real execution events, and paper-state is the UI progress source.
Parent owns additive deployment/dev-runner integration.

## Bounded first-delivery limits

There is no gap/ambiguity resolution endpoint or manual simulated-close route.
Holds may remain indefinitely and unresolved outcomes stay unscored. Closing
uses planned exits. Account equity is cash when flat/resolved and null while
open/unresolved; no contemporaneous marked-equity service is claimed.
Execution facts retain immutable model version 1 internally. These limits must
be stated in the UI/runbook rather than hidden by guessed financial values.

## Local delivery and verification

Use pnpm. No secrets in output, no reading/using the owner's `.env` for tests.
Use explicitly disposable PostgreSQL/services and synthetic candle adapters;
apply migrations only there. Keep all existing migrations and real-data guards.

Verify immutable capital and mode-compatible owner FKs, every real financial/
count/coverage path, no real API writes to paper accounts, no repeated execution
or P&L after retries/replicas, candle/revision boundaries, gaps/ambiguity,
minute-precision exit times, overlapping Play capital and independent settings.
Run backend and frontend checks, record failures/skips honestly, then independent
Sol reviews and browser verification with synthetic data. No release-tag change.
