using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Plays;

namespace Vessel.Persistence;

/// <summary>
/// Reads owner-confirmed fill allocations (Phase 2, item 11) for the allocation read rule: each with its original fill and
/// the fill's retained order, found by the shared <see cref="OrderKey"/> rule, and whether that order is known to miss
/// executions. Every Play reader that applies the rule loads them here.
/// </summary>
internal static class AllocatedFills
{
    /// <summary>
    /// Allocations with a line naming one of <paramref name="playIds"/>, and those on one of <paramref name="fillIds"/> in any
    /// state (a removed one has no lines). Read-only, owner-filtered like every query, optionally on one account.
    /// </summary>
    public static async Task<List<AllocatedFill>> ForAsync(VesselDbContext db, IReadOnlyCollection<Guid> playIds, IReadOnlyCollection<Guid> fillIds,
        CancellationToken ct, Guid? accountId = null)
    {
        var plays = playIds.Distinct().ToArray();
        var fills = fillIds.Distinct().ToArray();
        if (plays.Length == 0 && fills.Length == 0) return [];
        var query = db.FillAllocations.Where(a => fills.Contains(a.FillId) || a.Lines.Any(l => plays.Contains(l.PlayId)));
        if (accountId is { } account) query = query.Where(a => a.AccountId == account);
        var heads = await query.Include(a => a.Lines).AsNoTracking().ToListAsync(ct);
        if (heads.Count == 0) return [];
        var ids = heads.Select(h => h.FillId).ToArray();
        var allocated = await db.Fills.Where(f => ids.Contains(f.Id)).AsNoTracking().ToDictionaryAsync(f => f.Id, ct);
        var keys = allocated.Values.Select(OrderKey.Of).ToHashSet();
        var accounts = keys.Select(k => k.AccountId).Distinct().ToArray();
        var external = keys.Select(k => k.ExternalOrderId).Distinct(StringComparer.Ordinal).ToArray();
        // SQL narrows by account and external ID; the OrderKey rule keeps only each fill's own contract.
        var orders = (await db.Orders.Where(o => accounts.Contains(o.AccountId) && external.Contains(o.ExternalOrderId)).AsNoTracking().ToListAsync(ct))
            .Where(o => keys.Contains(OrderKey.Of(o))).ToDictionary(OrderKey.Of);
        var orderIds = orders.Values.Select(o => o.Id).ToArray();
        var orderFills = orderIds.Length == 0 ? Enumerable.Empty<Vessel.Domain.Workspace.ImportedFill>().ToLookup(OrderKey.Of)
            : (await db.FillsOf(db.Orders.Where(o => orderIds.Contains(o.Id))).AsNoTracking().ToListAsync(ct)).ToLookup(OrderKey.Of);
        // Which of those orders' fills have a current allocation, so a split order's unallocated executions are known.
        var allocatedOfOrders = await AllocatedIdsAsync(db, orderFills.SelectMany(g => g).Select(f => f.Id).ToList(), ct);
        return heads.Where(h => allocated.ContainsKey(h.FillId)).Select(h =>
        {
            var fill = allocated[h.FillId];
            var order = orders.GetValueOrDefault(OrderKey.Of(fill));
            var siblings = orderFills[OrderKey.Of(fill)];
            return new AllocatedFill(h, fill, order, order is not null && ExecutionIntegrity.MissingExecutions(order, orderFills[OrderKey.Of(order)]))
            {
                UnallocatedOrderFills = siblings.Count(f => !allocatedOfOrders.Contains(f.Id)),
            };
        }).ToList();
    }

    /// <summary>The internal IDs of fills with a current allocation (one with lines) among <paramref name="fillIds"/>.</summary>
    public static async Task<HashSet<Guid>> AllocatedIdsAsync(VesselDbContext db, IReadOnlyCollection<Guid> fillIds, CancellationToken ct)
    {
        var ids = fillIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        return (await db.FillAllocations.Where(a => ids.Contains(a.FillId) && a.Lines.Any()).Select(a => a.FillId).ToListAsync(ct)).ToHashSet();
    }
}
