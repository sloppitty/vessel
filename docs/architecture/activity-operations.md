# Activity operations

October 13 diary completion adds **Review this activity** to the drill-down.
The server previews original facts, current associations and eligibility.
Confirmation creates a retrospective Play only for a complete unassigned
round trip whose entire orders fit the selection. Split/flip, mixed,
already-linked and incomplete operations stay inspectable but are rejected
for this bounded workflow. Stale fingerprints return conflict, and identical
request retries do not duplicate Plays. Operations still describe position
facts, not inferred intent. Manual execution ledgers do not enter this feed.
See [the diary contract](diary-completion-contract.md).

October 12, 2026. Activity groups retained imported fills into position operations. This is a read-only report, not a Play creation, a thesis, an order link or a retrospective review. Overview shows the latest five operations, Activity adds filters, and account detail shows that account's operations. Every row can expand into constituent executions.

## Round trips

The Application-layer `OperationAggregator` uses normalized `side`, `positionEffect`, `feeBasis` and `pnlBasis`, retained quantities/prices and latest account snapshots. Venue direction text is display-only. Fills group by account and resolved contract identity, then walk chronologically. The canonical key remains the display/filter key. RISEx retired and replacement markets with the same canonical key are separate operations. Buy adds quantity and sell subtracts it. A snapshot observed at or after the resolved contract's last retained fill anchors the final signed position, with zero for an absent, resolved contract. Subtracting every signed fill derives the position before the first fill, equivalent to walking backward. The forward walk then uses side and quantity for boundaries, with effect labels as a cross-check.

Grouping, anchoring and snapshot comparison share one indexed identity rule. For each account and canonical key, collect distinct non-null native IDs across its retained fills and latest snapshot positions. With exactly one native ID, ID-less fills and legacy snapshot positions resolve to that native partition; all its fills keep their chronological walk. With no native IDs, retain the canonical-only partition and its normal snapshot comparison. With several native IDs, ID-less fills remain in a separate unresolved canonical partition. Native and canonical partition keys are distinct even if their identifier strings match. The unresolved partition always uses the no-snapshot forward walk: an absent snapshot row must not anchor or compare it as zero. Every operation in that partition and the report receive explicit native-identity uncertainty coverage, including closed operations and reports without a snapshot. A legacy snapshot position with several candidates makes every candidate's snapshot quantity unknown; it cannot anchor any candidate and receives identity coverage. It is never copied into several markets or treated as flat. Snapshot-only positions still receive comparison coverage. These read-time rules never rewrite stored fills or snapshots.

The adapters support this rule without venue-specific guesses: Hyperliquid leaves ordinary coin IDs null on both fills and positions, and stores the native coin name on both when a canonical alias differs. Lighter uses market indices on both; current RISEx fills and positions use market IDs. Older RISEx snapshot positions may lack that ID, and any retained ID-less fill follows the same conservative rule. An observed single native candidate does not prove lifetime market uniqueness; the rule is bounded to retained facts, never selects a replacement because it is active, and never uses another account or canonical key as evidence.

Without a usable snapshot, an initial unambiguous opening starts the bounded forward walk at zero, not a claim of lifetime coverage. An operation begins when position leaves zero and closes when it returns to zero. Partial closes and later increases remain one operation until flat. An operation still holding a position is open.

Quantity means maximum absolute position, not cumulative entry quantity or notional. Total entered, total exited and remaining quantities are separate. Entry and exit prices are quantity-weighted over their respective allocated fills. Distinct nonempty order IDs count once on each side; missing IDs produce a coverage warning and never receive fabricated identities. Fill count counts constituent retained fills in this operation. One flip belongs to both adjacent operations.

Fills sharing a timestamp are ordered consistently with their normalized effects and the position entering that timestamp. For example, a sell-close of a long must precede a sell-open of a short. The search compares fill membership, split quantities and per-operation peak position across consistent orders, not arbitrary display order within one operation. Peak position is also part of the memoized search state. If all orders preserve those boundaries, database IDs provide a stable display tie-breaker. If orders preserve boundaries and allocations but disagree on peak, only maximum quantity becomes null with explicit peak/timestamp coverage. Direction, status, times, entered/exited quantities, weighted prices and money totals remain known. A prior peak that exceeds all peaks in the tied burst remains known.

If no consistent order exists, or different consistent orders move a boundary, only operations touching that timestamp have unknown boundaries. The next known zero boundary recovers normal operations. Same-side bursts that cannot cross an internal boundary use a linear path, including unknown effects under an anchor. Same-side close/open bursts that must close exactly to zero before opening the opposite position also bypass the search. Mixed bursts over twelve fills or over 4,096 search states receive conservative timestamp-ambiguity coverage rather than unbounded permutation work.

Without a known starting size, opposing-side ties stay ambiguous; same-side closes before opens can still prove a direction change.

## Flip allocation

A valid flip must oppose the computed position and exceed its absolute quantity. The closing allocation is exactly the current absolute position. The opening allocation is exactly the fill quantity minus that closing quantity, at the same price and time. The closing operation gets **the full reported fee and closed P&L**, including any net-of-fee amount. The new operation gets zero fee and zero closed P&L from that fill. This attribution avoids a proportional fee division that could repeat indefinitely, and preserves the original reported sum across both operations. It is an allocation convention, not a claim that the opening portion traded without a fee.

`OperationFillDto` keeps the untouched `FillDto` and separate `role`, allocated `quantity`, `fee` and `closedPnlUsd`. Expanded execution rows show the allocation; a separate Original flip facts disclosure shows the original. No fill, order link, account snapshot or Play is modified.

## Exact values and results

All API monetary values, prices and quantities are decimal strings. The aggregator converts retained .NET decimals into signed `BigInteger` coefficients at scale 28. Sums, subtraction, maximum positions and flip splits remain exact even when differing decimal scales would round during normal decimal addition. Price-times-quantity products retain scale 56. Weighted averages round only at the division boundary, to 28 decimal places, halfway away from zero. Nonterminating averages cannot have a finite exact decimal representation. The scope notice names this precision limit.

Reported closed P&L sums allocated venue facts. The P&L basis is gross, net-of-fee or mixed. Net result follows the existing sizing rule: subtract listed nominal stablecoin fees on gross fills only. Net-of-fee fills already include their fees, so no fee is subtracted again. A nonzero gross fee in another token makes net result null, with a coverage reason, rather than converting it or claiming a complete result.

October 14, 2026: the operation summary displays before-fee closed P&L from
`grossClosedPnlUsd`, followed by fees and the existing net result. The aggregator
adds back allocated listed stablecoin fees only for net-of-fee facts, including negative-fee
rebates; gross facts stay unchanged. Before-fee P&L is null with coverage when a
net-of-fee fact has a nonzero fee in another token. `reportedClosedPnlUsd` and the
original executions retain the venue's reported values. RISEx net results must
not have their fees deducted a second time.

The adjacent percentage is the direction-adjusted change from weighted average
entry to weighted average exit, not a leveraged, margin or account return.
It excludes fees and is labelled as price return in its tooltip and drill-down.
Integer arithmetic rounds only the displayed percentage to two decimals.
Incomplete operations, unknown prices/direction and zero entry prices have no
percentage. Reentries can make this price comparison differ from realized
return on capital; it is not used in sizing or Review.

The display sums USDC, USDT, USDT0, USDE, USDe and USDH fees as nominal stablecoin units. These are explicit symbol labels from normalized execution facts, not a wallet token-identity validation or FX conversion. All other fee tokens stay in separate exact token totals. Since October 14, 2026 the display and the net result use the same list (see below), so a USDT or USDG gross fee is deducted nominally. Negative reported fees, such as rebates, retain their sign. Unsupported fees on net-of-fee fills do not make net result unknown because that P&L already accounts for them. October 16 adds a separate settled-funding ledger and sum; the existing execution net remains unchanged. A separate combined total is gated on coverage and a verified funding-P&L basis, which remains unverified for every current venue. See [settled venue funding](venue-funding.md).

The frontend does not total money. Sign colouring examines decimal strings directly; display formatting reuses the existing decimal-string formatter. Exact API values remain available in numeric-cell titles.

## Coverage

Retained imports are not complete lifetime history. `WorkspaceStore.SaveRefreshAsync` appends new fills and deduplicates existing source identities; it does not trim them to the overview route's 100-row display limit. Activity reads all retained facts, on enabled owner accounts only, in one repeatable-read transaction with the latest snapshots and confirmed order links.

With snapshot anchoring, a nonzero position before the first retained fill means only the earliest operation lacks its opening entries. It remains Incomplete, but keeps the known direction, exits, weighted exit price, remaining quantity and any known closing time. Opening time, peak quantity, full entered quantity and entry average stay null, even if later increases are retained. No entry average is fabricated. Following round trips are complete.

Unknown effects, including liquidation or settlement wording, do not invalidate anchored quantities. A contradictory open/close/flip label receives coverage on the affected operation, or both allocations of a flip, without discarding quantity-derived boundaries or marking later operations incomplete.

Without a snapshot, or when it predates the last retained fill, missing initial reductions, unknown effects and contradictory facts make the current size unknown. Those fills stay in an incomplete operation until facts prove flatness.

A close establishes the reduced position's direction but not its size. A subsequent open of the opposite position proves a reset before that open, which starts a normal operation. In execution-side terms, sell-close then sell-open proves a long-to-short reset; buy-close then buy-open proves short-to-long. Sell-close then buy-open may just increase the remaining long and is not a reset.

An open or flip can also establish direction while its initial size remains unknown. A flip does not establish its remaining quantity or start a complete operation; a later opposite-position open is still needed. Unknown effects and ambiguous timestamp order erase that direction proof; later reductions can establish a new one. A contradictory label in the known walk is not itself a direction proof. Unanchored unknown-boundary operations have null position-derived fields, but keep reported money and original fills. Completed operations before the uncertainty remain intact.

Missing entries do not make an anchored final position unknown. The service still reports genuine snapshot disagreements, including snapshot-only positions absent from retained fills. Missing snapshot rows mean zero only for resolved identities, not unresolved canonical-only facts or native candidates affected by an ambiguous legacy row. An older snapshot is explicitly not comparable with newer fills; an absent snapshot is reported. An unknown-boundary operation cannot be compared. Neither snapshots nor retained facts are mutated or corrected. Provider history notices remain visible.

Confirmed order links supply fill Play IDs. Only an operation whose **every** fill links to the same Play gets that Play ID. All-unlinked is Unassigned; multiple Plays or linked/unlinked mixtures are Mixed with a null operation Play ID. Suggested/dismissed links are not assignments. Multiple simultaneous Plays on a contract still form the one venue position operation, and their separate intent remains untouched.

## Endpoint and bounds

Authenticated `GET /api/activity` returns one page of newest-last-fill-first operations (October 16, 2026; see [list paging](list-paging.md)). Page rows carry every operation metric and coverage but no constituent rows. The response adds `page` (1-based, clamped to the last page or 1 when empty), `pageSize` (1 to 100, default 25), `totalCount` after filters, `scopeCount` before filters, retained fill count, `asOfUtc`, an opaque `version`, `facets` (sorted account IDs and contracts of the scoped operations), scope note and account/contract coverage for the accounts in scope. `scope` (`all` default, `unassigned`, `portfolio:{id}` or `account:{id}`) and the optional account-detail `accountId` select enabled owner accounts; both apply together. An unknown or foreign portfolio or account returns 404, and a disabled account has an empty scope. Order is latest execution descending, then account ID, then operation ID (ordinal), so pages are deterministic. Responses use `Cache-Control: no-store` and never cache authorization.

Aggregation runs over every retained fill of the scoped enabled accounts before filters and pages, so every metric and coverage notice is unchanged by them. There is no operation or fill-row cap on the list. Snapshot identities and latest-operation comparisons are indexed once by account/native contract rather than rescanning all operations per contract. Allocation records retain original fill references and exact amounts; page rows build no constituent DTOs. Expanding a row calls `GET /api/activity/fills?accountId={id}&operationId={id}`, which re-aggregates only that account and returns `{ operationId, fills, omittedFillCount }`: constituent rows in chronological order, at most 1,000, keeping the earliest, with the count of omitted rows. A missing, foreign or disabled account, or an operation that no longer exists because history changed, returns 404; a missing account or operation ID is 400. `version` hashes the scoped operation set and changes when any scoped operation's identity, fill count, last execution, open quantity or Play association changes, regardless of filters or page. A client compares it with page 1 to notice that later pages describe changed history. It is not a security token. The retrospective preview keeps its own 200-row whole-order limit.

## UI filters and colours

Activity filters run on the server after aggregation (October 16, 2026): enabled `account`, current `portfolio` membership including `unassigned`, canonical `contract` (exact, at most 128 characters), `direction` long or short, `status` open, partial, closed or incomplete, and `period` all (default), rolling 7/30/90 days or `custom` with `from`/`to` days (`yyyy-MM-dd`). Open includes partially closed operations; `partial` is an open operation with positive exited and remaining quantities, as the browser's status display defines it. Date filters use the latest constituent fill timestamp, not only opening time, so an operation opened earlier but active within the period remains visible. Rolling periods count back from the response's `asOfUtc`, so each accepted activity response advances the window, including Reload and retry. Custom dates include both selected UTC calendar days; either may be omitted. Unknown values, malformed GUIDs or dates, dates without `custom` and over-long contracts are 400; an empty value means no filter. Instrument options come from `facets`. The shell keeps account and Activity filters, page and page size in memory across detail views and page navigation; disconnect/reload discards them. Missing portfolio, enabled-account and instrument options resolve to All for both controls and filtering. Reset restores all operations in scope.

Long uses the positive green token and an up arrow; Short uses the negative red token and a down arrow. October 13, 2026 owner refinement: an expanded operation lists its executions by their own trade side from the normalized side, never venue wording: a buy is Long and a sell is Short, so a long's closing sells show as Short and a short's closing buys as Long. Open/close wording, the instrument and the account are not repeated on each execution because the operation row already states them. Order and fill counts are not displayed (the response still carries them); per-operation display omissions are stated without a count. Reported P&L and net result colour by sign. All CSS reads appearance tokens; direction badge text sits on the theme's card background, covered by built-in contrast tests. Controls have native labels, table headings and expanders with `aria-expanded`. Loading and failures never fall back to raw overview fills or synthetic operations.

October 14, 2026: the whole operation summary row toggles its executions; the
expander remains a native keyboard-operable button and expanded actions do not
toggle the row. Instrument, direction and status have distinct treatments.
Open is green, Partially closed is amber, Closed is muted, and Incomplete is
amber with a dashed border. Partially closed is display/filter progress for an
open operation with positive exited and remaining quantities, not a new API
status. Open filtering still includes partial operations. Pending is not
invented from executed activity. The summary retains peak size and shows
Remaining only above zero; cumulative entered/exited values and reporting-basis
notes move into the drill-down. Entry and exit have separate columns; the exit
colour follows the direction-adjusted price return. Times are shortened in UTC,
with full timestamps in a tooltip. Every dropdown in the app uses the shared
`NativeSelect` (except the account filter, which became the searchable
`AccountPicker` on October 16, 2026) (`components/ui/native-select`) for one chevron, one right inset,
text kept clear of it and native keyboard behaviour; density `compact` (32px) and
`bare` (borderless field bars) keep the same arrow.

## Nominal stablecoin fees (October 14, 2026)

`OperationAggregator`, sizing, Review, Overview and manual ledgers share the Application `NominalStablecoins` list (USDC, USDG, USDT, USDT0, USDE, USDe and USDH; exact, case-sensitive). Its fees are summed with exact integer arithmetic and deducted one-for-one, also when one operation mixes them; any other token stays in `otherFees` and makes the affected result unknown, never converted. The scope note and Overview summary carry the "nominal 1:1, no FX or depeg valuation" caveat because venues settling in USDC and USDG are pooled.

## Observed leverage (October 18, 2026)

The owner noticed that Activity did not recover the leverage of open or closed positions and asked to retrieve and show it. Each operation now carries `leverage` (the latest observed), `leverageMin` and `leverageMax` (whole numbers 1–1000), all null when nothing was observed. A range means the leverage changed during the operation; it is never averaged. When the latest observation time reports conflicting values (for example a snapshot and a fill at the same instant), `leverage` is null (latest unknown) while `leverageMin`/`leverageMax` keep the range; no value is picked from the tie.

**Sources, checked October 18, 2026.** Only venue-reported values count:

- **RISEx** `GET /v1/trade-history` documents `leverage` ("Leverage of position") on every account trade. `RisexReader` normalizes it onto `VenueFill.Leverage` and `ImportedFill.Leverage` (`imported_fills."Leverage"`, check 1–1000) only as a whole number from 1 to 1000; absent, null, empty, zero, negative, fractional or larger values are null, and non-decimal text is an invalid response. Fills retained before this change keep null: Vessel never stored the raw payload and never rewrites facts.
- **Hyperliquid** `userFills`, `historicalOrders` and `orderStatus` report no leverage. Only the snapshot (`clearinghouseState` position `leverage.value`) does.
- **Lighter** trades report `taker/maker_initial_margin_fraction_before`, an undocumented `uint16` whose unit and opening-from-flat meaning are unverified, so it is not normalized. Lighter leverage comes from the snapshot (`100 / initial_margin_fraction` when whole).

**Retained observations.** The latest snapshot still replaces its positions on every newer refresh. Before replacing them, `WorkspaceStore.SaveRefreshAsync` appends an `account_leverage_observations` row (owner, account, canonical and native contract, snapshot observation time, signed quantity, entry price and leverage) for each position with reported leverage that is new to the replaced snapshot, or whose signed quantity, entry price or leverage differs from the latest retained observation of that account and native contract, or whose market received newly imported executions in the same refresh (a fill without a native ID matches every native market of its canonical contract). A position can close and reopen with identical quantity, entry price and leverage between two refreshes; its new executions are the only trace of that boundary, so they always add an observation of the reopened position. Idle refreshes (the same position and only already-retained fills) add nothing, and an older observation never writes one. When the executions arrive in a later refresh than the reopening, the observation is recorded at that later refresh and still belongs to the reopened position if it is still open; a position that opened and closed entirely between refreshes was never observed and stays unknown. Rows are owner-scoped, are deleted with the account by explicit account deletion, run under the existing account lock and refresh unit of work, and are hidden with disabled accounts. The `LeverageObservations` migration also copies current snapshot positions that have a leverage, so a position open at upgrade keeps its leverage if it closes before the next refresh. Apply it explicitly like every journal migration.

**Attribution** (`OperationLeverage`, after aggregation and before funding): an observation, the latest snapshot position or a fill's reported leverage counts for an operation only when it shares the operation's account and resolved native-contract partition (the same `ActivityPositions` identity rule as grouping and anchoring), its signed quantity has the operation's direction, and its time lies from the opening (or the first retained execution when opening entries are missing) through the close, or now while the operation is open. Operations with unknown boundaries or in an ambiguous native-identity partition stay null. Observations are never grouping evidence: they do not change how fills partition. When fills, the latest snapshot and funding carry no native ID for a canonical contract (ID-less legacy fills), operations use the canonical partition, and an observation of the one native market ever observed for it is placed there too; when observations name several native markets for it, none is placed (null). An ID-less observation or latest-snapshot position is placed only while at most one native market is known for its canonical contract (from observations, fills, the latest snapshot or funding); beside several it could belong to any of them, so it is not placed either and fill grouping is unchanged. Observations are sorted once per partition and each operation reads only its own interval (binary search), so attribution stays linear in the history. A flip fill's reported leverage is assigned to neither split allocation. Snapshot observation times are Vessel's read times, not execution times. Leverage is never inferred from margin, notional, the plan or the venue maximum, and planned Play leverage is not shown here (owner-review recommendation adopted October 18). Lighter's `*_initial_margin_fraction_before` stays unused. Older operations closed before the owner refreshed with this change mostly show unknown.

The page `version` hash includes the three fields. The scope note says leverage is observed, may be a range and is never inferred. The browser shows a compact badge beside the direction in Activity, Overview's recent operations and account detail: `10×`, `3–10×` (dashed, with the latest in its tooltip, or "latest unknown" when the latest observation conflicted) or a muted `—×` "Leverage unknown". It uses theme tokens only; no section moves.
