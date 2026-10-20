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
        await transaction.CommitAsync(ct);
        return new(portfolios, accounts, closed, open, unassigned, first, incomplete)
        {
            UnresolvedPlays = unresolvedPlays.Select(p => new UnresolvedPlayFacts(p, PlayUnresolved.Latest(declarations[p.Id], p.EndedAtUtc))).ToList(),
        };
    }
}
