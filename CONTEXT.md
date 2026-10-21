# Vessel

Vessel is a trading diary for recording decisions, executions, and reflection so a trader can study mistakes and improve their process.

## Language

**Portfolio**:
A collection of trading accounts grouped by purpose, such as swing trading or intraday trading.

**Paper portfolio**:
A portfolio containing only paper accounts, separate from portfolios containing real accounts.
_Avoid_: A real portfolio with a paper label

**Account**:
A trader's real account at a broker or trading venue, or its paper-trading counterpart. Real accounts can contain manually recorded or imported activity and need not belong to a portfolio.
_Avoid_: User account, portfolio

**Paper account**:
An account with virtual capital and simulated executions, associated with a venue for its market context rather than a real balance or order history.
_Avoid_: Testnet account, manual real account

**Starting paper capital**:
The fixed virtual capital chosen when creating a paper account, distinct from later simulated trading gains or losses. A different starting allocation belongs to a new paper account.
_Avoid_: Editable current balance, deposit

**Subaccount**:
A separately identified account under a venue's main account. Each selected Lighter account index is a separate Vessel account, even when several belong to the same wallet.
_Avoid_: Portfolio, wallet

**Source identity**:
The venue's identifier for one concrete account. A wallet address can identify an account at one venue and only help discover several accounts at another.
_Avoid_: Account name, owner identity

**Unassigned accounts**:
Account records without a portfolio, visible in the all-account view. This is a grouping state rather than another stored portfolio.
_Avoid_: Default portfolio as a separate entity

**Disabled account**:
An account retained for management but excluded from normal imported activity and venue-position views. Enabling it restores retained observations rather than recreating the account.

**Venue**:
The broker or exchange associated with a real account, or providing the market context for a paper account, such as Hyperliquid or Quantfury.
_Avoid_: Account

**Play**, provisional:
One trading idea and its history, from the original thesis and intended entries through execution, outcome, and review.
_Avoid_: Entry as a name for the whole idea; plan as a synonym once terminology is agreed

**Paper Play**:
A Play on a paper account that tests a trading hypothesis through simulated rather than real executions.
_Avoid_: A real Play with an optional paper tag

**Plan**:
The intended actions and risk for a play, before or during execution.
_Avoid_: Actual execution

**Play status**:
Where a play stands: Draft, Planned, Paused, Open, Closed or Cancelled. Partial entries and exits are execution progress within Open, not statuses. Review is a separate record.
_Avoid_: Partially in, Executed as a status

**Plan revision**:
An append-only snapshot of a planned play's plan, with the reason it changed. Planning records revision 1.
_Avoid_: Overwriting the original plan

**Order link**:
The association of one venue order with one level of a play: an entry, one of its stops or targets, or an unplanned exit. Imported fills reach a play through linked orders. Links are automatic when unambiguous, suggested otherwise, and remembered when the owner declines them.
_Avoid_: Assigning fills by account and instrument alone

**Execution progress**:
How much of a Play's entries and exits recorded executions have covered. Real Plays use imported or explicitly owner-recorded facts; paper Plays use simulated executions, which never confirm a real trade.

**Entry**:
One planned tranche of the play's full position, with a share of its total quantity, its own entry price, stop loss, and take-profit targets. An entry is not evidence that a trade occurred.
_Avoid_: Play, fill

**Fill**:
An actual execution of all or part of an entry or exit.
_Avoid_: Planned entry, simulated execution

**Strategy**:
A reusable, testable hypothesis describing conditions under which a trade may make sense. A strategy can be unproven or unsuccessful.

**Strategy version**:
A particular revision of a strategy's reasoning and rules. A historical play refers to the version used at the time.

**Thesis**:
The reasoning for an individual play, including what would invalidate it.
_Avoid_: Strategy when the reasoning applies only to this play

**Review**:
A trader's account of what happened, whether they followed the plan, what changed, and what they learned.
_Avoid_: Result as a substitute for reflection

**Evidence**:
Chart captures, annotations, accompanying free-text notes, and other material that records the context of a decision or review.

**Risk budget**:
The amount the trader chooses to allocate to planned loss at the stops for a play, not a guarantee of maximum actual loss.

**Risk reference**:
The known account value, or an explicitly supplied manual budget when account value is unavailable, used to interpret a target risk percentage.
_Avoid_: Committed margin, guaranteed loss limit

**Entry share**:
The percentage of the full position's quantity assigned to an entry. It is distinct from that entry's share of margin or loss at the stop.
_Avoid_: Risk allocation, margin share

**Planned reward-to-risk**:
The ratio of modeled profit at the planned targets to modeled loss at the planned stops.
_Avoid_: Win probability, realized return

**Suggested position**:
A proposal for the full position's size and leverage derived from entry shares, price levels, desired risk, and available budget.
_Avoid_: Entered position, fill

**Entered position**:
The full position's size and leverage chosen by the trader, with individual entry quantities derived from their shares. It describes intended exposure, not proof of execution.
_Avoid_: Fill, actual execution

**Committed margin**:
The capital assigned to the position's exposure, distinct from its notional value and its planned loss at the stops.
_Avoid_: Risk budget, position notional

**Available budget**:
An optional amount of capital the trader makes available for the play, separate from the amount they choose to commit.
_Avoid_: Committed margin, account value, risk budget

**Stablecoin wallet**:
Supported stablecoin holdings from the venue's wallet ledger, separate from its perpetual-margin snapshot.
_Avoid_: Complete account equity

**Wallet available amount**:
A token's total less its held amount. It is distinct from perpetual free margin and withdrawal capacity.
_Avoid_: Guaranteed tradable collateral, guaranteed withdrawable balance

**Leverage**:
The exposure multiplier relating the full position's notional value to its committed margin. One-times leverage is unlevered exposure.

**Level percentage**:
A stop or target expressed as the return on margin at the play's leverage: a loss for a stop, a gain for a target. The price moves the percentage divided by the leverage from the entry, so 20% at 10× is a 2% price move. Changing the leverage moves percentage levels.
_Avoid_: Price distance from entry, profit calculation

**Stop share**, **target share**:
The part of an entry's quantity that one of its stops or targets closes. An entry can have several stops and several targets.

**Average entry price**:
The planned entry price weighted by entry quantity shares, excluding zero-share entries. The shares define this price even before the trader chooses the full position's size.
_Avoid_: Arithmetic mean of entry prices

**Account value**:
The equity attributed to the selected account, separate from an editable play budget. A paper account's value is virtual, not real capital.
_Avoid_: Available budget, committed margin

**Portfolio value**:
The combined value of the accounts belonging to the selected portfolio, expressed in a common currency.

**Margin share of portfolio**:
The position's committed margin as a percentage of portfolio value.
_Avoid_: Notional exposure share

**Exposure share of portfolio**:
The position's notional exposure as a percentage of portfolio value. This is distinct from committed capital and can exceed 100%.
_Avoid_: Margin share of portfolio

**Journal owner**:
The person whose private portfolios, accounts, plays, strategies, evidence and notifications belong together.
_Avoid_: Broker account, venue credential

**Instrument**:
A specific tradable market or contract at a venue, with its own identifiers, units and trading constraints. Vessel starts with perpetual contracts for every integrated venue.
_Avoid_: Asset symbol alone when distinguishing contracts or venues

**Venue position**:
The exposure reported by a broker account for an instrument at a particular time. It is distinct from a planned position and from the fills that produced it.
_Avoid_: Play, planned entry, complete execution history

**Concurrent plays**:
Distinct active plays that can share the same broker account and instrument. Their intent and execution associations remain separate from a venue's net position.

**Imported execution**:
A venue-reported fill retained with its source identity and provenance, independently of any thesis or play association.
_Avoid_: Original trading intent

**Funding payment**:
A settled perpetual-contract cashflow reported by a venue for an account. A positive payment is received and a negative payment is paid; a published funding rate is not a payment.
_Avoid_: Trading fee, estimated funding

**Operation**:
A position round trip reconstructed from an account's executions on one native contract, not an inferred Play. Its result distinguishes execution P&L, trading fees and attributable settled funding.
_Avoid_: Original trading intent, retrospective Play

**Manual execution**:
An actual entry or exit recorded by the owner, with its source, time, price,
quantity and known or unknown costs. It is not a venue-confirmed import.
_Avoid_: Planned entry, simulated fill

**Simulated execution**:
A paper entry or exit recorded under an explicit simulation model using market observations. It is neither a venue-confirmed fill nor an owner-recorded actual trade.
_Avoid_: Imported execution, manual execution

**Execution correction**:
An owner-recorded amendment to a manual execution that retains the prior values
and the reason for changing them.
_Avoid_: Silently replacing history

**Retrospective Play**:
An owner-confirmed grouping of completed imported executions for later review.
It records what happened without claiming that a plan was documented beforehand.
_Avoid_: Original thesis, inferred initial risk

**Retrospective review**:
A trader's analysis added after an execution, which does not claim to be the reasoning documented before the trade.
_Avoid_: Original thesis, pre-trade plan

**Price-level observation**:
Evidence that a specified market-price source reached or crossed a planned entry or exit level. It is not confirmation that an order executed.
_Avoid_: Fill, confirmed TP or SL execution

**Play tracking**:
The monitoring of a play's linked executions and relevant price levels, with the source and certainty of each observation kept distinct.

**Notification**:
A message to the journal owner about a relevant execution, price observation or operational condition. Its delivery is separate from the underlying fact.
