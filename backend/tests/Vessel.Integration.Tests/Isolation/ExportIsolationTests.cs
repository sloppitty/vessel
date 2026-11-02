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
}

/// <summary>The cases above in Token mode.</summary>
public sealed class ExportIsolationTests(IsolationApi api) : ExportIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode.</summary>
public sealed class WalletExportIsolationTests(WalletIsolationApi api) : ExportIsolationCases(api), IClassFixture<WalletIsolationApi>;
