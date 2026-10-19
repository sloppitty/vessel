# Application development

The application lives in `frontend/` and `backend/`. Overview, portfolio/account management, saved Plays, review and activity use the protected API. The disposable design prototype remains separate in `prototype/`.

## Tooling

- Node 24, selected by `.nvmrc`, with the engine constraint in root `package.json`.
- pnpm **12.8.1**, pinned in root `package.json`.
- .NET SDK **10.0.302**, pinned in `global.json` with patch roll-forward.
- Docker with Compose for local PostgreSQL and RabbitMQ.

Evidence storage keeps the backward-compatible Local default at `data/evidence`
relative to the API content root; no storage configuration or credentials are
needed for local development. To opt into S3 (RustFS recommended), uncomment
`Vessel__Evidence__Storage__Provider=S3` and the server-only endpoint/bucket/credential
settings in the ignored root `.env`, and provision a private bucket first.
`scripts/dev.py` passes these settings to the API, never Vite. Existing images
are **not** moved automatically; see the offline copy-and-verify command in
[storage/cutover](architecture/evidence-storage.md)
and [RustFS deployment](../deploy/README.md).

Always use pnpm for JavaScript/TypeScript work. There is one workspace and one root `pnpm-lock.yaml`, not npm or yarn lockfiles. The npm registry may still be the upstream package source; that does not mean npm is the package manager.

If Corepack is available:

```sh
corepack enable pnpm
corepack install
pnpm --version
pnpm install --frozen-lockfile
```

For a user-writable shim directory, as used on this NixOS host:

```sh
corepack enable pnpm --install-directory "$HOME/.local/bin"
corepack prepare pnpm@12.8.1 --activate
```

The tool was installed locally through Corepack during setup. Use a current Corepack or the official pnpm installation method when the selected Node distribution does not include Corepack; do not silently substitute another package manager. The root `pnpm-workspace.yaml` explicitly allows the already-reviewed pinned packages released within pnpm's default age window; there is no blanket release-age exemption.

## Release images

Releases are published by `.github/workflows/release.yml`. Merge a release pull
request (versions, a dated changelog section and `docs/releases/vX.Y.Z.md`),
then push an annotated tag on `main`:

```sh
git tag -a v0.3.1 -m "Vessel v0.3.1" origin/main
git push origin v0.3.1
```

The workflow checks that the tag, packaged versions and notes agree
(`python3 scripts/release.py check --tag v0.3.1`), runs the foundation checks and
`scripts/verify-release.py` for S3 and local storage, then builds and pushes
`skorcius/vessel:api-0.3.1` and `web-0.3.1`, moves `api-latest` and `web-latest`
only after both are verified, and creates the GitHub Release from the notes. It
never overwrites a version published from another commit. It needs the
`DOCKERHUB_TOKEN` secret on the `docker-hub` environment; see the
[`vessel-release` skill](../.agents/skills/vessel-release/SKILL.md).

CI runs only for release tags to conserve runner minutes. Branch pushes and
pull requests do not run foundation checks, packaging verification or a publish
rehearsal; run the documented local checks before merging. Foundation checks are
reused once by Release. A manual Release rerun must select a `vX.Y.Z` tag;
dispatching from a branch skips all jobs before allocating a runner. Existing
branches must incorporate this workflow change before their pushes stop running
the older branch-triggered configuration.

### Publishing from the organization fork

The active release repository is `sloppitty/vessel`; the Docker Hub destination
remains `skorcius/vessel`. A fork needs its own GitHub Actions configuration:

- Enable Actions and the Release/Foundation checks workflows in the fork.
- Grant the Blacksmith GitHub app access to the organization repository. Both
  workflows use `blacksmith-4vcpu-ubuntu-2404` runners.
- Create the `docker-hub` environment with a deployment policy allowing only
  tags matching `v*` (no branch policy). Add its `DOCKERHUB_TOKEN` Actions secret
  privately, with Docker Hub Read & Write access. Set `DOCKERHUB_USERNAME` to
  `skorcius`, or leave it unset to use that default. Secrets from the parent
  repository are not copied to the fork.
- Keep workflow `contents: write` permission on the GitHub Release job; its
  repository-scoped `GITHUB_TOKEN` creates the release without a separate PAT.

To enter the Docker token privately from your own terminal:

```sh
gh secret set DOCKERHUB_TOKEN --repo sloppitty/vessel --env docker-hub
```

After merging the prepared release, push its annotated tag. If a tag was pushed
before Actions was enabled, dispatch Release explicitly **from that tag**. Do
not dispatch an older tag whose packaged version or release notes do not match.
An existing tag is never moved without the owner's explicit direction, and a
published versioned image from another revision is never overwritten. Confirm
the Release run, both image digests and the GitHub Release before calling it
published.

Linux amd64 images can still be built locally from the repository root:

```sh
REVISION=$(git rev-parse HEAD)
docker build --platform linux/amd64 -f backend/Dockerfile \
  --build-arg VERSION=0.3.1 --build-arg REVISION="$REVISION" \
  -t skorcius/vessel:api-0.3.1 .
docker build --platform linux/amd64 -f frontend/Dockerfile \
  --build-arg VERSION=0.3.1 --build-arg REVISION="$REVISION" \
  -t skorcius/vessel:web-0.3.1 .
```

The `api-latest` and `web-latest` tags move with releases; deploy fixed versions
for predictable upgrades. Builds do not need secrets or a live database.
The API image includes `/app/migrate`; applying migrations remains an explicit
one-shot deployment step, never API startup behaviour.
See [deployment setup and verification](../deploy/README.md). Default nginx
ignores incoming forwarded claims; the API trusts only the pinned web peer.
The opt-in Caddy TLS overlay unpublishes web and trusts only Caddy's exact IP.
Host-gateway trust is a weaker explicit alternative: any local process can forge
scheme/client headers. The production credential guard is never disabled.
`python3 scripts/verify-release.py` builds isolated throwaway images, checks both
transport modes and restores synthetic backups into fresh volumes. Its optional
`--browser-module` check verifies the built SPA's CSP without real venue calls.

## PostgreSQL and RabbitMQ

Copy `.env.example` to the ignored root `.env`, set private database and broker passwords, a random API token and a nonempty owner GUID, and keep the file private. The development runner supports KEY=value entries and optional matching quotes, not shell commands. Then:

```sh
pnpm db:up
pnpm broker:up
```

The development service binds **only `127.0.0.1:55432`**. It uses PostgreSQL 18 and a named volume. These database credentials and privileges are for local development, not a production security configuration.

`pnpm dev`, `pnpm dev:api`, `pnpm dev:worker` and `pnpm db:migrate` start both services with `docker compose up --wait` first (set `Vessel_DEV_SKIP_SERVICES=1` to manage them yourself), and stop early when the broker credentials are missing, because the API and worker cannot start without the broker. `pnpm dev` runs the API, two workers built once (the `monitor` module, and the `notifications` module in its own deployment with the heartbeat file `/tmp/vessel-dev-notifications-heartbeat`) and the frontend; `pnpm dev:worker` runs both workers; `pnpm dev:api` runs the API alone, so the monitor and notifications show as offline.

RabbitMQ binds AMQP to `127.0.0.1:55672` and management to `127.0.0.1:55673`. Its credentials stay in the API/worker environment, never Vite. The development runner constructs the connection from `RABBITMQ_USER` and `RABBITMQ_PASSWORD`; an explicit `Vessel__Messaging__RabbitMqUri` overrides it. The production Compose file does not publish AMQP.

Set the backend connection in your process environment or secret manager:

```text
ConnectionStrings__Vessel=Host=127.0.0.1;Port=55432;Database=vessel;Username=vessel;Password=YOUR_LOCAL_PASSWORD
```

Do not commit the populated value. Migrations are deliberate:

```sh
pnpm db:migrate
```

Neither API nor worker migrates or provisions at startup. `pnpm db:migrate` starts both local services if needed, applies EF migrations, then runs the API binary's explicit `provision-messaging` command: envelope storage and the schema registration of every deployment (`api`, `monitor` and `notifications`), the broker topology of every module and insert-only subscription registrations. A host whose schema was provisioned for other modules refuses to start. The active-Play account/instrument index remains non-unique, with composite owner constraints.

## API and token

Configure `Vessel__Auth__Token` privately before connecting. There is no default production token. It is read by the API, not Vite. Never create a `VITE_*` token variable.

Set `Vessel__Auth__OwnerId` to the same nonempty GUID for API and worker. `Vessel__Auth__OwnerName` is optional; neither value is a venue credential.

Run:

```sh
pnpm dev:api
```

The development launch profile binds `http://127.0.0.1:5080`.

- `GET /health/live` is anonymous liveness and does not prove database readiness.
- `GET /api/system` requires `Authorization: Bearer …` and reports foundation metadata, including perps scope and planned/manual venue statuses.
- Missing/wrong tokens fail closed. New unannotated endpoints have an authenticated fallback policy.
- Development OpenAPI is also protected.

Lighter read-only tokens are separate from the Vessel API token. Configure a server-only encryption key ring before saving them; public account discovery and reads work without one. The default setup uses `scripts/credential_keys.py init` to create a private file without printing its key. Set only `Vessel__Credentials__KeyFile` in `.env`. The optional `fetch-infisical` command can provision the same file through an authenticated Infisical CLI later, without changing the API or existing ciphertext. Neither command runs automatically. See [credential setup, HTTPS and rotation](architecture/venue-credentials.md#operator-setup) and the [implemented import workflow](architecture/lighter-state.md). The Vite development build permits token entry on LAN HTTP when the API also runs in Development, and displays a plaintext-transport warning. Use only a trusted test network; HTTPS is still preferable. Production builds and non-Development APIs retain the HTTPS guard outside loopback.

Metadata and liveness can run without database connectivity. Protected endpoints provide accounts, portfolios, saved Plays, review, snapshots and retained executions. The separate worker checks active Plays and potentially resting linked exits at the configured interval. Manual Refresh and Check share account admission limits. Venue reads remain bounded, not a full-history backfill.

## Frontend

In a second terminal:

```sh
pnpm dev:web
```

Or start API, worker and frontend with `pnpm dev`. The runner reads private server values from root `.env` and does not pass them to the frontend. `pnpm dev:api` and `pnpm dev:worker` start the individual server processes. Setup remains explicit; neither server migrates implicitly. Use Ctrl+C to stop the runner while retaining PostgreSQL and RabbitMQ.

Open `http://127.0.0.1:5180`. The Vite development proxy forwards `/api` and `/health` to the API. `VESSEL_API_TARGET` can override the server-side proxy target; it is not a token or a browser configuration secret.

Enter the configured backend token at runtime. The field clears after connection; the API-client closure keeps the token only in session memory for protected reads/writes, until disconnect. Nothing stores it in browser storage, a URL or the build.

The default page uses actual core API records, with honest loading/empty/error states. Manual values may be unavailable. Venue refresh covers supported perpetual state and bounded recent executions. Imported fills carry no invented thesis, FX adjustment or complete-lifetime claim. Plays can be saved with revisions and lifecycle status, link imported orders and fills, render venue candle charts with drawings and indicators, and keep evidence images; unsaved drafts stay in browser memory. The browser reads committed monitor events and stored state rather than polling venues. Notification delivery remains later work. See [the monitor plan](architecture/play-monitor-plan.md), [the Plays contract](architecture/plays-workspace-contract.md), [Play lifecycle](architecture/play-lifecycle.md) and [portfolio review](architecture/portfolio-review.md).

These HTTP addresses are loopback development endpoints. A real self-hosted deployment needs HTTPS and deliberate proxy/authentication policy.

Outside `Development`, the API refuses to start unless `Vessel__Auth__Token` is a generated secret of at least 32 characters, `ConnectionStrings__Vessel` is set and any `Vessel__Auth__OwnerId` is a GUID. `/health/live` reports the process; `/health/ready` (anonymous, no details) returns 200 only when the database answers and every migration is applied. After 20 wrong tokens within 10 minutes, further wrong tokens from that client address receive 429 with `Retry-After` until the window ends; the right token keeps working so a shared proxy address cannot lock the owner out. Only loopback proxies may set `X-Forwarded-For`/`X-Forwarded-Proto`; put the TLS-terminating proxy on the same host or configure trusted proxies before relying on them. API responses carry `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer` and `Cache-Control: no-store` unless an endpoint sets its own policy. The existing LAN prototype at `10.1.0.219:5173` remains separate; the scaffold does not automatically open firewall ports.

## Current LAN review

October 1, 2026: the owner requested the actual frontend/backend preview on the LAN and explicitly requested a development-only authentication token.

- Application URL: **http://10.1.0.219:5180/**
- Frontend listener: `10.1.0.219:5180`, not all interfaces.
- API listener remains `127.0.0.1:5080`; the frontend's same-origin `/api` and `/health` proxy reaches it.
- PostgreSQL remains loopback-only at `127.0.0.1:55432`.
- User service: `vessel-lan-preview.service`.
- A subnet-specific NixOS firewall rule allows TCP 5180 on `ens18` from `10.1.0.0/24` to `10.1.0.219`.

The separate preview token override lives in ignored `.env.lan-preview`, mode 600. It is used only by the API process, not compiled into the frontend or copied into documentation. The original `.env` token and database password are unchanged and were not disclosed. The owner received the preview token explicitly for development access; future secret disclosure is not authorized by that exception.

The preview is HTTP development access, not a production deployment. Anyone given the token can use this single-owner development workspace. Do not reuse the preview token for production or expose this service to the internet.

```sh
systemctl --user status vessel-lan-preview.service
systemctl --user restart vessel-lan-preview.service
systemctl --user stop vessel-lan-preview.service
```

The service is transient and is not configured to return after a host reboot. The firewall rule also disappears after a firewall reload/reboot. Stopping the service does not delete the database volume.

To recreate the service on this machine after it is unloaded, retain the private preview environment file containing the token override, `Vessel_DEV_HOST=10.1.0.219`, and a `PATH` containing the user-installed pnpm shim:

```sh
systemd-run --user --unit=vessel-lan-preview \
  --description='Vessel actual app LAN development preview' \
  --property=WorkingDirectory=/home/ops/projects/vessel \
  --property=EnvironmentFile=/home/ops/projects/vessel/.env.lan-preview \
  --property=Restart=on-failure --property=RestartSec=3 \
  --property=TimeoutStopSec=35 \
  -- "$(command -v python3)" /home/ops/projects/vessel/scripts/dev.py lan
```

`pnpm dev:lan` uses the same runner but requires an explicit private `Vessel_DEV_HOST` in its process environment. The default `pnpm dev` remains loopback-only. A systemd user manager may not inherit the interactive shell's `~/.local/bin`; without the preview file's explicit `PATH`, pnpm is not found.

Check or add only this rule:

```sh
sudo iptables -C nixos-fw \
  -i ens18 -s 10.1.0.0/24 -d 10.1.0.219/32 -p tcp --dport 5180 \
  -m comment --comment vessel-app-lan-preview -j nixos-fw-accept ||
sudo iptables -I nixos-fw 1 \
  -i ens18 -s 10.1.0.0/24 -d 10.1.0.219/32 -p tcp --dport 5180 \
  -m comment --comment vessel-app-lan-preview -j nixos-fw-accept
```

To remove it, use the same match with `iptables -D nixos-fw`. This does not change the separate prototype rule on port 5173.

Verification included actual LAN page/liveness HTTP 200, missing/wrong bearer API 401, authenticated system/overview with mounted database 200, and a real T3 browser login into the Overview without synthetic responses. No wallet refresh or order request was made.

### October 5 SL/TP frontend comparison preview

The original `10.1.0.219:5180` frontend still served an October 4 revision when checked on October 5. The current agent host (`10.1.0.230`) has no authorized SSH access to update that service.

An additional transient user service, `vessel-sl-tp-preview.service`, serves the latest-main frontend plus the SL/TP display correction at **`http://10.1.0.230:5180/`** from the `t3code-dcce987b` worktree. Its server-only `VESSEL_API_TARGET=http://10.1.0.219:5180` forwards protected API requests through the existing preview. Connect with the existing API token; no token, database configuration or private environment was copied. Stop it with `systemctl --user stop vessel-sl-tp-preview.service`.

This is a frontend comparison preview, **not an upgrade of the original backend**. Newer API capabilities such as sizing suggestions still require updating and migrating the original backend with authorized access. The database, original service and firewall were left unchanged. SL/TP controls were browser-verified separately with a disposable, clearly labeled fixture that made no account/API requests.

## Checks

```sh
pnpm check
```

That runs the existing prototype tests, locked NuGet restore, backend Release build/tests, frozen pnpm install, TypeScript checks, lint, frontend tests and production build.

Set `Vessel_TEST_POSTGRES` to a real PostgreSQL test connection to execute database integration tests. Without it, 40 PostgreSQL tests explicitly skip; an invalid supplied connection fails. Tests migrate unique temporary schemas and drop only those schemas, not the shared database. The CI definition supplies a separate ephemeral PostgreSQL service.

Useful focused commands:

```sh
pnpm --filter vessel-frontend typecheck
pnpm --filter vessel-frontend lint
pnpm --filter vessel-frontend test
pnpm --filter vessel-frontend build
dotnet test backend/Vessel.slnx --configuration Release
```

CI is defined in `.github/workflows/checks.yml`. Local execution does not establish that a remote GitHub run succeeded; no push is implied.

Hyperliquid account detail now has supported stablecoin total/held/available wallet rows, separate from its primary perpetual snapshot. Use Refresh account to populate old snapshots. See [stablecoin wallet limits](architecture/stablecoin-wallet.md).

## Later work

Perpetuals-first and simultaneous active plays are confirmed. The core reader imports recent primary-DEX fills only. Complete backfills, source corrections, order/fill allocation, full Play lifecycle, payoff rules, scheduling, evidence and notification delivery need further specification. See [current core state](architecture/core-workspace-state.md).

See [MVP alignment](architecture/mvp-alignment.md), [the scaffold contract](architecture/scaffold-contract.md), and the per-directory READMEs for implemented boundaries.
