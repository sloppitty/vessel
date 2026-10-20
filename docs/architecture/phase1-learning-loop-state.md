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

Not started.

## 1.1 Actual versus plan

Not started.

## 1.2 Honest review queue and in-trade log

Not started.

## 1.3 Record repair

Not started.

## 1.4 Process and outcome as cases

Not started.

## 1.5 Lessons return and a recorded pre-flight

Not started.

## 1.6 Capture friction and time consistency

### 1.6a Activity and the retrospective preview

October 20, 2026, branch `t3/phase1-activity-journal`, stacked on the contract:
- **Activity Play column (F-05).**
  - Page rows carry `playTitle`, read with the owner filter for the shown linked rows only, after the cached aggregate.
  - Titles are not part of `version`.
  - The Play column opens the Play by its title. An unreadable title shows as "Untitled play". Long titles are cut with
    an ellipsis and the full title in a tooltip.
- **Journal this trade.** A closed, unassigned operation offers this button in its Play cell. It opens the same
  server-checked preview. Open and incomplete operations still say "Unassigned", and mixed ones say "Mixed".
- **Retrospective preview (F-06).**
  - It leads with quantity, average entry and exit, net result, and the UTC dates with the duration.
  - Fill IDs and transaction hashes move into a collapsed disclosure.
  - The title starts with the blank-title rule on the opening day in UTC, and a cleared field saves that default.
- **Tests.**
  - Backend: page rows name only their linked Play, mixed and unassigned rows stay untitled, a renamed Play keeps the
    version, and the title read is owner-scoped in PostgreSQL. The multi-owner isolation test now checks that each owner
    reads its own title and never the other owner's.
  - Frontend: the title link, the journal button, disabled links while opening, a failed open, the response guard, and
    the preview's summary, collapsed records and default title.
- **Checks.** `scripts/check.sh` passed with disposable PostgreSQL 18 and RabbitMQ 4.3.6:
  - Python: 78 tests;
  - prototype tests;
  - Release build: 0 warnings;
  - backend: 3,433 passed, 5 skipped (S3 and live venues);
  - frontend: typecheck, lint, 138 files and 2,456 tests, and the build.
- An earlier run failed once in an unrelated multi-owner worker test: a PostgreSQL read timed out while dropping a test
  database. It happened while other PostgreSQL tests ran against the same container. The test passes alone, and the
  full run above had nothing else running against the database.
- **Sol (Codex) round 1.** Two medium findings, both fixed:
  - **The title read loaded whole Plays.** It loaded plans, drawings and reviews. It now projects only the ID and
    title.
  - **Accessible names.** Two names did not start with the visible text. They now read "BTC breakout, open Play" and
    "Journal this trade, BTC".
  - **New tests.**
    - Titles are read only for the shown page.
    - A rename shows through the warm Activity cache in PostgreSQL, with an unchanged version.
    - The default title names the UTC day across a non-UTC midnight.
  - **Check.** `scripts/check.sh` passed again:
    - backend: 3,434 passed, 5 skipped;
    - frontend: 138 files and 2,457 tests;
    - build clean.
- **Sol round 2.** Both fixes were confirmed:
  - The projection compiles to `SELECT p."Id", p."Title"` with the owner filter.
  - Both accessible names start with their visible text.
  - The warm-cache and UTC-day tests are sound.

  No new finding of medium severity or above.
- **Rebased onto main** on October 20, after Phase 0 merged, with the changelog entry kept first under Unreleased.
  `scripts/check.sh` passed on main:
  - backend: 3,561 passed, 5 skipped;
  - frontend: 147 files and 3,748 tests;
  - build clean.
