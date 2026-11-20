using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Test-only defaults for <see cref="IDiaryStore"/> (issue #114): targeted reads filtered from the whole-account reads, and
/// no allocated fills.
/// </summary>
public interface IDiaryStoreDefaults : IDiaryStore
{
    async Task<List<ImportedOrder>> IDiaryStore.OrdersAsync(Guid accountId, IReadOnlyCollection<OrderKey> orders, CancellationToken ct) =>
        (await OrdersAsync(accountId, ct)).Where(o => orders.Contains(OrderKey.Of(o))).ToList();

    async Task<List<PlayOrderLink>> IDiaryStore.LinksAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds, CancellationToken ct) =>
        (await LinksAsync(accountId, ct)).Where(l => orderIds.Contains(l.ImportedOrderId)).ToList();

    Task<IReadOnlySet<Guid>> IDiaryStore.AllocatedFillIdsAsync(IReadOnlyCollection<Guid> fillIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}
