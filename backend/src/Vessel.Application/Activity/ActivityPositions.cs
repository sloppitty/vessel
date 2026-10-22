using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Activity;

internal readonly record struct ActivityContractKey(Guid AccountId, string Identity, bool IsNative);

/// <summary>
/// Phase 2, item 7b: an omitted execution in a partition. The walk loses its position from <paramref name="ResetAt"/>, and
/// a snapshot can anchor only fills after <paramref name="AnchorAfter"/>. Both are the execution's time when it is known;
/// otherwise the read window it lies in bounds it on either side.
/// </summary>
internal readonly record struct ActivityBreak(DateTimeOffset ResetAt, DateTimeOffset AnchorAfter);

/// <summary>One shared index for grouping, anchoring and comparison, including legacy identities.</summary>
internal sealed class ActivityPositions
{
    public const string AmbiguousIdentityNotice = "Missing or ambiguous native contract identity across markets sharing a canonical key; unresolved facts cannot use snapshot anchoring or comparison.";
    public IReadOnlyDictionary<Guid, AccountSnapshot> Snapshots { get; }
    public Dictionary<ActivityContractKey, string> Contracts { get; } = [];
    private readonly Dictionary<ActivityContractKey, ExactAmount?> quantities = [];
    private readonly Dictionary<(Guid AccountId, string ContractId), ActivityContractKey[]> nativeCandidates;

    private readonly Dictionary<ActivityContractKey, ActivityBreak[]> breaks = [];

    public ActivityPositions(IReadOnlyList<ImportedFill> fills, IReadOnlyList<AccountSnapshot> snapshots,
        IReadOnlyList<FundingPayment>? funding = null, IReadOnlyList<ExecutionOmission>? omissions = null)
    {
        Snapshots = snapshots.ToDictionary(s => s.AccountId);
        nativeCandidates = fills.Where(f => f.VenueContractId is not null)
            .Select(f => (f.AccountId, f.ContractId, Key: new ActivityContractKey(f.AccountId, f.VenueContractId!, true)))
            .Concat(Snapshots.Values.SelectMany(s => s.Positions.Where(p => p.VenueContractId is not null)
                .Select(p => (s.AccountId, p.ContractId, Key: new ActivityContractKey(s.AccountId, p.VenueContractId!, true)))))
            .Concat((funding ?? []).Where(p => p.VenueContractId is not null)
                .Select(p => (p.AccountId, p.ContractId, Key: new ActivityContractKey(p.AccountId, p.VenueContractId!, true))))
            .GroupBy(p => (p.AccountId, p.ContractId))
            .ToDictionary(g => g.Key, g => g.Select(p => p.Key).Distinct().ToArray());
        foreach (var fill in fills)
        {
            var key = Key(fill);
            Contracts[key] = fill.ContractId;
            // Missing identity is not evidence of a separate, absent (flat) market.
            if (!key.IsNative && nativeCandidates.GetValueOrDefault((fill.AccountId, fill.ContractId)) is { Length: > 1 })
                quantities[key] = null;
        }
        foreach (var snapshot in Snapshots.Values)
        foreach (var position in snapshot.Positions)
        {
            var candidates = nativeCandidates.GetValueOrDefault((snapshot.AccountId, position.ContractId));
            var keys = position.VenueContractId is null && candidates is { Length: > 1 }
                ? candidates
                : [Key(snapshot.AccountId, position.ContractId, position.VenueContractId)];
            foreach (var key in keys)
            {
                Contracts[key] = position.ContractId;
                // Never assign one legacy position to several native markets.
                if (!quantities.TryAdd(key, keys.Length == 1 ? ExactAmount.From(position.SignedQuantity) : null))
                    quantities[key] = null;
            }
        }
        IndexBreaks(omissions ?? []);
    }

    /// <summary>
    /// Phase 2, item 7b: the omitted executions of a partition, by the same identity rule as its fills. An omission on a
    /// market Vessel does not recognize has no canonical contract and belongs to no partition.
    /// </summary>
    public IReadOnlyList<ActivityBreak> Breaks(ActivityContractKey key) => breaks.GetValueOrDefault(key) ?? [];

    private void IndexBreaks(IReadOnlyList<ExecutionOmission> omissions)
    {
        foreach (var group in omissions.Where(m => m.ContractId is not null).GroupBy(m => Key(m.AccountId, m.ContractId!, m.VenueContractId)))
            breaks[group.Key] = group.Select(m => new ActivityBreak(m.OccurredAtUtc ?? m.UncertainFromUtc ?? DateTimeOffset.MinValue,
                m.OccurredAtUtc ?? m.UncertainToUtc ?? DateTimeOffset.MaxValue)).OrderBy(b => b.ResetAt).ToArray();
    }

    public ActivityContractKey Key(ImportedFill fill) => Key(fill.AccountId, fill.ContractId, fill.VenueContractId);
    public ActivityContractKey Key(FundingPayment payment) => Key(payment.AccountId, payment.ContractId, payment.VenueContractId);
    public ActivityContractKey Key(LeverageObservation observation) => Key(observation.AccountId, observation.ContractId, observation.VenueContractId);
    public ActivityContractKey Key(Guid accountId, AccountPosition position) => Key(accountId, position.ContractId, position.VenueContractId);

    public ActivityContractKey Key(Guid accountId, string contractId, string? nativeId) => nativeId is not null
        ? new(accountId, nativeId, true)
        : nativeCandidates.GetValueOrDefault((accountId, contractId)) is { Length: 1 } candidates
            ? candidates[0]
            : new(accountId, contractId, false);

    /// <summary>Whether fills, the latest snapshot or funding carry a native identity for this canonical contract.</summary>
    public bool HasNativeIdentity(Guid accountId, string contractId) => nativeCandidates.ContainsKey((accountId, contractId));

    /// <summary>The native identities fills, the latest snapshot or funding carry for this canonical contract.</summary>
    public IEnumerable<string> NativeIds(Guid accountId, string contractId) =>
        nativeCandidates.GetValueOrDefault((accountId, contractId))?.Select(k => k.Identity) ?? [];

    // Only a resolved contract absent from a coherent snapshot is flat; ambiguous identities stay unknown.
    public ExactAmount? Quantity(ActivityContractKey key) => quantities.TryGetValue(key, out var quantity) ? quantity : default(ExactAmount);
}
