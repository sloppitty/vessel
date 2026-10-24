# Core workspace implementation

October 1, 2026. The owner requested the main application layout without the Play page and authorized delegated backend/core/Hyperliquid/database work. Subsequent account/portfolio management is recorded in [management state](account-management-state.md); that record supersedes the initial management limitations below.

## Interface

The default frontend opens a Graphite application shell with Overview, Portfolios, Accounts, Activity and Settings. The original core step kept Play navigation disabled. The subsequent [Plays first pass](plays-workspace-contract.md) enables a prototype-aligned in-memory draft workspace with a chart placeholder and metadata-only Hyperliquid instrument choices; the disposable prototype remains separate.

- Overview uses known nominal values and persisted account/snapshot/execution counts. Empty states use setup guidance, not invented performance.
- Portfolios and accounts can be created through the authenticated API. New accounts belong to one portfolio; legacy unassigned heads remain valid.
- Accounts can be manual with optional value or Hyperliquid with a public address. Lighter and Quantfury remain deferred.
- Account detail reads latest snapshots and recent fills. October 16: account creation queues one initial bounded refresh in the browser session; later refreshes are explicitly requested. No durable background job or history backfill.
- Fills remain unassigned to plays and have no fabricated thesis. Retrospective editing is deferred.
- Mobile navigation supports keyboard close and background inertness. Tables scroll internally.
- The token stays in a session client closure, not storage, URLs or build assets. Disconnect releases it; the password field clears after connection.

The shell lives in `frontend/src/features/workspace/`. Plays now mounts the draft workspace from `frontend/src/features/plays/`. Overview remains the default page.

### October 15, 2026 Overview allocation

`frontend/src/features/workspace/OverviewAllocation.tsx`, `overview-allocation.ts` and `overview-allocation.css` add the Allocation panel between the metric tiles and Summary, following the [allocation contract](core-workspace-contract.md#october-15-2026-overview-allocation). `OverviewSummary` takes a `beforeSummary` slot so the tiles stay first. Theme tokens `--series-*` are added in `styles.css` and `appearance.ts`. No backend, API contract, persistence or migration changed.

Unit tests cover exact sums, missing/disabled/non-positive balances, folding into Other and stable colour slots; component tests cover the three scope views, emphasis, Other emphasis and scope selection. Two Sol (Codex) reviews found no blocker. Their verified findings were fixed with regressions: legend text no longer dims below contrast, long names no longer widen the panel at 320px, colour slots stay with the entity across balance changes, portfolios folded into Other keep their own share, and stale focus emphasis resets on scope change. A second round fixed wrapping in Not drawn notes, venue labels overlapping balances near 800px with the sidebar, and one-letter account names in small cards at 320px; a browser geometry check over four scopes at nine widths (320–1512px) found no overlap, overflow or names under 56px. A third round clamped the donut centre label after a 198-character focused name overflowed the figure; a focus check of every legend button at 320, 801 and 1512px keeps the centre inside the figure. The owner reviewed the panel on the LAN against an isolated synthetic `postgres:18-alpine` database seeded with manual accounts; the owner's database was not used.

### October 13, 2026 Overview summary

The verification follow-up restricts Activity reads to the summary's resolved enabled-account set for every scope. Portfolio and Unassigned requests no longer reconstruct unrelated accounts' history. The store filters accounts by owner, enabled status and the supplied IDs before loading fills, snapshots and order links. Empty scopes skip Activity reads, and empty sets issue no store queries. Included accounts retain their complete history and identical per-account aggregation; `/api/activity` keeps its existing all-account and single-account behaviour.

Four regression tests cover scope propagation, empty-scope short-circuiting, identical included-account operations/coverage and full summary totals beyond display limits. The PostgreSQL store case retains all 120 selected-account fills and their links while excluding other portfolios, Unassigned, disabled and foreign accounts; an empty set logs no commands. The follow-up Release build passed with zero warnings. The full backend suite passed 1,068 tests with two opt-in live tests skipped, 1,070 total, using disposable `postgres:18-alpine` on loopback port 55571. No frontend files changed.

Overview now offers All, portfolio, Unassigned and enabled-account scope, with By portfolio/By account grouping and 7/30/90-day or all-retained results. All/portfolio/30 days are the defaults. Native labelled selects, pressed-state grouping buttons and keyboard-accessible row buttons drive the view. Scope and options stay in shell memory across navigation; removed portfolios, removed/disabled accounts and disappearing Unassigned groups fall back to All after reload. The metric tiles, latest-five activity operations and saved Plays rows/counts use the same scope. The enabled-account panel remains below Summary with Accounts navigation and the existing owner-wide Refresh all action.

`backend/src/Vessel.Application/Overview/OverviewSummaryService.cs` and its new contracts implement authenticated, owner-scoped `/api/overview/summary`. Balance and availability reuse workspace account DTO definitions. The service sums exact strings without a decimal-range round trip, uses reported nonzero snapshot positions for count/unrealized P&L, and shares Activity's internal full summaries for closed-operation results before display truncation. Known zero differs from missing observations and no known closed result. UTC exit-time windows, omitted/incomplete operation counts, contributing-account counts and reasons remain explicit. No migration, persisted preference, retrospective Play, venue refresh or credential change was needed.

`frontend/src/features/workspace/OverviewSummary.tsx`, `OverviewScope.tsx`, their helpers/styles and `frontend/src/api/overview-summary.ts` hold the new UI and validated client contract. Shares are exact ratios; integer arithmetic rounds only the displayed percentage and bar. Rows are capped at 200, sorted by exact balance with nulls last, while scope totals include omitted groups. Failures offer retry without stale or invented summary amounts; narrower loading scopes do not display All balances. The common reload generation advances the summary read. See the [endpoint and coverage contract](core-workspace-contract.md#october-13-2026-scoped-overview-summary).

Verification passed frontend typecheck, lint, all 966 tests across 57 files, production build and Release .NET build with zero warnings. The full backend run passed 1,064 tests, with only the two existing opt-in live venue tests skipped, 1,066 total. All PostgreSQL tests ran against the disposable `vessel-overview-pg` PostgreSQL 18 container. This adds 24 backend unit/API cases plus one PostgreSQL endpoint case, and 52 frontend cases. Coverage includes grouping, Unassigned, enabled/owner exclusion, 400/404 scopes, exit-time boundaries, unknown and known-zero values, fees, beyond-decimal-range sums, full-history aggregation beyond display limits, scope propagation/fallback/memory, row selection, retry/cancellation, share rounding and API validation. Built-in theme contrast tests passed.

The production frontend was checked in T3's browser with synthetic responses only. Graphite desktop and Paper desktop/mobile controls rendered; 390px and 320px widths had no document overflow, and the summary table scrolls internally on mobile. Vite retains the existing non-failing chunk-size warning; bundle splitting is left to the performance plan. No owner database, root `.env`, live financial data or real venue calls were involved. No push, PR, merge or deployment was performed.

### October 12, 2026 workspace corrections

Overview now reads exact total/available balances and enabled-account coverage from `/api/overview`, across portfolios and unassigned accounts. Previously its value total was perps/manual-only and the presence of any wallet-capable venue switched the headline to wallet-only, hiding other venues. The new totals follow the [source definitions in the contract](core-workspace-contract.md#october-12-2026-combined-balances-and-overview-actions). Disabled accounts are excluded from every monetary subtotal.

The availability follow-up retains Lighter account `available_balance` and RISEx summary `free_collateral` as nullable `AvailableMarginUsd`, separate from withdrawal capacity. Unified/portfolio-margin accounts select wallet total minus held; all other accounts select reported margin availability first, then withdrawal. Standard Hyperliquid selects withdrawal even when its separate spot wallet was observed. Overview, portfolio totals and account DTOs share that selection and count known zero, never unavailable amounts. Detail names the source, and rows and portfolio tiles show availability with coverage. Existing snapshots need an explicit refresh after migration `20261012192227_AvailableMargin`; no owner database was migrated or refreshed for this follow-up.

Verification for the availability follow-up: `dotnet build backend/Vessel.slnx` passed with no warnings or errors. `dotnet test backend/Vessel.slnx` passed 964 tests with 2 opt-in live venue tests skipped, using a fresh disposable PostgreSQL 18 container matching `compose.yaml`. Frontend typecheck, lint, 807 tests across 52 files and production build passed through the pinned pnpm via Corepack. Vite retained the existing non-failing chunk-size warning. Regressions cover both adapters' absent/null/empty/zero/signed/exact/invalid availability, margin-versus-withdrawal precedence, unified-versus-standard wallet selection, disabled and unassigned accounts, portfolio coverage, beyond-decimal-precision sums, migration upgrade, owner isolation and failed-refresh retention. Parser and shell rendering tests cover the selected account amount and portfolio availability. No live owner reads, authenticated venue probes, owner migration, push or PR were performed.

The API and client already enforce no-store for owner financial data; compression is restricted to market data. Regression checks verify a second Overview HTTP read sees settings changes and refreshed shell reads update metrics. No response-cache change was needed.

Overview offers New play and an Add menu using the existing dialogs. New play uses the shared session guard, preserving unsaved draft or saved-Play edits. Accounts and Overview share sequential Refresh all. Account filters and manager selections follow the current common response, which also updates Plays capital context. The activity panels and execution contract are unchanged by this work.

Verification for this correction: `dotnet build backend/Vessel.slnx` passed without warnings; `dotnet test backend/Vessel.slnx` passed 939 cases against a disposable PostgreSQL 18 container, with only the two opt-in live venue cases skipped. Frozen-lockfile installation, frontend typecheck/lint, all 779 frontend cases and production build passed. Vite retained its non-failing large-chunk warning. The unified-wallet portfolio test and five initial balance cases failed before the fixes.

The T3 browser checked the production frontend against synthetic HTTP responses only. Graphite desktop and Paper mobile controls rendered correctly; widths 390 and 320 had no document overflow, and the Add menu supported keyboard focus with readable themed highlights. Existing theme contrast tests passed. No owner database, `.env`, real account refresh or production deployment was involved.

## Backend

Two Sol workers handled separate core and Hyperliquid scopes. The orchestrator handled contracts, frontend integration, mounted migrations and verification.

- Owner-scoped portfolio/account create, list/read routes, overview, snapshots and fills.
- Migration `20261001071844_CoreAccountPortfolio` preserves the initial foundation migration.
- Owned portfolio, snapshot, position and imported-fill tables extend existing account/play heads.
- Decimal-string DTOs and PostgreSQL numeric storage avoid two-decimal coercion.
- Composite owner foreign keys, query filters and tracked-write guards preserve isolation.
- Account-row locks serialize refreshes; snapshot replacement and fill deduplication are transactional.
- Safe Problem Details and working read-after-create resource locations.
- Provider failure preserves previous successful data.

The reader uses fixed `/info` requests for primary perpetual `meta`, `clearinghouseState` and unaggregated `userFills`. It requires only a public address. One 20-second deadline, bounded bodies/depth, exact numeric checks and safe errors apply. There is no signing or order placement.

## Explicit limits

- Primary perpetual DEX only; spot, other DEXs and contracts absent from current metadata are excluded.
- At most 2,000 fills before filtering; no complete lifetime-history or paging claim.
- Three venue reads are not an atomic snapshot, though persistence is transactional.
- No candle endpoint, persisted instrument catalogue, automated jobs, notifications, images, strategy history or full Play lifecycle.
- Correction and archival policies remain open. Repeated fill identities deduplicate.
- Raw provider JSON is parsed, not stored or returned. Normalized values and source identifiers are retained.
- Dollar-format fields are nominal. Hyperliquid collateral is USDC and quote units may differ; no FX conversion or unified-account valuation is implemented.
- Formatting changes neither stored strings nor API precision. Tiny/large values must not silently become zero or lose digits; historical dates include a year.

Official sources: [info](https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint), [perpetual schemas](https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint/perpetuals), [contract specifications](https://hyperliquid.gitbook.io/hyperliquid-docs/trading/contract-specifications).

## Local operation

Named-volume PostgreSQL is mounted and both migrations applied, on loopback port 55432. Private development settings live in the ignored root `.env` with mode 600; values are not printed or bundled.

```sh
pnpm db:up
pnpm db:migrate
pnpm dev
```

The runner gives credentials only to the API, not the frontend. API/frontend ports are loopback 5080/5180. Stopping them leaves the database volume. The LAN prototype on 5173 remains unchanged; no new firewall port was opened.

## Verification

- `pnpm check` passed 306 cases: 172 backend, 58 frontend and 76 unchanged prototype tests, with all PostgreSQL cases executed. Type checks, lint, Release build and frontend production build passed.
- Real PostgreSQL tests cover migration upgrade, ownership, concurrent deduplication and rollback. Provider tests use fixtures, not live wallets.
- HTTP checks created isolated-owner records and verified exact decimals, partial/unknown values, validation, owner isolation and the live proxy. Only the test owner's rows were removed.
- Browser layout checks used built assets in a temporary static preview with synthetic responses and a fake token. These are separate from real HTTP checks; no live browser-to-provider login is claimed.
- The temporary preview exposes neither a proxy nor private credentials and is removed afterward. No remote CI run or production deployment is claimed.
- Native fixture checks covered the empty and populated shell, account navigation and detail, plus 390px/320px navigation and internal table scrolling. An absolute screen-reader label initially widened the mobile page; positioning its scroll container fixed that overflow.

See [the shared contract](core-workspace-contract.md), [development setup](../development.md), and `backend/README.md`.

## October 24, 2026: shell header, breadcrumb trail and mode banner

Implemented the always-visible sticky header, Shell-memory route history,
accessible collapsed trails, Wallet Settings-tab locations, and the generic
mode-notice slot. Paper's existing disclosure now lives in that header, without
per-item Paper markers. Account status is Enabled/Disabled; simulated-result,
execution-source, sizing-source, immutable-capital and capability explanations
remain. The sidebar Trading mode select, switching behaviour, approved page
headings/layout, backend, `prototype/` and `AGENTS.md` were not changed.

Verification from the repository root:

- `pnpm install --frozen-lockfile` — pass; lockfile unchanged.
- `pnpm --dir frontend typecheck` — pass.
- `pnpm --dir frontend lint` — pass, **0 warnings**.
- `pnpm --dir frontend test` — **168 files, 4,056 tests passed**. Fourteen new
  tests cover top-level resets, retained account filters, detail/list Back,
  Review/account/notification drill-downs, earlier-crumb truncation, collapsed
  history, current-location accessibility, draft titles, cancelled/confirmed
  Play restoration (including hidden-editor retries), background save completion,
  hash routing and mode-level versus per-item Paper context.
  Existing identity tests also verify restoring Wallet Settings tabs.
- `pnpm --dir frontend build` — pass.
- T3's headless browser, using a temporary synthetic-only bootstrap of the
  production shell: **320, 390, 800, 1024 and 1440px**, Real and Paper, Graphite
  plus **Paper, Daylight and Sand** themes. The document width matched the
  viewport at every checked size; no header child extended past the viewport.
  Long account/Play labels truncate, the narrow trail collapses, and the hidden
  locations open without page overflow. Real has no mode strip; its bell is
  visible at ≤800px and hidden on desktop. The header retained `top: 0` after
  scrolling and its 10px backdrop blur. The synthetic bootstrap/server were
  removed/stopped afterward; no owner API, database or live venue was used.
- Rendered warning/text contrast in Graphite and those three light themes
  exceeded **4.5:1** on the notice's tinted surface (minimum measured warning
  5.26:1, body text 5.71:1); the full suite's built-in-theme contrast checks
  also pass.

The notice is a single line at 1440px. It wraps at 1024px as well as on mobile,
deliberately preserving all disclosure text and the minimum type scale instead
of clipping or creating horizontal overflow. Browser/hash navigation still
retains the existing Play editor and its mutation owner; only an explicit
All plays/page crumb shows the list. Cancelling a breadcrumb's discard prompt
keeps the page, hash, trail and unsaved edits, and a confirmed restore waits for
the guarded load before replacing them.

No backend checks, migration, deployment, release, push or pull request are
claimed by this frontend-only verification.

## October 24, 2026: trading mode lockup

Implemented the owner's choice, option 5 of `docs/design/trading-mode-switch/`:
the sidebar "Trading mode" select became the brand subtitle button (Real
journal / Paper playground), with a round swap button in the collapsed rail,
the subtitle in the narrow sheet, a decorative "Paper" header mark on narrow
screens, the paper-boat mark, the derived Paper tone (`--paper-*`) on the tile,
sidebar edge rail, contours and Paper strip, an alert dialog instead of
`window.confirm`, focus return and a polite announcement that survives the
per-mode remount. See the trading mode lockup subsection of the
[contract](core-workspace-contract.md#trading-mode-lockup). Backend,
`prototype/` and `AGENTS.md` were not changed.

Verification from the repository root:

- `pnpm install --frozen-lockfile` — pass; lockfile unchanged.
- `pnpm --dir frontend typecheck` — pass.
- `pnpm --dir frontend lint` — pass, **0 warnings**.
- `pnpm --dir frontend test` — **168 files, 4,076 tests passed**. The Paper
  workspace tests find the control only through one `switchTradingMode`
  helper; ten new tests cover both accessible names and the sibling (not
  nested) placement, the plain subtitle with no control when no venue
  simulates executions (rail included), the rail swap button and a kept rail
  across a switch, focus on the new control or the menu button, the live
  region surviving the remount, no mark animation on load or reload, Keep
  editing/Escape/outside clicks changing nothing and returning focus,
  Discard and switch, and the confirm-time guard re-check both ways (finished
  work still switches; a replaced connection switches nothing). Ten more
  check `--paper-ink` on the page, navigation and Paper strip for every
  built-in theme. Removing the re-check, the sheet's Escape guard or either
  focus step makes these tests fail.
- `pnpm --dir frontend build` — pass.
- Headless Chromium (Playwright 1.60 headless shell) against a temporary,
  uncommitted bootstrap of the real `ApplicationShell` with an in-memory
  synthetic API (invented accounts, no network, owner API, database or
  venue): **485 scripted checks passed, 0 failed** at **320, 390, 800, 1024
  and 1440px** in **Graphite, Daylight, Paper and Sand**, Real and Paper,
  expanded and collapsed (desktop) or sheet (narrow). No page, header or
  sidebar overflow; at 320px the breadcrumb keeps 185px beside the header
  mark. Keyboard switches leave a visible focus ring on the new control,
  the rail button or the menu button; the rail stays collapsed after a
  switch. The confirmation focuses Keep editing, shows the dashed pending
  outline, fits the viewport, returns focus on Escape and keeps the sheet
  open on narrow screens. With motion allowed the new mark runs
  `mode-mark-rise` and the old one `mode-mark-sink` after a switch, never on
  load or reload; under reduced motion the leaving mark is not displayed.
  No console errors. The bootstrap and server were removed afterwards.
- Rendered `--paper-ink` contrast (live tokens): Graphite 11.78 on the page,
  11.30 on the navigation, 10.24 on its top wash, 9.91 on hover and 9.85 on
  the Paper strip; Daylight 6.35 / 6.70 / 6.24 / 6.10 / 5.59; Paper 6.19 /
  6.52 / 6.07 / 6.00 / 5.45; Sand 6.05 / 6.43 / 5.99 / 5.78 / 5.34. Secondary
  text on the Paper strip is 5.62–7.49:1 across those themes.

Captures (`implemented-*.png` in the design folder) use the same synthetic
bootstrap. No backend checks, migration, deployment, release, push or pull
request are claimed.
