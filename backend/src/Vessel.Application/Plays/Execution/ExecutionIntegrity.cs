using Vessel.Application.Sizing;
using Vessel.Application.Venues;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays.Execution;

/// <summary>
/// Whether linked executions can settle a Play. Matching, linking, lifecycle, sizing and Review share these checks,
/// so a record that cannot be reconciled is neither closed nor scored. Fills are facts; no check reinterprets one.
/// </summary>
public static class ExecutionIntegrity
{
    public static string EntrySide(PlayPlanDocument plan) => plan.Direction == "long" ? ExecutionFacts.Buy : ExecutionFacts.Sell;

    /// <summary>
    /// The fill's normalized side or effect contradicts the role: an entry opens on the entry side and an exit reduces
    /// on the other. An unknown effect is no contradiction; a flip is split into its closing and opening parts only by an
    /// owner-confirmed allocation (Phase 2, item 11), whose lines carry the closing and opening effects.
    /// </summary>
    public static bool Contradicts(OrderLinkRole role, string entrySide, ImportedFill fill) => role == OrderLinkRole.Entry
        ? fill.Side != entrySide || fill.PositionEffect is ExecutionFacts.Close or ExecutionFacts.Flip
        : fill.Side == entrySide || fill.PositionEffect is ExecutionFacts.Open or ExecutionFacts.Flip;

    /// <summary>
    /// Why the order cannot take the role on a Play of this plan, or null. The owner decides which Play owns an
    /// order, not what it did: its side, reduce-only flag and retained fills must fit the role.
    /// </summary>
    public static string? RoleConflict(OrderLinkRole role, PlayPlanDocument plan, ImportedOrder order, IEnumerable<ImportedFill> fills)
    {
        var entrySide = EntrySide(plan);
        var entry = role == OrderLinkRole.Entry;
        var name = $"an {(entry ? "entry" : "exit")} of a {plan.Direction} play";
        if (entry != (order.Side == entrySide)) return $"A {order.Side} order cannot be {name}.";
        if (entry && (order.ReduceOnly || order.IsPositionTpsl)) return $"A reduce-only or position TP/SL order cannot be {name}.";
        if (role == OrderLinkRole.Stop && OrderMechanics.Classify(order.OrderType, order.TriggerPrice is not null) == OrderMechanism.TakeProfit)
            return $"A take-profit order cannot be a stop of a {plan.Direction} play.";
        var effects = fills.Where(f => Contradicts(role, entrySide, f)).Select(f => $"{f.Side} {f.PositionEffect}").Distinct().ToList();
        return effects.Count > 0 ? $"This order has {string.Join(", ", effects)} fills, so it cannot be {name}." : null;
    }

    /// <summary>
    /// A filled fixed-size order whose retained executions fall short of its size. Cancellation, rejection and trigger
    /// size changes are not executions, and a position TP/SL has no fixed size, so they never prove a missing fill.
    /// </summary>
    public static bool MissingExecutions(ImportedOrder order, decimal retained) =>
        MissingExecutions(order, ExactAmount.From(retained));

    public static bool MissingExecutions(ImportedOrder order, IEnumerable<ImportedFill> fills) =>
        MissingExecutions(order, Quantity(fills));

    /// <summary>
    /// A filled order whose retained executions fall short of its size, or any order an import left an execution of out
    /// (Phase 2, item 7), whatever its status.
    /// </summary>
    private static bool MissingExecutions(ImportedOrder order, ExactAmount retained) => order.ExecutionsOmitted ||
        order.Status == "filled" && !order.IsPositionTpsl && ExactAmount.From(order.OriginalSize).CompareTo(retained) > 0;

    internal static ExactAmount Quantity(IEnumerable<ImportedFill> fills) =>
        fills.Aggregate(default(ExactAmount), (sum, fill) => sum + ExactAmount.From(fill.Quantity));

    /// <summary>Exits never exceed the entries filled before them; fills at the same time net together.</summary>
    public static bool Chronological(IEnumerable<LinkedFill> fills)
    {
        var balance = default(ExactAmount);
        foreach (var group in fills.GroupBy(f => f.Fill.OccurredAtUtc).OrderBy(g => g.Key))
        {
            balance += Quantity(group.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill)) -
                Quantity(group.Where(f => f.Role != OrderLinkRole.Entry).Select(f => f.Fill));
            if (balance.Units.Sign < 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Why the linked fills cannot settle the Play, in the owner's words; empty when nothing stands in the way.
    /// Fewer exits than entries is ordinary progress, not a problem.
    /// </summary>
    public static List<string> Problems(string entrySide, IReadOnlyCollection<LinkedFill> fills, IEnumerable<string> incompleteOrders)
    {
        var problems = incompleteOrders.Distinct().Order(StringComparer.Ordinal)
            .Select(id => $"Order {id} is filled at the venue, but not all of its executions are retained yet.").ToList();
        var direction = entrySide == ExecutionFacts.Buy ? "long" : "short";
        problems.AddRange(fills.Where(f => Contradicts(f.Role, entrySide, f.Fill)).GroupBy(f => (f.Fill.ExternalOrderId, f.Fill.ContractId, Entry: f.Role == OrderLinkRole.Entry))
            .OrderBy(g => g.Key.ExternalOrderId, StringComparer.Ordinal).ThenBy(g => g.Key.ContractId, StringComparer.Ordinal).Select(g =>
                $"Order {g.Key.ExternalOrderId} has {string.Join(", ", g.Select(f => $"{f.Fill.Side} {f.Fill.PositionEffect}").Distinct())} fills, " +
                $"so it cannot be an {(g.Key.Entry ? "entry" : "exit")} of this {direction} play."));
        var counted = fills.Where(f => !Contradicts(f.Role, entrySide, f.Fill)).ToList();
        if (Quantity(counted.Where(f => f.Role != OrderLinkRole.Entry).Select(f => f.Fill))
            .CompareTo(Quantity(counted.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill))) > 0)
            problems.Add("Linked exits exceed the entered quantity; a fill shared with another play can be allocated between them.");
        else if (!Chronological(counted))
            problems.Add("A linked exit filled before the entry it would close.");
        return problems;
    }

    /// <summary>
    /// A closed Play whose linked fills settle it: nothing known missing, contradicting or out of order, and exits equal
    /// to entries. Only such outcomes are scored.
    /// </summary>
    public static bool Settled(ClosedPlayFacts facts)
    {
        var fills = facts.Fills;
        var entered = Quantity(fills.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill));
        return entered.Units.Sign > 0 && entered == Quantity(fills.Where(f => f.Role != OrderLinkRole.Entry).Select(f => f.Fill)) &&
            Problems(EntrySide(facts.CurrentPlan), fills, facts.IncompleteOrders ?? []).Count == 0;
    }
}
