using System.Data;
using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Reporting;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Plays;
using Vessel.Domain.Common;

namespace Vessel.Persistence;

public sealed class ReviewStore(VesselDbContext db, ISizingStore sizing) : IReviewStore
{
    public Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct) =>
        ReadAsync(query, asOf, new ReviewWindow(ReviewService.From(query.Period, asOf), asOf), ct);

    public Task<ReportingSettings?> ReportingAsync(CancellationToken ct) => db.ReportingSettings.AsNoTracking().SingleOrDefaultAsync(ct);

    public async Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, ReviewWindow window, CancellationToken ct)
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
            var firstPaperPlay = await FirstPlayAsync(mode, ids, query.Instrument, ct);
            await transaction.CommitAsync(ct);
            return new(portfolios, accounts, closed, open, 0, firstPaper, incomplete) { FirstPlayAtUtc = firstPaperPlay };
        }
        var fills = db.Fills.Where(f => ids.Contains(f.AccountId) && f.OccurredAtUtc <= asOf &&
            (query.Instrument == null || f.ContractId == query.Instrument));
        var first = await fills.Select(f => (DateTimeOffset?)f.OccurredAtUtc).MinAsync(ct);
        // The window's own rule: a rolling period's (from, now], or a reporting week's [from, to) (Phase 2, item 2).
        var windowed = fills;
        if (window.From is { } from)
            windowed = window.HalfOpen ? windowed.Where(f => f.OccurredAtUtc >= from) : windowed.Where(f => f.OccurredAtUtc > from);
        if (window.HalfOpen)
        {
            var to = window.To;
            windowed = windowed.Where(f => f.OccurredAtUtc < to);
        }
        var unassigned = await windowed.CountAsync(f => !db.OrderLinks.Any(l => l.AccountId == f.AccountId && l.State == OrderLinkState.Linked &&
                db.Orders.Any(o => o.OwnerId == l.OwnerId && o.AccountId == l.AccountId && o.Id == l.ImportedOrderId &&
                    o.ContractId == f.ContractId && o.ExternalOrderId == f.ExternalOrderId) &&
                db.Plays.Any(p => p.Id == l.PlayId && p.TradingMode == mode && p.ContractId == f.ContractId)), ct);
        // Unresolved Plays are never scored; Review counts and lists them with their latest declaration (1.3a).
        var enabled = accounts.Select(a => a.Id).ToList();
        var unresolvedPlays = await db.Plays.Where(p => p.Status == PlayStatus.Unresolved && p.TradingMode == mode && enabled.Contains(p.AccountId))
            .AsNoTracking().ToListAsync(ct);
        var unresolvedIds = unresolvedPlays.Select(p => p.Id).ToList();
        var declarations = (await db.StatusChanges.Where(c => unresolvedIds.Contains(c.PlayId) && c.To == PlayStatus.Unresolved)
            .AsNoTracking().ToListAsync(ct)).ToLookup(c => c.PlayId);
        // The earliest linked execution of each Unresolved Play in scope, by the shared OrderKey rule, for the post-hoc badge:
        // one grouped query, however many Plays.
        var scopedUnresolved = unresolvedPlays.Where(p => ids.Contains(p.AccountId) && (query.Instrument == null || p.ContractId == query.Instrument))
            .Select(p => p.Id).ToList();
        var firstExecution = await db.LinkedFills(db.OrderLinks.Where(l => scopedUnresolved.Contains(l.PlayId) && l.State == OrderLinkState.Linked))
            .GroupBy(r => r.Link.PlayId).Select(g => new { PlayId = g.Key, First = g.Min(r => r.Fill.OccurredAtUtc) })
            .ToDictionaryAsync(x => x.PlayId, x => x.First, ct);
        // Cancelled Plays join the process view (1.4b); they are never scored. Only plan revision 1 is read, for its pre-flight.
        var cancelledPlays = await db.Plays.Where(p => p.Status == PlayStatus.Cancelled && enabled.Contains(p.AccountId))
            .AsNoTracking().ToListAsync(ct);
        var endedIds = unresolvedIds.Concat(cancelledPlays.Select(p => p.Id)).ToList();
        var firstRevisions = (await db.PlanRevisions.Where(r => endedIds.Contains(r.PlayId) && r.Number == 1).AsNoTracking().ToListAsync(ct))
            .ToLookup(r => r.PlayId);
        var firstPlay = await FirstPlayAsync(mode, ids, query.Instrument, ct);
        await transaction.CommitAsync(ct);
        return new(portfolios, accounts, closed, open, unassigned, first, incomplete)
        {
            FirstPlayAtUtc = firstPlay,
            UnresolvedPlays = unresolvedPlays.Select(p => new UnresolvedPlayFacts(p, PlayUnresolved.Latest(declarations[p.Id], p.EndedAtUtc))
            {
                Revisions = firstRevisions[p.Id].ToList(), FirstExecutionAtUtc = firstExecution.TryGetValue(p.Id, out var at) ? at : null,
            }).ToList(),
            CancelledPlays = cancelledPlays.Select(p => new EndedPlayFacts(p, firstRevisions[p.Id].ToList())).ToList(),
        };
    }

    /// <summary>The first Play created in scope, in any status, Drafts included (Phase 2, item 2, Sol round 1).</summary>
    private Task<DateTimeOffset?> FirstPlayAsync(string mode, List<Guid> ids, string? instrument, CancellationToken ct) =>
        db.Plays.Where(p => p.TradingMode == mode && ids.Contains(p.AccountId) && (instrument == null || p.ContractId == instrument))
            .Select(p => (DateTimeOffset?)p.CreatedAtUtc).MinAsync(ct);

    public async Task<IReadOnlyList<string>> MistakeTagsAsync(int limit, CancellationToken ct)
    {
        // The owner's own vocabulary: every Play of theirs, newest review first; grouping and spelling follow ReviewService.
        var rows = await db.Plays.Where(p => p.ReviewMistakeTag != null && p.ReviewMistakeTag.Trim() != "")
            .OrderByDescending(p => p.ReviewedAtUtc).ThenByDescending(p => p.UpdatedAtUtc).ThenBy(p => p.Id)
            .Select(p => p.ReviewMistakeTag!).Take(5000).AsNoTracking().ToListAsync(ct);
        return rows.Select(t => t.Trim()).DistinctBy(ReviewService.TagKey).Take(limit).ToList();
    }
}
