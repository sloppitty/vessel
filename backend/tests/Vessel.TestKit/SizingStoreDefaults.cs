using Vessel.Application.Sizing;
using Vessel.Domain.Sizing;
using Vessel.Domain.Common;

namespace Vessel.Tests;

/// <summary>
/// Test-only defaults for <see cref="ISizingStore"/> (issue #114): the trading-mode and scoped closed-Play reads filtered in
/// memory from the full read, no allocated fills, and a single-flow settings-for-update that adds the initial row when absent.
/// </summary>
public interface ISizingStoreDefaults : ISizingStore
{
    /// <summary>Every closed Play the fake holds, of either mode; production ports take the mode (issue #113).</summary>
    Task<List<ClosedPlayFacts>> ClosedPlaysAsync(CancellationToken ct);

    async Task<List<ClosedPlayFacts>> ISizingStore.ClosedPlaysAsync(TradingMode tradingMode, CancellationToken ct) =>
        (await ClosedPlaysAsync(ct)).Where(f => f.Play.TradingMode == tradingMode).ToList();

    async Task<List<ClosedPlayFacts>> ISizingStore.ClosedPlaysAsync(TradingMode tradingMode, IReadOnlyCollection<Guid> accountIds, string? instrument,
        CancellationToken ct) =>
        (await ClosedPlaysAsync(tradingMode, ct)).Where(f => accountIds.Contains(f.Play.AccountId) &&
            (instrument is null || string.Equals(f.Play.ContractId, instrument, StringComparison.Ordinal))).ToList();

    Task<IReadOnlySet<Guid>> ISizingStore.AllocatedFillIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    async Task<SizingSettings> ISizingStore.SettingsForUpdateAsync(SizingSettings initial, CancellationToken ct)
    {
        if (await SettingsAsync(ct) is { } settings) return settings;
        Add(initial);
        return initial;
    }
}
