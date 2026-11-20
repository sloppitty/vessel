using Vessel.Application.Activity;
using Vessel.Domain.Common;

namespace Vessel.Tests;

/// <summary>Test-only default for <see cref="IActivityStore"/> (issue #114): the mode-aware read ignores the mode.</summary>
public interface IActivityStoreDefaults : IActivityStore
{
    /// <summary>The fake's facts; production ports take the mode (issue #113).</summary>
    Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null);

    Task<ActivityFacts> IActivityStore.ReadAsync(Guid? accountId, TradingMode tradingMode, CancellationToken ct, IReadOnlySet<Guid>? accountIds) =>
        ReadAsync(accountId, ct, accountIds);
}

/// <summary>
/// Test-only default for <see cref="IActivitySummaryReader"/> (issue #114): real mode returns the plain read, other modes
/// aggregate its facts again for that mode.
/// </summary>
public interface IActivitySummaryReaderDefaults : IActivitySummaryReader
{
    /// <summary>The fake's real-mode summaries; production ports take the mode (issue #113).</summary>
    Task<ActivitySummaries> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null);

    async Task<ActivitySummaries> IActivitySummaryReader.ReadAsync(Guid? accountId, TradingMode tradingMode, CancellationToken ct,
        IReadOnlySet<Guid>? accountIds)
    {
        var value = await ReadAsync(accountId, ct, accountIds);
        return tradingMode == TradingMode.Real ? value : ActivityService.Aggregate(value.Facts, tradingMode, accountId, accountIds);
    }
}
