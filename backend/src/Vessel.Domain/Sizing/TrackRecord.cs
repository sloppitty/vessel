using Vessel.Domain.Common;
using Vessel.Domain.Numerics;

namespace Vessel.Domain.Sizing;

public enum PlayOutcomeKind { Win, Loss, Scratch }

/// <summary>
/// What one closed Play returned, from its linked fills. Return is the net result over the entry notional,
/// an unlevered price-move equivalent. Planned risk is the entry notional at the planned stop distance;
/// it and the R multiple are null when the plan had no priced stop.
/// </summary>
public sealed record PlayOutcome(Guid PlayId, DateTimeOffset ClosedAtUtc, decimal NetResult, decimal EntryNotional,
    decimal? PlannedRisk, bool FeesComplete)
{
    public decimal ReturnPercent => EntryNotional > 0 ? NetResult / EntryNotional * 100 : 0;
    public decimal? RMultiple => PlannedRisk is > 0 and var risk ? NetResult / risk : null;
    public PlayOutcomeKind Kind => NetResult switch { > 0 => PlayOutcomeKind.Win, < 0 => PlayOutcomeKind.Loss, _ => PlayOutcomeKind.Scratch };
}

/// <summary>
/// Batting average and win/loss ratio over the most recent decided plays, after Minervini: the share of
/// winners matters only together with the average gain over the average loss. Unknown figures are null.
/// </summary>
public sealed record TrackRecord(int ClosedPlays, int DecidedPlays, int Window, int Wins, int Losses, int Scratches,
    decimal? BattingAverage, decimal? AverageGainPercent, decimal? AverageLossPercent, decimal? WinLossRatio,
    decimal? BreakEvenRewardRisk, decimal? AverageWinR, decimal? AverageLossR)
{
    public const int DefaultWindow = 20;

    /// <summary>Outcomes in any order; the window takes the latest decided ones by close time.</summary>
    public static TrackRecord From(IReadOnlyCollection<PlayOutcome> outcomes, int window = DefaultWindow)
    {
        var decided = outcomes.Where(o => o.Kind != PlayOutcomeKind.Scratch).OrderByDescending(o => o.ClosedAtUtc).ThenBy(o => o.PlayId)
            .Take(window).ToList();
        var wins = decided.Where(o => o.Kind == PlayOutcomeKind.Win).ToList();
        var losses = decided.Where(o => o.Kind == PlayOutcomeKind.Loss).ToList();
        decimal? batting = decided.Count > 0 ? (decimal)wins.Count / decided.Count : null;
        decimal? gain = wins.Count > 0 ? Average(wins.Select(o => o.ReturnPercent).ToList()) : null;
        decimal? loss = losses.Count > 0 ? Average(losses.Select(o => -o.ReturnPercent).ToList()) : null;
        return new TrackRecord(outcomes.Count, outcomes.Count(o => o.Kind != PlayOutcomeKind.Scratch), window, wins.Count, losses.Count,
            outcomes.Count(o => o.Kind == PlayOutcomeKind.Scratch), batting, gain, loss,
            gain is { } g && loss is > 0 and var l ? Ratio(g, l) : null,
            batting is > 0 and var b ? (1 - b) / b : null,
            AverageR(wins), AverageR(losses));
    }

    private static decimal? AverageR(List<PlayOutcome> outcomes)
    {
        var known = outcomes.Where(o => o.RMultiple.HasValue).Select(o => o.RMultiple!.Value).ToList();
        return known.Count > 0 ? Average(known) : null;
    }

    /// <summary>
    /// The decimal mean when the decimal sum fits; otherwise the exact sum over the count, rounded once. The mean of
    /// representable terms is itself representable, so an overflowing intermediate sum never fails the record.
    /// </summary>
    private static decimal Average(List<decimal> values)
    {
        try
        {
            return values.Average();
        }
        catch (OverflowException)
        {
            var sum = values.Aggregate(default(ExactAmount), (total, value) => total + ExactAmount.From(value));
            return sum.ToDecimal(values.Count);
        }
    }

    /// <summary>A derived ratio outside decimal range is unknown, never clamped or zeroed.</summary>
    private static decimal? Ratio(decimal numerator, decimal denominator)
    {
        try
        {
            return numerator / denominator;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}

/// <summary>
/// Decided outcomes that share an exact close time with another decided outcome, and how many distinct times they share.
/// The record and the ladder keep the stable Play-ID order for them: no economic order is invented, the tie is disclosed.
/// </summary>
public sealed record CloseTimeTies(int Outcomes, int Times)
{
    public static CloseTimeTies From(IEnumerable<PlayOutcome> outcomes)
    {
        var shared = outcomes.Where(o => o.Kind != PlayOutcomeKind.Scratch).GroupBy(o => o.ClosedAtUtc)
            .Select(g => g.Count()).Where(count => count > 1).ToList();
        return new(shared.Sum(), shared.Count);
    }

    /// <summary>The sentence the exposure reason ends with, or null without a tie.</summary>
    public string? Reason => Outcomes == 0 ? null : Times == 1
        ? $"{Outcomes} results share an exit time; their order is not known."
        : $"{Outcomes} results share {Times} exit times; their order is not known.";
}

public enum ExposureLevel { Full, Half, Quarter }

/// <summary>
/// Progressive exposure: smaller size after a losing streak, back up only once trades work again. Minervini
/// gives no fixed counts; these are the owner's: three consecutive losses step down to half, two more to a
/// quarter, and two consecutive wins at a reduced level with a positive net result since stepping down step back up
/// one level (a loss resets the win count). Scratches are ignored. Outcomes sharing a close time walk in Play-ID order.
/// </summary>
public sealed record Exposure(ExposureLevel Level, int LossStreak, int WinsSinceStepDown, string Reason)
{
    public const int LossesToHalf = 3;
    public const int LossesToQuarter = 2;
    public const int WinsToStepUp = 2;

    public decimal Multiplier => Level switch { ExposureLevel.Full => 1m, ExposureLevel.Half => 0.5m, _ => 0.25m };

    public static Exposure From(IEnumerable<PlayOutcome> outcomes)
    {
        var level = ExposureLevel.Full;
        int streak = 0, wins = 0;
        // Only the sign of the running result matters; exact units cannot overflow however large the results are.
        var sinceStepDown = default(ExactAmount);
        var reason = "Full size: no losing streak.";
        foreach (var outcome in outcomes.Where(o => o.Kind != PlayOutcomeKind.Scratch).OrderBy(o => o.ClosedAtUtc).ThenBy(o => o.PlayId))
        {
            if (level != ExposureLevel.Full) sinceStepDown += ExactAmount.From(outcome.NetResult);
            if (outcome.Kind == PlayOutcomeKind.Loss)
            {
                streak++;
                wins = 0;
                if (level == ExposureLevel.Full && streak >= LossesToHalf || level == ExposureLevel.Half && streak >= LossesToQuarter)
                {
                    level = level == ExposureLevel.Full ? ExposureLevel.Half : ExposureLevel.Quarter;
                    reason = $"{(level == ExposureLevel.Half ? "Half" : "Quarter")} size after {streak} losses in a row.";
                    streak = 0;
                    sinceStepDown = default;
                }
                continue;
            }
            streak = 0;
            if (level == ExposureLevel.Full) continue;
            wins++;
            if (wins >= WinsToStepUp && sinceStepDown.Units.Sign > 0)
            {
                level = level == ExposureLevel.Quarter ? ExposureLevel.Half : ExposureLevel.Full;
                reason = level == ExposureLevel.Full ? $"Full size again after {wins} wins at half size."
                    : $"Half size after {wins} wins at quarter size.";
                wins = 0;
                sinceStepDown = default;
            }
        }
        if (level != ExposureLevel.Full && wins > 0)
            reason += wins < WinsToStepUp ? $" {wins} of {WinsToStepUp} wins toward stepping up."
                : $" {wins} wins; steps up once the result since stepping down is positive.";
        return new Exposure(level, streak, wins, reason);
    }
}

/// <summary>Where a limit comes from: the default rule or the owner's record.</summary>
public enum LimitSource { Default, AverageGain, BattingAverage }

/// <summary>
/// The suggestion limits from the owner's risk setting and record. Stop distance is the price move from
/// entry, not the return on margin: at most 10%, or half the average gain once there are enough wins.
/// Reward-to-risk is at least 2, or the break-even ratio once there are enough decided plays.
/// </summary>
public sealed record SizingLimits(decimal EffectiveRiskPercent, decimal MaxStopPercent, LimitSource MaxStopSource,
    decimal MinRewardRisk, LimitSource MinRewardRiskSource)
{
    public const decimal DefaultMaxStopPercent = 10;
    public const decimal DefaultMinRewardRisk = 2;
    public const int WinsForAverageGain = 5;
    public const int DecidedForBattingAverage = 10;

    public static SizingLimits From(decimal riskPercent, TrackRecord record, Exposure exposure)
    {
        var (stop, stopSource) = record.Wins >= WinsForAverageGain && record.AverageGainPercent is { } gain && gain / 2 < DefaultMaxStopPercent
            ? (gain / 2, LimitSource.AverageGain) : (DefaultMaxStopPercent, LimitSource.Default);
        var (ratio, ratioSource) = record.DecidedPlays >= DecidedForBattingAverage && record.BreakEvenRewardRisk is { } even && even > DefaultMinRewardRisk
            ? (even, LimitSource.BattingAverage) : (DefaultMinRewardRisk, LimitSource.Default);
        // With no wins in the window the break-even ratio is unbounded; the default stands until there is one.
        return new SizingLimits(riskPercent * exposure.Multiplier, stop, stopSource, ratio, ratioSource);
    }
}

/// <summary>The owner's sizing preferences. Risk per trade is a percentage of the play's account balance.</summary>
public sealed class SizingSettings : IOwned
{
    public const decimal DefaultRiskPercent = 1.25m;
    public const decimal MinRiskPercent = 0.1m;
    public const decimal MaxRiskPercent = 5m;
    public const decimal WarnRiskPercent = 2.5m;

    public Guid OwnerId { get; private set; }
    public decimal RiskPercent { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    private SizingSettings() { }

    public SizingSettings(Guid ownerId, decimal riskPercent, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner ID must be nonempty.");
        OwnerId = ownerId;
        SetRisk(riskPercent, now);
    }

    public void SetRisk(decimal riskPercent, DateTimeOffset now)
    {
        if (riskPercent is < MinRiskPercent or > MaxRiskPercent || decimal.Round(riskPercent, 2) != riskPercent)
            throw new DomainValidationException($"Risk per trade is {MinRiskPercent}% to {MaxRiskPercent}% with at most two decimals.");
        RiskPercent = riskPercent;
        UpdatedAtUtc = now;
    }
}
