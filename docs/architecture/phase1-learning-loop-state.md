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

- **Post-sync Sol review (October 21).** Two findings, both fixed: the contract now records the owner's October 21
  answers, unweighted n in 1.4a, the "Imported position" Origin group in 1.4b and a decisions table. Round 2 found
  nothing new.

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

- **Post-sync Sol review (October 21).** The merge with main (PR #33) is sound: scoring never sees a position row.
  Three findings over two rounds, all fixed:
  - **Plan comparison (medium).** Saving a plan revision rereads the execution, and "Against the plan" stays unknown
    until that read is shown, instead of comparing fills with the previous revision.
  - **Stale receipt (medium).** After a failed reread, the Review tab's receipt and the retrospective receipt show the
    failure instead of the earlier figures; while a reread is pending they say so.
  - **Execution tab (medium, round 2).** While the execution is stale, its figures say they come from the last read,
    and Link here, Not this play and a manual Link wait for a fresh read. An existing link can still be unlinked.
  - Round 3 found nothing new.
  - **Check.** `scripts/check.sh` passed: backend 3,590 passed, 5 skipped; frontend 152 files and 3,804 tests.

## 1.2 Honest review queue and in-trade log

Not started.

## 1.3 Record repair

Not started.

## 1.4 Process and outcome as cases

Not started.

## 1.5 Lessons return and a recorded pre-flight

Not started.

## 1.6 Capture friction and time consistency

Not started.
