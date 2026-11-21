# Performance plan: candle caching and chart responsiveness

October 11, 2026. Status: **PR A and PR B authorized by the owner** (see [Owner decisions](#owner-decisions)).

The owner asked how to make chart loading faster, for example by caching candles already viewed so switching back or refreshing is shorter, what to cache and how to invalidate it, and what else would improve performance across the app, especially when working with the chart.

Two Codex Sol agents researched the backend and the frontend separately (read-only), the orchestrator merged their findings with its own reading, and a third Sol agent reviewed the draft ("approve with changes"). This version includes all of the review's required changes.

Every proposal keeps the existing rules: exact decimal strings outside display arithmetic, prices are observations and never fills, owner and enabled checks on every request, no secrets in logs, cache keys or browser storage, drafts discarded on reload and disconnect, capability-driven venue behaviour, and manual refresh for Lighter and RISEx (no background polling added).

## Where the time goes today

### Loading and switching

- **Browser:** `useCandles` (`frontend/src/features/market/useCandles.ts`) is keyed by account, instrument and interval. Any key change or remount drops the candles and fetches a fresh 500-candle window. Switching 1h → 4h → 1h, or BTC → HYPE → BTC, always waits for the venue round trip again. Older pages loaded by scrolling are lost as well. **This is the main cause of slow switch-back.**
- **Server:** `CandleCache` (`backend/src/Vessel.Application/MarketData/CandleService.cs`) keeps each response for 10 s. The latest-window key uses `to` rounded down to 10 s, so a return after 5 s hits only about half the time and a return after 10 s never does. Older pages use an explicit `endTime` and also expire after 10 s, although most of them hold only closed candles. When 256 entries are reached and none have expired, the whole cache is cleared. Concurrent identical misses each call the venue. Explicit Refresh within the same 10 s bucket returns the cached response.
- **Metadata:** Hyperliquid reads `meta` on every catalogue request; Lighter reads `orderBookDetails` before candles and account reads. RISEx already caches its catalogue for one minute.
- **Transport:** there is no response compression. A 500-candle response is about 74 KB of JSON and about 7 KB gzipped (synthetic measurement).
- **Guard:** each chart endpoint loads a tracked full `Account` to check owner, enabled state and venue, including on cache hits.
- **Bundle:** the main chunk is 692 KB (212 KB gzip) because Plays, Review and Settings are imported eagerly. The chart renderer is already a separate 198 KB (65 KB gzip) chunk, but its download starts only when the chart mounts, and a failed import is not handled.
- **Saved Plays:** opening a saved Play waits for every evidence image before the draft is built (`frontend/src/features/plays/saved.ts`), so image-heavy Plays delay the chart.

### Working with the chart

- **Dragging is not frame-batched.** Level drags (`lightweight.ts`) and drawing moves (`drawingController.ts`) publish on every `pointermove`. Each publish replaces the whole Play draft in the shell, re-rendering the editor, journal and suggestions; for saved Plays it also serializes the plan for dirty checking.
- **Live ticks are linear in loaded history.** Each streamed candle copies the array, converts every candle's six strings to numbers, recomputes all EMAs, RSI and volume, rebuilds the time index and price format, and calls `setData` on the candle series and every indicator series. At 5,000 candles with all indicators that is about 35,000 points handed to the renderer per tick. The indicator arithmetic itself is cheap (about 1.8 ms median at 5,000 bars); renderer replacement and allocation are the larger cost.
- **Market-context ticks** re-render the chart subtree, including the unmemoized `CandleChart`, but not the whole Plays page and not the indicators.
- **Expanding the chart** shares one data state and one stream (no refetch), but unmounts the inline chart and creates a new renderer, so zoom and scroll are lost.

## What to cache

| Data | Where | Lifetime | Key |
|---|---|---|---|
| Historical candle windows | Server memory | 24 h | venue + native market ID + interval + window |
| Tail candle windows | Server memory | 10 s (unchanged) | same |
| Empty windows | Server memory | 60 s | same |
| Instrument catalogue (full validated metadata, incl. retired markets) | Server memory | 60 s | venue + catalogue variant |
| Market context snapshot | Server memory | 10 s (unchanged) | venue |
| Candle series already viewed | Browser memory, per connection | Session; shown at once, then revalidated | account + instrument + interval |
| Authorization (owner, enabled) | **Never cached** | — | — |

Server entries hold public venue data, so they are shared across accounts after each request has passed the account guard. Browser entries are keyed by account, which keeps eligibility, errors and invalidation per account; a Lighter main account and subaccount each keep their own copy.

Closed candles are **stable but not guaranteed immutable**: venues can correct late trades. "Historical" therefore never means "permanent".

Not cached: SSE events (already live), owner financial data (overview, fills, snapshots, Plays), credentials, `/api/system` (contains owner identity).

## Server rules

### Lookup order

1. Account guard on every request: a no-tracking projection (exists for this owner, enabled, venue), keeping the owner query filter. Never skipped on a cache hit.
2. Resolve the instrument to its current native market through the cached catalogue (60 s). A retired or unknown market is rejected before any candle cache lookup, with a distinct problem ("This market is no longer listed.") rather than the generic venue failure, so the browser can drop its entry instead of showing stale candles. Interval support is checked against the same metadata.
3. Look up the candle cache by venue, **native** market ID and interval, so a reused symbol never inherits a replaced market's history.

### Tail or historical

A window is **historical** only if the request has an explicit `endTime` and

```text
endTime ≤ now − 3 × maxDuration(interval)
```

where `maxDuration` is the interval's longest possible length (1w = 7 days, 1M = 31 days; fixed intervals their own length). Everything else is **tail**: requests without `endTime`, future `endTime`, and windows ending later than the cutoff. This conservative rule covers the forming candle plus two closed candles without epoch-modulo arithmetic, venue week alignment or calendar months. Uncertain means tail.

- Tail windows: 10 s (unchanged, including the 10 s bucket for the latest window).
- Historical windows: 24 h, unless they are empty.
- Empty windows (either kind): 60 s.
- Failed reads are never cached.

### Requests by purpose

- **Activation** (opening or switching to a chart): normal cache rules.
- **Explicit Refresh**: a new `refresh=true` query parameter bypasses the cache for the latest window and replaces its entry. Coalesced with concurrent identical refreshes.
- **Older paging** (`endTime` = oldest − 1): historical rules apply.
- **Reconnect recovery**: same as activation for the latest window.

### Bounds and concurrency

- Bounded by both entry count (for example 1,024) and total candle count (for example 200,000), evicting least-recently-used entries one at a time, never a full clear.
- Single-flight per cache key, per venue context snapshot and per catalogue. The shared venue read runs with its own deadline; a cancelled waiter stops waiting without cancelling the read for others. In-flight entries are removed when they complete or fail. (The RISEx catalogue cache lets the first caller's token cancel the load, so it is a model for caching but not for cancellation.)
- Compression (Brotli/gzip) for successful candle, catalogue and market-context JSON only. Not SSE, credentials, evidence or error responses. `Cache-Control: no-store` stays.

## Browser rules

### Cache

- `features/market/candleCache.ts`: an in-memory store per `WorkspaceApi` connection, keyed by account + instrument + interval. It survives chart and page unmounts but not reload. Disconnect, logout or a new API instance clears it.
- Each entry keeps the exact venue candles, their numeric display form, the notice, `historyExhausted`, the latest observation time and, separately, **the time each older page was fetched**.
- Bounds: at most 20 entries (LRU by last view) and 5,000 candles per entry. The entry on screen is never evicted. When an entry reaches 5,000 candles, older paging stops with a "Local history limit reached" notice; the cache never drops a page it just loaded and never reports this as venue history exhausted.
- A generation counter per entry and per connection: results of requests started before an invalidation are discarded, so they cannot repopulate an evicted entry.

### Activation and revalidation

- On activation the cached candles are shown at once with their existing "Updated hh:mm" time, then the latest window is requested. The chart says it is refreshing until the response arrives.
- Merging: closed candles take the REST value (corrections win). For the forming candle see "REST and live updates" below.
- Older pages fetched more than 24 h ago are dropped on activation; the latest-window revalidation and live updates do not renew their age.
- **Gap rule:** if the fresh window's oldest candle is newer than the cached series' newest candle (no overlap in fetched ranges), the cached series is replaced by the fresh window and older paging restarts from there. Overlap is judged by fetched ranges, never by price continuity or fixed-step timestamps; missing slots can be legitimate no-trade periods. No synthetic candles. RISEx opens are kept exactly as received, including opens outside high/low.
- Refresh keeps today's meaning: the latest window (now with `refresh=true`), not all retained history.

### Failures

- 401/403: the connection is no longer authorized; clear the whole cache, as on disconnect.
- 404/409 (account deleted, disabled or manual) or "market no longer listed": drop the entry and show the existing error state, not stale candles.
- Venue failure (502) or network error: keep the cached candles with the existing "Refresh failed … Showing the previous candles." notice.
- Account disable or delete in the app evicts that account's entries and aborts its requests.

### REST and live updates

SSE candles carry no revision, and a relay can replay an older forming candle to a late subscriber, so arrival order does not prove freshness. Rules:

- Live candles are written into the same cache entry.
- While a latest-window request is in flight, live candles are buffered, coalesced by open time and tagged with the request generation. When the response arrives, the REST snapshot is merged first, then the buffer is reapplied. The buffer is discarded on invalidation, and on a failed request it is applied to the cached series.
- For the **same open time**, a forming candle only advances: the value with the larger trade count (or larger volume when trades are absent) wins; equal counts take the later arrival. For closed candles (open time before the current forming candle) the REST value wins.
- Live events for older open times (corrections) are applied, not ignored.
- When the stream reports `reconnecting` or `stale` and then `live` again without the browser stream ending, the browser revalidates the latest window, the same way it does after a browser reconnect today.

## Delivery

### PR A: switch-back and load caching

1. Browser candle cache with all the rules above, plus renderer preload when the Plays page opens and handling of a failed renderer import.
2. Server: catalogue cache with single-flight, native-identity lookup and the "no longer listed" problem, the tail/historical classification, entry and candle bounds, single-flight candle reads, `refresh=true`, and restricted compression.
3. No-tracking guard projection.

Latest-window server behaviour stays at 10 s, so switch-back speed comes from the browser cache. The server changes reduce venue reads for paging, catalogues and concurrent requests.

**State (October 12):** implemented. The server's `BoundedMarketDataCache` (LRU, single-flight, 30 s independent read deadline) holds candles (1,024 entries, 200,000 candles), the market-context snapshot and the Hyperliquid, Lighter and RISEx catalogues (60 s). Account and order imports refresh the Hyperliquid and Lighter catalogues instead of reusing them, so a market listed within the last minute is not filtered out of their facts. Candle reads resolve the catalogue first and are keyed by native market ID. The browser cache is `frontend/src/features/market/candleCache.ts`, one per `WorkspaceApi`. The gap rule compares the new window's `requestedFrom` with the end of the fetched REST coverage; live candles never extend that coverage. A failure from a read that a newer one superseded is ignored. As before the cache, an explicit refresh replaces a pending older read. A 409 drops only that chart's entry, so other charts of the account keep streaming; a 404 ends all of the account's charts. Try again after a dropped entry starts a fresh one with `refresh=true`. Verified with the backend suite including PostgreSQL in a throwaway container (931 passed; the 2 live-venue tests skipped) and the frontend typecheck, lint, tests (683 passed) and build. Sol (Codex) review rounds: request-changes, then two narrow follow-ups on the retry path, then approve.

### PR B: chart interaction

1. Batch level drags and drawing moves to one draft update per animation frame; flush the final position on `pointerup` even before the next frame; one undo step per gesture; cancel on `pointercancel` and unmount. Updates that round to the same tick are skipped for levels only, not for drawing moves along the time axis.
2. Incremental rendering: convert only changed candles; `series.update()` for the forming and newly opened bars; full `setData` only on reset, paging, corrections or trimming; rebuild the time index and price format only when they change. Indicator arithmetic stays a full computation for now; only the changed indicator points go to the renderer.
3. Memoize `CandleChart` with stable callbacks and keep market statistics and update-time re-renders out of the chart subtree.

**State (October 12):** implemented. `FrameEdit` (`frontend/src/components/chart/frameEdit.ts`) publishes level and drawing drags at most once per frame while the renderer draws the live preview; release publishes the final pointer position. A drag cut short by a chart reset, unmount, lost pointer capture or a release off the price scale records the moves already applied as one undo step. History actions carry the scope (account and instrument) where the gesture began and are dropped if the history has moved on, so a chart torn down by an instrument switch cannot write into the new instrument's history. Gestures that end where they started record nothing, and level suppression starts from the level's current price. `createSeriesWriter` (`seriesWriter.ts`) compares candles and indicator points by value and uses `update()` for tail changes, otherwise `setData`; the time index and price format are rebuilt only when they change. `CandleChart` is memoized, and `LiveChart` passes `onNeedOlder` and `onExpand` as stable callbacks. During a drag the chart still re-renders once per published frame. Verified with the frontend typecheck, lint, tests (676 passed) and build.

### Combined verification (October 12)

The subsequent [level-drag stability fix](chart-plan.md#october-12-stable-level-dragging) keeps frame batching and incremental rendering, but level moves now preview only on the chart and commit the workspace on release. Interrupted **level** gestures discard their preview rather than recording a partial move; drawing gesture history is unchanged. The price scale stays fixed during a level drag so autoscaling cannot feed back into pointer prices.

The combined implementation passed `scripts/check.sh` (737 frontend tests, typecheck, lint and production build) and the PostgreSQL-enabled backend suite (933 passed, two opt-in live-venue tests skipped). Tests used a throwaway PostgreSQL container; the owner database was untouched. Sol independently approved the merged chart/cache/renderer paths and the subsequent error-overlay fix.

Manual browser checks used a synthetic account and real public Hyperliquid candles: BTC/ETH and 1h/4h switching, cached revalidation, explicit refresh, a simulated 503 retaining previous candles, a real disabled-account 409 clearing candles, re-enable/retry, level and rectangle dragging with one-step undo/redo, Volume/RSI controls, expansion without a new candle read, live updates, older paging (four 500-candle pages), and disconnect. Browser storage held chart preferences, not candles or credentials.

On the production build at 1402 × 877, cached switch-back was visible after two animation frames in 42, 58, 55 and 69 ms while a deliberately delayed 1.2-second revalidation remained pending. Local API samples were 426 → 21 ms for repeated tail reads and 274 → 4 ms for a repeated historical window. Successful candle/catalogue responses used gzip and `Cache-Control: no-store`; warmed-cache unauthenticated/disabled requests returned uncompressed 401/409 errors. These are local smoke measurements, not cross-device performance guarantees or a 5,000-candle drag-FPS benchmark.

Manual checking found the chart canvas covering the error overlay's **Try again** button. The state overlay now sits above the renderer and drawing controls. A native browser click after re-enabling the test account restored candles with exactly one `refresh=true` read. To repeat the hit-testing regression check, trigger a candle error with no retained data, then run this in the browser before clicking **Try again** (also check the expanded chart):

```js
const retry = [...document.querySelectorAll('button')].find(button => button.textContent === 'Try again')
const box = retry.getBoundingClientRect()
const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2)
console.assert(retry === hit || retry.contains(hit), 'The chart must not intercept retry clicks')
```

### Later, after measuring A and B

- **Progressive evidence loading** for saved Plays (build the draft first, load images after, keep unloaded images distinct from removed ones so a save never deletes them). A separate PR because of the data-loss risk.
- **Incremental EMA/RSI state**, only if profiling shows the arithmetic matters.
- **Server bar store**: candles per series and open time with fetched coverage, responses assembled from a cached closed prefix plus a small tail read, fed by the existing Hyperliquid relay (no new socket). Per-venue horizons: Hyperliquid 5,000 candles and request weight; Lighter 500 per read and manual refresh; RISEx native intervals, no WebSocket.
- **Growing-history queries**: sizing, review, execution and the Plays list load full histories. Join exact linked order/fill pairs and project summaries. Consider fill indexes `(OwnerId, AccountId, OrderId)` and `(OwnerId, AccountId, OccurredAtUtc DESC, Id)` only after `EXPLAIN` on a synthetic database. November 2026 (issue #106): `(OwnerId, AccountId, OccurredAtUtc)` is added, with a trigram index on `play_search_texts.Folded` for journal search, after `EXPLAIN (ANALYZE, BUFFERS)` on a seeded database (`DatabaseIndexPlanTests`); an idle account's newest-fill read went from a backward scan of the owner's fills (18,000 rows filtered) to a four-buffer index-only scan.
- **Page code splitting** (Plays, Review, Settings) with the Plays session kept in the shell.
- **One renderer across expand**, if measured expand latency justifies the focus and resize work.

### Not recommended now

- TanStack Query or a general query cache: it would not fix the imperative chart hot path. Revisit only if overview, Plays, sizing, execution and review fetching are deliberately centralized.
- Prefetching favourite timeframes: speculative venue weight. At most one debounced request for the current interval on hover or focus of an instrument.
- Web workers for indicators: the arithmetic is not the bottleneck.
- Long browser `max-age` on API responses: a cached response would skip the server's newly-disabled-account check.
- IndexedDB for candles: would make the first chart after reload instant, but changes the rule that market data is not stored in the browser and adds cross-session schema and cleanup.
- A PostgreSQL candle archive: survives restarts and could keep history beyond Hyperliquid's horizon, but it is a product capability needing a migration and retention bounds.
- Venue HTTP/2 or connection tuning without a benchmark; new retries that multiply venue weight.

## Tests

Backend (the pinned SDK 10.0.302 is installed user-locally in `~/.dotnet`; the system `dotnet` is 10.0.112):

- Classification: cutoff ±1 ms, no `endTime`, future `endTime`, `1w` and `1M` (February and leap years), inclusive 501-open windows, empty and sparse windows.
- Identity: canonical `1000…` versus native `k…`, same symbol with a replaced market ID, retirement while cached, unsupported interval after a metadata refresh.
- Bounds and concurrency: entry and candle limits, empty-entry pressure, single-flight with one cancelled waiter, failed reads not cached and cleaned up, `refresh=true` bypass and coalescing.
- Guard runs on cache hits; compression excludes SSE and errors.

Frontend:

- Switch-back shows cached candles without waiting; revalidation merge; gap replacement; 24 h page expiry not renewed by live updates.
- Accounts: Lighter main and subaccount isolation, disable or delete during REST and SSE, 401/403 clears the cache, late responses from an old connection discarded.
- Live: buffered ticks with REST success and failure, candle rollover during refresh, stale relay replay, older correction events, upstream reconnect without the SSE ending, refresh versus paging completion order.
- Bounds: 20-entry LRU, local history limit during paging and live appends, no repeated paging loop.
- Interaction: `pointerup` before the scheduled frame, `pointercancel` and unmount, one undo step, same-tick suppression for levels only; incremental rendering equals a full `setData`.

Browser timing at 1402 × 877 against live Hyperliquid: first chart; switch-back (target: candles visible in under 100 ms, measured separately from revalidation finishing); live tick cost with 5,000 candles and all indicators; drag frame rate with the editor visible.

## Owner decisions

October 11, 2026:

1. PR A (caching) and PR B (chart interaction) are authorized as scoped above.
2. The browser cache is session-only memory; candles are not stored in the browser across reloads.
3. The server cache is in-memory for now. The owner's longer-term direction is a **TimescaleDB candle archive** that keeps more history than the venues expose and later supports backtesting strategies on the stored data. It needs its own plan (collector job, instruments and intervals, retention, storage budget, licensing, migration from the plain PostgreSQL image) and is not part of PR A or PR B. PR A keeps cache keys by venue, native market, interval and window so a persistent store can sit behind the same lookup later.

### Archive storage estimate

Rough sizing for the archive discussion. Assumptions: one row per candle with exact `numeric` OHLCV, about 140 bytes per row uncompressed including the primary-key index, and about 10–20 bytes per row with TimescaleDB columnar compression (segment by market, order by time). Storing 1m candles and deriving 5m to 1d with continuous aggregates adds about 30% rows.

| Rows per instrument and year | 1m | 5m | 15m | 1h | 4h | 1d |
|---|---|---|---|---|---|---|
| Candles | 525,600 | 105,120 | 35,040 | 8,760 | 2,190 | 365 |

| Scope, per year | Uncompressed | Compressed |
|---|---|---|
| 1 instrument, 1m + derived | ~95 MB | ~7–14 MB |
| 10 instruments, 1m + derived | ~1 GB | ~70–140 MB |
| 50 instruments, 1m + derived | ~5 GB | ~0.35–0.7 GB |
| ~200 instruments (most of Hyperliquid), 1m + derived | ~19 GB | ~1.4–2.8 GB |
| 50 instruments, 1h only | ~60 MB | ~5–10 MB |

Disk space is not the constraint; history availability is. Hyperliquid's REST API returns only the latest 5,000 candles per interval: about 3.5 days of 1m, 17 days of 5m, 52 days of 15m, 208 days of 1h, 2.3 years of 4h and 13.7 years of 1d. Deep 1m history therefore has to be **collected going forward** by an always-on job (the existing shared WebSocket or bounded polling), while coarser intervals can be backfilled to the venue horizon once. Lighter and RISEx have their own limits. Older history from other sources would need separate research.

## API cold start and cookie authentication (October 18, 2026)

The owner reported a slow first load and slow "message to sign" after an API start. Three behaviour-preserving changes, from the sign-in and post-sign-in load investigations:

- **ReadyToRun image.** `backend/Dockerfile` publishes the API and worker for `linux-x64` with `PublishReadyToRun`, framework-dependent, with the default tiered compilation and dynamic PGO, no trimming or AOT. The two executables list `linux-x64` in `RuntimeIdentifiers`, so the locked restore covers the RID graph (their lock files only gain an empty `net10.0/linux-x64` target); libraries stay RID-agnostic and the migration bundles are unchanged. The image grows by about 160 MB uncompressed (`/app` 202 → 360 MB).
- **Read-only warm-up before readiness** (`StartupWarmup`, `IdentityWarmup`). After Kestrel listens, a background pass builds both EF models, runs the cookie-session, challenge-by-ID and wallet-by-address lookups and recovers a fixed public EOA vector (Wallet mode), then runs the Overview, event head, notifications, monitor, Plays and Activity reads once for a fresh random owner with no rows. `/health/ready` answers 503 `{"status":"unavailable","reason":"warming-up"}` until it finishes or `Vessel:Startup:Warmup:Timeout` (default 30 s, 1–60 s so the gate always ends inside the image healthcheck budget) passes; the timeout is enforced with `WaitAsync`, so a database call that ignores cancellation (a stalled handshake) cannot hold readiness. Then every existing readiness check applies unchanged. Gating readiness is deliberate: Compose starts web only once the API is healthy, so the first user no longer pays first-use costs, and the bound keeps a broken warm-up from blocking a deployment. Invariants: no writes (a test runs it against `default_transaction_read_only=on` and requires every step to succeed), no challenge or session is issued or consumed, nothing is cached (every request still authenticates against the database), logs carry step names and exception types only, failures never stop the host. Enabled by default outside Development (`Vessel:Startup:Warmup:Enabled`). The worker has no request path and does not warm up. The challenge path is mostly its write transaction and is not warmed.
- **One read per cookie-authenticated request** (`SessionStore`). Session, user and wallet come from one `LEFT JOIN` statement instead of three SELECTs, with the same refusal codes in the same order. The conditional idle-refresh UPDATE is skipped when the row just read shows no refresh is due: `LastSeenAt` only moves forward, so the UPDATE could not have matched. When due, it keeps its revocation and expiry predicates, so a concurrent revoke still wins. The common request is one statement instead of four.

`/api/system` stays registry-based: several tests replace `IVenueRegistry`, so a descriptor-only path would split its source of truth for a sub-millisecond gain.

Measured with the packaged images in Wallet mode against throwaway PostgreSQL 18 and RabbitMQ on one workstation (direct Kestrel, no TLS/nginx; 5 cold starts per row, cold p50 in ms; warm is p50 of 30 after 20 untimed):

| Request | Before, first after readiness | After (R2R only) | After (R2R + warm-up) | Before, first request at all | After, first request at all | Warm before → after |
|---|---:|---:|---:|---:|---:|---:|
| `GET /api/auth/session` | 157 | 48 | 32 | 713 | 186 | 2.54 → 1.73 |
| `POST /api/auth/challenge` | 241 | 90 | 92 | 777 | 253 | 9.1 → 9.5 |
| `POST /api/auth/verify` | 397 | 233 | 174 | 981 | 336 | 10.3 → 10.3 |
| `GET /api/system` | 94 | 51 | 16 | 704 | 192 | 1.64 → 1.45 |
| `GET /api/overview` (empty journal) | 200 | 128 | 61 | 1,036 | 328 | 3.65 → 3.26 |

Launch to first 200 from readiness (polled every 50 ms): before 2.21 s p50, R2R only 1.30 s, R2R + warm-up 1.55 s (the warm-up itself took 0.74 s in Wallet mode). "First request at all" skips readiness and races the warm-up, the worst case for a direct caller. These are cold-start gains; warm latency is unchanged except the one-statement session read.

## 2026-10-18 — activity aggregation per facts revision (F1)

Overview Summary, Activity and operation expansion now share the existing exact
aggregation through an Application-owned `IActivitySummaryReader`, implemented
in Persistence. The arithmetic, native identity rules, funding attribution,
coverage, ordering and decimal strings are unchanged. The Activity version hash
is calculated once alongside the aggregate, in its original display order.
Filtering, paging, facets, rolling windows and response timestamps remain at the
request boundary; the cached aggregation and funding code read no clock.

Each read opens a fresh, explicitly assigned owner scope/DbContext and a
RepeatableRead transaction, then reads `activity_facts_revisions` inside that
snapshot. The cache key is **owner + revision + optional detail account + sorted
account set**, distinguishing unrestricted and empty sets. The winning cache
miss materializes facts in that snapshot, commits and releases its transaction/
connection, then aggregates while retaining its independent owner scope; other callers dispose their probe snapshots. Concurrent
callers of the same key share one computation. Individual request cancellation
only removes that waiter; the last departing waiter cancels and detaches the
flight so a new caller can retry. Failed/cancelled/abandoned work is never cached.
A write committed between a revision read and facts loading cannot mismatch the
key and facts: both reads use that same PostgreSQL snapshot.

The per-process LRU admits at most 64 entries and (by default) 256 MiB of conservative retained
graph charges (row/object/list/allocation overhead plus variable string lengths).
An oversized result is shared in flight but not retained. Old revisions compete
in this same bound rather than accumulating indefinitely. This is memory
accounting, not a claim about exact managed-heap/RSS usage; active requests still
need their own working memory. Owners always have separate keys and assigned
query filters. Authorization is performed normally on every HTTP request and is
never cached; HTTP responses retain `no-store`.

### Input and writer audit

`ActivityStore.ReadAsync` reads enabled accounts, all their retained fills,
snapshots with positions, funding payments/states, and linked-order identities.
`LinkedOrders` joins **play_order_links → imported_orders**, not Plays; fill/Play
associations are derived from the account/contract/external-order composite key.
The downstream positions, aggregator and funding code read only those facts and
the process's immutable venue capabilities. No linked-fill table, Play plan,
review, event, notification or credential is read. Portfolio labels/icons and
workspace balances are still queried outside the aggregate cache.

Migration `20261019100000_ActivityFactsRevision` covers INSERT, UPDATE and DELETE
on `accounts`, `imported_fills`, `imported_orders`, `play_order_links`,
`funding_payments`, `account_funding_states`, `account_snapshots`,
`account_positions` and (conservatively) `account_stablecoins`. Whole-row account
updates cover enabled status, deletion, portfolio moves, names, source/venue,
history notices and sync state. This includes API imports/refreshes, execution
link/unlink flows, account management/bulk membership and separate monitor worker
connections; database triggers cannot be omitted by an EF bulk write. Existing
account owners initialize at revision 1; absent owners read revision 0, and their
first input write inserts revision 1. Deleting the last account retains the
revision row. Rollbacks/savepoint rollbacks do not advance the committed revision.
No xid, append-log maximum, count or wall clock is used as a detector.

Statement transition tables gather both old/new owners into transaction-local
settings. An INITIALLY DEFERRED constraint trigger takes revision row locks only
at commit, once per transaction, in sorted owner order (including multi-owner
offline DML). Existing account/FK/advisory locks have already been acquired;
revision updates acquire no account/sync/link lock afterward. The other
transactions reaching these locks are likewise completing and cannot request
an earlier account lock. Current foreign keys are not deferred and there are no
other application deferred triggers. Tests exercise a linking transaction that
has written its link and waits on an account held by sync: sync can commit and
release it; neither write has held a revision lock prematurely. Another test
commits an older xid after a newer one and requires both increments. Keep this
lock-order invariant if adding deferred triggers; application code must not
force these constraint triggers immediate. Ordinary app writes never do so.
Administrative TRUNCATE, disabled triggers, direct edits to the revision table or
a live database restore are outside application DML; stop processes for such
operations, as the existing restore runbook requires.

Apply the migration explicitly with the journal bundle/EF workflow before
starting the new hosts. Nothing migrates at startup, changes the owner's database
in tests, alters an `IUnitOfWork` write boundary or adds infrastructure dependencies
to Domain/Application. The new counter table is migration-managed infrastructure,
like the server-state tables; its only runtime read explicitly filters the owner.

### Verification

Focused tests compare serialized cached/missed/uncached Activity and Summary
responses at a fixed clock across all/portfolio/unassigned/account scopes,
filters, pages, periods and grouping. They also cover separate-connection writes
to every fact table, link dismissal/confirmation/removal, account rename/move/
disable/enable/delete, real refresh unit-of-work imports/funding/snapshots,
owner isolation, snapshot consistency, migration backfill, rollback, failure,
cancellation, single-flight and LRU/oversized-entry eviction. The isolation
harness now explicitly requires the revision table and still fingerprints it;
its reversible-write self-test checks restored facts then rebases the legitimately
advanced revision.

### 50,000-fill Release benchmark

Replayed the investigation's existing `seed.sql` and `bench.py` in throwaway
PostgreSQL 18/RabbitMQ containers, with the unchanged `e38658d` source and this
branch both running `dotnet run --project src/Vessel.Api -c Release --no-build
--no-launch-profile` in `mcr.microsoft.com/dotnet/sdk:10.0.302`, Production/Token
mode, each API limited to two CPUs. Seed parameters were `rts=625`, `plays=300`,
`events=5000`, `notes=3000`: 10 accounts, 50,000 fills, 20,000 funding payments,
300 Plays and 420 links from the available harness (the old report listed 374).
Both versions read the same database; only the explicit revision migration was
added after capturing the original responses.

The shared host became busy during measurement, so the final comparison
**interleaves** baseline → committed no-op account UPDATE → cache miss → cache
hit for every round. The no-op still advances the revision; its SQL time is
outside the measured request waterfall. These are 30 measured rounds after
12 warm-up rounds, with extra baseline JIT warm-up, replaying the original
system → event head → Overview → four parallel page reads, alongside the
monitor/notification/SSE branches. No frontend scheduling change is included.

| Populated Overview (ms) | p50 | p95 |
| --- | ---: | ---: |
| Original full aggregation on every request | 810.4 | 932.7 |
| Revision miss after a committed write; one shared computation | 650.0 | 752.0 |
| Same revision, cache hit | 41.0 | 56.4 |

The warm hit is about 20× faster. Absolute timings reflect this shared host,
not the older report's quieter run. Five large baseline payloads (Activity
pages 1/7, a filtered page and both Summary groupings over all history) are
identical after removing only request-generated `asOfUtc`; fixed-clock tests
also compare serialized responses without normalization. Size accounting caches
its fixed type/property metadata so misses do not repeatedly allocate reflection
metadata for every retained fill. All benchmark containers are discarded; no
owner database or live venue is used.

Final validation in the pinned SDK container, from `backend/`, with disposable
`Vessel_TEST_POSTGRES` and `Vessel_TEST_EVENT_RABBIT` enabled:

- `dotnet build Vessel.slnx -c Release --no-restore`: passes, zero warnings/errors.
- `dotnet test Vessel.slnx -c Release --no-build --logger 'trx;LogFileName=f1-final.trx'`:
  **3,355 passed, 0 failed, 5 skipped, 3,360 total** (4 m 44 s), including all ten
  new cache/revision tests. Conditional skips are the three live venue checks
  and two external S3 checks; no skipped check is counted as a pass.
- The benchmark wrapper (`paired.py`, using the supplied `bench.py`) and raw
  results are retained locally under the ignored
  `backend/tests/Vessel.Tests/TestResults/activity-cache-benchmark/` directory.

## 2026-10-18 — activity cache review corrections

The unshipped `20261019100000_ActivityFactsRevision` migration is corrected in
place. Its transaction-local bumped-owner set replaces the single boolean:
`INSERT A; SET CONSTRAINTS ALL IMMEDIATE; INSERT B; COMMIT` now advances both
owners exactly once. Immediate row events also collect their own old/new owners
before the statement transition-table collector runs. Savepoint rollback restores
both owner sets. Normal deferred firing still locks all pending owners in sorted
UUID order at commit. Application writers must continue to leave constraints
deferred: forcing early firing preserves invalidation, but takes revision locks
before later account locks and cannot guarantee global lock ordering across
separate firings. Both functions capture the migration's search path, so a
writer changing its search path cannot redirect or break revision-table access.

All writers of the ten facts tables must use **READ COMMITTED**, including
operator SQL/import tools. A RepeatableRead/Serializable writer whose snapshot
predates another same-owner commit can fail at commit with PostgreSQL `40001`
on the shared revision row, even if its fact rows do not conflict. `UnitOfWork`
now requests ReadCommitted explicitly rather than inheriting a server/session
default; an integration test verifies a real fact write under a Serializable
session default. The existing RepeatableRead stores are read-only; keep new
facts writes inside the unit of work.

The collector filters out already-recorded owners before aggregation and skips
`set_config` entirely for repeated owners. Five interleaved trials of 50,000
single-row INSERT statements in one transaction (PL/pgSQL loop, PostgreSQL 18,
primary key and account FK, including COMMIT/deferred events) had median server
times of **2,169 ms before / 1,300 ms after**; the FK-only control was **763 ms**.
These are synthetic local timings, not an end-to-end refresh claim.

Last-waiter cancellation detaches the flight under the gate, then invokes token
callbacks outside it. Callback failures cannot replace caller cancellation or
corrupt flight state, and disposal waits for cancellation callbacks to finish.
A blocked, throwing callback test checks that unrelated hits and a replacement
flight remain usable while that callback is still blocked.

A producer commits/disposes its RepeatableRead transaction and releases its
connection as soon as all AsNoTracking facts are materialized, before CPU
aggregation. Revision and facts remain from one snapshot. An integration test
checks the transaction, connection and tracker during aggregation; existing
snapshot and serialized-equivalence tests continue to apply.

`Vessel:Activity:CacheCapacityBytes` (`Vessel__Activity__CacheCapacityBytes` in the
environment) configures the per-process retained-charge limit; default
**268435456 bytes (256 MiB)**, zero disables retention while preserving
single-flight, and negative values fail configuration. The 64-entry LRU limit
remains. Oversized entries are shared only in flight; old revisions and distinct
account scopes compete for the same capacity. This limits retained cache charge,
not process RSS or working memory for active requests. Each API replica has its
own cache.

An isolated Release console probe loaded and aggregated the existing synthetic
benchmark data, warmed EF/JIT first, disposed the context, then compared forced-GC
managed-heap totals with/without a retained summary (three runs per size).
At **50,000 fills + 20,000 funding rows**, steady deltas were **56.37–56.38 MB**
(**53.8 MiB**) against **86.44 MB (82.4 MiB)** charged. At **100,000 + 40,000**,
they were **112.69–112.70 MB (107.5 MiB)** against **172.78 MB (164.8 MiB)**
charged. The first post-warm run had about 0.55 MB additional runtime retention;
these deltas estimate retained managed memory, not an exact heap graph or RSS.
The probe confirmed one computation for two consecutive reads at both sizes
with the default cache. Keep the conservative charge (~1.53× these steady
measurements): 256 MiB admits the measured 100k entry or two measured 50k keys,
while long strings, more funding or different operation shapes can still require
a larger configured budget. Lazy constituent DTOs were not materialized to
measure or charge the summary.

The 50k populated-Overview waterfall was rerun against pre-review `32c5ff0` and
these corrections, with separate Production/Token SDK-container APIs limited to
two CPUs and the same disposable PostgreSQL/RabbitMQ dataset. Each of 42
interleaved rounds advances the revision with a committed no-op account UPDATE,
then measures a miss and hit for each API; before/after order alternates and the
first 12 rounds are warm-up. Across the remaining 30 rounds, miss p50/p95 was
**399.3/463.1 ms before → 414.8/497.9 ms after**; hit p50/p95 was
**22.2/34.2 ms → 22.3/32.0 ms**. No miss-latency improvement is claimed from this
noisy shared-host run: the correction releases the transaction/connection sooner
without reducing aggregation CPU. Five large Activity/Summary payloads were
identical after normalizing only request-generated `asOfUtc`; fixed-clock tests
still compare serialized responses directly. Benchmark scripts, raw samples and
probe outputs are in the ignored
`backend/tests/Vessel.Tests/TestResults/cache-review/` directory; the isolated
probe and pre-review build source are under root `TestResults/cache-review/`.

Final correction validation used `mcr.microsoft.com/dotnet/sdk:10.0.302`, from
`backend/`, with only throwaway PostgreSQL 18 and RabbitMQ enabled through
`Vessel_TEST_POSTGRES` / `Vessel_TEST_EVENT_RABBIT`:

- `dotnet build Vessel.slnx -c Release --no-restore`: zero warnings/errors.
- `dotnet test tests/Vessel.Tests -c Release --no-build --filter 'FullyQualifiedName~ActivityCache|FullyQualifiedName~ActivitySummaryCache' --logger 'trx;LogFileName=cache-review-focused.trx'`:
  **16 passed, 0 failed, 0 skipped** (13 s), including the exact INSERT/early
  constraint reproduction and blocked-callback regression.
- `dotnet test Vessel.slnx -c Release --no-build --logger 'trx;LogFileName=cache-review-full-final.trx'`:
  **3,361 passed, 0 failed, 5 skipped, 3,366 total** (4 m 31 s). The skips are
  three live venue checks and two external S3 checks; they are not passes.

All benchmark/test API, PostgreSQL and RabbitMQ containers were removed. No
owner database, live migration, deployment or publication was involved. Remote
PostgreSQL cancel latency and the browser/nginx abort path were not measured.

## Combined PR verification (October 19, 2026)

The API performance work now depends on the observed-leverage/sizing PR. The revision migration is `20261019100000_ActivityFactsRevision`, after `LeverageObservations`, and installs its INSERT/UPDATE/DELETE invalidation triggers on `account_leverage_observations` too (40 triggers across ten tables). Cache memory charging includes retained leverage observations and per-fill leverage allocations. Open-operation leverage depends only on retained observations; closed lifetimes still bound attribution.

The opaque Activity version now hashes every displayed summary field, sorted coverage and full funding DTO details incrementally, once per aggregate, without materializing execution rows. This corrects previously missed metric/coverage changes. Startup work is separately scheduled so its timeout covers synchronous model construction; readiness completes before cancellation cleanup, which observes callback/read faults and retains the cancellation source until both finish. Three timeout and six data-version regressions were demonstrated failing before their fixes. See [the verification record](pr-3-6-verification.md).

The previous unshipped revision ID `20261018130000_ActivityFactsRevision` must not remain in development migration history: recreate those disposable databases. Existing installation migrations remain explicit; no live database or deployment is part of this verification. Original benchmark measurements above predate these correctness follow-ups and were not rerun.

## Phase 2 benchmark (item 14d)

`scripts/benchmark.py` brings the 50,000-fill method above into the repository for item 14d of the
[Phase 2 contract](phase2-risk-coverage-contract.md). It is a manual measurement on a chosen host: nothing runs
in CI, and only its pure parts (the dataset's shape and determinism, the percentile math, the safety guard) are
unit-tested, in `scripts/test_benchmark.py`. Results are recorded in
[phase2-risk-coverage-state.md](phase2-risk-coverage-state.md).

Run it from the repository root with the pinned SDK, Node.js and OpenSSL on `PATH`, alone on the host.
Set `NOBLE_MODULE_DIR` to an absolute directory outside the checkout holding `@noble/curves` and
`@noble/hashes` v2 for `scripts/wallet-test-signer.cjs` (the same synthetic signer as release checks):

```sh
python3 scripts/benchmark.py --disposable --years 3 --scale 1 --warmup 5 --rounds 20 [--out /tmp/bench.json]
```

What it does:
- **Containers.** It starts its own `postgres:18-alpine` and `rabbitmq:4.3.6-management` containers (the
  deployment example's images and default settings), labelled with the run, on loopback ports with generated
  passwords.
- **Migrations.** It builds `Vessel.Api` in Release (`--no-build` reuses that build) and applies the migrations
  explicitly in `deploy/migrate`'s order: `check-config`, the identity and journal migrations
  (`dotnet ef database update --no-build`), `identity ensure-owner` and `provision-messaging`. Nothing migrates
  at startup.
- **Dataset.** One owner's journal, deterministic from `--seed`, `--years` (default 3) and `--scale` (1 is about
  50,000 fills over 3 years), ending at `--anchor` (default: now, to the hour):
  - portfolios; Hyperliquid, Lighter and RISEx accounts, including an unassigned, a disabled, a manual and a paper
    account;
  - orders and fills as signed-position round trips with partial fills, flips and still-open positions that match
    the latest snapshot; hourly funding, leverage and risk observations, import conflicts;
  - Plays in every status with plan revisions, if-then rules and pre-flight checks, linked orders, structured
    reviews, log entries and image notes; chart drawings the chart accepts (a few lines on most drawn Plays, a busy
    chart on two instruments for some, about 820 KB across every market for 3%);
  - Lighter forced events with their markers, fee-bearing and missing-P&L omission markers, coverage records,
    history gaps and backfills; paper drafts.

  It loads everything in one COPY transaction through `psql` in the container, so every constraint and trigger
  applies and the Activity facts revision is the one the triggers leave. It then runs `VACUUM (ANALYZE)` and the
  offline `play-search-rebuild --apply`, and counts the rows.
- **API.** It binds the synthetic operator wallet with the offline identity command and starts the built DLL
  in Production/Wallet mode over loopback HTTPS, without a launch profile or worker modules. Registration stays
  Closed. The client trusts its ephemeral certificate, waits for `/health/ready`, signs the real API challenge
  and times authenticated reads with the resulting session cookie and owner header. Cookies are kept in memory
  and redacted from failures; cleanup removes the certificate and key.
- **Timing.** Activity (first and a deep page, a filter, one expanded operation), Review (all, year, with
  imported positions), the Plays list (tabs, a deep Closed page, counts, title and instrument search), a Play's
  editor load (the Play with the largest drawings and one without), Sizing, a Closed Play's execution view, Overview
  and its summary, open risk and journal search (common word, a deep offset, rare word). Each round visits every
  endpoint, in an order rotated by one per round. It reports p50 and p95 after the warm-up rounds, and the wire
  and JSON sizes (gzip is accepted; the API's restricted compression leaves these JSON reads uncompressed).
- **Deep pages.** The deep page or offset is the middle one of the list, chosen from the counts the API reports
  (the Closed Plays count is checked against the generated one). Each check requires that exact page or offset
  with rows, so a clamped or empty page fails. A deep-page scenario the dataset cannot populate (everything on
  one page, as in small smoke runs) is skipped and named in the report. Any other response that is not 200, or
  not the expected non-empty JSON, fails the run.
- **Cache.** Reads through the Activity facts cache are made right after a committed no-op write to a retained fill,
  which advances the execution revision so the aggregate is computed again, then right after a write that only moves
  one snapshot's observation time (issue #107), then repeated at once. The API exposes no cache
  diagnostics, and the cache refuses an aggregate whose charge exceeds its capacity (256 MiB by default), so
  the repeat is not always a hit. The report's Cache column estimates the charge from the generated rows by the
  cache's own rule and calls the repeat a hit only well under the capacity; near or above it the repeat is
  "unconfirmed" or "not cached". At 3 years, scale 1 estimates about 128 MiB (a hit) and scale 2 about
  238 MiB (unconfirmed).
- **Report.** A Markdown block for the state document: parameters, git SHA, host, phase times, the API's peak
  RSS, row counts and timings. `--out` also writes the timings as JSON; it writes nothing else.

Safety:
- It refuses to run without `--disposable`, and under CI. It never reads `.env`, takes no connection string and
  touches no other database: its children get `scripts/dev.py`'s filtered environment plus its own settings.
- Secrets are generated per run, passed through the environment, never a command line, and redacted from any
  error it prints.
- Cleanup runs in `finally`: after errors, Ctrl+C and SIGTERM. It stops the API, then removes the containers it
  tracked (each name is recorded before its `docker run`) or finds by the run's label, each only after checking
  that label, with their volumes, and deletes its temporary directory. It reports any container it could not
  remove. Nothing runs after SIGKILL or a host crash: then remove the run's containers by hand with
  `docker ps -a --filter label=vessel-bench.run` and `docker rm -f -v`.
- On a shared host, serialize it with other heavy jobs (for example under `flock`). `--min-available-mb` waits
  for free memory before the build, the migrations, the seed and the API start.

## Drawings in their own table (issue #110, November 20, 2026)

Chart drawings moved from the `plays` row to `play_drawings` (migration `PlayDrawingsTable`). Measured with
`scripts/benchmark.py` at seed 1, 3 years, scale 1, anchor `2026-11-20T12:00Z`, 5 warm-up and 20 measured rounds,
one after the other on the same shared 8-CPU QEMU host. Both sides seed the same Plays and drawings (774 Plays; the
largest drawings document is 807 KiB on the wire): head `c6d8e4aa` into `play_drawings`, base `0f184dd3` with the
same generator writing them into the old `plays."Drawings"` column (a local copy of the script, not committed).

| Read | Base p50 / p95 ms | Head p50 / p95 ms |
| --- | --- | --- |
| Plays, All tab | 4.1 / 7.2 | 4.3 / 8.7 |
| Plays, In progress tab | 3.2 / 6.4 | 2.8 / 4.2 |
| Plays, Closed tab page 12 | 4.0 / 9.6 | 3.6 / 5.0 |
| Plays, title and instrument search | 4.0 / 9.9 | 3.7 / 5.6 |
| Review, all | 132.7 / 194.2 | 99.2 / 136.8 |
| Review, year | 128.0 / 168.8 | 89.9 / 104.8 |
| Sizing | 76.0 / 126.6 | 59.5 / 76.1 |
| Play editor load, largest drawings (807 KiB) | 19.1 / 29.5 | 14.2 / 17.8 |
| Play editor load, no drawings | 3.1 / 4.3 | 3.5 / 4.3 |
| Activity, page 1 (not affected; control) | 11.4 / 21.3 | 10.0 / 12.8 |

Response sizes are unchanged. The host was busier during the base run (load 8.2–8.9 against 4.7–7.9), and reads
that #110 does not touch, such as Activity, were also faster on head, so part of the difference is load. Review
and Sizing, which read closed Plays without needing their drawings, gained 25–30% at p50, more than the control;
the Plays list was already fast at this size and stays within noise. The editor load still returns the whole
drawings document, as before.

Not measured: one list row with large drawings against an otherwise identical row without. The list rows were
already projections that never selected drawings, and full Play loads no longer map them, so that comparison is
guaranteed by the query shape rather than timed here.

## 2026-11-20 — Activity cache at four times today's size (issue #107)

Activity, the Overview summary, row expansion, Sizing's Activity fallback and a Play's funding proof read one cached
aggregate of every retained fill. Before this change it was keyed by the facts revision, which every monitor check
moved by saving a newer snapshot, so with several monitored accounts most reads recomputed it; every hit also re-sorted
all operations and rebuilt the facets, and an account's row expansion read and walked that account again although the
owner-wide entry held it.

**Execution revision.** Migration `20261120143000_ActivityExecutionRevision` adds `execution_revision` to
`activity_facts_revisions`. Deferred constraint triggers (`activity_execution_commit`, `activity_execution_update`)
move it at commit, once per transaction per owner in sorted owner order, with the facts revision's own owner
collection, for every change to `imported_fills`, `funding_payments`, `account_leverage_observations`,
`execution_omissions` and `imported_forced_events`, for inserts and deletes of `accounts`, `imported_orders` and
`play_order_links`, and for updates of the columns that decide what the aggregate reads from them (account owner,
enabled and trading mode; order owner, account, contract and external ID; link owner, account, Play, order and
state). Snapshots, positions, wallet balances, funding coverage and account sync state move only the facts revision,
which still fences the funding proof. The lock-order invariant above is unchanged: both counters lock the same rows,
only at commit and in the same order. Apply the migration explicitly; existing owners start at execution revision 1.

**Observations are rebased, not ignored.** The cache key is the execution revision. Each read takes, in one
RepeatableRead snapshot, both revisions and the scoped accounts, latest snapshots with positions and funding coverage
(`ActivityObservations`), then `ActivityService.Rebase` moves the cached aggregate onto them. It reuses the walk only
when everything the walk, coverage, funding attribution and leverage read is the same: the account set and each
account's venue, history notice, enabled state and trading mode; each snapshot's completeness and positions
(contract, native market, exact quantity and leverage); each funding state's covered window, P&L basis and notice;
and each snapshot time, unless it moved past a retained instant of its account (a fill, funding settlement, leverage
observation, omission bound or funding coverage bound, widened by a millisecond for the funding window's rounding).
Anything else walks the cached history again in memory, without another database read. Either way the result carries
the new snapshots and facts revision, so freshness still reaches the anchoring and comparison proofs, unrealized marks
and the funding fence; a changed funding attempt time alone rehashes the version. The cache keeps the newest rebased
value. A monitor check that changes nothing but the observation time therefore costs one small observation read and
no aggregate work (a check whose snapshot passes the leverage observation the previous change recorded walks once).

**Hits sort nothing.** The aggregate keeps its operations in page order and its facets; an Activity page filters and
pages that list.

**One copy per account read.** An account-scoped read (row expansion, a Play's funding proof) that misses narrows a
cached aggregate over several accounts at the same execution revision, when that holds all of the account's facts
(it keeps only the newest forced events across its accounts), instead of reading the account again.

**Memory.** The cached fills, funding payments and leverage observations share one copy of each market, side,
venue-wording and fee string, and the cache charges each distinct value once. An aggregate whose charge exceeds the
capacity is still not retained; the cache now logs a warning about it, at most hourly, naming the fill and operation
counts, the estimate and `Vessel:Activity:CacheCapacityBytes`.

**Benchmark.** `scripts/benchmark.py` now also times `/api/sizing` and a Closed Play's execution view (its funding
proof reads the account's Activity aggregate), seeds chart drawings of realistic size (the frontend's persisted
`ChartDrawing` shape: usually a few to a few dozen per instrument, sometimes hundreds, from their own random stream so
the rest of the dataset is unchanged), and times cached reads after a no-op fill write ("After fill write", a
recomputation) and after a write that only moves one snapshot's observation time ("After observation", what a quiet
monitor check writes). `--cache-capacity-mib` runs the API with a chosen `Vessel:Activity:CacheCapacityBytes`.

### Figures

Measured on `602905f8518d` (benchmark changes, current code) and base `b41b2608` (the same
benchmark script against the base API), seed 1, 3 years, anchored 2026-11-20T00:00Z, 3 warm-up and 10 measured rounds,
on the shared 8-CPU, 20 GB development VM while other agents' jobs were queued behind it (host load 2–4.5). The host has
only the .NET 10.0.13 runtime, so the Release API and the EF migration bundles were built with SDK 10.0.302 in a
container and the bundles stood in for `dotnet ef database update`; everything else is `scripts/benchmark.py` with its
own throwaway PostgreSQL 18.6 and RabbitMQ containers. Scale 1 aggregates 45,928 fills into
15,532 operations; scale 4 aggregates 181,741 into 61,672.
Medians (p50) in milliseconds, base → current; "steady" is the repeat, "fill write" the first read after a no-op fill
write, "observation" the first read after only a snapshot's observation time moved.

| Read | Scale 1 steady | Scale 1 fill write | Scale 1 observation | Scale 4 steady | Scale 4 fill write | Scale 4 observation |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Activity, page 1 | 9.3 → 7.3 | 2,517.3 → 2,389.3 | 737.6 → 21.2 | 2,691.8 → 2,605.3 | 5,323.9 → 5,042.9 | 2,713.8 → 2,641.9 |
| Activity, page 79 of 100 rows / page 309 of 100 rows | 8.9 → 7.0 | 767.9 → 768.3 | 736.3 → 21.3 | 2,676.2 → 2,640.7 | 2,584.4 → 2,661.7 | 2,605.9 → 2,647.0 |
| Activity, closed longs in 90 days | 9.4 → 7.0 | 731.7 → 756.4 | 749.5 → 20.6 | 2,666.9 → 2,666.5 | 2,640.9 → 2,660.2 | 2,692.6 → 2,641.4 |
| Activity, executions of one operation | 2.7 → 3.5 | 109.2 → 110.1 | 97.8 → 4.4 | 3.0 → 4.2 | 475.2 → 491.0 | 520.9 → 4.9 |
| Review, all | 133.2 → 124.3 | – | – | 472.3 → 469.6 | – | – |
| Review, year | 121.3 → 110.4 | – | – | 415.9 → 406.3 | – | – |
| Review, all with imported positions | 625.1 → 608.1 | 1,369.7 → 1,336.8 | 1,340.6 → 624.1 | 5,088.7 → 5,099.8 | 5,340.3 → 5,114.9 | 5,160.1 → 5,094.6 |
| Plays, All tab | 4.4 → 4.3 | – | – | 4.7 → 4.4 | – | – |
| Plays, In progress tab | 3.3 → 3.4 | – | – | 3.8 → 3.6 | – | – |
| Plays, Closed tab page 12 / Closed tab page 47 | 3.8 → 3.8 | – | – | 5.5 → 5.5 | – | – |
| Plays, tab counts | 2.1 → 2.3 | – | – | 3.5 → 3.2 | – | – |
| Plays, title and instrument search | 4.0 → 3.8 | – | – | 5.7 → 5.7 | – | – |
| Sizing | 77.9 → 75.0 | – | – | 274.2 → 257.4 | – | – |
| A Play's execution view | 27.2 → 27.7 | 193.1 → 190.5 | 206.4 → 32.4 | 37.6 → 37.7 | 438.3 → 412.1 | 433.0 → 38.1 |
| Overview | 13.0 → 13.2 | – | – | 31.0 → 31.7 | – | – |
| Overview summary by portfolio, all | 190.2 → 173.8 | 904.7 → 926.3 | 884.8 → 195.0 | 3,485.6 → 3,402.4 | 3,397.6 → 3,418.0 | 3,507.2 → 3,382.2 |
| Overview summary by account, 30 days | 24.0 → 22.2 | 753.6 → 776.8 | 747.6 → 36.5 | 2,688.6 → 2,750.3 | 2,683.6 → 2,712.5 | 2,706.7 → 2,681.6 |
| Open risk | 9.8 → 9.9 | – | – | 16.7 → 17.7 | – | – |
| Journal search, common word | 27.8 → 28.3 | – | – | 132.0 → 127.5 | – | – |
| Journal search, offset 300 / offset 1150 | 30.9 → 31.2 | – | – | 138.2 → 136.2 | – | – |
| Journal search, rare word | 8.1 → 8.8 | – | – | 23.1 → 23.3 | – | – |

API peak RSS: scale 1 1,059 → 985 MB; scale 4 2,298 →
1,964 MB.

The cache estimate for every enabled account is 123 MiB at scale 1 (a hit) and
425 MiB at scale 4, above the default 256 MiB capacity: at scale 4 the owner-wide aggregate
is still never retained, so Activity, the Overview summary and Review with imported positions recompute it on every
read, before and after this change (and the API now logs the warning). Account-scoped reads (one operation's
executions, a Play's execution view) fit (est. 78 MiB) and no longer recompute after an
observation. With `--cache-capacity-mib 1024` (`Vessel:Activity:CacheCapacityBytes` = 1 GiB) at scale 4, current code
(peak RSS 2,644 MB):

| Read | Steady | Fill write | Observation |
| --- | ---: | ---: | ---: |
| Activity, page 1 | 10.6 | 4,838.5 | 49.5 |
| Activity, page 309 of 100 rows | 10.8 | 2,673.8 | 54.0 |
| Activity, closed longs in 90 days | 11.2 | 2,688.6 | 50.8 |
| Activity, executions of one operation | 3.5 | 468.0 | 4.7 |
| Review, all with imported positions | 2,461.6 | 5,113.6 | 2,492.4 |
| A Play's execution view | 37.5 | 411.9 | 37.9 |
| Overview summary by portfolio, all | 724.2 | 3,359.6 | 840.9 |
| Overview summary by account, 30 days | 71.8 | 2,733.9 | 111.6 |

**What it shows.** A monitor check that moves nothing but the observation time no longer recomputes the aggregate:
the first Activity read after it fell from about 740 ms to about 21 ms at scale 1, and from about 2.7 s to about 50 ms
at scale 4 when the aggregate fits the cache. Cache hits sort nothing (about 9 → 7 ms per Activity page at scale 1).
A recomputation after a real history change costs what it did. At four times today's size the default capacity is the
limit: the owner-wide aggregate needs about 425 MiB by the cache's conservative charge, so an installation of that
size needs `Vessel:Activity:CacheCapacityBytes` raised (here 1 GiB, at about 2.6 GB peak RSS) or a smaller retained
representation. The charge's 768-byte per-row base, about twice the retained size measured on October 18, is
unchanged here.

## 2026-11-20 — Activity read model (issue #108, first slice)

At four times today's size the owner-wide Activity aggregate (about 425 MiB by the cache's charge) never fits the default
cache, so every Activity read recomputed it (about 2.6 s, above). Activity pages, filters and counts now come from a
per-account read model in SQL, built by the same aggregate; see the read model section of `activity-operations.md` and
`deploy/README.md` (migration `20261120200000_ActivityReadModelTables`, commands `activity-rebuild` and `activity-verify`,
setting `Vessel:Activity:ReadModel`).

**Equivalence.** `scripts/benchmark.py` now runs `activity-rebuild --apply` and `activity-verify` after seeding and fails
the run on any difference. `activity-verify` found no difference on 15,532 stored operations at scale 1 and 61,672 at
scale 4. The integration tests compare every Activity response field (pages at two sizes and past the end, every filter,
scope, account and account detail, coverage, facets, unattributed funding, unrealized marks and the version) between the
two paths over the golden journal and a synthetic journal, before and after history, observation and position changes.

**Figures.** Measured on `a6d69fb0`, seed 1, 3 years, anchored 2026-11-20T00:00Z, 3 warm-up and 10 measured rounds, on the
shared 8-CPU, 20 GB development VM (host load 2.3–7.5 while other agents' jobs ran), API built with SDK 10.0.302.
"Before" is the same build with `--activity-read-model memory` (`Vessel:Activity:ReadModel=Memory`, the in-memory
aggregate with #107's cache), so the code under test is the only difference. Medians in milliseconds, before → after;
columns as in the #107 table.

| Read | Scale 1 steady | Scale 1 fill write | Scale 1 observation | Scale 4 steady | Scale 4 fill write | Scale 4 observation |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Activity, page 1 | 7.1 → 13.4 | 1,663.1 → 2,324.8 | 21.9 → 14.2 | 2,675.5 → 32.0 | 4,876.9 → 3,955.8 | 2,679.9 → 32.9 |
| Activity, page 79 of 100 rows / page 309 of 100 rows | 7.4 → 17.1 | 733.9 → 492.4 | 20.4 → 17.1 | 2,662.7 → 45.2 | 2,663.2 → 1,625.8 | 2,653.0 → 45.3 |
| Activity, closed longs in 90 days | 6.4 → 6.9 | 727.4 → 480.1 | 19.5 → 8.2 | 2,635.6 → 9.4 | 2,653.8 → 1,633.8 | 2,673.0 → 10.8 |
| Activity, executions of one operation | 3.3 → 3.6 | 100.5 → 108.8 | 4.5 → 4.7 | 3.7 → 4.0 | 478.0 → 482.2 | 4.9 → 4.9 |
| Review, all with imported positions | 539.4 → 534.4 | 1,293.5 → 1,312.9 | 557.0 → 574.3 | 4,945.9 → 4,984.2 | 4,901.2 → 4,970.3 | 4,948.2 → 4,959.7 |
| Sizing | 56.3 → 58.3 | – | – | 194.1 → 199.1 | – | – |
| A Play's execution view | 27.0 → 28.5 | 188.6 → 194.3 | 30.6 → 31.9 | 37.8 → 36.9 | 424.6 → 414.2 | 39.1 → 39.1 |
| Overview summary by portfolio, all | 163.0 → 170.4 | 887.9 → 916.0 | 178.1 → 184.4 | 3,415.0 → 3,493.0 | 3,389.8 → 3,429.7 | 3,373.6 → 3,481.3 |
| Overview summary by account, 30 days | 23.0 → 24.2 | 761.8 → 779.0 | 39.1 → 37.3 | 2,708.1 → 2,755.1 | 2,730.4 → 2,720.8 | 2,752.4 → 2,749.8 |

API peak RSS: scale 1 974 → 967 MB; scale 4 1,966 → 1,979 MB (the in-memory aggregate is still built for the reads
listed below). Phases: `activity-rebuild --apply` plus `activity-verify` took 7.8 s at scale 1 and 16.3 s at scale 4.

**What it shows.** At scale 4 every Activity page read falls from about 2.6 s to 9–45 ms, and a read after a routine
monitor check (only an observation time moved) costs the same as a steady one: nothing is rebuilt. A filtered page is
under 10 ms. At scale 1, where the in-memory aggregate fits the cache, a cache hit is a few milliseconds faster than the
SQL page (the page reads its rows and observations from PostgreSQL each time); a deep page of 100 rows costs about
17 ms. After a history change only the touched account is rebuilt, then the version is hashed again from the stored
rows, all scoped funding payments and markers: 0.5 s at scale 1 and 1.6 s at scale 4 for most reads. The first read
after the write ("Activity, page 1", the first Activity endpoint of each round) costs more in both modes, as in the #107
table; the cause was not isolated here. Reads still built from the in-memory aggregate (Review with imported positions,
the Overview summary, row expansion, Sizing's fallback, a Play's funding proof) are unchanged.

**Storage.** Operations are kept in two tables: `activity_operations` holds only what SQL counts, filters and sorts by,
and `activity_operation_rows` the roughly 1 KB list row a page returns. With one wide table the planner sorted whole rows
for a deep page and spilled to disk (an EXPLAIN on 4,800 operations: 9.9 ms with a 6 MB external merge; after the split
1.8 ms with an in-memory quicksort). `toast_tuple_target` does not help here: PostgreSQL only toasts rows above about
2 KB.

**Remaining for #108.** Aggregate on write in the same transaction (per account and contract), so the read after a
history change rebuilds nothing; serve Review's unassigned count and positions, the Overview summary and the sizing
fallback from the stored rows; then retire the in-memory path once `activity-verify` has passed on real data.
