using System.Globalization;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Activity;

/// <summary>
/// Unrealized P&amp;L of the open operations on one Activity page, read after the cached aggregate so it never enters the
/// page version. A venue with market context marks the remaining quantity at its current mark price; otherwise the
/// latest snapshot's venue-reported figure counts only when that snapshot position is the operation's remaining
/// position. Closed, incomplete or unmatched operations stay null, never zero.
/// </summary>
internal static class OperationUnrealized
{
    public const string Mark = "mark", Snapshot = "snapshot";
    private static readonly ExactAmount One = ExactAmount.Parse("1");

    public static async Task<List<ActivityOperationSummaryDto>> ApplyAsync(IReadOnlyList<ActivityOperationDto> rows, ActivityFacts facts,
        IVenueRegistry? venues, MarketContextCache? marks, CancellationToken ct)
    {
        var open = rows.Where(IsOpen).ToList();
        if (open.Count == 0) return rows.Select(ActivityOperationSummaryDto.From).ToList();
        var venueOf = facts.Accounts.ToDictionary(a => a.Id, a => a.VenueId);
        var contexts = new Dictionary<string, MarketContextCache.Snapshot?>();
        if (venues is not null && marks is not null)
            foreach (var venueId in open.Select(o => venueOf.GetValueOrDefault(o.AccountId)).OfType<string>().Distinct())
                if (venues.MarketContext(venueId) is { } reader)
                    contexts[venueId] = await ReadAsync(marks, venueId, reader, ct);
        var snapshots = facts.Snapshots.ToDictionary(s => s.AccountId);
        return rows.Select(o =>
        {
            var row = ActivityOperationSummaryDto.From(o);
            if (!IsOpen(o) || venueOf.GetValueOrDefault(o.AccountId) is not { } venueId) return row;
            return contexts.TryGetValue(venueId, out var context)
                ? FromMark(o, row, context)
                : FromSnapshot(o, row, snapshots.GetValueOrDefault(o.AccountId));
        }).ToList();
    }

    private static bool IsOpen(ActivityOperationDto o) => o.Status == "open" && o.Direction is not null &&
        o.OpenQuantity is { } quantity && ExactAmount.Parse(quantity).Units.Sign > 0;

    // A failed or timed-out venue read leaves the marks unknown; it never fails the page.
    private static async Task<MarketContextCache.Snapshot?> ReadAsync(MarketContextCache marks, string venueId,
        IMarketContextReader reader, CancellationToken ct)
    {
        try
        {
            return await marks.GetAsync(venueId, reader, ct);
        }
        catch (Exception ex) when (ex is VenueReadException or HttpRequestException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static ActivityOperationSummaryDto FromMark(ActivityOperationDto o, ActivityOperationSummaryDto row,
        MarketContextCache.Snapshot? context)
    {
        if (context is null || o.AverageEntryPrice is null) return row;
        var matches = context.Value.Where(c => c.ContractId == o.ContractId).Take(2).ToList();
        if (matches.Count != 1 || !decimal.TryParse(matches[0].MarkPrice, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mark) ||
            mark <= 0) return row;
        var move = ExactAmount.From(mark) - ExactAmount.Parse(o.AverageEntryPrice);
        var pnl = ExactAmount.Average(ExactAmount.Parse(o.OpenQuantity!).Units * move.Units, One)!;
        return row with
        {
            UnrealizedPnlUsd = o.Direction == "short" ? (-ExactAmount.Parse(pnl)).ToString() : pnl,
            UnrealizedPnlSource = Mark, UnrealizedPnlMarkPrice = ExactDecimal.Format(mark), UnrealizedPnlAsOfUtc = context.Observed
        };
    }

    private static ActivityOperationSummaryDto FromSnapshot(ActivityOperationDto o, ActivityOperationSummaryDto row,
        AccountSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.ObservedAtUtc < o.LastOccurredAtUtc) return row;
        var signed = ExactAmount.Parse(o.OpenQuantity!);
        if (o.Direction == "short") signed = -signed;
        var matches = snapshot.Positions.Where(p => p.ContractId == o.ContractId &&
            (!o.ContractKey.IsNative || p.VenueContractId == o.ContractKey.Identity)).Take(2).ToList();
        if (matches.Count != 1 || ExactAmount.From(matches[0].SignedQuantity) != signed) return row;
        return row with
        {
            UnrealizedPnlUsd = ExactDecimal.Format(matches[0].UnrealizedPnlUsd), UnrealizedPnlSource = Snapshot,
            UnrealizedPnlAsOfUtc = snapshot.ObservedAtUtc
        };
    }
}
