using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>Phase 1, 1.3a (decision 4): the owner declares a stuck Open Play unresolved.</summary>
public sealed class UnresolvedDomainTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
    private readonly Account account = new(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Trading");

    private Play Open()
    {
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), At);
        play.MarkPlanned(At);
        play.MarkOpen("Entry fill.", At.AddMinutes(1));
        return play;
    }

    [Fact]
    public void Only_an_open_Play_is_declared_with_a_reason_and_a_note_and_it_ends_without_a_plan_change()
    {
        var planned = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), At);
        planned.MarkPlanned(At);
        Assert.Throws<DomainRuleException>(() => planned.DeclareUnresolved(UnresolvedReason.Flip, "Flipped.", "{}", At));
        var play = Open();
        Assert.Throws<DomainValidationException>(() => play.DeclareUnresolved(UnresolvedReason.Flip, " ", "{}", At));
        Assert.Throws<DomainValidationException>(() => play.DeclareUnresolved((UnresolvedReason)99, "Flipped.", "{}", At));

        var change = play.DeclareUnresolved(UnresolvedReason.SharedClose, " Closed with another play. ", "{\"entered\":\"1\"}", At.AddHours(1));
        Assert.Equal((PlayStatus.Open, PlayStatus.Unresolved, UnresolvedReason.SharedClose, "Closed with another play.", StatusChangeSource.Owner),
            (change.From, change.To, change.UnresolvedReason, change.Note, change.Source));
        Assert.Equal("{\"entered\":\"1\"}", change.Evidence);
        Assert.Equal((PlayStatus.Unresolved, At.AddHours(1), true, false), (play.Status, play.EndedAtUtc, play.IsReviewable, play.IsEditable));
        Assert.Throws<DomainRuleException>(() => play.ChangePlan(PlayDocuments.Serialize(TestPlays.Plan("101")), "Moved.", At.AddHours(2)));
        Assert.Throws<DomainRuleException>(() => play.MarkClosed("Fills.", At.AddHours(2)));
    }

    [Fact]
    public void A_paper_Play_is_never_declared_unresolved()
    {
        // Main's paper trading: a simulation holds what it cannot settle as ambiguous or a gap, never as Unresolved.
        var paper = new Account(Guid.NewGuid(), account.OwnerId, "hyperliquid", "Hypotheses", "paper", 4000);
        paper.Configure(Guid.NewGuid(), null, null);
        var play = new Play(Guid.NewGuid(), paper, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), At);
        play.MarkPlanned(At);
        play.ApplyPaperExecutionState(hasEntry: true, fullyClosed: false, At.AddMinutes(1));
        Assert.Equal(PlayStatus.Open, play.Status);
        Assert.Throws<DomainRuleException>(() => play.DeclareUnresolved(UnresolvedReason.Other, "Stuck.", "{}", At.AddHours(1)));
        Assert.Equal((PlayStatus.Open, (DateTimeOffset?)null), (play.Status, play.EndedAtUtc));
    }

    [Fact]
    public void Leaving_a_final_state_clears_a_completed_review_and_keeps_its_text_and_answers()
    {
        var play = Open();
        play.DeclareUnresolved(UnresolvedReason.ForcedExitNotRetained, "Liquidated; the exit is not in history.", "{}", At.AddHours(1));
        var answers = new ReviewAnswers("deviated", "sizing", "Smaller size.", "Check margin first.");
        // Changed answers on an ended Play complete its review, an Unresolved one's too.
        Assert.Equal(ReviewActions.Completed, play.Annotate(play.Title, "{}", "Too large.", At.AddHours(2), answers)!.Action);
        Assert.Equal(ReviewCompletionKind.Completed, play.ReviewCompletion);

        Assert.Throws<DomainValidationException>(() => play.ReturnToOpen("  ", At.AddHours(3)));
        var (change, cleared) = play.ReturnToOpen("The exit fill arrived after all.", At.AddHours(3));
        Assert.Equal((PlayStatus.Unresolved, PlayStatus.Open, "The exit fill arrived after all."), (change.From, change.To, change.Note));
        Assert.Equal(ReviewActions.Cleared, cleared!.Action);
        Assert.Equal(((ReviewCompletionKind?)null, (DateTimeOffset?)null, (DateTimeOffset?)null), (play.ReviewCompletion, play.ReviewCompletedAtUtc, play.EndedAtUtc));
        Assert.Equal(("Too large.", answers), (play.Review, play.Answers));
        // While Open again, the stored answers save unchanged but cannot change.
        Assert.Null(play.Annotate(play.Title, "{}", "Too large.", At.AddHours(4), answers));
        Assert.Throws<DomainRuleException>(() => play.Annotate(play.Title, "{}", "Too large.", At.AddHours(4), answers with { Lesson = "Other." }));
        Assert.Throws<DomainRuleException>(() => play.ReturnToOpen("Again.", At.AddHours(4)));

        play.DeclareUnresolved(UnresolvedReason.Other, "Still stuck.", "{}", At.AddHours(5));
        Assert.Equal(At.AddHours(5), play.EndedAtUtc);
        play.CompleteReview(At.AddHours(6));
        var (closed, clearedAgain) = play.SettleUnresolved("Linked exits covered the 1 entered.", At.AddHours(7));
        Assert.Equal((PlayStatus.Unresolved, PlayStatus.Closed, StatusChangeSource.Venue), (closed.From, closed.To, closed.Source));
        Assert.Equal((PlayStatus.Closed, At.AddHours(7), (ReviewCompletionKind?)null), (play.Status, play.EndedAtUtc, play.ReviewCompletion));
        Assert.Equal(ReviewActions.Cleared, clearedAgain!.Action);
        Assert.Throws<DomainRuleException>(() => play.SettleUnresolved("Again.", At.AddHours(8)));
    }

    [Fact]
    public void A_return_keeps_the_reason_it_undoes_and_the_current_declaration_follows_the_end_time_not_the_clock()
    {
        var play = Open();
        List<PlayStatusChange> changes = [play.DeclareUnresolved(UnresolvedReason.SharedClose, "Shared.", "{}", At.AddHours(5))];
        var current = PlayUnresolved.Current(changes, play.EndedAtUtc)!;
        var (back, _) = play.ReturnToOpen("Relinked.", At.AddHours(6), current.UnresolvedReason);
        Assert.Equal((PlayStatus.Open, UnresolvedReason.SharedClose), (back.To, back.UnresolvedReason));
        changes.Add(back);
        // The clock stepped back: the newer declaration has the earlier time, and it is still the current one.
        changes.Add(play.DeclareUnresolved(UnresolvedReason.Flip, "Flipped.", "{}", At.AddHours(1)));
        Assert.Equal(UnresolvedReason.Flip, PlayUnresolved.Current(changes, play.EndedAtUtc)!.UnresolvedReason);
        Assert.Equal("flip", PlayUnresolved.Latest(changes, play.EndedAtUtc)!.Reason);
        // A return is never read as a declaration.
        Assert.Null(PlayUnresolved.Current([back], null));
    }

    [Fact]
    public void Declarations_at_one_instant_are_ordered_by_the_Play_versions_they_produced()
    {
        // Random change IDs must never decide: repeat so either ID order occurs.
        for (var round = 0; round < 64; round++)
        {
            var play = Open();
            var first = play.DeclareUnresolved(UnresolvedReason.SharedClose, "Shared.", "{}", At.AddHours(1));
            var (back, _) = play.ReturnToOpen("Relinked.", At.AddHours(1), PlayUnresolved.Current([first], play.EndedAtUtc)!.UnresolvedReason);
            var second = play.DeclareUnresolved(UnresolvedReason.Flip, "Flipped.", "{}", At.AddHours(1));
            Assert.True(first.PlayVersion < back.PlayVersion && back.PlayVersion < second.PlayVersion);
            Assert.Equal(play.Version, second.PlayVersion);
            Assert.Equal(UnresolvedReason.Flip, PlayUnresolved.Current([second, back, first], play.EndedAtUtc)!.UnresolvedReason);
            Assert.Equal(UnresolvedReason.Flip, PlayUnresolved.Current([first, back, second], play.EndedAtUtc)!.UnresolvedReason);
        }
    }

    [Fact]
    public void A_Play_without_a_completed_review_records_nothing_when_it_leaves_a_final_state()
    {
        var play = Open();
        play.DeclareUnresolved(UnresolvedReason.Flip, "Flipped short in one order.", "{}", At.AddHours(1));
        var revision = play.ReviewRevision;
        Assert.Null(play.ReturnToOpen("Relinked.", At.AddHours(2)).Cleared);
        Assert.Equal(revision, play.ReviewRevision);
    }
}
