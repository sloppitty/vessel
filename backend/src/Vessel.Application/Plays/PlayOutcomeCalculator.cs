using System.Globalization;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Application.Workspace;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays;

/// <summary>Shared executed result and initial planned risk. Reporting applies its own eligibility and time window.</summary>
public static class PlayOutcomeCalculator
{
    /// <summary>
    /// Net result and planned risk of one closed Play. Risk uses the plan in force at the first entry fill;
    /// a Play without entry fills or a close time has no outcome.
    /// </summary>
    public static PlayOutcome? Outcome(ClosedPlayFacts facts) => Outcome(facts, out _);

    /// <summary>A projected close validates manual writes before any ledger or lifecycle mutation.</summary>
    public static PlayOutcome? Outcome(ClosedPlayFacts facts, out string? problem, DateTimeOffset? projectedClose = null)
    {
        problem = null;
        if (projectedClose is null && facts.Play.Status != PlayStatus.Closed ||
            (projectedClose ?? facts.Play.EndedAtUtc) is not { } closedAt) return null;
        try
        {
            var result = Calculate(facts, closedAt);
            if (result is not null)
            {
                // These properties are also consumed by Review and sizing, so an unusable outcome is excluded at source.
                _ = result.ReturnPercent;
                _ = result.RMultiple;
            }
            return result;
        }
        catch (OverflowException)
        {
            problem = "Executed result, return percent or initial-risk R exceeds the supported decimal range or exact precision.";
            return null;
        }
    }

    private static PlayOutcome? Calculate(ClosedPlayFacts facts, DateTimeOffset closedAt)
    {
        var entries = facts.Fills.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill).ToList();
        if (entries.Count == 0) return null;
        var notional = ToDecimal(entries.Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.Quantity * f.Price)));
        if (notional <= 0) return null;
        var fills = facts.Fills.Select(f => f.Fill).ToList();
        // A net-of-fee closed PnL already has its fee taken off; only gross PnL needs the fee subtracted.
        var gross = fills.Where(f => f.PnlBasis == ExecutionFacts.PnlGross).ToList();
        // Decimal sums can lose a small result between large cancelling exits.
        var net = ToDecimal(fills.Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.ClosedPnlUsd)) -
            gross.Where(f => NominalStablecoins.Contains(f.FeeToken)).Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.Fee)));
        var feesComplete = facts.ManualFeesComplete && fills.All(f => ExecutionFacts.IsFeeBasis(f.FeeBasis)) &&
            gross.All(f => !string.IsNullOrWhiteSpace(f.FeeToken) && (NominalStablecoins.Contains(f.FeeToken) || f.Fee == 0));
        var firstEntry = entries.Min(f => f.OccurredAtUtc);
        var revisions = facts.Revisions.OrderBy(r => r.Number).ToList();
        var plan = (revisions.LastOrDefault(r => r.CreatedAtUtc <= firstEntry) ?? revisions.FirstOrDefault())?.Plan ?? facts.Play.Plan;
        decimal? risk = facts.Play.Origin != PlayOrigin.Retrospective && StopDistance(facts.ParsedPlan(plan)) is { } distance ? notional * distance : null;
        return new PlayOutcome(facts.Play.Id, closedAt, net, notional, risk, feesComplete);
    }

    /// <summary>The exact decimal of a sum; outside decimal range or precision it throws <see cref="OverflowException"/>.</summary>
    internal static decimal ToDecimal(ExactAmount value)
    {
        var result = decimal.Parse(value.ToString(), CultureInfo.InvariantCulture);
        if (ExactAmount.From(result) != value) throw new OverflowException();
        return result;
    }

    /// <summary>
    /// The share-weighted price distance to the stops over the share-weighted entry, as a fraction. Stop shares
    /// are normalized within each entry; entries or stops without a price are skipped.
    /// </summary>
    public static decimal? StopDistance(PlayPlanDocument plan)
    {
        var stops = ExecutionMatcher.Levels(plan).Where(l => l.Role == OrderLinkRole.Stop && l.Price is > 0).ToLookup(l => l.EntryId!);
        decimal loss = 0, entry = 0;
        foreach (var item in plan.Entries)
        {
            if (Positive(item.Price) is not { } price || Positive(item.Share) is not { } share) continue;
            var weighted = item.Stops.Select(stop => (Share: Positive(stop.Share), Price: stops[item.Id].FirstOrDefault(l => l.LevelId == stop.Id)?.Price))
                .Where(s => s is { Share: not null, Price: not null }).ToList();
            var shares = weighted.Sum(s => s.Share!.Value);
            if (shares <= 0) continue;
            loss += share * weighted.Sum(s => s.Share!.Value * Math.Abs(price - s.Price!.Value)) / shares;
            entry += share * price;
        }
        return entry > 0 && loss > 0 ? loss / entry : null;
    }

    private static decimal? Positive(string text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

}
