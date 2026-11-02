using Vessel.Application.Common;
using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Activity;

/// <summary>
/// Phase 2, item 9b: the indexes the funding proof beside a Play reads, built once per Activity aggregate and kept with it
/// in the facts-revision cache, so polling a Play's execution view never rebuilds them over the account's history.
/// </summary>
internal sealed class ActivityProofIndex
{
    private readonly Dictionary<ActivityContractKey, PartitionFills> partitions;
    private readonly ILookup<ActivityContractKey, ActivityOperationDto> operations;
    private readonly ILookup<OrderKey, ImportedFill> byOrder;
    private readonly ILookup<Guid, ImportedFill> byPlay;
    private readonly HashSet<(Guid AccountId, string ContractId)> unresolved = [];

    public ActivityPositions Positions { get; }

    public ActivityProofIndex(ActivitySummaries summaries)
    {
        var facts = summaries.Facts;
        Positions = summaries.Positions ?? new ActivityPositions(facts.Fills, facts.Snapshots, facts.FundingPayments, facts.Omissions);
        partitions = facts.Fills.GroupBy(Positions.Key).ToDictionary(g => g.Key, g => new PartitionFills(g));
        operations = summaries.Operations.ToLookup(o => o.ContractKey);
        byOrder = facts.Fills.ToLookup(OrderKey.Of);
        byPlay = facts.Fills.Where(f => facts.FillPlayIds.ContainsKey(f.Id)).ToLookup(f => facts.FillPlayIds[f.Id]);
        // A canonical contract whose ID-less facts stay in the unresolved partition: they could be any of its native markets.
        foreach (var key in facts.Fills.Select(Positions.Key).Concat(facts.FundingPayments.Select(Positions.Key)))
            if (!key.IsNative && Positions.HasNativeIdentity(key.AccountId, key.Identity)) unresolved.Add((key.AccountId, key.Identity));
    }

    /// <summary>The partition's retained fills, by time; null when it holds none.</summary>
    public PartitionFills? Partition(ActivityContractKey key) => partitions.GetValueOrDefault(key);
    public IEnumerable<ActivityOperationDto> Operations(ActivityContractKey key) => operations[key];
    /// <summary>An order's retained fills, by the shared <see cref="OrderKey"/> rule.</summary>
    public IEnumerable<ImportedFill> FillsOf(OrderKey order) => byOrder[order];
    /// <summary>The fills linked to a real Play, as the aggregate's confirmed links assign them.</summary>
    public IEnumerable<ImportedFill> FillsOfPlay(Guid playId) => byPlay[playId];
    /// <summary>Whether ID-less facts of this canonical contract could belong to any of its native markets.</summary>
    public bool Unresolved(Guid accountId, string contractId) => unresolved.Contains((accountId, contractId));
}

/// <summary>One partition's retained fills in time order, with signed prefix sums (buy adds, sell subtracts).</summary>
internal sealed class PartitionFills
{
    private readonly ImportedFill[] fills;
    private readonly ExactAmount[] prefix;

    public PartitionFills(IEnumerable<ImportedFill> source)
    {
        fills = source.OrderBy(f => f.OccurredAtUtc).ThenBy(f => f.Id).ToArray();
        prefix = new ExactAmount[fills.Length + 1];
        for (var i = 0; i < fills.Length; i++)
            prefix[i + 1] = fills[i].Side == ExecutionFacts.Buy ? prefix[i] + ExactAmount.From(fills[i].Quantity) : prefix[i] - ExactAmount.From(fills[i].Quantity);
        AllPositive = fills.All(f => f.Quantity > 0);
        Contracts = fills.Select(f => f.ContractId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The canonical contracts its fills carry (one, unless a catalogue rename relabelled a native market).</summary>
    public IReadOnlySet<string> Contracts { get; }

    public DateTimeOffset Last => fills[^1].OccurredAtUtc;
    public bool AllPositive { get; }

    /// <summary>The fills at or after <paramref name="from"/> and at or before <paramref name="to"/>.</summary>
    public IEnumerable<ImportedFill> Between(DateTimeOffset from, DateTimeOffset to)
    {
        for (var i = First(from); i < fills.Length && fills[i].OccurredAtUtc <= to; i++) yield return fills[i];
    }

    /// <summary>The signed sum of the fills before <paramref name="at"/>, and of those at or after it.</summary>
    public ExactAmount Before(DateTimeOffset at) => prefix[First(at)];
    public ExactAmount From(DateTimeOffset at) => prefix[^1] - prefix[First(at)];

    // The first fill at or after the time.
    private int First(DateTimeOffset at)
    {
        int low = 0, high = fills.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (fills[middle].OccurredAtUtc < at) low = middle + 1; else high = middle;
        }
        return low;
    }
}
