# Portfolio review

October 9, 2026. The owner approved implementation of the
[visual proposal](../design/portfolio-review/README.md) and requested two
independent Sol verifications. Review now follows Plays in the main navigation.
The Play workspace layout is unchanged.

## Scope

Review reports saved, closed Plays from their linked imported execution facts
or explicit manual execution ledger.
It does not assign fills, change lifecycle, sync a venue, invent a trading
thesis or persist a score. No sample results appear in the production page.

October 13 adds owner-confirmed retrospective Plays through the Activity
preview/confirmation workflow. Only complete unassigned round trips that can
be linked as whole orders are eligible. The record has an explicit retrospective
origin, no invented plan and no initial-risk R. Return still means net divided
by executed entry notional, not margin. Unassigned fills remain coverage, not
scored Plays, until that confirmation. See
[the diary contract](diary-completion-contract.md).

Review exclusion counts open a paginated list of affected Plays and concrete
reasons using `GET /api/review/exclusions` with the same filters. Reasons
identify offending order/fill IDs where available. Each record has at most
100 reasons, including an explicit omitted-count notice when necessary.
Source labels distinguish imported and manual facts. Structured review answers
count as reviewed only when saved, as does existing nonblank text.

All portfolios includes enabled accounts without a portfolio, once each.
Disabled accounts are excluded. Historical grouping follows current portfolio
membership. Asset grouping uses the exact stored contract key: multipliers
are preserved and aliases are not guessed.

## Read contract

Authenticated, owner-scoped `GET /api/review` accepts:

| Parameter | Values |
| --- | --- |
| `period` | `day`, `week`, `month` (default), `year`, `all` |
| `portfolio` | `all` (default), `unassigned`, or portfolio UUID |
| `accountId` | Optional account UUID; intersects the portfolio filter |
| `instrument` | Optional exact contract key, at most 128 characters |
| `offset` | Detail-page offset, default 0, maximum 1,000,000 |

Periods are rolling 24 hours, 7, 30 and 365 days, or all retained results.
The start is exclusive and the report time inclusive. A Play belongs to its
latest linked exit timestamp, not the time a refresh detected closure.
Partial exits on still-open Plays are not part of this report.

The response contains filter options, all five period totals, metrics, chart
points, portfolio/account/asset groups, coverage, and up to 50 contributing
Plays with a `nextOffset`. Every request reads a fresh PostgreSQL repeatable-read
snapshot. Pagination is not a frozen historical session; sync/link edits can
change membership between pages. Unknown or out-of-scope identifiers return
an empty selection rather than another owner's data.

Financial fields are invariant decimal strings. The server calculates totals
and metrics over the full eligible set, not the current detail page. The browser
formats exact strings; floating-point conversions are limited to visual
geometry/sign. Even the displayed chart minimum uses an exact comparison.

This first single-user implementation loads retained closed-Play facts into
memory. There is no silent history cap, but very large histories will need
database-side reporting before the memory/query cost becomes acceptable.
The original Review delivery needed no migration. The October 13 manual and
retrospective workflows use the explicit DiaryCompletion migration. There is
no financial cache, background job or new package.

## Eligibility and coverage

A scored imported Play must have distinct linked fills with:

- Both entry and exit quantities, all positive, with positive prices.
- The Play's account and contract on every fill.
- Entry effects `open`, exit effects `close`, consistent opposing sides.
- Equal entered/exited quantity and no negative running position, grouping
  fills with the same timestamp together.
- No future execution timestamps.
- A usable outcome and complete reported fees.

Review also checks retained linked orders. A fixed-size filled order establishes
a minimum executed quantity equal to its original size. If fewer fills are
retained, the Play is excluded even when
its retained entry/exit subset happens to balance. This also catches executed
linked orders with no retained fills. Cancelled/rejected/triggered/other states
do not establish execution through a size change alone. Dynamic position TP/SL
orders also do not establish this fixed-size bound. A normalized, reliably
reported executed-quantity field would allow checking partially filled cancelled
orders later; the current shared order contract does not have one.

Balanced fills and no known order discrepancy still cannot prove full venue
history. Coverage states that retained history may be bounded. The earliest
retained fill is not a guarantee of continuous coverage. Exclusion counts use
the available exit time, falling back to detected closure when no exit exists.
Open-Play counts describe current state, while unassigned fills are counted
within the selected time and account/asset scope.

## Calculations

| Metric | Rule |
| --- | --- |
| Net P&L | Sum reported closed P&L, subtracting listed nominal stablecoin fees (see below) only for gross-basis fills. Net-of-fee fills are never charged twice. Nonzero gross fees in unsupported tokens exclude the Play. |
| Win / loss / scratch | Positive / negative / exactly zero net result. |
| Batting average | Wins / (wins + losses). Scratches excluded. |
| Play return | Net P&L / executed entry notional × 100, not return on margin or account equity. |
| Average gain / loss | Mean winning return / absolute mean losing return. Each Play has equal weight. |
| Profit factor | Sum winning P&L / absolute sum losing P&L. |
| Expectancy | Mean net result including scratches; descriptive, not a forecast. |
| Drawdown | Largest drop from a prior peak in cumulative closed-Play P&L, starting at zero in the selected window. Not equity drawdown. |
| Streaks | Consecutive positive/negative outcomes in close-time/Play-ID order. Scratches do not break them. |
| Estimated R (planned-stop model) | Net P&L / an initial planned-risk estimate, only when the plan qualifies below. Shown with its denominator, "n of N with a complete initial stop". |
| Reviewed | Saved nonblank review text or at least one answered structured field. Does not establish plan adherence or decision quality. |

Missing denominators are null/N/A, not infinity. Empty performance is null,
distinct from a real zero result. Aggregate batting, payoff and profit factor
pool individual outcomes; they never average group percentages.

Amounts are nominal USD. Separate funding cashflows are not added. Whether
RISEx's reported realized P&L embeds funding remains unverified, so the UI
does not promise funding-free results. Stablecoin FX and transfers are not
part of this calculation.

October 18, 2026: executed entry notional is the exact sum of entry fills'
quantity × price products. A notional beyond exact decimal precision or range
excludes the Play as an arithmetic reason under incomplete executions; it is
never rounded into a different return.

### Planned-risk estimates

`PlayOutcomeCalculator` extracts the existing sizing arithmetic unchanged.
Review adds a stricter R eligibility check: a plan revision must exist at or
before the first entry, match the executed direction, allocate 100% of entry
shares, and give every entry positive prices and protective-side stops whose
positive shares total 100%. Otherwise R remains unknown.

The estimate still uses executed entry notional multiplied by the plan's
share-weighted stop distance. It is not a reconstruction of actual risk at
each fill. The sizing assistant retains its existing retrospective fallback,
partial-plan estimate and last-20-decided-Play window; Review does not alter it.

### Charts

Daily net results and cumulative P&L use UTC dates. Histories longer than
366 dates use labeled multi-day buckets so every outcome is included in at
most 366 points. Drawdown charts use bucket-closing values and can hide
within-bucket losses; the drawdown metric uses each closed Play. A text table
exposes chart values without pointer interaction.

## Browser behavior and verification

October 13 integrity correction: known missing fixed-size filled-order executions are now read by `SizingStore` and carried with `ClosedPlayFacts`, so lifecycle, sizing and Review share the same missing-execution rule. Review reuses the execution chronology check and retains its stricter positive quantities/prices, explicit open/close effects, account/instrument identity, complete-fee and initial-risk eligibility. This does not add retrospective attribution, change the reporting window or import funding.

Changing filters aborts superseded requests and hides old metrics until the
new response arrives. Portfolio changes clear the account filter. Removed
portfolio/account/asset options remain visibly unavailable while selected.
Errors do not fall back to mocked data. Definitions and coverage carry the
long explanations; controls retain the compact proposal layout.

Contributing Plays open through the shell's existing saved-Play loader.
Unsaved drafts are preserved, including leverage-only changes and blank entry
structure edits. Closing a dialog aborts a pending open and restores focus.

Regression tests cover service arithmetic, periods, scope, incomplete facts,
fees, risk provenance, bucketing, pagination, authenticated endpoint behavior,
actual PostgreSQL owner isolation/order completeness, API validation, exact
formatting, request races, filters, dialogs and draft-safe navigation.

`scripts/review-smoke.cjs` checks the production UI against an isolated real
API/database. October 9 captures use 525 synthetic closed Plays, never private
account data. The check covers all periods, filters, pagination, charts,
failure/retry, focus restoration and widths 320–1512. Axe reports no violations
at 390 and 1512 pixels. See
[captures and verification receipt](../design/portfolio-review/implementation/).

Both Sol reviewers' findings were addressed: missing known order executions,
initial-risk provenance, funding wording, plan-only draft protection, a removed
asset filter and exact drawdown-caption selection.

Final checks on October 9: 689 backend tests passed against PostgreSQL 18,
with only the opt-in live RISEx test skipped; 483 frontend tests and 76
prototype tests passed. Locked .NET restore, Release build, frontend build,
TypeScript and lint passed. Vite still reports its large-main-chunk warning.
Both Sol reviewers confirmed their findings closed on targeted recheck.

## Nominal stablecoin pooling (October 14, 2026)

Fees in any stablecoin of the Application `NominalStablecoins` list (USDC, USDG, USDT, USDT0, USDE, USDe and USDH) are deducted one-for-one, so USDG fees from Lighter Robinhood no longer exclude a Play; other tokens remain unsupported. Results from venues settling in different listed stablecoins pool at nominal 1:1. The review notice and the Scope explanation state the caveat: no FX or depeg valuation.

## Result bases (October 19, 2026)

Phase 0 item 0.3 ([contract](phase0-trust-correctness-contract.md#03-result-basis-receipt);
D-01 display, F-09, I-04, I-05). The scored basis does not change: Net P&L,
win/loss/scratch, every metric, the chart and the groups still use the venue
result above (owner decisions 1 and 8). Review now shows a second basis beside
it.

- **Play-local result.** Hyperliquid and Lighter realize P&L at the venue
  position's average cost, so with concurrent Plays on one instrument, or a
  position opened outside Vessel, a Play's venue result can take another
  position's entry price and even the wrong sign. The Play-local result uses
  only the Play's own linked fills: `direction sign × (Σ exit quantity × price
  − Σ entry quantity × price) − Σ trading fees of every linked fill`, that is
  sell proceeds minus buy costs for either direction (a short sold at 100 and
  bought back at 90 gains 10), with exact products and both P&L bases, so a
  RISEx fee is charged once. The browser describes it as "sell proceeds minus
  buy costs, less every trading fee". It has no
  funding by construction and is never called total profit. The shared
  `PlayLocalResult` calculator in `Vessel.Application.Plays` serves Review,
  sizing and the Execution panel.
- **When it is known.** The Play is closed and `ExecutionIntegrity.Settled`
  (balanced, in order, nothing contradicting, no known missing executions);
  every fill is on the Play's account and instrument with a positive price and
  quantity; the instrument comes from the venue catalogue and the venue
  descriptor's `QuoteAsset` is on the `NominalStablecoins` list (every
  registered venue module reads linear perpetuals quoted and margined in that
  asset: Hyperliquid primary perpetuals and RISEx in USDC, Lighter in its
  collateral USDC or USDG; Review, sizing and the execution view read no
  venue catalogue, so they use the descriptor's quote, which every current
  catalogue instrument shares); and every fee is verified (`reported` or
  `standard-account-free` basis, a non-blank token, and that token listed or
  the fee a known zero). Otherwise it is null with a short reason, and a result
  needing more than 28 decimal places is unknown, never rounded. A manual
  ledger is a linear USD-quoted contract by its own definition, so its
  average-cost result is the Play-local result, labelled "Manual ledger"; it
  equals its scored result.
- **Rows.** `plays[].local` is `{ netUsd, basis, reason, signDiffers }`:
  `basis` is `executions` or `manual-ledger`, `netUsd` an exact string or null
  with `reason`. The contributing-Plays table shows Net P&L with its basis
  ("Venue result" or "Manual ledger") and a Play-local column; an unknown one
  reads "Unknown" with the reason in an InfoTip. Both per-Play figures use
  adaptive precision: a non-zero value that would round to "$0" keeps its sign
  and leading digits (−0.00001 shows as "-$0.00001"), so only an exact zero
  reads "$0".
- **Sign differs by basis.** Signs compare as negative, zero and positive.
  Zero against a nonzero result counts as a difference, since scratch is
  exactly zero and the class would change. A warning pill in the row flags it;
  `metrics.signDiffers` counts it and `metrics.localUnknown` counts Plays
  without a Play-local result, in every metrics block (overall and groups).
- **Fee-only losses (I-04).** `metrics.feeOnlyLosses` counts losses whose
  venue result before trading fees is zero or better: Σ venue closed P&L plus
  the listed stablecoin fees already inside net-of-fee P&L (the Activity
  before-fee rule). The sign is decided from the exact sum
  (`PlayOutcome.GrossResult` is an exact amount), so a before-fee result beyond
  decimal precision still counts. A net-of-fee fee in an unlisted token leaves
  that figure unknown: such losses are counted in `metrics.unknownGrossLosses`,
  not as fee-only. They still count as losses.
- **Funding.** Whether venue-reported P&L includes funding is unverified for
  every venue ([venue funding](venue-funding.md)), so a sign difference or a
  fee-only loss can reflect funding rather than the Play's own fills. The
  Definitions and both metric tips say so in one sentence.
- **R.** "Avg. realized R" is now "Estimated R (planned-stop model)", with "n of
  N with a complete initial stop" and the formula in an InfoTip; the per-Play
  column is "Est. R" with the same tip. The plan is the revision at or before
  the first entry fill. The arithmetic is unchanged.

The Average outcome card gains the sign-differs and fee-only-loss rows; nothing
moves. Older servers omit the new fields and the browser omits the figures.

## Imported positions (October 20, 2026)

The owner asked Review to use imported positions when there are no Plays, with
documented Plays weighing more, a toggle and a configurable threshold.

- **What is added.** Closed Activity operations whose fills belong to no Play
  (`playAssociation` `unassigned`), on the selection's enabled accounts and
  asset. Eligibility is the sizing fallback's rule, shared through
  `SizingService.CompleteOperationsAsync`: closed, known direction and net
  result, positive exact entry notional, no ambiguous identity, no known
  missing execution by the `OrderKey`/`MissingExecutions` rule, and an
  in-range return. Net is the operation's execution net (reported closed P&L
  less listed stablecoin fees on gross fills, never charged twice) and return
  is net over entry notional. Positions have no plan, R, review or Play-local
  result, are placed at their last exit and never become Plays or links.
  Linked and mixed operations are left out, so a Play is never counted twice.
- **Modes.** `GET /api/review` accepts `positions=exclude|include|auto`
  (default `exclude` for older clients) and `minPlays` (0–1000, default 5).
  `auto` includes positions while the selection (scope, asset and period) has
  fewer scored Plays than `minPlays`; 0 never includes them automatically.
  The decision applies to every period total in the response. The response
  carries `positions: { mode, included, minPlays, plays, positions, omitted,
  playWeight }`; `positions` and `omitted` (closed positions that cannot be
  scored, by their close time) are null when positions were not read. Plays and
  positions come from separate reads, so Review rereads the Play snapshot after
  the positions and retries (three attempts, then 409) if closed Plays, their
  linked fills or enabled accounts changed in between.
  Activity is resolved only when positions are read. The exclusions endpoint
  still lists Plays only.
- **Weight.** A Play weighs `PlayWeight` = 2 positions in batting average,
  average gain and loss (percent and USD), payoff and expectancy. Net P&L,
  profit factor, drawdown, streaks, the chart, groups' net and every count are
  real amounts and unweighted. With one kind only, the weighted figures equal
  the unweighted ones. `metrics.positions` and `periods[].positions` count
  positions among `count`; R, reviews and Play-local counts cover Plays only.
- **Rows.** A position row has `id: null`, `operationId`, `origin`
  `position`, a `<asset> Long|Short` title, null R and no Play-local result.
  It cannot open a Play.
- **Browser.** An "Include imported positions" switch beside the scope shows
  the server's decision with an Automatic/Manual pill; switching it sends
  `include`/`exclude` and "Use automatic" returns to `auto`. Drill-downs pin
  the current report's decision so a narrower selection never switches it.
  The threshold is a browser preference (`review.preferences.v1`, per user in
  Wallet mode) in the Review section of Settings, saved when a valid value
  is completed (blur or Enter) and reread after another tab changes it.
  Reset and the asset shortcuts keep the positions mode. Counts read "N Plays · M positions"; with
  positions the breakdown's count column is "Results", and the Play-local and
  R denominators count Plays only. The scorecard says Plays weigh 2× in rates.

## Sample sizes and plans written after execution (October 20, 2026)

Phase 1.4a of the [learning-loop contract](phase1-learning-loop-contract.md),
covering F-08 and the first part of D-17. No statistic changes.
- **Each metric names its own n.** The "Small sample" marker (below 20)
  follows that denominator rather than the Play count. The A5 case, 20
  scratches and one win, now reads "n = 1 decided · Small sample" under a
  100% batting average.

  | Metric | n |
  | --- | --- |
  | Batting average and profit factor | decided Plays (wins + losses) |
  | Average gain / loss | the wins and the losses |
  | Expectancy per Play | all scored Plays |
  | Estimated R | Plays with a complete initial stop |

  - The highest and lowest asset cards are marked by their result counts.
  - Breakdown cells are marked one by one: batting average and profit
    factor by the row's decided count, average gain / loss by the smaller
    of its wins and losses.
- **With imported positions.** Each n counts real results, unweighted, as
  every count does. A Play counts once in n although it weighs
  `PlayWeight` in the rates. Expectancy reads "n = 3 Plays · 2 positions",
  and the asset cards use the same count. Estimated R, sign-differs and
  review denominators count Plays only.
- **"Estimated R expectancy (planned-stop model)"** is the existing average
  of estimated R under a clearer name. Its formula and eligibility are
  unchanged.
- **"Plan written after first execution."** A planned-origin Play whose
  `PlannedAtUtc` is later than its earliest linked or manual execution is
  marked.
  - Review's Play rows carry `plannedAfterExecution`, and the Play's receipt
    carries the same flag.
  - The badge shows in the Play header, above its review, and in Review's
    Play lists. The flag uses stored times only.
  - The Play is scored, counted and sized like any other. Decision 9 allows
    it to answer "Followed".
  - Its estimated R is unknown with "No plan revision was in force at the
    first entry fill", because Review already requires a revision written at
    or before that fill.

## Pre-flight counts (October 20, 2026)

Phase 1.5 of the [learning-loop contract](phase1-learning-loop-contract.md):
- `ReviewDto.preflight` counts the period's closed planned-origin Plays in
  scope, scored or excluded, in three ways:
  - those with a recorded pre-flight;
  - those without one, mostly planned before it existed;
  - per check, the recorded Plays that went ahead without it.
- Retrospective Plays have no plan and are left out.
- The coverage disclosure shows the counts only, with no rate.
- Since 1.4b the population is the process view's planned Plays that were
  planned: Closed, Cancelled and Unresolved, scored or not (a Draft cancelled
  before Plan it is left out). Each skipped count and "not recorded" opens its
  Plays through `GET /api/review/process?skippedCheck=…`.

## Completed reviews (October 20, 2026)

Phase 1.2: Review's reviewed count and each Play row's `reviewed` now read
the explicit review completion. Review text, answers and review times no
longer count. See the review completion section of `play-lifecycle.md`. The
upgrade keeps Plays that were already counted as `legacy` completions.

## Unresolved Plays (October 20, 2026)

Phase 1.3a of the [learning-loop contract](phase1-learning-loop-contract.md),
decision 4. An Unresolved Play was declared by its owner because its
executions can never settle it ([play lifecycle](play-lifecycle.md)).
- It has no result and is never scored, and no sign is inferred.
- `coverage.unresolved` counts those in scope whose declaration falls in the
  period. The excluded count and the coverage line include it.
- The exclusion drill-down lists each with the reason "unresolved", placed by
  its declaration time, and an `unresolved` block: the reason, the note, the
  declaration time and the execution facts kept with it.
- If its fills later settle it, it is Closed and scored like any other.

## Process cases (October 20, 2026)

Phase 1.4b of the [learning-loop contract](phase1-learning-loop-contract.md),
decision 9. It covers G-01.
- **Population.** The period's Closed, Cancelled and Unresolved Plays in
  scope, each placed by its own time: Review's close time (the latest exit),
  the cancellation time, or the declaration time.
- **Lanes.** Each Play lands in the first that applies:
  1. retrospective, scored or not;
  2. unscored (excluded, Cancelled or Unresolved) with at least one
     structured answer;
  3. scored with no adherence answer;
  4. scored and answered not applicable;
  5. the cells: followed or deviated, by win or loss, with each row's
     scratches counted beside it.

  An unscored planned Play with no answers is only in the exclusion list.
  Answers are read as stored; a completed review is not required.
- **Cancelled Plays in coverage.** `coverage.cancelled` counts the period's
  Cancelled Plays in scope, by their cancellation time, and the exclusion
  drill-down lists each with its cancel reason: it had no fill, so it has no
  result. The excluded total adds them to the incomplete and unresolved
  counts.
- **`ReviewDto.process`.** Two rows (`followed`, `deviated`) of wins, losses
  and scratches, the four lane counts, and mistake tags. Counts only: the
  browser shows a share only where its own denominator is at least 10.
- **Mistake tags.** Grouped case-insensitively after trimming and shown in
  their latest spelling (newest review first), alphabetical, each with wins,
  losses, scratches and unscored Plays and no money. At most 200 are listed,
  with the total.
- **Drill-down.** `GET /api/review/process` takes Review's filters and
  exactly one of `cell`, `lane`, `mistakeTag` or `skippedCheck`. It returns
  those Plays newest first, 50 per page, with nullable financial values, the
  stored answers, the post-hoc flag and the completion. Every count equals
  its drill-down's membership across all pages.
- **Post-hoc plans** (decision 9) count in their cells like any other; their
  drill-down rows carry the badge.
- **Autocomplete.** `GET /api/review/mistake-tags` returns the owner's
  earlier tags, newest use first, at most 100, for the review form.
- **Breakdown.** Origin (planned, retrospective) and Direction (long, short)
  join Portfolios, Accounts and Assets, in that fixed order and without row
  inspection, since Review has no origin or direction filter. Their batting
  average, average gain / loss and profit factor, the combined row included,
  appear only where their own denominator is at least 10; below it the cell
  shows `—` and its n.
- **Queries.** Review reads the first linked execution of every Unresolved
  Play in scope in one grouped query, never one per Play.
- **Plays.** Review's Play rows gain `adherence`, `mistakeTag`, `direction`
  and `reviewCompletion`.
- **Imported positions.** The process view, its drill-downs, pre-flight and
  mistake tags count Plays only, since a position has no plan or answers.
  - When positions are included, the Origin breakdown adds a third group,
    "Imported position" (`position`), after Planned and Retrospective.
  - Direction groups count positions by their own direction.
  - Position rows carry their `direction` and null answers.

## CSV export (October 21, 2026)

Phase 2, item 5a (see `phase2-risk-coverage-contract.md`).

`GET /api/review/export` takes the report's filters and returns its own rows as a
CSV file, `vessel-review-<period>-<yyyyMMdd>.csv` (`paper-` before the period in
paper mode). The report and its exclusions come from one snapshot; the export
pages nothing. Review has a **Download CSV** button beside Definitions.
- **Rows.**
  - A first `notice` row: not a tax report, plus the period, mode and as-of
    time.
  - The period's scored Plays and imported positions, newest first, as on
    the report.
  - Then the exclusions: closed, unresolved and cancelled Plays, and closed
    imported positions Review cannot score (by their close time, when
    positions are read), with their reasons.
- **Columns:** kind, Play ID, operation ID, title, account, venue,
  instrument, direction, origin, execution source, status and time (UTC
  ISO-8601). Then:
  - the scored venue net and return;
  - the Play-local net with its basis, reason and "sign differs";
  - fees by token (an imported position's as Activity totals them, with
    stablecoins at nominal USD; a paper Play's simulated zero is written as
    such);
  - estimated R with its reason when unknown;
  - "plan written after first execution";
  - adherence, mistake tag and review completion;
  - exclusion reasons.
- **Cells.**
  - Amounts are the report's exact decimal strings, written as they are.
  - An empty cell is unknown, never zero.
  - Text cells starting with `=`, `+`, `-`, `@`, tab or carriage return get a
    leading `'`.
  - Numbers must be plain ASCII decimals; anything else is refused, never
    rewritten.
- **What it leaves out.** Excluded rows carry no amounts, and imported
  positions carry no plan, R or review. Separate funding is not added. Owner
  data is never cached (`no-store`). Both auth modes isolate it by owner.

## Week page (October 22, 2026)

Phase 2, item 2 (G-11, N4, G-13; see `phase2-risk-coverage-contract.md`).
- **The reporting setting.** One owner setting, stored on the server in
  `reporting_settings` (migration `20261022100811_ReportingSettings`): an IANA
  time zone and a week start, Monday or Sunday. Without a row the defaults
  are UTC and Monday, so nothing shifts until the owner saves one.
  - `GET` and `PUT /api/settings/reporting` belong to neither trading mode, so
    Paper reads and saves it too. The zone must be an IANA ID this server
    knows, exactly as given; Windows IDs are refused. A stored zone the server
    no longer knows reads as UTC rather than failing the page.
  - Settings shows it as **Reporting week**, after the display zone (1.6e),
    which stays in the browser and display-only.
- **The week.** Every Review read (`/api/review`, `/process`, `/exclusions`,
  `/export`) takes an optional `week`: a local date (`yyyy-MM-dd`, from 2000
  on) or `current`, the week holding today in the reporting zone. It replaces
  the rolling period with that reporting week.
  - The week runs from local midnight of its first day to the next week's,
    half-open: `[from, to)`. A midnight a clock change skips starts the day at
    its first valid instant, and a repeated one at its first occurrence. A
    week can last 167 or 169 hours.
  - Membership uses the same times as the rolling periods (latest exit,
    cancellation, Unresolved declaration), as does the unassigned-fills count.
    Facts are read as of now.
  - The chart has one point per local day of the week, up to today.
  - Weeks after the current one are refused (400), however far ahead.
  - The report carries a `week` block: its first day, UTC range, zone, week
    start and `data`. `none` means Vessel holds nothing that early in the
    scope: no retained execution, and no Play of any status, Drafts included,
    created before the week's end (Sol round 1 on item 2).
    `empty` means it does, but nothing closed, was cancelled or was declared
    Unresolved in the week. Otherwise it is `some`.
  - The rolling periods and Activity keep UTC days, and rolling responses have
    no `week` block.
- **The page.** Review's **Week** action opens it in its own dialog, over the
  page's filters, so Review's approved sections keep their place.
  - Previous and next week, a date picker and This week.
  - The week's scorecard and its exclusions. A rate is shown only from n = 10
    decided Plays. Imported positions are counted apart from Plays, with the
    weighting note, and positions Review cannot score are never counted as
    excluded Plays.
  - Review's coverage disclosure for the week: incomplete fees and
    executions, Unresolved and cancelled Plays, unassigned fills, positions
    included or not scored, the notice and the earliest retained fill.
  - The process cases (1.4b), and the week's repeated mistake tags: those on
    two or more of its Plays. Each count drills into the same week.
  - The owner's latest next actions, as on the Plays page. Next actions have
    no done state, so these are the newest written, not only the week's.
  - The current Needs review count in the same scope, every asset.
  - The promoted rule's answers arrive with item 3.
- **Nothing more.** No reminder, schedule or generated text is added.
