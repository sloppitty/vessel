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
- **The D-20 finding.**
  - Reading the code showed that orders placed before the earliest active
    Play on a contract was created are never stored
    (`PlayExecutionService.ReconcileAsync`; `play-lifecycle.md`,
    "Relevance").
  - Item 4 therefore widens retention by a bounded lookback instead of only
    showing candidates.
- No owner database was touched.

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
