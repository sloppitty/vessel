# Notification Hub and in-app notifications: development plan

October 17, 2026. Status: **implementation authorized on October 17, 2026, with Sol (Codex) for design discussion and verification. The owner accepted the recommendations below provisionally, to be revisited once they can see it working, and added a Notifications page with an unread badge and timed popups.** Verified progress is recorded in [notification-hub-state.md](notification-hub-state.md).

The owner asked to start the Notification Hub with in-app notifications. The [monitor plan](play-monitor-plan.md#notification-hub-future) fixed the boundary: one `notifications` subscription on the `vessel.events` topic, per-owner rules, grouping by `runId`, relevance and expiry checks at delivery, a delivery record per notification and channel, and providers that never change publishers or the event contract. This plan fills that boundary for the first channel, `in-app`: popups plus a Notifications page. Telegram, Discord and price or drawing alerts remain later work.

## Multi-user integration — October 18, 2026

The integrated contract is [multi-user delivery §16](multi-user-delivery-contract.md#16-implementation-round-clarifications).
The subscription registers server-wide with per-owner progress; API, preferences,
notifications, claims and retention are owner-scoped. Suspended-owner live events
are acknowledged without notifications. Wallet workers use the identity directory,
including owners created after provisioning. Test requests have a 6/minute/user/API-process
cap besides the per-owner cooldown. Health is per deployment through
`NotificationWorkerState`, **not** per owner. Offline upgrade order and stop-all-hosts
rules are in the [runbook](../../deploy/README.md#multi-user-wallet-sign-in-this-delivery):
renamed `20261018100000_ServerEventSubscriptions`, renamed
`20261018110000_ServerMonitorState`, then `20261018120000_ServerNotificationState`,
which creates empty `server_notification_heartbeats` and drops/replaces
`notification_heartbeats`. No rolling upgrade or liveness backfill.

## What the owner has settled

October 17, 2026:

- **Notifications page.** A Notifications entry in the navigation opens a page listing notifications, so the owner can check them later. This supersedes the MVP alignment remark that a notification centre was not requested.
- **Unread badge.** The entry shows the usual red dot with the number of unread notifications.
- **Timed popups.** Every popup closes by itself after a timer, because the notification remains on the page. Critical popups stay longer than warnings, and warnings longer than information.
- **Recommendations accepted for now.** The eight decisions listed under [Decisions](#decisions) follow the recommendations, to be revisited after the owner has seen the feature running.

Unchanged rules from the monitor: PostgreSQL is the record and RabbitMQ is transport (November 20, 2026, issue #99: superseded; PostgreSQL `LISTEN/NOTIFY` is the transport and there is no broker, see [messaging and workers](messaging-and-workers.md#transport)); owner scope everywhere; exact decimal strings; no secrets or venue messages in logs or messages; migrations are explicit and never run at startup; tests never touch the owner's database; themes change colours and radius only, and the approved section positions stay.

## What already exists

- **Events.** Monitor runs and owner actions write `VesselEvent`s in the same unit of work as the change and publish them once to `vessel.events` (catalogue in [messaging and workers](messaging-and-workers.md#catalogue-version-1)). Each event carries `runId`, `severity`, `timeliness`, `relevance`, `expiresAt`, `subject` and exact-string `data`.
- **Subscriptions.** A module declares a subscription; `provision-messaging` creates its queue and registration; the dispatcher settles one receipt per event; catch-up recovers from PostgreSQL. Handlers are database-only and idempotent. Only the `verification` subscription exists, and only in release checks.
- **Workers.** `Vessel.Worker` runs the modules listed in `Vessel:Worker:Modules`. A deployment's module set is fixed once provisioned, so a new module needs its own deployment and Compose service.
- **Browser stream.** The shell keeps one SSE stream per tab (`GET /api/events/stream`, at most four per owner). Event frames only trigger a debounced reload of stored state; the stream comment already says a reset must reload state, not replay popups.
- **Popup stack.** `NotificationProvider` and `useNotifications()` (`frontend/src/components/notifications`) show transient popups: tones `error`, `success` and `info`, a `key` that replaces, timers paused on hover or focus, at most four shown, live regions kept mounted. Nothing in it is durable.
- **Shell.** The sidebar lists the workspace pages, then Settings and the owner card at the bottom; Accounts already shows a count. On narrow screens the header holds only the menu button and the brand.

## Goals

1. Shortly after the monitor commits something worth the owner's attention, every open tab updates its badge and list without a reload, and one visible tab shows the popup.
2. Every notification stays on the Notifications page, unread until the owner reads it, whether or not its popup was seen.
3. The sidebar shows the unread count, updated live in every tab.
4. A notification pops up once, not again after a reload or in another tab.
5. One monitor run produces one notification, not one per event.
6. A problem that cleared itself before the owner read it is withdrawn rather than counted.
7. The owner controls what reaches the in-app channel from Settings.
8. The same records and rules serve Telegram and Discord later without changing publishers, the event contract or the in-app path.

## Non-goals

- External channels (Telegram, Discord, email), browser system notifications or Web Push.
- Price, level or drawing alerts, and any new monitor detection.
- Changing the event contract, existing producers, monitor cadence or approved Play layout.
- Exactly-once delivery. In-app delivery is idempotent in the database; external channels will repeat after crashes, as already documented.

## Architecture

```text
 monitor worker / API ──unit of work──► events + outbox ──► vessel.events (topic)
                                                               │ binding "#"
                                                               ▼
                                         vessel.events.sub.notifications (+ .dlq)
                                                               │ inline listener, dispatcher, receipt
                         ┌─────────── worker-notifications (module "notifications") ───────────┐
                         │ NotificationHub handler, in the dispatcher's savepoint:             │
                         │   withdraw ─► rules ─► relevance ─► group by run ─► compose         │
                         │   in-app delivery row "unread", feed sequence (database only)       │
                         │ hosted: notification retention; heartbeat after each catch-up pass  │
                         └─────────────────────────────────────────────────────────────────────┘
                                                               │ PostgreSQL
 browser ◄──SSE (event + notification frames)── API ◄──────────┘ cursor reader over the in-app feed
         ──REST: head, page, claim, read, preferences, test──► API (unit of work, same feed)
```

- **The hub is a subscriber, nothing more.** It owns the `notifications` subscription, the rules and the records. Producers are unchanged and know nothing about it.
- **In-app is a database channel.** Its delivery is a row the browser reads; there is no external send, so it is created inside the hub's receipt transaction. No `vessel.notify.in-app` queue (decision 1).
- **The browser reads notifications through the API**, over the existing stream, from a second commit-ordered feed (decision 2).
- **PostgreSQL stays the record.** A lost broker delays notifications until catch-up; it loses none.

### Dependency rule

Same as the monitor: Domain and Application never reference Wolverine or RabbitMQ.

| Project | Parts |
|---|---|
| `Vessel.Application` | `Notifications/`: `NotificationHub` (implements `IVesselEventSubscriber`), `NotificationRules`, `NotificationIdentity` (dedup key parsing), `NotificationComposer`, `NotificationService` (head, page, claim, read, preferences, test), the `INotificationStore` port, `NotificationOptions` |
| `Vessel.Persistence` | Raw-SQL tables, `NotificationStore`, the feed cursor, unread count and reset version, notification retention, account deletion and disable handling, migration `NotificationHub` |
| `Vessel.Messaging` | `Modules/Notifications/NotificationsModule` (subscription `notifications`, no command queue), registered in `VesselModuleRegistry.From`; a heartbeat hook in `SubscriptionMaintenanceService` |
| `Vessel.Worker` | `case "notifications"` in `WorkerModules.AddHostedServices`: notification retention and the heartbeat writer |
| `Vessel.Api` | `NotificationEndpoints`, notification frames on the event stream, `notificationsOnline` in the monitor summary |

There is no domain model: like the event tables, notification state lives in raw-SQL tables behind the store. The `INotificationChannel` provider port the monitor plan reserved arrives with the first external channel; until then the hub writes the in-app delivery itself. A later Telegram channel will write a `pending` delivery and stage its provider command through the outbox in the same transaction, and send from elsewhere, never under the receipt lock.

## The hub

Revised on October 17 after Sol's design review; see [Design review](#design-review-october-17).

### Subscription

- **Name `notifications`, module `notifications`, filter `#`.** Filters are permanent, so a narrow list would need a new subscription name each time a category is added (`position.liquidated`, `price.crossed`). With `#` the rules decide and unknown types are skipped. The cost is one receipt per event, which is negligible at monitor volumes.
- `SupportedVersion` 1 and default limits (12 attempts, 1 minute to 1 hour backoff).
- First provisioning registers it at the owner's current sequence, so an upgrade never notifies about history.

### Two kinds of notification

- **Run notifications** group the lifecycle events of one account's run: `play.opened`, `play.closed`, `play.status-changed`, `execution.filled`, `link.needs-confirmation`, with `execution.link-changed` as detail lines only. They are news and are never withdrawn.
- **Standalone notifications** carry exactly one `discrepancy.*`, `monitor.state-changed`, `credential.expiring` or `notification.test-requested` event. Keeping them apart from run notifications lets a later event withdraw exactly the notification it supersedes, without hiding unrelated news.

The guarantee is one notification per eligible, retained batch of an account's run, with follow-ups for events committed later under the same run or beyond `RunGroupLimit`. There is no waiting timer: catch-up's discovery grace is not a run-completion barrier.

### Handling one event

The handler runs inside the dispatcher's savepoint. All effects are database-only and keyed so a redelivery changes nothing.

**Run events.**
1. Take a transaction advisory lock on (owner, `runId`, account).
2. If `notification_sources` already covers this event, return `Delivered`.
3. Read the retained events of the same owner, `runId` and account that no source covers, above the subscription's `responsible_from` and the event retention floor, in sequence order (indexed on `payload->>'runId'`), paging past rejected candidates (up to ten pages of 200), and always the waking event itself.
4. Keep each candidate only if it passes every check itself: version 1, not expired, detected within `MaxEventAge`, triggered by the monitor, allowed by the preferences, its Play still exists, and for `link.needs-confirmation` the link is still actionable (still `suggested`, its Play still Planned, Paused or Open, and its order not linked elsewhere). Discovery grace does not apply to peers that are already visible.
5. Without at least one headline (non-detail) candidate, return `Skipped` and write no source row, so a detail line can still join a later batch.
6. Otherwise cover up to `RunGroupLimit` events: headlines first (always including the waking event when it is an eligible headline, so every eligible headline's own dispatch covers it and overflow is never lost), then detail lines in the remaining budget. Compose them into one notification, write a source row per covered event and hand off to the channels. Peer receipts are neither locked nor settled; their own dispatches find them covered.

**Standalone events.**
1. Parse the subject and the revision it announces from the documented v1 dedup keys, centrally and with validation: `discrepancy:{id}:{revision}:{transition}`, `monitor:{accountId}:{stateRevision}:{state}`, `credential:{accountId}:{credentialRevision}:expiring`. A key that does not parse is skipped. Generations are ordered by the event's commit sequence: every later announcement of a subject commits later, and a credential revision is a hash with no order of its own. Subject, generation, revision and transition are stored on the source row, so they outlive event retention.
2. Take a transaction advisory lock on (owner, subject).
3. If a source of the same subject has a **newer** generation, this event arrived out of order: record it as handled and stop.
4. **Withdraw first**, whatever the preferences, trigger or age: unread in-app deliveries of older generations of the same subject, by the matrix below.
5. Then decide whether this event creates a notification: the decision rule below, the event's eligibility (trigger, age, preferences, with the notification's own severity) and its relevance to current state (the discrepancy record's `PublishedRevision` is not newer and the record exists; the account's `StateRevision` is not newer; the credential still exists with the same revision and has not expired).
6. Record a source row either way (with the notification, or as handled without one).

| Event | Withdraws unread older generations of the subject | Creates a notification when |
|---|---|---|
| `discrepancy.opened` | all | eligible |
| `discrepancy.unverifiable` | `resolved` only (unverifiable is not a resolution) | eligible |
| `discrepancy.resolved` (resolved or dismissed) | all | resolution is `resolved`, eligible, and an older notification of the discrepancy was **read** |
| `monitor.state-changed` to `degraded`, `stale` or `credential-error` | all | eligible; severity `warning` |
| `monitor.state-changed` to `live` or `idle` | all | the latest problem since the last healthy state has a **read** notification; severity `info` |
| `credential.expiring` | all | eligible and still relevant |
| `notification.test-requested` | — | the in-app channel is enabled (exempt from trigger, category and severity rules) |

A discrepancy whose unverifiable outcome is later cleared silently (fresh evidence on an open record publishes nothing) keeps its unverifiable notification as a historical note; there is no reconciliation loop.

**Errors.** A missing or deleted subject makes the event irrelevant (`Skipped`), never a retried failure. `TerminalEventException` is used only for a payload the composer cannot read.

### Locks

One hierarchy, kept by every writer:

| Writer | Order |
|---|---|
| Hub | dispatcher's locks (retention advisory shared, subscription row, receipt) → run or subject advisory lock → notification cursor row → notification and delivery rows |
| API read, read-all, claim, preferences, test cooldown | notification cursor row → delivery rows |
| Account disable or enable | existing management and account locks → notification cursor row → delivery rows |
| Account deletion | existing management and account locks → event rows and their receipts → notification cursor row → notification rows |
| Notification retention | notification cursor row → notification rows |

- **The hub never locks an account row.** Notifications have no foreign key to accounts or Plays (an FK check would take a key-share lock on the account and could deadlock with deletion). The notification cursor row is the serialization point with disable: the hub re-reads the account's enabled state after acquiring it, so a disable that committed first is seen, and a disable that waits for the hub withdraws what the hub created.
- **Deletion deletes events (waiting for in-flight receipts) before taking the notification cursor,** so it never holds the cursor while waiting for a dispatcher that wants it.
- The cursor is held only for bounded set-based writes and the sequence and count update, never while scanning events or composing content. Notification writes never touch `event_cursors`; reading its epoch takes no lock.

### Default rules for in-app

| Category | Event types | On by default |
|---|---|---|
| Executions and lifecycle | `play.opened`, `play.closed`, `execution.filled`, `play.status-changed` from the monitor | Yes |
| Link confirmations | `link.needs-confirmation`; `execution.link-changed` only as a detail line inside a run | Yes |
| Discrepancies | `discrepancy.opened`, `discrepancy.unverifiable`; `discrepancy.resolved` when an older one was read | Yes |
| Monitor health | `monitor.state-changed` to a problem state; returning when the problem was read | Yes |
| Credentials | `credential.expiring` | Yes |
| Never | `monitor.run-completed` (the Check button already reports it), owner-triggered events, unknown types | — |

Minimum severity defaults to `info`.

### Content

The hub composes the text once, so every channel says the same thing and golden tests can pin it. Content is bounded and contains only server-authored text, exact decimal strings and names snapshotted from Vessel records:

```json
{
  "title": "BTC Long closed",
  "summary": "Realized -320.5 USD before fees.",
  "lines": ["Entry filled: Buy 0.5 at 84541", "Stop filled: Sell 0.5 at 83900", "Fees 0.42 USDC"],
  "late": true,
  "links": [{ "kind": "play", "label": "Open Play", "playId": "…", "accountId": null }],
  "subject": { "accountId": "…", "accountName": "Main", "venueId": "hyperliquid", "playId": "…", "playTitle": "BTC Long", "instrument": "BTC" }
}
```

- Title at most 160 characters, summary and lines at most 200, at most 8 lines (the last one says how many changes were left out), at most 3 links with labels of at most 60. Owner and venue names are cleaned of control and format characters and bounded. Link kinds are `play`, `play-execution`, `account` and `account-credential`; the browser maps them to its own navigation and never follows a URL from content.
- **P&L wording keeps the reported basis:** "before fees" for gross, "after fees" for net-of-fee, "mixed fee basis" otherwise, with the reported fees by token as a line. It never claims a funding-complete result.
- `late` is true when an included event is `late`, or when the hub handles it more than twice the monitor interval after it was detected (the hub was down). The notification then shows its source time.
- Superseded events in a run are folded into lines under the event that overtook them ("Play closed" tells the whole story).
- Never included: venue error text, tokens, dismissal notes, discrepancy details or raw payloads.
- A renamed or deleted Play keeps the snapshotted name; the link then says the Play is no longer available.

## Records

Tables are created by raw SQL in one migration, like the event tables, with explicit owner predicates and no EF entities or foreign keys to accounts or Plays.

| Table | Holds | Written by |
|---|---|---|
| `notification_preferences` | Per owner and channel: enabled, minimum severity, enabled categories, `revision`. A missing row means the defaults | Settings API |
| `notifications` | ID (UUIDv7), owner, `created_sequence` (immutable), account, Play, run ID, kind (`run`, `discrepancy`, `monitor`, `credential`, `test`), category, severity, content (JSON), earliest `occurred_at`, latest `detected_at`, `created_at` | Hub |
| `notification_sources` | Owner and event ID (primary key), notification ID or none, event type, subject key, generation, transition or state, `handled_at` | Hub |
| `notification_deliveries` | Notification and channel (primary key): state, `shown_at`, `read_at`, `feed_sequence`, `state_at` | Hub, API, account disable |
| `notification_cursors` | Per owner: feed `sequence`, `unread_count`, `reset_version`, `last_test_at` | Every delivery change |
| `server_notification_heartbeats` | Per deployment: last productive maintenance pass; system `NotificationWorkerState` store | Notifications worker |

- **In-app states.** `unread`, `read` and `withdrawn` (it no longer applied before being read, or its account was disabled). `shown_at` records that a popup claim won it; it never changes the state and does not advance the feed.
- **Feed sequence.** Every delivery state change (created, read, withdrawn) takes the next owner feed sequence under the cursor row lock, held to commit. The feed is a latest-state synchronization feed: a change below the cursor can never appear later, and a reader converges on the current state of every delivery.
- **Created sequence.** `notifications.created_sequence` never changes. "Mark all as read" uses it, so a notification that existed at the browser's head is included even if its delivery changed later.
- **Reset version.** Changes that remove or hide rows without a surviving row to announce them increment `reset_version`: account deletion, disable and re-enable, preference changes and notification retention. A browser cursor from an older reset version gets a reset and reloads.
- **Unread count.** Kept on the cursor row and changed from the rows actually transitioned, under the same lock. Every read returns rows, head, count and reset version from one statement snapshot, as the event reader does.
- **Disable and re-enable.** Disabling an account withdraws its unread deliveries and hides its notifications from the page. Re-enabling shows them again, still withdrawn; nothing becomes unread or pops up again.
- **Retention.** `Vessel:Notifications:RetentionDays` (default 90) deletes old notifications with their sources and deliveries, and handled source rows without a notification after the same time, so a subject's latest closing generation stays known as long as its notifications. `RetentionDays` must exceed `MaxEventAge` by at least a day, so an event old enough to have lost its source can no longer create a notification.
- **Restore.** Epoch rotation resets both browser feeds. A backup older than a popup claim can show that popup again; popup-once holds within the surviving database history.
- One migration, applied only by `migrate`; envelope storage `wolverine_notifications` comes from provisioning.

## Delivery to the browser

### Cursor

A notification cursor is `{epoch}.{resetVersion}.{sequence}`, with the event stream's epoch, so `rotate-event-epoch` resets both feeds. It is opaque to the browser except in `frontend/src/api/event-cursor.ts`, which already owns the event cursor syntax. A cursor from another epoch or reset version, or ahead of the head, gets a reset naming the head.

### Stream

The existing stream gains an optional second cursor, so a tab keeps one connection and the four-stream limit is unchanged.

- `GET /api/events/stream?after={eventCursor}&notificationsAfter={notificationCursor}`. Without `notificationsAfter` it behaves exactly as today.
- Both cursors are validated and both first pages read before the SSE headers are sent. The stream slot is acquired once and released once.
- The first `status` frame adds `notifications: { state: "ready" | "reset", nextCursor, unreadCount, enabled }`; a later notification reset sends a status frame with only the `notifications` part.
- A `notification` frame carries `{ cursor, unreadCount, enabled, notification }`, where `cursor` is that row's own position, never a head the client has not received. The SSE `id:` line stays the event resume cursor; `sse.ts` is unchanged, and the decoder reads notification cursors only from data.
- Each poll reads both feeds; bounded batches of each are drained in turn, and the stream sleeps only when both are caught up.

### REST

| Route | Purpose |
|---|---|
| `GET /api/notifications/head` | `nextCursor`, `unreadCount` and `enabled` from one snapshot |
| `GET /api/notifications?tab=unread\|all&category=&severity=&page=&pageSize=` | The Notifications page, newest first, following the [shared paging rules](list-paging.md#shared-query-rules), with counts per tab; disabled accounts' notifications are hidden |
| `POST /api/notifications/claim` | Atomically claims every unread, unshown, visible notification for popups while the channel is enabled, setting `shown_at`. Answers the claimed notifications created within `PopupWindow`, most severe then newest, at most three, plus the number of other notifications it claimed. A second tab gets nothing |
| `POST /api/notifications/{id}/read` | Mark one read; idempotent; a withdrawn one answers its state |
| `POST /api/notifications/read` | `{ "upTo": cursor }`: mark every unread notification with `created_sequence` at or below it read. Another epoch or a cursor ahead of the head answers 409 |
| `GET` and `PUT /api/notifications/preferences` | The in-app preferences and the hub's status; `PUT` carries the `revision` and answers 409 on a stale one |
| `POST /api/notifications/test` | Records a `notification.test-requested` event (owner trigger). The cooldown is checked and stored under the cursor lock in the same transaction; within `TestCooldown` it answers 429 with `Retry-After` |

All routes are owner-scoped, `no-store` and bounded; filters and pages run on the server. The API reports `notificationsOnline` from the notifications heartbeat, never from the monitor's.

### Start-up order in the browser

1. Read the head (`GET /api/notifications/head`) after the event head, without holding up the workspace's stored reads if it fails.
2. Open the stream from both cursors. Frames upsert by notification ID and position.
3. A visible, focused tab claims popups at start-up and whenever a frame brings a new unread notification; a hidden tab waits until it becomes visible and focused.
4. On a notification reset, read the head again, drop popups for notifications that no longer exist or are no longer unread, and reload the Notifications page if it is open. Responses from an older epoch or reset version are ignored.

## Navigation and badge

- **Notifications** is a page (`#notifications`) with a bell icon, placed in the sidebar's bottom group above Settings, since it is not workspace data.
- **Red badge.** When there are unread notifications, the entry shows a red dot with the number (`99+` above 99), using the theme's `--destructive` and `--destructive-foreground` tokens; built-in themes must keep passing the contrast test for that pair. With the sidebar collapsed, the badge sits on the icon. The entry's accessible name includes the count ("Notifications, 3 unread").
- **Narrow screens.** The sidebar is behind the menu button there, so the header also gets a bell button with the same badge, linking to the page; the menu button shows a small red dot when there are unread notifications.
- The count updates from the stream in every tab. No other page, and none of the approved Play layout, moves.

## Notifications page

- **Heading** "Notifications" with **Mark all as read** and a **Preferences** link to Settings.
- **Tabs** Unread and All, with server counts. All also lists withdrawn notifications, marked "No longer current".
- **Filters** category and severity (`NativeSelect`), kept in shell memory across navigation like the Activity filters.
- **Rows**, newest first: severity mark and pill, title, summary, the account's identity capsule (venue logo, venue tag and short source), the time (relative, with the exact time on hover or focus), a Late pill when it applies, the links, and an unread dot. Expanding a row shows its detail lines. Using a link or **Mark as read** marks it read.
- **Paging** with the shared pager; delayed, geometry-matched skeletons that do not move under reduced motion; empty states for both tabs.
- New notifications appear at the top of an open page without moving the row that has focus.

## Popups

### Timers

Every popup closes by itself, because the notification stays on the page. The timer runs only while the tab is visible and focused, and pauses on hover or focus as today.

| Severity | Tone | Live region | Closes after |
|---|---|---|---|
| `critical` | `error` | assertive | 20 s |
| `warning` | new `warning` tone (theme `--warning`) | polite | 12 s |
| `info` | `info` | polite | 6 s |

The durations are frontend constants, easy to tune once the owner has seen them.

### Behaviour

- **Once only, by claim.** A visible, focused tab asks the server to claim unshown notifications; the claim is atomic, so exactly one tab wins each notification and only the winner pops it up. Other tabs and later reloads never pop it again. This is at most once: a tab that crashes between claiming and showing loses that popup, and the notification is still unread on the page. A hidden tab waits until it becomes visible and focused.
- **Closing is not reading.** When the timer ends, the popup closes and the notification stays unread on the page and in the badge. Dismissing it with the close button or using one of its links marks it read.
- **Every tab** removes a popup when the stream reports it read or withdrawn.
- **Links** use the shell's existing navigation (`viewPlay` and account detail), which already protects unsaved Play work. A missing Play or account shows a short notice instead.
- **Backlog.** One claim takes every unshown notification. The server answers the most severe and newest three created within `PopupWindow`, which pop up, and the number of the others; if there are others, one summary popup says "7 more unread notifications" with **Open notifications**, which goes to the page.
- **Old ones stay quiet.** A notification created more than `PopupWindow` before it is claimed never pops up on its own; it is counted in the badge, the summary and the page.
- **Focus.** When a popup with focus closes because another tab read it, it was withdrawn or it was pushed out of the stack, focus moves to the next popup or back to the page content, never to the document body.
- **Labels.** The close button's accessible name is "Mark as read and close"; the badge count is announced once when it changes, not on every poll; actions are named by their text, never by colour alone.
- **Shared stack.** Transient popups (saves, refresh results) and notification popups share the stack. A notification popup pushed out of the stack is still shown and unread.
- Reduced motion, focus order and the mounted live regions follow the current provider.

### Provider changes

- `NotificationTone` gains `warning`, with CSS from theme tokens only; built-in themes must keep passing the contrast test.
- `NotificationInput` gains `onClose(reason)` with `dismissed`, `action`, `timeout`, `replaced`, `evicted` or `withdrawn`, and `timer: 'visible-focused'` so the timer runs only while the document is visible and focused.
- Existing callers are unchanged.

## Settings

A **Notifications** section on the Settings page, beside Appearance:

- In-app notifications on or off (off also stops popups and the badge).
- Minimum severity: info, warning or critical.
- Category switches from the default-rules table.
- Hub status: "Delivering" or "Notification worker offline since 14:02", with any subscription lag or failed receipts.
- **Send test notification**, which travels the whole path (API, event, broker, hub, feed, stream) and pops up like any other.

Changes apply to events the hub handles afterwards; they never re-evaluate notifications already created.

## Health

- The notifications worker writes its own productive heartbeat (its own `server_notification_heartbeats` deployment row and file, same `<unix seconds> <timeout seconds>` format) after each maintenance pass in which every catch-up it runs succeeded. `SubscriptionMaintenanceService` gains a heartbeat hook for this; there is no second loop or liveness timer. The monitor's `server_monitor_heartbeats` row is never reused, so a healthy notifications worker cannot make a stopped monitor look online.
- `/app/worker-health` chooses its file by module: `Vessel__Notifications__HeartbeatFile` when `Vessel__Worker__Modules__0` is `notifications`, otherwise `Vessel__Monitor__HeartbeatFile` as today.
- While Plays are tracked and in-app notifications are on, an offline hub uses the shell's existing offline banner slot ("Notifications offline: they will arrive when it returns"). No new layout.
- Subscription lag, gaps and failed receipts appear in the Settings status. The SQL runbook queries in [messaging and workers](messaging-and-workers.md) cover the rest.

## Deployment

- A new Compose service `worker-notifications` from the same image: `Vessel__Worker__Modules__0: notifications`, `Vessel__Worker__Deployment: notifications`, its own healthcheck, no credential key ring (it never opens venue tokens), and the least-privilege broker user.
- `migrate` provisions the new deployment, queue, dead-letter queue and registration; no other service changes its module set.
- An installation that upgrades without ever starting the service keeps working: notifications do not arrive, and the subscription, holding no receipts, becomes `lagging` with a visible gap once events age past the event `RetentionDays`. A worker that ran and stopped can leave `pending` receipts, which hold event retention until they settle; restart the service or deregister the subscription. Deregistration is undone by the next provisioning while the module is compiled in; `deploy/README.md` says so.
- An explicit `Vessel:Messaging:Deployments` configuration needs a `notifications` entry; the default provisions it.
- Backups, upgrades and restores stop `worker-notifications` with the other hosts, including every hard-coded stop list in `scripts/verify-release.py`. Restore keeps the mandatory `rotate-event-epoch`, which resets both browser feeds.

## Configuration

| Setting | Default | Allowed |
|---|---|---|
| `Vessel:Notifications:RetentionDays` | 90 | 7 to 365, and at least `MaxEventAge` plus one day |
| `Vessel:Notifications:MaxEventAge` | 7 days | 1 hour to 30 days |
| `Vessel:Notifications:PopupWindow` | 24 hours | 5 minutes to 7 days |
| `Vessel:Notifications:RunGroupLimit` | 200 events | 1 to 1,000 |
| `Vessel:Notifications:HeartbeatFile`, `HeartbeatTimeout` | as the monitor's | as the monitor's |
| `Vessel:Notifications:TestCooldown` | 10 s | 1 s to 5 min |

The API and the notifications worker validate the same section.

## Delivery steps

Each step is a pull request on the previous one.

1. **N1, hub core (backend).** Tables and migration, `NotificationsModule` with the `notifications` subscription, rules, identity parsing, relevance, run grouping, withdrawal, composer, the feed cursor, unread count and reset version, the store's read, claim and read-all operations, account deletion and disable handling, notification retention, heartbeat. Unit and disposable-PostgreSQL tests, including blocked-transaction lock-order tests; a broker test from a monitor-style event to an unread delivery.
2. **N2, API.** Head, page, claim, read, read-all, preferences, the test event (new catalogue type with a golden test), notification frames on the stream, `notificationsOnline`. Owner-scope, paging, conflict and stream tests, including a reset after `rotate-event-epoch`.
3. **N3, browser.** `api/notifications.ts` with strict guards, cursor support in `event-cursor.ts`, the feed in the shell's stream hook, the sidebar entry and red badge, the narrow-screen bell, the Notifications page, provider changes and timed popups, the Settings section and the offline banner text. Unit tests and synthetic captures (Graphite and Paper, desktop and 390 px).
4. **N4, packaging and documentation.** Compose service, `deploy/README.md`, release verification (the service is healthy; a test notification reaches the page; with RabbitMQ stopped it arrives after catch-up), [messaging and workers](messaging-and-workers.md), `app-status.md` and `AGENTS.md`.

Later, with their own plans: Telegram (a module-owned provider queue; the monitor plan's `vessel.notify.<provider>` naming must be reconciled with `MessagingTopology`, which names command queues `vessel.commands.<name>`), Discord, browser system notifications, price and drawing alerts, and `position.liquidated`.

## Tests

- **Application:** rules per category, severity and trigger; dedup-key identity parsing, including malformed keys; composer golden content per type, including P&L basis and fees, `late`, bounds, cleaned owner text and a deleted Play; preferences defaults and validation.
- **PostgreSQL (disposable):** the dispatcher with the hub settles `delivered` and `skipped` and creates nothing twice on redelivery or catch-up; per-candidate eligibility in a run batch, a detail line that joins a later batch, follow-ups for a later unit and overflow; two dispatchers handling events of one run concurrently create one notification; the withdrawal matrix, including out-of-order generations, withdrawal by an owner dismissal and by an event filtered out by preferences, a resolution after a reopen and a read opening followed by a resolution; relevance for each listed type, including a hidden competing suggestion and an expired credential; a delayed commit is not skipped by the feed reader; claim wins once across concurrent callers and recheck the channel switch; read-all by created sequence; the unread count stays exact through every transition; account deletion removes and bumps the reset version; disable withdraws and re-enable restores visibility without unread; a hub racing a disable or a deletion (blocked transactions) neither deadlocks nor leaves an unread delivery on a disabled account; retention deletes by age and keeps handled sources for `MaxEventAge`; events older than `MaxEventAge` create none.
- **Broker (opt-in):** a published event reaches an unread delivery; a lost queue is recovered by catch-up; an owner-mismatched message is dead-lettered.
- **API:** owner scope; paging, tabs and filters; claim and read; `upTo` bounds and epochs; 409 on a stale preference revision; the atomic test cooldown; stream frames for both feeds with the unread count; a stream without `notificationsAfter` unchanged; resets after epoch rotation, deletion and preference changes.
- **Frontend:** guards; head-then-stream start-up and upsert by ID; independent event and notification cursors and resets; a claim that pops up only its winners; timers per severity that run only while visible and focused; a timed-out popup stays unread while close and links mark it read; withdrawal and reads in another tab remove popups and update the badge; the backlog summary and `PopupWindow`; badge rendering (`99+`, collapsed sidebar, narrow header, accessible name); page tabs, filters, paging and focus stability; links to a missing Play; Settings conflict handling; contrast of the `warning` tone and the badge pair in every built-in theme.
- **Release and browser smoke:** `worker-notifications` is healthy and stopped for backup and restore; live delivery, catch-up after a broker outage and notification state after a restore; a test notification pops up, closes on its timer, stays unread on the page with the badge, and reading it in one tab clears the badge in a second.

## Decisions

Accepted provisionally on October 17, 2026, to revisit after seeing the feature running:

1. **In-app channel without a queue.** The monitor plan listed `in-app` among the `vessel.notify.<provider>` queues. An in-app delivery is a database row with nothing to send, so it is created in the hub's transaction; queues stay for external providers.
2. **Browser transport.** Notification frames on the existing stream with a second cursor, keeping one connection per tab. (Alternatives were a second SSE stream or polling after each event frame.)
3. **What notifies by default.** The default-rules table, minimum severity `info`, owner-triggered events never.
4. **Hub offline.** The existing offline banner slot while Plays are tracked, otherwise only Settings.
5. **Backlog and history.** Replaced by the owner's choice: the Notifications page, the red badge and timed popups, with at most three popups plus a summary after an absence.
6. **Lifetimes.** Records kept 90 days; events older than 7 days create no notification; notifications older than 24 hours when first seen do not pop up.
7. **Test notification.** A new `notification.test-requested` event type, so the button and the release check prove the whole path.
8. **Content.** Composed once by the hub as channel-neutral text with exact values, so Telegram later says the same as the page.

## Design review, October 17

Sol (Codex) reviewed the first draft before implementation. The architecture stood: one subscription, in-app delivery in the receipt transaction, a separate worker deployment and one multiplexed stream. Its findings changed the following, all written into the sections above:

- **Locks.** A complete hierarchy; the hub never locks accounts and no notification table has a foreign key to accounts or Plays; the notification cursor serializes the hub with disable; deletion removes events before taking the cursor.
- **Popups.** An atomic server claim replaces "record shown", so exactly one tab wins each popup (at most once), and the claim answers the backlog's true count.
- **Withdrawal.** It runs before every preference, trigger and age rule, compares generations (event sequences of the same subject, whose revision comes from the dedup key), never treats `unverifiable` as a resolution, and only touches standalone notifications, which no longer share a notification with run events.
- **Feed.** An immutable created sequence for "Mark all as read", a reset version for deletion, disable and preference changes, and one statement snapshot for rows, head and count.
- **Grouping.** Every candidate in a run batch passes every check itself; detail lines are never consumed by a skipped event; follow-ups are explicit; an index on the run ID.
- **Relevance.** Generations are stored on source rows; a suggested link must still be actionable; credentials are checked for existence and expiry from metadata, never by opening the token; a missing subject is irrelevant, not a retried failure.
- **Health and release.** A separate heartbeat row tied to successful catch-up passes, a module-aware health script, and the new service in every release stop list.
- **Smaller points.** P&L wording keeps the reported basis and fees; hub delay marks a notification late; the test cooldown is atomic; popup focus and labels; the restore limit of popup-once; the narrower retention claim for a worker that stopped.

Sol then verified the backend implementation and found three defects, each with a failing test, now fixed: rejected or detail-only candidates could exhaust the run batch and lose an eligible notification; a recovery was announced when any earlier problem had been read rather than the latest one; and labels for several Plays skipped owner-text cleaning.

Simplified on the way: no domain model or provider port until an external channel exists, no reconciliation loop for a silently cleared unverifiable outcome, and no waiting timer for run grouping.

## Risks

- **A third stateful worker.** One more service to run and back up. Mitigation: the same image, provisioning and runbook as the monitor; notifications degrade to "arrive late", never "lost", while PostgreSQL holds the events.
- **Notification fatigue.** A noisy account can produce many notifications. Mitigation: run grouping, withdrawal, severity and category settings, timed popups and the backlog summary.
- **Stale news after downtime.** Mitigation: `MaxEventAge` at handling time, `PopupWindow` in the browser, `late` marking and relevance checks.
- **Owner text in content.** Play and account names are owner-entered. Mitigation: rendered as text by every channel; external channels escape for their own format.
