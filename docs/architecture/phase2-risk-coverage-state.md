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

Not started.

## 3. One promoted if-then rule

Not started.

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

Not started.

## 7. Lighter forced events

Not started.

## 8. Venue-reported risk context

Not started.

## 9. Funding

Not started.

## 10. Planning assumptions and soft limits

Not started.

## 11. Shared-exit allocation

Not started.

## 13. Journal-wide search

Not started.

## 14. Venue robustness and benchmark

Not started.
