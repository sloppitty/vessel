using System.Net;
using Vessel.Application.Workspace;

namespace Vessel.Tests.Isolation;

/// <summary>
/// Phase 2, item 6: the "Keep execution history" opt-in and the coverage ledger answer each owner from its own accounts
/// only; only a real venue account can keep its history.
/// </summary>
public abstract class RetentionIsolationCases(IsolationHost api)
{
    [PostgresFact]
    [Covers("GET", "/api/accounts/{id:guid}/history-coverage")]
    public async Task History_coverage_is_the_owners_and_only_a_venue_account_keeps_its_history()
    {
        var (a, b) = await api.Owners();
        var aAccount = (await a.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        var bAccount = (await b.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        foreach (var (owner, account) in new[] { (a, aAccount), (b, bAccount) })
        {
            var coverage = await owner.GetJson($"/api/accounts/{account}/history-coverage");
            // The Refresh that synced the account recorded its import; nobody has opted in yet.
            Assert.Equal((false, true, "refresh"), (coverage.GetProperty("keepExecutionHistory").GetBoolean(),
                coverage.GetProperty("available").GetBoolean(), coverage.GetProperty("recent")[0].GetProperty("source").GetString()));
        }

        var current = await a.GetJson($"/api/accounts/{aAccount}");
        var kept = await a.Send(HttpMethod.Put, $"/api/accounts/{aAccount}", new UpdateAccountRequest(current.GetProperty("name").GetString()!, null, true,
            current.GetProperty("settingsRevision").GetInt64(), KeepExecutionHistory: true));
        Assert.True(kept.GetProperty("keepExecutionHistory").GetBoolean());
        Assert.True((await a.GetJson($"/api/accounts/{aAccount}/history-coverage")).GetProperty("keepExecutionHistory").GetBoolean());
        // Leaving it out keeps it.
        var renamed = await a.Send(HttpMethod.Put, $"/api/accounts/{aAccount}", new UpdateAccountRequest("Renamed", null, true,
            kept.GetProperty("settingsRevision").GetInt64()));
        Assert.True(renamed.GetProperty("keepExecutionHistory").GetBoolean());
        Assert.False((await b.GetJson($"/api/accounts/{bAccount}")).GetProperty("keepExecutionHistory").GetBoolean());

        // A manual record has no venue history to keep.
        var manual = (await a.ManualAccount(Isolated.Marker("manual"))).GetProperty("id").GetGuid();
        var manualNow = await a.GetJson($"/api/accounts/{manual}");
        await a.Send(HttpMethod.Put, $"/api/accounts/{manual}", new UpdateAccountRequest(manualNow.GetProperty("name").GetString()!, null, true,
            manualNow.GetProperty("settingsRevision").GetInt64(), KeepExecutionHistory: true), HttpStatusCode.BadRequest);

        foreach (var (caller, foreign) in new[] { (a, bAccount), (b, aAccount) })
            await Isolated.NotFound(caller, foreign, id => new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{id}/history-coverage"),
                "Account not found.");
    }

    [PostgresFact]
    [Covers("POST", "/api/accounts/{id:guid}/history-gaps/{gapId:guid}/backfill")]
    public async Task A_backfill_starts_only_on_the_owners_own_account_and_gap()
    {
        var (a, b) = await api.Owners();
        var aAccount = (await a.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        var bAccount = (await b.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        Assert.True((await a.GetJson($"/api/accounts/{aAccount}/history-coverage")).GetProperty("backfillAvailable").GetBoolean());
        // A gap that is not the account's is not found; another owner's account is not found either.
        var unknown = Guid.NewGuid();
        await a.Post($"/api/accounts/{aAccount}/history-gaps/{unknown}/backfill", expected: HttpStatusCode.NotFound);
        foreach (var (caller, foreign) in new[] { (a, bAccount), (b, aAccount) })
            await Isolated.NotFound(caller, foreign, id => new HttpRequestMessage(HttpMethod.Post, $"/api/accounts/{id}/history-gaps/{unknown}/backfill"),
                "Account not found.");
    }
}

/// <summary>The cases above in Token mode.</summary>
public sealed class RetentionIsolationTests(IsolationApi api) : RetentionIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode.</summary>
public sealed class WalletRetentionIsolationTests(WalletIsolationApi api) : RetentionIsolationCases(api), IClassFixture<WalletIsolationApi>;
