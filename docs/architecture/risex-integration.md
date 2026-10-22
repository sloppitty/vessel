# RISEx integration: research, fit and tasks

Research checked October 5, 2026 against the official [RISEx API reference](https://developer.rise.trade/llms.txt) and live unauthenticated reads of the mainnet REST API (`https://api.rise.trade`). A public address taken from RISEx's open competition leaderboard was used only to inspect response shapes; it is not recorded here. No credentials were used. Nothing is implemented. This extends [lighter-integration.md](lighter-integration.md), whose L0 generalization phase RISEx shares, and feeds three refinements back into it (see [Changes to the shared L0 design](#changes-to-the-shared-l0-design)).

RISEx is a fully on-chain central-limit-order-book perpetuals exchange on RISE Chain (chain ID 4153). Perpetuals-first still applies: every RISEx market is a perpetual, including its stock, commodity and index/ETF perpetuals.

## Verdict

RISEx is the **easiest venue so far**. Everything Vessel needs is public and keyed by an EVM address, including orders and fills, so it needs **no credential and no vault**. Authenticated access exists only for trading and for private WebSocket channels, and both require EIP-712 signatures from a registered session key. Vessel must never hold such a key, so it simply does not use those paths. That makes RISEx a closer fit to Hyperliquid (address, public reads) than to Lighter (account index, token for orders), and it exercises the venue descriptor with a third combination (`evm-address` source, `none` credential, numeric market IDs).

## What RISEx exposes

Base URL `https://api.rise.trade`, WebSocket `wss://ws.rise.trade/ws`. REST is `GET` with query parameters and JSON responses wrapped as `{"data": …, "request_id": …}` (the body contains a stray newline before `request_id`; parse it with a normal JSON parser). Rate limit **500 requests per 10 s per IP** (WebSocket: 10 requests/s). The docs mark the API as under heavy development, so response shapes may change.

**Timestamps are nanoseconds** unless noted, as decimal strings (e.g. `"1791209839000000000"`, which fits `long` but not a double). All `intervals` and `from`/`to` candle bounds are in nanoseconds too.

**Header caveat:** the API's firewall answered `403` to the default `Python-urllib` user agent while accepting curl, an empty one and a custom one. Vessel sets an explicit `User-Agent`.

### Public endpoints used

| Need | Endpoint | Notes |
| --- | --- | --- |
| Catalogue | `GET /v1/markets` | Cached upstream for 5 min. 38 markets on October 5: 18 crypto, 11 stocks, 4 index/ETF, 4 commodities, 1 deprecated. Per market: `market_id` (string int), `config.name` (`BTC/USDC`), `config.step_price` (tick), `config.step_size` (lot), `config.min_order_size`, `config.max_leverage` (3–25), `config.maintenance_margin_factor` (4.5–37.5, unit unverified), `active`, `config.unlocked`, `category`, `mark_price`, `index_price`, `last_price`, `open_interest`, 24 h stats, `current_funding_rate`, `funding_interval` (always 3,600,000,000,000 ns = 1 h) |
| Account snapshot | `GET /v1/portfolio/details?account=0x…` | Decimal strings. Summary: `total_account_value` (equity), `usdc_balance`, `collateral_margin_balance`, `cross_margin_balance`, `free_collateral`, `total_unrealized_pnl`, `total_initial_margin`, `total_maintenance_margin`, `realized_pnl`, `unsettled_usdc`, `in_liquidation`, `risk_level`. Positions per market: `size`, `side`, `avg_entry_price`, `mark_price`, `leverage`, `unrealized_pnl`, `liquidation_price`, `margin_mode`, margin fields. Markets with no position appear with `size: "0"` and must be skipped. |
| Raw positions | `GET /v1/positions?account=…` | **18-decimal fixed-point integers** (`"size": "702475000000000000"` is 0.702475). Not used: portfolio details carries the same facts as decimals. |
| Fills | `GET /v1/trade-history?account=…&market_id=&start_time=&end_time=&page=&limit≤1000` | Page-numbered with `has_next_page`; time bounds in ns. Per fill: `id`, `order_id`, `market_id`, `side` (`BUY`/`SELL`), `price`, `size`, `fee` (USDC amount), `liquidity_indicator` (`MAKER`/`TAKER`), `time`, `blockchain_data.tx_hash`, `is_liquidation`, `realized_pnl`, `leverage`, `margin_mode`, `position_side`, `is_otc` |
| Orders | `GET /v1/orders?account=…&page=&limit≤1000` (history incl. open, filled, cancelled) and `GET /v1/orders/open?account=…` (resting, from on-chain state) | `id` (hex composite, equals the fill's `order_id`), `side`, `type` (`LIMIT`/`MARKET`), `time_in_force`, `status` (`ORDER_STATUS_OPEN`/`FILLED`/`CANCELLED`/…), `price`, `size`, `filled_size`, `avg_price`, `reduce_only`, `post_only`, `stop_type`, `stop_price`, `cancel_reason`, `created_at` (ns), `is_liquidation` |
| TP/SL orders | `GET /v1/orders/tpsl?account=…` | Stored off-chain by RISEx, executed on-chain when the stop price is met. Own status vocabulary (to verify). |
| Realized PnL events | `GET /v1/portfolio/realized-pnl?account=…` | Position-change events with entry/exit price, size, `pnl`, `funding`. Possible cross-check, not required. |
| Candles | `GET /v1/markets/id/{market_id}/trading-view-data?interval=<ns>&from=<ns>&to=<ns>` | See below. |

Everything above answered with **no authentication**. The OpenAPI files declare an empty `security` list for them.

### Candles: three hazards

1. **The parameters are `from`/`to`**, not `start_time`/`end_time`. The latter are silently ignored and the endpoint returns about the last hour.
2. **Native intervals are `1m 5m 15m 1h 4h 1d 1w`.** Any other value (`3m`, `30m`, `2h`, `8h`, `12h`, `3d`) silently falls back to **1m candles** (for `3d` that returned 42,529 rows), and `1M` is an error. Each row carries its `interval` label, so the adapter must reject a response whose label differs from the one requested.
3. **No row cap.** `5m` over 30 days returned 8,639 rows and `1h` over a year 4,496. The adapter must bound the window itself (as `CandleService` already caps Hyperliquid at 5,000 candles). History reaches back to the March 31, 2026 launch.

Rows have `time` (ns, bucket start), `open/high/low/close/volume` as decimal strings and **no trade count**.

### Streaming

`orderbook`, `trades`, `oracle` and `expected_funding_rate` are public; `orders`, `positions`, `fills`, `funding` and `account` need an EIP-712 authenticated session key. **There is no candle channel.** A live chart would be assembled from the public `trades` channel (price, size, per block) plus `oracle` (mark/index per block). Heartbeat: the server pings every 30 s and closes after 60 s without inbound traffic. Subscriptions are by **numeric `market_ids`**; subscribe requests are `{"method":"subscribe","params":{"channel","market_ids"}}`.

## Mapping to Vessel

### Identity and instruments

- **Account = EVM address** (the main wallet, 42 hex characters). The `evm-address` source type from the shared design applies unchanged. The same wallet can hold a Hyperliquid and a RISEx Vessel account because uniqueness is per owner, venue and source.
- **Contract = numeric `market_id`** (`VenueContractId`), with names like `BTC/USDC`. The canonical instrument is asset `BTC`, multiplier 1, quote `USDC` (key `BTC`). The map is built from `/v1/markets`.
- **Retired markets.** Market 13 is `DOGE/USDC [deprecated-1779958099]` (inactive) next to the live market 14 `DOGE/USDC`, and both reduce to `DOGE`. The one-to-one rule applies to **active** markets only. A retired market maps to the same key flagged `retired`, is never selectable, and its historical facts keep `VenueContractId = 13`. Order linking considers only the active market's orders. A venue-name suffix the map cannot classify excludes the market and is listed in the notice, never guessed.
- **Categories.** `category` (`crypto`, `stocks`, `commodity`, `index_etf`) is kept as display metadata on `VenueInstrument`. Hyperliquid's primary DEX is crypto only and Lighter's list is crypto-heavy, so equity perpetuals (`TSLA`, `NVDA`, `SPY`, `XAU`) are new to Vessel. They are perpetuals, so they are in scope, but their trading hours and oracle behaviour differ (to verify before offering liquidation estimates for them).

### Normalized execution facts

| Fact | RISEx source | Normalized |
| --- | --- | --- |
| Side | `side` `BUY`/`SELL` | `buy`/`sell` |
| Position effect | `position_side` is the side of the position the fill acts on. In 3,000 fills of one account: `side != position_side` in 1,671 (always with realized PnL beyond the fee: reducing or closing); `side == position_side` in 1,329, of which 1,097 had `realized_pnl == -fee` (opening or increasing) and **232 carried extra PnL** | `close` when different, `open` when equal and `realized_pnl == -fee`, otherwise `unknown`. The 232 are probably flips (the fill closes one position and opens the other, so `position_side` is the new side) or funding settled on trade; the open questions below decide it. Until verified they are `unknown`, never guessed as `open`. |
| Order ID | `order_id` = order `id` | direct link, no price matching needed |
| Fee | `fee`, USDC amount, 1 to 3 bps of notional | reported, `FeeBasis = reported` |
| Closed PnL | `realized_pnl` | **net of the fee**: every opening fill has `realized_pnl == -fee` exactly. Vessel's sizing computes closed PnL minus fees, so using this field as is would count the fee twice. See `PnlBasis` below. |
| Time | `time`, nanoseconds | UTC, millisecond precision kept, nanosecond string kept in raw facts |
| Liquidation | `is_liquidation` | preserved as venue flag |
| Order status | `ORDER_STATUS_OPEN`/`FILLED`/`CANCELLED`/… | `Open`/`Filled`/`Canceled`/… with the venue word kept |

Price rule: `config.step_price` (a tick) per market, e.g. 0.1 for BTC and 0.00001 for DOGE.

Margin: per-market `margin_mode` (cross `0` / isolated), leverage per position, `liquidation_price` reported by the venue. Equity, wallet and collateral stay distinct (`total_account_value` vs `usdc_balance` vs `free_collateral` vs `unsettled_usdc`) and are never summed, following `stablecoin-wallet.md`. The only supported collateral is USDC on RISE Chain, so there is no wallet read: the capital context shows equity and free collateral. The meaning of `unsettled_usdc` and `margin_health` needs verifying.

### What RISEx needs that Hyperliquid and Lighter did not

- A fill needs **`PnlBasis`** (`gross` for Hyperliquid, `net-of-fee` for RISEx) beside `FeeBasis`, and the sizing service must use it (task L0.5).
- A price tick (`PriceStep`) is the common form of a price rule (task L0.7).
- A stream protocol may have **no native candle channel** (task L0.2); chart refresh then uses REST.
- Candle intervals vary per venue, and an unsupported one must be rejected, not rounded (task L0.8).

## Changes to the shared L0 design

Applied to [lighter-integration.md](lighter-integration.md) in the same change:

1. **L0.2 stream protocol** declares capabilities (`candles`, `context`) so a venue without a candle channel is supported.
2. **L0.5 execution facts** gain `PnlBasis` and the sizing service reads it.
3. **L0.7 price rule** becomes a `PriceStep` tick (Lighter's `price_decimals` is `10^-d`; RISEx's `step_price` is direct); Hyperliquid keeps its five-significant-figure rule.
4. **L0.3 descriptor** gains `Category` support on instruments, and `Source` takes both `evm-address` and `account-index`.
5. **L0.6 instrument map** is keyed by the venue's own contract identifier (`market_id` for Lighter and RISEx, coin name for Hyperliquid) and handles retired markets.

## Tasks

RISEx follows L0 and needs nothing from L1 or L2. Because it needs no credential and no vault, the owner chose to build it **before Lighter**: see the revised sequence above, which folds the L0 dependencies into slices S1 to S3. Each task is one reviewable change that keeps `scripts/check.sh` green.

| ID | Task | Scope | Done when | Depends |
| --- | --- | --- | --- | --- |
| R1.1 | **Module skeleton.** `Venues/Risex`: descriptor (`evm-address` source, no credential, intervals `1m 5m 15m 1h 4h 1d 1w`, `tick-size` price rule, quote `USDC`, trade URL template), typed `HttpClient` with `RemoveAllLoggers()`, explicit `User-Agent`, 500/10 s budget with a safety margin (≤ 200 per 10 s), nanosecond timestamp helpers (`string → DateTimeOffset`, rejecting values beyond now + 5 min as Hyperliquid does). | Infrastructure | Helpers and budget unit-tested with `TimeProvider`; module registered, status `planned`. | L0 |
| R1.2 | **Catalogue and instrument map.** `/v1/markets` → active, unlocked markets as `VenueInstrument` (canonical asset, `PriceStep`, lot step, max leverage, category), retired market handling, cached about 5 min. | Risex module | Fixture from a recorded response: BTC, `DOGE` active/retired pair, a stock and a commodity; unclassifiable name excluded and noted. | R1.1, L0.6 |
| R1.3 | **Snapshot.** `/v1/portfolio/details` → positions (size × side, entry, uPnL, leverage, margin, reported liquidation price), account value (`total_account_value`), free collateral, margin used; zero-size rows skipped; equity, balance and unsettled kept distinct. | Risex module | Fixtures: no positions, long, short, isolated, in liquidation. | R1.2 |
| R1.4 | **Fills.** `/v1/trade-history` paged (≤ 1000 per page, bound the first refresh at 2,000 fills with a history notice that older fills are not imported), `side`/`position_side` → side and effect (`unknown` for the unverified same-side-with-PnL case), `order_id`, fee as reported, `PnlBasis = net-of-fee`, liquidation flag, nanosecond times. | Risex module, Application (sizing uses `PnlBasis`) | Fixtures: maker/taker × buy/sell × open/close; sizing test shows the fee is not counted twice; flip handling per the open question. | R1.2, L0.5 |
| R1.5 | **Orders.** `/v1/orders` (paged) + `/v1/orders/open` + `/v1/orders/tpsl`; status mapping, `stop_type`/`stop_price` → trigger, reduce-only, ns → UTC; verifies the order ID equals the fill's `order_id`. `PlayExecutionService` then tracks RISEx accounts with no credential. | Risex module, Application | Matcher and lifecycle tests run on a RISEx fixture: Planned → Open → Closed. | R1.4, L0.5, L0.7 |
| R1.6 | **Candles and market context.** `trading-view-data` with `from`/`to`, native-interval check (reject a response whose `interval` label differs), bounded window (≤ 5,000 candles, else clamp and notice), no trade count; context from `/v1/markets` (mark, index, last, open interest, 24 h volume, `current_funding_rate` hourly). | Risex module | Fixtures incl. a fallback-label response that must be rejected; chart shows RISEx candles in a manual check. | R1.2, L0.8 |
| R1.7 | **Account creation and rollout.** RISEx added to the account dialog (public address, validated like Hyperliquid's), `/api/system` status → `read-only`; chart live updates stay manual refresh until a trades-channel protocol exists. | Application, API, frontend | Create/duplicate/invalid tests; browser smoke adds a RISEx account from fixtures. | R1.1–R1.6, L0.9 |
| R1.8 | **Verification and docs.** Check the open questions with the owner's own account; update `account-management-contract.md`, `chart-plan.md` (intervals), `core-workspace-state.md`, `play-lifecycle.md` (RISEx tracking). | docs | Questions answered or explicitly deferred. | R1.7 |

### Later (not planned)

- Live chart from the public `trades` and `oracle` WebSocket channels (needs candle aggregation inside a stream protocol).
- Private streams: not possible without a session key, so polling stays the model.
- Depth/orderbook features: `getorderbooklevels` exists but is outside current scope.

## Owner decisions (October 5, 2026)

1. **Categories:** include every RISEx perpetual (crypto, stocks, commodities, index/ETFs), each with a visible **category tag** in the instrument picker. Liquidation estimates for non-crypto markets stay labelled as estimates and are verified in R1.8.
2. **Live chart:** manual refresh first, for testing; automatic updates (candles assembled from the public `trades` channel) follow once R1 works.
3. **Sequence:** RISEx first, **generalizing and refactoring as it makes sense** rather than as a separate neutral L0 phase. See [Revised sequence](#revised-sequence-risex-first).

## Revised sequence (RISEx first)

Doing RISEx first changes the order of the shared L0 work in [lighter-integration.md](lighter-integration.md). Each slice is a PR that is neutral for Hyperliquid and keeps `scripts/check.sh` green. A task moves to Lighter's phase when RISEx does not need it, so nothing is generalized before a second venue needs it.

| Slice | Contents | Why now |
| --- | --- | --- |
| **S1** | Venue registry and descriptor, services resolve adapters by `account.VenueId`, `/api/system` capabilities, frontend checks capabilities (L0.3, L0.9) | A second adapter cannot be injected without it. |
| **S2** | Normalized execution facts: `buy`/`sell`, `PositionEffect`, `FeeBasis`, `PnlBasis`; sizing reads `PnlBasis` (L0.5) | RISEx `realized_pnl` is net of fees. |
| **S3** | Canonical instrument and venue contract ID (RISEx `BTC/USDC` and numeric `market_id`), `PriceStep` and `Category` on `VenueInstrument`, loosened market-data contracts and descriptor-driven intervals (L0.6 core, L0.7, L0.8) | RISEx names, ticks, intervals and categories. |
| **S4** | RISEx module (R1.1 to R1.7). The strict JSON and bounded-HTTP helpers are extracted from the Hyperliquid adapter at the start (L0.1), because this is their second user. | The venue itself. |
| Deferred to Lighter | Generic stream relay (L0.2), generic `SourceId` instead of `Address` (L0.4), the Hyperliquid `k…` data migration and drawing-key rewrite, request budget tuning | RISEx uses an address and manual refresh, so it needs none of them. |

R1.8 (verification with the owner's account and docs) follows S4. Automatic chart updates for RISEx are a later task after that.

## Open questions to verify with a real account (R1.8)

- What the 232 same-side fills with extra realized PnL are: flips (`position_side` would then be the new side) or funding settled on trade. Compare them with `/v1/portfolio/realized-pnl` events and the position size before and after.
- The `realized_pnl` of a closing fill: net of its own fee only, or also of funding? (Opening fills equal `-fee`; the rate and `funding` fields in `realized-pnl` events can be compared.)
- Meaning and units of `maintenance_margin_factor`, `unsettled_usdc` and `margin_health`.
- TP/SL order statuses and how a triggered TP/SL appears in `/v1/orders`.
- Whether `orders/open` and `orders` can disagree briefly after a fill (open orders come from on-chain state, history from an indexer).
- Isolated margin positions: the margin shown in the capital context and the liquidation estimate.
- Whether the firewall's user-agent filtering applies to datacenter IPs or only to default client strings.
- The `fee_bps` order field (values 150 and 300 seen) against the 1–3 bps actually charged per fill.

## Implementation state (slice S4, October 5, 2026)

RISEx is available as a read-only venue (`Vessel.Infrastructure/Venues/Risex/RisexReader.cs`, descriptor `risex`). It was verified end to end against the live API with a public address: account creation, refresh (signed positions, equity, 2,000 fills), the catalogue, candles for crypto, stocks and DOGE's small tick across 1h, 1d and 5m, market context and order reads. The browser flow was also checked: the account dialog, account detail, the picker's category tags, and a chart without live updates.

Tasks R1.1 to R1.7 are done, with these differences from the plan:

- **Shared adapter kit:** `Venues/Common/StrictJson` and `BoundedJsonHttp` were extracted from the Hyperliquid adapter. Hyperliquid now uses them with its own messages, and its tests pass unchanged.
- **HTTP clients** are registered under RISEx-specific names. A typed client's default name is its interface, so sharing it would have mixed RISEx's base address into Hyperliquid's client. A test pins both.
- **No request budget class.** A refresh makes four requests (catalogue, portfolio and up to two fill pages), well under 500 per 10 s. The public catalogue is cached for one minute for instrument/chart reads; account and order imports force a fresh catalogue.
- **Snapshot:** account value is `total_account_value`, margin used is `total_initial_margin`. The existing summary `free_collateral` is retained as `AvailableMarginUsd` following the October 12 correction below. RISEx reports no account-level withdrawable amount, so `WithdrawableUsd` stays unknown. Position sizes come signed from portfolio details.
- **Fills:** the direction wording ("Open Long", "Close Short") is derived from side and position side, because RISEx sends none. The same side with PnL beyond the fee is `unknown`, as the effect rule above says. Closed PnL is `net-of-fee`, and fees are `reported` in USDC.
- **Orders** come from `/v1/orders` (the latest 1,000 records, open ones included) and `/v1/orders/tpsl`. A TP/SL is a trigger order (`Stop Market` or `Take Profit Market`, whole-position at 10,000 bps). When one triggers, its fills belong to a new regular order, which links as an unplanned exit or by the owner; this is still to verify.
- **Candles:** RISEx opens each candle at the previous close, so the open can lie a tick outside that candle's range (26 of 501 hourly BTC candles). Opens are kept as reported, and only the close is checked against the range. Rows labelled with another interval are rejected.
- **Market context:** `index_price` fills the oracle field. There is no previous-day price, mid price or premium, so those are null and the 24-hour change shows "—".
- **Trade link:** `https://www.rise.trade/trade/{instrument}`, the site's own market path.
- **Chart:** no live stream (`Stream` capability false). The live toggle is hidden and the chart refreshes on request, as the owner decided. Automatic updates from the public `trades` channel remain a later task.
- **Tests:** `RisexAdapterTests` (fixtures with a synthetic address) and the opt-in `RisexLiveTests` (set `Vessel_TEST_RISEX_ADDRESS`).

**October 19, 2026 (Phase 0 item 0.4c, review defect D-09).** The order reader never read `/v1/orders/open`, so every monitor check of a RISEx account reported an incomplete book: each run ended `degraded`, and a cancelled or missing stop could never be flagged. The owner chose to label this rather than build the read now. The RISEx descriptor states `CurrentOrderBook: false` ([venue registry](venue-registry.md#current-order-book-october-19-2026)): protection coverage is **unsupported: current book not read**, the order-book checks (missing, short, cancelled and excess stops, orphaned exits and entries, level drift) do not run and the UI names them, and the history-only read no longer degrades a run by itself. The planned `/v1/orders/open` read in R1.5 waits for its own slice, after its semantics and paging are verified against a real account (see the open question above on whether `orders/open` and `orders` can disagree).

Still open (R1.8, with the owner's own account): the meaning of same-side fills with extra PnL, TP/SL trigger linking, the units of `maintenance_margin_factor` and `unsettled_usdc`, and isolated-margin positions.

### October 16, 2026 import catalogue correction

Account and order imports previously reused the public catalogue for up to one
minute. A newly listed market could therefore be absent from the cached map,
silently excluding its positions, executions, regular orders and TP/SL orders.
Both import paths now refresh the catalogue before mapping facts, matching
Hyperliquid and Lighter. A failed refresh fails the read instead of falling back
to stale metadata. Instrument/chart cache TTL and single-flight behavior remain
unchanged. Synthetic regressions reproduce the loss with a warm catalogue and
verify both import paths; this is not evidence of a missing live account trade.
See the [cross-venue field audit](venue-import-field-audit.md) for fee/PnL handling.
Missing/null/empty execution PnL now fails with an explicit safe unknown-result
error, rather than the generic invalid-response error; its net-of-fee replacement
is still unverified and is never assumed to be zero.
No migration or production refresh is involved.

## October 12, 2026 free collateral follow-up

The existing `/v1/portfolio/details` response includes summary `free_collateral`; the recorded adapter fixture already contains it. The [official portfolio schema](https://developer.rise.trade/reference/accountservice_getportfoliodetails.md) defines it as "Free collateral available for new positions". It is not account withdrawal capacity. The position-level `free_isolated_usdc_balance` is separately documented as withdrawable for isolated positions and is not added to summary availability.

The adapter now stores summary `free_collateral` as nullable exact `AvailableMarginUsd`, exposed as snapshot `availableMarginUsd`. Missing, null and empty string remain unknown; explicit zero and signed decimals are retained. Invalid or unrepresentable decimals reject the observation. `WithdrawableUsd` remains null. No equity-minus-initial-margin calculation is substituted, and collateral, unsettled funds and position-level isolated balances are never added to free collateral.

Overview and portfolios select this reported amount once per enabled RISEx account with known-account coverage. Account rows and detail show reported margin availability, not withdrawable funds. Migration `20261012192227_AvailableMargin` leaves older snapshots unknown until explicit refresh. Fixture and disposable PostgreSQL tests cover parsing, exact persistence, unknown/zero replacement, owner isolation and retained observations after failed refresh. No live owner venue read or owner database migration was performed.

## October 21, 2026: robustness (Phase 2, item 14a)

See `phase2-risk-coverage-contract.md`, item 14a.
- **A trade without reported PnL (D-22, probe V5)** still fails the read, as
  before. Setting it aside waits for item 7's durable omission markers (Sol
  round 1 on 14a). Only a counted total of omissions reaches the import, and
  the shared `MissingExecutions` rule checks only orders with status filled.
  A set-aside trade of a partly filled order that is still open or was
  cancelled would leave the order looking complete, so its Play could close
  and be scored. Item 7's markers keep the trade's order, and a linked order
  with one has known missing executions whatever its status.
- **Counted omissions.** Trades on unrecognized markets were already counted
  in `OmittedExecutions`; the history notice now ends with one sentence for
  them, for example "Omitted executions: 1 on an unrecognized market."
- **A fixed end cut (F-13, probe V9).** Every `/v1/trade-history` page carries
  the same `end_time`: the snapshot's observation time in nanoseconds,
  truncated to whole milliseconds. A trade arriving between page requests
  therefore cannot shift page 2 and push a row out of the bounded read.
  - A row after the cut, if the venue returns one anyway, is skipped. It is
    neither imported nor counted as omitted.
  - That trade is read by the next refresh, whose own cut is later and whose
    window overlaps this one.
  - Fills are bounded by the same instant as the snapshot's positions. A
    trade whose venue time runs slightly ahead of Vessel's clock waits for
    the next refresh.
