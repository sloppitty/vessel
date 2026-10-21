using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Review;

/// <summary>
/// Review filters. <paramref name="Positions"/> is <c>exclude</c>, <c>include</c> or <c>auto</c>: auto adds imported positions
/// while the selection has fewer than <paramref name="MinPlays"/> scored Plays.
/// </summary>
public sealed record ReviewQuery(string Period = "month", string Portfolio = "all", Guid? AccountId = null,
    string? Instrument = null, int Offset = 0, string Positions = "exclude", int MinPlays = ReviewQuery.DefaultMinPlays, string? TradingMode = "real")
{
    public const int DefaultMinPlays = 5, MaxMinPlays = 1000;
}

/// <summary>One repeatable-read snapshot of the owner's enabled accounts and retained execution facts.</summary>
public sealed record ReviewFacts(IReadOnlyList<Portfolio> Portfolios, IReadOnlyList<Account> Accounts,
    IReadOnlyList<ClosedPlayFacts> ClosedPlays, int OpenPlays, int UnassignedFills, DateTimeOffset? FirstFillAtUtc,
    IReadOnlySet<Guid>? IncompleteOrderPlayIds = null);

public interface IReviewStore
{
    Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct);
}

public sealed record ReviewPortfolioDto(string Id, string Name);
public sealed record ReviewAccountDto(Guid Id, string Name, Guid? PortfolioId, string VenueId,
    DateTimeOffset? LastSyncedAtUtc, string SyncStatus, string? HistoryNotice);
public sealed record ReviewOptionsDto(IReadOnlyList<ReviewPortfolioDto> Portfolios,
    IReadOnlyList<ReviewAccountDto> Accounts, IReadOnlyList<string> Instruments);
/// <summary>
/// Pooled outcome metrics. <paramref name="AverageR"/> is the estimated R (planned-stop model) over the
/// <paramref name="RiskCount"/> Plays with a complete initial stop.
/// </summary>
public sealed record ReviewMetricsDto(int Count, int Wins, int Losses, int Scratches, string? Net,
    string? Batting, string? AverageGain, string? AverageLoss, string? Payoff, string? ProfitFactor,
    string? AverageWin, string? AverageLossUsd, string? Expectancy, string? AverageR,
    int RiskCount, int Reviewed, string? Drawdown, int MaxWins, int MaxLosses)
{
    /// <summary>Plays whose Play-local result and venue result differ in sign (negative, zero, positive).</summary>
    public int SignDiffers { get; init; }
    /// <summary>Plays with no Play-local result; each row says why.</summary>
    public int LocalUnknown { get; init; }
    /// <summary>Losses whose venue result before fees is zero or better: fees alone made them. Classification is unchanged.</summary>
    public int FeeOnlyLosses { get; init; }
    /// <summary>Losses whose venue result before fees is unknown (a net-of-fee fee in an unlisted token); not fee-only losses.</summary>
    public int UnknownGrossLosses { get; init; }
    /// <summary>Imported positions among <c>Count</c>; the rest are Plays. Plays-only figures (R, reviews, Play-local) exclude them.</summary>
    public int Positions { get; init; }
}
public sealed record ReviewPeriodDto(string Id, int Count, string? Net)
{
    /// <summary>Imported positions among <c>Count</c>.</summary>
    public int Positions { get; init; }
}
public sealed record ReviewGroupDto(string Id, string Name, string Detail, ReviewMetricsDto Metrics);
public sealed record ReviewGroupsDto(IReadOnlyList<ReviewGroupDto> Portfolios, IReadOnlyList<ReviewGroupDto> Accounts,
    IReadOnlyList<ReviewGroupDto> Assets);
public sealed record ReviewPointDto(DateTimeOffset AtUtc, string Net, string Cumulative, string Drawdown, int Count);
/// <summary>
/// One scored Play. <paramref name="Net"/> is the scored result: the venue result for imported executions, the ledger for
/// manual ones. <see cref="Local"/> is the Play-local result beside it. An imported position (origin <c>position</c>) has no
/// Play ID, R, review or Play-local result; <see cref="OperationId"/> identifies its Activity operation.
/// </summary>
public sealed record ReviewPlayDto(Guid? Id, string Title, Guid AccountId, string AccountName, string VenueId,
    string Instrument, DateTimeOffset ClosedAtUtc, string Net, string ReturnPercent, string? RMultiple, bool Reviewed,
    string Origin = "planned", string ExecutionSource = "imported")
{
    public PlayLocalResultDto? Local { get; init; }
    public string? OperationId { get; init; }
}
public sealed record ReviewExclusionDto(Guid PlayId, string Title, Guid AccountId, string AccountName, string Instrument,
    IReadOnlyList<string> Reasons);
public sealed record ReviewExclusionsDto(IReadOnlyList<ReviewExclusionDto> Items, int Total, int Offset, int? NextOffset, string Notice);
public sealed record ReviewCoverageDto(int ClosedCandidates, int IncompleteFees, int IncompleteExecutions, int OpenPlays,
    int UnassignedFills, DateTimeOffset? FirstFillAtUtc, string Notice);
/// <summary>
/// Whether imported positions are in this report. <paramref name="Plays"/> counts scored Plays in the selection, which auto
/// compares with <paramref name="MinPlays"/>. <paramref name="Positions"/> and <paramref name="Omitted"/> (closed positions
/// that cannot be scored) are null when positions were not read. Rate metrics weigh a Play <paramref name="PlayWeight"/> times
/// a position; dollar totals are never weighted.
/// </summary>
public sealed record ReviewPositionsDto(string Mode, bool Included, int MinPlays, int Plays, int? Positions, int? Omitted,
    string PlayWeight);
public sealed record ReviewDto(DateTimeOffset AsOfUtc, DateTimeOffset? FromUtc, string Period, string Currency,
    ReviewOptionsDto Options, IReadOnlyList<ReviewPeriodDto> Periods, ReviewMetricsDto Metrics,
    IReadOnlyList<ReviewPointDto> Series, int BucketDays, ReviewGroupsDto Groups, ReviewCoverageDto Coverage,
    IReadOnlyList<ReviewPlayDto> Plays, int Offset, int? NextOffset, string TradingMode = "real")
{
    public ReviewPositionsDto? Positions { get; init; }
}
