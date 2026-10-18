using System.Data;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Domain.Plays;

namespace Vessel.Persistence;

public sealed class ActivityStore(VesselDbContext db) : IActivityStore
{
    public async Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null)
    {
        if (accountIds is { Count: 0 }) return new([], [], [], new Dictionary<Guid, Guid>());
        // A shared aggregate reader supplies its own repeatable-read snapshot.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        if (accountId is { } id && !await db.Accounts.AnyAsync(a => a.Id == id, ct))
            throw new ProblemException(404, "Account not found.");
        var query = db.Accounts.Where(a => a.IsEnabled && (accountId == null || a.Id == accountId));
        if (accountIds is not null)
        {
            var selectedIds = accountIds.ToArray();
            query = query.Where(a => selectedIds.Contains(a.Id));
        }
        var accounts = await query.AsNoTracking().ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToArray();
        var fills = await db.Fills.Where(f => ids.Contains(f.AccountId)).AsNoTracking().ToListAsync(ct);
        var snapshots = await db.Snapshots.Where(s => ids.Contains(s.AccountId)).Include(s => s.Positions).AsNoTracking().ToListAsync(ct);
        var funding = await db.FundingPayments.Where(p => ids.Contains(p.AccountId)).AsNoTracking().ToListAsync(ct);
        var fundingStates = await db.FundingStates.Where(s => ids.Contains(s.AccountId)).AsNoTracking().ToListAsync(ct);
        var links = await db.LinkedOrders(db.OrderLinks.Where(l => ids.Contains(l.AccountId) && l.State == OrderLinkState.Linked))
            .Select(x => new { x.Order.AccountId, x.Order.ContractId, x.Order.ExternalOrderId, x.Link.PlayId }).AsNoTracking().ToListAsync(ct);
        // A fill belongs to the linked order with its account, contract and external ID.
        var byOrder = links.ToDictionary(l => new OrderKey(l.AccountId, l.ContractId, l.ExternalOrderId), l => l.PlayId);
        var playIds = fills.Where(f => byOrder.ContainsKey(OrderKey.Of(f)))
            .ToDictionary(f => f.Id, f => byOrder[OrderKey.Of(f)]);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(accounts, fills, snapshots, playIds) { FundingPayments = funding, FundingStates = fundingStates };
    }
}
