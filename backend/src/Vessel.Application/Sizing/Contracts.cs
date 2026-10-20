using System.Text.Json.Serialization;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Sizing;

public sealed record UpdateSizingSettingsRequest([property: JsonRequired] string RiskPercent);

public sealed record SizingSettingsDto(string RiskPercent);

public sealed record ExposureDto(string Level, string Multiplier, string Reason, int LossStreak, int WinsSinceStepDown)
{
    /// <summary>
    /// Decided outcomes the ladder walks that share an exact close time with another; they keep the Play-ID order, which is
    /// not a known economic order. When above zero the reason says so.
    /// </summary>
    public int TiedOutcomes { get; init; }
}

/// <summary>
/// One scored outcome. A Play outcome carries its <paramref name="PlayId"/>; an Activity fallback outcome carries the
/// operation and account instead and has no Play, plan, risk or R multiple. <paramref name="ClosedAtUtc"/> is the latest
/// valid linked exit for a venue-linked Play, the owner-entered exit for a manual ledger, the closing fill for an operation.
/// <paramref name="RMultiple"/> is the estimated R (planned-stop model): net result over planned risk, with the sizing
/// fallbacks for partial plans.
/// </summary>
public sealed record PlayOutcomeDto(Guid? PlayId, string Title, string? Instrument, DateTimeOffset ClosedAtUtc, string NetResultUsd,
    string ReturnPercent, string? RiskUsd, string? RMultiple, string Outcome, bool FeesComplete)
{
    public string? OperationId { get; init; }
    public Guid? AccountId { get; init; }
    /// <summary>Audit only: when a check or refresh detected a venue-linked Play's close. Null for manual ledgers and operations.</summary>
    public DateTimeOffset? DetectedAtUtc { get; init; }
}

public sealed record TrackRecordDto(int ClosedPlays, int DecidedPlays, int Window, int Wins, int Losses, int Scratches,
    string? BattingAverage, string? AverageGainPercent, string? AverageLossPercent, string? WinLossRatio, string? BreakEvenRewardRisk,
    string? AverageWinR, string? AverageLossR, IReadOnlyList<PlayOutcomeDto> Recent)
{
    /// <summary>
    /// Decided results in the window whose Play-local result differs in sign from the scored venue result. Null for the
    /// Activity fallback: positions are not Plays.
    /// </summary>
    public int? LocalSignChanges { get; init; }
    /// <summary>Decided results in the window with no Play-local result. Null for the Activity fallback.</summary>
    public int? LocalUnknown { get; init; }
    /// <summary>Losses in the window whose scored result before fees is zero or better. Win, loss and scratch are unchanged.</summary>
    public int FeeOnlyLosses { get; init; }
    /// <summary>Losses in the window whose scored result before fees is unknown; they are not counted as fee-only.</summary>
    public int UnknownGrossLosses { get; init; }

    /// <summary>
    /// <see cref="SizingService.SourcePlays"/> when closed Plays have a win or loss, <see cref="SizingService.SourceActivity"/>
    /// when the record falls back to closed Activity operations, <see cref="SizingService.SourceNone"/> when neither has one.
    /// The counts above describe the source's outcomes.
    /// </summary>
    public string Source { get; init; } = SizingService.SourcePlays;

    /// <summary>
    /// Unresolved Plays on enabled accounts (phase 1, 1.3a). They have no result, so they are never in the record, the
    /// window or the ladder; the count is shown beside the ladder as "N unresolved not counted".
    /// </summary>
    public int Unresolved { get; init; }
}

public sealed record SizingLimitsDto(string EffectiveRiskPercent, string MaxStopPercent, string MinRewardRisk, string MaxStopSource,
    string MinRewardRiskSource);

public sealed record SizingDto(SizingSettingsDto Settings, ExposureDto Exposure, TrackRecordDto Record, SizingLimitsDto Limits, string Notice);

/// <summary>
/// A closed Play with its plan revisions, the fills of its linked orders and the linked orders known to be filled
/// beyond their retained fills.
/// </summary>
public sealed record ClosedPlayFacts(Play Play, IReadOnlyList<PlayPlanRevision> Revisions, IReadOnlyList<LinkedFill> Fills,
    IReadOnlyCollection<string>? IncompleteOrders = null, bool ManualSource = false, bool ManualFeesComplete = true)
{
    private readonly ParsedPlans plans = new();

    /// <summary>The Play's current plan, parsed once per stored document.</summary>
    public PlayPlanDocument CurrentPlan => plans.Read(Play.Plan);

    /// <summary>A stored plan document of this Play (current or a revision), parsed once per distinct document.</summary>
    public PlayPlanDocument ParsedPlan(string stored) => plans.Read(stored);

    /// <summary>
    /// Parsed plans keyed by their stored text, so a changed or revised plan is never answered from another document.
    /// It carries no facts: record equality and hashing ignore it.
    /// </summary>
    private sealed class ParsedPlans
    {
        private readonly Dictionary<string, PlayPlanDocument> parsed = new(StringComparer.Ordinal);

        public PlayPlanDocument Read(string stored)
        {
            lock (parsed)
            {
                if (parsed.TryGetValue(stored, out var plan)) return plan;
            }
            var read = PlayDocuments.Read(stored);
            lock (parsed) parsed.TryAdd(stored, read);
            return read;
        }

        public override bool Equals(object? obj) => obj is ParsedPlans;
        public override int GetHashCode() => 0;
    }
}

public sealed record LinkedFill(OrderLinkRole Role, ImportedFill Fill);

// Application owns this bounded port; EF details stay in Persistence. Reads are owner-filtered.
public interface ISizingStore
{
    Task<SizingSettings?> SettingsAsync(CancellationToken ct);
    /// <summary>Closed Plays on enabled accounts with their revisions, linked fills and incompletely retained orders.</summary>
    Task<List<ClosedPlayFacts>> ClosedPlaysAsync(CancellationToken ct);
    /// <summary>How many Unresolved Plays the owner has on enabled accounts; they are never counted in the record.</summary>
    Task<int> UnresolvedPlaysAsync(CancellationToken ct);
    /// <summary>
    /// The owner's retained venue orders with these keys (any account state), for the Activity fallback's missing-execution
    /// check. Matching uses the shared <see cref="OrderKey"/> rule, never the external ID alone.
    /// </summary>
    Task<List<ImportedOrder>> OrdersAsync(IReadOnlyCollection<OrderKey> orders, CancellationToken ct);
    void Add(SizingSettings settings);

    /// <summary>
    /// Inside a unit of work: the owner's settings row, created from <paramref name="initial"/> when absent, locked for
    /// update. Concurrent first writes must not conflict. This default suits single-flow stores; Persistence overrides it atomically.
    /// </summary>
    async Task<SizingSettings> SettingsForUpdateAsync(SizingSettings initial, CancellationToken ct)
    {
        if (await SettingsAsync(ct) is { } settings) return settings;
        Add(initial);
        return initial;
    }
    Task SaveAsync(CancellationToken ct);
}
