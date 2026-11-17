# Golden journal

Issue #88. One small dataset, worked out by hand here and asserted through Activity, Overview, Review, Sizing and Open risk.
Each screen already has its own tests with hand-computed values. This one loads the same facts into all of them, so a
disagreement between screens fails a test.

The figures below were computed from the fills, not copied from what the code prints. Where the code disagrees with them,
section 9 says which one is right and why, and the test for that figure is skipped with the open issue named.

Code:

- `backend/tests/Vessel.TestKit/GoldenJournal.cs` builds the dataset. Every name used here (a1, P2, SOL-L) is the name in that file.
- `backend/tests/Vessel.TestKit/GoldenJournalChecks.cs` holds the figures and identities of sections 2 to 7.
- `backend/tests/Vessel.UseCases.Tests/GoldenJournalTests.cs` runs them over in-memory stores, with no database.
- `backend/tests/Vessel.Integration.Tests/GoldenJournalPostgresTests.cs` stores the same dataset in PostgreSQL, runs the same
  checks through the real stores and compares the Plays list projection with Review (section 8).

When a figure here changes, change the matching line in `GoldenJournalChecks.cs` in the same commit.

## 1. The dataset

The clock is fixed at `Now` = 2026-11-10 12:00 UTC. `D(n)` is `Now` minus n whole days, at 12:00.

### Accounts

| Account | Venue | P&L basis | Portfolio | State | Snapshot at Now |
| --- | --- | --- | --- | --- | --- |
| Gross | hyperliquid | gross: closed P&L before fees, fees in USDC | Swing | enabled | value 1000, available margin 600; AVAX +10 (venue unrealized 200), ETH -2 (venue unrealized -2) |
| Net | risex | net of fee: each fill's closed P&L already has its own fee taken off | Unassigned | enabled | value 500, available margin 400; XRP -4 (venue unrealized -0.2) |
| Disabled | hyperliquid | gross | Swing | disabled | none |

The registry has descriptors for manual and Hyperliquid only, and a mark reader for Hyperliquid: AVAX 130, ETH 52. RISEx has
no descriptor here, so its open positions are valued from the snapshot.

### Fills

Quantity, price, closed P&L (`pnl`) and fee are as the venue reports them. All fees are USDC except a12.

| Fill | Account | Contract | Side, effect | Qty | Price | pnl | Fee | Time | Play, role |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| a1 | Gross | ETH | sell, open | 2 | 50 | 0 | 0.05 | D(16) | P2 entry |
| a2 | Gross | ETH | buy, close | 2 | 52 | -4 | 0.05 | D(15) | P2 exit |
| a3 | Gross | BTC | buy, open | 1 | 100 | 0 | 0.1 | D(13) | P1 entry |
| a4 | Gross | BTC | sell, close | 1 | 110 | 10 | 0.1 | D(12) | P1 exit |
| a5 | Gross | BTC | buy, open | 1 | 100 | 0 | 0.1 | D(11) - 1h | P3 entry |
| a6 | Gross | BTC | sell, close | 1 | 100.2 | 0.2 | 0.1 | D(11) | P3 exit |
| a7 | Gross | BTC | buy, open | 2 | 100 | 0 | 0.2 | D(10) - 1h | P4 entry |
| a8 | Gross | BTC | sell, close | 2 | 103 | 6 | 0.2 | D(10) | P4 exit |
| a9 | Gross | ETH | buy, open | 1 | 60 | 0 | 0 | D(8) - 1h | P6 entry; its order filled 2 |
| a10 | Gross | ETH | sell, close | 1 | 63 | 3 | 0 | D(8) | P6 exit |
| a11 | Gross | DOGE | buy, open | 100 | 1 | 0 | 0.1 | D(9) - 1h | none |
| a12 | Gross | DOGE | sell, close | 100 | 1.02 | 2 | 0.01 HYPE | D(9) | none |
| a13 | Gross | BTC | buy, open | 1 | 100 | 0 | 0.1 | D(5) - 1h | P5 entry |
| a14 | Gross | BTC | sell, close | 1 | 100 | 0 | 0.1 | D(5) | P5 exit |
| a15 | Gross | AVAX | buy, open | 10 | 100 | 0 | 0 | D(3) | none |
| a16 | Gross | AVAX | sell, close | 5 | 110 | 50 | 0 | D(2) | none |
| a17 | Gross | AVAX | buy, open | 5 | 120 | 0 | 0 | D(1) | none |
| a18 | Gross | ETH | sell, open | 2 | 51 | 0 | 0.05 | D(1) | P8 entry |
| b1 | Net | ETH | buy, open | 1 | 200 | -0.1 | 0.1 | D(6) | P7 entry |
| b2 | Net | ETH | sell, close | 1 | 190 | -10.1 | 0.1 | D(5) | P7 exit |
| b3 | Net | SOL | buy, open | 2 | 10 | -0.01 | 0.01 | D(9) | none |
| b4 | Net | SOL | sell, flip | 5 | 12 | 3.95 | 0.05 | D(8) | none |
| b5 | Net | SOL | buy, close | 3 | 11 | 2.97 | 0.03 | D(7) | none |
| b6 | Net | XRP | sell, open | 4 | 0.5 | -0.002 | 0.002 | D(1) | none |
| c1 | Disabled | BTC | buy, open | 1 | 1000 | 0 | 0 | D(4) | none |
| c2 | Disabled | BTC | sell, close | 1 | 2000 | 1000 | 0 | D(3) | none |

Every Play fill has its own filled order of the same size, except a9: its order reports a filled size of 2 while one
execution of 1 is retained. That is the order with a known missing execution.

### Plays

All plans have one entry, `e1`, with share 100. Closed Plays are detected flat one hour after their last exit, so a screen
that used the detection time instead of the exit time would fail.

| Play | Account | Contract | Status | Plan | Planned at |
| --- | --- | --- | --- | --- | --- |
| P1 | Gross | BTC | Closed | long, margin 100 at 1x, entry 100, stop 95 | D(14) |
| P2 | Gross | ETH | Closed | short, margin 100 at 1x, entry 50, stop 52.5 | D(17) |
| P3 | Gross | BTC | Closed | long, margin 100 at 1x, entry 100, stop 95 | D(12) |
| P4 | Gross | BTC | Closed | long, margin 200 at 1x, entry 100, stop 95 | D(11) |
| P5 | Gross | BTC | Closed | long, margin 100 at 1x, entry 100, stop 95 | D(6) |
| P6 | Gross | ETH | Closed | long, margin 120 at 1x, entry 60, stop 57 | D(9) |
| P7 | Net | ETH | Closed | long, margin 200 at 1x, entry 200, stop 190 | D(7) |
| P8 | Gross | ETH | Open | short, quantity 4 at 2x, entry 50, stop 55 (price) | D(2) |
| P9 | Gross | BTC | Planned | long, quantity 1 at 3x, entry 100, stops 90 (price, 50%) and 15% (percent, 50%) | D(1) |

What each item is there for:

- Two P&L bases: Gross (P1 to P6, X1, AVAX, P8) and Net (P7, SOL, XRP).
- A long with an exit followed by an add: AVAX. A short: P2, plus the SOL short after the flip and the open P8.
- An exact-zero scratch between two wins: P1 win, P3 scratch, P4 win. A loss made only of fees: P5.
- A fee in an unlisted token: X1 (the DOGE round trip a11, a12). A position flip: SOL.
- An order with a known missing execution: P6's entry order.
- An open position with a supplied mark: AVAX and P8's ETH. XRP has no mark and uses the snapshot.
- Two closes at the same instant: P5 and P7, both at D(5). One close exactly on the 7-day boundary: the SOL short at D(7).
- A large win on a disabled account: c1, c2, +1000. No screen may show it.

## 2. Activity

Activity walks each account and contract, opening an operation when the position leaves zero and closing it when it returns.
For an operation:

- `pnl` is the sum of the fills' closed P&L.
- Stablecoin fees are the sum of fees in listed stablecoins.
- Net is `pnl` minus stablecoin fees on gross fills. Net-of-fee fills already carry their fee, so they are not charged again.
  A gross fill with a nonzero fee in an unlisted token makes the net unknown.
- Gross (before fees) is `pnl` plus the stablecoin fees inside net-of-fee fills.
- At a flip, the closing part takes the whole fill's fee and P&L. The opening part takes the rest of the quantity at the
  same price, with no money.

### 2.1 Operations on the enabled accounts

| Operation | Status | Entered, exited, open | Avg entry, exit | pnl | Fees | Net | Gross | Play |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| P2 (a1, short) | closed | 2, 2, 0 | 50, 52 | -4 | 0.05 + 0.05 = 0.1 | -4 - 0.1 = -4.1 | -4 | P2 |
| P1 (a3, long) | closed | 1, 1, 0 | 100, 110 | 10 | 0.2 | 10 - 0.2 = 9.8 | 10 | P1 |
| P3 (a5, long) | closed | 1, 1, 0 | 100, 100.2 | 0.2 | 0.2 | 0.2 - 0.2 = 0 | 0.2 | P3 |
| P4 (a7, long) | closed | 2, 2, 0 | 100, 103 | 6 | 0.4 | 6 - 0.4 = 5.6 | 6 | P4 |
| P6 (a9, long) | closed | 1, 1, 0 | 60, 63 | 3 | 0 | 3 | 3 | P6 |
| X1 (a11, long) | closed | 100, 100, 0 | 1, 1.02 | 2 | 0.1, plus HYPE 0.01 | unknown | 2 | none |
| P5 (a13, long) | closed | 1, 1, 0 | 100, 100 | 0 | 0.2 | 0 - 0.2 = -0.2 | 0 | P5 |
| AVAX (a15, long) | open | 15, 5, 10 | 1600 / 15, 110 | 50 | 0 | 50 | 50 | none |
| P8 (a18, short) | open | 2, 0, 2 | 51, none | 0 | 0.05 | -0.05 | 0 | P8 |
| P7 (b1, long) | closed | 1, 1, 0 | 200, 190 | -0.1 - 10.1 = -10.2 | 0.2 | -10.2 | -10.2 + 0.2 = -10 | P7 |
| SOL-L (b3, long) | closed | 2, 2, 0 | 10, 12 | -0.01 + 3.95 = 3.94 | 0.01 + 0.05 = 0.06 | 3.94 | 3.94 + 0.06 = 4 | none |
| SOL-S (b4, short) | closed | 3, 3, 0 | 12, 11 | 0 + 2.97 = 2.97 | 0 + 0.03 = 0.03 | 2.97 | 2.97 + 0.03 = 3 | none |
| XRP (b6, short) | open | 4, 0, 4 | 0.5, none | -0.002 | 0.002 | -0.002 | -0.002 + 0.002 = 0 | none |

AVAX's average entry is 1600 / 15 = 106.666…, which Activity rounds half away from zero to 28 places:
106.6666666666666666666666666667.

The flip b4 sells 5 against a long of 2. The long closes with an exit of 2 at 12, so SOL-L closes at D(8). SOL-S opens at
the same instant with 5 - 2 = 3 at 12 and closes with b5 at D(7).

13 operations, from 24 retained fills (18 on Gross, 6 on Net). The disabled account's c1, c2 operation is not there.

### 2.2 Closed totals

Known nets of the 10 closed operations: -4.1 + 9.8 + 0 + 5.6 + 3 - 0.2 - 10.2 + 3.94 + 2.97 = 10.81. X1 has no known net.

By account: Gross -4.1 + 9.8 + 0 + 5.6 + 3 - 0.2 = 14.1. Net -10.2 + 3.94 + 2.97 = -3.29.

### 2.3 Unrealized P&L

Activity values an open operation at the venue's mark when the venue has one, and from the latest snapshot otherwise.

- P8: the mark is 52, the entry 51, and it is short: -(2 x (52 - 51)) = -2. The venue's snapshot figure is also -2.
- XRP: RISEx has no mark here, so the snapshot's -0.2 counts. The snapshot is newer than b6 and holds exactly -4 on XRP.
- AVAX: the right figure is 200. After a16 sells 5 at the average cost of 100, 5 remain at 100. a17 adds 5 at 120, so the
  remaining cost is (5 x 100 + 5 x 120) / 10 = 110, and 10 x (130 - 110) = 200. The venue's snapshot says 200 too. See
  section 9 for what the code shows.
- Closed operations show no unrealized figure.

The snapshot path has guards. With a single current snapshot and one market per contract, Activity anchors its walk to the
snapshot, so the quantities agree by construction. The guards matter when a fill carries no native market ID and the
snapshot or other fills name more than one market. `Snapshot_unrealized_counts_only_for_the_position_it_describes` pins
them with variants of XRP:

| Variant | Unrealized |
| --- | --- |
| As recorded | -0.2 |
| Snapshot taken before b6 (D(2)) | unknown |
| Two native markets (XRP-1 -4, XRP-2 -1), b6 without an ID | unknown: two positions match |
| b6 without an ID, XRP-1 at -3, and a closed round trip on XRP-2 | unknown: the quantity disagrees |
| Two native markets, b6 on XRP-1 | -0.2: only XRP-1's figure counts, never XRP-2's -7 |

## 3. Overview

All periods, scope All, grouped by portfolio. Only the two enabled accounts count.

- Balance: 1000 + 500 = 1500. Available: 600 + 400 = 1000.
- Open positions in the snapshots: AVAX, ETH and XRP, so 3.
- Unrealized from the snapshots: 200 - 2 - 0.2 = 197.8. Swing 198, Unassigned -0.2.
- Imported fills: 24.
- Rows, by balance: Swing (Gross) 1000, share 1000 / 1500. Unassigned (Net) 500.
- Realized, Unassigned row: -3.29 from 3 operations, nothing excluded.
- X1 has no known net. Overview excludes it and says "Closed operation net result or exit time unavailable." with a count of 1.

The right realized total is 7.81, with Swing at 11.1: 10.81 minus P6's +3, because P6's entry order misses an execution
and Review and Sizing leave it out by the shared rule. That gives 8 contributing operations and 2 excluded (X1 and P6).
Section 9 says what the code shows.

## 4. Review

Review scores a closed Play from its linked fills: net = sum of `pnl` minus stablecoin fees on gross fills. Return is net over
the entry notional (quantity x price of the entries), times 100. R is net over the planned risk.

Planned risk is the entry notional times the stop distance. The stop distance is the loss to the stop over the entry price,
signed by direction: a long loses below its entry, a short above it. Review counts R only when the plan in force at the
first entry has a complete initial stop: a long's stop below its entry, a short's above it.

| Play | Exit | Net | Notional | Return % | Stop distance | Risk | R |
| --- | --- | --- | --- | --- | --- | --- | --- |
| P2 short | D(15) | -4 - 0.1 = -4.1 | 2 x 50 = 100 | -4.1 | (52.5 - 50) / 50 = 0.05 | 5 | -4.1 / 5 = -0.82 |
| P1 long | D(12) | 9.8 | 100 | 9.8 | (100 - 95) / 100 = 0.05 | 5 | 1.96 |
| P3 long | D(11) | 0 | 100 | 0 | 0.05 | 5 | 0 |
| P4 long | D(10) | 5.6 | 2 x 100 = 200 | 2.8 | 0.05 | 10 | 0.56 |
| P5 long | D(5) | -0.2 | 100 | -0.2 | 0.05 | 5 | -0.04 |
| P7 long | D(5) | -10.2 | 200 | -5.1 | (200 - 190) / 200 = 0.05 | 10 | -1.02 |

P6 is excluded: its entry order filled 2 and one execution of 1 is retained, so its fills cannot settle it
(`IncompleteExecutions` = 1). There are 7 closed candidates, and one Open Play (P8).

### 4.1 Plays only

Positions excluded, all periods. Wins P1, P4. Losses P2, P5, P7. Scratch P3.

- Net: -4.1 + 9.8 + 0 + 5.6 - 0.2 - 10.2 = 0.9.
- Batting: 2 / 5 = 0.4.
- Average gain: (9.8 + 2.8) / 2 = 6.3 (percent).
- Average loss: (4.1 + 0.2 + 5.1) / 3 = 9.4 / 3 = 3.1333333333333333333333333333.
- Payoff: 6.3 / 3.1333333333333333333333333333 = 2.0106382978723404255319148936. Review rounds every division once, half away
  from zero, to 28 places.
- Profit factor: (9.8 + 5.6) / (4.1 + 0.2 + 10.2) = 15.4 / 14.5 = 1.0620689655172413793103448276.
- Average win: 15.4 / 2 = 7.7. Average loss in USD: 14.5 / 3 = 4.8333333333333333333333333333.
- Expectancy: 0.9 / 6 = 0.15.
- Fee-only losses: 1. P5's gross is 0 and its net is -0.2. P2's gross is -4 and P7's is -10, so neither counts.

Rows, newest first, with P5 before P7 because they share D(5) and keep the Play-ID order: P5, P7, P4, P3, P1, P2.

Groups:

- Direction long: P1, P3, P4, P5, P7, net 9.8 + 0 + 5.6 - 0.2 - 10.2 = 5, average R (1.96 + 0 + 0.56 - 0.04 - 1.02) / 5 = 1.46 / 5 = 0.292.
- Direction short: P2, net -4.1, average R -0.82.
- Portfolio Swing 11.1, Unassigned -10.2. Asset BTC 9.8 + 0 + 5.6 - 0.2 = 15.2, ETH -4.1 - 10.2 = -14.3.

#### 4.1.2 Average R

All six scored Plays have a complete initial stop, the short P2 and the scratch P3 included:
(-0.82 + 1.96 + 0 + 0.56 - 0.04 - 1.02) / 6 = 0.64 / 6 = 0.1066666666666666666666666667, over 6 Plays.

Before this dataset, nothing asserted `AverageR`. Flipping the short branch of the initial-risk check, or the sign of the
short stop distance, makes P2's R unknown and changes this figure. Removing `AverageR` leaves it null. All of these fail tests.

#### 4.1.3 Drawdown and streaks

In exit order: P2 L, P1 W, P3 S, P4 W, then P5 and P7 L L.

- Running net: -4.1, 5.7, 5.7, 11.3, 11.1, 0.9. The first drawdown is 0 - (-4.1) = 4.1. The peak 11.3 then falls to 0.9, a
  drawdown of 10.4. The maximum is 10.4.
- The scratch P3 does not break the run of wins, so the longest is 2 (P1, P4). If it did, the longest would be 1.
- The longest run of losses is 2 (P5, P7).

### 4.2 Plays and imported positions

Positions included, all periods. Positions are closed Activity operations no Play claims, scored by the same net and return
rules, with no R. Eligible: SOL-L (+3.94 on a notional of 2 x 10 = 20, return 19.7%) and SOL-S (+2.97 on 3 x 12 = 36, return
8.25%). X1 is omitted because its net is unknown. P6's operation belongs to a Play, so it is not a position.

A Play weighs 2 in the rate metrics and a position 1. Dollar totals, drawdown and streaks are not weighted.

- Count 8: 6 Plays and 2 positions. Wins 4, losses 3, scratch 1.
- Net: 0.9 + 3.94 + 2.97 = 7.81.
- Batting: win weight 2 + 2 + 1 + 1 = 6, decided weight 6 + 3 x 2 = 12, so 0.5.
- Average gain: (2 x 9.8 + 2 x 2.8 + 19.7 + 8.25) / 6 = 53.15 / 6 = 8.8583333333333333333333333333.
- Average loss: (2 x 4.1 + 2 x 0.2 + 2 x 5.1) / 6 = 18.8 / 6 = 3.1333333333333333333333333333.
- Payoff: 8.8583333333333333333333333333 / 3.1333333333333333333333333333 = 2.8271276595744680851063829787.
- Profit factor: (9.8 + 5.6 + 3.94 + 2.97) / 14.5 = 22.31 / 14.5 = 1.5386206896551724137931034483.
- Average win: (2 x 9.8 + 2 x 5.6 + 3.94 + 2.97) / 6 = 37.71 / 6 = 6.285. Average loss in USD: 2 x 14.5 / 6 = 4.8333333333333333333333333333.
- Expectancy: (2 x 0.9 + 3.94 + 2.97) / (2 x 6 + 2) = 8.71 / 14 = 0.6221428571428571428571428571.
- Average R stays 0.1066666666666666666666666667 over the 6 Plays.
- Running net in exit order: -4.1, 5.7, 5.7, 11.3, 15.24 (SOL-L, D(8)), 18.21 (SOL-S, D(7)), then 7.81 after P5 and P7.
  Maximum drawdown max(4.1, 18.21 - 7.81) = 10.4.
- Streaks: P1, P4, SOL-L, SOL-S win in a row across the scratch, so 4. Losses 2.
- Groups: Swing 11.1, Unassigned -10.2 + 3.94 + 2.97 = -3.29. BTC 15.2, SOL 6.91, ETH -14.3.
- Rolling periods: day none; week P5 and P7, -10.4 (SOL-S at exactly D(7) is outside Review's (from, now]); month, year and
  all 8 items, 7.81.

## 5. Sizing

Sizing builds its record from closed Plays when at least one has a win or loss, so the source is `plays`. P6 is not settled.
P3 is a closed scratch. The record holds 6 closed Plays and 5 decided ones, inside the 20-result window.

Sizing uses decimal arithmetic and shows 8 places.

- Wins 2, losses 3, scratches 1. Batting 2 / 5 = 0.4.
- Average gain 6.3. Average loss 9.4 / 3 = 3.13333333.
- Win/loss ratio 6.3 / 3.1333333333333333333333333333 = 2.0106383.
- Break-even reward/risk (1 - 0.4) / 0.4 = 1.5.
- Average win R (1.96 + 0.56) / 2 = 1.26. Average loss R (-0.82 - 0.04 - 1.02) / 3 = -1.88 / 3 = -0.62666667.
- Fee-only losses 1 (P5), unknown gross 0.
- Exposure: in exit order P2 L, P1 W, P4 W, then P5 and P7 L L. The loss streak is 2, below the 3 that halves size, so the
  level is full (multiplier 1). P5 and P7 share an exit time: 2 tied outcomes. The reason reads "Full size: no losing streak.
  2 results share an exit time; their order is not known."
- Limits: risk 1.25% x 1 = 1.25. Two wins are fewer than the 5 that use the average gain, so the maximum stop stays 10. Five
  decided are fewer than 10, so the minimum reward/risk stays 2.
- Recent rows, newest first: P5 (risk 5, R -0.04), P7 (10, -1.02), P4 (10, 0.56), P3 (5, 0), P1 (5, 1.96), P2 (5, -0.82).

## 6. Open risk

Open risk is the loss if every stop of each Planned and Open Play fills, at the plan's prices. Long and short are never netted.

- P8, short, Open. The plan sizes 4. a18 filled 2 at 51. The open part is 2 x (55 - 51) = 8. The unfilled part is
  (4 - 2) x (55 - 50) = 10. Risk 18. The monitor has not checked its stops, so its protection is `not-checked`.
- P9, long, Planned. The plan sizes 1. Its percent stop is a 15% return on margin at 3x, so the price is
  100 - 100 x 15 / (3 x 100) = 95. Risk 1 x (0.5 x (100 - 90) + 0.5 x (100 - 95)) = 5 + 2.5 = 7.5.
- Long 7.5 from 1 Play, short 18 from 1 Play, none unknown. Balance 1500 from 2 of 2 accounts. Protection: 0 not covered, 1
  unverified.

Flipping the short branch of the loss (stop minus reference) makes P8's risk 0 and fails the test.

## 7. Identities

These hold whatever the figures are. They run without a database in `Identities_hold_across_the_screens`, and again in the
database run.

7.1. Review with positions = Activity's closed known nets minus the operations Review leaves out that have a known net:
10.81 - 3 (P6) = 7.81. Also, Review's 8 scored items + 1 excluded Play + 1 omitted position = Activity's 10 closed operations.

7.2. Overview realized = Activity's closed known nets minus the operations with a known missing execution. In the scopes
without one (Unassigned, the Net account) both are -3.29. In the scopes holding P6 (All, Swing, Gross), see section 9.

7.3. Group totals sum to their total. Overview by portfolio and by account: realized, unrealized and balance. Review, with
and without positions: portfolios, accounts, assets, directions and origins.

7.4. The same rolling period selects the same operations on every screen. 30 days has no close on its boundary, and each
screen counts 10 closed operations: Activity's filter, Overview's contributing plus excluded, Review's scored plus excluded
Plays plus omitted positions. Over 7 days, P5 and P7 are inside on every screen and SOL-L (D(8)) on none. SOL-S closes
exactly on D(7); see section 9.

7.5. Sizing's record and Review's Plays count the same wins, losses, scratches and closed Plays, with the same batting
average, 0.4.

7.6. For a position that has had no exit, the mark path and the venue's snapshot agree: P8 is -2 both ways.

## 8. Database run and the Plays list

`The_golden_journal_reads_the_same_through_the_real_stores_and_the_plays_list` stores the dataset in PostgreSQL, builds the
journal search projection and runs sections 2 to 7 through `ActivityStore`, `WorkspaceStore`, `ReviewStore`, `SizingStore`
and `RiskStore`. The Plays list must then show Review's own outcome for each Play:

| Play | List state | Exit | Result | R |
| --- | --- | --- | --- | --- |
| P1 | scored | D(12) | 9.8 | 1.96 |
| P2 | scored | D(15) | -4.1 | -0.82 |
| P3 | scored | D(11) | 0 | 0 |
| P4 | scored | D(10) | 5.6 | 0.56 |
| P5 | scored | D(5) | -0.2 | -0.04 |
| P7 | scored | D(5) | -10.2 | -1.02 |
| P6 | executions | D(8) | none | none |
| P8, P9 | active | | | |

### 8.1 A later omission

A later refresh learns that P1's exit order left an execution out (`ExecutionsOmitted`) and changes nothing else about the
order. Review drops P1 at once (`IncompleteExecutions` becomes 2). The list must show P1 as `executions` with no result or R.

## 9. Where the code disagrees

Each case below has one test, skipped with its issue named. The test asserts the figure this document gives, so it passes
once the issue is fixed. Nothing else in the golden journal is skipped.

| Figure | Right | Code | Why the document is right | Test |
| --- | --- | --- | --- | --- |
| AVAX unrealized (2.3) | 200 | 233.333333333333333333333333333 | The code values the remaining 10 at the average of all 15 entered, 1600 / 15, including the 5 already sold at a cost of 100. 10 x (130 - 106.666…) = 233.33…. The venue's average-cost figure and Overview's snapshot both say 200. | `Avax_unrealized_uses_the_cost_of_what_is_still_open`, #90 |
| Overview realized, All (3, 7.2) | 7.81, 8 contributing, 2 excluded; Swing 11.1 | 10.81, 9 contributing, 1 excluded; Swing 14.1 | P6's order filled 2 with one execution of 1 retained. Review and Sizing leave it out by the shared `MissingExecutions` rule; Overview counts its +3. | `Overview_realized_equals_activity_closed_nets_minus_known_missing_executions`, #93 |
| 7-day selection (7.4) | one count on every screen | Activity 3, Overview 3, Review 2 | SOL-S closes exactly at Now - 7 days. Activity uses [from, ...), Overview [from, now], Review (from, now]. Which rule is right is #125's decision; the test only requires all screens to agree. | `The_seven_day_period_selects_the_same_operations_on_every_screen`, #125 |
| P1 list row after a later omission (8.1) | executions, no result, no R | scored, 9.8, 1.96 | The projection reacts to an order's status, size and TP/SL flag, not to `ExecutionsOmitted`, so the row keeps a result Review has withdrawn. | `A_later_omission_on_a_scored_plays_order_withdraws_its_list_result`, #92 |

When #93 is fixed, section 3's realized total and identity 7.2 for All also make Review with positions equal Overview
realized (7.81 both), since both leave out P6 and X1. When #125 is fixed, update section 4.2's week and section 7.4 to the
chosen rule.
