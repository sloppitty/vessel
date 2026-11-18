using Vessel.Application.Plays.Execution;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Test-only defaults for <see cref="IPlayExecutionStore"/> (issue #114). The port declares every member abstract so a
/// production store cannot silently miss one; fakes that implement only the whole-account reads implement this instead and
/// get the targeted reads from them, and no allocations, flips, ledgers or venue risk.
/// </summary>
public interface IPlayExecutionStoreDefaults : IPlayExecutionStore
{
    async Task<List<ImportedOrder>> IPlayExecutionStore.OrdersAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds,
        IReadOnlyDictionary<string, DateTimeOffset> placedSince, CancellationToken ct) =>
        (await OrdersAsync(accountId, ct)).Where(o => IPlayExecutionStore.IncludesOrder(o, orderIds, placedSince)).ToList();

    async Task<ImportedOrder?> IPlayExecutionStore.OrderAsync(Guid accountId, string contractId, string externalOrderId, CancellationToken ct) =>
        (await OrdersAsync(accountId, ct)).FirstOrDefault(o => o.ContractId == contractId && o.ExternalOrderId == externalOrderId);

    async Task<List<PlayOrderLink>> IPlayExecutionStore.LinksAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds,
        IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
        (await LinksAsync(accountId, ct)).Where(l => orderIds.Contains(l.ImportedOrderId) || playIds.Contains(l.PlayId)).ToList();

    async Task<List<PlayOrderLink>> IPlayExecutionStore.PlayLinksAsync(Guid accountId, Guid playId, CancellationToken ct) =>
        (await LinksAsync(accountId, ct)).Where(l => l.PlayId == playId).ToList();

    async Task<PlayOrderLink?> IPlayExecutionStore.LinkAsync(Guid accountId, Guid playId, Guid linkId, CancellationToken ct) =>
        (await LinksAsync(accountId, ct)).FirstOrDefault(l => l.Id == linkId && l.PlayId == playId);

    async Task<HashSet<Guid>> IPlayExecutionStore.SuggestedElsewhereAsync(Guid accountId, Guid playId, IReadOnlyCollection<Guid> orderIds,
        CancellationToken ct)
    {
        if (orderIds.Count == 0) return [];
        var others = (await ActivePlaysAsync(accountId, ct)).Where(p => p.Id != playId).Select(p => p.Id).ToHashSet();
        if (others.Count == 0) return [];
        return (await LinksAsync(accountId, orderIds, [], ct)).Where(l => l.State == OrderLinkState.Suggested && others.Contains(l.PlayId))
            .Select(l => l.ImportedOrderId).ToHashSet();
    }

    async Task<HashSet<Guid>> IPlayExecutionStore.LinkedOrderIdsAsync(Guid accountId, CancellationToken ct) =>
        (await LinksAsync(accountId, ct)).Where(l => l.State == OrderLinkState.Linked).Select(l => l.ImportedOrderId).ToHashSet();

    Task<VenueRiskDto?> IPlayExecutionStore.PositionAsync(Guid accountId, string contractId, string? venueContractId, int direction,
        CancellationToken ct) => Task.FromResult<VenueRiskDto?>(null);

    Task<VenueRiskDto?> IPlayExecutionStore.RiskSinceAsync(Guid accountId, string contractId, string? venueContractId, int direction,
        DateTimeOffset since, DateTimeOffset? until, CancellationToken ct) => Task.FromResult<VenueRiskDto?>(null);

    Task<FundingEvidence?> IPlayExecutionStore.FundingEvidenceAsync(FundingEvidenceRequest request, CancellationToken ct) =>
        Task.FromResult<FundingEvidence?>(null);

    Task<List<AllocatedFill>> IPlayExecutionStore.AllocationsAsync(Guid accountId, IReadOnlyCollection<Guid> playIds,
        IReadOnlyCollection<Guid> fillIds, CancellationToken ct) => Task.FromResult(new List<AllocatedFill>());

    Task<FillAllocation?> IPlayExecutionStore.AllocationForUpdateAsync(Guid accountId, Guid fillId, CancellationToken ct) =>
        Task.FromResult<FillAllocation?>(null);

    Task<ImportedFill?> IPlayExecutionStore.FillAsync(Guid accountId, Guid fillId, CancellationToken ct) => Task.FromResult<ImportedFill?>(null);

    Task<List<ImportedFill>> IPlayExecutionStore.FlipFillsAsync(Guid accountId, string contractId, string side, DateTimeOffset since,
        int limit, CancellationToken ct) => Task.FromResult(new List<ImportedFill>());

    Task<List<Play>> IPlayExecutionStore.AllocationCandidatesAsync(Guid accountId, string contractId, IReadOnlyCollection<Guid> named,
        CancellationToken ct) => Task.FromResult(new List<Play>());

    Task<List<Play>> IPlayExecutionStore.PlaysAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult(new List<Play>());

    void IPlayExecutionStore.Add(FillAllocation allocation) => throw new NotSupportedException("This store keeps no allocations.");

    void IPlayExecutionStore.Add(FillAllocationRevision revision) => throw new NotSupportedException("This store keeps no allocations.");
}
