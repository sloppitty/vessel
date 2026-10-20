using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Paper;
using Vessel.Application.Plays;
using Vessel.Domain.Paper;

namespace Vessel.Tests;

public sealed class PaperEngineEvaluatorTests
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Account = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Play = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset At = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Margin_sizing_uses_quantity_weighted_average_and_does_not_normalize_entry_allocations()
    {
        var plan = Plan(size: "1500", mode: "margin", leverage: "2", entries:
            [Entry("a", "100", "25"), Entry("b", "200", "25")]);
        var allocations = PaperEvaluator.Allocations(plan);

        // Average entry = 150; total size = 20 units, but only 50% is allocated.
        Assert.Equal((5m, 250m), allocations["a"]);
        Assert.Equal((5m, 500m), allocations["b"]);
        Assert.Equal(750m, allocations.Values.Sum(a => a.Margin));
    }

    [Fact]
    public void Quantity_sizing_allocates_whole_position_shares_with_entry_specific_margin()
    {
        var allocations = PaperEvaluator.Allocations(Plan(size: "600", entries:
            [Entry("a", "100", "40"), Entry("b", "200", "30")]));

        Assert.Equal((240m, 4800m), allocations["a"]);
        Assert.Equal((180m, 7200m), allocations["b"]);
    }

    [Fact]
    public void Nonparticipating_entry_needs_no_price_and_receives_no_allocation()
    {
        var allocations = PaperEvaluator.Allocations(Plan(entries:
            [Entry("a"), Entry("unused", "", "0")]));

        Assert.Equal((10m, 200m), allocations["a"]);
        Assert.Equal((0m, 0m), allocations["unused"]);
    }

    [Theory]
    [InlineData("", "5", "100", "100")]
    [InlineData("0", "5", "100", "100")]
    [InlineData("-1", "5", "100", "100")]
    [InlineData("10", "0", "100", "100")]
    [InlineData("10", "1.5", "100", "100")]
    [InlineData("10", "101", "100", "100")]
    [InlineData("10", "5", "", "100")]
    [InlineData("10", "5", "0", "100")]
    [InlineData("10", "5", "-100", "100")]
    [InlineData("10", "5", "100", "")]
    [InlineData("10", "5", "100", "0")]
    [InlineData("10", "5", "100", "-1")]
    [InlineData("10", "5", "100", "101")]
    [InlineData("1.00000000000000000000000000001", "5", "100", "100")]
    public void Incomplete_or_invalid_allocation_is_a_conflict(string size, string leverage, string price, string share)
    {
        var error = Assert.Throws<ProblemException>(() =>
            PaperEvaluator.Allocations(Plan(size: size, leverage: leverage, entries: [Entry(price: price, share: share)])));

        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public void Shares_over_one_hundred_and_duplicate_entry_ids_are_not_silently_normalized()
    {
        Assert.Equal(409, Assert.Throws<ProblemException>(() => PaperEvaluator.Allocations(Plan(entries:
            [Entry("a", share: "60"), Entry("b", share: "41")]))).StatusCode);
        Assert.Equal(409, Assert.Throws<ProblemException>(() => PaperEvaluator.Allocations(Plan(entries:
            [Entry("a", share: "50"), Entry("a", share: "50")]))).StatusCode);
    }

    [Theory]
    [InlineData("79228162514264337593543950335", "margin", "100", "100", "100")]
    [InlineData("79228162514264337593543950335", "quantity", "5", "100", "100")]
    [InlineData("0.0000000000000000000000000001", "quantity", "5", "100", "1")]
    public void Overflow_and_allocations_below_decimal_precision_are_conflicts(
        string size, string mode, string leverage, string price, string share)
    {
        var error = Assert.Throws<ProblemException>(() => PaperEvaluator.Allocations(
            Plan(size: size, mode: mode, leverage: leverage, entries: [Entry(price: price, share: share)])));

        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public void Total_committed_margin_must_fit_decimal_even_when_individual_entries_fit()
    {
        var plan = Plan(size: "2", leverage: "1", entries:
        [
            Entry("first", "60000000000000000000000000000", "50"),
            Entry("second", "60000000000000000000000000000", "50")
        ]);

        Assert.Equal(409, Assert.Throws<ProblemException>(() => PaperEvaluator.Allocations(plan)).StatusCode);
    }

    [Theory]
    [InlineData("quantity", "0.1", "10")]
    [InlineData("margin", "0.001", "0.1")]
    public void Small_positive_shares_do_not_round_to_zero_before_multiplying_position_size(
        string mode, string expectedQuantity, string expectedMargin)
    {
        var plan = Plan(size: "10000000000000000000000000000", mode: mode, leverage: "1",
            entries: [Entry(share: "0.000000000000000000000000001")]);
        var allocation = PaperEvaluator.Allocations(plan)["entry"];

        Assert.Equal(decimal.Parse(expectedQuantity, System.Globalization.CultureInfo.InvariantCulture), allocation.Quantity);
        Assert.Equal(decimal.Parse(expectedMargin, System.Globalization.CultureInfo.InvariantCulture), allocation.Margin);
    }

    [Fact]
    public void Contiguous_candles_are_processed_chronologically_with_whole_tranches_once()
    {
        var result = Evaluate([Activation(Plan())],
            [Candle(1, 100, 101, 99, 100), Candle(0, 100, 101, 99, 100)]);

        var entry = Assert.Single(result.Executions);
        Assert.Equal("entry", entry.Role);
        Assert.Equal(10, entry.Quantity);
        Assert.Equal(100, entry.Price);
        Assert.Equal(200, entry.MarginUsd);
        Assert.Equal(0, entry.PnlUsd);
        Assert.Equal(1, entry.PlanVersion);
        Assert.Equal(Owner, entry.OwnerId);
        Assert.Equal(Account, entry.AccountId);
        Assert.Equal(Play, entry.PlayId);
        Assert.Equal("paper", entry.TradingMode);
        Assert.Equal(At, entry.CandleOpenAtUtc);
        Assert.Equal(At.AddMinutes(1), entry.CandleCloseAtUtc);
        Assert.Equal(entry.CandleCloseAtUtc, entry.OccurredAtUtc);
        Assert.Equal(At.AddMinutes(2), result.CheckpointUtc);
        Assert.Equal(100, result.PreviousClose);
        Assert.Null(result.Issue);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void Missing_first_candle_does_not_jump_forward_to_a_touch()
    {
        var result = Evaluate([Activation(Plan())], [Candle(1, 100, 101, 99, 100)]);

        Assert.Empty(result.Executions);
        Assert.Null(result.CheckpointUtc);
        Assert.Null(result.PreviousClose);
        Assert.Contains("Missing", result.Issue);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void Later_gap_returns_valid_earlier_facts_and_keeps_the_successful_frontier()
    {
        var result = Evaluate([Activation(Plan())],
            [Candle(0, 100, 101, 99, 100), Candle(2, 110, 111, 109, 110)]);

        Assert.Equal("entry", Assert.Single(result.Executions).Role);
        Assert.Equal(At.AddMinutes(1), result.CheckpointUtc);
        Assert.Equal(100, result.PreviousClose);
        Assert.Contains("Missing", result.Issue);
    }

    [Fact]
    public void A_due_empty_window_is_a_gap_but_an_unclosed_minute_is_only_waiting()
    {
        var activations = new[] { Activation(Plan()) };
        var missing = Evaluate(activations, [], through: At.AddMinutes(1));
        var waiting = Evaluate(activations, [Candle(0, 100, 101, 99, 100)], through: At.AddSeconds(59));

        Assert.Contains("Missing", missing.Issue);
        Assert.Empty(waiting.Executions);
        Assert.Null(waiting.Issue);
        Assert.Null(waiting.CheckpointUtc);
    }

    [Fact]
    public void A_supplied_checkpoint_is_the_first_expected_open_not_the_last_open()
    {
        var result = Evaluate([Activation(Plan())], [Candle(2, 100, 101, 99, 100)],
            checkpoint: At.AddMinutes(1), previousClose: 120);

        Assert.Empty(result.Executions);
        Assert.Equal(At.AddMinutes(1), result.CheckpointUtc);
        Assert.Equal(120, result.PreviousClose);
        Assert.Contains("Missing", result.Issue);
    }

    [Fact]
    public void Overlapping_old_candles_are_not_replayed_after_a_checkpoint()
    {
        var activations = new[] { Activation(Plan()) };
        var candle = Candle(0, 100, 101, 99, 100);
        var first = Evaluate(activations, [candle]);
        var replay = Evaluate(activations, [candle, Candle(1, 100, 101, 99, 100)],
            first.Executions, first.CheckpointUtc, first.PreviousClose);

        Assert.Empty(replay.Executions);
        Assert.Equal(At.AddMinutes(2), replay.CheckpointUtc);
        Assert.Null(replay.Issue);
    }

    [Fact]
    public void Duplicate_opens_freeze_before_any_facts_from_that_minute()
    {
        var result = Evaluate([Activation(Plan())],
            [Candle(0, 100, 101, 99, 100), Candle(0, 100, 101, 99, 100)]);

        Assert.Empty(result.Executions);
        Assert.Null(result.CheckpointUtc);
        Assert.Contains("Duplicate", result.Issue);
    }

    [Fact]
    public void A_later_duplicate_retains_earlier_unambiguous_entry_facts()
    {
        var result = Evaluate([Activation(Plan())],
            [Candle(0, 100, 101, 99, 100), Candle(1, 110, 111, 109, 110), Candle(1, 110, 111, 109, 110)]);

        Assert.Single(result.Executions);
        Assert.Equal(At.AddMinutes(1), result.CheckpointUtc);
        Assert.Contains("Duplicate", result.Issue);
    }

    [Theory]
    [InlineData(100, 99, 101, 100)]
    [InlineData(102, 101, 99, 100)]
    [InlineData(100, 101, 99, 102)]
    [InlineData(100, 101, 0, 100)]
    [InlineData(0, 101, 0, 100)]
    [InlineData(100, 101, 0, 0)]
    public void Inconsistent_ohlc_holds_the_frontier(decimal open, decimal high, decimal low, decimal close)
    {
        var result = Evaluate([Activation(Plan())], [Candle(0, open, high, low, close)]);

        Assert.Empty(result.Executions);
        Assert.Null(result.CheckpointUtc);
        Assert.Contains("Invalid", result.Issue);
    }

    [Fact]
    public void Inclusive_venue_millisecond_end_is_not_a_canonical_exclusive_close()
    {
        var candle = Candle(0, 100, 101, 99, 100) with { CloseAtUtc = At.AddMinutes(1).AddMilliseconds(-1) };
        var result = Evaluate([Activation(Plan())], [candle], through: At.AddMinutes(1));

        Assert.Empty(result.Executions);
        Assert.Contains("Invalid", result.Issue);
    }

    [Fact]
    public void Candle_longer_than_a_minute_is_not_accepted()
    {
        var candle = Candle(0, 100, 101, 99, 100) with { CloseAtUtc = At.AddMinutes(2) };
        var result = Evaluate([Activation(Plan())], [candle], through: At.AddMinutes(2));

        Assert.Empty(result.Executions);
        Assert.Contains("Invalid", result.Issue);
    }

    [Theory]
    [InlineData(90, 101)]
    [InlineData(99, 110)]
    public void Same_candle_entry_and_either_exit_is_ambiguous(decimal low, decimal high)
    {
        var result = Evaluate([Activation(Plan())], [Candle(0, 100, high, low, 100)]);

        Assert.True(result.Ambiguous);
        Assert.NotNull(result.Issue);
        Assert.Empty(result.Executions);
        Assert.Null(result.CheckpointUtc);
    }

    [Fact]
    public void Ambiguity_in_a_later_entry_rolls_back_the_entire_candle()
    {
        var plan = Plan(entries:
        [
            Entry("first", share: "50", targets: [Exit("far", "200")]),
            Entry("second", "105", "50", targets: [Exit("near", "110")])
        ]);
        var result = Evaluate([Activation(plan)], [Candle(0, 100, 111, 99, 105)]);

        Assert.Empty(result.Executions);
        Assert.True(result.Ambiguous);
        Assert.Null(result.CheckpointUtc);
    }

    [Fact]
    public void Opposing_exits_freeze_the_later_candle_but_not_the_earlier_entry()
    {
        var result = Evaluate([Activation(Plan())],
            [Candle(0, 100, 101, 99, 100), Candle(1, 100, 111, 89, 100)]);

        Assert.Equal("entry", Assert.Single(result.Executions).Role);
        Assert.True(result.Ambiguous);
        Assert.Equal(At.AddMinutes(1), result.CheckpointUtc);
        Assert.Equal(100, result.PreviousClose);
    }

    [Fact]
    public void Zero_share_exits_are_not_executable_or_ambiguous()
    {
        var plan = Plan(entries: [Entry(stops: [Exit("stop", "90", "0")], targets: [Exit("target", "110", "0")])]);
        var result = Evaluate([Activation(plan)], [Candle(0, 100, 111, 89, 100)]);

        Assert.Equal("entry", Assert.Single(result.Executions).Role);
        Assert.False(result.Ambiguous);
        Assert.Null(result.Issue);
    }

    [Fact]
    public void An_unset_stop_is_not_break_even()
    {
        var plan = Plan(entries: [Entry(stops: [Exit("stop", "", unit: "percent")])]);
        var result = Evaluate([Activation(plan)], [Candle(0, 100, 101, 99, 100)]);

        Assert.Equal("entry", Assert.Single(result.Executions).Role);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void Independent_entry_and_an_existing_entry_exit_are_not_intrinsically_ambiguous()
    {
        var plan = Plan(entries:
        [
            Entry("first", share: "50"),
            Entry("second", "120", "50", stops: [], targets: [Exit("target2", "130")])
        ]);
        var result = Evaluate([Activation(plan)],
            [Candle(0, 100, 101, 99, 100), Candle(1, 115, 121, 109, 120)]);

        Assert.Null(result.Issue);
        Assert.False(result.Ambiguous);
        Assert.Equal(new[] { "entry", "target", "entry" }, result.Executions.Select(e => e.Role));
        Assert.Equal("second", result.Executions[^1].EntryId);
    }

    [Fact]
    public void Partial_target_consumes_its_original_allocation_once_and_sibling_stop_caps_at_remainder()
    {
        var plan = Plan(entries: [Entry(targets: [Exit("target", "110", "30")])]);
        var result = Evaluate([Activation(plan)],
        [
            Candle(0, 100, 101, 99, 100), Candle(1, 110, 111, 109, 110),
            Candle(2, 110, 111, 109, 110), Candle(3, 90, 91, 89, 90)
        ]);

        Assert.Null(result.Issue);
        Assert.Equal(new[] { 10m, 3m, 7m }, result.Executions.Select(e => e.Quantity));
        Assert.Equal(new[] { 200m, -60m, -140m }, result.Executions.Select(e => e.MarginUsd));
        Assert.Equal(new[] { 0m, 30m, -70m }, result.Executions.Select(e => e.PnlUsd));
        Assert.Equal(0, result.Executions.Sum(e => e.MarginUsd));
        Assert.Equal(At.AddMinutes(4), result.CheckpointUtc);
    }

    [Fact]
    public void Each_target_share_is_of_whole_entered_quantity_not_the_remaining_quantity()
    {
        var plan = Plan(entries: [Entry(targets: [Exit("first", "110", "40"), Exit("second", "120", "40")])]);
        var result = Evaluate([Activation(plan)],
        [
            Candle(0, 100, 101, 99, 100), Candle(1, 110, 111, 109, 110),
            Candle(2, 120, 121, 119, 120)
        ]);

        Assert.Null(result.Issue);
        Assert.Equal(new[] { 10m, 4m, 4m }, result.Executions.Select(e => e.Quantity));
        Assert.Equal(new[] { 0m, 40m, 80m }, result.Executions.Select(e => e.PnlUsd));
    }

    [Fact]
    public void Several_same_side_targets_can_execute_their_planned_allocations_in_one_candle()
    {
        var plan = Plan(entries: [Entry(targets: [Exit("first", "110", "40"), Exit("second", "120", "60")])]);
        var result = Evaluate([Activation(plan)],
            [Candle(0, 100, 101, 99, 100), Candle(1, 110, 121, 109, 120)]);

        Assert.Null(result.Issue);
        Assert.Equal(new[] { 10m, 4m, 6m }, result.Executions.Select(e => e.Quantity));
        Assert.Equal(0, result.Executions.Sum(e => e.MarginUsd));
    }

    [Theory]
    [InlineData("long", 110, 100)]
    [InlineData("long", 90, -100)]
    [InlineData("short", 90, 100)]
    [InlineData("short", 110, -100)]
    public void Long_and_short_payoffs_use_defined_prices_without_fees_or_slippage(
        string direction, decimal exitPrice, decimal pnl)
    {
        var result = Evaluate([Activation(Plan(direction: direction))],
            [Candle(0, 100, 101, 99, 100), Candle(1, exitPrice, exitPrice + 1, exitPrice - 1, exitPrice)]);

        Assert.Null(result.Issue);
        var exit = result.Executions[1];
        Assert.Equal(exitPrice, exit.Price);
        Assert.Equal(pnl, exit.PnlUsd);
        Assert.Equal(-200, exit.MarginUsd);
        Assert.Equal(0, result.Executions.Sum(e => e.MarginUsd));
        Assert.Equal(At.AddMinutes(2), exit.OccurredAtUtc);
    }

    [Theory]
    [InlineData("long", 105)]
    [InlineData("short", 95)]
    public void Percent_levels_reuse_the_existing_leveraged_return_resolution(string direction, decimal targetPrice)
    {
        var plan = Plan(direction: direction, entries:
            [Entry(stops: [Exit("stop", "10", unit: "percent")], targets: [Exit("target", "25", unit: "percent")])]);
        var result = Evaluate([Activation(plan)],
            [Candle(0, 100, 101, 99, 100), Candle(1, targetPrice, targetPrice + 1, targetPrice - 1, targetPrice)]);

        Assert.Null(result.Issue);
        Assert.Equal(targetPrice, result.Executions[1].Price);
        Assert.Equal(50, result.Executions[1].PnlUsd);
    }

    [Fact]
    public void Covered_jump_through_stop_still_executes_at_the_defined_stop_price()
    {
        var result = Evaluate([Activation(Plan())],
            [Candle(0, 100, 101, 99, 100), Candle(1, 80, 81, 79, 80)]);

        Assert.Null(result.Issue);
        Assert.Equal("stop", result.Executions[1].Role);
        Assert.Equal(90, result.Executions[1].Price);
        Assert.Equal(-100, result.Executions[1].PnlUsd);
    }

    [Fact]
    public void Covered_jump_can_trigger_an_entry_while_missing_coverage_cannot()
    {
        var plan = Plan(entries: [Entry(stops: [], targets: [])]);
        var activations = new[] { Activation(plan) };
        var first = Candle(0, 110, 111, 109, 110);
        var covered = Evaluate(activations, [first, Candle(1, 90, 91, 89, 90)]);
        var gap = Evaluate(activations, [first, Candle(2, 90, 91, 89, 90)]);

        Assert.Equal(100, Assert.Single(covered.Executions).Price);
        Assert.Null(covered.Issue);
        Assert.Empty(gap.Executions);
        Assert.Contains("Missing", gap.Issue);
    }

    [Fact]
    public void First_activation_cannot_use_a_pre_arm_previous_close_or_earlier_candle_extremes()
    {
        var activation = Activation(Plan(), minute: 1);
        var result = Evaluate([activation],
            [Candle(0, 100, 101, 99, 100), Candle(1, 120, 121, 119, 120)],
            previousClose: 80);

        Assert.Empty(result.Executions);
        Assert.Equal(At.AddMinutes(2), result.CheckpointUtc);
        Assert.Null(result.Issue);
    }

    [Fact]
    public void Revision_boundary_disables_previous_close_crossing_but_subsequent_covered_minutes_can_cross()
    {
        var first = Plan(entries: [Entry(stops: [], targets: [])]);
        var next = Plan(entries: [Entry(price: "110", stops: [], targets: [])]);
        var activations = new[] { Activation(next, minute: 1, version: 2), Activation(first) };
        var boundary = Evaluate(activations,
            [Candle(0, 120, 121, 119, 120), Candle(1, 100, 101, 99, 100)]);
        var continued = Evaluate(activations, [Candle(2, 120, 121, 119, 120)],
            boundary.Executions, boundary.CheckpointUtc, boundary.PreviousClose);

        Assert.Empty(boundary.Executions);
        Assert.Null(boundary.Issue);
        Assert.Equal(110, Assert.Single(continued.Executions).Price);
        Assert.Equal(2, continued.Executions[0].PlanVersion);
    }

    [Fact]
    public void Restart_at_revision_boundary_also_suppresses_the_old_crossing()
    {
        var first = Plan(entries: [Entry(stops: [], targets: [])]);
        var next = Plan(entries: [Entry(price: "110", stops: [], targets: [])]);
        var result = Evaluate([Activation(first), Activation(next, minute: 1, version: 2)],
            [Candle(1, 100, 101, 99, 100)], checkpoint: At.AddMinutes(1), previousClose: 120);

        Assert.Empty(result.Executions);
        Assert.Null(result.Issue);
    }

    [Fact]
    public void Whole_historical_revision_is_selected_and_exits_keep_original_quantity_price_and_margin()
    {
        var old = Plan();
        var next = Plan(size: "999", leverage: "1", entries:
            [Entry(price: "200", targets: [Exit("changed", "120")])]);
        var result = Evaluate([Activation(next, minute: 2, version: 2), Activation(old)],
        [
            Candle(0, 100, 101, 99, 100), Candle(1, 105, 109, 101, 105),
            Candle(2, 120, 121, 119, 120)
        ]);

        Assert.Null(result.Issue);
        Assert.Equal(2, result.Executions.Count);
        Assert.Equal(1, result.Executions[0].PlanVersion);
        Assert.Equal(10, result.Executions[0].Quantity);
        Assert.Equal(200, result.Executions[0].MarginUsd);
        Assert.Equal(2, result.Executions[1].PlanVersion);
        Assert.Equal("changed", result.Executions[1].LevelId);
        Assert.Equal(10, result.Executions[1].Quantity);
        Assert.Equal(200, result.Executions[1].PnlUsd);
        Assert.Equal(-200, result.Executions[1].MarginUsd);
    }

    [Fact]
    public void Revised_percentage_exit_uses_retained_entry_price_and_current_leverage()
    {
        var next = Plan(leverage: "10", entries:
            [Entry(price: "200", targets: [Exit("percent", "50", unit: "percent")])]);
        var result = Evaluate([Activation(Plan()), Activation(next, minute: 1, version: 2)],
            [Candle(0, 100, 101, 99, 100), Candle(1, 105, 106, 104, 105)]);

        Assert.Null(result.Issue);
        Assert.Equal(105, result.Executions[1].Price);
        Assert.Equal(50, result.Executions[1].PnlUsd);
        Assert.Equal(-200, result.Executions[1].MarginUsd);
    }

    [Fact]
    public void Same_effective_time_selects_highest_plan_version_not_input_order()
    {
        var first = Plan();
        var next = Plan(entries: [Entry(price: "200")]);
        var result = Evaluate([Activation(next, version: 2), Activation(first)], [Candle(0, 100, 101, 99, 100)]);

        Assert.Empty(result.Executions);
        Assert.Null(result.Issue);
    }

    [Fact]
    public void Midminute_activation_cannot_reuse_a_straddling_candle()
    {
        var activation = Activation(Plan());
        activation.EffectiveAtUtc = At.AddSeconds(30);
        var result = Evaluate([activation], [Candle(0, 100, 101, 99, 100), Candle(1, 100, 101, 99, 100)]);

        Assert.Empty(result.Executions);
        Assert.NotNull(result.Issue);
        Assert.Null(result.CheckpointUtc);
    }

    [Fact]
    public void Final_exit_releases_exact_retained_margin_dust_even_after_leverage_revision()
    {
        var first = Plan(size: "3", leverage: "3", entries:
            [Entry(price: "1", stops: [Exit("stop", "0.5")], targets: [Exit("partial", "2", "33.33333333333333333333333333")])]);
        var activations = new[] { Activation(first), Activation(first with { Leverage = "17" }, minute: 2, version: 2) };
        var entered = Evaluate(activations, [Candle(0, 1, 1.1m, 0.9m, 1), Candle(1, 2, 2.1m, 1.9m, 2)]);
        var before = JsonSerializer.Serialize(entered.Executions);
        var closed = Evaluate(activations, [Candle(2, 0.5m, 0.6m, 0.4m, 0.5m)],
            entered.Executions, entered.CheckpointUtc, entered.PreviousClose);

        Assert.Null(entered.Issue);
        Assert.Null(closed.Issue);
        var exit = Assert.Single(closed.Executions);
        Assert.Equal(-entered.Executions.Sum(e => e.MarginUsd), exit.MarginUsd);
        Assert.Equal(0, entered.Executions.Concat(closed.Executions).Sum(e => e.MarginUsd));
        Assert.Equal(entered.Executions[0].Quantity,
            entered.Executions.Concat(closed.Executions).Where(e => e.Role != "entry").Sum(e => e.Quantity));
        Assert.Equal(before, JsonSerializer.Serialize(entered.Executions));
    }

    [Fact]
    public void A_previously_executed_level_cannot_execute_again_after_its_price_or_share_changes()
    {
        var first = Plan(entries: [Entry(targets: [Exit("target", "110", "30")])]);
        var next = Plan(entries: [Entry(targets: [Exit("target", "120", "100")])]);
        var activations = new[] { Activation(first), Activation(next, minute: 2, version: 2) };
        var before = Evaluate(activations, [Candle(0, 100, 101, 99, 100), Candle(1, 110, 111, 109, 110)]);
        var after = Evaluate(activations, [Candle(2, 120, 121, 119, 120)],
            before.Executions, before.CheckpointUtc, before.PreviousClose);

        Assert.Null(after.Issue);
        Assert.Empty(after.Executions);
        Assert.Equal(At.AddMinutes(3), after.CheckpointUtc);
    }

    [Fact]
    public void Closing_all_entered_quantity_disarms_unentered_tranches_before_later_candles()
    {
        var plan = Plan(entries:
        [
            Entry("first", share: "50"),
            Entry("never-entered", "80", "50", stops: [], targets: [])
        ]);
        var result = Evaluate([Activation(plan)],
        [
            Candle(0, 100, 101, 99, 100), Candle(1, 110, 111, 109, 110),
            Candle(2, 80, 81, 79, 80)
        ]);

        Assert.Null(result.Issue);
        Assert.Equal(new[] { "entry", "target" }, result.Executions.Select(e => e.Role));
        Assert.All(result.Executions, e => Assert.Equal("first", e.EntryId));
        Assert.Equal(At.AddMinutes(2), result.CheckpointUtc);
        Assert.Equal(110, result.PreviousClose);
    }

    [Fact]
    public void Retained_closed_position_does_not_reopen_an_unentered_tranche()
    {
        var plan = Plan(entries: [Entry("first", share: "50"), Entry("second", "80", "50", stops: [], targets: [])]);
        var activations = new[] { Activation(plan) };
        var first = Evaluate(activations, [Candle(0, 100, 101, 99, 100), Candle(1, 110, 111, 109, 110)]);
        var replay = Evaluate(activations, [Candle(2, 80, 81, 79, 80)],
            first.Executions, first.CheckpointUtc, first.PreviousClose);

        Assert.Empty(replay.Executions);
        Assert.Null(replay.Issue);
        Assert.Equal(first.CheckpointUtc, replay.CheckpointUtc);
    }

    [Fact]
    public void No_stop_or_target_leaves_an_entered_position_open_through_all_covered_candles()
    {
        var plan = Plan(entries: [Entry(stops: [], targets: [])]);
        var result = Evaluate([Activation(plan)], [Candle(0, 100, 101, 99, 100), Candle(1, 10, 11, 9, 10)]);

        Assert.Equal("entry", Assert.Single(result.Executions).Role);
        Assert.Null(result.Issue);
        Assert.Equal(At.AddMinutes(2), result.CheckpointUtc);
    }

    [Fact]
    public void Bad_later_plan_preserves_valid_earlier_candle_facts_without_applying_any_of_the_bad_minute()
    {
        var result = Evaluate([Activation(Plan()), Activation(Plan(size: ""), minute: 1, version: 2)],
            [Candle(0, 100, 101, 99, 100), Candle(1, 110, 111, 109, 110)]);

        Assert.Single(result.Executions);
        Assert.NotNull(result.Issue);
        Assert.Equal(At.AddMinutes(1), result.CheckpointUtc);
        Assert.Equal(100, result.PreviousClose);
    }

    [Fact]
    public void Evaluation_is_deterministic_and_does_not_mutate_inputs()
    {
        var activations = new[] { Activation(Plan()) };
        var candles = new[] { Candle(0, 100, 101, 99, 100), Candle(1, 110, 111, 109, 110) };
        var before = JsonSerializer.Serialize(new { activations, candles });
        var first = Evaluate(activations, candles);
        var second = Evaluate(activations, candles);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(before, JsonSerializer.Serialize(new { activations, candles }));
        Assert.NotEqual(Guid.Empty, first.Executions[0].Id);
        Assert.NotEqual(first.Executions[0].Id, first.Executions[1].Id);
    }

    [Fact]
    public void Foreign_retained_facts_fail_closed_instead_of_becoming_entry_evidence()
    {
        var activations = new[] { Activation(Plan()) };
        var entered = Evaluate(activations, [Candle(0, 100, 101, 99, 100)]);
        entered.Executions[0].OwnerId = Guid.NewGuid();
        var result = Evaluate(activations, [Candle(1, 110, 111, 109, 110)],
            entered.Executions, entered.CheckpointUtc, entered.PreviousClose);

        Assert.Empty(result.Executions);
        Assert.NotNull(result.Issue);
        Assert.Equal(At.AddMinutes(1), result.CheckpointUtc);
    }

    [Fact]
    public void Entry_admission_preserves_the_margin_of_untouched_pending_tranches()
    {
        var plan = Plan(entries:
        [
            Entry("cheap", share: "50", stops: [], targets: []),
            Entry("untouched", "200", "50", stops: [], targets: [])
        ]);
        var result = Evaluate([Activation(plan)], [Candle(0, 100, 101, 99, 100)], entryMarginBudget: 250);

        // The touched entry needs only 100, but all unentered allocations still require 300.
        Assert.Empty(result.Executions);
        Assert.Contains("Insufficient paper margin", result.Issue);
        Assert.False(result.Ambiguous);
        Assert.Null(result.CheckpointUtc);
    }

    [Fact]
    public void Exact_pending_reservation_is_affordable_and_entry_margin_is_consumed_across_candles()
    {
        var plan = Plan(entries:
        [
            Entry("first", share: "50", stops: [], targets: []),
            Entry("second", "200", "50", stops: [], targets: [])
        ]);
        var result = Evaluate([Activation(plan)],
            [Candle(0, 100, 101, 99, 100), Candle(1, 200, 201, 199, 200)], entryMarginBudget: 300);

        Assert.Null(result.Issue);
        Assert.Equal(new[] { 100m, 200m }, result.Executions.Select(e => e.MarginUsd));
        Assert.Equal(At.AddMinutes(2), result.CheckpointUtc);
    }

    [Fact]
    public void An_exit_can_restore_even_negative_available_budget_before_independent_entry_admission()
    {
        var plan = Plan(entries:
        [
            Entry("first", share: "50"),
            Entry("second", "120", "50", stops: [], targets: [])
        ]);
        var activations = new[] { Activation(plan) };
        var entered = Evaluate(activations, [Candle(0, 100, 101, 99, 100)]);
        var result = Evaluate(activations, [Candle(1, 115, 121, 109, 120)],
            entered.Executions, entered.CheckpointUtc, entered.PreviousClose, entryMarginBudget: -20);

        // Exit realizes 50 and releases 100: -20 + 50 + 100 = 130, enough for the second entry's 120.
        Assert.Null(result.Issue);
        Assert.Equal(new[] { "target", "entry" }, result.Executions.Select(e => e.Role));
        Assert.Equal(new[] { -100m, 120m }, result.Executions.Select(e => e.MarginUsd));
        Assert.Equal(50, result.Executions[0].PnlUsd);
        Assert.Equal(At.AddMinutes(2), result.CheckpointUtc);
    }

    [Fact]
    public void Losses_change_the_budget_before_later_candles_and_preserve_earlier_facts_when_an_entry_is_blocked()
    {
        var plan = Plan(entries:
        [
            Entry("first", share: "50", stops: [Exit("loss", "50", "50")], targets: []),
            Entry("second", "200", "50", stops: [], targets: [])
        ]);
        var result = Evaluate([Activation(plan)],
        [
            Candle(0, 100, 101, 99, 100), Candle(1, 50, 51, 49, 50),
            Candle(2, 200, 201, 199, 200)
        ], entryMarginBudget: 300);

        Assert.Equal(new[] { "entry", "stop" }, result.Executions.Select(e => e.Role));
        Assert.Equal(-125, result.Executions[1].PnlUsd);
        Assert.Equal(-50, result.Executions[1].MarginUsd);
        Assert.Contains("Insufficient paper margin", result.Issue);
        Assert.False(result.Ambiguous);
        Assert.Equal(At.AddMinutes(2), result.CheckpointUtc);
        Assert.Equal(50, result.PreviousClose);
    }

    [Fact]
    public void Blocked_admission_keeps_protective_exits_but_holds_the_frontier_and_replays_without_double_exit()
    {
        var plan = Plan(entries:
        [
            Entry("first", share: "50", stops: [Exit("partial", "90", "50")], targets: []),
            Entry("second", "80", "50", stops: [], targets: [])
        ]);
        var activations = new[] { Activation(plan) };
        var entered = Evaluate(activations, [Candle(0, 100, 101, 99, 100)]);
        var candle = Candle(1, 85, 91, 79, 85);
        var blocked = Evaluate(activations, [candle], entered.Executions, entered.CheckpointUtc,
            entered.PreviousClose, entryMarginBudget: 50);
        var replay = Evaluate(activations, [candle], entered.Executions.Concat(blocked.Executions).ToArray(),
            blocked.CheckpointUtc, blocked.PreviousClose, entryMarginBudget: 75);

        var exit = Assert.Single(blocked.Executions);
        Assert.Equal("stop", exit.Role);
        Assert.Equal(2.5m, exit.Quantity);
        Assert.Equal(-25, exit.PnlUsd);
        Assert.Equal(-50, exit.MarginUsd);
        Assert.Contains("Insufficient paper margin", blocked.Issue);
        Assert.False(blocked.Ambiguous);
        Assert.Equal(entered.CheckpointUtc, blocked.CheckpointUtc);
        Assert.Equal(entered.PreviousClose, blocked.PreviousClose);
        Assert.Empty(replay.Executions);
        Assert.Contains("Insufficient paper margin", replay.Issue);
        Assert.Equal(blocked.CheckpointUtc, replay.CheckpointUtc);

        var resolved = Evaluate(activations, [candle], entered.Executions.Concat(blocked.Executions).ToArray(),
            blocked.CheckpointUtc, blocked.PreviousClose, entryMarginBudget: 80);
        Assert.Equal("entry", Assert.Single(resolved.Executions).Role);
        Assert.Equal("second", resolved.Executions[0].EntryId);
        Assert.Null(resolved.Issue);
        Assert.Equal(At.AddMinutes(2), resolved.CheckpointUtc);
    }

    [Fact]
    public void An_explicit_unresolved_window_blocks_new_entries_but_not_existing_position_exits()
    {
        var plan = Plan(entries:
        [
            Entry("first", share: "50", targets: [Exit("partial", "110", "50")]),
            Entry("second", "120", "50", stops: [], targets: [Exit("would-be-ambiguous", "125")])
        ]);
        var activations = new[] { Activation(plan) };
        var entered = Evaluate(activations, [Candle(0, 100, 101, 99, 100)]);
        const string reason = "Another paper Play has an unresolved candle window.";
        var result = Evaluate(activations, [Candle(1, 115, 126, 109, 120)],
            entered.Executions, entered.CheckpointUtc, entered.PreviousClose,
            entryMarginBudget: 10000, entryBlockReason: reason);

        Assert.Equal("target", Assert.Single(result.Executions).Role);
        Assert.Equal(reason, result.Issue);
        Assert.False(result.Ambiguous);
        Assert.Equal(entered.CheckpointUtc, result.CheckpointUtc);
        Assert.Equal(entered.PreviousClose, result.PreviousClose);
    }

    [Fact]
    public void Insufficient_capital_does_not_invent_an_entry_and_then_flag_its_unexecuted_exit()
    {
        var result = Evaluate([Activation(Plan())], [Candle(0, 100, 111, 99, 100)], entryMarginBudget: 0);

        Assert.Empty(result.Executions);
        Assert.Contains("Insufficient paper margin", result.Issue);
        Assert.False(result.Ambiguous);
        Assert.Null(result.CheckpointUtc);
    }

    [Fact]
    public void An_entry_block_does_not_resolve_opposing_exits_of_an_already_open_position()
    {
        var activations = new[] { Activation(Plan()) };
        var entered = Evaluate(activations, [Candle(0, 100, 101, 99, 100)]);
        var result = Evaluate(activations, [Candle(1, 100, 111, 89, 100)],
            entered.Executions, entered.CheckpointUtc, entered.PreviousClose, entryMarginBudget: 0,
            entryBlockReason: "Another unresolved paper window.");

        Assert.Empty(result.Executions);
        Assert.True(result.Ambiguous);
        Assert.Equal(entered.CheckpointUtc, result.CheckpointUtc);
    }

    [Fact]
    public void An_entry_block_does_not_prevent_non_entry_minutes_from_advancing()
    {
        var result = Evaluate([Activation(Plan(entries: [Entry(stops: [], targets: [])]))],
            [Candle(0, 120, 121, 119, 120)], entryMarginBudget: -100,
            entryBlockReason: "Another unresolved paper window.");

        Assert.Empty(result.Executions);
        Assert.Null(result.Issue);
        Assert.Equal(At.AddMinutes(1), result.CheckpointUtc);
    }

    [Fact]
    public void Future_revision_reserve_prevents_a_cheap_entry_from_spending_untouched_capital()
    {
        var plan = Plan(size: "2", leverage: "10", entries:
        [
            Entry("cheap", share: "50", stops: [], targets: []),
            Entry("other", "1000", "50", stops: [], targets: [])
        ]);
        var activations = new[] { Activation(plan) };
        var candles = new[] { Candle(0, 100, 101, 99, 100) };
        var currentOnly = Evaluate(activations, candles, entryMarginBudget: 150);
        var futureReserved = Evaluate(activations, candles, entryMarginBudget: 150,
            reservedEntryMargins: new Dictionary<string, decimal> { ["cheap"] = 100, ["other"] = 100 });

        Assert.Equal(10, Assert.Single(currentOnly.Executions).MarginUsd);
        Assert.Empty(futureReserved.Executions);
        Assert.Contains("Insufficient paper margin", futureReserved.Issue);
        Assert.False(futureReserved.Ambiguous);
        Assert.Null(futureReserved.CheckpointUtc);
    }

    [Fact]
    public void Reserve_union_includes_future_only_entry_ids_not_in_the_current_plan()
    {
        var plan = Plan(size: "1", leverage: "10", entries: [Entry(stops: [], targets: [])]);
        var result = Evaluate([Activation(plan)], [Candle(0, 100, 101, 99, 100)], entryMarginBudget: 100,
            reservedEntryMargins: new Dictionary<string, decimal> { ["entry"] = 10, ["future-only"] = 200 });

        Assert.Empty(result.Executions);
        Assert.Contains("Insufficient paper margin", result.Issue);
    }

    [Fact]
    public void Entered_key_leaves_future_reserve_and_only_actual_committed_margin_is_spent()
    {
        var plan = Plan(size: "2", leverage: "10", entries:
        [
            Entry("cheap", share: "50", stops: [], targets: []),
            Entry("other", "1000", "50", stops: [], targets: [])
        ]);
        var activations = new[] { Activation(plan) };
        var reservations = new Dictionary<string, decimal> { ["cheap"] = 100, ["other"] = 100 };
        var result = Evaluate(activations,
            [Candle(0, 100, 101, 99, 100), Candle(1, 1000, 1001, 999, 1000)],
            entryMarginBudget: 200, reservedEntryMargins: reservations);
        var first = Evaluate(activations, [Candle(0, 100, 101, 99, 100)],
            entryMarginBudget: 200, reservedEntryMargins: reservations);
        var resumed = Evaluate(activations, [Candle(1, 1000, 1001, 999, 1000)],
            first.Executions, first.CheckpointUtc, first.PreviousClose,
            entryMarginBudget: 150, reservedEntryMargins: reservations);

        Assert.Null(result.Issue);
        Assert.Equal(new[] { 10m, 100m }, result.Executions.Select(e => e.MarginUsd));
        Assert.Null(resumed.Issue);
        Assert.Equal(100, Assert.Single(resumed.Executions).MarginUsd);
        Assert.Equal(At.AddMinutes(2), result.CheckpointUtc);
    }

    [Fact]
    public void Negative_reservation_cannot_reduce_the_required_pending_margin()
    {
        var result = Evaluate([Activation(Plan())], [Candle(0, 100, 101, 99, 100)], entryMarginBudget: 0,
            reservedEntryMargins: new Dictionary<string, decimal> { ["entry"] = -200 });

        Assert.Empty(result.Executions);
        Assert.Contains("negative", result.Issue);
        Assert.Null(result.CheckpointUtc);
    }

    [Fact]
    public void A_missing_reservation_key_cannot_admit_more_actual_entry_margin_than_the_budget()
    {
        var result = Evaluate([Activation(Plan())], [Candle(0, 100, 101, 99, 100)], entryMarginBudget: 100,
            reservedEntryMargins: new Dictionary<string, decimal>());

        Assert.Empty(result.Executions);
        Assert.Contains("Insufficient paper margin", result.Issue);
        Assert.False(result.Ambiguous);
    }

    private static PaperEvaluation Evaluate(IReadOnlyList<PaperPlanActivation> activations,
        IReadOnlyList<PaperCandle> candles, IReadOnlyList<PaperExecution>? retained = null,
        DateTimeOffset? checkpoint = null, decimal? previousClose = null, DateTimeOffset? through = null,
        decimal? entryMarginBudget = null, string? entryBlockReason = null,
        IReadOnlyDictionary<string, decimal>? reservedEntryMargins = null) =>
        PaperEvaluator.Evaluate(Owner, Account, Play, activations, retained ?? [], checkpoint, previousClose, candles,
            through ?? (candles.Count == 0 ? At : candles.Max(c => c.OpenAtUtc.AddMinutes(1))),
            entryMarginBudget, entryBlockReason, reservedEntryMargins);

    private static PaperPlanActivation Activation(PlayPlanDocument plan, int minute = 0, int version = 1) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = Account, PlayId = Play,
        PlanVersion = version, EffectiveAtUtc = At.AddMinutes(minute), PlanJson = PlayDocuments.Serialize(plan)
    };

    private static PlayPlanDocument Plan(string direction = "long", string size = "10", string mode = "quantity",
        string leverage = "5", IReadOnlyList<PlanEntry>? entries = null) =>
        new(direction, mode, size, leverage, null,
            entries ?? [direction == "long" ? Entry() : Entry(stops: [Exit("stop", "110")], targets: [Exit("target", "90")])],
            new("", "", "", ""));

    private static PlanEntry Entry(string id = "entry", string price = "100", string share = "100",
        IReadOnlyList<PlanExit>? stops = null, IReadOnlyList<PlanExit>? targets = null) =>
        new(id, id, "#ffffff", share, price, stops ?? [Exit("stop", "90")], targets ?? [Exit("target", "110")]);

    private static PlanExit Exit(string id, string price, string share = "100", string unit = "price") => new(id, unit, price, share);
    private static PaperCandle Candle(int minute, decimal open, decimal high, decimal low, decimal close) =>
        new(At.AddMinutes(minute), At.AddMinutes(minute + 1), open, high, low, close);
}
