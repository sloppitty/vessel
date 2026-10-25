# Vessel

A trading diary for recording decisions, executions, and reflection.

## Deploy with Docker

See the [Docker deployment guide](deploy/README.md) for the v0.3.3 API and web
images, Compose setup, explicit migrations, opt-in Caddy TLS, credential keys,
backups and isolated restores.
Both images use `skorcius/vessel` with `api-` and `web-` tags.
Read the [v0.3.3 release notes](docs/releases/v0.3.3.md) before upgrading.
The order-identity migration has no retained-data upgrade path when imported
orders, fills or Play order links exist. Sign-in is by Ethereum wallet only
and needs HTTPS; an installation that used the API token must bind the owner's
wallet before upgrading (see the deployment guide). Real-wallet acceptance is
still pending.

This repository contains the approved Graphite prototype and the React/Vite/TypeScript plus modular ASP.NET Core 10/PostgreSQL application. The default page is Overview. The app includes account/portfolio management, saved Plays and bounded read-only Hyperliquid, Lighter, Lighter · Robinhood and RISEx integration. The disposable prototype is separate from the application.

- [App today: current capabilities and diagrams](docs/app-status.md)
- [Project brief](docs/project-brief.md)
- [Working glossary](CONTEXT.md)
- [Original sketches](docs/references/)
- [First visual direction](docs/design/concept-01.md)
- [Approved baseline and implementation handoff](docs/design/approved-baseline.md)
- [Prototype scope and LAN operation](docs/prototype.md)
- [Frontend guide for newcomers](docs/architecture/frontend-guide.md) and [frontend review and improvement backlog](docs/architecture/frontend-review.md)
- [Backend guide for newcomers](docs/architecture/backend-guide.md) and [backend review and improvement backlog](docs/architecture/backend-review.md)
- [Stack and architecture discussion draft](docs/architecture/proposal.md)
- [Runtime and persistence research](docs/architecture/runtime-persistence-research.md)
- [MVP alignment, monitoring and ownership](docs/architecture/mvp-alignment.md)
- [Foundation implementation state](docs/architecture/foundation-state.md)
- [Development setup and checks](docs/development.md)
- [Core workspace contract](docs/architecture/core-workspace-contract.md)
- [Current core implementation and limits](docs/architecture/core-workspace-state.md)
- [Account and portfolio management](docs/architecture/account-management-state.md)
- [Hyperliquid stablecoin wallet](docs/architecture/stablecoin-wallet.md)
- [First-part PR review safeguards](docs/architecture/pr-review-fixes.md)
- [Venue credentials](docs/architecture/venue-credentials.md) and [Lighter integration state](docs/architecture/lighter-state.md)
- [Play lifecycle](docs/architecture/play-lifecycle.md), [chart plan](docs/architecture/chart-plan.md) and [portfolio review](docs/architecture/portfolio-review.md)
- [Play monitor plan](docs/architecture/play-monitor-plan.md), [messaging, events and workers reference](docs/architecture/messaging-and-workers.md) and [architecture overview page](docs/architecture/play-monitor-overview.html)

The owner authorized foundation scaffolding after confirming perpetuals-first across venues and multiple active plays on the same account/instrument. Frontend and backend work were delegated to separate Sol agents. The confirmed MVP remains self-hosted and single-user with token access, Hyperliquid first and Lighter next, manual accounts, optional history import and in-page notifications.

The app implements protected portfolio/account APIs, exact manual values, latest snapshots and retained fills, read-only Hyperliquid, RISEx and Lighter venue modules (Lighter order tracking through an encrypted read-only token), explicit migrations and owner isolation. Navigation covers Overview, Plays, Review, Portfolios, Accounts, Activity and Settings. Plays are saved with revisions and lifecycle status, link imported orders and fills, and carry venue candle charts with drawings and indicators plus evidence images. Review scores closed Plays from linked executions. Complete backfills, background jobs and notification delivery remain later work. The approved prototype is unchanged.

Portfolios and accounts support rename/delete. Accounts can be moved, unlinked to the virtual unassigned group, or disabled without deleting imported data. Unassigned records appear only in All accounts, not a default tile. Portfolio deletion unlinks accounts; account deletion explicitly removes retained facts and is blocked when a Play references the account.

Hyperliquid refresh also retrieves supported HyperCore wallet stablecoins with total/held/available amounts: USDC, USDE, USDT0 and USDH. Wallet availability is separate from primary perpetual equity; ledgers are not summed as full account equity. Non-stable/EVM/lending values and FX/depeg adjustment are excluded.

## Application quick start

Always use **pnpm**, pinned in root `package.json`. Install it with Corepack if needed, then:

```sh
corepack enable pnpm
corepack install
pnpm install --frozen-lockfile
```

Configure private values in the ignored root `.env` as described in [development setup](docs/development.md).

Saved images default to the original local folder when nothing is configured.
Opt into private S3-compatible storage with `Vessel__Evidence__Storage__Provider=S3`
and a pre-created bucket plus server-only credentials (RustFS recommended).
The [deployment examples](deploy/README.md) explicitly select and provision RustFS;
[offline copy-and-verify commands](docs/architecture/evidence-storage.md#s3-to-local-or-another-s3-provider)
copy images in either direction. Export to a local folder to switch S3 providers
without deleting originals, rewriting database records or changing configuration automatically.

Then start development:

```sh
pnpm db:up
pnpm broker:up
pnpm db:migrate
pnpm dev
```

The runner starts the API, monitor worker and frontend. Only the API and worker receive server credentials. API uses loopback 5080, frontend 5180, PostgreSQL 55432 and RabbitMQ 55672 (management 55673). `db:migrate` applies EF migrations and provisions both messaging schemas and broker bindings explicitly. Stop the runner with Ctrl+C; database and broker volumes remain. These processes do not replace the existing LAN prototype.

The owner-requested actual-app LAN review is currently **http://10.1.0.219:5180/**, backed by the real API and migrated local database. It uses a separate development token delivered to the owner, not a credential embedded here. See [LAN operation and stop/recreate instructions](docs/development.md#current-lan-review). This is a restricted HTTP development preview, not an internet/production deployment.

The Plays first pass is implemented in the working branch, but this change does not update that existing LAN service.

```sh
pnpm check
```

Set `Vessel_TEST_POSTGRES` for real PostgreSQL test execution; otherwise 40 database tests explicitly skip. The prior backend checkpoint ran database tests without skips. The frontend-only Plays verification reports these 40 skips explicitly; see [its verification record](docs/architecture/plays-workspace-contract.md).

## View the prototype

On the current server's LAN: **http://10.1.0.219:5173/**

Compare all ten dark designs: **http://10.1.0.219:5173/designs.html**

Graphite is the default, including when an old design-study preference exists. Direct variants use `/?theme=graphite`, `carbon`, `midnight`, `slate`, `forest`, `petrol`, `olive`, `espresso`, `aubergine`, or `stone`. The Design dropdown switches skins without resetting unsaved play data. Configurable themes are an accepted future capability.

The side-by-side layout is locked. The journal and bounded scrolling editor finish together above the summary. The gallery images preserve the earlier visual studies; open the live workspace for the approved geometry.

To run on another machine, from the repository root:

```sh
python3 -m http.server 5173 --bind 127.0.0.1 --directory prototype
```

Open `http://localhost:5173/`. No installation or build step is needed. HTML, CSS, JavaScript, an SVG chart, and a bundled font are prototype tools, not a production stack decision.

## What to try

- Compare the ten dark skins on the same locked side-by-side layout.
- Define entries and partial targets using absolute prices or entry-relative percentages.
- Set one full-position margin or quantity, then give each entry a percentage of that total quantity.
- Adjust leverage with the linked slider and numeric input, from 1× to 100×.
- Read the account-derived budget, use its pencil to edit, then save or cancel that override. The budget is locked again afterward.
- Choose Manual journal to try an unavailable account balance. The budget is optional and entered-position calculations still work without it.
- Compare account and portfolio values with both margin and leveraged exposure percentages.
- Set the available budget in the position panel, choose a risk target in the sizing assistant, and explicitly apply its suggestion if useful.
- Choose Aggregate or an individual entry from the chart dropdown. Click an entry's plotted label, legend item or editor header to focus it.
- Click anywhere on an entry-header background or its collapsed summary to open the corresponding details. Share inputs and remove buttons keep their own actions.
- Expand the selected entry into a large editable popup, switch entries there, and return with Close or Escape.
- Expand the chart into a large in-page popup and return with Escape. Browser fullscreen is not required.
- Read the bottom summary's modeled profit at targets and loss at stops from your entered position.
- Draw lines, capture the chart, and add free-text context to each capture.
- Switch between thesis, sample strategy versions, evidence, and review.
- Save a draft in this browser, or export it as JSON.

All accounts, portfolio values, prices, balances, and strategies are examples. No broker connection or orders. Portfolio value sums the selected portfolio's sample USD accounts. Margin share and notional exposure share have separate labels; exposure is not capped at 100%. Editing the available budget does not alter account or portfolio values.

The leverage and margin calculations are simplified; fees, funding, slippage, maintenance margin, liquidation, and venue-specific contract rules are excluded. The 1×–100× control is not a claim about any broker's supported range.

Save draft stores one play, its captures, and capture notes in this browser and origin only. It is not a durable journal or cross-device storage. Export JSON includes the images and notes; a capture can also be downloaded as a PNG with its notes below it.

## Check the model and theme palettes

```sh
node --test prototype/*.test.mjs
```

The tests cover 62 model cases and 14 theme/palette cases. The browser smoke runner checks unified selection, expanded entry edits, persistence, aligned column bottoms, bounded overflow, theme geometry, budget controls and evidence notes:

```js
await (await import('/tests/browser-smoke.mjs')).runSmokeChecks()
```

Run that command in an isolated tab on the prototype origin. It deliberately exercises sample data and restores the tab's existing stored draft afterward. Refresh the tab when finished. Browser checks are not a full accessibility audit.
