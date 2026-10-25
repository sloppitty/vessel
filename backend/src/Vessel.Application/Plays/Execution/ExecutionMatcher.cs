using System.Globalization;
using Vessel.Application.Venues;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays.Execution;

/// <summary>A plan level an order could belong to, with the price and side it implies.</summary>
public sealed record PlanLevelTarget(OrderLinkRole Role, string? EntryId, string? LevelId, decimal? Price, string Side)
{
    public string Key => PlayOrderLink.Key(Role, EntryId, LevelId);
}

public sealed record MatchCandidate(Play Play, PlanLevelTarget Level);

/// <summary>
/// Decides which plan levels an order could belong to. Automatic linking needs exactly one candidate;
/// several are suggestions for the owner. A price touch is never involved: only venue orders are matched.
/// </summary>
public static class ExecutionMatcher
{

    public static bool IsActive(Play play) => play.TradingMode == "real" && play.Status is PlayStatus.Planned or PlayStatus.Paused or PlayStatus.Open;

    /// <summary>
    /// Entries, stops and targets with prices resolved. A percent level is a return on margin, so its price
    /// distance from the entry is the percentage divided by the plan's leverage.
    /// </summary>
    public static IEnumerable<PlanLevelTarget> Levels(PlayPlanDocument plan)
    {
        var long_ = plan.Direction == "long";
        var leverage = int.TryParse(plan.Leverage, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1 ? value : 1;
        string entrySide = long_ ? ExecutionFacts.Buy : ExecutionFacts.Sell, exitSide = long_ ? ExecutionFacts.Sell : ExecutionFacts.Buy;
        foreach (var entry in plan.Entries)
        {
            var price = Positive(entry.Price);
            yield return new(OrderLinkRole.Entry, entry.Id, null, price, entrySide);
            foreach (var stop in entry.Stops)
                yield return new(OrderLinkRole.Stop, entry.Id, stop.Id, Resolve(price, stop, leverage, below: long_), exitSide);
            foreach (var target in entry.Targets)
                yield return new(OrderLinkRole.Target, entry.Id, target.Id, Resolve(price, target, leverage, below: !long_), exitSide);
        }
    }

    public static string ExitSide(PlayPlanDocument plan) => plan.Direction == "long" ? ExecutionFacts.Sell : ExecutionFacts.Buy;

    /// <summary>
    /// Candidates for one order. Planned levels win over an unplanned exit, which is only proposed for an
    /// open Play when the order reduces a position and was placed after the Play's first entry fill.
    /// The order's retained <paramref name="fills"/> must not contradict the level, and an exit that has filled
    /// must have filled at or after the Play's first entry fill; a resting stop or target may be placed earlier.
    /// </summary>
    public static List<MatchCandidate> Candidates(ImportedOrder order, IEnumerable<(Play Play, PlayPlanDocument Plan)> plays,
        Func<Play, string, bool> dismissed, bool closesPosition, Func<Play, DateTimeOffset?>? firstEntryFill = null, decimal? tick = null,
        IReadOnlyCollection<ImportedFill>? fills = null)
    {
        var planned = new List<MatchCandidate>();
        var exits = new List<MatchCandidate>();
        fills ??= [];
        DateTimeOffset? filledAt = fills.Count > 0 ? fills.Min(f => f.OccurredAtUtc) : null;
        foreach (var (play, plan) in plays)
        {
            if (!IsActive(play) || play.InstrumentSource != InstrumentSource.Venue || play.ContractId != order.ContractId ||
                order.PlacedAtUtc < play.CreatedAtUtc)
                continue;
            var acting = order.TriggerPrice ?? order.LimitPrice;
            var mechanism = OrderMechanics.Classify(order.OrderType, order.TriggerPrice is not null);
            var entrySide = ExecutionIntegrity.EntrySide(plan);
            var opened = firstEntryFill?.Invoke(play);
            bool Executed(OrderLinkRole role) => fills.All(f => !ExecutionIntegrity.Contradicts(role, entrySide, f)) &&
                (role == OrderLinkRole.Entry || filledAt is null || opened is { } at && filledAt >= at);
            foreach (var level in Levels(plan))
            {
                if (level.Side != order.Side || level.Price is not { } price || !Near(acting, price, tick) || dismissed(play, level.Key)) continue;
                var fits = level.Role switch
                {
                    OrderLinkRole.Entry => !order.ReduceOnly && !order.IsPositionTpsl,
                    // A stop acts through a trigger; a resting limit at the stop would fill at once.
                    OrderLinkRole.Stop => mechanism is not (OrderMechanism.Plain or OrderMechanism.TakeProfit),
                    OrderLinkRole.Target => mechanism != OrderMechanism.Stop,
                    _ => false
                };
                if (fits && Executed(level.Role)) planned.Add(new(play, level));
            }
            var exit = new PlanLevelTarget(OrderLinkRole.Exit, null, null, null, ExitSide(plan));
            if (play.Status == PlayStatus.Open && order.Side == exit.Side && (order.ReduceOnly || order.IsPositionTpsl || closesPosition) &&
                opened is { } first && order.PlacedAtUtc >= first && Executed(OrderLinkRole.Exit) && !dismissed(play, exit.Key))
                exits.Add(new(play, exit));
        }
        if (planned.Count > 0) return planned;
        return exits;
    }

    /// <summary>
    /// Same price within one step of the fifth significant figure (1 at 84,541, 0.01 at 100), or within the instrument's
    /// minimum step when it is coarser: a tick (RISEx DOGE trades in 0.00001 at 0.09), or Hyperliquid's last allowed decimal
    /// (0.00001 at <c>szDecimals = 1</c>, so a planned 0.051234 matches an order at 0.05123). A planned 82,850.18 from a
    /// percentage still matches an order at 82,850.
    /// </summary>
    public static bool Near(decimal actual, decimal planned, decimal? tick = null)
    {
        if (planned <= 0) return false;
        var magnitude = (int)Math.Floor(Math.Log10((double)planned));
        // Below decimal precision the step is zero: require equality unless a venue tick is coarser.
        var step = Pow10(magnitude - 4);
        if (tick is > 0 && tick > step) step = tick.Value;
        return Math.Abs(actual - planned) <= step;
    }

    private static decimal Pow10(int exponent)
    {
        var value = 1m;
        var factor = exponent < 0 ? 0.1m : 10m;
        for (var i = 0; i < Math.Abs(exponent); i++) value *= factor;
        return value;
    }

    private static decimal? Resolve(decimal? entry, PlanExit exit, int leverage, bool below)
    {
        if (exit.Unit == "price") return Positive(exit.Value);
        if (entry is not { } price || !TryDecimal(exit.Value, out var percent) || percent < 0) return null;
        var distance = percent / leverage;
        try
        {
            var resolved = price * (1 + (below ? -distance : distance) / 100);
            return resolved > 0 ? resolved : null;
        }
        catch (OverflowException)
        {
            // A valid percentage can imply a price outside decimal's range. It cannot match an order.
            return null;
        }
    }

    private static decimal? Positive(string text) => TryDecimal(text, out var value) && value > 0 ? value : null;

    private static bool TryDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
