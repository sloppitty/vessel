# Appearance settings

October 9, 2026: the owner asked for a Settings page with a theme customizer and built-in themes, mostly dark with some light ones. This implements the "configurable themes" capability approved on September 30 alongside the Graphite default.

## Scope

- Settings › Appearance offers ten built-in themes: seven dark (Graphite default, Carbon, Midnight, Forest, Petrol, Espresso, Aubergine, taken from the prototype design studies) and three light (Paper, Daylight, Sand).
- The customizer overrides sixteen colours (surfaces, text, actions, signals, chart candles) and the corner radius of the selected theme. Overrides are kept per theme and can be reset.
- October 10: the Appearance header has Theme builder and Customize colours buttons, and the Theme row of the Private session panel has a Customize button. They scroll to the section and focus its heading; they do not change the hash route.
- A theme only changes colours and radius. It never moves sections; the approved layout is shared by all themes.
- Every built-in theme passes WCAG AA (4.5:1) for text, secondary text, positive, negative and warning on all surfaces, and for the primary button (3:1 for the focus ring). The customizer reports failing pairs but does not block them. A test enforces this for the built-ins.

## Theme builder

The owner then asked for a builder that randomizes colour combinations and saves them under a custom name.

- *Randomize* rolls a dark, light or either-scheme palette: one tinted neutral hue for surfaces and text, one accent hue (same, analogous, complementary or split) for actions and focus, and conventional green/red/amber signals. Inks are lightened (dark) or darkened (light) until every readability check passes, so every roll is readable; a test checks 1,000 rolls.
- The roll becomes the *Random draft* theme and applies immediately. It can be fine-tuned in the customizer like any theme; a new roll replaces it.
- *Save theme* stores whatever is on screen (a draft, or a customized built-in or saved theme) under a name in *My themes*. Names are unique (case-insensitive), at most 40 characters; up to 24 saved themes. Saved themes can be deleted after a confirmation step; deleting the selected one returns to Graphite.
- Saved themes and the draft live in the same `vessel.appearance` entry. Malformed, duplicate or unknown entries are dropped when it is read.

## Implementation

- `frontend/src/features/settings/generator.ts` rolls palettes and suggests names.
- `frontend/src/features/settings/themes.ts` defines the catalogue and contrast checks; `appearance.ts` resolves the selection into CSS custom properties on `<html>` and stores it in `localStorage` under `vessel.appearance`. It is a browser preference, not workspace data, holds nothing sensitive and survives disconnecting. Server-side preference storage is not implemented.
- `initAppearance()` runs before the first render to avoid a flash of Graphite.
- Component styles read only tokens (`--background`, `--card`, `--sidebar`, `--positive`, `--chart-candle-*`, …); hard-coded shell colours were replaced by tokens or `color-mix` of tokens. `styles.css` keeps the Graphite values as the fallback.
- October 15: `styles.css` also derives the design-language tokens (type scale, `--brand*` from `--ring`, `--line`, `--sheen`, elevations, panel/control radii, contour texture) from the theme tokens, so every theme follows them without new customizer colours. See [the visual refresh](../design/visual-refresh/README.md).
- An open chart re-reads the tokens when the theme changes. Level tags choose dark or white text by the tag colour's luminance (`components/chart/ink.ts`).
- Light themes use hollow up candles and solid down candles, keeping red and green free for stops and targets.

## Known limits

- Entry colours, drawing palette and image markup colours are fixed pastel or saturated values chosen for dark backgrounds; pale entries are faint on light themes.
- No "follow system light/dark" option yet, no import/export of custom themes and no renaming (save under a new name, then delete the old one).

## Display time zone (October 20, 2026)

Phase 1.6e of the [learning-loop contract](phase1-learning-loop-contract.md),
covering F-05 and G-13.
- **Setting.** Settings has **Times**: **Local**, the browser's zone and the
  default, or **UTC**. It is a browser preference stored like appearance
  (`vessel.display`, or `vessel:u:{userId}:display` for a signed-in user),
  holding `{ "timeZone": "local" | "utc" }`. Anything else reads as Local.
- **One formatter.** `lib/time.ts` formats every displayed time and date in
  English day-month order on a 24-hour clock, in the chosen zone. Times name
  their zone ("13 Oct 2026, 12:00 CEST", "… 10:00 UTC"). `components/ui/time.tsx`
  renders one with the same instant in the other zone, to the second, as its
  tooltip, and re-renders when the setting changes, including on the Plays
  page kept mounted in the background.
- **Where.** Activity (operations and executions), Review (Play rows,
  drill-downs, exclusions, refresh times), the chart (axis ticks, crosshair,
  drawing tags, fill tooltips, stop evidence, "Updated", capture captions),
  Execution, Monitor, History, the Plays list, the journal log, accounts,
  credentials, notifications, funding settlements and coverage, the manual
  ledger's audit, Execution's fill list, and manual entry, which types and
  shows times in the zone and sends UTC. A manual draft keeps the zone its
  time was typed in, so changing the setting never moves it to another
  instant; its label names that zone.
- **Spans.** An Activity operation names one zone at its end, or each end's
  own when a daylight-saving change falls between them.
- **Day boundaries.** Server filters keep UTC days until Phase 2. Activity's
  custom range is labelled "(UTC)", and Review's period line, chart buckets
  and as-of time stay UTC and say so. Stored values never change.
- **Chart ticks.** Lightweight Charts places ticks at UTC boundaries; in the
  local zone each tick is labelled with its own instant's local date or time.
- **Numbers.** `numberFormat()` in `lib/format.ts` is the one en-US number
  formatter, cached per option set; the formatters that followed the
  browser's language now use it. Money keeps its en-US formatters. Exact
  strings are unchanged.
- **Not changed.** The header's greeting and today's date follow the browser.

