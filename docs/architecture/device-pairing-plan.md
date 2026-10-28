# Device pairing — sign in on a phone without a wallet

October 27, 2026. The owner answered every open question and authorized implementation the same day, delegated to Sonnet developers with orchestrator verification. Verified progress is in [device-pairing-state.md](device-pairing-state.md).

The owner wants to sign in to the same Vessel account on a phone that has no
wallet. A signed-in desktop shows a QR code; the phone scans it and receives
its own session. The phone is a mobile browser for now. A React Native app and
an installable web app (manifest, service worker) are parked; see
[Later](#later).

Authority for everything not changed here stays with the
[multi-user delivery contract](multi-user-delivery-contract.md) and the
October 25 [wallet-only sign-in](multi-user-state.md#wallet-only-sign-in--october-25-2026)
state.

## Owner decisions — October 27, 2026

| # | Decision |
| --- | --- |
| P1 | The desktop must approve the phone after it scans, by typing the confirmation code the phone shows. |
| P2 | "Keep me signed in" uses **10 days idle** everywhere: the `RememberIdleDays` default moves from 14 to 10 for desktop sign-in too. The absolute limit stays `RememberAbsoluteDays` (30). |
| P3 | A paired phone has **full access**, the same as a wallet session. |
| P4 | React Native and the installable web app are parked. |
| P5 | Phones and tablets get **Scan a code from your desktop** on the sign-in screen, beside wallet sign-in. |
| P6 | On the phone's pairing screen, "Keep me signed in" is **checked by default**. The desktop default stays unchecked (D8). |
| P7 | A **typed code** is an optional fallback, behind an "Enter a code instead" button for when scanning fails. |

## Why not the Hyperliquid or Lighter approach

Their "link mobile" QR carries a signing key, an approved agent or API key, so
the phone can trade. Vessel never signs anything on a venue, so the phone only
needs a Vessel session. The QR therefore carries a single-use pairing secret.
It never contains a private key, a venue credential or the desktop's session
cookie. The server exchanges the secret for a **new** session that belongs to
the phone.

## Flow

```
Desktop (signed in)                    Server                         Phone (no wallet)
1 Connect a phone
  step-up device.pair  ──signature──▶  grant (5 min)
2 POST /pairings       ────────────▶  pairing: secret hash,
  ◀── id, secret, expiresAt ───────   status pending, 2 min
  show QR https://origin/#pair=secret
                                                                 3 camera opens the URL
                                                                   fragment read, then removed
                                       ◀── POST /pairings/claim ── {secret}
                                       status claimed, binding     binding cookie set
                                       hash, 6-digit code ───────▶ shows "482 913"
4 poll GET /pairings/{id}
  "Safari on iPhone wants to connect"
  type 482913 ─ POST /approve ──────▶  status approved
                                       ◀── POST /pairings/complete ─ (polls with binding cookie)
                                       new session ──────────────▶ session cookie, Overview
```

1. **Step-up.** On the desktop, Settings → Profile & sign-in → Sessions →
   **Connect a phone**. The user signs a step-up with a new action,
   `device.pair`. It uses the existing step-up challenge, completion rules and
   5-minute grant on the desktop session. Statement:
   `Confirm connecting a new device to your Vessel journal. This is not a transaction.`
2. **Create.** `POST /api/auth/pairings` consumes the grant atomically and
   creates a pairing: 32 random bytes, of which only the SHA-256 is stored, with
   a 2-minute claim window. It records the desktop session, the user and the
   step-up signer's wallet. Creating a pairing cancels the user's earlier
   pending pairings. The desktop renders `{origin}/#pair={secret}` as a QR code.
   Under it, **Show a code to type** reveals the typed fallback
   ([Typed code](#typed-code-fallback)).
3. **Claim.** The phone either scans the QR from Vessel's sign-in screen
   ([Scan a code](#frontend)) or opens it with the system camera app, which
   opens the URL in the browser. The secret sits in the
   fragment, so it is never sent in a request line, never reaches proxy or
   server logs and never leaves in a `Referer` header. The frontend reads it,
   removes it immediately with `history.replaceState` and posts it in the body
   of `POST /api/auth/pairings/claim`. The server:
   - moves the pairing to `claimed`;
   - stores the hash of the phone's binding cookie, reusing the sign-in binding
     cookie mechanism;
   - stores the phone's user-agent summary;
   - generates a 6-digit confirmation code, stored as a hash;
   - returns the code with the journal's display name and the short wallet
     address it is joining.

   **A second claim on the same pairing cancels it** (`contested`). Someone else
   has scanned the code, so neither phone gets in, and both screens say so.
4. **Approve.** The desktop polls `GET /api/auth/pairings/{id}` every 2 s and
   then shows "Safari on iPhone wants to connect". The user types the phone's
   code into `POST /api/auth/pairings/{id}/approve`. A wrong code counts as an
   attempt; three wrong codes cancel the pairing. The claim must be approved
   within 2 minutes.
5. **Complete.** The phone polls `POST /api/auth/pairings/complete` with its
   binding cookie every 2 s. Once the pairing is approved, one identity
   transaction consumes it, issues the phone's session (cookie set, binding
   cookie cleared) and revokes any session cookie the phone sent, as `verify`
   does. Every other state answers with its status (`waiting`, `expired`,
   `cancelled`, `contested`).

The QR alone gets nobody in. It needs a claim from the phone's own browser
(binding cookie), plus approval on the signed-in desktop with the code that
only that phone shows.

## Sessions

- A paired session is a normal row in `sessions` with full access (P3). It has
  the same cookie, validation, tab-binding header, request guard, stream
  revalidation, suspension and `MaxSessionsPerUser` cap.
- **Wallet.** `wallet_id` is the wallet that signed the `device.pair` step-up.
  This keeps the invariant that a live session references a linked wallet:
  removing that wallet revokes the phone, and `WalletDto.current` shows it.
- **Lifetimes (P2, P6).** The phone's pairing screen offers "Keep me signed
  in on this device", checked by default. Checked: 10 days idle and 30 days
  absolute, the same `RememberIdleDays`/`RememberAbsoluteDays` as a desktop.
  Unchecked: the normal 12 h / 24 h. No pairing-specific lifetime setting.
- **Shorter default for everyone (P2).** The `RememberIdleDays` default becomes
  10. Existing remembered sessions keep their current idle expiry until their
  next refresh, which then extends them by 10 days. Configurations that set
  the value explicitly keep it. The contract (§1 D8, §3), `deploy/README.md`
  and the configuration tests are updated with it.
- **Source.** New `sessions.source` column (`wallet` | `pairing`), used for
  labels only.
- **No chaining.** A phone cannot pair another device or manage wallets,
  because both need a wallet signature it cannot give. It can still view and
  revoke sessions, sign out and change the profile.
- **Sessions list.** `SessionRowDto` gains `source`; the list labels paired
  devices ("Paired · Safari on iPhone").

## API

All routes are `no-store`, use ProblemDetails `code`s and have bodies capped at
16 KiB, as in the contract.

| Endpoint | Auth | Request | Success |
| --- | --- | --- | --- |
| `POST /api/auth/step-up/challenge` | session | `{action:"device.pair", address}` | as today; `target` must be absent |
| `POST /api/auth/pairings` | session + live `device.pair` grant (else 403 `step-up-required`) | — | 201 `{id, secret, typedCode, expiresAt}` |
| `GET /api/auth/pairings/{id}` | session, creating session only (else 404) | — | `{status, expiresAt, device?}` |
| `POST /api/auth/pairings/{id}/approve` | session, creating session only | `{code}` | 204 |
| `DELETE /api/auth/pairings/{id}` | session, creating session only | — | 204 (cancel) |
| `POST /api/auth/pairings/claim` | public | `{secret}` or `{typedCode}`, exactly one | `{code, expiresAt, journal:{displayName, wallet}}` |
| `POST /api/auth/pairings/complete` | public + binding cookie | `{remember}` | `{status}`; on `signed-in` also `SessionDto` and the session cookie |

- `status`: `pending`, `claimed`, `approved`, `completed`, `expired`,
  `cancelled`, `contested`. `expired` is reported for any unfinished pairing
  past its current deadline; the row may still hold the earlier status.

**Exact shapes** (camelCase JSON; times ISO-8601 UTC):

- **Deadlines.** `pending`: `created_at + 2 min`. `claimed`: `claimed_at + 2
  min` for approval. `approved`: `approved_at + 2 min` for completion. Every
  `expiresAt` in a response is the deadline of the current state.
- **Create** `POST /api/auth/pairings` (no body): 201
  `{id: uuid, secret: string, typedCode: string, expiresAt}`. `secret` is 32
  random bytes in base64url without padding (43 characters). `typedCode` is 10
  Crockford base32 characters shown as `XXXXX-XXXXX`.
- **Desktop status** `GET /api/auth/pairings/{id:guid}`:
  `{status, expiresAt, device: string | null}`. `device` is the claiming
  phone's user-agent summary, present from `claimed` on.
- **Approve** `POST /api/auth/pairings/{id:guid}/approve` `{code: "123456"}`:
  204 and status `approved`. Only valid while `claimed`, otherwise 400
  `pairing-invalid` (or `pairing-expired` past the deadline). A wrong code is
  400 `pairing-code-invalid` with an `attemptsLeft` extension; the third wrong
  code answers `attemptsLeft: 0` and cancels the pairing.
- **Cancel** `DELETE /api/auth/pairings/{id:guid}`: 204, idempotent for
  unfinished pairings of the creating session.
- **Claim** `POST /api/auth/pairings/claim` `{secret}` or `{typedCode}`: 200
  `{code: "123456", expiresAt, journal: {displayName, wallet}}`, where
  `wallet` is the signer's EIP-55 address and `expiresAt` the approval
  deadline. Unknown, used, wrong-origin, finished or mistyped →
  400 `pairing-invalid`; past the claim deadline → 400 `pairing-expired`; a
  pairing that is already claimed → 409 `pairing-contested` and the pairing
  becomes `contested`. Sets or reuses the binding cookie.
- **Complete** `POST /api/auth/pairings/complete` `{remember: boolean}`, polled
  by the phone: 200 `{status: "waiting"}` while `claimed`; 200
  `{status: "signed-in", session: SessionDto}` with the session cookie set and
  the binding cookie cleared once `approved`; 200 `{status: "expired" |
  "cancelled" | "contested"}` for the claiming phone when the pairing ended.
  No binding cookie, or one that matches no claimed pairing → 400
  `pairing-invalid`.
- **Sessions list** `GET /api/auth/sessions` rows gain
  `source: "wallet" | "pairing"`.
- **Step-up.** The challenge accepts `action: "device.pair"` with no `target`;
  `POST /api/auth/pairings` without a live `device.pair` grant answers 403
  `step-up-required` with `action: "device.pair"`.
- **Already signed-in phone.** If the phone already has a valid session when
  it opens a `#pair=` link, the frontend removes the fragment and shows
  "This device is already signed in" instead of pairing.
- **New codes:** 400 `pairing-invalid` (unknown, used or wrong binding), 400
  `pairing-expired`, 400 `pairing-code-invalid` (with `attemptsLeft`), 409
  `pairing-contested`.
- **Guards.** `claim` and `complete` are public auth POSTs. They follow the
  existing request guard (exact accepted `Origin`, `Sec-Fetch-Site`,
  `X-Vessel-Request: 1`) and the pairing's stored origin must equal the
  claiming origin. Approve and complete recheck, under the user lock, that the
  user is active and the signer wallet is still linked.
- **Rate limits.**

  | Route | Limit |
  | --- | --- |
  | create, approve | 10 per 10 min per session (the step-up bucket) |
  | claim | 10 per minute per client IP; 300 per minute global |
  | complete | 60 per minute per client IP, enough for 2 s polling |

## Typed code fallback

For when scanning fails: a damaged screen, glare, a camera refused or
unavailable (plain-HTTP Development origins).

- **Desktop.** Under the QR, **Show a code to type** reveals a 10-character
  code in two groups, such as `7KQ2M-HX9PD`. It belongs to the same pairing
  and expires with it.
- **Phone.** The sign-in screen's scanner, and its fallback message when the
  camera is unavailable, offer **Enter a code instead**. Submitting it calls
  `claim` with `{typedCode}`; from there the flow is identical (confirmation
  code, desktop approval, complete).
- **Format.** 50 random bits in Crockford base32. It is normalized like
  invitations before hashing: dashes and whitespace removed, upper case, `I`
  and `L` read as `1`, `O` read as `0`. It is stored only as its SHA-256.
- **Guessing.** It is only valid while its pairing is `pending` (at most 2
  minutes), at most 1,000 pairings are unfinished, and `claim` allows 300
  attempts per minute globally. That gives a blind guess a chance of about
  5 × 10⁻¹⁰ per pairing lifetime, and a correct guess still needs the owner
  to type the guesser's confirmation code on the desktop. A wrong typed code
  answers the same `pairing-invalid` as an unknown secret.

## Persistence

New identity table `device_pairings`, in its own migration in the identity
schema only. Nothing in the journal schema changes.

| Column | Notes |
| --- | --- |
| `id uuid`, `user_id`, `created_session_id`, `signer_wallet_id` | |
| `secret_hash bytea` | Unique |
| `typed_code_hash bytea` | Unique among unfinished pairings (partial index) |
| `origin` | Normalized desktop origin |
| `status text` | |
| `binding_hash bytea null`, `code_hash bytea null`, `code_attempts int` | |
| `device_summary varchar(80) null` | |
| `created_at`, `expires_at`, `claimed_at null`, `approved_at null`, `completed_at null` | |

- Global cap of 1,000 unfinished pairings; the oldest are evicted at the cap.
- The hourly identity cleanup deletes finished and expired pairings after a day.
- Suspension, wallet removal and `identity revoke-sessions` cancel the user's
  open pairings in the same transaction.
- The restore reset deletes all pairings.
- Plus `sessions.source text not null default 'wallet'`.

## Frontend

- **Desktop.** A Connect a phone button in Sessions opens a dialog:
  1. step-up (existing `WalletSignFlow`);
  2. QR with a countdown;
  3. "Safari on iPhone wants to connect", with a code field;
  4. connected, and the session list reloads.

  The QR is always dark on light with a quiet zone, whatever the theme, so
  phone cameras can read it. It is drawn as SVG by a small, dependency-free
  encoder.
- **Deadlines belong to the server.** A claim and an approval each restart the
  two-minute window, so a countdown reaching zero on either screen only asks the
  server at once; the request ends when the server says it expired, never from
  the local clock alone. A reply to an approval is dropped once its pairing has
  ended or Try again replaced it.
- **The origin must be reachable from the phone.** If the page's origin is
  loopback (`localhost`, `127.0.0.1`, `[::1]`), the dialog explains that a phone
  cannot open it and offers no QR.
- **Scan from the sign-in screen (phones and tablets).** The sign-in screen
  offers **Scan a code from your desktop** beside the wallet sign-in. It opens
  a camera view, decodes the QR, and continues on the pairing screen below
  without leaving the page. Opening the QR with the system camera app still
  works too.
  - **When it shows.** It is offered on any device whose browser can use a
    camera (`navigator.mediaDevices.getUserMedia` in a secure context), not
    chosen by user agent. Wallet sign-in stays available beside it.
  - **Plain HTTP.** A Development `http://` LAN origin has no camera access.
    There the option points to the system camera app or **Enter a code
    instead**.
  - **Decoding.** The browser's `BarcodeDetector` is used where it exists
    (Chrome on Android). Elsewhere, Safari on iPhone and iPad included, a small
    pure-JavaScript decoder is loaded only when the scanner opens. Wasm decoders
    are avoided because they would need `'wasm-unsafe-eval'` in the CSP.
  - **What it accepts.** Only a URL on the page's own origin with a `#pair=`
    fragment. A code for another Vessel origin is refused ("This code is for a
    different Vessel"); any other content is ignored and scanning continues.
  - **Camera handling.** The camera starts on the user's tap, prefers the rear
    camera and stops as soon as a code is read, the dialog closes or the page
    is hidden. Nothing is recorded or uploaded; only the secret is posted to
    `claim`. A refused permission explains how to allow it, and the system
    camera app remains an alternative.
  - **Headers.** No CSP change is needed: the stream plays through `srcObject`,
    and the decoder is a same-origin script. Add
    `Permissions-Policy: camera=(self), microphone=(), geolocation=()` to the
    static security headers and `verify-release.py`.
- **Phone.** Before the sign-in screen, the sign-in gate checks for `#pair=`,
  or receives the secret from the scanner. Either way it shows the pairing
  screen:
  - the journal name and short wallet;
  - the confirmation code in large digits;
  - the Keep me signed in box;
  - the waiting status.

  On success the full reload lands on Overview. Without a fragment nothing
  changes.
- The responsive layout already covers the phone; no page changes.

## Security notes

- **The secret** gives no access by itself, can be used once and lasts 2
  minutes. Only its hash is stored, and it never appears in a request line or
  log.
- **A photographed or screen-shared QR** gets an attacker no further than a
  claim. Their claim either contests the owner's own claim, which cancels the
  pairing, or shows a code the owner does not have.
- **A typed code** is a weaker secret than the QR's, so it only lives as long
  as the pairing and still needs the desktop approval; see
  [Typed code](#typed-code-fallback).
- **A malicious link** (someone sends the owner a pairing URL for the
  attacker's journal) would need the owner to type the attacker's code on the
  attacker's desktop. The phone also names the journal and wallet before
  anything is approved.
- **HTTPS.** Outside Development every origin is `https://`, with `__Host-`
  cookies. A Development `http://` LAN origin works with the existing
  non-prefixed cookie names; the plaintext caveats of that mode apply.

## Delivery

One PR, with one adversarial Sol (Codex) review before it opens:

- **Backend:** migration, endpoints, rate limits, cleanup and restore reset.
  Tests: identity tests for every state transition, contested claims, wrong
  codes, expiry, wallet removal and suspension during a pairing, lifetimes and
  rate limits. The endpoint inventory and isolation suite gain the new routes.
- **Frontend:** desktop dialog, sign-in scanner, typed-code entry, phone
  screen and API client, with Vitest (including origin checks on scanned
  content, typed-code normalization and the camera stopping).
- **Browser check:** two Playwright contexts (desktop and phone viewport)
  pairing against the isolated synthetic stack. The phone side runs once
  through the URL, once through the scanner using Chromium's fake camera fed
  with a rendered QR, and once with the typed code.
- **Owner check:** a real phone on the tailnet development instance.
- **State:** recorded in `device-pairing-state.md`.

No deployment, live migration or release is implied.

## Implementation choices — October 27, 2026

Settled during implementation, within the decisions above:

- **Rate buckets.** Create and approve share their own per-session bucket
  (10 per 10 minutes), separate from step-up and wallet linking, so pairing
  attempts never use up a step-up.
- **Typed codes never contest.** A typed code is only valid while its pairing
  is `pending`, so a typed claim on a claimed pairing answers
  `pairing-invalid`. A blind guess therefore cannot cancel a real claim; a
  second **secret** claim still contests.
- **Malformed approval codes** (not six digits) answer `request-invalid` and do
  not count as an attempt; only wrong six-digit codes do.
- **Wallet removal** cancels all of the user's open pairings, not only those
  signed by the removed wallet.
- **Desktop.** The step-up is asked every time Connect a phone starts, even if
  a grant is still live. A paired session does not show Connect a phone,
  because it cannot sign a step-up.
- **Browser check.** The release stack serves `https://localhost`, where the
  dialog correctly shows the loopback explanation instead of a QR. The browser
  smoke therefore signs a real `device.pair` step-up and creates and approves
  pairings through the session API, while the phone side runs the real
  screens: the link, the typed code and a contested claim. The dialog's
  QR → approve flow is covered by Vitest, which decodes the rendered QR.

## Open questions

None. The owner answered the October 27 questions (P5–P7, and P2's desktop
alignment).

## Later

- **Installable web app:** manifest, icons and a service worker that never
  caches `/api`; the CSP needs `manifest-src`/`worker-src`. On iOS a Home Screen
  app keeps its own cookies apart from Safari, so it must pair from inside the
  app; the sign-in scanner above already covers that.
- **React Native:** native apps send no browser `Origin` and keep no
  `SameSite` cookies, so they would need a separate per-device token kept in
  Keychain or Keystore. That is a deliberate exception to the "no bearer path"
  rule and needs its own decision. The pairing API above is meant to be reused,
  with a different final step.
