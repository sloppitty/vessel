# Lighter integration: research, decisions, plan and tasks

Research checked October 5, 2026 against the official docs ([apidocs.lighter.xyz](https://apidocs.lighter.xyz/llms.txt), OpenAPI `zklighter-perps@1.0.240`) and live unauthenticated reads of public mainnet endpoints. Owner decisions were recorded the same day. On October 7 the owner authorized implementation after RISEx, including secure credential entry and main/subaccount import management. The [delivery contract](lighter-delivery-contract.md) bounds that implementation; the phase table below remains the original investigation plan. Secure token storage is specified separately in [venue-credentials.md](venue-credentials.md).

Lighter is "planned" in `SystemMetadata` and "next after Hyperliquid" in the MVP alignment. A third venue, [RISEx](risex-integration.md), was investigated the same day and shares the L0 phase below; its findings changed L0.2, L0.3, L0.5, L0.6 and L0.7. Perpetuals-first still applies: Lighter now also lists spot markets (`market_type: "spot"`, e.g. `rhSPY/USDC`), which are excluded.

For the implemented October 7 behavior, verification and operational limits, read [Lighter implementation](lighter-state.md). It supersedes older endpoint assumptions below, including the interval list, fee-free assumptions and whether a read needs authentication.

## Owner decisions (October 5, 2026)

1. **Credential:** store a Lighter **read-only** token (`ro:…`) per account, securely. Approach: application-level AES-GCM with a master key outside the database ([venue-credentials.md](venue-credentials.md)). Trading-capable tokens are never accepted.
2. **Account granularity:** one Vessel account per Lighter **account index**, since a position belongs to one concrete account. Sub-accounts are separate Vessel accounts. An L1 address is only a discovery aid.
3. **Instruments:** Vessel normalizes to **its own canonical asset**, and each venue module maps its contracts to and from it. Plays, drawings, matching and sizing work on the canonical instrument. Facts keep the venue's original identifier for traceability. Details in [Canonical instruments](#canonical-instruments).
4. **Fees:** the owner trades on a Standard Lighter account, which is fee-free. Lighter fills record a fee of `0` USDC with a visible "Standard account: fee-free" basis. A defensive check stops this from silently hiding a real fee (task L1.4).
5. **Delivery:** plan first (this document), then implement in the order below.

## What Lighter exposes

Base URL `https://mainnet.zklighter.elliot.ai/api/v1/`, WebSocket `wss://mainnet.zklighter.elliot.ai/stream`. REST is `GET` with query parameters, unlike Hyperliquid's single `POST /info`.

### Identity

- A Lighter account is an **account index** (int64). One L1 (EVM) address owns a master account and optional sub-accounts: `accountsByL1Address?l1_address=0x…` lists them (case-insensitive lookup; the response keeps checksummed case).
- `account?by=index&value=N` is public.

### Public, no credential

| Need | Endpoint | Notes |
| --- | --- | --- |
| Catalogue | `orderBookDetails` (all markets) / `orderBooks` | `symbol`, `market_id`, `market_type`, `status`, `size_decimals`, `price_decimals`, `min_initial_margin_fraction` (bps: 200 → 50× max), `maintenance_margin_fraction`, fees, `is_frozen` |
| Account snapshot | `account?by=index` | `collateral`, `total_asset_value`, `available_balance`, `cross_initial_margin_requirement`, positions (`sign` × `position`, `avg_entry_price`, `unrealized_pnl`, `realized_pnl`, `initial_margin_fraction` → leverage = 100 / IMF%, `liquidation_price`, `margin_mode`, `allocated_margin`), `assets` (USDC `balance`, `locked_balance`, `margin_balance`) |
| Fills | `trades?account_index=N&sort_by=timestamp&limit≤100&cursor=` | Answered without auth on October 5, but the docs call history endpoints auth-gated. Send the token when one exists (it also moves rate limiting from IP to L1 address). |
| Candles | `candles?market_id&resolution&start_timestamp&end_timestamp&count_back` | Resolutions `1m 5m 15m 30m 1h 4h 12h 1d 1w` only. Prices and volumes are **JSON numbers**, and there is no trade count. |
| Market context | `orderBookDetails` (mark, index, last price, daily volume, OI); WS `market_stats/{market_id}` adds `current_funding_rate` and `premium` (percent) | `funding-rates` returns **other exchanges'** rates (binance, …). Never use it. |

### Requires the read-only token

`accountActiveOrders`, `accountInactiveOrders` (limit ≤ 100, cursor), `pnl` for main accounts, `positionFunding`, and the private WS channels (`account_all_orders`, `account_tx`, …). Read-only tokens look like `ro:{account_index}:{single|all}:{expiry_unix}:{hex}`, live from 1 day up to 10 years, and are created in the app (`app.lighter.xyz/read-only-tokens`). They cannot sign transactions or withdraw.

### Field semantics that differ from Hyperliquid

| Concern | Hyperliquid | Lighter |
| --- | --- | --- |
| Contract identity | coin name (`BTC`, `kPEPE`) | `market_id` int; `symbol` (`BTC`, `1000PEPE`) |
| Fill side | `side: "A"/"B"` | derived: `ask_account_id == me` → sell |
| Open/close direction | `dir: "Open Long"…` | derived from `{maker,taker}_position_size_before` and `*_position_sign_changed`, depending on our role (`is_maker_ask`) |
| Fill fee | amount + `feeToken` | `maker_fee` / `taker_fee` integer **rate**, absent when zero. Standard accounts are fee-free (decision 4). |
| Closed PnL | `closedPnl` per fill | `ask_account_pnl` / `bid_account_pnl` (optional; to verify) |
| Order ID on fill | `oid` | `ask_id_str` / `bid_id_str` = `order_index` |
| Order timestamps | ms | `Order.timestamp` is **seconds**; `Trade.timestamp` ms; `transaction_time` µs |
| Trigger orders | `isTrigger`, `triggerPx` | `type` ∈ `stop-loss[-limit]`, `take-profit[-limit]`; `trigger_price`, `trigger_status`; OCO via `to_cancel_order_id_0` |
| Order statuses | `open/filled/canceled/…Rejected` | `in-progress, pending, open, filled, canceled-*` (14 cancel reasons; no rejected family) |
| Price precision | 5 significant figures | fixed `price_decimals` per market (BTC: 1) |
| Funding | hourly fraction | `current_funding_rate` string; unit (percent vs fraction) to verify against `fundings.rate` |
| Liquidation | estimated in the UI (`sizing.ts`) | per-market `maintenance_margin_fraction` and per-position `liquidation_price` reported |
| Fill history bound | latest 2,000 | cursor-paged without a stated cap; `export` gives CSV for 12 months |

### Rate limits

Standard accounts allow **60 requests per rolling minute** per IP and per L1 address, unweighted. A refresh is about 4 + N requests: catalogue, account, trade pages, active and inactive orders. Lighter therefore needs a per-venue request budget, a catalogue cached for minutes and bounded paging. The first refresh reads ≤ 5 pages = 500 fills, labeled incomplete history like Hyperliquid's 2,000. WebSocket: at least one frame every 2 min (`{"type":"ping"}`), 500 subscriptions per connection, channels `candle/{market_id}/{res}` and `market_stats/{market_id}`, keyed by `market_id`.

## Canonical instruments

Venues name the same contract differently. On October 5, Hyperliquid listed `kPEPE`, `kBONK`, `kSHIB` and `kFLOKI`, while Lighter listed `1000PEPE`, `1000BONK`, `1000SHIB` and `1000FLOKI`. A plan's prices are in contract units, so the canonical identity must carry the multiplier as well as the asset.

- **Canonical instrument** (Domain `PerpetualInstrument`): `Asset` (upper-case base asset, e.g. `PEPE`), `Multiplier` (positive integer, default 1) and `Quote` (`USDC`). The key is `{Multiplier>1 ? Multiplier : ""}{Asset}`, for example `BTC` or `1000PEPE`. It is what Plays, drawings (`venueId:key`), matching, sizing and the API's `instrument` use. `VenueId` stays on the instrument, because a Play is on one account at one venue.
- **Venue mapping** lives in each module (`IInstrumentMap` per adapter, built from that venue's catalogue). Hyperliquid maps `kX` to (`X`, 1000) and other names to (`name`, 1). Lighter maps `1000X` to (`X`, 1000), other symbols to (`symbol`, 1), and resolves canonical ↔ `market_id`. Mapping must be one-to-one per venue: a catalogue where two contracts map to one key fails the read. A contract the map cannot classify is excluded and listed in the notice, never guessed.
- **Facts keep the venue truth.** Fills, orders and positions store the canonical key in `ContractId` and the venue's own identifier in a new `VenueContractId`, so a row can always be traced back.
- **Existing data:** a migration rewrites Hyperliquid `k…` contracts to canonical keys in plays, imported fills, venue orders, positions and drawing keys inside plan documents. Other names already equal their canonical key.
- **Manual instruments** stay free-text labels and are not normalized.

## Where the code is Hyperliquid-shaped today

**Backend**

1. **Single adapter injection.** `DependencyInjection` registers exactly one `IPerpetualVenueReader`, `ICandleReader`, `IMarketContextReader`, `IVenueOrderReader` and `IMarketStream`, all Hyperliquid. Services then check `reader.VenueId != account.VenueId` (`WorkspaceService`, `CandleService`, `MarketContextService`, `MarketStreamService`, `PlayExecutionService`).
2. **Hard-coded venue IDs in use cases.** `WorkspaceService`: creation accepts only `manual` or `hyperliquid` and checks a 42-hex address; sync answers "Only Hyperliquid accounts can be refreshed". `PlayExecutionService.UntrackedReason` says "Only Hyperliquid accounts are tracked".
3. **Venue encodings in the domain.** `ExecutionMatcher` compares sides as `"A"/"B"`, and `PlayExecutionService` detects closes with `Direction.StartsWith("Close")`. Both are Hyperliquid strings persisted in `imported_fills` and `venue_orders`.
4. **Price rule.** `ExecutionMatcher.Near` hard-codes 5 significant figures, as does `formatDraggedPrice` in `frontend/src/features/plays/levels.ts`.
5. **Source identity = EVM address.** `Account.Address` (max 42, lowercased, unique per owner/venue) is the only source key.
6. **No credential storage.**
7. **Market-data contracts assume Hyperliquid fields.** `MarketDataGuard.IntervalMs` is Hyperliquid's interval set. `VenueCandle.Trades` is required. `VenueMarketContext` requires `PreviousDayPrice`, `OraclePrice` and an hourly-fraction `FundingRate`. `CandleService` and `MarketContextService` notices name Hyperliquid's limits. `SizingService` hard-codes `USDC` fees, which is fine for Lighter.
8. **Reusable code trapped in the adapter.** The strict JSON helpers (`Property`, `Text`, the exact-decimal `ParseDecimal`, `Identity`, `RejectDuplicateProperties`, the raw-number `DecimalText`), the bounded/deadline HTTP read and the relay in `HyperliquidMarketStream` are generic. The relay covers ref-counting, coalescing queues, jittered reconnects and stale/dead detection; only frames, ping, keys and the parser are venue-specific.

**Frontend:** about 20 `=== 'hyperliquid'` checks in `CreateDialogs`, `ApplicationShell`, `AccountDetail`, `WorkspacePanels`, `PlayWorkspace`, `instruments.ts` (`primaryQuote`, catalogue scope), `api/plays.ts` (trade URL), `PlayChart`, `levels.ts` (price rule) and `sizing.ts` (liquidation estimate).

## How Lighter fits

Vessel's dependency direction stays as it is: Application defines ports and a **venue descriptor**; each venue module in Infrastructure implements the ports, owns its instrument map and registers itself. Use cases look up adapters by `account.VenueId` and never mention a venue by name. The frontend reads capabilities from `/api/system` instead of comparing venue IDs.

```
Application                       Infrastructure
  IVenueRegistry ───────────────▶  VenueRegistry (all registered modules)
  VenueDescriptor (capabilities)    Venues/Common   StrictJson, BoundedJsonHttp, SharedMarketStream
  IPerpetualVenueReader             Venues/Hyperliquid  reader, orders, candles, context, stream protocol, instrument map
  IVenueOrderReader                 Venues/Lighter      same set + request budget
  ICandleReader / IMarketContext    Credentials     AesGcmCredentialVault
  IMarketStream
  ICredentialVault
```

`VenueDescriptor` contains:
- `Id`, `Name`, `Status`;
- `Source` (`evm-address` for Hyperliquid and RISEx, `account-index` for Lighter) with its validator;
- `Credential` (`none` for Hyperliquid and RISEx, `optional-read-token` for Lighter);
- capabilities: `sync`, `orders` (may need the credential), `candles`, `context`, `stream`, `stablecoinWallet`;
- `Intervals`, `QuoteAsset`, `PriceRule` (`significant-figures:5` | `tick-size`), `TradeUrlTemplate` and notices (history, candle limit, fees).

Normalized execution facts:
- `Side` is `buy`/`sell`;
- `PositionEffect` is `open`/`close`/`flip`/`unknown`;
- the venue's own `Direction` text is kept for display;
- `FeeBasis` is `reported` (Hyperliquid, RISEx) or `standard-account-free` (Lighter);
- `PnlBasis` is `gross` (Hyperliquid closed PnL) or `net-of-fee` (RISEx `realized_pnl` already includes the fee, so the sizing service must not subtract the fee again).

The matcher, lifecycle and sizing read only the normalized fields.

## Tasks

**Sequence update (October 5, 2026):** the owner chose to build RISEx first. L0 is therefore delivered in slices as RISEx needs them (see [risex-integration.md](risex-integration.md#revised-sequence-risex-first)): L0.3 and L0.9 (S1), L0.5 (S2), L0.6, L0.7 and L0.8 (S3) and L0.1 (start of S4). **L0.2 (stream relay), L0.4 (source identity) and the Hyperliquid `k…` data migration move to the start of the Lighter phase**, since RISEx does not need them. The task definitions below are unchanged.

Each task is one reviewable change with its own tests. Every task keeps `scripts/check.sh` green, keeps behaviour unchanged where marked *neutral*, and updates the relevant contract/state doc. Phases follow the earlier recommendation: **L0** is a Hyperliquid-only PR with no visible change, **L1** adds Lighter on public data, and **L2** adds the token and orders.

### L0: generalize (neutral; one PR)

| ID | Task | Scope | Done when | Depends |
| --- | --- | --- | --- | --- |
| L0.1 | **Shared adapter kit.** Move strict JSON/decimal helpers and the bounded HTTP read out of `HyperliquidPerpetualReader` into `Venues/Common` (`StrictJson`, `BoundedJsonHttp` with GET and POST); venue name parameterizes messages. | Infrastructure | Hyperliquid tests unchanged and passing; helpers have direct tests (duplicate keys, exact decimals, raw-number text). | — |
| L0.2 | **Generic stream relay.** Split `HyperliquidMarketStream` into `SharedMarketStream` + `IMarketStreamProtocol` (frames per key, ping frame/interval, declared capabilities `candles`/`context` so a venue without a candle channel is allowed (RISEx has none), parse → ack/candles/context/control) and `HyperliquidStreamProtocol`. | Infrastructure | `HyperliquidMarketStreamTests` pass against the relay unchanged; a fake-protocol test covers ref-counting and reconnect. | L0.1 |
| L0.3 | **Venue registry + descriptor.** `IVenueRegistry` and `VenueDescriptor` in Application; registration per module; services (`WorkspaceService`, `CandleService`, `MarketContextService`, `MarketStreamService`, `PlayExecutionService`) resolve by `account.VenueId`; remove `"hyperliquid"` literals from use cases; `SystemMetadata.Venues` built from the registry (Lighter still `planned`, so not creatable). | Application, Infrastructure, Api | `rg '"hyperliquid"' backend/src/Vessel.Application` finds nothing; `/api/system` includes capabilities; tests cover "no adapter for venue". | — |
| L0.4 | **Generic source identity.** `Account.Address` → `SourceId` (string ≤ 128, venue-normalized), with the unique index renamed; the descriptor validates it; API keeps accepting `address` for Hyperliquid and adds `sourceId`. | Domain, Persistence (migration), Application, API | Migration preserves existing rows; duplicate-source 409 still works. | L0.3 |
| L0.5 | **Normalized execution facts.** Add `Side` (`buy`/`sell`) and `PositionEffect` to fills and orders, plus `FeeBasis` and `PnlBasis` on fills (sizing reads `PnlBasis` so a net-of-fee PnL is not reduced by the fee twice); migrate `B`→`buy`, `A`→`sell` and derive the effect from Hyperliquid `dir`; matcher and execution service use only normalized fields; DTOs keep the venue text for display. | Domain, Persistence (migration), Application, Hyperliquid adapter | Matcher/lifecycle tests pass with normalized data; no `"A"`/`"B"`/`"Close"` literals outside the Hyperliquid module. | L0.3 |
| L0.6 | **Canonical instruments.** `PerpetualInstrument` gains asset/multiplier/quote and its key; `IInstrumentMap` per adapter, keyed by the venue's own contract identifier (Hyperliquid coin, Lighter and RISEx `market_id`), with the Hyperliquid `k`-prefix rule, the Lighter `1000X` rule, the RISEx `X/USDC` rule and retired markets (one-to-one is required among active markets only; RISEx has a deprecated `DOGE/USDC` beside the live one); facts store canonical `ContractId` + `VenueContractId`; migration rewrites `k…` rows and drawing keys; the catalogue DTO returns canonical key, display name and venue contract. | Domain, Persistence (migration), Application, Hyperliquid adapter, frontend `instruments.ts` | Existing BTC plays unchanged; a `kPEPE` fixture round-trips as `1000PEPE` through catalogue, play, order link and chart. | L0.3 |
| L0.7 | **Per-instrument price rule and margin data.** `VenueInstrument` gains an optional `PriceStep` tick (Lighter `price_decimals` → `10^-d`, RISEx `step_price` directly; Hyperliquid keeps its five-significant-figure rule), a `Category` label (RISEx lists crypto, stock, commodity and index perpetuals) and `MaintenanceMarginFraction`; `ExecutionMatcher.Near` uses the descriptor's `PriceRule`; the frontend's dragged-price formatter and liquidation estimate take the instrument's rule and reported maintenance fraction when present. | Application, frontend `levels.ts`, `sizing.ts` | Hyperliquid tolerance tests unchanged; new fixed-decimals tests. | L0.3 |
| L0.8 | **Loosen market-data contracts.** `VenueCandle.Trades` nullable; `VenueMarketContext.OraclePrice` → `IndexPrice`, and `PreviousDayPrice` and `Premium` nullable; funding carries `interval` and `unit`; `MarketDataGuard` intervals and the service notices come from the descriptor; the frontend interval picker shows only supported intervals. | Application, frontend chart | Hyperliquid chart behaviour and tests unchanged. | L0.3 |
| L0.9 | **Frontend capabilities.** Replace every `venueId === 'hyperliquid'` with descriptor capabilities (`sync`, `stablecoinWallet`, `credential`, `tradeUrlTemplate`, `quoteAsset`); the account dialog renders the source field from the descriptor; copy becomes venue-neutral where it named Hyperliquid generically. | frontend | `rg "'hyperliquid'" frontend/src --glob '!*.test.*'` finds only fixtures/format names; existing tests pass. | L0.3–L0.8 |

### L1: Lighter on public data (one PR)

| ID | Task | Scope | Done when | Depends |
| --- | --- | --- | --- | --- |
| L1.1 | **Lighter module skeleton.** `Venues/Lighter`: descriptor (`account-index` source, `optional-read-token`, intervals, `instrument-decimals`, `app.lighter.xyz/trade/{contract}`), typed HttpClient on the base URL with `RemoveAllLoggers()`, and a **request budget** (token bucket, ≤ 50/min per process, 429/405 → `VenueReadException` with cooldown). | Infrastructure | Budget unit-tested with `TimeProvider`; module registered but `Status` stays `planned` until L1.7. | L0 |
| L1.2 | **Catalogue + instrument map.** `orderBookDetails` → active perps only (spot and frozen excluded, frozen kept for history); `1000X` multiplier rule; canonical ↔ `market_id`; cached about 5 min; leverage = ⌊10000 / `min_initial_margin_fraction`⌋; size/price decimals; maintenance fraction. | Lighter module | Fixture tests from recorded responses (BTC, `1000PEPE`, a spot row, a duplicate key → error). | L1.1 |
| L1.3 | **Snapshot.** `account?by=index` → positions (sign × size, entry, uPnL, leverage = 100/IMF, margin), account value (`total_asset_value`), withdrawable/available, margin used; USDC asset → stablecoin wallet (`balance`, `locked_balance`) kept distinct from `collateral`/`margin_balance`, never summed (stablecoin-wallet rules). | Lighter module | Fixtures incl. zero positions, short position, unknown market skipped. | L1.2 |
| L1.4 | **Fills.** `trades` paged by cursor, ≤ 5 pages and ≤ 500 fills; role from `is_maker_ask` and account IDs; side, position effect and venue direction text; `order_index` as `OrderId`; `trade_id_str` as fill ID; types `liquidation`/`deleverage`/`market-settlement` preserved; fee `0` USDC with `FeeBasis = standard-account-free`. **Guard:** a non-zero `maker_fee`/`taker_fee` for our role fails that fill's fee as unknown and adds a sync notice, rather than recording `0`. Closed PnL from `*_account_pnl` only after verification; until then null with a notice. | Lighter module, Application (nullable closed PnL path if needed) | Fixtures for maker/taker × buy/sell × open/close/flip, liquidation, the fee guard; history notice explains bounds. | L1.2 |
| L1.5 | **Candles + market context.** `candles` with raw number text (reuse the raw-token path), interval mapping, window bounds; context from `orderBookDetails` (mark, index, OI, day volume; previous-day price null); funding from `market_stats` once its unit is verified, otherwise null with a notice. | Lighter module | Fixture tests; chart shows Lighter candles in a manual check. | L1.2, L0.8 |
| L1.6 | **Stream protocol.** `LighterStreamProtocol` over `SharedMarketStream`: `candle/{market_id}/{res}`, `market_stats/{market_id}`, `{"type":"ping"}` every ≤ 60 s, `subscribed/*` acks, `update/*` messages; canonical ↔ `market_id` via the cached catalogue. | Lighter module | Protocol parse tests from recorded frames; relay tests with fake transport. | L0.2, L1.2 |
| L1.7 | **Account creation and discovery.** `GET /api/venues/lighter/accounts?l1Address=` (owner-only proxy to `accountsByL1Address`, nothing stored) and creation with `sourceId` = index (decimal int64, unique per owner); dialog: enter address → pick index, or enter an index; Lighter status → `read-only`. | Application, API, frontend | Create/duplicate/invalid tests; e2e smoke adds a Lighter account from fixtures. | L1.1–L1.6, L0.9 |
| L1.8 | **Docs and verification.** Update `account-management-contract.md`, `stablecoin-wallet.md` (Lighter USDC scope), `chart-plan.md` (intervals), `core-workspace-state.md`; verify the open questions below on the owner's account with a real token; record results here. | docs | Open questions answered or explicitly deferred. | L1.7 |

### L2: read-only token and order linking (one PR)

| ID | Task | Scope | Done when | Depends |
| --- | --- | --- | --- | --- |
| L2.1 | **Credential vault.** `ICredentialVault` (Application), `AesGcmCredentialVault` (Infrastructure), `Vessel:Credentials` options with startup validation, dev runner/env docs, reseal command. | Application, Infrastructure, scripts, docs | Tests: round-trip, tampered tag, wrong associated data (other account), unknown key ID, rotation. | — (can run in parallel with L1) |
| L2.2 | **Credential persistence and API.** `account_credentials` table (migration), owner-scoped store under the account lock; `PUT`/`DELETE /api/accounts/{id}/credential`; `AccountDto.credential` metadata only; Lighter validation (`ro:` format, index/`all` ownership, future expiry, one verification read). | Domain, Persistence, Application, API, Lighter module | Tests: rejects canonical tokens, wrong index, expired, venue refusal; the token never appears in responses, problem details or logs (log-capture test). | L2.1, L1.7 |
| L2.3 | **Authenticated reads.** Lighter adapter receives the opened credential per call (method parameter, never stored on the singleton); `Authorization` header only, never the `auth=` query; fills use it when present; expiry and refusal map to safe messages and `credential.status`. | Lighter module, Application sync | Tests with fake handler asserting header presence and absence from URLs and exceptions. | L2.2 |
| L2.4 | **Orders.** `IVenueOrderReader` for Lighter: `accountActiveOrders` + `accountInactiveOrders` (cursor, ≤ 5 pages), seconds → UTC, status mapping (`open`/`in-progress`/`pending` → Open; `filled`; `canceled-*` → Canceled, keeping the venue word), trigger types → `TriggerPrice`, reduce-only, position-tied flag once verified. `PlayExecutionService` tracks Lighter when the descriptor's orders capability is satisfied (credential present and valid); otherwise the untracked reason says "Add a read-only token to track orders". | Lighter module, Application | Matcher and lifecycle tests run on a Lighter fixture: Planned → Open → Closed from linked fills. | L2.3, L0.5, L0.7 |
| L2.5 | **Frontend credential UX.** Manage-account dialog: paste read-only token (password field, never prefilled), link to `app.lighter.xyz/read-only-tokens`, show scope/expiry/last verified, warning ≤ 14 days, remove; Play tracking copy for missing token. | frontend | Component tests; browser smoke. | L2.2 |
| L2.6 | **Docs and state.** Update `play-lifecycle.md` (Lighter tracking), `venue-credentials.md` with what was built, `AGENTS.md` pointer, and the security notes in `development.md`. | docs | — | L2.4, L2.5 |

### Later (not planned yet)

- Live private channels (`account_all_orders`, `account_all_trades`) instead of polling, after the step 4 monitor design.
- Optional history import via Lighter `export` CSV (12 months of trades).
- Premium accounts with fees: switch `FeeBasis` to a derived fee once fee units are verified.

## Open questions to verify with the owner's account (L1.8)

- What `ask_account_pnl` / `bid_account_pnl` mean, and whether they equal realized PnL per fill.
- The `current_funding_rate` unit and interval compared with `fundings.rate`.
- Whether `accountActiveOrders` works without `market_id`.
- The maximum `count_back` for candles.
- How position-tied TP/SL orders and OCO pairs appear in inactive orders.
- That `trades` keeps answering account queries with the token, and whether Lighter starts requiring auth for it.

## November 17, 2026: unrecognized trade types (issue #118)

A well-formed trade whose `type` is not `trade`, `liquidation`, `deleverage` or
`market-settlement` no longer fails the account's import. Its trade ID, market,
account role, size and fee rates still validate, and its time must be valid.
It is left out with an `unrecognized-row` marker on its market and order, and
the one counted omission sentence adds "N of an unrecognized type", for example
"Omitted executions: 1 of an unrecognized type." A `type` that is not a string,
or a row without a usable identity or time, still fails the read. To fit the
extra count, `accounts.HistoryNotice` is now 1,200 characters (migration
`UnrecognizedRowOmissions`). See the row-level section of `execution-facts.md`.
