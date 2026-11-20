# Frontend rules

The top-level [`AGENTS.md`](../AGENTS.md) applies first. Structure: the
[frontend guide](../docs/architecture/frontend-guide.md). Use pnpm and a Node
24 with full ICU locale data (see the container route in the top-level file).

## Layout and design

- Keep the approved section positions: chart, position editor, journal, capital
  context and summary ([approved baseline](../docs/design/approved-baseline.md)).
  The Play entry editor stays bounded, with its expanded editor.
- Graphite is the default theme. Themes change colours and radius only, never
  layout. Component CSS reads theme tokens, not hard-coded colours. Built-in
  themes, including the `--paper-*` tokens, must keep passing the contrast test;
  after a style change, also check the rendered controls
  ([appearance settings](../docs/architecture/appearance-settings.md)).
- Follow the [visual refresh](../docs/design/visual-refresh/README.md): reduced
  helper text (pills, `InfoTip`, `CoverageBadge` only when coverage is incomplete),
  and keep the required notices it lists. A trigger's accessible name starts with
  its visible text.
- Every select uses `NativeSelect`, except account selectors, which use
  `AccountPicker` or `AccountSelect`. `InstrumentPicker` has no venue logic
  ([market sources](../docs/architecture/market-sources.md)).
- Series and allocation colours come from `--series-*` tokens and follow the
  entity. Brand accents only tint marks, never replace text contrast
  ([core workspace](../docs/architecture/core-workspace-contract.md)).
- Long is green and up, Short red and down. Leverage is a whole number.
- Do not tag items as Paper; the Paper disclosure appears once, in the header.
- The journal shows Review only once a Play is Open, Closed or Cancelled.
- Motion is absent under reduced motion; skeletons are delayed and
  geometry-matched ([list paging](../docs/architecture/list-paging.md)).

## Charts and evidence

- Charts use Lightweight Charts with Vessel-built tools; follow the chart plan
  for drawings, indicators and level units.
- No sample candles. Manual instruments keep the chart placeholder; candles and
  live updates are observations, never fills
  ([chart plan](../docs/architecture/chart-plan.md)).
- Captures and uploaded images stay in the draft until the Play is saved. Markup
  sits beside the untouched original; the marked view is the default.

## Data in the browser

- The browser talks to the Vessel API, never directly to venue APIs.
- No secret, token or key in Vite, a request URL or query string, or browser
  storage. The one exception is device pairing: the single-use pairing secret
  travels only in the `#pair=` URL fragment of the QR, and the QR never carries a
  private key, venue credential or existing session token
  ([device pairing](../docs/architecture/device-pairing-plan.md)).
- `localStorage` holds only per-device preferences; candles are never stored
  across reloads.
- Unsaved Play drafts live in memory: shell navigation keeps them, reload and
  sign-out discard them. Filters, scopes and the breadcrumb live in shell memory.
- Sign-out, unmount or API replacement cancels refresh queues and requests;
  cancelled work never notifies or reloads
  ([account management](../docs/architecture/account-management-contract.md)).
- Account creation queues one bounded initial refresh in the shell, after any
  credential save; a failure keeps the account.
- Controls follow the API's venue capabilities, never venue names.
- Show unknown values as unknown, never as zero, and keep currency and coverage
  limits visible. Suggestions are never applied automatically, and are estimates,
  not advice ([sizing](../docs/architecture/sizing-suggestions.md)).
- Process views show cases and counts: no rate below its own n = 10, no ranking,
  no labels and nothing AI-written. Plan it is never blocked
  ([Phase 1](../docs/architecture/phase1-learning-loop-contract.md)).
- Notification popups close by themselves and never mark anything read
  ([Notification Hub](../docs/architecture/notification-hub-plan.md)).
- Event cursors are opaque and owned by `src/api/event-cursor.ts`.
- Keep the icon keys in `src/api/portfolio-icons.ts` in step with `PortfolioIcons`
  in the backend domain; never accept uploaded images or markup as icons.
