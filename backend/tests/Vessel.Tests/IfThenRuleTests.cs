using Microsoft.EntityFrameworkCore;
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

    private sealed class PauseActiveRead : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public readonly TaskCompletionSource Selected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData, System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM play_rules", StringComparison.Ordinal) && command.CommandText.Contains("IS NULL", StringComparison.Ordinal))
            {
                Selected.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    /// <summary>
    /// Sol round 1 on item 3: an archive and a replacement that both read version 1 as active cannot both succeed. The one
    /// that writes second finds version 1 already archived and gets a 409, in either order.
    /// </summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overlapping_archive_and_replacement_of_one_version_let_only_the_first_succeed(bool staleIsArchive)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var first = database.Context(owner);
        var work = TestUnitOfWork.For(first);
        var service = new PlayService(new Vessel.Persistence.PlayStore(first, work), null!, TimeProvider.System, work);
        await service.SetRuleAsync("real", new("the stop is hit", "I wait a day"), default);
        var pause = new PauseActiveRead();
        await using var second = new Vessel.Persistence.VesselDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Vessel.Persistence.VesselDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(pause).Options, new CoreOwner(owner));
        var secondWork = TestUnitOfWork.For(second);
        var stale = new PlayService(new Vessel.Persistence.PlayStore(second, secondWork), null!, TimeProvider.System, secondWork);
        var late = staleIsArchive ? stale.ArchiveRuleAsync("real", 1, default)
            : stale.SetRuleAsync("real", new("the stop is hit twice", "I stop for the week", ExpectedVersion: 1), default);
        await pause.Selected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (staleIsArchive) await service.SetRuleAsync("real", new("a new setup", "I size down", ExpectedVersion: 1), default);
        else await service.ArchiveRuleAsync("real", 1, default);
        pause.Resume.TrySetResult();
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => late)).StatusCode);
        var state = await service.RuleAsync("real", default);
        Assert.Equal(staleIsArchive ? 2 : null, state.Active?.Version);
    }
}
