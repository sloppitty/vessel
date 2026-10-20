# Phase 1 learning loop: state

This records verified progress against the
[Phase 1 contract](phase1-learning-loop-contract.md). An item is listed as
done only after:
- its tests pass;
- its pull request is open;
- its Sol (Codex) review has been answered.

Each entry records the checks that ran. Each item has its own section, so
stacked pull requests edit different lines.

## Contract

October 20, 2026:
- The contract was drafted from:
  - the October 18 review and its probes;
  - the open Phase 0 pull requests, #7 to #18;
  - the code.
- The owner answered decisions 3, 4, 5 and 9, chose the integration base
  and Sol reviews, and asked for a display time-zone setting.
- The Phase 0 tips' pairwise conflicts were found with `git merge-tree`.
- No owner database was touched.
- **Sol (Codex) round 1 (`gpt-6.1-sol`).** Five findings, all accepted into
  the contract:
  - **High:** Unresolved had no defined settlement path. It now takes no new
    links, its existing links still settle it, and it closes through the
    normal transition and event.
  - **Medium:**
    - the monitor watch now keeps the existing bounded window, which starts
      at the declaration;
    - completion is cleared when a Play leaves Unresolved;
    - process lanes now have a population, period times, precedence and a
      paginated drill-down;
    - Duplicate now opens an unsaved Draft that the plan checks gate.

  The legacy-completion migration was found sound.
- **Sol round 2.**
  - All five resolutions were confirmed against the code. The review notes
    what each one changes there:
    - Unresolved→Closed needs the reconciliation, closing and event gates
      changed;
    - a declaration resets the watch independently of the link revision;
    - `Annotate` must accept unchanged stored answers on an Open Play.
  - Two editorial points were applied: lane membership names the
    exclusion-only remainder, and Duplicate keeps New play's single-session
    guards.
  - No further high-severity gap was found.

## Integration base

October 20, 2026: `t3/phase1-base`, the draft "Phase 0 integration — do not merge" pull request:
- **Contents.**
  - It merges #18 (`2afa1c7`), #17 (`29705bc`), #14 (`ecb2d14`) and #9 (`bc1cd84`).
  - Every conflict resolution keeps both sides. The pull request lists them.
  - One follow-up commit, `3e60c68`, hoists `venues.readsCurrentBook` above the fills memo in `PlaysPage`. The React
    Compiler lint rejected the combination of 0.4c and 0.5b without it.
  - The contract (#20) is merged in as well, so stacked pull requests can record their progress here.
- **Checks.** With disposable PostgreSQL 18 and RabbitMQ 4.3.6:
  - frontend: typecheck, lint and build clean; 147 files and 3,722 tests pass;
  - backend: the Release build has 0 warnings; 3,549 tests pass and 5 are skipped (S3 and live venues).
- **Retired the same day.** Every Phase 0 pull request merged into main (`c39d98e`), with main's own fix for the same
  lint failure (`962c9f6`). The contract was rebased onto main, 1.6a and 1.1a onward were rebased onto the contract,
  and the draft pull request was closed. Each branch's check was then run again on main.

## 1.1 Actual versus plan

### 1.1a Receipt, journal Save, retrospective facts

October 20, 2026, branch `t3/phase1-receipt`, stacked on the integration base:
- **Backend.**
  - `PlayReceipts` gives each entry its planned price, planned quantity and plan revision. They come from the revision
    in force at the entry's first fill, or from the first revision when the plan was written after that fill.
  - It also gives a receipt: first entry and last exit times, average entry and exit, fill count, and estimated R with
    its initial risk.
  - R comes from `ReviewService.Score`. That is Review's per-Play rule, now extracted unchanged and shared, so the
    receipt and Review cannot disagree.
  - When R is unknown, the reason says which applies: open, retrospective, fees, executions, no complete stop, or no
    plan in force at the first fill.
  - Both the venue execution read and the manual ledger read carry these values.
- **Frontend.**
  - The Execution tabs list "Average fill X vs plan Y (±d)", with an exact difference, and "Filled q of Q".
  - The Review tab opens with the outcome: venue net, Play-local result and fees, or the manual ledger net, plus R and
    time held.
  - The journal and its Expand dialog show **Save play** while there is something to save. After the save, focus moves
    to the journal's own expand or close button.
  - A retrospective Play's "No original plan" panel lists its trade facts.
  - The recorded monitor API fixture gains the new fields.
- **Tests.**
  - Unit: planned values from the revision at the first fill, the first revision for a plan written after it, and the
    current plan before any fill; planned quantity under quantity and margin sizing; R and every unknown reason.
  - PostgreSQL, venue and manual: the contract case. Entry planned at 100 for 7.5 fills 0.5 at 100.4. After the close,
    R equals Review's `RMultiple` for the same Play.
  - Frontend: the receipt components, the API guards (including servers without these fields), and the Review tab
    with Save play inline and in the dialog, including where focus goes.
- **Checks.** `scripts/check.sh` passed with disposable PostgreSQL and RabbitMQ:
  - backend: 3,557 passed, 5 skipped;
  - frontend: 149 files and 3,738 tests;
  - typecheck, lint and build clean.
- **Not in this pull request.** Fill markers and the trade-dated chart are 1.1b. The popup-over-a-focused-field report
  has not been reproduced yet.
- **Sol (Codex) round 1.** Five findings, all fixed:
  - **Plan text (medium).** Planned values read plan numbers with the editor's plain-decimal grammar, and leverage as
    `leverageOf` does. Legacy text such as `100.`, `.5` or ` 3 ` can no longer make an execution response invalid or
    disagree with the editor.
  - **Manual fees (medium).** The manual ledger lists known fees by currency and counts executions with unknown costs.
    Both show in its Execution totals and its Review header.
  - **Small R (medium).** A non-zero R that rounds to zero reads "−<0.01R" or "+<0.01R".
  - **Focus (low).** A journal save that goes through the revision-reason dialog still returns focus to the journal. A
    test fails without this fix.
  - **Check.** `scripts/check.sh` passed again: backend 3,558 passed, 5 skipped; frontend 149 files and 3,741 tests.
- **Sol (Codex) round 2.** Three round-1 findings resolved. Two residual findings, both fixed:
  - **Plan text (medium).** Plan numbers with more precision than a decimal holds were rounded into known planned
    values. They are now unknown, as in the editor.
  - **Focus (low).** A journal save whose reason dialog was dismissed no longer moves focus after a later header save.
    A test fails without this fix.
  - **Check.** `scripts/check.sh` passed again: backend 3,558 passed, 5 skipped; frontend 149 files and 3,742 tests.

### 1.1b Fills on the chart

October 20, 2026, branch `t3/phase1-fill-markers`, stacked on 1.4b:
- **Markers (G-03, F-06).** A saved Play's fills, its linked venue fills or its manual ledger's executions, are drawn as
  markers at their time and price.
  - Buys are up arrows, sells down arrows, in the positive and negative theme colours.
  - Each sits on the candle its execution falls in. A fill outside the loaded candles is not drawn.
  - A tooltip shows the role, side, exact quantity and price, and UTC time. A visually hidden list carries the same
    text.
  - Plan levels keep their lines and tags, so a fill cannot be read as a level.
- **Trade dates.** Closed, Unresolved and retrospective Plays with fills open the chart on the trade: first to last fill,
  padded by a quarter of its length and at least five candles.
  - The candles come from an anchored cache entry whose tail ends at the padded end, never later than now.
  - Older pages load on scroll as usual, and live updates stay off.
  - The focus applies at a reset and once per new range, so the owner can pan.
  - Open Plays keep today's view and the live tail.
- **Adapter.** `ChartAdapter` gains `setMarkers` (Lightweight Charts series markers, hover through the hovered-object ID)
  and `setFocus`. The renderer still knows nothing of Plays.
- **Popup over a focused field.** Not reproduced: no browser session ran for it. The notification stack is fixed at the
  bottom right, up to 400 px wide and above the page, so it can sit over a field there on a short window. Notification
  delivery does not change in Phase 1, so the report stays open.
- **Tests.**
  - Adapter: markers snap to their candle and drop outside it, move when candles arrive, report hover, and the focus
    applies once per range and at resets.
  - Chart panel: an ended trade requests candles ending after it, focuses on it, marks its fills (never as levels) and
    never streams; an open trade stays live.
  - The candle chart shows the hovered fill's tooltip and lists every fill.
- **Checks.** `scripts/check.sh` passed: backend 3,608 passed, 5 skipped; frontend 153 files and 3,825 tests; build clean.

- **Sol (Codex) round 1.** Three findings, all fixed:
  - **Fractional end (medium).** A quarter of a trade with millisecond fills could be fractional, which the candle
    request refuses. The focus is rounded out to whole milliseconds.
  - **Late fills (medium).** When today's candles loaded before the Play's fills, the trade's candles arrived without a
    reset, so the focus never applied. The anchor is now part of the chart's view key, so they arrive as a reset. A
    test with candles before fills fails without this fix.
  - **Gaps and lone candles (medium).** A marker took the span of the neighbouring candles, or an endless one for a lone
    candle. Each marker now carries its interval and sits on a candle only within that candle's own span. Adapter
    tests cover a gap, a sparse end and a lone candle.
  - **Check.** `scripts/check.sh` passed again: backend 3,609 passed, 5 skipped; frontend 153 files and 3,829 tests.

- **Sol (Codex) round 2.** All three round-1 findings resolved. Two new findings, both fixed:
  - **Long trades (medium).** The chart shows only loaded candles, so a trade longer than the first page opened
    clipped. A focus that starts before the loaded candles is applied again as older pages arrive, until it fits or
    the owner moves the view.
  - **Monthly candles (medium).** The 30-day span put a January 31 fill outside January and a March 1 fill on a lone
    February. Monthly candles now span their calendar month.
  - Adapter tests cover both.
  - **Check.** `scripts/check.sh` passed again: backend 3,610 passed, 5 skipped; frontend 153 files and 3,832 tests.

- **Sol (Codex) round 3.** The month fix holds. One finding, fixed:
  - **Very long trades (medium).** At the library's half-pixel minimum per candle, a long trade's left edge left the
    screen, so older pages stopped coming. The chart now asks for the next older page itself while the trade is not
    covered, and candles can be as narrow as 0.05 px, so thousands fit one view. The adapter test checks the request.
  - **Check.** `scripts/check.sh` passed again: backend 3,610 passed, 5 skipped; frontend 153 files and 3,834 tests.

- **Sol (Codex) round 4.** No findings. The reviewer's real-renderer probes framed a 2,500-candle trade, stopped an
  unreachable one at the 5,000-candle cap after ten requests, and left the owner's own zoom alone.

## 1.2 Honest review queue and in-trade log

October 20, 2026, branch `t3/phase1-review-completion`, stacked on 1.5:
- **Completion (D-14, decision 3).**
  - A Play records a review completion kind (`completed`, `not-needed` or `legacy`) and its time.
  - **Complete review** works on a Closed or Cancelled Play with unchanged text or none. A save of changed structured
    answers on an ended Play completes it too. **No review needed** is offered on a Cancelled Play unless its receipt
    shows fills, and the server refuses it with 409 when the Play has a linked fill or a manual execution.
  - **Review again** clears the completion. Text and answers stay.
  - Writing text never completes a review. `ReviewedAtUtc` still means "review last edited".
  - Each action is a review revision with its action and time. History names it: "review completed", "reviewed
    again" or "no review needed".
  - The three actions are version-checked against the Play and return 409 when it changed.
- **Lists and Review.** Needs review and Reviewed read the completion. Review's count is "Completed reviews".
- **Legacy.** The explicit migration `20261020093407_ReviewCompletion` records a `legacy` completion for every Closed
  or Cancelled Play the Reviewed view listed before, at its `ReviewedAtUtc`, else its end time. Nothing else changes, and
  saved text is never moved or rewritten.
- **In-trade log (G-04).**
  - The journal's Log tab, after Evidence, takes append-only, timestamped entries of up to 2,000 characters on any
    saved Play, each with an optional state: calm, rushed, FOMO, tired or after a loss.
  - Entries live in their own owner-scoped `play_notes` table. They cannot be edited or deleted, and they never count
    as a review or feed Review.
  - An entry is written idempotently by its request ID, so a retried request never writes it twice, and the same ID
    with different content gets 409. Appending never changes the Play, so it is not version-checked. A Play holds at
    most 500 entries.
- **Open Plays.** The Review box stays, with a hint that the Log is for in-trade notes and that the review completes
  after the Play ends.
- **Not in this pull request.** Leaving a final state (an Unresolved Play returning to Open, or venue facts closing
  it) arrives with Unresolved in 1.3a. Today no Play leaves Closed or Cancelled.
- **Tests.**
  - Domain and service: completion paths, Review again, No review needed with and without fills, unchanged and changed
    answers, versions, and the log's idempotency and text bounds.
  - PostgreSQL: `Verify2.F8` and `More4.EarlyReview` (a note written while Open lands in Needs review after the close
    until Complete review), Complete review with unchanged text and Review again, a no-fill cancellation leaving the
    queue in one step, a log entry changing neither list, and the migration's legacy backfill down to `PlanPreflight`
    and back up. The two-owner isolation test covers the five new routes.
  - Older-schema migration tests insert Plays through `LegacyPlayRows`, because EF now maps the new columns.
  - Frontend: the completion bar, the log, History's actions, the Plays page's Complete review and the response
    guards.
- **Checks.** `scripts/check.sh` passed on main: backend 3,588 passed, 5 skipped; frontend 152 files and 3,790 tests;
  build clean. A first run had one isolation test time out under a load average near 60 from other work; it passed
  alone, and the full rerun passed.

- **Sol (Codex) round 1.** Five findings, all fixed:
  - **Unfinished entry (medium).** The entry being written, with its request identity, now lives in the Plays session
    for its Play, so switching journal tabs or expanding the journal never loses it. A test fails without this fix.
  - **Late read (medium).** A log read that started before a write arrived no longer replaces the list the write
    returned. A test fails without this fix.
  - **Request IDs across accounts (medium).** Log writes take an owner-wide request-ID lock before the Play lock, so the
    same ID sent at once on two accounts is one entry and a 409, not a 500. A PostgreSQL test sends both at once.
  - **Retrospective Plays (medium).** They get the log, as the contract says; the lifecycle doc no longer narrows it.
  - **Older servers (low).** Without a completion field, the journal shows no completion actions the server lacks.
  - **Check.** `scripts/check.sh` passed again: backend 3,591 passed, 5 skipped; frontend 152 files and 3,796 tests.

- **Sol (Codex) round 2.** All five round-1 findings resolved. Two new findings, both fixed:
  - **Late write after a remount (medium).** A log write that finishes after the Log remounts now changes the kept
    draft as it is then. It clears the entry only while the draft still holds that request with the same text and
    state. Otherwise it settles the request and keeps the newer text. A test fails without this fix.
  - **Unsaved log entries (medium).** The open Play's typed entry, or one whose write is unconfirmed, counts as unsaved
    work for New play, opening or requesting another Play, and the shell's disconnect guard. A page test fails without
    this fix.
  - **Check.** `scripts/check.sh` passed again: backend 3,591 passed, 5 skipped; frontend 152 files and 3,801 tests.

- **Sol (Codex) round 3.** Both round-2 findings resolved. Two new findings, both fixed:
  - **A page that has gone (medium).** A log write that finished after the whole Plays page remounted, or after
    disconnecting, published that page's outdated session, erasing newer text or bringing back a discarded session. A
    page that has unmounted, or whose API changed, now never publishes from a log write. A page test that remounts the
    page during a write fails without this fix.
  - **Entry after a remount (medium).** A Log mounted while a write was out showed "No entries yet" after the write
    landed. A confirmed write now counts on the kept draft, so a mounted Log that did not send it reads the list
    again; the Log that sent it already shows the returned list. A test fails without this fix.
  - **Check.** `scripts/check.sh` passed again: backend 3,591 passed, 5 skipped; frontend 152 files and 3,802 tests.

- **Sol (Codex) round 4.** Two new findings, both fixed:
  - **A page that has gone (medium).** Round 3 stopped a gone page from publishing, but that dropped its write's
    acknowledgement. The shell now takes the change through `onLogChange` and applies it to its current session under
    its API and work checks, so newer text survives, the entry shows and a discarded session is never changed. The
    page test checks the entry appears.
  - **A replaced attempt (medium).** A delayed write whose draft had moved on to another entry did not refresh the
    list. Every confirmed write for the Play now counts, whichever request owns the draft; only the entry a write sent
    is cleared. A test fails without this fix.
  - **Check.** `scripts/check.sh` passed again: backend 3,591 passed, 5 skipped; frontend 152 files and 3,803 tests.

- **Sol (Codex) round 5.** Two new findings, both fixed:
  - **Edits before a render (medium).** The shell applied a departed page's acknowledgement to its own session, and a
    mounted page's edit made before it rendered could overwrite it with an older copy, leaving a written entry as
    unsaved work. A mounted page now registers as the one that applies log changes, so acknowledgements land in its
    current session. A test clears the field in the same tick as the acknowledgement; it fails without this fix.
  - **A late own answer (medium).** A delayed answer could replace a newer list and hide an entry. Log answers merge by
    entry ID in time order, since entries are append-only. A test fails without this fix.
  - **Check.** `scripts/check.sh` passed again: backend 3,591 passed, 5 skipped; frontend 152 files and 3,805 tests.

- **Sol (Codex) round 6.** No findings: both round-5 fixes hold, including under StrictMode.

## 1.3 Record repair

### 1.3a Unresolved

October 20, 2026, branch `t3/phase1-unresolved`, stacked on 1.2:
- **The status (D-06, decision 4).** `Unresolved` ends an Open Play whose venue executions can never settle it.
  - Only the owner declares it, through `POST /api/plays/{id}/unresolved`, with `expectedVersion`, a reason
    (`shared-close`, `flip`, `forced-exit-not-retained` or `other`) and a note.
  - The Play must be Open and have imported executions on its linked orders. The monitor never sets it, and account
    flatness is never read.
  - It runs under the account and Play locks that imports hold.
- **Evidence.** The status change keeps the execution facts at that moment in a new `jsonb` column:
  - entered and exited quantity, and entered minus exited;
  - its linked orders (up to 200 listed, all counted), each with its filled quantity;
  - the integrity problems.

  History and Review's drill-down show them.
- **No result.** It has no result, no inferred sign and no R; the receipt says why.
- **Lists and review.**
  - Needs review or Reviewed list it by its completion, and All lists it, with an "Unresolved" chip. The Closed tab
    stays Closed only. Counts carry `unresolved`.
  - It is reviewed like a Closed Play: Complete review, structured answers and next actions.
- **Review and sizing.**
  - Review excludes it with the reason "unresolved" and counts it in coverage. The drill-down shows its reason, note
    and facts.
  - The sizing record shows "N unresolved not counted" beside the ladder. Ladder, window and Activity fallback are
    unchanged.
- **Settlement.**
  - It takes no new links or suggestions; a link attempt says to return it to Open first.
  - Reconciliation still settles it from its existing linked orders, by the Open rule. It then closes with the same
    `play.closed` event, and the declaration stays in history.
- **Monitor.**
  - It leaves the open-Play position comparison, so a position still at the venue is unexplained, never this Play's.
  - Its linked orders are watched by the Closed and Cancelled rule from the declaration. A later declaration starts a
    new watch, by also restarting a watch when the Play's end moves past its start; Closed and Cancelled ends never
    move.
- **Return to Open.** `POST /api/plays/{id}/return-to-open` takes `expectedVersion` and a reason.
- **Leaving a final state (completes 1.2).** Returning to Open, or venue facts closing it, clears a completed review
  with a `cleared` review revision. Text and answers stay. While Open again, stored answers save unchanged but cannot
  change.
- **Browser.**
  - **Declare unresolved** appears on an Open venue Play whose linked orders hold fills. It opens a dialog with the
    reason and "What happened".
  - **Return to Open** appears on an Unresolved Play.
  - Both wait for unsaved edits.
- **Migration.** The explicit `20261020102320_UnresolvedStatus` migration adds the nullable `UnresolvedReason` and
  `Evidence` columns to `play_status_changes`. Its rollback is refused while any Play or history row names Unresolved,
  because older code cannot read it.
- **Tests.**
  - Domain: who may declare, return and settle, the evidence, and the cleared completion. Stored answers save
    unchanged while Open again.
  - PostgreSQL acceptance:
    - a stuck Play (`More4.CloseApi`, `Exec.SharedExit`, `Exec.FlipOrder`) is declared with its evidence, excluded
      from Review with that reason, counted beside the ladder, refused new links, reviewed and returned to Open on
      request;
    - a delayed exit fill on its linked order closes it with the normal `play.closed` event, on an account with no
      other active Play;
    - its linked stop's watch starts at the declaration, and a second declaration starts it again;
    - the migration's rollback is refused.
  - The two-owner isolation test covers both new routes.
  - Frontend: the actions and dialogs, History, Review's drill-down and count, the sizing note, the completion bar
    and the response guards.
- **Checks.** `scripts/check.sh` passed: backend 3,600 passed, 5 skipped; frontend 152 files and 3,804 tests; build clean. The first
  run found two expected changes (the counts shape and the owner-scope SQL inventory) and two test mistakes (the stop
  watch is one of two linked-order watches, compared at the database's microsecond precision, and events are written
  only through an event-capturing unit of work); the dedicated event test covers the last.

- **Sol (Codex) round 1.** One finding, fixed:
  - **Return event reason (low).** A return to Open now carries the reason of the Play's latest declaration as its
    event `reason`, as the lifecycle doc says. The event test declares, returns, declares for another reason and
    returns again; it fails without this fix.
  - **Check.** After the rebase onto the 1.2 round-2 fix, `scripts/check.sh` passed: backend 3,600 passed, 5 skipped;
    frontend 152 files and 3,809 tests.

- **Sol (Codex) round 2.** The round-1 fix worked for ordinary use. One finding about ties, fixed:
  - **Which declaration (low).** The return event read the latest declaration by time then a random ID, so a tied or
    stepped-back clock, or several transitions in one unit of work, could name an earlier declaration. A return now
    keeps the reason of the declaration it undoes on its own status change, chosen under the Play lock. The current
    declaration is the one at the Play's end time, which a declaration sets to its own time; Review reads it the same
    way. A domain test steps the clock back between declarations, and History shows the reason on the return.
  - **Check.** `scripts/check.sh` passed again: backend 3,601 passed, 5 skipped; frontend 152 files and 3,810 tests.

- **Sol (Codex) round 3.** Two findings, both fixed:
  - **History (medium).** The browser's History guard refused the reason a return to Open now carries, so a returned
    Play's History failed to load. It accepts it there, and a test reads a whole declare, return and settle history.
  - **Tied times (low).** Declarations at one instant still fell back to random IDs. Each status change now keeps the
    Play version it produced (`PlayVersion`, added in place to the unshipped `UnresolvedStatus` migration), and the
    current declaration is the one with the highest. A domain test repeats tied declarations and returns.
  - **Check.** `scripts/check.sh` passed again: backend 3,602 passed, 5 skipped; frontend 152 files and 3,811 tests.

- **Sol (Codex) round 4.** No findings: both round-3 fixes hold, including the in-place migration change.

### 1.3b Manual correction batch

October 20, 2026, branch `t3/phase1-correction-batch`, stacked on 1.3a:
- **The request (D-15, decision 5).** `POST /api/plays/{id}/manual-executions/corrections` takes `requestId`,
  `expectedVersion`, one reason and 1–200 corrections. Each correction is an existing execution's full corrected row,
  as the single-row PUT sends it.
- **Validation.**
  - Each row is validated as a single correction is: role, entry and side stay, and a Closed ledger keeps each source
    label.
  - Only the final ledger is checked as a whole, so a closed 1/1 ledger becomes 2/2 in one batch.
  - Single corrections and the batch share one apply path, so they keep the same ledger rules.
- **Atomicity.** The batch applies all or nothing under the account and Play locks in one unit of work. A retry with
  the same `requestId` returns the first result, and another payload conflicts.
- **History.** Each row's before and after are kept with the reason and a shared `batchId`; single-row writes have
  none.
- **Limits.**
  - A Closed ledger stays closed, and an Open one may close.
  - The batch never adds, deletes or reopens rows, and counts toward the record and history limits.
  - Its body limit is 262,144 bytes.
- **Browser.**
  - A correction gains **Also correct**, which adds another execution with its own fields under the one reason.
  - With more than one row it is saved as a batch, and a failed batch is retried with the same request.
  - The audit history labels batch entries and counts the executions corrected together.
- **Migration.** The explicit `ManualCorrectionBatch` migration adds the nullable `BatchId` column to
  `manual_execution_history`.
- **Tests.**
  - PostgreSQL (`Verify.F5`):
    - a closed 1/1 ledger becomes 2/2 in one batch, with both rows' history under one batch ID;
    - the same change one row at a time is refused;
    - an invalid final ledger leaves the version, history and totals unchanged;
    - duplicates, an empty batch, a blank reason, a stale version and a changed side are refused;
    - a retry returns the first result, and the request ID with another payload conflicts;
    - single corrections still work.
  - The two-owner isolation test covers the new route, including another owner's execution ID inside a batch.
  - Frontend: the batch request and its single reason, Remove back to one correction, each execution offered once,
    and the same request on retry.
- **Checks.** `scripts/check.sh` passed: backend 3,601 passed, 5 skipped; frontend 152 files and 3,807 tests; build clean. A first
  run found the body-limit inventory missing the new route, now listed at its 262,144-byte limit.

- **Sol (Codex) round 1.** No findings. The reviewer named test gaps that are not defects and are not addressed
  here: a batch replayed after a later change, a request ID reused on another Play, the history-limit boundaries, an
  Open-to-Closed batch, and chunked, case-changed or trailing-slash requests against the body limit.

- **Rebase.** 1.3a's migration now adds `PlayVersion` to `play_status_changes`, so the `ManualCorrectionBatch` target
  model includes it; EF reports no pending model changes. `scripts/check.sh` passed: backend 3,603 passed, 5 skipped; frontend 152 files and 3,814 tests.

## 1.4 Process and outcome as cases

### 1.4a Post-hoc badge and per-metric n

October 20, 2026, branch `t3/phase1-post-hoc-badge`, stacked on 1.1a:
- **D-17 badge.** `PlayReceipts.PlannedAfterExecution` is true for a planned-origin Play whose `PlannedAtUtc` is later
  than its earliest linked or manual execution.
  - It reads stored times only.
  - The receipt and Review's Play rows carry the flag.
  - The badge "Plan written after first execution" shows in the Play header, above its review and in Review's Play
    lists.
  - Scoring and sizing are unchanged. By decision 9 the Play may answer "Followed".
- **F-08.** Each metric names its own n and is marked as a small sample (below 20) by it:
  - batting average and profit factor: decided Plays;
  - average gain / loss: wins and losses;
  - expectancy: scored Plays;
  - estimated R: Plays with a complete initial stop.

  The asset cards are marked by their Play counts, and breakdown rows by their decided counts.
- **Label.** The R average is now "Estimated R expectancy (planned-stop model)". The statistic is unchanged.
- **Tests.**
  - PostgreSQL, T29: a manual Play planned today for an execution recorded days earlier is flagged on its receipt and
    its Review row. Its R reason is "no plan revision in force". It answers "Followed" and is still scored as a win. A
    plan written before its execution is not flagged.
  - Unit: the flag's time boundaries, an exit-only early fill, and retrospective Plays.
  - Frontend: A5 (20 scratches and one win) now reads "n = 1 decided · Small sample"; the asset-card and row markers;
    the badge in Review rows, the header and the Review tab, and its absence otherwise.
- **Checks.** `scripts/check.sh` passed:
  - backend: 3,560 passed, 5 skipped;
  - frontend: 149 files and 3,741 tests;
  - build clean.
- **Sol (Codex) round 1.** Two findings, both fixed:
  - **Breakdown markers.** Breakdown rows and the combined footer now mark each cell by its own n:
    - batting average and profit factor by decided Plays;
    - average gain / loss by the smaller of wins and losses.

    So 20 wins and one loss flags only the gain / loss cell.
  - **Spacing.** Each metric's n sits on its own line instead of running into the text before it.
  - **Rebase.** The branch was rebased onto the fixed 1.1a. `scripts/check.sh` passed again: backend 3,561 passed, 5
    skipped; frontend 149 files and 3,745 tests.

- **Sol (Codex) round 2.** Both round-1 findings resolved. One new finding and a test gap, both fixed:
  - **Average outcome (medium).** The Winning Play and Losing Play averages each show their own n, with the small-sample
    mark below 20.
  - **Spacing test.** A stylesheet test now fails if the sample label's own-line rule is removed.
  - **Check.** After the rebase onto main, `scripts/check.sh` passed: backend 3,571 passed, 5 skipped; frontend 149
    files and 3,768 tests.

### 1.4b Process cases

October 20, 2026, branch `t3/phase1-process-cases`, stacked on 1.3b:
- **Process section (G-01, decision 9).** Below the Breakdown, over the period's Closed, Cancelled and Unresolved Plays
  in scope:
  - followed and deviated, by win and loss, with each row's scratches;
  - lanes for retrospective, unscored but answered, unanswered, and not-applicable Plays.

  Each Play is in the first lane that applies, and an unscored planned Play with no answers is only in the exclusion
  list. Answers are read as stored.
- **Times.** Closed Plays by Review's close time, Cancelled ones by their cancellation, Unresolved ones by their
  declaration.
- **Mistake tags.**
  - Counted per tag, trimmed and case-insensitive, shown in the latest spelling.
  - Each tag has its wins, losses, scratches and unscored Plays, with no money.
  - The review form autocompletes from `GET /api/review/mistake-tags` (at most 100, newest first).
- **Rates.** A win share appears only where its own decided count is at least 10. Rows keep a fixed order, with no
  ranking or labels.
- **Drill-down.** `GET /api/review/process` takes Review's filters and exactly one cell, lane, mistake tag or skipped
  pre-flight check (or `not-recorded`). It returns those Plays newest first, with nullable money, the stored answers,
  the post-hoc flag and the completion. Every count equals its drill-down.
- **Pre-flight counts (1.5).** They now cover the same population's planned Plays that were planned, and each skipped
  count and "not recorded" opens its Plays.
- **Breakdown.** Origin and Direction join in a fixed order, without row inspection, since Review has no such filter.
- **Contracts.** Review's Play rows gain adherence, mistake tag, direction and completion.
- **Tests.**
  - Service:
    - every lane, cell and exclusion-only case, with each count equal to its drill-down total;
    - tag grouping and latest spelling;
    - pre-flight counts and drill-downs across Closed, Cancelled and Unresolved Plays;
    - selection validation;
    - the fixed-order groups and row fields.
  - PostgreSQL (T29): a manual Play planned after its executions counts in its followed cell with the badge, a cancelled
    answered Play is unscored, and tags, pre-flight counts and drill-downs agree. The two-owner isolation test covers
    both routes.
  - Frontend: the shares at n ≥ 10, counts opening their Plays, the pre-flight drill-downs, the Origin/Direction tabs,
    the tag suggestions and the response guards.
- **Checks.** `scripts/check.sh` passed: backend 3,607 passed, 5 skipped; frontend 153 files and 3,813 tests; build clean.

- **Sol (Codex) round 1.** Four findings, all fixed:
  - **Cancelled Plays (medium).** An unanswered Cancelled Play was in neither the lanes nor the exclusion list. The
    exclusion drill-down now lists the period's Cancelled Plays with their cancel reason, `coverage.cancelled` counts
    them, and the excluded total includes them. The lane test checks the exclusion list and coverage add up; it fails
    without this fix. The two-owner isolation test expects the owner's own cancelled Play there.
  - **Rates (medium).** Origin and Direction rows and their combined row show batting average, average gain / loss and
    profit factor only where their own denominator is at least 10, otherwise `—` with the n. A test fails without
    this fix.
  - **Queries (medium).** Review read one query per Unresolved Play for the post-hoc flag. It now reads the first
    linked execution of every Unresolved Play in scope in one grouped query. A PostgreSQL test counts Review's queries
    with one and three Unresolved Plays (it fails on the old loop) and checks the earliest of two fills decides the
    flag.
  - **Link names (low).** Process drill-down links are named by their visible title.
  - **Check.** After the rebase onto the fixed 1.3a, `scripts/check.sh` passed: backend 3,608 passed, 5 skipped;
    frontend 153 files and 3,819 tests.

- **Sol (Codex) round 2.** All four round-1 findings resolved. One new finding, fixed:
  - **Venue (low).** Process drill-down rows now show the venue logo beside the instrument and account, as other Play
    lists do.
  - **Check.** After the rebase onto the fixed 1.3a, `scripts/check.sh` passed: backend 3,609 passed, 5 skipped; frontend 153 files and 3,820 tests.

- **Sol (Codex) round 3.** No findings: the venue fix and the rebase onto 1.3a's change hold.

## 1.5 Lessons return and a recorded pre-flight

October 20, 2026, branch `t3/phase1-preflight`, stacked on 1.4a:
- **Next actions (G-02).**
  - `GET /api/plays/next-actions` returns the owner's ten newest non-blank next actions from Closed or Cancelled Plays,
    by review time, each with its source Play.
  - The Thesis tab of a Draft, Planned or Paused Play lists them, collapsed. Each source opens its Play.
  - **Copy into plan** appends `If …, then <next action> (from <title>)` only on that click. On a Planned Play it saves
    through the revision-reason dialog as one revision.
- **Pre-flight (G-05).**
  - Plan it evaluates five checks from what the page shows:
    - priced stops;
    - shares;
    - thesis and invalidation;
    - risk within the setting × ladder level;
    - liquidation beyond the stops.
  - The checklist always opens, with **Plan it anyway** and **Back to the plan**. It never blocks.
  - The server validates codes and outcomes, refuses a pre-flight anywhere but Draft → Planned, and stores it on plan
    revision 1. That uses the explicit migration `20261020085301_PlanPreflight`, a nullable `jsonb` column.
  - History shows the recorded checks, or says "Pre-flight not recorded" for a plan from before them.
  - Review counts, over the period's closed planned Plays, those recorded, those not recorded, and the skips per check.
- **Tests.**
  - PostgreSQL: a Draft with an unpriced stop plans, its revision 1 records the skip, and Review counts it beside one
    unrecorded Play.
  - PostgreSQL: invalid codes, outcomes and duplicates get 400, as does a pre-flight sent when resuming.
  - PostgreSQL: next actions come newest first, at most ten, with blank and in-progress ones left out. The two-owner
    isolation test covers the new route.
  - Frontend:
    - the checks and their unknowns;
    - the dialog and going back;
    - copying into Draft and Planned Plays;
    - History, Review counts and the response guards.
  - Existing Plan it tests now go through the checklist.
- **Checks.** `scripts/check.sh` passed:
  - backend: 3,564 passed, 5 skipped;
  - frontend: 151 files and 3,757 tests;
  - build clean.

- **Sol (Codex) round 1.** Five findings, all fixed:
  - **Stale sizing (medium).** A sizing record that could not be reloaded is no record, so the risk check is unknown
    rather than met against an old ladder level. A test fails without this fix.
  - **Liquidation (medium).** The check is met only when every stop is priced. A priced stop past the liquidation is not
    met, and anything else is unknown.
  - **Allocations (medium).** An entry with no stops or no targets totals 0% there, which is not met.
  - **Dialog (low).** The checklist opens at every Plan it, as the contract says, including when every check is met. The
    earlier refinement is withdrawn.
  - **History (low).** A plan from before the pre-flight says "Pre-flight not recorded".
  - **Check.** After the rebase onto main, `scripts/check.sh` passed: backend 3,574 passed, 5 skipped; frontend 151
    files and 3,782 tests.

- **Sol (Codex) round 2.** All five round-1 findings resolved. Three new findings, all fixed:
  - **Priced stops (medium).** The check reused the stop-coverage gap, which also requires entry shares to total 100%,
    so a half-sized entry with its stop was recorded as missing it. It now asks only whether each priced entry has a
    stop that resolves to a price; shares stay with the allocation check, and with no priced entry it is unknown.
  - **Empty checklist (low).** An empty pre-flight counted as recorded in Review but not in History. The server now
    stores and reads it as no pre-flight.
  - **Lifecycle doc (low).** It no longer describes the withdrawn plan-at-once shortcut.
  - **Test.** An all-clear plan now has a test that fails if Plan it skips the checklist.
  - **Check.** `scripts/check.sh` passed: backend 3,576 passed, 5 skipped; frontend 151 files and 3,784 tests.

## 1.6 Capture friction and time consistency

1.6a is recorded with its own pull request.

### 1.6b–d Manual entry, Duplicate and Confirm rows

October 20, 2026, branch `t3/phase1-capture`, stacked on 1.1b:
- **Manual entry (1.6b, F-01).**
  - Each field shows its own error under it, marked invalid and described by the message. The first takes focus, and
    typing clears it. An empty Source highlights the Source field (U11).
  - The time is typed in the browser's zone (1.6e makes it the display zone) with **Now** and a "Sent as … UTC"
    line. A trailing `Z` or an offset is read as written. A time the zone skips, or 30 February, is refused, never
    moved. Fractions are kept digit for digit, so an unchanged correction sends the same instant.
  - The side follows the role and the Play's direction until the owner chooses one, and follows again when the role
    changes.
  - **Source label (required)** sits beside the time and side. Text fields are sent trimmed.
- **Duplicate to Draft (1.6c, F-04).**
  - The header of a saved planned-origin Play has **Duplicate**. Unsaved work (edits, a manual execution or a log
    entry) disables it, and a retrospective Play does not offer it.
  - It opens an unsaved Draft with the account, instrument, direction, leverage, the current plan's entries, stops and
    targets, thesis, invalidation and strategy, titled "<title> (copy)". Nothing else is copied, and the copy has no
    saved or observed identity.
  - The usual plan checks apply before the first Save creates it. An Open long's managed stop at 105 over an entry at
    100 is flagged in the copy until moved.
- **Confirm rows (1.6d, F-03).**
  - Suggestions are grouped by order: one row per order, a **Link as <level>** button per level it matches, and
    **Not this play** turning all of them down in one action.
  - The execution's suggestions carry `otherPlay`: whether the same order is also suggested to another Planned,
    Paused or Open Play of the account, read in one query. Only then does a row say "another play"; several levels of
    one Play read "Matches several levels of this play" (U7). Linked orders carry null.
- **Tests.**
  - Unit: the time in each zone (offsets, the spring-forward gap, microseconds, Now), per-field errors, the side rule,
    trimmed corrections and per-row batch errors; Duplicate's copy and session.
  - Panel: U11's Source error and focus, Now in Europe/Madrid sent as UTC, the side following the role, a stored
    microsecond time shown in Europe/Madrid and sent back unchanged.
  - Page: Duplicate's flagged stop, its created fields and untouched original; disabled with unsaved work and absent
    on a retrospective Play.
  - Confirm rows: U7's two orders in two rows with both levels and no "another play", the other-play wording, one
    action turning an order down; PostgreSQL: four suggestions for two take-profits, `otherPlay` on an entry two
    Plays match, false once the other Play is cancelled.
- **Checks.** `scripts/check.sh` passed: backend 3,609 passed, 5 skipped; frontend 153 files and 3,839 tests; build clean. A
  first run found the monitor contract fixture without the new `otherPlay` field; it was regenerated.

- **Sol (Codex) round 1.** Three findings, all fixed:
  - **Not this play (high).** Turning an order down sent one request per level, so a check between them could link the
    remaining level, and turning every level down still let the order link as an unplanned exit and close the Play.
    It is now one request, `POST /api/plays/{id}/execution/rejections`: under the account lock it declines every
    suggestion of that order to this Play and dismisses it for each level of the current plan and as an unplanned exit.
    An order linked to this Play is refused. A PostgreSQL test reproduces the reviewer's case; the two-owner isolation
    test covers the route.
  - **Autumn overlap (high).** A stored time in the repeated hour, shown and sent back unchanged, moved by an hour. Such
    a time is now shown with its offset, and Now does the same; a repeated local time typed without an offset asks
    for one. Tests cover both occurrences.
  - **Focus order (low).** A refused submit focuses the first invalid field in the form's order. A test fails without
    this fix.
  - **Check.** `scripts/check.sh` passed again: backend 3,611 passed, 5 skipped; frontend 153 files and 3,845 tests.

- **Sol (Codex) round 2.** Two findings, both fixed:
  - **Rejection and revisions (high).** A revision adding a level the rejected order matched let the next check link
    it. "Not this play" now keeps one `Rejected` link record for the Play and order; matching treats it as declined at
    every level and as an unplanned exit. The rejection reads no plan, so a concurrent revision cannot escape it. The
    owner can still link the order by hand. The PostgreSQL test adds a matching target after the rejection.
  - **Other overlaps (medium).** Ambiguity was checked at fixed steps, missing Casey's three-hour overlap. A typed time
    is now matched against every offset the zone uses around it; tests cover Casey and Lord Howe.
  - **Check.** `scripts/check.sh` passed again: backend 3,612 passed, 5 skipped; frontend 153 files and 3,848 tests.
