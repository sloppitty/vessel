using System.Text.Json.Serialization;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Application.Venues;

namespace Vessel.Application.Workspace;

public sealed record AccountSettingsExpectation(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] long SettingsRevision);
public sealed record CreatePortfolioRequest(string Name, string? Icon = null, IReadOnlyList<Guid>? AccountIds = null,
    IReadOnlyList<AccountSettingsExpectation>? Expected = null, string TradingMode = "real");
public sealed record MoveAccountsRequest(
    [property: JsonRequired] IReadOnlyList<Guid>? AccountIds,
    [property: JsonRequired] Guid? PortfolioId,
    IReadOnlyList<AccountSettingsExpectation>? Expected = null);
public sealed record CreateAccountRequest(Guid? PortfolioId, string Name, string VenueId, string? Address = null, string? ManualAccountValueUsd = null,
    string? SourceId = null, string TradingMode = "real", string? StartingCapitalUsd = null);
/// <summary>Renames a portfolio and, when <see cref="Icon"/> is present, changes its icon; an omitted icon is kept.</summary>
public sealed record UpdatePortfolioRequest(string Name, string? Icon = null, string? TradingMode = null);
public sealed record UpdateAccountRequest(
    [property: JsonRequired] string Name,
    [property: JsonRequired] Guid? PortfolioId,
    [property: JsonRequired] bool IsEnabled,
    [property: JsonRequired] long ExpectedRevision, string? StartingCapitalUsd = null, string? TradingMode = null,
    bool? KeepExecutionHistory = null);
/// <summary>
/// TotalValueUsd sums primary perps (or manual) values. BalanceUsd sums account balances: perps plus stablecoin wallet,
/// or the wallet alone in unified and portfolio-margin modes, so no ledger is counted twice.
/// </summary>
public sealed record PortfolioDto(Guid Id, string Name, int AccountCount, string? TotalValueUsd, string ValueCoverage,
    string? BalanceUsd = null, string BalanceCoverage = "unavailable", string Icon = PortfolioIcons.Default,
    string? AvailableBalanceUsd = null, int AvailableBalanceAccountCount = 0,
    string AvailableBalanceCoverage = "unavailable", int EnabledAccountCount = 0, string TradingMode = "real");
public sealed record AccountDto(Guid Id, Guid? PortfolioId, string Name, string VenueId, string? Address,
    string? AccountValueUsd, DateTimeOffset? LastSyncedAtUtc, string SyncStatus, string? LastSyncError,
    int PositionCount, string? HistoryNotice, bool IsEnabled, long SettingsRevision,
    string? AvailableStablecoinNominalUsd = null, string? StablecoinScope = null, string? AccountMode = null,
    string? TotalStablecoinNominalUsd = null, string? BalanceUsd = null, string? SourceId = null,
    string? AvailableBalanceUsd = null, string? AvailableBalanceSource = null, string TradingMode = "real",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StartingCapitalUsd = null,
    bool KeepExecutionHistory = false);
public sealed record AccountInstrumentsDto(string VenueId, string MarketScope, string Scope,
    IReadOnlyList<VenueInstrument> Instruments, string Notice);
/// <summary>Where a catalogue comes from: the venue's selectable perpetuals, or none for a manual account.</summary>
public static class InstrumentCatalogScopes
{
    public const string Venue = "venue-perpetuals", Manual = "manual";
}
/// <summary>
/// One snapshot position. <paramref name="LiquidationPrice"/> and <paramref name="MarginMode"/> are the venue's own figures
/// (Phase 2, item 8a); null when it reports none.
/// </summary>
public sealed record PositionDto(string ContractId, string SignedQuantity, string EntryPrice,
    string UnrealizedPnlUsd, string MarginUsedUsd, int? Leverage, string? LiquidationPrice = null, string? MarginMode = null);
public sealed record SnapshotDto(DateTimeOffset ObservedAtUtc, string ValueScope, string? AccountValueUsd,
    string? WithdrawableUsd, string? MarginUsedUsd, IReadOnlyList<PositionDto> Positions,
    StablecoinWalletDto? StablecoinWallet = null, string? AvailableMarginUsd = null);
public sealed record StablecoinBalanceDto(string Symbol, int TokenIndex, string TokenId,
    string Total, string Held, string Available);
public sealed record StablecoinWalletDto(DateTimeOffset ObservedAtUtc, string AccountMode, string Scope,
    string TotalNominalUsd, string AvailableNominalUsd,
    IReadOnlyList<StablecoinBalanceDto> Balances, string Notice);
public sealed record FillDto(Guid Id, Guid AccountId, string ContractId, string Side, string Direction,
    string Price, string Quantity, string Fee, string FeeToken, string ClosedPnlUsd, DateTimeOffset OccurredAtUtc,
    string OrderId, string SourceFillId, string TransactionHash, string PositionEffect, string FeeBasis, string PnlBasis,
    Guid? PlayId = null);
/// <summary>
/// A retained fill the venue later reported with different facts (Phase 2, item 14c). The first copy is kept; only the
/// field names that differed are listed, never either value.
/// </summary>
public sealed record FillConflictDto(string ContractId, string SourceFillId, IReadOnlyList<string> Fields,
    DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc, int Reports);
public sealed record FillConflictsDto(int Total, IReadOnlyList<FillConflictDto> Recent, string Notice);

/// <summary>A span of execution history that may be missing (Phase 2, item 6); null ends are unknown.</summary>
public sealed record HistoryGapDto(Guid Id, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, DateTimeOffset OpenedAtUtc, DateTimeOffset? ClosedAtUtc);
/// <summary>One coverage record: an import, or a run of continuous imports of one source.</summary>
public sealed record FillCoverageDto(string Source, DateTimeOffset FirstObservedAtUtc, DateTimeOffset LastObservedAtUtc, int ImportCount,
    DateTimeOffset? RequestedFromUtc, DateTimeOffset RequestedToUtc, bool? Truncated, bool Complete,
    DateTimeOffset? CoveredFromUtc, DateTimeOffset? CoveredToUtc, DateTimeOffset? GapFromUtc, DateTimeOffset? GapToUtc);
/// <summary>
/// An account's history coverage (Phase 2, item 6): the opt-in, whether the venue allows it, the open gaps, the latest
/// closed ones and the latest coverage records.
/// </summary>
public sealed record HistoryCoverageDto(bool KeepExecutionHistory, bool Available, int OpenGapCount, IReadOnlyList<HistoryGapDto> OpenGaps,
    IReadOnlyList<HistoryGapDto> ClosedGaps, int RecordCount, IReadOnlyList<FillCoverageDto> Recent, string Notice);
/// <summary>What the store reads for <see cref="HistoryCoverageDto"/>, newest first and bounded.</summary>
public sealed record HistoryCoverageFacts(int OpenGapCount, IReadOnlyList<Vessel.Domain.Monitoring.AccountHistoryGap> OpenGaps,
    IReadOnlyList<Vessel.Domain.Monitoring.AccountHistoryGap> ClosedGaps, int RecordCount, IReadOnlyList<Vessel.Domain.Monitoring.FillCoverageRecord> Recent);
public sealed record OverviewTotals(int PortfolioCount, int AccountCount, string? TotalAccountValueUsd,
    int ValuedAccountCount, int OpenPositionCount, int ImportedFillCount,
    string? AvailableStablecoinNominalUsd = null, int StablecoinAccountCount = 0,
    string? TotalBalanceUsd = null, int BalanceAccountCount = 0, string BalanceCoverage = "unavailable",
    string? AvailableBalanceUsd = null, int AvailableBalanceAccountCount = 0, string AvailableBalanceCoverage = "unavailable",
    int EnabledAccountCount = 0);
public sealed record OverviewDto(IReadOnlyList<PortfolioDto> Portfolios, IReadOnlyList<AccountDto> Accounts,
    OverviewTotals Totals, IReadOnlyList<FillDto> RecentActivity, string ScopeNote);

// Application owns this bounded use-case port; EF and transaction details stay in Persistence.
public sealed record MarketDataAccount(bool IsEnabled, string VenueId, string TradingMode = "real");
public sealed record PaperAccountBalance(string CashUsd, string? EquityUsd, string? AvailableMarginUsd, int OpenPositionCount);

public interface IWorkspaceStore
{
    /// <summary>
    /// An account was disabled or enabled under the account locks: disabling withdraws its unread notifications and
    /// hides them, enabling shows them again. Stores without notifications ignore it.
    /// </summary>
    Task AccountVisibilityChangedAsync(Guid accountId, bool enabled, CancellationToken ct) => Task.CompletedTask;
    Task<List<Portfolio>> PortfoliosAsync(CancellationToken ct);
    Task<List<Account>> AccountsAsync(CancellationToken ct);
    async Task<List<Portfolio>> PortfoliosAsync(CancellationToken ct, string tradingMode = "real") =>
        (await PortfoliosAsync(ct)).Where(p => p.TradingMode == tradingMode).ToList();
    async Task<List<Account>> AccountsAsync(CancellationToken ct, string tradingMode = "real") =>
        (await AccountsAsync(ct)).Where(a => a.TradingMode == tradingMode).ToList();
    Task<IReadOnlyDictionary<Guid, PaperAccountBalance>> PaperBalancesAsync(IReadOnlyList<Guid> accountIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PaperAccountBalance>>(new Dictionary<Guid, PaperAccountBalance>());
    Task<Account?> AccountAsync(Guid id, CancellationToken ct);
    Task<MarketDataAccount?> MarketDataAccountAsync(Guid id, CancellationToken ct);
    Task<AccountSnapshot?> SnapshotAsync(Guid id, CancellationToken ct);
    Task<bool> SourceExistsAsync(string venueId, string sourceId, CancellationToken ct);
    Task<List<AccountSnapshot>> SnapshotsAsync(CancellationToken ct);
    Task<List<ImportedFill>> FillsAsync(Guid? accountId, int limit, CancellationToken ct);
    /// <summary>Retained fills the venue later reported differently (Phase 2, item 14c): the count and the latest ones.</summary>
    Task<(int Total, IReadOnlyList<FillImportConflict> Recent)> FillConflictsAsync(Guid accountId, int limit, CancellationToken ct) =>
        Task.FromResult<(int, IReadOnlyList<FillImportConflict>)>((0, []));
    /// <summary>Phase 2, item 6: the account's history gaps and coverage records, bounded.</summary>
    Task<HistoryCoverageFacts> HistoryCoverageAsync(Guid accountId, int limit, CancellationToken ct) =>
        Task.FromResult(new HistoryCoverageFacts(0, [], [], 0, []));
    Task<int> FillCountAsync(CancellationToken ct);
    Task AddPortfolioAsync(Portfolio portfolio, CancellationToken ct);
    Task AddAccountAsync(Account account, CancellationToken ct);
    // Locks require the caller's active unit of work and never complete it.
    Task<T> WithAccountLockAsync<T>(Guid id, Func<Account, Task<T>> action, CancellationToken ct);
    Task SaveRefreshAsync(Account account, PerpetualVenueReadResult result, CancellationToken ct);
    // Management serializes owner grouping changes before acquiring account row locks.
    // Sync takes only the account lock, so independent venue reads can still run concurrently.
    Task<T> WithManagementLockAsync<T>(Func<Task<T>> action, CancellationToken ct);
    Task DeletePortfolioAsync(Guid id, CancellationToken ct);
    Task DeleteAccountAsync(Account account, CancellationToken ct);
    // Intermediate persistence through the active unit of work, never a commit.
    Task SaveAsync(CancellationToken ct);
}
