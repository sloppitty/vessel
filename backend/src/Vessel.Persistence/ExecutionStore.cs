using System.Data;
using Vessel.Domain.Numerics;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

public sealed class ExecutionStore(VesselDbContext db, UnitOfWork unitOfWork) : IPlayExecutionStore
{
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => db.Accounts.SingleOrDefaultAsync(x => x.Id == id, ct);
    public async Task<List<Play>> ActivePlaysAsync(Guid accountId, CancellationToken ct, bool settling = false)
    {
        unitOfWork.RequireActive();
        // The row locks make owner status and plan changes wait for this transaction (PlayStore.WithPlayLockAsync).
        // Owner is explicit in raw SQL as well as the global query filter. Settling adds Unresolved Plays, whose existing
        // linked orders still reconcile (phase 1, 1.3a).
        string[] statuses = settling ? ["Planned", "Paused", "Open", "Unresolved"] : ["Planned", "Paused", "Open"];
        var plays = await db.Plays.FromSqlInterpolated($"""
            SELECT * FROM plays WHERE "AccountId" = {accountId} AND "OwnerId" = {db.CurrentOwnerId}
            AND "TradingMode" = 'real' AND "Status" = ANY({statuses}) FOR NO KEY UPDATE
            """).OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id).ToListAsync(ct);
        // A scoped context could already track a Play read before waiting on the lock.
        foreach (var play in plays) await db.Entry(play).ReloadAsync(ct);
        return plays;
    }
    // Orders are read-only for every consumer; UpsertOrdersAsync loads its own tracked rows.
    public Task<List<ImportedOrder>> OrdersAsync(Guid accountId, CancellationToken ct) =>
        db.Orders.Where(o => o.AccountId == accountId).OrderBy(o => o.PlacedAtUtc).ThenBy(o => o.ExternalOrderId).AsNoTracking().ToListAsync(ct);

    public async Task<List<ImportedOrder>> OrdersAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds,
        IReadOnlyDictionary<string, DateTimeOffset> placedSince, CancellationToken ct)
    {
        var ids = orderIds.Distinct().ToArray();
        var contracts = placedSince.Keys.ToArray();
        if (ids.Length == 0 && contracts.Length == 0) return [];
        var query = db.Orders.Where(o => o.AccountId == accountId);
        if (contracts.Length == 0) query = query.Where(o => ids.Contains(o.Id));
        else
        {
            // SQL narrows to the contracts from their earliest time; the shared rule below applies each contract's own time.
            var from = placedSince.Values.Min().ToUniversalTime();
            query = query.Where(o => ids.Contains(o.Id) || contracts.Contains(o.ContractId) && o.PlacedAtUtc >= from);
        }
        var rows = await query.OrderBy(o => o.PlacedAtUtc).ThenBy(o => o.ExternalOrderId).AsNoTracking().ToListAsync(ct);
        return rows.Where(o => IPlayExecutionStore.IncludesOrder(o, orderIds, placedSince)).ToList();
    }

    public Task<ImportedOrder?> OrderAsync(Guid accountId, string contractId, string externalOrderId, CancellationToken ct) =>
        db.Orders.Where(o => o.AccountId == accountId && o.ContractId == contractId && o.ExternalOrderId == externalOrderId)
            .AsNoTracking().SingleOrDefaultAsync(ct);

    public Task<List<PlayOrderLink>> LinksAsync(Guid accountId, CancellationToken ct) =>
        db.OrderLinks.Where(l => l.AccountId == accountId).OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id).ToListAsync(ct);

    public Task<List<PlayOrderLink>> LinksAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds, IReadOnlyCollection<Guid> playIds,
        CancellationToken ct)
    {
        var ids = orderIds.Distinct().ToArray();
        var playList = playIds.Distinct().ToArray();
        if (ids.Length == 0 && playList.Length == 0) return Task.FromResult(new List<PlayOrderLink>());
        return db.OrderLinks.Where(l => l.AccountId == accountId && (ids.Contains(l.ImportedOrderId) || playList.Contains(l.PlayId)))
            .OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id).ToListAsync(ct);
    }

    public Task<List<PlayOrderLink>> PlayLinksAsync(Guid accountId, Guid playId, CancellationToken ct) =>
        db.OrderLinks.Where(l => l.AccountId == accountId && l.PlayId == playId).OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id)
            .AsNoTracking().ToListAsync(ct);

    public Task<PlayOrderLink?> LinkAsync(Guid accountId, Guid playId, Guid linkId, CancellationToken ct) =>
        db.OrderLinks.SingleOrDefaultAsync(l => l.Id == linkId && l.AccountId == accountId && l.PlayId == playId, ct);

    public async Task<HashSet<Guid>> SuggestedElsewhereAsync(Guid accountId, Guid playId, IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        // A read: one query, no unit of work, and only Plays that could still take the order count.
        var ids = orderIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        return (await db.OrderLinks.Where(l => l.AccountId == accountId && l.PlayId != playId && ids.Contains(l.ImportedOrderId) &&
                l.State == OrderLinkState.Suggested && db.Plays.Any(p => p.Id == l.PlayId && p.AccountId == accountId &&
                    (p.Status == PlayStatus.Planned || p.Status == PlayStatus.Paused || p.Status == PlayStatus.Open)))
            .Select(l => l.ImportedOrderId).Distinct().ToListAsync(ct)).ToHashSet();
    }

    public async Task<HashSet<Guid>> LinkedOrderIdsAsync(Guid accountId, CancellationToken ct) =>
        (await db.OrderLinks.Where(l => l.AccountId == accountId && l.State == OrderLinkState.Linked).Select(l => l.ImportedOrderId)
            .Distinct().ToListAsync(ct)).ToHashSet();
    public async Task<List<ImportedFill>> FillsAsync(Guid accountId, IReadOnlyCollection<OrderKey> orders, CancellationToken ct)
    {
        var keys = orders.Where(k => k.AccountId == accountId).ToHashSet();
        if (keys.Count == 0) return [];
        // SQL narrows by external ID; the shared OrderKey rule below keeps only each key's own contract.
        var external = keys.Select(k => k.ExternalOrderId).Distinct(StringComparer.Ordinal).ToArray();
        var contracts = keys.Select(k => k.ContractId).Distinct(StringComparer.Ordinal).ToArray();
        var rows = await db.Fills.Where(f => f.AccountId == accountId && external.Contains(f.ExternalOrderId) && contracts.Contains(f.ContractId))
            .OrderBy(f => f.OccurredAtUtc).ThenBy(f => f.Id).AsNoTracking().ToListAsync(ct);
        return rows.Where(f => keys.Contains(OrderKey.Of(f))).ToList();
    }
    public Task<List<AllocatedFill>> AllocationsAsync(Guid accountId, IReadOnlyCollection<Guid> playIds, IReadOnlyCollection<Guid> fillIds,
        CancellationToken ct) => AllocatedFills.ForAsync(db, playIds, fillIds, ct, accountId);

    public Task<FillAllocation?> AllocationForUpdateAsync(Guid accountId, Guid fillId, CancellationToken ct)
    {
        unitOfWork.RequireActive();
        return db.FillAllocations.Include(a => a.Lines).SingleOrDefaultAsync(a => a.AccountId == accountId && a.FillId == fillId, ct);
    }

    public Task<ImportedFill?> FillAsync(Guid accountId, Guid fillId, CancellationToken ct) =>
        db.Fills.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fillId && f.AccountId == accountId, ct);

    public Task<List<ImportedFill>> FlipFillsAsync(Guid accountId, string contractId, string side, DateTimeOffset since, int limit, CancellationToken ct) =>
        db.Fills.Where(f => f.AccountId == accountId && f.ContractId == contractId && f.Side == side && f.PositionEffect == ExecutionFacts.Flip &&
                f.OccurredAtUtc >= since && !db.FillAllocations.Any(a => a.FillId == f.Id && a.Lines.Any()))
            .OrderByDescending(f => f.OccurredAtUtc).ThenBy(f => f.Id).Take(limit).AsNoTracking().ToListAsync(ct);

    public Task<List<Play>> AllocationCandidatesAsync(Guid accountId, string contractId, IReadOnlyCollection<Guid> named, CancellationToken ct)
    {
        var ids = named.Distinct().ToArray();
        return db.Plays.Where(p => p.AccountId == accountId && p.TradingMode == TradingMode.Real && (ids.Contains(p.Id) ||
                p.ContractId == contractId && p.InstrumentSource == InstrumentSource.Venue && p.Origin == PlayOrigin.Planned &&
                (p.Status == PlayStatus.Planned || p.Status == PlayStatus.Paused || p.Status == PlayStatus.Open || p.Status == PlayStatus.Unresolved)))
            .AsNoTracking().ToListAsync(ct);
    }

    public Task<List<Play>> PlaysAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToArray();
        return db.Plays.Where(p => p.AccountId == accountId && wanted.Contains(p.Id)).ToListAsync(ct);
    }

    public void Add(FillAllocation allocation) { unitOfWork.RequireActive(); db.FillAllocations.Add(allocation); }
    public void Add(FillAllocationRevision revision) { unitOfWork.RequireActive(); db.FillAllocationRevisions.Add(revision); }
    public void Add(PlayOrderLink link) { unitOfWork.RequireActive(); db.OrderLinks.Add(link); }
    public void Add(PlayStatusChange change) { unitOfWork.RequireActive(); db.StatusChanges.Add(change); }
    public async Task ForgetWatchesAsync(Guid playId, CancellationToken ct)
    {
        unitOfWork.RequireActive();
        var watches = db.Set<MonitorExitWatch>();
        watches.RemoveRange(await watches.Where(w => w.PlayId == playId).ToListAsync(ct));
    }

    public Task UpsertOrdersAsync(Account account, IReadOnlyList<VenueOrder> orders, DateTimeOffset observedAt, CancellationToken ct) => unitOfWork.ParticipateAsync(async () =>
    {
        if (account.TradingMode != "real") throw new ProblemException(409, "Paper accounts cannot import venue orders.");
        // External identity is (contract, venue order ID): the same venue ID on another contract is another order.
        var ids = orders.Select(o => o.OrderId).Distinct(StringComparer.Ordinal).ToList();
        var known = (await db.Orders.Where(o => o.AccountId == account.Id && ids.Contains(o.ExternalOrderId)).ToListAsync(ct))
            .ToDictionary(o => (o.ContractId, o.ExternalOrderId));
        // A known order never moves to another native market. Checked before any change, so a refusal saves nothing.
        foreach (var group in orders.GroupBy(o => (o.ContractId, o.OrderId)))
        {
            var natives = group.Select(o => o.VenueContractId)
                .Append(known.TryGetValue(group.Key, out var retained) ? retained.VenueContractId : null)
                .OfType<string>().Distinct(StringComparer.Ordinal).Count();
            if (natives > 1) throw new VenueReadException("The venue reported a known order on a different market. No order changes were saved.");
        }
        foreach (var order in orders)
        {
            if (!known.TryGetValue((order.ContractId, order.OrderId), out var row))
            {
                row = new ImportedOrder
                {
                    Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id,
                    ContractId = order.ContractId, ExternalOrderId = order.OrderId
                };
                db.Orders.Add(row);
                known[(order.ContractId, order.OrderId)] = row;
            }
            // An older status never replaces a newer one.
            else if (order.StatusAtUtc < row.StatusAtUtc) continue;
            // ContractId is identity and is never rewritten.
            // An observation without a native ID keeps the established one, so a later mismatch is still refused.
            row.VenueContractId = order.VenueContractId ?? row.VenueContractId;
            row.Side = order.Side;
            row.OrderType = order.OrderType;
            row.LimitPrice = order.LimitPrice;
            row.TriggerPrice = order.TriggerPrice;
            row.ReduceOnly = order.ReduceOnly;
            row.IsPositionTpsl = order.IsPositionTpsl;
            row.OriginalSize = order.OriginalSize;
            row.RemainingSize = order.RemainingSize;
            row.PlacedAtUtc = order.PlacedAtUtc.ToUniversalTime();
            row.Status = order.Status.ToString().ToLowerInvariant();
            row.VenueStatus = order.VenueStatus;
            row.StatusAtUtc = order.StatusAtUtc.ToUniversalTime();
            row.ObservedAtUtc = observedAt.ToUniversalTime();
        }
        // Phase 2, item 7: an order an earlier import left an execution of out is known to be missing executions, even
        // when the marker arrived before the order did.
        // The same identity rule as a marker arriving later, so a marker on another native market fails closed (Sol round 1).
        var omitted = await db.ExecutionOmissions.Where(m => m.AccountId == account.Id && m.OrderId != null && ids.Contains(m.OrderId))
            .Select(m => new { m.ContractId, m.VenueContractId, m.OrderId }).ToListAsync(ct);
        foreach (var row in known.Values)
            foreach (var marker in omitted)
                switch (ExecutionOmission.Names(marker.ContractId, marker.VenueContractId, marker.OrderId!, row))
                {
                    case null: throw new VenueReadException(WorkspaceStore.OmissionConflict);
                    case true: row.ExecutionsOmitted = true; break;
                }
        await unitOfWork.SaveChangesAsync(UnitOfWork.ExecutionConflict, ct);
        return true;
    }, ct);

    public Task SaveAsync(CancellationToken ct) => unitOfWork.SaveChangesAsync(UnitOfWork.ExecutionConflict, ct);

    public Task<T> WithAccountLockAsync<T>(Guid accountId, Func<Task<T>> action, CancellationToken ct) => unitOfWork.ParticipateAsync(async () =>
    {
        var accounts = await db.Accounts.FromSqlInterpolated(
            $"SELECT * FROM accounts WHERE \"Id\" = {accountId} AND \"OwnerId\" = {db.CurrentOwnerId} FOR UPDATE").ToListAsync(ct);
        var account = accounts.SingleOrDefault() ?? throw new ProblemException(404, "Account not found.");
        await db.Entry(account).ReloadAsync(ct);
        var key = "vessel-execution:" + accountId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
        var result = await action();
        return result;
    }, ct);

    public async Task<VenueRiskDto?> PositionAsync(Guid accountId, string contractId, string? venueContractId, int direction, CancellationToken ct)
    {
        // A disabled account's positions stay hidden, as in the workspace (Sol round 1 on #41).
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId && a.IsEnabled, ct)) return null;
        // Only a position in the Play's direction on its native market; two markets sharing the key are ambiguous.
        var rows = await db.Snapshots.Where(s => s.AccountId == accountId)
            .SelectMany(s => s.Positions.Where(p => p.ContractId == contractId && (direction > 0 ? p.SignedQuantity > 0 : p.SignedQuantity < 0) &&
                    (venueContractId == null || p.VenueContractId == venueContractId))
                .Select(p => new { s.ObservedAtUtc, p.SignedQuantity, p.EntryPrice, p.LiquidationPrice, p.MarginMode }))
            .Take(2).AsNoTracking().ToListAsync(ct);
        var row = rows.Count == 1 ? rows[0] : null;
        return row is null ? null : new(ExactDecimal.Format(row.SignedQuantity), ExactDecimal.Format(row.EntryPrice),
            ExactDecimal.Format(row.LiquidationPrice), row.MarginMode, row.ObservedAtUtc);
    }

    public async Task<VenueRiskDto?> RiskSinceAsync(Guid accountId, string contractId, string? venueContractId, int direction,
        DateTimeOffset since, DateTimeOffset? until, CancellationToken ct)
    {
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId && a.IsEnabled, ct)) return null;
        // The Play's own position: its direction, within its lifetime, on its native market. With no native identity, the
        // observations must agree on one market, or attribution stays unknown.
        var rows = await db.RiskObservations.Where(o => o.AccountId == accountId && o.ContractId == contractId && o.ObservedAtUtc >= since &&
                (until == null || o.ObservedAtUtc <= until) && (direction > 0 ? o.SignedQuantity > 0 : o.SignedQuantity < 0) &&
                (venueContractId == null || o.VenueContractId == venueContractId))
            .OrderBy(o => o.ObservedAtUtc).ThenBy(o => o.Id).Take(50).AsNoTracking().ToListAsync(ct);
        if (venueContractId is null && rows.Select(o => o.VenueContractId).Distinct().Count() > 1) return null;
        var row = rows.FirstOrDefault();
        return row is null ? null : new(ExactDecimal.Format(row.SignedQuantity), ExactDecimal.Format(row.EntryPrice),
            ExactDecimal.Format(row.LiquidationPrice), row.MarginMode, row.ObservedAtUtc);
    }

    public async Task<FundingEvidence?> FundingEvidenceAsync(FundingEvidenceRequest request, CancellationToken ct)
    {
        // Phase 2, item 9b: one snapshot, so the ledger read belongs to the facts at its revision. An ambient snapshot (the Play
        // bundle's) is reused; the revision read at both ends still shows a change under a weaker ambient isolation.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var revision = await ActivityStore.RevisionAsync(db, db.CurrentOwnerId, ct);
        var (account, native, contracts) = (request.AccountId, request.VenueContractId, request.ContractIds.ToArray());
        var links = await db.OrderLinks.Where(l => l.AccountId == account && l.PlayId == request.PlayId && l.State == OrderLinkState.Linked)
            .Join(db.Orders, l => l.ImportedOrderId, o => o.Id, (l, o) => new { l.Role, Order = o }).AsNoTracking().ToListAsync(ct);
        // The latest complete record covers everything before its end. Only with one can evidence before the window matter
        // (the complete-history proof); otherwise rows ending before the window are left out, never ones that could matter.
        var complete = await db.FillCoverage.Where(r => r.AccountId == account && r.Complete && r.CoveredToUtc != null)
            .OrderByDescending(r => r.CoveredToUtc).Select(r => r.CoveredToUtc).FirstOrDefaultAsync(ct);
        var spansFrom = complete is { } until && until < request.FromUtc ? until : request.FromUtc;
        DateTimeOffset? rowsFrom = complete is null ? request.FromUtc : null;
        // A span ending before that cannot join the window or extend the complete record (a record ends at its last read).
        var covered = await db.FillCoverage.Where(r => r.AccountId == account && !r.Complete && r.CoveredFromUtc != null &&
                r.CoveredToUtc != null && r.LastObservedAtUtc >= spansFrom && r.CoveredToUtc >= spansFrom)
            .Select(r => new { r.CoveredFromUtc, r.CoveredToUtc }).AsNoTracking().ToListAsync(ct);
        var gaps = await db.HistoryGaps.Where(g => g.AccountId == account && g.ClosedAtUtc == null &&
                (rowsFrom == null || g.ToUtc == null || g.ToUtc >= rowsFrom))
            .Select(g => new { g.FromUtc, g.ToUtc }).AsNoTracking().ToListAsync(ct);
        var forced = await db.ForcedEvents.Where(e => e.AccountId == account && (contracts.Contains(e.ContractId) ||
                native != null && e.VenueContractId == native) && (rowsFrom == null || e.OccurredAtUtc >= rowsFrom))
            .Select(e => new { e.Id, e.ContractId, e.VenueContractId, e.OccurredAtUtc }).AsNoTracking().ToListAsync(ct);
        // Orders that may be missing executions (the shared rule's own preconditions), with their retained quantity; the
        // shared rule decides. A terminal order's executions all precede its last observation; any other's may still come.
        string[] terminal = ["filled", "canceled", "rejected"];
        var candidates = await db.Orders.Where(o => o.AccountId == account && (contracts.Contains(o.ContractId) ||
                native != null && o.VenueContractId == native) && (o.ExecutionsOmitted || o.Status == "filled" && !o.IsPositionTpsl) &&
                (rowsFrom == null || !terminal.Contains(o.Status) || o.ObservedAtUtc >= rowsFrom || o.StatusAtUtc >= rowsFrom))
            .Select(o => new
            {
                Order = o,
                Retained = db.Fills.Where(f => f.AccountId == o.AccountId && f.ContractId == o.ContractId && f.ExternalOrderId == o.ExternalOrderId)
                    .Sum(f => (decimal?)f.Quantity) ?? 0m
            }).AsNoTracking().ToListAsync(ct);
        // Phase 2, item 11: the instrument's fills with a current allocation, in this snapshot. Allocations move no Activity
        // revision (Activity does not read them), so the snapshot fences them; scoped like the forced events.
        var allocated = await db.FillAllocations.Where(a => a.AccountId == account && a.Lines.Any())
            .Join(db.Fills, a => a.FillId, f => f.Id, (a, f) => f)
            .Where(f => (contracts.Contains(f.ContractId) || native != null && f.VenueContractId == native) &&
                (rowsFrom == null || f.OccurredAtUtc >= rowsFrom))
            .Select(f => new { f.ContractId, f.VenueContractId, f.OccurredAtUtc }).AsNoTracking().ToListAsync(ct);
        var after = await ActivityStore.RevisionAsync(db, db.CurrentOwnerId, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(revision, after, links.Select(l => (l.Role, l.Order)).ToList(),
            [.. covered.Select(r => new LedgerSpan(r.CoveredFromUtc, r.CoveredToUtc)), .. complete is { } end ? [new LedgerSpan(null, end)] : Array.Empty<LedgerSpan>()],
            gaps.Select(g => new LedgerSpan(g.FromUtc, g.ToUtc)).ToList(),
            forced.Select(e => new Vessel.Application.Activity.ActivityOmission(e.Id, account, e.ContractId, e.VenueContractId, e.OccurredAtUtc, null, null)).ToList(),
            candidates.Where(c => ExecutionIntegrity.MissingExecutions(c.Order, c.Retained)).Select(c => new MissingExecutionOrder(c.Order.ContractId,
                c.Order.VenueContractId, c.Order.PlacedAtUtc, terminal.Contains(c.Order.Status)
                    ? c.Order.StatusAtUtc > c.Order.ObservedAtUtc ? c.Order.StatusAtUtc : c.Order.ObservedAtUtc : null)).ToList())
        {
            Allocated = allocated.Select(f => new AllocatedExecution(f.ContractId, f.VenueContractId, f.OccurredAtUtc)).ToList(),
        };
    }
}
