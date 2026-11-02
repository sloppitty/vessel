using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 3: one active if-then rule per trading mode, acknowledged or excepted at Plan it, answered in the review, and
/// counted in Review with drill-down only.
/// </summary>
public sealed class IfThenRuleTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-22T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static PlayRule Rule(int version = 1) => new(Guid.NewGuid(), "real", version, "price tags the stop", "I wait for the close", null, At);

    [Fact]
    public async Task Review_counts_the_rules_answers_by_outcome_and_drills_into_each_and_a_winning_deviation_stays_one()
    {
        var f = new ReviewFixture();
        var seen = PlayRules.Seen(Rule(), new(1, "acknowledged"));
        var won = f.Add(10, rule: seen);
        won.Play.Annotate(won.Play.Title, won.Play.Drawings, "", ReviewClock.Now, new(Rule: "deviated"));
        var lost = f.Add(-5, rule: PlayRules.Seen(Rule(), new(1, "exception", "Earnings week.")));
        lost.Play.Annotate(lost.Play.Title, lost.Play.Drawings, "", ReviewClock.Now, new(Rule: "followed"));
        f.Add(3, rule: PlayRules.Seen(Rule(), new(1))); // Shown, unanswered at Plan it and in the review.
        var without = f.Add(7); // Planned before any rule: not carried, and nothing to answer.
        Assert.Throws<DomainRuleException>(() => without.Play.Annotate(without.Play.Title, without.Play.Drawings, "", ReviewClock.Now, new(Rule: "followed")));
        var rule = (await f.Service.GetAsync(new(Period: "all"), default)).Rule!;
        Assert.Equal((3, 1, 1, 1), (rule.Carried, rule.Acknowledged, rule.Exceptions, rule.NotAcknowledged));
        Assert.Equal(new ReviewRuleRowDto(1, 0, 0, 0), rule.Deviated);
        Assert.Equal(new ReviewRuleRowDto(0, 1, 0, 0), rule.Followed);
        Assert.Equal(new ReviewRuleRowDto(1, 0, 0, 0), rule.Unanswered);
        Assert.Equal(new ReviewRuleRowDto(0, 0, 0, 0), rule.NotApplicable);
        var deviated = await f.Service.ProcessAsync(new(new(Period: "all"), Rule: "deviated"), default);
        Assert.Equal([won.Play.Id], deviated.Items.Select(i => i.Id));
        Assert.Equal(1, (await f.Service.ProcessAsync(new(new(Period: "all"), Rule: "unanswered"), default)).Total);
        Assert.Equal([lost.Play.Id], (await f.Service.ProcessAsync(new(new(Period: "all"), Rule: "exception"), default)).Items.Select(i => i.Id));
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => f.Service.ProcessAsync(new(new(Period: "all"), Rule: "broken"), default))).StatusCode);
    }
}
