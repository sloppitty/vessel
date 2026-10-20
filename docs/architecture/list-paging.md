# List paging: Activity and Plays

October 16, 2026: the owner asked that Activity and the Plays list stay usable as
history grows. Decisions:

- Activity uses numbered pages with a 25 / 50 / 100 rows selector, default 25.
- Plays uses the same pager for ended and mixed views. Drafts and In progress
  are shown in full because they stay small.
- Drafts are split from In progress, and Closed from Cancelled.
- Plays search covers title and instrument.
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
