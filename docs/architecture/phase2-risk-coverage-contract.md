# Phase 2: risk and coverage

October 21, 2026. The owner asked for Phase 2 of the October 18 expert-trader
review. The review is not committed to this repository; defect, finding and
decision numbers (D-xx, F-xx, G-xx, I-xx, V-xx) are its own. This contract
bounds the delivery. It does not authorize Phase 3 or later work, deployment,
image publication or any change to the owner's database.

Phase 2 does not change:
- the scored result basis (decision 1, the venue result);
- fee-only losses counting as losses (decision 8);
- the ladder thresholds, the pooled ladder, the 20-decided window or the
  exact-zero scratch rule;
- monitor severity by uncovered fraction, or re-announcing a worsened incident
  (the policy half of decision 10).

The owner answered on October 21 (see [Owner decisions](#owner-decisions-october-21-2026)).
Every item below is authorized except **item 12**, the external critical
channel and dead-man check, which the owner deferred. AGENTS.md's rule that
external channels are out of scope therefore still stands.

## Rules for every item

- **Phase 0's and Phase 1's rules apply:**
  - venues stay read-only, and a price touch is never a fill;
  - unknown stays null and is never shown as zero;
  - financial values stay exact decimal strings;
  - capabilities, never venue names, drive behaviour;
  - the approved section positions do not move; on Overview, Allocation stays
    between the metric tiles and Summary;
  - colours come from theme tokens;
  - migrations are applied explicitly;
  - every executed probe becomes a test that fails before its fix;
  - cases, not rates: no rate below its own n = 10, no ranking, nothing
    AI-written.
- **Risk is never netted.** Risk is the loss if stops fill. Long and short are
  shown separately and never subtracted from each other. A stop at or beyond
  its entry adds zero risk; it never becomes negative risk that offsets
  another stop.
- **Limits are the owner's.** Every limit is an owner setting with no default
  value. A limit is recorded and flagged, never enforced. Plan it is never
  blocked, and saving a Draft never requires anything new.
- **Exports.**
  - They are owner-scoped and hold exact strings.
  - Text cells are neutralized against spreadsheet formulas.
  - They never contain credentials, tokens, session data or storage keys.
  - They say they are not a tax report.
- **New facts stay beside fills.** Forced events, venue risk observations,
  coverage ranges and import conflicts are kept next to imported fills. They
  never become fills, order links or scored results, and they never fill a
  missing amount with zero.
- **Trading mode.** New Phase 2 reads follow the paper-trading contract:
  - `?tradingMode=real|paper`, with real when omitted;
  - counts are scoped before aggregation;
  - a mismatched account or Play is not found.

  Open risk, retention, backfill, forced events, venue risk context, funding
  and the raw-facts export read actual venue facts, so they are real-only: a
  paper request is refused with 409 before any read, like the other
  actual-only routes. The week page, the Review CSV, the if-then rule, search
  and the one-Play bundle work in the mode they are asked for.
- **Migrations.** New migrations order after
  `20261020155750_PaperTradingFoundation`. Every migration-bearing pull
  request stays in one linear chain, so model snapshots never conflict.

## 1. Open risk (heat)

Covers G-06 and M3. No decision; the limit value is the owner's.

**What it counts.**
- The population is the owner's Planned and Open Plays on enabled real
  accounts, venue and manual.
- Paused Plays have withdrawn their orders. Unresolved Plays are stuck. Both
  are listed by count as "not included", never as zero risk.
- Drafts are not counted.

**The risk of one Play** is the loss if every current stop of the current plan
fills, in the instrument's quote asset. It has two separate parts.
- **Open part.** For each entry with fills (linked fills for venue Plays, the
  manual ledger otherwise):
  - its remaining open quantity is its filled quantity minus the exits
    attributed to it, measured from the entry's exact average fill;
  - an exit linked to one of the entry's stops or targets is the entry's;
  - a position-level exit (an unplanned venue exit, or any manual exit)
    belongs to the entry only when it is the Play's only filled entry;
  - the remaining quantity is shared by the entry's stops whose linked
    orders have not executed, in proportion to their shares. A stop that has
    already filled has done its part and is not counted again.
- **Planned part.** For each entry, its planned quantity not yet filled, at
  its planned price, against every stop of the entry. That is the whole entry
  before its first fill, and the remainder after a partial fill. The planned
  quantity is derived as the editor derives it.
- **Per stop.** Each stop's quantity times the distance from the reference
  price to the stop on the loss side. A stop at or beyond the reference adds
  zero.
- **Unknown.** The whole Play is unknown when any part cannot be measured:
  - a stop share without a price;
  - stop shares that do not total 100%;
  - a quantity the plan cannot derive;
  - an exit that cannot be attributed to one entry;
  - exits that exceed their entry;
  - a linked fill that contradicts its role;
  - a quote asset that is not a listed nominal USD stablecoin;
  - a manual execution that cannot be read.

  An unknown Play is counted by direction and never partly summed.

**What it shows.**
- By direction, the known risk in nominal USD and as a share of the scope's
  balance, plus the unknown count.
- The total is long plus short: a sum of risks, never a net.
- The figures are labelled as "at your plan's stops". Beside them, Open venue
  Plays are counted from 0.4c's recorded coverage evidence:
  - only confirmed absence (a fresh known "not covered") counts as not
    covered;
  - shared protection counts as covered;
  - unknown, unsupported (such as "current book not read"), stale and
    not-yet-checked coverage are counted separately as unverified.
- **The scope** is the Overview scope (All, portfolio, Unassigned or account).
  For a Play, it is its portfolio, or the account itself when unassigned. The
  balance is the workspace's exact enabled-account balance for that scope,
  with its coverage.

**This Play's increment.**
- Capital context gains one line, for example "Open risk: long 1.8% · short
  0.6% of Main balance · 1 unknown — this Play +1.0% (limit 3%)".
- The increment is this Play's own risk by the same rule, computed by the
  server from the plan being edited (`POST /api/risk/open/preview`) against
  the saved Play's executions, or none for a new Draft. It replaces the saved
  Play's own contribution, so an edited stop shows at once. The browser only
  adds and divides exact figures; it never computes risk itself.
- When an owner limit is set, the line says whether the total with this Play
  would exceed it. Nothing is blocked.

**Overview.** An "Open risk" indicator sits among the metric tiles for the
selected scope, with long, short and unknown. Allocation stays between the
tiles and Summary.

**The limit.** An owner setting in Settings, stored on the server per owner:
"Open-risk soft limit, % of balance". It is blank by default, and a blank
limit shows no limit line. This needs a migration.

**API.** `GET /api/risk/open?scope=…` returns:
- per-direction totals as exact strings;
- unknown counts;
- not-included counts with reasons;
- per-Play items with each Play's risk or its unknown reason;
- the balance and its coverage.

**Acceptance.**
- Three 1.25% longs on BTC, ETH and SOL show long 3.75% and short 0%.
- A planned 10-unit entry at 100 with its stop at 90, filled 1 unit, risks 10
  on the open unit plus 90 on the 9 still planned.
- A long with a managed stop at 105 over an entry at 100 adds 0, never −5.
- A long and a short of equal risk show both, never zero.
- A Play with a 50% unpriced stop is unknown and not summed.

## 2. Weekly review page

Covers G-11, N4 and G-13. **Owner decision:** one reporting setting.

- **The setting.** One owner setting, stored on the server: a reporting time
  zone (IANA) and a week start (Monday or Sunday). The defaults are UTC and
  Monday, so nothing shifts until the owner sets them. It is separate from
  1.6e's display zone, which stays display-only.
- **What follows it.** It decides the week page's boundaries and 10b's day and
  week boundaries. Activity's date range and Review's rolling periods keep UTC
  days and keep saying so.
- **The week page.** On demand, in Review, for a chosen week. It reuses
  Review's computations on the week's half-open range in the reporting zone:
  - the scorecard with its coverage and exclusions;
  - the process cases (1.4b);
  - the week's repeated mistake tags with drill-down;
  - the owner's open next actions;
  - the Needs review count;
  - the promoted rule's answers (item 3).

  A week without data says so, separately from a week without trades. No
  reminder, schedule or generated text is added.

## 3. One promoted if-then rule

Covers G-02 step 2. Builds on 1.5's list and copy.

- **The rule.** The owner promotes one next action, or writes one, as the
  single active rule: "If …, then …", with a link to its source Play when
  there is one. It is owner-scoped and versioned. Changing or archiving it
  never rewrites an earlier snapshot.
- **At Plan it.** The pre-flight dialog shows the active rule. The owner marks
  it acknowledged, or an exception with a reason. It never blocks. The answer
  and the rule's text and version are stored with plan revision 1, beside the
  pre-flight. Retrospective Plays have none.
- **In the review.** A closed, cancelled or unresolved Play whose revision 1
  carries the rule asks "Followed the rule?": followed, deviated or not
  applicable. The answer is stored with the structured review and its
  history.
- **In Review** and on the week page there are counts and drill-down only, and
  no rate below n = 10. A winning deviation stays a deviation.
- This needs small owner-scoped rule and snapshot records and a migration.

## 4. Earlier-order link candidates

Covers D-20. No decision.

- **Retention is unchanged.** Every order an order read returns is already
  retained (`PlayExecutionService.RetainAsync`). The cut at the Play's
  creation is in the reconciliation, matching and Execution-panel reads,
  which load and offer only orders placed at or after it. Nothing narrows the
  existing retention.
- **Candidates, never matches.** The automatic matcher keeps its rule (placed
  at or after the Play's creation). The Execution panel also lists compatible
  orders placed in a bounded lookback before the Play was created
  (`Vessel:Plays:EarlierOrderLookback`, 24 hours by default) as "Placed before
  this Play was saved". They are on the same contract, open, filled or
  triggered, and not linked to any Play. The owner may link one to a level
  through the existing link endpoint.
- **Consequences.** The existing badge and R rules apply to the actual
  execution times; an early placement alone changes neither. When the first
  entry fill precedes the plan, 1.4a's "plan written after first execution"
  badge appears and estimated R stays unknown for its existing reason. An
  order placed before the Play but filled after it was planned has a
  contemporaneous plan and keeps its R.
- No migration.

## 5. Export

Covers G-07, X9 and I11. Not a tax report.

**Common rules.**
- CSV is UTF-8 with a header row. Times are UTC ISO-8601; RISEx funding keeps
  its native nanoseconds.
- Numeric cells are plain decimal strings that pass the exact-decimal grammar,
  and are written as they are.
- Every text cell whose first character is `=`, `+`, `-`, `@`, tab or carriage
  return gets a leading `'`.
- Unknown is an empty cell, never `0`.
- **5a. Review CSV.** Review's current selection, one row per scored or
  excluded Play and imported position, with:
  - the venue and Play-local results, with their basis labels;
  - fees by token;
  - estimated R with its reason;
  - the exclusion reason;
  - origin, adherence, mistake tag and the post-hoc flag.
- **5b. Raw-facts CSV.** Per account and date range, from Settings → Data
  export:
  - fills, with native fill, order and contract IDs, transaction hash, side,
    position effect, price, quantity, fee, fee token, fee basis, venue P&L
    and P&L basis;
  - manual executions, with their provenance and source label;
  - funding settlements as a separate file.

  Enabled real accounts only, as Activity shows them.
- **5c. One-Play bundle.** A zip with:
  - `play.json`: the plan revisions, status history, links, linked fills or
    the manual ledger and its history, the review and its history, the log,
    the pre-flight and the rule snapshot;
  - for a paper Play, its typed simulated executions and simulation history
    (model version, gaps, declined entries, suspensions), read through the
    paper readers and never presented as actual fills;
  - each evidence original with its markup and note.

  It is streamed, and object-storage reads run outside any unit of work. It
  includes no credential, storage key or internal path.

## 6. Opt-in account history retention

Covers I-02, G-08, V3 and V7. **Decision 11 accepted.**

- **The opt-in.** Each real venue account gets a "Keep execution history"
  setting. An opted-in account is read on the monitor's schedule even without
  an eligible Play. Its reads come after protection checks in the shared
  venue capacity, which reserves headroom for protection checks.
- **The coverage ledger.** Each account stores requested, covered and gapped
  time ranges per import, from the same continuity rule as 0.4b. A gap closes
  only when a read proves it is covered. A successful HTTP call alone never
  closes one.
- **Backfill.** Hyperliquid `userFillsByTime`, started by the owner per gap,
  is bounded by the venue's 10,000-fill reach and paced behind protection
  checks. Fills it finds go through the normal dedup and continuity rules. A
  backfill creates no Play, no thesis and no fresh-fill notification.
- **The no-stop check.** For an opted-in account: "open position, no resting
  stop, no Play". It is a warning-level discrepancy that follows the existing
  lifecycle and coverage rules, and the existing Hub rules apply to it.
- This needs migrations: the account setting, the ledger, and backfill
  progress.

## 7. Lighter forced events

Covers I-01, V1, D-16 and D-25. **Decision 11 accepted.**

- **Forced events.** Lighter liquidation, deleverage and settlement rows are
  retained as a separate forced-event fact:
  - kind, side, quantity, price when reported, time, and the native trade and
    market IDs;
  - nullable money: no reported or verified fee or P&L stays null;
  - an explicit exclusion reason.

  They are never fills, links or scored results. Ordinary fee-bearing Lighter
  trades stay omitted, so the Standard-account fee policy is unchanged for
  them.
- **Omission markers.** Every row an import omits (forced, fee-bearing or
  missing reported P&L) leaves a durable, deduplicated marker: its native
  trade and market IDs, time and omission kind, with no money. A later read
  that no longer returns the row cannot erase the evidence. When a venue
  gives no time or market for an omission, the marker records an explicit
  uncertainty range instead.
  - A marker also keeps the omitted row's native order ID when the venue
    gives one. A linked order with a marker has known missing executions
    whatever its status, under the shared rule, so its Play can neither
    settle nor be scored. The scored basis is unchanged.
  - RISEx's D-22 (from item 14a) lands here: one trade with an empty
    `realized_pnl` is set aside with a counted reason after its other
    fields validate, and the snapshot still applies. It is never zero.
- **Activity becomes gap-aware.** An account partition with omission
  markers or forced events after its earliest retained fill is not anchored
  across them; markers feed the aggregation, the detail reads and the
  version token. Affected operations read "direction unknown:
  executions omitted" instead of a phantom opposite operation. Forced events
  show as visible rows with their exclusion. Review, sizing and the ladder
  are unchanged.
- This needs a migration for the forced-event table.

## 8. Venue-reported risk context

Covers G-09, D-21 and V11. No decision.

- **8a. Snapshots.**
  - `VenuePosition` gains a nullable liquidation price, margin mode and
    maintenance requirement, as each venue reports them.
  - They are kept as append-only observations when a position appears, when
    a value changes, or when its market receives new executions, like
    `account_leverage_observations`.
  - The Execution panel and account detail show them with their observation
    age, labelled as the venue's, never mixed with the plan's isolated
    estimate.
  - Unknown stays null. This needs a migration.
- **8b. `protection.beyond-liquidation`.**
  - The condition: a linked stop resting at or beyond a fresh venue-reported
    liquidation price on the loss side. For a long that is at or below it;
    for a short, at or above.
  - Severity is critical. Its copy says the stop cannot act before
    liquidation.
  - It follows the discrepancy lifecycle and its confirmation, and needs Hub
    copy.
  - Cross-margin liquidation prices move with the account, and the copy says
    so.
- **8c. "Link this stop".**
  - A `protection.missing` or `protection.short` incident offers this when a
    compatible unlinked stop-mechanism order on the exit side rests on the
    contract.
  - It links through the existing endpoint, chosen by the owner and never
    automatic. When the price differs from the level, 0.5b's rebind follows.
- **Cadence (I-08).** The monitor panel states the cadence ("checked about
  every 10 minutes; not stop protection"), as copy only.

## 9. Funding

Covers D-23 and I-06.

- **9a. Resumable windows (no decision).** Funding is read in smaller windows
  that fit the venue's page budget. The account's stored contiguous funding
  coverage is the cursor, so no migration is needed. Each Refresh resumes
  until the 30-day window is covered. Coverage stays partial, and says so,
  until then.
- **9b. Beside a Play (no decision; after item 6).** Settled funding is shown
  on a Play only with proven sole exposure. That requires, over the Play's
  holding window:
  - affirmative execution coverage from item 6's coverage ledger: the window
    is covered, with no gap;
  - every retained fill on that account and resolved native contract belongs
    to the Play, and none of its orders has known missing executions;
  - no omission marker or forced event (item 7);
  - an evidenced flat boundary before the first entry and, once closed,
    after the last exit, anchored to a snapshot or to covered history.
    Activity's unanchored zero start is never proof;
  - complete funding coverage;
  - no ambiguous timestamp bucket.

  Otherwise it reads "shared, not allocated", or "coverage incomplete".
  Funding is never added to a result, and no basis moves from Unknown.
- **9c. A slow schedule (owner accepted).** Inside the existing monitor
  worker, every 6 hours by default (`Vessel:Funding:Interval`). It covers only
  accounts with an Open venue Play or retention turned on. It runs after
  protection checks and uses 9a's cursor.

## 10. Planning assumptions and soft limits

Covers G-10, G-17 and M9.

- **10a. Assumptions (no decision).**
  - The owner enters a round-trip fee and a slippage percentage per venue, in
    Settings. There are no defaults; with none set, nothing shows.
  - The Summary shows "Assumed costs ≈ −0.18R (fees 0.09%, slippage 0.05%)"
    beside the gross figures, never inside them.
  - Sizing and Review do not read them.
- **10b. Soft loss limits (follows item 2's setting).**
  - The owner sets daily and weekly loss limits in R. They are blank by
    default.
  - The day and week follow the reporting setting. The day's or week's loss
    is the sum of estimated R of real Plays closed in it, with a count of
    closed Plays without R, which is never treated as zero.
  - A breach is flagged in capital context and Suggestions.
  - At the next Plan it, the pre-flight shows it as a check. The owner may
    add a cooldown note, stored with revision 1.
  - Nothing is blocked.

## 11. Shared-exit quantity allocation and flip splits

Covers G-18 and C2. **Owner decision accepted.** Effort L; builds on 1.3a.

- **The allocation.** The owner allocates one exit fill's quantity across the
  Plays it closed, or splits a flip fill into one Play's exit and another
  Play's entry. Allocations conserve quantity: they sum exactly to the fill.
  They are version-checked, reasoned and atomic. An Unresolved Play may
  receive one.
- **Fees** are split pro rata by quantity, exactly. The remainder is assigned
  deterministically and disclosed.
- **Result.** A Play settled through an allocation can close. Its Play-local
  result is shown. Its venue result is "not allocated", because the venue's
  average-cost P&L cannot be split, so under decision 1 it is excluded from
  scoring with that reason.
- This needs a new allocation record, a migration and an update to
  `play-lifecycle.md`.

## 12. External critical channel (deferred)

The owner deferred it on October 21. Nothing is built.

## 13. Journal-wide search

Covers G-12. **Owner decision accepted:** a separate projection.

- **The projection.** An owner-scoped search projection over the fields
  below. It is written in the same unit of work as every change to them:
  Play saves and reviews, log appends, evidence note edits and deletions,
  lifecycle changes from venue facts, manual corrections and paper
  evaluation. A one-time initialization fills it for existing Plays. Results
  and outcomes reuse the current eligibility rules. The fields are:
  - title, thesis, invalidation, strategy and evidence notes;
  - the review note, lesson, mistake tag and next action;
  - log entries;
  - image notes.
- **The endpoint.** `GET /api/plays/search?q=` returns Plays with the matching
  field and a short excerpt, paged like Review's drill-downs. The paged Plays
  list is unchanged.
- **F-07.** The same projection carries each ended Play's real exit time,
  result and estimated R, so the Plays list can show them without reading
  plans.
- This needs a migration.

## 14. Venue robustness and a benchmark

No decision.

- **14a. RISEx.**
  - D-22 moved to item 7 (Sol round 1 on 14a): a set-aside trade must keep
    its order, which needs the durable omission markers. Until then a trade
    with an empty `realized_pnl` still fails the read.
  - F-13: paging uses a fixed end-time cut with overlap.
- **14b. Hyperliquid.**
  - F-11: prices follow the venue rule through a venue-provided price rule,
    in instruments, rounding and matching. The rule is at most 5 significant
    figures (integer prices are always allowed, whatever their length) and
    at most `6 − szDecimals` decimals. Typed prices keep today's policy.
  - F-14: a wallet-read failure degrades only the wallet, and only on
    default-mode accounts. Unified and portfolio-margin accounts still need
    it.
- **14c. Fill conflicts (F-12).** A repeated fill ID with changed values keeps
  the first stored copy, is never overwritten, and is recorded as an import
  conflict with the changed field names, never raw payloads. Account detail
  shows it. This needs a migration.
- **14d. Benchmark.** A script generating a multi-year synthetic dataset in a
  disposable database. It times Activity, Review, the Plays list, Overview,
  open risk and search, so it is delivered last, after item 13. Results are
  recorded in the state document; nothing runs in CI.
- **D-19.** Accepted as its own pull request. A persistent undersized-stop
  condition confirms on a condition invariant that survives changing
  quantities, and keeps the changing evidence. Severity and re-announcing are
  unchanged.

## Probe and finding scenarios as tests

| Source | Scenario today | Test after the fix |
| --- | --- | --- |
| G-06 | Three 1.25% longs: nothing shows the combined risk | Long 3.75%, short 0% |
| Round 3, managed stop | A stop at 105 on a 100 long counted as a $5 loss | Adds 0 risk |
| D-20, `PlayExecutionService.cs:376` | An entry placed before the Play was saved is retained but excluded from automatic matching and the Execution panel's candidate reads | Offered as an earlier-order candidate |
| `correctness-dotnet/Activity.cs` C6 | Buy, omitted liquidation, buy 2, sell 2 → phantom short | "Direction unknown: executions omitted" |
| `verify-new-dotnet/NewVerify.cs` (Lighter) | A liquidation is only counted | Kept as an unscored forced event |
| V4 | 720 hourly funding rows against a 500-row budget | Resumed over Refreshes until covered |
| V5 / D-22 | One empty RISEx P&L aborts the read | Row set aside; snapshot applied |
| V6 / F-14 | A wallet failure aborts the perps read | Perps applied; wallet degraded |
| V9 / F-13 | RISEx pages shift between requests | A fixed end cut |
| V10 / F-12 | A changed duplicate fill is silently ignored | Conflict recorded, first copy kept |
| C9 / F-11 | HL 0.051234 at `szDecimals = 1` does not link | Rounded to the venue rule; links |
| C12 / D-21 | A 50× stop beyond liquidation reads as protected | `protection.beyond-liquidation` |
| `correctness-dotnet/Exec.cs` `SharedExit`, `FlipOrder` | Both Plays stay stuck | Allocated; both close, unscored |
| M2 / D-19 | An undersized stop whose quantity changes never confirms | Confirms on the invariant |

## Owner decisions (October 21, 2026)

| Question | Answer |
| --- | --- |
| Order and stacking | Yes: 2a, then 2b, as one linear chain. |
| Limit defaults (items 1 and 10b) | None; blank shows no limit. Stored on the server. 24-hour earlier-order lookback; critical severity for `protection.beyond-liquidation`. |
| Reporting zone and week start (items 2 and 10b) | One owner setting. |
| Decision 11 (items 6 and 7) | Yes, as recommended. |
| Funding schedule (9c) | Yes, slow, inside the monitor worker. |
| Shared-exit allocation (11) | Yes, as recommended. |
| Decision 12 (item 12) | Deferred. |
| Search scope (13) | Yes, a separate projection. |
| If-then rule (3) | As recommended: one active rule. |
| D-19 | Yes, as its own pull request. |
| Phase 2 findings outside the 14 items | I-08's cadence copy with item 8 and F-07 with item 13. F-15 and G-22 wait. |
| Verification | One adversarial Sol (Codex) review per pull request. |

## Delivery

Small pull requests against `sloppitty/vessel`, in one linear chain. Each pull
request's diff stays within its own item.

```text
main (Phase 1 merged October 21) → contract + state (this document)
  2a: 1 open risk (migration) → 4 earlier orders → 5a Review CSV → 5b raw facts
      → 5c bundle → 8a risk observations (migration) → 8b beyond liquidation
      → 8c link this stop → 9a funding cursor → 10a assumptions
      → 14a RISEx → 14b Hyperliquid → 14c fill conflicts (migration)
  2b: 2 week page (migration) → 10b loss limits (migration) → 3 if-then rule (migration)
      → 6a retention and coverage ledger (migration) → 6b backfill (migration)
      → 6c no-stop check → 7a forced events and omission markers (migration)
      → 7b gap-aware Activity
      → 9b funding beside a Play → 9c funding schedule → D-19 confirmation
      → 13 search (migration) → 11 allocation (migration) → 14d benchmark
```

Progress is recorded in
[phase2-risk-coverage-state.md](phase2-risk-coverage-state.md) as each part
is verified. The contracts these items touch change with the code, and only
as far as the owner's answers allow:
- `plays-workspace-contract.md`;
- `core-workspace-contract.md`;
- `play-lifecycle.md`;
- `portfolio-review.md`;
- `activity-operations.md`;
- `venue-funding.md`;
- `play-monitor-plan.md`;
- `messaging-and-workers.md`;
- `venue-instruments.md`;
- `risex-integration.md`;
- `lighter-delivery-contract.md`;
- `list-paging.md`;
- `appearance-settings.md`.

Process:
- `scripts/check.sh` runs before each pull request, with the pinned SDK and
  pnpm.
- PostgreSQL and RabbitMQ tests run against disposable containers.
- The state document and changelog go in a separate commit after the check
  passes.
- Each pull request gets one adversarial Sol (Codex) review before merge.
