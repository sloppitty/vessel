using System.Net.Http.Json;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>Phase 1, 1.4b in PostgreSQL: the process view reads Closed, Cancelled and Unresolved Plays as stored.</summary>
public sealed partial class DiaryPostgresTests
{
    [PostgresFact]
    public async Task A_plan_written_after_its_execution_counts_in_its_cell_with_the_badge_and_a_cancelled_answer_is_unscored()
    {
        // T29: planned now for executions recorded days earlier.
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var account = await Account(database, owner);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var planned = await Planned(client, account.Id);
        var opened = await Add(client, planned, Fact(planned.Summary.Version, "entry", "1", "100", 0));
        var closed = (await Add(client, opened.Play, Fact(opened.Play.Summary.Version, "exit", "1", "110", 1))).Play;
        async Task<PlayDto> Answer(PlayDto play, ReviewAnswers answers)
        {
            var response = await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", new UpdatePlayRequest(play.Summary.Version, account.Id, "BTC",
                "manual", play.Summary.Title, play.Plan, play.Drawings, "", StructuredReview: answers));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<PlayDto>())!;
        }
        await Answer(closed, new("followed", "Late plan", "", ""));

        // A cancelled Play with an answer is unscored; one without answers stays out of the process view.
        var other = await Planned(client, account.Id);
        var cancel = await client.PostAsJsonAsync($"/api/plays/{other.Summary.Id}/status", new ChangePlayStatusRequest(other.Summary.Version, "cancelled", "missed"));
        cancel.EnsureSuccessStatusCode();
        await Answer((await cancel.Content.ReadFromJsonAsync<PlayDto>())!, new("deviated", " late PLAN ", "", ""));
        var quiet = await Planned(client, account.Id);
        (await client.PostAsJsonAsync($"/api/plays/{quiet.Summary.Id}/status", new ChangePlayStatusRequest(quiet.Summary.Version, "cancelled", "missed")))
            .EnsureSuccessStatusCode();

        var review = (await client.GetFromJsonAsync<ReviewDto>("/api/review?period=all"))!;
        var process = review.Process!;
        Assert.Equal((1, 1, 0), (process.Followed.Wins, process.Unscored, process.Deviated.Wins + process.Deviated.Losses));
        // Both spellings are one tag, shown as the latest review wrote it.
        var tag = Assert.Single(process.MistakeTags);
        Assert.Equal(("late PLAN", 1, 1), (tag.Tag, tag.Wins, tag.Unscored));
        // All three ended Plays were planned without a recorded pre-flight.
        Assert.Equal((0, 3), (review.Preflight!.Recorded, review.Preflight.NotRecorded));
        var cell = (await client.GetFromJsonAsync<ReviewProcessPageDto>("/api/review/process?period=all&cell=followed-win"))!;
        var row = Assert.Single(cell.Items);
        Assert.Equal((closed.Summary.Id, true, "followed", "closed"), (row.Id, row.PlannedAfterExecution, row.Adherence, row.Status));
        var unscored = Assert.Single((await client.GetFromJsonAsync<ReviewProcessPageDto>("/api/review/process?period=all&lane=unscored"))!.Items);
        Assert.Equal((other.Summary.Id, "cancelled", (string?)null), (unscored.Id, unscored.Status, unscored.Net));
        Assert.Equal(3, (await client.GetFromJsonAsync<ReviewProcessPageDto>("/api/review/process?period=all&skippedCheck=not-recorded"))!.Total);
        Assert.Equal(2, (await client.GetFromJsonAsync<ReviewProcessPageDto>("/api/review/process?period=all&mistakeTag=late%20plan"))!.Total);
        // The review form's autocomplete offers the latest spelling once.
        Assert.Equal(["late PLAN"], (await client.GetFromJsonAsync<string[]>("/api/review/mistake-tags"))!);
    }
}
