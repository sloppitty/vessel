using System.Data;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Domain.Plays;
using Vessel.Domain.Common;

namespace Vessel.Persistence;

public sealed class ActivityStore(VesselDbContext db) : IActivityStore
{
    public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) =>
        ReadAsync(accountId, TradingMode.Real, ct, accountIds);

    public async Task<ActivityFacts> ReadAsync(Guid? accountId, string tradingMode, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null)
    {
        tradingMode = TradingMode.Validate(tradingMode);
        if (accountIds is { Count: 0 }) return new([], [], [], new Dictionary<Guid, Guid>());
        // A shared aggregate reader supplies its own repeatable-read snapshot.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        if (accountId is { } id && !await db.Accounts.AnyAsync(a => a.Id == id && a.TradingMode == tradingMode, ct))
            throw new ProblemException(404, "Account not found.");
        var query = db.Accounts.Where(a => a.TradingMode == tradingMode && a.IsEnabled && (accountId == null || a.Id == accountId));
        if (accountIds is not null)
        {
            var selectedIds = accountIds.ToArray();
            query = query.Where(a => selectedIds.Contains(a.Id));
        }
        var accounts = await query.AsNoTracking().ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToArray();
        if (tradingMode == TradingMode.Paper)
        {
            var plays = await db.Plays.Where(p => p.TradingMode == tradingMode && ids.Contains(p.AccountId))
                .AsNoTracking().ToListAsync(ct);
            var paper = await SizingStore.PaperFactsAsync(db, plays, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return new(accounts, [], [], new Dictionary<Guid, Guid>()) { PaperPlays = paper };
        }
        var fills = await db.Fills.Where(f => ids.Contains(f.AccountId)).AsNoTracking().ToListAsync(ct);
        var snapshots = await db.Snapshots.Where(s => ids.Contains(s.AccountId)).Include(s => s.Positions).AsNoTracking().ToListAsync(ct);
        var funding = await db.FundingPayments.Where(p => ids.Contains(p.AccountId)).AsNoTracking().ToListAsync(ct);
        var fundingStates = await db.FundingStates.Where(s => ids.Contains(s.AccountId)).AsNoTracking().ToListAsync(ct);
        var leverage = await db.LeverageObservations.Where(o => ids.Contains(o.AccountId)).AsNoTracking().ToListAsync(ct);
        // Phase 2, item 7b: what imports left out, so Activity never anchors or walks across it.
        var omissions = await OmissionsAsync(db, ids, ct);
        // Only the forced events Activity shows are read; the rest are counted in SQL when there are more (Sol round 1).
        var forced = await db.ForcedEvents.Where(e => ids.Contains(e.AccountId)).OrderByDescending(e => e.OccurredAtUtc).ThenBy(e => e.Id)
            .Take(ActivityService.MaxForcedEventRows + 1).AsNoTracking().ToListAsync(ct);
        Dictionary<Guid, int>? forcedCounts = null;
        if (forced.Count > ActivityService.MaxForcedEventRows)
        {
            forced.RemoveAt(forced.Count - 1);
            forcedCounts = await db.ForcedEvents.Where(e => ids.Contains(e.AccountId)).GroupBy(e => e.AccountId)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        }
        var links = await db.LinkedOrders(db.OrderLinks.Where(l => ids.Contains(l.AccountId) && l.State == OrderLinkState.Linked &&
            db.Plays.Any(p => p.Id == l.PlayId && p.TradingMode == TradingMode.Real)))
            .Select(x => new { x.Order.AccountId, x.Order.ContractId, x.Order.ExternalOrderId, x.Link.PlayId }).AsNoTracking().ToListAsync(ct);
        // A fill belongs to the linked order with its account, contract and external ID.
        var byOrder = links.ToDictionary(l => new OrderKey(l.AccountId, l.ContractId, l.ExternalOrderId), l => l.PlayId);
        var playIds = fills.Where(f => byOrder.ContainsKey(OrderKey.Of(f)))
            .ToDictionary(f => f.Id, f => byOrder[OrderKey.Of(f)]);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(accounts, fills, snapshots, playIds)
        {
            FundingPayments = funding, FundingStates = fundingStates, LeverageObservations = leverage, Omissions = omissions,
            ForcedEvents = forced, ForcedEventCounts = forcedCounts
        };
    }

    /// <summary>
    /// Phase 2, item 7b: every omission marker of these accounts, with only the columns the walk reads (time or window and
    /// market). Activity and the retrospective preview share this read, so both aggregate the same gaps.
    /// </summary>
    internal static Task<List<ActivityOmission>> OmissionsAsync(VesselDbContext db, Guid[] accountIds, CancellationToken ct) =>
        db.ExecutionOmissions.Where(m => accountIds.Contains(m.AccountId))
            .Select(m => new ActivityOmission(m.Id, m.AccountId, m.ContractId, m.VenueContractId, m.OccurredAtUtc, m.UncertainFromUtc, m.UncertainToUtc))
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct)
    {
        if (playIds.Count == 0) return new Dictionary<Guid, string>();
        var ids = playIds.ToArray();
        // The owner filter leaves other owners' Plays out. Only the two columns are read, never plans, drawings or reviews.
        return await db.Plays.Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Title })
            .ToDictionaryAsync(p => p.Id, p => p.Title, ct);
    }
}
