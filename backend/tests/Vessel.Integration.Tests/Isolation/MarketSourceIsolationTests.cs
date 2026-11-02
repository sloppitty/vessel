using System.Net;
using Vessel.Application.MarketSources;

namespace Vessel.Tests.Isolation;

/// <summary>An owner's hidden market sources are their own, and hiding one never touches what the catalogue returns.</summary>
public abstract class MarketSourceIsolationCases(IsolationHost api)
{
    [PostgresFact]
    [Covers("GET", "/api/settings/market-sources")]
    [Covers("PUT", "/api/settings/market-sources/{venueId}/{sourceId}")]
    public async Task Hidden_sources_stay_with_their_owner_and_only_filter_the_selector()
    {
        var (a, b) = await api.Owners();
        var mine = (await a.Post("/api/accounts", new Vessel.Application.Workspace.CreateAccountRequest(null, Isolated.Marker("sources"), "hyperliquid", IsolationVenues.Address),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var theirs = (await b.Post("/api/accounts", new Vessel.Application.Workspace.CreateAccountRequest(null, Isolated.Marker("sources"), "hyperliquid", IsolationVenues.Address),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Empty((await a.GetJson($"/api/accounts/{mine}/instruments")).GetProperty("disabledSources").EnumerateArray());

        await a.Send(HttpMethod.Put, "/api/settings/market-sources/hyperliquid/xyz", new UpdateMarketSourceRequest(false), HttpStatusCode.NoContent);
        // Repeating a toggle is harmless.
        await a.Send(HttpMethod.Put, "/api/settings/market-sources/hyperliquid/xyz", new UpdateMarketSourceRequest(false), HttpStatusCode.NoContent);

        var hidden = await a.GetJson($"/api/accounts/{mine}/instruments");
        Assert.Equal(["xyz"], hidden.GetProperty("disabledSources").EnumerateArray().Select(s => s.GetString()));
        Assert.NotEmpty(hidden.GetProperty("instruments").EnumerateArray());
        Assert.Empty((await b.GetJson($"/api/accounts/{theirs}/instruments")).GetProperty("disabledSources").EnumerateArray());

        await a.Send(HttpMethod.Put, "/api/settings/market-sources/hyperliquid/xyz", new UpdateMarketSourceRequest(true), HttpStatusCode.NoContent);
        Assert.Empty((await a.GetJson($"/api/accounts/{mine}/instruments")).GetProperty("disabledSources").EnumerateArray());

        await a.Send(HttpMethod.Put, "/api/settings/market-sources/hyperliquid/NOT%20VALID", new UpdateMarketSourceRequest(false), HttpStatusCode.BadRequest);
        await a.Send(HttpMethod.Put, "/api/settings/market-sources/manual/xyz", new UpdateMarketSourceRequest(false), HttpStatusCode.NotFound);
        Assert.Contains((await a.GetJson("/api/settings/market-sources")).EnumerateArray(), venue => venue.GetProperty("venueId").GetString() == "hyperliquid");
    }
}

/// <summary>The cases above in Token mode.</summary>
public sealed class MarketSourceIsolationTests(IsolationApi api) : MarketSourceIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode.</summary>
public sealed class WalletMarketSourceIsolationTests(WalletIsolationApi api) : MarketSourceIsolationCases(api), IClassFixture<WalletIsolationApi>;
