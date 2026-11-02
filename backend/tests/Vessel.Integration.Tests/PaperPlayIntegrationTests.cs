using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Paper;
using Vessel.Domain.Evidence;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class PaperPlayIntegrationTests : PaperPlayTestsValues
{

    [PostgresFact]
    public async Task Simulated_model_provenance_is_defaulted_and_append_only_in_the_database()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
        var account = PaperAccount(owner, portfolio.Id);
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        var minute = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        var planned = play.MarkPlanned(minute);
        var execution = new PaperExecution
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, PlayId = play.Id,
            EntryId = "entry-1", Role = "entry", Price = 100, Quantity = 1, MarginUsd = 100,
            PlanVersion = 1, CandleOpenAtUtc = minute, CandleCloseAtUtc = minute.AddMinutes(1),
            OccurredAtUtc = minute.AddMinutes(1)
        };
        await using (var db = database.Context(owner))
        {
            db.Portfolios.Add(portfolio);
            db.Accounts.Add(account);
            db.Plays.Add(play);
            db.PlanRevisions.Add(planned.Revision);
            db.StatusChanges.Add(planned.Change);
            db.PaperLedgerEntries.Add(PaperLedgerEntry.Seed(owner, account.Id, 4000, minute));
            db.PaperExecutions.Add(execution);
            await db.SaveChangesAsync();
        }
        await using var check = database.Context(owner);
        Assert.Equal(1, (await check.PaperExecutions.AsNoTracking().SingleAsync()).ModelVersion);
        await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE paper_executions SET \"ModelVersion\"=2 WHERE \"OwnerId\"={owner} AND \"Id\"={execution.Id}"));
        Assert.Equal(1, (await check.PaperExecutions.AsNoTracking().SingleAsync()).ModelVersion);
    }

    [PostgresFact]
    public async Task Api_saved_paper_play_is_mode_scoped_and_never_exposes_actual_linking()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
        var account = PaperAccount(owner, portfolio.Id);
        var real = new Account(Guid.NewGuid(), owner, "hyperliquid", "Real");
        var realPlay = TestPlays.Create(real, new("hyperliquid", "BTC"));
        await using (var db = database.Context(owner))
        {
            db.Portfolios.Add(portfolio);
            db.Accounts.Add(account);
            db.Accounts.Add(real);
            db.Plays.Add(realPlay);
            db.PaperLedgerEntries.Add(PaperLedgerEntry.Seed(owner, account.Id, 4000, DateTimeOffset.UnixEpoch));
            await db.SaveChangesAsync();
        }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/accounts/{real.Id}?tradingMode=paper")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/accounts/{account.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/accounts/{real.Id}/credential?tradingMode=paper")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/accounts/{account.Id}/credential?tradingMode=paper")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/accounts/{account.Id}/sync?tradingMode=paper", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/API/PlAyS/{realPlay.Id}?tradingMode=paper",
            new UpdatePlayRequest(realPlay.Version, real.Id, "BTC", "venue", "Cross-mode bypass", TestPlays.Plan(),
                System.Text.Json.JsonSerializer.SerializeToElement(new { }), ""))).StatusCode);
        var request = new CreatePlayRequest(account.Id, "BTC", "venue", "Paper hypothesis", TestPlays.Plan());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/plays", request)).StatusCode);
        var created = await client.PostAsJsonAsync("/api/plays?tradingMode=paper", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.Equal("paper", play.Summary.TradingMode);
        Assert.Single((await client.GetFromJsonAsync<PlayListDto>("/api/plays"))!.Plays);
        Assert.Single((await client.GetFromJsonAsync<PlayListDto>("/api/plays?tradingMode=paper"))!.Plays);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plays/{play.Summary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/plays/{play.Summary.Id}?tradingMode=paper")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/API/PlAyS/{play.Summary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/API/PlAyS?tradingMode=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/plays/{play.Summary.Id}/execution?tradingMode=paper")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/plays/{play.Summary.Id}/manual-executions?tradingMode=paper")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plays/{play.Summary.Id}/evidence")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/plays/{play.Summary.Id}/evidence?tradingMode=paper")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/API/PlAyS/{play.Summary.Id}/eViDeNcE")).StatusCode);
        var evidenceId = Guid.NewGuid();
        await using (var evidenceDb = database.Context(owner))
        {
            var saved = await evidenceDb.Plays.SingleAsync(p => p.Id == play.Summary.Id);
            evidenceDb.Evidence.Add(new PlayEvidence(evidenceId, saved, "paper-test-only", "image/png", 1,
                new string('0', 64), EvidenceSource.Upload, "", DateTimeOffset.UtcNow));
            await evidenceDb.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/API/EvIdEnCe/{evidenceId}", new { note = "wrong-mode" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/API/EvIdEnCe/{evidenceId}?tradingMode=paper", new { note = "paper-only" })).StatusCode);
        var disabledPlan = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status?tradingMode=paper",
            new ChangePlayStatusRequest(play.Summary.Version, "planned"));
        Assert.Equal(HttpStatusCode.Conflict, disabledPlan.StatusCode);
        await using var verify = database.Context(owner);
        Assert.Equal(PlayStatus.Draft, (await verify.Plays.SingleAsync(p => p.Id == play.Summary.Id)).Status);
        Assert.Equal("", (await verify.Plays.SingleAsync(p => p.Id == realPlay.Id)).Title);
        Assert.Empty(await verify.Fills.ToListAsync());
        Assert.Empty(await verify.Orders.ToListAsync());
        Assert.Empty(await verify.ManualExecutions.ToListAsync());
        var beforeVersion = (await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/activity"))
            .GetProperty("version").GetString();
        var disabled = await client.PutAsJsonAsync($"/api/accounts/{account.Id}?tradingMode=paper",
            new Vessel.Application.Workspace.UpdateAccountRequest("Renamed paper", portfolio.Id, false, 1));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var afterVersion = (await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/activity"))
            .GetProperty("version").GetString();
        Assert.Equal(beforeVersion, afterVersion);
    }

    [PostgresFact]
    public async Task Saved_paper_draft_can_move_before_arming_but_database_execution_membership_stays_fixed()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
        var first = PaperAccount(owner, portfolio.Id);
        var second = PaperAccount(owner, portfolio.Id);
        await using (var seed = database.Context(owner))
        {
            seed.AddRange(portfolio, first, second,
                PaperLedgerEntry.Seed(owner, first.Id, 4000, DateTimeOffset.UtcNow),
                PaperLedgerEntry.Seed(owner, second.Id, 4000, DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString,
            settings: new() { ["Vessel:Auth:OwnerId"] = owner.ToString() });
        using var client = factory.AuthorizedClient();
        var created = await client.PostAsJsonAsync("/api/plays?tradingMode=paper",
            new CreatePlayRequest(first.Id, "BTC", "venue", "Movable hypothesis", TestPlays.Plan()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        var update = new UpdatePlayRequest(play.Summary.Version, second.Id, "BTC", "venue",
            play.Summary.Title, play.Plan, play.Drawings, play.Review);
        var moved = await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}?tradingMode=paper", update);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        play = (await moved.Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.Equal(second.Id, play.Summary.AccountId);
        await using (var check = database.Context(owner))
        {
            Assert.Empty(await check.PaperSimulations.ToListAsync());
            Assert.Empty(await check.PaperReservations.ToListAsync());
            Assert.Empty(await check.PaperExecutions.ToListAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/paper/settings",
            new { enabled = true, checkIntervalMinutes = 60 })).StatusCode);
        var planned = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status?tradingMode=paper",
            new ChangePlayStatusRequest(play.Summary.Version, "planned"));
        Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
        play = (await planned.Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}?tradingMode=paper",
            update with { ExpectedVersion = play.Summary.Version, AccountId = first.Id })).StatusCode);
        await using var verify = database.Context(owner);
        Assert.Equal(second.Id, (await verify.PaperSimulations.SingleAsync()).AccountId);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE plays SET \"AccountId\"={first.Id} WHERE \"OwnerId\"={owner} AND \"Id\"={play.Summary.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }
}
