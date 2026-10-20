# Plays chart: research and proposed plan

October 1, 2026. Status: **owner decisions recorded; first pass (C2–C5, C7) authorized**. See [Owner decisions](#owner-decisions) and [First pass contract](#first-pass-contract).

After PR #2 merged, the owner asked for research on the chart, planned entries on top of it, drawing tools and captures of drawings as Play evidence.

## Findings

### Renderer

| | Lightweight Charts 5.2.1 | KLineCharts 10.0.3 | TradingView Advanced Charts | ECharts 6.1 |
|---|---|---|---|---|
| License | Apache-2.0 + required TradingView attribution/link | Apache-2.0 | Proprietary, application/approval | Apache-2.0 |
| Size (ESM gzip) | ~60 KB | ~102 KB | Large hosted bundle | Much larger |
| Drawing tools | None; custom series primitives | Built-in overlays, no position box | Full | None |
| Level drag | Custom hit-test | Overlay move events | Built-in | Custom |
| Capture with overlays | `takeScreenshot(true)` includes canvas primitives | `getConvertPictureUrl` | Built-in | `getDataURL` |
| Activity | Very active | Active | n/a | Very active |

Recommendation: Lightweight Charts v5 behind a Vessel `ChartAdapter`, with our own primitives for levels and drawings. It has public symmetric time/price ↔ pixel conversion, the smallest bundle and canvas capture that includes primitives. KLineCharts would save drawing work but imposes its overlay model on levels, theming and evidence. Advanced Charts is ruled out by licensing friction. The young `lightweight-charts-drawing` package (MIT, one npm release) is reference code only, not a dependency.

Risks to settle in a spike: time coordinates beyond the last bar (future anchors) return null and need logical-index extrapolation; pointer hit-testing/drag is custom; HTML overlays are not captured, so all labels must be canvas-drawn; canvas is not testable in jsdom; attribution must be shown.

### Market data (Hyperliquid)

- `POST /info {"type":"candleSnapshot","req":{coin,interval,startTime,endTime}}`; intervals 1m–1M; only the latest 5,000 candles exist; prices/volume arrive as decimal strings.
- Weight: 1,200/min per IP shared with other reads; candles add weight per 60 items. Requests must be bounded and cached.
- Live: WebSocket `candle` subscription re-sends the forming candle; upsert by open time. HIP-3 coins are `dex:COIN`.
- No candle or rate-limit code exists in the backend yet. The reader pattern (`IPerpetualVenueReader`, bounded `POST /info`, strict decimal parsing, `VenueReadException` → 502) fits a new market-data capability.

### Drawings

Store drawings as `{id, schemaVersion, tool, points: [{timeMs, price}], style, locked}` anchored to UTC time and exact price, never to bar index or pixels, so they survive zoom, resize and timeframe change. Scope them to the instrument. Migrate by `schemaVersion` on load. The prototype stored chart-relative 0–1 coordinates and cleared lines on instrument/timeframe change; that is not suitable for real data.

### Captures and evidence

`takeScreenshot(true)` → compose header (instrument, venue, timeframe, UTC time, "planned levels are not fills") onto a fixed-scale canvas, longest edge capped → `toBlob('image/png')`. No base64 persistence.

Durable storage per [the proposal](proposal.md): PostgreSQL metadata (owner, key, type, size, SHA-256, note, Play association) plus a filesystem `IEvidenceStore` first, served through an authenticated streaming endpoint fetched as a blob. MinIO is no longer a sound choice (maintenance/archived); Garage or SeaweedFS are later S3 options. Because Plays are still in-memory drafts, durable evidence depends on Play persistence.

## Proposed tasks

| # | Task | Depends on | Notes |
|---|---|---|---|
| C1 | Spike: Lightweight Charts in Vite/React | — | Verify future-time anchors, primitive hit-test/drag, `takeScreenshot` with primitives, theming from CSS variables, attribution, jsdom strategy (fake adapter). Throwaway branch; go/no-go. |
| C2 | Backend candles endpoint | — | `IMarketDataReader` in Application, Hyperliquid `candleSnapshot` in Infrastructure, `GET /api/accounts/{id}/candles?instrument&interval&before`, owner/enabled checks, bounded count, exact decimal strings, gap/coverage metadata, short in-memory cache, handler-mocked tests. |
| C3 | Reusable chart component | C1 | `components/chart`: adapter interface + LWC implementation, timeframe selector, resize, older-history loading, loading/empty/error/stale states, Graphite theming, attribution. API client method and validator. Remove the unused `SampleChart`. |
| C4 | Planned levels overlay and selection | C3 | Map draft entries to overlays (resolve percent stops/targets against entry price, skip invalid), entry colors, canvas axis labels, Aggregate/selected dropdown, click and keyboard selection through the existing `selectionRequest` path. Legend stays. |
| C5 | Drag planned levels | C4 | Drag selected entry's entry/stop/targets; write a fixed price, switching percentage stops/targets to price units; price precision from metadata; keyboard nudge alternative. |
| C6 | Drawing tools | C3 | Toolbar and manager: trend line, horizontal line/ray, rectangle zone, Fibonacci retracement, long/short box, text note. Select, move, delete, undo, lock, clear. Stored in the draft per instrument. |
| C7 | Expanded chart dialog | C3 | In-page dialog sharing candles, levels, drawings and selection; Escape and focus restoration. |
| C8 | Captures in the draft | C4, C6 | Capture button, composed PNG, Evidence tab grid with per-capture note, download with note, remove with confirmation, count limit. In memory, discarded on reload like the rest of the draft. |
| C9 | Durable evidence storage | Play persistence | Filesystem store, upload validation (magic bytes, size, hash), metadata migration, authenticated streaming, orphan cleanup, backup notes. |
| C10 | Live candle updates | C2, C3 | Backend-held WebSocket or polling relay; stale/degraded indicators. |
| C11 | Contract, verification and PR | all chosen | Chart contract doc, `pnpm check`, browser checks at approved widths, Plane/Outline updates. |

C1 and C2 can run in parallel; C4 → C5 and C6 can run in parallel after C3.

## Owner decisions

October 1, 2026:

1. Lightweight Charts v5 with Vessel-built tools. Drawing tools (C6) move to the last step.
2. Captures (C8) and durable evidence storage (C9) are built together in the last step.
3. The six C6 tools are the starting set, later.
4. Manual candle refresh now; automatic/live updates (C10) later.
5. Manual instruments keep the chart placeholder until a market-data provider is added.
6. Drawings are kept per instrument when the instrument changes.

The owner authorized starting chart implementation: candles endpoint, reusable chart, planned levels, selection, level dragging and the expanded dialog.

## First pass contract

### Backend

`GET /api/accounts/{id}/candles?instrument={contractId}&interval={interval}&endTime={ms?}` under the authorized API group.

- Owner-scoped account lookup; disabled account → 409; manual account → 409 with "No market data provider for manual accounts."; venue reader mismatch or venue failure → generic 502.
- `instrument` must be a primary-perpetual contract id (1–32 chars of letters, digits, `-`, `_`; no `dex:` prefix in this pass). `interval` ∈ 1m, 3m, 5m, 15m, 30m, 1h, 2h, 4h, 8h, 12h, 1d, 3d, 1w, 1M. Invalid → 400.
- `endTime` defaults to now; window is at most 500 candles ending at `endTime` (1M uses 31 days). Paging older: pass the oldest `openTime - 1`.
- Response: `{ venueId, instrument, interval, priceSource: "trades", candles: [{ openTime, closeTime, open, high, low, close, volume, trades }], requestedFrom, requestedTo, retrievedAt, historyExhausted, notice }`. Times are UTC ms (retrievedAt ISO). OHLCV are exact decimal strings as received and validated; candles are sorted ascending and deduplicated by open time.
- `historyExhausted` is true when the venue returned no candles for the window; the notice states that Hyperliquid exposes only the latest 5,000 candles per interval.
- A short in-memory cache (≈10 s, keyed by instrument/interval/window) limits repeated manual refreshes. No background job, WebSocket or persistence.

### Frontend

- `components/chart` owns a renderer-neutral `CandleChart` with an adapter boundary; it receives candles, price overlays, selection and theme, never the Play draft. Lightweight Charts is the only implementation; tests use a fake adapter.
- Plays maps draft entries to overlays: percent stops/targets resolve against the entry price for display only; invalid or blank levels are omitted. Planned levels are not fills.
- Aggregate/selected dropdown; clicking a level or legend item uses the existing selection request path.
- Initially, dragging a selected entry's level wrote back in the level's own unit (price or percent of entry), rounded to five significant figures, and percent levels could not cross the entry. The October 5 correction below supersedes that unit-preserving behavior. The numeric editor fields remain the keyboard and screen-reader path; canvas keyboard nudging was not added.
- Timeframe selector and manual Refresh; older history loads on scroll-left until exhausted. Loading, empty, error and stale states are explicit. TradingView attribution is shown.
- Expanded chart is an in-page dialog sharing the same state, with Escape and focus restoration.
- Manual instruments, no account or no instrument keep the placeholder.

## First pass state

October 1, 2026. Implemented on branch `t3code/3826a317`; not yet published.

- Backend: `ICandleReader` and `CandleService` in Application (`MarketData/`), Hyperliquid `candleSnapshot` in the existing reader, `GET /api/accounts/{id}/candles`. Owner, enabled and manual checks precede the 10-second bounded cache. OHLC consistency and exact decimals are validated in the reader. `dex:` coins are rejected.
- Frontend: `lightweight-charts@5.2.1` (adds `fancy-canvas@2.1.0`) behind `components/chart` (`CandleChart`, `ChartAdapter`, `createLightweightAdapter`), lazy-loaded as a separate ~54 kB gzip chunk. `features/market/useCandles` loads, refreshes manually and pages older windows. `features/plays/PlayChart` and `levels.ts` map draft entries to overlays and dragged prices back to the draft. jsdom tests use a fake adapter; `src/test/setup.ts` replaces the canvas renderer globally.
- Placeholder remains for manual accounts, manual labels and no instrument. Capture stays disabled until evidence storage. TradingView attribution logo is shown.
- Verified with a throwaway PostgreSQL and API against live Hyperliquid in headless Chromium 151 at 1402 × 877, 1001 and 390 px: real BTC candles, entry/stop/target tags and axis labels, stop drag updated the editor (83335 → 82681), timeframe switch, older-window paging on pan without a view jump, expanded dialog with Escape focus restoration, no page overflow and no console errors.
- Known cosmetic issue: at narrow widths the TradingView attribution logo can overlap a level tag near the bottom-left.
- Not verified: touch dragging, very low-priced instruments beyond kPEPE API output, long sessions near the 5,000-candle limit, screen-reader behavior of the canvas.

## Owner refinements: Hyperliquid-style chart

October 1, 2026. The owner asked for a chart closer to Hyperliquid's, using a screenshot as reference.

- Candles are monochrome: white up, black down with a light edge (`--chart-candle-*` overridable). Red and green are reserved for stops (`--negative`) and targets (`--positive`). Entry lines and tags keep the entry's UI color; stop/target tags carry an entry-color marker.
- A market strip shows Mark, Oracle, 24h change, 24h notional volume, open interest (base units) and hourly funding from `GET /api/accounts/{id}/market-context?instrument=`. The backend caches one `metaAndAssetCtxs` snapshot per venue for 10 seconds and keeps exact strings; the 24h change is display-only (mark vs previous-day price). Perpetuals have no market cap or contract address, so those Hyperliquid spot fields are omitted. A statistics failure does not block the chart; Refresh reloads both.
- Timeframes use a compact bar (5m, 1h, 4h, D by default) plus a menu where any interval can be starred. Favorites and the last interval are stored in browser `localStorage` (`vessel.chart.preferences.v1`) as view preferences only.
- The marked left drawing rail remains the later drawing-tools step (C6) and will follow this layout.
- Verified against live Hyperliquid (HYPE, 4h, two entries) in headless Chromium at 1402, 1001 and 390 px: no overflow or console errors. `pnpm check` passes with 441 backend (40 skipped), 209 frontend and 76 prototype tests.

## Owner refinements: average entry, chart chrome and tool rail

October 1, 2026.

- The Aggregate view plots `AVG`, the quantity-weighted planned entry price over entries that have both a price and a positive quantity share (shares normalized over those entries; at least two required). It is a dashed reference line, not draggable or selectable, and is also listed in the legend. The owner requested it explicitly; it is a plan average, not a fill or execution average.
- Reusable chart chrome lives in `components/chart`: `ChartHeader` (symbol, caption, inline statistics), `ChartToolbar` with `ChartToolbarDivider`, `ChartIconButton` (tooltip and disabled reason), `ChartMenu` (single-choice popover with optional swatches), `TimeframeBar` and `ChartToolRail`. Plays composes them; analysis pages can reuse them without the Play model. Styles are in `components/chart/chart.css`.
- The toolbar replaces sparse buttons: timeframes and the view menu on the left; update status, refresh, capture (disabled until evidence storage) and expand icons on the right.
- The drawing rail is prepared with the agreed set (`drawingTools.tsx`): crosshair (active), trend line, horizontal line, rectangle zone, Fibonacci retracement, long/short position, text, snap and clear. All except the crosshair are visibly disabled with "Coming with drawing tools" until C6.
- Verified against live Hyperliquid (HYPE, 4h, two entries, average 85.25) in headless Chromium at 1402, 1001 and 390 px with no overflow or console errors; Escape restores focus to the expand control. `pnpm check`: 441 backend (40 skipped), 213 frontend, 76 prototype.

## Drawing tools (C6) state

October 2, 2026. The owner asked to start the drawing tools after PR #3 was opened. This work is stacked on that branch.

- Tools: trend line, horizontal line, rectangle zone, Fibonacci retracement (level 1 at the start, 0 at the end), long/short position (stop mirrors the target at 1R initially; display distances and R only, not plan levels or orders) and text note. The crosshair selects, moves and resizes; the magnet snaps anchors to the nearest candle open, high, low or close.
- Model: `components/chart/drawings.ts` stores `{ id, schemaVersion: 1, kind, points: [{ time, price }], text? }` with UTC-millisecond anchors. `TimeIndex` maps times to fractional bars and extrapolates by the bar interval, so anchors before or after loaded candles keep their time across zoom, timeframe changes and future space.
- Interaction and rendering: `DrawingController` works in pane coordinates through a `DrawingSpace` interface and draws on the chart canvas, so later captures include drawings. It picks the nearest stroke on click; filled areas rank behind strokes. The Lightweight Charts adapter routes pointer events to drawings before level drags and panning.
- Editing: `useDrawingEditor` owns tool, selection and a 50-step undo history (a whole move or a text edit is one step). Delete/Backspace removes, Escape cancels or deselects, Ctrl/Cmd+Z undoes. A floating bar shows the creation hint or the selected drawing with note text and delete. Clear is undoable rather than confirmed.
- Storage: drawings live in the in-memory Play draft under `venueId:contractId`, so switching instruments and back keeps them. Reload discards them like the rest of the draft; durable storage comes with captures/evidence (C8–C9). The magnet preference is stored with the other chart view preferences.
- Limits: up to 200 drawings per instrument. Canvas drawing is pointer-only; keyboard users can select tools, delete, undo and edit note text but cannot place anchors.
- Verified in headless Chromium against live Hyperliquid HYPE: every tool created by mouse, nearest-stroke selection, move, delete, undo, per-instrument retention across BTC and back, and anchors kept after switching 4h → 1h. No console errors. `pnpm check`: 441 backend (40 skipped), 230 frontend, 76 prototype.

### Owner refinements, October 2

- The chart header no longer repeats the instrument; it is already chosen and shown in the play fields. The header carries market statistics only, and the toolbar status names the instrument, venue and last update.
- Drawings have optional `style` (`color` from a curated palette, `line` solid/dashed/dotted, `width` 1–3) and `locked`. A floating edit bar changes color, line style, width and note text, locks/unlocks and deletes. Appearance changes are single undo steps and stay available while locked; locked drawings select but cannot be moved, resized, deleted or cleared (clear removes unlocked drawings only).
- The full-position summary shows the same quantity-weighted **planned average entry** as the chart's `AVG` line (one or more priced entries), keeping the chart and summary consistent. Other financial figures remain not calculated.
- Alignment pass: header statistics and toolbar content share a 15 px inset, toolbar controls share one center line, column bottoms align at 1402 × 877, and phones get a 320 px chart with a compact single-row edit bar. `pnpm check`: 441 backend (40 skipped), 234 frontend, 76 prototype.

### On-chart level editing and undo/redo, October 2

- Every planned entry, stop and target can be dragged on the chart, not only the selected entry's. Dragging another entry's level selects that entry when the drag ends. Handles still mark the selected entry. `AVG` stays read-only.
- Clicking a level's tag or double-clicking its line opens an in-chart editor. The chart always edits a **price**; initially, a level entered as a percentage kept its unit and stored the equivalent distance, as dragging did (superseded by the October 5 correction below). Targets also edit their share. The editor can add a target (2% steps beyond the entry in the trade direction), add a missing stop (2% on the risk side) and remove a target. All edits go into the same draft as the side editor.
- `useChartHistory` provides one undo/redo history for chart edits: drawings and level drags or edits. Entry actions reapply only the fields they changed, so later side-editor edits survive undo. Ctrl/Cmd+Z undoes; Ctrl/Cmd+Shift+Z or Ctrl/Cmd+Y redoes; the rail has Undo and Redo with the action name. Edits made in the side editor are not part of chart history.
- Verified in headless Chromium against live HYPE: dragging an unselected entry's stop selected it and updated its field (84 → 82.897), tag click and double-click opened the editor, a saved target price updated the editor (94 → 95), add target, undo and redo. `pnpm check`: 441 backend (40 skipped), 241 frontend, 76 prototype.

## Live updates (C10) contract

October 2, 2026. The owner asked for automatic chart updates, as close to real-time data as possible, so planned entries are judged against current prices. Manual refresh stays. Live prices are observations: a candle or mark touching a level is not a fill.

### Backend

`GET /api/accounts/{id}/market-stream?instrument={contractId}&interval={interval}` under the authorized API group returns `text/event-stream` (`Cache-Control: no-store`, `X-Accel-Buffering: no`).

- Same checks and error mapping as `/candles`, evaluated before the stream starts: owner-scoped account (404), disabled 409, manual 409, instrument/interval validation 400, venue reader mismatch 502. Errors are ProblemDetails, not stream events.
- Events (UTF-8 JSON, one `data:` line each):
  - `event: candle` → the candle shape of `/candles` (`openTime, closeTime, open, high, low, close, volume, trades`). The forming candle is re-sent as it changes; clients upsert by `openTime`.
  - `event: context` → exactly the `/market-context` response shape (`venueId, instrument, markPrice, oraclePrice, midPrice, previousDayPrice, dayNotionalVolume, openInterest, fundingRate, premium, observedAt, notice`).
  - `event: status` → `{ "state": "live" | "reconnecting" | "stale", "observedAt": ISO }`. `live` after the upstream subscriptions are acknowledged or data arrives, `reconnecting` while the upstream socket is down, `stale` when no upstream message arrived for 60 s.
  - A `: keepalive` comment every 15 s.
- Values keep the venue's exact decimal strings. Upstream numbers are converted from their raw JSON text, never through floating point. Validation matches the REST readers; malformed upstream messages are dropped and counted, not forwarded.
- Upstream: one shared Hyperliquid WebSocket (`wss://api.hyperliquid.xyz/ws`) per process in Infrastructure, behind an Application port. Subscriptions are reference-counted (`candle` per coin+interval, `activeAssetCtx` per coin). It connects lazily, unsubscribes when the last listener leaves, closes after 60 s without listeners, sends `{"method":"ping"}` every 30 s, and reconnects with jittered exponential backoff (1 s to 30 s), resubscribing everything.
- Bounds: at most 8 concurrent streams per process (beyond that, 429 ProblemDetails), at most 100 upstream subscriptions, bounded per-listener queues that coalesce candle updates by `openTime` and keep only the latest context, and a 1-hour maximum stream lifetime after which the server ends the response.
- No persistence, polling job or order/fill matching.

### Frontend

- `WorkspaceApi.marketStream(accountId, query, signal, onEvent)` reads the stream with `fetch` (bearer header; no `EventSource`, no token in URLs), parses SSE incrementally and validates every event with the existing candle/context validators. Invalid events are ignored.
- `useLiveMarket` starts after the first candle load. It upserts candles into the loaded series and replaces the header statistics. On stream end or error it reconnects with backoff (1, 2, 5, 10, 30 s) and refreshes candles over REST after a reconnect to fill gaps. After three consecutive stream failures it falls back to polling the REST endpoints every 15 s and keeps retrying the stream every 60 s. It pauses while the tab is hidden and refreshes when the tab becomes visible again.
- A `Live` toggle in the chart toolbar (persisted with the chart preferences, default on) shows the state: live (green dot), connecting/reconnecting (amber), polling fallback, stale, or off. Manual Refresh remains. The expanded dialog shares the same live state.

### Live updates state

October 2, 2026. Implemented by delegated backend and frontend workers against the contract above; integrated and verified by the orchestrator.

- Backend: `MarketDataGuard` now holds the shared account/instrument/interval checks for candles, market context and the stream. `IMarketStream`/`MarketStreamService` (Application) and `HyperliquidMarketStream` with a fakeable WebSocket transport (Infrastructure) implement the relay. `/market-stream` is in `MarketStreamEndpoint.cs`.
  - Deviations from the contract: a socket silent for 90 s despite pings is replaced. New listeners immediately receive the last candle/context already held for a shared subscription. The port uses a synchronous `Subscribe` so capacity errors return ProblemDetails before headers are written.
  - Observed live Hyperliquid shapes: `activeAssetCtx` values arrive as JSON strings; candle `data` is a single object.
- Frontend: `api/sse.ts` (incremental parser, 1 MiB bound), `WorkspaceApi.marketStream`, `useLiveMarket`, `LiveIndicator`, `useCandles.upsert` and `useMarketContext.apply`.
  - Deviation: streams that connect but drop within 60 s walk the full 1/2/5/10/30 s backoff; three streams in a row that fail without sending anything trigger 15 s polling with a 60 s stream retry.
  - The toolbar keeps one row by showing only the update time and Live state; the source caption is a tooltip and screen-reader text.
- Planned levels farther than ±50% from the latest close no longer widen the price scale, so a plan with another instrument's prices cannot flatten the candles.
- Verified against live Hyperliquid through Kestrel and the Vite proxy:
  - correct SSE headers;
  - status/context/candle events within a second;
  - mark and update time changing every 2 s in the browser;
  - Live off freezes the stats and on resumes them;
  - an instrument switch opens exactly one new stream;
  - 400 ProblemDetails for invalid instruments;
  - no console or API errors at 1402 and 390 px.
- Not verified: behaviour behind a reverse proxy; multi-hour sessions.
- `pnpm check`: 487 backend (40 skipped), 260 frontend, 76 prototype.

### PR #3 review fixes

October 2, 2026. The owner's review of `80876e1` raised three findings and a known visual issue. All are fixed:

- Chart undo/redo reapplies an entry edit at sub-field level: stop `unit`/`value`, and targets by ID (changed `unit`/`value`/`share`, additions and removals). A chart TP1 edit followed by a sidebar TP2 edit now undoes TP1 only.
- The in-chart level editor re-measures on chart or editor resize (ResizeObserver plus window resize) and fits its width to narrow charts, so it stays visible after a desktop-to-phone change and when a validation message grows it.
- Candle responses keep at most the newest 500 candles; an interval-aligned `endTime` previously returned 501 because both window ends are inclusive.
- Level tags near the bottom shift right of the required TradingView attribution instead of overlapping it.
- CI: the failed `pull_request` run on `80876e1` timed out 44 interaction-heavy frontend tests at the 5 s default on a slow runner, and one older-paging assertion assumed no later refresh. The frontend test timeout is now 15 s, and that assertion checks that the older request happened.

## Captures and evidence storage (C8–C9), October 2

After PR #3 merged, the owner asked for chart screenshots and uploaded images with editable notes on Plays, with storage local for now but replaceable by S3-compatible or remote storage through configuration. The owner chose to keep images **in the browser while the Play is unsaved** and to store them on the server only once a Play is saved.

Frontend (draft side):

- The chart toolbar's camera button captures the chart (`ChartAdapter.capture`, Lightweight Charts `takeScreenshot(true, false)`). Planned levels and drawings are series primitives, so they are in the image; the crosshair is not. A footer adds `instrument · venue · interval · UTC time · Planned levels are not fills`. It is enabled once candles have loaded, also in the expanded view.
- A capture is added to `PlayDraft.evidence` (Blob, name, context, note, time) and brings the journal's Evidence tab forward. Uploads accept PNG, JPEG or WebP detected by file signature, up to 10 MB and 50 images per play, mirroring the server defaults.
- The Evidence tab lists images with an inline note each, a larger viewer with the same note, download, and removal after confirmation. The general evidence notes field stays below. Object URLs are revoked when an image is removed. Like the rest of the draft, images are discarded on reload or disconnect. A saved Play's originals load lazily per image, with retry, after it opens (see the October 19 section below).
- Capture and upload finish asynchronously, so they apply to the latest draft rather than the one from the click.

Backend (saved Plays):

- `PlayEvidence` (domain) belongs to a persisted Play (`OwnerId`, `PlayId`, generated object key, detected content type, size, SHA-256, source capture/upload, note ≤ 4000, timestamps). Table `play_evidence` (migration `EvidenceStorage`) has an owner-scoped composite FK to `plays` with `ON DELETE RESTRICT`, so deleting a Play must deal with its images explicitly. The owner query filter and write guard cover it.
- `EvidenceService` (application) validates the play, source, note, per-play count, size (bounded read) and signature, hashes the bytes, writes the object, then the metadata; a failed metadata write deletes the object. Deletion removes the record first, so a failed object delete only leaves an unreferenced file.
- `IEvidenceObjectStore` (application port) knows only keys and bytes. `LocalEvidenceObjectStore` (infrastructure) writes to a temporary file and moves it into place, never overwrites, and rejects keys it did not generate. Keys are `owner/play/id.ext`, built from IDs only.
- Configuration lives in `backend/src/Vessel.Api/appsettings.json` under `Vessel:Evidence`: `MaxUploadBytes` (10 MiB), `MaxPerPlay` (50), `Storage:Provider` (`Local` by default for backward compatibility; explicit `S3` selects RustFS or another compatible service), `Storage:Local:RootPath` (`data/evidence`, relative to the API content root and git-ignored). Settings are validated at startup. See [evidence storage](evidence-storage.md) for the S3-compatible adapter, explicit RustFS deployment and offline existing-file copy/verification.
- Routes (bearer-protected): `GET/POST /api/plays/{playId}/evidence` (multipart `file`, optional `note`, `source`), `GET /api/evidence/{id}/content` (`nosniff`, `private, no-store`, sandbox CSP, SHA-256 ETag), `PATCH /api/evidence/{id}` `{ note }`, `DELETE /api/evidence/{id}`. Errors are safe Problem Details: 404 missing/foreign, 400 invalid form/note/source, 413 too large, 415 not PNG/JPEG/WebP, 409 count limit.

At the original October 2 delivery, Play save, orphan cleanup, backups and S3 were not included. Saved Plays now upload evidence; October 15 adds S3 and documented backups. Automatic orphan cleanup remains out of scope. Back up image storage together with the database.

Verified: backend 553 tests including real PostgreSQL (migration, owner filter, write guard, restrict, hash check); frontend 273 tests; live API upload/read/edit/delete with real files on disk, 413/415/401 paths; headless Chromium capture with levels, a drawing and the footer, uploads with a rejected file, viewer note editing, removal confirmation, and no horizontal overflow at 390 px.

### Saved originals load on their own, October 19

Phase 0.6 of the [trust and correctness contract](phase0-trust-correctness-contract.md)
(D-10). A saved Play opens from the Play and its evidence metadata
(`GET /api/plays/{id}/evidence`, database only) and no longer waits for every
original, so one storage failure cannot keep the thesis, plan, journal or
review closed.

- Originals load afterwards through the same authenticated, API-proxied
  `GET /api/evidence/{id}/content`, at most three at a time
  (`useEvidenceImages`/`EvidenceImageLoader` in `features/plays/evidence-images.ts`).
  Reads are aborted when the image leaves the open Play (another Play opens,
  Remove, discard), on a new API identity and on unmount. Nothing is kept in the
  browser across reloads.
- Each image shows its own state: loading, **Image unavailable** with **Retry**
  (503, 500, timeout or transport failure; nothing retries by itself) or
  **Missing from storage** (404). The viewer gives the stored size from the
  metadata. Download and markup need the loaded original; the note can still be
  edited and saved.
- Loaded originals stay outside the draft, keyed by server ID. Their arrival is
  not an edit and never changes the draft's identity, so opening another Play
  and save merging are unaffected.
- Save never deletes or uploads an image whose original has not loaded: it keeps
  its server ID, and its note and markup save through their own requests. Only
  the same confirmed Remove as for any other image deletes it.
- S3 stays fail-closed: an outage is a 503 for that image, never a Local
  fallback.

### Chart-picked SL/TP prices, October 5

The owner clarified that moving a percentage stop or target on the chart must switch it to **price units**, rather than retain a percentage representation. Chart drags now store the actual dragged price with the existing five-significant-figure precision, preserving the exit ID, share and sibling levels. Explicitly changing the price in the chart popover does the same; changing only its share leaves its unit/value unchanged. Undo restores both the previous unit and value; redo restores the price. Later leverage or entry-price changes do not move the chart-chosen price. Existing wrong-side plan validation applies instead of silently clamping a drag at the entry. Unmoved percentage levels retain their leveraged-return meaning. No persisted-plan migration is needed.

### Chart price precision, October 14

The frontend architecture review fixes the popover's S7 rounding defect.
An explicitly typed valid price stays exactly as entered, including its decimal
places. A price edit changes a percentage exit to price units; opening the
popover or changing only its share leaves the original unit and value intact.

Generated prices, including stops and targets added through the popover, use
the instrument's `priceStep`. The existing five-significant-figure rule is the
fallback when no tick is available. `PlayChart` passes the tick to `LevelEditor`
and `addChartExit`; typed prices do not pass through that formatter. This
follows the typed-versus-chart-generated distinction in
[venue-instruments.md](venue-instruments.md). No API or persisted-plan change
is involved.

### Image markup, October 2

The owner asked to draw over evidence images and keep the marked version, with a switch between original and marked (marked by default).

- Tools: pen (freehand), marker (translucent highlighter), arrow, box and text; six colours; three sizes; select to move, recolour or resize; double-click text to edit; Delete removes; undo/redo; clear all. Sizes scale with the image, so marks look alike on small captures and large uploads.
- Marks are vector shapes in natural image pixels (`ImageMarkup` in `frontend/src/features/plays/markup.ts`). The original image is never modified. On screen the marks are an SVG layered over the image with the same fit; download flattens them into `<name>-marked.png`.
- The viewer shows the marked version by default with a Marked/Original toggle, plus Mark up/Edit marks and a download of the version shown. Cards show the marked thumbnail with a Marked badge and download the marked version. Each finished mark is applied to the draft at once.
- Server: `play_evidence.Markup` (`jsonb`, migration `EvidenceMarkup`). `PUT /api/evidence/{id}/markup` replaces and `DELETE /api/evidence/{id}/markup` clears; upload accepts an optional `markup` JSON field. `EvidenceMarkup.Normalize` validates dimensions (≤ 20000 px), shape count (≤ 200), unique IDs, `#rrggbb` colours, points inside the image (≤ 2000 per stroke), stroke width, text size and text (1–280 characters, no control characters), and stores only the fields each kind uses. Evidence DTOs include `markup`.

Verified: backend 557 tests with real PostgreSQL (including the `jsonb` round trip); frontend 279 tests; headless Chromium drawing every tool on a real capture, moving and recolouring, Marked/Original toggle, flattened download, and the editor at 390 px without overflow. Found and fixed in the browser: the text box closed immediately because mouse-down moved focus. PR review, October 2: the viewer's marks layer was sized from the SVG's natural size rather than the shown image, so marks drifted in the Marked view; it is now laid over the image without adding size (verified equal rects in Chromium). Mark IDs combine time and randomness so marks added later cannot collide with saved markup, the markup toolbar drops its dividers when it wraps on narrow screens, and removal confirmation focuses Cancel.

## Play fixes, October 3

After PRs #5 and #6 merged, the owner reported issues from the LAN build and asked for more tools.

- **Plan tools**: a group in the tool rail sets the selected entry's price, adds a stop and adds a target by clicking the chart (one click per use, magnet applies, Esc cancels). A stop or target fills the first blank one of its kind, otherwise it is added in price units; the first of its kind closes 100% of the entry. Adapter: `setPricePicker(active)` and `onPricePick(price)`. Plan tools and the in-chart editor are hidden for read-only plays.
- **Several stops on the chart**: tags read `SL1`, `SL2` when an entry has several stops. The in-chart editor edits a stop's share and can add or remove stops and targets.
- **Leverage**: % levels plot, drag and edit as returns at the play's leverage (see the plays workspace contract).
- **One entry**: the entry legend (with its All / per-entry view switch, October 4) appears only with several entries, and a single entry's tags drop the `E1` prefix (`Entry`, `SL`, `TP1`).
- **Drawing tools**: vertical line (moves in time only, date label), date range (bars and duration) and price range (change and %), alongside the earlier set. Measurements are display-only.
- **Captures**: capture worked in headless Chromium on the LAN build; the image goes to the journal's Evidence tab. A failing or throwing capture now reports an error (for example when browser privacy protection blocks canvas export) instead of leaving the button stuck, and success offers **Show** to scroll to the Evidence tab.
- **Magnet reach** (later the same day): the magnet snaps an anchor or a picked plan price only when a candle's open, high, low or close is within 12 px of the pointer; otherwise the price under the pointer is kept. Before, it always snapped to the nearest of the four, so a pick away from the candles jumped to a candle price.
- **Fibonacci look**: each level has its own color (a chosen color applies to all), light bands fill the space between levels, and labels such as `0.618 (84433)` sit on a background outside the drawing (left when there is room). Clicking anywhere between levels 0 and 1 selects it.
- **Tooltips**: every chart icon button shows a tooltip with its name and one line on how to use it, or why it is unavailable. Hints exist for every drawing kind (`drawingToolHints`, typed per kind). A tool hint banner no longer blocks clicks on the chart beneath it.
- **Tool check**: in headless Chromium on live BTC candles, every drawing tool was created, deselected, reselected by clicking it, moved, resized by a handle (two-point tools) and deleted.
- **Free drawing**: anchors are no longer tied to candles. Lightweight Charts converts pointer x to a whole bar (rounding up) and returns 0 for a fractional bar, so the adapter converts linearly from bars 0 and 1 in both directions. Anchors keep the exact time under the pointer, also between candles and beyond the last one; only the magnet (when on and within reach) snaps. While a drawing or plan tool is active, the chart's bar-snapping vertical crosshair is hidden and a free dashed guide with its time follows the pointer.
- **Fibonacci levels**: 0, 0.236, 0.382, 0.5, 0.618, 0.65, 0.786 and 1, each with a distinct color. The golden pocket (0.618 to 0.65) is gold and filled more strongly than the other bands. Labels stack so close levels never overlap.

## Tool groups and favorites, October 4

- The rail shows the crosshair, then one button per group: Lines (trend, horizontal, vertical), Fibonacci, Shapes and positions (zone, long/short), Measure (price and date range) and Notes. A group button shows its last-used tool (kept in chart preferences); its small arrow opens a panel listing the group's tools, each with a star.
- Starred tools appear in the chart toolbar between the timeframes/view menu and the status, for one-click access. Favorites are stored with the chart preferences (default: trend line, horizontal line, Fibonacci).
- The plan entry tool sets the selected entry's price when it has none; otherwise a click adds a new entry at that price and selects it (one undo step). Before, it only ever moved the selected entry.
- **Revised the same day**: groups with one tool (Fibonacci, rectangle, text) are plain buttons; groups with several (Lines, Positions, Measure) show a corner triangle that opens their panel. A star button beside the toolbar favorites lists every tool for starring. Icons are Vessel-drawn SVGs in the style of common charting tools (anchor dots, range bars, long/short boxes).
- **Long and short positions** replace the combined tool. A click places a default box (140 px wide, target 60 px and stop 40 px from the entry, on the tool's side); a drag sizes it with the stop at 1R on the opposite side whatever the drag direction. Entry, target and stop handles move independently, and the target and stop corners set the width. Labels show target and stop prices with their percentages and the R ratio. Earlier `position` drawings still render.

## Indicators, October 8

The owner asked for four configurable EMAs, volume and RSI, each of which can be shown or hidden, with volume and RSI in their own panes (not drawn over the candles) and controls on the chart itself. This first pass uses split panes and will be refined.

- **Computation**: `components/chart/indicators.ts` computes the indicators from the loaded display candles (the same parsed numbers used for plotting). They are display only, not signals or financial calculations. EMAs start from the simple average of their first `period` closes and are blank before that. RSI uses Wilder's smoothing with 70/30 guides. History is bounded by the loaded candles, so a long EMA starts later on short histories; loading older candles recomputes it. `ChartCandle` now carries `volume` (the venue-reported traded volume from `/candles` and the stream).
- **Defaults**: EMA 9 purple, 50 red, 100 yellow and 200 green, updated at the owner's request on October 12, 2026, Volume and RSI 14 shown. Periods (EMA 1–500, RSI 2–100) and colors (swatches or any custom color) can be changed. Reset to defaults restores them. Settings, including pane sizes, are saved with the chart preferences (`indicators` in `vessel.chart.preferences.v1`) and apply to every chart. Legacy stored EMA slots migrate once, only when their period and color still match that slot's old defaults exactly; customized slots and visibility/pane preferences stay unchanged. Reading an unversioned preference document persists the migrated preferences with `schemaVersion: 2` in the same storage key, and every subsequent save carries that marker. Ordinary normalization never migrates periods/colors. A later deliberate customization matching an old default pair survives save and reload. If browser storage is unavailable, preferences remain in memory.
- **Renderer**: the adapter's `setIndicators(view)` draws EMAs as line series on the price pane and adds Volume (histogram) and RSI (line, fixed 0–100) as Lightweight Charts panes below it, rebuilt in a fixed order when one is shown or hidden. EMAs follow the plan-level autoscale rule: an average more than 50% from the latest close does not widen the price scale. Volume bars use the monochrome candle colors. Pane separators use the theme border color and can still be dragged; a drag is kept until a pane is shown, hidden or resized from its bar.
- **Pane sizes**: normal shares the height with the price pane (about 70% price, 13% Volume, 17% RSI), minimized keeps only the 26 px bar (series hidden; the bar still shows the value) and maximized takes most of the height while the price pane stays visible. Only one pane is maximized at a time. Hiding a pane keeps its size for when it is shown again.
- **Pane layout (October 9, after an adversarial review)**: `components/chart/paneLayout.ts` solves pixel heights with floors of 26 px (bar), 72 px (open indicator pane) and 120 px (price pane). When the chart is too short for every open pane (both open need 264 px of pane space, one open and one bar 218 px), panes are shown as bars, Volume first, then RSI, keeping a maximized pane open longest; the bar says "Too short to plot", offers maximize and hide but no minimize/restore, and the saved size is unchanged, so the plot returns when the chart grows. Both panes can be minimized, hidden and shown again. The adapter waits for the library to draw new panes before measuring, applies heights as stretch factors, re-solves on every container resize, and never writes factors while a pointer is down. A separator drag is kept as preferred proportions (floors still apply, a minimized pane snaps back to its bar) across resizes and candle updates, and dropped when panes are shown, hidden or resized from a bar. Bars are positioned from the pane rows' DOM rectangles, observed with `ResizeObserver` (rows are reused by position, so they are re-bound after each layout) plus a settled report two frames later, so they follow drags with live updates off. The bottom pane's bar label keeps clear of the attribution logo when it is minimized. Round 2 hardening: heights after a release are read only once the chart has drawn the last move, so a quick drag cannot skip the floors; a lost release (outside the window, window blur, or a move with no button down) and a container resize end Vessel's press, and every later release (pointer or the library's mouse-up) reconciles the layout against the last solution, so a drag the library finishes afterwards still gets the floors (a resize is never mistaken for a drag); plot cells are observed and every data update schedules a settled report, so a wider price scale moves the bars too; positions divide out CSS transforms such as the expanded dialog's opening animation; below 72 px of pane space (including none at all) there is no layout and no bars; a compacted pane offers only maximize (unless already chosen), Expand chart (inline only) and hide; stored settings with two maximized panes keep Volume's; caption parts wrap between words when one part is longer than a line.
- **On-chart controls**: a legend on the price pane lists each EMA with its value at the crosshair (or the latest), toggles it, offers hidden panes back, opens the settings popup and can collapse. Each indicator pane has a bar at its top with its value and minimize, maximize and hide buttons; RSI's bar also opens the settings. The adapter reports pane positions (`onPaneLayout`) after layout changes, resizes and separator drags, and the crosshair time (`onCrosshairMove`) for the values. A toolbar Indicators button opens the same popup.
- **Interaction limits**: drawings, plan-level picking, level drags and double-click editing respond only on the price pane; indicator panes keep the chart's own pan, zoom and crosshair. The attribution logo moves to the bottom pane, so level tags avoid it only while the price pane is the only one.
- **Captures**: the canvas capture includes the indicator lines, bars and axis labels but not the HTML legend or bars, so the caption names the indicators as shown (for example `EMA 9/50/100/200 · Volume minimized, not plotted · RSI 14`). The caption wraps onto more footer lines on narrow charts.
- **Known follow-ups**: the legend can cover level tags near the top of the price pane (collapse it if needed); no other indicator types, sources or per-instrument settings yet. Capture fails after the expanded chart is closed until it is reopened; this predates the indicators and is tracked separately.

Verified: frontend tests (indicator math against Wilder's reference values, settings normalization, legend toggles, pane bar actions, settings popup and reset); headless Chromium on the production build with synthetic candles: rendering, crosshair values, minimize/maximize/hide/show, settings popup, expanded dialog, a 420 px viewport, and that a drawing tool ignores clicks in the RSI pane but draws on the price pane. Screenshots and a short demo are in `docs/design/chart-indicators/`. October 9: two code reviews and two plan reviews (Codex) found squashed panes when maximizing, bars lagging after separator drags, drags lost on resize and clipped narrow captions; after the fixes, solver unit tests cover 190–736 px, the 218/264 px thresholds, maximize, compaction order, regrowth and dragged proportions, and headless Chromium confirmed bars aligned with their pane rows after a drag with live updates off, drags kept across resizes, floors on maximize, compaction at 220 px and recovery, and a wrapped narrow caption.

### October 12: stable level dragging

Expanded Suggestions could change height as a stop approached entry, moving the chart beneath the pointer; autoscaling the dragged level then amplified the price jumps. Entry/SL/TP gestures now preview only in the chart, committing the editor, suggestions and notices once on release (one undo step). The price scale is held during the gesture and its previous autoscale setting restored afterwards. Pointer cancellation, lost capture, window blur, resize or chart reset discard the preview instead of saving an accidental price. This applies to both inline and expanded charts; percentage exits still switch to price units on a completed drag.

## Fills on the chart (October 20, 2026)

Phase 1.1b of the [learning-loop contract](phase1-learning-loop-contract.md). It covers G-03 and the F-06 chart.
- **Markers, never lines.** A saved Play's fills are drawn as markers at their time and price: its linked venue fills,
  or its manual ledger's executions.
  - Buys are up arrows in the positive colour; sells are down arrows in the negative colour.
  - Each sits on the candle its execution falls in, within that candle's own span (its interval), so a gap or a lone
    candle never takes a fill from outside it. A fill outside the loaded candles is not drawn.
  - The pointer resting on a marker shows a tooltip: role, side, exact quantity and price, and the UTC time. A visually
    hidden list carries the same text for assistive technology.
  - Plan levels keep their lines and tags, so a fill cannot be read as a level.
- **Adapter.** `ChartAdapter.setMarkers(markers)` uses Lightweight Charts series markers (`atPriceMiddle`). The library's
  hovered-object ID reaches `ChartCallbacks.onMarkerHover`. The renderer stays unaware of Plays.
- **Trade dates.** An ended Play with fills (Closed, Unresolved or retrospective) opens on its trade: the first to the last
  fill, padded either side by a quarter of its length and at least five candles.
  - `ChartAdapter.setFocus(range)` applies the range at a reset and once per new range, so the owner can pan freely.
  - Its candles come from an anchored cache entry: the tail ends at the padded end of the trade, rounded out to whole
    milliseconds and never later than now, and older pages load on scroll as usual.
  - The anchor is part of the chart's view key, so the trade's candles arrive as a reset and the focus applies even
    when today's candles loaded before the fills.
  - Live updates stay off for such a chart, since a live tail would join today onto the trade's candles. Open Plays keep
    today's view and the live tail.
