# Phase 0: trust and correctness

October 19, 2026. The owner asked for Phase 0 of the October 18 expert-trader
review to begin. The review is not committed to this repository; defect IDs
(D-xx, F-xx, I-xx) and owner-decision numbers below are its own. This contract
bounds the delivery. It does not authorize Phase 1 or later work, deployment,
image publication or any change to the owner's database. It also does not
change the sizing thresholds, the pooled ladder, the 20-decided window, the
exact-zero scratch rule or the scored result basis (review decisions 1, 7 and
8).

The owner answered on October 19. Decision 2 (managed stops), decision 6
(budget, with no leverage-never-raised rule) and decision 10 (Refresh
continuity) are accepted as written below. Recovery wins stay consecutive; the
owner chose this in answer to the decision 7 question, which carried the
exit-time notice. RISEx gets the "current book not read" label. Every part of
this contract is therefore authorized.

## Rules for every item

- Venues stay read-only. A price touch is never a fill. Concurrent Plays on one
  account and instrument stay allowed. Nothing invents a thesis, intent or an
  attribution the facts do not prove.
- Unknown stays null and is never shown as zero. Financial values stay exact
  decimal strings, and nothing is rounded into a different sign.
- The approved positions of the chart, position editor, journal, capital
  context and Summary do not move. On Overview, Allocation stays between the
  metric tiles and Summary. New indicators are badges, labels and rows inside
  existing places. Colours come from theme tokens, and the contrast test must
  keep passing.
- Shared code and the browser check venue capabilities, never venue names.
- Migrations are applied explicitly. Database tests run against a disposable
  PostgreSQL container through `Vessel_TEST_POSTGRES`, never the owner's
  database or `.env`.
- Every executed review probe becomes a repository test that fails before its
  fix (see [probe scenarios](#probe-scenarios-as-tests)).

## In-flight work

- **sloppitty/vessel PR #3**, the Activity leverage and sizing fallback work
  (it includes the exact-notional change as #5), and **PR #6**, the API
  start-up work, merged on October 19. 0.2 and 0.3 build on PR #3's
  `SizingService` and `Sizing/Contracts.cs`, including the Activity fallback
  source, and keep its plays-or-positions wording. New `PlayOutcome` data uses
  init properties or defaulted trailing parameters, because the fallback
  constructs it positionally. New migrations order after
  `20261018180202_LeverageObservations`.
- **jcarcaboso/vessel PR #66** rewrites the `docs/app-status.md` trigger table
  (D-26). It is being merged separately. 0.4d waits for it and adds only the
  Refresh-continuity clause.

## 0.1 Sizing truth

Covers D-04, D-07, D-11, D-12, D-13, D-24, D-25 and F-16.

1. **Stop coverage gate (D-04).** Size and every suggestion derived from the
   size require complete priced stop coverage. "Complete" uses the Summary's
   existing coverage rule (`position-risk-summary.ts`): entry shares total
   100%, every entry is priced, each entry's stop shares total 100%, and every
   stop with a share is priced and on the correct side. Otherwise there is no
   size line and no size-driven leverage line. The block says what is missing,
   for example "50% of the position has no priced stop". A
   liquidation-only leverage line can remain, worded as an estimate against
   the priced stops.
2. **Freshness (D-13).** `useSizing` reports loading, ready, stale (a reload
   failed after an earlier success) and failed. While stale or failed, no
   suggestion offers Accept, field markers clear, and the block says the
   record could not be reloaded and offers Retry. Aborted reads are not
   failures.
3. **Budget (D-07, decision 6 accepted).** The draft budget is the manual
   amount when set, otherwise the account's normalized `availableBalanceUsd`,
   named by its source: wallet, available margin or withdrawable. A known
   zero is a budget of zero, not "Set budget". Only null is unknown. The
   check stays draft-only. Consequences: standard Hyperliquid accounts budget
   from withdrawable instead of spot USDC, and Lighter and RISEx drafts gain
   the budget check from their reported available margin. A known zero budget
   never produces a "raise leverage to fit" line. The owner declined a wider
   rule never to raise leverage to fit a budget, so a positive budget still
   gets today's leverage line.
4. **One risk denominator (D-12).** The Summary's risk share divides by the
   account `balanceUsd`, the same reference the size uses, labelled
   "Risk / <account> balance". An explicit manual budget is a labelled
   fallback; without either, the share is unknown. Capital-context ratios are
   labelled "Margin / <portfolio> balance" and "Exposure / <portfolio> balance".
5. **After a fill (D-11).** Once any entry has a linked or manual fill, Size
   and Leverage suggestions are hidden. Maximum-stop and minimum-target
   suggestions remain only for entries without fills, named by entry. Plan
   revisions through the editor are unchanged.
6. **Copy (D-24, D-25, F-16).** Statements beside Accept become estimates
   under their assumptions. "Cross margin liquidates further away" uses the
   existing `PositionEditor` wording about shared collateral. "The stop would
   never trigger" becomes "the estimated liquidation sits inside a stop".
   "The record loses money" and "never undoes more than half a win" are
   rephrased as the arithmetic they are. Each keeps PR #3's plays-or-positions
   branch. Lighter counts and names liquidation, deleverage and settlement
   omissions separately from fee-bearing ones ("1 liquidation omitted"); they
   still count as omitted executions. The over-budget toast uses the
   instrument's quote unit.

**Acceptance.** The D-04, D-07, D-11 and D-12 probe cases invert (see the
table below), and an accepted 1.25% risk shows as 1.25% in the Summary for the
same account.

**Not included:** a leverage-never-raised policy, changes to the Activity
fallback record or to the ladder, Overview changes.

## 0.2 Exit-time ordering

Covers D-05.

- A venue-linked Play's outcome close time is its latest valid linked exit
  fill time: the non-entry fills of a Play that passes
  `ExecutionIntegrity.Settled`, clamped to no later than the detection time,
  as the event store already does. Manual and retrospective Plays already
  store their real exit time and do not change. One helper replaces the three
  inline versions in Review, the event store and the calculator.
- `Play.EndedAtUtc` and the status change rows keep the detection time.
  Monitor watches and list ordering read it. Detection time stays available
  as audit data beside the outcome.
- **Ties.** Outcomes with the same exit time keep today's stable Play-ID order.
  No economic order is invented. The record discloses how many decided
  outcomes share an exit time with another, with a reason suffix such as "2
  results share an exit time; their order is not known". Activity fallback
  outcomes from PR #3 already use their closing fill time and are disclosed
  the same way.
- **Notice to the owner (decision 7).** Historical last-20 membership, limits
  and ladder levels can change on upgrade. The PR shows a before/after ladder
  on synthetic data: a W, L, L, L sequence found by one refresh is Full or
  Half depending on Play IDs before the change and always Half after it.
- **Recovery wins (decision 7: consecutive).** The ladder keeps requiring
  consecutive wins at a reduced level; a loss resets the win count. Only the
  text of `sizing-suggestions.md` changes, to "two consecutive winning plays".
  The code does not change.

**Not included:** a policy that orders genuine ties (decision 7) or any
threshold change.

## 0.3 Result-basis receipt

Covers the display part of D-01, plus F-09, I-04 and I-05. The scored basis
stays the venue result (decision 1).

- **Play-local execution result.** For a settled Play, `local gross =
  direction sign × (Σ exit quantity × price − Σ entry quantity × price)`, with
  exact products, and `local net = local gross − Σ trading fees of every
  linked fill`. The sum covers net-of-fee venues as well, so RISEx fees are
  charged once. The result is known only when:
  - the venue's quote asset is a listed nominal USD stablecoin on a linear
    contract;
  - every fill's fee is verified: a reported or Standard-account fee in a
    listed stablecoin, or a known zero;
  - the Play is settled, with no known missing executions.

  Otherwise it is null with a reason. It uses no funding, by construction, and
  is named "Play-local result", not total profit. For manual Plays the ledger
  result already is the Play-local result.
- **Where it shows.** The Execution panel totals and the Review Play rows
  show the venue net and the Play-local net with basis labels. A "sign
  differs by basis" flag appears where the two signs differ, and Review
  metrics count those cases. Nothing moves.
- **Sizing reasoning.** For a Play-sourced record: "N of the last 20 change
  sign under the Play-local basis", plus a separate count of results with no
  Play-local figure. This is omitted for the Activity fallback.
- **R.** "Avg. realized R" becomes "Estimated R (planned-stop model)" with its
  denominator ("n of N with a complete initial stop") and the formula in an
  `InfoTip`. Sizing's API R uses the same name in the contract.
- **Fee-only losses (I-04).** A count of decided results whose gross on the
  scored basis is ≥ 0 and whose net is < 0, shown in Review and in the sizing
  record. Win, loss and scratch classification is unchanged (decision 8).

**Not included:** switching the scored basis, withholding uncertain signs
from the ladder (decision 1), funding attribution, or shared-fill and flip
splits.

## 0.4 Monitor truth and visibility

Covers D-02, D-08, D-09, D-18, D-26 and F-02. It is staged:

**0.4a Visibility (no decision).**
- A persistent incident count from the existing `GET /api/monitor` summary
  appears on the Plays sidebar entry, on the Plays list row and in the Play
  header. It counts open records and unverifiable records that have not been
  dismissed.
- Overview gains a conditional incident row above the metric tiles, only when
  an incident is open.
- The Execution tab's heading follows the open incidents, not just the run
  state. "Monitored" never appears beside an open critical record.
- Human fact tables replace raw codes and JSON: open quantity, stop quantity,
  uncovered quantity (only when coverage is by orders), last observed and one
  next action. The JSON moves behind a disclosure. The discrepancy DTO gains
  its existing `LastObservedAtUtc`.

**0.4b Refresh continuity (decision 10 accepted).**
- Every import, Refresh included, checks continuity against the newest fill
  retained before that import. A capped read whose oldest raw fill is newer
  than that boundary sets the gap. A continuous read advances the continuity
  watermark.
- Refresh can set a gap but never clears one. No successful HTTP call alone
  clears a real gap.
- Backoff, staleness, due time and last success stay check-only.
- This replaces the settled rule in `play-monitor-plan.md:223` and the
  "Refresh versus check" table in `messaging-and-workers.md`. Both documents
  change with the code. Stored false gaps from before the change stay until a
  recovery rule exists.

**0.4c Coverage and evidence.**
- Per instrument and Play, the monitor persists its last coverage outcome with
  checked-at and a reason. This needs a new table and an explicit migration.
  The states are:
  - **known:** covered or not;
  - **shared:** another Play's whole-position stop covers it;
  - **unknown:** with a reason such as gap, omission, incomplete book or
    mechanics;
  - **unsupported:** the venue cannot show a current book;
  - **stale:** projected at read time.

  Structural unknowns never become missing and never degrade the run.
- The chart and level labels style a planned stop that has no confirmed
  resting order differently, using the evidence's own words ("not seen at the
  venue", "resting as of <time>").
- **RISEx (D-09, owner chose the label).** A venue capability says whether
  the order reader returns the current open book. RISEx says it does not.
  - Protection coverage on such a venue is **unsupported: current book not
    read**.
  - The run is no longer degraded for that reason alone.
  - Missing, short and cancelled stop checks do not run there, and the UI says
    so.
  - The `/v1/orders/open` read waits for its own slice, after its semantics
    and paging are verified.

**0.4d Documentation.**
- Once jcarcaboso PR #66 has merged, add the Refresh-continuity clause to the
  `docs/app-status.md` trigger table (D-26).
- Correct the gap wording in `messaging-and-workers.md`, which claims the gap
  clears.
- Update the monitor plan in step with 0.4b.

## 0.5 Managed stops

Covers D-03 and F-10.

**0.5a One numeric grammar (no decision).**
- Plan text is written as an unsigned plain decimal: `^\d+(\.\d+)?$`, at most
  64 characters, exact, with no whitespace, sign or exponent.
- The rule is strict on write and tolerant on read. A level whose text is
  unchanged from the stored plan still saves, and stored revisions are never
  rewritten.
- A negative percentage is rejected with a message pointing to a price stop.
- The browser never emits exponent text, and raw `Number()` reads of plan
  fields use the shared parser.
- Both runtimes share one fixture list.

**0.5b Stops at or beyond entry (decision 2 accepted).**
- After an entry has a linked or manual fill, its price stops may sit at or
  beyond that entry (break-even or profit) with a revision reason. Unfilled
  entries keep the wrong-side rule. Percent stops beyond entry are not
  expressible; use a price, and `0 %` stays break-even.
- The Summary's all-stops row is signed: "+5 locked in" or "−5 at risk". It is
  measured from the entry's average fill where one exists, otherwise from the
  planned entry, and says which.
- Initial risk and R stay frozen at the revision in force at the first entry
  fill. A stop that is not on the loss side never yields positive planned risk
  (`PlayOutcomeCalculator` uses absolute distance today).
- A linked stop or target can be rebound atomically to the observed order at
  its observed acting price, with a reason. The operation is version-checked
  against the Play and the observed price. It writes one plan revision and
  replaces the link in the same unit of work. This answers the monitor's
  `level.drift`.
- No migration is needed.

**Acceptance.** An Open long whose entry filled at 100 saves a stop at 105
with a reason and shows "+5 locked in". The R recorded at close still uses the
initial stop.

## 0.6 Journal opens without every image

Covers D-10.

- **Load.** A saved Play opens from the Play and evidence metadata alone,
  through the existing `GET /api/plays/{id}/evidence`. Originals load
  afterwards, each on its own, with a loading, unavailable (retry) or missing
  (404) state.
- **Display.** Thesis, plan, journal and review render while images load or
  fail. Size comes from metadata.
- **Save.**
  - An item that has not loaded or has failed stays in the draft with its
    server ID. Save never deletes it and never uploads it.
  - Notes and markup still save through their own requests.
  - Removing an unavailable image needs the same explicit Remove as any other.
- **Storage.** S3 stays fail-closed with no Local fallback. Reads stay
  authenticated and API-proxied, and nothing is stored in the browser across
  reloads.

**Acceptance.** One original returning 503 opens the Play, shows that image as
unavailable with Retry, and a Save afterwards keeps it on the server.

## Probe scenarios as tests

| Probe | Scenario | Test after the fix |
| --- | --- | --- |
| `verify-sol-frontend/f1f2.mjs` F2 | Entry 100; stops 50% at 95 and 50% unpriced → 25 units, Accept | No size line; the block names 50% unpriced stop coverage |
| `correctness-frontend/budget.mjs`, `f4.mjs` | Spot 3.12 or 250 is the budget; Lighter has no budget; `'0'` is unknown | Budget is `availableBalanceUsd` with its source; zero is known |
| `budget.mjs` | Size from 10,000 balance; Summary divides by 5,000 → 2.5% | Summary shows 1.25% of the same balance |
| `round3-dotnet/R3b.cs` | Accept size or leverage on an Open Play with a filled entry → revision 2 | No Size or Leverage suggestion after a fill |
| `correctness-dotnet/Program.cs` | W, L, L, L closed in one refresh → Full or Half by GUID | Always Half; ties disclosed |
| `round3-dotnet/R3.cs`, `correctness-dotnet/More.cs` | A 100 and B 110 exit 108, both +3; pre-existing hidden position | Play-local +8 and −2, sign differs on B |
| `verify-new-dotnet/NewVerify.cs` `Watermark` | 3,501 of 3,501 fills retained through Refresh, the next check reports a gap | No gap; a truly capped Refresh still records one |
| `verify-new-dotnet/NewVerify.cs` (Lighter) | A liquidation counted as fee-bearing | "1 liquidation omitted" |
| `verify-opus-dotnet/More4.cs` `StopRevision`, `f1f2.mjs` F1 | Open long 100 → stop 100 or 105 rejected; −5% saves as no stop | Stop 105 saves with a reason after a fill; −5% rejected |
| `verify-opus-dotnet/More4.cs` `Grammar` | `1e2`, `.5`, `+100` accepted by the server only | Rejected on write by both; legacy text still loads |
| `verify-sol-frontend-resolver/f6.mjs` | One original 503 → the Play cannot open | Opens; image unavailable with Retry; Save keeps it |

## Owner decisions (October 19, 2026)

| Question | Answer |
| --- | --- |
| Decision 2, managed stops (0.5b) | Yes. Gated per filled entry, measured from the average fill. |
| Decision 6, budget (0.1.3) | Yes. No rule never to raise leverage to fit a budget. |
| Decision 10, Refresh continuity (0.4b) | Yes. |
| Decision 7, recovery wins (0.2) | Consecutive, as the code does today. Ties are disclosed, not ordered. |
| RISEx current book (0.4c) | Label it now. |
| Sequencing | PR #3 and PR #6 merged first; PR #66 is being merged separately. |

## Delivery

Small, independently reviewable PRs against `sloppitty/vessel`, stacked only
where one depends on another:

1. This contract and the state document.
2. 0.6 journal images.
3. 0.4a monitor visibility.
4. 0.1 sizing truth.
5. 0.5a number format, then 0.5b managed stops stacked on it.
6. 0.4b Refresh continuity, then 0.4c coverage and the RISEx label, then
   0.4d documentation.
7. 0.2 exit-time ordering, then 0.3 result-basis receipt stacked on it.

Progress is recorded in
[phase0-trust-correctness-state.md](phase0-trust-correctness-state.md) as
each part is verified. The contracts named above (`sizing-suggestions.md`,
`plays-workspace-contract.md`, `play-lifecycle.md`, `play-monitor-plan.md`,
`messaging-and-workers.md`, `portfolio-review.md`) change only as far as the
owner's answers allow. `scripts/check.sh` runs before each PR with the pinned
SDK and pnpm, and PostgreSQL tests run against a disposable container.
