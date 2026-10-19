using System.Globalization;
using System.Numerics;
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
    /// a Play without entry fills or a close time has no outcome. It closes at <see cref="CloseTime"/>.
    /// </summary>
    public static PlayOutcome? Outcome(ClosedPlayFacts facts) => Outcome(facts, out _);

    /// <summary>A projected close validates manual writes before any ledger or lifecycle mutation.</summary>
    public static PlayOutcome? Outcome(ClosedPlayFacts facts, out string? problem, DateTimeOffset? projectedClose = null)
    {
        problem = null;
        if (projectedClose is null && facts.Play.Status != PlayStatus.Closed ||
            (projectedClose ?? CloseTime(facts)) is not { } closedAt) return null;
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
            problem = "Executed result, entry notional, return percent or initial-risk R exceeds the supported decimal range or exact precision.";
            return null;
        }
    }

    /// <summary>
    /// When a closed Play's outcome closes. A venue-detected close (<see cref="Play.MarkClosed"/>) keeps the detection time in
    /// <see cref="Play.EndedAtUtc"/>; its outcome closes at the latest valid linked exit (<see cref="PlayExitTime.LatestValidExit"/>),
    /// or at detection when the fills cannot settle it. Manual ledgers and retrospective Plays already store their real exit.
    /// </summary>
    public static DateTimeOffset? CloseTime(ClosedPlayFacts facts) => DetectedAt(facts) is { } detected
        ? PlayExitTime.LatestValidExit(facts, detected) ?? detected : facts.Play.EndedAtUtc;

    /// <summary>When a check or refresh detected a venue close; null for manual ledgers and retrospective Plays.</summary>
    public static DateTimeOffset? DetectedAt(ClosedPlayFacts facts) =>
        facts is { ManualSource: false, Play.Origin: PlayOrigin.Planned, Play.Status: PlayStatus.Closed } ? facts.Play.EndedAtUtc : null;

    private static PlayOutcome? Calculate(ClosedPlayFacts facts, DateTimeOffset closedAt)
    {
        var entries = facts.Fills.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill).ToList();
        if (entries.Count == 0) return null;
        // Sum the exact 56-place products; decimal multiplication would round each one to 28 places before this guard.
        var notional = ToDecimal(entries.Aggregate(BigInteger.Zero, (sum, f) => sum + ExactAmount.Product(f.Quantity, f.Price)));
        if (notional <= 0) return null;
        var fills = facts.Fills.Select(f => f.Fill).ToList();
        var venue = VenueNet(fills);
        var net = ToDecimal(venue.Net);
        var feesComplete = facts.ManualFeesComplete && venue.FeesComplete;
        var firstEntry = entries.Min(f => f.OccurredAtUtc);
        var revisions = facts.Revisions.OrderBy(r => r.Number).ToList();
        var plan = (revisions.LastOrDefault(r => r.CreatedAtUtc <= firstEntry) ?? revisions.FirstOrDefault())?.Plan ?? facts.Play.Plan;
        decimal? risk = facts.Play.Origin != PlayOrigin.Retrospective && StopDistance(facts.ParsedPlan(plan)) is { } distance ? notional * distance : null;
        return new PlayOutcome(facts.Play.Id, closedAt, net, notional, risk, feesComplete) { GrossResult = Exact(VenueGross(fills)) };
    }

    /// <summary>
    /// The scored venue result of fills: Σ venue closed PnL less fees in listed nominal stablecoins on gross-basis fills (a
    /// net-of-fee PnL already has its fee taken off, so it is never charged twice). Fees are complete when every fill has a
    /// fee basis and every gross-basis fee is in a listed stablecoin or zero. Exact: decimal sums can lose a small result
    /// between large cancelling exits.
    /// </summary>
    public static (ExactAmount Net, bool FeesComplete) VenueNet(IReadOnlyCollection<ImportedFill> fills)
    {
        var gross = fills.Where(f => f.PnlBasis == ExecutionFacts.PnlGross).ToList();
        var net = fills.Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.ClosedPnlUsd)) -
            gross.Where(f => NominalStablecoins.Contains(f.FeeToken)).Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.Fee));
        return (net, fills.All(f => ExecutionFacts.IsFeeBasis(f.FeeBasis)) &&
            gross.All(f => !string.IsNullOrWhiteSpace(f.FeeToken) && (NominalStablecoins.Contains(f.FeeToken) || f.Fee == 0)));
    }

    /// <summary>
    /// The venue result before trading fees, on the scored basis: Σ venue closed PnL plus the listed stablecoin fees already
    /// inside net-of-fee PnL. Null when a net-of-fee fill's nonzero fee is in another token, since nothing is converted.
    /// Same rule as an Activity operation's before-fee P&amp;L.
    /// </summary>
    public static ExactAmount? VenueGross(IEnumerable<ImportedFill> fills)
    {
        var sum = default(ExactAmount);
        foreach (var fill in fills)
        {
            sum += ExactAmount.From(fill.ClosedPnlUsd);
            if (fill.PnlBasis != ExecutionFacts.PnlNetOfFee) continue;
            if (NominalStablecoins.Contains(fill.FeeToken)) sum += ExactAmount.From(fill.Fee);
            else if (fill.Fee != 0) return null;
        }
        return sum;
    }

    /// <summary>The exact decimal of an amount, or null outside decimal range or precision; for figures that must not exclude an outcome.</summary>
    internal static decimal? Exact(ExactAmount? value)
    {
        if (value is not { } amount) return null;
        try { return ToDecimal(amount); }
        catch (OverflowException) { return null; }
    }

    /// <summary>The exact decimal of a sum; outside decimal range or precision it throws <see cref="OverflowException"/>.</summary>
    internal static decimal ToDecimal(ExactAmount value)
    {
        var result = decimal.Parse(value.ToString(), CultureInfo.InvariantCulture);
        if (ExactAmount.From(result) != value) throw new OverflowException();
        return result;
    }

    private static readonly BigInteger ProductScale = BigInteger.Pow(10, 28);

    /// <summary>
    /// The exact decimal of a 56-place product of two amounts' units (quantity × price). A nonzero remainder beyond 28
    /// places or a value outside decimal range throws <see cref="OverflowException"/>; nothing is rounded.
    /// </summary>
    internal static decimal ToDecimal(BigInteger product)
    {
        var units = BigInteger.DivRem(product, ProductScale, out var remainder);
        if (!remainder.IsZero) throw new OverflowException();
        return ToDecimal(new ExactAmount(units));
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
