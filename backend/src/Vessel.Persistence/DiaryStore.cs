using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

public sealed class DiaryStore(VesselDbContext db, UnitOfWork unitOfWork) : IDiaryStore
{
    public Task<T> WithRequestLockAsync<T>(Guid requestId, Func<Task<T>> action, CancellationToken ct) => unitOfWork.ParticipateAsync(async () =>
    {
        var key = $"vessel-diary-request:{db.CurrentOwnerId}:{requestId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
        var result = await action();
        return result;
    }, ct);
    public async Task<IReadOnlySet<Guid>> AllocatedFillIdsAsync(IReadOnlyCollection<Guid> fillIds, CancellationToken ct) =>
        await AllocatedFills.AllocatedIdsAsync(db, fillIds, ct);
    public Task<DiaryRequest?> RequestAsync(Guid requestId, CancellationToken ct) =>
        db.DiaryRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, ct);
    public Task<List<ManualExecution>> ManualAsync(Guid playId, CancellationToken ct) =>
        db.ManualExecutions.Where(r => r.PlayId == playId).OrderBy(r => r.OccurredAtUtc).ThenBy(r => r.Id).ToListAsync(ct);
    public Task<List<ManualExecutionHistory>> ManualHistoryAsync(Guid playId, CancellationToken ct) =>
        db.ManualExecutionHistory.Where(r => r.PlayId == playId).AsNoTracking().OrderBy(r => r.OccurredAtUtc).ThenBy(r => r.Id).ToListAsync(ct);
    public async Task<ActivityFacts> ActivityAsync(Guid accountId, CancellationToken ct)
    {
        var accounts = await db.Accounts.Where(a => a.Id == accountId && a.IsEnabled).AsNoTracking().ToListAsync(ct);
        var fills = await db.Fills.Where(f => f.AccountId == accountId).AsNoTracking().ToListAsync(ct);
        var snapshots = await db.Snapshots.Where(s => s.AccountId == accountId).Include(s => s.Positions).AsNoTracking().ToListAsync(ct);
        var funding = await db.FundingPayments.Where(p => p.AccountId == accountId).AsNoTracking().ToListAsync(ct);
        var links = await db.LinkedOrders(db.OrderLinks.Where(l => l.AccountId == accountId && l.State == OrderLinkState.Linked))
            .Select(x => new { x.Order.AccountId, x.Order.ContractId, x.Order.ExternalOrderId, x.Link.PlayId }).AsNoTracking().ToListAsync(ct);
        var byOrder = links.ToDictionary(l => new OrderKey(l.AccountId, l.ContractId, l.ExternalOrderId), l => l.PlayId);
        // Phase 2, item 7b (Sol round 1): the same omission markers Activity reads, so a preview never rebuilds an operation
        // across a gap. Forced events need no separate read here: each one also leaves a marker.
        var omissions = await ActivityStore.OmissionsAsync(db, [accountId], ct);
        return new(accounts, fills, snapshots, fills.Where(f => byOrder.ContainsKey(OrderKey.Of(f))).ToDictionary(f => f.Id, f => byOrder[OrderKey.Of(f)]))
        { FundingPayments = funding, Omissions = omissions };
    }
    public Task<List<ImportedOrder>> OrdersAsync(Guid accountId, CancellationToken ct) =>
        db.Orders.Where(o => o.AccountId == accountId).AsNoTracking().ToListAsync(ct);
    public Task<List<PlayOrderLink>> LinksAsync(Guid accountId, CancellationToken ct) =>
        db.OrderLinks.Where(l => l.AccountId == accountId).AsNoTracking().ToListAsync(ct);
    public async Task<List<ImportedOrder>> OrdersAsync(Guid accountId, IReadOnlyCollection<OrderKey> orders, CancellationToken ct)
    {
        var keys = orders.Where(k => k.AccountId == accountId).ToHashSet();
        var ids = keys.Select(k => k.ExternalOrderId).Distinct(StringComparer.Ordinal).ToArray();
        // SQL narrows by external ID; the shared OrderKey rule keeps only each key's own contract.
        return (await db.Orders.Where(o => o.AccountId == accountId && ids.Contains(o.ExternalOrderId)).AsNoTracking().ToListAsync(ct))
            .Where(o => keys.Contains(OrderKey.Of(o))).ToList();
    }
    public Task<List<PlayOrderLink>> LinksAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var ids = orderIds.ToArray();
        return db.OrderLinks.Where(l => l.AccountId == accountId && ids.Contains(l.ImportedOrderId)).AsNoTracking().ToListAsync(ct);
    }
    public void Add(ManualExecution execution) { unitOfWork.RequireActive(); db.ManualExecutions.Add(execution); }
    public void Add(ManualExecutionHistory history) { unitOfWork.RequireActive(); db.ManualExecutionHistory.Add(history); }
    public void Add(DiaryRequest request) { unitOfWork.RequireActive(); db.DiaryRequests.Add(request); }
    public void Add(PlayOrderLink link) { unitOfWork.RequireActive(); db.OrderLinks.Add(link); }
}
