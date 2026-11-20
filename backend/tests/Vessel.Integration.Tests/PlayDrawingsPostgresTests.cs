using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Issue #110: chart drawings live in the owner-scoped <c>play_drawings</c> table.</summary>
public sealed class PlayDrawingsPostgresTests
{
    private const string Previous = "20261118010457_DatabaseHygieneIndexes";
    private sealed record Row(Guid Id, Guid OwnerId, string Drawings, Guid? DrawingsOwnerId, string? Document);

    [PostgresFact]
    public async Task Migration_copies_every_plays_drawings_exactly_with_its_owner_and_reverses()
    {
        await using var database = await CoreDatabase.CreateAsync(Previous);
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        // Unicode, escapes, number spellings, key order and a document near the 1 MB limit, as jsonb stored them.
        var documents = new[]
        {
            """{"hyperliquid:BTC":[{"id":"d1","kind":"text","text":"Café \"retést\" \\ 🚀","price":1.50,"t":1e2}]}""",
            """{"z:last":[],"a:first":[{"points":[{"time":1760000000,"price":0.000012345}],"style":{"color":"#ff0000","width":2}}]}""",
            "{}",
            "{\"hyperliquid:ETH\":[" + string.Join(',', Enumerable.Range(0, 190).Select(i =>
                $$"""{"id":"d{{i}}","kind":"text","text":"{{new string((char)('a' + i % 26), 5000)}}"}""")) + "]}",
        };
        var plays = new List<(Guid Id, Guid Owner, string Document)>();
        foreach (var owner in new[] { first, second })
        {
            await using var db = database.Context(owner);
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
            db.Accounts.Add(account);
            foreach (var document in documents)
            {
                var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
                db.Plays.Add(play);
                plays.Add((play.Id, owner, document));
            }
            // A paper Play is a Play too.
            var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
            var paper = PaperPlayTestsValues.PaperAccount(owner, portfolio.Id);
            db.AddRange(portfolio, paper, PaperLedgerEntry.Seed(owner, paper.Id, 4000, DateTimeOffset.UtcNow));
            var simulated = TestPlays.Create(paper, new PerpetualInstrument("hyperliquid", "BTC"));
            db.Plays.Add(simulated);
            plays.Add((simulated.Id, owner, documents[0]));
            await db.SaveChangesAsync();
            foreach (var (id, _, document) in plays.Where(p => p.Owner == owner))
                await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE plays SET "Drawings" = CAST({document} AS jsonb) WHERE "Id" = {id}""");
        }
        Dictionary<Guid, string> before;
        await using (var db = database.Context(first))
            before = (await db.Database.SqlQueryRaw<Row>("""
                SELECT "Id", "OwnerId", "Drawings"::text AS "Drawings", NULL::uuid AS "DrawingsOwnerId", NULL AS "Document" FROM plays
                """).ToListAsync()).ToDictionary(r => r.Id, r => r.Drawings);
        Assert.Equal(plays.Count, before.Count);

        // As an upgrade runs it.
        await using (var db = database.Context(first)) await db.GetService<IMigrator>().MigrateAsync();
        await using (var db = database.Context(first))
        {
            var rows = await Rows(db);
            Assert.Equal(plays.Count, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Equal(row.OwnerId, row.DrawingsOwnerId);
                // Byte-identical: the jsonb text of the copy is the column's, which the migration leaves in place.
                Assert.Equal(before[row.Id], row.Document);
                Assert.Equal(before[row.Id], row.Drawings);
            });
            Assert.Equal(plays.Count, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM play_drawings""").SingleAsync());
        }
        // The editor reads each owner's drawings from the table, and only theirs.
        foreach (var (id, owner, document) in plays)
        {
            await using var db = database.Context(owner);
            var dto = await new PlayService(new PlayStore(db, TestUnitOfWork.For(db)), new MemoryObjectStore(), TimeProvider.System,
                TestUnitOfWork.For(db)).GetAsync(id, default);
            using var expected = JsonDocument.Parse(document);
            Assert.True(JsonElement.DeepEquals(expected.RootElement, dto.Drawings));
            Assert.Equal(documents.Length + 1, await db.PlayDrawings.CountAsync());
        }

        // An edit made after the upgrade lives only in the table; Down writes it back, then drops the table.
        var edited = plays[1].Id;
        const string after = """{"hyperliquid:BTC":[{"id":"after-upgrade"}]}""";
        await using (var db = database.Context(first))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE play_drawings SET "Document" = CAST({after} AS jsonb) WHERE "PlayId" = {edited}""");
            var afterText = await db.Database.SqlQuery<string>($"""SELECT CAST({after} AS jsonb)::text AS "Value" """).SingleAsync();
            await db.GetService<IMigrator>().MigrateAsync(Previous);
            Assert.False(await db.Database.SqlQueryRaw<bool>(
                """SELECT to_regclass('play_drawings') IS NOT NULL AS "Value" """).SingleAsync());
            var columns = await db.Database.SqlQueryRaw<Row>(
                """SELECT "Id", "OwnerId", "Drawings"::text AS "Drawings", NULL::uuid AS "DrawingsOwnerId", NULL AS "Document" FROM plays""").ToListAsync();
            Assert.Equal(afterText, columns.Single(c => c.Id == edited).Drawings);
            Assert.All(columns.Where(c => c.Id != edited), c => Assert.Equal(before[c.Id], c.Drawings));
            // Up again copies the current column.
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.All(await Rows(db), row => Assert.Equal(row.Drawings, row.Document));
        }
    }

    [PostgresFact]
    public async Task A_drawings_save_and_a_plan_save_from_one_version_conflict()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var original = Drawings("first");
        var created = await client.PostAsJsonAsync("/api/plays", new
        {
            accountId = account.Id, instrument = "BTC", instrumentSource = "venue", title = "Breakout", plan = TestPlays.Plan(), drawings = original,
        });
        var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        var path = $"/api/plays/{play.Summary.Id}";
        UpdatePlayRequest Save(long version, PlayPlanDocument plan, JsonElement drawings) =>
            new(version, account.Id, "BTC", "venue", "Breakout", plan, drawings, "");

        // Drawings first: the plan save from the same version is stale.
        var drawn = await client.PutAsJsonAsync(path, Save(play.Summary.Version, play.Plan, Drawings("second")));
        Assert.Equal(HttpStatusCode.OK, drawn.StatusCode);
        Assert.True((await drawn.Content.ReadFromJsonAsync<PlayDto>())!.Summary.Version > play.Summary.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, Save(play.Summary.Version, TestPlays.Plan(stop: "97"), original))).StatusCode);
        var current = (await client.GetFromJsonAsync<PlayDto>(path))!;
        Assert.Equal(("second", "95"), (Id(current.Drawings), current.Plan.Entries[0].Stops[0].Value));

        // The plan first: the drawings save from the same version is stale.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, Save(current.Summary.Version, TestPlays.Plan(stop: "97"), current.Drawings))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, Save(current.Summary.Version, current.Plan, Drawings("third")))).StatusCode);
        current = (await client.GetFromJsonAsync<PlayDto>(path))!;
        Assert.Equal(("second", "97"), (Id(current.Drawings), current.Plan.Entries[0].Stops[0].Value));

        // Sent together: one wins and the other is told to reload.
        var responses = await Task.WhenAll(
            client.PutAsJsonAsync(path, Save(current.Summary.Version, current.Plan, Drawings("fourth"))),
            client.PutAsJsonAsync(path, Save(current.Summary.Version, TestPlays.Plan(stop: "96"), current.Drawings)));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var final = (await client.GetFromJsonAsync<PlayDto>(path))!;
        Assert.True(responses[0].IsSuccessStatusCode
            ? (Id(final.Drawings), final.Plan.Entries[0].Stops[0].Value) == ("fourth", "97")
            : (Id(final.Drawings), final.Plan.Entries[0].Stops[0].Value) == ("second", "96"));
    }

    [PostgresFact]
    public async Task Paper_play_drawings_save_and_read_through_the_table()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
        var account = PaperPlayTestsValues.PaperAccount(owner, portfolio.Id);
        await using (var seed = database.Context(owner))
        {
            seed.AddRange(portfolio, account, PaperLedgerEntry.Seed(owner, account.Id, 4000, DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var created = await client.PostAsJsonAsync("/api/plays?tradingMode=paper",
            new CreatePlayRequest(account.Id, "BTC", "venue", "Hypothesis", TestPlays.Plan(), Drawings("paper-first")));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        var path = $"/api/plays/{play.Summary.Id}?tradingMode=paper";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, new UpdatePlayRequest(play.Summary.Version, account.Id, "BTC",
            "venue", play.Summary.Title, play.Plan, Drawings("paper-second"), play.Review))).StatusCode);
        Assert.Equal("paper-second", Id((await client.GetFromJsonAsync<PlayDto>(path))!.Drawings));
        await using var verify = database.Context(owner);
        var row = await verify.PlayDrawings.SingleAsync();
        Assert.Equal((owner, play.Summary.Id), (row.OwnerId, row.PlayId));
        Assert.Equal("paper-second", Id(JsonDocument.Parse(row.Document).RootElement));
        // The retained legacy column is neither read nor written.
        Assert.Equal("{}", await verify.Database.SqlQuery<string>($"""SELECT "Drawings"::text AS "Value" FROM plays""").SingleAsync());
    }

    [PostgresFact]
    public async Task A_play_row_carries_no_drawings()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account);
            db.Plays.Add(play);
            await db.SaveChangesAsync();
        }
        // Reading a Play never touches the drawings table, and a Play row carries no drawings.
        await using (var db = database.Context(owner))
        {
            var sql = db.Plays.Where(p => p.Id == play.Id).ToQueryString();
            Assert.DoesNotContain("Drawings", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("play_drawings", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(db.Model.FindEntityType(typeof(Play))!.GetProperties(), p => p.Name.Contains("Drawing", StringComparison.Ordinal));
            Assert.Empty(db.Model.FindEntityType(typeof(Play))!.GetNavigations());
        }
    }

    private static Task<List<Row>> Rows(VesselDbContext db) => db.Database.SqlQueryRaw<Row>("""
        SELECT p."Id", p."OwnerId", p."Drawings"::text AS "Drawings", d."OwnerId" AS "DrawingsOwnerId", d."Document"::text AS "Document"
        FROM plays AS p LEFT JOIN play_drawings AS d ON d."PlayId" = p."Id"
        """).ToListAsync();

    private static JsonElement Drawings(string id) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object[]> { ["hyperliquid:BTC"] = [new { id, kind = "text" }] });

    private static string Id(JsonElement drawings) => drawings.GetProperty("hyperliquid:BTC")[0].GetProperty("id").GetString()!;
}
