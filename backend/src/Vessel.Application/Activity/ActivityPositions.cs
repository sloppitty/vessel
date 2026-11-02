using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Activity;

internal readonly record struct ActivityContractKey(Guid AccountId, string Identity, bool IsNative);

/// <summary>
/// Phase 2, item 7b: an omitted execution in a partition. The walk loses its position from <paramref name="ResetAt"/>, stays
/// uncertain through <paramref name="AnchorAfter"/> (nothing at or before it proves flatness or direction), and a snapshot
/// can anchor only fills after it. Both are the execution's time when it is known; otherwise the read window it lies in
/// bounds it on either side, and an unknown bound is unbounded.
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
        IReadOnlyList<FundingPayment>? funding = null, IReadOnlyList<ActivityOmission>? omissions = null)
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
        IndexBreaks(fills, omissions ?? []);
    }

    /// <summary>
    /// Phase 2, item 7b: the omitted executions that may lie in a partition, ordered by <see cref="ActivityBreak.ResetAt"/>.
    /// See <see cref="IndexBreaks"/>.
    /// </summary>
    public IReadOnlyList<ActivityBreak> Breaks(ActivityContractKey key) => breaks.GetValueOrDefault(key) ?? [];

    /// <summary>
    /// Phase 2, item 7b (Sol round 1): a marker breaks every partition it could belong to, conservatively, and never moves a
    /// fill between partitions (markers are not grouping evidence). An ID-less fill's native market is unknown, so a marker
    /// on its canonical contract may lie in its partition whatever native market the marker names.
    /// <list type="bullet">
    /// <item>No market at all: every partition of its account.</item>
    /// <item>A canonical contract only: every partition holding a fill of that contract, native ones included.</item>
    /// <item>A native market: that native partition, and every partition holding an ID-less fill of its canonical contract.
    /// Without a canonical contract (a market Vessel does not recognize), the native ID is the market's identity, as for an
    /// ID-less fact under the order rule (<see cref="ExecutionOmission.Names"/>), so an ID-less fill keyed by it is included.</item>
    /// </list>
    /// </summary>
    private void IndexBreaks(IReadOnlyList<ImportedFill> fills, IReadOnlyList<ActivityOmission> omissions)
    {
        if (omissions.Count == 0) return;
        var byAccount = new Dictionary<Guid, HashSet<ActivityContractKey>>();
        var byContract = new Dictionary<(Guid, string), HashSet<ActivityContractKey>>();
        var idless = new Dictionary<(Guid, string), HashSet<ActivityContractKey>>();
        static void Add<T>(Dictionary<T, HashSet<ActivityContractKey>> index, T at, ActivityContractKey key) where T : notnull
        {
            if (!index.TryGetValue(at, out var keys)) index[at] = keys = [];
            keys.Add(key);
        }
        foreach (var fill in fills)
        {
            var key = Key(fill);
            Add(byAccount, fill.AccountId, key);
            Add(byContract, (fill.AccountId, fill.ContractId), key);
            if (fill.VenueContractId is null) Add(idless, (fill.AccountId, fill.ContractId), key);
        }
        IEnumerable<ActivityContractKey> Affected(ActivityOmission m) => (m.ContractId, m.VenueContractId) switch
        {
            (null, null) => byAccount.GetValueOrDefault(m.AccountId) ?? [],
            ({ } contract, null) => byContract.GetValueOrDefault((m.AccountId, contract)) ?? [],
            (var contract, { } native) => (idless.GetValueOrDefault((m.AccountId, contract ?? native)) ?? [])
                .Append(new ActivityContractKey(m.AccountId, native, true))
        };
        var found = new Dictionary<ActivityContractKey, List<ActivityBreak>>();
        foreach (var marker in omissions)
        {
            var at = new ActivityBreak(marker.OccurredAtUtc ?? marker.UncertainFromUtc ?? DateTimeOffset.MinValue,
                marker.OccurredAtUtc ?? marker.UncertainToUtc ?? DateTimeOffset.MaxValue);
            foreach (var key in Affected(marker).Distinct())
            {
                if (!found.TryGetValue(key, out var list)) found[key] = list = [];
                list.Add(at);
            }
        }
        foreach (var (key, list) in found) breaks[key] = list.OrderBy(b => b.ResetAt).ThenBy(b => b.AnchorAfter).ToArray();
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
