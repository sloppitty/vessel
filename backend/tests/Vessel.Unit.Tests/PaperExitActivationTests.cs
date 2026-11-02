using System.Text.Json;
using Vessel.Application.Paper;
using Vessel.Application.Plays;
using Vessel.Domain.Paper;
using Xunit.Abstractions;

namespace Vessel.Tests;

[Collection("Paper corrective PostgreSQL")]
public sealed class PaperExitActivationTests(ITestOutputHelper output) : PaperExitActivationTestsValues
{
    private static readonly Guid Owner = Guid.NewGuid(), AccountId = Guid.NewGuid(), PlayId = Guid.NewGuid();
    private static PaperPlanActivation A(PlayPlanDocument plan, int version, int minute) => new()
    {
        OwnerId = Owner, AccountId = AccountId, PlayId = PlayId, PlanVersion = version,
        PlanJson = PlayDocuments.Serialize(plan), EffectiveAtUtc = First.AddMinutes(minute)
    };
    private static PaperEvaluation Eval(PlayPlanDocument plan, PaperCandle[] candles) =>
        PaperEvaluator.Evaluate(Owner, AccountId, PlayId, [A(plan, 1, 0)], [], First, null, candles, First.AddMinutes(candles.Length));

    [Theory]
    [InlineData("target", false, false)] [InlineData("target", false, true)]
    [InlineData("target", true, false)] [InlineData("target", true, true)]
    [InlineData("stop", false, false)] [InlineData("stop", false, true)]
    [InlineData("stop", true, false)] [InlineData("stop", true, true)]
    public void H01_Zero_to_positive_exit_share_is_new_activation_not_proof_of_a_prior_gap_touch(string role, bool shortSide, bool farFirst)
    {
        var (old, current, prefix, last, expected) = Dormant(role, shortSide, farFirst);
        var prior = Eval(old, prefix);
        Assert.Null(prior.Issue); Assert.Equal(2, prior.Executions.Count);
        var result = PaperEvaluator.Evaluate(Owner, AccountId, PlayId, [A(old, 1, 0), A(current, 2, 2)],
            prior.Executions, First.AddMinutes(2), prior.PreviousClose, [last], First.AddMinutes(3), previousCloseAtUtc: prior.PreviousCloseAtUtc);
        output.WriteLine(JsonSerializer.Serialize(new { role, shortSide, farFirst, result }));
        Assert.Null(result.Issue);
        // Existing far exit is active during the opening jump; newly allocated near exit is not.
        var execution = Assert.Single(result.Executions);
        Assert.Equal("far", execution.LevelId);
        Assert.Equal(expected, prior.Executions.Concat(result.Executions).Sum(x => x.PnlUsd));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void H03_Newly_allocated_exit_outside_current_range_cannot_use_a_pre_activation_crossing(bool shortSide)
    {
        var near = X("near", shortSide ? 90 : 110);
        var old = P(shortSide, [E("e", "100", [X("prior", shortSide ? 110 : 90)], [near with { Share = "0" }])]);
        var current = old with { Entries = [old.Entries[0] with { Targets = [near] }] };
        var origin = shortSide ? 110 : 90;
        var prefix = Eval(old, [C(0, 100, 99, 101), C(1, origin, origin - 1, origin + 1)]);
        Assert.Null(prefix.Issue); Assert.Equal(2, prefix.Executions.Count);
        var end = shortSide ? 85 : 115;
        var result = PaperEvaluator.Evaluate(Owner, AccountId, PlayId, [A(old, 1, 0), A(current, 2, 2)], prefix.Executions,
            First.AddMinutes(2), origin, [C(2, end, end - 1, end + 1)], First.AddMinutes(3), previousCloseAtUtc: First.AddMinutes(2));
        output.WriteLine(JsonSerializer.Serialize(result));
        Assert.Null(result.Issue);
        Assert.Empty(result.Executions);
        Assert.Equal(First.AddMinutes(3), result.CheckpointUtc);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void H04_Equal_price_competing_exits_hold_the_candle_without_losing_prior_facts(bool farFirst)
    {
        var a = X("a", 110); var b = X("b", 110);
        var plan = P(false, [E("e", "100", [X("prior", 90)], farFirst ? [b, a] : [a, b])]);
        var result = Eval(plan, [C(0, 100, 99, 101), C(1, 90, 89, 91), C(2, 100, 99, 111)]);
        Assert.True(result.Ambiguous); Assert.NotNull(result.Issue);
        Assert.Equal(2, result.Executions.Count); Assert.Equal(-50, result.Executions.Sum(x => x.PnlUsd));
        Assert.Equal(First.AddMinutes(2), result.CheckpointUtc);
        Assert.All(result.Executions, x => Assert.True(x.CandleOpenAtUtc < First.AddMinutes(2)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void H05_Straddling_revision_rolls_back_all_entries_exits_and_declines_from_only_current_candle(bool ambiguousEntryFirst)
    {
        var a = E("a", "50", targets: [X("a-target", 130, "100")]);
        var b = E("b", "50", targets: [X("near", 150), X("far", 160)]);
        var old = P(false, ambiguousEntryFirst ? [b, a] : [a, b]);
        var revised = old with { Entries = old.Entries.Select(e => e.Id == "b" ? e with { Targets = [X("near", 110), X("far", 120)] } : e).ToArray() };
        var prior = Eval(old, [C(0, 100, 99, 101)]);
        Assert.Null(prior.Issue); Assert.Equal(2, prior.Executions.Count);
        var result = PaperEvaluator.Evaluate(Owner, AccountId, PlayId, [A(old, 1, 0), A(revised, 2, 1)], prior.Executions,
            First.AddMinutes(1), 100, [C(1, 115, 109, 131)], First.AddMinutes(2), previousCloseAtUtc: First.AddMinutes(1));
        Assert.True(result.Ambiguous); Assert.NotNull(result.Issue);
        Assert.Empty(result.Executions); Assert.Empty(result.DeclinedEntries ?? []);
        Assert.Equal(First.AddMinutes(1), result.CheckpointUtc); Assert.Equal(100, result.PreviousClose);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void H06_Explicit_zero_trade_minute_preserves_last_trade_source_for_first_crossing_order(bool farFirst)
    {
        var (old, _, prefix, _, _) = Dormant("target", false, farFirst);
        var plan = old with { Entries = [old.Entries[0] with { Targets = old.Entries[0].Targets.Select(x => x with { Share = "50" }).ToArray() }] };
        var result = Eval(plan, [prefix[0], prefix[1], C(2, 90, 90, 90, trades: 0), C(3, 125, 109, 126, 110)]);
        Assert.Null(result.Issue); Assert.False(result.Ambiguous);
        Assert.Equal("near", Assert.Single(result.Executions, x => x.Role == "target").LevelId);
        Assert.Equal(0, result.Executions.Sum(x => x.PnlUsd));
        Assert.DoesNotContain(result.Executions, x => x.CandleOpenAtUtc == First.AddMinutes(2));
    }

    [Theory]
    [InlineData("target", false, false)] [InlineData("target", false, true)]
    [InlineData("target", true, false)] [InlineData("target", true, true)]
    [InlineData("stop", false, false)] [InlineData("stop", false, true)]
    [InlineData("stop", true, false)] [InlineData("stop", true, true)]
    public void H08_Absent_level_control_has_no_gap_priority_and_old_active_level_wins(string role, bool shortSide, bool farFirst)
    {
        var (old, current, prefix, last, expected) = Dormant(role, shortSide, farFirst);
        old = old with { Entries = [old.Entries[0] with
        {
            Stops = old.Entries[0].Stops.Where(x => x.Id != "near").ToArray(),
            Targets = old.Entries[0].Targets.Where(x => x.Id != "near").ToArray()
        }] };
        var prior = Eval(old, prefix); Assert.Null(prior.Issue); Assert.Equal(2, prior.Executions.Count);
        var result = PaperEvaluator.Evaluate(Owner, AccountId, PlayId, [A(old, 1, 0), A(current, 2, 2)], prior.Executions,
            First.AddMinutes(2), prior.PreviousClose, [last], First.AddMinutes(3), previousCloseAtUtc: prior.PreviousCloseAtUtc);
        Assert.Null(result.Issue); Assert.Equal("far", Assert.Single(result.Executions).LevelId);
        Assert.Equal(expected, prior.Executions.Concat(result.Executions).Sum(x => x.PnlUsd));
    }

    [Theory]
    [InlineData("target", false, false)] [InlineData("target", false, true)]
    [InlineData("target", true, false)] [InlineData("target", true, true)]
    [InlineData("stop", false, false)] [InlineData("stop", false, true)]
    [InlineData("stop", true, false)] [InlineData("stop", true, true)]
    public void H10_Zero_share_intermediate_revision_breaks_continuous_gap_activation_across_native_empty_coverage(string role, bool shortSide, bool farFirst)
    {
        var (dormant, current, prefix, last, expected) = Dormant(role, shortSide, farFirst);
        var prior = Eval(current, prefix); Assert.Null(prior.Issue); Assert.Equal(2, prior.Executions.Count);
        var source = prior.PreviousClose!.Value;
        var movedLast = last with { OpenAtUtc = First.AddMinutes(3), CloseAtUtc = First.AddMinutes(4) };
        var result = PaperEvaluator.Evaluate(Owner, AccountId, PlayId,
            [A(current, 1, 0), A(dormant, 2, 2), A(current, 3, 3)], prior.Executions, First.AddMinutes(2), source,
            [C(2, source, source, source, trades: 0), movedLast], First.AddMinutes(4), previousCloseAtUtc: prior.PreviousCloseAtUtc);
        output.WriteLine(JsonSerializer.Serialize(new { role, shortSide, farFirst, result }));
        Assert.Null(result.Issue); Assert.Equal("far", Assert.Single(result.Executions).LevelId);
        Assert.Equal(expected, prior.Executions.Concat(result.Executions).Sum(x => x.PnlUsd));
    }
}
