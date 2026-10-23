# Pacifica read-only integration

Verified **October 23, 2026**, independently of the Extended adapter. Implementation
base: `315144f011ae86c73db6080ed3debd7201f714a1`; origin is
`git@github.com:sloppitty/vessel.git`. Independent adversarial review is **pending**.
No deployment, owner's database migration, publication, signing or trading is authorized.

## Primary-source verification

Web search returned no result body. Bounded HTTPS GETs (15–25 second timeouts,
size limits) retrieved the current official documentation, public metadata,
official Python SDK and official MCP source. Initial documentation requests
returned 403; the documentation root, REST index and `llms-full.txt` subsequently
returned 200. Research was local under the explicit no-delegation instruction.
No live wallet/account/history endpoint was queried; all account tests are synthetic.

Primary sources checked:

1. [Official API overview](https://docs.pacifica.fi/api-documentation/api),
   [REST overview](https://docs.pacifica.fi/api-documentation/api/rest-api) and
   [full documentation](https://docs.pacifica.fi/llms-full.txt).
   Production REST origin is `https://api.pacifica.fi`.
2. [Account info](https://docs.pacifica.fi/api-documentation/api/rest-api/account/get-account-info),
   [positions](https://docs.pacifica.fi/api-documentation/api/rest-api/account/get-positions),
   [settings](https://docs.pacifica.fi/api-documentation/api/rest-api/account/get-account-settings).
3. [Account trade history](https://docs.pacifica.fi/api-documentation/api/rest-api/account/get-trade-history),
   [account trade subscription schema/example](https://docs.pacifica.fi/api-documentation/api/websocket/subscriptions/account-trades).
   The subscription is evidence for financial semantics, **not an implemented stream**.
4. [Open orders](https://docs.pacifica.fi/api-documentation/api/rest-api/orders/get-open-orders),
   [summarized order history](https://docs.pacifica.fi/api-documentation/api/rest-api/orders/get-order-history),
   [per-order event history](https://docs.pacifica.fi/api-documentation/api/rest-api/orders/get-order-history-by-id),
   [last ID semantics](https://docs.pacifica.fi/api-documentation/api/last-id).
5. [Market info](https://docs.pacifica.fi/api-documentation/api/rest-api/markets/get-market-info),
   [case-sensitive symbols](https://docs.pacifica.fi/api-documentation/api/market-symbols),
   [ticks/lots](https://docs.pacifica.fi/api-documentation/api/tick-and-lot-size),
   [market specifications](https://docs.pacifica.fi/trading-on-pacifica/contract-specifications/market-specifications),
   [public live catalogue](https://api.pacifica.fi/api/v1/info).
   The response included both perpetuals and `SOL-USDC` spot: filtering is mandatory.
6. [Trade candles](https://docs.pacifica.fi/api-documentation/api/rest-api/markets/get-candle-data).
7. [Actual funding history](https://docs.pacifica.fi/api-documentation/api/rest-api/account/get-funding-history)
   versus [funding rates](https://docs.pacifica.fi/trading-on-pacifica/funding-rates).
8. [Trading fees](https://docs.pacifica.fi/trading-on-pacifica/trading-fees),
   [USDC deposits/withdrawals](https://docs.pacifica.fi/trading-on-pacifica/deposits-and-withdrawals),
   [unified margin](https://docs.pacifica.fi/trading-on-pacifica/unified-margin).
9. [Official Python SDK](https://github.com/pacifica-fi/python-sdk/tree/126ad9195369a2d1eaaa15cdecd577ca62c0f9c9)
   (revision verified through GitHub API). It contains signed trading/account-management
   examples; importing it would add signing/key handling, not solve read validation.
10. [Official MCP account reads](https://github.com/pacifica-fi/pacifica-mcp/blob/fbbea19681b78ebe791fdaed3d1c9389e004736f/src/tools/account.ts),
    [order reads](https://github.com/pacifica-fi/pacifica-mcp/blob/fbbea19681b78ebe791fdaed3d1c9389e004736f/src/tools/orders.ts),
    [market reads](https://github.com/pacifica-fi/pacifica-mcp/blob/fbbea19681b78ebe791fdaed3d1c9389e004736f/src/tools/markets.ts).
    The read tools use unsigned GETs with `account`; the neighboring write tools
    explicitly sign. Vessel does not use the MCP client's signing/key setup.
11. Official production frontend, fetched without a wallet:
    [realized-PnL calculation bundle](https://app.pacifica.fi/_next/static/chunks/6532-1bec39d1039cd206.js),
    [English financial labels bundle](https://app.pacifica.fi/_next/static/chunks/7152-78a8384e4364d621.js).
    SHA-256 respectively
    `8f995f4aab7536299aa62de73b341ef970c2e03616653545d701dce49108e3df`
    and `524beeaeea9ffb70b5d2ad832b04f1af78e05984c456dd67f4293c57bbf63023`.
    These are deployment artifacts, not stable API contracts; recheck before
    expanding the supported financial surface.
12. [Solana account addresses](https://solana.com/docs/core/accounts).
    Validation is decoded 32-byte public identity, not EVM hex, a seed/private key,
    signature verification, or an on-curve requirement (program-derived addresses exist).

## Shipped capability boundary

`pacifica`, read-only, source `solana-address`, nominal USDC, tick-size price rule.
Sync, instruments, reported orders and manual trade candles are enabled.
The current ordinary/stop order endpoint is read, but `OpenOrdersComplete` is
**always false**: TWAP/strategy parents and unsupported whole-position triggers
are not covered. Absence cannot establish cancellation or missing protection.
History-only open/partially-filled statuses keep their original venue wording
but normalize to `Other` (unknown current state), never to resting `Open`;
only the independent current endpoint establishes positive resting evidence.

No discovery, credentials, stablecoin-wallet ledger, market context, SSE/WebSocket,
paper execution, arbitrary history/backfill or trading capability is advertised.
An account may be created by its concrete main/subaccount public key; there is no
discovery or inferred relationship to a master wallet.

### Requests and local bounds

Every operation uses the existing strict JSON/HTTP helper: GET-only, one bounded
20-second operation deadline, 4 MiB per response, duplicate-property rejection,
safe errors and Retry-After handling. Clients remove loggers and disable redirects.
`Vessel:Venues:Pacifica:BaseUrl` defaults to `https://api.pacifica.fi/`; existing
startup host validation requires an HTTPS origin without user-info/query/path.

| Endpoint | Read contract / bounds |
| --- | --- |
| `GET /api/v1/info` | Full public catalogue, local 10,000-row ceiling, one-minute shared public metadata cache/single-flight; refresh/import forces fresh metadata. Only `instrument_type=perpetual` is selectable. |
| `GET /api/v1/account?account=…` | Reported `account_equity`, `total_margin_used`, optional `available_to_spend` and `available_to_withdraw`; validate `updated_at`. No browser request goes directly to Pacifica. |
| `GET /api/v1/positions?account=…` | Current unpaginated positions, local 10,000-row ceiling. Used only to establish an empty position set; nonempty positions are explicitly omitted (below). |
| `GET /api/v1/trades/history?account=…&limit=100[&cursor=…]` | At most 10 pages / 1,000 **raw** rows per refresh, all symbols, opaque escaped `next_cursor`, explicit `has_more`. No undocumented time ordering/maximum request limit assumed. |
| `GET /api/v1/orders/history?account=…&limit=100[&cursor=…]` | Same 10-page / 1,000 raw-row bound, summarized historical statuses, not a current book. No event-history expansion. |
| `GET /api/v1/orders?account=…` | Independent current ordinary/stop read after history, local 10,000-row ceiling. Current updates replace older history only with compatible identity and chronology. |
| `GET /api/v1/kline?symbol=…&interval=…&start_time=…&end_time=…&limit=4000` | Documented 4,000-candle maximum. Advertise native `1m,3m,5m,15m,30m,1h,2h,4h,8h,12h,1d,1w`. `1M` deliberately omitted: shared fixed-duration month assumptions do not prove a calendar month. No interval approximation. |

Cursor cycles, empty pages with `has_more`, oversized arrays and conflicting
duplicate history IDs fail safely. Cursor text is opaque, escaped and bounded;
it is never interpreted as an offset/time. Exact duplicates count once. Reaching
the local page ceiling with unread pages sets truncation even if every row was
excluded. The raw oldest timestamp precedes filtering; an invalid excluded
timestamp makes that boundary unknown. A terminal cursor never asserts complete
lifetime history. Shared refresh/check admission, ownership, account locks and
continuity recording remain in place; Refresh cannot clear a known history gap.

### Identity, instruments and financial facts

- `sourceId` is exact, case-sensitive base58 decoding to **32 bytes**, including
  leading-zero `1`s. Frontend `source-identity.ts` and backend `AccountSource`
  decode with existing BigInt/BigInteger facilities; no Solana package is added.
  `CreateDialogs` selects the rule from the descriptor, sends only `sourceId`,
  and does not lowercase it. `WorkspaceService` validates that source and rejects
  the legacy `Address` input. `Account.Configure` stores it unchanged with
  `Address=null`. The adapter validates again before issuing any HTTP request.
  Account display uses the retained source; address grouping uses a decoded
  Solana source without case folding. EVM paths remain lowercased.
- `PacificaSourceIdentity` installs the same decoded-byte check in PostgreSQL,
  keeps the existing owner/venue/source unique index, and pins `SourceId` to
  case-sensitive `C` collation. The legacy EVM/account-index branch is unchanged.
  It is an explicit migration, **never startup migration**. Rollback refuses
  retained Pacifica accounts rather than silently discarding identity.
- Native symbols and integer IDs remain exact strings, with no JavaScript
  numeric round trip. `history_id`, not `order_id`, identifies an execution.
  Native symbol is retained in `VenueContractId`. The verified `kBONK`/`kPEPE`
  thousand-unit contracts use Vessel's `1000BONK`/`1000PEPE` convention without
  rescaling prices/quantities. Native case is retained in requests/trade links.
  `tick_size`, decimal `lot_size` precision and `max_leverage` come from metadata;
  an unrepresentable non-power-of-ten lot is refused, not rounded to precision.
  No arbitrary lowercase-k conversion or inferred maintenance-margin rate.
  Other lowercase-k instruments (currently `kSHIB` in the live catalogue) are
  omitted until their Pacifica denomination is verified, not borrowed from a
  different venue's alias map.
- Ordinary trade side explicitly maps `open_long`/`close_short` to buy and
  `open_short`/`close_long` to sell; effect comes directly from open/close wording.
  A flip represented as distinct close/open history rows remains distinct;
  a new unsplit flip code is omitted, never guessed or split from prices.
  Liquidation, backstop liquidation, settlement, correction and unknown causes
  are omitted/count toward missing execution coverage. They do not become
  ordinary automatically linkable orders.
- A fill needs reported exact `fee` **and** `pnl`, positive `price`/`amount`,
  ordinary maker/taker execution, normal cause and nonzero native order ID.
  Missing/null/empty PnL or fee is omitted, not zero; unverified negative-fee
  rebates and spot fees are omitted. Fee token is USDC (USDC-margined linear
  contracts; nominal USD, no FX/depeg conversion). Transaction hash is unavailable
  and remains empty; leverage is unknown.
- **Net-of-trading-fee PnL evidence:** the official account-trades example reports
  a close-long execution of `0.00036`, exit `89477`, entry `89505`, fee `0.012885`,
  PnL `-0.022965`. The reported PnL is exactly the price result **minus fee**.
  The first-party frontend's realized-PnL implementation adds trade `pnl` values
  directly, without subtracting `fee`, and adds position funding separately;
  its matching tooltip identifies the displayed result as including fees/funding.
  These independent first-party artifacts support `PnlNetOfFee`; Vessel stores
  the **reported** PnL, never the reconstructed price result. Funding inclusion
  in execution PnL still remains `Unknown`; no combined funding result is enabled.
- A repeated ID with conflicting facts inside a read is rejected. Already
  retained fills follow the existing immutable/no-overwrite importer: a later
  upstream correction is **not applied**. There is no venue correction/reversal
  workflow; this is a limitation, not a last-write-wins guarantee.

## Explicit upstream/model blockers

1. **Open position projection:** documented `/positions` has size, entry price,
   isolated margin/funding and side, but no per-position unrealized PnL or cross
   margin requirement. Vessel's current position projection requires both money
   fields, so nonempty rows are not manufactured with zero, calculated from mark,
   or used to infer flatness. `PositionsComplete=false` and the persisted notice
   state the omission. Metadata maximum leverage and omitted default settings
   are never treated as reported historical leverage. No new leverage
   observations are created for these unknown positions.
2. **Spot/unified/lending equity:** account equity now includes raw spot market
   value. Any nonzero spot balance/value/collateral or accrued loan interest
   rejects the refresh before persistence with an explicit unsupported-account
   error. No non-stable valuation, lending or second wallet total is silently added.
3. **Funding settlement sign:** `/funding/history` is real payment history with
   `history_id`, symbol, side, payout, rate and milliseconds, unlike public
   historical rates. However, the payout field says “Funding paid” without
   establishing the signed received-positive convention; the funding guide's
   short-positive-rate example contradicts itself about the balance direction.
   No `IVenueFundingReader` is registered until sign/identity/coverage semantics
   are proven. Do not derive settlements from a rate or force a combined total.
4. **Current order coverage:** `/orders` is not TWAP/strategy parent coverage.
   Zero-size whole-position triggers are omitted, never assigned a guessed size.
   Partial current coverage is explicit even with zero reported orders.

## Verification and operation

`Fixtures/Pacifica/read-contract.json` contains a small recorded **public metadata**
subset and entirely synthetic account/execution/order/candle fixtures, with no
owner account reads. Tests cover exact source/ID preservation, decoded validation,
database uniqueness and owner scopes, legacy migration compatibility, strict
omissions, fee/PnL absence, liquidation/flip/correction cases, cursor escaping,
cycles, truncation before filtering, duplicates, partial positions/orders,
manual candles, safe HTTP errors, rate limits and the real locked refresh path.

Focused backend verification passes **97 tests** (Pacifica, venue registry,
existing Lighter-instance regressions and the exact API metadata contract)
with the isolated parent's PostgreSQL fixture.
Full frontend verification passes **4,061 tests across 168 files**; focused frontend
verification passes **111 tests**. Typecheck, lint and production build pass.
The Release solution build passes with zero warnings/errors. These cover the source validator, capability-driven account creation,
case-sensitive grouping and bundled logo allowlist. Broader checks and independent
adversarial review are tracked in the PR; this document does not claim sign-off.
The standalone full backend run was stopped at the parent's request to avoid
duplicating its combined full-suite run; no full backend pass is claimed here.
No existing database was migrated, no owner services were used and no CI
workflow was changed. The bundled venue mark is the official
[cyan SVG](https://app.pacifica.fi/imgs/cyan-logo.svg), fetched October 23, 2026;
it is served same-origin, not requested from the venue at runtime.
