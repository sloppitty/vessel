using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class IfThenRuleRulesTests
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
        // A rule the owner was not shown (a failed read, an older client) is never attributed to the Play (Sol round 1).
        Assert.Null(PlayRules.Seen(rule, null));
        var silent = PlayRules.Seen(rule, new(3))!;
        Assert.Equal((3, "price tags the stop", "I wait for the close"), (silent.Version, silent.If, silent.Then));
        Assert.Null(PlayRules.Read(silent.Snapshot)!.Answer);
        // Shown but unanswered, and changed since: asked again, like an answer.
        Assert.Equal(409, Assert.Throws<ProblemException>(() => PlayRules.Seen(rule, new(2))).StatusCode);
        Assert.Equal("acknowledged", PlayRules.Read(PlayRules.Seen(rule, new(3, "acknowledged"))!.Snapshot)!.Answer);
        var exception = PlayRules.Read(PlayRules.Seen(rule, new(3, "exception", "  The news moved it.  "))!.Snapshot)!;
        Assert.Equal(("exception", "The news moved it."), (exception.Answer, exception.Reason));
        // The rule changed or was archived since it was shown: plan it again.
        Assert.Equal(409, Assert.Throws<ProblemException>(() => PlayRules.Seen(rule, new(2, "acknowledged"))).StatusCode);
        Assert.Equal(409, Assert.Throws<ProblemException>(() => PlayRules.Seen(null, new(3, "acknowledged"))).StatusCode);
        foreach (var invalid in new PlanRuleAnswerDto[] { new(3, "exception"), new(3, "acknowledged", "why"), new(3, null, "why"), new(3, "ignored"),
            new(3, "exception", new string('x', 501)) })
            Assert.Equal(400, Assert.Throws<ProblemException>(() => PlayRules.Seen(rule, invalid)).StatusCode);
    }

}
