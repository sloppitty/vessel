# Core workspace contract

October 1, 2026. The owner requested the main application layout without the Play workspace and authorized backend core, Hyperliquid integration, PostgreSQL mounting and migrations.

The later [Plays first-pass contract](plays-workspace-contract.md) supersedes this record's original disabled-Play-navigation instruction. All backend/import limits below remain unchanged.

This step delivers account/portfolio management and a bounded manual-trigger read-only refresh. It does not implement automated jobs, complete lifetime history, Play editing, order placement or inferred fill matching.

## Work ownership

- Orchestrator owns `frontend/`, root configuration, this shared contract, integration and documentation.
- Backend core worker owns `backend/` except the Hyperliquid implementation/tests and `Vessel.Application/Venues/IPerpetualVenueReader.cs`.
- Venue worker owns only `Vessel.Infrastructure/Venues/Hyperliquid/` and `Vessel.Tests/HyperliquidAdapterTests.cs`.
- Shared normalized venue interface/types are already in Application. Neither worker changes them without coordinating.

## System metadata

Keep the existing system route and owner response shape. The API stage may become `core`; Hyperliquid venue status becomes `read-only`, not a claim of order placement or a running background sync. The frontend accepts both the prior foundation/planned metadata and the new core/read-only values during development.

October 14, 2026: each venue may carry an optional `logo` key from a fixed set (`hyperliquid`, `lighter`, `lighter-rh`, `risex`, `manual`). The browser keeps known keys, drops anything else without failing the system contract and shows a generic icon; it maps keys to bundled assets and still never branches on venue IDs. Overview summary rows grouped by account show the venue logo and descriptor name. See the venue logos section of `account-management-contract.md` and `robinhood-lighter-integration.md`.

## Owner-scoped endpoints

All routes below use the authenticated owner, never an owner ID supplied by the request body. GUIDs are JSON strings; money, price and quantity values are invariant decimal strings, not binary-float JSON numbers. Unknown values remain null.

### Portfolios

- `GET /api/portfolios` returns `PortfolioDto[]`.
- `GET /api/portfolios/{id}` returns the owner-scoped portfolio or 404.
- `POST /api/portfolios`, body `{ "name": "Swing trading" }`, returns created `PortfolioDto` with status 201.

`PortfolioDto`:

```text
id, name, accountCount,
totalValueUsd: string|null,
valueCoverage: "complete"|"partial"|"unavailable"
availableBalanceUsd: string|null, availableBalanceAccountCount, enabledAccountCount,
availableBalanceCoverage: "complete"|"partial"|"unavailable"
```

The first management workflow assigns each new account to one portfolio. Existing unassigned foundation accounts can remain unassigned until later migration/UI work. This is an initial workflow assumption, not permission to double-count accounts in future portfolio grouping.

### Accounts

- `GET /api/accounts` returns `AccountDto[]`.
- `GET /api/accounts/{id}` returns the owner-scoped account or 404.
- `POST /api/accounts`, body `{ portfolioId, name, venueId, address?, manualAccountValueUsd? }`, returns status 201 and `AccountDto`.
- New account choices are `manual` and `hyperliquid`. Lighter remains planned; Quantfury remains a feasibility candidate.
- Hyperliquid requires a valid 42-character public address. No private key or signing credential is collected.
- Manual account value is an optional nonnegative decimal string.

`AccountDto`:

```text
id, portfolioId: string|null, name, venueId,
address: string|null,
accountValueUsd: string|null,
availableBalanceUsd: string|null, availableBalanceSource: "wallet"|"margin"|"withdrawable"|null,
lastSyncedAtUtc: string|null,
syncStatus: "manual"|"not-synced"|"synced"|"error",
lastSyncError: string|null,
positionCount,
historyNotice: string|null
```

### Snapshots, fills and manual refresh

- `GET /api/accounts/{id}/snapshot` returns the latest `SnapshotDto` or JSON null if none exists; an absent/foreign account returns 404.
- `GET /api/accounts/{id}/fills` returns up to 100 latest owner-scoped `FillDto` items.
- `POST /api/accounts/{id}/sync` reads Hyperliquid account state, instruments and recent fills, persists a coherent result, and returns updated `AccountDto`.
- Sync is manual-triggered and bounded. Manual accounts return validation error for sync; no fake provider result is manufactured.
- Concurrent account refreshes should not duplicate fills. Scope source identity by account, venue contract and fill ID.
- Provider failures produce safe, generic errors and do not discard the previous successful snapshot. Auth values or raw provider details must not reach the frontend/logs.

`SnapshotDto`:

```text
observedAtUtc, valueScope,
accountValueUsd, withdrawableUsd, availableMarginUsd, marginUsedUsd,
positions: [{
  contractId, signedQuantity, entryPrice,
  unrealizedPnlUsd, marginUsedUsd, leverage: number|null
}]
```

`FillDto`:

```text
id, accountId, contractId, side, direction, price, quantity,
fee, feeToken, closedPnlUsd, occurredAtUtc, orderId,
sourceFillId, transactionHash, playId: null
```

Fill facts are never auto-linked to a Play, given a thesis, or labeled as a price-trigger confirmation. Fee currencies and source order/fill identities remain explicit. No full-history completeness is promised.

### Overview

`GET /api/overview` returns:

```text
portfolios: PortfolioDto[],
accounts: AccountDto[],
totals: {
  portfolioCount, accountCount,
  totalAccountValueUsd: string|null,
  valuedAccountCount, openPositionCount, importedFillCount,
  totalBalanceUsd: string|null,
  balanceAccountCount, balanceCoverage: "complete"|"partial"|"unavailable",
  availableBalanceUsd: string|null,
  availableBalanceAccountCount, availableBalanceCoverage: "complete"|"partial"|"unavailable",
  enabledAccountCount,
  availableStablecoinNominalUsd: string|null, stablecoinAccountCount
},
recentActivity: FillDto[],
scopeNote: string
```

Only known account values contribute to the aggregate. If no values are known, total is null, not zero. Counts and value coverage make partial knowledge visible. Hyperliquid values refer to the primary perpetual DEX margin snapshot, not a guarantee of the entire broker account value.

### October 12, 2026: combined balances and Overview actions

Overview shows Total balance and Available balance, with known-account counts out of enabled accounts and coverage. Both span all portfolios and unassigned accounts exactly once, never disabled accounts. The earlier `totalAccountValueUsd` remains the perps/manual subtotal; it is not the headline balance.

- Total balance sums each account's `Balance`: manual recorded value; supported-stablecoin wallet total alone for unified/portfolio-margin accounts; otherwise known perps equity plus any observed supported-stablecoin wallet total. A non-unified account with only one known ledger contributes that known amount. A unified account without an observed wallet contributes nothing, even if it has a perps field.
- Available balance selects one source per enabled account. A wallet-capable account in `unifiedAccount` or `portfolioMargin` uses observed supported wallet total minus held only. Without that observation it stays unknown, even if a perps field exists. All other modes use reported `availableMarginUsd` first, then `withdrawableUsd`. Standard Hyperliquid therefore uses perps `withdrawable`, not its separate spot wallet. Never add availability sources, derive availability from equity minus margin, or use manual value or discovery amounts as snapshot funds.
- `availableMarginUsd` stores Lighter account `available_balance` and RISEx summary `free_collateral`. The RISEx API defines free collateral as available for new positions. Lighter names an available balance but does not establish guaranteed withdrawal capacity in its account schema; Vessel does not relabel it as withdrawable. Missing, null or empty strings are unknown; an explicit zero is known. Signed exact decimals are preserved without clamping. Both venues keep `withdrawableUsd` null.
- Account DTOs expose the selected `availableBalanceUsd` and `availableBalanceSource` (`wallet`, `margin`, `withdrawable`, or null). Disabled accounts expose neither. Portfolio DTOs expose `availableBalanceUsd`, `availableBalanceAccountCount`, `availableBalanceCoverage` and `enabledAccountCount`, using the same selection as Overview. Account detail names the selected source and keeps the separate perps withdrawal field; account rows and portfolio tiles show availability without another ledger addition.
- Migration `20261012192227_AvailableMargin`, generated with the repository's pinned .NET EF CLI and persistence startup project, adds nullable unconstrained numeric `account_snapshots.AvailableMarginUsd`. Existing snapshots remain unknown until an explicit refresh. Apply it explicitly during deployment; verification uses only disposable databases.
- Coverage counts accounts with a known eligible amount, not completeness of venue valuation. No known accounts means null and `unavailable`; some means `partial`; all enabled means `complete`. An observed empty wallet is known zero. Exact ledger amounts reach `StablecoinTotals.Sum` without intermediate decimal addition, including within a single account.
- The legacy wallet-only subtotal also excludes disabled accounts. Position and imported-execution behaviour is unchanged.

Owner financial responses are not cached. The request client uses `cache: no-store`, API responses use `Cache-Control: no-store`, and `/api/overview` is outside the market-data compression allowlist. Refresh and management changes advance the shell request generation and reread Overview, including its portfolios and accounts.

Overview has a primary New play button and a separate keyboard-accessible Add menu for New portfolio and Add account. These reuse the existing editor and dialogs. New play opens a blank editor only when the current session is clean; unsaved draft or saved-Play changes reopen that editor with a notification instead of being discarded.

The `Usd`-suffixed fields preserve the initial shared contract's nominal dollar display convention. Hyperliquid collateral balances are USDC-denominated; oracle/contract quote units can differ. This slice performs no FX conversion and must not present the sum as a currency-aware fair-value calculation. Scope notes and the Overview metric name make that limit visible. A full currency/numeric contract remains later work.

## Failure contract

Use Problem Details for errors, with safe user-facing `detail`. Validation is 400, missing/foreign owner resources 404, provider failure 502 or unavailable service 503. Existing bearer failures remain 401. Do not disclose stack traces, connection strings or response bodies. Financial values use exact decimal storage compatible with .NET decimal; do not silently round provider numbers into a two-decimal schema.

## Boundaries and checks

Use existing clean dependency direction, migrations and PostgreSQL owner constraints. Provider endpoints are fixed server configuration, not user-supplied fetch URLs. Validate public addresses and bounded HTTP/JSON handling. Local tests use HTTP fixtures, not arbitrary real accounts.

Core tests must cover unauthorized calls, foreign resources, manual account creation, Hyperliquid validation, persisted snapshots and fill deduplication, failure preserving earlier data, portfolio coverage, multiple concurrent plays and explicit migration compatibility.

Frontend default becomes Overview with navigation for Overview, Portfolios, Accounts, Activity and Settings. Play navigation is disabled/marked later; it must not mount the approved Play workspace yet. Use real core API data with honest empty/loading/error states. No invented chart/P&L on the overview; no dummy working buttons for deferred features. The approved Play component and prototype are retained for later integration.

## October 13, 2026: scoped Overview summary

Overview defaults to All accounts and By portfolio. Its scope selector offers every current portfolio, Unassigned when it has enabled accounts, and each enabled account. The metric tiles, Summary, Recent activity and saved Plays panel use that account set. Scope, grouping and the result period live in shell memory across navigation; a removed portfolio, removed/disabled account or empty Unassigned selection falls back to All on the common reload. A row's native button or row click selects its portfolio/account scope. The enabled-account panel remains below Summary, including Accounts navigation and the existing owner-wide Refresh all action.

Authenticated `GET /api/overview/summary` accepts these optional query parameters:

- `groupBy=portfolio|account`, default `portfolio`.
- `period=7|30|90|all`, default `30`.
- `scope=all|portfolio:<GUID>|unassigned|account:<GUID>`, default `all`.

Invalid values return 400. Resource scopes require canonical GUID form and nonempty IDs. An unknown or foreign resource returns 404, without distinguishing those cases. An owned disabled account has an empty scope. The response and client requests use `no-store`; summary reads are outside market-data compression and authorization is never cached.

`OverviewSummaryDto` returns `groupBy`, `period`, `scope`, request-time UTC `asOfUtc`, nullable `fromUtc`, `totals`, `rows`, `totalGroupCount`, `omittedGroupCount` and `scopeNote`. The response includes at most 200 rows. Totals and shares include all scoped enabled accounts, even when some rows are omitted. Portfolio rows exist only for groups with enabled members; Unassigned is null membership, not a stored portfolio. Rows sort by exact balance descending, nulls last, with name/ID tie-breakers. Group IDs double as scope values. Each row has `id`, `name`, nullable portfolio `icon` or account `venueId`, `totals` and nullable `shareOfBalance`.

The summary passes its resolved enabled-account set into Activity's store and aggregation path. Portfolio, Unassigned and account scopes load only those accounts' retained fills, snapshots and confirmed order links. SQL account selection still enforces owner and enabled-only restrictions. An empty scope skips Activity reads entirely, and an empty account set also short-circuits the store before any query. All retained fills for included accounts contribute before display limits; restricting accounts does not change their operation results. The existing `/api/activity` all-account and single-`accountId` reads remain unchanged.

Scope and row totals contain:

```text
accountCount, openPositionCount, importedFillCount,
balance, available, unrealizedPnl, realizedResult: {
  amountUsd: string|null,
  coverage: {
    contributingAccountCount, totalAccountCount,
    contributingOperationCount, excludedOperationCount,
    reasons: [{ reason, count }]
  }
}
shareOfBalance: { numerator: string, denominator: string }|null
```

Balance/availability reuse `WorkspaceService` account DTO calculations without changing their ledger definitions. Exact strings reach `Activity/ExactAmount` for cross-account addition, including results exceeding .NET decimal's range. Position count includes nonzero signed perpetual positions only. Unrealized P&L sums their reported values from latest retained snapshots, with snapshot-account coverage. An observed empty snapshot contributes known zero; a missing snapshot contributes nothing. Counts describe retained snapshots, not a live completeness claim.

Realized result reuses Activity's complete internal operation summaries before any Activity filter or page. Only known-net closed operations with actual `closedAtUtc` in the inclusive UTC window contribute; `all` still excludes future exits. Gross USDC fees are deducted once by the existing aggregator, and net-of-fee P&L is not charged again. Unknown net results and incomplete operations are excluded and counted. For incomplete operations without an exit time, their latest execution determines only whether to include an exclusion notice in this period, never a fabricated closing date or result. Coverage states contributing accounts, known closed operations, excluded operations, accounts without a known closed result, and retained-history/snapshot limitations. No known closed result is null, not a fabricated zero. No retrospective Play, initial risk, complete-history or funding-free P&L is inferred.

Shares retain an exact numerator/denominator ratio, because division can recur. A missing balance or nonpositive denominator yields null. Percentages round to two places only for display using integer arithmetic; the theme-token bar clamps its visual width to 0–100%, without changing the ratio or balance. Unknown and partial financial values keep coverage visible. While a narrower request loads, its amounts stay unknown rather than briefly displaying All totals. Failures clear summary amounts and offer Retry summary. The common reload generation rereads Summary.

Recent activity applies the scoped account set before its latest-five display limit and uses account-scoped HTTP reads for individual accounts. Portfolio/Unassigned filtering does not expand retained import history. Saved Plays uses the same enabled-account set for rows and tab counts. October 16: both read one server page (`scope`, `pageSize=5`) with server counts; see `list-paging.md`. No new persistence, EF migration, packages, credential handling or venue network reads are involved.

## October 15, 2026: Overview allocation

The owner asked for pie charts showing what share of a portfolio each account represents, and then made the Allocation panel permanent between the metric tiles and Summary. It uses the account and portfolio data the common `/api/overview` read already returns; there is no new endpoint, persistence or migration.

- **All:** one donut of each portfolio's share of the enabled-account total, with Unassigned as its own slice, beside one small donut per portfolio of its accounts' shares. Portfolios without enabled accounts have no card.
- **Portfolio or Unassigned:** one donut of that group's accounts, with venue, exact balance and share.
- **Account:** its group's donut with that account emphasized; an account folded into Other emphasizes Other.

Slices use each enabled account's `balanceUsd`. Disabled accounts are excluded. Accounts without a reported balance, and zero or negative balances, are not drawn and are listed below the chart instead of becoming zero slices. Shares divide by the sum of drawn positive balances. Sums use exact integer decimal arithmetic and percentages round only for display through the existing `balanceShare` helper; floating point only places the arcs. A group with more than six positive slices keeps its five largest and folds the rest into a neutral Other; each portfolio card and heading still shows that portfolio's own share of the total. A part's colour is its position in the group's stable list order, so balance changes do not repaint it; only beyond six parts can a later part take a slot left free by a folded or undrawn one. Each donut colours its own entities.

Slices and legend names select the corresponding Overview scope; the legend is the keyboard and text path for every value. A scoped view opens with a small back arrow before the uppercase group heading (the name is plain text, with no clear control). The arrow is shown whenever the scope is not All (a portfolio, Unassigned or an account), and it always returns to All: its accessible name and tooltip are always "Back to all accounts", whatever the owner clicked before. Clicking the selected slice or legend item again deselects it and does the same, so an account goes straight back to All rather than to its group; clicking a different slice or legend item selects that one. The panel keeps no record of how a scope was reached, so there is no remembered path, no per-origin destination and no skipping of a group that only repeats its single account. After a keyboard or pointer activation inside the panel, focus returns to the control for the same entity in the new view, or to the back arrow, never to the page. The Overview scope row shows a compact "View all" button beside the select, level with it, only while the scope is narrower than All; it sets the scope to All and moves focus to the select (the button leaves the page, and on narrow screens it sits beside the select under the label). The scope select and the Summary result-period select set `autocomplete="off"`, so a browser restoring form state can never show a stale option that React does not hold; a fresh load always starts at All, and the scope lives only in shell memory. Hover and focus dim only the other slices and swatches, never legend text, and reset when the scope changes. Legends lay out by the width of their own section or card (container queries), not the viewport: the venue column hides first, then the large donut stacks above its legend, and small donuts stack in narrow cards. Long names truncate with a title, notes wrap, and the centre label clamps to two lines (one in small donuts), down to 320px. Colours come from theme tokens `--series-1`…`--series-6` and `--series-other`, chosen per scheme in `appearance.ts` and checked for adjacent colour-vision separation on the card surface. Light slots below 3:1 on white always carry visible labels.

## October 15, 2026: quieter Overview text

At the owner's request the Overview drops restating captions. Coverage counts appear as a compact badge (for example "6/7", reasons in its tooltip) only when coverage is incomplete, so complete figures carry no caption; incomplete coverage stays visible. Total balance carries a visible "Nominal USD" tag (stablecoins at 1:1 in its tooltip). The workspace scope note and Summary coverage reasons are shown verbatim in one collapsed "About these numbers" disclosure. Clicking a selected allocation slice or legend item again goes back like the Allocation back arrow, always to All accounts. See the [visual refresh](../design/visual-refresh/README.md).

## October 16, 2026: account identity

Accounts were told apart by name only, so "main" on Hyperliquid and "main" on RISEx looked the same. The owner compared options (`docs/design/account-identity/`) and chose identity capsules for the Allocation legends and a searchable account picker for account filters. No endpoint, persistence or migration changes.

- **Identity marks** (`components/accounts`): the venue logo as an avatar, the venue name as a tag tinted with the venue's brand accent, and the short source (address or account index, `shortSource`) in a chip whose colour is hashed from the source itself (FNV-1a hue), so one wallet keeps its colour on every venue and screen and same-named accounts differ. The full name, venue and source are in the title. An unnamed account reads as its short source with an Unnamed tag; a manual record shows "Manual record".
- **Brand accents** live beside the bundled logos (`venue-logos.ts`, `accent`), not in themes. They only tint marks through `color-mix` with theme tokens; text keeps theme contrast, light schemes use deeper tints, and logos without an accent stay neutral.
- **Allocation**: every legend row is a capsule: avatar ringed in the slice colour, name, venue tag and source chip for accounts (portfolio icon and account count for portfolio slices, a neutral tile for Other), share with a small bar in the slice colour, and the exact balance in the large legend. Rows keep the existing scope buttons, hover/focus emphasis and focus restoration. The venue tag never truncates for the chip: the chip wraps under it in narrow cards.
- **Account picker** (`AccountPicker`): replaces the native select for the Activity account filter, the Review account filter and the Overview scope (portfolios and Unassigned as grouped choices above the accounts). The trigger shows the logo and name, plus the source chip only when the name is blank or shared with another listed account (trimmed, case-insensitive). The popover (Radix Popover) has a search field over name, venue, source and portfolio, venue filter chips, rich rows with balance and portfolio, and a result count. Keyboard: arrow keys open and move, Enter chooses, Escape closes and returns focus to the trigger. The trigger is named by its field label followed by the value; options are named "name, venue, source, portfolio". Review reads sources from the shell's accounts because its own options do not carry them. The other selects stay `NativeSelect`.
- **Account select** (`AccountSelect`, October 18): the same picker for choosing exactly one account in a form, used by the Play editor's Account field. It offers no aggregate choices (All accounts, portfolios), its trigger adds the venue tag beside the name (replacing the venue badge that sat beside the label; a trigger narrower than about 200px drops the tag and keeps the logo so the name stays readable), it is disabled when there is nothing to choose, and a value missing from the list reads as an explicit label ("Unavailable or disabled account" on a fixed Play). The Play editor uses the bare density to sit level with the Portfolio select; the Portfolio filter still narrows its list, and an account can be changed but not cleared. Options carry `data-value` for browser automation.
