# Stablecoin wallet availability

October 1, 2026. The owner reported missing Hyperliquid available funds and authorized stablecoin retrieval. The old reader used primary `clearinghouseState` only. The configured account is unified mode, whose wallet source is the spot ledger; its zero primary perpetual snapshot was not its wallet balance.

## Scope and identities

Refresh additionally reads `userAbstraction`, `spotMeta` and `spotClearinghouseState`. Perpetual positions/history remain primary-DEX-only; spot trading is not enabled.

Supported assets are USDC, USDE, USDT0 and USDH. Identity uses curated token IDs plus current metadata index/name mappings, not ticker or `isCanonical` alone. The official public metadata marks only USDC canonical, so that flag would exclude the other three incorrectly.

| Symbol | Exact HyperCore token ID |
| --- | --- |
| USDC | `0x6d1e7cde53ba9467b783cb7c530ce054` |
| USDE | `0x2e6d84f2d7ca82e6581e03523e4389f7` |
| USDT0 | `0x25faedc3f054130dbb4e4203aca63567` |
| USDH | `0x54e00a5988577cb0b0c9ab0cb6ef7f4b` |

## Value meaning

- Total/Held are exact venue token units. Available is exact `Total - Held`.
- This is wallet/order-hold availability, not guaranteed perpetual collateral, borrowing or withdrawal capacity.
- Primary perpetual equity/withdrawable/margin fields stay separate. Unified and portfolio-margin balances use the wallet alone; other account modes sum the separate perps equity and observed wallet total once.
- Overview shows total and available balance across enabled accounts, with coverage. Account rows show wallet availability where supported; account detail shows each supported coin's amounts.
- Nominal summaries assume one nominal unit per supported USD-pegged token. There is no pricing, FX, depeg or full-account valuation. Exact strings and integer-arithmetic sums avoid decimal addition rounding.
- HYPE, UBTC and other assets, HyperEVM wallets and lending/borrow accounting are excluded.

Empty wallets are zero only after a valid observed balance-array read. Missing metadata does not invent tokens; old snapshots remain unknown until refreshed. Invalid identities/numbers/holds or request failures preserve prior successful data. Six sequential requests share one 20-second deadline, but are not an atomic venue snapshot.

## Persistence and API

Migration `20261001125452_StablecoinWallet` adds observation/mode/scope fields and owner-scoped `account_stablecoins`, keyed by owner/account/token ID. Replacement is transactional with the existing account lock and snapshot. Disable hides wallet rows without deletion; enable restores them. Account deletion cascades through the snapshot.

`SnapshotDto.stablecoinWallet` is nullable, with observation time, mode, scope, balances and caveats. Account DTOs add available nominal wallet amount, scope and mode. Overview totals add that amount and observed enabled-account count. Existing perps/manual value fields are not silently redefined.

`AccountSnapshot.AvailableMarginUsd` and the snapshot DTO `availableMarginUsd` retain venue-reported availability separately from `withdrawableUsd`. Lighter supplies account `available_balance`; RISEx supplies summary `free_collateral`, documented as available for new positions. Standard Hyperliquid uses its existing perps `withdrawable`. Neither a separate spot wallet nor a second availability field is added to the selected account amount. Missing/null/empty is unknown and zero is known. Equity minus margin is never calculated as availability.

Account DTOs carry the selected `availableBalanceUsd` and its `wallet`/`margin`/`withdrawable` source. Overview and portfolio totals sum it once per enabled account, with known-account coverage. Account detail, account rows and portfolio tiles show it; the raw withdrawal field remains separate. Migration `20261012192227_AvailableMargin` leaves older snapshots null until refresh and was tested only in disposable PostgreSQL.

## Verification

### October 12, 2026 correction

Overview's wallet-only subtotal now selects snapshots through enabled accounts explicitly. The store already filters disabled snapshots; the service no longer relies on that filter for monetary totals. The new total balance follows the existing account/portfolio balance rule, and available balance uses wallet availability only for wallet-capable unified/portfolio-margin accounts; other accounts use reported available margin, then reported withdrawal. It never adds those two availability sources together for one account. Manual values and discovery collateral are not availability. Unknown remains null; an observed empty wallet is zero. Coverage counts known enabled accounts, not full venue valuation. Both account-level and cross-account sums pass original decimal ledger amounts to the exact integer-arithmetic summation without intermediate decimal addition.

See [the core contract](core-workspace-contract.md#october-12-2026-combined-balances-and-overview-actions) for the fields and source limitations. These nominal sums still exclude non-stable assets and make no promise about free margin or withdrawability.

### Original verification

`pnpm check` passed 452 cases: 299 backend, 77 frontend, 76 unchanged prototype. Adapter coverage includes 169 fixture cases across six requests. Three new PostgreSQL cases cover exact persistence, isolation, replacement/empty/unknown observations, failure retention, disable/enable and deletion. Previous migration/API-shape expectations were updated.

After migration, the user's configured account was refreshed once under the request. Positive available unified-wallet funds and the supported symbols were verified without copying balances, addresses or credentials to logs/docs. Non-stable assets were excluded, perps fields remained separate and fills deduplicated. Native LAN login verified a nonzero wallet metric and no alert. The preview token and user records remain unchanged.

Primary sources: [account modes](https://hyperliquid.gitbook.io/hyperliquid-docs/trading/account-abstraction-modes), [spot info API](https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint/spot), [info requests](https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint). Token identities were checked against official public spot metadata. No signing or order request occurred.

## Nominal fee and result list (October 14, 2026)

Execution fees and pooled results use a separate Application list, `NominalStablecoins` (USDC, USDG, USDT, USDT0, USDE, USDe and USDH), which adds USDG (Lighter Robinhood collateral) and USDT. It applies to normalized fee-token labels, not to wallet token identities: wallet reads still validate the curated token IDs above and do not gain new assets from it. Both count at nominal 1:1 with no FX or depeg valuation.

## Wallet failures on separate ledgers (October 21, 2026)

Phase 2, item 14b (F-14, probe V6). In the `default`, `disabled` and
`dexAbstraction` modes the perps and spot ledgers are separate, so the wallet
is not the perps balance. A failed or invalid `spotMeta` or
`spotClearinghouseState` read on these accounts now degrades the wallet alone.
- The perps snapshot, positions and fills still apply.
- The read reports no wallet, so the store keeps the last observed wallet with
  its own observation time; it is never read as empty.
- The history notice adds "The stablecoin wallet could not be refreshed; its
  last observed balances and their time are kept."

Unified and portfolio-margin accounts hold every balance in the wallet, so a
wallet failure still fails their read. A rate limit, a missing account mode or
the shared read deadline still stops the whole read on every mode.
