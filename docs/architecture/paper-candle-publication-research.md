# Paper candle publication — bounded Hyperliquid observation

**Date:** October 21, 2026 UTC. **Scope:** PR32, Opus round-4 conditional
C-residual; evidence only, not integration approval or a policy change.

## Finding

**Insufficient affirmative evidence of prompt, trade-independent publication.**
Neither TNSR nor HMSTR returned the candidate freshly closed minute at the
approximately +10-second or +75-second observations. HMSTR returned older
explicit `n = 0` rows, but none after its latest returned positive-trade candle.
TNSR returned empty candle arrays throughout. [E]

Consequently, **“quiet rows require a later trade” was not falsified for these
samples**. The observations are consistent with that premise, but do not prove
the causal requirement: no subsequent trade/backfill transition was observed,
and another publication, availability or serving issue could explain absence.
**Universal completeness/finality is not established.** The prior historical
360/360-slot sample with 343 zero-trade rows remains historical coverage
evidence, not publication-latency evidence. [E, L1, L2]

## Method and source interpretation

- Public mainnet `POST https://api.hyperliquid.xyz/info`, only native TNSR and
  HMSTR; six `candleSnapshot` reads (`interval: "1m"`) and two `recentTrades`
  reads. No catalogue scan or account endpoint. [E]
- Each candle request covered eight complete minutes plus the elapsed current
  minute: aligned `startTime`, `endTime` at request preparation. Baseline,
  early and later starts were in minutes 00:31, 00:32 and 00:33, respectively.
  Exact bodies, epoch-millisecond bounds and acquisition timestamps are
  preserved. Nothing synthesized absent rows. [E]
- The target native slot was **00:31:00.000–00:31:59.999 UTC**:
  `t = 1792542660000`, `T = 1792542719999`; its close boundary was
  **00:32:00.000 UTC**. Timing offsets below use that boundary, one millisecond
  after inclusive native `T`. The host reported `NTPSynchronized=yes`. [E]
- Official REST documentation describes the request and the most recent
  5,000-candle limit. Official schemas identify `t`/`T` as candle open/close
  milliseconds, `n` as trade count, `v` as base-unit volume and trade `time`
  as milliseconds. The inspected sections provide no explicit zero-trade
  publication deadline or completeness/finality guarantee. [S1, S2]
- Offline analysis checked returned candle symbol/interval, aligned and unique
  opens, `T = t + 59,999`, bounds, integer nonnegative counts and consistent
  decimal OHLC/volume. All returned rows passed those checks; **this validates
  their shape, not completeness**. [E]

**Budget:** eight unauthenticated market-data POSTs, two official documentation
page opens and two cached documentation text lookups: **12 operations even
charging each lookup**. No further requests. API acquisition lasted
00:31:15.953–00:33:15.599 UTC, within four minutes of the first local inspection
at 00:29:30 UTC and below the 12-minute ceiling. All eight responses were HTTP
200; no retries, redirects or 429 occurred. The one-shot probe would stop on
429 and preserve its `Retry-After` header rather than loop. [E]

## Recorded responses

All times are **October 21, 2026 UTC**, unless explicitly dated otherwise.
Counts are per response, not distinct executions summed across repeated reads.
For candle responses, “trades” means the sum of native `n`; an empty array
does **not** establish zero actual trades. [E]

| # | Market / read | Request start → end UTC | Rows | `n = 0` rows | Reported trades | Body bytes |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| 1 | TNSR baseline candles | 00:31:15.953 → 00:31:16.296 | 0 | — | — | 2 |
| 2 | HMSTR baseline candles | 00:31:16.296 → 00:31:16.589 | 8 | 6 | 2 | 1,091 |
| 3 | TNSR early candles | 00:32:10.000 → 00:32:10.304 | 0 | — | — | 2 |
| 4 | TNSR recent trades | 00:32:10.305 → 00:32:10.590 | 10 | — | 10 returned | 2,748 |
| 5 | HMSTR early candles | 00:32:10.591 → 00:32:10.870 | 7 | 6 | 1 | 951 |
| 6 | HMSTR recent trades | 00:32:10.871 → 00:32:11.159 | 10 | — | 10 returned | 2,783 |
| 7 | TNSR later candles | 00:33:15.001 → 00:33:15.306 | 0 | — | — | 2 |
| 8 | HMSTR later candles | 00:33:15.307 → 00:33:15.598 | 6 | 5 | 1 | 816 |

### Native rows and returned trade timestamps

- **HMSTR:** request 2 returned opens 00:23 through 00:30, inclusive.
  Opens **00:24, 00:25, 00:26, 00:27, 00:28 and 00:29** each had `n = 0`,
  `v = "0.0"` and flat OHLC. Their native closes were respectively
  **00:24:59.999 through 00:29:59.999**. Opens 00:23 and 00:30 each had
  `n = 1`, closing at 00:23:59.999 and 00:30:59.999.
  Request 5 retained opens 00:24–00:30; request 8 retained 00:25–00:30.
  Earlier rows leaving the response reflect the moving request start. [E]
- **Fresh target absent:** neither market returned open 00:31 in requests
  3/5 or 7/8. HMSTR's later response also omitted the newly closed open 00:32
  (`T = 00:32:59.999`). The target was absent in responses received at
  **+10.304 / +75.306 seconds for TNSR**, and **+10.870 / +75.598 seconds
  for HMSTR**. These are discrete observations of absence, not measured
  publication delays or continuous observation. [E]
- **TNSR:** all three candle bodies were exactly `[]`. Their complete-minute
  request ranges were respectively 00:23–00:30, 00:24–00:31 and 00:25–00:32.
  Thus some absent requested slots were already older than five minutes.
  This does not establish why those rows were absent or prove current
  instrument availability; no catalogue expansion was attempted. [E]
- **Recent trades:** TNSR returned 10 timestamps ranging from
  **October 20, 22:39:06.461** to **October 21, 00:06:29.938**.
  HMSTR returned 10 ranging from **October 20, 19:32:36.452** to
  **October 21, 00:30:08.211**. Each response contained **zero timestamps
  at or after 00:31:00.000**, and therefore zero at or after the target close.
  These are the latest **returned** trades, not an independently guaranteed
  complete/final trade tape. Recent trades were not fetched again at +75
  seconds; do not extend this timestamp evidence to the later observation. [E]

No fresh `n = 0` row was observed before a subsequent positive-trade row or
returned recent trade. Therefore **no affirmative publication-latency upper
bound can be reported**. If such a row had been observed, close-to-response-end
would only bound that sample's observed availability, never a venue SLA.
Historical carry-price rows, and any current-minute rows, are coverage
observations only—not fills or evidence of a tradable touch. [E; interpretation]

## Interpretation for conditional C

The requested counterexample would be a newly closed native `n = 0` row already
available before any later observed trade. That would justify **“lazy-only
publication falsified for the observed sample,”** not universal eager
publication. This run obtained no such counterexample. It also did not observe
a later trade followed by backfilled rows, so it cannot positively establish
lazy publication. **The conditional residual remains unresolved, not dismissed
by the previous historical coverage result.** [E, L1, L2]

The working policy remains the parent's five-minute trailing-suffix grace
clocked from the missing close, with old/interior holes becoming gaps. This
note neither changes that policy nor approves its residual risk. Missing rows
must not be called zero-trade rows or skipped from these observations alone.
The parent decides the risk response. No application scheduler was instrumented.

## Evidence and provenance

**[E]** All acquisition and offline artifacts are under:

`/home/ops/.cache/vessel-verification/paper-candle-publication-20261021-MkiggqIx/`

- `NN-*.request.json`: exact POST bytes; `NN-*.response.bin`: unmodified
  response entity bytes; `NN-*.headers.txt`: HTTP headers captured by curl.
- `requests.json`: millisecond UTC start/end, bodies, status, byte lengths
  and SHA-256 for requests, responses, headers, curl metadata and stderr.
- `analysis.json`, `analysis-summary.txt`, `offline-verification.json`:
  native open/close/count inventory, missing slots and offline checks.
- `session.json`, `source-provenance.md`, `local-inputs.sha256`:
  limits, source access and hashes of local framing inputs.
- `observe.py`: completed bounded one-shot acquisition; **do not rerun**.
  `analyze.py`: offline-only replay, with no network calls.
- `SHA256SUMS`: SHA-256 inventory of all other artifact files.
  Manifest SHA-256:
  `ea39c8b20aa0bb5e4cc5d22d590139b5578e8bf75b9d0d18a56e9f496a8a67d0`.
  `analysis.json` SHA-256:
  `681cb541fe9f93684a8816c135808e617554952cafe2efe82d33ae803698514b`.

Response-body SHA-256, keyed to the request table:

| Requests | SHA-256 |
| --- | --- |
| 1, 3, 7 (`[]`) | `4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945` |
| 2 | `8a44794ee239de3e6c954a2e31bbe61040a7337ad52afe5c4826289e85b334d6` |
| 4 | `3c331e2f818d248ce02039b400a1ceb98272a83decde256e0a0e38ef7c294773` |
| 5 | `7d0f5ff75b779c5213ac1915ee225a91cc337c98062b33121450e44234fd6b55` |
| 6 | `a29b30b8107a15733ac3b4681020b54fcaeda7d707c2ad443c5db4aff3a96127` |
| 8 | `9704534a43ec11b3be7413f9a8d33007f84abc8dcf0847c855b3c4bd7b513182` |

Primary sources inspected during preparation on October 21, 2026:

- **[S1]** Official Hyperliquid REST info reference, candle snapshot and recent
  trades: `https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint`.
- **[S2]** Official Hyperliquid candle/trade schemas:
  `https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/websocket/subscriptions`.

Local framing, not venue guarantees:

- **[L1]** `docs/architecture/paper-candle-coverage-research.md`,
  October 20 historical sample.
- **[L2]** `/home/ops/.cache/vessel-verification/opus-r4-9c66d65-wCgj14/REPORT.md`,
  “C-residual — conditional: lazily published quiet minutes.”

Only this new repository note was written. No existing docs/code edits,
builds, application tests, containers, commits, pushes, delegation, owner
environment/data/accounts/authentication or browser profiles were used.
