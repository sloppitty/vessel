using System.Data;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Plays;
using Vessel.Domain.Common;

namespace Vessel.Persistence;

public sealed class ReviewStore(VesselDbContext db, ISizingStore sizing) : IReviewStore
{
    public async Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct)
    {
        var mode = TradingMode.Validate(query.TradingMode ?? TradingMode.Real);
        // One database snapshot for links, outcomes, membership and coverage during concurrent sync/management.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var portfolios = await db.Portfolios.Where(p => p.TradingMode == mode).AsNoTracking().ToListAsync(ct);
        var accounts = await db.Accounts.Where(a => a.IsEnabled && a.TradingMode == mode).AsNoTracking().ToListAsync(ct);
        var ids = accounts.Where(a => (query.AccountId is null || a.Id == query.AccountId) &&
            (query.Portfolio == "all" || query.Portfolio == "unassigned" && a.PortfolioId is null ||
                a.PortfolioId?.ToString() == query.Portfolio)).Select(a => a.Id).ToList();
        var closed = await sizing.ClosedPlaysAsync(mode, ct);
        // Known filled orders with missing executions come with the closed Plays, as sizing reads them.
        var incomplete = closed.Where(p => p.IncompleteOrders is { Count: > 0 }).Select(p => p.Play.Id).ToHashSet();
        var open = await db.Plays.CountAsync(p => p.TradingMode == mode && ids.Contains(p.AccountId) && p.Status == PlayStatus.Open &&
            (query.Instrument == null || p.ContractId == query.Instrument), ct);
        if (mode == TradingMode.Paper)
        {
            var firstPaper = await db.PaperExecutions.Where(e => ids.Contains(e.AccountId) && e.TradingMode == mode &&
                e.OccurredAtUtc <= asOf && db.Plays.Any(p => p.Id == e.PlayId && p.TradingMode == mode &&
                    (query.Instrument == null || p.ContractId == query.Instrument)))
                .Select(e => (DateTimeOffset?)e.OccurredAtUtc).MinAsync(ct);
            await transaction.CommitAsync(ct);
            return new(portfolios, accounts, closed, open, 0, firstPaper, incomplete);
        }
        var fills = db.Fills.Where(f => ids.Contains(f.AccountId) && f.OccurredAtUtc <= asOf &&
            (query.Instrument == null || f.ContractId == query.Instrument));
        var first = await fills.Select(f => (DateTimeOffset?)f.OccurredAtUtc).MinAsync(ct);
        var from = ReviewService.From(query.Period, asOf);
        var unassigned = await fills.CountAsync(f => (from == null || f.OccurredAtUtc > from) &&
            !db.OrderLinks.Any(l => l.AccountId == f.AccountId && l.State == OrderLinkState.Linked &&
                db.Orders.Any(o => o.OwnerId == l.OwnerId && o.AccountId == l.AccountId && o.Id == l.ImportedOrderId &&
                    o.ContractId == f.ContractId && o.ExternalOrderId == f.ExternalOrderId) &&
                db.Plays.Any(p => p.Id == l.PlayId && p.TradingMode == mode && p.ContractId == f.ContractId)), ct);
        await transaction.CommitAsync(ct);
        return new(portfolios, accounts, closed, open, unassigned, first, incomplete);
    }
}
