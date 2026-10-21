using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
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
}
