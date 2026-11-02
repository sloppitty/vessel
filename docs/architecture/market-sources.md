# Market sources and the generic instrument selector

November 2, 2026. The owner found gold missing from Hyperliquid, chose the Cascade option from
[the selector comparison](../design/instrument-selector/README.md) and asked for one selector for every venue, with
builder markets that can be turned on and off and Plays that survive it.

## Why instruments were missing

The catalogue read only Hyperliquid's primary DEX. Gold is `xyz:GOLD`, on a HIP-3 builder DEX. On November 2, 2026 the
primary DEX listed 179 active perpetuals and the builder DEXes 166. `userFills` and `historicalOrders` already returned
builder rows, which Vessel dropped; positions, open orders and market context are per DEX.

## The generic selector

`InstrumentPicker` has no venue logic. It reads the catalogue and shows a search field over cascading columns: Category,
Source, Instrument. A column exists only if the catalogue has more than one choice for it, so the same component is three
columns on Hyperliquid, two on RISEx and a plain list on Lighter, which reports no categories. The shape follows the whole
catalogue, not the current filter, so the panel does not jump while narrowing. A count is what clicking it shows. Deviation
from the prototype: the field itself stays the search box, keeping the existing Tab, type and Enter flow, rather than a
second search box in the panel.

`VenueInstrument` gained `Source`, `SourceName`, `DisplayName` and `Keywords`. `Category` is now one normalized set
(`MarketCategories`: crypto, stocks, commodities, indices, fx, rates, pre-ipo, other); an unknown label is `other`, a venue
that states none leaves it null, and the browser counts a null beside labelled markets as Other. Hyperliquid's primary
markets carry no annotation and are crypto by this adapter's mapping. Lighter's API has no category field, so it stays null
rather than guessed from symbols.

## Market sources

A venue with `VenueCapabilities.MarketSources` lists markets from several sources. `disabled_market_sources` holds one row
per owner, venue and source while it is off, so a new source starts on. `GET /api/settings/market-sources` lists them;
`PUT /api/settings/market-sources/{venueId}/{sourceId}` with `{ "enabled": bool }` is idempotent. Migration `MarketSources`;
apply it explicitly, never to the owner's database during tests.

**Turning a source off only filters the selector.** The catalogue still returns its instruments, with `disabledSources`
beside them, so a saved Play keeps its leverage, tick and label. Imports, positions, orders, funding and the monitor never
read this setting, so a Play on a disabled market keeps linking fills and being monitored.

## Hyperliquid builder markets

- **Identity.** The native coin (`xyz:GOLD`) is the contract key: 16 assets are on more than one DEX. Instrument patterns
  allow one colon. The selector shows the display name and source, and titles and quantity units use the asset (`GOLD`); Play lists show the key as it is, which keeps the two NVDA markets apart.
- **Scope.** USDC-margined DEXes only (`collateralToken == 0`). Results, fees and sizing assume USDC, so `flx`, `vntl` and
  `km` (USDH), `hyna` (USDE) and `cash` (USDT0) stay out, and their fills stay out of scope. None had an active market on
  November 2, 2026.
- **Catalogue.** `allPerpMetas`, `perpDexs` and `perpConciseAnnotations`, cached 5 minutes and not forced by imports, on top
  of the unchanged primary `meta`. A malformed third-party market is skipped. Missing annotations leave markets labelled
  Other. If builder metadata fails, primary reads continue and positions and open orders report incomplete coverage.
- **Reads.** One `clearinghouseState`, `frontendOpenOrders` and `metaAndAssetCtxs` per DEX with a selectable market. Fills,
  order history, funding, candles and streams needed only the contract check relaxed. Builder funding rows carry an
  all-zero hash, which the reader used to reject, failing the whole funding read of any such account.
- **Balances are unchanged.** Account value, withdrawal and margin stay the primary DEX's; a builder position appears with
  its own margin and is not added to them, and the history notice says so. Whether unified accounts share margin across
  DEXes is not decided here.
- **Isolated-only markets.** `VenueInstrument.IsolatedOnly` carries the venue's `onlyIsolated`, and the selector tags them.
- **Accepted estimates.** Maintenance margin keeps half the initial margin at maximum leverage; builder margin tables can
  differ, and the liquidation estimate stays an estimate. Vessel's fifth-significant-figure price rule applies as on primary.

## Verification

On November 2, 2026 the real API and frontend ran against a throwaway PostgreSQL and RabbitMQ with the `MarketSources`
migration applied, signed in with the synthetic wallet and reading live Hyperliquid data: 345 markets listed, a public
account refreshed with builder fills in Activity, gold chosen with `xau`, its candles and live stream loaded, a Play saved,
XYZ turned off in Settings, and the saved Play kept its market and chart with the notice shown. The broker-backed monitor
end-to-end tests run on `xyz:GOLD`. The address used for live checks is not recorded here.

## Tests

`HyperliquidBuilderMarketTests`, `MarketSourceServiceTests`, `MarketSourceIsolationTests`, the cascade cases in
`InstrumentPicker.test.tsx` and `MarketSourceSettings.test.tsx`. `HyperliquidBuilderLiveTests` reads the real API when
`Vessel_TEST_HYPERLIQUID_ADDRESS` holds a public address with builder history.
