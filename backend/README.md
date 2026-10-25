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
dotnet test backend/Vessel.slnx
```

One xUnit project, `tests/Vessel.Tests`, holds rule, adapter, API (`WebApplicationFactory` with fake venues) and PostgreSQL/broker tests. External services are opt-in by environment variable; without them those tests are reported as skipped with the variable named:

| Variable | Enables |
| --- | --- |
| `Vessel_TEST_POSTGRES` | PostgreSQL tests. A real, disposable database where the user can `CREATE SCHEMA`. Each test migrates its own random `vessel_test_<uuid>` schema and drops it afterwards; the database and other schemas are never touched. |
| `Vessel_TEST_EVENT_RABBIT` | RabbitMQ transport, worker and end-to-end tests (also needs `Vessel_TEST_POSTGRES`). A disposable `amqp://` broker; each test uses its own topology prefix. |
| `VESSEL_TEST_S3_ENDPOINT`, `VESSEL_TEST_S3_ACCESS_KEY`, `VESSEL_TEST_S3_SECRET_KEY` | S3-compatible evidence store tests. |
| `Vessel_TEST_RISEX_ADDRESS`, `Vessel_TEST_LIGHTER_PUBLIC=1` | Live public venue reads. |

CI sets the first two (see `.github/workflows/checks.yml`); `pnpm check` runs the full repository check as CI does. Never point these at the owner's database or broker.

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
