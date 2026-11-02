using System.Text.Json.Serialization;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Risk;

namespace Vessel.Application.Risk;

/// <summary>
/// One direction's open risk: the sum of the known Plays' risk in nominal USD, an exact decimal string ("0" when none is
/// known), with how many Plays it sums and how many are unknown. <paramref name="Rounded"/> says a Play's risk did not
/// terminate within 28 decimal places and was rounded once.
/// </summary>
public sealed record OpenRiskDirectionDto(string KnownUsd, int KnownCount, int UnknownCount, bool Rounded);

/// <summary>Plays in the scope that open risk leaves out, by reason: <c>paused</c> or <c>unresolved</c>.</summary>
public sealed record OpenRiskExcludedDto(string Reason, int Count);

/// <summary>
/// One Planned or Open Play's risk at its plan's stops, or null with the reason it is unknown. <paramref name="Protection"/>
/// is the monitor's last recorded stop coverage of an Open venue Play (0.4c): <c>covered</c>, <c>shared</c>,
/// <c>not-covered</c>, <c>unknown</c>, <c>unsupported</c>, <c>stale</c> or <c>not-checked</c>; null for Planned and manual Plays.
/// </summary>
public sealed record OpenRiskPlayDto(Guid PlayId, string Title, string Direction, string Status, Guid AccountId,
    string? RiskUsd, string? Reason, bool Rounded, string? Protection);

/// <summary>
/// Open venue Plays by recorded stop coverage: <paramref name="NotCovered"/> counts only confirmed absence;
/// <paramref name="Unverified"/> counts unknown, unsupported (such as a current book that is not read), stale and
/// not yet checked coverage. Covered and shared protection count in neither.
/// </summary>
public sealed record OpenRiskProtectionDto(int NotCovered, int Unverified);

/// <summary>The scope's enabled-account balance, an exact sum of the known balances, with how many accounts it covers.</summary>
public sealed record OpenRiskBalanceDto(string? AmountUsd, int ContributingAccountCount, int TotalAccountCount);

/// <summary>
/// Open risk (heat) across the scope's Planned and Open real Plays (Phase 2, item 1). Long and short are never netted;
/// shares of the balance are left to the reader as exact ratios. <paramref name="LimitPercent"/> is the owner's soft limit,
/// null when unset; it is flagged, never enforced.
/// </summary>
public sealed record OpenRiskDto(DateTimeOffset AsOfUtc, string Scope, OpenRiskBalanceDto Balance,
    OpenRiskDirectionDto Long, OpenRiskDirectionDto Short, IReadOnlyList<OpenRiskExcludedDto> NotIncluded,
    OpenRiskProtectionDto Protection, IReadOnlyList<OpenRiskPlayDto> Plays, string? LimitPercent, string Notice);

/// <summary>The owner's soft risk limits; null is unset.</summary>
/// <summary>The soft limits; each is null until the owner sets it. Loss limits are in R (Phase 2, item 10b).</summary>
public sealed record RiskSettingsDto(string? OpenRiskLimitPercent, string? DailyLossLimitR = null, string? WeeklyLossLimitR = null);
/// <summary>
/// The soft limits to save. A property present replaces its limit, null clearing it; a property left out keeps the stored
/// limit, so an older client that knows only the open-risk limit never clears the loss limits (Sol round 1 on 10b).
/// </summary>
public sealed class UpdateRiskSettingsRequest
{
    private readonly string? open, daily, weekly;

    public UpdateRiskSettingsRequest() { }
    public UpdateRiskSettingsRequest(string? openRiskLimitPercent) => OpenRiskLimitPercent = openRiskLimitPercent;
    public UpdateRiskSettingsRequest(string? openRiskLimitPercent, string? dailyLossLimitR, string? weeklyLossLimitR) : this(openRiskLimitPercent)
    {
        DailyLossLimitR = dailyLossLimitR;
        WeeklyLossLimitR = weeklyLossLimitR;
    }

    public string? OpenRiskLimitPercent { get => open; init { open = value; HasOpenRiskLimit = true; } }
    public string? DailyLossLimitR { get => daily; init { daily = value; HasDailyLossLimit = true; } }
    public string? WeeklyLossLimitR { get => weekly; init { weekly = value; HasWeeklyLossLimit = true; } }
    [JsonIgnore] public bool HasOpenRiskLimit { get; private init; }
    [JsonIgnore] public bool HasDailyLossLimit { get; private init; }
    [JsonIgnore] public bool HasWeeklyLossLimit { get; private init; }
}

/// <summary>
/// One reporting day or week's loss (Phase 2, item 10b): the exact sum of estimated R of real Plays closed in it
/// (<paramref name="NetR"/>, "0" with none), how many have R and how many have none, which are never counted as zero, and
/// whether the known loss reached the limit.
/// </summary>
public sealed record LossWindowDto(string Start, DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? LimitR, string NetR, int WithR,
    int WithoutR, bool Breached);
public sealed record LossLimitsDto(LossWindowDto Day, LossWindowDto Week, string TimeZone, string WeekStart, string Notice);

/// <summary>A stop of one entry, as the editor holds it: a price or a percent return on margin, and its share of the entry.</summary>
public sealed record RiskStopDto(string Id, string Unit, string Value, string Share);
public sealed record RiskEntryDto(string Id, string Share, string Price, IReadOnlyList<RiskStopDto> Stops);
/// <summary>The parts of a plan open risk reads. Number fields are the editor's text and may be blank in a Draft.</summary>
public sealed record RiskPlanDto(string Direction, string SizingMode, string Size, string Leverage, IReadOnlyList<RiskEntryDto> Entries);

/// <summary>
/// The plan being edited, so capital context can show this Play's own increment by the same rule as every other Play.
/// <paramref name="PlayId"/> is the saved Play whose executions count, or null for a new Draft.
/// </summary>
public sealed record OpenRiskPreviewRequest(Guid? PlayId, [property: JsonRequired] Guid AccountId, [property: JsonRequired] RiskPlanDto Plan,
    [property: JsonRequired] string InstrumentSource);
public sealed record OpenRiskPreviewDto(Guid? PlayId, string Direction, string? RiskUsd, string? Reason, bool Rounded);

/// <summary>
/// One active real Play with what open risk reads: its executions as linked fills or the manual ledger, the monitor's last
/// recorded stop coverage, and a problem that makes its executions unreadable or incomplete.
/// </summary>
public sealed record OpenRiskFacts(Play Play, IReadOnlyList<RiskExecution> Executions, MonitorCoverage? Coverage,
    bool ManualLedger, string? Problem = null);

// Application owns this bounded port; EF details stay in Persistence. Reads are owner-filtered.
public interface IOpenRiskStore
{
    /// <summary>
    /// The owner's real Planned, Paused, Open and Unresolved Plays on enabled accounts, or exactly the real Plays named by
    /// <paramref name="playIds"/> in any status and account state, with their linked executions or manual ledger.
    /// </summary>
    Task<List<OpenRiskFacts>> ActivePlaysAsync(IReadOnlyCollection<Guid>? playIds, CancellationToken ct);
    /// <summary>The owner's Play by ID, in any status and mode; null when unknown.</summary>
    Task<Play?> PlayAsync(Guid id, CancellationToken ct);
    Task<RiskSettings?> SettingsAsync(CancellationToken ct);
    /// <summary>Inside a unit of work: the owner's settings row, created when absent, locked for update.</summary>
    Task<RiskSettings> SettingsForUpdateAsync(RiskSettings initial, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}
