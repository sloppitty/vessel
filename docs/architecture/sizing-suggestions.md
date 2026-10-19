# Sizing suggestions

October 13 diary completion adds eligible closed manual-ledger outcomes to the
same validated result calculation. Unknown execution costs do not become zero.
Retrospective Plays remain outside the sizing ladder; they have no initial plan
or risk estimate. The ladder thresholds, rolling window and existing
return-on-executed-notional arithmetic are unchanged. Review can still show
retrospective results separately. See
[the diary contract](diary-completion-contract.md).

October 5, 2026: the owner asked for position-size suggestions after Mark Minervini's risk management: a success rate from past plays, smaller size after repeated losses and back up only once success returns, a risk-based size, a suggested maximum stop, a minimum target and a leverage. Each is a suggestion the owner accepts with a button; nothing is applied automatically. This replaces the prototype's sizing assistant (removed from the app on October 4) and follows the [project brief](../project-brief.md#suggested-position): suggested and entered sizing stay separate.

These are mechanical estimates from the owner's own settings and record, not investment advice.

## Sources

Researched October 5 from Minervini's posts and summaries of *Trade Like a Stock Market Wizard* and *Think & Trade Like a Champion* (the books themselves were not read):

- Risk about 1.25% of total equity per trade on average, 2.5% at most. Position size follows from the stop: position = risk ÷ stop distance.
- An absolute maximum stop of 10%; average loss much smaller. Rule of thumb: cut losses at half the average gain; a loss larger than the average gain is the "cardinal sin". At least a 2:1 win/loss ratio.
- Batting average (share of winners) matters only together with average gain over average loss.
- After an abnormal losing streak, scale exposure down (his example: 5,000 → 2,000 → 1,000 shares) and pyramid back up only once a few trades work at the smaller size. Get off margin while struggling.

He gives no fixed loss count or recovery threshold; the ladder below is the owner's choice, not his rule.

## Owner decisions

- **Risk per trade**: 1.25% by default, editable per owner from 0.1% to 5%. Above 2.5% the editor warns.
- **Risk reference**: the play's account balance (`balanceUsd`: perps equity plus supported stablecoin wallet, or the wallet alone for unified accounts). Without a balance there is no size suggestion; the other suggestions still show.
- **Exposure ladder**: three consecutive losing plays step size down to 50%; two more consecutive losses step it to 25%. Two consecutive winning plays at a reduced level with a positive net result since stepping down step it back up one level; a loss resets the win count (owner decision 7, October 19, 2026).
- **Scope**: the record covers the owner's closed plays on all enabled accounts.

## Track record (backend)

A closed play's outcome comes from its linked fills (entry and exit roles), never from price touches or the plan:

```text
Net result      = Σ venue closed PnL − Σ fees paid in listed nominal USD stablecoins
Entry notional  = Σ entry fill quantity × fill price
Return          = net result ÷ entry notional × 100          (unlevered price-move equivalent)
Stop distance   = Σ_entries share × Σ_stops stopShare × |entry − stop|  ÷  Σ_entries share × entry
Planned risk    = entry notional × stop distance
R multiple      = net result ÷ planned risk        (Estimated R, planned-stop model)
```

Stop distance uses the plan revision in force at the first entry fill (the latest revision created at or before it, else revision 1, else the current plan), with percent stops resolved at that plan's leverage. Stop shares are normalized within each entry, so they need not total 100%. Entries or stops without a price are skipped; with none left, planned risk and R are null. Fees in another token are left out and the outcome says so (`feesComplete: false`).

A play with a net result above zero is a win, below zero a loss and exactly zero a scratch. Scratches count in totals but are neither wins nor losses and do not break streaks.

A play's close time is its latest valid linked exit fill (see [Exit-time ordering](#exit-time-ordering-october-19-2026)), not the time a check or refresh detected the closure. Statistics use the most recent 20 decided (win or loss) closed plays, ordered by close time:

- Batting average = wins ÷ decided plays.
- Average gain and average loss: mean return of wins and of losses (losses as positive numbers).
- Win/loss ratio = average gain ÷ average loss.
- Break-even reward-to-risk = (1 − batting average) ÷ batting average.
- Average R of wins and losses where planned risk is known.

The ladder walks every decided play from the oldest, so its level reflects the whole history. Plays with the same close time walk in Play-ID order, which is stable but not economic; the exposure reason discloses them.

### Execution eligibility correction on October 13

Before calculating the record and ladder, `SizingService` requires linked executions to settle the Play. Entry and exit quantities must match, roles must not contradict the normalized side/effect, chronology must not take exposure negative, and no fixed-size filled order may have known missing executions. `SizingStore` supplies those incomplete-order IDs to both sizing and Review. Cancellation or changing remaining size does not prove execution; position-sized TP/SL orders have no fixed completeness bound.

This changes which historical Closed records are eligible, not the arithmetic, window or exposure ladder. Previously inconsistent records stay Closed and may disappear from the scored record; no migration or automatic history repair occurs. Review still has stricter explicit-effect, fee and initial-risk requirements. Unknown effect alone is not a role contradiction for sizing.

## Limits (backend)

```text
Effective risk %   = risk % × exposure level (100%, 50% or 25%)
Max stop distance  = 10% of entry, or half the average gain when that is lower and there are at least 5 wins
Min reward-to-risk = 2, or the break-even ratio when that is higher and there are at least 10 decided plays
Leverage           = at most the venue maximum; no increase over the plan's leverage while exposure is reduced
```

Stop distance is the price move from entry, not the return on margin.

## API

`GET /api/sizing` (owner-scoped):

```json
{
  "settings": { "riskPercent": "1.25" },
  "exposure": { "level": "full|half|quarter", "multiplier": "1", "reason": "…", "lossStreak": 0, "winsSinceStepDown": 0, "tiedOutcomes": 0 },
  "record": {
    "closedPlays": 0, "decidedPlays": 0, "window": 20,
    "wins": 0, "losses": 0, "scratches": 0,
    "battingAverage": null, "averageGainPercent": null, "averageLossPercent": null,
    "winLossRatio": null, "breakEvenRewardRisk": null, "averageWinR": null, "averageLossR": null,
    "recent": [{ "playId": "…", "title": "…", "instrument": "BTC", "closedAtUtc": "…", "detectedAtUtc": "…|null", "netResultUsd": "…",
                 "returnPercent": "…", "riskUsd": null, "rMultiple": null, "outcome": "win|loss|scratch", "feesComplete": true }],
    "localSignChanges": 0, "localUnknown": 0, "feeOnlyLosses": 0, "unknownGrossLosses": 0
  },
  "limits": { "effectiveRiskPercent": "1.25", "maxStopPercent": "10", "minRewardRisk": "2",
              "maxStopSource": "default|averageGain", "minRewardRiskSource": "default|battingAverage" },
  "notice": "…"
}
```

Numbers are invariant decimal strings; unknowns are null. `PUT /api/sizing/settings` takes `{ "riskPercent": "1.5" }` and returns the same document; out-of-range or malformed values are 400.

## Suggestions (frontend)

Computed live from the draft, the account balance and `GET /api/sizing`, in the plays feature beside `sizing.ts`.

**Presentation** (revised October 5 at the owner's request): suggestions live in one yellow **Suggestions** block between capital context and the workspace, the place the approved baseline gives the sizing assistant. That keeps them apart from the entered plan and leaves room for the reasoning.
- **Collapsed**, it is one line: the number of suggestions, the exposure level when reduced, and what would change (e.g. `Size 2,891.21 USDC · Entry 1 stop 114 · Entry 1 target 141.5`).
- **Opened**, the left column lists each suggestion: the current value struck through, the suggested value, **Accept**, and a sentence of reasoning (the arithmetic behind the size, the rule and source behind a stop or target limit, why leverage should change). The right column shows the record (win rate, win/loss ratio, break-even R:R, exposure and its reason), the limits in force, the editable risk per trade and a collapsible "How these are worked out".
- **Fields** with a waiting suggestion only get a dashed yellow border and a tooltip pointing to the block, in the sidebar and in the expanded entry editor.

Accepting changes only that field; changing anything afterwards never reapplies a suggestion. An accepted leverage with percent levels opens the existing leverage dialog first.

- **Size**: `balance × effective risk % ÷ loss per unit` where loss per unit is `Σ share × Σ stopShare × |entry − stop|`. Accept sets the size in the current unit (margin at the current leverage, or quantity rounded down to venue precision). Shows the risk amount and the exposure level when reduced. Every priced entry needs a priced stop on the right side, otherwise the risk is unbounded and there is no size line. Hidden while the entered size is within 2% of it, and when the margin would exceed the budget the line says so and suggests a leverage instead.
- **Maximum stop**, for each stop: when the stop is further than the max stop distance from its entry, suggest the price at that distance. Accept sets the stop to that price (percent stops keep their unit).
- **Minimum target**, for each entry: when the entry's planned R:R is below the minimum, suggest the target price that reaches it for the entry's farthest target. Accept sets that target.
- **Leverage**: the lowest whole leverage that fits the suggested (or entered quantity's) margin in the budget and keeps the estimated liquidation beyond the farthest stop, capped at the venue maximum and, while exposure is reduced, at the current leverage. It shows only when the current leverage is a problem: the margin does not fit, the liquidation sits inside a stop, or exposure is reduced. A working higher leverage is left alone. Without a budget only the liquidation decides: when the estimate sits inside a stop, it suggests the highest lower leverage that clears the stops ("keeps liq. past the stops"). Accepting with percent levels goes through the existing leverage dialog.

Suggestions only apply to editable plays. Planned plays change through revisions as before.

## Implementation notes

- Backend: `Vessel.Domain/Sizing` (outcomes, record, ladder, limits, settings), `Vessel.Application/Sizing` (service and store port), `SizingStore` and the `SizingSettings` migration (`sizing_settings`, one row per owner, risk 0.1–5 with two decimals).
- Frontend: `api/sizing.ts`, `features/plays/useSizing.ts`, `suggestions.ts` (`planSuggestions` computes every suggestion once for the panel and the field markers) and `Suggestions.tsx` (the panel). The chart's level popover has no markers.
- Maximum-stop prices round toward the entry and minimum-target prices away from it (five significant figures), so accepting clears the line.

## Nominal stablecoin fees (October 14, 2026)

For the Lighter Robinhood venue (USDG settlement) the fee rule uses the single Application list `NominalStablecoins` (USDC, USDG, USDT, USDT0, USDE, USDe and USDH, case-sensitive as reported). A gross fill's fee in any listed stablecoin is deducted one-for-one; other fee tokens still leave the outcome's fees incomplete and are never converted. Plays on venues with different quote stablecoins pool into one track record at nominal 1:1, with no FX or depeg valuation, and the sizing notice says so. The arithmetic, ladder and window are unchanged.

## Activity fallback for the record (October 18, 2026)

The owner asked to use closed positions to calculate the statistics when nothing else gives a starting point. `SizingService` keeps the Play-based record (closed planned Plays and eligible manual-ledger outcomes, unchanged) whenever it has at least one decided (win or loss) outcome. Only when it has none does it read Activity operations (`ActivityService`, all enabled owner accounts) and score them instead; the sources are never mixed. Scratch-only Plays and retrospective Plays alone therefore still use the fallback.

An operation counts when it is `closed` with a known direction, opening and closing time, a known net result and a positive exact entry notional, outside an ambiguous native-identity partition. Incomplete, open and unknown-boundary operations are left out, as are operations whose gross fee is in an unlisted token (unknown net result). Balanced retained fills do not prove completeness: an operation with an execution of a retained filled fixed-size order whose retained executions (inside or outside the operation, matched by the shared `OrderKey` rule) fall short of its size is left out by `ExecutionIntegrity.MissingExecutions`, the same safeguard Play sizing applies to linked orders (`ISizingStore.OrdersAsync` reads the orders by key). Fills without a retained order record cannot prove a missing execution. A net result, entry notional or return percent outside decimal range or exact precision is unknown and the operation is left out, mirroring `PlayOutcomeCalculator`; the record never fails. Net result is the Activity rule: reported closed PnL less listed nominal stablecoin fees on gross fills only, so net-of-fee results are not charged their fee twice. Return = net result ÷ entry notional (Σ allocated entry quantity × fill price, exact) × 100, unlevered, as for a Play. The notional is the unrounded product (the Activity API's rounded weighted entry average is never used); a notional needing more than 28 decimal places is not exactly representable and the operation is left out, never rounded first. Order keys come from the operation's retained executions directly, without building its execution rows. Planned risk and R multiples are null: an operation has no plan and none is inferred. Operations count regardless of Play association, including operations already converted into retrospective Plays (retrospective Plays are never Play-source outcomes); none becomes a Play, a link or a retrospective review.

The same window (latest 20 decided by actual close time), win/loss/scratch rules, batting average, average gain and loss, win/loss ratio and break-even R:R apply. The exposure ladder also walks every decided fallback outcome: they are real results, and the thresholds and arithmetic are unchanged. Its reason ends with "Counted from closed Activity positions until a play closes with a win or loss." Limits follow from that record as usual.

`record.source` is `plays`, `activity` or `none` (neither has a decided outcome; the existing empty Play record is returned). Record counts describe the source's outcomes. `recent` rows of the fallback carry `playId: null`, `operationId`, `accountId`, title `<contract> Long|Short` and null `riskUsd`/`rMultiple`. The notice explains the fallback and keeps "not advice". The Suggestions record column shows a "From Activity" pill with "Based on N closed positions from Activity — no closed play with a win or loss yet." (scratch-only or retrospective closed Plays may exist) and an InfoTip; win-rate wording says positions. The first decided closed Play replaces the fallback entirely.

## Exact Play entry notional (October 18, 2026)

A Play's entry notional is the exact sum of its entry fills' quantity × price products, the same unrounded rule as the Activity fallback above; no product is rounded before the precision check. A notional beyond exact decimal precision (more than 28 decimal places) or range leaves the Play out of the record, never rounded into a different return. Planned risk keeps its documented estimate: the exact notional times the stop distance, a rounded ratio.

## Exit-time ordering (October 19, 2026)

Phase 0 item 0.2 ([contract](phase0-trust-correctness-contract.md#02-exit-time-ordering), defect D-05). A venue close used to be scored at the time a check or refresh detected it, so several closures found by one refresh shared one timestamp and the ladder followed random Play IDs: a W, L, L, L sequence found together came out Full or Half by GUID.

- **Close time.** A venue-linked Play's outcome closes at its latest valid linked exit: the latest non-entry fill of a Play whose linked fills settle it (`ExecutionIntegrity.Settled`), clamped to no later than the detection time, since a fill stamped after the check that saw it is no evidence of a later close. `PlayExitTime.LatestValidExit` computes it for both the outcome calculator and the `play.closed` event's source time. Fills that cannot settle the Play keep the detection time (sizing does not score them anyway). Manual ledgers keep their owner-entered exit and retrospective Plays their operation's closing fill, as before. Review keeps its own rule (the unvalidated latest exit, `PlayExitTime.LatestExit`, without the clamp, also for excluded Plays).
- **Detection time.** `Play.EndedAtUtc` and the status change rows still record the detection time; monitor watches and list ordering read it. Each Play-sourced `recent` row carries it as audit data in `detectedAtUtc`; it is null for manual ledgers and Activity fallback rows.
- **Ties.** Outcomes with the same exact close time keep the stable Play-ID order; no economic order is invented. `exposure.tiedOutcomes` counts the decided outcomes the ladder walks that share a close time with another, and when it is above zero the exposure reason gains, before any Activity suffix, "N results share an exit time; their order is not known." (or "N results share M exit times; …"). Activity fallback outcomes already close at their closing fill and are disclosed the same way.
- **Recovery wins** stay consecutive (owner decision 7): the ladder code is unchanged. Only wording changes: this document says "two consecutive winning plays" and the Suggestions "How these are worked out" list says "two wins in a row".

Thresholds, the 20-decided window, win/loss/scratch rules and the pooled ladder are unchanged. On upgrade, historical last-20 membership, limits and ladder levels can change because outcomes move from their detection time to their exit. On synthetic data, a W, L, L, L sequence found by one refresh was Full in 977 and Half in 1,023 of 2,000 random Play-ID sets before the change, and Half in all 2,000 after it.

## Result bases (October 19, 2026)

Phase 0 item 0.3 ([contract](phase0-trust-correctness-contract.md#03-result-basis-receipt)).
The record, ladder, limits, window and win/loss/scratch rules keep scoring the
venue result above (owner decisions 1 and 8). The record now reports how its
window looks under the Play-local basis described in
[Review's result bases](portfolio-review.md#result-bases-october-19-2026):
each Play's own fills, sell proceeds minus buy costs (direction × (exits −
entries)), less every trading fee.

- **Counts.** Over the decided results of the window (the latest 20 by close
  time, chosen as the record chooses them), `record.localSignChanges` counts
  results whose Play-local result differs in sign (negative, zero, positive)
  from the scored venue result, and `record.localUnknown` those with no
  Play-local result. Both are null for the Activity fallback: positions are
  not Plays. `record.feeOnlyLosses` counts window losses whose scored result
  before trading fees is zero or better (`PlayOutcome.GrossResult`, exact so a
  value beyond decimal precision keeps its sign: Σ venue
  closed P&L plus listed stablecoin fees already inside net-of-fee P&L; for the
  fallback, the operation's before-fee P&L), and `record.unknownGrossLosses`
  the losses where that figure is unknown. All four are init fields, so the
  positional records the fallback builds are unchanged.
- **Suggestions.** The record column adds one short line, e.g. "1 of the last
  11 changes sign under the Play-local basis. 2 have no Play-local figure. 1
  loss came from fees alone.", with an InfoTip. Each part appears only when its
  count is above zero: a zero count adds nothing, and with every count zero (or
  absent from an older server) there is no line at all, following the
  helper-text reduction of the visual refresh. "None change sign" is never
  stated. The sign and unknown parts are omitted for the Activity fallback.
- **Funding.** Whether venue-reported P&L includes funding is unverified for
  every venue ([venue funding](venue-funding.md)), so a sign change or a
  fee-only loss can reflect funding; the InfoTip says so.
- **R.** Sizing's API `rMultiple`, `averageWinR` and `averageLossR` are the
  estimated R (planned-stop model): net result ÷ (entry notional × stop
  distance) from the plan in force at the first entry, with the fallbacks
  above for partial plans. Review's figure has the same name but a stricter
  plan rule. Neither is actual risk, and sizing's R is not shown in the
  browser.
