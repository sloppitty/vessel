using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Monitoring;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

public sealed class MonitorDiscrepancyApiTests
{
    [PostgresFact]
    public async Task Real_dismiss_routes_validate_auth_membership_and_notes_and_commit_the_event()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 2 };
        await database.Seed(f);
        await using (var candidate = database.Scope(f)) await candidate.Evaluate(f.Observation());
        f.Advance(60);
        await using (var confirmed = database.Scope(f)) await confirmed.Evaluate(f.Observation());
        Guid id;
        await using (var read = database.Scope(f)) id = (await read.Db.Set<PlayDiscrepancy>().SingleAsync()).Id;
        // The new Play was not involved in this confirmed instrument discrepancy.
        var unrelated = f.AddPlay("long", 1);
        await using (var insert = database.Scope(f)) { insert.Db.Add(unrelated); await insert.Db.SaveChangesAsync(); }
        await using var factory = new CoreApiFactory(f.Account.OwnerId, connection: database.ConnectionString);
        using var client = factory.CreateClient();
        var accountUrl = $"/api/accounts/{f.Account.Id}/discrepancies/{id}/dismiss";
        var playUrl = $"/api/plays/{f.Play.Id}/discrepancies/{id}/dismiss";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(playUrl, new { note = "unauthenticated" })).StatusCode);
        TestAuthentication.SignIn(client.DefaultRequestHeaders, f.Account.OwnerId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/plays/{unrelated.Id}/discrepancies/{id}/dismiss", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/accounts/{Guid.NewGuid()}/discrepancies/{id}/dismiss", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(playUrl, new { note = new string('a', 2001) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(playUrl, new { note = "bad\u0001note" })).StatusCode);
        using var dismissed = await client.PostAsJsonAsync(playUrl, new { note = "accepted for now" });
        Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        Assert.Contains("no-store", dismissed.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(accountUrl, new { note = "idempotent retry" })).StatusCode);
        await using (var read = database.Scope(f))
        {
            var row = await read.Db.Set<PlayDiscrepancy>().SingleAsync();
            Assert.Equal("dismissed", row.State);
            Assert.Equal("accepted for now", row.Note);
            var events = (await EventReads.ReadAsync(read.Db, 0, 100, default)).Events;
            Assert.Equal(2, events.Count);
            Assert.Equal("dismissed", events[1].Data.GetProperty("resolution").GetString());
        }
        await using var foreignFactory = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString);
        using var foreign = foreignFactory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync(playUrl, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync(accountUrl, new { })).StatusCode);
    }

    [PostgresFact]
    public async Task Internal_candidate_is_not_dismissible_or_exposed_in_account_monitor()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 0 };
        await database.Seed(f);
        Guid id;
        await using (var candidate = database.Scope(f))
        {
            await candidate.Evaluate(f.Observation());
            id = (await candidate.Db.Set<PlayDiscrepancy>().SingleAsync()).Id;
        }
        await using var factory = new CoreApiFactory(f.Account.OwnerId, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var monitor = await client.GetFromJsonAsync<AccountMonitorDto>($"/api/accounts/{f.Account.Id}/monitor");
        Assert.Empty(monitor!.Discrepancies);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/accounts/{f.Account.Id}/discrepancies/{id}/dismiss", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/plays/{f.Play.Id}/discrepancies/{id}/dismiss", new { })).StatusCode);
        await using var read = database.Scope(f);
        Assert.Empty((await EventReads.ReadAsync(read.Db, 0, 100, default)).Events);
    }
}
