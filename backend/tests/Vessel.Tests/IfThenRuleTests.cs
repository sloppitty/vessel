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
    public void A_rule_is_two_trimmed_plain_text_parts_of_at_most_500_characters()
    {
        var rule = new PlayRule(Guid.NewGuid(), "real", 1, "  the setup fails  ", " I step aside\nfor a day ", null, At);
        Assert.Equal(("the setup fails", "I step aside\nfor a day"), (rule.If, rule.Then));
        foreach (var (@if, then) in new[] { ("", "x"), ("x", " "), (new string('x', 501), "x"), ("x", "bell\a") })
            Assert.Throws<DomainValidationException>(() => new PlayRule(Guid.NewGuid(), "real", 1, @if, then, null, At));
        rule.Archive(At.AddHours(1));
        rule.Archive(At.AddHours(2));
        Assert.Equal(At.AddHours(1), rule.ArchivedAtUtc);
    }

    [Fact]
    public void Plan_it_keeps_the_active_rule_with_an_acknowledgement_an_exception_or_no_answer()
    {
        var rule = Rule(3);
        Assert.Null(PlayRules.Seen(null, null));
        var silent = PlayRules.Seen(rule, null)!;
        Assert.Equal((3, "price tags the stop", "I wait for the close"), (silent.Version, silent.If, silent.Then));
        Assert.Null(PlayRules.Read(silent.Snapshot)!.Answer);
        Assert.Equal("acknowledged", PlayRules.Read(PlayRules.Seen(rule, new(3, "acknowledged"))!.Snapshot)!.Answer);
        var exception = PlayRules.Read(PlayRules.Seen(rule, new(3, "exception", "  The news moved it.  "))!.Snapshot)!;
        Assert.Equal(("exception", "The news moved it."), (exception.Answer, exception.Reason));
        // The rule changed or was archived since it was shown: plan it again.
        Assert.Equal(409, Assert.Throws<ProblemException>(() => PlayRules.Seen(rule, new(2, "acknowledged"))).StatusCode);
        Assert.Equal(409, Assert.Throws<ProblemException>(() => PlayRules.Seen(null, new(3, "acknowledged"))).StatusCode);
        foreach (var invalid in new PlanRuleAnswerDto[] { new(3, "exception"), new(3, "acknowledged", "why"), new(3, "ignored"), new(3, "exception", new string('x', 501)) })
            Assert.Equal(400, Assert.Throws<ProblemException>(() => PlayRules.Seen(rule, invalid)).StatusCode);
    }

    [Fact]
    public async Task Review_counts_the_rules_answers_by_outcome_and_drills_into_each_and_a_winning_deviation_stays_one()
    {
        var f = new ReviewFixture();
        var seen = PlayRules.Seen(Rule(), new(1, "acknowledged"));
        var won = f.Add(10, rule: seen);
        won.Play.Annotate(won.Play.Title, won.Play.Drawings, "", ReviewClock.Now, new(Rule: "deviated"));
        var lost = f.Add(-5, rule: PlayRules.Seen(Rule(), new(1, "exception", "Earnings week.")));
        lost.Play.Annotate(lost.Play.Title, lost.Play.Drawings, "", ReviewClock.Now, new(Rule: "followed"));
        f.Add(3, rule: PlayRules.Seen(Rule(), null)); // Unanswered at Plan it and in the review.
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
