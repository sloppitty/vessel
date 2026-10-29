using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vessel.Application.Events;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public sealed class VesselEventApiIntegrationTests
{

    [VesselEventPostgresFact]
    public async Task Manual_API_status_changes_emit_lifecycle_events_once_without_fill_events()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic manual account");
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var create = await client.PostAsJsonAsync("/api/plays", new
        {
            accountId = account.Id, instrument = "BTC", instrumentSource = "manual",
            title = "Synthetic manual Play", plan = TestPlays.Plan()
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var play = (await create.Content.ReadFromJsonAsync<PlayDto>())!;
        var status = $"/api/plays/{play.Summary.Id}/status";
        foreach (var next in new[] { "planned", "paused" })
        {
            var response = await client.PostAsJsonAsync(status, new { expectedVersion = play.Summary.Version, status = next });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            play = (await response.Content.ReadFromJsonAsync<PlayDto>())!;
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(status,
            new { expectedVersion = 1, status = "planned" })).StatusCode);

        var read = await client.GetAsync("/api/events?after=0&limit=200");
        Assert.True(read.Headers.CacheControl?.NoStore);
        var page = (await read.Content.ReadFromJsonAsync<VesselEventPage>())!;
        Assert.Equal(["1", "2"], page.Events.Select(e => e.Sequence));
        Assert.All(page.Events, e =>
        {
            Assert.Equal(owner, e.OwnerId);
            Assert.Equal("play.status-changed", e.Type);
            Assert.Equal(play.Summary.Id, e.Subject.PlayId);
            Assert.Equal("owner", e.Source.Trigger);
        });
        Assert.Equal(["planned", "paused"], page.Events.Select(e => e.Data.GetProperty("current").GetString()));
        Assert.Empty((await client.GetFromJsonAsync<VesselEventPage>($"/api/events?after={page.NextCursor}"))!.Events);
        // A bare sequence predates stream epochs: the client must reload rather than trust it.
        Assert.True((await client.GetFromJsonAsync<VesselEventPage>("/api/events?after=2"))!.ResetRequired);
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/events"));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("events")[0].GetProperty("sequence").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/events?after=-1")).StatusCode);

        await using var foreign = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString);
        using var other = foreign.AuthorizedClient();
        Assert.Empty((await other.GetFromJsonAsync<VesselEventPage>("/api/events"))!.Events);
    }
}
