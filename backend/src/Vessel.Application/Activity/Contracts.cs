using System.Numerics;
using System.Text.Json.Serialization;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;
using Vessel.Application.Sizing;

namespace Vessel.Application.Activity;

public sealed record ActivityFacts(IReadOnlyList<Account> Accounts, IReadOnlyList<ImportedFill> Fills,
    IReadOnlyList<AccountSnapshot> Snapshots, IReadOnlyDictionary<Guid, Guid> FillPlayIds)
{
    public IReadOnlyList<FundingPayment> FundingPayments { get; init; } = [];
    public IReadOnlyList<AccountFundingState> FundingStates { get; init; } = [];
    public IReadOnlyList<LeverageObservation> LeverageObservations { get; init; } = [];
    public IReadOnlyList<PaperPlayFacts> PaperPlays { get; init; } = [];
}
public interface IActivitySummaryReader
{
    Task<ActivitySummaries> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null);
    async Task<ActivitySummaries> ReadAsync(Guid? accountId, string tradingMode, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null)
    {
        var value = await ReadAsync(accountId, ct, accountIds);
        return tradingMode == "real" ? value : ActivityService.Aggregate(value.Facts, accountId, accountIds, tradingMode: tradingMode);
    }
}

public interface IActivityStore
{
    // All retained facts, not the overview's most recent 100; owner-scoped and enabled accounts only.
    // An optional account set restricts every fact query; an empty set reads no facts.
    Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null);
    Task<ActivityFacts> ReadAsync(Guid? accountId, string tradingMode, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) =>
        ReadAsync(accountId, ct, accountIds);
}
public sealed record OperationFillDto(FillDto Fill, string Role, string Quantity, string Fee, string ClosedPnlUsd);
public sealed record OperationFeeDto(string Token, string Amount);
public sealed record OperationFundingDto(Guid Id, Guid AccountId, string SourceId, string ContractId, string? VenueContractId,
    DateTimeOffset OccurredAtUtc, string Amount, string Asset, string? OccurredAtUnixNanoseconds);
public sealed record ActivityOperationDto(string Id, Guid AccountId, string ContractId, string? Direction, string Status,
    DateTimeOffset? OpenedAtUtc, DateTimeOffset? ClosedAtUtc, DateTimeOffset LastOccurredAtUtc,
    string? Quantity, string? TotalEnteredQuantity, string? TotalExitedQuantity, string? OpenQuantity,
    string? AverageEntryPrice, string? AverageExitPrice, int EntryOrderCount, int ExitOrderCount, int FillCount,
    string ReportedClosedPnlUsd, string StablecoinFeesNominalUsd, IReadOnlyList<OperationFeeDto> OtherFees,
    string? NetResultUsd, string PnlBasis, Guid? PlayId, string PlayAssociation,
    IReadOnlyList<string> Coverage, IReadOnlyList<OperationFillDto> Fills, int OmittedFillCount = 0)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TradingMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionSource { get; init; }
    [JsonIgnore]
    public IReadOnlyList<PaperExecutionDto> PaperExecutions { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PaperExecutionCount { get; init; }
    // Comparable before-fee P&L for display. The venue-reported facts above remain unchanged.
    public string? GrossClosedPnlUsd { get; init; }
    public string? TotalResultUsd { get; init; }
    public string? FundingNetUsd { get; init; }
    public string FundingStatus { get; init; } = "unavailable";
    public string FundingPnlBasis { get; init; } = FundingFacts.Unknown;
    public DateTimeOffset? FundingAsOfUtc { get; init; }
    public int FundingPaymentCount { get; init; }
    /// <summary>
    /// The latest venue-reported position leverage observed during the operation, with the lowest and highest when it
    /// changed. All three are null when no observation falls inside the operation. Only the latest is null, with the range
    /// kept, when the latest observation time reports conflicting values. Never inferred from margin, notional or a plan.
    /// </summary>
    public int? Leverage { get; init; }
    public int? LeverageMin { get; init; }
    public int? LeverageMax { get; init; }
    internal IReadOnlyList<FundingPayment> FundingPayments { get; init; } = [];
    // The first retained execution, the lower bound for observations when opening entries are missing.
    internal DateTimeOffset FirstOccurredAtUtc { get; init; }
    // Leverage reported on this operation's executions, excluding split flip allocations.
    internal IReadOnlyList<(DateTimeOffset At, int Leverage)> FillLeverages { get; init; } = [];
    // The exact entry notional, Σ allocated entry quantity × fill price as 10^-56 units (never rounded), for the sizing
    // fallback's return.
    internal BigInteger? ExactEntryNotional { get; init; }
    // The imported executions behind the allocations, read lazily (order keys for sizing) without building row DTOs.
    internal IEnumerable<ImportedFill> SourceFills { get; init; } = [];
    // Internal partition identity, never the canonical display key or a serialized API field.
    internal ActivityContractKey ContractKey { get; init; }
}
public sealed record ActivityCoverageDto(Guid AccountId, string? ContractId, string Notice);
public sealed record PaperExecutionDto(Guid Id, string EntryId, string? LevelId, string Role, string Price, string Quantity,
    string PnlUsd, DateTimeOffset OccurredAtUtc, DateTimeOffset CandleOpenAtUtc, DateTimeOffset CandleCloseAtUtc);

/// <summary>An Activity list row: every operation metric, without its constituent execution rows.</summary>
public sealed record ActivityOperationSummaryDto(string Id, Guid AccountId, string ContractId, string? Direction, string Status,
    DateTimeOffset? OpenedAtUtc, DateTimeOffset? ClosedAtUtc, DateTimeOffset LastOccurredAtUtc,
    string? Quantity, string? TotalEnteredQuantity, string? TotalExitedQuantity, string? OpenQuantity,
    string? AverageEntryPrice, string? AverageExitPrice, int EntryOrderCount, int ExitOrderCount, int FillCount,
    string ReportedClosedPnlUsd, string StablecoinFeesNominalUsd, IReadOnlyList<OperationFeeDto> OtherFees,
    string? NetResultUsd, string PnlBasis, Guid? PlayId, string PlayAssociation,
    IReadOnlyList<string> Coverage, string? GrossClosedPnlUsd)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TradingMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionSource { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PaperExecutionCount { get; init; }
    public string? TotalResultUsd { get; init; }
    public string? FundingNetUsd { get; init; }
    public string FundingStatus { get; init; } = "unavailable";
    public string FundingPnlBasis { get; init; } = FundingFacts.Unknown;
    public DateTimeOffset? FundingAsOfUtc { get; init; }
    public int FundingPaymentCount { get; init; }
    public int? Leverage { get; init; }
    public int? LeverageMin { get; init; }
    public int? LeverageMax { get; init; }
    public static ActivityOperationSummaryDto From(ActivityOperationDto o) => new(o.Id, o.AccountId, o.ContractId, o.Direction, o.Status,
        o.OpenedAtUtc, o.ClosedAtUtc, o.LastOccurredAtUtc, o.Quantity, o.TotalEnteredQuantity, o.TotalExitedQuantity, o.OpenQuantity,
        o.AverageEntryPrice, o.AverageExitPrice, o.EntryOrderCount, o.ExitOrderCount, o.FillCount, o.ReportedClosedPnlUsd,
        o.StablecoinFeesNominalUsd, o.OtherFees, o.NetResultUsd, o.PnlBasis, o.PlayId, o.PlayAssociation, o.Coverage, o.GrossClosedPnlUsd)
    {
        TotalResultUsd = o.TotalResultUsd, FundingNetUsd = o.FundingNetUsd, FundingStatus = o.FundingStatus,
        FundingPnlBasis = o.FundingPnlBasis, FundingAsOfUtc = o.FundingAsOfUtc, FundingPaymentCount = o.FundingPaymentCount,
        Leverage = o.Leverage, LeverageMin = o.LeverageMin, LeverageMax = o.LeverageMax,
        TradingMode = o.TradingMode, ExecutionSource = o.ExecutionSource, PaperExecutionCount = o.PaperExecutionCount
    };
}
public sealed record ActivityFacetsDto(IReadOnlyList<Guid> AccountIds, IReadOnlyList<string> Contracts);
/// <summary>
/// One page of operations after server-side filters. <c>ScopeCount</c> counts the scope before filters; <c>Version</c>
/// identifies the scoped operation set so a client can notice that later pages describe changed history.
/// </summary>
public sealed record ActivityPageDto(IReadOnlyList<ActivityOperationSummaryDto> Operations, int Page, int PageSize, int TotalCount,
    int ScopeCount, int RetainedFillCount, DateTimeOffset AsOfUtc, string Version, ActivityFacetsDto Facets,
    IReadOnlyList<ActivityCoverageDto> Coverage, string ScopeNote)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TradingMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PaperExecutionCount { get; init; }
    public IReadOnlyList<OperationFundingDto> UnassignedFundingPayments { get; init; } = [];
    public int OmittedUnassignedFundingPaymentCount { get; init; }
}
/// <summary>Query-string values exactly as sent; <see cref="ActivityService"/> validates them.</summary>
public sealed record ActivityQuery(Guid? AccountId = null, string? Scope = null, string? Account = null, string? Portfolio = null,
    string? Contract = null, string? Direction = null, string? Status = null, string? Period = null, string? From = null,
    string? To = null, string? Page = null, string? PageSize = null, string? TradingMode = "real");
/// <summary>One operation's constituent rows, chronological, earliest kept when capped.</summary>
public sealed record OperationFillsDto(string OperationId, IReadOnlyList<OperationFillDto> Fills, int OmittedFillCount)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TradingMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PaperExecutionDto>? PaperExecutions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OmittedPaperExecutionCount { get; init; }
    public IReadOnlyList<OperationFundingDto> FundingPayments { get; init; } = [];
    public int OmittedFundingPaymentCount { get; init; }
}
