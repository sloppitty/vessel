# Venue instruments, price rules and market-data contracts

October 5, 2026, slice S3 of the RISEx-first plan ([risex-integration.md](risex-integration.md)). It builds on S1 ([venue registry](venue-registry.md)) and S2 ([execution facts](execution-facts.md)). Behaviour for Hyperliquid is unchanged.

## Instruments

`VenueInstrument(ContractId, QuantityDecimals, MaxLeverage, QuoteAsset, PriceStep?, Category?, VenueContractId?, MaintenanceMarginFraction?)`:

- **`ContractId`** is Vessel's instrument key: the canonical asset (`BTC`, `DOGE`). Plays, drawings, matching and the chart use it.
- **`VenueContractId`** is the venue's own identifier when it differs, such as RISEx's numeric `market_id`. Imported fills and orders store it beside the key (nullable column on both tables, migration `VenueContractIds`). Hyperliquid leaves it null because its coin name is the key.
- **`PriceStep`** is the instrument's tick when the venue has one.
- **`MaintenanceMarginFraction`** (October 7, 2026) is the maintenance margin as a fraction of notional when the venue states it. The Hyperliquid adapter reports its rule, half the initial margin at the contract's maximum leverage (0.0125 at 40×). RISEx leaves it null until R1.8 confirms the unit of `maintenance_margin_factor`. The frontend's liquidation estimate (`estimatedLiquidation`) uses it, and the leverage suggestion and stop warnings use that estimate. Without it the estimate assumes the same half-of-initial-margin rule and stays labelled as an estimate.
- **`Category`** labels non-crypto markets (`stocks`, `commodity`, `index_etf`). The instrument picker shows a tag for them (decision: include every RISEx category, tagged).

Deferred to the Lighter phase: the multiplier part of the canonical key and the Hyperliquid `k…` → `1000…` data migration, since RISEx has no multiplier contracts.

## Price rules

`VenueDescriptor.PriceRule` is `significant-figures` (Hyperliquid) or `tick-size`.

- **Matching:** `ExecutionMatcher.Near` accepts the larger of one fifth-significant-figure step and one tick. On October 5, four RISEx markets (DOGE, PUMP, CRCL and a retired DOGE) had a tick coarser than that step. A tick finer than the step never tightens the tolerance. For `tick-size` venues the execution check reads the catalogue for ticks. If that read fails, matching falls back to the significant-figure step instead of failing the check.
- **Chart:** prices placed or dragged on the chart round to the instrument's tick when there is one (`formatDraggedPrice(price, step)`), otherwise to five significant figures. Typed prices are kept as entered.
- **Suggestions:** suggested stop and target prices round to the tick in the direction that keeps them within their limit (`roundPrice(price, way, step)`), otherwise to five significant figures.
- **Decimal cap (October 21, 2026; Phase 2, item 14b, F-11).** A significant-figure venue may state a cap per instrument as `VenueInstrument.MaxPriceDecimals`. Hyperliquid perpetuals take at most five significant figures, any integer whatever its length, and at most `6 − szDecimals` decimals.
  - **Matching.** `PriceRules.MinimumStep` gives one unit of the last allowed decimal, which joins the tick in `ExecutionMatcher.Near` and the monitor's drift and level checks. A planned 0.051234 at `szDecimals = 1` can only be placed as 0.05123, and now matches it (probe C9). The fifth-significant-figure step is otherwise unchanged, so nothing becomes stricter.
  - **The browser.** It reads the cap as a `PriceStep` (`lib/price-step.ts`): a tick, or `{ maxDecimals }`.
    - Chart placement and drags (`formatDraggedPrice`, `roundedLevelPrice`) and suggestions (`roundPrice`) keep five significant figures within the cap. Prices of six digits or more stay whole numbers.
    - `pricesMatch` uses the same minimum step as the server.
    - Typed prices are still kept as entered.

## Market data

- **Intervals:** `VenueDescriptor.Intervals` lists the intervals the venue serves natively. Candles and streams refuse others with `Interval must be one of …` built from that list, rather than approximating. `/api/system` publishes `intervals` and `priceRule`. The chart's timeframe bar offers only the venue's intervals and falls back to `1h` (or the first interval) when the saved preference is not served.
- **Notices:** `CandleNotice` and `MarketContextNotice` on the descriptor replace the generic notices. Hyperliquid keeps its exact texts.
- **Loosened fields:** `VenueCandle.Trades` (and `CandleDto.trades`) may be null when a venue reports no trade count. `VenueMarketContext.PreviousDayPrice` may be null, and the chart then shows no 24-hour change. `OraclePrice` keeps its name; for RISEx it carries the index price.

## Venue-neutral shared code (October 7, 2026)

Application and frontend code no longer carry Hyperliquid wording or rules: the catalogue scope is `venue-perpetuals`, the generic market-context notice and catalogue error drop "primary perpetual DEX", wallet and overview notices speak of venue stablecoin wallets, account labels say "Perps equity" and "Perps withdrawable", and the account mark colours venues with the `sync` capability instead of a `.hyperliquid` class. Hyperliquid keeps its own texts in its descriptor and adapter.

Still Hyperliquid-shaped, by decision or until Lighter needs it: the `unifiedAccount`/`portfolioMargin` account modes that decide whether the wallet already holds perps equity (only Hyperliquid has a stablecoin wallet), the market stream relay, the `Address` source identity and the `k…` key migration ([lighter-integration.md](lighter-integration.md) L0.2, L0.4), and `OraclePrice` keeping its name with funding interval and unit implied as hourly (L0.8).

## Tests

- `VenueMarketDataTests`: venue intervals and notices, null trade count and previous-day price, tick-aware tolerance.
- `ApiTests`: the published `intervals` and `priceRule`.
- `InstrumentCatalogueApiTests`: the catalogue's exact shape including the new nullable fields.
- Frontend: tick rounding in `levels.test.ts`, `TimeframeBar.test.tsx`, and system-contract validation of intervals and price rule.
- October 7: Hyperliquid maintenance fractions in the adapter and catalogue tests; stated maintenance margin in `sizing.test.ts`; tick rounding of suggestions in `suggestions.test.ts`; catalogue scope and fraction validation in `workspace.test.ts`.
