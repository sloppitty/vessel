using Vessel.Domain.Workspace;

namespace Vessel.Application.Activity;

/// <summary>
/// Attributes venue-reported position leverage to operations. Sources are leverage the venue reported on the
/// operation's own executions, retained refresh observations and the latest snapshot. An observation counts only on
/// the operation's account and native-contract partition, with the operation's direction, observed between its opening
/// (or first retained execution when opening entries are missing) and its close, or now while it is open. Leverage is
/// never inferred from margin, notional, a plan or the venue maximum, and never averaged; unknown stays null.
/// </summary>
internal static class OperationLeverage
{
    public static List<ActivityOperationDto> Apply(IReadOnlyList<ActivityOperationDto> operations, ActivityFacts facts,
        ActivityPositions positions, DateTimeOffset now)
    {
        var observed = facts.LeverageObservations
            .Select(o => (Key: positions.Key(o), At: o.ObservedAtUtc, Sign: Math.Sign(o.SignedQuantity), o.Leverage))
            .Concat(positions.Snapshots.Values.SelectMany(s => s.Positions.Where(p => p.Leverage is not null)
                .Select(p => (Key: positions.Key(s.AccountId, p), At: s.ObservedAtUtc, Sign: Math.Sign(p.SignedQuantity), Leverage: p.Leverage!.Value))))
            .ToLookup(o => o.Key);
        return operations.Select(operation =>
        {
            // Unknown boundaries or a market mix under one canonical key cannot place an observation.
            if (operation.Direction is null || positions.Quantity(operation.ContractKey) is null) return operation;
            var sign = operation.Direction == "long" ? 1 : -1;
            var from = operation.OpenedAtUtc ?? operation.FirstOccurredAtUtc;
            var to = operation.ClosedAtUtc ?? now;
            var seen = observed[operation.ContractKey].Where(o => o.Sign == sign && o.At >= from && o.At <= to)
                .Select(o => (o.At, o.Leverage)).Concat(operation.FillLeverages).ToList();
            if (seen.Count == 0) return operation;
            var latest = seen.OrderByDescending(o => o.At).ThenByDescending(o => o.Leverage).First();
            return operation with
            {
                Leverage = latest.Leverage, LeverageMin = seen.Min(o => o.Leverage), LeverageMax = seen.Max(o => o.Leverage)
            };
        }).ToList();
    }
}
