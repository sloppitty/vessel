using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

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
