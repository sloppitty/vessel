using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays;

/// <summary>
/// What happened, beside the plan, by Review's own rules: when the trade began and last exited, its average entry and exit,
/// and its estimated R (planned-stop model) with the initial planned risk it divides by, or why R is unknown. Times held
/// run from <paramref name="FirstEntryAtUtc"/> to <paramref name="LastExitAtUtc"/> once the Play is closed.
/// </summary>
public sealed record PlayReceiptDto(DateTimeOffset? FirstEntryAtUtc, DateTimeOffset? LastExitAtUtc, string? AverageEntryPrice,
    string? AverageExitPrice, int FillCount, string? EstimatedR, string? InitialRisk, string? EstimatedRReason);

/// <summary>One entry as planned, from the plan revision in force at its first fill (phase 1, 1.1a).</summary>
public sealed record PlannedEntry(string? Price, string? Quantity, int? Revision);

public static partial class PlayReceipts
{
    public const string RetrospectiveReason = "A retrospective Play has no plan, so it has no planned risk.";
    public const string OpenReason = "Known once the Play is closed and its fills settle it.";
    public const string FeesReason = "Review leaves this Play out because a fee is missing or in an unsupported token.";
    public const string ExecutionsReason = "Review leaves this Play out because its executions are incomplete or do not settle it.";
    public const string NoStopReason = "The plan in force at the first entry fill lacks a complete stop on the loss side of every entry.";
    public const string PlanAfterFillReason = "No plan revision was in force at the first entry fill: the plan was written after it.";

    /// <summary>The receipt of a Play from the facts Review and sizing read: every linked fill, or the manual ledger.</summary>
    public static PlayReceiptDto Build(ClosedPlayFacts facts, DateTimeOffset now)
    {
        var entries = facts.Fills.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill).ToList();
        var exits = facts.Fills.Where(f => f.Role != OrderLinkRole.Entry).Select(f => f.Fill).ToList();
        var (r, risk, reason) = EstimatedR(facts, now);
        return new(entries.Count == 0 ? null : entries.Min(f => f.OccurredAtUtc), exits.Count == 0 ? null : exits.Max(f => f.OccurredAtUtc),
            Average(entries), Average(exits), facts.Fills.Count, r, risk, reason);
    }

    private static (string? R, string? Risk, string? Reason) EstimatedR(ClosedPlayFacts facts, DateTimeOffset now)
    {
        if (facts.Play.Origin == PlayOrigin.Retrospective) return (null, null, RetrospectiveReason);
        if (facts.Play.Status != PlayStatus.Closed) return (null, null, OpenReason);
        var score = ReviewService.Score(facts, now, facts.IncompleteOrders is { Count: > 0 });
        if (score.Exclusion == "fees") return (null, null, FeesReason);
        if (score.Result is null || score.Exclusion is not null) return (null, null, ExecutionsReason);
        if (score.Result is { RMultiple: { } r, PlannedRisk: { } risk }) return (ExactDecimal.Format(r), ExactDecimal.Format(risk), null);
        var firstEntry = facts.Fills.Where(f => f.Role == OrderLinkRole.Entry).Min(f => f.Fill.OccurredAtUtc);
        return (null, null, facts.Revisions.Any(r => r.CreatedAtUtc <= firstEntry) ? NoStopReason : PlanAfterFillReason);
    }

    private static string? Average(IReadOnlyCollection<ImportedFill> fills)
    {
        var quantity = ExecutionIntegrity.Quantity(fills);
        return quantity.Units.Sign > 0
            ? ExactAmount.Average(fills.Aggregate(BigInteger.Zero, (sum, f) => sum + ExactAmount.Product(f.Price, f.Quantity)), quantity) : null;
    }

    /// <summary>
    /// One entry's planned price and quantity from the plan revision in force at <paramref name="firstFill"/>: the latest
    /// revision written at or before it, else the first revision (a plan written after the fill), else the current plan.
    /// Before any fill it is the current plan. Quantity follows the editor: the whole-position quantity (margin sizing:
    /// size × leverage over the share-weighted planned entry) times the entry's share. Anything the plan cannot give is null.
    /// </summary>
    public static PlannedEntry Planned(Play play, IReadOnlyList<PlayPlanRevision> revisions, string entryId, DateTimeOffset? firstFill)
    {
        var ordered = revisions.OrderBy(r => r.Number).ToList();
        var revision = firstFill is { } at ? ordered.LastOrDefault(r => r.CreatedAtUtc <= at) ?? ordered.FirstOrDefault() : null;
        var plan = PlayDocuments.Read(revision?.Plan ?? play.Plan);
        var number = revision?.Number ?? (play.PlanRevision > 0 ? play.PlanRevision : null);
        var entry = plan.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return new(null, null, number);
        // Plain decimal text as the browser writes it; older text the editor would read as unknown stays unknown here too.
        return new(Positive(entry.Price) is { } price ? ExactDecimal.Format(price) : null,
            Quantity(plan, entry) is { } quantity ? ExactDecimal.Format(quantity) : null, number);
    }

    private static decimal? Quantity(PlayPlanDocument plan, PlanEntry entry)
    {
        if (Positive(plan.Size) is not { } size || Positive(entry.Share) is not { } share) return null;
        try
        {
            if (plan.SizingMode == "quantity") return size * share / 100;
            if (plan.SizingMode != "margin") return null;
            var weighted = plan.Entries.Select(e => (Price: Positive(e.Price), Share: Positive(e.Share)))
                .Where(e => e.Price is not null && e.Share is not null).ToList();
            var shares = weighted.Sum(e => e.Share!.Value);
            if (weighted.Count == 0 || shares <= 0) return null;
            var average = weighted.Sum(e => e.Price!.Value * e.Share!.Value) / shares;
            // As the editor's leverageOf reads it: trimmed whole digits of at least 1, otherwise 1.
            var leverage = int.TryParse(plan.Leverage.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var lever) && lever >= 1 ? lever : 1;
            return average <= 0 ? null : size * leverage / average * share / 100;
        }
        catch (OverflowException) { return null; }
    }

    /// <summary>A plan number as the editor's <c>positivePlanNumber</c> reads it: trimmed plain decimal text above zero.</summary>
    private static decimal? Positive(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return PlainDecimal().IsMatch(trimmed) &&
            decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
    }

    [GeneratedRegex("^[0-9]+(\\.[0-9]+)?$")]
    private static partial Regex PlainDecimal();
}
