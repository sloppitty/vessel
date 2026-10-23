# Deploy with Docker

Vessel is a self-hosted trading diary. These examples use the
API and web images in one Docker Hub repository:

- `skorcius/vessel:api-0.3.2` and `skorcius/vessel:api-latest`
- `skorcius/vessel:web-0.3.2` and `skorcius/vessel:web-latest`

Use versioned tags for deployments. The `latest` tags move with each release.
This release targets Linux amd64.

Releases are published by the repository's Release workflow when an annotated
`vX.Y.Z` tag is pushed on `main`; see the
[`vessel-release` skill](../.agents/skills/vessel-release/SKILL.md) and the
[release notes](../docs/releases/). Publishing never upgrades an installation.

## Sign-in mode

**Token is still the default.** Existing installs need no identity settings and
continue to use their configured owner and in-memory browser token. Wallet mode
is an explicit operator opt-in, not a deployment or an automatic mode switch.
These Wallet instructions require **both v0.3.0 images or newer**; do not apply
this overlay to older published images.
The implementation and automated acceptance evidence are in
[`multi-user-state.md`](../docs/architecture/multi-user-state.md). Complete its
§10 checklist before enabling registration for real users: the real-wallet,
non-mainnet EOA check is **owner-pending**; unavailable browser checks must not
be treated as passed. Start with `Closed` until that acceptance is complete.
The D1–D12 defaults are adopted, not additional owner answers; the owner can
change them deliberately through settings or a small reviewed code change.

Wallet mode signs in an externally owned Ethereum account (EOA), creates a
separate journal per user and supports multiple linked wallets. A signature is
not a transaction, approval or venue action. The signed message fixes **Chain
ID 1**; no network switch is needed, including when the wallet is connected to
a non-mainnet network. WalletConnect and smart-contract wallets are not supported.
Operators cannot read other users' journals through the application; host and
database administrators still control the underlying data. There is no user or
journal deletion and no password/email recovery.

### Identity settings

API, both workers (`worker` and `worker-notifications`), migration/provisioning
and offline hosts must read the same authentication settings.
Invalid settings refuse startup; validation names keys, never secret values.
Environment variable spelling replaces `:` with `__`; arrays use `__0`, `__1`,
etc. Lists below are configuration arrays, not comma-separated strings.
Keys not mapped by the supplied Compose files must be added to `environment:`
on each host in an operator overlay; adding an arbitrary key to `.env` alone
does not pass it into a container.

| Key | Values and default | Rules |
| --- | --- | --- |
| `Vessel:Auth:Mode` | `Token` (default when absent) \| `Wallet` | Any other value refuses startup |
| `Vessel:Auth:Token` | — | Token mode: required outside Development (unchanged). Wallet mode: absent or empty, otherwise refuse to start |
| `Vessel:Auth:OwnerId` | GUID | Token mode: as today, and the default GUID stays accepted. Wallet mode: required outside Development; used by `ensure-owner` and legacy-preference adoption. In Wallet mode only, the well-known default GUID is refused outside Development unless `Vessel:Identity:AllowLegacyDefaultOwner=true` (logged at startup as a warning) |
| `Vessel:Auth:OwnerName` | `Owner` | Display name given by `ensure-owner` |
| `Vessel:Identity:Origins` | list | Wallet mode: required, at least one entry. Each entry is normalized to `scheme://host[:port]` (lower-case host, no path, no default port, no trailing slash). **Outside Development every origin must be `https://`.** In Development `http://` origins are allowed |
| `Vessel:Identity:Registration` | `Invitation` | Supported in this delivery: `Closed` and `Invitation`. B2 enabled registration; operator opt-in and the §10 acceptance checklist still apply. `AllowList` and `Open` are parsed but **unsupported** until per-user account/evidence quotas exist (delivery contract §13); `check-config` warns if either is set |
| `Vessel:Identity:AllowList` | empty | `0x` addresses, normalized to lower case. Used only by the unsupported `AllowList` policy |
| `Vessel:Identity:Session:IdleHours` / `AbsoluteHours` | 12 / 24 | 1–720; idle ≤ absolute |
| `Vessel:Identity:Session:RememberIdleDays` / `RememberAbsoluteDays` | 14 / 30 | 1–365; idle ≤ absolute |
| `Vessel:Identity:MaxSessionsPerUser` | 20 | 1–200. Issuing a session above the cap atomically revokes the oldest live sessions |
| `Vessel:Identity:MaxPendingRegistrations` | 10000 | At the cap, the oldest are evicted |
| `Vessel:Identity:CookieName` | `__Host-vessel-session` | Must start with `__Host-` |
| `Vessel:Identity:SignInCookieName` | `__Host-vessel-signin` | Must start with `__Host-` |
| `Vessel:Identity:AllowLegacyDefaultOwner` | false | See `OwnerId` |
| `Vessel:Identity:Schema` | `identity` | Lower-case `[a-z_][a-z0-9_]{0,62}`. Only tests change it |
| `Vessel:Monitor:WorkerConcurrency` | 2 | 1–64. Replaces the constant in `MonitorScheduler` (B2) |
| `Vessel:Monitor:VenueReadConcurrency` | 4 | 1–64, server-wide per venue (B2) |
| `Vessel:Events:MaxStreams` | 64 | 1–1024 event streams per API process, beside 4 per owner (A1) |

Outside Development, origins must be HTTPS. Origins identify the exact public
scheme/hostname/port, never a path. Preserve the request's public origin through
the trusted proxy; do not weaken forwarded-header trust to make authentication
work. Cookies use `__Host-`, `Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/` and
no Domain. **Every installation needs its own hostname: different ports do not
isolate cookies.** Only Development with an explicitly allowed HTTP origin drops
`__Host-`/`Secure` (names `vessel-session` and `vessel-signin`). Never use that
exception for an internet installation. Wallet startup refuses a nonempty
`Vessel:Auth:Token`; the Wallet overlay explicitly clears the Token setting.

### Opt in to Wallet mode

Retain the installation's existing owner GUID: credential encryption binds it.
A new installation must generate a fresh GUID; do not copy the well-known legacy
default. With a legacy default GUID, migrating to a new GUID is **not** safe;
use the explicit `AllowLegacyDefaultOwner` override only after reviewing the
startup warning. The database role needs `CREATE` on the database for identity's
`CREATE SCHEMA`, in addition to journal migration privileges.

Add to the **private operator** `.env`, not the source checkout or Vite:

```dotenv
# Use a separate hostname for each installation.
VESSEL_AUTH_MODE=Wallet
Vessel__Auth__Token=
# Keep the complete overlay list here, including any storage/credential overlays:
COMPOSE_FILE=compose.yaml:compose.tls.yaml:compose.wallet.yaml
VESSEL_IDENTITY_ORIGIN=https://diary.example.com
VESSEL_IDENTITY_REGISTRATION=Closed
# After completing the acceptance checklist, you may switch to Invitation (the
# API default). AllowList and Open are unsupported until per-user quotas exist.
# VESSEL_ALLOW_LEGACY_DEFAULT_OWNER=true
```

Wallet installs must blank `Vessel__Auth__Token`. Persist both `VESSEL_AUTH_MODE`
and `COMPOSE_FILE` in the mode-600 private `.env` so bare `docker compose`
commands (including backup/upgrade/recovery) keep the mode and overlays.
Append e.g. `:compose.credentials.yaml` and the selected storage overlay to
`COMPOSE_FILE` when needed. Explicit `-f` flags **replace** that file list:
include every required overlay if using them. Omitting the Wallet overlay with
`VESSEL_AUTH_MODE=Wallet` fails closed for missing origins, never as Token.
With Caddy, `VESSEL_IDENTITY_ORIGIN` must equal `https://$VESSEL_TLS_HOST`,
including the public HTTPS port if it is not 443 (e.g. `:8443`); do not use
the proxy's internal port. The example uses the bundled Caddy overlay;
an existing trusted HTTPS proxy is also valid. Substitute the actual owner GUID
and public wallet address below. **Never supply a private key or seed phrase.**

```sh
docker compose config --quiet
docker compose up -d --wait postgres rabbitmq
docker compose run --rm migrate
# migrate runs check-config → identity bundle → journal bundle → identity ensure-owner → provision-messaging.
# Readiness is 503, reason no-operator-wallet, until this explicit binding:
docker compose run --rm --no-deps \
  --entrypoint dotnet migrate /app/Vessel.Api.dll identity bind-wallet \
  --user <owner-guid> --address <0x-public-address> --label Operator
docker compose up -d
```

`Invitation` admits a new address only with a usable invite; `Closed` admits
linked wallets only. These are the only supported policies in this delivery:
`AllowList` (listed addresses) and `Open` (any supported EOA) admit users without
an invite, which requires per-user account and evidence quotas that do not exist
yet, so do not configure them. `ensure-owner` is idempotent and seeds no journal
facts. `migrate` never binds a wallet automatically.

### Configuration check

`deploy/migrate` first runs `dotnet /app/Vessel.Api.dll check-config`, which
applies the API's startup validation (mode, Token length outside Development,
owner GUID, connection string present, and the identity settings both workers
also read: case-sensitive `Token`/`Wallet`, empty token and owner rules in Wallet
mode, origins including the HTTPS-only rule outside Development, registration
policy, allow-list addresses, session/stream limits, cookie names, schema and the
legacy-owner flag) **without opening a database or broker connection**, so it
works on a first install before the identity schema exists. A rejected setting
stops `migrate` before either bundle. Run it alone with the same overlay set:

```sh
docker compose run --rm --no-deps --entrypoint dotnet migrate /app/Vessel.Api.dll check-config
```

Exit codes: **0** valid (warnings, such as an unsupported registration policy or
`AllowLegacyDefaultOwner`, are printed but do not fail), **1** invalid
configuration, **2** usage error. Messages name the setting, never its value.

### Offline identity commands

Use the same overlay set and `run --rm --no-deps --entrypoint dotnet migrate
/app/Vessel.Api.dll` prefix as above. Commands never start the API. Exit codes:
**0** success, **1** operational/configuration error, **2** usage error,
**3** refused precondition. Identity values are write-only where appropriate;
only `invite` prints a secret, once. Do not capture its output in shared logs.
Keep a private, access-restricted operator log of **every** `identity` command
and its output, with date, actor and target; protect invitation output as a
secret and never include it in shared logs or acceptance evidence.
Every command except `ensure-owner` refuses Token mode with exit **3**.

| `identity` arguments | Effect / safeguard |
| --- | --- |
| `ensure-owner` | Wallet: create configured operator if absent. Token: no-op, exit 0. |
| `bind-wallet --user <guid> --address 0x… [--label <label>]` | Idempotent for the same owner; refuses an unknown user, foreign address or a 21st wallet, exit 3. |
| `invite [--note <note>] [--expires 7d] [--max-uses 1]` | Prints a code **once**; expiry is a positive integer number of days, max uses a positive integer. Give it privately to the intended user. |
| `unbind-wallet --wallet <guid>` | Refuses last wallet; revokes that wallet's sessions. |
| `suspend --user <guid>` / `restore --user <guid>` | Suspend revokes all user sessions and stops new subscriber/monitor work; restore resumes eligibility. |
| `promote --user <guid>` / `demote --user <guid>` | Change operator/member role. No application journal access is granted by operator role. |
| `revoke-sessions --user <guid>` or `--all` | Revoke live sessions; open streams revalidate within 15 s. |
| `reset-after-restore [--keep-active <guid> …]` | Mandatory reset and review table before any host starts after restore; unknown `--keep-active` user exits 3 with no change; see below. |
| `verify` | Read-only report of orphan journal owner IDs and users without wallets. |

Suspending/demoting/removing a wallet from an **active operator with a linked
wallet** may not leave zero such operators. This guard does not block suspending
members or wallet-less operators. `reset-after-restore` only warns, so recovery
can suspend everyone. Wallet removal checks `last-wallet` before step-up. User
wallet linking/removal requires a fresh, action-bound step-up from a linked
wallet, with a single-use five-minute grant; at most 20 wallets are linked.

### Shared limits and deployment maps

Both auth modes enforce these **per API process**, in memory: four market
streams per user (eight total); four event streams per user (`MaxStreams` total,
64 default); one candle `refresh=true` per user/series/60 s; account discovery
10/user/minute; credential verification 30/user/minute. Refusal is 429 with
`Retry-After`. A server-wide venue deadline refusal consumes no user budget.
Refresh keys are bounded to 256/user with a fair 8,192-key backstop. Cookie auth
also limits challenge 10/client/minute (300/process), verify/register
20/client/minute (600/process), invitation attempts 10/client/hour, and separate
step-up/link buckets of 10/session/10 minutes each. IPv6 clients share a /64.

Monitor and account Refresh share PostgreSQL-backed, server-wide venue capacity
(`VenueReadConcurrency`, default 4) and venue deadlines across all users and
processes. This caps leased account runs, not every adapter HTTP request;
discovery/credential verification honour deadlines but do not occupy those
leases. Public market data retains its existing cache/backoff, not these
deadlines. Lighter's adapter semaphore **remains per process**. Manual Check
queues even at capacity; the claim waits for capacity. `WorkerConcurrency`
(default 2) bounds the worker process, not a deployment-wide venue total.

`Vessel:Messaging:Deployments` must be **identical on API, worker and provisioning
hosts**, including any custom module map; the API filters monitor heartbeat
health by its own map. Heartbeats are per deployment, not per user.
`retry-event-receipts` requires `--owner <guid>` **or** `--all-owners`; with
`--all-owners`, `--limit` is **per owner**, not a global work bound. Registration
and deregistration of a subscriber are server-wide; handling/progress/receipts
remain owner-scoped. See the [worker reference](../docs/architecture/messaging-and-workers.md).

## Requirements

Install Docker Engine with Docker Compose 2.24.4 or newer on a Linux amd64 host, OpenSSL,
`uuidgen` and Python 3. Provide persistent disk space for PostgreSQL and evidence
images in private RustFS S3-compatible storage and the RabbitMQ volume, plus a DNS
name for remote TLS use. RustFS 1.0.1 is the default; local folders and external S3
remain configurable. This Compose example deliberately sets `Provider=S3`. The API
itself remains backward-compatible: no storage settings means `Local`, not an
automatic S3 choice. The worker never reads evidence storage. The optional TLS
overlay supplies Caddy.
The optional credential-key helper needs a source checkout.

Copy this `deploy/` directory to a private operator directory outside the source
checkout. Do not reuse the development `.env`, database or LAN preview services.
In default HTTP mode only web publishes a port. In the TLS overlay Caddy replaces
that web port. RabbitMQ management is separately published on loopback for
troubleshooting. PostgreSQL, API, worker, AMQP and RustFS have no host ports; the
RustFS console is disabled. **Existing folder installations must select the local
overlay or complete the storage cutover below before switching to the new default.**

## Configure an installation

From the operator directory:

```sh
umask 077
cp .env.example .env
chmod 600 .env
```

Generate the database password and Vessel login token directly into that private
file. This example runs `openssl rand` and `uuidgen` with captured output rather
than printing secrets to the terminal or shared logs. Do not enable shell tracing
or record environment dumps.

```sh
python3 - <<'PY'
import os
import pathlib
import subprocess

path = pathlib.Path(".env")
text = path.read_text()
replacements = {
    "REPLACE_WITH_GENERATED_HEX_PASSWORD":
        subprocess.check_output(["openssl", "rand", "-hex", "32"], text=True).strip(),
    "REPLACE_WITH_GENERATED_BROKER_PASSWORD":
        subprocess.check_output(["openssl", "rand", "-hex", "32"], text=True).strip(),
    "REPLACE_WITH_GENERATED_TOKEN":
        subprocess.check_output(["openssl", "rand", "-hex", "48"], text=True).strip(),
    "REPLACE_WITH_GENERATED_GUID":
        subprocess.check_output(["uuidgen"], text=True).strip(),
    "REPLACE_WITH_GENERATED_S3_ACCESS_KEY":
        subprocess.check_output(["openssl", "rand", "-hex", "16"], text=True).strip(),
    "REPLACE_WITH_GENERATED_S3_SECRET_KEY":
        subprocess.check_output(["openssl", "rand", "-hex", "32"], text=True).strip(),
    "REPLACE_WITH_GENERATED_BROKER_PASSWORD":
        subprocess.check_output(["openssl", "rand", "-hex", "32"], text=True).strip(),
}
for placeholder, value in replacements.items():
    if text.count(placeholder) != 1:
        raise SystemExit("Expected an unconfigured .env.example copy; nothing written.")
    text = text.replace(placeholder, value)
path.write_text(text)
os.chmod(path, 0o600)
PY
```

Keep the password hexadecimal. Compose inserts it into an Npgsql connection
string, so a custom password must not contain connection-string delimiters.
Keep `RABBITMQ_USER=vessel` or choose a lowercase ASCII username. The broker
password must be hexadecimal because Compose places it in the private AMQP URI.
Never use `guest`, print that URI, or publish AMQP outside the private network.
Set `Vessel__Auth__OwnerName` if desired. Keep the generated owner GUID for the
life of this installation. Changing it hides existing owner-scoped records.

`VESSEL_WEB_BIND` defaults to `127.0.0.1:8080`. Keep it loopback-only, or restrict
it to a private interface reachable only by your TLS proxy. Never expose plain
HTTP to the internet. Choose another port if 8080 is already used.

The Compose network is pinned to `172.30.86.0/24` with gateway `172.30.86.1`.
Web is pinned to `172.30.86.10`; optional Caddy is pinned to `172.30.86.20`.
If these overlap a host, VPN or Docker network, change `VESSEL_NETWORK_SUBNET`,
`VESSEL_NETWORK_GATEWAY`, `VESSEL_WEB_IP` and `VESSEL_TLS_PROXY_IP` together.
Keep both service IPs inside the subnet and distinct from its gateway.

## Event messaging: RabbitMQ

Committed events are recorded in PostgreSQL and relayed after commit through a
durable outbox to RabbitMQ, which is transport, not the record. Compose runs the
pinned `rabbitmq:4.3.6-management` image with the `RABBITMQ_USER`/`RABBITMQ_PASSWORD`
credentials from `.env`, a named `rabbitmq` volume for durable quorum queues and a
`rabbitmq-diagnostics -q ping` healthcheck. AMQP stays on the private Compose
network. The management page binds to `VESSEL_RABBITMQ_MANAGEMENT_BIND`
(default `127.0.0.1:15672`) for troubleshooting; reach it through an SSH tunnel or
an HTTPS proxy, never plain HTTP on the internet.

Publishers write each event once to the topic exchange `vessel.events`, with the
event type as routing key. Each subscriber owns a queue `vessel.events.sub.<name>`
bound by its filter, with a dead-letter exchange and queue `….dlx`/`….dlq` and a
quorum delivery limit of 20 as a transport guard. Account monitor commands use the
point-to-point queue `vessel.commands.monitor`, which the worker's `monitor`
module listens to. This release registers no production event subscription.

The one-shot `migrate` service applies the EF migrations and then runs
`provision-messaging`: Wolverine envelope storage for every logical deployment
(`wolverine_api`, `wolverine_monitor`) with its module set recorded in
`messaging_deployments`, the broker topology of every module and insert-only
subscription registrations. Neither the API nor a worker creates storage or
declares topology at startup. A host whose deployment was not provisioned, or
whose schema was provisioned for a different module set, refuses to start, so no
deployment recovers envelopes it has no route for. To change the modules of an
existing deployment, stop it, let its outbox drain, and provision it under a new
deployment name (and schema) instead of editing the old one.
Administrator commands run in the API image with the server configuration:

```sh
# Mandatory step of every database restore, before provisioning and starting hosts.
docker compose run --rm --no-deps --entrypoint dotnet migrate /app/Vessel.Api.dll rotate-event-epoch
# Return a subscription's failed receipts to pending with a bounded extra budget.
docker compose run --rm --no-deps --entrypoint dotnet migrate /app/Vessel.Api.dll \
  retry-event-receipts <name> --owner <guid> --limit 100 --attempts 3
# Remove a subscription's registration, receipts and queue.
docker compose run --rm --no-deps --entrypoint dotnet migrate /app/Vessel.Api.dll \
  deregister-event-subscription <name>
```

Retry requires exactly one of `--owner <guid>` or `--all-owners`. With
`--all-owners`, `--limit` applies per owner, not to the command's total.

A release check can set `Vessel__Messaging__Verification=true` on `migrate` to
register the `verification` subscription, prove delivery from its receipts and
then deregister it. Dead-letter queues are diagnostic copies; redriving one never
reopens a settled receipt.

## Image storage: RustFS by default

The default Compose file starts private RustFS, waits for its health check and
runs `storage-init` once to create `VESSEL_S3_BUCKET` (default `vessel-evidence`).
API startup waits for successful initialization as well as migrations. Re-running
initialization does not erase the bucket. Original images live in the `objects`
volume; notes, markup and ownership stay in PostgreSQL. All image access passes
through the authenticated Vessel API; do not enable public bucket policies or CORS.

Generate the S3 credentials above, keep `.env` private and retain the same pair
across restarts/restores. The bundled, installation-dedicated RustFS uses that
pair for both administration and API access. For a shared object store, use a
separate private bucket and bucket-scoped runtime credentials with the external
option below; do not reuse an administrator key from another installation.
Vessel's venue credential ring stays on its separate secret mount, never in S3.

### Existing/external S3-compatible service

Provision a private bucket separately and set `VESSEL_S3_ENDPOINT` (an HTTPS
origin, without path/query/credentials), `VESSEL_S3_BUCKET`, `VESSEL_S3_REGION`,
`VESSEL_S3_ACCESS_KEY_ID` and `VESSEL_S3_SECRET_ACCESS_KEY`. Set
`VESSEL_S3_FORCE_PATH_STYLE=false` if the provider requires virtual-host addressing.
Grant the API only `s3:GetObject`, `s3:PutObject` and `s3:DeleteObject` on that
bucket's objects. Keep list/create/backup privileges with the operator.
For offline **destination-copy** credentials on AWS S3, additionally grant
`s3:ListBucket` on that destination bucket (not its object ARN). The helper
does not list objects, but needs AWS to return 404 for an absent key rather
than an indistinguishable 403 access failure. Use this separate operator role
for copy commands; do not broaden the running API role.

```sh
docker compose -f compose.yaml -f compose.storage-external.yaml up -d
```

This omits the bundled RustFS and initializer. API startup validates configuration
but does not provision or probe an external bucket. The provider must support
atomic conditional PUT (`If-None-Match: *`); never weaken this to allow overwrites.
Use the provider's backup/restore process, not the RustFS volume commands below.

### Local folder / preserving an existing installation

```sh
docker compose -f compose.yaml -f compose.storage-local.yaml up -d
```

This selects `Local`, omits RustFS/initialization and retains the original
`evidence:/var/lib/vessel/evidence` volume. S3 credentials are not needed. Keep the
same Compose project name to reuse existing data. Outside Docker, no provider
setting is needed for Local; optionally set
`Vessel__Evidence__Storage__Local__RootPath`.

Include your selected storage overlay on **every** Compose command, together
with TLS/credential overlays when used. Do not enable the internal
`unused-storage` profile. Local and external overlays are alternatives.

Changing provider does **not** move existing files. Stop writers, copy every
original with its existing `owner/play/image.ext` key and verify sizes/SHA-256
before switching; see [the cutover checklist](../docs/architecture/evidence-storage.md).
No database migration is needed. These changes require a newly built/published
API image containing the S3 adapter; API images without this adapter support
only local storage. Do not apply the new Compose default to an old image.

### Copy existing local images to S3

This is an explicit offline file copy, **not a schema migration or automatic
startup task**. Keep the same Compose project name, take coordinated backups and
select a newly built API image containing the copy command. Stop all web/API
writers; PostgreSQL must remain available. Add your usual TLS/key overlays to the
commands as appropriate.

For the bundled RustFS destination, provision it without starting API/web:

```sh
docker compose -f compose.yaml -f compose.storage-local.yaml stop web api
docker compose -f compose.yaml up -d --wait rustfs
docker compose -f compose.yaml run --rm --no-deps storage-init
```

For external S3, provision the private bucket separately and configure its
`VESSEL_S3_*` settings instead of starting RustFS. Then run against the **original
local volume**, which the local overlay mounts at the unchanged path:

```sh
# Dry run: checks every database-referenced original; does not upload.
docker compose -f compose.yaml -f compose.storage-local.yaml run --rm --no-deps \
  api evidence-copy-to-s3 --source /var/lib/vessel/evidence

# Only after a successful dry run and reviewing the reported count/byte total:
docker compose -f compose.yaml -f compose.storage-local.yaml run --rm --no-deps \
  api evidence-copy-to-s3 --source /var/lib/vessel/evidence --apply
```

`--no-deps` prevents this operation from running the schema migrator or starting
other services. The command temporarily targets S3 while the local overlay keeps
the normal API on Local. It verifies each source against database size/SHA-256,
uses create-only uploads, downloads/verifies the result and skips matching
objects on reruns. It never deletes originals, changes notes/markup/database
records or saves a provider switch. Different destination contents stop the run.
After any failure, resolve it and rerun; do not cut over on partial success.

Rerun the dry run to verify all objects already match. Then explicitly remove the
local overlay (use the external overlay for external S3) and restart API/web.
Keep the original volume and backups until authenticated image reads, notes/markup
and a new upload are verified. See the [complete cutover and rollback checklist](../docs/architecture/evidence-storage.md#existing-local-folder-installation-explicit-cutover).

### Export S3 to Local or switch S3 providers

Use an API image containing `evidence-copy-to-local`; older published images do
not gain this command from a Compose update. Stop web/API writers and take
coordinated backups, but leave PostgreSQL and the source S3 running. Preserve
the project name and installation overlays on every command below (add
`-f compose.storage-external.yaml` when the source is external S3).

Prepare a dedicated private host folder writable by the container's app user.
For rootful Docker without user-namespace remapping:

```sh
sudo install -d -m 700 -o 1654 -g 1654 /private/evidence-export
docker compose -f compose.yaml run --rm --no-deps \
  --volume /private/evidence-export:/var/lib/vessel/export \
  api evidence-copy-to-local --destination /var/lib/vessel/export
docker compose -f compose.yaml run --rm --no-deps \
  --volume /private/evidence-export:/var/lib/vessel/export \
  api evidence-copy-to-local --destination /var/lib/vessel/export --apply
```

Use the mapped host UID with rootless Docker/user namespaces instead. The S3
settings still identify the **old/source** bucket. Rerun without `--apply` and
confirm every image already verifies. Local conflicts are never overwritten.
`--no-deps` prevents a schema migration or any dependent service startup.
The host bind mount, not the container's ephemeral filesystem, retains the copy.

For another S3 provider, make a separate mode-600 `.env.copy-destination` from
the installation's `.env`, retaining the same database/owner/auth values and
changing only the S3 endpoint, bucket, region, addressing and credentials.
Provision that private bucket first; keep the running installation unchanged.
Upload the export through a **read-only** mount:

```sh
docker compose --env-file .env.copy-destination \
  -f compose.yaml -f compose.storage-external.yaml run --rm --no-deps \
  --volume /private/evidence-export:/var/lib/vessel/export:ro \
  api evidence-copy-to-s3 --source /var/lib/vessel/export
docker compose --env-file .env.copy-destination \
  -f compose.yaml -f compose.storage-external.yaml run --rm --no-deps \
  --volume /private/evidence-export:/var/lib/vessel/export:ro \
  api evidence-copy-to-s3 --source /var/lib/vessel/export --apply
```

Keep writers stopped through export/import/cutover. After a successful
verification rerun, explicitly update the installation's S3 settings, restart
API/web and verify authenticated image reads and a new upload. Keep the old
bucket, local copy and coordinated backups until verified. To switch to Local
instead, persistently mount this export at the configured Local root and select
`Provider=Local` before restarting. Neither command changes configuration,
deletes originals or copies database notes/markup; this is not a complete
application/bucket backup. See the
[portable-copy guarantees](../docs/architecture/evidence-storage.md#s3-to-local-or-another-s3-provider).

## Optional Lighter credential storage

Public venue reads do not require a key ring. Saving a verified Lighter `ro:`
token does. Keep the ring outside the database and outside all repositories.
For a new installation only, use the existing helper from the source checkout:

```sh
install -d -m 700 "$HOME/.config/vessel-secrets"
python3 /absolute/path/to/vessel/scripts/credential_keys.py init \
  --key-file "$HOME/.config/vessel-secrets/credential-keys.json" \
  --new-installation
sudo chown 1654:1654 "$HOME/.config/vessel-secrets/credential-keys.json"
sudo chmod 600 "$HOME/.config/vessel-secrets/credential-keys.json"
```

Set `VESSEL_CREDENTIAL_KEYS_FILE` in the operator `.env` to that expanded absolute
host path, then include the optional override on **every** Compose command:

```sh
docker compose -f compose.yaml -f compose.credentials.yaml up -d
```

The override mounts the file read-only at
`/run/secrets/vessel-credential-keys.json` and sets
`Vessel__Credentials__KeyFile`. The API runs as `app`, UID 1654. Its loader
requires a regular, non-symlink file with exactly mode `0600`, readable by that
user. The loader checks mode and readability, not ownership separately. On
rootful Docker without user namespace remapping, host UID 1654 is required to
make a `0600` bind mount readable. With rootless Docker or user namespace
remapping, use the corresponding mapped host UID.

Local Compose file-backed secrets do not reliably implement their `uid`, `gid`
and `mode` declarations. This example uses a bind mount with
`create_host_path: false`, not a default `0444` Compose secret. Do not loosen the
loader's permission check. For another deployment system's secret, make sure the
mounted file is `0600` and readable by UID 1654.

The helper initially owns the file as the invoking host user; after changing
ownership, future helper operations must run as the file's host owner in an
owned mode-700 directory. Provision a replacement securely and remount it for
rotation rather than modifying the read-only container file.
Restore existing key IDs and bytes if encrypted credentials already exist.
Never generate a replacement for a lost existing ring. See
[credential setup and rotation](../docs/architecture/venue-credentials.md).

## Optional venue hosts

The API and the worker read each venue's host from `Vessel:Venues`. The
defaults are the production hosts, so most installations set nothing. To point
a venue at a different host, for example an egress proxy, add the matching
variable to both the `api` and the `worker` services' `environment` in your own
Compose override file and include it on every Compose command. The worker makes
all monitor reads, so setting it only on the API leaves monitoring on the
default hosts. The example `compose.yaml` does not forward these
from `.env`, and an empty value is invalid rather than a default.

| Variable | Default |
| --- | --- |
| `Vessel__Venues__Hyperliquid__BaseUrl` | `https://api.hyperliquid.xyz/` |
| `Vessel__Venues__Hyperliquid__StreamUrl` | `wss://api.hyperliquid.xyz/ws` |
| `Vessel__Venues__Risex__BaseUrl` | `https://api.rise.trade/` |
| `Vessel__Venues__Lighter__BaseUrl` | `https://mainnet.zklighter.elliot.ai/` |
| `Vessel__Venues__LighterRh__BaseUrl` | `https://api.rh.lighter.xyz/` |

Base URLs must be `https://` origins with no path. Stream URLs must be `wss://`.
Neither may contain user info, a query or a fragment. The API refuses to start
with an invalid value, and so does the worker; both name only the key, never the value. A host receives
the venue's read-only tokens, so configure only hosts you trust as much as the
venue itself.

## Start and connect

Without optional credential storage:

```sh
docker compose up -d
docker compose ps -a
```

Compose waits for healthy PostgreSQL and RabbitMQ, then runs `/app/migrate` as a
deliberate one-shot step. The wrapper runs the EF bundle, then messaging provisioning
(envelope storage, broker topology and insert-only subscription registrations).
It then starts API and both workers and waits for API readiness before starting web.
The EF bundle reads `ConnectionStrings__Vessel` from its environment through the
design-time factory. The password is not an argument and the bundle does not
print it. Neither API nor worker migrates or auto-provisions message storage at
startup. Missing storage is a setup error, not permission to migrate a live database.

The worker uses `/app/worker` in the same versioned API image and has no HTTP
ingress or host port. It runs the modules in `Vessel__Worker__Modules` (Compose:
`monitor`) in the logical deployment `Vessel__Worker__Deployment` (default: the
first module), which names its envelope schema (`wolverine_monitor`) and service.
An unknown module stops startup. Replicas of one deployment share its schema.
Every worker also runs subscription catch-up for the subscriptions it hosts and
the hourly event retention sweep; both are claimed in PostgreSQL and safe with
replicas. Journal operations resolve in fresh assigned owner scopes. Token
mode uses the configured owner; Wallet mode validates each message through the
identity directory. Unknown owners dead-letter, suspended owners' commands are
dropped/logged and their events acknowledged for later catch-up; new work starts
only for active users. Logs contain IDs, never payloads.

`RunAccountMonitor` commands are only a hand-off: the pending request lives in
PostgreSQL (`monitor_accounts`), the listener acknowledges once it has validated
it and wakes the scheduler, and a lost command is found on the next 30-second
tick. After each productive scheduling pass (discovery and claims), the worker
writes its **deployment** database heartbeat and a local heartbeat file holding the time and the
configured timeout; `/app/worker-health` rejects a missing, malformed or older
file. If the file cannot be written, the database heartbeat is not recorded
either, so API monitor responses report the worker offline.

The Notification Hub runs as `worker-notifications`: the same image and entrypoint
with `Vessel__Worker__Modules__0: notifications` in its own deployment
(`wolverine_notifications`). It hosts the `notifications` subscription to every
event type, writes in-app notifications in the receipt's own transaction, and
deletes notifications older than `Vessel__Notifications__RetentionDays` (default
90) hourly. It never opens venue tokens, so it gets no credential key ring. After
each maintenance pass in which every catch-up succeeded it writes its own heartbeat
row in `server_notification_heartbeats` (one per configured deployment, through
`NotificationWorkerState`, not an owner journal) and the file `Vessel__Notifications__HeartbeatFile` (default
`/tmp/vessel-notifications-heartbeat`); `/app/worker-health` reads that file when
the worker's first module is `notifications`. The API reports it as
`workerOnline` in `GET /api/notifications/head`, separately from the monitor.

Wallet mode gives `worker-notifications` the same `Vessel__Auth__Mode`, empty
`Vessel__Auth__Token` and Wallet settings as `worker`; retain the Wallet overlay
on every command. `WorkerHost.Configure` calls `AddVesselIdentityDirectory` for
**every** module, replacing Token's fixed-owner directory with
`IdentityOwnerDirectory` in Wallet mode. The Hub discovers active/new owners
without re-provisioning: registration is server-wide, but receipts/progress,
notifications, preferences, feed cursors, unread counts, popup claims and API
reads/writes are owner-scoped. Operator role never reads another journal.
Suspended owners' live deliveries are acknowledged **without** producing
notifications; active-owner handling resumes through retained catch-up after
restore/unsuspension. Retention runs per retained owner, including suspended
owners, without a request scope.

`POST /api/notifications/test` has a **6 requests/minute/user/API-process** budget
in addition to the stored per-owner test cooldown (default 10 seconds). A 429
includes `Retry-After`; this is not a deployment-wide quota or a venue call.

Provisioning registers the `notifications` subscription whichever services run.
An installation that never starts `worker-notifications` keeps working without
notifications: the subscription holds no receipts and becomes `lagging` once events
age past `Vessel__Events__RetentionDays`. A notifications worker that ran and then
stopped can leave pending receipts, which hold event retention until they settle;
start it again, or stop it for good with
`docker compose run --rm --no-deps --entrypoint dotnet migrate /app/Vessel.Api.dll deregister-event-subscription notifications`
(the next provisioning registers it again while the module is compiled in). An
explicit `Vessel__Messaging__Deployments` configuration needs a `notifications`
entry.

### Optional paper worker

The paper playground requires a **paper-capable API/web build and its explicit
journal migration**. No published older image gains this feature by adding an
overlay. Set `VESSEL_VERSION` explicitly to the matching build and
`VESSEL_AUTH_MODE` explicitly to the API's `Token` or `Wallet` mode, keep the
same database/project, and include `compose.paper.yaml` plus `--profile paper`
alongside your existing storage, TLS, Wallet and credential overlays. For
example, adding `-f compose.paper.yaml --profile paper` to your established
Compose command enables the private `worker-paper` service.

It runs the same `/app/worker` binary with only the `paper` module and deployment
`paper`. This standalone scheduler does not use command queues, messaging
envelope schemas or RabbitMQ; it needs no paper messaging provisioning, HTTP
bearer, credential key ring, evidence mount or exposed port. Existing
monitor/notifications deployments and their immutable module sets stay unchanged.
Their normal migrations/provisioning requirements still apply to the full app.

Paper is off by default per owner. Settings enables it and sets a paper-only
check interval (default 60 minutes). The worker reads bounded closed minute
candles for the unevaluated period and shares instrument reads; it does not
stream prices or refresh a real account. Both modes retain their own results.
The scheduler's productive-pass file is
`Vessel:Paper:HeartbeatFile` (default `/tmp/vessel-paper-heartbeat`);
`/app/worker-health` selects it for the `paper` module. A configured paper check
is not a promise of real-time execution or a venue fill.

Evaluation waits ten seconds after a minute closes. Explicit native zero-trade
minutes carry coverage but do not simulate a touch; unknown missing minutes are
not invented. An account-local arithmetic/validation failure is retained for
retry without stopping other owners, while shared database/schema failures still
fail worker health.

This first version cannot manually resolve a candle gap/ambiguity or manually
close a simulated position. Affected reservations may remain held indefinitely
and unresolved results stay unscored; normal closes use planned exits. Virtual
equity stays unknown while open/unresolved rather than using stale candle marks.
Changing the shared sizing risk preference remains a Real-only action.
Owner suspension is different from a genuine source gap: enabling monitoring
again resumes previously active Plays at a fresh minute boundary, skips
disabled-period touches and retains unscored history. Owner-paused Plays stay
paused. A declined capital admission does not stop protective exits on already
open tranches.

**Include `worker-paper` when stopping all writers for any migration, backup,
restore, identity reconciliation or event-epoch operation.** In paper-enabled
installs, run `docker compose stop worker-paper` using the complete overlay/profile
command before the stop/backup examples below. Do not leave another paper
replica running. The paper worker still needs the same authentication mode and
identity directory as the API; retain `VESSEL_AUTH_MODE=Wallet` and your complete
Wallet/TLS overlays on every command in a Wallet installation.

### Monitor and broker settings

`VESSEL_MONITOR_INTERVAL`, `VESSEL_MONITOR_PROTECTION_GRACE` and
`VESSEL_MONITOR_MANUAL_COOLDOWN` are TimeSpan strings, such as `00:10:00`.
They map to `Vessel:Monitor:Interval`, `ProtectionGrace` and `ManualCooldown`.
The defaults are ten minutes, ten minutes and sixty seconds. Interval is
validated from one through sixty minutes and scheduling adds ten-percent
jitter. The cooldown may not exceed the interval and gates manual Check and
Refresh only, never scheduled or confirmation runs; failed checks also start
it. Account Refresh and claimed Checks share the fixed two-account read limit
per owner plus the server-wide per-venue capacity (default four). A manual Check
queues even when full; the claim enforces capacity. The worker process uses
`WorkerConcurrency` (default two).
A Refresh never changes check backoff, monitor state or the scheduled check, and
a refused or abandoned Refresh is never replayed by the worker.
`Vessel:Monitor:WatchLifetime` (default `7.00:00:00`) bounds how long the linked
exits and entries of a closed or cancelled Play are watched; after it, a watch
without proof ends as a persistent `unverifiable`, never as resolved.
`Vessel:Monitor:UnobservableLifetime` (default `7.00:00:00`) bounds how long a
discrepancy's subject may stay unobservable before the record becomes
`unverifiable` and stops holding the account in monitoring. Both accept one hour
to ninety days.
Venue HTTP 429 `Retry-After` deadlines are retained in PostgreSQL **per venue,
server-wide across owners** (`server_venue_deadlines`). Monitor claims, Refresh,
discovery and credential verification respect the longest deadline across API
and worker instances; a shorter later response cannot shorten it. A missing
or invalid `Retry-After` uses a sixty-second delay, and any deadline is clamped to
at most one hour. The API exposes the effective
manual deadline and returns it as `Retry-After` when admission is throttled.

`Vessel:Monitor:HeartbeatTimeout` defaults to `00:01:30`, with a minimum
`00:00:30`. `Vessel:Monitor:HeartbeatFile` defaults to
`/tmp/vessel-worker-heartbeat`. The packaged health script reads the timeout the
worker wrote into that file. `Vessel:Events:RetentionDays` defaults to 90.
Pruning leaves pending delivery work and deduplication history intact.

RabbitMQ is pinned to `rabbitmq:4.3.6-management`, with a named `rabbitmq`
volume and a stable hostname. Durable quorum queues, the `vessel.events`
topic exchange and consumer bindings are declared by the explicit provisioning
step. Publisher confirmations do not replace consumer receipts.
`VESSEL_RABBITMQ_MANAGEMENT_BIND` defaults to `127.0.0.1:15672`. Use an SSH tunnel
or an HTTPS proxy to reach it remotely, never plain HTTP to the internet.
Before upgrading RabbitMQ, enable the current version's feature flags and follow
its supported upgrade path.

PostgreSQL is the record; RabbitMQ is transport. Subscribers recover unsettled
retained events from PostgreSQL through catch-up, and pending manual requests are
recovered from `monitor_accounts` with their original request ID. A lost broker
volume costs latency, not data; dead-letter contents and broker diagnostics are not
reconstructed. Do not add the broker volume to the database/evidence backup
archive.

### Broker users and passwords

`RABBITMQ_USER`/`RABBITMQ_PASSWORD` create the broker's administrator on the
**first** start of an empty `rabbitmq` volume only; changing them in `.env` later
does not change the broker. That user is what `migrate` needs to declare topology.
Use URL-safe passwords (for example `openssl rand -hex 24`): Compose places them in
an AMQP URI.

Rotate each broker user separately: change the password in RabbitMQ first, then the matching `.env` value, then recreate only the services that use that user. `rabbitmqctl` reads the new password from standard input, and the shell's `read -s` keeps it out of the terminal, shell history and process arguments. Compose reads `.env` itself; its values are not exported to your shell, so name the user explicitly.

Administrator (`RABBITMQ_USER`, used by `migrate`, and by API and worker when no application user is configured):

```sh
read -rs NEW_PASSWORD   # a new URL-safe value, for example from: openssl rand -hex 24
printf '%s' "$NEW_PASSWORD" | docker compose exec -T rabbitmq rabbitmqctl change_password <administrator-user>
unset NEW_PASSWORD
# Edit RABBITMQ_PASSWORD in .env, then recreate what uses the administrator:
docker compose up -d --force-recreate api worker worker-notifications   # only if RABBITMQ_APP_USER is not set
docker compose run --rm migrate                    # confirms provisioning works with the new password
```

Application user (`RABBITMQ_APP_USER`, used by API and worker):

```sh
read -rs NEW_PASSWORD
printf '%s' "$NEW_PASSWORD" | docker compose exec -T rabbitmq rabbitmqctl change_password <application-user>
unset NEW_PASSWORD
# Edit RABBITMQ_APP_PASSWORD in .env, then:
docker compose up -d --force-recreate api worker worker-notifications
```

Recreate every other service that uses the same user too, for example additional worker services. Between the broker change and the recreation, the running hosts keep their existing connections; a reconnect with the old password fails until they are recreated, and their outboxes hold publications meanwhile.

For least privilege, API and worker can use a separate, non-administrator
application user limited to the `vessel.` names (hosts declare nothing at
runtime: no reply queue, no topology), while `migrate` keeps the administrator.
The broker tests pass with exactly these permissions. Create it once and set `RABBITMQ_APP_USER`/`RABBITMQ_APP_PASSWORD`
in `.env`; without them API and worker use the administrator:

```sh
read -rs NEW_PASSWORD   # the value you also put in RABBITMQ_APP_PASSWORD
printf '%s' "$NEW_PASSWORD" | docker compose exec -T rabbitmq rabbitmqctl add_user vessel-app
unset NEW_PASSWORD
# configure, write (commands are published through the default exchange), read
docker compose exec rabbitmq rabbitmqctl set_permissions -p / vessel-app \
  '^vessel\..*' '^(vessel\..*|amq\.default)$' '^vessel\..*'
docker compose up -d --force-recreate api worker worker-notifications
```

The management page is published for troubleshooting and bound to loopback by
default; it shows queue contents to anyone with broker credentials. Leave
`VESSEL_RABBITMQ_MANAGEMENT_BIND` on loopback unless an HTTPS proxy fronts it.

Open your HTTPS URL and connect with the configured Vessel API token from your
private operator file. Do not confuse it with a Lighter read-only token. Vessel
keeps this login token only in browser session memory, not local storage or URLs.

## HTTPS and proxy trust

### Default HTTP mode

Web overwrites `X-Forwarded-For` with its observed client address and
`X-Forwarded-Proto` with its own HTTP scheme. It clears `X-Forwarded-Host` and
`Forwarded`. `VESSEL_TRUSTED_PROXY` is empty, so no caller can claim HTTPS or
choose another rate-limiter address by supplying these headers. Docker NAT may
make different clients share the gateway address and therefore the failure
limiter. This is conservative, not an identity guarantee.

The API trusts only the pinned `VESSEL_WEB_IP`, with `ForwardLimit=1`, not the
bridge subnet or gateway. PostgreSQL and API are unpublished. Plain HTTP remains
unsuitable for credentials and login tokens beyond a secured local host. The
production Lighter credential guard stays enabled.

### Preferred TLS mode, Caddy in Compose

Set these in the private operator `.env`:

```text
VESSEL_TLS_HOST=vessel.example.com
VESSEL_TLS_HTTP_BIND=0.0.0.0:80
VESSEL_TLS_HTTPS_BIND=0.0.0.0:443
VESSEL_TLS_DIRECTIVE=
```

Point DNS to the host and permit inbound 80/443 for Caddy's public certificates.
Use the overlay on every command, and include the credential override when needed:

```sh
docker compose -f compose.yaml -f compose.tls.yaml up -d
docker compose -f compose.yaml -f compose.tls.yaml ps -a
# With optional credential storage:
docker compose -f compose.yaml -f compose.tls.yaml -f compose.credentials.yaml up -d
```

The overlay resets web's published ports to an empty list. Only Caddy publishes
HTTP/HTTPS. Caddy replaces untrusted forwarded headers and terminates TLS. Nginx
trusts exactly Caddy's pinned IPv4 address through `set_real_ip_from`,
`real_ip_header X-Forwarded-For` and `real_ip_recursive off`. A `geo`/`map` checks
`$realip_remote_addr`, the **immediate** peer before real-IP replacement, before
accepting its scheme. Other peers get their observed address and `$scheme`, even
when they can reach web on the private network. Nginx sends one sanitized hop to
the API, preserving Caddy's observed client IP and HTTPS. The unprivileged nginx
image must include `--with-http_realip_module`; the local verification checks it.
Do not attach untrusted containers or publish web/API in this mode. A container
controlling Caddy's exact IP is a trusted proxy and can impersonate clients.

For an isolated local test, use `VESSEL_TLS_HOST=localhost`, loopback-only free
ports and `VESSEL_TLS_DIRECTIVE=tls internal`. Test with `curl -k`, or install the
local CA in your test browser. Never use `-k` for production credentials. The
`caddy_data` volume contains certificates and private keys; protect it.

### Host-local TLS proxy, weaker alternative

Keep `VESSEL_WEB_BIND=127.0.0.1:8080`, omit the TLS overlay, and explicitly set
`VESSEL_TRUSTED_PROXY=172.30.86.1`, or the actual gateway seen by nginx. Configure
the host proxy with trusted certificates, the same upload limit and unbuffered
SSE. For example:

```nginx
server {
    listen 443 ssl;
    server_name vessel.example.com;
    ssl_certificate /etc/ssl/vessel/fullchain.pem;
    ssl_certificate_key /etc/ssl/vessel/privkey.pem;
    client_max_body_size 10551296;
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host "";
        proxy_set_header Forwarded "";
        proxy_set_header Connection "";
        proxy_buffering off;
        proxy_read_timeout 1h;
    }
}
```

**This explicitly trusts every host-local process that can reach the web port.**
Docker NAT gives those processes the same gateway peer as the host TLS proxy.
They can forge HTTPS and client addresses. Loopback binding does not distinguish
processes or encrypt that hop. Use this only on a host where all local processes
are trusted; prefer the unpublished-web overlay. Never combine gateway trust
with a public web binding.

### Other API deployments

Outside this Compose example the API defaults to loopback trust. Nonempty
indexed `Vessel__ForwardedHeaders__KnownNetworks__0` and/or
`Vessel__ForwardedHeaders__KnownProxies__0` settings **replace** loopback defaults.
The API rejects malformed CIDRs, `/0`, unspecified proxies and limits outside
1 to 5. Prefer exact peers and a one-hop chain that sanitizes headers. Every peer
in a configured network can impersonate clients. Never trust a whole bridge
just to make TLS work.

## Web response policy

Static documents, deep-link fallbacks, assets and nginx errors carry `nosniff`,
`DENY`, `no-referrer` and this Content-Security-Policy:

```text
default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'
```

Style attributes are needed for theme variables and chart positioning. Scripts
have no inline/eval exception. Blob/data images support captures, uploads and
markup; fonts and API/SSE connections stay same-origin. API responses use their
own security headers, without duplicate nginx headers. Nginx gzip is disabled
for `/api` and `/health`; it passes backend-selected market-data compression
unchanged and compresses static assets only.

## Journal search index

October 22, 2026 (Phase 2, item 13). Journal-wide search and the Plays list's
exit time, result and estimated R read a search projection: the tables
`play_search` and `play_search_texts`, added by the journal migration
`JournalSearch`. Every change to a Play writes its projection in the same
transaction, but Plays that existed before the upgrade have none until it is
filled once. Until then search counts them as "not indexed yet" and the list
shows "Result not indexed yet"; nothing is guessed.

Run the offline command once, after `migrate` has applied the migration. It
dry-runs by default:

```sh
# Counts each owner's Plays that are not indexed yet; writes nothing.
docker compose run --rm --no-deps --entrypoint dotnet migrate /app/Vessel.Api.dll play-search-rebuild
# Indexes them in batches of 100 Plays per transaction (--batch 1-500).
docker compose run --rm --no-deps --entrypoint dotnet migrate /app/Vessel.Api.dll play-search-rebuild --apply
```

- It reads only `ConnectionStrings__Vessel`. It starts no HTTP listener and
  applies no schema migration. It never changes Plays, plans, reviews,
  executions or any other journal fact.
- It takes the same per-Play locks as the API and workers, so hosts may keep
  running. Batches already committed stay indexed after a failure or Ctrl+C.
- Running it again finds nothing left to do. `--all` rebuilds every Play's
  projection, for example after a later release changes what it holds.
- Use the same overlay set as `migrate`. Backups need nothing new: the
  projection is derived data and can always be rebuilt from the journal.

## Upgrade

### From v0.3.1 to v0.3.2

Read the [release notes](../docs/releases/v0.3.2.md). This patch restores the
Review account selector styling and adds no database migrations or configuration
changes. Back up the installation, set `VESSEL_VERSION=0.3.2` and recreate the
services with matching API and web images, preserving existing overlays.
Earlier versions must also follow the upgrade steps below.

### From v0.3.0 to v0.3.1

Read the [release notes](../docs/releases/v0.3.1.md). Stop web, API and every
worker on all hosts, including `worker-notifications`, and back up both database
schemas, evidence and credential keys. Preserve the owner GUID and all selected
storage, credential, authentication and TLS overlays.

Set `VESSEL_VERSION=0.3.1` and run the explicit `migrate` service before starting
the matching API, web and worker images. It applies these journal migrations in
order; there are no new identity migrations:

| Journal migration | Effect |
| --- | --- |
| `20261018180202_LeverageObservations` | Retains observed position leverage and adds nullable leverage to imported fills. Copies current leveraged snapshot positions; historical fills stay unknown. Apply explicitly before starting upgraded hosts. |
| `20261019100000_ActivityFactsRevision` | Adds per-owner activity revision counters and commit-deferred invalidation triggers for the bounded in-process aggregation cache. Existing owners start at revision 1. Apply with the explicit journal migration bundle before starting the new API/workers. |

Current snapshot leverage is backfilled; past leverage is not reconstructed.
The cache holds Activity aggregates only and never caches authorization. No
application host migrates at startup. Rollback requires the pre-upgrade backup
and matching previous images. Recreate disposable databases that used the
unshipped `20261018130000_ActivityFactsRevision` ID; never rename migration-history
rows. When upgrading from v0.2.0 or earlier, the v0.3.0 order-identity restriction
below still applies.

### From v0.2.0 to v0.3.0

Read the [release notes](../docs/releases/v0.3.0.md) first. This release includes
settled funding, order identity, the Notification Hub and the multi-user stack.
**There is no retained-data upgrade path if imported orders, fills or Play
order links exist.** The order-identity guard below refuses the migration;
do not bypass it or clear individual tables to force an upgrade.
Keep a verified backup and the old installation before deliberately creating
a fresh database and evidence store.

Stop every web, API and worker instance, including `worker-notifications`,
before changing images or running the one-shot `migrate` service. Set
`VESSEL_VERSION=0.3.0`, preserve the chosen storage/credential/TLS overlays
and keep Token mode unless explicitly opting into Wallet mode. The full
migration and provisioning sequence is below; rollback requires the old
images and pre-upgrade backup, not down migrations. Publishing the release
does not perform any of these installation steps.

### After v0.2.0: order identity (breaking, no upgrade path)

The v0.3.0 `OrderIdentity` migration changes how orders, fills and
Play order links are keyed and has **no backfill**. It refuses to run when
the database holds imported orders, fills or links. There is no retained-data
upgrade path: an installation with v0.2.0 data must be recreated (fresh
database and evidence store) before applying it. See
`docs/architecture/order-identity.md`.

### After v0.2.0: the Notification Hub

- Migration `20261017180000_NotificationHub` adds the notification tables and an
  index on the events' run identity. `migrate` applies it, then provisions the
  `notifications` deployment, queue, dead-letter queue and subscription.
- Compose gains the `worker-notifications` service. Add it to any Compose override
  that pins images, and to your own stop lists for upgrades, backups and restores.
- Optional settings: `Vessel__Notifications__RetentionDays` (90), `MaxEventAge`
  (7 days), `PopupWindow` (24 hours), `TestCooldown` (10 seconds),
  `HeartbeatTimeout` and `HeartbeatFile`.

Stop web, API and the workers, back up, then upgrade as usual.

### Multi-user wallet sign-in (this delivery)

Keep the private `.env` mode and complete `COMPOSE_FILE` overlay list from
Sign-in mode; verify with `docker compose config --quiet` before migrating.
The Token interpolation changed from `:?` to `:-` to permit Wallet installs.
Without a preflight, a token-less Token install could migrate (including
irreversible `ServerMonitorState`) before the API refuses to start.
The packaged `deploy/migrate` now refuses an empty/whitespace Token, a mode
other than exactly `Token` or `Wallet`, Wallet mode with a token or missing
origin, and anything else the API rejects at startup ([`check-config`](#configuration-check))
**before either EF bundle**.
Do not bypass that wrapper when upgrading.

**Stop every API and worker process on every host/replica, including
`worker-notifications`, and take a private, verified backup first.** This is not a
rolling upgrade. This rebased delivery also includes the breaking OrderIdentity
change above: a v0.2.0 database with imported orders/fills/links must be recreated;
these identity migrations do not supply an order backfill.

Identity's `20261017155708_InitialIdentity` uses its own schema/history. The
journal bundle applies `20261016174509_FundingPayments`,
`20261017091636_OrderIdentity` and `20261017180000_NotificationHub` after
v0.2.0's migrations, followed in order by:

| Journal migration | Effect |
| --- | --- |
| `20261018100000_ServerEventSubscriptions` | Server-wide subscription registrations, separate per-owner progress. Renamed from the unshipped `20261017120000_ServerEventSubscriptions`. |
| `20261018110000_ServerMonitorState` | Copies MAX venue deadlines, creates empty per-deployment monitor heartbeats, drops `monitor_heartbeats` and `monitor_venue_budgets`. Renamed from the unshipped `20261017193000_ServerMonitorState`. |
| `20261018120000_ServerNotificationState` | Creates **empty** `server_notification_heartbeats` keyed by deployment and **drops/replaces** `notification_heartbeats`. Owner liveness is not copied; the notifications worker must complete a productive pass. |

Do not rename applied migration-history rows or run old and new hosts together.
Recreate disposable databases that applied the old development IDs. Rolling back
to an older image requires **restoring the pre-upgrade backup**, not down
migrations. The database role needs database `CREATE` privilege.

Use the explicit `migrate` service: `check-config`, then **identity bundle, then journal bundle**, then
`identity ensure-owner`, then messaging provisioning. No application host
migrates. Token installs continue unchanged without identity configuration
(`ensure-owner` is a no-op); the identity schema is still migrated. Wallet opt-in
also needs the existing owner GUID, allowed HTTPS origin and explicit operator
wallet binding above. Back up both schemas together with the evidence and key
ring. Publishing v0.3.0 does not deploy or migrate any installation.

### From v0.1.3 to v0.2.0 (the account monitor)

This upgrade adds two services and new required settings. Its migrations sort
after v0.1.3's `PlayDirection`, so an older installation applies both in one
migration run. Before it:

- Add `RABBITMQ_USER` and `RABBITMQ_PASSWORD` (URL-safe) to `.env`, and keep
  `Vessel__Auth__OwnerId` set: Token mode uses it for the fixed journal; Wallet mode validates
  message owners through the identity directory. Optional: `VESSEL_RABBITMQ_MANAGEMENT_BIND`,
  `VESSEL_MONITOR_*`, `RABBITMQ_APP_USER`/`RABBITMQ_APP_PASSWORD`.
- Messaging settings are `Vessel__Messaging__*` (for example
  `Vessel__Messaging__RabbitMqUri`, which Compose builds for you).
- Migrations `20261016110000_VesselEvents`, `20261016110100_AccountMonitor`,
  `20261016110200_MessagingDeployments`, `20261016110300_PlayDiscrepancies` and
  `20261016110400_PlayDiscrepancyUnverifiable` add the event log, subscriptions
  and receipts, monitor state, the deployment registrations and discrepancies.
- `migrate` now also runs `provision-messaging` against the new `rabbitmq`
  service, which must be healthy first; then `api` and the new `worker` start.
- The restore procedure gains the mandatory `rotate-event-epoch` step below.

Stop web, API and the workers, back up, then follow the procedure below. A worker
stopped during a run loses nothing: runs are idempotent and the next run repeats
the work.

### From v0.1.2 to v0.1.3

This upgrade includes migration `20261016104756_PlayDirection`, which adds a
`Direction` column to saved Plays, fills it from each Play's plan and adds two
list indexes. Back up the database first and keep web and API stopped while the
explicit migration service runs, as in the [upgrade procedure](#upgrade-procedure).
After the migration, older API images can no longer save new Plays, because
they do not write the new column; roll back by restoring the backup, not by
starting an older image. Storage, credentials and Compose files are unchanged.

### From v0.1.1 to v0.1.2

There is no migration. With no storage settings the API keeps its original
local evidence folder, but the example `compose.yaml` now selects private
RustFS. If you take the new file and keep images locally, add
`-f compose.storage-local.yaml` to every Compose command; without it the stack
uses RustFS, where your existing images are not present. To move them to S3,
follow [Copy existing local images to S3](#copy-existing-local-images-to-s3)
first. Do not use the S3 default with an API image older than v0.1.2. The
PostgreSQL health check now probes TCP, so first-time starts no longer let the
migration service connect before the database is ready.

### From v0.1.0 to v0.1.1

This upgrade includes migration `20261013190344_DiaryCompletion`, which adds
manual-execution records and correction history, retrospective Play provenance,
and structured review with amendment history. Back up the database, evidence
and credential key ring before upgrading. Keep web and API stopped while the
explicit migration service runs. The application never migrates at startup.

Venue and architecture changes introduce no additional migration. The new
Lighter · Robinhood venue uses its own accounts and read-only tokens; existing
Lighter accounts and tokens remain separate. Cross-venue supported-stablecoin
totals use nominal 1:1 values, with no FX or depeg valuation.

### Upgrade procedure

Back up first, read the changelog, and set `VESSEL_VERSION` to the chosen release:
Wallet installs must retain `VESSEL_AUTH_MODE=Wallet`, a blank token and their
complete `COMPOSE_FILE` in the private `.env`. This applies to every bare
command below; explicit `-f` lists must include all those overlays.

```sh
docker compose pull
docker compose stop web api worker worker-notifications
docker compose up -d
docker compose ps -a
```

`up` restarts the completed migration service and applies any pending migrations
before API and both workers start. Re-running it against the same version is idempotent.
Stop web, every API and both workers on all hosts first when a schema change is expected, so no old process
uses the schema while it changes. This
ordering is the explicit deployment migration step, not API startup migration.
Do not use `docker compose restart` to upgrade or apply migrations. Failed
migrations block API startup; inspect the migration logs before proceeding.
Use both `-f` options above when credential storage is configured.
An application downgrade does not roll back the database. Restore backups if a
new schema is incompatible with the old image.

## Backups

Keep backups private and test restores in an isolated installation. Stop web, API
and both workers to keep database references and evidence consistent, then (the RabbitMQ
volume is excluded: subscribers recover unsettled events from PostgreSQL):

Wallet installs retain `VESSEL_AUTH_MODE=Wallet`, a blank token and the complete
`COMPOSE_FILE` in the private `.env` for **all** commands below, including the
final `up`. Never omit the Wallet/TLS/storage/credential overlays on restart.

```sh
umask 077
mkdir -p backups
docker compose stop web api worker worker-notifications
docker compose exec -T postgres pg_dump -U vessel -d vessel -Fc > backups/vessel.dump
docker compose stop rustfs
docker compose run --rm --no-deps -T --entrypoint tar rustfs \
  -C /data -czf - . > backups/objects.tar.gz
docker compose up -d
```

Back up the credential ring **separately**, with restricted permissions and a
separate protected destination. Save the owner GUID and operator settings
privately too. Do not put the keys inside the database or evidence archive.
PostgreSQL 18 stores its data below the named volume mounted at
`/var/lib/postgresql`. Do not copy a live data directory as a substitute for
`pg_dump`. RustFS uses the named `objects` volume at `/data`, owned by UID 10001.
Archive the **entire** stopped volume, including RustFS metadata, and initially
restore with the same RustFS version. This is not a directory of original images;
use the S3 API for cross-provider copies. Restore it, the database and the same key ring into a
disposable deployment before relying on the procedure. The local release
verification exercises the restore below with synthetic records and evidence.

With `compose.storage-local.yaml`, keep web, every API and both workers stopped but replace the RustFS
stop/archive commands with:

```sh
docker compose -f compose.yaml -f compose.storage-local.yaml run --rm --no-deps -T \
  --entrypoint tar api -C /var/lib/vessel/evidence -czf - . > backups/evidence.tar.gz
```

## Isolated restore

Use a new private operator directory, a unique project name and **fresh** named
volumes, not an existing installation. Copy the deploy files and create a private
`.env` with a new database password, the **same** `Vessel__Auth__OwnerId`, and the
saved operator settings. You may rotate the Vessel login token. Select the image
version that produced the backup first. Bind web to an unused loopback port and
choose an unused subnet and pinned service IPs. Do not start web, API or worker yet. The
broker volume is not restored; RabbitMQ starts empty and subscribers recover
unsettled retained events from PostgreSQL.

The examples use the default mode; add every applicable `-f` overlay to each
command. `-p vessel-restore-check` must never name the live project.

```sh
umask 077
# Verify this project does not already exist before creating any volumes.
docker compose -p vessel-restore-check ps -a
docker compose -p vessel-restore-check up -d --wait postgres
# The fresh database is empty. -Fc dumps require pg_restore, not psql.
docker compose -p vessel-restore-check exec -T postgres \
  pg_restore -U vessel -d vessel --no-owner --no-privileges --exit-on-error \
  < /private/backups/vessel.dump
# For a plain SQL dump instead, use:
# docker compose -p vessel-restore-check exec -T postgres \
#   psql -U vessel -d vessel -v ON_ERROR_STOP=1 < /private/backups/vessel.sql

# Extract only your trusted RustFS archive before RustFS/API start.
docker compose -p vessel-restore-check run --rm --no-deps -T \
  --user 0 --entrypoint sh rustfs -c \
  'tar -xzf - -C /data && chown -R 10001:10001 /data' \
  < /private/backups/objects.tar.gz
```

For a local-folder backup, include `compose.storage-local.yaml`, restore
`evidence.tar.gz` using service `api`, path `/var/lib/vessel/evidence` and UID/GID
`1654:1654` instead. Never extract a RustFS archive into the API's evidence root.

Place the original credential ring in a separate mode-700 secret directory,
not the operator directory, database or evidence archive. Preserve every original
key ID and byte, including retired keys needed by retained credentials. On rootful
Docker set the file owner to `1654:1654` and mode to `0600`; apply the mapped UID
for rootless/user-namespace deployments. Use a regular, non-symlink file. Set
`VESSEL_CREDENTIAL_KEYS_FILE` to that absolute path and include
`compose.credentials.yaml` on subsequent commands. Generating a new ring cannot
decrypt the restored credentials.

```sh
# Wallet mode only: mandatory BEFORE any API/worker/provisioning host starts.
# Keep ONLY an independently confirmed reconciliation operator active (optional).
# All commands need compose.wallet.yaml plus the same TLS/storage overlays.
docker compose \
  -p vessel-restore-check run --rm --no-deps --entrypoint dotnet migrate \
  /app/Vessel.Api.dll identity reset-after-restore --keep-active <confirmed-operator-guid>
# Without --keep-active, every user is suspended. Reset revokes all sessions,
# challenges, pending registrations and usable invitations, and prints a review table.
# Compare it against trusted POST-backup records; reapply unbind-wallet, suspend,
# demote, then identity restore --user ONLY for confirmed current users.
# Users/wallets that cannot be confirmed remain suspended. Never blindly restore all.
# Mandatory after every restore and before any host starts: browser cursors from
# before the restore must reset, even once the restored stream reaches their sequence.
docker compose -p vessel-restore-check run --rm --no-deps --entrypoint dotnet migrate \
  /app/Vessel.Api.dll rotate-event-epoch
# This is the explicit migration and provisioning step after restoring, not API startup.
docker compose -p vessel-restore-check run --rm migrate
docker compose -p vessel-restore-check up -d
docker compose -p vessel-restore-check ps -a
curl --fail http://127.0.0.1:YOUR_UNUSED_PORT/health/live
curl --fail http://127.0.0.1:YOUR_UNUSED_PORT/health/ready
```

For Wallet/TLS, use the restored installation's **own hostname** and HTTPS health
URLs instead of the default loopback HTTP probes above. Before provision/start,
compare every reset-table user/role/status/wallet against independently trusted
post-backup records. Reapply removals (`identity unbind-wallet --wallet <guid>`),
suspensions and demotions; only then `identity restore --user <guid>` for confirmed
current users. If a removed address is the last wallet, keep the user suspended
until a trusted replacement can be bound before removal. `--keep-active` never
reactivates an already suspended user. Do not restore all users to bypass the
readiness warning; unconfirmed users stay suspended. Rotate the event epoch
before any hosts start, even when identity reset has already revoked sessions.

**Trusted post-backup records** are independently retained, dated evidence of
changes after the backup, not facts recovered from that same old database:
the private operator command/output log, plus out-of-band confirmation of
self-service wallet links/removals and other identity changes from the affected
person through a previously trusted channel. Do not trust a restored session
or an unverified message from the wallet being checked. The operator using
`--keep-active` must check **their own wallets and role first**, before relying
on that exception. Wallets linked after the backup are absent from the restore
and must be re-linked after confirmed recovery. Keep uncertain users suspended.

Authenticate through the UI with the restored installation's selected mode (Token or a confirmed EOA). Check
that portfolios/accounts and saved Plays are visible under the unchanged owner,
that an original evidence image and its markup open, and that credential metadata
reports configured storage. Readiness confirms database connectivity/migrations,
not decryption or venue access. To test decryption without exposing tokens, use
an isolated stub venue or the approved read-only refresh against the real venue;
do not print ciphertext, keys or plaintext. The local release check tests ring
placement/startup and exact backup bytes, not a real Lighter token refresh.

After the check, remove **only the disposable project** and its volumes:

```sh
docker compose -p vessel-restore-check down -v
```

## Local release verification

From a source checkout, the scripted check builds throwaway amd64 images and
uses its own temp operator directory, unique project/unused subnet, free loopback ports
and synthetic owner/token. It never reads the repository `.env`, uses the owner's
database, pushes images or calls a registry login. Public image pulls are needed.

```sh
python3 scripts/verify-release.py
# Retained local-folder deployment and restore:
python3 scripts/verify-release.py --storage local
# Isolated RustFS/PostgreSQL protocol and CLI check (no app image build):
python3 scripts/verify-evidence-storage.py
# Optional production browser CSP check, using an already available Playwright:
python3 scripts/verify-release.py --browser-module /absolute/path/to/playwright
# When Playwright's default browser is unavailable, set CHROMIUM_PATH to a
# compatible installed Chromium executable before invoking the same check.
```

It checks HTTP and internal-CA TLS trust, spoofed scheme/address handling,
compression, headers, unbuffered SSE via a local fixture, upload limits, unpublished
web in TLS mode and a fresh-volume restore. For delivery it sets
`Vessel__Messaging__Verification=true` on `migrate` and runs the `verification`
subscription in a separate `verifier` worker (deployment `verification`), leaving
the monitor worker's configuration as shipped: receipts prove live delivery, an
outbox that holds the publication while RabbitMQ is stopped, delivery after the
broker returns and after a restore without the broker volume; the subscription is
deregistered at the end. The fixture replaces only the upstream
for its nginx transport test, not production code. Cleanup runs on success/failure.
The browser check uses synthetic API data and intercepts chart market reads, with
no real venue call. Run frontend/.NET checks separately as in `docs/development.md`.

### Wallet acceptance checks

Wallet mode is a documented opt-in, **not activated**: §10 is not signed off.
Keep registration `Closed` until both-browser writes and the owner real-wallet/
non-mainnet EOA check pass and are recorded in `multi-user-state.md`.
CI currently gates Token mode only. The packaged Wallet and browser check is
a **manual pre-release step**, not a CI guarantee.

Synthetic packaged HTTPS check (external signer dependencies, never app packages):

```sh
# In an external tools directory; never use a real key or venue credential.
pnpm --dir /absolute/tools add playwright@1.58.2 @noble/curves@2.4.0 @noble/hashes@2.4.0
PLAYWRIGHT_SKIP_BROWSER_GC=1 pnpm --dir /absolute/tools exec playwright install chromium firefox
NOBLE_MODULE_DIR=/absolute/tools/node_modules \
  python3 scripts/verify-release.py --auth wallet \
  --browser-module /absolute/tools/node_modules/playwright
```

On this rootless host, Firefox runs through the official matching Playwright
server image on the disposable project's network; only its server port is
published on loopback. Pull once, then run this exact manual pre-release check:

```sh
docker pull mcr.microsoft.com/playwright:v1.58.2-noble
export NOBLE_MODULE_DIR=/tmp/vessel-pw/node_modules
export CHROMIUM_PATH=$HOME/.cache/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-linux64/chrome-headless-shell
python3 scripts/verify-release.py --auth wallet \
  --browser-module /tmp/vessel-pw/node_modules/playwright \
  --wallet-browsers chromium,firefox \
  --wallet-browser-image mcr.microsoft.com/playwright:v1.58.2-noble
```

`--wallet-browser-image` requires `--auth wallet --browser-module` and a
pre-pulled image (`docker image inspect` runs before builds). The external
client and image must both use Playwright 1.58.2. The read-only tools mount and
single public forwarder file never expose `.env` or credential keys.
Firefox connects to `https://localhost:<isolated-port>` via the in-container
loopback forwarder to `tls:443`, preserving the Origin and `__Host-` cookie
host. Only Firefox receives the random-path loopback `ws:` endpoint; caller
endpoints are stripped. `ignoreHTTPSErrors` permits Caddy's isolated internal
CA, but Secure/host-only binding and session cookie assertions stay mandatory.
The browser server is removed even on failure and before Compose teardown;
the pre-pulled image is retained for reuse.

`--auth` defaults to `token`. Wallet mode uses its isolated TLS overlay, both EF
bundles, `ensure-owner`, synthetic EOA binding, readiness, real API sign-in,
strict host cookies, guarded writes, sign-out and an older-database restore
whose backup **precedes session revocation**. The reset must prevent that session
from authenticating again. With `--browser-module`, it seeds an invitation and
runs `wallet-browser-smoke.cjs` in Chromium and Firefox, using an EIP-6963
provider bridged to a Node noble signer. `--wallet-browsers chromium` selects a
partial check explicitly; it never counts Firefox as passed. A browser launch
failure fails the command and still cleans up the isolated Compose stack.
The smoke covers invitation registration, shell, POST/PATCH/bodyless DELETE,
multipart evidence, step-up linking, independent users and owner-only notification
delivery/popups (with a foreign-owner read denied), server-side stream
revocation (15 s + 5 s margin) and frontend sign-out. It blocks external calls.

The remaining **human compatibility check** is not replaceable by synthetic
signatures: on an isolated HTTPS installation, use a real browser-wallet EOA
with no funds required, switch the wallet to a non-mainnet network, choose it
in Vessel and inspect the displayed message (correct origin/URI, Chain ID 1,
nonce and expiry), sign without a network switch, redeem a private invitation
if needed and reach the shell. Repeat sign-out/sign-in and link a recovery EOA
through step-up. Confirm there is no transaction, approval, gas or private-key
request. Record wallet/version, browser/version, connected network, date and
outcome in the state checklist. Never put the signature, invitation or session
cookie in that record.

### Full-stack end-to-end test

```sh
python3 scripts/e2e-full-stack.py               # build images, run, clean up
python3 scripts/e2e-full-stack.py --skip-build  # reuse vessel-e2e:api/web from a --keep run
python3 scripts/e2e-full-stack.py --keep        # leave the stack, temp directory and images for inspection
```

It builds the API and web images as `vessel-e2e:api`/`vessel-e2e:web` and runs the
real Compose stack (PostgreSQL, RabbitMQ with the least-privilege application user,
RustFS, migrate, API, monitor and notifications workers, a `verifier` worker for the `verification`
subscription and web) on its own project, unused subnet and loopback ports, with
generated secrets and a synthetic owner. Only Docker and Python are needed on the
host: the fake venue's certificates are generated inside its pinned Python image. `scripts/e2e/fake_venue.py` is a
deterministic Hyperliquid info-API fake on the Compose network. It serves TLS from
a throwaway CA that only the API and worker containers trust (`SSL_CERT_FILE`), so
the production `https`/`wss` host rules stay unchanged, and a loopback-only control
port lets the harness place, fill and cancel orders, list markets and gate or fail
reads between steps. The scenario drives the public API, the SSE stream through
nginx and a Playwright browser that runs in the pinned
`mcr.microsoft.com/playwright:v1.63.0-noble` image. `playwright-core` 1.63 is
installed there with the repository's pinned pnpm (Corepack) into a temporary
Docker volume, so only Docker and Python are needed; `--playwright-module` can
point at an existing host package instead. The scenario covers
explicit provisioning and insert-only registration for both subscriptions,
notification topology and per-deployment health, Refresh, automatic linking by a Check,
fills opening and closing Plays, confirmed and dismissed discrepancies, closed-Play
watches, orphaned entries, omitted executions, broker outage and broker volume
loss, a worker killed mid-read with a second replica recovering after the lease,
a disabled account with a pending Check, admin receipt retry, retention, and
backup/restore with an epoch rotation that resets an old SSE cursor. It sets the
shortest allowed interval, protection grace, lifetimes and retention (one minute,
zero, one hour and one day) and a 15-second manual cooldown (zero is allowed, but a
nonzero cooldown is needed to exercise the countdown and 429 responses) and
shifts three timestamps in the synthetic database instead of waiting an hour or
a day; the fixed five-minute lease is waited out. A run takes about twenty minutes
and prints a pass/fail table; it exits non-zero on any failure. Screenshots and
redacted logs go to a new temp artifacts directory (`--artifacts` to choose).
It never reads the repository `.env`, uses the owner's database or pushes images.

## Logs and troubleshooting

```sh
docker compose ps -a
docker compose logs --tail=100 migrate api web
curl --fail http://127.0.0.1:8080/health/ready
```

Do not publish operator `.env`, `docker inspect`, `docker compose config`, request
bodies or full environment dumps; they can contain secrets. The API logs safe
failure categories, not raw dependency details.

- `/health/live` only proves the API process responds.
- `/health/ready` returns 200 when PostgreSQL is reachable and there are no pending
  migrations. A 503 reveals no database details. It does not verify venue or object-store access.
  Right after start it answers 503 `warming-up` while a read-only warm-up prepares the
  sign-in and first-page reads, for at most `Vessel__Startup__Warmup__Timeout`
  (default `00:00:30`, at most `00:01:00` so it ends inside the healthcheck
  budget); a failed or slow warm-up never stops the API.
  `Vessel__Startup__Warmup__Enabled=false` turns it off.
- If API does not start, check the generated token length, owner GUID, migration
  exit status, database health and optional key-file mode/readability.
- If Lighter reports insecure transport, check HTTPS and trusted proxy addresses.
  Do not switch production to Development to bypass the guard.
- If web returns 502, check API health and `VESSEL_API_UPSTREAM`.
- Uploads allow a 10 MiB image plus 64 KiB multipart overhead. Match this limit in
  the external proxy. API independently validates image size, type and ownership.
- If a different non-Docker deployment mounts an existing evidence directory,
  ensure UID 1654 can write it. Docker initializes a fresh named volume with the
  image directory's ownership.
- For S3 failures, check `storage-init` exit status, RustFS health, bucket name
  and server-only credentials without printing them. Confirm an authenticated
  upload/read, not just API readiness. Missing bucket/access/outage errors do not
  fall back to local files.

## Build locally from source

From the repository root, with no populated secret files in the build context:

```sh
REVISION=$(git rev-parse HEAD)
docker build --platform linux/amd64 -f backend/Dockerfile \
  --build-arg VERSION=0.3.2 --build-arg REVISION="$REVISION" \
  -t skorcius/vessel:api-0.3.2 .
docker build --platform linux/amd64 -f frontend/Dockerfile \
  --build-arg VERSION=0.3.2 --build-arg REVISION="$REVISION" \
  -t skorcius/vessel:web-0.3.2 .
```

The API build pins SDK `10.0.302` to match the repository's `global.json`. The
moving `sdk:10.0` tag can contain a different feature band that the SDK pin rejects.
It uses locked NuGet
restore and local EF tool manifest. It includes the migration bundle in the same
runtime image. The web build uses Node 24, Corepack, the pinned pnpm and frozen
workspace lockfile. Neither build needs the database, auth token or key ring.
The root `.dockerignore` applies to both builds. Hashed assets are immutable for
one year; `index.html` and SPA fallbacks use `no-cache`. API data remains
`no-store`, and SSE proxy buffering is off.
