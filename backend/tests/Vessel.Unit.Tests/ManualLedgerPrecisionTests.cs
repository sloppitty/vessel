using System.Globalization;
using Vessel.Application.Common;
using Vessel.Application.Plays;

namespace Vessel.Tests;

public sealed class ManualLedgerPrecisionTests : ManualLedgerPrecisionTestsValues
{

    [Theory]
    [InlineData("long", "0.9")]
    [InlineData("long", "0.1")]
    [InlineData("short", "0.9")]
    [InlineData("short", "0.1")]
    public void Final_exit_that_cannot_consume_cost_exactly_is_rejected(string direction, string finalPrice)
    {
        var play = CreatePlay(direction);
        var rows = new[]
        {
            Row(play, "entry", "1", Large, 0),
            Row(play, "entry", "2", LargePlusOne, 1),
            Row(play, "exit", "1", LargePlusOne, 2),
            Row(play, "exit", "1", TwiceLarge, 3),
            Row(play, "exit", "1", finalPrice, 4)
        };

        var partial = ManualLedger.Calculate(play, rows[..^1]);
        Assert.Equal(1, partial.Open);
        Assert.Null(partial.ClosedAt);
        var error = Assert.Throws<ProblemException>(() => ManualLedger.Calculate(play, rows));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("exact decimal precision", error.Message);
    }

    [Theory]
    [InlineData("long", "1.1", "0.1")]
    [InlineData("short", "1.1", "-0.1")]
    [InlineData("long", "1.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("short", "1.0000000000000000000000000001", "-0.0000000000000000000000000001")]
    public void Supported_large_cancellation_preserves_the_small_completed_result(
        string direction, string firstExitPrice, string expected)
    {
        var play = CreatePlay(direction);
        var revision = play.MarkPlanned(At.AddSeconds(-1)).Revision;
        var ledger = ManualLedger.Calculate(play,
        [
            Row(play, "entry", "2", "1", 0),
            Row(play, "exit", "1", firstExitPrice, 1),
            Row(play, "entry", "1", TwiceLarge, 2),
            Row(play, "exit", "1", TwiceLarge, 3),
            Row(play, "exit", "1", "1", 4)
        ]);
        play.RecordManualOpen(At);
        play.RecordManualClose(At.AddSeconds(4), ReviewClock.Now);

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), ledger.Gross);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), ledger.Net);
        var outcome = PlayOutcomeCalculator.Outcome(new(play, [revision], ledger.Projection, ManualSource: true));
        Assert.NotNull(outcome);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), outcome.NetResult);
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void Completed_gross_that_requires_rounding_is_rejected(string direction)
    {
        var play = CreatePlay(direction);
        var error = Assert.Throws<ProblemException>(() => ManualLedger.Calculate(play,
        [
            Row(play, "entry", "3", "1", 0),
            Row(play, "exit", "1", "1.1", 1),
            Row(play, "exit", "1", Large, 2),
            Row(play, "exit", "1", "1", 3)
        ]));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("exact decimal precision", error.Message);
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void Completed_net_that_requires_rounding_is_rejected(string direction)
    {
        var play = CreatePlay(direction);
        var exit = Row(play, "exit", "1", TwiceLarge, 1);
        exit.Fee = "0.1";
        var error = Assert.Throws<ProblemException>(() => ManualLedger.Calculate(play,
            [Row(play, "entry", "1", Large, 0), exit]));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("exact decimal precision", error.Message);
    }

    [Theory]
    [InlineData("long", "105", "10")]
    [InlineData("short", "105", "-10")]
    [InlineData("long", "150", "100")]
    [InlineData("short", "150", "-100")]
    public void Ordinary_repeating_partial_with_a_higher_final_price_closes_exactly(
        string direction, string finalPrice, string expected)
    {
        var play = CreatePlay(direction);
        var ledger = ManualLedger.Calculate(play,
        [
            Row(play, "entry", "1", "100", 0),
            Row(play, "entry", "2", "101", 1),
            Row(play, "exit", "1", "102", 2),
            Row(play, "exit", "2", finalPrice, 3)
        ]);

        Assert.Equal(At.AddSeconds(3), ledger.ClosedAt);
        Assert.Equal(expected, ManualLedger.Text(ledger.Gross));
        Assert.Equal(ledger.Gross, ledger.Net);
        Assert.Equal(ledger.Gross, ledger.Projection.Sum(fill => fill.Fill.ClosedPnlUsd));
    }

    [Theory]
    [InlineData("long", "987654224")]
    [InlineData("short", "-987654224")]
    public void Later_partial_exit_preserves_headroom_from_an_earlier_repeating_allocation(string direction, string expected)
    {
        var play = CreatePlay(direction);
        var ledger = ManualLedger.Calculate(play,
        [
            Row(play, "entry", "1", "100", 0),
            Row(play, "entry", "2", "101", 1),
            Row(play, "exit", "1", "102", 2),
            Row(play, "exit", "1", "103", 3),
            Row(play, "exit", "1", "987654321", 4)
        ]);
        Assert.Equal(expected, ManualLedger.Text(ledger.Gross));
        Assert.Equal(ledger.Gross, ledger.Net);
        Assert.Equal(ledger.Gross, ledger.Projection.Sum(fill => fill.Fill.ClosedPnlUsd));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void Outcome_excludes_a_completed_net_that_requires_rounding(string direction)
    {
        var play = CreatePlay(direction);
        var revision = play.MarkPlanned(At.AddSeconds(-1)).Revision;
        var ledger = ManualLedger.Calculate(play,
            [Row(play, "entry", "1", Large, 0), Row(play, "exit", "1", TwiceLarge, 1)]);
        play.RecordManualOpen(At);
        play.RecordManualClose(At.AddSeconds(1), ReviewClock.Now);
        ledger.Projection[^1].Fill.Fee = 0.1m;

        Assert.Null(PlayOutcomeCalculator.Outcome(new(play, [revision], ledger.Projection, ManualSource: true), out var problem));
        Assert.Contains("exact precision", problem);
    }
}
