using Vessel.Application.Common;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Monitoring;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;

namespace Vessel.Persistence;

public sealed class MonitorDiscrepancyStore(VesselDbContext db, UnitOfWork work) : IMonitorDiscrepancyStore
{
    /// <summary>
    /// Safety bound on one instrument's fills after its earliest open entry. Exceeding it leaves only that instrument's
    /// unlinked-fill check unknown; it never disables the account's other checks.
    /// </summary>
    private const int MaxCandidateFills = 10000;
    public Task<Account?> AccountAsync(Guid accountId, CancellationToken ct) =>
        db.Accounts.SingleOrDefaultAsync(x => x.Id == accountId, ct);
    public Task<Play?> PlayAsync(Guid playId, CancellationToken ct) => db.Plays.SingleOrDefaultAsync(x => x.Id == playId, ct);
    public async Task<IReadOnlyList<PlayDiscrepancy>> RecordsAsync(Guid accountId, CancellationToken ct) =>
        await db.Set<PlayDiscrepancy>().Where(x => x.AccountId == accountId).OrderBy(x => x.Id).ToListAsync(ct);
    public void Add(PlayDiscrepancy record) { work.RequireActive(); db.Add(record); }
    public async Task LockAsync(Guid accountId, CancellationToken ct)
    {
        work.RequireActive();
        var accounts = await db.Accounts.FromSqlInterpolated($"SELECT * FROM accounts WHERE \"Id\" = {accountId} AND \"OwnerId\" = {db.CurrentOwnerId} FOR UPDATE").ToListAsync(ct);
        if (accounts.Count != 1) throw new ProblemException(404, "Account not found.");
    }
    public async Task<MonitorDiscrepancyFacts> LoadAsync(Guid accountId, DateTimeOffset recentSince, IReadOnlyCollection<OrderKey> restingOrders,
        CancellationToken ct)
    {
        work.RequireActive();
        // Only the subjects the checks can still report on, never the account's lifetime history. Callers hold the
        // account row lock, which every Play writer takes first (PlayStore.WithPlayLockAsync, ExecutionStore), so the
        // Play rows need no lock of their own here.
        var watches = db.Set<MonitorExitWatch>();
        // SQL narrows by external ID; the OrderKey rule keeps only the account's orders on the reported contract, so an
        // order with the same venue ID on another contract never admits its terminal Play.
        var restingKeys = restingOrders.Where(x => x.AccountId == accountId).ToHashSet();
        var restingExternal = restingKeys.Select(x => x.ExternalOrderId).Distinct(StringComparer.Ordinal).ToArray();
        Guid[] resting = restingExternal.Length == 0 ? [] : (await db.Orders.AsNoTracking()
                .Where(o => o.AccountId == accountId && restingExternal.Contains(o.ExternalOrderId))
                .Select(o => new { o.Id, o.AccountId, o.ContractId, o.ExternalOrderId }).ToListAsync(ct))
            .Where(o => restingKeys.Contains(new OrderKey(o.AccountId, o.ContractId, o.ExternalOrderId))).Select(o => o.Id).ToArray();
        var records = db.Set<PlayDiscrepancy>();
        var plays = await db.Plays.Where(p => p.AccountId == accountId && p.InstrumentSource == InstrumentSource.Venue &&
                p.Origin == PlayOrigin.Planned && p.Status != PlayStatus.Draft &&
                (p.Status == PlayStatus.Planned || p.Status == PlayStatus.Paused || p.Status == PlayStatus.Open ||
                    p.EndedAtUtc >= recentSince ||
                    // Same terminal watch as MonitorStore: a linked order whose current watch has not ended. Its run
                    // records lifetime expiry before evaluation; positive resting evidence still admits it below.
                    db.OrderLinks.Any(l => l.PlayId == p.Id && l.State == OrderLinkState.Linked && !watches.Any(e =>
                        e.LinkId == l.Id && e.EndedAtUtc != null && e.LinkRevisionAtUtc >= l.UpdatedAtUtc && (p.EndedAtUtc == null || p.EndedAtUtc <= e.StartedAtUtc))) ||
                    // Evaluation membership, independent of scheduling: tracked records, even unverifiable ones.
                    records.Any(d => d.AccountId == accountId && d.PlayId == p.Id && (d.State == DiscrepancyLifecycle.Candidate ||
                        d.State == DiscrepancyLifecycle.Open || d.PendingFingerprint != null ||
                        d.PublishedTransition == DiscrepancyLifecycle.Opened || d.PublishedTransition == DiscrepancyLifecycle.Unverifiable)) ||
                    db.OrderLinks.Any(l => l.PlayId == p.Id && l.State == OrderLinkState.Linked && resting.Contains(l.ImportedOrderId))))
            .OrderBy(x => x.Id).ToListAsync(ct);
        var ids = plays.Select(x => x.Id).ToArray();
        var links = await db.OrderLinks.Where(x => x.AccountId == accountId && ids.Contains(x.PlayId))
            .OrderBy(x => x.Id).ToListAsync(ct);
        var linked = links.Where(x => x.State == OrderLinkState.Linked).ToArray();
        var orderIds = linked.Select(x => x.ImportedOrderId).Distinct().ToArray();
        var linkedOrders = db.Orders.Where(x => x.AccountId == accountId && orderIds.Contains(x.Id));
        var orders = await linkedOrders.ToListAsync(ct);
        var ordersById = orders.ToDictionary(x => x.Id);
        var fills = await db.FillsOf(linkedOrders).OrderBy(x => x.Id).AsNoTracking().ToListAsync(ct);
        // Unlinked-fill candidates: fills on an Open Play's instrument after the earliest linked entry fill there.
        var open = plays.Where(x => x.Status == PlayStatus.Open && x.ContractId is not null).ToDictionary(x => x.Id, x => x.ContractId!);
        var entries = linked.Where(x => x.Role == OrderLinkRole.Entry && open.ContainsKey(x.PlayId) && ordersById.ContainsKey(x.ImportedOrderId))
            .ToLookup(x => OrderKey.Of(ordersById[x.ImportedOrderId]), x => open[x.PlayId]);
        var earliest = fills.SelectMany(f => entries[OrderKey.Of(f)].Select(contract => (Contract: contract, f.OccurredAtUtc)))
            .GroupBy(x => x.Contract, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Min(y => y.OccurredAtUtc), StringComparer.Ordinal);
        var loaded = fills.Select(x => x.Id).ToHashSet();
        var truncated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (contract, since) in earliest.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var candidates = await db.Fills.Where(x => x.AccountId == accountId && x.ContractId == contract && x.OccurredAtUtc > since)
                .OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).Take(MaxCandidateFills + 1).AsNoTracking().ToListAsync(ct);
            if (candidates.Count > MaxCandidateFills) truncated.Add(contract);
            fills.AddRange(candidates.Where(x => loaded.Add(x.Id)));
        }
        // Assignment is account-wide (retrospective Plays included), but only the loaded fills' orders matter.
        // SQL narrows by external ID; the OrderKey rule keeps only links whose order has a loaded fill's own contract.
        var fillKeys = fills.Select(OrderKey.Of).ToHashSet();
        var fillOrders = fillKeys.Select(x => x.ExternalOrderId).Distinct(StringComparer.Ordinal).ToArray();
        // Two single-table reads, orders then links, rather than a link-order join: with long assignment history a join
        // plan can degrade badly, while each read here stays an indexed lookup.
        var assignedOrders = fillOrders.Length == 0 ? [] : (await db.Orders.AsNoTracking()
                .Where(o => o.AccountId == accountId && fillOrders.Contains(o.ExternalOrderId)).ToListAsync(ct))
            .Where(o => fillKeys.Contains(OrderKey.Of(o))).ToDictionary(o => o.Id);
        var assignedIds = assignedOrders.Keys.ToArray();
        var assigned = assignedIds.Length == 0 ? [] : (await db.OrderLinks.AsNoTracking()
                .Where(x => x.AccountId == accountId && (x.State == OrderLinkState.Linked || x.State == OrderLinkState.Suggested) &&
                    assignedIds.Contains(x.ImportedOrderId))
                .OrderBy(x => x.Id).ToListAsync(ct))
            .Select(x => new LinkedOrderRow { Link = x, Order = assignedOrders[x.ImportedOrderId] }).ToList();
        // The checks resolve each assignment's order by internal ID, so its order comes with the facts.
        orders.AddRange(assigned.Select(x => x.Order).Where(x => !ordersById.ContainsKey(x.Id)).DistinctBy(x => x.Id));
        return new(plays, links, orders, fills, true, assigned.Select(x => x.Link).ToList(), true, truncated);
    }
}
