using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Review;

/// <summary>
/// Review filters. <paramref name="Positions"/> is <c>exclude</c>, <c>include</c> or <c>auto</c>: auto adds imported positions
/// while the selection has fewer than <paramref name="MinPlays"/> scored Plays.
/// </summary>
public sealed record ReviewQuery(string Period = "month", string Portfolio = "all", Guid? AccountId = null,
    string? Instrument = null, int Offset = 0, string Positions = "exclude", int MinPlays = ReviewQuery.DefaultMinPlays)
{
    public const int DefaultMinPlays = 5, MaxMinPlays = 1000;
}

/// <summary>One repeatable-read snapshot of the owner's enabled accounts and retained execution facts.</summary>
public sealed record ReviewFacts(IReadOnlyList<Portfolio> Portfolios, IReadOnlyList<Account> Accounts,
    IReadOnlyList<ClosedPlayFacts> ClosedPlays, int OpenPlays, int UnassignedFills, DateTimeOffset? FirstFillAtUtc,
    IReadOnlySet<Guid>? IncompleteOrderPlayIds = null)
{
    /// <summary>Unresolved Plays on enabled accounts with their latest declaration (phase 1, 1.3a). They are never scored.</summary>
    public IReadOnlyList<UnresolvedPlayFacts> UnresolvedPlays { get; init; } = [];
    /// <summary>Cancelled Plays on enabled accounts, with plan revision 1 for the pre-flight (phase 1, 1.4b). Never scored.</summary>
    public IReadOnlyList<EndedPlayFacts> CancelledPlays { get; init; } = [];
}

/// <summary>A Cancelled Play in Review's process view: never scored, placed by its cancellation time.</summary>
public sealed record EndedPlayFacts(Play Play, IReadOnlyList<PlayPlanRevision> Revisions);

/// <summary>An Unresolved Play and its latest declaration, placed in a period by its end, the declaration time.</summary>
public sealed record UnresolvedPlayFacts(Play Play, UnresolvedDeclarationDto? Declaration)
{
    public DateTimeOffset EndedAt => Play.EndedAtUtc ?? Declaration?.DeclaredAtUtc ?? Play.UpdatedAtUtc;
    /// <summary>Plan revision 1, for the pre-flight (phase 1, 1.4b).</summary>
    public IReadOnlyList<PlayPlanRevision> Revisions { get; init; } = [];
    /// <summary>Its earliest linked execution, for the "plan written after first execution" badge; null without one.</summary>
    public DateTimeOffset? FirstExecutionAtUtc { get; init; }
}

public interface IReviewStore
{
    Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct);
    /// <summary>
    /// The owner's own mistake tags for autocomplete, newest use first: trimmed, grouped case-insensitively in their latest
    /// spelling, at most <paramref name="limit"/> (phase 1, 1.4b).
    /// </summary>
    Task<IReadOnlyList<string>> MistakeTagsAsync(int limit, CancellationToken ct);
}

/// <summary>
/// One process drill-down (phase 1, 1.4b): Review's filters with exactly one of a cell (<c>followed-win</c> … <c>deviated-scratch</c>),
/// a lane (<c>retrospective</c>, <c>unscored</c>, <c>unanswered</c>, <c>not-applicable</c>), a mistake tag, or a pre-flight check
/// that was skipped (a check code, or <c>not-recorded</c>).
/// </summary>
public sealed record ReviewProcessQuery(ReviewQuery Filters, string? Cell = null, string? Lane = null, string? MistakeTag = null,
    string? SkippedCheck = null);

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
    IReadOnlyList<ReviewGroupDto> Assets)
{
    /// <summary>Planned and retrospective Plays, in that fixed order (phase 1, 1.4b).</summary>
    public IReadOnlyList<ReviewGroupDto> Origins { get; init; } = [];
    /// <summary>Long and short Plays, in that fixed order (phase 1, 1.4b).</summary>
    public IReadOnlyList<ReviewGroupDto> Directions { get; init; } = [];
}
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
    /// <summary>The plan was written after the Play's first execution (D-17); it is scored like any other.</summary>
    public bool PlannedAfterExecution { get; init; }
    /// <summary>The stored answers, read as they are: a completed review is not required (phase 1, 1.4b).</summary>
    public string? Adherence { get; init; }
    public string? MistakeTag { get; init; }
    public string Direction { get; init; } = "";
    /// <summary>completed, not-needed, legacy, or null while the review is not complete.</summary>
    public string? ReviewCompletion { get; init; }
    public string? OperationId { get; init; }
}

/// <summary>One adherence row of the process cases: scored Plays by win and loss, with its scratches counted beside them.</summary>
public sealed record ReviewProcessRowDto(int Wins, int Losses, int Scratches);
/// <summary>Plays per mistake tag, in its latest spelling: scored ones by outcome, and unscored ones. No money.</summary>
public sealed record ReviewMistakeTagDto(string Tag, int Wins, int Losses, int Scratches, int Unscored);
/// <summary>
/// Process and outcome as cases (phase 1, 1.4b): adherence (followed, deviated) × outcome (win, loss) over the period's
/// Closed, Cancelled and Unresolved Plays, plus the lanes a Play falls into first: retrospective, unscored with answers,
/// unanswered, not applicable. Counts only; rates are left to the reader's own denominator.
/// </summary>
public sealed record ReviewProcessDto(ReviewProcessRowDto Followed, ReviewProcessRowDto Deviated, int Retrospective, int Unscored,
    int Unanswered, int NotApplicable, IReadOnlyList<ReviewMistakeTagDto> MistakeTags, int MistakeTagCount);
/// <summary>One Play of a process drill-down; financial values are null when Review does not score it.</summary>
public sealed record ReviewProcessPlayDto(Guid Id, string Title, Guid AccountId, string AccountName, string VenueId, string Instrument,
    string Status, string Origin, string Direction, DateTimeOffset AtUtc, string? Net, string? ReturnPercent, string? RMultiple,
    string? Adherence, string? MistakeTag, bool PlannedAfterExecution, string? ReviewCompletion);
public sealed record ReviewProcessPageDto(IReadOnlyList<ReviewProcessPlayDto> Items, int Total, int Offset, int? NextOffset);
public sealed record ReviewExclusionDto(Guid PlayId, string Title, Guid AccountId, string AccountName, string Instrument,
    IReadOnlyList<string> Reasons)
{
    /// <summary>An Unresolved Play's declaration: its reason, note and execution facts (phase 1, 1.3a).</summary>
    public UnresolvedDeclarationDto? Unresolved { get; init; }
}
public sealed record ReviewExclusionsDto(IReadOnlyList<ReviewExclusionDto> Items, int Total, int Offset, int? NextOffset, string Notice);
public sealed record ReviewCoverageDto(int ClosedCandidates, int IncompleteFees, int IncompleteExecutions, int OpenPlays,
    int UnassignedFills, DateTimeOffset? FirstFillAtUtc, string Notice)
{
    /// <summary>Unresolved Plays declared in the period, excluded with the reason "unresolved" (phase 1, 1.3a).</summary>
    public int Unresolved { get; init; }
    /// <summary>Plays cancelled in the period: they have no fills and no result, and the exclusion list shows them (phase 1, 1.4b).</summary>
    public int Cancelled { get; init; }
}
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
    IReadOnlyList<ReviewPlayDto> Plays, int Offset, int? NextOffset)
{
    public ReviewPositionsDto? Positions { get; init; }
    /// <summary>
    /// The pre-flight recorded at Plan it, over the period's planned Plays in the process view: Closed, Cancelled and
    /// Unresolved, scored or not (phase 1, 1.5 and 1.4b).
    /// </summary>
    public ReviewPreflightDto? Preflight { get; init; }
    /// <summary>Process and outcome as cases (phase 1, 1.4b).</summary>
    public ReviewProcessDto? Process { get; init; }
}
/// <summary>
/// <paramref name="Recorded"/> Plays have a pre-flight on revision 1 and <paramref name="NotRecorded"/> were planned before
/// it existed; <paramref name="Skipped"/> counts, per check, the recorded Plays that went ahead without it.
/// </summary>
public sealed record ReviewPreflightDto(int Recorded, int NotRecorded, IReadOnlyList<ReviewPreflightCountDto> Skipped);
public sealed record ReviewPreflightCountDto(string Code, int Count);
