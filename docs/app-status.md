# Vessel today

Vessel is a self-hosted trading diary with separate journals per user. It connects decisions,
execution facts and later reflection. It does not place orders or offer trading
advice. A chart reaching a price never proves that an order filled.

## Which version this describes

This is the October 18, 2026 multi-user stack, rebased onto integrated D1 `a336bb3`
(A1 identity core, B1 scopes/events, A2 identity accounts, B2 monitor/limits,
C1 isolation and D1 frontend) on main `43ebaa0`, including Notification Hub N1–N4,
order identity and the October 17 backend pass, plus P4 operator/developer documentation and
isolated release/browser verification. See the
[delivery state and exact evidence](architecture/multi-user-state.md).
These are checkout/verification states, not a claim that a branch has merged,
a release has been published or an installation has been deployed/migrated.
The D1–D12 defaults are adopted recommendations and remain owner-overridable.
October 25, 2026: the owner removed Token sign-in; wallet sign-in is the only
way in, so installations must bind the owner's wallet before upgrading.
§10 is still **not signed off**: the real-wallet EOA/non-mainnet compatibility
check remains owner-pending;
Firefox **146.0.1 Wallet writes PASS** through the official Playwright 1.58.2
container ([exact command](architecture/multi-user-state.md#manual-wallet-pre-release-step-not-a-ci-gate)).
Keep registration `Closed` until both pass and are recorded there.

The manual diary, retrospective workflows and background monitor are present.
Older discovery documents retain historical limits, not the current inventory.

Start with the [four diagrams](diagrams/app-status.md). For backend internals,
continue to the [backend guide](architecture/backend-guide.md). The
[glossary](../CONTEXT.md) defines Play, plan, execution and review.

## Find your way around

Sign in over HTTPS with an Ethereum wallet: choose an EIP-6963 browser wallet,
sign the displayed EOA message and use an invitation to create a journal when
required. There is no API token. This is
not a venue login, transaction or approval; the message fixes Chain ID 1 without
requiring a network switch. WalletConnect/smart-contract wallets are absent.
Profile & sign-in manages display name, linked recovery wallets and sessions;
link/removal uses a fresh step-up, and the last wallet cannot be removed.
Sessions have server expiry/revocation and forced teardown; users never inherit
another user's journal or preferences. Operators administer identity offline,
not journals through an authorization bypass. There is no user/journal deletion.

| Screen | What works here |
| --- | --- |
| Overview | Scope balances, availability, recent imported operations and saved Plays to all accounts, a portfolio, Unassigned or one account. Group the Summary by portfolio or account and choose a realized-result period. |
| Plays | Save ideas and plans, edit entries and exits, inspect executions, keep chart evidence, and write or amend a review. Filter the list by progress or review state. |
| Review | Inspect eligible saved closed-Play results by rolling period, portfolio, account or asset. Open contributing Plays and drill into exclusion reasons. |
| Portfolios | Create optional groups with a name and built-in icon. Add, move or unlink accounts, including bulk membership changes. |
| Accounts | Add manual or supported venue accounts, inspect balances and positions, refresh imports, manage credentials where needed, or disable an account. |
| Activity | Inspect retained imported executions grouped into position operations. Expand the original facts, filter them, and preview an eligible operation for retrospective creation. |
| Notifications | Read the current user's retained notifications, filter the feed and mark rows read; the unread badge and popups share the same owner-scoped delivery state. |
| Settings | Choose dark or light themes, customize colours and radius, or generate and save a named palette in this browser. Inspect connection and capability information. |

One ordinary journey is Accounts, Refresh, Activity, then Plays or Review.
Another starts with a new Play and records its reasoning before execution.
Portfolios are optional. Unassigned means no portfolio, not a default portfolio.

Disabling an account retains its imports but hides its financial activity and
blocks refresh. Deleting a portfolio unlinks its accounts. Account deletion
requires confirmation and is blocked by linked Plays.

See the [screen workflow](diagrams/app-status.md#screens-and-owner-workflow),
[shell implementation](../frontend/src/features/workspace/ApplicationShell.tsx)
and [account-management contract](architecture/account-management-contract.md).

## What a Play contains

The desktop workspace keeps capital context above the work area, with a chart
on the left, the journal beneath it, a bounded position editor on the right,
and a position Summary below. Entry details can expand without losing edits.
Themes change colours and radius, not this layout.

- A plan has direction, whole-number leverage, sizing, entry quantity shares,
  multiple stops and targets, thesis, invalidation and free-text strategy notes.
- The chart has candles, drawings, plan/range tools, configurable EMAs, Volume
  and RSI. Manual instruments keep a placeholder instead of invented prices.
- Captures and uploaded images keep notes and editable markup beside the
  untouched original image.
- Suggestions offer size, stop, target and leverage estimates with reasoning
  and individual Accept actions. Nothing is applied automatically.
- The bottom Summary estimates the whole planned position and shows allocation
  and price/exit coverage. Partial coverage is not silently scaled to 100%.

Planned money estimates exclude fees, funding and slippage. An estimated loss at
a stop is not a guaranteed maximum loss.

Status, execution progress and review are separate. A saved Draft becomes
Planned when the owner commits to it; its account and instrument become fixed.
Later plan changes require a reason and append a revision. Recorded executions
can move it through Open to Closed.
Closed and Cancelled are final; review and evidence can still change.

Structured review records adherence, a mistake tag, a lesson and a next action
alongside free text. Review amendments keep history. The Strategy tab is a note,
not an implemented strategy library.

The [lifecycle diagram](diagrams/app-status.md#play-lifecycle) and
[diary contract](architecture/diary-completion-contract.md) explain the terminal
rules. [PlayWorkspace](../frontend/src/features/plays/PlayWorkspace.tsx) assembles
the current layout; [chart-plan](architecture/chart-plan.md) records its tools.

## Keep three kinds of evidence separate

| Kind | Where it comes from | What it means |
| --- | --- | --- |
| Imported execution | A read-only venue response retained with source identity | The venue reported a fill. It does not supply a thesis or prove which Play the owner intended. |
| Manual execution | An explicit owner entry or exit on a saved manual-account Play | The owner recorded an execution, with time, side, quantity, price, source and known or unknown fees. It is not venue-confirmed. |
| Market observation | Candles, market context or the live chart stream | A price was observed. It does not create fills, order links or execution-driven status changes. |

Imported orders link to Play levels automatically only when the match is
unambiguous. Ambiguity becomes a suggestion for the owner. Fills reach a Play
through those order links, never merely because the account and asset match.
Several active Plays can share an account and instrument.

Manual executions use a chronological average-cost ledger for linear USD-quoted
positions. Corrections require a reason and retain before/after values.
They can drive Planned to Open to Closed, but cannot reopen a Closed Play.
They do not enter the imported Activity feed or alter reported account balances.

Activity operations are calculated round trips, not inferred Plays. The owner
can preview and confirm a complete, closed, unassigned operation as a
retrospective Play. Creation rechecks the facts under a lock and links whole
orders atomically. Split/flip allocations, overlapping associations and missing
order executions can make an operation ineligible.

A retrospective Play starts Closed. It has no invented pre-trade thesis,
leverage or initial-risk R. Eligible net results can enter Review, but these
Plays are excluded from the sizing ladder. Eligible closed manual Plays can
contribute to Review and sizing, with manual provenance and cost checks.

[Data-flow diagram](diagrams/app-status.md#facts-intent-and-reports) ·
[DiaryService](../backend/src/Vessel.Application/Plays/DiaryService.cs) ·
[Activity rules](architecture/activity-operations.md) ·
[Review rules](architecture/portfolio-review.md) ·
[SizingService](../backend/src/Vessel.Application/Sizing/SizingService.cs).

## Venue support is deliberately uneven

All integrated instruments are perpetuals. Browser controls follow the API's
venue capabilities rather than assuming every venue provides the same data.

| Venue | Account identity and access | Account/execution reads | Chart |
| --- | --- | --- | --- |
| Hyperliquid | Public EVM address; no venue credential | Primary-perpetual snapshot, bounded recent fills/orders, supported stablecoin wallet balances | Candles, market context and live updates |
| RISEx | Public EVM address; no venue credential | Snapshot and bounded recent fills/orders; all supported market categories retain their category tag | Candles and market context; manual refresh, no stream |
| Lighter | A separate Vessel account for each selected account index; wallet discovery lists main/subaccounts | Public account/fill reads; orders require a verified read-only `ro:` token | Candles with manual refresh; no market context or stream |
| Manual | Owner-entered account and instrument | Optional account value and explicit Play executions; no venue imports | Placeholder; uploaded evidence remains usable |

Lighter history excludes fee-bearing or unverified-fee fills and fills without
reported PnL, with a coverage notice. Importing an account does not refresh its
history automatically. A missing order token does not erase successful public
balance/fill reads, but order completeness and linking remain limited.

Canonical instrument keys preserve multipliers, such as `1000PEPE`, while native
venue contract IDs remain available for matching and trade links.
This is not spot trading, EVM wallet valuation or guaranteed full-history import.

See [venue registration](../backend/src/Vessel.Infrastructure/DependencyInjection.cs),
[Lighter delivery](architecture/lighter-delivery-contract.md),
[RISEx scope](architecture/risex-integration.md) and
[stablecoin scope](architecture/stablecoin-wallet.md).

## What is saved, and where

| Location | Contents and lifetime |
| --- | --- |
| PostgreSQL | Accounts, portfolios, latest snapshots, retained fills/orders, saved Plays, plan/status/review history, order links, manual executions/corrections, diary retry records and sizing settings. Evidence notes/markup and encrypted venue credentials also live here. The separate identity schema stores users, wallets, hashed sessions, challenges and invitations; events, owner progress/receipts and server monitor state live in the journal schema. |
| Private evidence object store | Original saved-Play image bytes behind the owner-scoped API. Local remains the no-settings default; explicit S3 opts into private S3-compatible storage (bundled RustFS in Compose). |
| Server configuration | Allowed sign-in origins, registration policy and credential-encryption key ring. The key ring is outside the database and never sent to Vite or the browser. |
| Browser memory | User/session metadata (never the HttpOnly cookie value), unsaved edits/drawings/images, shell filters and scope, and the session candle cache. Navigation retains the draft; reload or sign-out discards unsaved work. |
| Browser `localStorage` | User-scoped appearance/chart preferences and device theme/last-provider convenience keys, including saved palettes and indicator settings. No token, execution history, candle history or Play draft. |
| Browser cookie jar | Host-only HttpOnly session/binding cookies, server-bounded expiry and revocation; remembered sessions can persist. No wallet private key. |
| Server memory | Bounded public catalogue, candle and market-context caches, plus active market-stream subscriptions. These are not a durable candle archive. |

Save writes the Play and then synchronizes its images. These are separate
requests, so a saved Play can still have an image upload pending after failure.
Check the save result before treating evidence as durable. Retrying keeps
successful image work instead of claiming the whole save was atomic.

The [runtime diagram](diagrams/app-status.md#runtime-and-trust-boundaries),
[database context](../backend/src/Vessel.Persistence/VesselDbContext.cs) and
[browser save path](../frontend/src/features/plays/saved.ts) show these boundaries.

## What starts work

| Trigger | Current behaviour |
| --- | --- |
| Add/import account | Creates account records. Discovery is read-only; there is no automatic history refresh. |
| Refresh account / Refresh all | Reads bounded venue state and fills, then retains available orders. Refresh all runs sequentially with progress and one result summary. This path does not assign orders to Plays. |
| Open a tracked Play / Check execution | Checks venue orders and fills, reconciles links and derives status. While that Play editor is active, the browser repeats checks every 60 seconds when the tab is visible. |
| Open/switch/refresh chart | Loads or revalidates bounded candle windows. Manual Refresh bypasses reusable cached data. |
| Live chart enabled | Hyperliquid data flows through one shared backend WebSocket and authenticated server-sent events. Reconnects can fall back to 15-second market-data polling. Hidden tabs pause the stream. |
| Save, record/correct execution, confirm retrospective, review | Explicit owner writes. Version checks or preview fingerprints reject stale changes rather than silently overwriting them. |
| Open Overview, Activity or Review | Reads retained facts and derives reports. Reporting does not sync a venue, assign executions or change a Play's status. |

The market stream and execution polling are separate. Neither is an
always-running background Play monitor. Closing the browser stops its polling.
See [execution polling](../frontend/src/features/plays/usePlayExecution.ts),
[live market handling](../frontend/src/features/market/useLiveMarket.ts) and
[cache rules](architecture/performance-plan.md).

## Read the numbers with their limits

Owner identity comes from API authentication, not request-body claims. Database
queries and writes enforce that owner; normal financial reports exclude disabled
accounts. The browser talks to Vessel, not directly to venue APIs.

Financial API values are exact decimal strings; unknown values remain null.
Derived estimates can have documented rounding. Coverage notices explain missing
facts and currency limits. A missing fee is not a zero fee, and reported net PnL
is not reduced by its fee a second time.

Balances describe account observations. Activity describes retained imported
operations. Review describes eligible saved closed Plays, using actual exits
and return on executed entry notional, not return on margin or account equity.
Do not add wallet and perpetual ledgers blindly, infer missing initial risk,
or assume RISEx reported PnL excludes funding.

## What is still absent here

| Not implemented in this checkout | What exists instead |
| --- | --- |
| External notification channels | The production in-app Hub, per-owner Notifications page, unread count and server-claimed popups exist. Telegram, Discord, browser system notifications and price alerts remain out of scope. |
| Strategy library, reusable strategy definitions and versions | Free-text strategy notes plus disposable discovery prototypes. |
| Durable candle archive, backtesting and complete historical backfill | Bounded imports and memory caches. TimescaleDB is a direction, not a running component. |
| Automatic evidence-storage migration | Local and private S3-compatible adapters exist; cutover is explicit and offline. |
| WalletConnect, smart-contract wallet auth, user deletion or order execution | EOA onboarding, isolated journals and read-only venue access exist. No venue signing or trading. |

Wallet sign-in is delivered against the [multi-user contract](architecture/multi-user-delivery-contract.md),
with [verification/activation evidence](architecture/multi-user-state.md). The
original [analysis](architecture/multi-user-wallet-auth.md) and
[design](design/wallet-sign-in/README.md) are historical context; adopted D1–D12
defaults remain owner-overridable. Wallet sign-in is the only sign-in since
October 25, 2026, but §10 is not signed off: Firefox 146.0.1 Wallet writes
passed, and the real-wallet/non-mainnet EOA check is still owner-pending. Both must pass and be recorded
in `multi-user-state.md` before registration leaves `Closed`.

The [backend review](architecture/backend-review.md) and
[strategy plan](architecture/strategies-plan.md) describe further work, not
additional running features. [Release packaging](../deploy/README.md) documents
images, HTTPS and explicit migrations; it is not evidence of a deployed version.
