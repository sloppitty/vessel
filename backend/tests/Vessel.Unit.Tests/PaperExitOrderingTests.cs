using Vessel.Application.Paper;
using Vessel.Application.Plays;

namespace Vessel.Tests;

[Collection("Paper corrective PostgreSQL")]
public sealed class PaperExitOrderingTests : PaperExitOrderingTestsValues
{

    [Theory]
    [InlineData("stop", false, false)] [InlineData("stop", false, true)]
    [InlineData("stop", true, false)] [InlineData("stop", true, true)]
    [InlineData("target", false, false)] [InlineData("target", false, true)]
    [InlineData("target", true, false)] [InlineData("target", true, true)]
    public void Partial_exit_remainder_follows_proven_crossing_not_saved_array_order(string role, bool shortSide, bool farFirst)
    {
        var (plan, candles, pnl) = Scenario(role, shortSide, farFirst);
        var result = Evaluate(plan, candles);
        Assert.Null(result.Issue);
        Assert.Equal(pnl, result.Executions.Sum(e => e.PnlUsd));
        Assert.Equal("near", Assert.Single(result.Executions, e => e.Role == role).LevelId);
        Assert.Equal(0m, result.Executions.Sum(e => e.Role == "entry" ? e.Quantity : -e.Quantity));
        Assert.Equal(0m, result.Executions.Sum(e => e.MarginUsd));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void An_unchanged_level_crossed_before_the_open_precedes_intraminute_touches(bool farFirst)
    {
        var (plan, candles, _) = Scenario("target", false, farFirst);
        candles[2] = Candle(2, 125, 109, 126, 110);
        var result = Evaluate(plan, candles);
        Assert.Null(result.Issue);
        Assert.Equal("near", Assert.Single(result.Executions, e => e.Role == "target").LevelId);
        Assert.Equal(0m, result.Executions.Sum(e => e.PnlUsd));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void A_revised_level_does_not_gain_opening_jump_priority_over_an_unchanged_exit(bool shortSide, bool farFirst)
    {
        var (current, candles, _) = Scenario("target", shortSide, farFirst);
        var original = current with { Entries = [current.Entries[0] with
        {
            Targets = current.Entries[0].Targets.Select(t => t.Id == "near"
                ? t with { Value = shortSide ? "70" : "130" } : t).ToArray()
        }] };
        var prior = Evaluate(original, candles[..2]);
        var entry = prior.Executions[0];
        var result = PaperEvaluator.Evaluate(entry.OwnerId, entry.AccountId, entry.PlayId,
            [new() { OwnerId = entry.OwnerId, AccountId = entry.AccountId, PlayId = entry.PlayId, PlanVersion = 1,
                PlanJson = PlayDocuments.Serialize(original), EffectiveAtUtc = First },
             new() { OwnerId = entry.OwnerId, AccountId = entry.AccountId, PlayId = entry.PlayId, PlanVersion = 2,
                PlanJson = PlayDocuments.Serialize(current), EffectiveAtUtc = First.AddMinutes(2) }],
            prior.Executions, First.AddMinutes(2), shortSide ? 110 : 90,
            [shortSide ? Candle(2, 75, 74, 91, 90) : Candle(2, 125, 109, 126, 110)], First.AddMinutes(3),
            previousCloseAtUtc: First.AddMinutes(2));
        Assert.Null(result.Issue);
        Assert.Equal("far", Assert.Single(result.Executions).LevelId);
        Assert.Equal(100m, Assert.Single(result.Executions).PnlUsd);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Competing_exit_touches_on_opposite_sides_of_the_open_are_ambiguous(bool farFirst)
    {
        var (plan, candles, _) = Scenario("target", false, farFirst);
        // Without a retained source close, the opening jump cannot establish which level was touched first.
        var prior = Evaluate(plan, candles[..2]);
        var changed = plan with { Entries = [plan.Entries[0] with { Targets = farFirst
            ? [new("far", "price", "120", "50"), new("near", "price", "110", "50")]
            : [new("near", "price", "110", "50"), new("far", "price", "120", "50")] }] };
        var owner = prior.Executions[0].OwnerId;
        var account = prior.Executions[0].AccountId;
        var play = prior.Executions[0].PlayId;
        var result = PaperEvaluator.Evaluate(owner, account, play,
            [new() { OwnerId = owner, AccountId = account, PlayId = play, PlanVersion = 1,
                PlanJson = PlayDocuments.Serialize(changed), EffectiveAtUtc = First }],
            prior.Executions, First.AddMinutes(2), null, [Candle(2, 115, 109, 121)], First.AddMinutes(3));
        Assert.True(result.Ambiguous);
        Assert.NotNull(result.Issue);
        Assert.Empty(result.Executions);
        Assert.Equal(First.AddMinutes(2), result.CheckpointUtc);
    }

    private static PaperEvaluation Evaluate(PlayPlanDocument plan, PaperCandle[] candles)
    {
        var owner = Guid.NewGuid(); var account = Guid.NewGuid(); var play = Guid.NewGuid();
        return PaperEvaluator.Evaluate(owner, account, play,
            [new() { OwnerId = owner, AccountId = account, PlayId = play, PlanVersion = 1,
                PlanJson = PlayDocuments.Serialize(plan), EffectiveAtUtc = First }],
            [], First, null, candles, First.AddMinutes(candles.Length));
    }
}
