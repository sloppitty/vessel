using Vessel.Application.Common;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Ownership;
using Vessel.Application.Credentials;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

public sealed class MonitorStore(VesselDbContext db, UnitOfWork unitOfWork, IServerStateStore server,
    IJournalOwnerDirectory owners, MonitorOptions? options = null) : IMonitorStore
{
    private readonly MonitorOptions settings = options ?? new();
    private IQueryable<MonitorAccount> Rows => db.Set<MonitorAccount>();
    private IQueryable<MonitorExitWatch> Exits => db.Set<MonitorExitWatch>();

    public async Task<MonitorCapture?> CaptureAsync(Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null) return null;
        var purpose = VenueCredentialPurpose.ReadToken(account.VenueId);
        var credential = await db.AccountCredentials.AsNoTracking()
            .SingleOrDefaultAsync(c => c.AccountId == accountId && c.Purpose == purpose, ct);
        var revision = credential is null ? null : CredentialAttempt.RevisionOf(credential);
        var newestFill = await db.Fills.Where(f => f.AccountId == accountId)
            .MaxAsync(f => (DateTimeOffset?)f.OccurredAtUtc, ct);
        return new(account, revision, credential?.ExpiresAt, newestFill);
    }

    public async Task<IReadOnlyList<(MonitorAccount Row, string VenueId)>> SummaryAsync(IReadOnlyCollection<string> venueIds,
        DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - settings.WatchLifetime;
        var rows = await (from m in Rows.AsNoTracking()
                          join a in db.Accounts on m.AccountId equals a.Id
                          where a.IsEnabled && a.SourceId != null && venueIds.Contains(a.VenueId) &&
                              (ActivePlays.Any(p => p.AccountId == a.Id) ||
                               Watching.Any(x => x.Link.AccountId == a.Id && (x.Link.UpdatedAtUtc > cutoff || x.EndedAtUtc > cutoff)) ||
                               AwaitingDiscrepancies.Any(d => d.AccountId == a.Id) ||
                               // Independent of scheduling: what the persistent incident indicators read stays listed.
                               OutstandingAnnouncements.Any(d => d.AccountId == a.Id))
                          orderby m.AccountId
                          select new { Row = m, a.VenueId }).Take(200).ToListAsync(ct);
        return rows.Select(r => (r.Row, r.VenueId)).ToArray();
    }

    public Task<MonitorAccount?> FindAsync(Guid accountId, CancellationToken ct) =>
        Rows.AsNoTracking().SingleOrDefaultAsync(m => m.AccountId == accountId, ct);
    public Task<DateTimeOffset?> VenueRetryAtAsync(string venueId, CancellationToken ct) => server.VenueRetryAtAsync(venueId, ct);
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> VenueRetriesAsync(CancellationToken ct) => server.VenueRetriesAsync(ct);
    public Task DeferVenueAsync(string venueId, DateTimeOffset retryAtUtc, DateTimeOffset now, CancellationToken ct) =>
        server.DeferVenueAsync(venueId, retryAtUtc, now, ct);
    public Task<ServerMonitorHeartbeat?> HeartbeatAsync(CancellationToken ct) => server.HeartbeatAsync(ct);

    private IQueryable<Play> ActivePlays => db.Plays.Where(p => p.InstrumentSource == InstrumentSource.Venue && p.ContractId != null &&
        p.Origin == PlayOrigin.Planned && (p.Status == PlayStatus.Planned || p.Status == PlayStatus.Paused || p.Status == PlayStatus.Open));

    // Member initialization, not a constructor, so the projection stays translatable when it is composed further.
    private sealed class TerminalExit
    {
        public PlayOrderLink Link { get; init; } = null!;
        /// <summary>The link's order; immutable, so the watch identifies it through the link.</summary>
        public ImportedOrder Order { get; init; } = null!;
        public DateTimeOffset? EndedAtUtc { get; init; }
    }

    /// <summary>
    /// Linked orders of terminal planned venue Plays, exits and entries alike (a resting entry on a Cancelled Play is an
    /// orphan too), with the Play's end for the watch start.
    /// </summary>
    private IQueryable<TerminalExit> TerminalLinks =>
        from l in db.OrderLinks
        join p in db.Plays on l.PlayId equals p.Id
        join o in db.Orders on new { l.OwnerId, l.AccountId, Id = l.ImportedOrderId } equals new { o.OwnerId, o.AccountId, o.Id }
        where l.State == OrderLinkState.Linked &&
            p.InstrumentSource == InstrumentSource.Venue && p.Origin == PlayOrigin.Planned &&
            (p.Status == PlayStatus.Closed || p.Status == PlayStatus.Cancelled)
        select new TerminalExit { Link = l, Order = o, EndedAtUtc = p.EndedAtUtc };

    /// <summary>Terminal Plays' linked orders whose current link revision has no ended watch.</summary>
    private IQueryable<TerminalExit> Watching => TerminalLinks.Where(x => !Exits.Any(e => e.LinkId == x.Link.Id &&
        e.EndedAtUtc != null && e.LinkRevisionAtUtc >= x.Link.UpdatedAtUtc));

    public async Task<bool> EligibleAsync(Guid accountId, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - settings.WatchLifetime;
        // The watch starts at the later of the Play's end and the link change; it is live while that is after the cutoff.
        return await ActivePlays.AnyAsync(p => p.AccountId == accountId, ct) ||
            await Watching.AnyAsync(x => x.Link.AccountId == accountId && (x.Link.UpdatedAtUtc > cutoff || x.EndedAtUtc > cutoff), ct) ||
            await AwaitsDiscrepancyAsync(accountId, ct);
    }

    /// <summary>
    /// Announced records still awaiting the owner, the browser's incidents: an open record, or one announced unverifiable and
    /// not dismissed since. They keep their account in the summary after its scheduling work has ended; they never make it
    /// eligible for checks.
    /// </summary>
    private IQueryable<PlayDiscrepancy> OutstandingAnnouncements => db.Set<PlayDiscrepancy>().Where(PlayDiscrepancy.IsOutstanding);

    /// <summary>Outstanding, still verifiable discrepancy work: a (re-)confirmation or an open warning.</summary>
    private IQueryable<PlayDiscrepancy> AwaitingDiscrepancies => db.Set<PlayDiscrepancy>().Where(d => d.UnverifiableAtUtc == null &&
        (d.State == DiscrepancyLifecycle.Candidate || d.State == DiscrepancyLifecycle.Open || d.PendingFingerprint != null));

    /// <summary>
    /// Outstanding discrepancy work keeps the account checked, even after its last Play or watch ends: a (re-)confirmation
    /// in progress whatever the public state, including across interrupted sequences, or an open warning awaiting fresh
    /// clearance. An unverifiable record no longer does. Rows changed in this unit of work but not yet saved take precedence.
    /// </summary>
    private async Task<bool> AwaitsDiscrepancyAsync(Guid accountId, CancellationToken ct)
    {
        var local = db.ChangeTracker.Entries<PlayDiscrepancy>()
            .Where(e => e.Entity.AccountId == accountId && e.State != EntityState.Deleted).Select(e => e.Entity).ToArray();
        if (local.Any(x => x.HasWork)) return true;
        var tracked = local.Select(x => x.Id).ToArray();
        return await AwaitingDiscrepancies.AnyAsync(d => d.AccountId == accountId && !tracked.Contains(d.Id), ct);
    }

    public async Task<IReadOnlyList<MonitorCandidate>> DiscoveryCandidatesAsync(IReadOnlyCollection<string> venueIds,
        DateTimeOffset now, Guid? after, int limit, CancellationToken ct)
    {
        var cutoff = now - settings.WatchLifetime;
        // Keyset page: the filter applies before the limit, so every candidate is reached by a later page.
        var rows = await db.Accounts.AsNoTracking()
            .Where(a => a.IsEnabled && a.SourceId != null && venueIds.Contains(a.VenueId) ||
                Rows.Any(m => m.AccountId == a.Id && (m.State != "idle" || m.NextRunAtUtc != null || m.ManualState != "idle")))
            .Where(a => after == null || a.Id.CompareTo(after.Value) > 0)
            .OrderBy(a => a.Id).Take(Math.Clamp(limit, 1, 1000))
            .Select(a => new
            {
                a.Id, a.VenueId,
                Monitorable = a.IsEnabled && a.SourceId != null && venueIds.Contains(a.VenueId),
                Active = ActivePlays.Any(p => p.AccountId == a.Id),
                Watching = Watching.Any(x => x.Link.AccountId == a.Id && (x.Link.UpdatedAtUtc > cutoff || x.EndedAtUtc > cutoff)),
                Expired = Watching.Any(x => x.Link.AccountId == a.Id && !(x.Link.UpdatedAtUtc > cutoff || x.EndedAtUtc > cutoff)),
                Awaiting = AwaitingDiscrepancies.Any(d => d.AccountId == a.Id),
                Row = Rows.FirstOrDefault(m => m.AccountId == a.Id)
            }).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToArray();
        var credentials = (await db.AccountCredentials.AsNoTracking().Where(c => ids.Contains(c.AccountId)).ToListAsync(ct))
            .ToLookup(c => c.AccountId);
        return rows.Select(r => new MonitorCandidate(r.Id, r.VenueId, r.Monitorable, r.Active || r.Watching || r.Awaiting, r.Expired,
            credentials[r.Id].Where(c => c.Purpose == VenueCredentialPurpose.ReadToken(r.VenueId)).Select(CredentialAttempt.RevisionOf).FirstOrDefault(),
            r.Row)).ToArray();
    }

    public Task<T> LockedAsync<T>(Guid accountId, Func<Account, MonitorAccount, Task<T>> action, CancellationToken ct) =>
        unitOfWork.ParticipateAsync(async () =>
        {
            var account = (await db.Accounts.FromSqlInterpolated($"""
                SELECT * FROM accounts WHERE "Id" = {accountId} AND "OwnerId" = {db.CurrentOwnerId} FOR UPDATE
                """).ToListAsync(ct)).SingleOrDefault() ?? throw new ProblemException(404, "Account not found.");
            return await MonitorLockedAsync(account, action, ct);
        }, ct);

    private async Task<T> MonitorLockedAsync<T>(Account account, Func<Account, MonitorAccount, Task<T>> action, CancellationToken ct)
    {
        await db.Entry(account).ReloadAsync(ct);
        var id = account.Id;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO monitor_accounts ("AccountId", "OwnerId", "State", "StateRevision", "ManualState", "Trigger", "Generation", "ConsecutiveFailures", "HasHistoryGap")
            VALUES ({id}, {db.CurrentOwnerId}, 'idle', 0, 'idle', 'monitor', 0, 0, false) ON CONFLICT ("AccountId") DO NOTHING
            """, ct);
        var row = (await db.Set<MonitorAccount>().FromSqlInterpolated($"""
            SELECT * FROM monitor_accounts WHERE "AccountId" = {id} AND "OwnerId" = {db.CurrentOwnerId} FOR UPDATE
            """).ToListAsync(ct)).Single();
        await db.Entry(row).ReloadAsync(ct);
        return await action(account, row);
    }

    // Owner-scoped convenience for callers that already have an owner; never hold several account/venue locks.
    public async Task<IReadOnlyList<MonitorLease>> ClaimDueAsync(string workerId, DateTimeOffset now, int limit, CancellationToken ct)
    {
        if (unitOfWork.IsActive) throw new InvalidOperationException("Due claims require independent account transactions.");
        var ids = await Rows.AsNoTracking().Where(m => (m.NextRunAtUtc <= now || m.ManualState != "idle") &&
                (m.LeaseUntilUtc == null || m.LeaseUntilUtc <= now))
            .OrderBy(m => m.NextRunAtUtc).ThenBy(m => m.AccountId).Select(m => m.AccountId)
            .Take(Math.Clamp(limit, 1, 64)).ToListAsync(ct);
        var leases = new List<MonitorLease>();
        foreach (var id in ids)
            if (await ClaimAsync(id, null, workerId, now, ct) is { } lease) leases.Add(lease);
        return leases;
    }

    public Task<MonitorLease?> ClaimAsync(Guid accountId, Guid? requestId, string workerId, DateTimeOffset now, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(async () =>
        {
            // Background claims must not wait behind an owner's credential verification or settings update.
            // API admission still uses the blocking account lock in LockedAsync.
            var account = (await db.Accounts.FromSqlInterpolated($"""
                SELECT * FROM accounts WHERE "Id" = {accountId} AND "OwnerId" = {db.CurrentOwnerId} FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct)).SingleOrDefault();
            if (account is null) return null;
            return await MonitorLockedAsync<MonitorLease?>(account, async (account, row) =>
            {
                if (await owners.StatusAsync(db.CurrentOwnerId, ct) != JournalOwnerStatus.Active ||
                    !account.IsEnabled || account.SourceId is null) return null;
                row.ReleaseAbandonedRefresh(now);
                if (row.ManualState == "running" && row.LeaseUntilUtc > now ||
                    row.ManualState == "idle" && !(row.NextRunAtUtc <= now) || row.Trigger == "refresh" && requestId is not null)
                    return null;
                if (!await ReadCapacityAsync(account.VenueId, now, ct) || await server.VenueRetryAtAsync(account.VenueId, ct) > now)
                    return null;
                if (requestId is null && row.ManualState == "idle")
                {
                    if (!await EligibleAsync(account.Id, now, ct)) { row.NextRunAtUtc = null; return null; }
                    row.Admit(Guid.CreateVersion7(), row.ConfirmationAtUtc <= now ? "confirmation" : "monitor", now, scheduled: true);
                }
                if (row.Trigger == "refresh") return null;
                var request = requestId ?? row.RequestId;
                return request is { } pending && row.Claim(pending, workerId, now, MonitorOptions.Lease)
                    ? new MonitorLease(db.CurrentOwnerId, accountId, pending, row.Generation, workerId, row.Trigger) : null;
            }, ct);
        }, ct);

    public async Task<bool> OwnerAtCapacityAsync(DateTimeOffset now, CancellationToken ct) =>
        await Rows.CountAsync(m => m.ManualState == "running" && m.LeaseUntilUtc > now, ct) >= 2;

    public async Task<bool> ReadCapacityAsync(string venueId, DateTimeOffset now, CancellationToken ct)
    {
        unitOfWork.RequireActive();
        var ownerKey = "vessel-monitor-budget:" + db.CurrentOwnerId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ownerKey}, 0))", ct);
        var venueKey = "vessel-venue-budget:" + venueId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({venueKey}, 0))", ct);
        // Count on this transaction's connection, including any lease it has just saved. No owner filter is removed.
        await unitOfWork.SaveChangesAsync(ct);
        if (await OwnerAtCapacityAsync(now, ct)) return false;
        return !await VenueAtCapacityAsync(venueId, now, ct);
    }

    public async Task<bool> VenueAtCapacityAsync(string venueId, DateTimeOffset now, CancellationToken ct) =>
        await db.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value" FROM monitor_accounts m JOIN accounts a
                ON a."Id" = m."AccountId" AND a."OwnerId" = m."OwnerId"
            WHERE a."VenueId" = {venueId} AND m."ManualState" = 'running' AND m."LeaseUntilUtc" > {now}
            """).SingleAsync(ct) >= settings.VenueReadConcurrency;

    public Task<bool> RenewAsync(MonitorLease lease, DateTimeOffset now, CancellationToken ct) =>
        lease.OwnerId != db.CurrentOwnerId ? Task.FromResult(false) : unitOfWork.ExecuteAsync(() => LockedAsync(lease.AccountId, (_, row) =>
        {
            if (!row.Owns(lease.Generation, lease.WorkerId, now)) return Task.FromResult(false);
            row.LeaseUntilUtc = now + MonitorOptions.Lease;
            return Task.FromResult(true);
        }, ct), ct);

    public async Task UpdateWatchesAsync(Guid accountId, VenueOrderReadResult? orders, DateTimeOffset now, CancellationToken ct)
    {
        unitOfWork.RequireActive();
        var cutoff = now - settings.WatchLifetime;
        var links = await TerminalLinks.Where(x => x.Link.AccountId == accountId).ToListAsync(ct);
        var watches = await Exits.Where(e => e.AccountId == accountId).ToDictionaryAsync(e => e.LinkId, ct);
        foreach (var exit in links)
        {
            var (link, order, ended) = (exit.Link, exit.Order, exit.EndedAtUtc);
            var started = ended > link.UpdatedAtUtc ? ended.Value : link.UpdatedAtUtc;
            if (!watches.TryGetValue(link.Id, out var watch))
            {
                watch = new() { LinkId = link.Id, OwnerId = link.OwnerId, AccountId = accountId, PlayId = link.PlayId,
                    LinkRevisionAtUtc = link.UpdatedAtUtc, StartedAtUtc = started };
                db.Add(watch);
            }
            if (watch.LinkRevisionAtUtc < link.UpdatedAtUtc)
            {
                // A relinked order is a new watch with its own lifetime.
                watch.LinkRevisionAtUtc = link.UpdatedAtUtc;
                watch.StartedAtUtc = started;
                watch.EndedAtUtc = null;
                watch.Outcome = null;
            }
            if (watch.Outcome == MonitorWatchOutcomes.Unverifiable) continue;
            // Closing during this import does not make an earlier order observation fresh after closure.
            if (orders is not null && orders.ObservedAtUtc > link.UpdatedAtUtc && !(ended >= orders.ObservedAtUtc))
            {
                var current = orders.Orders.FirstOrDefault(o => o.ContractId == order.ContractId && o.OrderId == order.ExternalOrderId);
                // Reads combine order history with the current book. Only a current-book row (observed with the read)
                // establishes a resting order; a historical Open row is not evidence that it still rests.
                var resting = current is { Status: VenueOrderStatus.Open } && current.StatusAtUtc == orders.ObservedAtUtc;
                if (resting)
                {
                    watch.EndedAtUtc = null;
                    watch.Outcome = null;
                }
                else if (current?.Status is VenueOrderStatus.Filled or VenueOrderStatus.Canceled or VenueOrderStatus.Rejected)
                {
                    // The read reports this specific order's end, whether or not it covers the whole book.
                    watch.EndedAtUtc = orders.ObservedAtUtc;
                    watch.Outcome = MonitorWatchOutcomes.Terminal;
                }
                else if ((current is null || current.Status == VenueOrderStatus.Open) && orders.OpenOrdersComplete)
                {
                    // Absent from a complete current book; absence proves nothing on an incomplete read.
                    watch.EndedAtUtc = orders.ObservedAtUtc;
                    watch.Outcome = MonitorWatchOutcomes.Absent;
                }
            }
            if (watch.Outcome is null && watch.StartedAtUtc <= cutoff)
            {
                watch.EndedAtUtc = now;
                watch.Outcome = MonitorWatchOutcomes.Unverifiable;
            }
        }
    }

    public async Task SaveCoverageAsync(Guid accountId, IReadOnlyList<MonitorCoverage> rows, CancellationToken ct)
    {
        unitOfWork.RequireActive();
        if (rows.Any(x => x.AccountId != accountId || x.OwnerId != db.CurrentOwnerId))
            throw new InvalidOperationException("Coverage rows must belong to the locked account and the current owner.");
        var stored = await db.Set<MonitorCoverage>().Where(x => x.AccountId == accountId).ToDictionaryAsync(x => x.PlayId, ct);
        foreach (var row in rows)
        {
            if (!stored.Remove(row.PlayId, out var current)) { db.Add(row); continue; }
            current.ContractId = row.ContractId;
            current.State = row.State;
            current.Covered = row.Covered;
            current.Reason = row.Reason;
            current.CheckedAtUtc = row.CheckedAtUtc;
            current.Stops = row.Stops;
        }
        // Plays the check no longer evaluated (closed, cancelled or moved) keep no stale outcome.
        db.RemoveRange(stored.Values);
    }

    public async Task<IReadOnlyList<MonitorCoverage>> CoverageAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        if (accountIds.Count == 0) return [];
        var ids = accountIds.Distinct().ToArray();
        var rows = await db.Set<MonitorCoverage>().AsNoTracking()
            .Where(x => ids.Contains(x.AccountId) && db.Plays.Any(p => p.Id == x.PlayId && p.Status == PlayStatus.Open))
            .OrderBy(x => x.AccountId).ThenBy(x => x.PlayId).ToListAsync(ct);
        return rows.GroupBy(x => x.AccountId).SelectMany(x => x.Take(500)).ToArray();
    }

    public async Task<IReadOnlyList<MonitorWatchDto>> WatchesAsync(Guid playId, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - settings.WatchLifetime;
        var links = await TerminalLinks.AsNoTracking().Where(x => x.Link.PlayId == playId).ToListAsync(ct);
        var ids = links.Select(x => x.Link.Id).ToArray();
        var watches = await Exits.AsNoTracking().Where(e => ids.Contains(e.LinkId)).ToDictionaryAsync(e => e.LinkId, ct);
        return links.OrderBy(x => x.Order.ExternalOrderId, StringComparer.Ordinal).Select(x =>
        {
            var started = x.EndedAtUtc > x.Link.UpdatedAtUtc ? x.EndedAtUtc.Value : x.Link.UpdatedAtUtc;
            var watch = watches.GetValueOrDefault(x.Link.Id);
            if (watch is not null && watch.LinkRevisionAtUtc < x.Link.UpdatedAtUtc) watch = null;
            var state = watch?.Outcome switch
            {
                MonitorWatchOutcomes.Unverifiable => "unverifiable",
                MonitorWatchOutcomes.Terminal or MonitorWatchOutcomes.Absent => "resolved",
                // Past its lifetime but not yet recorded by a run: still never "resolved".
                _ => started <= cutoff ? "unverifiable" : "watching"
            };
            return new MonitorWatchDto(x.Link.Id, x.Link.PlayId, x.Order.ExternalOrderId, x.Link.Role.ToString().ToLowerInvariant(), state, watch?.StartedAtUtc ?? started, watch?.EndedAtUtc);
        }).ToArray();
    }
}
