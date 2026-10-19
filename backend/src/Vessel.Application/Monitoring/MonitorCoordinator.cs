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
    TimeProvider time, IMonitorEvaluator? evaluator = null, IMonitorRunLog? log = null)
{
    public async Task<MonitorSummaryDto> SummaryAsync(CancellationToken ct)
    {
        var heartbeat = await store.HeartbeatAsync(ct);
        var online = IsOnline(heartbeat?.HeartbeatAtUtc);
        // One bounded read of rows and venue deadlines; no per-row account capture.
        var budgets = await store.VenueRetriesAsync(ct);
        var accounts = new List<AccountMonitorDto>();
        foreach (var (row, venueId) in await store.SummaryAsync(EligibleVenues(), time.GetUtcNow(), ct))
            accounts.Add(await ToDto(row, budgets.TryGetValue(venueId, out var deadline) ? deadline : null, online, ct));
        return new(online, heartbeat?.HeartbeatAtUtc, accounts);
    }

    public async Task<AccountMonitorDto> AccountAsync(Guid id, CancellationToken ct)
    {
        var capture = await store.CaptureAsync(id, ct) ?? throw new ProblemException(404, "Account not found.");
        var row = await store.FindAsync(id, ct) ?? new() { AccountId = id, OwnerId = owner.OwnerId };
        return await ToDto(row, await store.VenueRetryAtAsync(capture.Account.VenueId, ct),
            IsOnline((await store.HeartbeatAsync(ct))?.HeartbeatAtUtc), ct);
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
                    var monitorable = account.IsEnabled && account.SourceId is not null && venueIds.Contains(account.VenueId);
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
            if (!account.IsEnabled || account.SourceId is null || !EligibleVenue(account.VenueId) ||
                !await store.EligibleAsync(accountId, now, ct))
                throw new ProblemException(409, "This account has no eligible venue Plays, pending linked orders or discrepancy work.");
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
        var refresh = lease.Trigger == "refresh";
        var snapshotApplied = false;
        try
        {
            if (!refresh && !await store.EligibleAsync(lease.AccountId, time.GetUtcNow(), ct))
                return await SupersedeAsync(lease, ct, attempted: false);
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
            if (!capture.Account.IsEnabled || capture.Account.SourceId is null) return await SupersedeAsync(lease, ct, attempted: false);
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
                    // Read outside the unit of work; apply facts only after the lease/settings fence.
                    var to = DateTimeOffset.FromUnixTimeMilliseconds(sync.Snapshot.ObservedAtUtc.ToUnixTimeMilliseconds());
                    var from = to.AddDays(-30);
                    VenueFundingReadResult funding;
                    try
                    {
                        var retryAt = await store.VenueRetryAtAsync(capture.Account.VenueId, ct);
                        if (retryAt > time.GetUtcNow()) throw new VenueRateLimitException(retryAt.Value);
                        funding = await fundingReader.ReadFundingAsync(capture.Account, from, to, ct);
                        ct.ThrowIfCancellationRequested();
                        VenueFundingChecks.Validate(funding, from, to);
                    }
                    catch (Exception ex) when (ex is VenueReadException or HttpRequestException ||
                        ex is OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        if (ex is VenueRateLimitException limited)
                            await store.DeferVenueAsync(capture.Account.VenueId, limited.RetryAtUtc, time.GetUtcNow(), CancellationToken.None);
                        funding = new([], from, to, false,
                            "Funding history could not be refreshed. Retained payments are unchanged; missing funding is not zero.");
                    }
                    sync = sync with { Funding = funding };
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
            return await ApplyAsync(lease, capture, new(sync, orders, orderError, credentialError), ct, snapshotApplied: snapshotApplied);
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

    private Task<bool> ApplyAsync(MonitorLease lease, MonitorCapture captured, MonitorRead read, CancellationToken ct,
        bool snapshotOnly = false, bool snapshotApplied = false) =>
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
            var evaluation = evaluator is null ? new MonitorEvaluationResult(null) : await evaluator.EvaluateAsync(
                new(lease.RequestId, account, read, previousSuccess, previousFill, gap, lease.Trigger), ct);
            row.LastObservationAtUtc = read.Sync.Snapshot.ObservedAtUtc;
            row.LastOrdersObservationAtUtc = read.Orders?.ObservedAtUtc ?? row.LastOrdersObservationAtUtc;
            AdvanceWatermark(row, read.Sync, previousFill);
            row.CredentialRevision = captured.CredentialRevision;
            var omitted = read.Sync.OmittedExecutions > 0;
            var degraded = read.OrderError is not null || evaluation.Incomplete || gap || omitted || !read.Sync.PositionsComplete ||
                !read.Sync.FillHistoryComplete && read.Sync.FillHistoryTruncated is null ||
                read.Orders is { OpenOrdersComplete: false };
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
    private async Task<AccountMonitorDto> ToDto(MonitorAccount row, DateTimeOffset? venueDeadline, bool online, CancellationToken ct)
    {
        var state = row.State == "live" && row.LastSuccessAtUtc < time.GetUtcNow() - options.Interval * 2 ? "stale" : row.State;
        var records = evaluator is null ? new MonitorDiscrepancyList([], false) : await evaluator.ReadListAsync(row.AccountId, null, ct);
        return new(row.AccountId, state, row.LastAttemptAtUtc, row.LastSuccessAtUtc,
            row.NextRunAtUtc is null ? null : Later(row.NextRunAtUtc, venueDeadline),
            row.ManualState, RunTrigger(row), Later(row.NextManualAllowedAtUtc, venueDeadline), row.Error, online,
            records.Records, records.Truncated);
    }
}
