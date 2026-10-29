using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Evidence;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class PlayPostgresTests
{
    private static object Body(Guid accountId, string title = "Breakout", string price = "100") => new
    {
        accountId, instrument = "BTC", instrumentSource = "venue", title, plan = TestPlays.Plan(price),
        drawings = new Dictionary<string, object[]> { ["hyperliquid:BTC"] = [new { id = "d1", kind = "text" }] },
    };

    [PostgresFact]
    public async Task Saved_plays_round_trip_with_revisions_history_and_concurrency()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();

        var created = await client.PostAsJsonAsync("/api/plays", Body(account.Id));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.Equal("d1", play.Drawings.GetProperty("hyperliquid:BTC")[0].GetProperty("id").GetString());

        var planned = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status", new { expectedVersion = play.Summary.Version, status = "planned" });
        play = (await planned.Content.ReadFromJsonAsync<PlayDto>())!;
        // Entity rules reach the owner as their status and exact message.
        var resumed = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status", new { expectedVersion = play.Summary.Version, status = "planned" });
        Assert.Equal((HttpStatusCode.Conflict, "Only a paused Play can be resumed."),
            (resumed.StatusCode, (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()));
        var longReason = await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", new UpdatePlayRequest(play.Summary.Version, account.Id,
            "BTC", "venue", "Breakout", TestPlays.Plan(stop: "97"), play.Drawings, "", new string('r', PlayPlanRevision.MaxReasonLength + 1)));
        Assert.Equal((HttpStatusCode.BadRequest, "Reasons are limited to 2000 characters."),
            (longReason.StatusCode, (await longReason.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()));
        var update = new UpdatePlayRequest(play.Summary.Version, account.Id, "BTC", "venue", "Breakout", TestPlays.Plan(stop: "97"), play.Drawings, "", "Moved stop");
        var revised = await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", update);
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        // The same expected version again is stale.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", update)).StatusCode);

        var history = (await client.GetFromJsonAsync<PlayHistoryDto>($"/api/plays/{play.Summary.Id}/history"))!;
        Assert.Equal([1, 2], history.Revisions.Select(r => r.Number));
        Assert.Equal("97", history.Revisions[1].Plan.Entries[0].Stops[0].Value);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/plays/{play.Summary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/accounts/{account.Id}")).StatusCode);

        // Another owner sees nothing and cannot read the history.
        await using var foreign = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString);
        using var other = foreign.AuthorizedClient();
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/plays")).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/plays/{play.Summary.Id}/history")).StatusCode);

        await using var db2 = database.Context(owner);
        Assert.Equal("97", await db2.Database.SqlQueryRaw<string>(
            "SELECT \"Plan\"->'entries'->0->'stops'->0->>'value' AS \"Value\" FROM plays").SingleAsync());
    }

    [PostgresFact]
    public async Task Plan_numbers_are_plain_decimals_on_write_and_stored_text_still_saves_through_the_api()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        var stored = PlayDocuments.Serialize(TestPlays.LegacyPlan());
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, stored, DateTimeOffset.UnixEpoch);
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); db.Plays.Add(play); await db.SaveChangesAsync(); }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        async Task<string?> Detail(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();

        var created = await client.PostAsJsonAsync("/api/plays", Body(account.Id, price: "1e2"));
        Assert.Equal((HttpStatusCode.BadRequest, $"Entry 1 price: {PlayDocumentTestsValues.PlainDecimalRule}"), (created.StatusCode, await Detail(created)));

        var dto = (await client.GetFromJsonAsync<PlayDto>($"/api/plays/{play.Id}"))!;
        Assert.Equal("1e2", dto.Plan.Entries[0].Price);
        var renamed = await client.PutAsJsonAsync($"/api/plays/{play.Id}", new UpdatePlayRequest(dto.Summary.Version, account.Id,
            "BTC", "venue", "Renamed", dto.Plan, dto.Drawings, "", null));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        dto = (await renamed.Content.ReadFromJsonAsync<PlayDto>())!;
        var changed = await client.PutAsJsonAsync($"/api/plays/{play.Id}", new UpdatePlayRequest(dto.Summary.Version, account.Id,
            "BTC", "venue", "Renamed", dto.Plan with { Size = ".25" }, dto.Drawings, "", null));
        Assert.Equal((HttpStatusCode.BadRequest, $"Size: {PlayDocumentTestsValues.PlainDecimalRule}"), (changed.StatusCode, await Detail(changed)));

        await using var check = database.Context(owner);
        Assert.Equal(stored, PlayDocuments.Serialize(PlayDocuments.Read((await check.Plays.SingleAsync()).Plan)));
    }

    [PostgresFact]
    public async Task Deleting_a_draft_removes_its_evidence()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account);
            db.Plays.Add(play);
            db.Evidence.Add(new PlayEvidence(Guid.NewGuid(), play, "k/a.png", "image/png", 1, new string('a', 64), EvidenceSource.Upload, "", DateTimeOffset.UnixEpoch));
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(owner))
        {
            var store = new Vessel.Persistence.PlayStore(db, TestUnitOfWork.For(db));
            Assert.Equal(["k/a.png"], await TestUnitOfWork.For(db).ExecuteAsync(async () => await store.DeleteAsync((await store.FindAsync(play.Id, default))!, default), default));
        }
        await using (var db = database.Context(owner))
        {
            Assert.Empty(await db.Plays.ToListAsync());
            Assert.Empty(await db.Evidence.ToListAsync());
            db.Accounts.Remove(await db.Accounts.SingleAsync());
            await db.SaveChangesAsync();
        }
    }

    [PostgresFact]
    public async Task Migration_keeps_earlier_play_heads_as_planned()
    {
        await using var database = await CoreDatabase.CreateAsync("EvidenceMarkup");
        var owner = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            var account = Guid.NewGuid();
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO accounts ("Id", "OwnerId", "VenueId", "Name", "SyncStatus") VALUES ('{account}', '{owner}', 'manual', 'A', 'manual');
                INSERT INTO plays ("Id", "OwnerId", "AccountId", "VenueId", "ContractId", "Status") VALUES ('{Guid.NewGuid()}', '{owner}', '{account}', 'manual', 'BTC', 'Active');
                """, connection);
            await insert.ExecuteNonQueryAsync();
        }
        await using (var db = database.Context(owner))
            await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync();
        await using (var db = database.Context(owner))
        {
            var play = await db.Plays.SingleAsync();
            Assert.Equal((PlayStatus.Planned, InstrumentSource.Manual, 1L, "{}"), (play.Status, play.InstrumentSource, play.Version, play.Drawings));
            Assert.Equal("1", PlayDocuments.Read(play.Plan).Leverage);
        }
    }
}
