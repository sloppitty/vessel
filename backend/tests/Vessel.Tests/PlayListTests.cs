using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class PlayListPostgresTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    private static readonly PlayPlanDocument ShortPlan = new("short", "margin", "250", "3", null,
        [new PlanEntry("entry-1", "Entry 1", "#b9c9e4", "100", "100", [new PlanExit("stop-1", "price", "105", "100")],
            [new PlanExit("target-1", "price", "90", "100")])], new PlanNotes("", "", "", ""));
    private const string Whitespace ="\u00A0\u2003\u3000\t";

    private sealed record Seeded(Guid Owner, Guid OtherOwner, Portfolio Swing, Account First, Account Second, Account Disabled,
        Dictionary<string, Guid> Plays);

    private static Play New(Account account, string? contract, int minute, string direction = "long", string title = "")
    {
        var plan = PlayDocuments.Serialize(TestPlays.Plan() with { Direction = direction });
        var play = new Play(Guid.NewGuid(), account, contract is null ? null : new PerpetualInstrument(account.VenueId, contract),
            account.VenueId == "manual" ? InstrumentSource.Manual : InstrumentSource.Venue, plan, T0.AddMinutes(minute));
        play.Annotate(title, "{}", "", T0.AddMinutes(minute));
        return play;
    }

    private static Play Planned(Account account, string contract, int minute, string direction = "long", string title = "")
    {
        var play = New(account, contract, minute, direction, title);
        play.MarkPlanned(T0.AddMinutes(minute));
        return play;
    }

    private static Play Closed(Account account, string contract, int minute, string title, string review = "", ReviewAnswers? answers = null)
    {
        var play = Planned(account, contract, minute, title: title);
        play.MarkOpen("Filled", T0.AddMinutes(minute));
        play.MarkClosed("Flat", T0.AddMinutes(minute));
        if (review != "" || answers is not null) play.Annotate(title, "{}", review, T0.AddMinutes(minute), answers);
        return play;
    }

    private static Play Completed(Play play, int minute)
    {
        play.CompleteReview(T0.AddMinutes(minute));
        return play;
    }

    private static Play Cancelled(Account account, string contract, int minute, string title, ReviewAnswers? answers = null)
    {
        var play = Planned(account, contract, minute, title: title);
        play.Cancel(CancelReason.Missed, null, T0.AddMinutes(minute));
        if (answers is not null) play.Annotate(title, "{}", "", T0.AddMinutes(minute), answers);
        return play;
    }

    // First is in Swing, Second is Unassigned (manual) and Disabled is a disabled Swing account. Minutes set
    // update and end times, so every order below is deliberate.
    private static async Task<Seeded> SeedAsync(CoreDatabase database)
    {
        var owner = Guid.NewGuid(); var otherOwner = Guid.NewGuid();
        var swing = new Portfolio(Guid.NewGuid(), owner, "Swing", "wallet");
        var first = new Account(Guid.NewGuid(), owner, "hyperliquid", "First"); first.Configure(swing.Id, null, null);
        var second = new Account(Guid.NewGuid(), owner, "manual", "Second");
        var disabled = new Account(Guid.NewGuid(), owner, "hyperliquid", "Disabled"); disabled.UpdateSettings("Disabled", swing.Id, false);
        var plays = new Dictionary<string, Play>
        {
            ["d1"] = New(first, "BTC", 10, title: @"Breakout 50%_\ test"),
            ["d2"] = New(second, null, 20, title: "Range idea"),
            ["p1"] = Planned(first, "ETH", 30, "short", "Fade"),
            ["p2"] = Paused(Planned(second, "GOLD", 40, title: "Breakout gold"), 41),
            ["o1"] = Open(Planned(first, "SOL", 50, title: "Trend"), 51),
            ["c1"] = Closed(first, "BTC", 60, "No review"),
            ["c2"] = Closed(first, "ETH", 70, "Blank review", Whitespace, new ReviewAnswers(null, "  ", "\n", " ")),
            ["c3"] = Completed(Closed(second, "GOLD", 80, "Zero width", "\u200B"), 80),
            ["x1"] = Cancelled(first, "BTC", 90, "Answered", new ReviewAnswers("not-applicable")),
            ["x2"] = Cancelled(second, "OIL", 100, "Reviewed earlier"),
            ["r1"] = Play.Retrospective(Guid.NewGuid(), first, new PerpetualInstrument("hyperliquid", "DOGE"),
                PlayDocuments.Serialize(TestPlays.Plan() with { Direction = "short" }), T0.AddMinutes(5), T0.AddMinutes(110), "Retro"),
            ["hidden"] = New(disabled, "BTC", 200, title: "Breakout hidden")
        };
        await using (var db = database.Context(owner))
        {
            db.Portfolios.Add(swing); db.Accounts.AddRange(first, second, disabled); db.Plays.AddRange(plays.Values);
            await db.SaveChangesAsync();
            // A review time alone no longer completes a review (decision 3): only an explicit completion does.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plays SET \"ReviewedAtUtc\" = {T0} WHERE \"Id\" = {plays["x2"].Id}");
        }
        var foreign = new Account(Guid.NewGuid(), otherOwner, "hyperliquid", "Foreign");
        await using (var db = database.Context(otherOwner))
        {
            db.Accounts.Add(foreign); db.Plays.Add(New(foreign, "BTC", 300, title: "Breakout foreign"));
            await db.SaveChangesAsync();
        }
        return new(owner, otherOwner, swing, first, second, disabled, plays.ToDictionary(p => p.Key, p => p.Value.Id));
    }

    private static Play Paused(Play play, int minute) { play.Pause(T0.AddMinutes(minute)); return play; }
    private static Play Open(Play play, int minute) { play.MarkOpen("Filled", T0.AddMinutes(minute)); return play; }

    private static PlayService Service(VesselDbContext db) => new(new PlayStore(db, TestUnitOfWork.For(db)), new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.For(db));

    [PostgresFact]
    public async Task Views_follow_the_review_definition_order_and_projection()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        var names = seeded.Plays.ToDictionary(p => p.Value, p => p.Key);
        await using var db = database.Context(seeded.Owner);
        var service = Service(db);
        async Task<string[]> View(string? view, string? q = null, string? scope = "all", string? page = null, string? size = null) =>
            (await service.ListAsync(view, q, scope, page, size, default)).Plays.Select(p => names[p.Id]).ToArray();

        Assert.Equal(["d2", "d1"], await View("drafts"));
        Assert.Equal(["o1", "p2", "p1"], await View("progress"));
        // Reviewed is an explicit completion: Complete review (c3) or saved answers (x1). Text and review times never count.
        Assert.Equal(["x2", "c2", "c1", "r1"], await View("review"));
        Assert.Equal(["x1", "c3"], await View("reviewed"));
        Assert.Equal(["c3", "c2", "c1", "r1"], await View("closed"));
        Assert.Equal(["x2", "x1"], await View("cancelled"));
        Assert.Equal(["r1", "x2", "x1", "c3", "c2", "c1", "o1", "p2", "p1", "d2", "d1"], await View("all"));
        Assert.Equal(await View("all"), await View(null));

        var list = await service.ListAsync("review", null, null, null, null, default);
        Assert.Equal(("review", 1, 25, 4), (list.View, list.Page, list.PageSize, list.TotalCount));
        var c2 = list.Plays.Single(p => names[p.Id] == "c2");
        Assert.False(c2.HasReview); Assert.Null(c2.ReviewedAtUtc);
        var detail = await service.GetAsync(c2.Id, default);
        Assert.Equal(detail.Summary, c2);
        var retro = list.Plays[^1];
        Assert.Equal(("closed", "short", "retrospective"), (retro.Status, retro.Direction, retro.Origin));
        Assert.Equal(T0.AddMinutes(5), retro.EndedAtUtc);
        foreach (var (key, id) in seeded.Plays.Where(p => p.Key != "hidden"))
            Assert.Equal((await service.GetAsync(id, default)).Summary, (await service.ListAsync("all", null, null, null, "100", default)).Plays.Single(p => p.Id == id));

        Assert.Equal(new PlayCountsDto(2, 3, 4, 2, 4, 2, 11), await service.CountsAsync(null, "all", default));
        // The unscoped Plays page also lists Plays on disabled accounts; Overview scopes never do.
        Assert.Equal(["hidden", "d2", "d1"], await View("drafts", scope: null));
        Assert.Equal(new PlayCountsDto(3, 3, 4, 2, 4, 2, 12), await service.CountsAsync(null, null, default));
    }

    [PostgresFact]
    public async Task List_queries_project_summary_columns_without_plan_drawings_or_review_text()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        var sql = new List<string>();
        await using var db = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString)
            .LogTo(message => sql.Add(message), [Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted]).Options,
            new CoreOwner(seeded.Owner));
        var service = Service(db);
        foreach (var view in new[] { "drafts", "progress", "review", "reviewed", "closed", "cancelled", "all" })
            await service.ListAsync(view, "a", $"portfolio:{seeded.Swing.Id}", null, null, default);
        await service.CountsAsync("a", "unassigned", default);
        var selects = sql.Where(s => s.Contains("FROM plays", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(selects);
        Assert.All(selects, s =>
        {
            var projection = s[s.IndexOf("SELECT", StringComparison.Ordinal)..s.IndexOf("FROM plays", StringComparison.Ordinal)];
            Assert.DoesNotContain("\"Plan\"", s); Assert.DoesNotContain("\"Drawings\"", s);
            // Review text is only tested inside SQL; it is never a selected column.
            Assert.DoesNotMatch(@"(?m)(SELECT|,)\s*p\.""(Review|ReviewLesson|ReviewNextAction|ReviewMistakeTag)""\s*(,|$)", projection);
        });
    }

    [PostgresFact]
    public async Task Scope_keeps_enabled_owner_accounts_and_unknown_scopes_are_404()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        var names = seeded.Plays.ToDictionary(p => p.Value, p => p.Key);
        await using var db = database.Context(seeded.Owner);
        var service = Service(db);
        async Task<string[]> All(string scope) => (await service.ListAsync("all", null, scope, null, "100", default)).Plays.Select(p => names[p.Id]).ToArray();

        Assert.Equal(["x2", "c3", "p2", "d2"], await All("unassigned"));
        Assert.Equal(["r1", "x1", "c2", "c1", "o1", "p1", "d1"], await All($"portfolio:{seeded.Swing.Id}"));
        Assert.Equal(await All($"portfolio:{seeded.Swing.Id}"), await All($"account:{seeded.First.Id}"));
        Assert.Empty(await All($"account:{seeded.Disabled.Id}"));
        // Unassigned holds x2 (a review time, not completed) and c3 (completed).
        Assert.Equal(new PlayCountsDto(1, 1, 1, 1, 1, 1, 4), await service.CountsAsync(null, "unassigned", default));
        Assert.Equal(new PlayCountsDto(0, 0, 0, 0, 0, 0, 0), await service.CountsAsync(null, $"account:{seeded.Disabled.Id}", default));
        foreach (var scope in new[] { $"portfolio:{Guid.NewGuid()}", $"account:{Guid.NewGuid()}" })
        {
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.ListAsync("all", null, scope, null, null, default))).StatusCode);
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.CountsAsync(null, scope, default))).StatusCode);
        }

        // Another owner's accounts are unknown to this owner, and its own Play stays invisible here.
        await using var foreignDb = database.Context(seeded.OtherOwner);
        var foreign = Service(foreignDb);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => foreign.ListAsync("all", null, $"account:{seeded.First.Id}", null, null, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => foreign.CountsAsync(null, $"portfolio:{seeded.Swing.Id}", default))).StatusCode);
        Assert.Equal(1, (await foreign.CountsAsync(null, null, default)).All);
    }

    [PostgresFact]
    public async Task Search_is_a_trimmed_case_insensitive_literal_substring_of_title_or_instrument()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        var names = seeded.Plays.ToDictionary(p => p.Value, p => p.Key);
        await using var db = database.Context(seeded.Owner);
        var service = Service(db);
        async Task<string[]> Find(string? q) =>
            (await service.ListAsync("all", q, "all", null, "100", default)).Plays.Select(p => names[p.Id]).Order().ToArray();

        Assert.Equal(["d1"], await Find("%"));
        Assert.Equal(["d1"], await Find("_"));
        Assert.Equal(["d1"], await Find(@"\"));
        Assert.Equal(["d1"], await Find(@"50%_\ T"));
        Assert.Equal(["d1", "p2"], await Find("  BREAKOUT "));
        Assert.Equal(["o1"], await Find("sol"));
        Assert.Equal(["c1", "d1", "x1"], await Find("btc"));
        Assert.Empty(await Find("b_c"));
        Assert.Equal(11, (await Find("   ")).Length);
        Assert.Equal(11, (await Find(null)).Length);
        Assert.Equal(new PlayCountsDto(1, 1, 0, 0, 0, 0, 2), await service.CountsAsync("breakout", "all", default));
        Assert.Equal(new PlayCountsDto(2, 1, 0, 0, 0, 0, 3), await service.CountsAsync("breakout", null, default));
        Assert.Empty(await Find(" " + new string('x', 100) + " "));
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Find(new string('x', 101)))).StatusCode);
    }

    [PostgresFact]
    public async Task Ended_views_page_with_clamping_and_unpaged_views_stop_at_200_with_an_exact_total()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        await using (var db = database.Context(seeded.Owner))
        {
            var first = await db.Accounts.SingleAsync(a => a.Id == seeded.First.Id);
            db.Plays.AddRange(Enumerable.Range(0, 200).Select(n => New(first, "BTC", 1000 + n, title: $"Bulk {n}")));
            await db.SaveChangesAsync();
        }
        await using var reading = database.Context(seeded.Owner);
        var service = Service(reading);
        var drafts = await service.ListAsync("drafts", null, "all", null, null, default);
        Assert.Equal(("drafts", 1, 200, 202, 200), (drafts.View, drafts.Page, drafts.PageSize, drafts.TotalCount, drafts.Plays.Count));
        var recent = await service.ListAsync("drafts", null, "all", "3", "5", default);
        Assert.Equal((1, 5, 202, 5, "Bulk 199"), (recent.Page, recent.PageSize, recent.TotalCount, recent.Plays.Count, recent.Plays[0].Title));
        Assert.Equal("Bulk 199", drafts.Plays[0].Title);
        var progress = await service.ListAsync("progress", null, "all", null, null, default);
        Assert.Equal((1, 200, 3, 3), (progress.Page, progress.PageSize, progress.TotalCount, progress.Plays.Count));

        var page = await service.ListAsync("closed", null, null, "2", "3", default);
        Assert.Equal((2, 3, 4, 1), (page.Page, page.PageSize, page.TotalCount, page.Plays.Count));
        var clamped = await service.ListAsync("closed", null, null, "99", "3", default);
        Assert.Equal((2, page.Plays[0].Id), (clamped.Page, clamped.Plays[0].Id));
        var empty = await service.ListAsync("cancelled", "nothing matches", null, "7", null, default);
        Assert.Equal((1, 25, 0), (empty.Page, empty.PageSize, empty.TotalCount));
        Assert.Empty(empty.Plays);
        var all = await service.ListAsync("all", null, "all", "3", "100", default);
        Assert.Equal((3, 11), (all.Page, all.Plays.Count));
        Assert.Equal(211, all.TotalCount);
        Assert.Equal(new PlayCountsDto(202, 3, 4, 2, 4, 2, 211), await service.CountsAsync(null, "all", default));
        Assert.Equal(new PlayCountsDto(203, 3, 4, 2, 4, 2, 212), await service.CountsAsync(null, null, default));
    }

    [PostgresFact]
    public async Task Direction_column_follows_create_and_plan_revisions()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var created = await client.PostAsJsonAsync("/api/plays", new CreatePlayRequest(account.Id, "BTC", "venue", "Idea", ShortPlan));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        async Task<string> Stored() { await using var db = database.Context(owner); return (await db.Plays.SingleAsync()).Direction; }
        Assert.Equal("short", await Stored());
        play = (await (await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", new UpdatePlayRequest(play.Summary.Version, account.Id,
            "BTC", "venue", "Idea", TestPlays.Plan(), play.Drawings, ""))).Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.Equal("long", await Stored());
        play = (await (await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status", new { expectedVersion = play.Summary.Version, status = "planned" }))
            .Content.ReadFromJsonAsync<PlayDto>())!;
        var revised = await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", new UpdatePlayRequest(play.Summary.Version, account.Id,
            "BTC", "venue", "Idea", ShortPlan, play.Drawings, "", "Flipped the idea"));
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        Assert.Equal("short", await Stored());
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/plays?view=progress");
        Assert.Equal("short", listed.GetProperty("plays")[0].GetProperty("direction").GetString());
    }

    [PostgresFact]
    public async Task List_endpoints_have_exact_shapes_no_store_and_validation()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        await using var factory = new CoreApiFactory(seeded.Owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync("/api/plays?view=reviewed&q=&scope=unassigned&page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["plays", "view", "page", "pageSize", "totalCount"], list.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("reviewed", 1, 2, 1), (list.GetProperty("view").GetString(), list.GetProperty("page").GetInt32(),
            list.GetProperty("pageSize").GetInt32(), list.GetProperty("totalCount").GetInt32()));
        // Reviewed is the explicit completion, named on the summary with its time.
        Assert.Equal("completed", list.GetProperty("plays")[0].GetProperty("reviewCompletion").GetString());
        Assert.NotEqual(JsonValueKind.Null, list.GetProperty("plays")[0].GetProperty("reviewCompletedAtUtc").ValueKind);
        using var counts = await client.GetAsync("/api/plays/counts?q=breakout&scope=all");
        Assert.True(counts.Headers.CacheControl?.NoStore);
        var countJson = await counts.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["drafts", "progress", "review", "reviewed", "closed", "cancelled", "all"], countJson.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, countJson.GetProperty("all").GetInt32());
        foreach (var bad in new[] { "/api/plays?view=open", "/api/plays?view=Drafts", "/api/plays?page=0", "/api/plays?pageSize=101",
            "/api/plays?scope=everything", "/api/plays?q=" + new string('q', 101), "/api/plays/counts?scope=account:nope" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(bad)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plays?scope=portfolio:{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plays/counts?scope=account:{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/plays/{seeded.Plays["d1"]}")).StatusCode);
    }
}

public sealed class PlayDirectionMigrationTests
{
    [PostgresFact]
    public async Task Migration_backfills_direction_from_each_stored_plan_and_requires_it()
    {
        await using var database = await CoreDatabase.CreateAsync("20261013190344_DiaryCompletion");
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"VenueId\", \"Name\", \"SyncStatus\") VALUES ({account}, {owner}, 'manual', 'Main', 'manual')");
        var plays = new Dictionary<string, Guid> { ["short"] = Guid.NewGuid(), ["long"] = Guid.NewGuid(), [""] = Guid.NewGuid() };
        foreach (var (direction, id) in plays)
        {
            var plan = direction == "" ? "{}" : PlayDocuments.Serialize(TestPlays.Plan() with { Direction = direction });
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO plays ("Id", "OwnerId", "AccountId", "VenueId", "ContractId", "Status", "InstrumentSource", "Title", "Review", "Plan", "PlanRevision", "Version", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES ({0}, {1}, {2}, 'manual', 'BTC', 'Draft', 'Manual', 'Idea', '', {3}::jsonb, 0, 1, now(), now())
                """, id, owner, account, plan);
        }

        await db.Database.MigrateAsync();

        var stored = await db.Plays.ToDictionaryAsync(p => p.Id, p => p.Direction);
        Assert.All(plays, p => Assert.Equal(p.Key, stored[p.Value]));
        Assert.False(db.Database.HasPendingModelChanges());
        var nullable = await db.Database.SqlQueryRaw<string>(
            "SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'plays' AND column_name = 'Direction'").SingleAsync();
        Assert.Equal("NO", nullable);
    }
}
