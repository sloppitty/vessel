# Changelog

## Unreleased

### Added

- Recording a manual execution is quicker and clearer: each error sits on
  its own field, the time is typed in your zone with a Now button and sent
  as UTC, the side follows the role, and the Source stays required beside the
  time (F-01).
- Duplicate opens a planned Play's plan as a new Draft, with its account,
  instrument, direction, leverage, levels and notes, checked like any new plan
  before its first Save (F-04).
- Orders to confirm are listed once each with every level they match, and say
  "another play" only when another active Play is offered the same order
  (F-03).
- A Play's fills appear on its chart as buy and sell markers at their time
  and price, with a tooltip, never as a level line. Closed, unresolved and
  retrospective Plays open the chart on the trade's own dates instead of
  today (G-03, F-06).
- Review shows process and outcome as cases: how the period's closed,
  cancelled and unresolved Plays were answered (followed, deviated, not
  applicable, unanswered) beside how they turned out, with retrospective and
  unscored Plays in their own lanes and each mistake tag counted. Every count
  opens exactly its Plays; a share appears only once its own count reaches 10.
  The breakdown adds Origin and Direction, whose rates also wait for 10, the
  pre-flight counts open their Plays, and the review form suggests your
  earlier mistake tags (G-01). Cancelled Plays now appear with the exclusions,
  with their cancel reason, and count in the excluded total.
- Several manual executions can be corrected together with one reason: a
  closed 1-in/1-out ledger can become 2-in/2-out in one step. The corrections
  apply all or nothing, only the final ledger has to be valid, and the audit
  history groups them (D-15). New migration `ManualCorrectionBatch`; apply it
  explicitly.
- An open Play whose venue executions can never settle it (a shared close, a
  flip, a forced exit that was not retained) can be declared Unresolved, with
  a reason and a note. Vessel keeps a snapshot of its execution facts, gives
  it no result and never sets it on its own. It is reviewed like a closed
  Play, Review lists it among the excluded with its declaration, and sizing
  shows how many are not counted. It takes no new orders until returned to
  Open; if fills on its linked orders settle it later, it closes as usual
  (D-06). New migration `UnresolvedStatus`; apply it explicitly. Rolling it
  back is refused once an Unresolved Play exists.
- A review is complete only when you say so. Complete review works on a
  closed or cancelled Play with or without new text, saving changed review
  answers completes it too, and Review again reopens it; a cancellation that
  never filled can be marked "No review needed" in one step. Writing in the
  Review box no longer moves a Play to Reviewed, so a note written while it
  was open waits in Needs review after it closes. History names each step,
  and Review counts completed reviews. The journal gains an append-only Log
  for timestamped notes during the trade, each with an optional state (calm,
  rushed, FOMO, tired, after a loss); log entries never count as a review
  (D-14, G-04). New migration `ReviewCompletion`; apply it explicitly. It
  keeps every Play the Reviewed tab listed before as reviewed.
- Planning brings your own lessons back: the Thesis tab lists your recent next
  actions with their source Plays, and Copy into plan adds one as an if-then
  you complete. Plan it first shows a short checklist (priced stops, shares,
  thesis and invalidation, risk within your setting at the ladder level,
  liquidation beyond the stops). It never blocks, keeps the checklist with the
  plan and is counted in Review (G-02, G-05). New migration
  `PlanPreflight`; apply it explicitly.
- A Play whose plan was written after its first execution is marked "Plan
  written after first execution" in its header, above its review and in
  Review's lists. It is still scored and sized like any other (D-17). Review
  names each metric's own sample size: 20 scratches and one win reads
  "n = 1 decided · Small sample" rather than a confident 100% batting
  average. The R average is labelled "Estimated R expectancy (planned-stop
  model)" (F-08).
- A Play shows what happened beside its plan. Each entry shows its average
  fill against the planned price and its filled against planned quantity,
  from the plan in force at its first fill. The Review tab opens with the
  result on both bases, fees, estimated R over its initial risk (or why it is
  unknown, by Review's own rule) and time held. The journal and its expanded
  view gain Save play, and a retrospective Play's panel lists its trade facts
  (G-03, F-06).
- Once an entry has filled, its stops can be moved to break-even or into
  profit with a reason; before any fill, a stop at the entry, `0 %`
  included, is refused. The Summary shows the signed result if every stop is
  hit ("+5 USDC locked in" or "−5 USDC at risk") from the average fill, and
  initial R stays frozen at the first fill. A drifted linked stop or target
  can be rebound to the order resting at the venue in one version-checked
  step (D-03, F-10).
- Each closed Play shows a Play-local result from its own fills beside the
  scored venue result, with basis labels and a "Sign differs by basis" flag
  in the Execution panel and Review; Review and sizing count sign differences
  and fee-only losses. R is labelled "Estimated R (planned-stop model)". The
  scored basis is unchanged (D-01, F-09, I-04, I-05).
- Review can include closed imported positions that belong to no Play. An
  "Include imported positions" switch is automatic by default: positions are
  included while the selection has fewer scored Plays than a threshold (5 by
  default, set in Settings → Review, stored in the browser). A Play weighs
  twice a position in batting average, average gain and loss, payoff and
  expectancy; net P&L, profit factor, drawdown, streaks and counts are not
  weighted. Positions have no plan, R or review and never become Plays.

### Fixed

- Plan numbers follow one grammar everywhere: the server refuses exponent,
  signed and trailing-dot text on write, naming the field, while unchanged
  stored text still saves; the browser never writes exponent text; a negative
  percentage stop is refused instead of saving as no stop (D-03).
- Sizing suggestions tell the truth about their inputs. A size is suggested
  only when every part of the position has a priced stop. Nothing offers
  Accept while the record is stale or failed. The budget is the account's
  normalized available balance with its source and time, and a known zero
  stays zero. The Summary's risk uses the account balance. Size and leverage
  are no longer suggested after an entry fills. Lighter liquidations are no
  longer counted as fee-bearing (D-04, D-07, D-11, D-12, D-13, D-24, D-25,
  F-16).
- The monitor records each Open Play's stop coverage (known, shared,
  unknown with a reason, unsupported, or stale) and never says "Monitored"
  unless every Play is covered. Chart stops say what the venue evidence shows
  ("resting as of … UTC", "not seen at the venue", "unknown"). RISEx, whose
  current order book Vessel cannot read, is labelled "current book not read"
  instead of being permanently degraded (D-08, D-09, D-18). New migration
  `MonitorCoverage`; apply it explicitly.
- A manual Refresh now records fill continuity like a scheduled check, so a
  run of Refresh-only use no longer leaves a permanent false history gap that
  switched off missing- and short-stop checks. Refresh can set a gap but never
  clears one (D-02).
- Open monitor incidents stay visible: a count on the Plays navigation entry,
  Plays rows and the Play header, a conditional row on Overview, and a monitor
  heading that never says "Monitored" beside an open record. Discrepancies
  read as plain facts (open, stop and uncovered quantity, last observed) with
  one next action; the recorded JSON moves behind a disclosure (D-08, F-02).
- Sizing orders closed Plays by when they actually closed, their latest linked
  exit fill, instead of when a check or Refresh noticed. Several results found
  by one Refresh no longer get a ladder level that depends on Play IDs; results
  that share an exit time are disclosed (D-05). Historical ladder levels can
  change on upgrade.
- A saved Play opens even when an evidence image cannot be read. Originals load
  one by one after the Play opens; an unavailable image offers Retry, a missing
  one says so, and saving never deletes or re-uploads an image that has not
  loaded (D-10).

## 0.3.2 - October 19, 2026

Release notes: [docs/releases/v0.3.2.md](docs/releases/v0.3.2.md).

### Fixed

- The Review account selector keeps its shared border, field background and
  typography instead of inheriting the page button reset.

### Changed

- GitHub Actions runs checks and publication only for release tags; branch
  pushes and pull requests use local checks without starting CI runners.

### Upgrade

- No new database migrations or configuration changes from v0.3.1. Use matching
  v0.3.2 API and web images; earlier upgrade requirements still apply.

## 0.3.1 - October 19, 2026

Release notes: [docs/releases/v0.3.1.md](docs/releases/v0.3.1.md).

### Added

- Activity operations show the venue-reported leverage observed while they were
  open or closed: from RISEx fills that report it and from retained refresh
  observations (new `LeverageObservations` migration), including a position
  that closed and reopened identically between refreshes. Unobserved leverage,
  and the latest value when its observations conflict, is shown as unknown,
  never inferred.
- Sizing suggestions start from closed Activity positions while no closed Play
  has a win or loss, labelled "From Activity"; R multiples stay unknown, and
  positions with known missing executions, an out-of-range return or an entry
  notional beyond exact decimal precision are left out.

### Fixed

- A Play's entry notional is now the exact sum of its entry fills' quantity ×
  price, summed before any conversion. A Play whose summed entry notional needs
  more than 28 decimal places is excluded from Review and sizing as beyond exact
  precision, instead of being rounded into a different return.

### Changed

- Faster first requests through ReadyToRun API/worker images, a bounded read-only
  API warm-up before readiness, and fewer cookie-authentication database reads.
- Activity aggregation is reused within a bounded in-process, owner-scoped
  cache keyed by a commit-ordered facts revision. Database writes invalidate
  cached facts; authentication and authorization are checked on every request.
- Play account selection uses the shared searchable account picker.
- GitHub workflows use Blacksmith runners in the `sloppitty/vessel` fork.

### Upgrade

- Stop web, API and every worker on all hosts and back up both database schemas,
  evidence and credential keys before upgrading. Use both v0.3.1 images and the
  explicit one-shot `migrate` service before restarting hosts.
- Journal migrations `20261018180202_LeverageObservations` and
  `20261019100000_ActivityFactsRevision` retain reported leverage and install
  Activity cache revision counters/invalidation triggers. No new identity
  migration is required. Recreate disposable databases that applied the old
  unshipped `20261018130000_ActivityFactsRevision`; never rename history rows.
- Upgrading from v0.2.0 or earlier retains the v0.3.0 order-identity restriction:
  databases containing imported orders, fills or Play order links cannot be
  upgraded in place. Rollback requires the pre-upgrade backup and old images.

### Known limits

- Unreported leverage stays unknown; retained refresh observations do not
  reconstruct earlier history. Activity sizing is a fallback, not a merged
  record of Activity and Play outcomes.
- API warm-up is bounded to 30 seconds by default (60 seconds maximum); a
  timeout or failure does not prevent the existing readiness checks from running.
- Wallet mode remains opt-in, with real-wallet acceptance pending and registration
  kept `Closed`. Funding remains separate from execution net.

## 0.3.0 - October 18, 2026

Release notes: [docs/releases/v0.3.0.md](docs/releases/v0.3.0.md).

### Added

- Opt-in Wallet sign-in for Ethereum externally owned accounts, with separate
  owner-scoped journals, invitations and revocable server sessions. Token mode
  remains the default. Wallet signatures are sign-in proofs, never transactions
  or venue permissions.
- Profile, linked-wallet and session management, action-bound wallet step-up,
  and offline operator commands for invitations, access and restore recovery.
- Multi-owner background monitoring and event subscriptions, database-wide
  venue read admission and per-user API limits. Suspended users cannot start
  new monitor or notification work.
- In-app Notification Hub: a Notifications page, unread badge, preferences,
  test notifications and timed popups claimed once across tabs. A separate
  `worker-notifications` service delivers notifications and catches up from
  retained events.
- Bounded 30-day settled funding imports for Hyperliquid, RISEx, Lighter and
  Lighter on Robinhood Chain, with exact signed payments, attribution coverage
  and attributable/unassigned Activity views.

### Changed

- Account pickers and allocation legends show account identity. Lighter import
  diagnostics distinguish excluded history from valid reported executions.
- The workspace loads after the connection screen; React has a separate vendor
  chunk. Backend reads, event capture and JSON parsing are bounded more tightly.
- Health reports monitor and notification deployments separately. Offline
  monitor checks are refused explicitly; expired unclaimed work is completed
  rather than left pending.
- Migration runs validate configuration before opening a database or applying
  either EF bundle. Wallet session streams close if identity revalidation
  stalls; frontend identity updates reject stale cross-tab responses.

### Changed (breaking)

- Order identity: Play order links now reference Vessel's internal order ID,
  and a venue order ID is unique per account and instrument rather than per
  account. Fills belong to an order by account, instrument and venue order ID,
  so a fill can never be attributed to a Play on another instrument. The same
  venue ID on two instruments becomes two orders, and an order reported on a
  different native market fails closed instead of being overwritten. The
  `execution.filled` dedup key uses the internal order ID. API and event
  payloads keep the venue `orderId`. See
  [docs/architecture/order-identity.md](docs/architecture/order-identity.md).
- **Database:** the `OrderIdentity` migration has no backfill and refuses to
  run when imported orders, fills or Play order links exist. Recreate the
  database before applying it.

### Upgrade

- Stop all API, web and worker instances on every host, including the new
  `worker-notifications`, and back up both database schemas, evidence and
  credential keys. This is not a rolling upgrade. Do not bypass the order
  migration guard or delete individual tables to force an upgrade.
- The identity bundle adds `20261017155708_InitialIdentity`. Journal migrations
  since v0.2.0 are `20261016174509_FundingPayments`,
  `20261017091636_OrderIdentity`, `20261017180000_NotificationHub`,
  `20261018100000_ServerEventSubscriptions`,
  `20261018110000_ServerMonitorState` and
  `20261018120000_ServerNotificationState`. Server heartbeat migrations replace
  owner heartbeat tables; old and new hosts must not run together.
- Use the explicit `migrate` service: configuration validation, identity bundle,
  journal bundle, owner bootstrap and messaging provisioning, in that order.
  Nothing migrates at application startup. Rollback requires the pre-upgrade
  backup and matching old images, not down migrations.
- Wallet mode needs an allowed HTTPS origin, an empty bearer token and explicit
  binding of the operator's public wallet address. Keep registration `Closed`
  until real-wallet acceptance is complete. After restore, reconcile identity
  records and run `identity reset-after-restore` as well as event-epoch rotation
  before starting hosts. See [the deployment guide](deploy/README.md).

### Known limits

- Real-wallet compatibility, including a non-mainnet-connected EOA, still awaits
  owner acceptance. Automated Chromium/Firefox checks use synthetic signatures.
  WalletConnect, smart-contract wallets, user deletion and public `Open` or
  `AllowList` registration are not supported.
- Funding remains separate from execution net while the execution-PnL funding
  basis is unknown. It does not change Review, sizing or Play attribution.
- Notifications are in-app only. External channels, browser system
  notifications and drawing/price alerts remain out of scope.
- Venue checks remain periodic and read-only. Per-user API rate limits are
  per process; they are not a global cap on every venue HTTP call.

## 0.2.0 - October 16, 2026

Release notes: [docs/releases/v0.2.0.md](docs/releases/v0.2.0.md).

### Added

- Background Play monitor: a separate `worker` service (same API image,
  `/app/worker`) checks accounts with active venue Plays or pending linked
  orders every 10 minutes by default, imports fills and orders, links what
  matches and moves Plays to Open or Closed without an open browser. Check
  in the account detail or the Play's Execution tab requests an extra run
  (202, with a server-enforced cooldown); account Refresh stays synchronous.
- Discrepancies: missing, cancelled, short or excess stop protection, orphaned
  stops, targets and entries after a Play ends, plan drift, venue positions
  that disagree with open Plays and unlinked fills. They are flagged and
  dismissable, never corrected; subjects that stay unobservable for 7 days
  become `unverifiable`.
- Events: every Play, execution, link, discrepancy and monitor change is
  recorded in PostgreSQL in the same transaction and published once to a
  RabbitMQ topic through a Wolverine outbox; subscriptions catch up from the
  database. The browser follows them through an authenticated SSE stream with
  an opaque `{epoch}.{sequence}` cursor and reloads stored state only.
- Linked exits and entries of closed or cancelled Plays stay watched until the
  venue shows they no longer rest, or end `unverifiable` after 7 days.

### Changed

- All database writes go through one unit of work; object-storage writes
  refuse to run inside a database transaction.
- `POST /api/plays/{id}/execution/check` no longer reads venues: it records the
  same request as `POST /api/accounts/{id}/monitor/run` and answers 202.
  Opening a Play or a browser timer never reads a venue.
- Adapters report omitted executions (unverified fees, unreported PnL,
  unrecognized contracts); a run with omissions is degraded and its fill-based
  discrepancy checks are unknown.

### Upgrade

- New services: `rabbitmq` (set `RABBITMQ_USER` and `RABBITMQ_PASSWORD` in
  `.env`; optional least-privilege `RABBITMQ_APP_USER`/`RABBITMQ_APP_PASSWORD`)
  and `worker`. AMQP stays unpublished; the management page binds to loopback.
- Migrations `20261016110000_VesselEvents`, `20261016110100_AccountMonitor`,
  `20261016110200_MessagingDeployments`, `20261016110300_PlayDiscrepancies` and
  `20261016110400_PlayDiscrepancyUnverifiable` apply after v0.1.3's
  `PlayDirection`. `migrate` runs them, then `provision-messaging` against a
  healthy `rabbitmq` (envelope storage, broker topology, subscriptions);
  nothing migrates or provisions at API or worker startup.
- Stop web, API and worker for upgrades and backups. Every database restore
  must run `rotate-event-epoch` before provisioning and starting hosts.
  See "From v0.1.3 to v0.2.0" in `deploy/README.md`.

### Known limits

- Checks are periodic (10 minutes by default), not real time; a price touch is
  never a fill.
- No notification channel exists yet; events are delivered to the browser
  stream and recorded for a later Notification Hub.
- Lighter Standard accounts exclude fee-bearing or unverified-fee fills, which
  leaves those checks unknown.

## 0.1.3 - October 16, 2026

Release notes: [docs/releases/v0.1.3.md](docs/releases/v0.1.3.md).

### Added

- Activity pages on the server with a 25 / 50 / 100 rows selector (default 25),
  server-side filters and executions loaded when an operation is expanded. The
  500-operation and 10,000-row response limits are gone.
- Plays list tabs Drafts, In progress, Needs review, Reviewed, Closed, Cancelled
  and All with counts, a title/instrument search and the same pager for ended
  and All views. Overview Plays adds a Drafts tab and shows the five most recent
  Plays per tab.
- Delayed loading skeletons that match the row layout.

### Changed

- The Plays list reads summary columns only; a new `Direction` column
  (migration `PlayDirection`, backfilled from the plan) avoids parsing every
  plan. Apply the migration before starting the new API.
- Overview Plays and recent operations request only the five rows they show,
  with counts from the server.

### Upgrade

- Migration `20261016104756_PlayDirection` adds the column and two list
  indexes. Stop web and API while it runs. Older API images cannot save Plays
  after it is applied; restore the backup to roll back.

### Known limits

- Activity still aggregates every retained fill on each request before paging.
- On a desktop Overview the Plays tabs wrap to two lines.

## 0.1.2 - October 15, 2026

Release notes: [docs/releases/v0.1.2.md](docs/releases/v0.1.2.md).

### Added

- Overview Allocation panel: donuts of each portfolio's share of the total and
  each account's share of its portfolio, following the Overview scope. A View all
  button sits beside the scope; the back arrow and a second click on the selected
  slice always return to All accounts.
- S3-compatible evidence storage. The deployment Compose example now starts a
  private RustFS by default; overlays keep a local folder
  (`compose.storage-local.yaml`) or use an external S3 service
  (`compose.storage-external.yaml`). Invalid S3 settings or outages never fall
  back to local files.
- Offline, dry-run-by-default `evidence-copy-to-s3` and `evidence-copy-to-local`
  API commands that copy and verify originals by size and SHA-256 without
  deleting them or changing metadata or configuration.
- Default Play titles (`BTC Long · 15 Oct 2026`) when saved without a name.
- Accounts grouping by venue or owner address.
- Release workflow: pushing an annotated `vX.Y.Z` tag on `main` runs the checks
  and release verification, publishes both Docker Hub images, moves the latest
  tags and creates the GitHub Release from `docs/releases/vX.Y.Z.md`.

### Changed

- Visual refresh: a readable type scale, a signature accent per theme, lit panels,
  a new mark, a depth-contour texture and ruled journal notes; layout unchanged.
  Graphite's accent moved from `#b9c9e4` to `#9ab5f2`.
- Play journal: one header row, an Expand journal dialog, more default height and
  a Review tab only once a Play is Open, Closed or Cancelled.
- Distinct asset, venue and account in Play lists, and one shared select style.
- Less helper text: pills, info tips, coverage badges shown only when incomplete
  and one "About these numbers" disclosure, keeping required notices.

### Fixed

- The deployment Compose PostgreSQL health check probes TCP, so a first start no
  longer lets migrations connect to the image's temporary init server.

### Upgrade

- No database migration. The one-shot migration service has nothing to apply.
- With no storage settings the API keeps using its original local evidence
  folder. Existing Compose installations that take the new `compose.yaml` must
  add `-f compose.storage-local.yaml` to every Compose command to keep their
  images, or copy them to S3 first with `evidence-copy-to-s3`. Do not point the
  new Compose default at an older API image.
- Linux amd64 image tags are `skorcius/vessel:api-0.1.2` and
  `skorcius/vessel:web-0.1.2`.

### Known limits

- Lighter and manual accounts store no owner address, so grouping by address
  lists them together in one group without an address.

## 0.1.1 - October 15, 2026

### Added

- Owner-recorded manual executions with partial exits, explicit unknown costs,
  correction history and idempotent writes.
- Preview and confirmation of eligible retrospective Plays from completed
  imported operations, without inventing a pre-trade plan or initial-risk R.
- Structured Play reviews, amendment history and pending/reviewed queues.
- Review exclusion details with the affected Play, order and fill reasons.
- Whole-position planned loss, gain, reward-to-risk and risk-share estimates
  with separate allocation, priced-quantity and exit coverage.
- Read-only Lighter · Robinhood accounts, with separate credential purposes,
  USDG collateral support and bounded history.
- Bundled venue logos, updated account controls and configurable venue hosts.
- Nominal supported-stablecoin fee and result handling across Activity, Review,
  manual executions, sizing and Overview, with explicit 1:1 valuation caveats.

### Fixed

- Execution completeness, exact residual quantities, partial-exit average cost
  and execution-time ordering in lifecycle, Review and sizing calculations.
- Imported order-link roles, retrospective eligibility and atomic account moves.
- Retained order-history coverage, route body limits and pending evidence edits.
- Extreme percentage-derived prices and tiny-price matching tolerances.
- Stale execution and discovery responses, cancellation, polling and evidence
  Blob URL cleanup.
- Unsaved Play exit protection, page error recovery and lazy-loaded pages.
- Typed chart-level precision, generated tick rounding, leverage confirmation,
  light-theme native controls and remaining hard-coded component colours.

### Upgrade

- Apply `20261013190344_DiaryCompletion` with the explicit one-shot migration
  service while web and API are stopped. Back up the database, evidence and
  credential key ring first. API startup never applies migrations.
- Linux amd64 image tags are `skorcius/vessel:api-0.1.1` and
  `skorcius/vessel:web-0.1.1`. Image publication remains an owner-operated step.
- Existing Lighter accounts and tokens remain separate from Lighter · Robinhood.
  Supported stablecoins are valued nominally at 1:1, not at an FX or depeg price.

## 0.1.0 - October 13, 2026

First manual release of the self-hosted, single-owner Vessel application.

- Overview with enabled-account balances, coverage and recent activity.
- Accounts and portfolios, custom portfolio icons, manual accounts, disabling,
  moving and explicit deletion with retained-data safeguards.
- Bounded read-only Hyperliquid, Lighter and RISEx integration. Lighter read-only
  tokens use encrypted server-side storage. No trading orders are submitted.
- Saved Plays, lifecycle status, plan revisions and imported order/fill linking.
- Venue candle charts, drawings, plan/range tools, EMAs, Volume and RSI.
- Journal evidence captures and uploads, notes and vector markup beside originals.
- Portfolio review of closed Plays from linked executions, with coverage limits.
- Sizing suggestions with explicit acceptance and reasoning, not automatic advice.
- Venue-neutral activity operations with expandable executions and filters.
- Settings, built-in dark/light themes and browser-saved custom palettes.
- Production auth hardening, health endpoints and explicitly trusted proxy settings.
- Separate API and web Docker images, explicit migration bundle and Compose example.
- Exact single-hop proxy trust, an optional Caddy TLS overlay with unpublished web,
  static security headers/CSP and backend-only API compression decisions.
- Isolated database/evidence/key-ring restore instructions and local HTTP/TLS,
  browser CSP and restore verification scripts.

Full-history backfills, background monitoring and notification delivery remain
outside this release. Activity operations are not retrospective Plays or inferred
trading intent.
