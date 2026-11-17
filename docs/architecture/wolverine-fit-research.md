# Wolverine fit for Vessel's Play monitor

October 14, 2026. W0 investigation by Sol. Research and disposable executable evidence only. No product implementation or deployment changes.

## Recommendation

**Conditional go for Wolverine 6.50.0. No-go for a drop-in replacement of the stores' inner saves.**

The spike proved the important transaction, recovery, owner and runtime cases with Vessel's actual sealed `VesselDbContext` and stores, PostgreSQL and RabbitMQ. The required integration conditions are:

1. Persist the business event and durable envelope inside the current outer transaction. Commit through the existing outer lock owner. Flush only after that commit succeeds.
2. Keep business `EventId` dedupe in Vessel, with its effect in the same transaction. Envelope dedupe is a separate, shorter-lived safeguard.
3. Provision both envelope schemas and the complete durable broker topology explicitly before starting producers. Keep automatic PostgreSQL schema creation off.
4. Set and validate the worker owner in a new scope before resolving its DbContext. Preserve the normal filters and write validation.
5. Ship runtime-compilation dependencies, or separately prove a pregenerated build. This spike proved the former.

These are integration requirements, not authorization to build the monitor. The safe adapter itself is not implemented in product code.

After reviewing W0 on October 14, the owner chose a bounded application-level unit of work before Wolverine integration. It will centralize saves, transaction completion and later post-commit dispatch rather than attach hooks to individual stores. The spike tested the existing store-owned transactions, not that refactor; its regression tests must preserve the proved transaction guarantees. See the revised delivery steps in [the monitor plan](play-monitor-plan.md).

October 15 note: the owner then replaced the delivery design with topics and subscriptions (see [the monitor plan](play-monitor-plan.md#topics-subscriptions-and-delivery)). Publishers publish each event once to `vessel.events`; each subscriber owns a quorum queue, a dead-letter queue, a registration and per-event receipts in PostgreSQL, and recovers missed events from the database with its own catch-up sweep. There are no publisher-written delivery rows and no republish sweep. Wolverine and RabbitMQ code moves into one `Vessel.Messaging` adapter. The W0 evidence below still holds; where this report speaks of consumers, per-consumer delivery claims or business republishing, read subscriptions, subscription receipts and subscriber catch-up. Predeclared topology remains required, but with catch-up an unbound topic delays delivery within the retention window instead of losing it. Subscription and command listeners use Wolverine's inline mode rather than the durable inbox: a 6.50 durable-inbox listener settles the broker delivery before its handler runs, and RabbitMQ 4.3 does not count nacks towards `x-delivery-limit`. PostgreSQL receipts are the business retry limit; the durable outbox stays. This needs its own pinned-version integration test; W0 did not cover it.

## Versions and license

NuGet's flat-container indexes and downloaded package `.nuspec` files were checked directly, then rechecked before finishing. The latest stable versions for `WolverineFx`, `WolverineFx.EntityFrameworkCore`, `WolverineFx.Postgresql`, `WolverineFx.RabbitMQ` and `WolverineFx.RuntimeCompilation` were all **6.50.0**. NuGet records the core publication at **2026-10-14 01:49:08 UTC**, and its companion packages around the same time. This is a same-day release, not a previously validated Vessel dependency. All five packages identify source commit `55a46250e9ffd99badab1ae032d334b73e2cbe56`. [NuGet index][nuget-index], [registration][nuget-registration], [core package metadata][nuget-core], [EF package metadata][nuget-ef], [PostgreSQL metadata][nuget-pg], [RabbitMQ metadata][nuget-rabbit], [compiler metadata][nuget-compiler].

| Component | Version actually exercised |
|---|---|
| SDK, from Vessel's `global.json` | 10.0.302 |
| Local .NET/ASP.NET runtime | 10.0.10 |
| Runtime-only Linux container | `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` 10.0.10, linux-x64, no SDK |
| Wolverine packages | 6.50.0 |
| EF Core / Relational | 10.0.10, matching Vessel |
| EF Design, pulled by Wolverine | 10.0.4 |
| Npgsql provider / Npgsql driver | 10.0.3 / 10.0.3, matching Vessel's provider |
| RabbitMQ.Client | 7.2.2 |
| JasperFx / Weasel | 2.87.1 / 9.45.0 |
| JasperFx.RuntimeCompiler / Roslyn | 5.0.0 / 5.0.0 |
| Disposable PostgreSQL / RabbitMQ | 18.6 / 4.3.6 |
| Docker | 29.6.2 |

The NuGet packages have actual `net10.0` dependency groups, not merely computed compatibility. Wolverine's EF package requires EF 10.0.4 or later; restore selected Vessel's runtime EF 10.0.10 without a downgrade or build warning. Build, migration, persistence and handler execution passed. EF Design remained 10.0.4 in the isolated graph; align it deliberately with Vessel's 10.0.10 tooling in any authorized integration. At inspection, NuGet's latest stable EF was 10.0.13, Npgsql provider 10.0.3 and RabbitMQ.Client 7.2.2. This investigation did not upgrade Vessel.

Each of the five downloaded Wolverine packages declares SPDX **MIT**. The repository's `LICENSE` at the exact package source commit contains the MIT grant. It permits commercial use, modification and redistribution, requires retaining the copyright and permission notice, and disclaims warranties and liability. There is no revenue threshold, source-publication obligation or paid-production condition in that license. Paid support is separate. This is a check of the selected Wolverine packages and their matching source license, not a complete transitive-license audit. [License text][license].

The measured image digests are in `spikes/wolverine/evidence.txt`. The locally cached floating `aspnet:10.0` tag resolved to runtime 10.0.13, so the runtime gate deliberately used **`aspnet:10.0.10`**. Do not treat floating tags as version evidence.

## Evidence matrix

“Proved” below means executed against the disposable services unless the row says otherwise.

| Gate | Result | Evidence / limit |
|---|---|---|
| Real context and nested transaction | **Proved** | Real `WorkspaceStore` outer row lock, real `ExecutionStore` inner advisory lock and reused transaction, multiple saves, real Planned/Open/Closed changes. Sealed context worked without subclassing. |
| Event, Play change and outbox atomicity | **Proved** | Rollback left Planned, zero event rows and zero outgoing envelopes. Commit left Closed, event row and durable outbox row before explicit flush. Separate-connection reads could not see the event before outer commit. |
| Inner `SaveChangesAndFlushMessagesAsync` | **Failed, unsafe use** | It committed the outer transaction. A later deliberate exception left Open, event row and consumer effect committed. |
| Inner `FlushOutgoingMessagesAsync` | **Failed, unsafe use** | Consumer acted while the event was still invisible. Outer rollback then left Planned and no event/outbox row, but the effect survived. |
| Commit, crash, restart | **Proved** | Abrupt exit 73 after commit, before flush/disposal. Envelope remained in API schema with only worker alive. Restarted API relay recovered it; consumer recorded one delivery. |
| Broker outage | **Proved, restart recovery** | Broker stopped after producer startup. Business transaction committed; send failed and envelope remained. Broker and producer restart delivered it. Continuous in-process reconnect without a producer restart was not tested in the spike; on November 17, 2026 issue #98 measured it in Vessel (no restart needed; a host started without the broker fails after about 95 s), see [Without the broker](messaging-and-workers.md#without-the-broker-measured-november-17-2026). |
| Business `EventId` dedupe | **Proved, PostgreSQL effect** | Two fresh envelopes with one EventId invoked handler twice, but a unique event/consumer claim plus effect in one transaction incremented once. |
| Same-envelope inbox dedupe | **Proved** | Two RabbitMQ copies with identical `MessageId` produced one incoming row and one handler invocation. A subsequent distinct message also ran. |
| No startup PostgreSQL DDL | **Proved + source inspection** | `AutoCreate.None` with absent schema failed startup and did not create it. Explicit `SetupResources()` built both schemas. Normal startup did not create `w0_missing`. |
| EF envelope mapping | **Proved** | `AddDbContextWithWolverineIntegration<VesselDbContext>` worked on the sealed context. Mapped transaction tests passed; design-time model showed both envelope tables excluded from EF migrations. |
| API/worker envelope isolation | **Proved for API crash case + source inspection** | Same DB, separate schemas and service names. Worker did not recover API envelope; restarted API did. Recovery SQL partitions by schema, not business owner or service-name column. |
| Topics, quorum and confirms | **Proved** | Durable topic exchange, durable/non-auto-delete quorum consumer queue; live broker channel metadata reported `confirm=true`. Both confirmation switches were enabled. |
| Producer before consumer | **Proved with predeclared queue** | Setup created bindings first. With worker stopped, queued copies remained with `consumers=0`; restarted worker consumed them. |
| Unbound topic | **Failed as a loss-prevention assumption** | `no.binding` was confirmed and removed from PostgreSQL outbox, without consumer delivery. |
| Owner scopes | **Proved** | Missing/unconfigured owner and foreign account rejected. Forty alternating-owner messages plus one A message overlapped at concurrency four, yielding A +21/B +20 effects with no context bleed. |
| Runtime-only release execution | **Proved, dynamic codegen** | Published worker ran as uid 1654 `app`, read-only `/app`, no SDK, on .NET 10.0.10; generated handler consumed real broker message and accessed PostgreSQL. Explicit setup also ran in that image. |
| Pregenerated handlers, local/in-memory test suite | **Documented, not tested** | No static-codegen or local-only suite was executed. Local/in-memory transport cannot prove PostgreSQL atomicity or RabbitMQ confirmation/recovery. |
| Whole product release / external notifications | **Not tested** | No monitor, image rebuild, Compose change, actual venue read, broker-volume loss/DB restore, scheduler, Telegram/Discord send or retention implementation. The spike sent API-to-worker notices, not the full bidirectional product command/event path. |

## Parent verification after handoff

On October 14, the owner asked to stop the agent and continue locally. Sol had already completed; the native close call found no live agent and T3 reported `no_active_run`. The parent reviewed the report and spike source, rechecked the save-and-flush implementation and license at the pinned source commit, and rebuilt the spike with zero warnings or errors.

The parent then used new disposable PostgreSQL and RabbitMQ containers with randomly assigned loopback ports and explicit `W0_CS`/`W0_AMQP` overrides. It independently reproduced:

- missing-storage startup rejection, with no `w0_missing` schema created;
- rollback and successful outer-commit/flush, both with and without EF envelope mappings;
- the unsafe helper leaving an Open Play, event and consumer effect despite the intended rollback;
- pre-commit flush leaving a consumer effect after the Play and event changes rolled back.

The first parent attempt timed out waiting for RabbitMQ readiness and ran no test modes. A second attempt capped Erlang server schedulers at `+S 2:2` and CLI schedulers at `+S 1:1` and passed. The timeout's cause was not established. Both attempts cleaned their own containers, volumes and networks. The parent did not repeat the complete crash, outage, owner-concurrency or runtime-image matrix; those results remain Sol's recorded evidence, not independently repeated tests.

## What the real Vessel transaction requires

`VesselDbContext` is sealed. It filters business entities through `CurrentOwnerId` and checks tracked business ownership in both sync and async saves. Its EF model does not currently contain Wolverine envelopes.

The execution path has two commit boundaries. Account refresh completes first. Execution then runs inside `WorkspaceStore.WithAccountLockAsync`, which starts an explicit transaction and locks the owner-filtered account row. `ExecutionStore.WithAccountLockAsync` reuses that transaction and acquires `pg_advisory_xact_lock`. `UpsertOrdersAsync` and execution saves run before the workspace store's outer commit. A Wolverine wrapper around one inner save must not own that commit.

The current [outbox implementation][outbox-source] explicitly calls `Database.CommitTransactionAsync` when a transaction exists, before flushing. Its separate flush method also does not check whether that transaction committed. The tests reproduced both problems, not just a successful isolated EF save.

The tested safe order was:

```text
begin outer transaction and acquire existing locks
make business changes; SaveChangesAsync as needed
insert business event row on the same DbContext transaction
publish through the enrolled IDbContextOutbox<VesselDbContext>
SaveChangesAsync for tracked envelopes when mappings are used
complete remaining business saves
outer transaction owner commits
only then FlushOutgoingMessagesAsync
```

Without envelope mappings, Wolverine's [EF envelope transaction][ef-transaction] executes envelope INSERTs through the context's connection and existing transaction. With mappings, it tracks envelopes for EF to save. Both paths passed. Neither needs the context unsealed or business types coupled to Wolverine.

The smallest integration is a scoped persistence event sink and an **outermost-commit hook/adapter**, retaining current locks and ownership checks. Nested stores must neither commit nor flush. Require an active transaction when recording a business event, rather than letting a sink silently start one. Rollback discards pending in-memory sends. A post-commit flush failure must not be reported as a rolled-back business operation: the durable envelope can recover. Do not let Wolverine auto-transaction middleware compete with these store-owned boundaries.

The spike's `w0_events` table is synthetic. It proved insertion on the real business transaction, not a future product `play_events` mapping or every lifecycle service branch.

## Schema setup, process isolation and packaging

In 6.50.0, `AutoCreate.None` does fail fast for missing PostgreSQL core storage by default. [Startup source][startup-source] calls `AssertStorageProvisionedAsync`; [the probe][storage-source] checks incoming, outgoing, dead-letter and main-store node tables. It does **not** perform a full index/function/column drift check. No additional missing-table guard was needed in this test. Retain the default fail-on-errors policy and enabled durability agents; disabling agents skips this startup path.

Explicit `IHost.SetupResources()` works even with automatic schema creation disabled, both locally and in the runtime-only container. Put explicit setup for **both** configured applications after Vessel's EF bundle in the one-shot migration step. An EF bundle alone does not provision Wolverine's full schema. For version upgrades, run setup and, if required, its full storage assertion before starting the applications. [Storage management][storage-docs].

`MapWolverineEnvelopeStorage` is supported, but its two envelope mappings call `ExcludeFromMigrations`. The sealed-context registration can add them through a model customizer; it does not transfer schema ownership to EF. Nodes, dead letters, functions and indexes remain additional Wolverine resources. The simplest choice for Vessel is its existing EF migrations for business tables plus explicit Wolverine setup, **not** hand-copying Wolverine's generated DDL into EF migrations. [Mapping source][mapping-source].

Use the same database but separate `wolverine_api` and `wolverine_worker` schemas, with distinct application/service names. Those are distinct Wolverine recovery clusters. Separate schemas are required for the requested isolation, not a universal Wolverine requirement: identical replicas of one application normally share a schema. A distinct `ServiceName` alone is not an envelope discriminator in [outgoing recovery SQL][outgoing-source]. The integer envelope `owner_id` is a Wolverine node identifier, not Vessel's owner GUID.

For the proven runtime option, include `WolverineFx.RuntimeCompilation` 6.50.0 and use dynamic generation. Core Wolverine 6 no longer carries the Roslyn compiler. Static generation is a documented alternative, but its `codegen write` and Release compilation pipeline still need their own test. [Codegen documentation][codegen]. No SDK installation in the final image is necessary for the dynamic option tested here.

The runtime image printed a nonfatal missing `libgssapi_krb5.so.2` warning. Password-authenticated PostgreSQL queries, setup and handlers succeeded. This does not prove Kerberos support; address that warning in packaging if that authentication is ever supported.

## Delivery and owner contract

Delivery is **at least once**. Broker confirmation is not consumer completion. Wolverine's sender awaits `BasicPublishAsync`; its confirmation switches default to false and must both be enabled. Its publisher sets persistent messages on durable endpoints, but sends with **mandatory=false**. RabbitMQ confirms unroutable messages too. Predeclare all intended durable queues and topic bindings before a producer can publish, and validate topology. Do not rely on a consumer's first startup to create the queue. [Sender source][sender-source], [confirmation defaults][confirm-source], [RabbitMQ confirms][rabbit-confirms].

Single-node quorum queues provide durable storage, not host/disk redundancy. The message-safety guarantee depends on surviving replicas; one node has only itself. [RabbitMQ quorum documentation][rabbit-quorum].

Wolverine inbox dedupe uses envelope identity, with configurable handled-message retention. The test did not enable PostgreSQL inbox partitioning, whose documented uniqueness is weaker. Vessel's stable EventId and per-consumer unique delivery claim remain necessary for new-envelope republishes and longer retention. The synthetic effect was a PostgreSQL increment in the same transaction as that claim. An external notification API is not part of that transaction; a crash between send and receipt recording can still repeat a notification unless the provider supports an idempotency key or Vessel accepts that limitation. [Idempotency documentation][idempotency].

The duplicate-envelope batch produced a temporary “Inbox database unavailable” listener pause before rejecting the duplicate. A following distinct event ran, so recovery was observed, but retain this case in regression tests and avoid treating that log alone as proof PostgreSQL is down.

The handler accepts only configured owners, opens a fresh DI scope, sets its scoped owner once, then resolves `VesselDbContext`. It verifies the account through its normal query filter and checks context identity again after an overlap delay. No HTTP request or mutable singleton supplies identity. Two synthetic owners were allowed only to test concurrency; the product worker should validate Vessel's configured single owner ID. This investigation does not propose unscoped account enumeration.

## Corrections to carry into the draft plan

- State the **outer commit then flush** requirement explicitly. The documented EF convenience API is unsafe inside Vessel's nested callbacks.
- Separate business EventId dedupe from inbox-envelope dedupe. Do not claim exactly-once external notifications.
- Missing storage fails fast in current 6.50.0, but full schema drift does not. Explicit setup and upgrade validation are still needed.
- Envelope mapping is not EF migration ownership. Keep explicit Wolverine schema setup in the one-shot migration path.
- Queue/binding existence is part of loss prevention. Quorum plus publisher confirms alone does not cover an unbound topic. (Since October 15, subscriber catch-up from PostgreSQL covers that case; topology is still provisioned explicitly.)
- Database backup consistency and business republishing remain useful plan directions, but restoration after broker-volume loss and event replay/retention were **not** proved by W0. (Since October 15, recovery is subscriber-side catch-up rather than republishing.)

## Reproduction and scope

The isolated executable is `spikes/wolverine/W0.csproj`. It references the real Persistence project but adds no product packages or locks. `Program.cs` contains the complete fixtures, two host configurations and test modes. Selected original outputs, resolved packages, source identity and image digests are in `spikes/wolverine/evidence.txt`.

Start **new disposable resources only**. These names and ports must be unused. The database defaults in the executable are exclusively the following throwaway connection, not repository `.env` or Compose configuration.

```sh
docker network create vessel-w0-sol-net
docker run -d --name vessel-w0-sol-pg --network vessel-w0-sol-net \
  -p 127.0.0.1:55489:5432 \
  -e POSTGRES_DB=w0 -e POSTGRES_USER=w0 -e POSTGRES_PASSWORD=w0-disposable-only \
  postgres@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873
docker run -d --name vessel-w0-sol-rabbit --network vessel-w0-sol-net \
  -p 127.0.0.1:56789:5672 -p 127.0.0.1:15689:15672 \
  -e RABBITMQ_DEFAULT_USER=w0 -e RABBITMQ_DEFAULT_PASS=w0-disposable-only \
  rabbitmq@sha256:8dd6e3570ddaa2ef82a6c3a8950e79c1f73893adfbdddbc374fc1f1ac8a0f5dd
docker exec vessel-w0-sol-pg pg_isready -U w0 -d w0
docker exec vessel-w0-sol-rabbit rabbitmq-diagnostics -q ping

dotnet restore spikes/wolverine/W0.csproj
dotnet build spikes/wolverine/W0.csproj --no-restore
# Run once on this disposable DB; missing is an expected startup rejection.
dotnet spikes/wolverine/bin/Debug/net10.0/W0.dll missing
dotnet spikes/wolverine/bin/Debug/net10.0/W0.dll setup
```

Wait for both services' readiness commands to succeed. In a separate terminal, run `dotnet spikes/wolverine/bin/Debug/net10.0/W0.dll worker`. Wait for `WORKER_READY`, then run modes `tx`, `bad-helper`, `early-flush`, `duplicates`, `owners`, `owner-stress` and `unrouted`. `bad-helper` and `early-flush` deliberately demonstrate unsafe integration. For mapped envelopes run `W0_MAP=1 ... map-model` and `W0_MAP=1 ... tx`.

For crash recovery, keep only the worker alive, run `... crash`, expect exit **73**, and query `w0_api.wolverine_outgoing_envelopes`. It remained at one after eight seconds. Run `... recover`; that restarted API host recovered it. Spike-only dead-node timeouts are shortened to seconds; do not copy them into production.

For outage recovery, stop the recovery host. Start `... outage`, wait for `OUTAGE_READY`, stop **only** `vessel-w0-sol-rabbit`, then press Enter. The command commits and attempts flush with the broker absent. Query the outgoing envelope and matching delivery before restarting the broker. Then restart the broker and `... recover`; verify delivery and zero remaining outgoing envelopes.

For same-envelope replay, stop worker, run `... runtime` to queue a new message, then `... envelope-replay`. It republishes the queued message twice with the same envelope MessageId. Restart worker and verify one incoming row/handler invocation. The separate `duplicates` mode tests a stable business EventId across **different** envelopes.

Inspect broker topology with `rabbitmqctl -q list_queues name type durable consumers messages_ready` and its management API's `/api/channels`. Compare `w0_deliveries`, `w0_effects`, `w0_rejections` and envelope tables using `docker exec vessel-w0-sol-pg psql -U w0 -d w0`. Handler output is asynchronous; wait for its EventId before declaring a delivery result.

Runtime reproduction, with the local worker stopped:

```sh
dotnet publish spikes/wolverine/W0.csproj -c Release --no-restore -o /tmp/vessel-w0-sol-publish
docker run --rm --name vessel-w0-sol-runtime --network vessel-w0-sol-net \
  --user app --workdir /app \
  --mount type=bind,source=/tmp/vessel-w0-sol-publish,target=/app,readonly \
  -e 'W0_CS=Host=vessel-w0-sol-pg;Database=w0;Username=w0;Password=w0-disposable-only' \
  -e 'W0_AMQP=amqp://w0:w0-disposable-only@vessel-w0-sol-rabbit:5672' \
  mcr.microsoft.com/dotnet/aspnet@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b \
  dotnet W0.dll worker
# In another terminal:
dotnet spikes/wolverine/bin/Debug/net10.0/W0.dll runtime
docker exec vessel-w0-sol-runtime dotnet --list-sdks
docker exec vessel-w0-sol-runtime dotnet --list-runtimes
```

Use `setup` instead of `worker` to reproduce explicit schema setup in that runtime image. NuGet transitive versions are recorded in the evidence file; a future restore can change unpinned transitives, so compare the graph before treating it as the same experiment.

After stopping only the spike's host processes and runtime container, remove only the containers and network created above:

```sh
docker rm -fv vessel-w0-sol-pg vessel-w0-sol-rabbit
docker network rm vessel-w0-sol-net
```

The investigation removed its containers, their newly created anonymous volumes and its network. No existing database, Compose service, secret file, environment file or existing spike image was used. Existing parent edits to `AGENTS.md` and the monitor plan were left alone. No commit, push or PR was made.

[nuget-index]: https://api.nuget.org/v3-flatcontainer/wolverinefx/index.json
[nuget-registration]: https://api.nuget.org/v3/registration5-semver1/wolverinefx/6.50.0.json
[nuget-core]: https://api.nuget.org/v3-flatcontainer/wolverinefx/6.50.0/wolverinefx.nuspec
[nuget-ef]: https://api.nuget.org/v3-flatcontainer/wolverinefx.entityframeworkcore/6.50.0/wolverinefx.entityframeworkcore.nuspec
[nuget-pg]: https://api.nuget.org/v3-flatcontainer/wolverinefx.postgresql/6.50.0/wolverinefx.postgresql.nuspec
[nuget-rabbit]: https://api.nuget.org/v3-flatcontainer/wolverinefx.rabbitmq/6.50.0/wolverinefx.rabbitmq.nuspec
[nuget-compiler]: https://api.nuget.org/v3-flatcontainer/wolverinefx.runtimecompilation/6.50.0/wolverinefx.runtimecompilation.nuspec
[license]: https://github.com/JasperFx/wolverine/blob/55a46250e9ffd99badab1ae032d334b73e2cbe56/LICENSE
[outbox-source]: https://github.com/JasperFx/wolverine/blob/55a46250e9ffd99badab1ae032d334b73e2cbe56/src/Persistence/Wolverine.EntityFrameworkCore/DbContextOutbox.cs
[ef-transaction]: https://github.com/JasperFx/wolverine/blob/55a46250e9ffd99badab1ae032d334b73e2cbe56/src/Persistence/Wolverine.EntityFrameworkCore/Internals/EfCoreEnvelopeTransaction.cs
[startup-source]: https://github.com/JasperFx/wolverine/blob/55a46250e9ffd99badab1ae032d334b73e2cbe56/src/Wolverine/Runtime/WolverineRuntime.HostService.cs
[storage-source]: https://github.com/JasperFx/wolverine/blob/55a46250e9ffd99badab1ae032d334b73e2cbe56/src/Persistence/Wolverine.RDBMS/MessageDatabase.Admin.cs
[storage-docs]: https://wolverinefx.net/guide/durability/managing
[mapping-source]: https://github.com/JasperFx/wolverine/blob/55a46250e9ffd99badab1ae032d334b73e2cbe56/src/Persistence/Wolverine.EntityFrameworkCore/WolverineEntityCoreExtensions.cs
[outgoing-source]: https://github.com/JasperFx/wolverine/blob/55a46250e9ffd99badab1ae032d334b73e2cbe56/src/Persistence/Wolverine.RDBMS/MessageDatabase.Outgoing.cs
[codegen]: https://wolverinefx.net/guide/codegen
[sender-source]: https://github.com/JasperFx/wolverine/blob/55a46250e9ffd99badab1ae032d334b73e2cbe56/src/Transports/RabbitMQ/Wolverine.RabbitMQ/Internal/RabbitMqSender.cs
[confirm-source]: https://github.com/JasperFx/wolverine/blob/55a46250e9ffd99badab1ae032d334b73e2cbe56/src/Transports/RabbitMQ/Wolverine.RabbitMQ/WolverineRabbitMqChannelOptions.cs
[rabbit-confirms]: https://www.rabbitmq.com/docs/confirms
[rabbit-quorum]: https://www.rabbitmq.com/docs/quorum-queues
[idempotency]: https://wolverinefx.net/guide/durability/idempotency
