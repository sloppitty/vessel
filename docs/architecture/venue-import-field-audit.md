# Venue import field audit

Checked October 16, 2026. Source checks cover public official documentation and schemas for Hyperliquid `userFills` and RISEx `/v1/trade-history`, without account API requests. Local investigation also inspected aggregate sync diagnostics from the existing isolated database snapshot in a read-only transaction. This is not a live owner reproduction.

## Verdict

Neither venue's inspected sources establish Lighter's rule that absent/null fees and opening PnL can default to zero. Hyperliquid documents explicit values. RISEx documents empty or omitted PnL, but does not explain how those variants preserve a net-of-fee result. Missing, empty and JSON null are different cases.

| Case | Source verdict | Comparison with current parser |
| --- | --- | --- |
| Hyperliquid missing/null/empty `fee`, `closedPnl` or `feeToken` | No zero-default or nullable variant established. Official fill schemas require strings. [H1–H3] | No confirmed valid rejected variant. |
| Hyperliquid zero opening PnL with a nonzero signed fee | Explicit zero opening PnL is documented; negative fees mean rebates. [H1–H2] | Supported by the existing strict, signed parser. |
| RISEx empty `realized_pnl` | REST field description allows an empty value when there is no position change. [R1] | Documented variant rejected by the decimal parser. Its financial replacement is not established. |
| RISEx omitted `realized_pnl` | REST schema has no required-property list; WebSocket fills explicitly omit PnL when nothing is realized. [R1–R2] | Strict presence is stronger than the REST schema. WebSocket omission does not prove REST zero semantics. |
| RISEx null PnL or missing/null/empty fee | No safe-zero rule found. JSON null is not declared nullable. [R1–R2] | No confirmed financially safe default. |
| RISEx negative fee rebate | No explicit negative-fee/rebate convention found. Schema has no sign restriction. [R1, R3–R4] | `Json.Nonnegative(fee)` is stricter than the schema, but a valid negative rebate rejected by it is not confirmed. |

## Hyperliquid

The REST primary-perpetual example pairs `dir: "Open Long"` and `closedPnl: "0.0"` with a positive `fee` and `feeToken: "USDC"`. Opening PnL is therefore not the opening fee's negative value in this example. The `fee` already includes any builder fee; only `builderFee` is explicitly optional when zero. That optionality does not apply to `feeToken`. [H1]

The official WebSocket `WsFill` interface declares `closedPnl`, `fee` and `feeToken` as mandatory strings and explicitly identifies negative fees as rebates. The official Python SDK's `Fill` TypedDict agrees, with no `NotRequired`, optional string or `total=False` on those fields. These are schema declarations, not runtime validation or an exhaustive guarantee about every historical response. They provide no evidence for omitted/null values meaning zero. [H2–H3]

No optional `feeToken` or nullable `closedPnl` variant was established for the supported primary-perpetual import. Spot and HIP-3 examples do not expand Vessel's supported import scope. [H1]

## RISEx

The REST endpoint's `v1ApiTrade` schema describes `fee` as an account-trade decimal string and `realized_pnl` as a decimal string that may be empty when no position changes. Neither field has a default or `nullable: true`. The object has no `required` list, so it does **not** formally require either property. This permissive schema is not a rule for recovering missing monetary values. [R1]

The official WebSocket fills page calls itself the real-time counterpart of REST history, but documents a distinct omission policy. PnL fields disappear when empty, and its calculation section ties their presence to reductions or flips. It defines fill PnL through the change in cumulative position realized PnL. It does not state whether this amount includes trading fees or settled funding. Its fee field is a decimal USDC amount. [R2]

Vessel's existing [RISEx integration record](risex-integration.md) treats `realized_pnl` as net of fees, including opening fees. The inspected public definitions do not independently establish that opening fills always report `-fee`, or reconcile that interpretation with empty/omitted opening PnL. Substituting zero is therefore not justified for Vessel's net-of-fee field. The sources also do not justify claiming funding-free realized PnL. [R1–R2]

### Fee rebates

The trade schema places no numeric sign constraint on its fee string. The fee-tier schema likewise declares maker/taker rates as numbers without a minimum, but publishes no negative-rate example or rebate encoding. Public WebSocket trades document an explicit zero for a maker whose tier charges no fee. None establishes that a negative account-trade `fee` is a supported rebate. Absence of a lower bound is not proof of that economic meaning, nor proof that fees must always be nonnegative. [R1, R3–R4]

No first-party fill-normalizing client with zero defaults was established. RISE's own API guide explicitly labels `risex-client` unofficial and community-maintained; its defaults or optional properties cannot establish venue semantics. The developer reference's TypeScript client example covers authentication, not fill normalization. [R5–R6]

## Limits

The confirmed documentation/parser mismatch is RISEx's empty PnL variant. REST schema-level omission is also permitted, but its monetary meaning remains unspecified. No nullable-PnL variant, optional Hyperliquid fee currency or RISEx negative-fee rebate was confirmed. These findings identify contract gaps, not parser fixes.

RISEx's extracted OpenAPI advertises a testnet server, and its general reference warns that the API is changing. Documentation alone cannot establish current mainnet emission frequency or reproduce an import failure. Catalogue freshness and unknown-market filtering are separate from these field semantics. [R1, R7]

## Local corrections and verification

RISEx account and order imports now force a catalogue refresh before filtering
facts. Four failing synthetic regressions demonstrated that a warm catalogue
could hide newly listed market positions, fills and orders, or prevent the
import from noticing a failed metadata read. Both paths now use the shared
cache's existing refresh mechanism. Instrument/chart TTL and single-flight
behavior are unchanged. Hyperliquid and Lighter already refresh before imports.

RISEx missing/null/empty PnL now produces an explicit safe error explaining that
its net-of-fee result is unknown and the refresh was not imported, instead of a
generic invalid-response error. This does not yet make such executions importable.
Resolving that gap requires verified financial semantics or a separately designed
unknown-money representation, not an invented zero or an inferred closing PnL.
Missing fees remain errors. No Hyperliquid parser change was justified.

November 17, 2026 (issue #118): a RISEx negative `fee` is no longer a
response-level failure. The rebate's meaning is still not established, so the
trade is not imported; it is set aside with a `fee` marker ("negative fee")
keeping its order, and the rest of the read applies. An unknown Hyperliquid
`side` or RISEx `side`/`position_side` is likewise a marked row-level omission
rather than a failed read. Missing, null or malformed values remain errors. See
the row-level section of `execution-facts.md`.

Regressions cover missing/null/empty fee and PnL, Hyperliquid's fee currency,
explicit zero opening PnL, signed Hyperliquid fees, and both RISEx import paths
with warm catalogues. Full backend verification passes 1,784 tests with 137
database/live opt-ins skipped. The Release build has zero warnings and errors.
No schema change, migration, deployment or live account refresh was performed.
The isolated reproduction container is stopped again.

## Primary sources

- **H1:** [Hyperliquid info endpoint, user fills](https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint#retrieve-a-users-fills). REST opening example and builder-fee inclusion.
- **H2:** [Hyperliquid WebSocket subscriptions, `WsFill`](https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/websocket/subscriptions). Required strings and signed rebate convention.
- **H3:** [Official Python SDK `Fill`, lines 132–150](https://github.com/hyperliquid-dex/hyperliquid-python-sdk/blob/b4d2d1bfde9bfb3411fec3f781e3981b48a1a0c5/hyperliquid/utils/types.py#L132-L150). Pinned source revision.
- **R1:** [RISEx account trade history, extracted OpenAPI](https://developer.rise.trade/reference/getaccounttradehistory.md). `/v1/trade-history`, `components.schemas.v1ApiTrade`; page metadata updated July 16, 2026.
- **R2:** [RISEx fills channel](https://developer.rise.trade/reference/fills-channel.md). Field table, omission policy and PnL calculation; page metadata updated August 17, 2026. WebSocket contract, not a REST presence guarantee.
- **R3:** [RISEx fee-tier OpenAPI](https://developer.rise.trade/reference/feetierservice_getuserfees.md). `apiFeeScheduleEntry` and `apiGetUserFeesResponse`; documentation only, no authenticated request.
- **R4:** [RISEx public trades channel](https://developer.rise.trade/reference/trades-channel). Explicit zero maker fee; distinct from account history.
- **R5:** [RISE API guide](https://docs.risechain.com/docs/risex/api.mdx). Explicit community-SDK status.
- **R6:** [RISEx TypeScript reference](https://developer.rise.trade/reference/javascripttypescript.md). Authentication example, no fill decoder.
- **R7:** [RISEx general information](https://developer.rise.trade/reference/general-information). API-development warning.
