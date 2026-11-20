# Backend rules

The top-level [`AGENTS.md`](../AGENTS.md) applies first. How to run, test and
configure: [`README.md`](README.md); structure: the
[backend guide](../docs/architecture/backend-guide.md). Each rule names the
contract that holds the detail.

## Boundaries

- Domain and Application never reference Wolverine, RabbitMQ or other concrete
  infrastructure. Wolverine and RabbitMQ code lives in `Vessel.Messaging`; feature
  modules register their own topology and hosted services.
- A bounded Application-owned `IUnitOfWork` owns saves and the one commit. Nested
  stores never commit or dispatch, and object-storage I/O refuses inside any unit
  or transaction.
- PostgreSQL is the record and RabbitMQ the transport. Events come from Vessel
  state changes, written with their outbox envelope in the same transaction,
  never from re-read rows. Subscriptions are provisioned only by the explicit
  insert-only step; recovery is the subscriber's catch-up from the database,
  never a producer republish ([messaging and workers][msg]).
- Use cases check venue capabilities, never venue names. Venue adapters translate
  their encoding into normalized facts; matching, lifecycle and sizing never read
  a venue's encoding, and net-of-fee P&L is never reduced by its fee twice.
- Shared strict JSON and bounded HTTP helpers live in `Venues/Common`; each venue
  registers its HTTP clients under its own names.
- .NET packages restore from the NuGet lockfiles (`--locked-mode`).

## Venues, instruments and balances

- Perpetuals first: instruments and executions are perpetuals. Map native symbols
  to the canonical instrument (`kPEPE` → `1000PEPE`) and keep native venue IDs
  ([venue instruments][inst]).
- Wallet reads cover supported stablecoins only, validated against token
  metadata; no EVM, lending or non-stable valuation. Wallet total, held and
  available stay distinct from perps equity, withdrawal and collateral; never
  double-count ledgers ([stablecoin wallet][wallet]).
- Availability is wallet funds on wallet-capable venues, else the venue-reported
  available margin, else reported withdrawal; never equity minus margin or a
  manual value ([account management][acct]).
- Funding is a signed actual settlement beside the execution net. Show a
  funding-inclusive total only with verified coverage and a proven execution-P&L
  funding basis ([venue funding][funding]).
- Lighter: one Vessel account per account index (string `SourceId`); streaming
  and the generic stream relay are deferred. Fee-bearing trades are omitted with
  durable markers; liquidations, deleverages and settlements are unscored forced
  events ([Lighter delivery][lighter], [Phase 2][p2]).
- A disabled market source only filters the selector; catalogue, imports,
  positions and the monitor never read it ([market sources][sources]).
- Unrealized P&L marks are null on failure, never zero, and never enter results
  ([Activity operations][activity]).

## Accounts and history

- Every query and write is owner-scoped; keep account and sync locking.
- Disabling an account preserves its imports, hides its activity and positions
  and blocks sync. Deleting a portfolio only unlinks its accounts. Deleting an
  account needs explicit confirmation and is blocked by linked Plays. Unassigned
  is null membership, never a portfolio ([account management][acct]).
- Membership changes are atomic and check the expected revisions under lock; a
  mismatch returns 409 with no partial change, and a stale selection is never
  replayed after a conflict.
- Financial values are exact decimal strings. Unknown stays null, a missing fee
  is not a zero fee, and disabled accounts stay out of financial reports.
- Never cache authorization or owner financial data.
- Every import, Refresh included, records fill continuity; Refresh can set a
  history gap but never clear one ([Phase 0][p0]).
- Only venue-reported leverage counts; never infer it from margin, notional, plan
  or venue maximum.
- Evidence is stored only for saved Plays, through `IEvidenceObjectStore`;
  markup sits beside the untouched original.
- Exports are owner-scoped, exact and formula-neutralized, never contain secrets
  and are not a tax report ([Phase 2][p2]).

## Plays, matching and review

- A price touch, candle or stream update is an observation, never an actual fill.
  Discrepancies are flagged, never corrected.
- Fills belong to an order only through the shared `OrderKey`/`LinkedFills` rule,
  never the external order ID alone. Play order links reference `ImportedOrderId`;
  a known order reported on another native market fails closed.
- Linking is automatic only when unambiguous; otherwise the owner chooses. Plan
  changes after planning are revisions with reasons ([Play lifecycle][life]).
- Initial R follows the current first entry fill; it is never inferred for
  imported history. A stop that is not set is no stop, never break-even. The
  draft budget defaults to the normalized available balance, and a known zero
  stays zero ([Phase 0][p0]).
- `Unresolved` is declared by the owner with a null result, never set from
  account flatness ([Phase 1][p1]).
- Keep the execution and evidence integrity fixes of the
  [diary completion contract][diary].
- Open risk never nets long and short; a stop at or beyond entry adds zero; a Play
  with unknown risk is counted, never partly summed. Limits are owner settings
  with no default, flagged and never enforced ([Phase 2][p2]).
- The sizing record uses decided closed Plays and manual outcomes; the Activity
  fallback applies only while none exists and never mixes with them. The ladder
  thresholds, pooled ladder, 20-decided window and exact-zero scratch rule change
  only with the owner ([sizing][sizing], [Phase 1][p1]).
- Review scores saved closed Plays from linked executions at actual exit times,
  rejecting known missing executions and incomplete fees; R and reviews cover
  Plays only, and RISEx P&L is never promised funding-free
  ([portfolio review][review]).
- The Plays list reads the `Direction` column, never plan, drawing or review
  text. The unscoped list still shows Plays on disabled accounts
  ([list paging][paging]).

## Paper

- Paper is opt-in and Hyperliquid-only. Paper accounts have creation-only,
  immutable virtual capital. The simulation alone counts touched planned prices
  as typed simulated executions ([paper delivery][paper]).
- Shared-account decisions run chronologically across all active sibling Plays.
  Ambiguities and gaps are held and unscored, never guessed.
- Paper data never reaches real financial readers or actual facts, and paper
  lifecycle never generates real events. Venue-fact endpoints answer paper
  requests with 409.

## Monitor and workers

- Linked exits and entries of closed or cancelled Plays stay checked until fresh
  venue evidence shows they no longer rest. Dismissing a warning never stops
  checks ([monitor plan][monitor]).
- A Check with a stale worker heartbeat returns 503 and records nothing. Expired
  unclaimed check work returns to idle at admission and never blocks Refresh
  ([backend review][breview]).
- Background work runs per assigned owner scope from the identity directory.
  Subscriptions register server-wide with per-owner progress; venue deadlines and
  capacity are shared in PostgreSQL; heartbeats are per deployment
  ([multi-user delivery][mu]).
- Notifications are in-app only; external channels, browser system notifications
  and price alerts are out of scope ([Notification Hub][notify]).

## Secrets and sign-in

- Venue credentials are encrypted with the server-only AES-GCM key ring, per
  owner, account and purpose, through a write-only API. Only verified read-only
  tokens are accepted ([venue credentials][creds]).
- Token entry needs HTTPS outside explicit development. The only exception is
  the Vite development build with the API in Development, which shows a
  plaintext-transport warning ([Lighter delivery][lighter]).
- Verification runs outside database transactions and locks; a short locked
  recheck then saves, or returns 409 if the account or its credential changed
  ([backend review][breview]).
- The user ID is the existing owner GUID, and credential encryption binds it. The
  operator role grants no journal bypass ([multi-user delivery][mu]).
- Sign-in is by wallet only. API tests authenticate through the test-only header
  scheme; never add a bearer path to production code.

## Migrations

- Create migrations with the EF tools; apply them only through tests against the
  throwaway database. Tests migrate unique temporary schemas and drop only those.
- An installation is migrated only in an upgrade the owner authorizes, by the
  operator's explicit one-shot `migrate` step. The API and workers never migrate
  or provision at startup; `provision-messaging` is explicit too.
- A migration that needs an upgrade step says so in the pull request and the
  release notes.

[msg]: ../docs/architecture/messaging-and-workers.md
[inst]: ../docs/architecture/venue-instruments.md
[wallet]: ../docs/architecture/stablecoin-wallet.md
[acct]: ../docs/architecture/account-management-contract.md
[funding]: ../docs/architecture/venue-funding.md
[lighter]: ../docs/architecture/lighter-delivery-contract.md
[sources]: ../docs/architecture/market-sources.md
[activity]: ../docs/architecture/activity-operations.md
[life]: ../docs/architecture/play-lifecycle.md
[p0]: ../docs/architecture/phase0-trust-correctness-contract.md
[p1]: ../docs/architecture/phase1-learning-loop-contract.md
[p2]: ../docs/architecture/phase2-risk-coverage-contract.md
[sizing]: ../docs/architecture/sizing-suggestions.md
[review]: ../docs/architecture/portfolio-review.md
[paper]: ../docs/architecture/paper-trading-delivery-contract.md
[monitor]: ../docs/architecture/play-monitor-plan.md
[breview]: ../docs/architecture/backend-review.md
[mu]: ../docs/architecture/multi-user-delivery-contract.md
[notify]: ../docs/architecture/notification-hub-plan.md
[creds]: ../docs/architecture/venue-credentials.md
[diary]: ../docs/architecture/diary-completion-contract.md
[paging]: ../docs/architecture/list-paging.md
