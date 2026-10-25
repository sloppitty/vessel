# Normalized execution facts

October 13 diary completion adds explicit manual facts in separate play-owned
records, never in imported order/fill tables. The ledger preserves submitted
decimal strings and correction history. Its normalized in-memory projection
reuses lifecycle/result validation while retaining manual provenance and
unknown-cost coverage. Whole-order retrospective linking never changes the
imported facts. See [the diary contract](diary-completion-contract.md).

October 5, 2026, slice S2 of the RISEx-first plan ([risex-integration.md](risex-integration.md)). Builds on the [venue registry](venue-registry.md) (S1). Behaviour for Hyperliquid is unchanged; stored Hyperliquid rows are converted once by a migration.

## Why

Matching, lifecycle and sizing used to read Hyperliquid's encoding directly: `"B"`/`"A"` for sides and `Direction` text starting with `"Close"` for reducing fills. RISEx (and later Lighter) report the same facts differently: RISEx uses `BUY`/`SELL` with a `position_side`, and its `realized_pnl` already has the fee taken off. Each adapter now translates into one vocabulary, and the business logic reads only that.

## Vocabulary (`Vessel.Domain.Workspace.ExecutionFacts`)

| Fact | Values | Meaning |
| --- | --- | --- |
| `Side` (fills and orders) | `buy`, `sell` | Direction of the execution or order. |
| `PositionEffect` (fills) | `open`, `close`, `flip`, `unknown` | Opened or increased, reduced or closed, or flipped the position. `unknown` when the venue's wording does not say; never guessed. |
| `FeeBasis` (fills) | `reported`, `standard-account-free` | The venue's fee amount, or zero because the account tier is fee-free (Lighter Standard). A fee-free basis with a non-zero fee is rejected. |
| `PnlBasis` (fills) | `gross`, `net-of-fee` | Closed PnL before fees (fee reported separately; Hyperliquid) or with the fee already taken off (RISEx). |

`Direction` keeps the venue's own wording for display.

## Rules

- **Adapters** translate. Hyperliquid: `B`→`buy`, `A`→`sell`; `dir` "Open Long/Short" → `open`, "Close Long/Short" → `close`, "Long > Short"/"Short > Long" → `flip`, anything else (liquidation or settlement wording) → `unknown`; closed PnL is `gross`, fees `reported`.
- **Validation:** `VenueFactChecks` rejects adapter output outside the vocabulary before anything is stored. Sync treats it as a venue failure (the account keeps its previous facts and reports a refresh error); an order read answers the usual 502.
- **Matcher:** plan levels carry `buy`/`sell`; a long entry buys and its exits sell. Retained fills must not contradict the assigned role. Entry links reject close/flip effects, and exit links reject open/flip effects. Owner linking uses the same compatibility rule.
- **Lifecycle:** unknown effect does not assert a contradiction, but a known flip cannot settle a Play without split attribution. Compatible exits must equal entries in valid chronology, with no known missing fixed-size filled-order executions or resting entry order, before closure. Review retains its stricter requirement for explicit open/close effects.
- **Sizing:** a Play's net result is the sum of closed PnL minus the listed nominal stablecoin fees of `gross` fills only. `net-of-fee` fills already include their fee, and a fee paid in another token on such a fill does not make the result incomplete.
- **Execution totals** report `closedPnlBasis`: `gross`, `net-of-fee` or `mixed`. The panel labels net-of-fee totals "after fees". Since October 19 they also carry the scored `venueNetUsd` and the Play-local result `local` (see below).

## Persistence

Migration `NormalizedExecutionFacts` adds `PositionEffect`, `FeeBasis` and `PnlBasis` to `imported_fills`, converts existing rows (`B`→`buy`, `A`→`sell`, effect from the stored direction; fees `reported`, PnL `gross`) and only then adds check constraints for all four columns and for `imported_orders.Side`. `Down` converts sides back before dropping the columns.

## API

- `FillDto` (account activity) adds `positionEffect`, `feeBasis` and `pnlBasis`; `side` is `buy`/`sell`.
- Execution fills add `side`, `positionEffect` and `pnlBasis`; execution orders use `buy`/`sell`; totals add `closedPnlBasis`.
- October 13 integrity corrections add execution `issues`. Contradictory linked fills remain visible on their original links, but do not contribute to progress/P&L totals; the issues explain the exclusion.
- The browser validates these values and stops treating `A`/`B` as sides.

## Tests

`ExecutionFactsTests` (Hyperliquid mapping, vocabulary checks), the net-of-fee cases in `SizingTests`, `NormalizedExecutionFactsMigrationTests` (Postgres: conversion, constraints, no pending model changes) and the execution panel's net-of-fee label. Existing matcher, lifecycle and sizing tests run on the normalized values.

## Nominal stablecoin fee tokens (October 14, 2026)

`FeeToken` is still stored exactly as the venue reports it. Fee deduction checks it against the single Application list `NominalStablecoins` (USDC, USDG, USDT, USDT0, USDE, USDe and USDH; ordinal match, so `usdg` is not listed). Listed tokens count one-for-one in USD, without FX or depeg valuation; all other tokens leave gross-basis results unknown. Manual executions use the same list for their fee currency, and the frontend `features/plays/stablecoins.ts` mirrors it (a backend test checks they match).

## Reported fill leverage (October 18, 2026)

`VenueFill.Leverage` and `ImportedFill.Leverage` are an optional fact: the position leverage the venue reports on an execution, whole numbers 1–1000, null otherwise. Only RISEx reports it today; `VenueFactChecks` rejects other values. It is display evidence for Activity operations ([observed leverage](activity-operations.md#observed-leverage-october-18-2026)), never used by matching, lifecycle, Review or sizing.

## Derived results beside the facts (October 19, 2026)

Phase 0 item 0.3 ([contract](phase0-trust-correctness-contract.md#03-result-basis-receipt)).
Two derived results now sit beside the facts; neither changes a fact.

- **Venue net** (`PlayOutcomeCalculator.VenueNet`), the scored basis: Σ
  closed PnL less listed nominal stablecoin fees of `gross` fills only. The
  execution totals expose it as `venueNetUsd` over the fills they count
  (contradicting fills are left out), so far while the Play is open; null when
  a fee basis is missing or a gross-basis fee is in another token. Review and
  sizing score this basis only for a closed Play whose fills settle it, each
  with its own eligibility rules, and retrospective Plays are not in sizing.
  Whether the venue's execution P&L includes funding is unverified
  ([venue funding](venue-funding.md)). `PlayOutcomeCalculator.VenueGross`
  is the same basis before trading fees (closed PnL plus the listed fees
  already inside `net-of-fee` PnL; unknown for an unlisted one), kept exact,
  and feeds the fee-only-loss counts.
- **Play-local result** (`PlayLocalResult`): the Play's own fills, sell
  proceeds minus buy costs (direction × (exits − entries)) at exact
  quantity × price, less the fee of every linked fill whatever
  its `PnlBasis`, so each fee is charged once. It needs verified fees: a
  `FeeBasis`, a non-blank token, and a listed stablecoin or a zero fee. The
  totals expose it as `local` with `netUsd`, `basis`, `reason` and
  `signDiffers`. The Execution panel shows "Venue net" and "Play-local result"
  with a "Sign differs by basis" pill. Gates and reasons are in
  [Review's result bases](portfolio-review.md#result-bases-october-19-2026).

## Position risk context (October 21, 2026)

Phase 2, item 8a (see `phase2-risk-coverage-contract.md`). `VenuePosition` carries
the venue's own `LiquidationPrice` and `MarginMode` (`cross` or `isolated`). Both
are null when the venue reports none, and a reported zero price is null too. A
negative price is an invalid response, never unknown.
- **What each venue reports.**

  | Venue | Liquidation price | Margin mode |
  | --- | --- | --- |
  | Hyperliquid | `liquidationPx` | `leverage.type` |
  | Lighter | `liquidation_price` | `margin_mode` (0 cross, 1 isolated) |
  | RISEx | `liquidation_price` | `margin_mode` (0 cross, 1 isolated) |

  An unfamiliar mode value stays null. No venue reports a per-position
  maintenance requirement, so none is stored; Hyperliquid and RISEx report
  account totals only.
- **Where they are kept.**
  - The current snapshot (`account_positions`) always holds the latest
    report.
  - `account_risk_observations` keeps them when a position appears, when its
    quantity, entry price or margin mode changes, when an isolated position's
    liquidation price changes, or when its market has executions since the
    last observation, newly imported or already retained. History can arrive
    before the snapshot that observes a reopening, as for leverage.
  - A cross-margin liquidation price that only drifts with the account is not
    kept again.
  - Positions reporting neither field are skipped.
  - Account deletion removes the observations. Migration
    `20261021164349_RiskContext`, applied explicitly.
- **Where they show.**
  - Account detail lists each position's venue liquidation price and mode.
  - The Play execution read adds `venuePosition` and `riskAtFirstFill`. Both
    are limited to the Play's own position:
    - its direction;
    - the one native market its linked fills name (several make it unknown);
    - from its first entry fill to its last fill once it is flat;
    - an enabled account. A disabled account shows neither, as in the
      workspace.
  - `venuePosition` is the current one, and `riskAtFirstFill` is the first
    observation kept after the first entry fill.
  - The Execution panel shows each with the time it was read, for example
    "Venue liquidation 82.5 (cross) read … · first read after entry …".
    After the position is flat it keeps the first-read value ("Flat now").
  - These are the venue's figures, never mixed with the plan's isolated
    estimate.
