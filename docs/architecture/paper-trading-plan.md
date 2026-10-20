# Paper trading: an isolated hypothesis playground

October 20, 2026, refined and authorized by the owner the same day.
**Implementation authorized**, including flagging ambiguous same-candle touches,
delegated Sol implementation and independent Sol verification. No live database
migration, deployment or publication is authorized. Follow the bounded shared
[delivery contract](paper-trading-delivery-contract.md); record verified progress
in [the delivery state](paper-trading-state.md).
Terminology is recorded in [the glossary](../../CONTEXT.md).

## 1. Requested concept

The owner wants to test trading hypotheses without using or changing real
capital:

- Paper trading is opt-in, not enabled or populated by default.
- A special paper portfolio contains only virtual accounts.
- Paper accounts use **Hyperliquid only**. Other venues are out of scope.
- Starting capital is chosen when adding the account, default **4,000 nominal
  USD**, and is immutable immediately after creation, even before any Play.
  There are no later capital edits, deposits, withdrawals or reset operations.
  Create multiple paper accounts for different starting allocations/experiments.
- Normal Plays, entries, stops, targets, notes, evidence and reviews can be used
  on these accounts.
- A worker simulates entries and exits from market observations, even with the
  browser closed. The virtual balance changes with the simulated results.
- Check **once per hour by default**, with a configurable paper-check interval
  to reduce Hyperliquid requests. Continuous/live paper monitoring is not needed.
- When the venue price touches a defined entry, TP or SL, count its full
  eligible quantity as simulated at that defined price. **No fees, no slippage.**
  This is a simple hypothesis tool, not an exchange-execution emulator.
- Paper accounts have their own category and the same kinds of statistics as
  real accounts, **without changing any real balances or metrics**.

Interpretation to confirm: “should affect the metric of the others” is read as
“should **not** affect” them, consistent with the owner's explicit isolation
requirement. The owner's subsequent **Hyperliquid-only** decision supersedes
the original broader “any venue” request.

## 2. Recommended model: a mode, not a tag

Use an immutable `real | paper` mode on portfolios and accounts. A Play inherits
its account's mode; the API exposes that mode as read-only. Do not overload
`PlayOrigin` (planned/retrospective), `IsEnabled`, the manual venue or a free-text
tag to represent paper trading.

```text
Journal owner
├── Real portfolios / real unassigned accounts
│   └── Real accounts → real Plays → imported or manual actual executions
└── Paper portfolios
    └── Paper accounts → paper Plays → simulated executions + virtual ledger

Shared: venue catalogue, chart, Play editor, journal, evidence, metric formulas
Separate: balances, executions, positions, outcomes, sizing records and totals
```

A `Paper` badge/filter is useful, but is a presentation of this enforced mode,
not an editable label that can remove the boundary.

### Membership and identity

- A paper account must belong to an owner-owned paper portfolio. A real account
  cannot move into one, and a paper account cannot move into a real portfolio
  or become unassigned. Paper-to-paper moves remain possible.
- Real accounts keep today's optional membership and deletion behavior. Deleting
  a nonempty paper portfolio is refused until its accounts are moved to another
  paper portfolio; do not delete history or silently create unassigned papers.
- Mode is not editable. Existing portfolios/accounts migrate explicitly to
  `real`. Do not offer real-to-paper conversion.
- Paper accounts need no wallet, address, venue account index or credential.
  Their internal account ID identifies the simulation. Multiple paper accounts
  can use Hyperliquid without pretending to be venue source identities. The
  owner's “multiple wallets” means these virtual accounts, not on-chain wallets.
- Keep Hyperliquid's native contract identity, ticks and canonical
  instrument mapping. The market source is not an account import source.
- Paper is not a venue testnet account. No real or testnet orders are submitted.
  Cloning a hypothesis into a new real draft could be a later explicit action;
  it must never convert executions or copy simulated results into real history.

The minimum structural additions are mode discriminators, immutable starting
capital, a virtual capital ledger, simulated executions and durable simulation
progress. Reuse existing Plays, plan revisions, evidence and reviews; do not
build a second diary application or a general exchange emulator.

## 3. What the repository already supports, and what it does not

This review follows the checked-out code, not the disposable prototype.

| Existing seam | Reuse / required change |
| --- | --- |
| [Account](../../backend/src/Vessel.Domain/Accounts/Account.cs) and [Portfolio](../../backend/src/Vessel.Domain/Workspace/Portfolio.cs) | Ownership, membership and account locking exist; no real/paper discriminator exists. |
| [WorkspaceService](../../backend/src/Vessel.Application/Workspace/WorkspaceService.cs) | Real venue creation requires a real source identity and rejects manual balances. Paper creation needs its own validated path, not a fake address or a relaxed real-account guard. |
| [AccountConfiguration](../../backend/src/Vessel.Persistence/Configurations/AccountConfiguration.cs) | Owner-composite membership FKs and source-identity constraints exist. Preserve real-account constraints; add mode-compatible membership constraints, including mandatory paper membership. |
| [WorkspaceService](../../backend/src/Vessel.Application/Workspace/WorkspaceService.cs), [ActivityStore](../../backend/src/Vessel.Persistence/ActivityStore.cs), [SizingStore](../../backend/src/Vessel.Persistence/SizingStore.cs), [ReviewStore](../../backend/src/Vessel.Persistence/ReviewStore.cs) | Aggregates select enabled accounts, not execution mode. Every relevant read must gain an explicit mode boundary before paper data is admitted. |
| [Play](../../backend/src/Vessel.Domain/Plays/Play.cs), [lifecycle](play-lifecycle.md), [diary contract](diary-completion-contract.md) | Reuse plans, revisions, review and execution progress. Add simulation provenance without calling simulated executions imported fills or manual actual trades. |
| [PlayOutcomeCalculator](../../backend/src/Vessel.Application/Plays/PlayOutcomeCalculator.cs), [Review](portfolio-review.md), [sizing](sizing-suggestions.md) | Reuse appropriate arithmetic through explicit paper facts. Current venue-PnL/fee assumptions cannot be bypassed by manufacturing imported records. |
| [MonitorCoordinator](../../backend/src/Vessel.Application/Monitoring/MonitorCoordinator.cs) | Today's monitor reads actual venue account/order facts and reconciles them. It is not a price-trigger execution simulator. |
| [ICandleReader](../../backend/src/Vessel.Application/MarketData/ICandleReader.cs) and [Hyperliquid candles](../../backend/src/Vessel.Infrastructure/Venues/Hyperliquid/HyperliquidPerpetualReader.Candles.cs) | Reuse the existing bounded native-market candle reads for scheduled price windows. Inspect closed candles chronologically, not just the latest price. |
| [IMarketStream](../../backend/src/Vessel.Application/MarketData/IMarketStream.cs) | No continuous stream is required for paper execution. Candle ranges still do not establish intrabar entry/TP/SL order. |
| [Worker modules](messaging-and-workers.md#modules) | Reuse the worker host, owner scopes, transactions, outbox, health and fencing patterns; add a bounded, opt-in paper module. No new broker or framework is needed. |

Advertise paper execution only for Hyperliquid, using a capability checked by
the browser and use cases rather than venue-name conditionals. Other venue
modules and real-account capabilities stay unchanged. Reuse Hyperliquid's candle
adapter; do not scaffold a new stream, tick archive or future venue simulators.

This feature therefore has two distinct pieces: safe accounting/report
isolation, and a new simulation engine. Adding a portfolio flag and another
monitor check does not deliver it.

## 4. Isolation is a backend invariant

Proposed rules, to establish before exposing paper-account creation:

1. Every aggregate request resolves one mode. Existing/default routes resolve
   `real`; a paper report requires explicit paper scope. There is no combined
   real-plus-paper financial total. “All accounts/portfolios/Plays” means all
   within the selected mode.
2. Enforce mode before aggregation, counts, coverage, filter options and paging,
   not by hiding rows in React. Reject a mismatched account/portfolio selection
   without retrying in another mode. Owner isolation remains mandatory.
3. Scope Overview, allocation percentages, balance/availability coverage,
   account/portfolio counts, Activity, Plays tabs, Review, exclusions, exports,
   capital context and sizing. Paper must not change even the denominator or
   available options of a real report.
4. In particular, a paper win must not replace the real Activity fallback,
   change the real 20-decided-outcome record or advance its exposure ladder.
   Paper has an independent record. For the simplest first delivery, sizing
   reads the owner's existing risk preference read-only; paper cannot update
   that preference. Keep real scoring thresholds and result-basis decisions
   unchanged.
5. Real imports, Refresh/Refresh all, discovery, credential writes, order
   linking, manual-actual execution writes and real discrepancy checks reject
   paper accounts, including direct API calls. Paper evaluation cannot write
   imported orders/fills, venue snapshots, leverage observations or funding.
6. Paper execution and ledger records have owner/account/Play references and
   uniqueness constraints. Mode-compatible relationships are enforced at the
   database boundary as well as in application commands. Account mode cannot
   change underneath historical Plays.
7. Financial responses and browser state identify mode and result provenance.
   Mode is part of report-state/request keys. Switching modes cancels pending
   reads and must not display a late real response as paper, or the reverse.
   Keep owner financial data uncached as today.

“Paper trading disabled by default” means an owner opt-in plus an available
worker capability, not `IsEnabled=false` on a pretend real account. Existing
users see unchanged real data and no seeded paper portfolio. Switching the
visible category does not stop an already armed simulation.

## 5. Proposed owner workflow

1. Enable **Paper trading** in Settings. Explain that capital and executions are
   simulated, Hyperliquid is the only venue and the model is touch-based with
   zero fees and zero slippage.
2. Create a **Paper portfolio**, for example “Hypothesis lab”. Render it in a
   distinct Paper category, never add its value to the real portfolio headline.
3. Add a paper account: name and starting capital (prefilled `4000`), with
   Hyperliquid fixed as its venue. Confirm that starting capital cannot change
   after creation. No wallet import, credentials, cost configuration or automatic
   account history refresh. Create another account for a new allocation.
4. Create a Play on that account using the existing layout. Keep the approved
   chart/editor/journal/capital/summary positions. Show a persistent **Paper ·
   simulated** badge and virtual capital labels, not a different editor.
5. Planning arms the simulated entries after validation and capital reservation.
   Draft does not monitor. Show the price source, fixed touch/zero-cost model,
   effective armed time, last processed market time, last/next check and worker
   health. Settings has a separate Paper check interval, default one hour.
6. Simulated entry executions move Planned → Open; compatible exits move Open
   → Closed. Multiple entries/stops/targets and concurrent Plays remain allowed.
   Paper events/popups say “Simulated entry/target/stop”, not “Venue filled”.
7. Review the outcome through the same report components in **Paper** mode,
   with simulation provenance and coverage visible.

For unfilled Plays, Paused disarms pending entries and releases reservations;
resuming re-arms prospectively. Cancelled remains never executed. An Open Play
cannot be cancelled to erase a loss: an explicit simulated close needs a fresh
price and retains its provenance. Closing must also disarm remaining entries.
No new lifecycle status is needed; “Awaiting price”, “Data gap” and “Insufficient
paper margin” are simulation health/admission states.

Disabling a paper account preserves history and stops evaluation, with a
coverage gap on resumption; it does not invent exits during the interruption.
Disabling the overall feature requires explicit suspension confirmation,
rather than merely hiding active risk or refusing until every Play finishes.
Hold existing reservations, retain history and atomically fence evaluation;
re-enabling starts at a fresh effective candle boundary and retains a visible
gap rather than replaying disabled-period touches. A settings API disable is
itself explicit authenticated owner intent; the UI confirmation adds no new
request field.

## 6. Simple touch-based simulation

### Confirmed model and technical defaults

Forward-only, deterministic **price-trigger simulation**, not order-book replay
or backtesting. The owner chose full simulated execution at the touched level
with **zero fees and zero slippage**, including stops. Do not retain configurable
costs, realistic stop-gap pricing or a multi-venue rollout from the first draft.

Proposed technical defaults, not additional owner answers:

- At each scheduled check, read Hyperliquid trade candles covering the period
  since the last successfully evaluated checkpoint, not just the current price
  and not just “the last hour”. That lets one check discover touches that happened
  and reversed between runs.
- Use closed **one-minute candles**, processed chronologically, rather than
  one hourly candle. The check frequency and candle resolution are different
  settings/concepts: hourly checking does not require hourly price resolution.
  Keep the candle resolution fixed in v1; only the check interval is configurable.
- A level inside a candle's low/high range is touched. Exact equality at the
  check time is not required. A price jump across the level between consecutive
  covered candles also counts at that defined level under the chosen zero-
  slippage assumption; a missing-data gap is not a covered price jump.
- Activate a new level or revision at the next full candle boundary after its
  save, show that effective time, and never apply earlier/straddling candle
  extremes to it. Do not backdate executions before arming.
- Candle ranges establish touches, not their order within the candle. Do not
  manufacture intrabar order, fetch a continuous stream or build a tick archive
  just to make this simple hourly tool appear exact.

### Check cadence and request budget

Use one owner-level **Paper check interval**, default **60 minutes**, inherited
by that owner's paper accounts/Plays. Validate a positive server-bounded duration.
This setting is independent of real-account monitor intervals and never changes
them. No per-Play timer or interval override is needed for v1.

Only check enabled paper accounts with armed entries or open quantity. Group
active Plays by native Hyperliquid instrument and share the bounded public
candle window across them, including across accounts where possible; do not
make a separate venue request for every Play or level. Keep private decisions
owner-scoped even when public candle reads are shared.

Persist due times and successfully processed candle checkpoints. Prevent
overlapping runs, stagger due work, respect shared venue capacity/backoff and
deduplicate fetched/evaluated windows. A longer interval or failed run must not
silently skip the unevaluated period. Bound catch-up to supported history and
request budgets; record a gap when it cannot be recovered. No browser-open
requirement, high-frequency retry loop or continuous paper WebSocket.

### Entries and exits

- An entry executes its full planned tranche when touched. No separate limit/
  stop order-type selector, marketable-order simulation or queue model is needed:
  the level is simply a hypothesis trigger.
- A TP or SL executes its full remaining allocated quantity when touched, at
  exactly its defined price, not at the first observed price beyond the level.
  Existing plan price/quantity validation still applies.
- An exit can close only quantity already entered on its associated entry.
  Stop/target allocations follow the saved plan, capped at remaining quantity.
  Every exit reduces its sibling exits' remaining executable quantity, so a
  stop after a partial TP cannot close the original full quantity again.
- “Fully executed” is per level/tranche, not permission for one partial TP to
  close unrelated entries or the whole Play. There are no liquidity-based
  partial executions; planned quantity allocations remain supported.
- If one candle touches an entry and an exit, or both TP and SL for already-open
  quantity, the touch sequence may be unknowable. The owner accepted: flag
  **Ambiguous touches**, hold the affected execution frontier and ask for explicit
  resolution rather than choose a win or loss automatically. Do not use an
  arbitrary OHLC path, silently choose TP first or close the quantity twice.
  This flag-not-guess policy was accepted when implementation was authorized.
- For unambiguous touches, use the candle close as the deterministic recorded
  simulation time and retain the candle's time range. Label minute precision;
  do not claim to know the exact intraminute touch time.
- Plan changes are version-checked, reasoned revisions effective only after
  their commit boundary. Preserve the current filled-entry/managed-stop guards
  and first-entry initial-risk principle; revised levels never rescore old prices.
  Evaluate each candle against the revision effective at that candle's open,
  not the latest plan at the hourly check. Retain that revision on the simulated
  execution and use the first entry's effective revision for paper initial R.
  A simulated entry supplies execution proof only for its paper Play; it never
  relaxes a real Play's requirement for an actual entry execution.

### Deliberate simplifications

The fee-free model is an explicit owner choice, not missing cost data treated as
zero and not a claim that Hyperliquid charges no fees. Keep a model version and
trigger provenance for audit, but no per-account fee/slippage settings.

V1 excludes order-book queueing, borrowing, liquidation, cross-margin/venue-netting
fidelity and funding modeling. State **“Simulated plan result · no fees or
slippage · funding and liquidation not modeled”**, not a complete venue-equivalent
net result. Start with supported Hyperliquid linear perpetuals; no automatic
venue funding imports.

Concurrent Plays keep independent simulated exposure even on the same market,
while sharing one account's virtual capital. This serves hypothesis testing,
but is not a replica of a venue's netted position. If venue-faithful netting or
liquidation is essential, it needs a separate model before leveraged results
can be described that way.

### Downtime and incomplete evidence

Persist executions, arming/revision boundaries and evaluated candle checkpoints
for restart and deduplication. Normal hourly checks replay the intervening closed
candles in order; this bounded catch-up is not a historical backtesting feature.
The normal wait for the next scheduled check is not an outage or a coverage gap.
Show “As of”/next-check times rather than claim live prices or live equity.

After downtime, catch up from the checkpoint within the same bounded window
rules. Missing history, failed reads and ambiguous touches must be visible;
do not advance past unevaluated/ambiguous facts and pretend coverage is complete.
If recovery is impossible, resume only with an explicit recorded gap. A result
spanning an unresolved gap/ambiguity is excluded from scored paper statistics,
with a drill-down reason. Real reports remain unaffected.

## 7. Virtual capital: balance is not notional

Use an auditable paper ledger, not repeated edits to `ManualAccountValueUsd`.
Starting capital is fixed at account creation and is not trading profit.
The evolving balance changes only through simulated trading results.

```text
Paper cash balance = immutable starting capital + realized simulated P&L
Paper equity       = paper cash balance + unrealized simulated P&L
Paper available    = max(0, paper cash balance - open initial margin
                           - pending-entry reservations)
```

This is a deliberately cash-funded, isolated simulation margin model: unrealized
gains do not finance new entries. Pending reservations cover entry margin; on
execution, release that reservation and allocate the open margin. No fee is
reserved or debited. Serialize admission/evaluation at account scope so
concurrent Plays cannot spend the same virtual funds. When unresolved touches
leave account capital uncertain, hold new entry admissions until resolved.

For linear contracts, gross closed P&L is direction × closed quantity ×
(exit price − entry execution price). Leverage changes quantity for a fixed
margin, or margin for fixed quantity; it does not multiply P&L a second time.
Return metrics retain the existing unlevered entry-notional basis.

Example: start at 4,000; reserve 200 margin for a 5× long,
quantity 10 at price 100. Reservation leaves 3,800 available, not 3,000.
An exit at 110 realizes +100, releases the margin and leaves 4,100 cash/equity.
The real Overview and real statistics are identical before and after.

- Default starting capital is `4000`; accept exact nonnegative amounts, including
  a known zero (which cannot fund entries). Currency labels say nominal virtual
  USD, not a real USDC/USDT wallet.
- Starting capital is editable only in the add-account form and is immutable
  after account creation, including before any Play is armed. Do not expose
  capital-edit, top-up, withdrawal, adjustment or reset commands. Starting a
  fresh experiment/allocation means a new paper account; keep prior history.
- Reservations reduce available capital, not equity. Closing releases only
  margin belonging to the closed quantity. Do not count reserved notional as a
  balance or subtract margin as a realized loss.
- A loss can make remaining pending entries unaffordable: revalidate their
  admission under the account lock, suspend affected entries and disclose the
  shortfall. No implicit credit or new capital.
- Do not cap a gap loss at the initial margin or clamp a negative cash balance
  to zero. Record a deficit and block new entries; this is not a simulated
  liquidation claim.
- Mark unrealized P&L with its source/time. Stale or missing marks leave current
  equity unknown/partial, not equal to cash by assumption. Cash stays known.
- Preserve exact decimal strings and existing precision checks end to end.

## 8. Worker integration and result provenance

Add an opt-in standalone `paper` host path to the existing worker binary. It has
no command queues or event subscriptions, so it does not need Wolverine,
RabbitMQ, envelope storage or paper messaging provisioning. Existing transport
modules keep the [module/deployment contract](messaging-and-workers.md#add-a-worker-module-and-deployment)
unchanged; paper cannot alter their registered module sets. Its evaluator and
ledger rules belong in Domain/Application; venue transports remain Infrastructure.
Do not add business-facing contracts inside an adapter.

The module needs owner-directory admission, durable scheduling/progress,
lease fencing and account/Play version checks. Share public candle reads where
possible, while keeping private plans, subscriptions and decisions owner-scoped
and respecting existing deployment-wide venue capacity/deadlines. Suspended
owners must not produce new simulated results.

Commit execution, capital movement, remaining quantities, lifecycle update and
source checkpoint atomically. Duplicate
candles, redelivery and a second replica must not duplicate P&L postings or an
execution. Here the ledger movements are simulated P&L, never a trading fee or
owner capital adjustment. A stale lease, disabled account or superseded plan
cannot commit.
Store the triggering observation/source identity and assumptions needed to
explain each decision; do not put every market tick on the durable event bus.

Extend health, deployment documentation and isolated checks. The paper-only host
runs separately from existing monitor/notifications deployments, with a productive
scheduler heartbeat even when no owner has paper enabled. It still needs explicit
journal migrations. Paper event/notification delivery is deferred; paper-state
reads supply UI progress, not fake real execution events. Do not present
simulations as real fills or real missing-protection incidents.

## 9. Statistics: reuse presentation, never pool the populations

Paper mode should offer the existing eligible closed-Play totals, win/loss/
scratch counts, unlevered returns, estimated initial R, periods, groups,
structured reviews and exclusion drill-down. Use the modeled exit candle time,
not the hourly worker detection time; disclose candle precision rather than
invent exact touch times. Preserve exact-zero scratches, tie disclosure,
the 20-decided window and consecutive-recovery rules.

Paper sizing may use its own closed paper Plays and virtual available capital;
it never borrows the real account/Activity record or real balance. Real manual
accounts remain real and keep their current eligibility. A paper account with
no outcomes says “No paper record”, not “From Activity” using real executions.
The existing owner risk preference is shared read-only for v1; the paper risk
control cannot write it. Paper outcomes still compute a separate ladder/record/
limits. This is an engineering simplification, not another owner scope decision.

Paper Activity presents simulated executions/operations with explicit source;
do not insert them into imported facts to reuse today's operation reader.
Reuse pure aggregation where its assumptions hold, otherwise adapt paper facts
at the application boundary. Zero simulated costs do not prove real venue
cost/continuity coverage. Both modes can share components and formulas
without sharing queries or pretending their evidence is equivalent.

V1 has one fixed touch/zero-cost model, so it needs no fee-scenario filters.
Different starting allocations can live in separate accounts and use the
existing account/portfolio filters. Paper equity progression belongs in paper
account detail; the existing closed-Play result curve must not be relabeled
as account equity.

## 10. Delivery in bounded PRs, after approval

1. **PT1 — isolation foundation.** Mode/membership constraints and explicit
   real-default query scopes; mode-aware contracts and negative tests for every
   financial/count/coverage reader and real-only write path. Existing users and
   fixtures remain real. Paper creation stays unavailable.
2. **PT2 — paper capital and deterministic engine.** Creation-only capital, ledger,
   reservations, simulated executions, source/model provenance and evaluator
   against synthetic candle windows. Prove full level executions, partial planned
   exits, zero fees/slippage, immutable starting capital, revisions, concurrency,
   gaps and exact arithmetic without live venue data.
3. **PT3 — Hyperliquid monitoring and workspace.** Reuse and verify the existing
   Hyperliquid candle reader; wire hourly/configurable scheduling, shared window
   reads, the opt-in worker, health/catch-up, paper portfolio/account creation
   and the existing Play workspace. Include candle coverage/ambiguity contract
   tests rather than introduce a continuous price stream.
4. **PT4 — paper reporting and release verification.** Paper Activity/Review/
   sizing, exclusions, mode switching and isolated browser/packaged checks.
   Deliver PT1–PT4 before calling the requested feature complete.

There is no remaining-venues phase: paper accounts are Hyperliquid-only.

Not included: a strategy-definition system, historical backtesting, optimization,
real order execution, a general matching engine, configurable fees/slippage,
capital adjustments/resets, other venues, deployment or owner-database migration.
Any schema change is applied explicitly to disposable verification databases
first, never to the owner's database as a test.

## 11. Acceptance cases that protect the concept

- With paper disabled, today's real UI/API results and behavior stay unchanged.
- A paper-only owner can use the playground without linking a real account.
- Creating, winning, losing, disabling or moving a paper
  account changes no real totals, counts, coverage, filters, eligibility,
  sizing-source choice or exposure ladder.
- Choose starting capital in creation, then reject every attempt to change it,
  including before the first Play. No top-up/withdrawal/reset route can bypass
  this. Multiple paper accounts can start with different allocations.
- Hyperliquid is the only paper venue; no real wallet/source/credential is
  needed, and another venue cannot be supplied through a direct API request.
- Cross-mode and cross-owner membership, Play links, direct API writes and
  imports fail safely. Deleting a nonempty paper portfolio retains everything.
- Two simultaneous Plays cannot reserve the same capital; no execution charges
  fees or applies slippage; partial exits and sibling stops cannot over-close.
- Equality/crossing at an entry/TP/SL executes its whole eligible allocation
  at the defined level. A jump through a stop still uses that level, not the
  observed worse price. No limit/stop entry-type UI is required.
- An entry and exit cannot be invented from an unordered candle range.
- A single hourly check finds an intervening touch even when price has reversed
  by check time. Read the whole unevaluated window once per shared instrument,
  not one latest-price sample or one request per Play/level.
- Changing the Paper check interval changes paper due work only. Real monitor
  timing stays unchanged; normal hourly waiting is not reported as a data gap.
- An arming/revision boundary inside a candle cannot reuse pre-boundary extremes.
  Same-candle entry/exit or TP/SL ambiguity cannot silently produce a scored win.
- A post-arm price can open and close a paper Play with the browser closed,
  while corresponding real Plays and imported facts remain untouched.
- Duplicate delivery, two replicas, restart, changed plans, disabled accounts,
  suspended owners and lease loss cannot duplicate or apply stale decisions.
- Missing source coverage produces a visible gap, not a lucky assumed result.
  An unresolved outcome is explained in paper exclusions.
- The immutable 4,000 seed never counts as profit. Missing prices,
  zero capital, negative cash and insufficient margin remain honest.
- Paper and real fixtures with equivalent outcome inputs exercise the same
  pure metric formulas while retaining different provenance and query scopes.
- Mode switches with in-flight requests, desktop/mobile themes and saved
  drafts keep the approved workspace layout and never mislabel results.

## 12. Confirmed owner choices and remaining technical defaults

The October 20 follow-up settles these product choices:

- **Capital:** set it only when adding the account; immutable afterward.
  Use multiple paper accounts for different starting allocations.
- **Execution:** price touches execute the full level allocation at its
  defined price. **No fees and no slippage.**
- **Venue:** **Hyperliquid only**, not a multi-venue feature.
- **Cadence:** check once an hour by default, with a configurable interval to
  reduce Hyperliquid load; no need for continuous checks.
- **Ambiguity:** flag same-candle uncertain touches rather than invent the
  sequence or score a guessed result.
- **Purpose:** a simple tool for recording hypotheses and seeing a possible
  plan outcome, not a realistic broker/exchange emulator.

The detailed candle resolution/coverage, activation boundary, same-candle
ambiguity policy, shared reads, capital reservation and data-gap behavior above
are proposed engineering defaults to make that simple model deterministic and safe. They are not
additional recorded owner answers. The earlier adjustable-capital, configurable
costs, realistic stop-gap pricing and broader venue suggestions are superseded.

The owner subsequently authorized implementation and delegated Sol work and
verification. Do not reopen the settled capital/cost/venue/cadence choices or
add realism features as prerequisites.
