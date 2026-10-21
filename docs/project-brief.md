# Vessel project brief

Initial discovery, September 29, 2026. Based on the owner's description and the two sketches in [references](references).

Approved baseline, September 30, 2026: the owner selected Graphite, accepted configurable palettes as a future capability, and confirmed the final side-by-side workspace with a larger journal, a bounded scrolling entry editor and expanded entry details. See [approved decisions and implementation handoff](design/approved-baseline.md). That visual baseline does not itself select a production stack.

MVP alignment, September 30, 2026: the owner confirmed a self-hosted single-user start with token access, TypeScript, feature/module boundaries, Hyperliquid first then Lighter, manual venues, optional historical imports and in-page notifications. They prefer shadcn/ui as the component base and reusable workflow-led charts. Specific packages, authentication transport, instrument scope, matching and monitoring rules remain to be specified in [the architecture alignment](architecture/mvp-alignment.md). No implementation is authorized by these scope notes.

Foundation authorization, September 30, 2026: the owner confirmed perpetuals-first across venues and multiple active plays for the same account/instrument, then authorized scaffolding delegated to Sol agents. See [the bounded shared contract](architecture/scaffold-contract.md). The foundation does not authorize automatic fill assignment, spot integration or all later import/monitoring features.

Core development, October 1, 2026: the owner requested the main application shell without the Play page and authorized backend core, Hyperliquid and mounted database/migration work. The default app now has owner-scoped Overview, Portfolios, Accounts, Activity and Settings; the disposable prototype stays separate. Read [the current implementation and limits](architecture/core-workspace-state.md) before treating planned features as complete. The new main-shell design is an initial version, not a new approval of every navigation or screen detail.

Plays first pass, October 1, 2026: after merging PR #1, the owner authorized the main Plays page and components from the approved prototype, delegated to Sol agents. The chart is deferred until research settles its implementation. This step supports blank in-memory drafts, not saved Plays or execution matching. The owner's follow-up calls for venue-backed instruments where possible, green/up Long and red/down Short, integer leverage and closer prototype fidelity. See [the bounded page contract](architecture/plays-workspace-contract.md).

The owner initially clarified that a generated picture was enough. On September 29, 2026, they requested a browser-viewable prototype served on the LAN and authorized opening its port. The disposable implementation is described in [Prototype scope and LAN operation](prototype.md). This remains design discovery, not a production stack decision.

## Purpose

Core diary completion, October 13, 2026: the owner authorized the P1 must-haves
in the current product review. The bounded
[delivery contract](architecture/diary-completion-contract.md) covers explicit
manual executions, safe retrospective review, actionable data-quality
explanations, whole-position estimates and structured review. P2/P3 remain
proposals.

Paper-trading discovery and refinement, October 20, 2026: the owner requested
an opt-in hypothesis playground with paper-only portfolios and **Hyperliquid-only**
virtual accounts. Starting capital defaults to 4,000 nominal USD, is chosen
when adding the account and cannot change afterward; separate accounts support
different starting allocations. A touched entry/TP/SL level counts as fully
simulated at its defined price, with no fees or slippage. Paper balances and
statistics stay isolated from real accounts. The worker checks paper Plays
hourly by default, with a configurable interval to limit Hyperliquid load;
the proposed implementation reads the intervening candle window, not only the
latest price. See the
[paper-trading proposal](architecture/paper-trading-plan.md) for the code review,
confirmed product choices, recommended technical boundary and simulation
limitations. The owner subsequently authorized implementation, including the
ambiguous-touch flag, delegated Sol implementation and independent Sol
verification; follow the [delivery contract](architecture/paper-trading-delivery-contract.md)
and [verified state](architecture/paper-trading-state.md). No owner's database
migration, deployment or publication is authorized.

Make it easy to document trades, preserve the reasoning behind them, and discover repeated mistakes. The diary should help distinguish a poor decision from an unfavorable outcome, and a sound process from a lucky result.

Vessel is a diary and planning tool. Order execution, investment recommendations, and a promise that any strategy works are not part of the current request.

## What the owner has established

- Accounts belong to brokers or trading venues. Hyperliquid, Quantfury, Lighter, and other venues are examples, not committed integrations.
- Portfolios collect accounts by purpose, for example swing trading or intraday trading.
- Where a venue allows it, trade results could be imported. Manual recording must remain possible.
- Imports cannot replace the trader's feedback about adherence, thesis changes, and other context.
- Strategies are hypotheses to test, not rules assumed to be profitable. They need version history as the reasoning evolves.
- The central page describes a trading idea, its entries, status, results, and later feedback.
- Entries need their own stops and take-profit targets. The page should make sizing and reward-to-risk understandable.
- Chart images and annotations should remain available for later analysis.
- Each chart capture should have free-text context.
- The owner wants leverage from 1× to 100× represented in the prototype, with 1× meaning unlevered.
- Stops and targets need both percentage and absolute-price input.
- Multiple entries should be visible together with an average entry price, while retaining a selected-entry view.
- The bottom summary should show hypothetical profit at targets and loss at stops.
- Suggested sizing must be distinct from the trader's chosen position. The trader defines the entries first, then can apply or ignore a collapsible sizing assistant.
- Size belongs to the full position. Each entry owns a percentage of that position, rather than an independently entered size.
- Leverage needs a slider and a precise numeric input.
- An available budget should start from the selected account's value where possible and remain editable. Margin and leveraged position size should both be visible.
- Entries should have distinct, consistent edge colors in the chart and editor.
- Account and portfolio value should provide context for how much of the portfolio the position represents.
- The owner prefers the side-by-side layout and keeping thesis, strategy, and other notes in a separate area.
- Chart expansion should be a large in-page popup instead of requiring browser fullscreen.
- Account-derived budget is read-only by default, with a pencil for a deliberate manual override. If no balance is available, providing a budget is optional.
- Chart selection should work from plotted entries and the editor, with a dropdown listing each entry and Aggregate.
- The bottom summary should include leverage.
- The owner has locked the side-by-side layout for this design phase. Keep the sections in place while comparing ten sober, minimalist dark skins with slightly rounded corners, varied component treatments, and readable contrast.
- Graphite is now the selected default; retain configurable palette support for later themes.
- Selecting entries from the chart bar or the whole entry header must open the same corresponding details editor.
- Entry details need a zoomed editing dialog, while the main editor is height-limited and scrollable.
- The journal should fill the remaining left-column height so the workspace finishes evenly above the position summary.
- Imported history may be reviewed after the fact without requiring or generating an original thesis. Retrospective notes must remain distinguishable from pre-trade intent.
- Monitoring should observe linked entries/exits and relevant price levels, with execution confirmation kept separate from market-price observations.
- MVP notifications are page popups; future delivery may include Telegram or Discord.
- Discover the central page's layout, visual design, and contents before selecting the production stack or designing the rest of the product.

## Working terminology

Use **Play** provisionally for the whole idea. Reserve **Entry** for an individual tranche, **Plan** for intended actions, and **Fill** for actual execution. See [CONTEXT.md](../CONTEXT.md) for the glossary.

Calling the whole record an entry becomes confusing as soon as it contains two entry prices. Calling it a plan undersells execution and review. Neither argument makes Play a final product name.

## Proposed relationships, not a database design

```text
Portfolio -> accounts
Account -> venue
Account -> plays
Play -> entries -> stop and take-profit targets
Play -> optional strategy version -> strategy
Play -> thesis, evidence, execution history, result, review
```

The first visual concept assumes a play uses one account, one instrument, one direction, and at most one strategy version. These are simplifying assumptions, not settled constraints.

Historical intent should remain distinguishable from later edits and actual execution. Referencing strategy version 1 must not rewrite a past play when version 2 is created.

## The play workspace

### From the owner's sketch

1. Portfolio, account, instrument, and long/short selection at the top.
2. A prominent chart with entry, stop, and target levels.
3. Edit or add targets and stops, and split entries.
4. A fullscreen chart and time controls.
5. Capture the chart together with the trader's drawings.
6. Entry details, strategy, thesis, and notes near the chart.
7. A sizing helper and visible risk and reward-to-risk.

### First layout hypothesis

A wide chart sits beside a narrower entry editor. Account context stays above them. A collapsible sizing assistant appears before the chart. The bottom summary reports the entered position's modeled loss, modeled profit, reward-to-risk, margin, and average entry. Thesis, strategy, evidence, and review sit below the chart in tabs.

Side-by-side is now locked for desktop; the layout switch has been removed. The responsive stack remains shared by all themes. The capital context strip separates portfolio and account value from the position's committed-margin and notional-exposure percentages.

The original warm-light concept is historical. Graphite is the approved default. Carbon, Midnight, Slate, Forest, Petrol, Olive, Espresso, Aubergine and Stone remain visual-study references. Their registry supports later palette configuration without duplicating the workspace or its calculations.

The journal now fills the space beneath the chart. A bounded scrolling right editor ends level with it, followed immediately by the position summary. Entry details can be expanded into an in-page dialog that reuses the live form and preserves edits.

### Interaction goals

Some of these are now represented in the disposable prototype. See [the implementation boundaries](prototype.md#what-works) before treating a goal as implemented.

- Add a second entry without losing the first entry's context.
- Give each entry a different stop and one or more partial take-profit targets.
- Select an entry to highlight its levels on the chart.
- Change prices in the editor or drag the selected entry's chart handles.
- Allocate a chosen risk budget between entries, with explicit units.
- Recalculate size and modeled reward-to-risk as prices and allocations change.
- Preserve annotations in a chart capture and attach external chart screenshots.
- Write the thesis before execution and review afterward.
- Save and reload a local draft without implying a broker connection.

## Simplified prototype sizing

The disposable prototype models linear USD-quoted instruments using synthetic prices and account values. The current input contract follows the owner's request for a full-position size with percentage entries. It is not broker-accurate sizing or financial advice.

### Full position and entry shares

The trader chooses one total margin in USD or one total quantity in the selected instrument. Each entry receives a percentage of the total quantity. Shares must be finite, between 0% and 100%, and sum to 100%. This quantity-based interpretation of the owner's percentage request is explicit in the UI and remains reviewable.

Let `w_i` be each entry's quantity share expressed as a fraction, `E_i` its entry price, `S_i` its stop price, `M` the total margin input, and `L` the shared play leverage.

```text
Planned average entry = sum of w_i × E_i
Total quantity with margin input = M × L / planned average entry
Total quantity with quantity input = the trader's entered quantity
Entry quantity = total quantity × w_i
Position notional = sum of entry quantity × entry price
Required margin = position notional / L
Entry loss at stop = entry quantity × absolute(E_i - S_i)
Entry target profit = sum of entry quantity × target exit fraction
                      × direction-adjusted(target price - E_i)
```

The planned average can appear before size is entered because the shares define it. A zero-share entry contributes nothing to that average or to P&L. Its planned levels remain visible.

With margin input, changing leverage changes full-position quantity, notional and dollar outcomes while holding committed margin fixed. With quantity input, changing leverage changes required margin while holding quantity fixed. Switching input basis converts the current position rather than resetting it.

Editing a share redistributes the full position only after the shares total 100%. Adding or removing entries rebalances shares equally, without changing the selected full-position input. This replaces the previous independent per-entry sizing model.

### Available budget and account context

The available budget starts from the selected account's sample value and is read-only until the trader selects the pencil. Save or Enter commits the override; Cancel or Escape restores the current value. Account-linked mode follows account changes. A manual override stays fixed until Use account value is selected. Available budget is not automatically committed margin, and the sample account value is not verified free collateral.

When no account balance exists, budget may remain absent. The Manual journal fixture demonstrates this. Entered-position quantity, margin and P&L do not require an account value. Account and portfolio percentages remain unavailable rather than inventing zero equity.

The optional assistant needs a positive risk reference and available budget. It uses known account value when present; otherwise an explicitly supplied manual budget can serve as the risk reference and is labeled as such. Without either reference, the assistant explains the missing input while the entered position remains usable.

The prototype shows selected account value and the sum of the selected portfolio's account values, all in sample USD. A manual budget override changes neither reference value.

```text
Margin share of portfolio = committed margin / portfolio value × 100
Exposure share of portfolio = position notional / portfolio value × 100
```

The UI also shows the equivalent account percentages. Exposure can exceed 100% with leverage and is not capped. These distinct labels replace an ambiguous single “position percentage.” Live balances, cross-currency valuation, overlapping portfolio membership, and available collateral are not implemented.

### Suggested position

The assistant uses the same entry quantity shares and exits, so accepting a suggestion does not change the entry distribution.

```text
Desired loss budget = labeled risk reference × target risk percentage / 100
Loss per unit of total quantity = sum of w_i × absolute(E_i - S_i)
Suggested total quantity = desired loss budget / loss per unit
Suggested notional = suggested total quantity × planned average entry
Suggested leverage = smallest integer from 1 to 100 that makes
                     suggested notional / leverage fit available budget
```

Risk alone does not determine leverage. The lowest-fitting multiplier is a mechanical estimate, not a safe-leverage recommendation. Impossible budgets produce an explanation, not a clipped suggestion.

Use suggested position copies the full quantity and leverage only. The entry shares remain unchanged. Changing assistant settings, stops, or prices never reapplies the suggestion automatically. The bottom summary always uses the entered position, not the assistant's values.

### Stops, targets and validation

Stop and target percentages mean direction-relative price distances from entry, not leveraged returns. For a long, a 2% stop is below entry and a 6% target above; shorts reverse those directions. The other representation remains visible below the input.

Prices are the stored values. Switching input modes does not change them. Editing entry price in percentage mode preserves the percentages; editing entry price in price mode preserves the exit prices. Leverage does not silently move technical exit levels.

Target exit fractions must total 100% within each entry. Invalid assistant settings suppress the suggestion independently of an otherwise valid entered position. Invalid entry shares, prices, leverage or total size suppress affected position calculations rather than showing stale results.

Combined outcomes assume all assigned quantities and exit shares fill at the stated prices. The all-stops and all-targets values are separate scenarios, not a simulation of the route between them. Fees, funding, spread, slippage, gaps, contract multipliers, inverse contracts, lot rounding, maintenance margin, liquidation, and currency conversion are excluded. A high-leverage position may liquidate before its stop. The 1×–100× control is a prototype range, not a claim about venue support.

Do not average per-entry reward-to-risk ratios. Aggregate dollar profit and loss first. Do not label planned values as verified fills or realized returns.

## Lifecycle to explore

The prototype has manual Draft, Planned, Active, Closed, and Cancelled labels. It does not implement execution-driven transitions or a settled lifecycle. October 3, 2026: the owner settled the application lifecycle in [the Play lifecycle](architecture/play-lifecycle.md).

Questions remain around partially filled entries, partial exits, cancelled unfilled entries, moved stops, scaled positions, reopened ideas, and retrospective recording. A winning play can violate the strategy. A losing play can follow it correctly. Outcome and process review must remain separate.

## First-pass boundaries

The delivered browser prototype is disposable. It includes full-position sizing, quantity-share entries with independent exits, account and portfolio context, an optional sizing assistant, and basic leverage/margin calculations.

The chart supports entry colors, selected/aggregate views, drawings, captures with notes, and an expanded popup. Thesis, strategy and review remain in a separate journal area. Browser-local saving and JSON export preserve the play. See [prototype scope and operation](prototype.md) for the exact limits.

It does not connect wallets or brokers, retrieve prices, place orders, implement durable or shared storage, edit strategies, reconcile fills, or establish production authentication or architecture. Chart-level dragging, external screenshot uploads, and real strategy history are deferred. Serving the static prototype on the LAN is not a production deployment decision.

## Discovery questions

1. Does the chart deserve most of the workspace, or should entry editing have equal weight?
2. Is Play the right name for the record?
3. Perpetuals are confirmed first for all venues; instrument-specific constraints and later spot scope still need specifications.
4. The owner wants suggested and entered sizing separate. Should margin or quantity be the default full-position input? Margin remains the current default.
5. Can the same account belong to multiple portfolios? If so, how should balances and results avoid double counting?
6. Multiple active plays may share an account and instrument. Can one play itself span accounts or directions? That separate question remains open.
7. Does each entry own its exits, or can exits apply to the combined position?
8. Does “multiple stops” mean independently protected entries, partial stops for one entry, or a stop that changes over time?
9. The suggestion preserves its risk target when a stop changes. The entered position preserves the chosen full-position input until changed or explicitly replaced. Does this distinction feel clear?
10. Which pre-trade notes are required, and which would create too much friction?
11. Should chart captures be taken automatically at planning, execution, and review?
12. How should imported executions be matched to a play without overwriting the original plan?
13. What questions should later analysis answer first, such as strategy adherence, early exits, or repeated thesis invalidation?
14. Is entry-relative price movement the desired meaning of stop/target percentages, or should return-on-margin input be a separate option?
15. Is total quantity the intended basis for entry percentages? The current prototype uses quantity shares rather than notional or margin shares.
16. Should the available budget track equity, free collateral, or a separately reserved trading allocation once live account data exists?

## How to evaluate this iteration

Open the browser prototype and identify what draws attention first, whether the chart has enough space, whether the entry controls are easy to scan, and what information is missing or distracting.

Compare visual skins at `/designs.html` or with the Design dropdown. Use the same play across skins so palette and component choices can be judged without layout or data changes.

Try pencil budget editing, the unavailable-balance case, chart dropdown and plotted-entry selection, and the bottom leverage summary. Numeric fields move chart levels; dragging those levels is not implemented. Record design preferences before expanding scope.

## Sequence

1. Capture this context and preserve the sketches.
2. Inspect the initial LAN-served play workspace.
3. Mutate the layout, style, and interactions when useful.
4. Resolve the domain questions the prototype exposes.
5. Select the production stack.
6. Design accounts, portfolio views, strategy history, integrations, and analysis.
