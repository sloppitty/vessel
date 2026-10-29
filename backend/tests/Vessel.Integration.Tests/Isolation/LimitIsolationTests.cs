using System.Net;
using System.Net.Http.Json;
using Vessel.Application.Workspace;

namespace Vessel.Tests.Isolation;

/// <summary>
/// Contract §9.4 per-user caps with two signed-in users: one user exhausting a budget never refuses the other.
/// Budgets and stream slots live in the API process, not the journal, so they outlast the owners of earlier cases on
/// this host; each case uses fresh users, and the class has its own host so no other case holds a slot. Server-wide
/// venue capacity and deadlines are shared by design and are not asserted here.
/// </summary>
public sealed class LimitIsolationTests(WalletIsolationApi api) : IClassFixture<WalletIsolationApi>
{
    private static async Task Limited(HttpResponseMessage response, int seconds)
    {
        await Isolated.Problem(response, HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.Equal(TimeSpan.FromSeconds(seconds), response.Headers.RetryAfter?.Delta);
    }

    private static async Task<Guid> VenueAccount(Owner owner) =>
        (await owner.Post("/api/accounts", new CreateAccountRequest(null, Isolated.Marker("venue"), "hyperliquid", IsolationVenues.Address),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();

    [PostgresFact]
    public async Task Candle_refresh_budget_is_per_user_for_the_same_public_series()
    {
        var (a, b) = await api.Owners();
        var aAccount = await VenueAccount(a);
        var bAccount = await VenueAccount(b);
        string Refresh(Guid account, string interval = "1h") =>
            $"/api/accounts/{account}/candles?instrument=BTC&interval={interval}&refresh=true";

        // A spends its one manual refresh of hyperliquid BTC 1h for the next 60 s.
        await a.GetJson(Refresh(aAccount));
        await Limited(await a.Http.GetAsync(Refresh(aAccount)), 60);
        // B refreshes the very same public series through its own account: B's budget is untouched by A's.
        await b.GetJson(Refresh(bAccount));
        await Limited(await b.Http.GetAsync(Refresh(bAccount)), 60);
        await Limited(await a.Http.GetAsync(Refresh(aAccount)), 60);
        // Positive controls: plain reads and another series are not part of either refusal.
        await a.GetJson($"/api/accounts/{aAccount}/candles?instrument=BTC&interval=1h");
        await a.GetJson(Refresh(aAccount, "1m"));
    }

    [PostgresFact]
    public async Task Account_discovery_budget_is_per_user()
    {
        var (a, b) = await api.Owners();
        var query = $"/api/venues/{SecurityFixture.Venue.Id}/accounts?address={IsolationVenues.Address}";
        for (var i = 0; i < 10; i++) await a.GetJson(query);
        await Limited(await a.Http.GetAsync(query), 60);
        // B's ten discoveries in the same minute are all admitted, then B alone is refused.
        for (var i = 0; i < 10; i++) await b.GetJson(query);
        await Limited(await b.Http.GetAsync(query), 60);
        await Limited(await a.Http.PostAsJsonAsync($"/api/venues/{SecurityFixture.Venue.Id}/accounts/credential",
            new { address = IsolationVenues.Address, token = SecurityFixture.Token }), 60);
    }

    [PostgresFact]
    public async Task Credential_verification_budget_is_per_user()
    {
        var (a, b) = await api.Owners();
        var aAccount = (await a.IndexAccount(Isolated.Marker("index-a"))).GetProperty("id").GetGuid();
        var bAccount = (await b.IndexAccount(Isolated.Marker("index-b"))).GetProperty("id").GetGuid();
        var credential = new { token = SecurityFixture.Token };
        for (var i = 0; i < 30; i++) await a.Send(HttpMethod.Put, $"/api/accounts/{aAccount}/credential", credential);
        var state = await Snapshot.Of(a, $"/api/accounts/{aAccount}/credential", $"/api/accounts/{aAccount}");
        await state.After(async () => await Limited(await a.Http.PutAsJsonAsync($"/api/accounts/{aAccount}/credential", credential), 60));
        // B verifies and stores its own credential while A is refused; A's stored credential is unchanged.
        var saved = await state.After(() => b.Send(HttpMethod.Put, $"/api/accounts/{bAccount}/credential", credential));
        Assert.Equal("valid", saved.GetProperty("credential").GetProperty("status").GetString());
        await state.After(async () => await Limited(await a.Http.PutAsJsonAsync($"/api/accounts/{aAccount}/credential", credential), 60));
    }

    [PostgresFact]
    public async Task Market_stream_cap_is_per_user()
    {
        var (a, b) = await api.Owners();
        var aAccount = await VenueAccount(a);
        var bAccount = await VenueAccount(b);
        string Stream(Guid account) => $"/api/accounts/{account}/market-stream?instrument=BTC&interval=1h";
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var open = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var response = await a.Http.SendAsync(new(HttpMethod.Get, Stream(aAccount)), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                open.Add(response);
            }
            var subscriptions = api.Venues.Stream.Subscriptions.Count;
            // A's fifth is refused before it subscribes; B, below the process cap, still opens its own.
            await Limited(await a.Http.GetAsync(Stream(aAccount), cancel.Token), 5);
            Assert.Equal(subscriptions, api.Venues.Stream.Subscriptions.Count);
            var other = await b.Http.SendAsync(new(HttpMethod.Get, Stream(bAccount)), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            open.Add(other);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }
        finally
        {
            foreach (var response in open) response.Dispose();
        }
    }
}
