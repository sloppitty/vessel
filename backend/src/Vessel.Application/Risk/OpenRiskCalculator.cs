using System.Globalization;
using Vessel.Application.Plays;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Risk;

/// <summary>
/// One execution of a Play as open risk reads it: its role, the entry and level it belongs to (null for an unplanned or
/// manual exit), its exact quantity and price, and whether its side or effect contradicts the role.
/// </summary>
public sealed record RiskExecution(OrderLinkRole Role, string? EntryId, string? LevelId, decimal Quantity, decimal Price,
    bool Contradicts = false)
{
    /// <summary>The retained venue fill behind a linked execution, so its side is checked against the plan being measured.</summary>
    public ImportedFill? Fill { get; init; }
}

/// <summary>
/// A venue order linked to one of a Play's stops, as the venue last reported it: its normalized status (open, triggered,
/// filled, canceled, rejected or other), original and remaining size, whether it is a whole-position TP/SL, and the
/// quantity of its own retained executions. Open risk reads it only for a stop that has executed, to know what that stop
/// still rests for; it never derives a resting quantity from the plan.
/// </summary>
public sealed record RiskOrder(string EntryId, string LevelId, string Status, decimal OriginalSize, decimal RemainingSize,
    bool IsPositionTpsl, decimal Executed);

/// <summary>
/// A Play's risk at its plan's stops, or null with the reason it is unknown. Whether its stops rest at the venue is not
/// decided here: that is the monitor's recorded coverage.
/// </summary>
public sealed record PlayRisk(Ratio? Risk, string? Reason);

/// <summary>
/// Open risk of one Planned or Open Play (Phase 2, item 1): the loss if every current stop of the current plan fills.
/// <list type="bullet">
/// <item><b>Open part.</b> Each entry with fills: the quantity still open, from the entry's exact average fill.</item>
/// <item><b>Planned part.</b> Each entry's planned quantity that has not filled, at its planned price.</item>
/// <item><b>Per stop.</b> A stop's part of what remains of its entry, times the distance from the reference price to the
/// stop on the loss side. A stop at or beyond the reference adds zero: risk is never negative and never offsets another
/// stop. What remains is shared in proportion to what each stop rests for. A stop that has not executed counts for its
/// share of the entry's filled quantity. A stop that has executed counts for what its linked orders still rest for, as the
/// venue reports them: the remaining size of each open or triggered order. A filled, cancelled or rejected order rests
/// for nothing, so a fully filled stop adds nothing, a part-filled one counts for its remaining size, a part-filled one
/// that was cancelled counts for nothing and one whose order was replaced counts for the new order. Order facts that do
/// not reconcile make the Play unknown.</item>
/// </list>
/// Anything that cannot be measured makes the whole Play unknown, never partly summed. No fees, slippage or funding.
/// </summary>
public static class OpenRiskCalculator
{
    public const string NoStop = "An entry has no stop.";
    public const string UnpricedStop = "A stop has no price.";
    public const string StopShares = "An entry's stop shares do not total 100%.";
    public const string EntryShares = "The entry shares do not total 100%, or an entry has no price.";
    public const string NoQuantity = "The plan's size, leverage or entries do not give a quantity.";
    public const string Unattributed = "An exit cannot be attributed to one entry.";
    public const string ExitsExceed = "The exits exceed the entries.";
    public const string Contradicting = "A linked fill contradicts its role.";
    public const string Units = "The quote asset is not a listed USD stablecoin.";
    public const string MissingExecutions = "A linked order is missing executions, so its fills are not all known.";
    public const string UnallocatedExecution = "A split order has an execution that is not allocated yet.";
    public const string UnreconciledStop = "A stop has executed, but its orders do not say what it still rests for.";

    private static readonly Ratio Hundred = Ratio.Of(100);

    public static PlayRisk Calculate(PlayPlanDocument plan, IReadOnlyList<RiskExecution> executions, bool usdQuoted,
        IReadOnlyList<RiskOrder>? orders = null)
    {
        orders ??= [];
        if (!usdQuoted) return new(null, Units);
        if (executions.Any(e => e.Contradicts)) return new(null, Contradicting);
        var sign = plan.Direction == "short" ? -1 : 1;
        // Preview accepts unfinished editor text. An absent or invalid leverage cannot be treated as 1x: doing so can
        // report a plausible but false quantity or percent-stop price. Saved plans use the same whole-number range.
        if (!int.TryParse(plan.Leverage, NumberStyles.None, CultureInfo.InvariantCulture, out var leverage) ||
            leverage is < 1 or > 100 || plan.Leverage != leverage.ToString(CultureInfo.InvariantCulture))
            return new(null, NoQuantity);

        // Entries the plan sizes; a zero share is planned but holds nothing.
        var entries = new List<(PlanEntry Entry, Ratio Share, Ratio? Price)>();
        foreach (var entry in plan.Entries)
        {
            if (Ratio.Plan(entry.Share) is not { } share) return new(null, EntryShares);
            entries.Add((entry, share, Ratio.Plan(entry.Price) is { Sign: > 0 } price ? price : null));
        }

        var filled = new Dictionary<string, (Ratio Quantity, Ratio Notional)>(StringComparer.Ordinal);
        var exited = new Dictionary<string, Ratio>(StringComparer.Ordinal);
        var executedLevels = new Dictionary<string, Ratio>(StringComparer.Ordinal);
        var unattributed = Ratio.Zero;
        foreach (var execution in executions)
        {
            var quantity = Ratio.From(execution.Quantity);
            if (execution.Role == OrderLinkRole.Entry)
            {
                if (execution.EntryId is null) return new(null, Unattributed);
                var sum = filled.GetValueOrDefault(execution.EntryId, (Ratio.Zero, Ratio.Zero));
                filled[execution.EntryId] = (sum.Item1 + quantity, sum.Item2 + quantity * Ratio.From(execution.Price));
            }
            else if (execution.EntryId is { } entryId)
            {
                exited[entryId] = exited.GetValueOrDefault(entryId, Ratio.Zero) + quantity;
                if (execution.Role == OrderLinkRole.Stop && execution.LevelId is { } level)
                    executedLevels[Key(entryId, level)] = executedLevels.GetValueOrDefault(Key(entryId, level), Ratio.Zero) + quantity;
            }
            else unattributed += quantity;
        }
        if (filled.Keys.Any(id => entries.All(e => e.Entry.Id != id)) || exited.Keys.Any(id => entries.All(e => e.Entry.Id != id)))
            return new(null, Unattributed);
        if (unattributed.Sign > 0)
        {
            // A position-level exit belongs to an entry only when one entry has filled.
            var open = filled.Where(f => f.Value.Quantity.Sign > 0).Select(f => f.Key).ToList();
            if (open.Count != 1) return new(null, Unattributed);
            exited[open[0]] = exited.GetValueOrDefault(open[0], Ratio.Zero) + unattributed;
        }

        // The plan must allocate the whole position: entry shares total exactly 100%, as the editor's coverage requires.
        if (entries.Aggregate(Ratio.Zero, (sum, e) => sum + e.Share) != Hundred) return new(null, EntryShares);
        // Integrity before anything is skipped: an entry, with or without a share, may never exit more than it entered.
        foreach (var (entry, _, _) in entries)
            if (exited.GetValueOrDefault(entry.Id, Ratio.Zero) > filled.GetValueOrDefault(entry.Id, (Ratio.Zero, Ratio.Zero)).Item1)
                return new(null, ExitsExceed);
        var planned = PlannedQuantities(plan, entries, leverage);
        var levels = ExecutionLevels(plan, entries, leverage);
        var risk = Ratio.Zero;
        foreach (var (entry, share, price) in entries)
        {
            if (share.Sign == 0 && !filled.ContainsKey(entry.Id)) continue;
            var (quantity, notional) = filled.GetValueOrDefault(entry.Id, (Ratio.Zero, Ratio.Zero));
            var exit = exited.GetValueOrDefault(entry.Id, Ratio.Zero);
            var remaining = quantity - exit;
            if (remaining.Sign < 0) return new(null, ExitsExceed);

            if (planned is null || !planned.TryGetValue(entry.Id, out var plannedQuantity)) return new(null, NoQuantity);
            var unfilled = plannedQuantity - quantity;

            // The entry's stops with their exact prices; when anything remains or is planned, all must be priced and total 100%.
            var stops = entry.Stops.Select(stop => (stop.Id, Share: Ratio.Plan(stop.Share), Price: levels.TryGetValue(Key(entry.Id, stop.Id), out var level) ? level : (Ratio?)null))
                .ToList();
            if (remaining.Sign <= 0 && unfilled.Sign <= 0) continue;
            if (stops.Any(s => s.Share is null)) return new(null, StopShares);
            var priced = stops.Where(s => s.Share!.Value.Sign > 0).ToList();
            if (priced.Count == 0) return new(null, NoStop);
            if (priced.Any(s => s.Price is null)) return new(null, UnpricedStop);
            if (priced.Aggregate(Ratio.Zero, (sum, s) => sum + s.Share!.Value) != Hundred) return new(null, StopShares);

            // Open part: what remains, from the average fill, shared in proportion to what each stop rests for. A stop that
            // has executed rests for what its linked orders still rest for at the venue, never for a quantity from the plan.
            if (remaining.Sign > 0)
            {
                var average = Ratio.Divide(notional, quantity)!.Value;
                var resting = new List<(Ratio? Price, Ratio Rests)>();
                foreach (var stop in priced)
                {
                    if (executedLevels.GetValueOrDefault(Key(entry.Id, stop.Id), Ratio.Zero).Sign == 0)
                        resting.Add((stop.Price, quantity * Ratio.Divide(stop.Share!.Value, Hundred)!.Value));
                    else if (StillRests(orders, entry.Id, stop.Id) is { } rests) resting.Add((stop.Price, rests));
                    else return new(null, UnreconciledStop);
                }
                var total = resting.Aggregate(Ratio.Zero, (sum, s) => sum + s.Rests);
                if (total.Sign == 0) return new(null, NoStop);
                foreach (var (stopPrice, rests) in resting.Where(s => s.Rests.Sign > 0))
                    risk += remaining * Ratio.Divide(rests, total)!.Value * Loss(average, stopPrice!.Value, sign);
            }

            // Planned part: the planned quantity not yet filled, at the planned price, against every stop.
            if (unfilled.Sign > 0)
            {
                if (price is null) return new(null, EntryShares);
                foreach (var stop in priced)
                    risk += unfilled * Ratio.Divide(stop.Share!.Value, Hundred)!.Value * Loss(price.Value, stop.Price!.Value, sign);
            }
        }
        return new(risk, null);
    }

    private static string Key(string entryId, string levelId) => entryId + "\u001f" + levelId;

    /// <summary>
    /// What an executed stop still rests for: the remaining size of each linked order that is open or triggered. A filled,
    /// cancelled or rejected order rests for nothing; a replacement order counts for its own remaining size. Null when the
    /// orders do not say: none is known, a status is unrecognized, a size is out of range, a live order rests for nothing,
    /// is a whole-position TP/SL without a fixed size, or has executed other than what its retained executions show, or a
    /// filled order still reports a remaining size.
    /// </summary>
    private static Ratio? StillRests(IReadOnlyList<RiskOrder> orders, string entryId, string levelId)
    {
        var own = orders.Where(o => o.EntryId == entryId && o.LevelId == levelId).ToList();
        if (own.Count == 0) return null;
        var rests = Ratio.Zero;
        foreach (var order in own)
        {
            var original = Ratio.From(order.OriginalSize);
            var left = Ratio.From(order.RemainingSize);
            if (left.Sign < 0 || left > original) return null;
            switch (order.Status)
            {
                case "open" or "triggered":
                    if (order.IsPositionTpsl || left.Sign == 0 || original - left != Ratio.From(order.Executed)) return null;
                    rests += left;
                    break;
                case "filled":
                    if (left.Sign != 0) return null;
                    break;
                case "canceled" or "rejected":
                    break;
                default:
                    return null;
            }
        }
        return rests;
    }

    /// <summary>The loss per unit from the reference to the stop on the loss side; zero at or beyond the reference.</summary>
    private static Ratio Loss(Ratio reference, Ratio stop, int sign) => Ratio.Max(Ratio.Zero, sign > 0 ? reference - stop : stop - reference);

    /// <summary>
    /// Each entry's planned quantity, exactly, as the editor derives it: the size times the entry's share in quantity
    /// sizing, or margin × leverage over the share-weighted planned average entry in margin sizing, which needs every entry
    /// priced and the shares to total 100%. Null when the plan cannot be sized.
    /// </summary>
    private static Dictionary<string, Ratio>? PlannedQuantities(PlayPlanDocument plan, List<(PlanEntry Entry, Ratio Share, Ratio? Price)> entries, int leverage)
    {
        if (Ratio.Plan(plan.Size) is not { } size) return null;
        var result = new Dictionary<string, Ratio>(StringComparer.Ordinal);
        if (plan.SizingMode == "quantity")
        {
            foreach (var (entry, share, _) in entries) result[entry.Id] = size * Ratio.Divide(share, Hundred)!.Value;
            return result;
        }
        if (plan.SizingMode != "margin") return null;
        if (size.Sign == 0)
        {
            foreach (var (entry, _, _) in entries) result[entry.Id] = Ratio.Zero;
            return result;
        }
        var shares = entries.Aggregate(Ratio.Zero, (sum, e) => sum + e.Share);
        if (shares != Hundred || entries.Any(e => e.Share.Sign > 0 && e.Price is null)) return null;
        var notional = entries.Where(e => e.Share.Sign > 0).Aggregate(Ratio.Zero, (sum, e) => sum + e.Price!.Value * e.Share);
        if (Ratio.Divide(notional, shares) is not { Sign: > 0 } average) return null;
        var total = Ratio.Divide(size * Ratio.Of(leverage), average)!.Value;
        foreach (var (entry, share, _) in entries) result[entry.Id] = total * Ratio.Divide(share, Hundred)!.Value;
        return result;
    }

    /// <summary>
    /// Stop prices, exactly: a price stop is its own value; a percent stop is a return on margin at the plan's leverage, so
    /// its distance from the planned entry is the percentage over the leverage. Null when it cannot be priced.
    /// </summary>
    private static Dictionary<string, Ratio> ExecutionLevels(PlayPlanDocument plan, List<(PlanEntry Entry, Ratio Share, Ratio? Price)> entries, int leverage)
    {
        var sign = plan.Direction == "short" ? -1 : 1;
        var result = new Dictionary<string, Ratio>(StringComparer.Ordinal);
        foreach (var (entry, _, price) in entries)
            foreach (var stop in entry.Stops)
            {
                if (Ratio.Plan(stop.Value) is not { } value) continue;
                Ratio? resolved = stop.Unit == "price" ? value
                    : price is { } planned ? planned - Ratio.Divide(planned * value, Ratio.Of(leverage * 100))!.Value * Ratio.Of(sign) : null;
                if (resolved is { Sign: > 0 } level) result[Key(entry.Id, stop.Id)] = level;
            }
        return result;
    }
}
