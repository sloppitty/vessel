using System.Text.Json;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class StructuredReviewTests
{
    private readonly MemoryPlayStore store;
    private readonly PlayService service;
    private readonly Play play;

    public StructuredReviewTests()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Manual");
        store = new(account.OwnerId);
        store.Accounts.Add(account);
        play = TestPlays.Create(account, new("manual", "BTC"));
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        store.Add(play.MarkPlanned(at).Revision);
        play.RecordManualOpen(at);
        play.RecordManualClose(at.AddSeconds(1), at.AddSeconds(1));
        store.Add(play);
        service = new(store, new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.Saving(store.SaveAsync));
    }

    private Task<PlayDto> Save(ReviewAnswers answers, string review = "Updated review") =>
        service.UpdateAsync(play.Id, new(play.Version, play.AccountId, play.ContractId, "manual", play.Title,
            PlayDocuments.Read(play.Plan), JsonSerializer.Deserialize<JsonElement>(play.Drawings), review,
            StructuredReview: answers), default);

    private static ReviewAnswers WithValue(string field, string value) => field switch
    {
        "mistakeTag" => new("deviated", value, "Original lesson", "Original next action"),
        "lesson" => new("deviated", "late", value, "Original next action"),
        "nextAction" => new("deviated", "late", "Original lesson", value),
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private async Task AssertRejectedWithoutChanges(ReviewAnswers answers)
    {
        var before = (play.Answers, play.Review, play.Version, play.ReviewRevision, play.PlanRevision,
            play.UpdatedAtUtc, play.ReviewedAtUtc, store.Saves, store.Reviews.Count);
        await Assert.ThrowsAsync<DomainValidationException>(() => Save(answers, "Rejected review"));
        Assert.Equal(before, (play.Answers, play.Review, play.Version, play.ReviewRevision, play.PlanRevision,
            play.UpdatedAtUtc, play.ReviewedAtUtc, store.Saves, store.Reviews.Count));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task Multiline_answers_and_revision_history_keep_content_unchanged(string newline)
    {
        var answers = new ReviewAnswers("deviated", "late", $"  Wait for confirmation{newline}{newline}Keep the stop.  ",
            $"  Set an alert{newline}Wait for the retest.  ");
        var version = play.Version;
        var planRevision = play.PlanRevision;

        var saved = await Save(answers);

        Assert.Equal(answers, saved.StructuredReview);
        Assert.Equal(answers, (await service.GetAsync(play.Id, default)).StructuredReview);
        Assert.Equal(answers, Assert.Single((await service.HistoryAsync(play.Id, default)).Reviews!).StructuredReview);
        Assert.Equal(1, play.ReviewRevision);
        Assert.Equal(version + 1, play.Version);
        Assert.Equal(planRevision, play.PlanRevision);
        Assert.NotNull(play.ReviewedAtUtc);
        Assert.Equal(1, store.Saves);
    }

    [Theory]
    [InlineData("mistakeTag")]
    [InlineData("lesson")]
    [InlineData("nextAction")]
    public async Task Other_controls_are_rejected_without_partial_changes_and_tag_stays_single_line(string field)
    {
        await Save(new("followed", "original", "Original lesson", "Original next action"), "Original review");
        foreach (var control in Enumerable.Range(0, char.MaxValue + 1).Select(i => (char)i).Where(char.IsControl))
        {
            if (field != "mistakeTag" && control is '\r' or '\n') continue;
            await AssertRejectedWithoutChanges(WithValue(field, $"Before{control}After"));
        }
    }

    [Theory]
    [InlineData("mistakeTag", 100)]
    [InlineData("lesson", 4000)]
    [InlineData("nextAction", 4000)]
    public async Task Existing_field_bounds_are_kept(string field, int limit)
    {
        var answers = WithValue(field, new('x', limit));
        Assert.Equal(answers, (await Save(answers)).StructuredReview);
        await AssertRejectedWithoutChanges(WithValue(field, new('x', limit + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("followed")]
    [InlineData("deviated")]
    [InlineData("not-applicable")]
    public async Task Existing_adherence_values_are_kept(string? adherence)
    {
        var answers = new ReviewAnswers(adherence, Lesson: "A lesson");
        Assert.Equal(answers, (await Save(answers)).StructuredReview);
        await AssertRejectedWithoutChanges(answers with { Adherence = "unknown" });
    }
}
