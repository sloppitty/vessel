# Extended: public-data partial integration

Verified **October 23, 2026**, against the current official API reference,
product documentation and official Python SDK, not third-party integrations.
Base: `315144f011ae86c73db6080ed3debd7201f714a1`. This is **not a delivered
account integration**. No account, owner credential, private/Stark key, signed
request or state-changing exchange request was used in this investigation.

The search/open web tool returned no result bodies. Bounded HTTPS GETs
(`curl --max-time 25`, explicit User-Agent for public API reads) retrieved the
primary sources below. The SDK `starknet` branch resolved to
`da9a79726d23fb605518348dd6f4fa7d3dbd83c9`; SDK citations are pinned to that
revision. Tests use synthetic responses or trimmed **public market data**
captured on this date, never owner data. SDK source is research, not a dependency.

## Security blocker: an API key is not verifiably read-only

The reference's
[Authentication](https://api.docs.extended.exchange/#authentication) section
calls API keys “Read-Only Access” and says Stark signatures authorize creating
and cancelling orders. **Do not rely on that summary.** Its detailed
[cancel-by-ID](https://api.docs.extended.exchange/#cancel-order-by-id),
[mass cancellation](https://api.docs.extended.exchange/#mass-cancel) and
[leverage](https://api.docs.extended.exchange/#update-leverage) sections describe
unsigned state changes. The official implementation makes the contradiction
concrete:

- [OrderManagementModule](https://github.com/x10xchange/python_sdk/blob/da9a79726d23fb605518348dd6f4fa7d3dbd83c9/x10/clients/rest/modules/order_management_module.py):
  `cancel_order` sends `DELETE /user/order/{id}` with only `api_key`;
  `cancel_order_by_external_id` does the same with `externalId`;
  `mass_cancel` sends `POST /user/order/massCancel`, including `cancelAll`,
  with only `api_key`. None requests a Stark account or creates a signature.
- [AccountModule](https://github.com/x10xchange/python_sdk/blob/da9a79726d23fb605518348dd6f4fa7d3dbd83c9/x10/clients/rest/modules/account_module.py):
  `update_leverage` sends `PATCH /user/leverage` with market/leverage and only
  `api_key`, without a Stark signature.
- [BaseModule](https://github.com/x10xchange/python_sdk/blob/da9a79726d23fb605518348dd6f4fa7d3dbd83c9/x10/clients/rest/modules/base_module.py)
  accepts an API key **without** a Stark account;
  [HTTP transport](https://github.com/x10xchange/python_sdk/blob/da9a79726d23fb605518348dd6f4fa7d3dbd83c9/x10/utils/http.py)
  builds `X-Api-Key`, User-Agent and content headers; it does not secretly sign
  these PATCH/DELETE/POST requests.
- The [API-key request model](https://github.com/x10xchange/python_sdk/blob/da9a79726d23fb605518348dd6f4fa7d3dbd83c9/x10/models/account.py)
  contains a description, not a restricted/read-only permission declaration.
- [Dead man's switch](https://api.docs.extended.exchange/#mass-auto-cancel-dead-man-39-s-switch):
  `POST /api/v1/user/deadmanswitch?countdownTime=...` schedules cancellation.
  It is another write endpoint, not a permitted read.

No documented read-only key permission, scope introspection or server-verifiable
read-only variant was found in these sources. This is not proof that one can
never exist; it is insufficient evidence to accept one now. We did **not**
experiment with cancellation or setting changes to resolve the contradiction.
Ordinary API keys must be treated as cancellation/settings-capable, not as
Vessel read-only credentials. A locally GET-only adapter does not reduce the
authority of a leaked key.

**Decision/remaining options:** wait for Extended to provide a documented,
verifiable restricted-key permission (with account binding and tests), or ask
the owner for a separate explicit credential-policy decision. This PR does
neither implicitly. It accepts/stores no Extended secret, registers no
credential verifier, adds no credential UI/setup URL, and leaves the encrypted
vault, HTTPS guards and existing read-only policy unchanged.

## Verified public surface implemented

Production origin:
<https://api.starknet.extended.exchange/>; API reference:
<https://api.docs.extended.exchange/>. Origins remain operator-configurable
under `Vessel:Venues:Extended:BaseUrl`, with existing HTTPS-origin validation.
Named HTTP client `extended` has no loggers, a 20-second timeout, a mandatory
non-secret User-Agent and redirects disabled. The shared bounded HTTP/strict
JSON helpers reject duplicate properties, inexact decimals, oversized payloads,
malformed envelopes and safely redact source/transport errors. No SDK, signer,
authentication handler, private route or dependency is added.

| Read | Verified semantics and implementation |
| --- | --- |
| [`GET /api/v1/info/markets`](https://api.docs.extended.exchange/#get-markets) | Unpaginated `{status:"OK",data:[...]}`. Mixed `SPOT`/`PERPETUAL`; explicitly filter perpetuals, `collateralAssetName:"USD"`, `active:true`, `status:"ACTIVE"`. The live response included delisted rows still marked active, so `active` alone is insufficient. Up to 2,000 raw rows accepted; unexpected pagination is rejected rather than represented as complete. |
| Instrument identity | `assetName` becomes the canonical asset key, with native `name` retained separately as `VenueContractId`; only verified `<asset>-USD` identities with uppercase/digit/underscore asset keys are supported. Distinct schedule suffixes are **not** stripped/merged. `k…` assets are omitted: a leading letter is not proof of a 1,000-unit multiplier. No guessed alias or price/quantity rescaling. The recorded fixture includes `kDOGS` to pin that omission. |
| Tick, size and leverage | `tradingConfig.minPriceChange` is the exact positive decimal price tick; `assetPrecision` is the reported quantity precision; whole-number positive `maxLeverage` (including decimal text such as `"50.00"`) is retained, never an account leverage observation. The shared contract does not convey minimum quantity/integer quantity steps or position-value tiers, so this is metadata, **not order validation**. Maintenance margin remains null. `USD` is retained literally, not silently renamed `USDC`. Optional reported non-Crypto category is retained. |
| [`GET /api/v1/info/candles/{market}/trades`](https://api.docs.extended.exchange/#get-candles-history) | Trade candles only, no mark/index replacement. Query `interval`, `limit`, `endTime` in UTC milliseconds; no `startTime` parameter exists. At most 500 requested/accepted rows in one call, then local lower-bound filtering. No history walk. Descending unique interval-aligned `T` timestamps become ascending opens, with close time `T + interval - 1`; exact `o/h/l/c/v` strings are preserved. Missing/invalid volume or OHLC, future/out-of-bound rows and duplicate timestamps are rejected. Trades count stays null. An empty/short window is not a guarantee of lifetime history. |

The official [CandleInterval model](https://github.com/x10xchange/python_sdk/blob/da9a79726d23fb605518348dd6f4fa7d3dbd83c9/x10/models/candle.py)
defines `PT1M`, `PT5M`, `PT15M`, `PT30M`, `PT1H`, `PT2H`, `PT4H`, `P1D`;
Vessel maps those to `1m/5m/15m/30m/1h/2h/4h/1d`. No approximated intervals.
The [market model](https://github.com/x10xchange/python_sdk/blob/da9a79726d23fb605518348dd6f4fa7d3dbd83c9/x10/models/market.py)
and [trading rules](https://docs.extended.exchange/extended-resources/trading/crypto-markets/trading-rules.md)
support the precision/tick distinction. The live catalogue had both spot and
perpetual rows; the public BTC trade-candle GET succeeded without an API key.
RFQ/orderbook and off-hours markets can coexist; this partial adapter promises
no current book, trading-hours status, market context or continuous candles.

### Reachability: deliberately not a usable Extended account

- `/api/system` and **Settings → Venue capabilities** expose `Extended` as a
  `candidate`, explicitly labelled **Public data only · account integration
  unavailable**. The generic wallet icon is used (no unverified brand asset).
- Only `Instruments` and `Candles` are true. `Sync`, `Orders`,
  `CurrentOrderBook`, `ReadOnlyCredential`, `AccountDiscovery`, `MarketContext`,
  `Stream`, `StablecoinWallet`, `ManualExecutions` and `PaperExecution` are false.
- `Source = none` means **no supported account source**, not that Extended lacks
  account IDs. Existing frontend `Venues.creatable()` excludes it. Existing
  account creation rejects its unsupported source (including source/address
  workarounds), and paper creation rejects it. It is not repurposed as Manual.
- Existing catalogue/candle HTTP routes are owner/account-scoped:
  `/api/accounts/{id}/instruments` and `/api/accounts/{id}/candles`. No ordinary
  user can currently create an Extended account to reach them or select it in
  Plays. Their public-data adapter paths work for synthetic seeded accounts in
  tests, **not an advertised production workflow**. No new global market-data
  endpoint, database seed or account-creation exception is introduced.
- No current/history orders, account snapshots, fills, leverage observations,
  actual funding payments or continuity claims are imported. The direct
  account-read port fails before any HTTP request. No real or paper lifecycle,
  financial reader, sizing, review, matching, background job or layout changes.

## Authenticated surface researched but NOT implemented

These facts describe a possible future implementation, not permission to enable
the endpoints or evidence that Vessel can import them today.

| Primary reference | Identity/fields/coverage and unresolved semantics |
| --- | --- |
| [Account details](https://api.docs.extended.exchange/#get-account-details), [sub-accounts](https://api.docs.extended.exchange/#get-sub-accounts), [account model](https://github.com/x10xchange/python_sdk/blob/da9a79726d23fb605518348dd6f4fa7d3dbd83c9/x10/models/account.py), [trading accounts](https://docs.extended.exchange/extended-resources/trading/trading-accounts-and-modes.md) | `GET /api/v1/user/account/info` returns the key's `accountId`, `l2Key`, `l2Vault`, status and description; `/user/accounts` lists subaccounts. Up to ten independently margined accounts per wallet, each with its own API/Stark keys. Future SourceId must be the exact exchange account ID, not blindly an EVM address, vault ID or wallet key. Verify key-bound `/account/info.accountId` equals the chosen account, preserving IDs as exact strings (the reference inconsistently calls them numbers/strings). Never convert through JavaScript numbers. |
| [Balance](https://api.docs.extended.exchange/#get-balance), [positions](https://api.docs.extended.exchange/#get-positions), [unified balances](https://docs.extended.exchange/extended-resources/trading/unified-margin-and-balances.md) | `/user/balance` reports `collateralName`, `balance`, `equity`, `availableForTrade`, `availableForWithdrawal`, `initialMargin`, unrealized/withdrawable PnL and spot/unified contributions. Its documented zero-balance 404 needs a typed rule, not generic zero substitution. `/user/positions` reports LONG/SHORT, absolute size, margin, leverage, open/mark price, unrealized/realized PnL and paid funding; it covers perpetuals, not spot holdings. No double counting unified collateral/spot; absent values stay unknown. |
| [Trades](https://api.docs.extended.exchange/#get-trades), [AccountTradeModel](https://github.com/x10xchange/python_sdk/blob/da9a79726d23fb605518348dd6f4fa7d3dbd83c9/x10/models/trade.py) | `/user/trades` mixes spot/perpetuals; native trade, account and order IDs plus BUY/SELL, `TRADE/LIQUIDATION/DELEVERAGE`, `fee`, `createdTime` in ms. Example/SDK use `price/qty`; reference table says `averagePrice/filledQty`. **Neither inspected REST example/table nor SDK provides per-fill realized PnL or an opening/closing effect.** Position PnL is not fill PnL. Do not invent zero PnL, infer it from size/price, or assign aggregate position history to executions. Missing fee is not zero; fee/rebate sign, currency identity, fee-inclusion basis and execution-funding basis require independent confirmation. No `VenueFill` mapping ships. |
| [Open orders](https://api.docs.extended.exchange/#get-open-orders), [history](https://api.docs.extended.exchange/#get-orders-history), [pagination](https://api.docs.extended.exchange/#pagination), [rate limits](https://api.docs.extended.exchange/#rate-limits) | `/user/orders` reports NEW/PARTIALLY_FILLED/UNTRIGGERED current rows; `/user/orders/history` reports FILLED/CANCELLED/REJECTED/EXPIRED. Mixed spot/perpetual; retain exact native ID versus user external ID. Terminal unfilled records retained only seven days; high-volume traders may not retain these at all. Cursor is the prior response's `pagination.cursor`, descending ID, with `limit`; at most 10,000 per response. Bounded history never proves lifetime continuity. Any future import must retain omitted/oldest-raw/truncated/overlap facts and preserve missing-execution integrity, atomic sync and owner/account locking. No current-book completeness claim is made now. |
| [Actual funding](https://api.docs.extended.exchange/#get-funding-payments), [funding mechanics](https://docs.extended.exchange/extended-resources/trading/funding-payments.md) | `/user/funding/history?startTime=...` is subaccount-scoped and paginated: native `id/accountId/positionId/market`, side, size/value, `fundingFee`, `fundingRate`, `paidTime` in ms. Public `/info/{market}/funding` rate history is **not** actual account settlement and must not fill this port. The inspected `fundingFee` description does not establish its signed cashflow convention; do not guess the sign from LONG/SHORT or the rate. Funding integration remains off, and execution-funding PnL basis remains Unknown. |

## Checks and remaining work

`ExtendedAdapterTests` pins strict envelopes/decimals, filtering, exact native
identity/ticks, precision, bounded candle requests, malformed/oversized/duplicate
responses, redacted errors/rate limits, cancellation, cache single-flight and
configured HTTPS hosts/registration. `ExtendedApiTests` pins the capability
contract, source/paper creation refusal, credential refusal, and existing
owner-scoped public-data routes using **synthetic seeded** accounts.
Frontend tests pin actual creation-dialog exclusion and the Settings partial
label, without creating an Extended account or requesting a credential.

Remaining account integration requires the credential-policy decision, a safe
key's verifiable permissions and account binding, and a truthful execution-PnL
strategy compatible with Vessel's exact imported-fact contract. Any wider
public-market workflow also needs an explicit product decision; this PR does
not silently invent one. Two independent adversarial Sol reviews are coordinated
by the parent; they are **pending**, not recorded as passes here.

Local verification on October 23, 2026 (SDK **10.0.302**, no live database):

- Backend: Extended, exact `/api/system`, venue registry/market-data,
  candle API/cache and Lighter Robinhood host-registration suites:
  **161 passed, 1 skipped**. The existing
  `VenueRegistryTests.Refresh_needs_the_sync_capability_and_an_adapter`
  requires PostgreSQL and was not counted as a pass.
- Frontend: `ApplicationShell.test.tsx`, `CreateDialogs.test.tsx`,
  `api/system.test.ts`, `api/venues.test.tsx`: **140 passed**.
- `pnpm --dir frontend typecheck`, `lint`, `build`: passed.
- `git diff --check`: passed. No deployment, migration, release, owner credential
  or owner database was used. Broader/combined checks and independent reviews
  remain with the parent.
