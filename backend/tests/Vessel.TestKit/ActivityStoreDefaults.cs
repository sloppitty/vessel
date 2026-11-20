using Vessel.Application.Activity;

namespace Vessel.Tests;

/// <summary>Test-only default for <see cref="IActivityStore"/> (issue #114): the mode-aware read ignores the mode.</summary>
public interface IActivityStoreDefaults : IActivityStore
{
    Task<ActivityFacts> IActivityStore.ReadAsync(Guid? accountId, string tradingMode, CancellationToken ct, IReadOnlySet<Guid>? accountIds) =>
        ReadAsync(accountId, ct, accountIds);
}

/// <summary>
/// Test-only default for <see cref="IActivitySummaryReader"/> (issue #114): real mode returns the plain read, other modes
/// aggregate its facts again for that mode.
/// </summary>
public interface IActivitySummaryReaderDefaults : IActivitySummaryReader
{
    async Task<ActivitySummaries> IActivitySummaryReader.ReadAsync(Guid? accountId, string tradingMode, CancellationToken ct,
        IReadOnlySet<Guid>? accountIds)
    {
        var value = await ReadAsync(accountId, ct, accountIds);
        return tradingMode == "real" ? value : ActivityService.Aggregate(value.Facts, accountId, accountIds, tradingMode: tradingMode);
    }
}
