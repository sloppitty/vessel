# Vessel current-state diagrams

Read with [Vessel today](../app-status.md). These diagrams describe the
October 14, 2026 checkout at `d17f56a`, not a deployment or a future architecture.
PR #38 remains a draft at this handoff. Waves 1 and 2 passed Sol verification
after transport corrections; later cleanup waves remain unfinished.

## Screens and owner workflow

Arrows show common journeys, not restrictions on navigation.

```mermaid
flowchart TD
    Connect["Sign in with a wallet"] --> Overview["Overview<br/>Balances, recent operations, saved Plays"]
    Overview --> Accounts["Accounts<br/>Add, inspect, refresh"]
    Accounts --- Portfolios["Portfolios<br/>Optional grouping"]
    Accounts --> Activity["Activity<br/>Imported position operations"]
    Overview --> Plays["Plays<br/>Plan, executions, journal, evidence"]
    Activity -->|"Preview and confirm eligible facts"| Retro["Retrospective Play<br/>Created Closed"]
    Plays -->|"Eligible saved closed results"| Review["Review<br/>Results and exclusion drill-down"]
    Retro -->|"Eligible results"| Review
    Review -->|"Open a Play and reflect"| Plays
    Overview --- Settings["Settings<br/>Appearance and capabilities"]
```

The [shell](../../frontend/src/features/workspace/ApplicationShell.tsx) owns
navigation and unsaved Play state. Activity does not create Plays automatically.

## Runtime and trust boundaries

Arrows show calls or data delivery, not project-reference dependencies.
Application modules share one API process; these are not separate services.

```mermaid
flowchart TD
    Browser["Browser<br/>React + TypeScript + Lightweight Charts"]
    subgraph Host["Self-hosted server boundary"]
        API["ASP.NET Core 10 API<br/>Wallet session and owner scope"]
        Modules["Application modules<br/>Workspace, Plays, Diary, Activity, Overview, Review, Sizing"]
        Market["Market data and evidence services"]
        Stores["EF Core persistence stores"]
        DB[("PostgreSQL<br/>Owner-scoped records")]
        Images[("Local evidence directory<br/>Original images")]
        Adapters["Read-only venue adapters"]
        Relay["Shared Hyperliquid WebSocket relay"]
        API --> Modules
        API --> Market
        Modules --> Stores
        Market -->|"Evidence metadata"| Stores
        Stores --> DB
        Market -->|"Original bytes"| Images
        Modules -->|"Refresh / execution checks"| Adapters
        Market -->|"Candles / context"| Adapters
        Relay -->|"Observations via API"| API
    end
    Browser -->|"Authenticated HTTP; SSE subscription"| API
    API -->|"Responses and market events"| Browser
    Adapters -->|"Read-only HTTP"| Venues["External venues<br/>Hyperliquid, RISEx, Lighter"]
    HL["Hyperliquid public market stream"] -->|"WebSocket observations"| Relay
```

Production packaging puts nginx in front of the API and can add a TLS proxy.
The diagram does not imply a deployed topology. Encryption keys stay in server
configuration; encrypted Lighter tokens stay in PostgreSQL. The browser cannot
read them back. Venue inputs are validated before they become retained facts.
See [backend boundaries](../architecture/backend-guide.md),
[credential storage](../architecture/venue-credentials.md) and
[deployment packaging](../../deploy/README.md).

## Facts, intent and reports

Activity and Review answer different questions. Manual records never enter the
imported Activity branch; retrospective records never enter the sizing branch.

```mermaid
flowchart TD
    Venue["Read-only venue reads"] --> Snapshot["Account snapshots"]
    Venue --> Imports["Retained imported fills and orders<br/>Normalized side, effect, fee and PnL basis"]
    Snapshot --> Overview["Overview<br/>Balances and retained-operation results"]
    Imports --> Activity["Activity<br/>Position operations from fills"]
    Activity -->|"Realized operation results"| Overview
    Imports --> Match["Order-to-plan linking<br/>Automatic only when unambiguous"]
    Plan["Owner's saved plan"] --> Match
    Match --> Planned["Planned-origin Play<br/>Linked imported executions"]
    Manual["Explicit manual entries and exits<br/>Correction history; no venue imports"] --> ManualPlay["Manual-account Play<br/>Owner-recorded executions"]
    Planned -->|"Eligible Closed"| Review["Review<br/>Saved closed-Play results"]
    ManualPlay -->|"Eligible Closed"| Review
    Planned -->|"Eligible Closed"| Sizing["Sizing<br/>Track record and suggestions"]
    ManualPlay -->|"Eligible Closed"| Sizing
    Activity -->|"Preview, confirm, recheck whole orders"| Retro["Closed retrospective Play<br/>No inferred thesis or initial risk"]
    Retro -->|"Eligible net result; no initial-risk R"| Review
    Prices["Candles / market context / live prices"] --> Chart["Chart observations only<br/>Never fills"]
```

Unknown costs and incomplete or contradictory executions can block scoring.
See [diary rules](../architecture/diary-completion-contract.md),
[Review](../architecture/portfolio-review.md) and the
[sizing eligibility filter](../../backend/src/Vessel.Application/Sizing/SizingService.cs).

## Play lifecycle

This is the saved-record lifecycle. An unsaved editor draft is browser memory,
not a database record. Execution progress and review are separate tracks.

```mermaid
stateDiagram-v2
    [*] --> Draft: Save new idea
    Draft --> Planned: Plan it with revision 1
    Planned --> Paused: Pause before fills
    Paused --> Planned: Resume
    Planned --> Open: Linked entry fill or manual entry
    Paused --> Open: Linked entry fill still wins
    Open --> Closed: Valid recorded exits settle entries
    Draft --> Cancelled: Cancel with reason via API
    Planned --> Cancelled: Cancel before fills
    Paused --> Cancelled: Cancel before fills
    Draft --> [*]: Delete saved draft
    [*] --> Closed: Confirm eligible retrospective operation
    note right of Closed
        Final status; no reopening
        Review and evidence may change
        Manual corrections must stay closed
    end note
```

Imported closure also requires complete, compatible linked executions and no
resting entry order. A flat venue position alone is insufficient. Draft
cancellation is an API/domain capability; the current Draft toolbar offers
Delete instead. See [Play rules](../../backend/src/Vessel.Domain/Plays/Play.cs)
and [lifecycle contract](../architecture/play-lifecycle.md).
