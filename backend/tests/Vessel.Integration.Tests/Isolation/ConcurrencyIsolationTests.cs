using Microsoft.EntityFrameworkCore;

namespace Vessel.Tests.Isolation;

/// <summary>Concurrent requests for two owners on one server never take each other's identity or data.</summary>
public abstract class ConcurrencyIsolationCases(IsolationHost api)
{
    private const int Rounds = 12;

    [PostgresFact]
    public async Task Concurrent_requests_of_two_owners_never_cross()
    {
        var (a, b) = await api.Owners();
        var owners = new[] { a, b };
        // Interleaved writes and reads, all in flight together.
        var work = owners.SelectMany(owner => Enumerable.Range(0, Rounds).Select(async round =>
        {
            var system = await owner.GetJson("/api/system");
            Assert.Equal(owner.Id, system.GetProperty("owner").GetProperty("id").GetGuid());
            var name = $"{owner.Id:N}-{round}";
            var portfolio = await owner.Portfolio(name);
            var account = (await owner.ManualAccount($"{owner.Id:N}-account-{round}", portfolio)).GetProperty("id").GetGuid();
            Assert.Equal(name, (await owner.GetJson($"/api/portfolios/{portfolio}")).GetProperty("name").GetString());
            var other = owner == a ? b : a;
            foreach (var path in new[] { "/api/portfolios", "/api/accounts", "/api/overview" })
                Assert.DoesNotContain(other.Id.ToString("N"), await owner.GetText(path), StringComparison.OrdinalIgnoreCase);
            return (owner, portfolio, account);
        })).ToList();
        var created = await Task.WhenAll(work);

        foreach (var owner in owners)
        {
            var mine = created.Where(c => c.owner == owner).ToList();
            var portfolios = (await owner.GetJson("/api/portfolios")).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToHashSet();
            var accounts = (await owner.GetJson("/api/accounts")).EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
            Assert.Equal(mine.Select(c => c.portfolio).ToHashSet(), portfolios);
            Assert.Equal(mine.Select(c => c.account).ToHashSet(), accounts);
            await using var db = api.Database.Context(owner.Id);
            Assert.All(await db.Portfolios.IgnoreQueryFilters().Where(p => portfolios.Contains(p.Id)).ToListAsync(),
                p => Assert.Equal(owner.Id, p.OwnerId));
            Assert.All(await db.Accounts.IgnoreQueryFilters().Where(x => accounts.Contains(x.Id)).ToListAsync(),
                x => Assert.Equal(owner.Id, x.OwnerId));
        }
    }

    [PostgresFact]
    public async Task Concurrent_refreshes_of_the_same_source_keep_one_copy_per_owner()
    {
        var (a, b) = await api.Owners();
        // Both owners import and refresh the same native address at the same time.
        var synced = await Task.WhenAll(new[] { a, b }.Select(async owner =>
            (owner, account: (await owner.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid())));
        foreach (var (owner, account) in synced)
        {
            await using var db = api.Database.Context(owner.Id);
            var fills = await db.Fills.IgnoreQueryFilters().Where(f => f.AccountId == account).ToListAsync();
            Assert.Equal(["entry-fill", "exit-fill"], fills.Select(f => f.SourceFillId).Order());
            Assert.All(fills, f => Assert.Equal(owner.Id, f.OwnerId));
            var activity = await owner.GetJson("/api/activity");
            Assert.Equal(account, Assert.Single(activity.GetProperty("operations").EnumerateArray()).GetProperty("accountId").GetGuid());
        }
        // Concurrent reads of the two owners' read models answer each from its own copy.
        var reads = await Task.WhenAll(synced.SelectMany(s => Enumerable.Range(0, 6).Select(async _ =>
            (s.owner, s.account, body: await s.owner.GetText("/api/overview/summary?groupBy=account&period=all")))));
        foreach (var (owner, account, body) in reads)
        {
            Assert.Contains(account.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            Isolated.NoLeak(body, synced.Single(s => s.owner != owner).account);
        }
    }
}

/// <summary>The cases above with the suite's test scheme selecting the owner.</summary>
public sealed class ConcurrencyIsolationTests(IsolationApi api) : ConcurrencyIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases through wallet sessions: two signed-in users, each request mapped to its owner by the session scheme.</summary>
public sealed class WalletConcurrencyIsolationTests(WalletIsolationApi api) : ConcurrencyIsolationCases(api), IClassFixture<WalletIsolationApi>;
