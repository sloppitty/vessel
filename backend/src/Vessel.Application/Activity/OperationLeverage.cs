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
    private readonly record struct Observed(DateTimeOffset At, int Sign, int Leverage);

    public static List<ActivityOperationDto> Apply(IReadOnlyList<ActivityOperationDto> operations, ActivityFacts facts,
        ActivityPositions positions, DateTimeOffset now)
    {
        // Native identities that only observations carry, per canonical contract. Observations are not grouping evidence.
        var observedNative = facts.LeverageObservations.Where(o => o.VenueContractId is not null)
            .GroupBy(o => (o.AccountId, o.ContractId))
            .ToDictionary(g => g.Key, g => g.Select(o => o.VenueContractId!).Distinct(StringComparer.Ordinal).Count());
        // Sorted once per partition; each operation then reads only its own interval.
        var observed = facts.LeverageObservations
            .Select(o => (Key: Key(o, positions, observedNative), Item: new Observed(o.ObservedAtUtc, Math.Sign(o.SignedQuantity), o.Leverage)))
            .Concat(positions.Snapshots.Values.SelectMany(s => s.Positions.Where(p => p.Leverage is not null)
                .Select(p => (Key: positions.Key(s.AccountId, p), Item: new Observed(s.ObservedAtUtc, Math.Sign(p.SignedQuantity), p.Leverage!.Value)))))
            .GroupBy(o => o.Key)
            .ToDictionary(g => g.Key, g => g.Select(o => o.Item).OrderBy(o => o.At).ToArray());
        return operations.Select(operation =>
        {
            // Unknown boundaries or a market mix under one canonical key cannot place an observation.
            if (operation.Direction is null || positions.Quantity(operation.ContractKey) is null) return operation;
            var sign = operation.Direction == "long" ? 1 : -1;
            var from = operation.OpenedAtUtc ?? operation.FirstOccurredAtUtc;
            var to = operation.ClosedAtUtc ?? now;
            var range = new Range();
            if (observed.TryGetValue(operation.ContractKey, out var partition))
                for (var i = LowerBound(partition, from); i < partition.Length && partition[i].At <= to; i++)
                    if (partition[i].Sign == sign) range.See(partition[i].At, partition[i].Leverage);
            foreach (var (at, leverage) in operation.FillLeverages) range.See(at, leverage);
            return range.Count == 0 ? operation : operation with { Leverage = range.Latest, LeverageMin = range.Min, LeverageMax = range.Max };
        }).ToList();
    }

    /// <summary>
    /// The shared resolver's partition for an observation. When fills, snapshot and funding carry no native identity for
    /// the canonical contract (ID-less legacy fills), operations use the canonical partition; an observation of the one
    /// native market observed for it belongs there too. Several observed native markets stay apart, so they place nothing.
    /// </summary>
    private static ActivityContractKey Key(LeverageObservation o, ActivityPositions positions, Dictionary<(Guid, string), int> observedNative) =>
        o.VenueContractId is not null && !positions.HasNativeIdentity(o.AccountId, o.ContractId) && observedNative[(o.AccountId, o.ContractId)] == 1
            ? new(o.AccountId, o.ContractId, false)
            : positions.Key(o);

    private static int LowerBound(Observed[] sorted, DateTimeOffset from)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sorted[mid].At < from) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Lowest, highest and latest leverage seen; conflicting values at the latest time leave the latest unknown.</summary>
    private sealed class Range
    {
        private DateTimeOffset latestAt;
        private bool conflict;
        public int Count { get; private set; }
        public int? Latest { get; private set; }
        public int Min { get; private set; } = int.MaxValue;
        public int Max { get; private set; } = int.MinValue;

        public void See(DateTimeOffset at, int leverage)
        {
            Min = Math.Min(Min, leverage);
            Max = Math.Max(Max, leverage);
            if (Count++ == 0 || at > latestAt) (latestAt, conflict) = (at, false);
            else if (at < latestAt) return;
            else conflict |= Latest != leverage;
            Latest = conflict ? null : leverage;
        }
    }
}
