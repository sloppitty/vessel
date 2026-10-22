# Portfolio and account management contract

October 1, 2026. The owner requested rename/delete portfolios and accounts, disabling accounts to hide imported movements, and moving or unlinking accounts from portfolios.

## Behavior

- Unassigned accounts use `portfolioId: null`, not a seeded/default Portfolio row. They appear in All accounts or its Unassigned filter, not a portfolio tile or individual portfolio filter. Accounts may be created without a portfolio.
- Renaming changes only the display name, not the venue, address, source identity or history.
- Deleting a portfolio unlinks all its accounts, including disabled accounts, transactionally. It never deletes accounts, snapshots, fills or plays.
- Disabled accounts remain visible in account management with a Disabled marker and can be enabled again. Disable preserves all data, blocks refresh, and hides their imported fills and venue positions from normal activity, overview and detail routes.
- Overview/portfolio balances and position/fill counts exclude disabled accounts. Account record counts still include disabled records. UI coverage text must refer to enabled account values rather than imply every record contributes.
- Hard account deletion removes that account's retained imported facts and latest snapshots/positions. Require an explicit confirmation in the UI. If any Play head references it, return safe 409 and retain everything; do not delete trading intent as a side effect.
- Move/unlink does not rewrite imported facts or historical intent. Grouping uses the current account assignment; historical portfolio membership analysis is not part of this step.

## October 16, 2026: initial account refresh

Adding an account in the browser queues one initial refresh when its venue descriptor advertises `sync`.
Manual and disabled accounts are not queued. Wallet imports finish the optional read-only credential
save attempt before queueing each confirmed account, so its first read can use the saved credential.
A failed credential save still keeps the account and queues its available public reads.

The shell owns this session-only queue, shared with individual refresh and Refresh all: reads run
sequentially, queued/current account IDs are deduplicated, and a failure does not stop later accounts.
Closing the import dialog or navigating pages does not cancel queued work. Disconnect, shell unmount
or API replacement aborts the request and discards the queue without late notifications or reloads.
Account creation stays successful if its initial refresh fails; existing refresh notifications and
manual retry remain available. This is not a durable backend job, automatic retry or history backfill;
venue limits and fee/PnL integrity checks are unchanged.

## October 12, 2026: refresh and account filters

Accounts and Overview's enabled-account panel offer Refresh all. It takes every enabled account whose descriptor has `sync`, regardless of the current account filters, and calls the existing locked refresh endpoint one account at a time. The button shows "Refreshing N of M", the active row spins, and individual/bulk refresh buttons are disabled during any refresh. No eligible accounts also disables the action. Failures do not stop later accounts; one summary notification names failed accounts and the shell reloads once after the batch. Backend management/sync locking is unchanged.

Accounts combines portfolio, text search by name/source/address, venue and status filters. Status choices are enabled, disabled, refresh failed, not refreshed and manual. A result count and Reset filters remain visible. Unassigned is a filter over null membership, not a stored portfolio. If a selected portfolio disappears on reload, the view falls back to All accounts.

Portfolio tiles headline `balanceUsd` and `balanceCoverage`, with the perps/manual `totalValueUsd` as secondary detail. Explicit null balances stay unknown, never falling back to a perps value. Record counts include disabled accounts; balances exclude them. Successful management changes reload the common Overview response used by tiles, account filters and Plays capital context. Manager selections retain IDs, not stale account/portfolio objects, so reopening uses the current revision and assignment.

## October 13, 2026: portfolio membership

The owner requested easier assignment of existing accounts, creating a portfolio while adding accounts, and moving accounts between portfolios.

- `POST /api/accounts/portfolio` takes `{ "accountIds": ["…"], "portfolioId": "…" }`, with explicit null to unlink, and returns the selected updated account DTOs. Both properties are required. IDs must be a nonempty list of at most 200 distinct accounts. Invalid lists return 400; any unknown/foreign account or target portfolio returns 404 with no changes.
- `POST /api/portfolios` also accepts optional `accountIds`. Omitted/null means an empty portfolio; a supplied list has the same nonempty, distinct and 200-account bound. The portfolio and assignments commit together; a failure leaves neither a new portfolio nor partially moved accounts. The response includes current member counts and exact balances with enabled-account coverage.
- Both endpoints accept optional `expected: [{ "accountId": "…", "settingsRevision": 1 }]`. When supplied, it must contain exactly one positive integer revision for every selected account, with no duplicates or other IDs. Empty, incomplete or malformed expectations return 400. After taking all account row locks, the server compares every revision with the current locked row before creating a portfolio or changing any account. Any mismatch returns 409 with no membership or revision changes from that request, including accounts already at the requested target. A rejected create leaves no new portfolio. Foreign/missing accounts still return 404 before revision comparison. Missing/null `expected` preserves the unconditional API operation, "move these accounts here regardless of current settings"; it is not used by existing-account membership UI flows.
- Both operations acquire the existing owner management transaction lock, then account row locks in ID order. All selected rows are validated and locked before mutation. Disabled accounts can move. Only changed assignments bump `settingsRevision`; accounts already at the target are no-ops. A stale full-settings PUT still returns 409. No source, enabled state, imported history or Play record changes, and no migration is needed.
- New portfolio includes an optional account checklist showing venue, current portfolio or Unassigned, and disabled status. Long lists are searchable. Checking an account from another portfolio moves it; help text explains retention. Selected accounts keep their displayed revisions for the guarded create request, even if shell data changes. A conflict clears selection, reloads shell data and refreshes both dialog lists without closing or creating a portfolio. The owner reviews and reselects accounts before retrying. Portfolio tiles, including empty ones, offer Add accounts and focus the membership section of Manage portfolio.
- Manage portfolio saves additions through the bulk move endpoint and removals through the null target. Both requests carry expectations from the displayed account settings; pending checklist edits retain those revisions across shell reloads. Rename/icon and delete retain their existing routes. These are separate transactions, not one atomic dialog-wide write; a failure can follow a successful rename or move. The dialog reports that possibility, reloads shell data, and requires Reload accounts before another save. A conflict immediately refreshes the dialog's account/portfolio lists and resets membership to the current saved state, but saving remains blocked until Reload accounts. Never reread and then send an unconditional unlink: the revision check belongs inside the locked transaction.
- New account and Lighter discovery/import offer New portfolio… with a name. They create that portfolio first, then the account records; this sequence is deliberately not atomic. If later creation/import fails, the portfolio stays selected and retained, an explicit message explains retry, and the normal shell reload is requested. A confirmed new portfolio remains available while a reload is pending or fails, so retry does not create a duplicate. Lighter retains its existing rediscovery requirement after an unconfirmed account import.
- Accounts has row checkboxes, select-all for visible filtered rows, and a Move to / Move / Clear bar with No portfolio and New portfolio… destinations. Selection drops hidden/disappeared accounts rather than reselecting them when filters clear. It retains the selected row revisions and sends them with the bulk request, including after inline portfolio creation. A conflict explicitly reports that no accounts moved, clears the selection and reloads shell data; the owner reviews and reselects current rows before retrying. Each request is bounded to 200; successful moves clear selection, notify and reload the common Overview data. The existing per-account Manage action remains the quick alternative.
- Pending controls are disabled. Dialog portfolio options follow shell reloads; a disappearing destination is an error, never an implicit unassignment. Account-manager conflict reload still rereads both the account and portfolios. The shared reload updates Overview, tile counts/balances, filters and Plays capital context; owner financial data remains uncached.

## API additions

All routes remain owner-scoped. Unknown/foreign IDs return 404 without revealing another owner's data.

- `PATCH /api/portfolios/{id}`, `{ "name": "New name" }` (optionally with `"icon"`, see Portfolio icons), returns updated `PortfolioDto`.
- `DELETE /api/portfolios/{id}`, returns 204 after unlinking accounts.
- `PUT /api/accounts/{id}`, full settings body `{ "name": "New name", "portfolioId": null, "isEnabled": true, "expectedRevision": 1 }`, returns updated `AccountDto`.
- `DELETE /api/accounts/{id}`, returns 204 or 409 when Plays prevent deletion.
- `CreateAccountRequest.portfolioId` becomes nullable and optional.
- `AccountDto` adds `isEnabled: boolean`; the migration defaults existing accounts to enabled.
- Full account PUT requires all settings properties and `expectedRevision`, including explicit portfolio null. Omitted properties must not silently disable or unlink accounts. `AccountDto.settingsRevision` changes with settings, changed bulk assignments and portfolio-deletion unlinking, independently of sync time. A stale expected revision returns 409 rather than overwrite newer settings.

Existing account DTOs may be accepted temporarily by the frontend during the dev-server rollout with missing `isEnabled` treated as enabled. New API output always includes it.

Disabled account GET detail metadata remains available for management. Snapshot GET returns literal JSON null and fills GET returns an empty list while disabled. No hidden fills should leak through activity limit/count routes. Re-enabling makes retained imported facts visible again.

## Portfolio icons

October 10, 2026. The owner asked for custom portfolio icons.

- Each portfolio has an icon chosen from a fixed set of drawn icons (folder, briefcase, wallet, piggy-bank, landmark, coins, bitcoin, gem, trending-up, chart-line, chart-candlestick, chart-column, target, compass, rocket, zap, flame, timer, hourglass, shield, anchor, mountain, sprout, crown). The default is `folder`, which existing portfolios receive through the `PortfolioIcons` migration.
- Icons are keys, not uploaded images or markup. The server accepts only keys in `PortfolioIcons` (`Vessel.Domain/Workspace/Portfolio.cs`) and returns 400 without changes for anything else; the browser list lives in `frontend/src/features/workspace/portfolio-icons.ts` and must stay in step.
- `POST /api/portfolios` accepts an optional `icon`. `PATCH /api/portfolios/{id}` takes `{ "name": "…", "icon": "…" }`; an omitted or null icon keeps the stored one, so a rename-only client cannot reset it. `PortfolioDto.icon` is always present.
- The new-portfolio and manage-portfolio dialogs show the icons as a radio group; the portfolio tile shows the chosen icon. A key the browser does not know shows the folder on the tile and leaves the picker unchecked; the manage dialog sends an icon only after the owner picks one, so a rename never overwrites a stored key the browser does not know. The picker uses theme tokens only, so every theme restyles it without layout changes.

## October 14, 2026: venue logos and account rows

Venue logos follow the same fixed-set pattern as portfolio icons, but the key comes from the venue descriptor (`logo` in `/api/system`), never from the owner. The browser set is `frontend/src/components/venues/venue-logos.ts` (`hyperliquid`, `lighter`, `lighter-rh`, `risex`, `manual`) and must stay in step with the backend list. A missing, unknown or malformed key is dropped while reading `/api/system` and shows the generic wallet icon; it never rejects the contract. Logo files are bundled same-origin assets (small SVGs, inlined by Vite as `data:` images, which the static CSP `img-src 'self' blob: data:` allows); the browser never fetches venue sites. Logos sit on a neutral `--muted`/`--border` tile so every theme keeps contrast; brand colours stay inside the files.

`VenueBadge` (logo + descriptor name, or compact logo-only with the name as accessible name and tooltip) replaces raw venue text in account rows, account detail, portfolio account checklists, the Manage account source line, Lighter-style import, Overview summary rows, Settings capabilities, the Plays list and account picker (inline only; Plays sections do not move) and Review (names, not IDs). Account rows now show logo, name, venue, shortened source identity (full value as tooltip), a portfolio chip (dashed for Unassigned), balance/availability and a sync column with a toned status badge, last refresh and position count. The Accounts venue filter is a row of logo chips with per-venue counts (`aria-pressed` buttons in a group named "Filter by venue"); the create-account venue picker is a radio group of logo cards. Filters, bulk selection, Refresh all and account detail behaviour are unchanged.

## October 15, 2026: grouping accounts

Accounts has a Group by control (None, Venue, Address) applied after the existing filters and kept in shell memory with them; Reset filters leaves grouping alone. Venue groups follow venue-chip order. Address groups compare EVM addresses case-insensitively across venues; accounts without an address (manual records, and Lighter accounts, whose L1 owner address is not stored or returned today) form a final "No address recorded" group. Group headers show the exact sum of enabled members' `balanceUsd`, or a partial marker when a member has none. Each group is a labelled `<tbody>` with a tri-state "Select all {group} accounts" checkbox; bulk moves are unchanged.

## Concurrency and ownership

Management and sync must coordinate account row locks. A disable/delete cannot be undone by a late refresh, and unlink cannot race portfolio deletion into a dangling FK. Verify target portfolio ownership before changing the account. Portfolio deletion handles concurrent assignments through FK/locking semantics, not an unchecked in-memory cascade.

Write guards, owner-composite FKs and the multiple-active-play non-unique index remain. Use exact decimal strings and don't change venue/public-address metadata through a settings update.

## Tests and deployment

Verify names, nullable create, reparent/unlink, foreign IDs, portfolio deletion retaining data, disabled-history/position exclusion and restoration, refresh blocking, full-PUT missing fields, permanent delete and linked-Play protection, plus actual PostgreSQL migration and concurrent behavior.

The backend worker owns `backend/` and backend tests; the orchestrator owns frontend/root/docs and integration. The existing Hyperliquid adapter and approved Play/prototype layout need no changes. No new packages are required.

Roll out the new explicit migration and rebuilt API to the owner-requested LAN preview only after checks, preserving existing records and the preview token.

## Raw-facts export (October 21, 2026)

Phase 2, item 5b (see `phase2-risk-coverage-contract.md`).

`GET /api/accounts/{id}/facts?kind=executions|funding&from=yyyy-MM-dd&to=yyyy-MM-dd`
streams one account's retained facts as CSV, in time order. The days are UTC
and both ends are included; leaving them out exports everything retained.
- **Which accounts.** Enabled real accounts only, as Activity shows them. A
  disabled or paper account is refused with 409, and another owner's account
  reads as not found.
- **Requests.**
  - The route is actual-only, so a `tradingMode=paper` request gets 409
    before any read.
  - An end date with no following day (9999-12-31) is a 400.
  - A cancelled download stops its database reads.
- **Executions** are imported fills, then owner-entered manual executions.
  Each has:
  - its provenance;
  - native fill, order and contract IDs and the transaction hash;
  - the normalized side, the venue's own direction and the position effect;
  - exact price, quantity, fee and fee token, with the fee basis;
  - the venue's P&L and its basis;
  - the venue-reported leverage;
  - for a manual row, its Play's instrument, and its Play, role and source
    label.
- **Funding** is its own file. Each settlement keeps its native source ID,
  exact signed amount, asset, and RISEx's native nanoseconds. Settlements
  within one microsecond follow their native nanoseconds.
- **No notice row.** The files have none, so other tools can ingest them.
  Settings → Data export states that they are retained facts only, not a tax
  report.
- **Cells.** Numbers are plain decimal strings written exactly, and an
  unknown is empty. Text cells are neutralized against spreadsheet formulas.
  Nothing is computed: no lots, no valuation, no inferred fill.

## Keep execution history (October 22, 2026)

Phase 2, item 6a of the [risk and coverage contract](phase2-risk-coverage-contract.md), decision 11.
- **The opt-in.** Manage account shows **Keep execution history** for a real
  account with a source on a venue the monitor checks (sync and orders). It is
  off until the owner turns it on. `PUT /api/accounts/{id}` takes an optional
  `keepExecutionHistory`; left out, the stored value is kept, so older clients
  never clear it. Turning it on for a paper account is a 409, and for a manual
  record or another venue a 400; the database checks it too
  (`CK_accounts_keep_history`, migration `HistoryRetention`). Like every
  setting it takes a new settings revision.
- **What it does.** The monitor reads the account on its schedule even without
  an eligible Play, behind protection checks (see the
  [workers reference](messaging-and-workers.md#scheduler-leases-and-fencing)).
  Such a run imports and records coverage; it opens no discrepancy. A disabled
  account is not read.
- **History coverage.** Account detail shows the opt-in, the open gaps (an
  unknown end reads as such) and what each import covered. A gap closes only
  when a read proves its span covered. Deleting an account deletes its ledger
  with its other facts.
- **Backfill (6b).** On a venue that serves executions by time, each open gap
  offers **Backfill**, one at a time per account. Its progress shows under
  **Backfills** and the panel reads it again every 15 seconds while it reads. A
  span the venue no longer serves is marked beyond its reach and offers none.
  See the [workers reference](messaging-and-workers.md#coverage-signals).
