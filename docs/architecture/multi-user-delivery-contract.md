# Multi-user wallet sign-in: delivery contract

October 17, 2026, **version 3**. Status: **delivery contract for implementation.**

On October 17 the owner asked to start orchestrating the work, with Opus and Sol (Codex) agents to discuss and verify the approach. This version folds in three review rounds:
- Round 1: both reviewers said "go with changes".
- Round 2: Opus said "ready with edits" and Sol said "not ready".
- Round 3: Opus said "ready". Sol said "not ready", with two small gaps (the step-up signer and fail-closed restore reconciliation).

Every required edit is applied or explicitly decided in §14, and the round-3 fixes are in §15. The discussion is recorded in [the review log](multi-user-review-log.md).

**October 25, 2026 amendment: wallet sign-in only.** The owner removed Token sign-in. D3 is superseded: `Vessel:Auth:Mode` is retired (absent or `Wallet` is accepted, any other value refuses startup) and a nonempty `Vessel:Auth:Token` refuses startup in every environment, including `check-config` and the identity commands. The Token rows of §3, the bearer scheme and its limiter, the configured-owner directory (§9.1), `AlwaysValidPrincipal` (§9.5) and the `bearer` transport and Token gate of §11 no longer exist; `/api/auth/config` answers `{registration, originAccepted}`. Readiness always requires an active operator with a bound wallet. Workers no longer register authorization. Everything else stands, and §10 is still not signed off. State: [multi-user-state.md](multi-user-state.md#wallet-only-sign-in--october-25-2026).

Where this contract and [the analysis](multi-user-wallet-auth.md) disagree, this contract wins. The screens are in [`docs/design/wallet-sign-in/`](../design/wallet-sign-in/README.md).

Implementers must not decide anything this contract leaves open. Ask the orchestrator instead.

## 1. Adopted decisions

The owner has not answered D1–D12. These are the working defaults, adopted from the recommendations as amended by the reviews. Each is a configuration value or a small code change, so the owner can override them.

| # | Decision |
| --- | --- |
| D1 | Registration policy `Invitation` by default. Policies: `Closed`, `Invitation`, `AllowList`, `Open`. Wallet mode forces `Closed` in code until B2 (§10). |
| D2 | The configured owner GUID becomes user 1 through the idempotent `identity ensure-owner` command (role `operator`). The owner's wallet is attached with `identity bind-wallet`. No migration seeds data. |
| D3 | *(Superseded October 25, 2026: wallet sign-in only; see the amendment above.)* `Vessel:Auth:Mode` = `Token` (the default when the key is absent) or `Wallet`. The default is never flipped implicitly. In Wallet mode the bearer scheme is not registered, and startup fails if `Vessel:Auth:Token` is set. |
| D4 | No WalletConnect and no CSP change. |
| D5 | Regular wallets (EOAs) only. Unsupported signature formats get their own code; Vessel never claims to know the wallet type. |
| D6 | Several wallets per user; the last one cannot be removed. Wallet changes are serialized per user. |
| D7 | Operator tasks are offline commands first. "The operator cannot see journals" is application authorization only, not protection from whoever administers the host or database. |
| D8 | **"Keep me signed in on this device"**, unchecked by default. Unchecked: a browser-session cookie with 12 h idle and 24 h absolute expiry. Checked: a persistent cookie with 14 days idle and 30 days absolute expiry. Browsers may restore session cookies; the server-side bounds are the real limit. |
| D9 | The same venue account may be followed by several journals. Venue deadlines are server-wide (§9.4). |
| D10 | Per-user preference keys, plus a device-level theme key. Only the migrated owner adopts the legacy keys. A full reload on sign-out and on a user change. |
| D11 | No user or journal deletion. Suspension is defined (§9.6). |
| D12 | `Chain ID: 1` fixed. |

## 2. Pull requests, lanes and write scopes

```text
base: t3/wallet-based-user-auth (this contract + shared ports)
├─ Lane A (Sol):   A1 identity core ─► A2 identity account management
├─ Lane B (Sol):   B1 owner scopes + events ─► B2 monitor + venue limits (lifts the registration gate)
├─ Lane C (Opus):  C1 isolation suite (tests only)
└─ Lane D (Opus):  D1 frontend identity
then P4: deployment, release checks, runbooks
```

Each lane works in its own worktree from the base commit of branch `t3/wallet-based-user-auth`. Lanes B, C and D build against this contract, not against lane A's code.

### Write scopes

Each lane edits **only** these paths. Any lane may also fix existing tests broken by its own change, and must list them in its PR description.

- **A1:**
  - `backend/src/Vessel.Identity/**` and `backend/tests/Vessel.Tests/Identity/**`
  - `backend/Vessel.slnx`, `backend/Dockerfile`, `deploy/migrate`
  - in `Vessel.Api`:
    - the auth, identity, readiness and composition sections of `Program.cs`;
    - `ProductionConfiguration.cs`, `Vessel.Api.csproj`, `packages.lock.json`;
    - revalidation and the global cap in `VesselEventEndpoints.cs`;
    - revalidation only in `MarketStreamEndpoint.cs`.
  - the auth registration section of `Vessel.Infrastructure/DependencyInjection.cs`
  - `Vessel.Worker/Vessel.Worker.csproj`, `Vessel.Worker/packages.lock.json`, and one composition line in `Vessel.Worker/Program.cs`
  - `backend/tests/Vessel.Tests/packages.lock.json`, the project rows of `ArchitectureTests.cs`, and existing auth and readiness fixtures
- **A2:** lane A paths.
- **B1:**
  - in `backend/src/Vessel.Persistence/**`:
    - everything except the monitor files, plus `MonitorRegistration.cs` for the scope-factory change;
    - `Migrations/**`, including the snapshot.
  - `backend/src/Vessel.Infrastructure/Auth/HttpJournalOwnerContext.cs`, and the owner-context registration lines in `Vessel.Infrastructure/DependencyInjection.cs`, a section separate from A1's auth section
  - `backend/src/Vessel.Messaging/**`
  - `backend/src/Vessel.Worker/**` except A1's line
  - `backend/src/Vessel.Application/Events/**`
  - event, messaging and worker tests in `backend/tests/Vessel.Tests/**`, excluding `Identity/**` and `Isolation/**`
  - a new `OwnerScopeArchitectureTests.cs`
  - the `IJournalOwnerInventory` implementation
- **B2:**
  - in `Vessel.Persistence`: monitor files, `MonitorModel.cs`, the new `SystemMonitorStore.cs` and `ServerStateStore.cs`, and `Migrations/**`, including the snapshot
  - `Vessel.Domain/Monitoring/MonitorAccount.cs`
  - in `Vessel.Application`: `Monitoring/**`, `MarketData/**`, `Workspace/AccountDiscoveryService.cs`, `Credentials/AccountCredentialService.cs`
  - `Vessel.Worker/MonitorScheduler.cs`
  - market-data and monitor tests
  - the gate constant in `Vessel.Identity`: one line
- **C1:** `backend/tests/Vessel.Tests/Isolation/**` only.
- **D1:** `frontend/src/**`, `frontend/package.json` (dev dependencies only) and `pnpm-lock.yaml`.
- **P4:** `deploy/**`, `scripts/**`, `docs/**` and `AGENTS.md`.

### Shared ports

These are already in the base commit. `Application/Common/ProblemException` gained optional `Code` (the ProblemDetails `code` extension) and `RetryAfterSeconds` (the `Retry-After` header). The rest are in `backend/src/Vessel.Application/Ownership/`:
- `IJournalOwnerDirectory`, with `JournalOwnerStatus` and `ConfiguredOwnerDirectory`
- `IOwnerScopeFactory` / `IOwnerScope`
- `OwnerScopeAssignment`
- `IPrincipalValidity`, with `AlwaysValidPrincipal`
- `IJournalOwnerInventory`

Only the orchestrator changes them.

### Merge order and release

The merge order is A1 → B1 → A2 → B2 → C1 → D1 → P4, and the orchestrator integrates. Expected overlaps:
- A1 and B1 on `Vessel.Worker/Program.cs` and `Infrastructure/DependencyInjection.cs`, in separate sections;
- A1 and P4 on `deploy/migrate`;
- B1 and B2 on `MonitorScheduler.cs` and the migrations.

**No release tag** is cut from `main` between A1 and P4, and Wallet mode is not documented for operators before P4.

## 3. Configuration

All keys are read identically by the API, the worker and offline commands. Validation messages name settings, never values. Invalid values refuse startup.

| Key | Values and default | Rules |
| --- | --- | --- |
| `Vessel:Auth:Mode` | `Token` (default when absent) \| `Wallet` | Any other value refuses startup |
| `Vessel:Auth:Token` | — | Token mode: required outside Development (unchanged). Wallet mode: absent or empty, otherwise refuse to start |
| `Vessel:Auth:OwnerId` | GUID | Token mode: as today, and the default GUID stays accepted. Wallet mode: required outside Development; used by `ensure-owner` and legacy-preference adoption. In Wallet mode only, the well-known default GUID is refused outside Development unless `Vessel:Identity:AllowLegacyDefaultOwner=true` (logged at startup as a warning) |
| `Vessel:Auth:OwnerName` | `Owner` | Display name given by `ensure-owner` |
| `Vessel:Identity:Origins` | list | Wallet mode: required, at least one entry. Each entry is normalized to `scheme://host[:port]` (lower-case host, no path, no default port, no trailing slash). **Outside Development every origin must be `https://`.** In Development `http://` origins are allowed |
| `Vessel:Identity:Registration` | `Invitation` | `Closed`, `Invitation`, `AllowList`, `Open`. Forced to `Closed` while the §10 gate is in place |
| `Vessel:Identity:AllowList` | empty | `0x` addresses, normalized to lower case |
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

**Cookies over plain HTTP:**
- Only in Development, and only for an `http://` entry of `Origins`, does the API drop the `__Host-` prefix and the `Secure` attribute from both cookie names. The session cookie becomes `vessel-session` and the binding cookie `vessel-signin`.
- `https://` origins always get the configured `__Host-` names with `Secure`.
- Ports do not isolate cookies. Each Vessel installation needs **its own hostname**, which P4 documents as a requirement.

## 4. Identity persistence and packaging

- **Project.** `Vessel.Identity` (net10.0) references only `Vessel.Application`. Its packages are:
  - EF Core with Npgsql, at the same versions as Persistence;
  - `Microsoft.EntityFrameworkCore.Design` (`PrivateAssets=all`);
  - Nethereum.Signer 7.x;
  - the ASP.NET Core framework reference.

  The API and the worker reference it; no other project does. `ArchitectureTests` gets an `Identity` row `"Application"`, and the `Api` and `Worker` rows gain `Identity`.
- **Database.** `IdentityDbContext` uses `HasDefaultSchema(schema)`.
  - Its history table is `__EFMigrationsHistory` in that schema.
  - Identity has its own transaction helper and never uses the journal `IUnitOfWork`.
  - There are no foreign keys between schemas.
  - The tables below are not `IOwned`, and `VesselDbContext` never maps them.
- **Design time.** `IdentityDbContextFactory` reads `ConnectionStrings__Vessel` and `Vessel__Identity__Schema`, and needs neither a live database nor auth configuration.
- **Migrations for an isolated schema.** Migrations take the schema from an injected `IdentitySchema` through a replaced `IMigrationsAssembly`. The model cache key includes it. An isolated test schema therefore runs the real migrations, history table included. Tests assert that neither `identity` nor `public` was touched.
- **Bundles.** The Dockerfile builds and copies a second executable bundle, `/app/migrate-identity-ef`. `deploy/migrate` runs the steps below with `set -e`, so a failure stops the script:
  1. `/app/migrate-identity-ef`, with no arguments;
  2. the journal bundle, with `"$@"` as today;
  3. `identity ensure-owner`, always. It reads the effective mode from all configuration sources and does nothing (exit 0, with a log line) in Token mode;
  4. `provision-messaging`.

  Token-mode upgrades therefore need no new configuration. The mode value must be exactly `Token` or `Wallet`.
- **Readiness.** `/health/ready` checks both contexts. In Wallet mode it also answers 503 with exactly `{"status":"unavailable","reason":"no-operator-wallet"}` while no active operator has a wallet. Other failures stay `{"status":"unavailable"}`.

| Table | Columns | Constraints |
| --- | --- | --- |
| `users` | `id uuid`, `display_name varchar(60)`, `role text` (`member`, `operator`), `status text` (`active`, `suspended`), `created_at`, `updated_at`, `suspended_at null`, `revision int` | PK `id`. New users get a random v4 GUID; only `ensure-owner` uses the configured GUID |
| `user_wallets` | `id uuid`, `user_id`, `address char(42)` (lower-case `0x…`), `label varchar(40) null`, `added_at`, `last_used_at null` | PK `id`, FK `user_id`, **unique `address`** |
| `auth_challenges` | `id uuid`, `purpose text` (`sign-in`, `link`, `step-up`), `address`, `origin`, `message text` (exact signed text), `binding_hash bytea null`, `user_id null`, `session_id null`, `action text null`, `target text null`, `issued_at`, `expires_at`, `consumed_at null` | Index on `expires_at`. At most 10 000 unconsumed rows; the oldest are evicted at the cap |
| `pending_registrations` | `id uuid`, `token_hash bytea`, `address`, `origin`, `binding_hash`, `created_at`, `expires_at`, `consumed_at null` | Unique `token_hash`. Lifetime 10 min. Capped by `MaxPendingRegistrations` |
| `sessions` | `id uuid`, `user_id`, `wallet_id null` (FK, `ON DELETE SET NULL`), `token_hash bytea`, `remember bool`, `created_at`, `last_seen_at`, `idle_expires_at`, `absolute_expires_at`, `revoked_at null`, `revoke_reason text null`, `step_up_action null`, `step_up_target null`, `step_up_wallet_id null` (the wallet that signed the grant), `step_up_expires_at null`, `user_agent_summary varchar(80) null` | Unique `token_hash`. **Invariant:** a live session always references an existing wallet of its user. Removing a wallet revokes its sessions in the same transaction, so only revoked sessions can have a null wallet. No IP address stored |
| `invitations` | `id uuid`, `code_hash bytea`, `note varchar(80) null`, `created_at`, `expires_at`, `max_uses int`, `uses int`, `revoked_at null` | Unique `code_hash` |

**Other rules:**
- **Cleanup.** An hourly hosted cleanup in the API deletes expired challenges and pending registrations, and sessions that expired or were revoked more than 30 days ago.
- **Display names** are trimmed, 1–60 characters, printable Unicode only (no control or format characters), and need not be unique.
- **Labels** are null or trimmed printable Unicode of up to 40 characters; blank becomes null.
- **`user_agent_summary`** is the browser family and OS from `User-Agent`, for example `Firefox on Linux`, at most 80 characters, otherwise null.

## 5. The signed message

The server writes and stores the exact text. Lines end in LF (`\n`) with no trailing newline:

```text
{domain} wants you to sign in with your Ethereum account:
{address}

{statement}

URI: {uri}
Version: 1
Chain ID: 1
Nonce: {nonce}
Issued At: {issued}
Expiration Time: {expires}
```

**Fields:**
- `domain`: the host from the normalized origin, plus `:port` when the port is not the default. Every `http://` origin writes the scheme too, for example `http://100.123.45.42:5180`.
- `uri`: the origin followed by `/`.
- `address`: EIP-55 checksummed. Input addresses must be 40 hex characters. A mixed-case input whose EIP-55 checksum fails is refused with `address-invalid`; all-lower or all-upper input is accepted.
- `nonce`: 22 random base62 characters, at least 128 bits.
- `issued` and `expires`: UTC, `yyyy-MM-ddTHH:mm:ss.fffZ`. The lifetime is 5 minutes, accepted with 30 seconds of clock skew.

**Statements** (ASCII, one line):
- `sign-in`: `Sign in to Vessel. This is not a transaction and costs no gas.`
- `link`: `Link this wallet to the Vessel journal signed in with {sessionWalletAddressEip55}. This is not a transaction.`
- `step-up`, action `wallet.link`: `Confirm adding a sign-in wallet to your Vessel journal. This is not a transaction.`
- `step-up`, action `wallet.remove`: `Confirm removing sign-in wallet {targetAddressEip55} from your Vessel journal. This is not a transaction.`

**Signing in the browser:** `personal_sign` with params `["0x" + hex(UTF-8 bytes of message), address]`.

**Verification:**
- The message is `keccak256("\x19Ethereum Signed Message:\n" + decimal byte length + bytes)` over the stored text. Recover the address from the signature and compare it as 20 bytes with the challenge address.
- A signature must be `0x` followed by 130 hex characters (65 bytes), with `r` and `s` in `[1, n-1]` and `v` in {0, 1, 27, 28}. A high `s` is replaced by `n - s` with the recovery parity flipped, then accepted.
  - The ERC-6492 suffix, any other length, or another `v` → `signature-unsupported`.
  - A 65-byte signature that does not parse or recover → `signature-invalid`.
  - A signature that recovers another address → `signature-mismatch`.

## 6. HTTP API

**Token mode:**
- Only `GET /api/auth/config` exists among the auth routes.
- Every other `/api/auth/{**rest}` answers 404 `not-found`, anonymous requests included, not the fallback policy's 401.

**Wallet mode:**
- All auth responses are `no-store`.
- Auth request bodies are capped at 16 KiB.

**Errors** are ProblemDetails with a `code` extension:

| Status | Codes |
| --- | --- |
| 400 | `request-invalid` (malformed body), `address-invalid`, `challenge-invalid` (unknown, used, wrong purpose, binding cookie missing or mismatched), `challenge-expired`, `signature-invalid`, `signature-unsupported`, `signature-mismatch`, `registration-invalid`, `display-name-invalid`, `label-invalid` |
| 401 | `session-required`, `session-expired`, `session-revoked`, `session-user-mismatch` |
| 403 | `origin-rejected` (origin not accepted, or differs from the challenge origin), `csrf-rejected`, `registration-closed`, `invitation-required`, `invitation-invalid`, `user-suspended`, `step-up-required` (with an `action` extension), `operator-required` |
| 404 | `not-found` |
| 409 | `wallet-taken`, `wallet-already-linked`, `last-wallet`, `revision-conflict`, `session-changed` |
| 413 | `request-too-large` |
| 429 | `rate-limited`, with `Retry-After` |
| 503 | `identity-unavailable` |

### Public (A1)

**`GET /api/auth/config?origin=<window.location.origin>`**
- Returns `{mode:"token"}` or `{mode:"wallet", registration:"closed"|"invitation"|"allow-list"|"open", originAccepted:boolean}`.
- `originAccepted` compares the query value with the normalized list; it is only a UI hint, and only the guarded `Origin` header is trusted.
- `registration` is the effective policy, after the gate. The origin list is never returned.

**`POST /api/auth/challenge`** `{address}`
- Returns `{challengeId, message, expiresAt}`.
- Purpose `sign-in`, origin taken from the guarded `Origin`.
- **Binding cookie:** a valid one sent with the request is reused, so two tabs signing in don't invalidate each other. Otherwise a new 32-byte random value is set for 10 minutes. The challenge stores the hash.
- **Never reads users, wallets or the allow-list.**

**`POST /api/auth/verify`** `{challengeId, signature, remember:boolean}`
- **Checks:** the challenge is consumed in every outcome, inside one identity transaction. Origin, purpose, expiry and the binding cookie are checked first. Users, wallets and the allow-list are read **only after** a valid signature.
- **Known active wallet:** `{status:"signed-in", session:SessionDto}`. Sets the session cookie, revokes the session of any session cookie sent with the request, and clears the binding cookie.
- **Unknown wallet that the policy admits:** `{status:"registration-required", registration, requiresInvitation, expiresAt}`. Re-issues the binding cookie with a fresh 10-minute lifetime and stores its hash in the pending registration.
- **Otherwise:** 403 `registration-closed` (Closed, or an AllowList address not on the list), or `user-suspended`.

**`POST /api/auth/register`** `{registration, displayName, invitation?, remember}`
- Returns 201 `{session:SessionDto}`, sets the session cookie and clears the binding cookie.
- **Validation first:** display name, invitation format and policy are checked without consuming anything.
- **Then one identity transaction:**
  1. Consume the pending registration (binding cookie and origin must match; unexpired; unused).
  2. Consume the invitation atomically.
  3. Insert the user (`member`, `active`), the wallet and the session.
- If any step fails, the transaction rolls back, so a valid pending registration is never burned by a failed attempt.

### Session (A1 for session and sign-out, A2 for the rest)

| Endpoint | Request | Success | Notes |
| --- | --- | --- | --- |
| `GET /api/auth/session` | — | `SessionDto` | The only protected route that does not require `X-Vessel-User` |
| `POST /api/auth/sign-out` | `{expectedSessionId}` | 204, clears the cookie | Revokes only when the cookie's session is `expectedSessionId`; otherwise 409 `session-changed` with no revocation and no cookie change |
| `GET /api/auth/sessions` | — | `[{id, current, createdAt, lastSeenAt, remember, userAgent, walletAddress}]` | A2. Live sessions only |
| `DELETE /api/auth/sessions/{id}` | — | 204 | A2. Own sessions only, otherwise 404. Deleting the current session behaves like sign-out |
| `POST /api/auth/sessions/revoke-others` | — | 204 | A2 |
| `PATCH /api/auth/profile` | `{displayName, expectedRevision}` | `UserDto` | A2 |
| `GET /api/auth/wallets` | — | `[WalletDto]` | A2 |
| `POST /api/auth/step-up/challenge` | `{action:"wallet.link"\|"wallet.remove", target?:walletId, address}` | `{challengeId, message, expiresAt}` | A2. `address` must be one of this user's wallets (otherwise 400 `address-invalid`); `target` is required for `wallet.remove`. The challenge stores the session, action and target |
| `POST /api/auth/step-up` | `{challengeId, signature}` | 204 | A2. See the completion rules below |
| `POST /api/auth/wallets/challenge` | `{address}` | `{challengeId, message, expiresAt}` | A2. Requires a live `wallet.link` grant (otherwise 403 `step-up-required`). Does not check whether the address is linked anywhere |
| `POST /api/auth/wallets` | `{challengeId, signature, label?}` | 201 `WalletDto` | A2. See the completion rules below |
| `PATCH /api/auth/wallets/{id}` | `{label}` | `WalletDto` | A2 |
| `DELETE /api/auth/wallets/{id}` | — | 204 | A2. See the completion rules below |

**Completion rules (A2)** apply to step-up, link and remove.
- **Serialization:** each completion runs in one identity transaction serialized on the user row (`SELECT … FOR UPDATE`).
- **Re-checked at completion:**
  - the challenge's session is the request's live session;
  - the user is active;
  - the step-up signer is still linked to this user;
  - the grant's action and target match the call.
- **Atomic consumption:** the challenge and grant are consumed with the mutation, so concurrent reuse allows at most one success.
- **Step-up** sets a single grant on the session: the action, the target, the signing wallet (`step_up_wallet_id`) and 5 minutes.
  - At link or remove completion, under the user lock, that exact wallet must still be linked to the user.
  - Every unlink path (the API and `identity unbind-wallet`) clears grants signed by the removed wallet, in the same transaction.
- **Link:**
  - the address is already linked to this user → 409 `wallet-already-linked`;
  - it is linked to another user → 409 `wallet-taken`.
- **Remove** rechecks `last-wallet` under the lock and revokes the wallet's sessions in the same transaction. If that includes the current session, its cookie is cleared.

### DTOs

- `SessionDto = {user: UserDto, wallet: WalletDto, session: {id, createdAt, idleExpiresAt, absoluteExpiresAt, remember}, adoptsLegacyPreferences: boolean}`
  - `adoptsLegacyPreferences` is true only for the user whose ID equals `Vessel:Auth:OwnerId`.
- `UserDto = {id, displayName, role:"member"|"operator", revision}`
- `WalletDto = {id, address, label, addedAt, lastUsedAt, current}`. `current` marks the wallet that authenticated this session; `address` is EIP-55.

**`GET /api/system`:** unchanged in shape. Its `owner` is the signed-in user.

## 7. Sessions, request guard and rate limits

### Sessions

- **Cookie.** The value is 32 random bytes in base64url; only their SHA-256 is stored. Attributes: `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, no `Domain`, except the Development HTTP variant in §3. With "keep me signed in", `Max-Age` equals the absolute lifetime; otherwise it is a browser-session cookie.
- **Error handler (A1).** The API error handler in `Program.cs` writes `ProblemException.Code` as the `code` extension and `RetryAfterSeconds` as `Retry-After` after `Response.Clear()`. Today `Clear()` drops the `Retry-After: 5` that `VesselEventEndpoints.cs:38` sets. All auth errors, both event-stream caps (A1) and B2's per-user caps use it, and tests assert the header.
- **Validation** reads the database on every request, with **no cache**. A request without a cookie never touches the database.
  - The session must not be revoked or past its idle or absolute expiry.
  - Its user must be active.
  - Its wallet must be present.
- **Idle refresh.** One conditional `UPDATE … WHERE last_seen_at < now() - interval '5 minutes'` outside any journal unit of work.
- **Claims** are the same `NameIdentifier`, `Name` and authentication type as today, plus `vessel:session` (the session ID). Principal-to-owner mapping stays as it is.
- **Owner context.** `HttpJournalOwnerContext` returns `OwnerScopeAssignment.OwnerId` when set (B1), otherwise the claims. It throws when both are present and differ.
- **Suspension** revokes every session of the user (`revoke_reason = suspended`).

### Tab binding

- **Exempt routes.** Public auth routes (`config`, `challenge`, `verify`, `register`) and anonymous health routes are exempt, even when a session cookie is present.
- **Header rule.** Every other cookie-authenticated `/api` request except `GET /api/auth/session` must send `X-Vessel-User: <user id>`. A missing or different value answers **401 `session-user-mismatch`**.
- The server never selects an owner from the header.

### Request guard

The guard applies to cookie-authenticated unsafe methods (POST, PUT, PATCH, DELETE) and to every public `/api/auth` POST:
- `Origin` must be present and match an accepted origin exactly. `null` is refused.
- `Sec-Fetch-Site`, when present, must be `same-origin`. It is absent on plain-HTTP LAN origins.
- Public auth POSTs must send `X-Vessel-Request: 1`. Authenticated requests already send `X-Vessel-User`. Either header forces a CORS preflight, which Vessel never grants, and no CORS is configured.
- There is no content-type rule, so multipart evidence uploads keep working.

**Sending a real Origin.** Every API fetch from the browser sets `referrerPolicy: "same-origin"`. The page's `Referrer-Policy: no-referrer` would otherwise make browsers send `Origin: null` on writes.

### Streams

- The event stream and market streams call `IPrincipalValidity.StillValidAsync` on a **15 s timer that runs regardless of traffic**, and end the response when it turns false or the check fails.
- Each check has a 10 s deadline on the injected clock that does not depend on the check honouring cancellation; a check that times out fails closed and ends the response, so a revoked session's stream ends within about 25 s even while the identity database stalls. The deadline also cancels the database command.
- Revalidation never extends the idle expiry.
- `Vessel:Events:MaxStreams` caps event streams per process, beside the existing 4 per owner. A refusal answers 429 with `Retry-After: 5`.

### Rate limits

ASP.NET Core rate limiter, partitioned by client IP after forwarded headers (IPv6 grouped by /64). Idle partitions are cleaned up.

| Endpoint | Per client | Global |
| --- | --- | --- |
| challenge | 10 per minute | 300 per minute |
| verify, register | 20 per minute | 600 per minute |
| register with invitation | 10 per hour | — |
| step-up and wallet link | 10 per 10 min, per session | — |

There are no per-wallet-address counters.

### Invitations

- **Format:** 128 random bits, shown as `VSL-` plus 26 Crockford base32 characters in groups of 5-5-5-5-6.
- **Normalization before hashing:**
  1. strip `VSL-` (case-insensitive);
  2. remove `-` and whitespace;
  3. upper-case;
  4. map `I` and `L` to `1` and `O` to `0`;
  5. require exactly 26 characters.
- **Storage:** the code is shown once by the CLI and stored as its SHA-256.
- **Consumption:** atomic `UPDATE … SET uses = uses + 1 WHERE code_hash = @h AND revoked_at IS NULL AND expires_at > now() AND uses < max_uses RETURNING id`.

## 8. Offline commands (A1, A2)

They run as `dotnet Vessel.Api.dll identity <command>`. Exit codes: 0 success, 1 error, 2 usage, 3 precondition failed. Output names IDs, addresses and counts, never secrets. Codes are printed only by `invite`.

| Command | PR | Effect |
| --- | --- | --- |
| `identity ensure-owner` | A1 | Wallet mode: creates the configured owner as user 1 (`operator`, `active`, `OwnerName`) if absent; idempotent; refuses the default GUID per §3. Token mode: does nothing, exit 0 |
| `identity bind-wallet --user <guid> --address 0x… [--label]` | A1 | Exit 3 if the address belongs to another user |
| `identity invite [--note] [--expires 7d] [--max-uses 1]` | A1 | Prints the code once |
| `identity unbind-wallet --wallet <id>` | A2 | Refuses the last wallet, and revokes that wallet's sessions |
| `identity suspend --user` / `identity restore --user` | A2 | Suspend revokes all of the user's sessions |
| `identity promote --user` / `identity demote --user` | A2 | Refuses to demote the last active operator |
| `identity revoke-sessions --user <guid> \| --all` | A2 | |
| `identity reset-after-restore` | A2 | See below. **Mandatory before any host starts after a restore** |
| `identity verify` | A2 | Read-only. API-level orchestration over identity reads and `IJournalOwnerInventory` (B1). Reports journal owner IDs without a user and users without wallets. Identity never queries journal tables |

`identity reset-after-restore [--keep-active <user guid> …]`:
- **suspends every user except those listed** (normally the operator who will reconcile), so a restored, previously removed wallet or a user whose later suspension was lost cannot obtain a fresh session;
- revokes every session;
- deletes challenges and pending registrations;
- revokes still-usable invitations;
- prints a review table of users (ID, role, status) and their wallets (ID and address).

**Reconciliation.** All API and worker hosts stay stopped until both the reset and the reconciliation are done. The operator compares the table with trusted records made after the backup, then:
- reapplies wallet removals (`unbind-wallet`), suspensions and demotions;
- restores only the users confirmed as current (`identity restore --user`).

Users who cannot be confirmed stay suspended pending recovery. P4 documents this in the restore runbook.

## 9. Multi-owner backend (B1, B2)

### 9.1 Owner scopes and system stores (B1)

- **Factory.** `IOwnerScopeFactory` is implemented in `Vessel.Persistence`. It creates a fresh scope, assigns that scope's `OwnerScopeAssignment` (registered as scoped) and returns it.
  - It rejects an empty GUID.
  - It never mutates an HTTP request scope.
  - It never consults the directory.
- **Worker hosts** stop registering `WorkerOwnerContext`. They use `HttpJournalOwnerContext`, which throws when the scope has no assignment and no principal.
- **No plain scope factories.** Every background scope uses the factory, at these sites:
  - `MonitorRegistration.cs:63-72` (renewals, including API-triggered Refresh);
  - `MonitorScheduler.cs:64,93,108`;
  - `SubscriptionMaintenanceService.cs:33,45`;
  - `EventSubscriptionListener.cs:47`;
  - `MonitorCommandListener.cs:23`;
  - `MessagingProvisioning.cs:52,71,85`, inside the `RegistrationServices` container built at `:90-95`. That container cannot reference Infrastructure, so B1 adds `Vessel.Persistence.AssignedOwnerContext : IJournalOwnerContext`, which returns the assignment and throws when it is unset. B1 registers it there with `OwnerScopeAssignment` and the factory, replacing `ConfiguredOwner` (`:94`, `:102`):
    - `:52` (provisioning) and `:71` (server-wide deregister) become system-store calls;
    - `:85` (`retry-event-receipts`) uses the factory per owner named by `--owner` or `--all-owners`.
  - `MessagingHosting.cs:62`: `MessagingHost.OwnerId` is removed; listeners take the owner from each message.

  `OwnerScopeArchitectureTests` forbids `IServiceScopeFactory`, `CreateScope` and `CreateAsyncScope` in `Vessel.Worker`, `Vessel.Messaging` and the monitor files, except in the factory itself.
- **Owner directory registration** is order-independent:
  - B1 registers `services.TryAddSingleton<IJournalOwnerDirectory>(new ConfiguredOwnerDirectory(JournalOwner.Parse(...)))` in the owner-context section of `AddVesselInfrastructure`, which both the API and the worker call. B1 cannot edit `Api/Program.cs`.
  - In Wallet mode, A1's `AddVesselIdentityDirectory(configuration)` uses `services.Replace(...)` with the identity implementation.
- **System stores** are singletons over an `NpgsqlDataSource` from `ConnectionStrings:Vessel`, and never use `VesselDbContext` or an owner scope. They cover:
  - subscription registrations;
  - owner discovery for catch-up and retention;
  - `IJournalOwnerInventory`;
  - `DueCandidatesAsync`;
  - `IServerStateStore`.

  The one exception is the venue count in §9.4.
- **Inventory test.** `OwnerScopeArchitectureTests` also lists every raw-SQL site without an owner predicate:
  - `EvidenceCopyCommand.cs:66`;
  - `EventStreamEpoch.cs:17`;
  - `Vessel.Messaging/MessagingDeployments.cs`;
  - every new system-store statement.

  It also asserts zero `IgnoreQueryFilters` in `src`. Any new site fails the test until it is reviewed.
- **Heartbeat.** B1 writes the heartbeat for each active owner, as today's per-owner table requires. B2 moves it to `IServerStateStore` (§9.4).
- **Interim scheduler (B1).** `MonitorScheduler` loops over `ActiveOwnersAsync` pages and calls the existing owner-scoped `ClaimDueAsync` in a factory scope per owner, keeping the process-wide cap of 2. Renewals take the owner from the executor's scope until B2 adds `MonitorLease.OwnerId`.
- **Out of scope:** the analysis's two integrity foreign keys (`monitor_exit_watches.LinkId`, `play_order_links.AccountId`). They need their own orphan audit.

### 9.2 Event subscriptions (B1)

The design is subscriber-side, as Opus proposed, with corrections from both reviewers in round 2. Publishers never read or write subscription state, and `VesselEventStore` is unchanged.

**B1 migration:**
- Create `event_subscription_registrations(name varchar(48) PK, module varchar(48), filters text[], filter_fingerprint char(64), registered_at timestamptz)` in the journal schema.
- Backfill it with `DISTINCT ON (name)` from `event_subscriptions`. Fail the migration if two owners hold different fingerprints for one name.
- Then add `FOREIGN KEY (name) REFERENCES event_subscription_registrations ON DELETE CASCADE` on `event_subscriptions`. Receipts keep their FK to progress rows.
- Existing progress rows, with their responsible-from, low-water mark, status, gaps and receipts, are preserved unchanged.
- Progress rows keep their copied `module`, `filters` and `filter_fingerprint` columns, but decisions read the registration.

**Provisioning (insert-only, one statement):**

```sql
WITH reg AS (
  INSERT INTO event_subscription_registrations(name, module, filters, filter_fingerprint, registered_at)
  VALUES (@name, @module, @filters, @fp, now())
  ON CONFLICT (name) DO NOTHING RETURNING name)
INSERT INTO event_subscriptions(owner_id, name, module, filters, filter_fingerprint, responsible_from, low_water_mark, status, registered_at, updated_at)
SELECT c.owner_id, reg.name, @module, @filters, @fp, c.sequence, c.sequence, 'active', now(), now()
FROM reg CROSS JOIN event_cursors c
ON CONFLICT DO NOTHING
```

Then the registration's module and fingerprint are verified, as today. **Re-provisioning never backfills.** A missing progress row under an existing registration is repaired only by the initialization rule below, never at the current cursor.

**Progress-row initialization.** One helper creates progress rows. The dispatcher's responsibility check and catch-up (per owner) call it inside a transaction that already holds `pg_advisory_xact_lock_shared(RetentionLock(owner))`. Before inserting, the helper locks the registration `FOR KEY SHARE`, so a racing deregistration never fails with a foreign-key error:

```sql
INSERT INTO event_subscriptions(owner_id, name, module, filters, filter_fingerprint, responsible_from, low_water_mark, status, gap_from, gap_to, registered_at, updated_at)
SELECT @owner, r.name, r.module, r.filters, r.filter_fingerprint, 0, f.floor,
       CASE WHEN f.floor > 0 THEN 'lagging' ELSE 'active' END,
       CASE WHEN f.floor > 0 THEN 1 END, CASE WHEN f.floor > 0 THEN f.floor END, now(), now()
FROM event_subscription_registrations r
CROSS JOIN (SELECT COALESCE((SELECT retention_floor FROM event_cursors WHERE owner_id = @owner), 0) AS floor) f
WHERE r.name = @name AND r.filter_fingerprint = @fp
ON CONFLICT DO NOTHING
```

The helper then reads the winning row with the existing `FOR KEY SHARE`. The `gap_from`/`gap_to` columns and the `low_water_mark >= responsible_from` check already exist. Gap evidence is preserved when the row later becomes active. The low-water advance never creates rows.

**Responsibility:**
- For a matching subscription, an existing row excludes sequences **at or below** `responsible_from`.
- **A missing row never makes an event `NotResponsible`.**
- Event types outside the filter stay ignored.

**Registration lock:**
- Dispatch, failure recording (`RecordFailureAsync`) and low-water advance first lock the matching registration `FOR KEY SHARE`.
- An absent or incompatible registration produces no subscriber effects.
- Every retry and failure transaction re-validates the registration and responsibility.

**Deregistration:**
- `deregister-event-subscription <name>` is server-wide. It deletes the registration under an exclusive row lock, the FK cascades to progress rows and receipts, and then the queue is deleted.
- Stale catch-up work cannot recreate the rows, because initialization requires the registration.

**Catch-up discovery:**
- Per subscription, a system-store query over `event_cursors c LEFT JOIN event_subscriptions s` selects owners where any of these holds:
  - there is no progress row (including owners whose whole history is pruned, so the gap becomes visible);
  - `c.sequence > s.low_water_mark`;
  - a due pending receipt exists at any sequence, including below the low-water mark.
- Keyset paging on `owner_id`, 100 per page, continuing across passes.
- Only `Active` owners are dispatched. Skipped owners never stall the scan, which wraps around at the end.

**Listeners** check the message owner with `IJournalOwnerDirectory.StatusAsync`:
- **Active:** dispatch in the owner's scope.
- **Suspended:** acknowledge without dispatching or writing a receipt; catch-up delivers after restore.
- **Unknown:** dead-letter.

Monitor commands for a suspended owner are dropped and logged by ID; for an unknown owner they are dead-lettered.

**Suspension timing.** Owner eligibility is checked before each dispatch transaction starts. Transactions already started when suspension happens may finish and settle. No new subscriber handling starts while the owner is suspended. After restore, discovery resumes, including pending retries. Events pruned by retention in the meantime appear as a lagging gap.

**Retention** iterates every owner in `event_cursors`, suspended ones included, in keyset pages.

**`retry-event-receipts`** requires `--owner <guid>` or `--all-owners`.

### 9.3 Monitor (B2)

- **Leases.** `MonitorLease` gains `OwnerId`. The existing discovery record `MonitorCandidate` (`Application/Monitoring/Contracts.cs:56`, which carries a tracked `MonitorAccount`) stays as it is.
- **Discovery** stays owner-scoped (the existing EF code) and iterates `ActiveOwnersAsync` pages.
- **Candidates** come from `ISystemMonitorStore.DueCandidatesAsync(DueMonitorKey? after, DateTimeOffset dueBy, int limit)`. It returns `DueMonitorCandidate(OwnerId, AccountId, VenueId, DueAt)`, where `DueMonitorKey = (DueAt, OwnerId, AccountId)`. It is a lock-free system query across owners, with keyset pages in that order. Pages omit no due row (there is no per-owner cut), so the continuation never passes work it did not examine; owner fairness comes from the transactional owner budget and the in-tick skip of owners at capacity.
  - The scheduler keeps the continuation across ticks, so later owners cannot starve. It skips suspended owners (directory status, cached for one tick) and owners at capacity.
  - The continuation walks a **bounded scan cycle**: the bound is the time the cycle started, and only work due by then is considered. Work rescheduled during the cycle waits for the next one. When a cycle's pages run out, the scheduler starts a new cycle (at most once per tick), so an account skipped earlier (row locked, owner or venue at capacity, owner failure) is revisited instead of starving behind recurring work. A tick reads at most 10 pages of 100 candidates and resumes from the continuation on the next tick.
- **Claiming.** Each candidate is claimed in **its own transaction** (one account) in its owner's scope, re-checking owner status, deadlines and both capacities. The scheduler no longer calls `ClaimDueAsync`; its multi-row claim can deadlock on venue locks.
- **Lock order:** account row, monitor row, owner budget lock (`vessel-monitor-budget:{owner}`), venue budget lock (`vessel-venue-budget:{venue}`). It is the same in `MonitorCoordinator`, `ClaimAsync` and Refresh admission. A transaction never takes two venue locks. `ReadCapacityAsync` takes `venueId`.
- **The owned-entity rule stays.** The `VesselDbContext` owner filter and the mixed-owner save guard are not weakened.

### 9.4 Server state and venue limits (B2)

- **Server-state tables.** `server_monitor_heartbeats (deployment pk, seen_at)` and `server_venue_deadlines (venue_id pk, retry_after_until)` are accessed only through `IServerStateStore` (system store). B2 removes the `MonitorHeartbeat` and `MonitorVenueBudget` EF mappings, the domain types in `MonitorAccount.cs` and their snapshot entries.
- **Migration.**
  1. Copy `MAX(RetryAtUtc)` per venue into `server_venue_deadlines`.
  2. Start `server_monitor_heartbeats` empty.
  3. Drop `monitor_venue_budgets` and `monitor_heartbeats`.

  **Stop all API and worker processes before applying it.** Rolling back to a pre-B2 image afterwards means restoring the pre-upgrade backup.
- **Heartbeat.** Deployment IDs come from the existing `Vessel:Messaging:Deployments` module mapping. Only a productive `MonitorScheduler` pass writes its deployment's heartbeat, so unrelated worker deployments cannot make monitor health green.
- **Venue concurrency.**
  - `VenueReadConcurrency` caps leased monitor runs and account Refresh across owners and processes. It is counted by raw SQL on the claiming transaction's connection, after the venue lock, and listed in the inventory.
  - It is not a cap on every HTTP request an adapter makes.
  - Discovery and verification do not count toward it.
  - Lighter's adapter semaphore stays per process and is documented as such.
- **Venue deadlines.**
  - Account-read paths honour and record the server-wide deadline: monitor runs, Refresh, account discovery and credential verification.
  - A 429 is recorded in an independent server-state transaction that survives a rollback of the caller.
  - Rate-limit exceptions are caught before the existing broad safe-error conversion, for example in `AccountCredentialService.PutAsync`.
  - Public market-data reads (candles, catalogue, market context) keep their existing cache and backoff behaviour. They neither honour nor record server-wide deadlines in this delivery.
- **Per-user caps** are in memory per API process and apply in both modes. The frontend shows a 429 with `Retry-After` as a wait notice.
  - market streams: at most 4 per user, 8 per process stays;
  - candle `refresh=true`: one per user and series per 60 s;
  - account discovery and credential verification: 10 per user per minute.

### 9.5 Stream revalidation (A1)

See §7. The stream endpoints get `IPrincipalValidity` from DI: `AlwaysValidPrincipal` in Token mode, and the session check in Wallet mode.

### 9.6 Suspension

| Effect | Behaviour |
| --- | --- |
| Sessions | Revoked; sign-in answers 403 `user-suspended` after a valid signature |
| Monitor | Discovery and claims skipped. In-flight runs finish, renew and settle normally |
| Events | No new delivery; acknowledged without receipts; retention continues; catch-up resumes after restore. Pruned events appear as a gap |
| Data | Retained unchanged |

## 10. Activation gate

- **A1 constant.** `IdentityRegistrationGate.MultiOwnerReady = false` forces the effective policy to `Closed`. `/api/auth/config` reports `closed`, and a startup log line says registration is gated. **B2 sets it to `true`.**
- **Wallet mode is not documented** for operators until P4 has verified:
  - the isolation suite;
  - the two-owner worker and event tests;
  - revocation of open streams;
  - HTTPS cookies;
  - the restore reset;
  - a real-wallet compatibility check, including an EOA connected to a non-mainnet network;
  - Wallet-mode writes (POST, PATCH, bodyless DELETE, multipart upload) in **Chromium and Firefox**.

P4 operator documentation is published under the explicit §16 clarification,
not activation approval. §10 is not signed off; Wallet is a documented opt-in
but not activated. Keep registration `Closed` until both-browser writes and the
owner real-wallet/non-mainnet EOA check pass and are recorded in
`multi-user-state.md`. Publishing this runbook does not waive either gate.

## 11. Frontend (D1)

### Gate

- `api/auth.ts` holds the endpoints, guards and the code-to-copy map. `features/identity/*` holds the UI.
- `WorkspacePage` first calls `GET /api/auth/config?origin=…`:
  - `token`: the existing `ConnectionGate`, unchanged;
  - `wallet`: `SignInGate`, which probes `GET /api/auth/session` with the **public** call kind. A 200 goes to the shell. A 401 shows the sign-in screen and is never a teardown; otherwise the teardown's reload would land on the probe again and loop.

### Transport

`createApiCall` takes one of three kinds:

| Kind | `credentials` | Headers | `referrerPolicy` |
| --- | --- | --- | --- |
| `{kind:"bearer", token}` | `"omit"`, as today | `Authorization` | unchanged |
| `{kind:"session", userId, sessionId}` | `"same-origin"` | `X-Vessel-User` | `"same-origin"` |
| `{kind:"public"}` (public auth calls) | `"same-origin"` | `X-Vessel-Request: 1` | `"same-origin"` |

### Status handling

- **A 401 is forced teardown.**
  1. Write "Your session ended" to `sessionStorage['vessel:device:notice']`.
  2. Clear the shell's unsaved-work exit state, so the `beforeunload` prompt (`ApplicationShell.tsx:104-116`) never appears.
  3. `location.replace` to the sign-in, with no confirmation.

  The sign-in screen reads and removes the notice.
- **403 by code:**
  - `step-up-required` opens the step-up dialog and retries once;
  - `user-suspended` opens a suspended screen;
  - other codes show their message and never tear down.
- **Voluntary sign-out** keeps today's unsaved-Play confirmation, then calls `sign-out` with `expectedSessionId`, then does the same clearing and reload. A 409 `session-changed` reloads without broadcasting a sign-out.

### Cross-tab

- `BroadcastChannel("vessel-session")` posts `{type:"signed-in"|"signed-out", userId, sessionId}`. Tests inject the channel factory.
- A sign-out ends only the tab whose session it names.
- A sign-in with a different user reloads before adopting any state, with the notice "Signed in as someone else in another tab".
- A new session for the same user replaces the old session generation.
- A generation counter discards stale responses and callbacks. It cannot undo server actions already executed or cookies already set.

### Wallet adapter

- **Discovery.** EIP-6963 discovery. If nothing is announced within 500 ms and `window.ethereum` exists, list a "Browser wallet" fallback.
- **Metadata** is untrusted:
  - name up to 40 characters;
  - icon only as `data:image/(png|jpeg|webp|svg+xml)` up to 64 KB, rendered with `<img>`;
  - `rdns` is never used for trust.
- **Last used.** The last-used `rdns` is stored in `vessel:device:last-wallet`.
- **Methods.** It calls only `eth_requestAccounts` and `personal_sign`, and listens to `accountsChanged` and `disconnect`. A changed account during signing shows the "wallet switched account" state.

### Preferences

- `vessel:u:{userId}:appearance` and `vessel:u:{userId}:chart.preferences.v1`.
- `vessel:device:theme` holds built-in theme IDs only. `initAppearance` applies it when present, otherwise the legacy key.
- When `adoptsLegacyPreferences` is true, each legacy key is copied **only if the user's key is absent**, then deleted.
- The in-memory stores switch to the user's keys after sign-in; a user change always reloads the page.

### Screens and copy

- Screens follow `prototype/wallet-sign-in.html`, including "keep me signed in on this device", the generic unsupported-signature copy and the corrected "unreachable" copy.
- Statements that Vessel never signs anything become: "Vessel never signs, sends or approves anything at a venue."
- **No CSP change and no new runtime dependency.** `@noble/curves` and `@noble/hashes` are allowed as dev dependencies, for test signing only.

## 12. Tests required

### A1

- Golden message bytes per purpose and origin form.
- Signature vectors made with `EthECKey`: `v` 0/1/27/28, high `s` (normalized and accepted), the 6492 suffix, wrong length, wrong address, mixed-case bad checksum.
- Challenges: single use, expiry and skew, the binding cookie (reused across two tabs), origin mismatch.
- **A new user's verify → register with cookies**: register succeeds because the re-issued binding cookie is accepted. A failed register does not burn the pending registration.
- Registration and invitation races: the same wallet twice; invitation exhaustion under concurrency.
- Request guard and tab binding:
  - missing or `null` `Origin`;
  - `Sec-Fetch-Site: cross-site`;
  - missing headers;
  - multipart and bodyless DELETE allowed;
  - tab-binding mismatch;
  - public routes exempt with a cookie present.
- `originAccepted` with and without the query value; Token-mode 404s, anonymous included; Wallet-mode startup refusals.
- At most one idle refresh per 5 minutes under concurrency; the readiness reason; cookie attributes per origin kind.
- `code` and `Retry-After` reach the client through the error handler, for auth 429s and both event-stream caps.
- `ensure-owner` does nothing in Token mode, and the default GUID is accepted in Token mode.
- Rate-limit partitions; session idle, absolute and remember expiry; `MaxSessionsPerUser`; suspension.
- Revalidation ends an open, otherwise idle stream within 15 s.
- Sign-out with a stale `expectedSessionId` returns 409 without revoking.
- Bootstrap and CLI exit codes; the default-GUID refusal and override; Token-mode `deploy/migrate` without `ensure-owner`.
- Identity schema isolation, real migrations included.

### A2

- The step-up grant is bound to session, action and target.
- The signer is unlinked between challenge and completion.
- Concurrent removal of the last two wallets.
- Removing the current wallet clears the cookie.
- Demoting the last operator is refused.
- Another user's session or wallet ID gives 404.
- `reset-after-restore` output and its effects: everyone except `--keep-active` is suspended. A wallet removed after the backup, now restored, cannot get a fresh session. A user suspended after the backup, now restored as active, is suspended again by the reset. `unbind-wallet` clears grants signed by that wallet.
- A session authenticated by wallet B holds a step-up signed by wallet A. A is then unlinked, so the grant can no longer link wallet C.

### B1

- **Two owners:**
  - owner-scoped scheduler claims;
  - a renewal after more than 1 minute, from the worker and from API-triggered Refresh;
  - an unknown owner's message dead-letters.
- **Subscriptions:**
  - the first event of a new owner reaches a provisioned subscription;
  - concurrent first events;
  - provisioning backfill;
  - **re-provisioning while a new owner lacks a progress row does not backfill**;
  - an owner created after provisioning;
  - row creation racing retention keeps the gap, including when all events are already pruned;
  - deregistration racing dispatch, failure recording, advance and catch-up leaves no orphans;
  - a pending retry below the low-water mark is delivered;
  - suspension during delivery, then restore after retention (gap shown);
  - the B1 migration backfill and its conflicting-fingerprint failure.
- `OwnerScopeArchitectureTests` and the raw-SQL inventory.
- Provisioning and `retry-event-receipts` through the `RegistrationServices` container, with `AssignedOwnerContext`.

### B2

- Candidate pages dominated by suspended or capped owners do not starve others.
- An account skipped once (row locked) is revisited at the next scan cycle while another owner's recurring work stays due.
- An owner's unlocked third account, behind two row-locked ones, and its manual Check are claimed while another owner's work recurs.
- Two owners × two venues claim concurrently without deadlock.
- The venue count sees its own lease.
- A server-wide venue deadline holds across owners; a 429 recorded during a rolled-back credential save survives.
- `WorkerConcurrency`.
- Heartbeat only from a productive monitor pass.
- The migration copies deadlines.
- Per-user caps, with `Retry-After` asserted.

### C1

- **Harness:**
  - A test-only authentication scheme selecting the owner.
  - An endpoint inventory from `EndpointDataSource`: every route is covered or explicitly exempted.
- **Positive controls:** each case first succeeds for the owner.
- **Foreign IDs:**
  - foreign and nonexistent IDs give identical status and ProblemDetails `title`, `detail` and `code` (excluding `traceId` and `X-Correlation-ID`);
  - foreign IDs in route, query, body and bulk payloads, with post-mutation state assertions;
  - foreign `/api/auth/sessions/{id}` and `/api/auth/wallets/{id}` (pending A2).
- **Shared identities:** identical native venue identities and source fill IDs in both owners; Overview, Activity, Review and Sizing results stay separate.
- **Other data:** evidence bytes, credential associated data, diary request IDs reused across owners, event streams and cursors.
- **Concurrency:** concurrent requests for two owners.

### D1

- **Vitest:**
  - the adapter: discovery, fallback, metadata bounds, stale results;
  - the sign-in state machine;
  - transport (credentials, headers and `referrerPolicy` per kind);
  - 401 teardown (no `beforeunload` prompt; the notice survives the reload);
  - non-auth 403s don't tear down;
  - cross-tab messages with session IDs;
  - sign-out with `session-changed`;
  - preference adoption only when absent;
  - a 401 from the session probe shows sign-in without reloading.
- **Hygiene:** inject `BroadcastChannel` and clear `localStorage` after each test.

### P4

- A Playwright smoke with an injected EIP-6963 provider signing through a page-exposed Node signer (`@noble/curves`), in Chromium and Firefox.
- `verify-release` in both modes.
- A restore from a backup made before a revocation, then `reset-after-restore`.

## 13. Out of scope

- WalletConnect, smart-contract wallets and an RPC endpoint;
- the operator page, user or journal deletion and export, passkeys, recovery codes;
- per-user quotas (accounts, evidence bytes), required before `AllowList` or `Open` registration is documented;
- server-side preferences;
- server-wide deadlines for public market-data reads;
- the two integrity foreign keys;
- deployment to or migration of the owner's installation.

## 14. Round-2 decisions on reviewer differences

| Topic | Sol | Opus | Decision |
| --- | --- | --- | --- |
| Where the owner override lives | Infrastructure, beside `HttpJournalOwnerContext` | An `OwnerScopeAssignment` type in Application; the factory in Persistence | Opus's: the type is a shared port, and Persistence and Messaging resolve the factory through the interface (§9.1) |
| Owner-directory registration | — | A1 registers it for both modes | `TryAdd` (B1, Token) plus `Replace` (A1, Wallet): order-independent and lets B1 build alone (§9.1) |
| `originAccepted` source | `?origin=` query value | `Origin`, else forwarded scheme and Host | The query value, because it is explicit and only a hint (§6) |
| Loopback HTTP outside Development | HTTPS only | Cookie attributes follow the origin | HTTPS only outside Development (§3) |
| Which reads honour venue deadlines | Every upstream path, including market data | Account reads only | Account-read paths honour and record. Market data keeps its own behaviour, explicitly out of scope (§9.4) |
| Candidate fairness | Keyset pages continued across ticks | At most 2 per owner per page | Both at first; the per-owner cut was later removed because it hid rows from the continuation (§9.3, §16) |
| Live session without a wallet | Nullable reference allowed only on revoked sessions | `wallet_id` NOT NULL | Nullable FK with the invariant that live sessions always have a wallet (§4) |
| Market streams per user | — | 4 (optional) | 4 (§9.4) |
| Sessions per user | 20 | about 50 (optional) | 20 (§3) |
| Integrity foreign keys | Decide explicitly | Decide explicitly | Out of scope (§9.1, §13) |

## 15. Round-3 fixes

| From | Fix | Where |
| --- | --- | --- |
| Sol | The step-up grant records its signing wallet (`step_up_wallet_id`). Completion re-checks that wallet under the user lock, and every unlink clears grants it signed | §4, §6, §12 A2 |
| Sol | `reset-after-restore` suspends everyone except `--keep-active` users, and hosts stay stopped until reconciliation against post-backup records | §8, §12 A2, P4 runbook |
| Opus | `ProblemException.Code`/`RetryAfterSeconds` (base), written by A1's error handler after `Response.Clear()` | §2, §7 |
| Opus | `AssignedOwnerContext` for the messaging `RegistrationServices` container; correct provisioning line references; `MessagingHost.OwnerId` removed | §9.1 |
| Opus | `DueMonitorCandidate`/`DueMonitorKey` instead of the discovery `MonitorCandidate`; B2 gains the system-store files | §9.3, §2 |
| Opus | The base branch is `t3/wallet-based-user-auth` | §2 |
| Opus | The default-GUID refusal applies in Wallet mode only; `ensure-owner` always runs in `migrate` and does nothing in Token mode; B1's interim scheduler; the initialization helper locks the registration first; the session probe uses the public call kind and a 401 never reloads; `IPrincipalValidity` checks "at least every 15 s" | §3, §4, §8, §9.1, §9.2, §11 |


## 16. Implementation-round clarifications

Cross-model verification of the first lanes found gaps the contract left open. The orchestrator settled them as follows; later lanes and P4 follow them.

| Found in | Clarification | Where |
| --- | --- | --- |
| P4 | Operator docs are published before two §10 items passed; they mark Wallet mode as not activated and start registration `Closed` | §10 |
| P4 stack verification | `deploy/migrate` first runs `check-config`: the API's startup validation (production settings and the identity settings the workers also read) without a database connection, so first installs can be checked. Exit 0 valid, 1 invalid, 2 usage; nothing migrates on a non-zero exit. The shell preflight matches `Token`/`Wallet` case-sensitively, as the application does | §10; P4 runbook |
| P4 stack verification | Operator docs support only `Closed` and `Invitation`. `AllowList` and `Open` stay in code and tests but are unsupported and undocumented until the per-user quotas of §13 exist; `check-config` warns when Wallet mode selects either | §13; P4 runbook |
| A1 review | The request guard treats every method except GET, HEAD, OPTIONS and TRACE as unsafe, compared case-insensitively | §7 |
| A1 review | A reused binding cookie keeps its value but is re-issued with a fresh lifetime on every challenge | §6, §7 |
| A1 review | Stream endpoints resolve `IPrincipalValidity` as a required service; there is no "always valid" fallback | §7, §9.5 |
| A1 review | Each stream revalidation check has a 10 s deadline independent of the database task; a timeout fails closed and ends the stream | §7 |
| B1 review | The interim scheduler isolates failures per owner: one owner's error never stops discovery, claims or heartbeats for later owners | §9.1, §9.3 |
| B1 review | `retry-event-receipts` requires `--owner <guid>` or `--all-owners`; with `--all-owners`, `--limit` applies per owner | §8, P4 runbook |
| B1 review | The raw-SQL inventory fails closed. A C# literal (decoded with UTF-16 escape semantics; an undecodable escape never throws and any command keyword then needs review) is SQL when it, or any statement after a `;`, starts after whitespace, `;`, `(` and nested comments with any PostgreSQL command keyword in any case (`SELECT`, `SET`, `DO`, `BEGIN`, `REFRESH` …), or when it holds `--` or `/*` and a command keyword anywhere. Prose such as "Copy failed." or "refresh" is therefore listed as a reviewed `Not SQL: …` entry. Only a small owner-safe subset is exempt: a single-table `SELECT`/`UPDATE`/`DELETE` whose top-level `WHERE` has an exact owner-equality conjunct, or a single-row `INSERT` (or `INSERT … SELECT` from such a table or `unnest` of parameters) whose owner column receives the owner value, with `ON CONFLICT DO UPDATE` only on a target including the owner column; neither may assign an owner column. Any comment, `U&` escape, quoted identifier other than `"OwnerId"`, `$` other than a positional parameter, prefixed or backslash-bearing string, unparseable text, CTE, set operation, `OR`, join, subquery, multi-row `VALUES` or text spliced into parameterised SQL needs a reviewed entry with a one-line justification, one per distinct file and statement text Raw literals are read with their actual delimiter width (three or more quotes); a WHERE holding `CASE` or an `ARRAY[…]`/subscript is never exempt, since an `AND` inside it is not a top-level conjunct | §9.1 |
| D1 review | Display names, labels and notes: trim, then reject Unicode categories Cc, Cf, Cs, Cn, Zl and Zp; lengths count UTF-16 code units (60, 40 and 80); private-use characters are allowed. The frontend mirrors the server rule; the server stays authoritative | §6, §11 |
| D1 review | The browser sends only a `0x`-prefixed 130-hex-digit signature. Any other hex string, including ERC-6492, shows the `signature-unsupported` state without spending the challenge | §5, §11 |
| D1 review | A teardown before the shell is on screen still reloads. It sets the one-shot `sessionStorage['vessel:device:landing'] = 'sign-in'`, and the next load shows the chooser without probing, so a failing cookie cannot loop. "Use another wallet" on the suspended screen does the same | §11 |
| D1 review | Stale-generation responses, errors, stream readers and side effects are discarded, as §11 requires; generation-bound streams reopen under the current generation | §11 |
| D1 review | The "Signed in as someone else in another tab" notice is shown after a successful reload too, and removed only once presented | §11 |
| D1 review | The remember checkbox is locked from the moment signing starts | §11 |
| A2 | At most 20 linked wallets per user (`IdentityLimits.MaxWalletsPerUser`); 409 `wallet-limit` at `wallets/challenge` before the step-up grant check and again at completion; `bind-wallet` exits 3 at capacity | §6, §8 |
| A2 | Step-up and wallet-link use separate per-session buckets, each 10 requests per 10 minutes | §7 |
| A2 | `suspend`, `demote` and operator wallet removal cannot leave no active operator with a linked wallet, but only when the target currently is such an operator; members and wallet-less operators may always be suspended. `reset-after-restore` only warns | §8 |
| A2 | Wallet removal checks `last-wallet` before requiring step-up | §6 |
| A1/A2 | Identity text rejects lone surrogates directly; JSON input already returns 400 `request-invalid` | §6 |
| B2 | Candidate selection excludes venues under a server-wide deadline; a venue found at capacity is skipped for the rest of the tick; background claims use account `SKIP LOCKED`, while API admission still waits | §9.3–9.4 |
| B2 | A manual Check queues even when capacity is full; claiming enforces capacity | §9.3 |
| B2 | Separate per-user budgets: account discovery 10/minute, credential verification 30/minute. Refusal by a server-wide venue deadline consumes no user budget | §9.4 |
| B2 | Candle refresh admission follows catalogue lookup; keys are capped at 256/user with an 8,192 backstop that never lets one user refuse everyone | §9.4 |
| D1 | Once the tab's session ends, session calls never settle and streams settle quietly; nothing is retried, reopened, published or reported | §11 |
| D1 | An oversized `0x` wallet answer is classified unsupported without scanning it | §11 |
| Integration with main | The Notification Hub registers server-wide with per-owner progress/receipts; active/new Wallet owners use the identity directory and assigned scopes. Notifications, API, preferences, feed/unread counts, popup claims and retention remain owner-scoped. Suspended-owner live deliveries are acknowledged without notifications; retained catch-up resumes when active | §9.1–9.2; Notification Hub plan/state |
| Integration with main | Notification heartbeat is per deployment through the system `NotificationWorkerState` store and `server_notification_heartbeats`, separate from monitor health. Empty until a productive notifications maintenance pass; never copy per-owner liveness | §9.4; worker reference |
| Integration with main | Test-notification admission is 6 requests/minute/user/API-process (`UserReadLimits`) plus the stored per-owner cooldown; 429 with `Retry-After`, no venue call | §9.4; `/api/notifications/test` |
| Integration with main | Rename unshipped `20261017120000_ServerEventSubscriptions` → `20261018100000_ServerEventSubscriptions` and `20261017193000_ServerMonitorState` → `20261018110000_ServerMonitorState`; add `20261018120000_ServerNotificationState` (creates empty `server_notification_heartbeats`, drops/replaces `notification_heartbeats`). Stop every API/worker on all hosts and back up both schemas; identity → journal → ensure-owner → provision → start. Recreate development databases with old IDs, never edit history; rollback restores backup | §8, §10; deployment upgrade/restore runbook |
| B2 review | Candidate scans run in bounded cycles (only work due by the cycle start), so accounts skipped during a cycle are revisited at the next wrap; a Check made mid-cycle waits for the next cycle when workers are saturated | §9.3 |
| B2 review | Candidate pages have no per-owner cut. Ranking each owner's rows before the keyset let the continuation pass rows never returned, so an owner's third account (and its Check) starved behind two locked ones. Owner fairness comes from the transactional owner budget and the in-tick capped-owner skip; a tick reads at most 10 pages of 100 and resumes from its continuation | §9.3 |
| D1 review | Adopting a same-user session from another tab re-probes `/api/auth/session` and refreshes the shell's session, wallet and profile lists without touching drafts; open removal dialogs and their step-ups are dropped. After a wallet removal the server's session probe, not the removed row, decides whether this tab ended. Each probe is bound to its controller and generation: a newer probe, adoption or teardown aborts it, and its late answer (a 401 or `user-suspended` included) is dropped. A profile epoch increases on every authoritative profile change (a newer generation's profile, a newer revision, a successful save, a conflict rebase). Every asynchronous profile read (session probe, `revision-conflict` recovery read) captures the generation and epoch when it starts and applies its profile only if both are unchanged when it answers; a current-generation probe still updates the session and wallet. Within one generation the profile only moves forward by `UserDto.revision`; a probe of a newer generation, and a recovery read whose generation and epoch are still current, replace it at any revision, so a restored backup with a lower revision never leaves saves conflicted. An adoption while the workspace loads is followed once it is on screen. Until the adopted session's wallet and session lists load, Remove and session sign-outs are withheld, and Remove rechecks its target against the current list and session at confirmation | §11 |
| D1 review | The session call whose own 401 ends the session never settles, like the calls pending at the teardown; 403s that end nothing still reject | §11 |
| D1 review | Profile offers Remove only on wallets that did not sign this session (mockup screen 8); that wallet is removed from another session. A link suggests the new wallet's announced name as its label | §11 |
| D1 review | A rate-limited registration keeps the form and holds Create journal until the `Retry-After` deadline, like the other sign-in waits. Sign-in screens show addresses in EIP-55 form; wallet calls keep the address as the wallet shared it | §11 |
