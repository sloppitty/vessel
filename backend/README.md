# Vessel backend

ASP.NET Core 10 API and background worker over PostgreSQL, with RabbitMQ (through Wolverine) as event and command transport. This file covers running, testing, migrating and configuring. For structure, modules, flows and conventions read the [backend guide](../docs/architecture/backend-guide.md); for events, subscriptions, the worker and its operations read [messaging and workers](../docs/architecture/messaging-and-workers.md).

## Running

From the repository root, the usual development path is the scripts in `package.json` (see [development setup](../docs/development.md)):

```sh
pnpm db:migrate    # start PostgreSQL and RabbitMQ, apply migrations, provision messaging
pnpm dev:api       # API on http://127.0.0.1:5080
pnpm dev:worker    # worker running the monitor module
```

Directly with the .NET CLI:

```sh
dotnet restore backend/Vessel.slnx --locked-mode
dotnet build backend/Vessel.slnx --no-restore
dotnet run --project backend/src/Vessel.Api       # launch profile: Development, http://127.0.0.1:5080
dotnet run --project backend/src/Vessel.Worker
```

The API and worker need `ConnectionStrings__Vessel`, `Vessel__Messaging__RabbitMqUri` and at least one `Vessel__Identity__Origins__<n>`. Sign-in is by Ethereum wallet only: `identity ensure-owner` and `identity bind-wallet` (offline commands of the API binary, run by `pnpm db:migrate` and `pnpm dev:identity`) give the configured owner a wallet. The database connection is resolved lazily, so `GET /health/live` answers without one. Outside Development the API refuses to start without a connection string, a non-default owner GUID and `https://` origins; `Vessel__Auth__Token`, or a `Vessel__Auth__Mode` other than `Wallet`, refuses startup everywhere. Development OpenAPI is at `/openapi/v1.json` behind the same session. Production packaging, TLS and proxies are in [deploy/README.md](../deploy/README.md).

## Testing

```sh
pnpm test:backend                         # all backend tests
pnpm test:unit                            # value-only rules and calculators
pnpm test:usecases                        # Application services with in-memory fakes
pnpm test:venues                          # venue adapters, recorded payloads and caches
pnpm test:api                             # in-process HTTP hosts, no external services
pnpm test:integration                     # infrastructure, plus opt-in service tests
```

All projects live under `backend/tests/` and belong to `backend/Vessel.slnx`.
The scripts above call `dotnet test` on the corresponding project. The solution
command remains `dotnet test backend/Vessel.slnx`; append `-c Release` to select
Release, or use `--filter` to narrow a run further.

| Project | What belongs here |
| --- | --- |
| `Vessel.Unit.Tests` | Domain types and pure Application rules/calculators over values. No I/O ports, fake stores, unit of work, host, database or HTTP adapters. Architecture and ownership-rule source checks also live here. References only Domain and Application. |
| `Vessel.UseCases.Tests` | Application services/use cases over in-memory stores, fake venues, fake clocks and fake units of work. No web host, real venue HTTP adapters or external services. |
| `Vessel.Venues.Tests` | Venue adapters and venue-common code, recorded JSON, fake HTTP/WebSocket transports, registry, catalogue/candle caches and market streams. Live venue reads stay opt-in. |
| `Vessel.Api.Tests` | In-process `WebApplicationFactory`/`TestServer` HTTP tests with no external service, including contracts, auth, transport, compression, SSE and diagnostics. |
| `Vessel.Integration.Tests` | PostgreSQL, RabbitMQ and S3 tests, including API/worker tests over PostgreSQL, isolation, multi-owner and migrations. Also non-venue infrastructure without external services, such as filesystem evidence, persistence/messaging helpers, credentials, identity and host composition. |
| `Vessel.TestKit` | A non-test class library for support needed by more than one project, environment-gated attributes and shared fixtures. Single-project helpers stay beside their users. Unit links the value-only support sources rather than referencing the hosting/infrastructure support assembly. |

Choose by the heaviest dependency: external service → Integration; web host →
Api; real venue adapter → Venues; Application service with fakes → UseCases;
otherwise Unit. Split mixed files method by method. Keep the existing namespaces
and method names; give secondary classes a descriptive suffix. End-to-end tests
remain the full-stack and browser scripts, not a .NET project.

Integration needs explicit `Vessel_TEST_*` service settings for its service-gated
tests. Without them those tests skip, naming the missing variable; its
service-free infrastructure tests still run. S3 uses the uppercase
`VESSEL_TEST_S3_*` names below. These switches and their skip behaviour are
unchanged:

| Variable | Enables |
| --- | --- |
| `Vessel_TEST_POSTGRES` | PostgreSQL tests. A real, disposable database where the user can `CREATE SCHEMA`. Each test migrates its own random `vessel_test_<uuid>` schema and drops it afterwards; the database and other schemas are never touched. |
| `Vessel_TEST_EVENT_RABBIT` | RabbitMQ transport, worker and end-to-end tests (also needs `Vessel_TEST_POSTGRES`). A disposable `amqp://` broker; each test uses its own topology prefix. |
| `VESSEL_TEST_S3_ENDPOINT`, `VESSEL_TEST_S3_ACCESS_KEY`, `VESSEL_TEST_S3_SECRET_KEY` | S3-compatible evidence store tests. |
| `Vessel_TEST_RISEX_ADDRESS`, `Vessel_TEST_LIGHTER_PUBLIC=1` | Live public venue reads. |

For example, with throwaway PostgreSQL and RabbitMQ containers published on
non-default loopback ports:

```sh
Vessel_TEST_POSTGRES='Host=127.0.0.1;Port=25432;Database=vessel_tests;Username=vessel_tests;Password=disposable-only' \
Vessel_TEST_EVENT_RABBIT='amqp://vessel_tests:disposable-only@127.0.0.1:25672' \
pnpm test:integration
```

Release CI sets the first two, see `.github/workflows/checks.yml`. `pnpm check`
still builds and tests the whole solution. Shared fixtures live in
`Vessel.TestKit/Fixtures`; project references copy them, including the linked
original evidence JPEG, into consumers' output directories at the same
`Fixtures/...` paths. Never point tests at the owner's database, broker or
object store, and never load the repository's `.env` for test runs.

## Migrating

Schema changes and messaging topology are applied explicitly; the API and worker never migrate at startup. With `ConnectionStrings__Vessel` (and the broker settings for provisioning) set, from `backend/`:

```sh
dotnet tool restore
dotnet ef database update --project src/Vessel.Persistence
dotnet run --project src/Vessel.Api --no-launch-profile -- provision-messaging
```

`pnpm db:migrate` runs the same steps against the development services. The design-time factory reads the connection variable directly and does not start the API. To inspect SQL without a database, `dotnet ef migrations script --idempotent --project src/Vessel.Persistence`; to add a change, `dotnet ef migrations add ChangeName --project src/Vessel.Persistence --output-dir Migrations`. In packaged images, `/app/migrate` runs both steps (see [deploy/README.md](../deploy/README.md)).

The API binary also carries offline commands: the messaging commands (`provision-messaging`, `rotate-event-epoch`, `deregister-event-subscription`, `retry-event-receipts`; see [messaging and workers](../docs/architecture/messaging-and-workers.md)) and the evidence copies (`evidence-copy-to-s3 --source /absolute/root [--apply]`, `evidence-copy-to-local --destination /absolute/root [--apply]`, dry-run by default; see [evidence storage](../docs/architecture/evidence-storage.md)).

## Configuring

Secrets go in the process environment or an ignored local file, never in tracked files or the frontend build. Non-secret defaults are in `src/Vessel.Api/appsettings.json`.

| Setting | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Vessel` | none | PostgreSQL connection for the API, worker, migrations and offline commands. |
| `Vessel__Auth__OwnerId` | `11111111-1111-1111-1111-111111111111` (Development only) | The installation owner: `identity ensure-owner` creates the operator user with this ID, so a journal from before wallet sign-in keeps its owner. Required outside Development, where the default GUID needs `Vessel__Identity__AllowLegacyDefaultOwner=true`. Every host uses the same setting. |
| `Vessel__Auth__OwnerName` | `Owner` | Display name `ensure-owner` gives that user. |
| `Vessel__Identity__Origins__<n>` | none | Exact page origins allowed to sign in and write; `https://` outside Development. Other `Vessel__Identity__*` settings (registration policy, session lifetimes, cookie names) are in [deploy/README.md](../deploy/README.md#identity-settings). |
| `Vessel__Credentials__ActiveKeyId`, `Vessel__Credentials__Keys__<id>` or `Vessel__Credentials__KeyFile` | none | Key ring for encrypted venue tokens; see [venue credentials](../docs/architecture/venue-credentials.md). |
| `Vessel__ForwardedHeaders__KnownProxies__<n>`, `__KnownNetworks__<n>`, `__ForwardLimit` | loopback only | Trusted reverse proxies; explicit settings replace the loopback default. |
| `Vessel__Venues__<Venue>__BaseUrl` (and `Hyperliquid__StreamUrl`) | production hosts | Venue hosts for `Hyperliquid`, `Risex`, `Lighter`, `LighterRh`. |
| `Vessel__Messaging__RabbitMqUri` | none | `amqp://` or `amqps://` broker; required by the API and worker. Other `Vessel__Messaging__*` settings (`Prefix`, `Deployments`, `StorageSchemas`, `DeliveryLimit`) are described in messaging and workers. |
| `Vessel__Worker__Modules__<n>`, `Vessel__Worker__Deployment` | `["monitor"]`, first module | Modules a worker runs and its logical deployment. |
| `Vessel__Monitor__Interval`, `__ManualCooldown`, `__ProtectionGrace`, … | 10 min, 60 s, 10 min | Monitor timing; the full list is in messaging and workers. |
| `Vessel__Events__RetentionDays` | `90` | Event retention. |

Evidence storage (images attached to saved Plays):

| Setting | Default | Meaning |
| --- | --- | --- |
| `Vessel__Evidence__Storage__Provider` | `Local` | `Local` unless explicitly `S3`. Unknown providers fail startup; S3 outages never fall back to local. |
| `Vessel__Evidence__Storage__Local__RootPath` | `data/evidence` | Absolute, or relative to the API content root. |
| `Vessel__Evidence__Storage__S3__Endpoint` | `http://localhost:9000` | Server-side S3 origin; HTTPS outside a private network. |
| `Vessel__Evidence__Storage__S3__Bucket` | `vessel-evidence` | Private, pre-created bucket. |
| `Vessel__Evidence__Storage__S3__Region` | `us-east-1` | Signing region. |
| `Vessel__Evidence__Storage__S3__ForcePathStyle` | `true` | Path-style addressing (RustFS). |
| `Vessel__Evidence__Storage__S3__AccessKeyId`, `__SecretAccessKey` | none | Required with `S3`; server-only. |
| `Vessel__Evidence__MaxUploadBytes` | `10485760` | Largest accepted image. |
| `Vessel__Evidence__MaxPerPlay` | `50` | Images per Play. |

Back up the evidence store with the database; a database backup alone does not preserve images.
