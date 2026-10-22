using Vessel.Application.Common;
using System.Text.Json;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;

namespace Vessel.Application.Monitoring;

public sealed class MonitorCoordinator(IMonitorStore store, IWorkspaceStore workspace, IMonitorImporter executions,
    IVenueRegistry venues, IAccountCredentialReader credentials, IUnitOfWork unitOfWork,
    IVesselEventSink events, IMonitorCommandSink commands, IJournalOwnerContext owner, MonitorOptions options,
    TimeProvider time, IMonitorEvaluator? evaluator = null, IMonitorRunLog? log = null, FundingOptions? funding = null)
{
    public async Task<MonitorSummaryDto> SummaryAsync(CancellationToken ct)
    {
        var heartbeat = await store.HeartbeatAsync(ct);
        var online = IsOnline(heartbeat?.HeartbeatAtUtc);
        // One bounded read of rows and venue deadlines; no per-row account capture.
        var budgets = await store.VenueRetriesAsync(ct);
        var rows = await store.SummaryAsync(EligibleVenues(), time.GetUtcNow(), ct);
        // One bounded coverage read, and one count of Open Plays still without coverage, for every listed account.
        var ids = rows.Select(x => x.Row.AccountId).ToArray();
        var coverage = (await store.CoverageAsync(ids, ct)).ToLookup(x => x.AccountId);
        var uncheckedPlays = await store.UncheckedPlaysAsync(ids, ct);
        var accounts = new List<AccountMonitorDto>();
        foreach (var (row, venueId) in rows)
            accounts.Add(await ToDto(row, budgets.TryGetValue(venueId, out var deadline) ? deadline : null, online,
                coverage[row.AccountId], uncheckedPlays.GetValueOrDefault(row.AccountId), ct));
        return new(online, heartbeat?.HeartbeatAtUtc, accounts);
    }

    public async Task<AccountMonitorDto> AccountAsync(Guid id, CancellationToken ct)
    {
        var capture = await store.CaptureAsync(id, ct) ?? throw new ProblemException(404, "Account not found.");
        if (capture.Account.TradingMode != "real") throw new ProblemException(409, "Paper accounts use the paper scheduler, not the actual-account monitor.");
        var row = await store.FindAsync(id, ct) ?? new() { AccountId = id, OwnerId = owner.OwnerId };
        return await ToDto(row, await store.VenueRetryAtAsync(capture.Account.VenueId, ct),
            IsOnline((await store.HeartbeatAsync(ct))?.HeartbeatAtUtc), await store.CoverageAsync([id], ct),
            (await store.UncheckedPlaysAsync([id], ct)).GetValueOrDefault(id), ct);
    }

    public Task<IReadOnlyList<MonitorWatchDto>> WatchesAsync(Guid playId, CancellationToken ct) =>
        store.WatchesAsync(playId, time.GetUtcNow(), ct);

    /// <summary>
    /// Starts or stops scheduling as accounts change: eligibility, disable and credential changes. Inputs are read
    /// set-based; only rows whose scheduling or state changes are locked, revalidated and written.
    /// </summary>
    public async Task<int> DiscoverAsync(IReadOnlyCollection<string> venueIds, DateTimeOffset now, CancellationToken ct,
        int batch = 500)
    {
        var changed = 0;
        Guid? after = null;
        while (true)
        {
            // Keyset pages: every account is reached each pass, however many the owner has.
            var page = await store.DiscoveryCandidatesAsync(venueIds, now, after, batch, ct);
            changed += await DiscoverPageAsync(page, venueIds, now, ct);
            if (page.Count < batch) return changed;
            after = page[^1].AccountId;
        }
    }

    private async Task<int> DiscoverPageAsync(IReadOnlyList<MonitorCandidate> candidates, IReadOnlyCollection<string> venueIds,
        DateTimeOffset now, CancellationToken ct)
    {
        var changed = 0;
        foreach (var candidate in candidates)
        {
            if (!NeedsDiscovery(candidate)) continue;
            if (await unitOfWork.ExecuteAsync(() => store.LockedAsync(candidate.AccountId, async (account, row) =>
                {
                    var old = row.State;
                    var monitorable = account.TradingMode == "real" && account.IsEnabled && account.SourceId is not null && venueIds.Contains(account.VenueId);
                    if (row.ManualState != "idle")
                    {
                        // A live run settles itself (it sees the same changes); a monitorable account's pending work
                        // is claimed normally. Work no scheduler will ever claim, because the account was disabled or
                        // lost its source, is settled here instead of staying pending forever: an unclaimed request,
                        // or a running lease whose worker died. A still-valid lease keeps its fence.
                        if (monitorable || row.ManualState == "running" && row.LeaseUntilUtc > now) return false;
                        // Settled like any superseded run, so the manual cooldown starts; the unmonitorable account
                        // is then left idle with no scheduled run.
                        row.Supersede(now, options.ManualCooldown, MonitorOptions.FirstRetry);
                        row.Idle();
                        await StateEventAsync(account.Id, old, row, row.RequestId ?? Guid.CreateVersion7(), "monitor", now, ct);
                        // The settled owner Check completes like any other: browsers waiting on it go idle.
                        if (row.RequestId is { } request)
                            await RunCompletedAsync(account.Id, request, row.Trigger, row, "superseded", now, ct);
                        return true;
                    }
                    if (monitorable) await store.UpdateWatchesAsync(account.Id, null, now, ct);
                    var current = await store.CaptureAsync(account.Id, ct);
                    if (row.CredentialRevision != current?.CredentialRevision)
                    {
                        // Saving or removing a token reschedules the account.
                        row.CredentialRevision = current?.CredentialRevision;
                        if (row.State == "credential-error") { row.Idle(); row.NextRunAtUtc = now; }
                    }
                    var eligible = monitorable && await store.EligibleAsync(account.Id, now, ct);
                    if (eligible && row.State != "credential-error") row.NextRunAtUtc ??= now;
                    else if (!eligible) row.Idle();
                    await StateEventAsync(account.Id, old, row, Guid.CreateVersion7(), "monitor", now, ct);
                    return true;
                }, ct), ct))
                changed++;
        }
        return changed;
    }

    private static bool NeedsDiscovery(MonitorCandidate candidate)
    {
        var eligible = candidate.Monitorable && candidate.Eligible;
        if (candidate.Row is not { } row) return eligible || candidate.Monitorable && candidate.HasExpiredWatches;
        // Pending or abandoned work on an account that can no longer be monitored is settled by discovery.
        if (row.ManualState != "idle") return !candidate.Monitorable;
        return row.CredentialRevision != candidate.CredentialRevision || candidate.Monitorable && candidate.HasExpiredWatches ||
            (eligible ? row.State != "credential-error" && row.NextRunAtUtc is null : row.State != "idle" || row.NextRunAtUtc is not null);
    }

    public const string MonitorOffline = "The monitor is offline; try again later.";

    /// <summary>
    /// How long due, pending check work may wait for a worker before it stops gating manual admission: a full lease plus
    /// the heartbeat timeout, so a live worker always had the chance to claim it.
    /// </summary>
    private TimeSpan UnclaimedAfter => MonitorOptions.Lease + options.HeartbeatTimeout;

    /// <returns>The released request and its trigger, when unclaimed check work stopped gating admission.</returns>
    private (Guid RequestId, string Trigger)? ReleaseUnclaimed(MonitorAccount row, DateTimeOffset now) =>
        row.RequestId is { } request && row.Trigger is var trigger && row.ReleaseUnclaimedWork(now, UnclaimedAfter)
            ? (request, trigger) : null;

    /// <summary>
    /// The released owner Check will not run under its own request (a returning worker runs the still-due check as a
    /// scheduled run), so it completes as <c>superseded</c>, like any owner Check settled without its own run. Only
    /// called once admission has succeeded, just before <see cref="MonitorAccount.Admit"/> replaces the request, in the
    /// same transaction: a refused admission rolls back and records nothing.
    /// </summary>
    private Task CompleteExpiredAsync(Guid accountId, (Guid RequestId, string Trigger)? expired, MonitorAccount row,
        DateTimeOffset now, CancellationToken ct) => expired is { } released
        ? RunCompletedAsync(accountId, released.RequestId, released.Trigger, row, "superseded", now, ct)
        : Task.CompletedTask;

    public async Task<MonitorRunAccepted> RequestAsync(Guid accountId, CancellationToken ct)
    {
        var request = Guid.CreateVersion7();
        await unitOfWork.ExecuteAsync(() => store.LockedAsync(accountId, async (account, row) =>
        {
            var now = time.GetUtcNow();
            if (account.TradingMode != "real" || !account.IsEnabled || account.SourceId is null || !EligibleVenue(account.VenueId) ||
                !await store.EligibleAsync(accountId, now, ct))
                throw new ProblemException(409, "This account has no eligible venue Plays, pending linked orders, discrepancy work or kept history.");
            row.ReleaseAbandonedRefresh(now);
            var expired = ReleaseUnclaimed(row, now);
            var retry = await RetryAfterAsync(account.VenueId, row, now, ct);
            if (retry > 0) throw new MonitorAdmissionException(retry);
            // Only the worker runs a Check. With its heartbeat stale (the "worker offline" signal) nothing is recorded:
            // accepted work would sit pending and block this account's admission until a worker returns.
            if (!IsOnline((await store.HeartbeatAsync(ct))?.HeartbeatAtUtc))
                throw new ProblemException(503, MonitorOffline);
            await CompleteExpiredAsync(accountId, expired, row, now, ct);
            row.Admit(request, "manual", now);
            await commands.EnqueueAsync(new(request, accountId, owner.OwnerId, "manual"), ct);
            return true;
        }, ct), ct);
        return new(request, await AccountAsync(accountId, ct));
    }

    /// <summary>Refresh shares the account gate, owner and venue budgets, but can refresh accounts without Plays.</summary>
    public Task<MonitorLease> AdmitRefreshAsync(Guid accountId, string readerId, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(() => store.LockedAsync(accountId, async (account, row) =>
        {
            if (account.TradingMode != "real") throw new ProblemException(409, "Paper accounts cannot import venue history.");
            if (!account.IsEnabled) throw new ProblemException(400, "Enable the account before refreshing it.");
            if (account.SourceId is null || venues.Descriptor(account.VenueId)?.Capabilities.Sync != true)
                throw new ProblemException(400, "This account cannot be refreshed.");
            if (venues.Reader(account.VenueId) is null) throw new ProblemException(503, "Venue reader is unavailable.");
            var now = time.GetUtcNow();
            row.ReleaseAbandonedRefresh(now);
            // Check work nobody claimed (worker down or not deployed) must not refuse Refresh indefinitely.
            var expired = ReleaseUnclaimed(row, now);
            var retry = await RetryAfterAsync(account.VenueId, row, now, ct);
            if (retry > 0) throw new MonitorAdmissionException(retry);
            if (!await store.ReadCapacityAsync(account.VenueId, now, ct)) throw new MonitorAdmissionException(30);
            await CompleteExpiredAsync(accountId, expired, row, now, ct);
            var request = Guid.CreateVersion7();
            row.Admit(request, "refresh", now);
            row.Claim(request, readerId, now, MonitorOptions.Lease);
            return new MonitorLease(owner.OwnerId, accountId, request, row.Generation, readerId, "refresh");
        }, ct), ct);

    public async Task<bool> RunAsync(MonitorLease lease, CancellationToken ct)
    {
        if (lease.OwnerId != owner.OwnerId) throw new InvalidOperationException("A monitor lease belongs to another owner.");
        var capture = await store.CaptureAsync(lease.AccountId, ct);
        if (capture is null) return false;
        var before = await store.FindAsync(lease.AccountId, ct);
        if (before is null || !before.Owns(lease.Generation, lease.WorkerId, time.GetUtcNow())) return false;
        if (lease.Trigger == "backfill") return await RunBackfillAsync(lease, capture, ct);
        var refresh = lease.Trigger == "refresh";
        var snapshotApplied = false;
        try
        {
            if (!refresh && !await store.EligibleAsync(lease.AccountId, time.GetUtcNow(), ct))
                return await SupersedeAsync(lease, ct, attempted: false);
            // Phase 2, item 6: whether the run has protection work, as it stood when the run began. A watch that ends
            // during this run still gets the comparison it was admitted for.
            var protection = refresh || await store.ProtectionEligibleAsync(lease.AccountId, time.GetUtcNow(), ct);
            // Commit the watched-history baseline before any adapter call.
            await unitOfWork.ExecuteAsync(async () =>
            {
                await events.EnsureMonitoringBaselineAsync(lease.AccountId, ct);
                return true;
            }, ct);
            // A different account may have learned a venue-wide deadline after this lease was claimed.
            // Keep accepted check work pending rather than consuming it without a venue observation.
            var deferred = await DeferThrottledLeaseAsync(lease, ct);
            if (deferred != 0)
            {
                if (deferred > 0 && refresh) throw new MonitorAdmissionException(deferred);
                return false;
            }
            if (capture.Account.TradingMode != "real" || !capture.Account.IsEnabled || capture.Account.SourceId is null) return await SupersedeAsync(lease, ct, attempted: false);
            if (!refresh && venues.Descriptor(capture.Account.VenueId)?.Capabilities.ReadOnlyCredential == true &&
                !await credentials.IsUsableAsync(capture.Account, ct))
                return await FailAsync(lease, capture, true, "A usable read-only credential is required. Update it to resume monitoring.", ct);
            var reader = venues.Reader(capture.Account.VenueId) ?? throw new VenueReadException("Reader unavailable.");
            var sync = await reader.ReadAsync(capture.Account, ct);
            if (!sync.Fills.All(VenueFactChecks.Valid))
                throw new VenueReadException("Invalid execution facts.");
            if (refresh)
            {
                if (venues.Funding(capture.Account.VenueId) is { } fundingReader)
                {
                    // Read outside the unit of work; apply facts only after the lease/settings fence. Phase 2, item 9a: in
                    // bounded windows that extend the stored coverage (FundingWindows).
                    var to = DateTimeOffset.FromUnixTimeMilliseconds(sync.Snapshot.ObservedAtUtc.ToUnixTimeMilliseconds());
                    var settings = funding ?? new FundingOptions();
                    var from = to - settings.Lookback;
                    VenueFundingReadResult fundingRead;
                    FundingCursor? cursor = null;
                    try
                    {
                        cursor = await store.FundingCursorAsync(capture.Account.Id, ct);
                        var read = await FundingWindows.ReadAsync(fundingReader, capture.Account, to, cursor, settings, async token =>
                        {
                            var retryAt = await store.VenueRetryAtAsync(capture.Account.VenueId, token);
                            if (retryAt > time.GetUtcNow()) throw new VenueRateLimitException(retryAt.Value);
                        }, time, ct);
                        fundingRead = read.Result;
                        VenueFundingChecks.Validate(fundingRead, fundingRead.FromUtc, fundingRead.ToUtc);
                        // A rate limit after some windows completed keeps them; the venue still waits.
                        if (read.RetryAtUtc is { } retry)
                            await store.DeferVenueAsync(capture.Account.VenueId, retry, time.GetUtcNow(), CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is VenueReadException or HttpRequestException ||
                        ex is OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        if (ex is VenueRateLimitException limited)
                            await store.DeferVenueAsync(capture.Account.VenueId, limited.RetryAtUtc, time.GetUtcNow(), CancellationToken.None);
                        // A failed read keeps the last window, so the next Refresh starts from half of it, not the lookback.
                        fundingRead = new([], from, to, false,
                            "Funding history could not be refreshed. Retained payments are unchanged; missing funding is not zero.")
                        { WindowFromUtc = cursor?.LastAttemptFromUtc, WindowToUtc = cursor?.LastAttemptToUtc };
                    }
                    sync = sync with { Funding = fundingRead };
                }
                // Preserve the public Refresh commit even if the later optional order read is cancelled.
                // This is a short fenced transaction; the lease, not a DB lock, spans the order read.
                if (!await ApplyAsync(lease, capture, new(sync, null), ct, snapshotOnly: true)) return false;
                snapshotApplied = true;
            }
            VenueOrderReadResult? orders = null;
            string? orderError = null;
            var credentialError = false;
            if (venues.Descriptor(capture.Account.VenueId)?.Capabilities.Orders == true)
            {
                try
                {
                    var retryAt = await store.VenueRetryAtAsync(capture.Account.VenueId, ct);
                    if (retryAt > time.GetUtcNow()) throw new VenueRateLimitException(retryAt.Value);
                    if (venues.Descriptor(capture.Account.VenueId)?.Capabilities.ReadOnlyCredential == true &&
                        !await credentials.IsUsableAsync(capture.Account, ct))
                        throw new VenueCredentialRejectedException();
                    orders = await (venues.Orders(capture.Account.VenueId) ?? throw new VenueReadException("Order reader unavailable."))
                        .ReadOrdersAsync(capture.Account, ct);
                    if (!orders.Orders.All(VenueFactChecks.Valid)) throw new VenueReadException("Invalid order facts.");
                }
                catch (Exception ex) when (refresh && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
                {
                    // Refresh imports public facts even when private/order coverage is unavailable.
                    // Never expose exception text, which can contain credentials or request URLs.
                    log?.Failed(lease, ex.GetType().Name);
                    orders = null;
                    if (ex is VenueRateLimitException limited)
                        await store.DeferVenueAsync(capture.Account.VenueId, limited.RetryAtUtc, time.GetUtcNow(), CancellationToken.None);
                    credentialError = ex is VenueCredentialRejectedException or CredentialStorageException;
                    orderError = credentialError
                        ? "Public account data refreshed; order coverage is unavailable. Update the read-only credential."
                        : ex is VenueRateLimitException
                        ? "Public account data refreshed; the venue rate limit deferred the order check."
                        : "Public account data refreshed; order coverage is unavailable. Retry the account check later.";
                }
            }
            ct.ThrowIfCancellationRequested();
            return await ApplyAsync(lease, capture, new(sync, orders, orderError, credentialError), ct, snapshotApplied: snapshotApplied,
                protection: protection);
        }
        catch (Exception ex) when (ex is VenueCredentialRejectedException or CredentialStorageException)
        {
            log?.Failed(lease, ex.GetType().Name);
            return await FailAsync(lease, capture, true, "The read-only credential is unavailable or was refused. Update it to resume monitoring.", ct, snapshotApplied);
        }
        catch (VenueRateLimitException limited)
        {
            log?.Failed(lease, limited.GetType().Name);
            // A throttle is valid even when this account's lease or settings were superseded.
            await store.DeferVenueAsync(capture.Account.VenueId, limited.RetryAtUtc, time.GetUtcNow(), CancellationToken.None);
            return await FailAsync(lease, capture, false, "The venue rate limit deferred the next account check.", ct, snapshotApplied);
        }
        catch (Exception ex) when (ex is not MonitorAdmissionException &&
            (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            // Do not log exception payloads: HTTP errors may contain credential-bearing request information.
            log?.Failed(lease, ex.GetType().Name);
            var credential = !refresh && venues.Descriptor(capture.Account.VenueId)?.Capabilities.ReadOnlyCredential == true &&
                !await credentials.IsUsableAsync(capture.Account, ct);
            return await FailAsync(lease, capture, credential, credential ?
                "The read-only credential is unavailable or was refused. Update it to resume monitoring." :
                "The venue check failed. Monitoring will retry.", ct, snapshotApplied);
        }
        catch (OperationCanceledException)
        {
            // An interrupted run (request abort, shutdown or a lost lease) is not a failure; the next run repeats the
            // work. A refresh still releases its lease with the cooldown, but records no venue sync failure.
            if (refresh) await FailAsync(lease, capture, false, "", CancellationToken.None, snapshotApplied, interrupted: true);
            else await SupersedeAsync(lease, CancellationToken.None);
            throw;
        }
    }

    /// <param name="protection">Whether the run had protection work when it began; without it, and without any now, a run
    /// only for kept history compares nothing.</param>
    private Task<bool> ApplyAsync(MonitorLease lease, MonitorCapture captured, MonitorRead read, CancellationToken ct,
        bool snapshotOnly = false, bool snapshotApplied = false, bool protection = true) =>
        unitOfWork.ExecuteAsync(() => store.LockedAsync(lease.AccountId, async (account, row) =>
        {
            var refresh = lease.Trigger == "refresh";
            await events.SetRunAsync(lease.RequestId, refresh ? "owner" : "monitor", ct);
            var now = time.GetUtcNow();
            if (!row.Owns(lease.Generation, lease.WorkerId, now)) return false;
            var current = await store.CaptureAsync(account.Id, ct);
            // Refresh imports immutable facts even when a ledger timestamp is unchanged. The store
            // leaves equal-time ledger values intact. Checks still require a new observation before
            // reconciliation/confirmation, and a genuinely newer retained observation fences both.
            if (!account.IsEnabled || account.SettingsRevision != captured.Account.SettingsRevision ||
                account.SourceId != captured.Account.SourceId || account.VenueId != captured.Account.VenueId ||
                current?.CredentialRevision != captured.CredentialRevision ||
                (refresh ? row.LastObservationAtUtc > read.Sync.Snapshot.ObservedAtUtc :
                    row.LastObservationAtUtc >= read.Sync.Snapshot.ObservedAtUtc) ||
                read.Orders is { } orderRead && (refresh ? row.LastOrdersObservationAtUtc > orderRead.ObservedAtUtc :
                    row.LastOrdersObservationAtUtc >= orderRead.ObservedAtUtc) ||
                (refresh ? account.LastSyncedAtUtc > read.Sync.Snapshot.ObservedAtUtc :
                    account.LastSyncedAtUtc >= read.Sync.Snapshot.ObservedAtUtc))
            {
                // Changed settings or a newer observation superseded this run: discarded, not failed.
                var discardedState = row.State;
                Supersede(account, row, refresh, now);
                await ApplyVenueBudgetAsync(account.VenueId, row, ct);
                // The discarded observation is neither reconciled nor used for confirmation, but the run still settles.
                await SettleDiscrepanciesAsync(account, lease, ct);
                if (!refresh && account.IsEnabled && !await store.EligibleAsync(account.Id, now, ct)) row.Idle();
                await StateEventAsync(account.Id, discardedState, row, lease.RequestId, "monitor", now, ct);
                await RunCompletedAsync(lease, row, "superseded", now, ct);
                return false;
            }
            // Fill continuity is judged against the newest fill known before this import. `current` was read under this
            // lock and before the import below. A Refresh imports in its snapshot-only unit and records its continuity
            // there; its second unit (snapshotApplied) already sees its own fills and never uses this boundary.
            var boundary = ContinuityBoundary(row.NewestFillAtUtc, current?.NewestRetainedFillAtUtc);
            if (!snapshotApplied) await workspace.SaveRefreshAsync(account, read.Sync, ct);
            if (snapshotOnly)
            {
                row.LastObservationAtUtc = read.Sync.Snapshot.ObservedAtUtc;
                // Every import records its own continuity, in the unit that retains its fills. A Refresh can set a gap and
                // advances the watermark, but never clears a gap; with unknown truncation it records nothing.
                if (read.Sync.FillHistoryTruncated is not null)
                {
                    if (!read.Sync.FillHistoryComplete && ObservedGap(read.Sync, boundary)) row.HasHistoryGap = true;
                    AdvanceWatermark(row, read.Sync, boundary);
                }
                await RecordCoverageAsync(account, FillCoverageRecord.Refresh, read.Sync, boundary, ct);
                return true;
            }
            if (refresh)
            {
                // An owner refresh retains facts and fences older results, nothing more: no lifecycle reconciliation, and
                // no change to check backoff, state, last success or due time. Its fill continuity (gap evidence and
                // watermark) was recorded with its import, in the snapshot-only unit.
                if (read.Orders is { } refreshed)
                {
                    await executions.RetainAsync(account, refreshed, ct);
                    account.RecordOrderHistoryNotice(refreshed.Notice);
                    await workspace.SaveAsync(ct);
                }
                if (read.OrderError is not null)
                {
                    account.RecordOrderHistoryNotice(read.OrderError);
                    await workspace.SaveAsync(ct);
                }
                // Refresh performs no lifecycle reconciliation, but still settles discrepancy housekeeping.
                await SettleDiscrepanciesAsync(account, lease, ct);
                row.LastObservationAtUtc = read.Sync.Snapshot.ObservedAtUtc;
                row.LastOrdersObservationAtUtc = read.Orders?.ObservedAtUtc ?? row.LastOrdersObservationAtUtc;
                row.CompleteRefresh(now, options.ManualCooldown);
                await ApplyVenueBudgetAsync(account.VenueId, row, ct);
                return true;
            }
            var oldState = row.State;
            var previousSuccess = row.LastSuccessAtUtc;
            var previousFill = boundary;
            // Only a check clears a gap, and only when the adapter reports complete history.
            var gap = !read.Sync.FillHistoryComplete && (row.HasHistoryGap || ObservedGap(read.Sync, previousFill));
            row.HasHistoryGap = gap;
            await RecordCoverageAsync(account, FillCoverageRecord.Check, read.Sync, previousFill, ct);
            if (read.Orders is { } orders)
            {
                await executions.RetainAsync(account, orders, ct);
                await executions.ReconcileAsync(account, orders, read.Sync.Instruments, ct);
                account.RecordOrderHistoryNotice(orders.Notice);
                await workspace.SaveAsync(ct);
            }
            // With or without an order read: a watch past its lifetime ends unverifiable either way.
            await store.UpdateWatchesAsync(account.Id, read.Orders, now, ct);
            // Eligibility queries must see the updated watches within this same transaction.
            await unitOfWork.SaveChangesAsync(ct);
            if (read.OrderError is not null)
            {
                account.RecordOrderHistoryNotice(read.OrderError);
                await workspace.SaveAsync(ct);
            }
            // A venue whose order reader does not return the current book is labelled, not degraded: its book-dependent
            // checks do not run, and its history-only order read is not missing evidence.
            var currentBook = venues.Descriptor(account.VenueId)?.Capabilities.CurrentOrderBook == true;
            // Phase 2, item 6: an account read only because it keeps its history has no protection work, so its run
            // imports and records coverage without comparing positions or opening discrepancies.
            var compare = protection || await store.ProtectionEligibleAsync(account.Id, now, ct);
            var evaluation = evaluator is null || !compare ? new MonitorEvaluationResult(null) : await evaluator.EvaluateAsync(
                new(lease.RequestId, account, read, previousSuccess, previousFill, gap, lease.Trigger, currentBook), ct);
            if (evaluation.Coverage is { } coverage)
                await store.SaveCoverageAsync(account.Id, coverage.Select(x => new MonitorCoverage
                {
                    PlayId = x.PlayId, OwnerId = owner.OwnerId, AccountId = account.Id, ContractId = x.ContractId, State = x.State,
                    Covered = x.State == MonitorCoverageStates.Known ? x.Covered : null, Reason = x.Reason, CheckedAtUtc = now,
                    Stops = JsonSerializer.Serialize(x.Stops, CoverageJson)
                }).ToArray(), ct);
            row.LastObservationAtUtc = read.Sync.Snapshot.ObservedAtUtc;
            row.LastOrdersObservationAtUtc = read.Orders?.ObservedAtUtc ?? row.LastOrdersObservationAtUtc;
            AdvanceWatermark(row, read.Sync, previousFill);
            row.CredentialRevision = captured.CredentialRevision;
            var omitted = read.Sync.OmittedExecutions > 0;
            var degraded = read.OrderError is not null || evaluation.Incomplete || gap || omitted || !read.Sync.PositionsComplete ||
                !read.Sync.FillHistoryComplete && read.Sync.FillHistoryTruncated is null ||
                currentBook && read.Orders is { OpenOrdersComplete: false };
            row.Complete(now, options.ManualCooldown, options.NextDelay(0, Random.Shared.NextDouble()), true, degraded,
                credentialError: read.CredentialError,
                error: read.OrderError ?? (gap ? "The venue fill window may have a gap. Missing executions cannot be ruled out." :
                    omitted ? "The venue reported executions Vessel could not verify, so they were left out. Missing executions cannot be ruled out." :
                    evaluation.Incomplete ? evaluation.SafeNotice ?? "The account comparison has unknown coverage." :
                    degraded ? "The venue observation has incomplete position, fill-window or open-order coverage." : null));
            row.ConfirmationAtUtc = evaluation.NextConfirmationAtUtc;
            if (!read.CredentialError && evaluation.NextConfirmationAtUtc is { } deadline)
            {
                // The manual cooldown never delays a confirmation run; only the venue budget can.
                var earliest = now.AddSeconds(60);
                if (deadline < earliest) deadline = earliest;
                if (deadline < row.NextRunAtUtc) row.NextRunAtUtc = deadline;
            }
            await ApplyVenueBudgetAsync(account.VenueId, row, ct);
            if (!await store.EligibleAsync(account.Id, now, ct)) row.Idle();
            await StateEventAsync(account.Id, oldState, row, lease.RequestId, "monitor", now, ct);
            await RunCompletedAsync(lease, row, "succeeded", now, ct);
            if (captured.CredentialExpiresAtUtc is { } expires && expires > now && expires <= now.AddDays(14))
                await events.AppendAsync(new("credential.expiring", lease.RequestId,
                    $"credential:{account.Id}:{captured.CredentialRevision}:expiring", "warning", now, now,
                    new(AccountId: account.Id, VenueId: account.VenueId), new("monitor"),
                    JsonSerializer.SerializeToElement(new { expiresAtUtc = expires })), ct);
            return true;
        }, ct), ct);

    /// <summary>
    /// Pages one backfill run reads: the venue's whole reach at its page size, so an unchanging history is proven in one run
    /// while the run stays short and paced behind protection checks.
    /// </summary>
    private const int BackfillPagesPerRun = 5;

    /// <summary>
    /// Phase 2, item 6b: the owner starts a backfill of one open gap the venue may still serve. The scheduler reads it in
    /// runs of its own, behind protection checks; nothing is read here.
    /// </summary>
    public Task<HistoryBackfillDto> StartBackfillAsync(Guid accountId, Guid gapId, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(() => store.LockedAsync(accountId, async (account, _) =>
        {
            if (account.TradingMode != "real") throw new ProblemException(409, "Paper accounts have no venue history.");
            if (!account.IsEnabled) throw new ProblemException(400, "Enable the account before backfilling it.");
            if (account.SourceId is null || !EligibleVenue(account.VenueId) || venues.FillHistory(account.VenueId) is null)
                throw new ProblemException(400, "This venue cannot backfill executions.");
            var gap = await store.GapAsync(accountId, gapId, ct) ?? throw new ProblemException(404, "Gap not found.");
            if (!gap.IsOpen) throw new ProblemException(409, "This gap is already closed.");
            if (gap.BeyondVenueReach) throw new ProblemException(409, "The venue no longer serves this span.");
            if (await store.ActiveBackfillAsync(accountId, ct) is not null)
                throw new ProblemException(409, "A backfill is already running for this account.");
            var backfill = HistoryBackfill.For(gap, time.GetUtcNow());
            await store.AddAsync(backfill, ct);
            return HistoryBackfillDto.Of(backfill);
        }, ct), ct);

    /// <summary>
    /// A backfill run: up to <see cref="BackfillPagesPerRun"/> pages, each read outside any transaction and applied in its
    /// own unit fenced by this lease, then the lease is released. It never touches the check schedule, state or cooldown,
    /// its failures are its own, and an unfinished backfill waits for a later run.
    /// </summary>
    private async Task<bool> RunBackfillAsync(MonitorLease lease, MonitorCapture capture, CancellationToken ct)
    {
        try
        {
            if (venues.FillHistory(capture.Account.VenueId) is not { } reader || capture.Account.TradingMode != "real" ||
                !capture.Account.IsEnabled || capture.Account.SourceId is null) return false;
            for (var page = 0; page < BackfillPagesPerRun; page++)
            {
                ct.ThrowIfCancellationRequested();
                if (await store.ActiveBackfillAsync(lease.AccountId, ct) is not { } active) return true;
                if (await store.VenueRetryAtAsync(capture.Account.VenueId, ct) > time.GetUtcNow()) return true;
                var from = active.CursorUtc;
                IReadOnlyCollection<string> expected = active.Witnesses();
                if (active.Pages == 0 && active.StartKnown)
                {
                    // The first page starts at the newest execution Vessel retained at or before the gap's start, and must
                    // return it again: that proves the venue still serves everything after it.
                    var anchor = await store.FillWitnessAsync(lease.AccountId, active.FromUtc, ct);
                    (from, expected) = anchor is { } known ? (HistoryBackfill.Floor(known.At), known.Ids) : (active.FromUtc, []);
                }
                // Every page reads to now, so a gap that widened while the backfill ran is still covered.
                var to = HistoryBackfill.Floor(time.GetUtcNow());
                VenueFillPage read;
                try
                {
                    read = await reader.ReadFillPageAsync(capture.Account, from, to, ct);
                    if (!read.Fills.All(VenueFactChecks.Valid)) throw new VenueReadException("Invalid execution facts.");
                }
                catch (VenueRateLimitException limited)
                {
                    // The venue's deadline holds for every account; the backfill waits for a later run.
                    log?.Failed(lease, limited.GetType().Name);
                    await store.DeferVenueAsync(capture.Account.VenueId, limited.RetryAtUtc, time.GetUtcNow(), CancellationToken.None);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Never record exception text, which can carry request details.
                    log?.Failed(lease, ex.GetType().Name);
                    await BackfillUnitAsync(lease, capture, active.Id, (_, _, backfill, now) =>
                    {
                        backfill.Failure("The venue read failed. The backfill will retry.", now);
                        return Task.FromResult(false);
                    }, ct);
                    return true;
                }
                if (!await BackfillUnitAsync(lease, capture, active.Id, async (account, row, backfill, now) =>
                {
                    // Only the page this run read on top of the backfill it saw.
                    if (backfill.Pages != active.Pages) return false;
                    var added = await workspace.SaveHistoricalFillsAsync(account, read.Fills, now, ct);
                    await events.ClaimHistoricalFillsAsync(added, ct);
                    if (backfill.Read(read.Raw.Select(r => (r.Id, r.At)).ToList(), from, added.Count, read.OmittedExecutions, expected,
                            to, reader.PageLimit, reader.Reach, now))
                        await ConcludeBackfillAsync(account, row, backfill, reader.Reach, now, ct);
                    return backfill.IsActive;
                }, ct)) return true;
            }
            return true;
        }
        finally
        {
            // Released even when the run is cancelled or failed; a lost lease is left to recovery.
            await unitOfWork.ExecuteAsync(async () =>
            {
                await store.ReleaseBackfillAsync(lease, time.GetUtcNow(), CancellationToken.None);
                return true;
            }, CancellationToken.None);
        }
    }

    /// <summary>One backfill step under the run's own fence: its lease, and unchanged settings and source.</summary>
    private Task<bool> BackfillUnitAsync(MonitorLease lease, MonitorCapture captured, Guid backfillId,
        Func<Account, MonitorAccount, HistoryBackfill, DateTimeOffset, Task<bool>> step, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(() => store.LockedAsync(lease.AccountId, async (account, row) =>
        {
            // The owner started the backfill: like a Refresh, nothing it changes is announced as monitor news.
            await events.SetRunAsync(lease.RequestId, "owner", ct);
            var now = time.GetUtcNow();
            if (!row.Owns(lease.Generation, lease.WorkerId, now) || !account.IsEnabled || account.TradingMode != "real" ||
                account.SettingsRevision != captured.Account.SettingsRevision || account.SourceId != captured.Account.SourceId) return false;
            if (await store.BackfillAsync(account.Id, backfillId, ct) is not { IsActive: true } backfill) return false;
            var next = await step(account, row, backfill, now);
            await unitOfWork.SaveChangesAsync(ct);
            return next;
        }, ct), ct);

    /// <summary>
    /// Phase 2, item 6b: what a finished backfill window proves, against the gaps as they stand now. Omitted executions
    /// keep the gap open, since facts are known to be missing. A proven span closes the part of each gap it covers: its
    /// own gap, or every open gap when the whole history is proven. What is left stays open as its own gap: before the
    /// span, beyond the venue's reach when the venue no longer serves it; after the span (a gap that widened, or a read
    /// whose continuity broke), open to another backfill. The account's gap flag clears only when no gap is left open.
    /// </summary>
    private async Task ConcludeBackfillAsync(Account account, MonitorAccount row, HistoryBackfill backfill, int reach, DateTimeOffset now,
        CancellationToken ct)
    {
        if (backfill.OmittedExecutions > 0)
        {
            backfill.Stop("The venue reported executions Vessel could not verify, so the gap stays open.", now);
            return;
        }
        var proof = backfill.Conclude(reach);
        var record = FillCoverageRecord.ForBackfill(owner.OwnerId, account.Id, now, backfill.FromUtc, backfill.ToUtc, proof.CoveredFrom,
            proof.CoveredTo, proof.Complete);
        await store.RecordCoverageAsync(record, ct);
        var gaps = proof.Complete ? await store.OpenGapsAsync(account.Id, ct)
            : await store.GapAsync(account.Id, backfill.GapId, ct) is { IsOpen: true } open ? [open] : [];
        // Whether anything of the backfill's own gap is left open.
        var leftover = !gaps.Any(g => g.Id == backfill.GapId) && !proof.Complete;
        DateTimeOffset? start = proof.Complete ? null : proof.CoveredFrom;
        var proven = proof.Complete || proof.CoveredFrom is not null;
        foreach (var gap in gaps)
        {
            var own = gap.Id == backfill.GapId;
            // An unknown end lies before the latest import that found the gap.
            var end = gap.ToUtc ?? await store.GapEndBoundAsync(gap, ct);
            if (!proven || start > end || gap.FromUtc > proof.CoveredTo)
            {
                // The proven span misses the gap, which stays as it is: beyond the venue's reach when it serves none of it.
                if (proof.StartBeyondReach && (!proven || start > end)) gap.BeyondVenueReach = true;
                leftover |= own;
                continue;
            }
            gap.Close(record.Id, now);
            if (start is { } served && (gap.FromUtc is null || gap.FromUtc < served))
            {
                await store.AddAsync(new AccountHistoryGap
                {
                    Id = Guid.CreateVersion7(), OwnerId = gap.OwnerId, AccountId = gap.AccountId, FromUtc = gap.FromUtc, ToUtc = served,
                    OpenedAtUtc = now, OpenedByBackfillId = backfill.Id, BeyondVenueReach = proof.StartBeyondReach
                }, ct);
                leftover |= own;
            }
            if (end > proof.CoveredTo)
            {
                await store.AddAsync(new AccountHistoryGap
                {
                    Id = Guid.CreateVersion7(), OwnerId = gap.OwnerId, AccountId = gap.AccountId, FromUtc = proof.CoveredTo, ToUtc = gap.ToUtc,
                    OpenedAtUtc = now, OpenedByBackfillId = backfill.Id
                }, ct);
                leftover |= own;
            }
        }
        backfill.Finish(leftover ? HistoryBackfill.Narrowed : HistoryBackfill.Completed, now);
        if (!await store.HasOpenGapAsync(account.Id, ct)) row.HasHistoryGap = false;
    }

    /// <summary>
    /// Phase 2, item 6: one import's coverage by the 0.4b rule, in the unit that retains its fills. An import of unknown
    /// truncation records its request only.
    /// </summary>
    private Task RecordCoverageAsync(Account account, string source, PerpetualVenueReadResult read, DateTimeOffset? boundary,
        CancellationToken ct) => store.RecordCoverageAsync(FillCoverageRecord.For(owner.OwnerId, account.Id, source,
            read.Snapshot.ObservedAtUtc, read.FillHistoryTruncated, read.FillHistoryComplete, read.OldestRawFillAtUtc, boundary), ct);

    /// <summary>
    /// The newest fill known before an import: the continuity watermark or the newest retained fill, whichever is later.
    /// Every import records its own continuity against it, so a gap behind fills a Refresh retained is already recorded.
    /// </summary>
    private static DateTimeOffset? ContinuityBoundary(DateTimeOffset? watermark, DateTimeOffset? retained) =>
        watermark is null || retained > watermark ? retained : watermark;

    /// <summary>
    /// A capped read whose oldest raw fill is newer than the boundary, or unknown, may have skipped executions. A first
    /// import, with no fill known before it, cannot be discontinuous.
    /// </summary>
    private static bool ObservedGap(PerpetualVenueReadResult read, DateTimeOffset? boundary) =>
        read.FillHistoryTruncated == true && boundary is not null &&
        (read.OldestRawFillAtUtc is null || read.OldestRawFillAtUtc > boundary);

    /// <summary>Moves the continuity watermark to the newest fill the import read; it never moves back.</summary>
    private static void AdvanceWatermark(MonitorAccount row, PerpetualVenueReadResult read, DateTimeOffset? boundary)
    {
        var newest = read.Fills.Count == 0 ? boundary : read.Fills.Max(f => f.OccurredAtUtc);
        if (newest > row.NewestFillAtUtc || row.NewestFillAtUtc is null) row.NewestFillAtUtc = newest;
    }

    private Task<bool> FailAsync(MonitorLease lease, MonitorCapture expected, bool credential, string error, CancellationToken ct,
        bool snapshotApplied = false, bool interrupted = false) =>
        unitOfWork.ExecuteAsync(() => store.LockedAsync(lease.AccountId, async (account, row) =>
        {
            var refresh = lease.Trigger == "refresh";
            await events.SetRunAsync(lease.RequestId, refresh ? "owner" : "monitor", ct);
            var now = time.GetUtcNow();
            if (!row.Owns(lease.Generation, lease.WorkerId, now)) return false;
            var old = row.State;
            var capture = await store.CaptureAsync(lease.AccountId, ct);
            var settingsChanged = !account.IsEnabled || account.SettingsRevision != expected.Account.SettingsRevision ||
                account.SourceId != expected.Account.SourceId || capture?.CredentialRevision != expected.CredentialRevision;
            if (refresh)
            {
                // The owner gets the failure; check backoff, state, last success and due time stay as they were.
                if (!settingsChanged)
                {
                    if (snapshotApplied)
                        account.RecordOrderHistoryNotice("Public account data refreshed; order coverage is unavailable because the order check did not complete.");
                    // Caller cancellation interrupted the read: the venue did not fail, so keep the sync status.
                    else if (!interrupted) account.RecordSyncFailure();
                }
                row.CompleteRefresh(now, options.ManualCooldown);
                await ApplyVenueBudgetAsync(account.VenueId, row, ct);
                await SettleDiscrepanciesAsync(account, lease, ct);
                return false;
            }
            if (settingsChanged)
            {
                // Superseded by changed settings or credentials during the read: discarded, not failed.
                Supersede(account, row, false, now);
            }
            else
            {
                row.CredentialRevision = capture?.CredentialRevision;
                await store.UpdateWatchesAsync(account.Id, null, now, ct);
                await unitOfWork.SaveChangesAsync(ct);
                row.Complete(now, options.ManualCooldown, options.NextDelay(row.ConsecutiveFailures + 1, Random.Shared.NextDouble()),
                    false, credentialError: credential, error: error);
            }
            // Failed and discarded reads still age discrepancy work, so they cannot hold the account eligible forever.
            await SettleDiscrepanciesAsync(account, lease, ct);
            if (account.IsEnabled && !await store.EligibleAsync(account.Id, now, ct)) row.Idle();
            await ApplyVenueBudgetAsync(account.VenueId, row, ct);
            await StateEventAsync(lease.AccountId, old, row, lease.RequestId, "monitor", now, ct);
            await RunCompletedAsync(lease, row, settingsChanged ? "superseded" : "failed", now, ct);
            return false;
        }, ct), ct);

    /// <summary>The account no longer qualifies, or the run was interrupted, before any result: not a failure.</summary>
    /// <param name="attempted">False before any venue read: housekeeping then only ages outstanding work.</param>
    private Task<bool> SupersedeAsync(MonitorLease lease, CancellationToken ct, bool attempted = true) =>
        unitOfWork.ExecuteAsync(() => store.LockedAsync(lease.AccountId, async (account, row) =>
        {
            var now = time.GetUtcNow();
            if (!row.Owns(lease.Generation, lease.WorkerId, now)) return false;
            await events.SetRunAsync(lease.RequestId, lease.Trigger == "refresh" ? "owner" : "monitor", ct);
            var old = row.State;
            Supersede(account, row, lease.Trigger == "refresh", now);
            await SettleDiscrepanciesAsync(account, lease, ct, attempted);
            if (account.IsEnabled && !await store.EligibleAsync(account.Id, now, ct)) row.Idle();
            await ApplyVenueBudgetAsync(account.VenueId, row, ct);
            await StateEventAsync(lease.AccountId, old, row, lease.RequestId, "monitor", now, ct);
            await RunCompletedAsync(lease, row, "superseded", now, ct);
            return false;
        }, ct), ct);

    private void Supersede(Account account, MonitorAccount row, bool refresh, DateTimeOffset now)
    {
        if (refresh) row.CompleteRefresh(now, options.ManualCooldown);
        else row.Supersede(now, options.ManualCooldown, MonitorOptions.FirstRetry);
        if (!account.IsEnabled) row.Idle();
    }

    /// <summary>
    /// The single place where a lease-owned run that made no discrepancy decisions (failed, discarded, superseded,
    /// deferred or refresh) settles discrepancy housekeeping. A failed, discarded or interrupted check attempted an
    /// observation and interrupts pending confirmations; a deferral, or a check superseded before its read, attempted
    /// nothing and, like owner refreshes, only ages outstanding work. An evaluated check settles inside the evaluator.
    /// </summary>
    private Task SettleDiscrepanciesAsync(Account account, MonitorLease lease, CancellationToken ct, bool attempted = true) =>
        evaluator?.SettleAsync(account, lease.RequestId, lease.Trigger, interrupted: attempted && lease.Trigger != "refresh", ct) ?? Task.CompletedTask;

    private Task<int> DeferThrottledLeaseAsync(MonitorLease lease, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(() => store.LockedAsync(lease.AccountId, async (account, row) =>
        {
            var now = time.GetUtcNow();
            if (!row.Owns(lease.Generation, lease.WorkerId, now)) return -1;
            var deadline = await store.VenueRetryAtAsync(account.VenueId, ct);
            if (!(deadline > now)) return 0;
            var refresh = lease.Trigger == "refresh";
            // Repeated deferrals must still age discrepancy work; nothing was attempted, so nothing is interrupted.
            await events.SetRunAsync(lease.RequestId, refresh ? "owner" : "monitor", ct);
            await SettleDiscrepanciesAsync(account, lease, ct, attempted: false);
            if (refresh)
            {
                // The owner gets 429 for this refresh. It is not kept pending for the worker to replay.
                row.Release();
                row.NextManualAllowedAtUtc = Later(row.NextManualAllowedAtUtc, deadline);
                return SecondsUntil(deadline, now);
            }
            row.ManualState = "pending";
            row.LeaseHolder = null;
            row.LeaseUntilUtc = null;
            row.NextRunAtUtc = Later(row.NextRunAtUtc, deadline);
            row.NextManualAllowedAtUtc = Later(row.NextManualAllowedAtUtc, deadline);
            // A check without remaining work is not kept pending.
            if (!await store.EligibleAsync(account.Id, now, ct))
            {
                var old = row.State;
                row.Release();
                row.Idle();
                await StateEventAsync(account.Id, old, row, lease.RequestId, "monitor", now, ct);
                await RunCompletedAsync(lease, row, "superseded", now, ct);
            }
            return SecondsUntil(deadline, now);
        }, ct), ct);

    private async Task<int> RetryAfterAsync(string venueId, MonitorAccount row, DateTimeOffset now, CancellationToken ct) =>
        Math.Max(row.RetryAfter(now), SecondsUntil(await store.VenueRetryAtAsync(venueId, ct), now));

    private async Task ApplyVenueBudgetAsync(string venueId, MonitorAccount row, CancellationToken ct)
    {
        var deadline = await store.VenueRetryAtAsync(venueId, ct);
        row.NextManualAllowedAtUtc = Later(row.NextManualAllowedAtUtc, deadline);
        if (row.NextRunAtUtc is not null) row.NextRunAtUtc = Later(row.NextRunAtUtc, deadline);
    }

    private static DateTimeOffset? Later(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null || right > left ? right : left;
    private static int SecondsUntil(DateTimeOffset? deadline, DateTimeOffset now) =>
        deadline > now ? (int)Math.Min(int.MaxValue, Math.Ceiling((deadline.Value - now).TotalSeconds)) : 0;

    /// <summary>
    /// One event per persisted state change, keyed by its <see cref="MonitorAccount.StateRevision"/>: repeated
    /// cycles are distinct events, and a retry or re-read of an unchanged state is none.
    /// </summary>
    private Task StateEventAsync(Guid accountId, string old, MonitorAccount row, Guid runId, string trigger,
        DateTimeOffset now, CancellationToken ct) => old == row.State ? Task.CompletedTask :
        events.AppendAsync(new("monitor.state-changed", runId, $"monitor:{accountId}:{row.StateRevision}:{row.State}",
            old == "live" && row.State != "idle" ? "warning" : "info", now, now, new(AccountId: accountId), new(trigger),
            JsonSerializer.SerializeToElement(new { previous = old, state = row.State, error = row.Error })), ct);

    /// <summary>
    /// An owner Check can finish without a state change. Browsers still need a signal that it is idle again, so every
    /// completed owner Check appends one event, keyed by its request. A deferred Check is still pending: no signal.
    /// </summary>
    private Task RunCompletedAsync(MonitorLease lease, MonitorAccount row, string outcome, DateTimeOffset now, CancellationToken ct) =>
        RunCompletedAsync(lease.AccountId, lease.RequestId, lease.Trigger, row, outcome, now, ct);

    private Task RunCompletedAsync(Guid accountId, Guid requestId, string trigger, MonitorAccount row, string outcome,
        DateTimeOffset now, CancellationToken ct) => trigger != "manual" ? Task.CompletedTask :
        events.AppendAsync(new("monitor.run-completed", requestId, $"monitor-run:{accountId}:{requestId}",
            "info", now, now, new(AccountId: accountId), new("owner"),
            JsonSerializer.SerializeToElement(new { trigger, outcome, state = row.State })), ct);

    // Owner Checks, owner refreshes and scheduled/confirmation runs share admission; the browser labels them apart.
    private static string? RunTrigger(MonitorAccount row) => row.ManualState == "idle" ? null : row.Trigger switch
    {
        "manual" => "owner",
        "refresh" => "refresh",
        _ => "scheduled",
    };

    private string[] EligibleVenues() => venues.Descriptors
        .Where(v => v.Capabilities is { Sync: true, Orders: true }).Select(v => v.Id).ToArray();
    private bool EligibleVenue(string venue) => venues.Descriptor(venue) is { Capabilities.Sync: true, Capabilities.Orders: true };
    private bool IsOnline(DateTimeOffset? heartbeat) => heartbeat is { } at && at <= time.GetUtcNow() &&
        time.GetUtcNow() - at <= options.HeartbeatTimeout;
    private async Task<AccountMonitorDto> ToDto(MonitorAccount row, DateTimeOffset? venueDeadline, bool online,
        IEnumerable<MonitorCoverage> coverage, int uncheckedPlays, CancellationToken ct)
    {
        var state = row.State == "live" && row.LastSuccessAtUtc < time.GetUtcNow() - options.Interval * 2 ? "stale" : row.State;
        var records = evaluator is null ? new MonitorDiscrepancyList([], false) : await evaluator.ReadListAsync(row.AccountId, null, ct);
        return new(row.AccountId, state, row.LastAttemptAtUtc, row.LastSuccessAtUtc,
            row.NextRunAtUtc is null ? null : Later(row.NextRunAtUtc, venueDeadline),
            row.ManualState, RunTrigger(row), Later(row.NextManualAllowedAtUtc, venueDeadline), row.Error, online,
            records.Records, records.Truncated, coverage.Where(x => x.AccountId == row.AccountId).Select(Coverage).ToArray(), uncheckedPlays)
        {
            IntervalMinutes = (int)Math.Round(options.Interval.TotalMinutes),
        };
    }

    private static readonly JsonSerializerOptions CoverageJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Stored coverage as read: <c>stale</c> is a projection, never stored, once the check is older than twice the
    /// interval (the account's own staleness rule). The recorded state, reason and evidence stay visible.
    /// </summary>
    private MonitorCoverageDto Coverage(MonitorCoverage row) => new(row.PlayId, row.ContractId,
        row.CheckedAtUtc < time.GetUtcNow() - options.Interval * 2 ? MonitorCoverageStates.Stale : row.State, row.State,
        row.State == MonitorCoverageStates.Known ? row.Covered : null, row.Reason, row.CheckedAtUtc,
        JsonSerializer.Deserialize<MonitorStopEvidenceDto[]>(row.Stops, CoverageJson) ?? []);
}
