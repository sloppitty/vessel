using Vessel.Application.Paper;
using Vessel.Application.Plays;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class PaperCorrectiveEvaluatorTests
{
    private static readonly DateTimeOffset Open = new(2026, 10, 20, 12, 1, 0, TimeSpan.Zero);
    private static PaperCandle Candle(int minute, decimal price) =>
        new(Open.AddMinutes(minute), Open.AddMinutes(minute + 1), price, price, price, price);

    private static PaperEvaluation Run(PlayPlanDocument plan, params PaperCandle[] candles)
    {
        var owner = Guid.NewGuid(); var account = Guid.NewGuid(); var play = Guid.NewGuid();
        return PaperEvaluator.Evaluate(owner, account, play,
            [new PaperPlanActivation { Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, PlayId = play,
                PlanVersion = 1, PlanJson = PlayDocuments.Serialize(plan), EffectiveAtUtc = Open }],
            [], null, null, candles, Open.AddMinutes(candles.Length));
    }

    [Fact]
    public void Partial_stop_and_multiple_targets_consume_the_exact_fully_allocated_remainder()
    {
        var plan = new PlayPlanDocument("long", "margin", "1000", "10", null,
            [new("e", "Entry", "#ffffff", "100", "97.3", [new("s", "price", "96", "25")],
                [new("t1", "price", "98", "25"), new("t2", "price", "99", "50")])], new("", "", "", ""));
        var result = Run(plan, Candle(0, 97.3m), Candle(1, 96), Candle(2, 98), Candle(3, 99));
        Assert.Null(result.Issue);
        Assert.Equal(4, result.Executions.Count);
        Assert.Equal(25.693730729701m, result.Executions.Single(e => e.LevelId == "s").Quantity);
        Assert.Equal(25.693730729701m, result.Executions.Single(e => e.LevelId == "t1").Quantity);
        Assert.Equal(51.387461459405m, result.Executions.Single(e => e.LevelId == "t2").Quantity);
        Assert.Equal(0m, result.Executions.Sum(e => e.Role == "entry" ? e.Quantity : -e.Quantity));
        Assert.Equal(0m, result.Executions.Sum(e => e.MarginUsd));
        Assert.Equal(71.942446043166m, result.Executions.Sum(e => e.PnlUsd));
    }

    [Fact]
    public void An_intentionally_unallocated_quantum_is_not_rounding_dust_even_at_a_trillion_dollar_price()
    {
        var plan = new PlayPlanDocument("short", "quantity", "1", "1", null,
            [new("e", "Entry", "#ffffff", "100", "1000000000000", [],
                [new("t", "price", "999999999999", "99.9999999999")])], new("", "", "", ""));
        var result = Run(plan, Candle(0, 1_000_000_000_000m), Candle(1, 999_999_999_999m));
        Assert.Null(result.Issue);
        Assert.Equal(0.999999999999m, result.Executions.Single(e => e.Role == "target").Quantity);
        Assert.Equal(0.000000000001m, result.Executions.Sum(e => e.Role == "entry" ? e.Quantity : -e.Quantity));
        Assert.Equal(1m, result.Executions.Sum(e => e.MarginUsd));
        Assert.Equal(0.999999999999m, result.Executions.Sum(e => e.PnlUsd));
    }
}
