# Account identity options

October 16, 2026. The owner asked how to tell accounts apart in the Overview
Allocation donuts and the Activity account filter when names are similar
(“main”, “Main account”, “main”). This is a disposable comparison for the owner
to choose from; nothing is implemented in the app.

- Page: `prototype/account-identity.html` (serve the repository root, e.g.
  `python3 -m http.server` and open `/prototype/account-identity.html`).
- Captures: `prototype/account-identity-capture.mjs` with an external
  Playwright (`PLAYWRIGHT_MODULE`, optional `CHROMIUM_PATH`).
- Data is synthetic: the owner's three accounts plus a stress set (long account
  name, blank name, manual record, two “main” accounts on one venue, and a
  hypothetical venue with a long name and no bundled logo). Graphite tokens.

Account names are required today, so “unnamed” shows the fallback should that
change: the short source (address or index, `shortSource`) becomes the title.

## Donut legends

| Option | Capture |
| --- | --- |
| Current | ![](legend-current.png) |
| A · Venue chip | ![](legend-chip.png) |
| B · Two-line identity | ![](legend-two-line.png) |
| C · Logo as marker | ![](legend-marker.png) |
| D · Disambiguate only when needed | ![](legend-adaptive.png) |
| E · Account identity tile | ![](legend-tile.png) |

## Account selector

| Option | Capture |
| --- | --- |
| Current | ![](selector-current.png) |
| 0 · Richer native text | ![](selector-native-text.png) |
| 1 · Rich listbox | ![](selector-rich.png) |
| 2 · Grouped by portfolio | ![](selector-by-portfolio.png) |
| 3 · Grouped by venue | ![](selector-by-venue.png) |
| 4 · Searchable combobox | ![](selector-search.png) |

Any custom selector departs from the visual-refresh rule that every select uses
`NativeSelect`; the chosen option would need its own listbox/combobox keyboard
pattern, a mobile sheet and an update to that rule.

## Round 2 · styled options

The owner asked for something more modern than plain text. The interactive,
self-contained page is `prototype/account-identity-v2.html` (open it directly;
tabs switch options, the selector can be opened, searched, filtered by venue and
picked). Each venue gets an accent colour beside its logo; in production that
would be a venue descriptor field, mixed with theme tokens, not a theme colour.

| Legend | Capture |
| --- | --- |
| 1 · Capsules | ![](v2-legend-capsule.png) |
| 2 · Share bars | ![](v2-legend-bars.png) |
| 3 · Tiles | ![](v2-legend-tiles.png) |
| 4 · Compact + hover card | ![](v2-legend-hover.png) |

| Selector | Capture |
| --- | --- |
| 1 · Command picker | ![](v2-selector-command.png) |
| 2 · Tile picker | ![](v2-selector-tiles.png) |
| 3 · Chip rail | ![](v2-selector-rail.png) |

## Implemented · capsules and account picker

The owner chose option 1 for both surfaces and asked that the address chip take a
colour derived from the address. See the account identity section of the
[core workspace contract](../../architecture/core-workspace-contract.md#october-16-2026-account-identity).

Captures of the production frontend served by Vite with Playwright-mocked
synthetic API responses (no database, venue or personal data): the owner's three
accounts plus a long name, an unnamed account, a manual record, a second "main"
on Hyperliquid and an unassigned account.

| View | Capture |
| --- | --- |
| Allocation, Graphite | ![](implementation/desktop-allocation.png) |
| Allocation, Paper (light) | ![](implementation/light-allocation.png) |
| Allocation, 390px | ![](implementation/mobile-allocation.png) |
| Overview scope picker | ![](implementation/desktop-scope-picker.png) |
| Overview scope picker, 390px | ![](implementation/mobile-scope-picker.png) |
| Activity filter, shared name chosen | ![](implementation/desktop-activity-filter.png) |
| Activity picker searching "main" | ![](implementation/desktop-activity-picker-search.png) |
| Activity picker, Paper (light) | ![](implementation/light-activity-picker.png) |
| Review account filter | ![](implementation/desktop-review-picker.png) |
| Play editor account select (`AccountSelect`), October 18 | ![](implementation/desktop-play-account-select.png) |
| Play account select open | ![](implementation/desktop-play-account-select-open.png) |
| Play account select searching "scalp" | ![](implementation/desktop-play-account-select-search.png) |
| Play account select open, Paper (light) | ![](implementation/light-play-account-select-open.png) |
| Play account select open, 390px | ![](implementation/mobile-play-account-select-open.png) |
| Account select states: chosen, empty, unavailable, no accounts | ![](implementation/account-select-states.png) |
