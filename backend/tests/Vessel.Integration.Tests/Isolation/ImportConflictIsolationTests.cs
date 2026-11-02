namespace Vessel.Tests.Isolation;

/// <summary>Fill import conflicts (Phase 2, item 14c) answer each owner from its own accounts only.</summary>
public abstract class ImportConflictIsolationCases(IsolationHost api)
{
    [PostgresFact]
    [Covers("GET", "/api/accounts/{id:guid}/import-conflicts")]
    public async Task Import_conflicts_are_the_owners_and_another_owners_account_reads_as_unknown()
    {
        var (a, b) = await api.Owners();
        var aAccount = (await a.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        var bAccount = (await b.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        foreach (var (owner, account) in new[] { (a, aAccount), (b, bAccount) })
            Assert.Equal(0, (await owner.GetJson($"/api/accounts/{account}/import-conflicts")).GetProperty("total").GetInt32());
        foreach (var (caller, foreign) in new[] { (a, bAccount), (b, aAccount) })
            await Isolated.NotFound(caller, foreign, id => new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{id}/import-conflicts"),
                "Account not found.");
    }
}

/// <summary>The cases above in Token mode.</summary>
public sealed class ImportConflictIsolationTests(IsolationApi api) : ImportConflictIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode.</summary>
public sealed class WalletImportConflictIsolationTests(WalletIsolationApi api) : ImportConflictIsolationCases(api), IClassFixture<WalletIsolationApi>;
