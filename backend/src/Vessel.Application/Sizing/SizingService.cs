using Vessel.Application.Activity;
using Vessel.Application.Workspace;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Numerics;
using Vessel.Domain.Sizing;
using Vessel.Domain.Plays;
using Vessel.Domain.Common;

namespace Vessel.Application.Sizing;

/// <summary>
/// The owner's track record from closed Plays, the exposure level it implies and the limits the editor's
/// sizing suggestions use. Real execution outcomes and explicit paper simulations have isolated sources;
/// neither mode borrows the other's record or exposure ladder.
/// </summary>
public sealed class SizingService(ISizingStore store, IJournalOwnerContext owner, TimeProvider time, IUnitOfWork unitOfWork,
    ActivityService? activity = null, IVenueRegistry? venues = null)
{
    public const string SourcePlays = "plays", SourceActivity = "activity", SourceNone = "none", SourcePaper = "paper";
    private const string PaperNotice = "Simulated paper Plays only. Full touched allocations at their defined price, zero fees and zero slippage. " +
        "Unresolved or incomplete simulations are excluded. Starting capital is never profit. " +
        "Real Plays, imported Activity and manual actual executions are not used. The risk percentage is the owner's read-only shared preference. " +
        "Suggestions are estimates, not advice.";
    private const string Notice = "Results come from each closed play's linked fills: venue closed PnL less fees paid in listed USD stablecoins. " +
        NominalStablecoins.Caveat + " Plays on venues with different quote stablecoins are pooled nominally. " +
        "Suggestions are mechanical estimates from your risk setting and record, not advice.";
    private const string ActivityNotice = "No closed play has a win or loss yet, so the record starts from closed positions in Activity on enabled accounts: " +
        "reported closed PnL less listed stablecoin fees on gross fills (net-of-fee results are not charged twice), over the entry notional. " +
        "Positions are not Plays: they have no plan, so R multiples are unknown, and none becomes a Play or a link. " +
        "The first closed play with a win or loss replaces this record. " + NominalStablecoins.Caveat + " " +
        "Suggestions are mechanical estimates from your risk setting and record, not advice.";
    private const string ActivityReason = " Counted from closed Activity positions until a play closes with a win or loss.";
    private const string RatioNotice = " The win/loss ratio exceeds the supported decimal range and is unknown.";

    public async Task<SizingDto> GetAsync(CancellationToken ct, string? tradingMode = "real")
    {
        tradingMode = TradingMode.Validate(tradingMode ?? TradingMode.Real);
        var settings = await store.SettingsAsync(ct);
        return await BuildAsync(settings?.RiskPercent ?? SizingSettings.DefaultRiskPercent, tradingMode, ct);
    }

    public async Task<SizingDto> UpdateSettingsAsync(UpdateSizingSettingsRequest request, CancellationToken ct, string? tradingMode = "real")
    {
        tradingMode = TradingMode.Validate(tradingMode ?? TradingMode.Real);
        if (tradingMode == TradingMode.Paper)
            throw new ProblemException(409, "Paper sizing reads the risk preference; it cannot change real sizing settings.");
        if (!ExactDecimal.TryParseRiskPercent(request.RiskPercent, out var risk))
            throw new ProblemException(400, "Risk per trade must be a decimal percentage.");
        var now = time.GetUtcNow();
        await unitOfWork.ExecuteAsync(async () =>
        {
            // Validates before any write; concurrent first writes share one owner row and the last writer wins.
            var settings = await store.SettingsForUpdateAsync(new SizingSettings(owner.OwnerId, risk, now), ct);
            settings.SetRisk(risk, now);
            await store.SaveAsync(ct);
            return true;
        }, ct);
        return await BuildAsync(risk, tradingMode, ct);
    }

    private async Task<SizingDto> BuildAsync(decimal risk, string tradingMode, CancellationToken ct)
    {
        var closed = (await store.ClosedPlaysAsync(tradingMode, ct)).Where(f => f.Play.TradingMode == tradingMode).ToList();
        if (tradingMode == TradingMode.Paper)
        {
            var paper = closed.Where(f => f.Paper is not null).Select(f => (Facts: f.Paper!, Outcome: f.Paper!.Outcome(time.GetUtcNow(), out _)))
                .Where(x => x.Outcome is not null).Select(x => new Scored(x.Outcome!,
                    Row(x.Facts.Play.Id, x.Facts.Play.Title, x.Facts.Play.ContractId, x.Outcome!))
                { Local = PaperPlayFacts.Local(x.Outcome!) }).ToList();
            return ToDto(risk, paper, SourcePaper) with { TradingMode = TradingMode.Paper };
        }
        var plays = PlayOutcomes(closed, venues);
        if (plays.Any(x => x.Outcome.Kind != PlayOutcomeKind.Scratch) || activity is null) return ToDto(risk, plays, SourcePlays);
        // Only with no decided Play outcome; the two sources are never mixed.
        var operations = await ActivityRecordAsync(activity, ct);
        return operations.Any(x => x.Outcome.Kind != PlayOutcomeKind.Scratch) ? ToDto(risk, operations, SourceActivity) : ToDto(risk, plays, SourceNone);
    }

    /// <summary>
    /// Fallback outcomes from closed operations. Balanced retained fills cannot prove completeness: an operation with an
    /// execution of a filled fixed-size order whose retained executions (in or outside the operation) fall short of its
    /// size is left out, by the same <see cref="OrderKey"/> and <see cref="ExecutionIntegrity"/> rules Play sizing uses.
    /// Fills without a retained order record cannot prove a missing execution.
    /// </summary>
    private async Task<List<Scored>> ActivityRecordAsync(ActivityService activity, CancellationToken ct) =>
        ActivityOutcomes((await CompleteOperationsAsync(activity, store, null, ct)).Complete);

    /// <summary>
    /// Closed operations of the given enabled accounts (all when null) that can be scored like a Play, and the closed ones
    /// left out. Review shares this rule for its imported positions.
    /// </summary>
    internal static async Task<(List<ActivityOperationDto> Complete, List<ActivityOperationDto> Omitted)> CompleteOperationsAsync(
        ActivityService activity, ISizingStore store, IReadOnlySet<Guid>? accountIds, CancellationToken ct)
    {
        var summaries = await activity.ReadSummariesAsync(null, ct, accountIds);
        var candidates = summaries.Operations.Where(Candidate)
            .Select(o => (Operation: o, Orders: o.SourceFills.Where(f => !string.IsNullOrWhiteSpace(f.ExternalOrderId))
                .Select(OrderKey.Of).ToHashSet())).ToList();
        var keys = candidates.SelectMany(c => c.Orders).ToHashSet();
        HashSet<OrderKey> incomplete = [];
        if (keys.Count > 0)
        {
            var retained = summaries.Facts.Fills.ToLookup(OrderKey.Of);
            incomplete = (await store.OrdersAsync(keys, ct)).Where(o => keys.Contains(OrderKey.Of(o)) &&
                ExecutionIntegrity.MissingExecutions(o, retained[OrderKey.Of(o)])).Select(OrderKey.Of).ToHashSet();
        }
        var complete = candidates.Where(c => !c.Orders.Overlaps(incomplete)).Select(c => c.Operation).ToList();
        var kept = new HashSet<ActivityOperationDto>(complete, ReferenceEqualityComparer.Instance);
        return (complete, summaries.Operations.Where(o => o.ClosedAtUtc is not null && o.Status is "closed" or "incomplete" && !kept.Contains(o)).ToList());
    }

    private static bool Candidate(ActivityOperationDto operation) =>
        operation is { Status: "closed", Direction: not null, OpenedAtUtc: not null, ClosedAtUtc: not null, NetResultUsd: not null,
            ExactEntryNotional: { Sign: > 0 } } &&
        !operation.Coverage.Contains(ActivityPositions.AmbiguousIdentityNotice) && Guid.TryParse(operation.Id.AsSpan(0, Math.Min(36, operation.Id.Length)), out _);

    /// <summary>A scored outcome and its row; a Play outcome also has its Play-local result, never scored.</summary>
    internal sealed record Scored(PlayOutcome Outcome, PlayOutcomeDto Row)
    {
        public PlayLocalResult? Local { get; init; }
    }

    private static List<Scored> PlayOutcomes(List<ClosedPlayFacts> closed, IVenueRegistry? venues) =>
        // A closed record whose linked fills are known incomplete, contradictory or out of order is not a decided outcome.
        // A venue-linked outcome closes at its latest valid exit; the detection time stays beside it for audit.
        closed.Where(f => f.Play.Origin != PlayOrigin.Retrospective && f.ManualFeesComplete)
            .Where(ExecutionIntegrity.Settled).Select(facts => (Facts: facts, Outcome: PlayOutcomeCalculator.Outcome(facts))).Where(x => x.Outcome is not null)
            .Select(x => new Scored(x.Outcome!, Row(x.Facts.Play.Id, x.Facts.Play.Title, x.Facts.Play.ContractId, x.Outcome!) with
            {
                DetectedAtUtc = PlayOutcomeCalculator.DetectedAt(x.Facts)
            })
            {
                Local = PlayLocalResult.From(x.Facts, venues?.Descriptor(x.Facts.Play.VenueId)?.QuoteAsset)
            }).ToList();

    /// <summary>
    /// Closed, complete operations with a known net result and entry notional, scored like a Play: return is the net
    /// result over the exact entry notional, unlevered. Ambiguous native identities are left out. Planned risk is unknown.
    /// Values the Play calculator would refuse (outside decimal range or exact precision, including the return) are
    /// unknown and left out, never clamped. The notional is the unrounded quantity × price product, so one with more
    /// than 28 decimal places is not representable and left out too, never rounded first.
    /// </summary>
    internal static List<Scored> ActivityOutcomes(IEnumerable<ActivityOperationDto> operations)
    {
        var result = new List<Scored>();
        foreach (var operation in operations.Where(Candidate))
        {
            try
            {
                var outcome = new PlayOutcome(Guid.Parse(operation.Id.AsSpan(0, 36)), operation.ClosedAtUtc!.Value,
                    PlayOutcomeCalculator.ToDecimal(ExactAmount.Parse(operation.NetResultUsd!)), PlayOutcomeCalculator.ToDecimal(operation.ExactEntryNotional!.Value),
                    null, true)
                {
                    // The operation's before-fee P&L (null when a net-of-fee fee is in an unlisted token); never excludes it.
                    GrossResult = operation.GrossClosedPnlUsd is { } gross ? ExactAmount.Parse(gross) : null
                };
                // The record and its row read the return, so an unusable one is excluded here, like the Play calculator does.
                var row = Row(null, $"{operation.ContractId} {(operation.Direction == "long" ? "Long" : "Short")}", operation.ContractId, outcome) with
                {
                    OperationId = operation.Id, AccountId = operation.AccountId
                };
                result.Add(new(outcome, row));
            }
            catch (OverflowException)
            {
                continue;
            }
        }
        return result;
    }

    private static PlayOutcomeDto Row(Guid? playId, string title, string? instrument, PlayOutcome outcome) =>
        new(playId, title, instrument, outcome.ClosedAtUtc, ExactDecimal.FormatEightPlaces(outcome.NetResult),
            ExactDecimal.FormatEightPlaces(outcome.ReturnPercent), ExactDecimal.FormatEightPlaces(outcome.PlannedRisk),
            ExactDecimal.FormatEightPlaces(outcome.RMultiple), Name(outcome.Kind), outcome.FeesComplete);

    private static SizingDto ToDto(decimal risk, List<Scored> outcomes, string source)
    {
        var record = TrackRecord.From(outcomes.Select(x => x.Outcome).ToList());
        // The ladder walks every decided outcome of the chosen source, fallback operations included: they are real results.
        var exposure = Exposure.From(outcomes.Select(x => x.Outcome));
        // Outcomes sharing a close time keep the Play-ID order; the reason discloses them rather than inventing an order.
        var ties = CloseTimeTies.From(outcomes.Select(x => x.Outcome));
        var limits = SizingLimits.From(risk, record, exposure);
        var recent = outcomes.OrderByDescending(x => x.Outcome.ClosedAtUtc).ThenBy(x => x.Outcome.PlayId).Take(record.Window)
            .Select(x => x.Row).ToList();
        // The decided results the record's window counts, chosen as TrackRecord chooses them.
        var window = outcomes.Where(x => x.Outcome.Kind != PlayOutcomeKind.Scratch).OrderByDescending(x => x.Outcome.ClosedAtUtc)
            .ThenBy(x => x.Outcome.PlayId).Take(record.Window).ToList();
        var activitySource = source == SourceActivity;
        var notice = source == SourcePaper ? PaperNotice : activitySource ? ActivityNotice : Notice;
        var reason = exposure.Reason + (ties.Reason is { } tie ? " " + tie : "") + (activitySource ? ActivityReason : "");
        return new SizingDto(new SizingSettingsDto(ExactDecimal.FormatEightPlaces(risk)),
            new ExposureDto(Name(exposure.Level), ExactDecimal.FormatEightPlaces(exposure.Multiplier),
                reason, exposure.LossStreak, exposure.WinsSinceStepDown) { TiedOutcomes = ties.Outcomes },
            new TrackRecordDto(record.ClosedPlays, record.DecidedPlays, record.Window, record.Wins, record.Losses, record.Scratches,
                ExactDecimal.FormatEightPlaces(record.BattingAverage), ExactDecimal.FormatEightPlaces(record.AverageGainPercent),
                ExactDecimal.FormatEightPlaces(record.AverageLossPercent), ExactDecimal.FormatEightPlaces(record.WinLossRatio),
                ExactDecimal.FormatEightPlaces(record.BreakEvenRewardRisk), ExactDecimal.FormatEightPlaces(record.AverageWinR),
                ExactDecimal.FormatEightPlaces(record.AverageLossR), recent)
            {
                Source = source,
                // Positions are not Plays: the Play-local counts are omitted for the Activity fallback, not zero.
                LocalSignChanges = activitySource ? null : window.Count(x => x.Local?.SignDiffers(x.Outcome.NetResult) == true),
                LocalUnknown = activitySource ? null : window.Count(x => x.Local?.Net is null),
                FeeOnlyLosses = window.Count(x => x.Outcome.FeeOnlyLoss),
                UnknownGrossLosses = window.Count(x => x.Outcome is { Kind: PlayOutcomeKind.Loss, GrossResult: null }),
            },
            new SizingLimitsDto(ExactDecimal.FormatEightPlaces(limits.EffectiveRiskPercent), ExactDecimal.FormatEightPlaces(limits.MaxStopPercent),
                ExactDecimal.FormatEightPlaces(limits.MinRewardRisk), Source(limits.MaxStopSource), Source(limits.MinRewardRiskSource)),
            record is { AverageGainPercent: not null, AverageLossPercent: > 0, WinLossRatio: null } ? notice + RatioNotice : notice);
    }

    private static string Name<T>(T value) where T : Enum => value.ToString().ToLowerInvariant();

    private static string Source(LimitSource source) => source switch
    {
        LimitSource.AverageGain => "averageGain",
        LimitSource.BattingAverage => "battingAverage",
        _ => "default",
    };
}
