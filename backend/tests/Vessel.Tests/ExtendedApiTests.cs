using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Credentials;
using Vessel.Application.Monitoring;
using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public sealed class ExtendedApiTests
{
    [Fact]
    public async Task Public_only_metadata_never_advertises_accounts_credentials_orders_or_paper_execution()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        using var client = factory.AuthorizedClient();
        using var response = JsonDocument.Parse(await client.GetStringAsync("/api/system"));
        var venue = response.RootElement.GetProperty("venues").EnumerateArray().Single(v => v.GetProperty("id").GetString() == "extended");
        Assert.Equal("candidate", venue.GetProperty("status").GetString());
        Assert.Equal("none", venue.GetProperty("source").GetString());
        Assert.Equal("tick-size", venue.GetProperty("priceRule").GetString());
        Assert.Equal("USD", venue.GetProperty("quoteAsset").GetString());
        Assert.Equal(JsonValueKind.Null, venue.GetProperty("credentialSetupUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, venue.GetProperty("tradeUrlTemplate").ValueKind);
        foreach (var capability in venue.GetProperty("capabilities").EnumerateObject())
            Assert.Equal(capability.Name is "instruments" or "candles", capability.Value.GetBoolean());
        Assert.Equal(["1m", "5m", "15m", "30m", "1h", "2h", "4h", "1d"],
            venue.GetProperty("intervals").EnumerateArray().Select(i => i.GetString()));
    }

    [Theory]
    [InlineData(null, null, null, "real")]
    [InlineData("123", null, null, "real")]
    [InlineData(null, "0x1111111111111111111111111111111111111111", null, "real")]
    [InlineData(null, null, "4000", "real")]
    [InlineData(null, null, null, "paper")]
    public async Task Accounts_cannot_be_created_by_guessing_an_index_address_or_manual_balance(string? source, string? address,
        string? balance, string tradingMode)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        await using var factory = new CoreApiFactory(owner, store);
        using var client = factory.AuthorizedClient();
        var response = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = "Extended unavailable", venueId = "extended", sourceId = source, address, manualAccountValueUsd = balance, tradingMode
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(store.Accounts);
    }

    [Fact]
    public async Task Synthetic_seed_can_exercise_existing_owner_scoped_market_routes_but_cannot_save_a_key()
    {
        // Not a supported creation workflow: the account exists only in this disposable in-memory fixture.
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "extended", "Synthetic seeded account");
        account.Configure(null, null, null, "123"); store.Accounts.Add(account);
        var handler = new ExtendedAdapterTests.Handler(); var reader = ExtendedAdapterTests.Reader(handler);
        var credentials = new SecurityCredentialStore();
        await using var factory = new CoreApiFactory(owner, store, reader: reader, candles: reader, configure: services =>
        {
            services.AddSingleton<IAccountCredentialStore>(credentials);
            services.AddSingleton<IServerStateStore>(new MonitorServerState());
        });
        using var client = factory.AuthorizedClient();
        var catalogue = await client.GetAsync($"/api/accounts/{account.Id}/instruments");
        Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode);
        Assert.Contains("BTC-USD", await catalogue.Content.ReadAsStringAsync());
        var candles = await client.GetAsync($"/api/accounts/{account.Id}/candles?instrument=BTC&interval=1m&endTime={ExtendedAdapterTests.Last}");
        Assert.Equal(HttpStatusCode.OK, candles.StatusCode);
        var body = await candles.Content.ReadAsStringAsync();
        Assert.Contains("Account integration is blocked", body);
        Assert.Contains("81533.5", body);
        var calls = handler.Requests.Count;
        var credential = await client.PutAsJsonAsync($"/api/accounts/{account.Id}/credential", new { token = "synthetic-do-not-store-or-echo" });
        Assert.Equal(HttpStatusCode.BadRequest, credential.StatusCode);
        Assert.DoesNotContain("synthetic-do-not-store", await credential.Content.ReadAsStringAsync());
        Assert.Null(credentials.Stored);
        Assert.Equal(calls, handler.Requests.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/accounts/{Guid.NewGuid()}/instruments")).StatusCode);
        Assert.Equal(calls, handler.Requests.Count);
        Assert.Empty(store.Snapshots); Assert.Empty(store.Fills);
    }
}
