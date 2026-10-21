using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>Phase 1.2 (D-14, G-04, decision 3): review completion is explicit, and the in-trade log never counts as a review.</summary>
public sealed class ReviewCompletionTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly Account Account = new(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Venue");

    private static Play Open()
    {
        var play = TestPlays.Create(Account, new PerpetualInstrument("hyperliquid", "BTC"));
        play.MarkPlanned(At);
        play.MarkOpen("Filled.", At.AddMinutes(1));
        return play;
    }

    private static Play Closed()
    {
        var play = Open();
        play.MarkClosed("Exited.", At.AddHours(1));
        return play;
    }

    [Fact]
    public void A_note_in_the_Review_box_while_Open_no_longer_marks_the_Play_reviewed_when_it_closes()
    {
        // verify-sol-dotnet/Verify2.cs F8 and verify-opus-dotnet/More4.cs EarlyReview: before this, the note made the
        // closed Play "Reviewed" through HasReview || ReviewedAtUtc. Now only an explicit completion does.
        var play = Open();
        var revision = play.Annotate("", "{}", "TP1 taken; reassess tomorrow", At.AddMinutes(5));
        Assert.Equal(ReviewActions.Edited, revision!.Action);
        play.MarkClosed("Exited.", At.AddHours(1));
        Assert.True(play.HasReview);
        Assert.NotNull(play.ReviewedAtUtc);
        Assert.Null(play.ReviewCompletion);
    }

    [Fact]
    public void Complete_review_works_with_unchanged_text_and_Review_again_reopens_it()
    {
        var play = Closed();
        play.Annotate("", "{}", "Followed the plan", At.AddHours(2));
        var version = play.Version;
        var completed = play.CompleteReview(At.AddHours(3));
        Assert.Equal((ReviewCompletionKind.Completed, At.AddHours(3), ReviewActions.Completed, "Followed the plan"),
            (play.ReviewCompletion, play.ReviewCompletedAtUtc, completed.Action, completed.Review));
        Assert.True(play.Version > version);
        Assert.Throws<DomainRuleException>(() => play.CompleteReview(At.AddHours(4)));
        var reopened = play.ReopenReview(At.AddHours(5));
        Assert.Equal((null, null, ReviewActions.Reopened), (play.ReviewCompletion, play.ReviewCompletedAtUtc, reopened.Action));
        Assert.Equal("Followed the plan", play.Review);
        Assert.Throws<DomainRuleException>(() => play.ReopenReview(At.AddHours(6)));
        // An empty review can be completed too: completion is the owner's statement, not a word count.
        var empty = Closed();
        empty.CompleteReview(At);
        Assert.Equal(ReviewCompletionKind.Completed, empty.ReviewCompletion);
    }

    [Fact]
    public void Saving_changed_answers_on_an_ended_Play_completes_it_and_text_alone_never_does()
    {
        var play = Closed();
        Assert.Equal(ReviewActions.Edited, play.Annotate("", "{}", "Text only", At.AddHours(2))!.Action);
        Assert.Null(play.ReviewCompletion);
        var answered = play.Annotate("", "{}", "Text only", At.AddHours(3), new ReviewAnswers("followed"));
        Assert.Equal((ReviewCompletionKind.Completed, ReviewActions.Completed), (play.ReviewCompletion, answered!.Action));
        // Once complete, edits keep it complete; clearing answers on a reopened review does not complete it.
        play.Annotate("", "{}", "More text", At.AddHours(4));
        Assert.Equal(ReviewCompletionKind.Completed, play.ReviewCompletion);
        play.ReopenReview(At.AddHours(5));
        play.Annotate("", "{}", "More text", At.AddHours(6), new ReviewAnswers());
        Assert.Null(play.ReviewCompletion);
    }

    [Fact]
    public void Only_an_ended_Play_completes_and_only_a_cancelled_one_needs_no_review()
    {
        Assert.Throws<DomainRuleException>(() => Open().CompleteReview(At));
        Assert.Throws<DomainRuleException>(() => Closed().MarkNoReviewNeeded(At));
        var cancelled = TestPlays.Create(Account, new PerpetualInstrument("hyperliquid", "BTC"));
        cancelled.MarkPlanned(At);
        cancelled.Cancel(CancelReason.Missed, null, At.AddMinutes(1));
        var marked = cancelled.MarkNoReviewNeeded(At.AddMinutes(2));
        Assert.Equal((ReviewCompletionKind.NotNeeded, ReviewActions.NotNeeded), (cancelled.ReviewCompletion, marked.Action));
        Assert.Throws<DomainRuleException>(() => cancelled.MarkNoReviewNeeded(At.AddMinutes(3)));
        cancelled.ReopenReview(At.AddMinutes(4));
        Assert.Null(cancelled.ReviewCompletion);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("ok", "anxious")]
    [InlineData("bell\u0007", null)]
    public void A_log_entry_is_bounded_plain_text_with_a_known_state(string text, string? state)
    {
        Assert.Throws<DomainValidationException>(() => new PlayNote(Open(), Guid.NewGuid(), text, state, At));
    }

    [Fact]
    public void A_log_entry_keeps_lines_and_a_state_and_changes_nothing_on_the_Play()
    {
        var play = Open();
        var version = play.Version;
        var note = new PlayNote(play, Guid.NewGuid(), "  Rushed the add.\nWaiting now.  ", "rushed", At);
        Assert.Equal(("Rushed the add.\nWaiting now.", "rushed"), (note.Text, note.State));
        Assert.Equal(version, play.Version);
        Assert.Throws<DomainValidationException>(() => new PlayNote(play, Guid.Empty, "x", null, At));
        Assert.Throws<DomainValidationException>(() => new PlayNote(play, Guid.NewGuid(), new string('x', PlayNote.MaxTextLength + 1), null, At));
    }
}

public sealed class ReviewCompletionServiceTests
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly MemoryPlayStore store;
    private readonly PlayService service;
    private readonly Account account;

    public ReviewCompletionServiceTests()
    {
        store = new MemoryPlayStore(owner);
        account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        store.Accounts.Add(account);
        service = new PlayService(store, new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.Saving(store.SaveAsync));
    }

    private Play Cancelled()
    {
        var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
        play.MarkPlanned(DateTimeOffset.UtcNow);
        play.Cancel(CancelReason.Missed, null, DateTimeOffset.UtcNow);
        store.Plays.Add(play);
        return play;
    }

    [Fact]
    public async Task No_review_needed_is_refused_when_the_Play_has_linked_fills_and_every_action_takes_the_version()
    {
        var play = Cancelled();
        store.LinkedFills = true;
        var refused = await Assert.ThrowsAsync<ProblemException>(() => service.NoReviewNeededAsync(play.Id, new(play.Version), default));
        Assert.Equal(409, refused.StatusCode);
        store.LinkedFills = false;
        await Assert.ThrowsAsync<ProblemException>(() => service.NoReviewNeededAsync(play.Id, new(play.Version - 1), default));
        var marked = await service.NoReviewNeededAsync(play.Id, new(play.Version), default);
        Assert.Equal("not-needed", marked.Summary.ReviewCompletion);
        Assert.Equal(ReviewActions.NotNeeded, Assert.Single(store.Reviews).Action);
        var reopened = await service.ReopenReviewAsync(play.Id, new(marked.Summary.Version), default);
        Assert.Null(reopened.Summary.ReviewCompletion);
        Assert.Equal("completed", (await service.CompleteReviewAsync(play.Id, new(reopened.Summary.Version), default)).Summary.ReviewCompletion);
    }

    [Fact]
    public async Task A_log_entry_is_written_once_per_request_and_never_completes_a_review()
    {
        var play = Cancelled();
        var request = new AddPlayNoteRequest(Guid.NewGuid(), "Missed the fill; chasing would break the plan.", "calm");
        var first = await service.AddNoteAsync(play.Id, request, default);
        var retry = await service.AddNoteAsync(play.Id, request, default);
        Assert.Equal(first.Single().Id, retry.Single().Id);
        var reused = await Assert.ThrowsAsync<ProblemException>(() => service.AddNoteAsync(play.Id, request with { Text = "Different" }, default));
        Assert.Equal(409, reused.StatusCode);
        Assert.Null(play.ReviewCompletion);
        Assert.Empty(store.Reviews);
        Assert.Single(await service.NotesAsync(play.Id, default));
    }
}
