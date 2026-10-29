using Vessel.Application.Plays;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class SizingRuleTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    /// <summary>Outcomes one day apart; positive is a win of that return on a 1,000 notional, negative a loss.</summary>
    private static List<PlayOutcome> Outcomes(params decimal[] returns) => returns
        .Select((r, i) => new PlayOutcome(Guid.NewGuid(), Start.AddDays(i), r * 10, 1000, 50, true)).ToList();

    [Fact]
    public void Three_losses_halve_and_two_more_quarter_the_size()
    {
        Assert.Equal(ExposureLevel.Full, Exposure.From(Outcomes(-1, -1)).Level);
        var half = Exposure.From(Outcomes(-1, -1, -1));
        Assert.Equal((ExposureLevel.Half, 0.5m, 0), (half.Level, half.Multiplier, half.LossStreak));
        Assert.Equal("Half size after 3 losses in a row.", half.Reason);
        Assert.Equal(ExposureLevel.Half, Exposure.From(Outcomes(-1, -1, -1, -1)).Level);
        var quarter = Exposure.From(Outcomes(-1, -1, -1, -1, -1));
        Assert.Equal((ExposureLevel.Quarter, 0.25m), (quarter.Level, quarter.Multiplier));
        Assert.Equal(ExposureLevel.Quarter, Exposure.From(Outcomes(-1, -1, -1, -1, -1, -1, -1, -1)).Level);
    }

    [Fact]
    public void A_win_breaks_the_streak_and_scratches_are_ignored()
    {
        Assert.Equal(ExposureLevel.Full, Exposure.From(Outcomes(-1, -1, 2, -1, -1)).Level);
        Assert.Equal(ExposureLevel.Half, Exposure.From(Outcomes(-1, 0, -1, 0, -1)).Level);
        Assert.Equal(2, Exposure.From(Outcomes(-1, -1, 0)).LossStreak);
    }

    [Fact]
    public void Two_wins_with_a_positive_result_step_back_up_one_level()
    {
        var waiting = Exposure.From(Outcomes(-1, -1, -1, 2));
        Assert.Equal((ExposureLevel.Half, 1), (waiting.Level, waiting.WinsSinceStepDown));
        Assert.Contains("1 of 2 wins", waiting.Reason);
        var full = Exposure.From(Outcomes(-1, -1, -1, 2, 2));
        Assert.Equal((ExposureLevel.Full, "Full size again after 2 wins at half size."), (full.Level, full.Reason));
        Assert.Equal(ExposureLevel.Half, Exposure.From(Outcomes(-1, -1, -1, -1, -1, 2, 2)).Level);
        Assert.Equal(ExposureLevel.Full, Exposure.From(Outcomes(-1, -1, -1, -1, -1, 2, 2, 2, 2)).Level);
    }

    [Fact]
    public void Wins_after_a_step_down_must_recover_the_losses_since()
    {
        // Two small wins after a larger loss at half size are not yet a positive result.
        var stuck = Exposure.From(Outcomes(-1, -1, -1, -5, 1, 1));
        Assert.Equal((ExposureLevel.Half, 2), (stuck.Level, stuck.WinsSinceStepDown));
        Assert.Contains("steps up once the result since stepping down is positive", stuck.Reason);
        Assert.Equal(ExposureLevel.Full, Exposure.From(Outcomes(-1, -1, -1, -5, 1, 1, 4)).Level);
    }

    [Fact]
    public void Record_reports_batting_average_and_win_loss_ratio_over_the_latest_window()
    {
        var record = TrackRecord.From(Outcomes(12, -4, 0, 8, -6, -5));
        Assert.Equal((6, 5, 2, 3, 1), (record.ClosedPlays, record.DecidedPlays, record.Wins, record.Losses, record.Scratches));
        Assert.Equal(0.4m, record.BattingAverage);
        Assert.Equal(10m, record.AverageGainPercent);
        Assert.Equal(5m, record.AverageLossPercent);
        Assert.Equal(2m, record.WinLossRatio);
        Assert.Equal(1.5m, record.BreakEvenRewardRisk);
        Assert.Equal(2m, record.AverageWinR);
        Assert.Equal(-1m, record.AverageLossR);

        var windowed = TrackRecord.From(Outcomes(-1, -1, 3, 3), window: 2);
        Assert.Equal((2, 1m), (windowed.Wins, windowed.BattingAverage));
        Assert.Null(windowed.WinLossRatio);
        var empty = TrackRecord.From([]);
        Assert.Null(empty.BattingAverage);
        Assert.Null(TrackRecord.From(Outcomes(-1)).BreakEvenRewardRisk);
    }

    [Fact]
    public void Limits_default_to_ten_percent_and_two_to_one_until_the_record_says_more()
    {
        var full = Exposure.From([]);
        var none = SizingLimits.From(1.25m, TrackRecord.From([]), full);
        Assert.Equal((1.25m, 10m, LimitSource.Default, 2m, LimitSource.Default),
            (none.EffectiveRiskPercent, none.MaxStopPercent, none.MaxStopSource, none.MinRewardRisk, none.MinRewardRiskSource));

        // Five wins averaging 8% cap the stop at 4%; 25% winners over ten plays need 3:1.
        var record = TrackRecord.From(Outcomes(8, 8, 8, 8, 8, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2));
        var limits = SizingLimits.From(2m, record, Exposure.From([]));
        Assert.Equal((4m, LimitSource.AverageGain, 3m, LimitSource.BattingAverage),
            (limits.MaxStopPercent, limits.MaxStopSource, limits.MinRewardRisk, limits.MinRewardRiskSource));
        Assert.Equal(1m, SizingLimits.From(2m, record, Exposure.From(Outcomes(-1, -1, -1))).EffectiveRiskPercent);

        // A large average gain never loosens the 10% stop.
        Assert.Equal(10m, SizingLimits.From(1, TrackRecord.From(Outcomes(40, 40, 40, 40, 40)), full).MaxStopPercent);
    }

    [Theory]
    [InlineData("0.09")]
    [InlineData("5.01")]
    [InlineData("1.255")]
    [InlineData("-1")]
    public void Settings_reject_risk_outside_the_range(string risk) =>
        Assert.Throws<DomainValidationException>(() => new SizingSettings(Guid.NewGuid(), decimal.Parse(risk, System.Globalization.CultureInfo.InvariantCulture), Start));
}

public sealed class SizingServiceTests : SizingServiceTestsValues
{

    [Fact]
    public void Outcome_is_closed_pnl_less_usdc_fees_with_risk_from_the_plan_at_the_first_entry_fill()
    {
        // Revised two hours after planning; the entry filled 30 minutes after planning, so revision 1 (stop 95) applies.
        var facts = Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 2)), new(OrderLinkRole.Target, Fill(110, 2, pnl: 20))],
            revised: Plan(entries: Entry("entry-1", "100", "100", new PlanExit("stop-1", "price", "90", "100"))));
        var outcome = PlayOutcomeCalculator.Outcome(facts)!;
        Assert.Equal((19.8m, 200m, 10m, PlayOutcomeKind.Win, true), (outcome.NetResult, outcome.EntryNotional, outcome.PlannedRisk, outcome.Kind, outcome.FeesComplete));
        Assert.Equal(9.9m, outcome.ReturnPercent);
        Assert.Equal(1.98m, outcome.RMultiple);
    }

    [Fact]
    public void Net_of_fee_closed_pnl_is_not_reduced_by_its_fee_again()
    {
        // RISEx-style: every fill's closed PnL already includes its fee (an opening fill reports -fee).
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Plan(), [
            new(OrderLinkRole.Entry, Fill(100, 2, pnl: -0.1m, fee: 0.1m, pnlBasis: ExecutionFacts.PnlNetOfFee)),
            new(OrderLinkRole.Target, Fill(110, 2, pnl: 19.9m, fee: 0.1m, pnlBasis: ExecutionFacts.PnlNetOfFee))]))!;
        Assert.Equal((19.8m, true, PlayOutcomeKind.Win), (outcome.NetResult, outcome.FeesComplete, outcome.Kind));
    }

    [Fact]
    public void A_net_of_fee_fill_paid_in_another_token_still_counts_as_complete()
    {
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Plan(), [
            new(OrderLinkRole.Entry, Fill(100, 1, pnl: -0.5m, fee: 0.5m, token: "HYPE", pnlBasis: ExecutionFacts.PnlNetOfFee)),
            new(OrderLinkRole.Stop, Fill(95, 1, pnl: -5.1m, pnlBasis: ExecutionFacts.PnlNetOfFee))]))!;
        Assert.Equal((-5.6m, true), (outcome.NetResult, outcome.FeesComplete));
    }

    [Fact]
    public void Fees_in_another_token_are_left_out_and_flagged()
    {
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 1, fee: 0.5m, token: "HYPE")),
            new(OrderLinkRole.Stop, Fill(95, 1, pnl: -5))]))!;
        Assert.Equal((-5.1m, false, PlayOutcomeKind.Loss), (outcome.NetResult, outcome.FeesComplete, outcome.Kind));
        Assert.Equal(-1.02m, outcome.RMultiple);
    }

    [Theory]
    [InlineData("USDG", "USDG")]
    [InlineData("USDC", "USDG")]
    [InlineData("USDT0", "USDe")]
    public void Fees_in_listed_stablecoins_are_deducted_nominally_even_when_mixed(string entryToken, string exitToken)
    {
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 1, fee: 0.5m, token: entryToken)),
            new(OrderLinkRole.Stop, Fill(95, 1, pnl: -5, fee: 0.25m, token: exitToken))]))!;
        Assert.Equal((-5.75m, true, PlayOutcomeKind.Loss), (outcome.NetResult, outcome.FeesComplete, outcome.Kind));
    }

    [Theory]
    [InlineData("usdg")]
    [InlineData("PYUSD")]
    [InlineData("")]
    public void Fees_in_tokens_outside_the_list_are_never_converted(string token)
    {
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 1, fee: 0.5m, token: token)),
            new(OrderLinkRole.Stop, Fill(95, 1, pnl: -5, fee: 0.25m, token: "USDG"))]))!;
        Assert.Equal((-5.25m, false), (outcome.NetResult, outcome.FeesComplete));
    }

    [Fact]
    public void Stop_distance_weights_entries_and_stops_and_resolves_percent_at_leverage()
    {
        // Entry 1: 60% at 100, stops 95 (50%) and 90 (50%) → 7.5 per unit. Entry 2: 40% at 90, a 20% stop at 4× → 5% → 4.5.
        var plan = Plan("4", Entry("a", "60", "100", new PlanExit("s1", "price", "95", "50"), new PlanExit("s2", "price", "90", "50")),
            Entry("b", "40", "90", new PlanExit("s3", "percent", "20", "100")));
        Assert.Equal((0.6m * 7.5m + 0.4m * 4.5m) / (0.6m * 100 + 0.4m * 90), PlayOutcomeCalculator.StopDistance(plan));
        Assert.Null(PlayOutcomeCalculator.StopDistance(Plan(entries: Entry("a", "100", "100"))));
        Assert.Null(PlayOutcomeCalculator.StopDistance(Plan(entries: Entry("a", "100", "", new PlanExit("s", "price", "95", "100")))));
    }

    [Fact]
    public void A_play_without_entry_fills_has_no_outcome()
    {
        Assert.Null(PlayOutcomeCalculator.Outcome(Closed(Plan(), [new(OrderLinkRole.Exit, Fill(100, 1, pnl: 3))])));
    }
}
