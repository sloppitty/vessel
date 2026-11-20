using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Plays;
using Vessel.Domain.Common;

namespace Vessel.Tests;

/// <summary>
/// Test-only defaults for <see cref="IPlayStore"/> (issue #114): the mode-aware scope check ignores the mode, search and
/// rule writes are unsupported, there is no active rule or title, and a rule save is the ordinary save.
/// </summary>
public interface IPlayStoreDefaults : IPlayStore
{
    /// <summary>The fake's scope check, whatever the mode; production ports take the mode (issue #113).</summary>
    Task<bool> ScopeExistsAsync(ListScope scope, CancellationToken ct);

    Task<bool> IPlayStore.ScopeExistsAsync(ListScope scope, TradingMode tradingMode, CancellationToken ct) => ScopeExistsAsync(scope, ct);

    Task<PlaySearchResult> IPlayStore.SearchAsync(string pattern, TradingMode tradingMode, int offset, int limit, CancellationToken ct) =>
        throw new NotSupportedException();

    Task<PlayRule?> IPlayStore.ActiveRuleAsync(TradingMode tradingMode, CancellationToken ct) => Task.FromResult<PlayRule?>(null);

    Task<PlayRule> IPlayStore.AddRuleAsync(TradingMode tradingMode, string @if, string then, Guid? sourcePlayId, DateTimeOffset now,
        CancellationToken ct) => throw new NotSupportedException();

    Task<string?> IPlayStore.TitleAsync(Guid playId, TradingMode tradingMode, CancellationToken ct) => Task.FromResult<string?>(null);

    Task IPlayStore.SaveRuleAsync(CancellationToken ct) => SaveAsync(ct);
}
