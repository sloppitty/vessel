using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Phase 1, 1.4b: process and outcome as cases, with drill-downs that hold exactly what each count counts.</summary>
public sealed class ReviewProcessTests
{
    private readonly ReviewFixture f = new();
    private static readonly DateTimeOffset At = ReviewClock.Now.AddHours(-1);
    private static readonly string Plan = PlayDocuments.Serialize(TestPlays.Plan());
    private static readonly string EmptyPlan = PlayDocuments.Serialize(new PlayPlanDocument("long", "margin", "", "0", null, [], new PlanNotes("", "", "", "")));

    private static void Answer(Play play, string? adherence, string? tag = null, DateTimeOffset? at = null) =>
        play.Annotate(play.Title, "{}", play.Review, at ?? ReviewClock.Now, new ReviewAnswers(adherence, tag, "Lesson.", ""));

    private ClosedPlayFacts Scored(decimal net, string? adherence, string? tag = null, DateTimeOffset? reviewed = null)
    {
        var facts = f.Add(net);
        if (adherence is not null || tag is not null) Answer(facts.Play, adherence, tag, reviewed);
        return facts;
    }

    private Play Cancelled(string? adherence = null, string? tag = null, string? preflight = null, bool planned = true)
    {
        var account = f.Accounts.FirstOrDefault() ?? f.Account();
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument(account.VenueId, "BTC"), InstrumentSource.Venue, Plan, At.AddDays(-3));
        List<PlayPlanRevision> revisions = [];
        if (planned) revisions.Add(play.MarkPlanned(At.AddDays(-3), preflight).Revision);
        play.Cancel(CancelReason.Missed, null, At.AddMinutes(-30));
        if (adherence is not null || tag is not null) Answer(play, adherence, tag);
        f.Cancelled.Add(new(play, revisions));
        return play;
    }

    private Play Unresolved(string? adherence)
    {
        var account = f.Accounts.FirstOrDefault() ?? f.Account();
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument(account.VenueId, "BTC"), InstrumentSource.Venue, Plan, At.AddDays(-3));
        var revision = play.MarkPlanned(At.AddDays(-3)).Revision;
        play.MarkOpen("filled", At.AddDays(-2));
        play.DeclareUnresolved(UnresolvedReason.Flip, "Flipped.", "{}", At.AddMinutes(-20));
        if (adherence is not null) Answer(play, adherence);
        f.Unresolved.Add(new(play, null) { Revisions = [revision] });
        return play;
    }

    /// <summary>A fee in a token Review cannot price excludes the Play.</summary>
    private static ClosedPlayFacts Unpriced(ClosedPlayFacts facts)
    {
        var exit = facts.Fills.First(x => x.Role == OrderLinkRole.Exit).Fill;
        exit.Fee = 0.01m; exit.FeeToken = "MYSTERY";
        return facts;
    }

    private async Task<int> Total(string? cell = null, string? lane = null, string? tag = null, string? check = null) =>
        (await f.Service.ProcessAsync(new(new(Period: "all"), cell, lane, tag, check), default)).Total;

    [Fact]
    public async Task Each_ended_Play_lands_in_the_first_lane_that_applies_and_every_count_equals_its_drill_down()
    {
        Scored(5, "followed"); Scored(-5, "followed"); Scored(0, "followed");
        Scored(5, "deviated"); Scored(-5, "deviated");
        Scored(5, null);                         // unanswered
        Scored(-5, "not-applicable");
        var excluded = Unpriced(f.Add(5));
        Answer(excluded.Play, "followed");       // excluded by Review, but answered: unscored
        Unpriced(f.Add(5));                      // unanswered and excluded: exclusion list only
        Cancelled("deviated");                   // unscored
        Cancelled();                             // no answers: exclusion list only
        Unresolved("followed");                  // unscored
        var retrospective = Play.Retrospective(Guid.NewGuid(), f.Accounts[0], new("hyperliquid", "BTC"), EmptyPlan,
            At, At, "Imported");
        f.Closed.Add(new ClosedPlayFacts(retrospective, [], f.Closed[0].Fills));

        var process = (await f.Service.GetAsync(new(Period: "all"), default)).Process!;
        Assert.Equal(new ReviewProcessRowDto(1, 1, 1), process.Followed);
        Assert.Equal(new ReviewProcessRowDto(1, 1, 0), process.Deviated);
        Assert.Equal((1, 3, 1, 1), (process.Retrospective, process.Unscored, process.Unanswered, process.NotApplicable));
        foreach (var cell in ReviewService.ProcessCells)
        {
            var row = cell.StartsWith("followed") ? process.Followed : process.Deviated;
            var count = cell.EndsWith("win") ? row.Wins : cell.EndsWith("loss") ? row.Losses : row.Scratches;
            Assert.Equal(count, await Total(cell: cell));
        }
        Assert.Equal((1, 3, 1, 1), (await Total(lane: "retrospective"), await Total(lane: "unscored"), await Total(lane: "unanswered"),
            await Total(lane: "not-applicable")));
        // Unscored rows carry no money; the cancelled and unresolved ones are placed by their own times.
        var unscored = (await f.Service.ProcessAsync(new(new(Period: "all"), Lane: "unscored"), default)).Items;
        Assert.All(unscored, row => Assert.Equal(((string?)null, (string?)null), (row.Net, row.RMultiple)));
        Assert.Equal(["cancelled", "closed", "unresolved"], unscored.Select(row => row.Status).Order());
    }

    [Fact]
    public async Task Mistake_tags_count_together_whatever_their_case_and_show_the_latest_spelling()
    {
        Scored(5, "deviated", " Sizing", ReviewClock.Now.AddHours(-3));
        Scored(-5, "deviated", "sizing ", ReviewClock.Now.AddHours(-2));
        Scored(0, "followed", "SIZING up", ReviewClock.Now.AddHours(-1));
        Cancelled("deviated", "Sizing");
        var process = (await f.Service.GetAsync(new(Period: "all"), default)).Process!;
        Assert.Equal(2, process.MistakeTagCount);
        var sizing = Assert.Single(process.MistakeTags, t => t.Tag.Equals("sizing", StringComparison.OrdinalIgnoreCase));
        // The cancelled Play's review is the latest, at the clock's now.
        Assert.Equal(new ReviewMistakeTagDto("Sizing", 1, 1, 0, 1), sizing);
        Assert.Equal(3, await Total(tag: "  SIZING"));
        Assert.Equal(1, await Total(tag: "sizing up"));
    }

    [Fact]
    public async Task Pre_flight_counts_and_their_drill_down_cover_every_planned_ended_Play_in_the_period()
    {
        var skipped = PlayPreflight.Serialize([new("thesis", "skipped"), new("risk", "passed")]);
        var closed = Scored(5, "followed");
        Cancelled(preflight: skipped);
        Cancelled(planned: false);               // never planned: no Plan it, so no pre-flight to count
        Unresolved(null);
        var preflight = (await f.Service.GetAsync(new(Period: "all"), default)).Preflight!;
        Assert.Equal((1, 2), (preflight.Recorded, preflight.NotRecorded));
        Assert.Equal(1, Assert.Single(preflight.Skipped, s => s.Code == "thesis").Count);
        Assert.Equal((1, 2, 0), (await Total(check: "thesis"), await Total(check: "not-recorded"), await Total(check: "risk")));
        Assert.Contains(closed.Play.Id, (await f.Service.ProcessAsync(new(new(Period: "all"), SkippedCheck: "not-recorded"), default)).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_drill_down_takes_exactly_one_known_selection()
    {
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Total())).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Total(cell: "followed-win", lane: "unscored"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Total(cell: "followed-maybe"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Total(lane: "cells"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Total(check: "vibes"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Total(tag: " "))).StatusCode);
    }

    [Fact]
    public async Task Origin_and_direction_groups_keep_a_fixed_order_and_rows_carry_the_answers()
    {
        Scored(-5, "deviated", "Late");
        var retrospective = Play.Retrospective(Guid.NewGuid(), f.Accounts[0], new("hyperliquid", "BTC"), EmptyPlan,
            At, At, "Imported");
        f.Closed.Add(new ClosedPlayFacts(retrospective, [], f.Closed[0].Fills));
        var review = await f.Service.GetAsync(new(Period: "all"), default);
        Assert.Equal(["planned", "retrospective"], review.Groups.Origins.Select(g => g.Id));
        Assert.Equal(["long"], review.Groups.Directions.Select(g => g.Id));
        var row = review.Plays.Single(p => p.Origin == "planned");
        Assert.Equal(("deviated", "Late", "long", (string?)"completed"), (row.Adherence, row.MistakeTag, row.Direction, row.ReviewCompletion));
    }
}
