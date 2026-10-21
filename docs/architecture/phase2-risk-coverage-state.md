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
- The migration is applied explicitly, never to the owner's database.

## 2. Weekly review page

Not started.

## 3. One promoted if-then rule

Not started.

## 4. Earlier-order link candidates

Not started.

## 5. Export

Not started.

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
