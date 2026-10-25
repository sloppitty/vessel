# Multi-user wallet sign-in — delivery state

October 25, 2026: Token sign-in was removed; see
[Wallet-only sign-in](#wallet-only-sign-in--october-25-2026). The rest of this
page records the October 18 delivery as it was verified then.

October 18, 2026. P4 rebased onto D1 `a336bb3` (the integrated A1 → B1 → A2 →
B2 → C1 → D1 stack on main `43ebaa0`), on `t3/mu-p4-deploy-docs`. This is implementation/verification
state, not a merged release, a published image or a deployed installation.
Authority: [delivery contract](multi-user-delivery-contract.md), including §16;
review history: [review log](multi-user-review-log.md#implementation-round--october-18-2026).
The original analysis/prototype is historical, not an implementation gate.

## Implemented

| Lane | Delivered boundary | Verification |
| --- | --- | --- |
| A1 identity core | Separate Identity module/schema/history and EF bundle; EOA SIWE challenge/verification, invitation registration, bounded cookie sessions, request guard, directory and open-stream revalidation; explicit owner bootstrap/readiness. | Identity tests, host route/security inventory, both-mode/order composition and full backend suite; Opus fixes recorded in the review log. |
| B1 scopes/events | Assigned fresh background scopes, mode-selected owner directory, reviewed singleton system stores, server subscription registration with subscriber-side per-owner progress, owner-aware catch-up/retention/listeners. | Owner-scope architecture/inventory, multi-owner worker/event/messaging tests and isolation suite; independent Opus verification. |
| A2 identity accounts | Profile/revision, linked wallets (20 cap), sessions, action-bound single-use step-up, offline suspension/role/recovery commands and restore reset. | Identity account/administration/readiness/wallet-cap tests and full suite; independent Opus verification and §16 decisions. |
| B2 monitor/limits | Fair candidate/claim scheduling, account `SKIP LOCKED`, database-backed cross-owner venue capacity/deadlines, per-deployment heartbeats, per-user API budgets; registration constant enabled. | Multi-owner monitor/worker/limits tests, migration tests and full suite; independent Opus fixes verified. |
| C1 isolation | Positive-control HTTP isolation, exact hidden-resource responses, per-attempt state snapshots, mutation probes, Token and Wallet harnesses. | 66 isolation cases in the inherited verified stack; rerun evidence below. Opus implementation, independent Sol review/fixes. |
| D1 frontend | Mode discovery, EIP-6963 sign-in/registration/profile/step-up, account menu, generation and terminal-session transport, cross-tab teardown, scoped preferences. | Integrated D1: 2,405 Vitest cases reported by the orchestrator; prior typecheck/lint/build and independent Sol reviews/fixes. P4 changes no frontend source. |
| P4 deployment/docs | Explicit Wallet Compose overlay (Token default unchanged), operator settings/CLI/upgrade/restore runbooks, developer references, packaged `--auth wallet` check and external noble-signed browser smoke. | Claude Opus 5.5 reviewed `0988a1b`: **ready-with-fixes**, no blockers. Scoped follow-up fixes and rerun evidence below; no independent re-review is claimed. |

D1–D12 are **adopted working defaults**, not answers retroactively attributed to
the owner. They remain owner-overridable through explicit configuration or a
small reviewed code change. Token stays the default. An operator opts into
Wallet over HTTPS; start with registration `Closed` until the acceptance
checklist is complete. User IDs preserve existing journal owner GUIDs and
credential-encryption bindings. Operator role grants no journal bypass.

## Integration with main — October 18, 2026

P4's original `4ffc714` is retained as `t3/mu-p4-deploy-docs-pre-main`.
`git rebase --onto t3/mu-d1-frontend-identity 08d07e7` replayed the six P4 commits
onto `a336bb3`; conflicts in `deploy/README.md`, `backend-guide.md` and
`messaging-and-workers.md` were resolved in the original docs commit. Main's
Notification Hub, order-identity breaking-upgrade warning, October 17 backend
pass, module-aware health, release/e2e scripts and development runner remain;
P4's Wallet overlay, persistent fail-closed mode/Compose files, migration
preflight, noble signer, remote Firefox and restore reset remain.

Both workers receive the same auth mode/token mapping. The Wallet overlay also
gives `worker-notifications` the empty bearer and Wallet settings: its common
`WorkerHost.Configure` calls `AddVesselIdentityDirectory` for every module, so it
uses `IdentityOwnerDirectory`, not a single bootstrap owner. The Hub's registration
is server-wide with per-owner progress/receipts; notifications, preferences, API,
feed cursors, unread counts, claims and retention are owner-scoped. Suspended-owner
live deliveries are acknowledged without notifications; retained catch-up resumes
when active. Retention visits retained owners, including suspended users. The
browser uses session transport and the owner's server claim as the "shown once"
marker. The test-event endpoint allows **6 requests/minute/user/API-process**
in addition to the stored owner cooldown, with 429/`Retry-After`.

Notification health is per deployment through `NotificationWorkerState`, in
`server_notification_heartbeats`, separate from monitor health. Stop **all**
API/worker hosts/replicas, including notifications, and back up both schemas first:
identity bundle → journal bundle (main's migrations, then
`20261018100000_ServerEventSubscriptions` → `20261018110000_ServerMonitorState`
→ `20261018120000_ServerNotificationState`) → owner bootstrap → messaging
provisioning → hosts. The first two IDs replace unshipped October 17 IDs;
recreate development databases that applied them, never edit history. The last
migration creates empty notification deployment health and **drops/replaces**
`notification_heartbeats`; a productive pass must establish liveness anew. Monitor
state also replaces tables. No rolling upgrade; rollback restores the pre-upgrade
backup. Main's OrderIdentity retained-data refusal/backfill limit still applies.

October 18, final stack tip (P4 on D1 `a89ed49`, B2 including the integration
fixes `38e15a0`), run serially by the orchestrator:

| Check | Result |
| --- | --- |
| `pnpm check` | scripts 77 passed; Release build 0 warnings; backend **3,191 passed**, 0 failed, 5 opt-in skips; frontend **2,405 passed** (133 files); typecheck, lint and build passed |
| `python3 scripts/verify-release.py` (Token, S3) | **177 passed**, 0 failed |
| `python3 scripts/verify-release.py --auth wallet … --wallet-browsers chromium,firefox --wallet-browser-image mcr.microsoft.com/playwright:v1.58.2-noble` | **177 passed**, 0 failed: 155 packaged + 11 Chromium + 11 Firefox 146.0.1 (remote); revoked-session stream closed in 14,155 ms (Chromium) and 13,370 ms (Firefox) |

The Opus verdict on the integration with main is recorded in the review log.
The earlier evidence below is historical.

### Fresh integration checks

All checks run serially from the P4 worktree, sourcing
`/tmp/vessel-mu/test-env.sh` without printing it where needed. Release checks
create their own secrets, owner GUIDs, loopback ports, unused subnets and fresh
volumes; they never read an installation `.env` or use its database. No pushes,
publication, live migrations or deployment. The commands below use S3/RustFS by
default; the prior local-storage evidence remains historical.

| Command | Fresh integration result |
| --- | --- |
| `python3 -m unittest discover -s scripts -p 'test_*.py'` | **77 passed**, zero failures/skips. Includes provisioning reports for both new/existing subscriptions, real Compose config resolution for both workers, bare persisted Wallet overlays and explicit overlay omission (mode remains Wallet/fails preflight), plus notification SSE session transport. |
| `python3 scripts/verify-release.py` | **177 packaged assertions passed**. Matrix: HTTP 44, TLS 47, image 5, monitor 26, notifications 22, restore 15, security 8, worker 10. Both deployments healthy; Token notification delivery, claim/read, broker outage catch-up and restore passed. |
| Exact two-browser Wallet command in **Manual Wallet pre-release step** below | **155 packaged + 11 Chromium + 11 Firefox = 177 assertions passed**. Matrix: TLS 44, image 5, monitor 26, notifications 22, restore 15, security 8, wallet 25, worker 10. Chromium **153.0.8010.12** local, Firefox **146.0.1** remote, client/server Playwright 1.58.2. |
| `python3 scripts/e2e-full-stack.py --artifacts /tmp/vessel-mu/p4-integration-e2e` | **140 passed, one stale harness assertion failed** at broker re-provisioning; cleanup completed. Correct application output included both registered subscriptions. Not counted as a passing full-stack run. |
| `python3 scripts/e2e-full-stack.py --artifacts /tmp/vessel-mu/p4-integration-e2e-rerun` | **177 passed, zero failed**, all **16 stages**, 1,099 s, from fresh volumes. Includes both deployments/subscriptions and module health, two browser phases, linking/lifecycle, broker loss/re-provisioning, crash/lease recovery, disable/re-enable, receipt retry, retention, backup/restore/epoch rotation and SSE continuity. Pinned official Playwright 1.63.0 image/module as in main's harness. |
| `python3 -m py_compile scripts/verify-release.py scripts/e2e-full-stack.py scripts/test_release.py scripts/test_e2e_full_stack.py`; `node --check scripts/wallet-browser-smoke.cjs`; `node --check scripts/wallet-test-signer.cjs`; `sh -n deploy/migrate deploy/worker-health`; `git diff --check` | Passed. Local Markdown link targets and the P4 write-scope inventory also passed. |

The Wallet smoke creates a new invitation user after provisioning, signs a
second user into an independent browser context, settles that second user's own
older synthetic test popups, requests the first user's test event and waits for
both its API row and actual popup. The second user's feed/total stay unchanged,
no popup appears there, and reading the first user's notification ID returns
404. Both engines passed with zero page errors, CSP violations or external
browser requests. Notification health is checked via the module-aware script,
the API head and the `server_notification_heartbeats` deployment row, with Wallet
mode/empty bearer/origin inspected only through assertions (no secrets printed).

The first full-stack run passed **140 checks** before its stale
`already registered: verification` substring assertion rejected the correct
`already registered: notifications, verification` report. This was a harness
mismatch, not an application failure. The shared provisioning-report parser now
checks both inserted and already-present sets, with a regression test, and the
scenario is rerun from fresh volumes (not resumed or counted as a pass). Broker
recovery listener checks also include the notifications queue.

Final resource audit compares Docker names/tags with the pre-run inventory:
**no new release/e2e containers, volumes, networks or temporary image tags remain**.
This covers both original/restore release projects, the remote Firefox server,
the first failed full-stack run, the successful fresh rerun and its restore
project. Run-owned Playwright dependency volumes were removed. Reusable official
Playwright images **1.58.2 and 1.63.0 are retained**; no shared-host browser/system
packages were installed. Logs and synthetic browser captures remain only under
`/tmp/vessel-mu/p4-integration-*`; counts here are the durable repository record,
not a promise that temporary artifacts remain.

### Stack-verification fixes — October 18, 2026

The final stack review found that the shell migration preflight lowercased the
mode and only checked that a Wallet origin was present, so settings the API
rejects (`wallet`, an `http://` origin in Production, a misspelt registration)
still applied both bundles before `ensure-owner` failed. `deploy/migrate` now runs
`dotnet /app/Vessel.Api.dll check-config` (`ConfigurationCheckCommand`) before
either bundle: the API's own startup validation with no database or broker
connection, exit 0/1/2. The shell mode match is case-sensitive like the
application. Operator docs now support only `Closed` and `Invitation`;
`AllowList`/`Open` remain in code but are unsupported until per-user quotas
(contract §13), and `check-config` warns on them. The README pins the synthetic
signer to `@noble/curves@2.4.0`/`@noble/hashes@2.4.0`. `scripts/test_release.py`
prepends its stubs to `PATH` instead of replacing it, which hid `tr` on NixOS.

| Command / check | Result |
| --- | --- |
| `python3 -m unittest discover -s scripts -p 'test_*.py'` | **78 passed** on NixOS (previously 2 failures from the replaced `PATH`). Adds lower-case mode rejection and `test_configuration_the_application_rejects_runs_neither_bundle`. |
| `dotnet test --filter ConfigurationCheckCommandTests` | **26 passed**: valid Token/Wallet/Development, 18 rejected settings with no values echoed, registration warning, legacy owner, binder errors, usage. |
| `dotnet build backend/Vessel.slnx -c Release` | 0 warnings, 0 errors. |

## Original P4 verification evidence (before integration with main)

All commands ran only in `/home/ops/.local/share/vessel-mu/worktrees/p4`.
JavaScript used pinned pnpm 12.8.1; Node 24.18.0; .NET SDK 10.0.302 from
`$HOME/.dotnet`. Backend checks sourced `/tmp/vessel-mu/test-env.sh` without
printing it and used disposable services, not the owner/developer database.
Image builds were sequential. No branch/tag/image push, publication, live
migration or installation change was performed.

| Command | Result |
| --- | --- |
| `export PATH=$HOME/.local/share/pnpm/bin:$PATH; pnpm install --frozen-lockfile` | Passed; no lockfile change. |
| `pnpm --filter vessel-frontend typecheck` | Passed. |
| `pnpm --filter vessel-frontend lint` | Passed, zero warnings. |
| `pnpm --filter vessel-frontend test` | Initial run: 2,306 passed, one timing-sensitive fixture comparison failed (two `new Date()` fixtures differed by 1 ms in `sign-in-machine.test.ts:31`). No application/test file changed. |
| `pnpm --filter vessel-frontend test -- src/features/identity/sign-in-machine.test.ts` | This invocation reran the whole suite: **2,307 passed, 127 files**, zero failures. |
| `pnpm --filter vessel-frontend build` | Passed. |
| `pnpm test:prototype` | **76 passed**. |
| `dotnet restore backend/Vessel.slnx --locked-mode` | Passed. |
| `dotnet build backend/Vessel.slnx --no-restore --configuration Release -m:1` | Passed, zero warnings/errors. |
| `dotnet test backend/Vessel.slnx --no-build --configuration Release` | **2,890 passed, five opt-in skips, 2,895 total**. Skips: three live venue tests and two S3 environment/CLI opt-ins. |
| `python3 -m unittest discover -s scripts -p 'test_*.py'` | **63 passed** (rerun after the script edits). |

Additional focused/backend and packaged checks:

| Command | Result |
| --- | --- |
| `dotnet test backend/Vessel.slnx --no-build --configuration Release --filter 'FullyQualifiedName~Vessel.Tests.Isolation'` | **66 passed**, zero skips. |
| `dotnet test backend/Vessel.slnx --no-build --configuration Release --filter 'FullyQualifiedName~Vessel.Tests.Isolation\|FullyQualifiedName~Vessel.Tests.MultiOwner\|FullyQualifiedName~Vessel.Tests.OwnerScopeArchitectureTests'` | **120 passed**, zero skips. Covers both directory modes, two-owner worker/events/messaging, server limits and the reviewed SQL/scope inventory. |
| `python3 scripts/verify-release.py` | **152 assertions passed** with S3/RustFS: HTTP 44, TLS 47, image 5, monitor 26, restore 12, security 8, worker 10. Cleanup completed. |
| `python3 scripts/verify-release.py --storage local` | **152 assertions passed**, including retained local evidence and fresh-volume restore; cleanup completed. |
| `CHROMIUM_PATH=$HOME/.cache/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-linux64/chrome-headless-shell python3 scripts/verify-release.py --browser-module /tmp/vessel-pw/node_modules/playwright` | Token regression: **152 packaged + 14 Chromium browser assertions passed**, zero CSP/console/page errors; cleanup completed. |

Wallet packaged/browser command (synthetic keys only, no real wallet):

```sh
source /tmp/vessel-mu/test-env.sh
export NOBLE_MODULE_DIR=/tmp/vessel-pw/node_modules
export CHROMIUM_PATH=$HOME/.cache/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-linux64/chrome-headless-shell
python3 scripts/verify-release.py --auth wallet \
  --browser-module /tmp/vessel-pw/node_modules/playwright --wallet-browsers chromium
```

**Initial P4 run passed: 128 packaged assertions plus 10 Chromium assertions.** Matrix:
TLS 44, image 5, monitor 26, restore 12, security 8, wallet 23, worker 10.
The check ran both EF bundles via `deploy/migrate`, demonstrated
`no-operator-wallet`, bound the synthetic operator, inspected both host cookies,
rejected bearer/stale-user/foreign-origin requests, and signed out. For recovery:
it proved the operator session was live at backup, revoked it **after** the dump,
restored that older database with all hosts stopped, ran reset/epoch rotation
before provisioning/start, and got **401** for the restored session. A fresh
signature recovered the confirmed operator's journal and original evidence.
Original and restore project containers/volumes/networks and both temporary
image tags were removed by the harness. A final bounded label/tag audit of all
six P4 disposable project bases (including the failed harness attempt) found no
remaining containers, volumes, networks or temporary image tags.

The first
Chromium attempt passed registration/cookies/writes/linking/isolation, then the
harness navigation to `/favicon.ico` was aborted; it now uses the plain
`/health/live` document for its independent stream reader. No application code
was changed for this harness correction.
Transient logs are under `/tmp/vessel-mu/p4-*`; they are not installation
configuration or a durable backup. Results/counts in this document are the
repository record, not a promise that those temporary files remain.

### P4 review-fix rerun — October 18, 2026

Claude Opus 5.5's review of `0988a1b` was **ready-with-fixes**, no blockers.
The scoped follow-up preserves the signer and application code, adds the
fail-before-migration auth preflight and persistent Compose mode/overlays,
clarifies activation/recovery and strengthens the smoke/restore assertions.

| Command / check | Result |
| --- | --- |
| `python3 -m unittest discover -s scripts -p 'test_*.py'` | **74 passed**, zero failures. Includes preflight/order/negative readiness tests, browser image option/precheck, network/publish/read-only mounts/resource limits/secret exclusion, endpoint stripping and cleanup order/failure cases. |
| `python3 scripts/verify-release.py` | **152 packaged assertions passed**: HTTP 44, TLS 47, image 5, monitor 26, restore 12, security 8, worker 10. Default Token regression, S3/RustFS. |
| Exact Wallet command in **Manual Wallet pre-release step** below | **129 packaged + 10 Chromium + 10 Firefox = 149 assertions passed**. Matrix: TLS 44, image 5, monitor 26, restore 12, security 8, wallet 24, worker 10. Chromium **153.0.8010.12** local; Firefox **146.0.1** remote. |
| Three `docker compose config --format json` resolutions, private temporary synthetic `.env` | **22 assertions passed**: Token default on migrate/API/worker; bare Wallet commands retain TLS+Wallet overlays and default `Closed`; explicit omission of the overlay retains Wallet mode but lacks origins (preflight refuses before migrations), not Token downgrade. |
| `python3 -m py_compile scripts/verify-release.py scripts/test_release.py`; `node --check scripts/wallet-browser-smoke.cjs`; `sh -n deploy/migrate`; `git diff --check` | Passed. |

The restore rerun now positively proves that the restored operator session is
live (`revoked_at IS NULL`, count 1) **on the restore project before reset**.
Reset then revokes it and its old cookie returns 401. Browser checks inspect
both issuing Set-Cookie headers (no Domain) and stored secure host cookies.

The first remote-server attempt failed with
`browserType.connect: WebSocket error: socket hang up`: rootless Docker's
published TCP proxy accepted connections before Playwright's Node server was
listening. It was a harness readiness race, not a browser/app failure. The
check now waits for `/json` with its exact random `wsEndpointPath`; a regression
test covers the initial disconnect. A standalone isolated probe then reported
`firefox 146.0.1`, and the full command passed in both browsers. The whole-smoke-
inside-container fallback was **not used**.

Both standalone probe containers/networks, all three packaged run project bases
(including the failed remote attempt), restore stacks and temporary API/web
image tags were removed. The final audit found **zero** remaining `vessel-check-*`
containers, volumes or networks, and no image tags for the three run bases.
Only the requested reusable Playwright image was retained, digest
`sha256:6446946a1d9fd62d9ae501312a2d76a43ee688542b21622056a372959b65d63d`.
Logs are `/tmp/vessel-mu/p4-review-{token,wallet,wallet-final,python-tests}.log`;
no operator installation, live database, frontend/backend file or `.github`
workflow changed. Nothing was pushed or published.

## Contract §10 activation checklist

| Gate | Outcome / evidence |
| --- | --- |
| Isolation suite | **PASS (inherited):** original P4 reran 66 isolation cases, zero skips; integrated C1 reports 3,185 backend cases via the orchestrator. P4 did not rerun that full suite on this rebase; fresh packaged browser isolation is below. |
| Two-owner worker/events | **PASS (inherited + fresh packaged):** prior focused 120-case isolation/multi-owner/scope-inventory run; integrated B2 reports 3,111 backend cases. Fresh Token/Wallet packaged workers, delivery/catch-up/restore and new-user two-context notification isolation pass. |
| Revocation of already-open streams | **PASS:** latest integrated Chromium **14,226 ms**, Firefox **13,492 ms** (≤15 s + 5 s margin), using frontend bodyless session DELETE and an independent already-open SSE reader. Smoke asserts the reader is still open immediately before revocation and elapsed time is nonnegative; backend identity/stream/isolation cases also passed. |
| HTTPS cookies | **PASS:** packaged HTTPS check plus Chromium and Firefox inspect both binding/session `__Host-` cookies, Secure, HttpOnly, SameSite=Strict, Path=/ and no Domain. Browser storage is checked before signing (binding) and after authentication (session), and both issuing response headers must omit Domain. |
| Restore reset | **PASS:** packaged backup had a live operator session; revocation happened after the dump; reset ran offline after restore; the resurrected cookie returned 401, then a new signature authenticated the preserved operator GUID. |
| Packaged integration | **PASS:** Token/S3 177 packaged assertions; Wallet/S3 155 packaged + 11 Chromium + 11 Firefox; main's full-stack 177 checks across 16 stages. Explicit identity → journal → bootstrap → provisioning and offline restore reset remain verified. Cleanup inventory passed for all created stacks/tags. |
| Multi-user Notification Hub | **PASS:** Token/Wallet notification worker health (module-aware file, API and per-deployment database row), broker-outage catch-up and notification restore; Chromium/Firefox prove a newly registered owner receives its test notification and the other session cannot list, show or read it. Suspension and the 6/minute cap are inherited backend coverage, not new browser probes. |
| Real-wallet compatibility, including non-mainnet EOA | **OWNER-PENDING**; exact acceptance steps below. Synthetic noble signatures are not a real-wallet compatibility test. |
| Wallet writes in Chromium | **PASS:** 11 integrated Chromium browser assertions, including new-user notification delivery/popup and foreign-owner denial: EIP-6963/invitation/shell, POST, frontend profile PATCH, bodyless DELETE, multipart evidence, step-up recovery link, two-user isolation, open-stream revocation and sign-out; zero page/CSP errors or external calls. |
| Wallet writes in Firefox | **PASS: Firefox 146.0.1**, remote official `mcr.microsoft.com/playwright:v1.58.2-noble` server, Playwright 1.58.2 client/server. All 11 integrated browser assertions passed, including per-owner notification delivery/popup and foreign-owner denial, real Wallet-mode POST, frontend PATCH, bodyless DELETE and multipart, both secure cookies, step-up, isolation, revocation and sign-out; zero page/CSP errors or external calls. Exact command below. The local-host dependency failure is superseded, not counted as a pass. |

**Activation outcome:** implementation supports explicit Wallet opt-in, but §10
acceptance is **not fully signed off**: Firefox has passed, while the real-wallet/
non-mainnet EOA check remains **OWNER-PENDING**. Keep registration **`Closed`**
until both pass and are recorded here. Synthetic signatures do not satisfy the
human check. The operator documentation exposes opt-in with this warning; no
installation was switched. P4 changes the deployment overlay's default
registration to `Closed`, not the application registration constant/default.

### Owner-pending compatibility steps

1. Use an isolated HTTPS installation with its own hostname, the same code and a
   privately issued invitation (or a pre-bound test EOA). Never use the owner's
   production database or submit a real private key/seed phrase to Vessel.
2. In a real browser-wallet extension, choose an ordinary EOA and connect it to
   a **non-mainnet** network. Record wallet/version, browser/version and network.
3. Choose the extension from Vessel's wallet selector. Inspect the displayed
   signed message: exact installation origin/URI, Chain ID 1, nonce and expiry,
   sign-in purpose and no transaction/approval/gas. No network switch is needed.
4. Sign, redeem the private invitation if prompted, create the journal and reach
   the shell. Sign out/sign in again. Link a second EOA through linked-wallet
   step-up and its own link signature. Never record invitations, signatures or
   cookies in the acceptance record.
5. Record date and actual outcomes here. Separately rerun the exact manual
   pre-release command below. Revalidate both browsers' writes, isolation,
   cookies and revocation checks; synthetic coverage never replaces steps 1–4.

### Manual Wallet pre-release step (not a CI gate)

CI gates Token mode only. Before a release, manually run the packaged Wallet
check and both browser smokes against the disposable stack. The deployment
overlay defaults registration to `Closed`; the synthetic check explicitly
selects `Invitation` only in its private disposable `.env`.

```sh
# One-time reusable image prerequisite (no host browser libraries installed):
docker pull mcr.microsoft.com/playwright:v1.58.2-noble
export NOBLE_MODULE_DIR=/tmp/vessel-pw/node_modules
export CHROMIUM_PATH=$HOME/.cache/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-linux64/chrome-headless-shell
python3 scripts/verify-release.py --auth wallet \
  --browser-module /tmp/vessel-pw/node_modules/playwright \
  --wallet-browsers chromium,firefox \
  --wallet-browser-image mcr.microsoft.com/playwright:v1.58.2-noble
```

Client/server Playwright are both 1.58.2. The official browser server runs as
`pwuser`, with 2 GiB/2 CPU limits, on the disposable Compose project network,
published only on `127.0.0.1`. Read-only mounts contain `/tmp/vessel-pw` and one
0644 Node forwarder, never the private `.env`/key-ring directory. The forwarder
binds `127.0.0.1` and `::1` at the isolated HTTPS port and routes to `tls:443`.
Firefox keeps the exact `https://localhost:<port>` origin; the isolated internal
CA uses `ignoreHTTPSErrors`, never weakened cookie assertions. Invitations stay
in the local smoke's environment, not Docker arguments or browser-server
environment. Server removal precedes Compose teardown, including failure paths.

### Browser prerequisites and superseded local-host failure

External tools were installed with
`pnpm --dir /tmp/vessel-pw add @noble/curves @noble/hashes` (both 2.4.0), never
as app dependencies. `pnpm --dir /tmp/vessel-pw exec playwright install firefox`
downloaded revision 1509 but reported missing host libraries. The bounded launch
probe also failed before opening a page:

```sh
node - <<'JS'
const { firefox } = require('/tmp/vessel-pw/node_modules/playwright');
(async () => { const browser = await firefox.launch({ headless: true }); await browser.close() })()
  .catch(error => { console.error(error.message); process.exitCode = 1 });
JS
```

Raw error: `browserType.launch: Host system is missing dependencies to run
browsers.` Missing sonames included `libXcursor.so.1`, `libgtk-3.so.0`,
`libgdk-3.so.0`, `libpangocairo-1.0.so.0`, `libpango-1.0.so.0`,
`libcairo-gobject.so.2`, `libcairo.so.2`, `libgdk_pixbuf-2.0.so.0`.
That original attempt did **not** run Firefox writes and was never a pass.
The follow-up uses the official container instead; no shared-host system
packages were installed. Playwright's Firefox installer pruned the older shared Chromium cache;
the supplied revision-1243 Chromium/headless-shell caches were restored from the
official download, and the supplied shell launched and ran the smoke successfully.

## Wallet-only sign-in — October 25, 2026

The owner asked to remove the Token sign-in and keep wallets only, verified by
Sol (Codex) reviews before the pull request. Branch `t3/remove-token-authentication`.

- **API and hosts.** The bearer scheme, its wrong-token limiter and
  `VesselAuthOptions` are deleted. `AddVesselIdentityDirectory` always replaces
  the owner directory with `IdentityOwnerDirectory`; `AddVesselIdentityApi`
  always registers the session scheme, `SessionPrincipal` stream validity and
  the private fallback authorization policy (workers no longer register
  authorization, so they also validate in Development). `IdentitySettings`
  always applies the former Wallet-mode rules; `Vessel:Auth:Token` set, or a
  `Vessel:Auth:Mode` other than `Wallet`, refuses the API, workers,
  `check-config` and identity commands, naming the setting only. Readiness
  always requires an active operator with a bound wallet. `/api/auth/config`
  answers `{registration, originAccepted}`. The session principal's
  authentication type is now `VesselSession`. `ConfiguredOwnerDirectory` and
  `AlwaysValidPrincipal` moved to the test project. No migration.
- **Browser.** `ApiCredentials` is `session | public`; the token connection
  screen, the Token owner card and Settings panel are removed and the shell
  always has an identity.
- **Deploy and scripts.** `compose.yaml` carries the identity settings for
  migrate, API and both workers (`VESSEL_IDENTITY_ORIGIN` required,
  registration `Closed` by default); `compose.wallet.yaml` is removed and
  `deploy/migrate` relies on `check-config`. The README has a Token-to-wallet
  upgrade path that keeps the owner GUID. `scripts/dev.py` applies identity
  migrations and `ensure-owner`, defaults the origin to the Vite page, runs dev
  workers as Development and adds `pnpm dev:identity`. Release, storage and
  end-to-end checks sign in with a synthetic EOA.
- **Tests.** API tests authenticate through a test-only header scheme that
  forwards to the real session scheme when its header is absent, with a fixed
  single-owner directory where no identity schema exists. The isolation suite
  keeps its two hosts (test scheme and real wallet sessions), which now map
  identical routes.

Verification (throwaway PostgreSQL 18 and RabbitMQ 4.3.6 containers, never the
owner's database): full backend suite with `Vessel_TEST_POSTGRES`, then the
broker-gated classes with `Vessel_TEST_EVENT_RABBIT`; frontend typecheck, lint
and the full vitest suite (176 files, 4,087 tests); `scripts` unit tests.
§10 is unchanged: still **not signed off**; the real-wallet/non-mainnet EOA
check remains owner-pending and registration stays `Closed` by default.

## Deliberate limits and remaining work

- The timing-sensitive frontend comparison in
  `frontend/src/features/identity/sign-in-machine.test.ts` (two `new Date()`
  fixtures built a millisecond apart) is fixed on old D1 `08d07e7` (now integrated at `a336bb3`): the unchanged
  signing state is now compared with the same fixture. P4 changed no
  frontend source; its only backend change is the `check-config` command.
- The development runner applies the identity and journal migrations and
  `ensure-owner` (`pnpm db:migrate`), and binds a development wallet only through
  the explicit `pnpm dev:identity bind-wallet …`. No automatic installation
  migration is added.
- No WalletConnect, smart-contract wallet verification, user/journal deletion,
  admin journal browsing or venue signing/trading.
- Public market-data backoff and Lighter's process-local semaphore are not a
  global cap on all venue HTTP calls. In-memory per-user API limits are per
  API process; account-run capacity and venue deadlines are database-wide.
- The two historical integrity foreign-key audits identified in §9.1 remain
  outside this delivery. A pending real-wallet/browser acceptance is not an
  authorization to broaden scope or apply any owner migration.
- Restore is an offline, trusted-record reconciliation, not merely session
  revocation: an older backup can resurrect wallets, roles and invitations.
  Unconfirmed users stay suspended. Never change owner GUIDs to bypass recovery.
