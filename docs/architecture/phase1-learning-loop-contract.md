# Phase 1: close the learning loop

October 20, 2026. The owner asked for Phase 1 of the October 18 expert-trader
review, built on the open Phase 0 pull requests. The review is not committed
to this repository; defect, finding and decision numbers (D-xx, F-xx, G-xx)
are its own. This contract bounds the delivery. It does not authorize Phase 2
or later work, deployment, image publication or any change to the owner's
database.

Phase 1 does not change the scored result basis (decision 1, the venue
result), fee-only losses counting as losses (decision 8), the ladder
thresholds, the pooled ladder, the 20-decided window or the exact-zero scratch
rule.

The owner answered on October 20:
- decision 3 (review completion), decision 4 (unresolved Plays) and decision 5
  (correction batch), as recommended;
- decision 9: a plan written after the first execution may answer "Followed"
  and is counted in the cells, marked only by its badge;
- the integration base;
- an adversarial Sol (Codex) review for every pull request;
- a display time-zone setting.

Every part of this contract is therefore authorized.

## Rules for every item

- Phase 0's rules apply:
  - venues stay read-only, and a price touch is never a fill;
  - unknown stays null and is never shown as zero;
  - financial values stay exact decimal strings;
  - capabilities, never venue names, drive behaviour;
  - the approved section positions do not move;
  - colours come from theme tokens;
  - migrations are applied explicitly;
  - every executed probe becomes a test that fails before its fix.
- **Cases, not rates.** No new view shows a rate unless its own denominator
  is at least 10. Nothing ranks setups, tags or adherence cells. Nothing labels
  a decision good, bad or lucky. No money is attributed to a mistake. Nothing
  is written, scored or inferred by AI. No streaks, badges for activity or
  review-count goals.
- **Nothing is guessed.** Review completion is never inferred from
  timestamps. A result's sign is never inferred from account flatness. A
  stuck Play's outcome is never estimated.
- **Plan it is never blocked** by the pre-flight, and saving a Draft never
  requires a thesis or stops.
- **New places live inside existing panels.** The journal gains a tab and
  buttons. Review gains sections below the scorecard. Settings gains a field.
  No section moves.
- **Migrations.** New migrations order after 0.4c's
  `20261019132931_MonitorCoverage`. They stay in one linear chain of pull
  requests (1.5, then 1.2, then 1.3), so model snapshots never conflict.

## Integration base (owner chose option A)

The Phase 0 chain tips conflict with one another, all in frontend files and
the changelog:
- #9 conflicts with #17 and #18 in `PlayWorkspace.tsx`, `PlaysPage.tsx` and
  `WorkspacePanels.tsx`;
- #14 conflicts with #18 in `ExecutionPanel.tsx`;
- #17 conflicts with #18 in `MonitorPanel.tsx`, `ExecutionPanel.tsx`,
  `PlayChart.tsx`, `PlayWorkspace.tsx`, `PlaysPage.tsx` and `decimal.ts`.

Phase 1 edits these same files, so its code stacks on one branch,
`t3/phase1-base`, rather than on one Phase 0 tip.
- **Contents.** The branch merges #9 (0.6), #14 (0.3, which carries 0.2), #17
  (0.4c, which carries 0.4a and 0.4b) and #18 (0.5b, which carries 0.1 and
  0.5a). Conflicts are resolved once, in its merge commits.
- **It is never merged.** It is a draft pull request titled "Phase 0
  integration — do not merge", with the conflict resolutions in its
  description so the Phase 0 thread can reuse them.
- **When Phase 0 changes,** the new tips are merged into the base. The base is
  never rebased, and Phase 0 branches are never pushed to, rebased or
  force-pushed from this work. Phase 1 branches are then rebased onto the new
  base.
- **When Phase 0 merges into main,** the base is rebuilt from main and any
  unmerged tips, and Phase 1 pull requests are retargeted in order.
- This contract and the state document stack on #7. 1.6a has no Phase 0
  overlap, so it stacks on this contract.

## 1.1 Actual versus plan

Covers G-03, G-14 and F-06.

**1.1a Receipt (no decision).**
- **Per entry**, in the Execution panel:
  - the average fill against the planned price, with the signed difference;
  - the filled quantity against the planned quantity.

  Planned values come from the plan revision in force at the entry's first
  fill. A planned quantity the plan cannot derive is unknown.
- **The receipt** appears in the Execution totals and as a compact header at
  the top of the Review tab. It shows:
  - the venue result and the Play-local result with their basis labels, and
    the "sign differs" flag (0.3);
  - fees by token;
  - estimated R with its denominator, or the reason it is unknown, by
    Review's rule;
  - time held, from the first entry fill to the last exit fill. While Open it
    reads "held so far".
- **Backend.** The Play execution read gains the estimated R, its reason, and
  the first-entry and last-exit fill times. Nothing is computed in the browser
  that Review computes on the server.
- **Save in the journal.** The journal's action row and its Expand dialog get
  a Save button that runs the header Save, including its revision-reason
  dialog. The header Save stays.
- **Retrospective receipt.** The existing "No original plan" panel lists the
  trade's facts:
  - direction;
  - entered and exited quantity;
  - average entry and exit price;
  - the result on both bases;
  - fees;
  - time held;
  - the fill count.

  It shows no R and no plan comparison.

**1.1b Fills on the chart.**
- Linked fills are drawn as markers at their time and price: buy and sell
  shapes, with tooltips, never as price lines.
- Plan levels keep their lines and labels, so a fill marker cannot be read as
  a level.
- Closed and retrospective Plays open the chart on the trade's dates, padded
  either side, instead of today.
- The review reported a popup covering a focused field once. It must be
  reproduced before notification delivery changes; if it does not reproduce,
  the state document says so.

**Acceptance.**
- A Closed long with entry planned at 100, filled 0.5 of 1 at an average of
  100.4, shows "100.4 vs 100 (+0.4)" and "0.5 of 1".
- The Review tab header shows both results, fees, estimated R with "n of N"
  or its reason, and time held.
- A retrospective Play's panel lists its facts.

## 1.2 Honest review queue and in-trade log

Covers D-14 and G-04. Decision 3 was accepted as recommended.

- **Completion is explicit.** A Play gains a review completion: a time and a
  kind (`completed`, `not-needed` or `legacy`). Completion comes only from:
  - **Complete review**, on a Closed, Cancelled or Unresolved Play. It works
    with unchanged text, and with no text;
  - **saving changed structured answers** on such a Play;
  - **No review needed**, a one-click action offered only on Cancelled
    Plays. The server checks that the Play has no linked fills.
- **Review again** clears the completion. Text and answers stay.
- **History.** Completion, reopening and no-review-needed are written to the
  review history with their time.
- **Editing does not complete.** `Annotate` stops setting completion.
  `ReviewedAtUtc` keeps meaning "review last edited".
- **Lists.** Needs review and Reviewed read the completion instead of
  `HasReview || ReviewedAtUtc != null`.
- **Review metrics.** "Written reviews" counts completions, and its label
  names that.
- **Legacy.** The migration records a `legacy` completion for every Closed or
  Cancelled Play that the Reviewed view lists today. The time is its
  `ReviewedAtUtc`, else its end time. Nothing else is reclassified.
  - An Open, Planned or Paused Play keeps any Review-box text, but needs
    Complete review after it closes.
  - Saved text is never moved, rewritten or turned into log entries.
- **In-trade log.** The journal gains a Log tab: append-only, timestamped
  entries of up to 2,000 characters, on any saved Play.
  - Each entry may carry one state: calm, rushed, FOMO, tired or after a
    loss.
  - Entries cannot be edited or deleted. A mistake is corrected by a new
    entry.
  - Log entries never count as a review, never complete one and never feed
    Review metrics.
  - They are stored in their own owner-scoped table, not in the Review text.
- **The Review box while Open.** It stays, with a hint that the Log is for
  in-trade notes and that the review completes only after close.
- **Leaving a final state.** When an Unresolved Play returns to Open, or
  venue facts close it, its completion is cleared and the history says why.
  - Its text and answers stay.
  - While it is Open again, its stored answers save unchanged, but they
    cannot be changed and they do not complete the review.
  - Once it ends again, it is in Needs review until Complete review.
- **Concurrency.** Writes are version-checked against the Play and return
  409 when it changed.

**Acceptance.**
- `Verify2.F8` and `More4.EarlyReview`: a note written in the Review box
  while Open, followed by a close, lands in **Needs review** until Complete
  review.
- An Unresolved Play goes through Complete review, then return to Open, a
  Save, and a close from venue facts. It lands in Needs review.
- Complete review with unchanged text moves the Play to Reviewed, and Review
  again moves it back.
- A no-fill cancellation leaves the queue with one click.
- A log entry never changes either list.

## 1.3 Record repair

Covers D-06 and D-15.

**1.3a Unresolved (decision 4 accepted).**
- **The status.** A new owner-declared `Unresolved` status ends an Open Play
  whose venue executions can never settle it. It is distinct from Closed.
- **Who can set it.** The owner only, never the monitor. It is never set
  from account flatness.
- **What it requires:**
  - the Play is Open and has imported executions;
  - a reason: shared close, flip, forced exit not retained, or other;
  - a note;
  - the expected version.
- **Evidence.** The status change stores a snapshot of the Play's execution
  facts at that moment: entered, exited and open quantity, linked orders,
  and the integrity problems `ExecutionIntegrity.Problems` reports.
- **Result.** The result stays null and no sign is inferred.
- **Review.** It is excluded with the reason "unresolved". The exclusion
  drill-down lists it with its reason, note and evidence. Coverage counts it.
- **Sizing.** The record says "N unresolved not counted" beside the ladder.
  The ladder, the window and the Activity fallback rule do not change.
- **Matching and settlement.** An Unresolved Play takes no new links and is
  never offered order suggestions.
  - Fills on its existing linked orders are still reconciled under the
    account lock.
  - If they settle the Play, it becomes Closed from venue facts. That uses
    the same transition and closed-result event as an Open Play, and the
    unresolved record stays in history.
  - Linking another order needs a return to Open first.
- **Monitor.**
  - The Play leaves the open-Play position comparison.
  - Its linked orders are watched under the existing Closed/Cancelled rule,
    including its bounded window and visible "unverifiable" expiry.
  - The window starts at the declaration time, which is the Play's end time.
    Returning to Open ends that watch, and a later declaration starts a new
    window.
  - A position still at the venue then shows as unexplained, never as this
    Play's.
- **Return to Open.** The owner can return it to Open, with a reason.
- **Review.** It can be reviewed like a Closed Play.
- **Lists.** It appears in Needs review or Reviewed and in All, with an
  "Unresolved" chip. The Closed tab stays Closed only.
- **Events.** It uses the existing status-change event. No notification rule
  is added.

**1.3b Manual correction batch (decision 5 accepted).**
- **The request.** `POST /api/plays/{id}/manual-executions/corrections`
  carries:
  - `requestId` and `expectedVersion`;
  - one reason;
  - one or more corrections to existing rows, each a full row like today's
    single-row PUT.
- **Validation.** Each correction is validated as a row. Only the final
  ledger is checked as a whole, so 1/1 → 2/2 succeeds in one batch.
- **Atomicity.** The batch applies all or nothing in one unit of work, and
  retries with the same `requestId` return the first result.
- **History.** Each row's before and after is kept, grouped by a shared
  batch ID.
- **Limits.**
  - A Closed ledger must remain closed.
  - The batch never adds, deletes or reopens rows.
  - The batch counts toward the existing record and history limits.
  - Single-row corrections keep working.

**Acceptance.**
- `More4.CloseApi`, `Exec.SharedExit` and `Exec.FlipOrder`: the stuck Play
  can be declared Unresolved. It is excluded from Review with that reason,
  counted beside the ladder, and returns to Open on request.
- A delayed exit fill on an order already linked to an Unresolved Play closes
  it with the normal closed event. The test uses an account with no other
  active Play.
- An old linked stop's watch window starts at the declaration, and a second
  declaration starts it again.
- `Verify.F5`: a closed manual 1/1 corrected to 2/2 in one batch, with both
  rows' history under one batch ID. An invalid final ledger leaves nothing
  changed.

## 1.4 Process and outcome as cases

Covers G-01, D-17 and F-08.

**1.4a Badge and sample sizes (no decision; ships first).**
- **"Plan written after first execution".** The badge shows when a
  planned-origin Play's `PlannedAtUtc` is later than its earliest linked or
  manual execution. It is computed from stored times and shown:
  - in the Play header;
  - in the Review tab header;
  - in Review's Play lists.

  It changes nothing in sizing.
- **Denominator-specific n (F-08).** Each metric names its own n:
  - batting average: decided Plays;
  - average win: wins;
  - average loss: losses;
  - estimated R: Plays with a complete initial stop.

  The existing "Small sample" marker (below 20) is applied per denominator,
  including the highest and lowest asset cards.
- **"Estimated R expectancy (planned-stop model)".** This is the existing
  average with a new label. The statistic does not change.

**1.4b Process cases (decision 9: allowed and counted).**
- **The 2×2.** Review gains a Process section below the Breakdown: adherence
  (followed, deviated) × outcome (win, loss). Each row also states its
  scratch count. Cells hold counts, and each opens its drill-down.
- **Population.** Closed, Cancelled and Unresolved Plays in Review's scope
  and filters.
- **Period time.**
  - Closed: Review's close time, the latest exit.
  - Cancelled: the cancellation time.
  - Unresolved: the declaration time.
- **Lanes.** Each Play falls in the first of these that applies:
  1. **Retrospective:** retrospective origin, scored or not.
  2. **Unscored:** planned origin, not scored by Review (excluded, Cancelled
     or Unresolved), with at least one structured answer. Unscored Plays
     without answers appear only in the exclusion list.
  3. **Unanswered:** scored, with no adherence answer.
  4. **Not applicable:** scored, answered not applicable.
  5. **The cells:** scored and answered followed or deviated, by win or loss.
     Scratches are counted per row.
- **Answers.** The lanes read the answers as stored. A completed review is
  not required.
- **Drill-down.** A paginated `GET /api/review/process` takes:
  - the same filters;
  - one cell, lane, mistake tag or skipped check.

  It returns those Plays with nullable financial values. Every count equals
  the membership of its drill-down across all pages.
- **Post-hoc plans.** A Play whose plan was written after its first
  execution counts in its cell like any other. Its drill-down row carries
  the badge.
- **Mistake tags.**
  - Counts of Plays per tag, with win, loss, scratch and unscored counts and
    drill-down. No money totals.
  - Tags are grouped case-insensitively after trimming, and shown in their
    latest spelling.
  - The review form autocompletes from the owner's earlier tags, read from a
    bounded owner-scoped endpoint.
- **Origin and Direction.** The Breakdown gains Origin (planned,
  retrospective) and Direction (long, short) groups with the existing
  metrics.
- **Rates.** A share appears only where its own denominator is at least 10.
  Rows are listed in a fixed order, not by result.
- **Contracts.** `ReviewPlayDto` gains:
  - adherence;
  - mistake tag;
  - direction;
  - the post-hoc flag;
  - the review completion.

**Acceptance.**
- The A5 case (20 scratches + 1 win) marks batting average as a small sample
  of 1 decided.
- T29: a manual Play planned on October 18 for an October 15 execution shows
  the badge and counts in its answered cell.
- Cells under 10 show no rate.

## 1.5 Lessons return and a recorded pre-flight

Covers G-02 and G-05. No decision. Hard requirements would need one.

- **Recent next actions.**
  - `GET /api/plays/next-actions` returns the owner's most recent non-empty
    next actions: at most 10, newest review edit first, each with its source
    Play's ID, title and close time.
  - The journal's Thesis tab shows them on Draft, Planned and Paused Plays,
    in a collapsed "Your recent next actions" list. Each links to its Play.
  - **Copy into plan** appends `If …, then <next action> (from <title>)` to
    the thesis notes for the owner to complete. Nothing is copied without
    that click.
  - On a Planned or Paused Play, the change saves through the normal
    revision-reason dialog.
- **Pre-flight at Plan it.** A non-blocking checklist dialog opens when the
  owner chooses Plan it. Each check shows passed, skipped or unknown:
  - a priced stop for every priced entry;
  - entry shares total 100%, and stop and target shares total 100% per
    entry;
  - thesis and invalidation are written;
  - risk is within the sizing setting × the ladder level, using 0.1's risk
    denominator;
  - the estimated liquidation lies beyond every stop, using
    `stopsPastLiquidation`.

  "Unknown" means an input is missing, such as the balance or the record.
  **Plan it anyway** and **Back to the plan** are both always available.
- **Recorded with revision 1.**
  - The planning request carries each check's code and outcome. The server
    accepts only known codes and outcomes, and stores them on plan revision
    1.
  - Plans written before this change have no recorded pre-flight, and say
    so.
  - Retrospective Plays have none.
- **In Review.** Scored and unscored Plays are counted per skipped check,
  with drill-down, plus "not recorded".

**Acceptance.**
- A Draft with an unpriced stop can still be planned. Its revision 1 records
  the skipped check, and Review counts it.
- Copying a next action into a Planned Play asks for a reason and writes one
  revision.

## 1.6 Capture friction and time consistency

Covers F-01, F-03, F-04, F-05 and G-13 (plus the F-06 preview). No decision.

**1.6a Activity and the retrospective preview (stacks on this contract).**
- The Activity Play column shows the Play's title as a link that opens it.
  The Activity DTO gains the title. A row-level **Journal this trade** button
  opens the retrospective preview for an eligible operation.
- The retrospective preview leads with a human summary:
  - direction and asset;
  - quantity;
  - average entry and exit;
  - result;
  - dates.

  Raw fill IDs and transaction hashes move behind a disclosure.
- The title defaults to the blank-title rule
  `<asset> <Direction> · <D Mon YYYY>`, editable.

**1.6b Manual entry.**
- Errors appear on their own fields.
- The time gets a **Now** button and is entered in the display time zone,
  then sent as UTC.
- The side follows the role and the Play's direction, and stays editable.
- The Source label stays required, and sits visibly with the fields.

**1.6c Duplicate to Draft.**
- **Where.** The Play header of a planned-origin Play gets **Duplicate**. A
  retrospective Play has no plan to copy and does not offer it.
- **What it opens.** An unsaved Draft in the editor, as New play does, and
  the first Save creates it through the existing create endpoint. It carries:
  - the account and instrument;
  - the direction and leverage;
  - the current plan's entries, stops and targets;
  - thesis, invalidation and strategy;
  - the title "<title> (copy)".
- **Plan checks.** The usual checks apply before that first Save, and the
  owner fixes what they flag. They flag, for example:
  - a stop that was valid only because its entry had filled, such as one
    beyond the entry;
  - plan text kept only because an older plan stored it.

  Nothing is created on the server before that Save.
- It copies no executions, links, review, log, evidence, drawings,
  pre-flight or history.
- Copying actual exits into the plan, or re-splitting target shares, happens
  only through separate explicit actions, never by default.

**1.6d Confirm rows (F-03).**
- Suggestions are grouped by order, so each order is listed once with every
  level it matches.
- The copy reads "matches several levels of this Play" when the matches are
  within one Play. "Another play" appears only when the other Play exists.

**1.6e Time and money (last; touches every page).**
- **A display time-zone setting** in Settings: **Local**, the browser zone
  and the default, or **UTC**.
  - It is stored with the other browser preferences per signed-in user, like
    appearance.
  - Every displayed time uses one formatter that reads it: Activity, Review,
    the chart axis and crosshair, Execution, Monitor, History, the Plays list
    and manual entry.
  - Every time names its zone, and a tooltip shows the other zone.
- **Day boundaries.** Server filters keep UTC days until Phase 2's reporting
  time zone. Activity's custom date range and Review's as-of line say
  "UTC". The setting changes display only.
- **Money and numbers.** One money formatter and one number formatter, both
  `en-US`, replace the scattered ones. Display only; exact strings are
  unchanged.

**Acceptance.**
- U17: the same fill shows the same time in Activity, Execution and the
  chart under either setting.
- U7: two position take-profits produce two rows, not four, with no
  "another play".
- U11: an empty Source highlights its field.
- Duplicating an Open long with a managed stop at 105 over an entry at 100
  opens a Draft whose stop is flagged until it is moved. A retrospective Play
  offers no Duplicate.

## Probe scenarios as tests

| Probe | Scenario today | Test after the fix |
| --- | --- | --- |
| `verify-sol-dotnet/Verify2.cs` F8, `verify-opus-dotnet/More4.cs` `EarlyReview` | A Review-box note while Open, then a close, lists the Play as Reviewed | Needs review until Complete review |
| `More4.cs` `CloseApi` | Closed or resolved rejected on an Open Play | Unresolved accepted with reason, note and evidence |
| `correctness-dotnet/Exec.cs` `SharedExit`, `FlipOrder` | The Plays stay Open forever | Declared Unresolved, excluded with that reason, counted beside the ladder |
| `verify-sol-dotnet/Verify.cs` F5 | A closed 1/1 → 2/2 is refused in every order | One batch succeeds; an invalid final ledger changes nothing |
| Hands-on U7 | Two position take-profits produce four Confirm rows, "another play" | Two rows, "several levels of this Play" |
| A5 | 20 scratches + 1 win, no small-sample warning | Batting average marked as a sample of 1 decided |
| T29 | A plan written after the execution looks contemporaneous | Badge shown; counted in its cell |
| Hands-on U17 | One fill two hours apart between pages at UTC+2 | One time under either setting |

## Owner decisions (October 20, 2026)

| Question | Answer |
| --- | --- |
| Decision 3, review completion (1.2) | Yes, as recommended: explicit Complete review, structured answers count, log entries never count, legacy Reviewed stays. |
| Decision 4, Unresolved (1.3a) | Yes, as recommended: owner-declared status, independent of the monitor, null result, no inferred sign. |
| Decision 5, correction batch (1.3b) | Yes. |
| Decision 9, post-hoc plans (1.4b) | Allowed and counted in the cells; the badge marks them. |
| Stacking | An integration base merging the Phase 0 tips, never merged itself. |
| Verification | One adversarial Sol (Codex) review per pull request before merge. |
| Time display | A user setting, Local by default, aligned on every page. |

## Delivery

Small pull requests against `sloppitty/vessel`. Each pull request's diff
stays within its own item.

```text
#7 → contract + state (this document)
     └─ 1.6a Activity link, Journal this trade, retrospective summary and title
t3/phase1-base (draft, do not merge: #9 + #14 + #17 + #18)
└─ 1.1a receipt, journal Save, retrospective receipt
   └─ 1.4a post-hoc badge, per-metric n, R expectancy label
      └─ 1.5 next actions and pre-flight            (migration)
         └─ 1.2 review completion and log            (migration)
            └─ 1.3a Unresolved                       (migration)
               └─ 1.3b correction batch              (migration)
                  └─ 1.4b process cases
                     └─ 1.1b fill markers, trade-dated chart
                        └─ 1.6b–d manual entry, Duplicate, Confirm rows
                           └─ 1.6e time zone setting and formatters
```

Progress is recorded in
[phase1-learning-loop-state.md](phase1-learning-loop-state.md) as each part
is verified. The contracts these items touch change with the code, and only
as far as the owner's answers allow:
- `play-lifecycle.md`;
- `diary-completion-contract.md`;
- `portfolio-review.md`;
- `list-paging.md`;
- `sizing-suggestions.md`;
- `play-monitor-plan.md`;
- `activity-operations.md`;
- `plays-workspace-contract.md`;
- `appearance-settings.md`.

Process:
- `scripts/check.sh` runs before each pull request, with the pinned SDK and
  pnpm.
- PostgreSQL and RabbitMQ tests run against disposable containers.
- The state document and changelog go in a separate commit after the check
  passes.
- Each pull request gets one adversarial Sol (Codex) review before merge.
