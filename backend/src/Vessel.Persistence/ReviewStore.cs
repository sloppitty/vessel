using System.Data;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Plays;

namespace Vessel.Persistence;

public sealed class ReviewStore(VesselDbContext db, ISizingStore sizing) : IReviewStore
{
    public async Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct)
    {
        // One database snapshot for links, outcomes, membership and coverage during concurrent sync/management.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var portfolios = await db.Portfolios.AsNoTracking().ToListAsync(ct);
        var accounts = await db.Accounts.Where(a => a.IsEnabled).AsNoTracking().ToListAsync(ct);
        var ids = accounts.Where(a => (query.AccountId is null || a.Id == query.AccountId) &&
            (query.Portfolio == "all" || query.Portfolio == "unassigned" && a.PortfolioId is null ||
                a.PortfolioId?.ToString() == query.Portfolio)).Select(a => a.Id).ToList();
        var closed = await sizing.ClosedPlaysAsync(ct);
        // Known filled orders with missing executions come with the closed Plays, as sizing reads them.
        var incomplete = closed.Where(p => p.IncompleteOrders is { Count: > 0 }).Select(p => p.Play.Id).ToHashSet();
        var open = await db.Plays.CountAsync(p => ids.Contains(p.AccountId) && p.Status == PlayStatus.Open &&
            (query.Instrument == null || p.ContractId == query.Instrument), ct);
        var fills = db.Fills.Where(f => ids.Contains(f.AccountId) && f.OccurredAtUtc <= asOf &&
            (query.Instrument == null || f.ContractId == query.Instrument));
        var first = await fills.Select(f => (DateTimeOffset?)f.OccurredAtUtc).MinAsync(ct);
        var from = ReviewService.From(query.Period, asOf);
        var unassigned = await fills.CountAsync(f => (from == null || f.OccurredAtUtc > from) &&
            !db.OrderLinks.Any(l => l.AccountId == f.AccountId && l.State == OrderLinkState.Linked &&
                db.Orders.Any(o => o.OwnerId == l.OwnerId && o.AccountId == l.AccountId && o.Id == l.ImportedOrderId &&
                    o.ContractId == f.ContractId && o.ExternalOrderId == f.ExternalOrderId) &&
                db.Plays.Any(p => p.Id == l.PlayId && p.ContractId == f.ContractId)), ct);
        // Unresolved Plays are never scored; Review counts and lists them with their latest declaration (1.3a).
        var enabled = accounts.Select(a => a.Id).ToList();
        var unresolvedPlays = await db.Plays.Where(p => p.Status == PlayStatus.Unresolved && enabled.Contains(p.AccountId))
            .AsNoTracking().ToListAsync(ct);
        var unresolvedIds = unresolvedPlays.Select(p => p.Id).ToList();
        var declarations = (await db.StatusChanges.Where(c => unresolvedIds.Contains(c.PlayId) && c.To == PlayStatus.Unresolved)
            .AsNoTracking().ToListAsync(ct)).ToLookup(c => c.PlayId);
        // Each Unresolved Play's earliest linked execution, by the shared OrderKey rule, for the post-hoc badge.
        var firstExecution = new Dictionary<Guid, DateTimeOffset?>();
        foreach (var id in unresolvedIds)
        {
            var orders = db.Orders.Where(o => db.OrderLinks.Any(l => l.PlayId == id && l.State == OrderLinkState.Linked &&
                l.AccountId == o.AccountId && l.ImportedOrderId == o.Id));
            firstExecution[id] = await db.FillsOf(orders).MinAsync(f => (DateTimeOffset?)f.OccurredAtUtc, ct);
        }
        // Cancelled Plays join the process view (1.4b); they are never scored. Only plan revision 1 is read, for its pre-flight.
        var cancelledPlays = await db.Plays.Where(p => p.Status == PlayStatus.Cancelled && enabled.Contains(p.AccountId))
            .AsNoTracking().ToListAsync(ct);
        var endedIds = unresolvedIds.Concat(cancelledPlays.Select(p => p.Id)).ToList();
        var firstRevisions = (await db.PlanRevisions.Where(r => endedIds.Contains(r.PlayId) && r.Number == 1).AsNoTracking().ToListAsync(ct))
            .ToLookup(r => r.PlayId);
        await transaction.CommitAsync(ct);
        return new(portfolios, accounts, closed, open, unassigned, first, incomplete)
        {
            UnresolvedPlays = unresolvedPlays.Select(p => new UnresolvedPlayFacts(p, PlayUnresolved.Latest(declarations[p.Id], p.EndedAtUtc))
            {
                Revisions = firstRevisions[p.Id].ToList(), FirstExecutionAtUtc = firstExecution[p.Id],
            }).ToList(),
            CancelledPlays = cancelledPlays.Select(p => new EndedPlayFacts(p, firstRevisions[p.Id].ToList())).ToList(),
        };
    }

    public async Task<IReadOnlyList<string>> MistakeTagsAsync(int limit, CancellationToken ct)
    {
        // The owner's own vocabulary: every Play of theirs, newest review first; grouping and spelling follow ReviewService.
        var rows = await db.Plays.Where(p => p.ReviewMistakeTag != null && p.ReviewMistakeTag.Trim() != "")
            .OrderByDescending(p => p.ReviewedAtUtc).ThenByDescending(p => p.UpdatedAtUtc).ThenBy(p => p.Id)
            .Select(p => p.ReviewMistakeTag!).Take(5000).AsNoTracking().ToListAsync(ct);
        return rows.Select(t => t.Trim()).DistinctBy(ReviewService.TagKey).Take(limit).ToList();
    }
}
