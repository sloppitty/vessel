# Play lifecycle

October 3, 2026. After PR #4 merged, the owner asked to save Plays, link the instrument on its venue and settle the Play lifecycle. The owner agreed to the model below and asked to build it directly in the repository, without Plane or Outline updates for now.

## Three independent tracks

October 13 diary completion adds explicit manual executions and retrospective
origin. Read [the bounded diary contract](diary-completion-contract.md).
Manual execution records on manual accounts can move a Planned Play through
Open to Closed. Corrections retain their before/after history and cannot reopen
a terminal Play. Retrospective Plays are created Closed after the owner
confirms eligible imported facts; they do not manufacture a Planned transition.
Structured review and all later review amendments preserve frozen plans.

Execution/link/plan mutations now serialize through account, execution advisory
and Play locks. Fresh plan/fill checks happen under those locks. This protects
terminal links and filled or linked plan references from stale concurrent
changes. The existing sync operation still holds its account transaction while
reading the venue; the new diary paths do not make venue HTTP requests.

A Play's lifecycle is split into three tracks rather than one combined status:

| Track | Driven by | Values |
|---|---|---|
| Status | The owner, and later linked venue facts | Draft, Planned, Paused, Open, Closed, Cancelled |
| Execution progress | Linked venue orders and fills, derived | e.g. "2 of 3 entries filled · 65% of planned quantity in · 30% out" |
| Review | The owner | none, reviewed, amended; every revision kept |

Partial entries and partial exits are progress within Open, not extra statuses. An entry can fill and reach its first target while another entry is still resting, so status values such as "partially in" would multiply. Waiting for a review is not a stage of the trade either. Reviews are their own record, available for Closed and Cancelled Plays ("why didn't I take it?").

## Status

```
Draft ──► Planned ◄──► Paused
  │          │            │
  ├──────────┴────────────┴──► Cancelled (reason)
  │          ▼
  │        Open ──► Closed
  └──► deleted (Drafts only)
```

- **Draft**: the idea. Freely editable, saved on the server, not yet committed. Only Drafts can be deleted; deleting one removes its evidence.
- **Planned**: committed to the plan, orders expected on the venue. Planning records plan revision 1. The account and instrument are fixed from here on.
- **Paused**: orders withdrawn while the idea stays valid. Only reachable before any fill. Resuming returns to Planned. A fill that arrives while paused still wins: the Play becomes Open and is flagged for review.
- **Open**: at least one linked entry fill.
- **Closed**: compatible linked exit quantity equals linked entry quantity, execution chronology is valid, no fixed-size filled order has known missing executions, and no linked entry order is still resting. Over-exits and contradictory fills prevent closure. A flat venue position is not enough, because concurrent Plays can share one account and instrument.
- **Cancelled**: never filled. Keeps a reason (invalidated, missed, changed mind, expired, mistake, other) and an optional note. Missed and abandoned ideas are review material.

Closed and Cancelled are final. A follow-up idea is a new Play rather than a reopened one. Imported history without a plan will enter as Open or Closed, marked retrospective, with no invented thesis.

## Plan revisions

Stops, targets and entries that have not been taken can always change, because conditions change. Each change after planning is a new **plan revision** with the reason the owner gives, so analysis can compare the original plan with what was done ("the stop moved twice").

- Draft edits are free and create no revision.
- Planning saves revision 1, reason "Planned".
- In Planned, Paused and Open, a plan change requires a reason and creates the next revision. Title, drawings and the review note do not create revisions.
- Revisions are append-only snapshots of the plan: direction, sizing, leverage, budget override, entries with stops and targets, thesis, invalidation, strategy and evidence notes.
- Once entry fills are linked, direction stays fixed. Only entries without fills can change their entry price or share. Stops and targets can still change with a revision reason. Since October 19, 2026, a filled entry's stops may sit at or beyond it (see [Managed stops](#managed-stops)).

### Pre-flight at Plan it (October 20, 2026)

Phase 1.5 of the [learning-loop contract](phase1-learning-loop-contract.md).
- **The checks.** Planning a Draft may carry the pre-flight the owner saw.
  Each check is passed, skipped or unknown:
  - a priced stop for every priced entry;
  - entry, stop and target shares each total 100%;
  - thesis and invalidation are written;
  - risk is within the sizing setting at the current ladder level;
  - the estimated liquidation lies beyond every stop.
- **Storage.** The server accepts only those codes and outcomes, each at
  most once. It stores them in a fixed order on plan revision 1, in the
  nullable `Preflight` column added by the explicit `PlanPreflight`
  migration. History shows them.
- **Never blocking.** The checklist never blocks planning. Plan it always
  shows it first, with **Plan it anyway** and **Back to the plan**, also when
  every check is met.
- **Limits.** Resuming a Paused Play is not planning a Draft, so a pre-flight
  sent then is refused with 400. Older plans have none, and nothing is
  backfilled.

## Review completion and the in-trade log (October 20, 2026)

Phase 1.2 of the [learning-loop contract](phase1-learning-loop-contract.md),
decision 3. It covers D-14 and G-04.
- **Completion is explicit.** A Play's review is complete only after one of
  these:
  - **Complete review** on a Closed or Cancelled Play
    (`POST /api/plays/{id}/review/complete`). It works with unchanged or
    empty text.
  - **A save that changes structured answers** on such a Play.
  - **No review needed** (`/review/not-needed`) on a Cancelled Play. The
    server checks the Play has no linked or manual fill.
- **What never completes it.** Writing or editing review text never
  completes it, at any status. `reviewedAtUtc` keeps meaning "review last
  edited".
- **Review again.** `/review/reopen` clears the completion. Text, answers and
  history stay.
- **History.** Each action takes `expectedVersion` (409 when stale). Each is
  kept in review history as `completed`, `reopened` or `not-needed` beside
  `edited`.
- **Lists.** Needs review and Reviewed read the completion, and so does
  Review's completed-review count. Summaries carry `reviewCompletion`
  (`completed`, `not-needed`, `legacy`) and `reviewCompletedAtUtc`.
- **Legacy.** The explicit `ReviewCompletion` migration records a `legacy`
  completion for exactly the Plays the old Reviewed view listed: ended, with
  review text, answers or a review time. It uses their review time, else their
  end time. Nothing else is reclassified, and no text moves.
  - An Open Play with a note in its Review box needs review once it closes.
- **In-trade log.** `GET` and `POST /api/plays/{id}/notes` hold append-only,
  timestamped entries on any saved, planned-origin Play.
  - An entry is up to 2,000 characters, with an optional state: calm,
    rushed, FOMO, tired or after a loss.
  - Entries cannot be edited or deleted.
  - An entry is idempotent by its request ID. It does not take the Play's
    version, because it changes nothing the Play holds.
  - An entry never counts as a review and never feeds Review.
  - The journal shows them in a Log tab after Evidence.

## Managed stops

October 19, 2026, Phase 0 item 0.5b of the [trust and correctness contract](phase0-trust-correctness-contract.md#05-managed-stops), under the owner's decision 2.

- **Stops at or beyond a filled entry.** Once an entry has a linked fill (one that does not contradict the entry side) or a manual entry execution, its price stops may sit at or beyond the entry: break-even or locked-in profit. It is a plan change after planning, so it is a revision with a reason. Entries without fills keep the wrong-side rule, and targets always do. Percent stops cannot reach past the entry: use a price. `0 %` is break-even at the planned entry, so, like a price stop at the entry, it needs a fill on the entry (the owner's decision of October 20, 2026): before any fill it is refused, priced entry or not, and a stop kept unchanged from the stored plan still saves. A stop that is not set is no stop, not break-even. Negative percentages stay refused. The server checks this per entry under the Play lock; the browser mirrors it. A stop unchanged from the stored plan (the same text, unit, entry price and direction, which is everything the side check reads) is not checked again, so a Play whose entry fill is unlinked after a stop moved beyond it still saves, and its other levels still rebind.
- **Frozen initial risk.** Initial risk and R come from the plan revision in force at the first entry fill, so moving a stop afterwards never changes them. The distance to a stop is signed by the direction: a stop at or beyond its entry adds no risk and offsets the others, and a plan whose stops are not a loss in total has no planned risk and no R. The scored result, win, loss and scratch, the sizing thresholds, the ladder and the window do not change.
- **Rebind of a linked level.** A linked stop or target cannot be edited in a save. When the monitor reports `level.drift`, the owner can move it to the price its order rests at, with a reason: `POST /api/plays/{id}/execution/links/{linkId}/rebind` with `expectedVersion`, `orderId`, `observedPrice` and `reason`. Under the account and Play locks it checks the Play version and that the retained order still rests at the observed acting price (trigger, else limit), or returns 409 and changes nothing. As in a save, it refuses (409) when another linked order still references the level. One unit of work then writes one plan revision (the level becomes that price) and, when the observed order is another order, dismisses the old link and links that order through its internal ID; it refuses (409) to replace a link whose order already has fills, which would drop executed exits from the Play. The answer is the saved Play. Vessel still never edits the order.
- No migration is needed.

## Venue link

Vessel stays read-only and never places orders. The workspace links to the instrument's trading page on the venue (Hyperliquid: `https://app.hyperliquid.xyz/trade/{contractId}`) so the owner can place the planned orders there. Manual instruments have no link.

## Order and fill linking

The owner's policy is **automatic where possible, manual only when it cannot be**:

- Vessel reads the account's open orders and fills for the Play's instrument and links them to entries and exits automatically when the match is unambiguous: same account, instrument and side; price equal to the planned level within the instrument's tick; size consistent with the planned quantity; and only one Planned, Paused or Open Play that could claim it.
- Ambiguous candidates, for example two concurrent Plays with the same level, are shown for the owner to confirm. They are never assigned silently.
- Linked order IDs drive the status: the first linked entry fill moves Planned to Open, and the last exit fill with no linked entry order still resting moves Open to Closed. Each automatic transition records its source facts.
- A price touching a level is an observation, never a fill.

## Monitoring and notifications

The later monitor watches linked order IDs and plan levels server-side. It drives the automatic transitions above and creates notifications when an entry, stop or target fills, or when a level is approached without a linked order. It changes who triggers the transitions, not the model.

## Delivery order

1. **Saved Plays** (this step): Draft, Planned, Paused and Cancelled with manual transitions, plan revisions with reasons, a history view, saved evidence and the venue link.
2. **Order and fill linking**: automatic matching with manual confirmation of ambiguous cases, Open and Closed with execution progress.
3. **Review**: a separate record with revisions and a "needs review" list.
4. **Monitoring and notifications**: server-side tracking that drives transitions and alerts.

## Saved Plays contract

Authenticated, owner-scoped routes under `/api`:

| Route | Purpose |
|---|---|
| `GET /plays` | Summaries, most recently updated first |
| `POST /plays` | Create a Draft |
| `GET /plays/{id}` | Full Play: head, plan, drawings, review note |
| `PUT /plays/{id}` | Save. Requires `expectedVersion`. A plan change after planning requires `revisionReason` |
| `POST /plays/{id}/status` | `{ expectedVersion, status, reason?, note? }` for plan, pause, resume and cancel |
| `GET /plays/{id}/history` | Plan revisions and status changes |
| `DELETE /plays/{id}` | Drafts only, with their evidence |

- The account must be the owner's and enabled to create or plan a Play. The instrument belongs to the account's venue; venue instruments use contract IDs, manual accounts use a manual label.
- Planning requires an instrument and at least one entry with a positive price.
- `version` increases on every write; a stale `expectedVersion` returns 409 so two tabs cannot overwrite each other.
- Number fields keep the exact text the owner entered. Draft values may be blank or incomplete.
- Drawings are stored per instrument as the chart's own JSON, bounded in size, and are not part of plan revisions.
- Evidence uses the existing evidence routes once the Play exists. The browser uploads draft images on the first save and synchronizes notes, markup and removals on later saves.

## Saved Plays state

October 3, 2026. Step 1 is implemented on branch `t3code/603adfcf`.

- Backend: `Play` (Domain) holds status, plan JSON, drawings, review, plan revision and version. `PlayPlanRevision` and `PlayStatusChange` are append-only, owner-scoped, `Restrict` foreign keys. `PlayDocuments` (Application) validates the plan (≤20 entries, ≤10 targets each, decimal-text numbers, whole leverage 1–100, unique item IDs, notes ≤20,000 characters) and bounds drawings (≤100 instruments, ≤200 drawings each, ≤1 MB). `PlayService` applies the transitions; `PlayStore` (Persistence) maps a concurrent EF write to 409. Routes are in `PlayEndpoints.cs`.
- Migration `SavedPlays` turns earlier `Active` heads into `Planned` with a blank plan, sets manual sources for manual venues and keeps closed heads closed. The API still does not migrate on start; run `pnpm db:migrate`.
- Cancel is also allowed from Draft in the API; the UI offers Delete for Drafts and Cancel for Planned and Paused.
- Frontend: Plays opens a list (In progress, Closed and cancelled, All) with New play. The shell keeps the open play and its unsaved edits while navigating; another play cannot be opened until they are saved or discarded. The editor reuses the approved workspace and adds Save, Plan it, Pause, Resume, Cancel play, Delete (drafts), History and the venue link. Planned plays fix the account and instrument. Closed and cancelled plays disable the plan and keep the review and evidence editable.
- Saving a planned plan change asks for a reason. Title, drawings, review and evidence changes save without one. History lists revisions with a readable diff by entry and target identity, and status changes with cancel reasons.
- Evidence: the first save uploads draft images through the existing evidence routes; later saves sync note and markup edits and removals. A failed image sync keeps the Play saved and retries on the next save. Capture context text and file names are not stored on the server, so reopened images get generated names.
- Draft item IDs now combine time and randomness instead of a per-page counter, because saved plans keep them across page loads.

Verification:

- `pnpm check` passes 573 backend tests (PostgreSQL tests run against a disposable test database, none skipped), 288 frontend tests, both builds, lint and the prototype tests.
- A headless Chromium 151 run against a real API and a scratch PostgreSQL database covered: save draft, the Hyperliquid catalogue and the link `https://app.hyperliquid.xyz/trade/HYPE`, Plan it, stop change with a revision reason, history diff ("Entry 1 stop: 37 → 38"), reload and reopen, cancel with reason, and evidence upload, note change, reload, removal and draft deletion. No console errors; no horizontal overflow at 1402, 1001 and 390 pixels.
- Not covered: Open and Closed (they need order linking), concurrent edits from two browsers beyond the version check, and very large drawing sets.

## Order and fill linking state

October 3, 2026. Step 2 is implemented on branch `t3code/play-execution`, stacked on the saved-Plays branch.

### Rules

- **Sources**: Hyperliquid `frontendOpenOrders` (current book) and `historicalOrders` (latest 2,000 status updates, latest per order kept), plus the existing `userFills` sync. Only primary perpetual contracts; rejected orders are never stored.
- **Relevance**: orders are stored in `imported_orders` only for contracts with a Planned, Paused or Open venue Play on that account, placed at or after the earliest such Play was created. Account deletion removes them with the other venue facts.
- **Price match**: within one step of the fifth significant figure of the plan level, the precision Hyperliquid accepts (1 at 84,541; 0.01 at 100). Percentage stops and targets are returns on margin at the plan's leverage, so they resolve to a price move of percentage ÷ leverage from the entry. A trigger order is compared at its trigger price, a limit order at its limit price.
- **Level fit**: entries need the entry side and an order that is neither reduce-only nor a position TP/SL. Stops need a trigger order that is not a take profit. Targets accept limit or take-profit orders, not stop orders. Orders placed before the Play was created never match it.
- **Unplanned exit**: when no level matches, an exit-side order on an Open Play links as an unplanned exit if it reduces the position (reduce-only, position TP/SL, or its fills close) and was placed after the Play's first linked entry fill.
- **Automatic vs. confirmation**: one candidate links automatically. Several (concurrent Plays on the same level) become suggestions; the owner links one ("Link here") or declines ("Not this play"). Unlinking is remembered as dismissed and never re-proposed for that level; other suggestions for the order return if it is unlinked.
- **Manual fallback**: other live or filled orders on the instrument since the Play was created (latest 20) can be linked by hand to a compatible level or as an unplanned exit. Side, reduce-only/position-TP/SL flags and retained fill effects constrain the role; owner attribution cannot reinterpret an execution. An order belongs to at most one level of one Play (unique index on linked orders).
- **Status**: the first compatible linked entry fill moves Planned or Paused to Open (the note names the fill; a paused Play is flagged "Filled while paused"). Open becomes Closed only when compatible exits equal entries, chronology is valid, no fixed-size filled order has known missing executions and no linked entry order is still open. These changes are recorded with source `venue`. There is no automatic reverse transition.
- **Edits**: after planning, an entry with linked fills keeps its price and share and cannot be removed. The Play also keeps its direction. Its stops and targets, and untaken entries, can still change with a revision reason.
- **Several stops**: each stop of an entry is its own level, like each target. Stop and target links carry the level ID (`levelId`, formerly `targetId` for targets only).

### Checking

`POST /api/plays/{id}/execution/check` runs the existing account sync (fills and snapshot), reads orders, then matches and applies transitions under a per-account advisory lock. It repeats matching while statuses move, so an entry and its exit in the same window settle in one check. `GET /api/plays/{id}/execution` returns the stored state; `POST …/execution/links` and `DELETE …/execution/links/{linkId}` link and unlink.

The browser checks a tracked Play when it is opened or its status changes, and every 60 seconds while the tab is visible. The server-side monitor (step 4) will replace this polling. The editor shows linked orders and fills, suggestions and other orders in an Execution journal tab, so the locked layout is unchanged.

### October 13 integrity corrections

Automatic matching checks retained fill side/effect and the time of an exit's first execution against this Play's first compatible entry fill. A resting bracket may be placed before entry and execute afterward. Existing incompatible links remain inspectable, but their fills are excluded from progress totals and block closure. A flip cannot be assigned wholly to an entry or exit; split-fill attribution remains unsupported.

Execution responses include an `issues` list, displayed as "Not reconciled". The shared integrity checks cover role contradictions, chronology, over-exits and known missing fixed-size filled-order executions. Cancelled/rejected remaining quantity does not prove an execution, and position-sized TP/SL orders do not establish a fixed missing-fill bound.

Closed and Cancelled Plays retain actual order links. Declining an old suggestion does not alter execution facts and remains allowed. Existing inconsistent terminal records are not automatically repaired or reopened. Sizing excludes newly ineligible historical records, so its displayed track record can change without a migration.

### Verification

- `pnpm check` passes 598 backend tests (PostgreSQL tests run, none skipped) and 291 frontend tests, lint, builds and prototype tests.
- Live read-only check against a public Hyperliquid market-maker address on a scratch database (Play creation time moved back three days to cover existing history): the BTC entry at 84,541 linked exactly one filled order automatically and the Play moved to Open with the venue note; an exit placed after the entry fill linked as an unplanned exit; a manual link from the "other orders" list worked; one check took about 6 seconds. Headless Chromium showed the Execution tab with one automatic check on open, no Pause/Cancel for Open, the history entry "Planned → Open · from linked venue fills", no console errors and no overflow at 390 pixels.
- The first live run found two problems, both fixed and covered by tests: a 2 bps tolerance (about $17 on BTC) linked neighbouring orders, and exits placed before the entry fill closed the Play.
- Not covered: Open → Closed against live data (the sample account kept a position), Hyperliquid builder-deployed (HIP-3) markets, and history older than the latest 2,000 orders or fills.
