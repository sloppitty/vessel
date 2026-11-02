using System.Globalization;
using Vessel.Domain.Sizing;

namespace Vessel.Tests;

/// <summary>Individually representable outcomes whose sums leave decimal range never fail the record or the ladder.</summary>
public sealed class SizingAggregateOverflowTests : SizingAggregateOverflowTestsValues
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture);

    private static PlayOutcome Outcome(int day, decimal net, decimal notional = 1, decimal? risk = null) =>
        new(Guid.NewGuid(), Start.AddDays(day), net, notional, risk, true);

    [Fact]
    public void Ten_maximal_returns_average_exactly_instead_of_overflowing()
    {
        // Each win returns 1e28 percent (net 1e26 over a notional of 1); the decimal sum of ten is 1e29.
        var outcomes = Enumerable.Range(0, 10).Select(i => Outcome(i, E28 / 100)).ToList();
        Assert.Throws<OverflowException>(() => outcomes.Average(o => o.ReturnPercent));
        var record = TrackRecord.From(outcomes);
        Assert.Equal((10, 10, E28, (decimal?)null), (record.Wins, record.DecidedPlays, record.AverageGainPercent, record.AverageLossPercent));
        Assert.Equal(1m, record.BattingAverage);
    }

    [Fact]
    public void Overflowing_average_rounds_to_the_nearest_representable_mean()
    {
        var big = 7 * E28;
        var outcomes = Enumerable.Range(0, 9).Select(i => Outcome(i, -big / 100)).Append(Outcome(9, -0.01m)).ToList();
        var record = TrackRecord.From(outcomes);
        // (9 x 7e28 + 1) / 10 = 6.3e28 + 0.1, beyond decimal precision.
        Assert.Equal(6.3m * E28, record.AverageLossPercent);
        Assert.Null(record.AverageGainPercent);
    }

    [Fact]
    public void Overflowing_r_multiples_average_exactly()
    {
        var tiny = decimal.Parse("1e-28", NumberStyles.Float, CultureInfo.InvariantCulture);
        var outcomes = Enumerable.Range(0, 10).Select(i => Outcome(i, 1, notional: 100, risk: tiny)).ToList();
        Assert.Equal(E28, outcomes[0].RMultiple);
        var record = TrackRecord.From(outcomes);
        Assert.Equal((E28, 1m), (record.AverageWinR, record.AverageGainPercent));
    }

    [Fact]
    public void An_unrepresentable_win_loss_ratio_is_unknown_and_limits_keep_their_defaults()
    {
        var tinyLoss = -decimal.Parse("1e-22", NumberStyles.Float, CultureInfo.InvariantCulture);
        var record = TrackRecord.From([Outcome(0, 7 * E28 / 100), Outcome(1, tinyLoss)]);
        Assert.Equal(7 * E28, record.AverageGainPercent);
        Assert.Equal(decimal.Parse("1e-20", NumberStyles.Float, CultureInfo.InvariantCulture), record.AverageLossPercent);
        Assert.Null(record.WinLossRatio);
        Assert.Equal(1m, record.BreakEvenRewardRisk);
        var limits = SizingLimits.From(1.25m, record, Exposure.From([]));
        Assert.Equal((10m, LimitSource.Default, 2m, LimitSource.Default),
            (limits.MaxStopPercent, limits.MaxStopSource, limits.MinRewardRisk, limits.MinRewardRiskSource));
    }

    [Fact]
    public void Sixteen_total_losses_reach_quarter_size_without_overflowing_the_running_result()
    {
        var losses = Enumerable.Range(0, 16).Select(i => Outcome(i, -E28, notional: E28)).ToList();
        Assert.All(losses, o => Assert.Equal(-100m, o.ReturnPercent));
        var exposure = Exposure.From(losses);
        Assert.Equal((ExposureLevel.Quarter, 11, 0), (exposure.Level, exposure.LossStreak, exposure.WinsSinceStepDown));
    }

    [Fact]
    public void Ladder_steps_up_on_the_exact_sign_after_an_overflowing_drawdown()
    {
        var big = 7 * E28;
        List<PlayOutcome> outcomes = [Outcome(0, -1), Outcome(1, -1), Outcome(2, -1), Outcome(3, -1), Outcome(4, -1)];
        // At quarter: -14e28 in total (beyond decimal), then two wins that only cancel it exactly to zero.
        outcomes.AddRange([Outcome(5, -big), Outcome(6, -big), Outcome(7, big), Outcome(8, big)]);
        var stuck = Exposure.From(outcomes);
        Assert.Equal((ExposureLevel.Quarter, 2), (stuck.Level, stuck.WinsSinceStepDown));
        // A tiny further win makes the exact running result positive: step up one level, and the result resets.
        outcomes.Add(Outcome(9, 0.0000001m));
        var up = Exposure.From(outcomes);
        Assert.Equal((ExposureLevel.Half, 0), (up.Level, up.WinsSinceStepDown));
        // Reset means a later loss at half needs two to reach quarter again, and old wins do not count.
        outcomes.AddRange([Outcome(10, -big), Outcome(11, big)]);
        var again = Exposure.From(outcomes);
        Assert.Equal((ExposureLevel.Half, 1), (again.Level, again.WinsSinceStepDown));
    }

    [Fact]
    public void Ordinary_values_match_the_plain_decimal_computation()
    {
        var random = new Random(4242);
        for (var round = 0; round < 300; round++)
        {
            var outcomes = Enumerable.Range(0, random.Next(0, 40)).Select(i =>
            {
                var notional = random.Next(1, 100_000) / 100m;
                var net = random.Next(-3, 4) == 0 ? 0 : random.Next(-500_000, 500_000) / 1000m;
                decimal? risk = random.Next(3) == 0 ? null : random.Next(1, 10_000) / 100m;
                return new PlayOutcome(Guid.NewGuid(), Start.AddHours(random.Next(0, 2000)), net, notional, risk, true);
            }).ToList();
            var record = TrackRecord.From(outcomes);
            var decided = outcomes.Where(o => o.Kind != PlayOutcomeKind.Scratch).OrderByDescending(o => o.ClosedAtUtc).ThenBy(o => o.PlayId)
                .Take(TrackRecord.DefaultWindow).ToList();
            var wins = decided.Where(o => o.Kind == PlayOutcomeKind.Win).ToList();
            var losses = decided.Where(o => o.Kind == PlayOutcomeKind.Loss).ToList();
            decimal? gain = wins.Count > 0 ? wins.Average(o => o.ReturnPercent) : null;
            decimal? loss = losses.Count > 0 ? losses.Average(o => -o.ReturnPercent) : null;
            static decimal? R(List<PlayOutcome> list) => list.Where(o => o.RMultiple.HasValue).Select(o => o.RMultiple!.Value).ToList() is { Count: > 0 } k
                ? k.Average() : null;
            Assert.Equal(gain, record.AverageGainPercent);
            Assert.Equal(loss, record.AverageLossPercent);
            Assert.Equal(gain is { } g && loss is > 0 and var l ? g / l : null, record.WinLossRatio);
            Assert.Equal((R(wins), R(losses)), (record.AverageWinR, record.AverageLossR));
            // Scale is part of the decimal: ordinary results keep today's exact text too.
            Assert.Equal(gain?.ToString(CultureInfo.InvariantCulture), record.AverageGainPercent?.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(PlainLadder(outcomes), Exposure.From(outcomes));
        }
    }

    /// <summary>The ladder as it was with a decimal running result, for ordinary values.</summary>
    private static Exposure PlainLadder(IEnumerable<PlayOutcome> outcomes)
    {
        var level = ExposureLevel.Full;
        int streak = 0, wins = 0;
        decimal since = 0;
        var reason = "Full size: no losing streak.";
        foreach (var outcome in outcomes.Where(o => o.Kind != PlayOutcomeKind.Scratch).OrderBy(o => o.ClosedAtUtc).ThenBy(o => o.PlayId))
        {
            if (level != ExposureLevel.Full) since += outcome.NetResult;
            if (outcome.Kind == PlayOutcomeKind.Loss)
            {
                streak++;
                wins = 0;
                if (level == ExposureLevel.Full && streak >= 3 || level == ExposureLevel.Half && streak >= 2)
                {
                    level = level == ExposureLevel.Full ? ExposureLevel.Half : ExposureLevel.Quarter;
                    reason = $"{(level == ExposureLevel.Half ? "Half" : "Quarter")} size after {streak} losses in a row.";
                    streak = 0;
                    since = 0;
                }
                continue;
            }
            streak = 0;
            if (level == ExposureLevel.Full) continue;
            wins++;
            if (wins >= 2 && since > 0)
            {
                level = level == ExposureLevel.Quarter ? ExposureLevel.Half : ExposureLevel.Full;
                reason = level == ExposureLevel.Full ? $"Full size again after {wins} wins at half size." : $"Half size after {wins} wins at quarter size.";
                wins = 0;
                since = 0;
            }
        }
        if (level != ExposureLevel.Full && wins > 0)
            reason += wins < 2 ? $" {wins} of 2 wins toward stepping up." : $" {wins} wins; steps up once the result since stepping down is positive.";
        return new Exposure(level, streak, wins, reason);
    }
}
