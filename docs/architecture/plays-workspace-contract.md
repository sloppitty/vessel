# Plays workspace first pass

October 13 diary completion extends the saved workspace under
[its delivery contract](diary-completion-contract.md). Manual execution entry
and correction history use the existing Execution tab. Optional adherence,
mistake tag, lesson and next action use the existing Review tab. Pending and
reviewed filters follow saved state. Cross-screen open requests preserve
unsaved notes, plans, evidence and manual forms until the owner discards them.

The existing bottom Summary now includes all-stops loss, all-targets gain,
aggregate R:R and a named risk-reference share. Entry allocation, priced
quantity and stop/target coverage are separate. Partial plans show known
subtotals, not full-plan totals. Fees, funding and slippage are excluded, and
actual loss can exceed a planned stop estimate. No section is moved.

The chart placeholder is superseded for venue instruments by [the chart plan](chart-plan.md).

October 1, 2026. The owner merged PR #1 and authorized the Plays page, delegated to Sol agents. This step builds the page and components from the approved prototype, with the chart deferred until its renderer and data requirements are researched.

## Scope

- Enable Plays in the existing application shell, including `#plays`.
- Start with an empty local draft, not the old BTC sample fixture.
- Preserve Graphite and the approved capital-above, chart-left, journal-below-chart, position-right and summary-below layout.
- Keep the position sidebar bounded and internally scrollable. Use the same responsive stack and a shared expanded entry form with Escape and focus restoration.
- Edit title, account, venue-backed perpetual instrument where supported or an explicit manual label, direction, whole-position margin or quantity, integer leverage, quantity-share entries, planned stops, partial targets and separate journal notes.
- Use existing API account and portfolio context without mutating accounts or refreshing account balances/fills. Exclude disabled accounts from draft selection.
- Keep wallet availability, primary perpetual equity and portfolio coverage distinct. Wallet availability is not a trading budget or verified collateral. Automatic budget derivation remains unspecified, so budget is unavailable unless the trader explicitly overrides it.
- Keep the chart as an explicit placeholder. Planned-entry selection can still synchronize with the editor, but no candles, prices, fills or execution observations are generated.

## Limits

Drafts exist in React memory only. Navigation within the shell retains the draft; reload or disconnect discards it. Explain this on the page. Do not offer a Play save, publish, execution or lifecycle action. Saving a budget override changes only the local draft.

Payoff calculations remain deferred: no reward-to-risk, stop loss or target profit. Since October 3 the owner asked for position sizing, so margin, position size (notional) and quantity are derived from the chosen size, leverage and planned average entry (see below). Instrument labels do not establish validated quote/base units; the editor labels prices as quote units and sizing as currency or instrument units. Account context and an explicit budget override use nominal USD separately. Price and percentage inputs are distinct local representations; a percentage is the return on margin at the play's leverage (see the October 3 refinements below). Switching units clears the value rather than inventing a conversion.

No Play API, database migration, strategy-version editor, media upload, import allocation, monitoring, notification or chart dependency is added. Existing imported executions remain unassigned. The UI does not enforce a unique account/instrument combination or claim that a local draft is a saved Play.

## Initial first-pass verification

- Frontend type checks, lint, production build and all 131 tests pass. Coverage includes blank drafts, disabled accounts, account-specific budget editing, navigation retention, level-unit changes, partial targets, selection and expanded-dialog state.
- The independent review reproduced repeated legend selection failing to reveal the same entry. A regression test now covers the fixed selection-request path.
- The existing prototype passes all 76 tests. Locked .NET restore and Release build pass; backend tests report 312 passed and 40 PostgreSQL cases skipped because no test database connection was supplied. This frontend-only step does not claim new database verification.
- T3 browser checks use built assets, a synthetic API fixture and fake connection text. They do not contact a venue, real API or database.
- Desktop geometry at 1402 × 877 keeps the chart and journal left, position right and both column bottoms aligned. The sidebar scrolls internally; selection does not move the outer page. Dialog edits survive Escape with focus restored and no duplicate input IDs.
- Responsive browser frames at 1200, 1100, 1001, 1000, 900, 850, 800, 390 and 320 CSS pixels show no horizontal overflow. At 390 and 320 pixels, the shared stack keeps a large journal and bounded editor. The 320-pixel expanded dialog fits the frame and scrolls internally.
- Rendered text/control samples use the Graphite tokens and exceed 4.5:1 text contrast. This is not a full accessibility audit.

The T3 viewport-resize command timed out, so responsive checks used same-origin browser frames with explicit CSS widths rather than device emulation. The existing LAN service, user database, private environment and firewall remain unchanged. No production deployment or remote CI run is claimed.

## Delegation

- Position worker owns `PositionEditor.tsx`, `EntryForm.tsx` and `PositionEditor.test.tsx`.
- Panels worker owns `WorkspacePanels.tsx`, `WorkspacePanels.test.tsx` and `plays-workspace.css`.
- Orchestrator owns `draft.ts`, `PlayWorkspace.tsx`, shell integration, workspace tests, documentation and verification.

Workers share the local draft types in `frontend/src/features/plays/draft.ts`. No worker commits independently.

## Owner refinements

October 1, 2026. The owner requested venue-backed instruments where possible, a green Long/up-arrow and red Short/down-arrow toggle, whole-number leverage and closer fidelity to the approved prototype. This supersedes the initial manual-only instrument control, not the deferred chart or persistence scope.

The existing shell navigation remains. Within Plays, use the prototype's editable title heading, compact portfolio/account/instrument/direction context, capital strip, collapsed assistant, position controls and entry-level switches. Do not mount prototype samples, enable unsupported lifecycle actions or move the locked workspace sections.

Shared authenticated API contract: `GET /api/accounts/{id}/instruments` returns:

```json
{
  "venueId": "hyperliquid",
  "marketScope": "perpetuals",
  "scope": "venue-perpetuals",
  "instruments": [
    { "contractId": "BTC", "quantityDecimals": 5, "maxLeverage": 40, "quoteAsset": "USDC",
      "priceStep": null, "category": null, "venueContractId": null, "maintenanceMarginFraction": 0.0125 }
  ],
  "notice": "Venue perpetual contract metadata only. No orders, balances or execution refresh."
}
```

The numbers above illustrate the shape, not current venue limits. The scope was `primary-perpetual-dex` until October 7, 2026; it now names any venue's selectable perpetuals (see [venue-instruments.md](venue-instruments.md) for the instrument fields). Manual accounts return the same shape with `scope: "manual"`, their venue ID, an empty instrument list and a manual-catalogue notice. Missing/foreign accounts return 404; disabled accounts return 409 without a venue read; safe venue failures return 502. The application owns the contract and reuses the existing adapter's fixed `/info` request, cancellation, deadline and response bounds. No new persistence, account refresh or source credentials.

Hyperliquid choices come from a metadata-only read of the primary perpetual DEX. Preserve exact contract IDs and exclude delisted contracts from selectable choices without changing historical-fill recognition. The UI loads when an enabled Hyperliquid account is selected, cancels stale reads, offers retry, and clearly labels an explicit manual fallback when unavailable. Account changes clear the prior instrument and local budget rather than carry a venue contract into another account.

The displayed catalogue does not validate a complete order or position, price/base/quote units, margin tiers or trading eligibility. Keep sizing/payoff calculations deferred. Leverage controls use whole multipliers. With a venue contract the range is that contract's catalogue maximum; choosing a contract with a lower maximum lowers the plan's leverage to it, and a saved plan above it is flagged. Without one, the range is 1× to 100×. Size is never resized from metadata.

Follow-up worker scopes:

- Backend worker owns backend implementation/tests only.
- UI worker owns `DirectionToggle.tsx`, its tests, `PositionEditor.tsx`, `EntryForm.tsx`, `PositionEditor.test.tsx` and `plays-workspace.css`.
- Orchestrator owns instrument API/client and picker, `PlayWorkspace.tsx`, journal/capital component composition, shell integration, documentation and full verification.

### Refinement verification

- `pnpm check` passes all 164 frontend tests, 387 backend tests and 76 unchanged prototype tests, types, lint, locked restores and both builds. Forty PostgreSQL cases explicitly skip without a test connection; no new database-verification claim.
- The metadata route has provider/service/API coverage for authentication, ownership, disabled/manual accounts, exact IDs, delisted choices versus retained historical fills, bounded bodies/depth/deadlines, cancellation and no refresh/write side effects.
- A single public `/info` metadata request supplied no account address. Its saved response had 234 universe entries; an offline smoke through the compiled adapter returned 178 non-delisted choices with exact IDs/decimals/leverage. These are observation-time counts, not hard-coded product options or guaranteed availability.
- Client tests cover loading, safe errors/retry, explicit manual provenance, wrong-venue data, cancellation and stale results, account/instrument/budget clearing, and portfolio-filter changes. Independent review found an obsolete hidden portfolio filter reappearing after a metadata move; guarded state normalization fixes it, with a regression test.
- T3 comparison used the actual approved Graphite prototype and built application, not another design mockup. The desktop workspace is 940 pixels tall with roughly 64/36 chart/journal heights, aligned column bottoms and an internally scrolling entry sidebar. Existing application navigation remains.
- Native browser interaction confirmed green/up Long and red/down Short, pressed states, decimal leverage input normalizing to an integer, and arrow-key slider changes of one whole multiplier. It also covered catalogue loading, failure, manual fallback and retry, using fixture accounts and the saved public catalogue rather than a real wallet session.
- Responsive browser frames at 1200, 1001, 900, 850, 800, 390 and 320 CSS pixels showed no horizontal overflow with the catalogue loaded. The 320-pixel expanded form fits, uses unique IDs, and restores trigger focus on native Escape.

Chart rendering/research, payoff rules and Play persistence remain deferred. No dependency, migration, user-record, private-environment, firewall or existing LAN-service changes. No push, PR, deployment or remote CI is claimed.

## Navigation and visual polish

October 1, 2026. The owner requested icon-only desktop navigation and further prototype-led visual refinement.

- Desktop navigation toggles between the existing expanded widths and a 78-pixel icon rail. State lasts through shell navigation without browser storage. Labels remain accessible; icons have native tooltips and active-page markers.
- At 800 pixels and below, the full 236-pixel mobile drawer overrides desktop collapse. Its focus, inert background and Escape behavior remain unchanged.
- Graphite section positions and shared responsive stack remain fixed. The recovered canvas at the 1402-pixel browser width gives a chart/journal column of about 895 pixels and a position editor of about 340 pixels, closely matching the prototype.
- Controls have grouped direction tracks, colored selection indicators, clearer entry selection, native slider progress/thumb styling, softer surfaces and more readable journal text. A restrained grid and icon identify the chart placeholder without fabricating data. Draft wording distinguishes unsent edits from permitted metadata reads.
- `pnpm check` passes 169 frontend, 387 backend and 76 prototype cases; 40 PostgreSQL cases explicitly skip without a test connection. The final phone-layout adjustment also passed types, lint, frontend tests and build.
- Native T3 checks verify 78-pixel desktop collapse, aligned 940-pixel columns, icon-link names/tooltips, and text contrast above 4.5:1. At 320 pixels, the drawer remains 236 pixels, desktop collapse is hidden, no page overflow occurs, and the bounded editor and expanded dialog remain usable. Native Escape restores focus for both drawer and dialog.

T3 snapshot and viewport-resize calls timed out during this pass. Geometry/control checks used the native browser and a same-origin 320-pixel frame; a T3 recording was captured. No new screenshot, device emulation or full accessibility audit is claimed. No backend, dependency, user-record, private-environment, firewall or existing LAN-service changes in this styling pass.

## PR review polish

October 1, 2026. Review of PR #2 against the approved Graphite prototype, using a fixture API and headless Chromium screenshots at 1402, 1200, 1001, 900, 800, 390 and 320 CSS pixels.

- Journal tabs share the heading row, as in the prototype, and the repeated per-tab label is screen-reader only. At 1402 pixels the note field grows from about 124 to 204 pixels without moving any locked section.
- The position editor restores the prototype's "Distribute your entries" heading with entry count, the plain sum of entered quantity shares and an explicit Split equally action (two decimals, remainder on the last entry). Adding an entry still leaves its share blank. The share total is bookkeeping, not position validation.
- Leverage gains the prototype's 1×, 5×, 10× and 25× presets. They set the same whole-number draft value as the slider and input.
- "Expand selected entry" now uses the same 10-pixel button text as its siblings. `DialogTrigger asChild` replaces the button's `data-slot`, so the shared rule did not apply.
- Number spinners are hidden in Plays fields so short values such as a 100% target share fit at 320 pixels. Direction spans two context columns between 851 and 1050 pixels instead of crossing the divider.
- The application has an inline favicon, removing the only console error.

`pnpm check` passes 171 frontend, 387 backend and 76 prototype cases; 40 PostgreSQL cases still skip without a test connection. No page-level horizontal overflow at the widths above; columns stay aligned at 940 pixels; Escape restores focus from the expanded editor. T3 preview automation timed out again, so screenshots used a local headless Chromium against a synthetic API.

### Code review and second polish pass

- Backend review found no defects: authentication precedes venue reads; owner, disabled and manual guards hold; delisted contracts are excluded only from choices; provider failures map to a generic 502 without touching sync state; caller cancellation is not a provider failure.
- Frontend fix: when a successful reload disables or deletes the draft's account, the draft clears account, instrument and budget like an explicit account change. Previously a venue contract stayed in the draft and was shown as a manual label. Loading and failed reloads keep the last known list, so a transient error does not clear the draft. A regression test covers both disable and removal.
- The summary gains the prototype's intro column: entry count, Long/Short in direction colour, instrument and leverage. "Not calculated", "Not chosen" and "Unavailable" values are muted so real figures carry the weight. Entry headers show the planned entry price once entered. Context status reads "Draft" instead of repeating the heading's "Local draft". The shell footer uses singular/plural account wording.
- `pnpm check` passes 172 frontend, 387 backend and 76 prototype cases; 40 PostgreSQL cases skip without a test connection. No page or element overflow at 1402–320 pixels; columns remain aligned at 940 pixels.

## October 3 refinements

After PRs #5 and #6 merged, the owner asked for several stop losses, percentages that follow the leverage, more chart tools, venue leverage limits and instrument search.

- **Several stops**: each entry has a list of stops like its list of targets. Each stop and target has its own unit and a share of the entry it closes. A new entry starts with one blank stop and one blank target at 100%. The `MultipleStops` migration turns each stored single stop (plans and plan revisions) into a one-item list at 100% and names the stop on existing stop order links.
- **Percentages**: a stop or target in % is the return on margin at the play's leverage. The price moves % ÷ leverage from the entry, on the side the direction gives: at 10×, a 20% stop on a 2711.3 long sits at 2657.1. The editor shows that price and the price move under each % field. Changing leverage moves % levels; the order matcher resolves them the same way. Stored values are not converted: existing % levels are now read at the plan's leverage.
- **Instruments**: the catalogue reports a quote asset per contract (`quoteAsset`, USDC for Hyperliquid primary perps). Venue contracts show as pairs (BTC/USDC) in the picker, the fixed instrument field, the venue link and the plays list; the stored contract ID is unchanged. The picker is a searchable combobox: type to filter (exact, then prefix, then contains, keeping the venue's order), arrow keys and Enter to choose, Escape to keep the current choice.
- **Leverage changes with % levels**: with any stop or target in %, the leverage controls preview a new value and apply it only after a dialog. It lists each % level's price now, with prices kept (the % is rescaled by new ÷ old leverage) and with the % kept (the level moves), plus how the size changes. The owner keeps prices, keeps % or cancels. Without % levels, leverage applies at once. Clearing the number field and leaving it keeps the current leverage.
- **Position size**: margin sizing fixes the margin, so notional = margin × leverage and quantity = notional ÷ planned average entry. Quantity sizing fixes the quantity, so notional = quantity × planned average entry and margin = notional ÷ leverage. Figures are before fees, funding and venue margin rules, in the contract's quote asset and base (generic units for manual instruments), with the venue's quantity decimals. They show under the size input and in the full-position summary, which says what is missing (a size or an entry price) instead of a figure.
- **Size unit switch**: a USDC | BTC switch (generic Currency | Quantity without a venue contract) replaces the sizing select. Switching converts the size at the current leverage and planned average entry (quantity rounded to the venue's quantity decimals) and clears it only when there is no entry price to convert at.

## October 4 refinements

The owner asked for a minimal, clean page with more room for entries.

- **Position panel** (revised the same day): the owner tried two tabs, then asked to go back to one panel now that the text is shorter. Sizing sits on top; the entries accordion below takes the remaining height. Only the selected entry shows its form; the others are one-line rows with name, price, share and their stop/target count. A selection made elsewhere (chart, legend) opens and reveals the entry. The "Selected entry" select is gone; the expanded editor remains.
- **Compact entry form**: share and entry price, then Stops and Targets lists with a + button; each level is one row (Price/% toggle, value, share, remove) with the resolved price under % levels.
- **Less text**: removed the page eyebrow and subtitle, the draft banner (notices now appear only for unsaved changes, revisions and read-only plays), the perpetuals note, the "THE PLAY" strip, the deferred sizing assistant, long explanatory paragraphs in the position panel and entry form, the journal help lines (now placeholders) and the summary's "Not calculated" payoff rows. Caveats such as nominal USD and coverage moved into tooltips. Capital context shows margin and exposure as shares of portfolio value once the position is sized.
- **Balances**: capital context shows the portfolio balance (sum of its enabled accounts' balances; an unassigned account shows its own), the wallet total, the available amount and, for standard accounts, perps equity; margin and exposure are shares of that balance. An account balance is perps equity plus the supported-stablecoin wallet total, except unified and portfolio-margin accounts, where Hyperliquid keeps every balance in the spot wallet and the perps state is not meaningful, so the wallet total alone is the balance. Non-stable assets are excluded. The API adds `balanceUsd` and `totalStablecoinNominalUsd` to accounts and `balanceUsd`/`balanceCoverage` to portfolios; the perps-only `totalValueUsd` keeps its meaning. October 12, 2026: portfolio tiles and Overview use the same balance meaning. Account management reloads the shared account/portfolio data consumed here, and an explicitly null balance remains unknown instead of falling back to perps equity. Overview New play reuses the editor entry path and reopens unsaved work with a notification rather than discarding it.
- **Typeface**: the app uses Inter (self-hosted `@fontsource-variable/inter`) through one `--app-font` variable, replacing Manrope at the owner's request.
- **Budget**: the play's budget is a manual amount when set, otherwise the account's normalized available balance (`availableBalanceUsd`), shown on one line with its source: manual, wallet, reported margin or reported withdrawal. A known zero is a budget of zero; only when neither is known does a "Set budget" action show. A manual budget can be reset to the available amount. (October 19, Phase 0 decision 6; previously the stablecoin wallet amount when above zero.)
- **Entries heading**: count, allocated share (e.g. "100%") and icon actions: split equally, add entry, expand selected entry. Each entry header carries a red remove icon (hidden when it is the only entry). Removing an entry renumbers default names ("Entry 2" becomes "Entry 1"); colors stay.
- **Labels**: the price/percent toggle reads `$` / `%` and the size switch `$` / the contract (e.g. BTC).
- **Header actions**: History, Save, the status step (Plan it, Pause or Resume; the only filled button) and Delete or Cancel play always render in the same order with one outline style; unavailable ones are disabled. Destructive actions (delete, cancel, remove entry, remove level, delete drawing, clear drawings) are red. Reload accounts is an icon to the right of the account select (`AccountSelect`, the shared account picker, since October 18); the Manual / Use venue catalogue switch sits beside the Instrument label.
- **Plan checks**: a price stop or target on the wrong side of its priced entry (or at it) for the direction is an error: long stops below and targets above, short the reverse. Since October 19, 2026, a filled entry's stops are exempt (see [managed stops](#october-19-2026-managed-stops)). Percent levels are placed by direction and are never flagged. Errors appear in a notification area between the play fields and the workspace, mark the inputs invalid and offer only the corrections that clear every issue: swap the stops and targets of the affected entries, or switch the direction. Save and Plan it refuse while an error remains and say why until it is fixed; the API rejects such plans with 400 as well.
- **Chart entry view**: the Aggregate/selected menu moved out of the toolbar into the entry legend below the chart: an "All" button shows every entry and the average, clicking an entry shows only that entry and selects it.
- **Chart opening**: a chart jumps to the latest candles with 12 bars of space to the right, so the last candle is clear of the price scale and level tags.
- **Popup notifications**: the application shell has one notification stack (bottom right; `NotificationProvider` and `useNotifications()` in `components/notifications`). Notifications are error, success or info; a `key` replaces an earlier one instead of stacking, errors stay 10 s and others 5.5 s, hovering or focusing pauses the timer, `duration: null` keeps one until dismissed, and an optional action button is supported. Plays errors (save, status, open, image sync), the blocked-save reason, account refresh failures and the shell's success messages use it. Errors that describe the state of a page (the plan check notice, a failed load with Try again, dialog errors) stay inline.

## October 5 refinements

- **Margin within the budget**: while a play is a draft, its margin (the margin field, or the margin a quantity needs at the leverage and average entry) must fit the budget: the manual amount when set, otherwise the account's normalized available balance with its source. Above it, the size field is invalid, a line under it names the budget and its source in the instrument's quote unit ("Margin 700 USDC is above the 250 USDC budget (wallet).") and offers "Use" to set the size to the whole budget when the budget is above zero (quantities round down to the venue precision), and Save and Plan it refuse with the same reason. Planned and later plays are not checked, because the venue counts the margin of resting orders and open positions as already used.
- **Entry shares**: adding an entry, from the editor or the chart's entry tool, splits the shares equally; the owner can change them afterwards. A single entry takes the whole position: removing down to one sets its share to 100%, and its share field and Split equally are disabled until another entry is added. Removing an entry from an equal split keeps it equal; an uneven split is left for the owner.
- **Reward to risk**: each entry header shows its planned R:R (e.g. "1:2.5"): the share-weighted distance to its targets over the share-weighted distance to its stops, before fees. A dash shows until the entry has a price, a stop and a target, or while a level sits on the wrong side. Below 1:1 it is shown in the warning color.
- **Estimated liquidation price**: the size readout shows "Liq. ≈" and the chart draws a red dashed `LIQ ≈` reference line, which cannot be dragged. It is an estimate for the whole plan filled at its share-weighted average entry with isolated margin: the price where the margin (notional ÷ leverage) plus the unrealized result reaches Hyperliquid's maintenance margin, half the initial margin at the venue maximum leverage (`1 / (2 × max)`). Long: `avg × (1 − 1/L) ÷ (1 − mmr)`; short: `avg × (1 + 1/L) ÷ (1 + mmr)`. It ignores fees, funding, margin tiers of large positions and mark-versus-last price. Cross margin depends on shared collateral and other positions; it is not always safer. It is unknown without the venue maximum (manual instruments), and a 1× long has none. The venue's own figure applies once the position is open. A stop at or past the estimate shows a non-blocking warning in the plan notices.
- **Liquidation in the entry details**: under the entry price, each entry shows its own estimate (`Liq. ≈ 111.32 · 7.69% below entry`), with "this entry alone" when there are several entries; the readout and chart keep the whole-plan figure. A stop at or past the plan estimate gets a warning border and tooltip. Every figure follows the direction: switching Long/Short moves the estimate to the other side of the entry, and the warnings are recomputed once the stops and targets fit the new direction.
- **SL/TP interpretation**: the helper explicitly separates the underlying `price move (1×)` from the loss/gain percentage at the selected leverage. The entered % remains the leveraged return, consistent with saved plans and order matching: a 10% stop at 3× on a 3.75 long resolves to 3.625, a 3.3333% underlying move. Once size, price and quantity shares are known, each exit also shows its estimated loss/gain in quote units, using full-position quantity × entry share × exit share × price distance; leverage is already included in quantity and is not applied again. At 130 USDC margin, that stop estimates 13 USDC loss, and a 50% target at 4.375 estimates 65 USDC gain. These are planned estimates before fees, funding and slippage, not realized results or guaranteed execution.
- **Chart-picked SL/TP prices**: dragging a percentage stop/target or explicitly changing its price in the chart popover switches it to price units and stores the chosen price; the side editor selects `$` immediately. Share-only edits keep the previous unit and value. Undo/redo restore the unit and value together. Unmoved percentage levels keep the existing return-at-leverage meaning; chart-picked prices stay fixed when leverage or the entry price changes.

## October 19, 2026: sizing truth (Phase 0, 0.1)

The owner accepted decision 6 of the October 18 review; see the [Phase 0 contract](phase0-trust-correctness-contract.md#01-sizing-truth). Section positions are unchanged.

- **Budget source** (decision 6): the Budget and Margin-within-the-budget bullets above now use the account's normalized `availableBalanceUsd`, named by its source. A standard Hyperliquid account budgets from its reported withdrawal, not spot stablecoin dust; Lighter and RISEx drafts gain the check from their reported available margin. A known zero is a zero budget. The check stays draft-only. There is no rule never to raise leverage to fit a budget: a positive budget still gets the leverage suggestion, but a zero budget never does.
- **One risk denominator**: the Summary's risk share divides by the account `balanceUsd`, the same reference the size suggestion uses, labelled "Risk / <account> balance" (with the nominal-stablecoin note); an explicit manual budget is a labelled fallback, otherwise the share is unknown. An accepted 1.25% risk therefore reads 1.25% in the Summary. Capital context labels its ratios "Margin / <portfolio> balance" and "Exposure / <portfolio> balance", naming the account when it is unassigned.
- **Over-budget message**: the editor line and the blocked-save notification share one sentence in the instrument's quote unit with the budget source.
- **One available figure**: capital context's "Available" shows the same normalized `availableBalanceUsd` as the budget, for every syncing venue, with its source; the stablecoin wallet stays the separately labelled "Wallet total". The budget and that figure add the snapshot time ("reported margin · as of 14:02") and "last refresh failed" when the account's latest refresh did not succeed; no age limit is assumed.
- **Liquidation wording**: the cross-margin caveat matches the size readout: shared collateral and other positions decide it; it is not always safer.
- Sizing suggestions also change (stop coverage gate, freshness, after a fill, wording); see [sizing suggestions](sizing-suggestions.md#phase-0-sizing-truth-october-19-2026).

## October 13 execution and pending-save corrections

- Direction is fixed once entry fills exist. Open Plays disable the Long/Short toggle and the direction-reversal fix in plan notices; the API independently enforces the filled-entry rule. Untaken plans can still change direction, and stop/target revisions remain available.
- The Execution tab filters owner-link choices by order side and reduce-only/position-TP/SL flags. The API also checks retained fill effects. Owner attribution never changes immutable fill facts.
- Execution responses include `issues`, rendered as "Not reconciled". They explain known missing executions, incompatible fill roles, over-exits and invalid chronology. Incompatible fills remain visible on their links but are excluded from progress/P&L totals.
- Saving captures the submitted draft. When a response arrives, newer evidence notes/markup remain in the editor; only persisted IDs and saved-state metadata merge into them. The Save button remains enabled until those later edits are saved.
- Removing an image while a save/upload is pending does not resurrect it in the editor. If that upload succeeds, its server ID remains in deletion bookkeeping and the next Save deletes it. Discarding the later edits leaves the already-saved image on the server.

These corrections preserve the approved layout. They do not introduce autosave, split-fill attribution, terminal Play reopening, manual executions or a retrospective-record workflow.

## October 15, 2026: journal, default titles and play identity

Follow-ups to the [visual refresh](../design/visual-refresh/README.md), requested by the owner. Section positions are unchanged.

- **Journal**: one header row (icon and title, underline tabs, actions). An "Expand journal" button opens the same live journal in an in-page dialog, like the chart and entry expansions; Close or Escape restores focus. The left column now gives the journal more height (about 1.3 : 1 chart to journal, minimums 460 and 400 pixels; the desktop workspace is `clamp(1000px, 100dvh + 100px, 1180px)`), superseding the earlier 940-pixel, 64/36 figures. Notes keep faint ruled lines. The "Journal context" disclosure was removed.
- **Review tab**: shown once a Play is Open, Closed or Cancelled; hidden for Draft, Planned and Paused (Paused is only reachable before any fill). A Play that already has review text or structured answers keeps the tab. Retrospective Plays are unchanged.
- **Default title**: saving a Play whose trimmed title is empty names it `<asset> <Direction> · <D Mon YYYY>` (local save date, fixed English month names), for example `BTC Long · 15 Oct 2026`, and the title input previews it as its placeholder. A typed title is never replaced. Closed and Cancelled Plays are skipped because their title cannot change; legacy blank titles keep the "Untitled play" display fallback.
- **Play identity**: lists of saved Plays (Plays list, Overview Plays, Review play lists) show the asset as a ticker chip, the venue as its logo (name in the tooltip and accessible name) and the account with a wallet icon, instead of one "asset · venue · account" line.
- **Less text, again**: notices for revisions and retrospective records are pills beside Status; caveats live in info tooltips (`InfoTip`); sizing suggestions keep their reasoning and an "Estimates" tag; the risk summary states "Planned, before fees, slippage and funding" with the remaining caveats in a tooltip.

## October 19, 2026: plan number format

Phase 0, item 0.5a of the [trust and correctness contract](phase0-trust-correctness-contract.md). No section moves.

- **Strict on write**: the size, budget, entry shares and prices, and stop and target values and shares are saved only as blank or an unsigned plain decimal (`^[0-9]+(\.[0-9]+)?$`), at most 64 characters, that a C# decimal holds exactly (28 decimal places). No sign, exponent or whitespace: `1e2`, `.5`, `5.`, `+100`, `-1` and a trailing newline are refused with 400, naming the field (for example "Entry 1 price: use a plain decimal such as 100 or 0.5…"). Leverage keeps its own whole-number rule. The accepted and refused strings are one list, `frontend/src/test/contracts/plan-numbers.json`, checked by both the backend and the browser tests.
- **Tolerant on read**: stored plans and plan revisions are never rewritten. Server reads keep parsing stored text tolerantly, and the browser loads any stored text. A field whose text is unchanged from the Play's stored plan still saves, so no earlier plan becomes unsaveable: the same field of the same item, meaning the size or budget, an entry's price or share by entry ID, or a stop's or target's value (in the same unit) or share by entry and level ID. Another spelling of a stored value is new text. Retrospective records are unchanged.
- **Negative percentages** are refused with a pointer to a price: "Entry 1 stop 1: use a price for a stop beyond the entry; a percentage cannot be negative." Previously `−5 %` saved and silently resolved to no stop. The grammar decides first: only a minus sign before an otherwise valid, non-zero plain decimal gets this reason (`-5`), while `-1e30`, `-.5` or `-0` get the plain-decimal reason, in both runtimes. `0 %` stays valid and is a break-even stop at the entry price. Price stops keep the wrong-side rule until their entry fills (0.5b, below).
- **Browser**: every plan field is read with one parser (`features/plays/numbers.ts`): the trimmed text when it is plan text, otherwise unknown, so the chart, estimates and leverage dialog agree with each other and with what the server stores. Leverage is read as whole digits, as the server reads it. Surrounding whitespace is trimmed before saving, except stored text sent back unchanged. A field the server would refuse is marked invalid with the server's reason as its tooltip, and Save and Plan it refuse with that reason. Unchanged stored text that is not plain carries a warning instead (also `1\n`, which only trims to plain text): it saves, but estimates ignore text that is not plain. A field holding text a number input would blank (`+100`, `100.`, `1\n`) is shown as a decimal text field, so the stored text stays visible. The wrong-side check reads stored text as the server's side check does (`NumberStyles.Float`), so a stored `1e2` entry flags a new long stop at 105 before saving. The chart level editor gives the same plain-decimal reason. Numbers the browser writes (size conversion, Use budget, dragged and picked prices, rescaled percentages, suggestions) never use exponents; tiny quantities print as `0.0000005`, not `5e-7`.

## October 19, 2026: managed stops

Phase 0, item 0.5b of the [trust and correctness contract](phase0-trust-correctness-contract.md#05-managed-stops), under the owner's decision 2. No section moves; the lifecycle rules are in [play lifecycle](play-lifecycle.md#managed-stops).

- **Stops at or beyond a filled entry.** An entry with a linked or manual fill may have price stops at or beyond it, for break-even or a locked-in profit. Saving asks for a revision reason as any plan change after planning does. The plan checks and the entry form mark nothing for them. Entries without fills keep the wrong-side rule, and targets keep theirs. A percent stop cannot go past the entry: use a price. The editor knows which entries filled per entry, as the server decides it: from the execution's linked entry fills for a venue Play (leaving out fills that contradict the entry, as `entries[].filledQuantity` does), and from the manual ledger for a manual Play, which the page reads for every saved Play past Draft. Loaded facts count whatever the saved status says, so a Play the venue opened still counts as filled when rereading it failed. Until they load, while a link, unlink, dismissal or rebind is in flight, until the execution reflects the last one to complete (its own answer, or a read started after it), after a failed execution read (until newer facts are applied), or until the current ledger read succeeds (every read, also at the same version, drops the previous answer), the fills are unknown: no entry counts as filled for the plan checks, the earlier figures are not shown as current, and an Open status alone never makes one filled. A stop kept unchanged from the saved plan (the same text, unit, entry price and direction) still saves even if its entry has no fill any more, for instance after its fill was unlinked: the plan notices and the entry form show it as a warning ("Entry 1 stop is above the entry price, but its entry has no fill. It was saved there and still saves unchanged."), and a changed stop there is an error again.
- **Signed Summary row.** The first Summary metric is now "All stops". It reads what every stop filling would do to the position: "+5 USDC locked in", "−5 USDC at risk" or "0 USDC break-even", in the quote unit. A small line says what it is measured from: "From the average fill" for entries whose average fill is known (the quantity × price of the linked entry fills over their quantity, exactly, or the manual ledger's quantity-weighted entry price), "From the planned entry" otherwise, or both. While the fills load it reads "Unknown · Waiting for the entries' fills", never a figure from the planned entry. With a size of zero there is no position, so it reads "0 USDC", not "break-even". If an older server's execution lists fills that do not add up to the entry's filled quantity, its 28-place average is used, and a result within that rounding of zero reads "Unknown · Within the rounding of the reported average fill" rather than claim a sign. It uses the planned full-position quantity, entry and stop shares, and exact fractions over the plan text and the exact average fill (`lib/decimal.ts`), rounded once for display. A small non-zero result reads "< 0.01" with its sign. A partial plan shows "Known …" with "Total unknown", and unknown stays "Unknown". Stop coverage counts a filled entry's priced stops wherever they sit. A result that is not a loss sets the risk share to a known 0% and Planned R:R to "None" ("No loss at the stops"). Percent stops resolve from the planned entry at the leverage, as order matching does.
- **Rebind from the drift record.** In the Execution tab, a `level.drift` record lists each drifted linked stop or target ("Entry 1 stop: order 123 rests at 101; the plan said 95.") with a reason field and "Use 101". Rows are shown only where the monitor found the order away from its level, matched as the server matches (one step of the fifth significant figure, or the instrument's tick when coarser). A percentage level says so on the button, for example "Use 101.5 (changes 5 % to a price)". It sends the saved version and the order's price as Vessel stores it. The answer is the saved Play, which the editor adopts, and the execution is read again. The record then says the plan matches the order until the monitor's next check clears it. The action waits while plan edits are unsaved or other Play work runs. A 409 (the Play or the order changed) is shown on the record.

