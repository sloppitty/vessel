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

Not started.

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
