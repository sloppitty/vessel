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

## 1.2 Honest review queue and in-trade log

Not started.

## 1.3 Record repair

Not started.

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
- **Main sync with PR #33 (imported positions).** Merged forward, not rebased.
  - Each n counts real results, unweighted, like every count in #33. A Play counts once in n although it weighs 2 in
    the rates. The owner chose this on October 21, 2026, over weighted units or showing both.
  - Expectancy and the asset cards read "n = 3 Plays · 2 positions" when positions are included.
  - Estimated R and sign-differs denominators count Plays only.
  - Position rows never carry the badge.

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

Not started.
