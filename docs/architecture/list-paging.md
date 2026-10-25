# List paging: Activity and Plays

October 16, 2026: the owner asked that Activity and the Plays list stay usable as
history grows. Decisions:

- Activity uses numbered pages with a 25 / 50 / 100 rows selector, default 25.
- Plays uses the same pager for ended and mixed views. Drafts and In progress
  are shown in full because they stay small.
- Drafts are split from In progress, and Closed from Cancelled.
- Plays search covers title and instrument. Since October 22, 2026, a
  separate journal search covers the notes, review and log; see the last
  section.
- Loading shows a delayed skeleton that matches the row geometry.

Research basis (October 16): the cost of an Activity request is the aggregation of
retained fills, not skipping rows, so offset pages over the already-sorted
operations are exact and cheap. Plays are a few thousand rows at most per owner,
where offset plus an exact `count(*)` on an owner-scoped index is cheap. No
virtualization or TanStack Query is needed at 100 rows per page; the existing
`AbortController` loading pattern stays.

## Shared query rules

- `page` is 1-based, default 1. A page past the end is clamped to the last page
  (or 1 when empty); the response reports the page actually returned.
- `pageSize` is an integer from 1 to 100, default 25. The UI offers 25, 50 and
  100; Overview asks for 5. Anything else is 400.
- `scope` uses the Overview summary format: `all` (default), `unassigned`,
  `portfolio:{id}` or `account:{id}`. Unknown portfolio/account is 404. Disabled
  and foreign accounts are never included.
- Unknown enum values, malformed GUIDs/dates and over-long text are 400.
- Responses are `no-store`, like other owner financial data.

## Activity

`GET /api/activity` replaces the bounded document with a page.

Query, in addition to the shared rules: `accountId` (existing, account detail),
`account` (GUID), `portfolio` (GUID or `unassigned`), `contract` (≤128 chars),
`direction` (`long`|`short`), `status` (`open`|`partial`|`closed`|`incomplete`),
`period` (`all` default, `7`, `30`, `90`, `custom`), `from`/`to` (`yyyy-MM-dd`,
only with `custom`, inclusive UTC days). Filters keep the existing client
semantics in `frontend/src/features/workspace/activity-filters.ts`: dates use
the operation's latest execution, rolling periods count back from the server's
`asOfUtc`, and `partial` is an open operation that has already exited some
quantity (port `operationStatus` exactly).

Order: `lastOccurredAtUtc` descending, then account ID, then operation ID
(ordinal), so pages are deterministic.

Response:

```text
{
  operations: ActivityOperation[]   // as today, without `fills`/`omittedFillCount`
  page, pageSize, totalCount         // totalCount after all filters
  scopeCount                         // operations in scope before user filters
  retainedFillCount, asOfUtc
  version                            // opaque token of the scoped operation set
  facets: { accountIds: GUID[], contracts: string[] }   // from the scoped set, sorted
  coverage, scopeNote                // coverage limited to accounts in scope
}
```

Aggregation still runs over all retained enabled-account fills before filtering
and paging, so every metric and coverage notice is unchanged. The 500-operation
and 10,000-row response caps are removed; `scopeNote` drops that sentence.
`version` changes when scoped operation summary metrics, coverage notices or retained
funding details change. It hashes summary projections without loading execution
DTOs, once per aggregate; it is not a security token.

`GET /api/activity/fills?accountId={id}&operationId={id}` returns one
operation's constituent rows on expand:
`{ operationId, fills: OperationFill[], omittedFillCount }`, chronological, at
most 1,000 rows (earliest kept). It re-aggregates only that account. A missing
operation (history changed) is 404.

## Plays

Plays gains a `Direction` column (migration backfilled from
`"Plan"->>'direction'`, maintained on create and plan revision). The list
projects summary columns only and never reads or parses `Plan`, `Drawings` or
review text into memory.

Views:

| view | rows | order | paged |
| --- | --- | --- | --- |
| `drafts` | Draft | updated desc, id | no |
| `progress` | Planned, Paused, Open | updated desc, id | no |
| `review` | Closed/Cancelled without a review | ended desc, updated desc, id | yes |
| `reviewed` | Closed/Cancelled with a review | ended desc, updated desc, id | yes |
| `closed` | Closed | ended desc, updated desc, id | yes |
| `cancelled` | Cancelled | ended desc, updated desc, id | yes |
| `all` | every Play | updated desc, id | yes |

"With a review" means an explicit review completion (`reviewCompletion` is
set) since October 20, 2026; see the review completion section of
`play-lifecycle.md`. Until then it was `hasReview || reviewedAtUtc != null`.
The upgrade keeps exactly those Plays as `legacy` completions. It is
evaluated in SQL. Unpaged views return one page of at most
200 rows, or of an explicit smaller `pageSize` such as Overview's 5; `totalCount`
stays exact so the UI can say the list was cut.

Without `scope` (the Plays page) the list keeps every owner Play, including
Plays on disabled accounts, as before; any explicit scope, including `all`
from Overview, keeps enabled accounts only.

`GET /api/plays?view=&q=&scope=&page=&pageSize=` returns
`{ plays: PlaySummary[], view, page, pageSize, totalCount }`. `q` is trimmed,
at most 100 characters, case-insensitive substring of title or instrument with
`%`, `_` and `\` escaped; blank means no search.

`GET /api/plays/counts?q=&scope=` returns
`{ drafts, progress, review, reviewed, closed, cancelled, all }` from one
grouped query with the same search and scope.

## Frontend

- Shared `Pager` (`components/ui/pager.tsx`): "1–25 of 1,234", Previous/Next,
  numbered pages with ellipses (first, last, current ±1), and a "Rows per page"
  `NativeSelect` with 25/50/100. `nav` with an accessible name; the current page
  has `aria-current="page"`. Buttons hide when there is one page.
- Shared `Skeleton` rows and `useDelayedFlag` (about 200 ms) so fast loads never
  flash. Skeleton rows copy the real row geometry, use theme tokens and drop the
  shimmer under `prefers-reduced-motion`. On page or filter changes the previous
  rows stay, dimmed with `aria-busy`, and the pager is disabled until the new
  page arrives; the skeleton is only for a first load with nothing to show.
- Activity: filters, page and page size live in shell memory with the existing
  Activity filters; any filter or size change returns to page 1. Instrument
  options come from `facets`. Changing page scrolls to the table top. Expanding
  a row loads its fills with a small skeleton, Retry on error and a "this
  operation changed, reload" message on 404. When a later page reports a
  different `version` than page 1, show "Activity changed · Reload" (reload
  returns to page 1). Account detail pages the same way; Overview asks for 5
  scoped rows without a pager.
- Plays page: tabs Drafts, In progress (default), Needs review, Reviewed,
  Closed, Cancelled and All with counts; a debounced search box; the pager on
  paged views and a notice when an unpaged view is cut at 200. Tab, search,
  page and size survive opening a Play and returning, in shell memory.
- Overview Plays: tabs Drafts, In progress, Needs review and All with scoped
  counts and the 5 most recent rows each; View all opens Plays on the same tab.
- Unresolved Plays (phase 1, 1.3a) have no tab of their own. They are ended, so
  Needs review or Reviewed lists them by their completion, and All lists them;
  Closed stays Closed only. Counts carry `unresolved`, so Drafts + In progress
  + Closed + Cancelled + Unresolved = All, and Needs review + Reviewed =
  Closed + Cancelled + Unresolved. Older servers omit it.

## Journal search and list outcomes

October 22, 2026 (Phase 2, item 13, with F-07). See
`phase2-risk-coverage-contract.md`. The paged list, its tabs, its counts and its
title/instrument search are unchanged.

**The projection.** Each Play has one `play_search` row and one
`play_search_texts` row per searchable text (migration
`20261022185604_JournalSearch`). It is derived data, never a fact.
- The texts are the title, the current plan's thesis, invalidation, strategy
  and evidence notes, the review note, lesson, mistake tag and next action,
  each log entry and each image note. Blank texts are left out.
- Each text is stored normalized (NFKC, every run of white space or control
  characters one space, invisible format characters dropped, trimmed) and
  case-folded with the same length, so a match position in the folded text is
  the same in the shown text.
- The row also holds the Play's outcome (F-07): a state (`active`, `scored`,
  `executions`, `fees`, `unresolved`, `cancelled`, or since October 23
  `not-allocated` for a Closed Play with a fill allocated between Plays, Phase
  2 item 11, whose venue result cannot be split), the latest actual exit
  execution of a Closed Play, Review's scored result and its estimated R, and
  why a scored Play has no R. They come from Review's own rules
  (`ReviewService.Score` over the facts sizing and Review read, and
  `PlayExitTime.LatestExit`). Unknown stays null, never zero. A Play's own
  figures do not depend on whether its account is enabled; Review still
  leaves disabled accounts out.

**Written in the same unit of work.** Every save records what it touched on
the context, whichever store makes it. Before the commit, one unit-of-work
participant (`PlaySearchProjector`) reprojects those Plays from the database
state of that transaction. A Play is reached directly (Plays, plan and review
revisions, status changes, log entries, image notes, manual executions, order
links, paper executions and simulations) or through its linked orders (an
imported fill by the shared order-key rule, or an order whose status, size or
position TP/SL flag changed). So Play saves and reviews, log appends, image
note edits and deletions, venue lifecycle, manual corrections, retrospective
Plays and paper evaluation all commit or roll back with their projection.
- Each projected Play is locked first (an advisory lock per owner and Play,
  in ID order, the last lock of the unit), so concurrent units take turns and
  the later one reads what the earlier committed. A Play the unit deleted is
  not locked; its rows cascade with it.
- The Play rows are then read with a key-share lock, which no edit waits on.
  A deletion that comes later waits for the projection to commit and its rows
  cascade then; a Play deleted earlier is simply not projected (Sol round 1).
- A read-only snapshot never writes the projection. A rolled-back unit keeps
  what it touched for the next unit, since projecting a Play again is harmless.

**Expiring exclusions (Sol round 1).** Review leaves out a Play with an
execution dated after its own clock, and scores it once that time has passed.
The row keeps that time as `RecheckAtUtc`, only on an `executions` outcome.
From then the list and search compute that row's outcome again, by the same
rules over the same snapshot, instead of serving the stored one. The read
writes nothing. The next write to the Play, or the rebuild, stores the new
outcome.

**Initialization.** The migration creates empty tables. The offline
`play-search-rebuild` command fills them once, dry run by default, with
`--apply` and `--all`; see `deploy/README.md`. It also rewrites rows past their
recheck time. Until a Play is projected with the current rules
(`JournalSearch.Version`), search counts it as not indexed and the list shows
its outcome as `not-indexed`.

**`GET /api/plays/search?q=&offset=`** searches the requested trading mode
(`?tradingMode=real|paper`, real when omitted). It is not a venue-fact
feature, so paper is allowed.
- `q` is trimmed and normalized like the stored text: 2 to 100 characters,
  else 400. It is a case-insensitive substring of the folded text, with `\`,
  `%` and `_` escaped. No PostgreSQL extension is used.
- Like the unscoped list, it covers every owner Play in the mode, Plays on
  disabled accounts included. A Play in the other mode or of another owner is
  never found or counted.
- `offset` is 0 to 1,000,000. Pages hold 50 Plays, newest updated first, as
  Review's drill-downs do: `{ query, items, total, offset, nextOffset,
  notIndexed }`.
- Each item is the Play's list summary, the field of its first match (in the
  field order above, then oldest log entry or image), an excerpt of about 160
  characters around the match with an ellipsis where it was cut, and the
  match's start and length in the excerpt.

**List rows.** `PlaySummary.outcome` appears on list and search rows only:
`{ state, exitAtUtc, resultUsd, estimatedR, reason }`, with `not-indexed`
when the Play has no current projection. It is read for the page's IDs from
the projection in the page's snapshot, never from the plan. The Play's own
read keeps its receipt instead.

**Browser.**
- A "Search the journal" box sits beside New play. From two characters, after
  the same pause as the list search, its results replace the tabs and list
  until Clear search. Query and offset stay in shell memory with the list.
  A finished owner action or an accepted stored-state reload (a Play the
  monitor closed, say) reads the same results again, as it does the list.
- Each result shows the Play as the list does (title, asset chip, venue logo,
  account, direction, status), the matched field and the excerpt with the
  match marked, and opens the Play. Previous and Next page by 50. A count of
  Plays not indexed yet says search cannot find them.
- An ended row's meta column adds "Exit <date> · <result> · Est. <R>". An
  unknown exit, result or R reads as unknown, with the reason in the row's
  description; "Result not indexed yet", "Unresolved · no result" and
  "No result" (Cancelled) say why there is none.
