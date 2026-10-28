# Device pairing — delivery state

October 27, 2026. Branch `t3/device-pairing`. Authority:
[device-pairing-plan.md](device-pairing-plan.md), including its implementation
choices. This is implementation and verification state, not a release, a
deployment or a migration of any installation.

## Implemented

| Area | Delivered |
| --- | --- |
| Identity schema | Migration `20261027213124_DevicePairing`: `device_pairings` (hashed secret, typed code, binding and confirmation code; per-state 2-minute deadlines) and `sessions.source` (`wallet` \| `pairing`). Identity schema only; nothing in the journal changes. |
| API | `device.pair` step-up; create, status, approve, cancel (creating session only); public claim and complete behind the sign-in request guard and binding cookie; `attemptsLeft` on wrong codes; `source` in the sessions list; rate limits; 1,000 unfinished-pairing cap; cleanup; cancellation on suspension, wallet removal and `revoke-sessions`, and when the creating session signs out or is revoked; completion refused once the creating session has ended; deletion in the restore reset. |
| Sessions | `RememberIdleDays` default 10 for every sign-in (30 days absolute unchanged). A paired session uses the step-up signer's wallet and the normal lifetimes. |
| Desktop | Settings → Sessions → Connect a phone: step-up, QR (always black on white), Show a code to type, approval with the phone's code, cancel on close; loopback origins get an explanation instead of a QR; paired sessions labelled "Paired · …". |
| Phone | `#pair=` read and stripped on first render; Scan a code from your desktop (`BarcodeDetector`, else lazily loaded `jsqr`; same-origin links only; camera stopped on read, close, unmount and hidden page); Enter a code instead; confirmation screen with Keep me signed in checked by default; "This device is already signed in" when a session exists. |
| Headers | `Permissions-Policy: camera=(self), microphone=(), geolocation=()`, checked by `verify-release.py`. CSP unchanged. |
| Dependencies | `uqr` 0.1.3 (MIT) and `jsqr` 1.4.0 (Apache-2.0), exact-pinned, zero-dependency. |

Development used two Sonnet developers with disjoint write scopes (backend and
frontend); the orchestrator reviewed the diff and ran the checks below.

## Verification

| Command | Result |
| --- | --- |
| `dotnet test backend/Vessel.slnx -c Release` with a disposable `Vessel_TEST_POSTGRES` | 4,030 passed, 0 failed, 17 skipped (RabbitMQ-only tests without `Vessel_TEST_EVENT_RABBIT`). Includes 40 device-pairing tests and the endpoint inventory/isolation cases for the new routes. |
| `pnpm --filter vessel-frontend typecheck`, `lint`, `build` | Passed. |
| `pnpm --filter vessel-frontend test` | 4,217 passed in three consecutive full runs. One earlier run, made while the backend suite loaded the host, reported a failure that did not reproduce (its output was not kept). |
| `python3 -m unittest discover -s scripts -p 'test_*.py'`; `git diff --check` | 90 passed; clean. |
| `python3 scripts/verify-release.py --browser-module … --wallet-browsers chromium,firefox --wallet-browser-image mcr.microsoft.com/playwright:v1.58.2-noble` | **253 passed**, exit 0. Matrix: HTTP 45, TLS 48, image 5, monitor 26, notifications 22, restore 15, security 8, wallet 28, worker 10. Chromium 145 (local) and Firefox 146.0.1 (remote container), 16 browser checks each. |

Browser checks added to `scripts/wallet-browser-smoke.cjs`, in both engines:
the loopback explanation; the QR link (secret stripped from the address, Keep
me signed in checked by default, a wrong code counted, approval, signed in,
"Paired" in Sessions, no second completion); the typed code entered in lower
case with a space and Keep me signed in unchecked; a contested claim shown on
both phones; completion refused without a binding cookie; and revoking other
sessions on the desktop signing both phones out.

## Sol (Codex) reviews — October 28, 2026

Two independent adversarial `gpt-6.1-sol` reviews, backend/security and
frontend/flows, each run until it approved:

| Round | Finding | Fix |
| --- | --- | --- |
| Backend 1 | An approved pairing could still complete after the creating desktop session was signed out or revoked. | `a0629af1`: ending the creating session cancels its pairings; completion requires it to be still signed in. |
| Frontend 1 | A local countdown ended pairings the server had extended; a late approval reply could cancel the pairing Try again created. | `5c1b0f7e`: the server decides expiry; replies answer only for their own pairing. |
| Frontend 2 | The expiry check repeated every second at zero (doubling polls against the shared completion limit); an in-flight completion could issue a remembered session after the box was unticked. | `e7893322`: one check per deadline; the box holds still while a completion is in flight. |
| Backend 2, frontend 3 | None: **APPROVE**. | |

The reviewers' reproduction tests are kept as regressions
(`DevicePairingRevocationTests.cs`, `pairing-races.test.tsx`,
`pairing-polls.test.tsx`). After the fixes: backend 4,038 passed, 0 failed, 17
skipped (RabbitMQ); frontend 4,228 passed; typecheck and lint pass. The packaged
`verify-release.py` run above predates these fixes and is repeated by the
release checks.

## Not verified

- **Scanner with a camera.** The in-app scanner is covered by Vitest with
  mocked `getUserMedia`/`BarcodeDetector`; no Chromium fake-camera run was
  made.
- **Desktop QR in a browser.** The packaged stack is served on `localhost`, so
  the dialog shows the loopback explanation there. Its QR → approve flow is
  covered by Vitest (which decodes the rendered QR).
- **A real phone.** Owner check pending on an HTTPS origin the phone can reach
  (the in-app camera needs HTTPS; a plain-HTTP origin can use the system camera
  app or the typed code).
