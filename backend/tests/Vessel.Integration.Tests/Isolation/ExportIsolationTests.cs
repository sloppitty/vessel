namespace Vessel.Tests.Isolation;

/// <summary>Exports answer each owner from its own Plays and facts only (Phase 2, item 5).</summary>
public abstract class ExportIsolationCases(IsolationHost api)
{
    [PostgresFact]
    [Covers("GET", "/api/review/export")]
    public async Task The_review_export_holds_only_the_owners_plays()
    {
        var (a, b) = await api.Owners();
        var titles = new Dictionary<Owner, string>();
        foreach (var owner in new[] { a, b })
        {
            var account = (await owner.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
            titles[owner] = Isolated.Marker("export");
            await owner.ClosedPlay(account, titles[owner]);
        }
        foreach (var (mine, theirs) in new[] { (a, b), (b, a) })
        {
            var csv = await mine.GetText("/api/review/export?period=all");
            Assert.Contains(titles[mine], csv);
            Assert.DoesNotContain(titles[theirs], csv);
        }
    }

    [PostgresFact]
    [Covers("GET", "/api/accounts/{id:guid}/facts")]
    public async Task Raw_facts_are_only_the_owners_and_another_owners_account_reads_as_unknown()
    {
        var (a, b) = await api.Owners();
        var aAccount = (await a.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        var bAccount = (await b.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        foreach (var (owner, account) in new[] { (a, aAccount), (b, bAccount) })
        {
            // Both follow the shared synthetic address, so each owner's own copy of the same fills is exported.
            var csv = await owner.GetText($"/api/accounts/{account}/facts?kind=executions");
            Assert.Contains("entry-fill", csv);
            Assert.StartsWith("provenance,", csv);
        }
        foreach (var (caller, foreign) in new[] { (a, bAccount), (b, aAccount) })
            await Isolated.NotFound(caller, foreign, id => new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{id}/facts?kind=funding"),
                "Account not found.");
    }

    [PostgresFact]
    [Covers("GET", "/api/plays/{id:guid}/bundle")]
    public async Task A_play_bundle_is_only_the_owners()
    {
        var (a, b) = await api.Owners();
        var plays = new Dictionary<Owner, Guid>();
        foreach (var owner in new[] { a, b })
        {
            var account = (await owner.ManualAccount(Isolated.Marker("bundle"))).GetProperty("id").GetGuid();
            var draft = await owner.Post("/api/plays", new Vessel.Application.Plays.CreatePlayRequest(account, "BTC", "manual",
                Isolated.Marker("bundle-play"), TestPlays.Plan()), System.Net.HttpStatusCode.Created);
            plays[owner] = draft.GetProperty("summary").GetProperty("id").GetGuid();
        }
        foreach (var (caller, foreign) in new[] { (a, plays[b]), (b, plays[a]) })
            await Isolated.NotFound(caller, foreign, id => new HttpRequestMessage(HttpMethod.Get, $"/api/plays/{id}/bundle"), "Play not found.");
        using var mine = await a.Http.GetAsync($"/api/plays/{plays[a]}/bundle");
        Assert.Equal(System.Net.HttpStatusCode.OK, mine.StatusCode);
    }
}

/// <summary>The cases above in Token mode.</summary>
public sealed class ExportIsolationTests(IsolationApi api) : ExportIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode.</summary>
public sealed class WalletExportIsolationTests(WalletIsolationApi api) : ExportIsolationCases(api), IClassFixture<WalletIsolationApi>;
