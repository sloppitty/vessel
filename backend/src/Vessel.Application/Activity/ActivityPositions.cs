using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Activity;

internal readonly record struct ActivityContractKey(Guid AccountId, string Identity, bool IsNative);

/// <summary>One shared index for grouping, anchoring and comparison, including legacy identities.</summary>
internal sealed class ActivityPositions
{
    public const string AmbiguousIdentityNotice = "Missing or ambiguous native contract identity across markets sharing a canonical key; unresolved facts cannot use snapshot anchoring or comparison.";
    public IReadOnlyDictionary<Guid, AccountSnapshot> Snapshots { get; }
    public Dictionary<ActivityContractKey, string> Contracts { get; } = [];
    private readonly Dictionary<ActivityContractKey, ExactAmount?> quantities = [];
    private readonly Dictionary<(Guid AccountId, string ContractId), ActivityContractKey[]> nativeCandidates;

    public ActivityPositions(IReadOnlyList<ImportedFill> fills, IReadOnlyList<AccountSnapshot> snapshots,
        IReadOnlyList<FundingPayment>? funding = null)
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
    }

    public ActivityContractKey Key(ImportedFill fill) => Key(fill.AccountId, fill.ContractId, fill.VenueContractId);
    public ActivityContractKey Key(FundingPayment payment) => Key(payment.AccountId, payment.ContractId, payment.VenueContractId);
    public ActivityContractKey Key(LeverageObservation observation) => Key(observation.AccountId, observation.ContractId, observation.VenueContractId);
    public ActivityContractKey Key(Guid accountId, AccountPosition position) => Key(accountId, position.ContractId, position.VenueContractId);

    private ActivityContractKey Key(Guid accountId, string contractId, string? nativeId) => nativeId is not null
        ? new(accountId, nativeId, true)
        : nativeCandidates.GetValueOrDefault((accountId, contractId)) is { Length: 1 } candidates
            ? candidates[0]
            : new(accountId, contractId, false);

    // Only a resolved contract absent from a coherent snapshot is flat; ambiguous identities stay unknown.
    public ExactAmount? Quantity(ActivityContractKey key) => quantities.TryGetValue(key, out var quantity) ? quantity : default(ExactAmount);
}
