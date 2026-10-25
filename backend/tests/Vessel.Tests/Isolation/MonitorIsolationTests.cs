using System.Net;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Plays;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests.Isolation;

/// <summary>Monitor state, manual checks and discrepancy dismissal across two owners.</summary>
public abstract class MonitorIsolationCases(IsolationHost api)
{
    /// <summary>
    /// Seeds the existing discrepancy fixture (its Open Play and linked orders) into this API's database and confirms an
    /// instrument discrepancy for it, as two monitor passes would. Returns the fixture and the discrepancy ID.
    /// </summary>
    private async Task<(DiscrepancyFixture Fixture, Guid Discrepancy)> Confirmed()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        var connection = api.Database.ConnectionString;
        await using (var seed = new DiscrepancyDatabaseScope(connection, f, f.Account.OwnerId))
        {
            seed.Db.Add(f.Account); seed.Db.AddRange(f.Store.Plays); seed.Db.AddRange(f.Store.Links);
            seed.Db.AddRange(f.Store.Orders); seed.Db.AddRange(f.Store.Fills);
            await seed.Db.SaveChangesAsync();
        }
        await using (var candidate = new DiscrepancyDatabaseScope(connection, f, f.Account.OwnerId)) await candidate.Evaluate(f.Observation());
        f.Advance(60);
        await using (var confirmed = new DiscrepancyDatabaseScope(connection, f, f.Account.OwnerId)) await confirmed.Evaluate(f.Observation());
        await using var read = new DiscrepancyDatabaseScope(connection, f, f.Account.OwnerId);
        return (f, (await read.Db.Set<PlayDiscrepancy>().SingleAsync(d => d.AccountId == f.Account.Id)).Id);
    }

    [PostgresFact]
    [Covers("POST", "/api/accounts/{id:guid}/discrepancies/{discrepancyId:guid}/dismiss")]
    [Covers("POST", "/api/plays/{id:guid}/discrepancies/{discrepancyId:guid}/dismiss")]
    public async Task Discrepancies_of_another_owner_cannot_be_dismissed_by_any_route()
    {
        var (f, discrepancy) = await Confirmed();
        var a = await api.Owner(f.Account.OwnerId);
        var account = f.Account.Id;
        var play = f.Play.Id;
        var monitor = await a.GetJson($"/api/accounts/{account}/monitor");
        Assert.Equal(discrepancy, Assert.Single(monitor.GetProperty("discrepancies").EnumerateArray()).GetProperty("id").GetGuid());
        var paths = new[] { $"/api/accounts/{account}/monitor", $"/api/plays/{play}/execution", "/api/events" };
        var state = await Snapshot.Of(a, paths);
        // After each attempt A's views and its stored discrepancy are unchanged.
        async Task Attempt(Func<Task> attempt)
        {
            await state.After(attempt);
            await using var read = api.Database.Context(a.Id);
            Assert.NotEqual("dismissed", (await read.Set<PlayDiscrepancy>().SingleAsync(d => d.Id == discrepancy)).State);
        }

        // B has its own discrepancy to dismiss, so every ID it holds is valid for itself.
        var (g, own) = await Confirmed();
        var b = await api.Owner(g.Account.OwnerId);
        var note = new { note = "dismissed by b" };
        await Attempt(() => Isolated.NotFound(b, account, Isolated.Request(HttpMethod.Post, id => $"/api/accounts/{id}/discrepancies/{discrepancy}/dismiss", _ => note)));
        await Attempt(() => Isolated.NotFound(b, play, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/discrepancies/{discrepancy}/dismiss", _ => note)));
        // A's discrepancy ID under B's own account and Play.
        await Attempt(() => Isolated.NotFound(b, discrepancy, Isolated.Request(HttpMethod.Post, id => $"/api/accounts/{g.Account.Id}/discrepancies/{id}/dismiss", _ => note)));
        await Attempt(() => Isolated.NotFound(b, discrepancy, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{g.Play.Id}/discrepancies/{id}/dismiss", _ => note)));
        // B's discrepancy ID under A's account and Play.
        await Attempt(() => Isolated.NotFound(b, account, Isolated.Request(HttpMethod.Post, id => $"/api/accounts/{id}/discrepancies/{own}/dismiss", _ => note)));
        await Attempt(() => Isolated.NotFound(b, play, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/discrepancies/{own}/dismiss", _ => note)));

        // Each owner dismisses its own.
        await Attempt(() => b.Send(HttpMethod.Post, $"/api/plays/{g.Play.Id}/discrepancies/{own}/dismiss", note, HttpStatusCode.NoContent));
        await a.Send(HttpMethod.Post, $"/api/accounts/{account}/discrepancies/{discrepancy}/dismiss", new { note = "dismissed by a" }, HttpStatusCode.NoContent);
        Isolated.NoLeak(await b.GetText("/api/events"), account, play, discrepancy, "dismissed by a");
        Isolated.NoLeak(await a.GetText("/api/events"), g.Account.Id, g.Play.Id, own, "dismissed by b");
    }

    private static async Task Planned(Owner owner, Guid account)
    {
        var draft = await owner.Post("/api/plays", new CreatePlayRequest(account, "BTC", "venue", Isolated.Marker("monitored"), TestPlays.Plan()),
            HttpStatusCode.Created);
        await owner.Post($"/api/plays/{draft.GetProperty("summary").GetProperty("id").GetGuid()}/status",
            new ChangePlayStatusRequest(draft.GetProperty("summary").GetProperty("version").GetInt64(), "planned"));
    }

    [PostgresFact]
    [Covers("GET", "/api/monitor")]
    [Covers("GET", "/api/accounts/{id:guid}/monitor")]
    [Covers("POST", "/api/accounts/{id:guid}/monitor/run")]
    public async Task Monitor_state_and_manual_checks_of_another_owner_answer_like_unknown_ones()
    {
        var (a, b) = await api.Owners();
        var account = (await a.SyncedVenueAccount(Isolated.Marker("venue-a"))).GetProperty("id").GetGuid();
        var own = (await b.SyncedVenueAccount(Isolated.Marker("venue-b"))).GetProperty("id").GetGuid();
        // The summary lists monitored accounts: those with an active Play.
        await Planned(a, account);
        await Planned(b, own);
        Assert.Equal(account, (await a.GetJson($"/api/accounts/{account}/monitor")).GetProperty("accountId").GetGuid());

        // Past the cooldown each refresh set, before any attempt: a foreign request that reached A's account would be
        // admitted, not hidden behind a cooldown that only the positive controls below have escaped.
        var now = api.Clock.Now;
        api.Clock.Now = now.AddMinutes(2);
        // A worker is alive at that clock, so the positive controls are admitted rather than refused as offline.
        await api.MonitorOnline();
        try
        {
            var state = await Snapshot.Of(a, $"/api/accounts/{account}/monitor", "/api/monitor");
            await state.After(() => Isolated.NotFound(b, account, Isolated.Request(HttpMethod.Get, id => $"/api/accounts/{id}/monitor")));
            // No manual request is admitted for A's account.
            await state.After(() => Isolated.NotFound(b, account, Isolated.Request(HttpMethod.Post, id => $"/api/accounts/{id}/monitor/run")));
            Assert.Equal([own], (await b.GetJson("/api/monitor")).GetProperty("accounts").EnumerateArray().Select(m => m.GetProperty("accountId").GetGuid()));
            Assert.Equal([account], (await a.GetJson("/api/monitor")).GetProperty("accounts").EnumerateArray().Select(m => m.GetProperty("accountId").GetGuid()));

            // B's own request, at the same clock, is admitted and pending for B only; A's own request is still admitted.
            var accepted = await state.After(() => b.Post($"/api/accounts/{own}/monitor/run", expected: HttpStatusCode.Accepted));
            Assert.Equal("pending", accepted.GetProperty("monitor").GetProperty("manualState").GetString());
            var mine = await a.Post($"/api/accounts/{account}/monitor/run", expected: HttpStatusCode.Accepted);
            Assert.Equal(("pending", account), (mine.GetProperty("monitor").GetProperty("manualState").GetString(),
                mine.GetProperty("monitor").GetProperty("accountId").GetGuid()));
        }
        finally { api.Clock.Now = now; }
    }
}

/// <summary>The cases above with the suite's test scheme selecting the owner.</summary>
public sealed class MonitorIsolationTests(IsolationApi api) : MonitorIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases through wallet sessions: two signed-in users, each request mapped to its owner by the session scheme.</summary>
public sealed class WalletMonitorIsolationTests(WalletIsolationApi api) : MonitorIsolationCases(api), IClassFixture<WalletIsolationApi>;
