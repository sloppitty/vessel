# Frontend rules

The top-level [`AGENTS.md`](../AGENTS.md) applies first. Structure: the
[frontend guide](../docs/architecture/frontend-guide.md). Use pnpm and a Node
24 with full ICU locale data (see the container route in the top-level file).

## Layout and design

- Keep the approved section positions: chart, position editor, journal, capital
  context and summary ([approved baseline](../docs/design/approved-baseline.md)).
  The Play entry editor stays bounded, with its expanded editor.
- Themes change colours and radius only, never layout. Component CSS reads theme
  tokens, not hard-coded colours. Built-in themes must keep passing the contrast
  test; after a style change, also check the rendered controls.
- Follow the [visual refresh](../docs/design/visual-refresh/README.md): reduced
  helper text (pills, `InfoTip`, `CoverageBadge` only when coverage is incomplete),
  and keep the required notices it lists.
- Every select uses `NativeSelect`, except account selectors, which use
  `AccountPicker` or `AccountSelect`.
- Long is green and up, Short red and down. Leverage is a whole number.
- Do not tag items as Paper; the Paper disclosure appears once, in the header.
- Motion is absent under reduced motion.

## Data in the browser

- The browser talks to the Vessel API, never directly to venue APIs.
- No secret, token or key in Vite, a URL or browser storage. `localStorage` holds
  only per-device preferences; candles are never stored across reloads.
- Unsaved Play drafts live in memory: shell navigation keeps them, reload and
  sign-out discard them.
- Controls follow the API's venue capabilities, never venue names.
- Show unknown values as unknown, never as zero. Suggestions are never applied
  automatically.
- Event cursors are opaque and owned by `src/api/event-cursor.ts`.
- Keep the icon keys in `src/api/portfolio-icons.ts` in step with `PortfolioIcons`
  in the backend domain; never accept uploaded images or markup as icons.
