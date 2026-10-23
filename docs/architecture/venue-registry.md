# Venue registry and capabilities

October 23, 2026: [Pacifica](pacifica-integration.md) adds a `solana-address`
source rule and the bundled `pacifica` logo. The public source is an exact
base58-encoded 32-byte `SourceId`, never the legacy lowercased EVM `Address`.
Its current ordinary/stop orders are read with explicitly incomplete book
coverage; positions requiring unreported money fields and funding settlements
with an unverified sign remain outside its supported projection.

October 5, 2026, slice S1 of the RISEx-first plan ([risex-integration.md](risex-integration.md)). Behaviour for Hyperliquid and manual accounts is unchanged. This is the seam through which every further venue (RISEx, then Lighter) plugs in.

## Contract

**Application** (`Vessel.Application.Venues`):

- `VenueDescriptor(Id, Name, Status, Source, Capabilities, QuoteAsset?, TradeUrlTemplate?)` describes a venue. `Source` is how an account is identified: `evm-address` or `none` (manual and planned venues). `TradeUrlTemplate` contains an `{instrument}` placeholder.
- `VenueCapabilities(Sync, Instruments, Orders, Candles, MarketContext, Stream, StablecoinWallet)`, with optional `AccountDiscovery`, `ReadOnlyCredential`, `ManualExecutions` and `CurrentOrderBook`. Use cases and the browser check these, never a venue name.
- `IVenueRegistry` returns a venue's descriptor and its adapters: `Reader`, `Orders`, `Candles`, `MarketContext`, `Stream`. A missing adapter means the venue does not offer that capability. Each service turns that into its own safe error (502 or 503, with the same messages as before). When one venue has several registrations of a port, the last wins, as with a single injected service.
- `WorkspaceService`, `CandleService`, `MarketContextService`, `MarketStreamService` and `PlayExecutionService` resolve adapters through the registry. No `"hyperliquid"` literal remains in `Vessel.Application` except the two Hyperliquid-specific notices and comments scheduled for slice S3 (candle limit text and the matcher's price rule).

**Infrastructure:** each venue module registers its `VenueDescriptor` and adapters in `DependencyInjection`. Hyperliquid owns `HyperliquidPerpetualReader.Descriptor`; the manual venue is `VenueDescriptor.Manual`. `IVenueRegistry` is scoped.

## API: `GET /api/system`

`venues` is built from the registry, then the named-but-unbuilt venues (Quantfury `candidate`) that have no adapter, with manual last. Since October 14, 2026 the stale planned Lighter entry is gone: `lighter` and `lighter-rh` are registered venues.

```json
{ "id": "hyperliquid", "name": "Hyperliquid", "status": "read-only", "source": "evm-address",
  "capabilities": { "sync": true, "instruments": true, "orders": true, "candles": true,
                    "marketContext": true, "stream": true, "stablecoinWallet": true,
                    "accountDiscovery": false, "readOnlyCredential": false, "manualExecutions": false,
                    "currentOrderBook": true },
  "quoteAsset": "USDC", "tradeUrlTemplate": "https://app.hyperliquid.xyz/trade/{instrument}", "logo": "hyperliquid" }
```

`logo` is an optional key from the fixed backend set `VenueLogos` (`hyperliquid`, `lighter`, `lighter-rh`, `risex`, `manual`). `VenueRegistry` refuses to build when a registered descriptor uses any other key, so an unknown key fails at the first registry use rather than reaching the browser. Planned venues have no logo (`null`). The browser maps the key to a bundled same-origin asset (`venue-logos.ts`, kept in step like portfolio icons) and falls back to a generic icon.

Planned venues report every capability `false`. The browser accepts any number of venues as long as each states its capabilities, IDs are unique, `manual` is present and trade links are `https://` with an `{instrument}` placeholder.

## Current order book (October 19, 2026)

Phase 0 item 0.4c (review defect D-09, the owner chose the label). `CurrentOrderBook` says whether the venue's order reader returns the current open order book: open rows observed with the read, and an `OpenOrdersComplete` that means something. It defaults to false, so a venue opts in. Hyperliquid and both Lighter instances say true. RISEx says false: its reader reads bounded order history and TP/SL records, not `/v1/orders/open`.

Without it, the monitor:
- records protection coverage as **unsupported: current book not read** (reason `current-book-not-read`);
- does not run the book-dependent checks (missing, short and cancelled stops, excess exits, orphaned exits and entries, level drift). They are undecidable by design, never missing evidence;
- does not degrade a run because the order read is incomplete. Other degradation reasons (a gap, omitted executions, partial positions, an order-read failure) still apply.

The browser reads the same flag (`capabilities.currentOrderBook`, optional; `Venues.readsCurrentBook` is false only when an order-reading venue states false) and says that those checks do not run there. Exit and entry watches are unchanged: a reported terminal order still ends a watch, and absence still needs a complete current read. See the [monitor plan](play-monitor-plan.md#stop-coverage).

## Behaviour that follows from the descriptor

- **Creating an account:** the venue must be registered (`Choose manual, hyperliquid or … .` otherwise). A venue with an `evm-address` source requires a 42-character hexadecimal address, with the venue's name in the message.
- **Refreshing:** needs `Sync`; otherwise `Only <sync venues> accounts can be refreshed.` A registered `Sync` venue without an adapter answers 503.
- **Order tracking:** a Play is tracked only when its account's venue has `Orders`.
- **Frontend:** account rows, the sync button, wallet panels, the account dialog (venue list, address field), the instrument catalogue, trade links and instrument labels read `useVenues()`. `VenuesProvider` is mounted by `ApplicationShell`; components outside it see no venues.

## Adding a venue

1. Implement the ports it supports in `Infrastructure/Venues/<Venue>/` and expose a `Descriptor`.
2. Register the descriptor and adapters in `DependencyInjection`. Read the host from `VenueHostOptions` (`Vessel:Venues:<Section>`), never a literal: add a section with today's host as its default and let `VenueHostValidation` cover it.
3. If it has a logo, add the key to `VenueLogos` and the frontend `venue-logos.ts` together.
4. Nothing else in Application or the frontend should need to name the venue. If it does, generalize that code first.

## Hosts and several instances of one module

Venue hosts are configuration under `Vessel:Venues` (`Hyperliquid:BaseUrl`, `Hyperliquid:StreamUrl`, `Risex:BaseUrl`, `Lighter:BaseUrl`, `LighterRh:BaseUrl`), defaulting to the production hosts in `appsettings.json` and `VenueHostOptions`. `ValidateOnStart` requires absolute `https://` origins for REST (adapters send absolute request paths, so a base path would be silently dropped) and `wss://` for streams, with no user info, query or fragment; an origin without a trailing slash is normalized. Failures name the key, never the value. Tests override hosts through ordinary configuration. Operator settings are in [deploy/README.md](../../deploy/README.md#optional-venue-hosts).

One module may register several venues. The Lighter module registers one instance per `LighterExchange` (`lighter`, `lighter-rh`): each has its own descriptor, named `HttpClient`, keyed catalogue cache, keyed scoped reader and read gate, and registers each port once, so the registry and the `SingleOrDefault(v => v.VenueId == …)` lookups for discovery and credential verification find exactly one adapter per venue ID. See [robinhood-lighter-integration.md](robinhood-lighter-integration.md#implementation-state).

## Tests

`MonitorCoverageTests` pins which descriptors read the current book and the monitor behaviour without it. `VenueRegistryTests` covers lookup (including last-wins), a fake second venue through create, catalogue and sync rules, capability-driven order tracking, the metadata order and logo-key validation. `ApiTests` checks the contract shape, including `logo` and the `lighter-rh` descriptor. `LighterRobinhoodTests` covers host validation, configured hosts per named client and one registration per Lighter instance. Frontend `venues.test.tsx` and `system.test.ts` cover lookups and contract validation. Tests that need venues in the browser tree use `@/test/render`.
