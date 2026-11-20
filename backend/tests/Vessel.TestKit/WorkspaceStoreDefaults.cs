using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Test-only defaults for <see cref="IWorkspaceStore"/> (issue #114): visibility changes are ignored, mode-aware lists are
/// filtered from the full lists, there are no paper balances, conflicts or history coverage, and backfill and funding
/// imports are unsupported.
/// </summary>
public interface IWorkspaceStoreDefaults : IWorkspaceStore
{
    Task IWorkspaceStore.AccountVisibilityChangedAsync(Guid accountId, bool enabled, CancellationToken ct) => Task.CompletedTask;

    async Task<List<Portfolio>> IWorkspaceStore.PortfoliosAsync(CancellationToken ct, string tradingMode) =>
        (await PortfoliosAsync(ct)).Where(p => p.TradingMode == tradingMode).ToList();

    async Task<List<Account>> IWorkspaceStore.AccountsAsync(CancellationToken ct, string tradingMode) =>
        (await AccountsAsync(ct)).Where(a => a.TradingMode == tradingMode).ToList();

    Task<IReadOnlyDictionary<Guid, PaperAccountBalance>> IWorkspaceStore.PaperBalancesAsync(IReadOnlyList<Guid> accountIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PaperAccountBalance>>(new Dictionary<Guid, PaperAccountBalance>());

    Task<(int Total, IReadOnlyList<FillImportConflict> Recent)> IWorkspaceStore.FillConflictsAsync(Guid accountId, int limit, CancellationToken ct) =>
        Task.FromResult<(int, IReadOnlyList<FillImportConflict>)>((0, []));

    Task<HistoryCoverageFacts> IWorkspaceStore.HistoryCoverageAsync(Guid accountId, int limit, CancellationToken ct) =>
        Task.FromResult(new HistoryCoverageFacts(0, [], [], 0, []));

    Task<IReadOnlyList<ImportedFill>> IWorkspaceStore.SaveHistoricalFillsAsync(Account account, IReadOnlyList<VenueFill> fills,
        DateTimeOffset observedAt, CancellationToken ct, IReadOnlyList<VenueOmission>? omissions) =>
        throw new NotSupportedException("This store cannot import backfilled fills.");

    Task IWorkspaceStore.SaveFundingAsync(Account account, VenueFundingReadResult result, CancellationToken ct) =>
        throw new NotSupportedException("This store cannot import funding on its own.");
}
