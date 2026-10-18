using Vessel.Application.Activity;
using Vessel.Application.Workspace;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Numerics;
using Vessel.Domain.Sizing;
using Vessel.Domain.Plays;

namespace Vessel.Application.Sizing;

/// <summary>
/// The owner's track record from closed Plays, the exposure level it implies and the limits the editor's
/// sizing suggestions use. Outcomes come only from linked venue fills; nothing is inferred from prices.
/// </summary>
public sealed class SizingService(ISizingStore store, IJournalOwnerContext owner, TimeProvider time, IUnitOfWork unitOfWork,
    ActivityService? activity = null)
{
    public const string SourcePlays = "plays", SourceActivity = "activity", SourceNone = "none";
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

    public async Task<SizingDto> GetAsync(CancellationToken ct)
    {
        var settings = await store.SettingsAsync(ct);
        return await BuildAsync(settings?.RiskPercent ?? SizingSettings.DefaultRiskPercent, ct);
    }

    public async Task<SizingDto> UpdateSettingsAsync(UpdateSizingSettingsRequest request, CancellationToken ct)
    {
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
        return await BuildAsync(risk, ct);
    }

    private async Task<SizingDto> BuildAsync(decimal risk, CancellationToken ct)
    {
        var plays = PlayOutcomes(await store.ClosedPlaysAsync(ct));
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
    private async Task<List<Scored>> ActivityRecordAsync(ActivityService activity, CancellationToken ct)
    {
        var summaries = await activity.ReadSummariesAsync(null, ct);
        var candidates = summaries.Operations.Where(Candidate)
            .Select(o => (Operation: o, Orders: o.Fills.Select(f => new OrderKey(f.Fill.AccountId, f.Fill.ContractId, f.Fill.OrderId))
                .Where(k => !string.IsNullOrWhiteSpace(k.ExternalOrderId)).ToHashSet())).ToList();
        var keys = candidates.SelectMany(c => c.Orders).ToHashSet();
        HashSet<OrderKey> incomplete = [];
        if (keys.Count > 0)
        {
            var retained = summaries.Facts.Fills.ToLookup(OrderKey.Of);
            incomplete = (await store.OrdersAsync(keys, ct)).Where(o => keys.Contains(OrderKey.Of(o)) &&
                ExecutionIntegrity.MissingExecutions(o, retained[OrderKey.Of(o)])).Select(OrderKey.Of).ToHashSet();
        }
        return ActivityOutcomes(candidates.Where(c => !c.Orders.Overlaps(incomplete)).Select(c => c.Operation));
    }

    private static bool Candidate(ActivityOperationDto operation) =>
        operation is { Status: "closed", Direction: not null, OpenedAtUtc: not null, ClosedAtUtc: not null, NetResultUsd: not null,
            EntryNotional: { } notional } && notional.Units.Sign > 0 &&
        !operation.Coverage.Contains(ActivityPositions.AmbiguousIdentityNotice) && Guid.TryParse(operation.Id.AsSpan(0, Math.Min(36, operation.Id.Length)), out _);

    internal sealed record Scored(PlayOutcome Outcome, PlayOutcomeDto Row);

    private static List<Scored> PlayOutcomes(List<ClosedPlayFacts> closed) =>
        // A closed record whose linked fills are known incomplete, contradictory or out of order is not a decided outcome.
        closed.Where(f => f.Play.Origin != PlayOrigin.Retrospective && f.ManualFeesComplete)
            .Where(ExecutionIntegrity.Settled).Select(facts => (facts.Play, Outcome: PlayOutcomeCalculator.Outcome(facts))).Where(x => x.Outcome is not null)
            .Select(x => new Scored(x.Outcome!, Row(x.Play.Id, x.Play.Title, x.Play.ContractId, x.Outcome!))).ToList();

    /// <summary>
    /// Closed, complete operations with a known net result and entry notional, scored like a Play: return is the net
    /// result over the exact entry notional, unlevered. Ambiguous native identities are left out. Planned risk is unknown.
    /// Values the Play calculator would refuse (outside decimal range or exact precision, including the return) are
    /// unknown and left out, never clamped.
    /// </summary>
    internal static List<Scored> ActivityOutcomes(IEnumerable<ActivityOperationDto> operations)
    {
        var result = new List<Scored>();
        foreach (var operation in operations.Where(Candidate))
        {
            try
            {
                var outcome = new PlayOutcome(Guid.Parse(operation.Id.AsSpan(0, 36)), operation.ClosedAtUtc!.Value,
                    PlayOutcomeCalculator.ToDecimal(ExactAmount.Parse(operation.NetResultUsd!)), PlayOutcomeCalculator.ToDecimal(operation.EntryNotional!.Value),
                    null, true);
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
        var limits = SizingLimits.From(risk, record, exposure);
        var recent = outcomes.OrderByDescending(x => x.Outcome.ClosedAtUtc).ThenBy(x => x.Outcome.PlayId).Take(record.Window)
            .Select(x => x.Row).ToList();
        var activitySource = source == SourceActivity;
        var notice = activitySource ? ActivityNotice : Notice;
        return new SizingDto(new SizingSettingsDto(ExactDecimal.FormatEightPlaces(risk)),
            new ExposureDto(Name(exposure.Level), ExactDecimal.FormatEightPlaces(exposure.Multiplier),
                activitySource ? exposure.Reason + ActivityReason : exposure.Reason, exposure.LossStreak, exposure.WinsSinceStepDown),
            new TrackRecordDto(record.ClosedPlays, record.DecidedPlays, record.Window, record.Wins, record.Losses, record.Scratches,
                ExactDecimal.FormatEightPlaces(record.BattingAverage), ExactDecimal.FormatEightPlaces(record.AverageGainPercent),
                ExactDecimal.FormatEightPlaces(record.AverageLossPercent), ExactDecimal.FormatEightPlaces(record.WinLossRatio),
                ExactDecimal.FormatEightPlaces(record.BreakEvenRewardRisk), ExactDecimal.FormatEightPlaces(record.AverageWinR),
                ExactDecimal.FormatEightPlaces(record.AverageLossR), recent) { Source = source },
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
