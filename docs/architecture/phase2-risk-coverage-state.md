# Phase 2 risk and coverage: state

This records verified progress against the
[Phase 2 contract](phase2-risk-coverage-contract.md). An item is listed as
done only after:
- its tests pass;
- its pull request is open;
- its Sol (Codex) review has been answered.

Each entry records the checks that ran. Each item has its own section, so
stacked pull requests edit different lines.

## Contract

October 21, 2026:
- The contract was drafted from:
  - the October 18 review, its round reports and probes, and the 27 findings
    its index places in Phase 2;
  - main at `fde40a7d`, with Phase 1 (#20, #22–#31, #34), paper trading (#32)
    and Review imported positions (#33) merged;
  - the code.
- **Owner answers (October 21).** All questions were answered as recommended,
  except decision 12 (the external channel and dead-man check), which was
  deferred.
- **The D-20 finding,** corrected by Sol round 1 (below). The cut at a
  Play's creation is in the reconciliation, matching and Execution-panel
  reads (`PlayExecutionService`), not in retention: `RetainAsync` already
  keeps every order a read returns. Item 4 therefore adds a bounded
  candidate lookback and changes no retention.
- No owner database was touched.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `c9373106`. Nine
  findings, all accepted into the contract:
  - **High:**
    - Item 1 counted only filled quantity once an entry had filled. Open
      and planned parts are now separate, the remainder of a partial fill
      counts at its planned price, and stops that already filled are not
      counted again.
    - Item 7 kept forced rows but not ordinary omissions. Every omitted row
      now leaves a durable, deduplicated omission marker that Activity
      reads.
    - Item 9b's "flat by the operation walk" did not prove sole exposure.
      It now needs item 6's coverage ledger, resolved native identity, no
      omission marker and an evidenced flat boundary, so 9b moves to 2b
      after item 6.
  - **Medium:**
    - The retention premise (above) was wrong.
    - An early placement alone neither makes a plan post-hoc nor removes R.
    - The paper bundle includes typed paper facts.
    - The search projection lists every write path and a one-time
      initialization.
    - Hyperliquid's integer-price exception is in the price rule.
  - **Low:** the benchmark moves after search.

  Sol found sound:
  - the per-stop clamp and separate long and short;
  - whole-Play unknowns;
  - blank, server-stored limits;
  - the paper-409 boundary for new routes;
  - nullable risk observations and critical beyond-liquidation severity;
  - the unchanged funding basis and scoring;
  - the item 14 defects still present on main;
  - the migration order.
- **Sol round 2,** on `a1339ff1`. All nine round-1 resolutions were confirmed,
  and the delivery order (6, then 7, then 9b) was found sound. Two findings,
  both accepted:
  - **Medium:** the "without a resting stop" count must come from 0.4c's
    coverage evidence. It now counts confirmed absence only, keeps shared
    protection, and reports unknown, unsupported, stale and unchecked
    coverage as unverified.
  - **Low:** the probe table still said an earlier order "is never stored".

## 1. Open risk

October 21, 2026. Implemented on `t3/phase2-open-risk`.
- **The calculator.** `OpenRiskCalculator` computes the loss if every current
  stop fills, in exact rationals (`Ratio`), and rounds once for the response:
  - each filled entry's remaining quantity is measured from its exact average
    fill;
  - exits linked to the entry's levels, and a position-level exit when there
    is only one filled entry, reduce it;
  - the stops whose linked orders have not executed share it;
  - the unfilled remainder of each entry counts at its planned price against
    every stop;
  - each stop's loss is clamped at zero.

  A Play is unknown, with a named reason, when:
  - a stop is unpriced;
  - stop shares do not total 100%;
  - the plan cannot be sized;
  - an exit cannot be attributed to one entry;
  - exits exceed their entry;
  - a linked fill contradicts its role;
  - the quote asset is not a listed stablecoin;
  - a manual execution cannot be read.
- **The API.**
  - `GET /api/risk/open?scope=` sums Planned and Open real Plays by direction
    on enabled accounts. It lists Paused and Unresolved Plays as not included
    and counts Open Plays without a linked resting stop.
  - `POST /api/risk/open/preview` measures the plan being edited against the
    saved Play's executions.
  - `GET`/`PUT /api/risk/settings` store the soft limit per owner. It is blank
    by default, above 0 and at most 100, with up to 4 decimals (migration
    `20261021144248_RiskSettings`).
  - `/api/risk` is actual-only, so paper requests get 409.
- **The browser.**
  - Capital context gains one line, for Draft, Planned, Paused and Open real
    Plays: long and short of the scope's balance, the unknown count, the
    not-included count, this Play's increment and the limit flag.
  - Overview gains an Open risk tile after the metric tiles, so Allocation
    keeps its place.
  - Settings gains Risk limits.
  - The browser only replaces the saved Play's contribution with its preview
    and forms exact shares.
  - Clients without the read (partial test doubles) show neither.
- **Tests.**
  - `OpenRiskCalculatorTests` (10): G-06, the managed stop at 105 adding 0,
    the average fill, exits and executed stops, the planned remainder (Sol
    round 1's 10-unit case), position-level exits, D-04's unpriced half, and
    the other unknowns.
  - `OpenRiskApiTests` (3, PostgreSQL): directions never netted, exclusions,
    scope, 404/400, paper 409, preview and limit validation.
  - `RiskIsolationTests` in both auth modes.
  - The SQL inventory has two new reviewed hashes. The body-limit inventory
    lists the two new bounded writes.
  - Frontend: validators, the paper guard, increment arithmetic, exact
    shares, the capital-context line, the Overview tile and the Settings
    panel.
- **Check.** `scripts/check.sh` passed on `5c50b05a`: backend 4,023 passed,
  5 skipped; frontend 170 files and 4,056 tests. An earlier run found 42
  frontend failures from test doubles without `openRisk`, which the guard
  fixed.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `f2bcce35`. Nine
  findings, all fixed in `bc500cd1`:
  - A linked order with known missing executions (the shared
    `OrderKey`/`MissingExecutions` rule) now makes the Play unknown, never a
    partial sum.
  - The editor's preview is read again on every reload, so new fills count.
  - Every plan must allocate 100% of the position across its entries,
    quantity sizing included; otherwise the Play is unknown.
  - Exits beyond the filled quantity are checked before zero-share entries
    are skipped.
  - Protection comes from 0.4c's recorded coverage. Only a confirmed absence
    counts as not covered. Unknown, unsupported, stale and unchecked coverage
    is unverified (this also settles the contract's round-2 finding).
  - Preview rejects duplicate entry or stop IDs (400). A Play ID that is not
    this owner's real Play on the account is 404, never a Draft fallback.
  - Shares say when the scope's balance covers only some accounts.
  - Overview lists the Plays not included.
- **Check after round 1.** `scripts/check.sh` passed on `bc500cd1`: backend
  4,024 passed, 5 skipped; frontend 170 files and 4,060 tests.
- **Sol round 2 (`gpt-6.1-sol`, high),** on `a74a0b47`. It confirmed round 1's
  fixes 2, 3, 4 and 6 to 9, and completed 1 and 5 with four findings, all fixed
  in `43eeaa53`:
  - A preview reads its named Play's facts in any status and account state.
    On a disabled account, missing executions still make it unknown.
  - A retained fill contradicts its role by the plan being measured. An
    edited short with the saved long's buy fill is unknown.
  - The preview carries the edited instrument source. A manual instrument on
    a venue account is unknown as a Draft as well as once planned.
  - A coverage check time ahead of the clock, beyond a minute's skew,
    verifies nothing.
- **Check after round 2.** `scripts/check.sh` passed on `43eeaa53`: backend 4,024 passed, 5 skipped; frontend 170 files and 4,061 tests.
- The migration is applied explicitly, never to the owner's database.

## 2. Weekly review page

October 22, 2026.

**2. Weekly review page.** Implemented on `t3/phase2-weekly-review`.
- **The setting.** One owner setting on the server (`reporting_settings`, migration
  `20261022100811_ReportingSettings`): an IANA time zone and a week start, Monday or Sunday. It is UTC
  and Monday until saved.
  - `GET` and `PUT /api/settings/reporting` belong to neither trading mode. Windows IDs and zones this
    server does not know are refused.
  - Settings shows it as **Reporting week**, apart from the display zone (1.6e).
- **The week.** Review's reads take `week`, a local `yyyy-MM-dd` date or `current`: the reporting
  week holding it replaces the rolling period.
  - It is half-open from local midnight of its first day. A skipped or repeated midnight starts the
    day at its first instant, so weeks of 167 and 169 hours are handled.
  - The chart has one point per local day, and later weeks are refused.
  - The report's `week` block says whether the week has data: `none`, `empty` (no trades) or `some`.
  - Rolling periods and Activity keep UTC days.
- **The page.** Review's **Week** action opens it in its own dialog over the page's filters:
  - the scorecard, with no rate below n = 10, its exclusions and the week's coverage;
  - the process cases and the week's repeated mistake tags, each drilling into the same week;
  - the latest next actions, and the current Needs review count in scope.
- **Contract notes.** 1.5's next actions have no open or done state, so "the owner's open next
  actions" are the latest ones written, as on the Plays page, and the page says so. The promoted
  rule's answers arrive with item 3.
- **Tests.**
  - `ReportingWeekTests` and `ReviewWeekTests`: zones, DST weeks, Havana's skipped and repeated
    midnight, boundaries, both week starts, data states, refused weeks, drill-downs and the export.
  - The isolation case in both auth modes.
  - The Settings panel, the week page and the validators.
- **Migration.** It is applied explicitly, never to the owner's database.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `51e7f281`. Four findings, all fixed in
  `087bb795`:
  - A Draft, Planned or Open Play with no fills left the weeks after it
    reading as without data. The store now reads the scope's first Play
    created in any status.
  - The page dropped Review's coverage disclosure, and its Excluded card left
    out imported positions Review cannot score. It shows the week's coverage
    and lists those positions apart from excluded Plays.
  - Rates mixed Plays and imported positions without saying so. The counts
    name them apart, with the weighting note.
  - `week=9999-12-31` gave a 500. A date past the current week is refused
    before its range is built.

  Sol confirmed the rolling boundaries, week snapping and local-day buckets,
  the DST and Havana cases, IANA aliases, `current` across UTC date
  boundaries, concurrent first writes, the migration, isolation in both
  auth modes and the owner SQL inventory.
- **Check.** `scripts/check.sh` passed on `973efd36`: backend 4,136 passed, 5 skipped; frontend 182 files and 4,099 tests.

## 3. One promoted if-then rule

October 22, 2026.

**3. One promoted if-then rule.** Implemented on `t3/phase2-if-then-rule`.
- **The rule.** Each trading mode keeps at most one active rule, "If …, then …", each part 1–500
  characters (`play_rules`, migration `20261022124043_IfThenRule`). It is versioned: a change adds the
  next version and archives the active one, and archiving leaves none.
  - `GET`, `PUT` and `DELETE /api/plays/rule` work in the mode they are asked for. Each write names
    the version the owner saw; a different one, or a concurrent change, is a 409.
  - The Thesis tab of a Draft, Planned or Paused Play shows it, with Write, Change and Archive. A next
    action offers **Make this the rule**, which fills its Then and names its source Play.
- **At Plan it.** The pre-flight dialog shows the rule. The owner may mark it acknowledged, or an
  exception with a reason of at most 500 characters. No answer never blocks. The request names the
  version shown, so a rule changed meanwhile is a 409 and is asked again.
- **The snapshot.** Revision 1 keeps the rule shown at Plan it (version, text, source) with the
  answer, beside the pre-flight; History lists it. The Play keeps the version and text for its review.
  A request that names no shown rule keeps none. Changing or archiving the rule never rewrites a
  snapshot. Retrospective Plays have none.
- **In the review.** A Closed, Cancelled or Unresolved Play planned under a rule asks "Followed the
  rule?": followed, deviated or not applicable. It is stored with the structured review and its
  history; a Play planned without a rule refuses an answer.
- **In Review and the week page.** Counts and drill-down only: the Plays planned under a rule, the
  answers at Plan it, and "Followed the rule?" by wins, losses, scratches and unscored Plays. No share
  is shown, and a winning deviation stays a deviation. `GET /api/review/process` takes `rule`.
- **Tests.**
  - `IfThenRuleTests`: the rule's text, the Plan it snapshot with and without an answer, stale and
    invalid answers, and Review's counts and drill-downs.
  - The isolation case in both auth modes: versions, stale writes, archiving, paper apart from real,
    another owner's source Play, and a planned Play keeping its version. The `PUT` body limit.
  - In the browser: the Thesis line, promotion, the dialog, the pre-flight answer, History, the review
    question and Review's section.
- **Migration.** It is applied explicitly, never to the owner's database.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `fa81a948`. Five findings, all fixed in
  `d48b8ea8`:
  - An archive and a replacement that both read version 1 could both
    succeed, leaving the reply and the stored rule apart. Archiving now
    updates only a row still active, so the second gets a 409, in either
    order.
  - Plan it kept the active rule even when the owner was not shown it: after
    a failed read, from an older client, or after an unanswered rule changed.
    The request now names the version shown; without it nothing is kept, and
    a shown version no longer active is a 409, answered or not.
  - After a 409 the editor saved again against the version read in the
    background, replacing a rule the owner had not seen. It saves against
    the version it was opened on, and offers **Load the current rule** first.
  - A rule read still pending when the owner saved a new version could
    replace it in the page afterwards. A read answered after a later read or
    write began is dropped.
  - A promoted rule named its source Play as plain text. It links to it.

  Sol confirmed that the archive is flushed before the new version is added,
  owner and mode scoping of the source Play, real and paper apart, stale
  answers refused, resume and later revisions keeping revision 1, the review
  answer's rules and history, Review's and the week page's counts against
  their drill-downs with positions left out and no rates, that the bundle
  carries the snapshot and that the CSV needs no rule columns.
- **Check.** `scripts/check.sh` passed on `d48b8ea8`: backend 4,146 passed, 5 skipped; frontend 184 files and 4,117 tests.

## 4. Earlier-order link candidates

October 21, 2026. Implemented on `t3/phase2-earlier-orders`.
- **The panel's read.** `PlayExecutionService.GetAsync` loads the contract's
  orders from `Vessel:Plays:EarlierOrderLookback` (24 hours by default, 0 to
  7 days, validated at start-up) before the Play's creation. It lists the
  compatible ones placed before creation as `earlierOrders`, at most 20: open,
  filled or triggered, and not linked to any Play.
- **What is unchanged.** Retention (`RetainAsync` keeps every returned order)
  and the automatic matcher's rule (placed at or after creation).
- **The panel.** The Execution panel shows them under "Placed before this Play
  was saved", with the same Link controls as other orders.
- **Linking** uses the existing endpoint. The badge follows the actual fill
  times:
  - a fast entry that filled before the plan shows "plan written after first
    execution";
  - an order placed before the Play but filled after planning keeps a
    contemporaneous plan.
- **Tests.**
  - `EarlierOrderCandidateTests` (4, three on PostgreSQL): a check offers but
    never links an earlier order, a zero lookback offers none, linking a fast
    entry opens the Play with the badge, a contemporaneous plan stays one, and
    the lookback is bounded.
  - The Execution panel test.
  - The monitor contract fixture regenerated with `earlierOrders`.
- **Check.** `scripts/check.sh` passed on `b18f6db8` (before item 1's round-1
  fixes were restacked beneath it): backend 4,027 passed, 5 skipped; frontend
  170 files and 4,057 tests. An earlier run failed only on the contract
  fixture, which was then regenerated.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `a2672af9`. Two findings,
  both fixed in `9c1dd421`:
  - Incompatible orders could use up the 20 places. An earlier order is now
    offered only when some role of the plan accepts its side, reduce-only and
    take-profit mechanics, and retained fills. This is checked before the cap,
    over at most 200 recent orders. Price is not required to match, since a
    fast market entry rarely fills at the planned price.
  - The panel said "the day before" whatever the configured lookback. It now
    says 24 hours unless the server is set otherwise.

  Sol confirmed that nothing links earlier orders automatically, the matcher's
  ambiguity is unchanged, the scoping and identity rules hold, and the
  configuration reaches the API and the worker.
- **Check after round 1.** `scripts/check.sh` passed on `9c1dd421`: backend 4,029 passed, 5 skipped; frontend 170 files and 4,062 tests.

## 5. Export

October 21, 2026.

**5a. Review CSV.** Implemented on `t3/phase2-review-csv`.
- **The read.** `GET /api/review/export` takes Review's own filters (period, scope, positions and
  threshold, trading mode) and returns the report's rows as CSV.
  - The scored Plays and imported positions come first, then the exclusions with their reasons and no
    amounts.
  - Report and exclusions come from one snapshot, so the file matches the page.
- **The cells.**
  - Amounts are exact plain decimals, and an empty cell is unknown, never zero.
  - Times are ISO UTC.
  - Text that a spreadsheet would read as a formula (`=`, `+`, `-`, `@`, tab, carriage return) is
    prefixed with `'` (`Common/Csv.cs`).
  - The notice row says it is not a tax report and that separate funding is not added.
- **The page.** Review gains **Download CSV** for the current selection.
- **Tests.**
  - `ReviewExportTests` (3): neutralized text and exact numbers, scored rows then exclusions, and
    exclusion reasons without amounts.
  - The export isolation case in both auth modes.
  - The button and download helper tests.
- **Check.** `scripts/check.sh` passed on `2ae53843`: backend 4,034 passed, 5 skipped; frontend 172 files and 4,065 tests.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `31e99f08`. Three findings, all fixed in
  `62fff421`:
  - An imported position's known fees were exported as unknown. Positions
    now carry their fees as Activity totals them, and a paper Play's
    simulated zero fees say so instead of an empty cell.
  - Closed positions Review cannot score were missing from the CSV. They are
    exported as exclusions with their reason and no amounts, matching the
    report's omitted count.
  - Number cells accepted a trailing newline and non-ASCII digits. They must
    now be plain ASCII decimals.

  Sol confirmed the owner filters, paper isolation, quoting and formula
  protection, `no-store` and the safe filenames.
- **Check after round 1.** `scripts/check.sh` passed on `62fff421`: backend 4,035 passed, 5 skipped; frontend 172 files and 4,065 tests.

**5b. Raw facts CSV.** Implemented on `t3/phase2-facts-csv`.
- **The read.** `GET /api/accounts/{id}/facts?kind=executions|funding&from=&to=` streams one
  account's retained facts as CSV, over whole UTC days and in time order.
  - Executions keep native IDs, the order and transaction identity, the exact price, quantity, fee and
    fee asset apart from the closed P&L, and its basis.
  - The manual ledger's rows follow with their Play, role and source label.
  - Funding is its own file, with RISEx's native nanoseconds.
- **What it is not.** Nothing is computed or inferred: there is no notice row, it is data only. A
  disabled or paper account is refused with 409, and an unknown one is not found.
- **The page.** Settings gains **Data export**, which states that these are retained facts only, not
  a tax report.
- **Tests.**
  - `FactsExportTests` (6, one on PostgreSQL): native IDs and exact decimals, manual rows, funding
    nanoseconds, request validation before any read, refusals, and half-open days in one account's
    time order.
  - The isolation case in both auth modes.
  - The Settings panel test.
- **Check.** `scripts/check.sh` passed on `ce60a82f`: backend 4,044 passed, 5 skipped; frontend 173 files and 4,067 tests.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `cf118a42`. Five findings, all fixed in
  `83069b31`:
  - A paper-mode request on a real account returned the CSV. The route is
    actual-only now: a paper request gets 409 before any read.
  - Manual executions had no instrument. Each row now carries its Play's
    instrument.
  - The database streams ignored cancellation. A dropped download now stops
    its query.
  - Funding settled within one microsecond follows its native nanoseconds.
  - An end date of 9999-12-31 gave a 500. It is a 400 now, checked before any
    read.

  Sol confirmed the owner filters, the uncapped retained fills, exact
  formatting, empty unknowns and formula neutralization.
- **Check after round 1.** `scripts/check.sh` passed on `7790a4d3` (before documentation-only commits were restacked beneath it): backend 4,048 passed, 5 skipped; frontend 173 files and 4,067 tests.

**5c. Play bundle.** Implemented on `t3/phase2-play-bundle`.
- **The read.** `GET /api/plays/{id}/bundle` returns a zip in the Play's own trading mode.
  - `play.json` holds, from the API's own read models: the Play; its plan revisions, status and review
    history; its executions (linked orders and fills, the manual ledger, or paper's simulated state);
    its log; and its evidence with markup and notes.
  - Each evidence original sits under `evidence/`, unchanged.
- **What it leaves out.** It holds no storage key, path or credential. An original that cannot be read
  is listed as unavailable rather than failing the download.
- **How it is built.** The archive is built in a delete-on-close temporary file and then copied, so no
  synchronous write reaches the response.
- **The page.** A saved Play's header gains **Download** beside History.
- **Tests.**
  - `PlayBundleTests` (2, PostgreSQL): the record and an unchanged original; an unreadable original
    listed, not hidden; another owner's Play and the other mode not found.
  - The isolation case in both auth modes.
  - The button test.
- **Check.** `scripts/check.sh` passed on `75371a37`: backend 4,048 passed, 5 skipped; frontend 174 files and 4,069 tests.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `20d1ef0f`. Six findings, all fixed in
  `218aff6b`:
  - **High.** The temporary archive took the umask (0644), so other local
    users could read it during a download. It is created owner-only (0600).
  - A manual Play on a disabled account could not export its ledger. It now
    can.
  - A paper bundle lacked its simulation provenance. Each simulated
    execution states its model and plan version, with the plan activations,
    through a paper export projection.
  - A link the owner removed disappeared. Every retained link is exported in
    any state, with its times and order ID.
  - A concurrent change could make the bundle contradict itself. All database
    reads come from one repeatable-read snapshot
    (`IUnitOfWork.ReadSnapshotAsync`), closed before originals are read.
  - Concurrent downloads could fill temporary storage. At most two bundles
    are built at once, and each original is cut off at its recorded size.

  Sol confirmed that the bundle holds no credentials, sessions, storage keys
  or raw venue JSON, and that paths and headers are safe. Its own backend
  tests could not start in its environment, which lacked SDK 10.0.302; the
  check below ran them.
- **Check after round 1.** `scripts/check.sh` passed on `41044726` (before documentation-only commits were restacked beneath it): backend 4,058 passed, 5 skipped; frontend 174 files and 4,069 tests.

## 6. Opt-in account history retention

October 22, 2026.

**6a. Retention and the coverage ledger.** Implemented on `t3/phase2-retention`.
- **The opt-in.** Manage account offers **Keep execution history** for a real account with a source
  on a venue the monitor checks. It is off until set. `PUT /api/accounts/{id}` takes an optional
  `keepExecutionHistory` and keeps the stored value when it is left out. A paper account gets a 409 and
  a manual record a 400, and the database checks it too (`CK_accounts_keep_history`).
- **The schedule.** An opted-in account is eligible for scheduled checks without a Play. Each tick
  claims protection work first; a claim with only kept history behind it then leaves a worker slot, one
  of the owner's two slots and up to `ProtectionHeadroom` venue slots (default 1, never the whole venue)
  free for protection checks. Owner Checks and Refreshes keep the plain budgets.
- **No comparison without protection work.** A run with no Play, watch or discrepancy work, neither when
  it began nor before evaluation, imports fills and orders and records coverage, but compares nothing and
  opens no discrepancy. Otherwise opting in would flag every untracked position on such an account; the
  contract's own warning for these accounts comes with 6c.
- **The ledger.** Every import, Refresh or check, records in `account_fill_coverage` what it requested,
  whether the venue's cap was reached, the span it proves continuous and the gap it may have skipped, by
  0.4b's rule. Consecutive continuous imports of one source extend one record.
  - Each gap is an `account_history_gaps` row that closes only when a read proves its span covered. Only
    verified complete history does so today; 6b's backfill will.
  - The migration (`20261022143200_HistoryRetention`) turns each existing gap flag into an open gap of
    unknown range.
- **The read.** `GET /api/accounts/{id}/history-coverage` (real only) returns the opt-in, the open and
  recently closed gaps and the latest records, 50 of each at most. Account detail shows them as **History
  coverage**. Deleting an account deletes its ledger.
- **Contract notes.** Item 6 is delivered in three pull requests in the chain: 6a (this), 6b (the
  owner-started Hyperliquid backfill and its progress) and 6c (the "open position, no resting stop, no
  Play" warning). The ledger is recorded for every real account, opted in or not, so gaps found before
  opting in can still be backfilled.
- **Tests.**
  - `HistoryCoverageTests`: the coverage rule for unknown, uncapped, capped, blind, first and complete
    reads, and when a record is extended.
  - `MonitorPostgresTests`: an opted-in account checked without a Play, compared with nothing, its record
    extended and a gap opened after a pause; the headroom for protection checks.
  - The isolation case in both auth modes; the settings `PUT` and its refusals.
  - In the browser: the validator, the panel and the Manage account toggle.
- **Migration.** It is applied explicitly, never to the owner's database.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `30ab6173`. Six findings, all fixed in
  `0d6811c8`:
  - A fill a few minutes ahead of the read time could make a gap end before
    it began, and the database refused the whole import. A gap's ends are
    now both execution times, with an unknown end when no fill time is
    reported.
  - With one venue read slot, the default headroom kept it all, so an opted-in
    account was never read on the schedule. The headroom now keeps at most all
    but one slot.
  - The database budget alone did not make protection checks go first: two
    kept-history accounts sorting earlier could take both worker slots. Each
    tick now claims protection work first, and kept history then leaves one
    worker slot free.
  - Capped reads of out-of-scope fills kept the boundary where it was, so each
    check added an identical open gap. The same gap found again widens the
    open one.
  - Extending a record kept its start when a later read reached further back.
    The start now moves back.
  - A Refresh reporting complete history would have closed every gap while the
    monitor's gap flag stayed set. Only a check closes them, as only a check
    clears the flag.

  Sol confirmed owner filtering and the composite account keys, isolation in
  both auth modes, the paper and manual refusals, an omitted setting keeping
  the opt-in, disabled accounts hidden, settings-revision fencing,
  transactional fill and coverage writes, bounded responses, the
  capability-driven toggle and that the evaluation gate rechecks protection
  work after reconciliation and watch updates.
- **Check.** `scripts/check.sh` passed on `e4cfdbe0`: backend 4,156 passed, 5 skipped; frontend 185 files and 4,121 tests.

**6b. Backfill.** Implemented on `t3/phase2-backfill`.
- **Starting it.** `POST /api/accounts/{id}/history-gaps/{gapId}/backfill` (real only) starts a backfill of
  one open gap, one at a time per account (`account_history_backfills`, migration `HistoryBackfill`). A
  closed gap, one beyond the venue's reach, or a venue without the new `FillHistory` capability is
  refused. Account detail offers **Backfill** on each such gap and shows the backfills' progress.
- **Runs of its own.** Hyperliquid's `userFillsByTime` serves at most 2,000 fills a page and only the
  latest 10,000. The scheduler claims due backfills last, after protection checks and kept-history reads,
  with the same headroom and free worker slot (trigger `backfill`). A run reads up to five pages, each in
  its own unit fenced by the lease, and releases it; it never moves the check schedule, attempt time,
  state or cooldown. The next run waits a minute.
- **Witnesses.** Every page reads to now. The first starts at the newest execution Vessel retained at or
  before the gap's start and must return it again; each later page must return the rows the previous one
  ended on, by trade ID. Only a page with nothing newer ends the window; a full page at one time, or more
  rows than the venue serves, stops the backfill.
- **Proof.** A served start and kept continuity prove the span from the gap's start to the last page. An
  unserved start proves only from the oldest row read, and the span before it is beyond the venue's
  reach. A missing witness proves nothing after the cursor. A gap of unknown range is proven only when the
  window is exhausted with continuity kept and fewer rows than the venue serves; that proof covers every
  open gap. Executions the adapter could not verify keep the gap open.
- **Conclusion.** The gaps are revalidated as they stand: each covered gap closes with a `backfill`
  coverage record, and the rest stays open as its own gap, beyond the venue's reach before the span or
  open to another backfill after it. The gap flag clears only when no gap is left open.
- **No notifications.** Backfilled fills are deduplicated like any import, without touching the snapshot,
  marked `Backfilled` and claimed as history in their unit: no `execution.filled`, now or when their
  order is linked later. A Play a later check opens or closes from a backfilled deciding fill gets the
  `history` event source, which no notification announces. A backfill creates no Play or thesis.
- **Tests.**
  - `HistoryBackfillTests`: windows, witnesses, exhaustion, the page and reach limits, and the proof for
    known and unknown starts and broken continuity.
  - The Hyperliquid page reader's raw rows; the `history` lifecycle source in `VesselEventTests`.
  - `MonitorPostgresTests`: a gap closed over two runs without moving the check schedule, one narrowed
    beyond the venue's reach, one whose reach moved between pages, and whole history closing every gap.
  - The scheduler leaving a due backfill behind a protection check; the isolation case in both auth modes;
    the panel's Backfill action, progress and polling after a failed read in the browser.
- **Migration.** It is applied explicitly, never to the owner's database.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `b7ebbd06`. Eight findings, all fixed in
  `89854857`:
  - The venue's reach could move past the cursor between pages, so a page
    could skip fills and the gap still close. Every page must now return the
    rows the previous page ended on, by trade ID; a page without them proves
    nothing after the cursor, which stays open as its own gap.
  - A window frozen at the start could not prove a gap of unknown range
    complete: later fills use up the reach without appearing. Every page now
    reads to now, and whole history is proven only by a window exhausted
    with continuity kept and fewer rows than the venue serves.
  - Matching the start by time did not prove the starting execution was
    still served. The first page starts at the newest retained execution at
    or before the gap's start, and must return every retained execution at
    that time again.
  - Completion closed the gap as it stood, even after it widened. The
    conclusion now revalidates the current gaps: the proven span closes what
    it covers, and the rest stays open as its own gap, beyond the venue's
    reach only before the span.
  - A backfilled fill could later open or close a Play and notify. Fills are
    marked as backfilled; such a lifecycle change gets the `history` event
    source, which no notification announces, and backfill units run under
    the owner's trigger.
  - Backfill pages ran inside protection checks, delaying them and using
    their capacity. A backfill now runs on its own lease, claimed last by the
    scheduler with the kept-history headroom and free slot, at most five
    pages a run, without moving the check schedule.
  - The browser rejected the `backfill` coverage record and stopped polling
    after one failed read. Both are fixed.

  Sol confirmed isolation in both auth modes, the capability and real-only
  guards, account locking and the unique active backfill, the shared fill
  deduplication and conflict rules without snapshot or sync changes, that
  historical claims precede execution capture and survive later linking,
  that omitted executions prevent closure, and that venue deadlines are
  checked between pages.
- **Check.** `scripts/check.sh` passed on `89854857`: backend 4,175 passed, 5 skipped; frontend 185 files and 4,124 tests.

**6c. Open position, no resting stop, no Play.** Implemented on `t3/phase2-no-stop`.
- **The warning.** On an account that keeps its history, `position.unprotected` warns of an open position
  with no Open Play on its market and no stop resting on the closing side in the current book. Any stop on
  that side counts, whatever its size. It follows the usual lifecycle and the Hub's discrepancy rules.
- **Its evidence.** A fresh, complete position read and a complete current book; a fill gap does not affect
  it. Without a current book it is undecidable by design, and an unrecognized trigger on the closing side
  leaves it unknown. Only an Open Play owns the position; a Planned one does not until its entry is linked.
- **Confirmation.** It confirms on its condition (`ConfirmsOnCondition`, the lifecycle mechanism D-19 also
  uses), so a changing quantity neither re-announces it nor restarts its confirmation. Its fingerprint keeps
  the side and quantity, so a dismissed warning reopens when the position changes, after the same 60 s. It
  rests on two reads: it is observed at the later and confirms only when every source of the confirming
  observation is at least 60 s after the later source of the first (`EarliestSourceAtUtc`).
- **Where it runs.** In every run of such an account, protection runs included. A run without protection
  work decides only this kind and leaves every other record as it is.
- **Not protection work.** An open warning never brings back the full comparison on an account without
  Plays. It keeps the account checked until it resolves, and turning the opt-in off resolves it.
- **Tests.** `MonitorUnprotectedPositionTests`: opening, a changing quantity, a closing-side stop, a stop on
  the wrong side, an unrecognized trigger, no current book, a flat account, the opt-in turned off, an Open
  Play owning the position, a Planned Play not owning it, a dismissed warning reopening on a changed
  position, two reads confirming only when both are 60 s apart, and a protection run. The lifecycle's
  condition and earliest-source rules in `DiscrepancyLifecycleTests`. The eligibility split against
  PostgreSQL. The browser's wording and facts.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `f044295d`. Three findings, all fixed in
  `bc4accae`:
  - A dismissed warning never reopened when the position grew, since its
    fingerprint held only the side. It now confirms on its condition, the
    lifecycle mechanism D-19 also uses, with the quantity in its
    fingerprint: a changing position still neither re-announces it nor
    restarts its confirmation, but a dismissed warning reopens after 60 s.
  - Confirmation used the snapshot's time for a finding that also rests on
    the book, so two book reads 41 s apart could confirm it. It is observed
    at the later read and confirms only when every source of the confirming
    observation is 60 s after the first.
  - The suggested action ("plan a Play for it, then Check") did not clear
    it: only an Open Play owns the position. The action now says to link
    the Play's entry so it is Open.

  Sol confirmed the closing-side and take-profit classification, native
  market matching, incomplete evidence and RISEx handling, that `KeepOnly`
  leaves other kinds alone, protection-first scheduling and headroom, owner
  and real-account scoping, the warning severity, Hub grouping and
  withdrawal, and the browser's validators.
- **Check.** `scripts/check.sh` passed on `bc4accae`: backend 4,194 passed, 5 skipped; frontend 185 files and 4,125 tests.

## 7. Lighter forced events

October 23, 2026.

**7a. Omission markers and forced events.** Implemented on `t3/phase2-omissions`.
- **Markers.** Every in-scope row an import leaves out leaves a durable marker (`execution_omissions`,
  migration `ExecutionOmissions`): trade ID, kind, reason, market, order ID and time, and no money.
  - The kinds are `forced`, `fee`, `missing-pnl` and `unrecognized-market`, from Lighter, RISEx and
    Hyperliquid alike.
  - Markers are deduplicated by kind and trade ID, and a later read that no longer returns the row
    erases nothing.
  - An unreported time is bounded by the read's window, which ends when the read was recorded.
  - Refresh, checks and backfill pages all write them. A Hyperliquid row on an unlisted market needs its
    trade ID; without one the read fails rather than count an omission it cannot mark.
- **Known missing executions.** A marker naming an order flags that retained order, whichever arrived
  first. The shared `MissingExecutions` rule then counts it as missing executions whatever its status, so
  its Play can neither settle nor be scored. The scored basis is unchanged.
  - `ExecutionOmission.Names` follows the order identity rule: on a recognized market, the canonical
    contract and venue order ID with agreeing native markets; on an unrecognized market, the order on that
    native market; with no market, every order with the ID. A known order named on another native market
    fails the import closed.
- **Forced events.** Lighter liquidations, deleverages and market settlements are kept as facts
  (`imported_forced_events`) with side, quantity, price, time, native IDs, an exclusion reason and nullable
  money. They are never fills, links or scored results.
- **RISEx D-22.** A trade with an empty `realized_pnl` is set aside after its other fields validate, with a
  counted reason and a marker keeping its order; the rest of the read applies. It is never zero.
- **Contract notes.** Item 7 ships in two pull requests: 7a (this) and 7b, gap-aware Activity with forced
  events as visible rows.
- **Tests.** The Lighter, Hyperliquid and RISEx adapters' markers and forced events, and a Hyperliquid
  row without a trade ID failing the read; the shared rule for every status; the naming rule's cases;
  markers surviving later reads, flagging orders in either order, unrecognized markets and a conflicting
  native market failing closed either way, against PostgreSQL.
- **Migration.** It is applied explicitly, never to the owner's database.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `4edf5368`. Four findings, all fixed in
  `8da6be1d`:
  - A marker on a market Vessel no longer recognizes has no canonical
    contract, so it never flagged its order: a cancelled, partly filled order
    could still settle and score. Markers now name orders by the order
    identity rule (`ExecutionOmission.Names`), through the native market for
    an unrecognized one, in both arrival orders.
  - Attribution ignored the native market, so a marker on another native
    market mapped to the same instrument flagged an unrelated order. A known
    order named on another native market now fails the import closed, like
    an order reported on another market, either way round.
  - A Hyperliquid row on an unlisted market without a trade ID was counted
    as omitted with no marker. Its trade ID is now required, as for any
    in-scope fill, so the read fails instead.
  - An unreported time was bounded by the snapshot, though Lighter reads its
    trades later. The bound is now when the read was recorded.

  Sol confirmed owner isolation, ordinary deduplication and durability,
  deletion and locking, forced-event sides, quantities, nullable money and
  wording, RISEx D-22's validation and count, backfill forwarding, the
  migration's indexes, keys, triggers and `Down`, and that the shared rule
  blocks flagged orders in lifecycle, Review, sizing and the monitor.
- **Check.** `scripts/check.sh` passed on `8da6be1d` after one rerun (the first run's only failure was
  the HyperliquidMarketStream idle-timeout flake, which passed alone): backend 4,204 passed, 5 skipped; frontend 185 files and 4,125 tests.

**7b. Gap-aware Activity.** Implemented on `t3/phase2-gap-activity`.
- **Never across an omission.** A marker at or after a partition's first retained fill breaks Activity's walk
  there. An operation still open at it reads "direction unknown: executions omitted", with unknown
  boundaries, instead of the phantom opposite operation an anchor across it used to produce (probe C6).
  - The walk after it needs a new proof of flatness after the marker's window ends, or the snapshot anchor,
    which applies only to fills after the partition's last marker. Every unknown operation until then carries
    the same explanation.
  - A marker without a reported time is bounded by its read window on both sides; an unknown end never
    recovers.
- **Partitions.** A marker breaks every partition it could belong to and never moves a fill between them:
  - one without a market breaks every partition of its account;
  - a canonical-only marker breaks every partition of that contract;
  - a native marker breaks its partition and the ID-less one of its contract, or, on a market Vessel does not
    recognize, the partition keyed by its native ID (7a's order rule).
- **One set of inputs.** Activity, Overview, the sizing fallback, Review's imported positions and the
  retrospective preview, confirmation and fingerprint aggregate the same markers, so a round trip across an
  omission is never eligible to become a Play.
- **Forced events** are coverage rows on the page with their facts as reported and their exclusion. The newest
  50 are shown, and the rest are counted in SQL per account.
- **Freshness.** Markers and forced events move the cached facts revision, and markers are part of the version
  token.
- Review, sizing and the ladder are unchanged.
- **Tests.** `ActivityOmissionTests`:
  - the C6 probe; an omission after the last fill, before the first, on another market and without a time;
  - nothing inside a window proving flatness; markers without a market or a canonical contract; markers
    breaking every candidate partition without merging fills; the explanation on every unknown operation;
  - the forced-event rows and version.

  Against PostgreSQL: the store reading markers and moving the revision; only the newest forced events loaded,
  with the rest counted; and a retrospective preview and confirmation refused across an omission Activity
  reports.
- **Unrecognized markets (owner answer, October 23).** A marker on a market Vessel does not recognize breaks only
  the partition keyed by its native ID. An older partition of the same market listed under another key (for
  example a retired RISEx market whose old fills say `BTC` with no native ID) is not broken. Breaking every
  ID-less partition of the account would close that, but any marker on an unrecognized Hyperliquid market would
  then break every ordinary coin. The owner chose not to break them, so the rule stays as built.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `af35eb75`. Six findings, all fixed in
  `779efaed`:
  - A fill inside an omission's uncertainty window could prove flatness or a
    direction, so a close in the window ended the unknown operation and
    Activity reported a closed round trip across the omission. Only fills
    after the window's end count now; an exact time is a window of one
    instant, and a window without a known end never recovers.
  - A marker without a market broke no partition, so the walk crossed it.
    It now breaks every partition of its account, and a marker with only a
    native market breaks that market by 7a's order rule.
  - A native marker missed the ID-less legacy partition of its contract, and
    a canonical-only marker missed the native ones. A marker now breaks
    every partition it could belong to, and fills never move between them.
  - The retrospective preview, confirmation and fingerprint aggregated
    without markers, so a closed round trip across an omission was eligible
    and could become a Play. The diary now reads the markers Activity reads
    through the same aggregation.
  - Only an operation already open at the marker carried the omission
    notice: one that started unknown at or after it read as generic missing
    history. Every unknown operation from a marker on now explains it until
    the walk recovers or the snapshot anchors it.
  - Every read loaded all retained forced events to show 50. The store now
    reads the newest 51 and counts the rest in SQL only past the cap, and
    markers load as a minimal record.

  Sol confirmed owner isolation, disabled-account exclusion, filter and page
  ordering, exact money formatting, nullable forced-event money, leverage
  suppression for broken operations, cache revision invalidation and the
  triggers.
- **Check.** `scripts/check.sh` passed on `779efaed`: backend 4,215 passed, 5 skipped; frontend 185 files and 4,125 tests.

## 8. Venue-reported risk context

October 21, 2026.

**8a. Liquidation price and margin mode.** Implemented on `t3/phase2-risk-context`.
- **The facts.** Each venue's reported liquidation price and margin mode are kept with the
  snapshot's positions.
  - A reported zero liquidation price means none, so it reads null.
  - Nothing is estimated.
- **Observations.** `account_risk_observations` (migration `20261021164349_RiskContext`) records them
  when a position appears or changes, or when its market receives new executions. A cross-margin
  price that only drifts with the account is not kept again.
- **Where it shows.**
  - The Execution panel shows "Venue liquidation" now and at the first entry fill.
  - Account detail adds the column.
- **Tests.**
  - `RiskObservationPostgresTests` (2): when observations are kept, and the current and first-fill
    reads.
  - The adapter mappings.
  - The Execution panel test.
  - A regenerated monitor contract fixture.
- **Migration.** It is applied explicitly, never to the owner's database.
- **Check.** `scripts/check.sh` passed on `42010626`: backend 4,050 passed, 5 skipped; frontend 174 files and 4,071 tests.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `187d2561`. Seven findings, all fixed in
  `d0d87215`:
  - **High.** The first-fill read could take another position: the opposite
    direction, another native market, or one opened after the Play closed.
    Both reads now take only the Play's own position: its direction, the one
    native market its fills name (several make it unknown), its lifetime once
    flat, and an enabled account. A disabled account's position no longer
    shows through Plays.
  - An isolated position's liquidation change was dropped. Only cross-margin
    drift is skipped.
  - A reopening seen in earlier history lost its context. Retained executions
    since the last observation count, as for leverage.
  - Once flat, the panel hid the first-read context. It keeps it now.
  - The panel did not say when each value was read. It does, and the context
    reads "first read after entry", not a measurement at the fill.
  - A negative liquidation price from Lighter or RISEx became unknown. It is
    an invalid response now.
- **Check after round 1.** `scripts/check.sh` passed on `4cb06c01` (before documentation-only commits were restacked beneath it): backend 4,064 passed, 5 skipped; frontend 174 files and 4,071 tests.

**8b. `protection.beyond-liquidation`.** Implemented on `t3/phase2-beyond-liquidation`.
- **The rule.** An Open Play's stop that sits at or beyond the venue's reported liquidation price on
  the losing side opens a critical incident.
  - It is identified by its stop IDs.
  - A drifting cross-margin price keeps confirming the same incident.
  - A stop inside the liquidation price, or a position with no reported price, opens nothing.
- **Unchanged.** Severity by uncovered fraction and re-announcing.
- **Where it shows.** Notification text and the browser's discrepancy title, action and facts.
- **Tests.**
  - Two `MonitorDiscrepancyTests` cases.
  - `discrepancy-text.test.ts`.
- **Check.** `scripts/check.sh` passed on `53c029f1`: backend 4,054 passed, 5 skipped; frontend 175 files and 4,073 tests.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `d29aaec3`. Two findings, both fixed in
  `49de89e0`:
  - **High.** An incomplete order book that omitted the stop resolved a
    critical incident. Only the complete book, or a fresh snapshot without
    the Play's position, clears it.
  - The position lookup ignored direction. Only a position in the Play's
    direction counts; an opposite one leaves the check undecidable, for
    `position.opposite` to report.

  Sol confirmed equality at the boundary, cross and isolated prices, the
  resting order's acting price, quiet cross drift and the stale-read and
  capability guards.
- **Check after round 1.** `scripts/check.sh` passed on `5c278dd6` (before documentation-only commits were restacked beneath it): backend 4,070 passed, 5 skipped; frontend 175 files and 4,073 tests.

**8c. Link this stop, and check timing.** Implemented on `t3/phase2-link-stop`.
- **Link this stop.** A `protection.missing` or `protection.short` record offers **Link this stop** for
  each compatible unlinked order.
  - The order must rest on the Play's instrument on the exit side, with stop mechanics (never a
    take-profit) as `OrderMechanics` decides.
  - The link goes through the existing endpoint, chosen by the owner, never automatic.
- **Check timing (I-08).** The monitor panel's timing line explains that checks are periodic and are
  not stop protection.
- **Tests.**
  - The Execution panel cases.
  - The stop-mechanism helper.
- **Check.** `scripts/check.sh` passed on `a8c7c85d` (before item 5a's round-1 fixes were restacked beneath it): backend 4,054 passed, 5 skipped; frontend 175 files and 4,074 tests.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `b127ede6`. Four findings, all fixed in
  `77119bbd`:
  - Resolved and dismissed warnings still offered the link. Only an open
    incident does.
  - A stop order that contradicts a retained fill could be offered, then
    refused with 409. The execution read states each offered order's roles by
    side, mechanics and retained fills (`RoleConflict`), and only an order
    that can be a stop is offered. The general Link choices follow the same
    roles.
  - With unsaved plan edits the shortcut sent an unsaved stop. It waits until
    the edits are saved, as rebinding does.
  - The cadence was unstated. The monitor panel always states the configured
    interval (`intervalMinutes`) and "not stop protection" (I-08). The
    contract fixture was regenerated.
- **Check after round 1.** `scripts/check.sh` passed on `3fe2334e` (before documentation-only commits were restacked beneath it): backend 4,071 passed, 5 skipped; frontend 175 files and 4,076 tests.

## 9. Funding

October 21, 2026.

**9a. Resumable windows.** Implemented on `t3/phase2-funding-windows`.
- **The cursor.** Refresh reads settled funding in windows (`FundingWindows`, `Vessel:Funding`)
  that extend the account's stored contiguous coverage.
  - The stored coverage is the cursor, so **no migration** was needed. The contract's "needs a
    migration" is corrected here.
  - The span after it is read oldest first, and the span before it newest first.
  - The last day of stored coverage is re-read (`Recheck`), so a late settlement is kept and a changed
    one still fails closed.
- **Halving.** A window halves only when an adapter marks the result `Truncated`, meaning its page or
  row budget alone stopped it. Other incompleteness ends the read.
- **Bounds.** At most 8 reads, 10,000 payments and 20 seconds per Refresh. The single read it replaces
  had the same 20-second deadline, so the browser's Refresh keeps its time.
- **Failures.** Completed windows survive a later failure or rate limit, and the rate limit still
  defers the venue.
- **The notice.** Partial coverage says "refresh again to read the rest"; missing funding is not zero.
- **Tests.**
  - `FundingWindowsTests` (12), including the probe's case: 720 hourly rows against Lighter's
    500-row budget, covered over two Refreshes.
  - A PostgreSQL resume test.
  - Truncation flags in the Hyperliquid, Lighter and RISEx reader tests.
- **Check.** `scripts/check.sh` passed on `8aa3f35c` (before item 5a's round-1 fixes were restacked beneath it): backend 4,074 passed, 5 skipped; frontend 175 files and 4,074 tests.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `6b00008d`. Two findings, both fixed in
  `e76c04be`:
  - Window sizes were relearned every Refresh: 100 hourly markets against
    Lighter's 500-row budget spent all eight reads halving, every time. The
    funding state's last attempt is now the widest window the Refresh
    completed, or the last one tried when it completed none. The next
    Refresh starts from it, or half of it after an incomplete read, and a
    failed read keeps it. Still no migration.
  - Rechecks could starve new coverage with one-hour windows. The uncovered
    spans are read first, and the recheck uses only the reads left.

  A window also doubles only after a full-size success now, since one cut at
  a boundary proves nothing larger. `FundingWindowsTests` has 14 tests, and
  the PostgreSQL resume test's second Refresh takes six reads, not three
  halvings again. Sol confirmed the rounding, the gap-free merging, the
  inclusive recheck boundary and the failure handling. It noted that a
  one-day settlement delay is unproven: a settlement reported later than the
  recheck is not read again.
- **Check after round 1.** `scripts/check.sh` passed on `834004c2` (before documentation-only commits were restacked beneath it): backend 4,093 passed, 5 skipped; frontend 175 files and 4,076 tests.

**9b. Beside a Play.** Implemented on `t3/phase2-funding-play`.
- **The line.** The execution view of a real venue Play carries `funding`, and the execution panel shows one
  "Settled funding" line after the Play-local result. It is never added to the venue or Play-local result,
  and no basis moves from Unknown. It has four states:
  - `sole`: the exact nominal sum, its asset, the payment count and the window;
  - `shared` ("shared, not allocated"): another Play's fill, an unlinked fill or an open position on the
    market;
  - `incomplete` ("coverage incomplete"), with its reason, `changing` included when the facts moved while
    being read;
  - `unavailable`: a venue without a funding reader (a capability) or a disabled account.

  Manual instruments and Plays before their first entry fill have none; paper keeps its 409.
- **The window.** It runs from the first linked fill. While the Play's own fills net to zero (a Closed Play),
  it ends at the last fill. Otherwise it ends at the later of the last fill and the latest snapshot, the rule
  Activity uses for an open operation (`OperationFunding.End`, now shared).
- **The proof** (`PlayFunding`). `sole` needs all of these:
  - one resolved partition by Activity's rules, with no ID-less fill or settlement that could belong to it;
  - no other fill in the window, and no order on the market with known missing executions overlapping it
    or either proof's span;
  - no omission marker (its read window included), forced event or open gap overlapping it;
  - ledger coverage containing the window;
  - a flat start, proven by complete history or by the latest snapshot walked back over covered,
    unomitted history, and no other fill in the exit's millisecond;
  - exactly one Activity operation made of exactly these fills, with complete funding and no ambiguous
    bucket, so there is still one attribution rule.
- **One state.** The evidence is read in one repeatable-read snapshot whose facts revision, at its start and
  end, must equal the one the cached Activity aggregate was built from. Every write to fills, orders, links,
  snapshots, funding, markers or forced events moves it. The proof's indexes are built once per aggregate,
  and evidence is read only for the market from the window on.
- **The bundle.** The one-Play bundle (5c) reuses the execution view, so it now carries the funding object,
  read in the bundle's own snapshot.
- **Open questions for the owner.**
  - **Old Plays.** They rarely prove a flat start, because only the latest snapshot is kept and only a
    full backfill writes a complete ledger record. Keeping flat observations would need a migration.
  - **Play status.** It is not fenced; it only affects the `not-settled` check.
  - **Order lifetimes.** They follow Vessel's clock.
- **Owner answer (October 23).** These stay as built for now; the owner will rethink them later. Old Plays
  keep reading "coverage incomplete" until a flat start is proven, and no flat observations are kept.
  - **To revisit (owner, October 23).** Opening a new Play could close an older one on the same market that
    never filled, only where that makes sense. Nothing is built: concurrent Plays stay allowed, and a Play's
    status changes only by the owner or its linked fills.
- **Tests.** `PlayFundingPostgresTests` (27):
  - the sole sum with unchanged results;
  - each failing condition, with missing executions in, after and before the window;
  - a complete history, concurrent Plays, an open Play and an ID-less legacy fill;
  - a backfill between the reads, a ledger change alone and the bundle's snapshot;
  - paper, owner isolation, a disabled account and a venue without a funding reader.

  In the browser: the validator and the line's states.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `cea2b982`. Three findings, all fixed in
  `0e9ca318`:
  - **High.** A known missing execution on another order could prove a false
    flat start: an earlier position closed by a retained filled order whose
    execution is missing left the latest snapshot flat, so the walk back over
    retained fills found zero exposure before the Play and showed its
    funding as sole. Any order on the partition with known missing
    executions overlapping the window now reads `missing-executions`, and
    one overlapping either proof's span rules that proof out.
  - **High.** The fills and the coverage ledger were read separately, so a
    backfill landing between the two reads could pair the older fills with
    the newly completed coverage and certify a window that another round trip
    shared. The evidence is read in one repeatable-read snapshot with the
    owner's facts revision at its start and end, which must equal the
    revision the Activity aggregate was built from; otherwise it retries
    once and reads `changing`.
  - Every poll loaded the account's whole forced-event history and rebuilt
    its partition and omission indexes. Evidence is now read for the market
    from the window on (earlier only for the complete-history proof), without
    truncating it, and the proof's indexes are built once per cached Activity
    aggregate.

  Sol confirmed exact arithmetic and unsupported-asset rejection, owner
  isolation, the disabled, manual and paper guards, capability checks, the
  separation from Review and sizing, the unchanged Activity funding after the
  shared window rule moved, the browser validator, the monitor's composition
  and the locked layout.
- **Check.** `scripts/check.sh` passed on `0e9ca318`, after the first run's only failure, the Overview
  query-count test, was updated for the revision read (10 queries instead of 9): backend 4,242 passed, 5 skipped; frontend 186 files and 4,141 tests.

**9c. A slow schedule.** Implemented on `t3/phase2-funding-schedule`.
- **The run.** The monitor worker reads settled funding every `Vessel:Funding:Interval` (6 hours by default,
  1 hour to 7 days, never longer than `Lookback`) as a background run, trigger `funding`. Like a backfill, it
  never moves the check schedule, attempt time, state or cooldown, and releases its lease when done.
- **Who.** Enabled real accounts with a source, on venues with the funding capability, that have an Open
  venue Play or keep their execution history. Paper accounts, disabled accounts, suspended owners and
  manual records never.
- **Priority.** A fourth scheduler pass after protection checks, kept-history reads and backfills. It needs
  the protection headroom and the owner slot kept for protection, so a due check always goes first.
- **The read** is the Refresh's own (`MonitorCoordinator.ReadFundingAsync`): 9a's cursor, windows and bounds,
  saved in one unit under the account lock and fenced by the lease and the settings, source, venue and
  credential it read with. It reads no account or order.
- **Due time.** `monitor_accounts.NextFundingAtUtc` (migration `FundingSchedule`) is empty until the first
  read, so a newly eligible account is due at once, and moves one interval after every finished read. A rate
  limit defers the venue and leaves the read due.
- **Quiet.** A failed read keeps the last window and the Refresh's fixed notice, and logs only the exception
  type. No event, notification or Play attribution; Refresh, Review, sizing and Activity are unchanged.
- **Tests.** `MonitorPostgresTests` (the cursor resumed each interval, the check schedule untouched, a due
  check first, rate-limit deferral, a quiet failure, eligibility), `MultiOwnerWorkerTests` (protection first,
  a suspended owner, no funding reader), `DueQueueTests` (a full page of skipped candidates, resuming across
  ticks, the page budget), the keyset continuation against PostgreSQL, the Wallet-mode isolation case, the
  interval bounds and the reviewed SQL inventory.
- **Migration.** It is applied explicitly, never to the owner's database.
- **Owner answer (October 23).** Funding matters for the final figures, not in between, so the schedule stays as
  built on every point below.
  - A run that stops at the read limit waits a full interval even while coverage is partial; the backfill
    brings its next run forward instead.
  - A Refresh does not push the scheduled read back.
  - An owner Check or Refresh during a funding run gets a 429, as during a backfill.
  - After the migration every eligible account is due at once; the background slot paces them.
  - Unresolved Plays do not count as Open.
  - `Interval` must not exceed `Lookback`, so a `Lookback` under an hour now fails at startup.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `b0a2d2bb`. One finding, fixed in
  `2ca2de90`:
  - Funding discovery returned the first 100 due accounts every tick with no
    continuation, so a full page of accounts the scheduler skips (a suspended
    owner's, capped owners or venues, refused claims) hid every account after
    them. The backfill pass had the same shape. Both passes now page through
    a `DueQueue` that keeps its place across ticks, after the last candidate
    examined; a tick that began part way starts again from the beginning
    once.

  Sol confirmed protection ordering, capacity reservations, lease renewal and
  stale-result fencing; eligibility at claim, owner scopes, the paper,
  disabled and source-less exclusions and the credential guards; the shared
  windows, exact settlements, native nanoseconds, deduplication, coverage
  merge and account locking; the due time on failure, rate limit,
  cancellation and a superseded result; check scheduling, admission,
  Activity invalidation, events, notifications and secret-safe logging; and
  the migration and configuration. It noted untested mutation during a read,
  lease loss and concurrent capacity, which it found sound statically.
- **Check.** `scripts/check.sh` passed on `2ca2de90`: backend 4,258 passed, 5 skipped; frontend 186 files and 4,141 tests.

## 10. Planning assumptions and soft limits

October 21, 2026.

**10a. Assumed costs.** Implemented on `t3/phase2-cost-assumptions`.
- **The setting.** Settings gains **Cost assumptions**: per venue, a round-trip fee and a slippage
  percentage of the position size.
  - Each is 0 to 10 with up to four decimals; blank is not set. There are no defaults.
  - They are kept in the browser per user (`costs.assumptions.v1`), so no migration. The contract's
    chain lists none.
  - They are hidden in paper mode. The Manual venue has none, since one manual account can stand
    for any broker.
- **The summary.** A real Play's summary shows one line beside the planned figures, never inside
  them, for example "Assumed costs ≈ −0.03R (fees 0.09%, slippage 0.05%)".
  - It is stated in R only against a known loss at the stops, otherwise in the quote asset.
  - An unset part reads "not set", never zero.
- **Unchanged.** Sizing, Review, open risk and stored results never read them.
- **Tests.**
  - `cost-assumptions.test.ts`: validation, stored values, exact position size and R.
  - The summary line.
  - The Settings panel.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `d501cea8`. Three findings, all fixed in
  `b4343e74`:
  - The cost went through a float before rounding: 1% of
    100.49999999999999999999999 showed 1.01 instead of 1. It is rounded once
    to cents from the exact value.
  - On a 1024-pixel desktop with the navigation open, the six-column form
    did not fit its panel, which clipped the slippage field and Save. The
    fields now wrap within the panel at any width.
  - The Manual venue could hold an assumption, and its Plays then showed the
    line. Settings no longer lists it, and its Plays show no line even with
    one stored.

  Sol confirmed that blank and zero stay distinct, that invalid text and
  stored JSON are rejected, the per-user scopes, the R denominator, the
  paper exclusion, that nothing else reads the assumptions, and the theme
  tokens and layout.
- **Check.** `scripts/check.sh` passed on `b4343e74`: backend 4,093 passed, 5 skipped; frontend 177 files and 4,085 tests.

**10b. Soft loss limits.** Implemented on `t3/phase2-loss-limits`.
- **The limits.** Settings → Risk limits gains daily and weekly loss limits in R, above 0 and at most
  100 with up to four decimals, blank until set. They are stored with the open-risk limit
  (`risk_settings`, migration `20261022104353_LossLimits`). A `PUT` changes only the limits it names.
- **The loss.** `GET /api/risk/loss` (real only) gives today and this week by the reporting setting:
  - the exact sum of estimated R of real Plays closed in the span, on enabled accounts;
  - how many have R, and how many have none, never counted as zero;
  - whether the known loss reached the limit: the net R at or below minus the limit.
- **Where it shows.** Capital context beside open risk, the Suggestions block even while closed, and
  the pre-flight at Plan it as the `loss-limit` check. That check is not met at a reached limit,
  unknown when a Play without R could still reach one, and absent without a limit or after a failed
  read.
- **The cooldown note.** At a reached limit the pre-flight dialog takes an optional note of at most
  500 characters, stored with revision 1 on the check and listed in History.
- **Fresh reads.** The loss is read again after the limits or the reporting week are saved, a second
  after its day or week ends, and on returning to the tab after it.
- **Nothing is blocked**, and sizing, Review and the ladder do not read the limits.
- **Tests.**
  - `LossLimitTests`: the limit rules, the cooldown note, and today's and this week's sums in Madrid
    with a Play without R.
  - The pre-flight note kept on revision 1, Review's counts and the skipped drill-down, and a `PUT`
    keeping the limits it leaves out.
  - The isolation case in both auth modes.
  - In the browser: the check rules, the line, the flag, the dialog, History, Settings and the fresh
    reads.
- **Migration.** It is applied explicitly, never to the owner's database.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `a54a99d3`. Four findings, all fixed in
  `a8611244`:
  - Saving a limit in Settings left the Plays page's loss read as it was
    until a reload, so a newly reached limit did not show. Saving the limits
    or the reporting week now refreshes every mounted read.
  - A read never expired, so yesterday's breach stood for today across the
    reporting midnight. It is read again a second after the day or week it
    shows ends, and on returning to the tab after that.
  - An older client's `PUT` with only the open-risk limit cleared both loss
    limits. A `PUT` now changes only the limits it names.
  - A PostgreSQL test still expected five pre-flight counts. It expects the
    loss-limit check, and a skipped one drills down to its Play.

  Sol confirmed that Review's planned-stop R is reused, that the sums are
  exact with wins offsetting losses and a breach at equality, that Plays
  without R are counted apart, and that paper, disabled accounts, positions,
  cancelled and Unresolved Plays are left out. It also confirmed the latest
  exit time, the reporting boundaries, the paper guard, owner filters, note
  validation and revision-1 storage.
- **Check.** `scripts/check.sh` passed on `a8611244`: backend 4,139 passed, 5 skipped; frontend 183 files and 4,105 tests.

## 11. Shared-exit allocation

October 23, 2026.

**11. Shared-exit allocation and flip splits.** Implemented on `t3/phase2-allocation`.
- **The record.** `fill_allocations` holds one allocation per imported fill: its reason (`shared-close`, `flip` or
  `other`), note, revision and fee remainder. `fill_allocation_lines` holds each Play's line: role (exit or entry),
  the plan entry for an entry line, an exact quantity and a fee share. `fill_allocation_revisions` keeps every
  change, append-only (migration `FillAllocations`).
- **The request.** `POST /api/plays/{id}/execution/allocations` is real-only, so paper gets a 409 before any read.
  - It takes the fill, the allocation's expected revision, every involved Play's expected version, the reason,
    the note and the lines. Empty lines remove the allocation, as a new revision.
  - It runs in one unit under the request and account locks, with the account's active Plays locked. It is
    idempotent by request ID, the 1.3b mechanism.
- **Validation.**
  - The lines sum exactly to the fill.
  - A shared exit is all exit lines, on Plays the fill's side closes. A flip is one exit, equal to that Play's
    open quantity without this fill, and one entry of the other direction.
  - Entries alone are accepted only for a later opening fill of an order a flip already split.
  - Exit lines need an Open or Unresolved Play, and an entry line a Planned, Paused or Open one.
  - The final ledger is checked: a Closed Play keeps an identical line, and no Open or Unresolved Play is left
    without its entry.
- **Fees.** Each share is the fee times the line's share of the fill, truncated at the fee's own decimals (never
  fewer than 6). The remainder goes to the largest line, with ties to the lowest Play ID; it is stored and shown.
  An unknown fee stays unknown on every line.
- **Order links are unchanged.** An allocation never creates or removes a link. A fill's order must be retained,
  and if it is linked, that Play must be on the lines with a matching role. An allocated fill counts for a Play
  only through its own line, everywhere the Play's fills are read.
- **Lifecycle.** The existing transitions follow the write: Planned or Paused to Open, Open to Closed, and
  Unresolved to Closed through `SettleUnresolved`. Nothing moves a Play back.
  - An order behind an allocated entry that still rests keeps its Play from closing.
  - Once a flip split opens a Play, its order is split: its other fills count for no Play, even through a link,
    and flag its Plays as not reconciled until the owner allocates them.
- **Scoring.** The Play-local result is shown, and the venue result reads "Not allocated". Review excludes a
  settled allocated Play as `not-allocated`, counted in its coverage and listed in the drill-down and CSV; sizing
  leaves it out and counts it; search shows the state. `play.closed` carries the allocated quantities and a null
  venue P&L.
- **Funding (9b).** A Play with an allocated part, or an allocated fill in its holding window or in a span a
  funding proof walks, reads "shared" (`allocated`). Allocations are read inside 9b's evidence snapshot, so the
  Activity facts revision and cache are unchanged.
- **The browser.** A "Shared fills" section inside the execution panel offers Allocate, Split flip and Edit. The
  dialog shows the remainder and fee split live and the server's refusal.
  - The execution view holds the newest 20 allocations and up to 20 offered fills, with a total.
  - `GET /api/plays/{id}/execution/allocations?offset=` returns the older ones 20 at a time, so every allocation
    stays editable. It is real-only.
- **Other readers.**
  - The same per-line rule now feeds the monitor's protection checks and their quantities, open risk, the
    matcher's first entry fill, the plan-edit checks on filled entries and Review's unassigned-fill count.
  - An allocated fill is never `fill.unlinked`.
  - Review's imported positions and the sizing fallback leave out operations holding an allocated fill.
  - A retrospective Play cannot be made from allocated fills.
  - Activity and the monitor's position comparison are unchanged.
- **Tests.**
  - `FillAllocationTests`: conservation, the fee rule and its remainder, unknown fees, shapes and revisions.
  - `FillAllocationPostgresTests` and the allocation cases in `ExecutionPostgresTests`: two Plays closing
    through a shared exit, a flip opening the second Play, an Unresolved Play settling, stale versions and
    revisions refused with nothing written, Review and sizing exclusions, a resting flip order and its later
    fill, successive flips kept out of imported positions, and paging past 20 allocations.
  - `FillAllocationMonitorTests`: a flip-opened Play's missing stop and a shared exit's undersized stop.
  - The allocated cases of `PlayFundingPostgresTests`, owner isolation, the paper refusal and the inventories.
  - In the browser: the dialog's live remainder, fee split, bounds and errors, older pages, and the panel.
- **Migration.** It is applied explicitly, never to the owner's database.
- **To revisit (owner, October 23).** These choices stay as built and are recorded to revisit later.
  - An allocation is allowed on a fill whose order is linked to no Play. A flip order can never be linked, and an
    order links to one Play only.
  - Fee precision follows the reported fee's own decimals, at least 6; there is no per-token table.
  - A flip whose opening part belongs to no Play cannot be allocated, since the lines must sum to the fill.
  - A Closed Play's lines cannot change, so a wrong split that closed it cannot be corrected for it.
  - The monitor's position comparison still reads linked fills only, so a Play opened by a flip split shows an
    unexplained position.
  - Any allocated fill in a funding proof's span makes funding "shared", even before the Play's window.
  - The revisions are kept but not shown, and the migration's `Down` refuses while allocations exist.
  - Entries alone go beyond the contract's wording. They are limited to later opening fills of an order a flip
    already split; the alternatives are counting them for the Play that took the entry, or refusing a flip split
    while its order still rests.
  - A shared exit does not split its order, so the order's other fills still count wholly for the Play holding
    the link. A mismatch shows as an over-exit; splitting the order would make every later fill wait for an
    allocation.
  - Each page offers at most 200 Plays for a new line.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `e7267e40`. Four findings, all fixed in
  `8148cfb6`:
  - **High.** The monitor built a Play's executions from linked orders only,
    so a Play opened by a flip split had no entry: its missing stop stayed
    undecidable even with a complete book, and a shared exit's whole fill set
    the protection quantities. Protection checks now read allocated
    executions by the per-line rule; the position comparison is unchanged.
  - An allocated entry's order still resting did not stop the Play from
    closing, so a partly filled flip order could close the Play it opened.
    The orders behind allocated entry lines now count in the closing guard
    and entry progress, and a later fill of an order a flip split counts for
    no Play, flagging its Plays as not reconciled, until the owner allocates
    it.
  - Review's imported positions still treated an allocated round trip whose
    orders link to no Play as unassigned, so a trade excluded as not
    allocated could be scored as a position. Operations with an allocated
    fill are now left out of imported positions and the sizing fallback.
  - The browser capped shared fills below what the server returns, so a Play
    with many allocations failed validation on every read. The execution view
    now holds the newest 20 allocations and 20 offered fills with a total, a
    paged route returns the rest, and both sides share the same bounds.

  Sol confirmed exact conservation and fee arithmetic, ownership and account
  checks, the paper refusal, lock ordering, version checks and rollback,
  Closed lines kept, Unresolved settlement, the scoring exclusions, the
  funding fence, the migration and deletion, theme tokens and `NativeSelect`.
- **Check.** `scripts/check.sh` passed on `8148cfb6`, after the first run's only failure, the monitor's browser
  contract fixture, was regenerated for the new execution fields: backend 4,314 passed, 5 skipped; frontend 189 files and 4,160 tests.

## 13. Journal-wide search

October 23, 2026.

**13. Journal-wide search and list outcomes (F-07).** Implemented on `t3/phase2-search`.
- **The projection.** `play_search` holds one row per Play: its outcome state, latest actual exit, Review's
  scored result, estimated R and the reason R is missing. `play_search_texts` holds each searchable text,
  normalized and lower-cased: title, thesis, invalidation, strategy and evidence notes; the review note,
  lesson, mistake tag and next action; log entries; image notes (migration `JournalSearch`). Both are
  deleted with their Play.
- **Written with every change.** Each save records the Plays it touched directly (revisions, status
  changes, log entries, image notes, manual executions, order links, paper executions and simulations)
  and through imported fills and changed orders, by the shared `OrderKey` rule. A unit participant
  rewrites those Plays from the same transaction before commit, under per-Play advisory locks taken in ID
  order as the unit's last lock, then `FOR KEY SHARE` on the Plays, so a concurrent Draft deletion waits
  and a deleted Play is skipped. Read-only snapshot units write nothing.
- **Outcomes.** Review's own rules (`ReviewService.Score`, `PlayExitTime.LatestExit`), so list and
  search figures equal `/api/review`. An exclusion by an execution dated after now carries a recheck time;
  past it, reads compute the outcome again from the page's snapshot without writing, until the next write
  or the rebuild stores it.
- **The endpoint.** `GET /api/plays/search?q=&offset=&tradingMode=` takes 2–100 characters, escapes `%`,
  `_` and `\`, pages 50 at a time with `nextOffset`, and returns the matched field, an excerpt and the
  match position, with how many Plays are not indexed yet. List rows gain `outcome`; a Play without a
  projection reads as not indexed, never as zero. The paged list is otherwise unchanged.
- **The browser.** "Search the journal" sits beside New play; its results replace the tabs and list while
  a query is active, and read again after any finished action or stored-state reload. Ended rows show the
  exit, result and estimated R, with unknown values and their reason.
- **Initialization.** The offline `play-search-rebuild [--apply] [--all] [--batch 1-500]` command dry-runs
  by default, walks every owner in bounded batches under the same locks, is idempotent, and also stores
  outcomes past their recheck time. See `deploy/README.md`.
- **Tests.** `JournalSearchTests` and `JournalSearchPostgresTests`: every write path through the API, a
  rollback, fills and orders reaching a closed Play, paper evaluation, trading-mode separation, paging and
  escaping, the rebuild's idempotence, an exclusion expiring without a write, and a Draft deleted while it is
  projected. `SearchIsolationTests` in both auth modes. The browser's search, its reload and validators.
- **Migration.** It is applied explicitly, never to the owner's database; run `play-search-rebuild --apply`
  once after it. It was regenerated on the restack (`20261023075337_JournalSearch`) so that it orders after
  9c's `FundingSchedule`; its operations are unchanged.
- **Owner answer (October 23).** Outcomes past their recheck time are computed again on every read without
  writing, until the next write or the rebuild stores them. The owner chose to leave it as built.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `78581e97`. Three findings, all fixed in
  `b262ad07`:
  - An exclusion by an execution dated after now never expired: list and
    search kept the outcome unknown after Review scored it. The projection
    now keeps a recheck time, and reads past it compute the outcome again
    without writing; the rebuild stores it.
  - Visible search results ignored stored-state reloads, so a Play the
    monitor closed kept its old status and result. Search now reads again
    with every finished action and reload.
  - A Draft deleted while the rebuild projected it failed the parent key and
    ended the command. The projection now takes `FOR KEY SHARE` on the Plays
    after its advisory locks, so the deletion waits, and a Play deleted
    earlier is skipped.

  Sol confirmed capture completeness across saves, reviews, logs, evidence
  deletion, manual corrections, retrospective creation, links, imports, the
  monitor and paper evaluation; that the figures match Review with exact
  strings and unknowns kept; the unit of work and locks; owner and
  trading-mode isolation; escaping and paging; the migration and rebuild;
  and the browser's validators, debounce, cancellation and layout. It judged
  disabled-account figures, an excluded Play keeping its exit and the
  unsearched notes acceptable.
- **Check.** `scripts/check.sh` passed on `b262ad07`: backend 4,286 passed, 5 skipped; frontend 188 files and 4,149 tests.

## 14. Venue robustness and benchmark

October 21, 2026.

**14a. RISEx.** Implemented on `t3/phase2-risex-robustness`.
- **F-13 (probe V9).** Every trade-history page carries one `end_time` cut, the snapshot's observation.
  - A trade arriving between pages cannot shift page 2.
  - Later rows wait for the next refresh, whose window overlaps.
- **Counted omissions.** The history notice ends with one sentence for trades on unrecognized
  markets, for example "Omitted executions: 1 on an unrecognized market."
- **D-22 moved to item 7.** A trade without reported PnL still fails the read, as before.
  - Setting it aside left only a count. The shared `MissingExecutions` rule checks only orders
    with status filled, so a cancelled or still-open order with a set-aside trade looked complete,
    and its Play could close and be scored (Sol round 1, below).
  - Item 7's durable omission markers keep the trade's order. A linked order with a marker will
    have known missing executions whatever its status, which also covers Lighter's existing
    omissions. The contract is corrected here.
- **Tests.** RISEx adapter cases for the cut and its paging request shapes, the counted notice, and
  missing PnL still failing the read.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `1b7620d7`. Two findings, both settled in
  `ddf801e7`:
  - **High.** A trade set aside for missing PnL left only a count. The shared
    `MissingExecutions` rule checks only filled orders, so a cancelled or
    still-open order with a set-aside trade looked complete, and its Play
    could close and be scored with a unit still open. The set-aside (D-22)
    moves to item 7, whose durable omission markers keep the trade's order;
    until then such a trade fails the read, as before.
  - The set-aside returned before the trade's other required fields were
    validated, so a malformed row was accepted. The revert removes that path,
    and item 7 validates first.

  Sol confirmed that every history request carries the same nanosecond cut,
  that a row at the cut is kept and a later one skipped without counting as
  an omission, and that net-of-fee handling and the history-gap rule are
  unchanged.
- **Check.** `scripts/check.sh` passed on `ddf801e7`: backend 4,094 passed, 5 skipped; frontend 177 files and 4,085 tests.

**14b. Hyperliquid.** Implemented on `t3/phase2-hyperliquid-robustness`.
- **F-11 (probe C9).** Instruments state `MaxPriceDecimals` (`6 − szDecimals`).
  - Matching and the monitor's drift and level checks take one unit of the last allowed decimal as a
    minimum step. A planned 0.051234 at `szDecimals = 1` now matches an order at 0.05123.
  - The browser's chart and suggestion rounding keep five significant figures within the cap.
  - Prices of six digits or more stay whole numbers.
  - Nothing becomes stricter, and typed prices are kept as entered.
- **F-14 (probe V6).** On separate-ledger accounts (`default`, `disabled`, `dexAbstraction`) a failed,
  dropped or malformed wallet read degrades only the wallet.
  - Perps and fills still apply, and the last observed wallet is kept with its own read time.
  - The account mode read with the perps is kept even when the wallet failed, so availability
    follows the current mode.
  - The balance is unknown until a wallet is read with the perps again. Separate ledgers add up only
    when the wallet was read with the perps or after them: an older wallet could hold funds since
    moved into perps. Account detail shows the wallet's own read time.
  - Unified and portfolio-margin accounts, rate limits and the shared deadline still fail the read.
- **Tests.**
  - `PriceRuleTests` (2, one on PostgreSQL).
  - Wallet degrade cases, the stale-wallet balance, and the transfer case on PostgreSQL.
  - `price-step.test.ts` and the Account detail stale-wallet note.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `fd157150`. Three findings, all fixed in
  `68b8933d`:
  - A dropped connection or a malformed wallet body escaped the wallet
    boundary and discarded the perps and fills already read. They degrade the
    wallet like an invalid response; a rate limit, cancellation or the read's
    deadline still stops the read.
  - **High.** A kept wallet looked current and could count funds twice: with
    1,000 in the wallet moved into perps and the next wallet read failing, the
    balance read 2,000. Separate ledgers now add up only when the wallet was
    read with the perps or after them; otherwise the balance is unknown, and
    Account detail shows the wallet's own read time.
  - **High.** The account mode lived only in the wallet, so a failed wallet
    read kept the old mode and its stale availability. The mode read with the
    perps is kept on its own.

  Sol confirmed that the decimal cap reaches instruments, matching, the
  monitor's checks and the browser's rounding without venue-name checks, that
  typed levels are never rewritten, and that unknown modes, unified and
  portfolio-margin accounts, rate limits and deadlines still fail the read.
- **Check.** `scripts/check.sh` passed on `b91d8ea9`: backend 4,106 passed, 5 skipped; frontend 178 files and 4,090 tests.

**14c. Fill conflicts (F-12, probe V10).** Implemented on `t3/phase2-fill-conflicts`.
- **The rule.** A retained fill ID reported again with different venue facts keeps its first copy,
  never overwritten.
  - It is recorded in `fill_import_conflicts` (migration `20261021180627_FillImportConflicts`), one row
    per fill, with the differing field names only, first and last seen, and a report count.
  - The venue's direction wording is compared. Vessel's own readings are not: canonical label,
    position effect and bases.
  - A changed later copy within one read is compared with the copy stored from it, and RISEx
    collapses only identical copies.
  - A venue contract or leverage newly stated where none was is not a change.
- **The read.** `GET /api/accounts/{id}/import-conflicts` returns the count and the latest 50. A
  disabled account returns none, and a paper request gets 409.
- **The page.** Account detail shows them only when there are some.
- **Lifecycle.** Account deletion removes them, and the fill foreign key cascades. The owner inventory
  already reaches them through `imported_fills`.
- **Tests.**
  - `FillConflictPostgresTests` (4), and a RISEx adapter case for identical and changed copies.
  - The isolation case in both auth modes.
  - The Account detail panel test.
- **Migration.** It is applied explicitly, never to the owner's database.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `f32f731e`. Four findings, all fixed in
  `c9b9e625`:
  - Deleting the account in a context that had recorded a conflict hit the
    account relationship, since the conflict stayed tracked. Deletion detaches
    it with the account's other facts.
  - A changed second copy within the first read was dropped silently. It is
    compared with the copy stored from it, and RISEx collapses only identical
    copies, so the change reaches the store.
  - The venue's direction wording was not compared: a buy reported again as
    Close Short instead of Open Long was no conflict. It is compared now;
    derived effects and bases still are not.
  - A disabled account's conflicts stayed readable through the API. It
    returns none now.

  Sol confirmed that changed repeats used to be ignored and originals stay
  untouched, the decimal and time precision, owner scope, account locking,
  the paper refusal, DTOs without values or payloads, and the migration. Its
  own tests could not run under the memory limit; the targeted runs and the
  check below cover the fixes.
- **Check.** `scripts/check.sh` passed on `c9b9e625`: backend 4,113 passed, 5 skipped; frontend 179 files and 4,091 tests.

**D-19. Undersized-stop confirmation.** Implemented on `t3/phase2-stop-confirmation`.
- **The defect (M2).** The undersized-stop fingerprint covers the quantities and orders involved, so a stop
  that stayed smaller than a changing position, or a stop whose quantity kept changing, restarted
  confirmation on every check and never opened.
- **The fix.** `protection.short` confirms on its condition (`ConfirmsOnCondition`): its kind and subject,
  with the Play's linked stop covering less than its open quantity.
  - The pending fingerprint, and a candidate's or open record's fingerprint and details, follow the latest
    observation. An open record shows the current quantities without re-announcing, and a dismissed record
    reopens only on changed evidence, after the same 60 s.
  - An unknown, failed, discarded or interrupted check, a fresh absence, expiry and the dismissed evidence
    returning still restart the sequence.
  - Every other kind still confirms on its fingerprint, apart from 6c's `position.unprotected`, which uses
    the same mechanism. Severity and announcements are unchanged. No migration.
- **Contract notes.** The lifecycle mechanism and its tests landed in 6c (its Sol round 1), which needed it
  first. This pull request applies it to `protection.short`.
- **Owner answer (October 23).** Other kinds should confirm on their condition where a changing size is normal:
  `protection.missing`, `protection.excess`, `position.short` and `position.excess`, delivered in D-19b below. The
  rest stay on their fingerprint.
- **Tests.** `MonitorDiscrepancyStopConfirmationTests`:
  - changing quantities confirming once, with the latest evidence;
  - the condition ending resolving it;
  - unknown and interrupted observations breaking the sequence;
  - a dismissed record staying dismissed until changed evidence confirms, and reopening with its latest
    evidence;
  - other kinds still confirming on their evidence.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `cd1cafde`. No findings. Sol confirmed:
  - the 60-second sequence surviving changing quantities, and every interruption restarting it;
  - silent evidence updates without new revisions, dismissal and reopening;
  - the Hub's relevance and withdrawal on published revisions;
  - every other kind unchanged;
  - no dependence on the position snapshot, so no earliest source time;
  - per-Play subjects, under which a replaced undersized stop rightly continues the sequence;
  - the tests and docs.
- **Check.** `scripts/check.sh` passed on `9dd2b8ed`: backend 4,268 passed, 5 skipped; frontend 186 files and 4,141 tests.

**14d. Benchmark.** Implemented on `t3/phase2-benchmark`.
- **The script.** `scripts/benchmark.py` (standard library only, with `scripts/test_benchmark.py`). See the "Phase 2
  benchmark (item 14d)" section of `performance-plan.md`.
  - It runs only with `--disposable`, never under CI, never reads `.env` and takes no connection string.
  - It starts its own labelled PostgreSQL 18 and RabbitMQ containers with per-run secrets passed through the
    environment, migrates explicitly in `deploy/migrate`'s order, and starts the Release API in Production/Token
    mode without worker modules. Cleanup removes everything carrying the run's label, after errors and signals
    too.
- **The dataset.** It is deterministic from `--seed` and `--anchor`, and scaled by `--years` and `--scale`. It is
  seeded in one COPY transaction, with every constraint and trigger, then the search projection is rebuilt.
  - One owner with four portfolios and ten accounts across Hyperliquid, Lighter and RISEx, one disabled.
  - Round trips, flips and partial fills, with funding, snapshots, leverage and risk observations.
  - Plays in every status, with plans, links, reviews, log entries, evidence notes and if-then rules.
  - Omission markers, forced events, coverage records, gaps, backfills and import conflicts.
- **The full run** (`--years 3 --scale 1 --warmup 5 --rounds 20 --anchor 2026-10-23T12:00Z`), on `00cb8ceb`, on a
  Ryzen 5 7640HS with 10 CPUs and 20 GB (13 GB free), in about 10 minutes.
  - **The data.** 184,047 rows: 48,745 fills, 52,147 orders, 58,951 funding payments and 774 Plays. Activity
    aggregates the 45,928 fills of enabled accounts into 15,532 operations.
  - **Peaks.** The API's peak RSS was 1,016 MB and PostgreSQL's 170 MiB.
  - **The method.** Times are ms to the last body byte. "After write" is the first read after a committed no-op
    write moves the facts revision. Cached repeats are estimated hits at about 128 MiB of the 256 MiB cache.

  | Read | p50 | p95 | After write p50 |
  | --- | ---: | ---: | ---: |
  | Activity, page 1 | 8.4 | 10.1 | 640.6 |
  | Activity, page 79 of 100 rows | 8.8 | 10.4 | 611.1 |
  | Activity, closed longs in 90 days | 8.5 | 13.1 | 653.1 |
  | Activity, one operation's executions | 1.9 | 2.0 | 87.4 |
  | Review, all | 1,577.7 | 1,649.3 | – |
  | Review, year | 616.3 | 654.9 | – |
  | Review, all with imported positions | 3,506.4 | 3,574.1 | 4,134.8 |
  | Plays, All / In progress | 3.7 / 2.5 | 4.5 / 4.0 | – |
  | Plays, Closed page 12 / counts / `q=BTC` | 3.2 / 1.4 / 3.0 | 4.4 / 1.8 / 4.5 | – |
  | Overview | 12.3 | 13.2 | – |
  | Overview summary by portfolio, all | 25.0 | 27.2 | 616.7 |
  | Overview summary by account, 30 days | 12.1 | 12.7 | 609.9 |
  | Open risk | 9.5 | 10.4 | – |
  | Journal search: common word / offset 300 / rare word | 26.5 / 29.1 / 7.0 | 27.6 / 30.9 / 7.9 | – |
- **What it shows.**
  - Review is the slowest read: it is not cached, about 1.6 s over all time and 3.5 s with imported positions,
    which also rebuild the Activity aggregate after a write (4.1 s).
  - An Activity aggregate after a write takes about 0.6 s, and its cached repeats stay under 15 ms.
  - The Plays list, Overview, open risk and search stay under 30 ms.
  - No change is made here; caching Review is a candidate for later work.
- **Not covered.** Monitor tables, events and notifications, manual executions, retrospective Plays,
  allocations and most paper data are not seeded. Review's process, exclusion and CSV views, the week page,
  and scoped Activity and Overview are not timed.
- **Tests.** `test_benchmark.py` (25): the generator's shape, determinism and order-before-fill times, the
  percentiles, the safety guard, cleanup after an interrupted or failed start, the cache estimate, and deep-page
  selection and rejection. They need no Docker.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `9baf1409`. Four findings, all fixed in
  `00cb8ceb`:
  - A container that Docker created before an error or a signal interrupted
    its start was never tracked, so cleanup could leave it running. Each name
    is now recorded before its start, and cleanup also removes every
    container carrying the run's label, after checking the label.
  - The repeat request after a miss was always reported as a cache hit,
    though the Activity cache refuses an aggregate larger than its capacity,
    as at large scales. The API exposes no cache diagnostics, so the report
    now shows the first read after the write apart from the repeat, and a
    Cache column estimated from the rows by the cache's own size rule: hit,
    unconfirmed or not cached.
  - The deep-page checks accepted empty or clamped pages, so a small dataset
    timed page 1 or an empty page as a deep one. Each deep page is now the
    middle page by the counts the API reports, must come back as requested
    and with rows, and is skipped, by name, when the dataset cannot fill it.
  - Some generated executions preceded their order's placement. Fill times
    now come first, and the order is placed before its earliest fill.

  Sol confirmed database isolation and secret handling, the timing
  arithmetic (interleaving, warm-up exclusion and percentiles), determinism,
  and that nothing runs in CI or needs Docker in the unit tests.
- **Check.** `scripts/check.sh` passed on `00cb8ceb`: backend 4,314 passed, 5 skipped; frontend 189 files and 4,160 tests.

**D-19b. More warnings confirm on their condition.** Implemented on `t3/phase2-confirmation-kinds`.
- **The owner's decision (October 23).** Four more kinds confirm on their condition (`ConfirmsOnCondition`), where a
  changing size is normal while a trade is built. Their subjects are unchanged: per Play for the protection kinds,
  per account and instrument for the position kinds.
  - **`protection.missing` (critical).** A position that keeps growing through partial entry fills after the
    grace used to restart the sequence on every check, so the warning never opened. It passes no earliest
    source, since it becomes present only from linked fills and the book.
  - **`protection.excess`.** It passes the snapshot's time as its earliest source when a whole-position stop or
    target counts the open quantity because the snapshot agrees; a plain oversized order rests on the book alone.
  - **`position.short` and `position.excess`.** They rest on the snapshot and linked fills, and pass none. They
    stay separate kinds, so a switch between them starts the other's own sequence.
- **Unchanged.** Every other kind still confirms on its fingerprint, and `protection.cancelled` still supersedes
  missing and short. Severity and announcements are unchanged. No migration.
- **Accepted as built.**
  - A switch between an oversized stop and an oversized target on one Play continues `protection.excess`.
  - An untracked position that flips without a flat read continues `position.excess`.
  - A rare flip of the expected sign continues `position.short`.
- **Tests.** `MonitorDiscrepancyConditionKindsTests`. For each kind: changing quantities confirming once with the
  latest evidence; interruptions and a fresh absence restarting; and a dismissed record reopening only on changed
  evidence, also against PostgreSQL. Also a growing position after the grace, the short-to-excess switch both
  ways, and an excess resting on the snapshot. 32 of its 50 cases failed before the fix. The other kinds stay on
  their evidence.
- **Sol (Codex) round 1 (`gpt-6.1-sol`, high),** on `2d4c1645`. No findings. Sol confirmed:
  - the grace gating and every interruption restarting the sequence;
  - each kind's earliest-source choice, given which reads decide it;
  - the accepted switches keeping the kind-and-subject invariant;
  - the fingerprint kinds unchanged, cancelled-stop supersession included;
  - silent evidence updates changing no revision or relevance, with a critical missing stop announcing once on
    opening;
  - the docs.

  It suggested more coverage: a whole-position target with split read times, the accepted switches and direct
  revision assertions.
- **Check.** `scripts/check.sh` passed on `2d4c1645`: backend 4,354 passed, 5 skipped; frontend 189 files and 4,160 tests.
