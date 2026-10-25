using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class InstrumentCatalogueApiTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("invalid-token")]
    public async Task Instrument_catalogue_requires_a_session_before_venue_read(string? token)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "HL"); store.Accounts.Add(account);
        var reader = new FixtureReader();
        await using var factory = new CoreApiFactory(owner, store, reader: reader);
        using var client = factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync($"/api/accounts/{account.Id}/instruments");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, reader.InstrumentReads);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("foreign", HttpStatusCode.NotFound)]
    [InlineData("foreign-disabled", HttpStatusCode.NotFound)]
    [InlineData("disabled", HttpStatusCode.Conflict)]
    [InlineData("disabled-manual", HttpStatusCode.Conflict)]
    public async Task Instrument_catalogue_honors_owner_and_enabled_guards_without_venue_read(string scenario, HttpStatusCode status)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), scenario.StartsWith("foreign", StringComparison.Ordinal) ? Guid.NewGuid() : owner,
            scenario == "disabled-manual" ? "manual" : "hyperliquid", "Private foreign account");
        if (scenario.Contains("disabled", StringComparison.Ordinal)) account.UpdateSettings(account.Name, null, false);
        if (scenario != "missing") store.Accounts.Add(account);
        var reader = new FixtureReader { Fail = true };
        await using var factory = new CoreApiFactory(owner, store, reader: reader);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync($"/api/accounts/{account.Id}/instruments");
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Private foreign account", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, reader.InstrumentReads);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task Manual_instrument_catalogue_uses_shared_shape_and_never_reads_a_venue()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Manual"); store.Accounts.Add(account);
        var reader = new FixtureReader { Fail = true };
        await using var factory = new CoreApiFactory(owner, store, reader: reader);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync($"/api/accounts/{account.Id}/instruments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "venueId", "marketScope", "scope", "instruments", "notice" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        var result = json.RootElement.Deserialize<AccountInstrumentsDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("manual", result.VenueId);
        Assert.Equal("perpetuals", result.MarketScope);
        Assert.Equal("manual", result.Scope);
        Assert.Empty(result.Instruments);
        Assert.Contains("Manual catalogue", result.Notice);
        Assert.Equal(0, reader.InstrumentReads);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task Instrument_catalogue_returns_exact_shape_from_meta_only_without_refreshing_account()
    {
        const string meta = """
            {"universe":[
              {"name":"MiXeD","szDecimals":5,"maxLeverage":40},
              {"name":"OLD","szDecimals":4,"maxLeverage":20,"isDelisted":true}
            ]}
            """;
        var owner = Guid.NewGuid();
        var store = new MemoryWorkspaceStore(owner) { RejectOwnerWideReads = true, RejectActivityReads = true };
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "HL");
        account.Configure(null, "0x1111111111111111111111111111111111111111", null);
        store.Accounts.Add(account);
        using var handler = new CatalogueHandler(meta);
        using var venueClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/") };
        var reader = new HyperliquidPerpetualReader(venueClient, TimeProvider.System);
        await using var factory = new CoreApiFactory(owner, store, reader: reader);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync($"/api/accounts/{account.Id}/instruments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "venueId", "marketScope", "scope", "instruments", "notice" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        var root = json.RootElement;
        Assert.Equal("hyperliquid", root.GetProperty("venueId").GetString());
        Assert.Equal("perpetuals", root.GetProperty("marketScope").GetString());
        Assert.Equal("venue-perpetuals", root.GetProperty("scope").GetString());
        Assert.Equal("Venue perpetual contract metadata only. No orders, balances or execution refresh.",
            root.GetProperty("notice").GetString());
        var instrument = Assert.Single(root.GetProperty("instruments").EnumerateArray());
        Assert.Equal(new[] { "contractId", "quantityDecimals", "maxLeverage", "quoteAsset", "priceStep", "category", "venueContractId", "maintenanceMarginFraction" },
            instrument.EnumerateObject().Select(p => p.Name));
        // Hyperliquid rounds to significant figures, labels no categories and keys contracts by name.
        Assert.All(new[] { "priceStep", "category", "venueContractId" }, name => Assert.Equal(JsonValueKind.Null, instrument.GetProperty(name).ValueKind));
        Assert.Equal("MiXeD", instrument.GetProperty("contractId").GetString());
        Assert.Equal(5, instrument.GetProperty("quantityDecimals").GetInt32());
        Assert.Equal(40, instrument.GetProperty("maxLeverage").GetInt32());
        Assert.Equal("USDC", instrument.GetProperty("quoteAsset").GetString());
        Assert.Equal(0.0125m, instrument.GetProperty("maintenanceMarginFraction").GetDecimal());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.hyperliquid.xyz/info", request.Uri);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(new[] { "type", "dex" }, body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("meta", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("", body.RootElement.GetProperty("dex").GetString());
        Assert.DoesNotContain(account.Address!, request.Body);
        Assert.Equal("not-synced", account.SyncStatus);
        Assert.Null(account.LastSyncedAtUtc);
        Assert.Null(account.LastSyncError);
        Assert.Null(account.HistoryNotice);
        Assert.Equal(1, account.SettingsRevision);
        Assert.Empty(store.Snapshots);
        Assert.Empty(store.Fills);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("http")]
    public async Task Instrument_provider_failure_returns_safe_generic_502_without_recording_sync_failure(string failure)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "HL"); store.Accounts.Add(account);
        using var handler = new CatalogueHandler("{\"private-secret\":",
            failure == "http" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
        using var venueClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/") };
        var reader = new HyperliquidPerpetualReader(venueClient, TimeProvider.System);
        await using var factory = new CoreApiFactory(owner, store, reader: reader);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync($"/api/accounts/{account.Id}/instruments");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-secret", text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal("The venue instrument read failed. Try again later.", json.RootElement.GetProperty("detail").GetString());
        Assert.Single(handler.Requests);
        Assert.Equal("not-synced", account.SyncStatus);
        Assert.Null(account.LastSyncError);
        Assert.Null(account.LastSyncedAtUtc);
        Assert.Equal(0, store.Saves);
        Assert.Empty(store.Snapshots);
        Assert.Empty(store.Fills);
    }

    [Fact]
    public async Task Aborted_catalogue_waiter_does_not_cancel_shared_read_or_record_sync_failure_or_write()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "HL"); store.Accounts.Add(account);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken shared = default;
        using var handler = new CatalogueHandler("{\"universe\":[]}", wait: async ct =>
        {
            entered.TrySetResult();
            shared = ct;
            await release.Task.WaitAsync(ct);
        });
        using var venueClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/") };
        var reader = new HyperliquidPerpetualReader(venueClient, TimeProvider.System);
        await using var factory = new CoreApiFactory(owner, store, reader: reader);
        using var client = factory.AuthorizedClient();
        using var caller = new CancellationTokenSource();
        var request = client.GetAsync($"/api/accounts/{account.Id}/instruments", caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = client.GetAsync($"/api/accounts/{account.Id}/instruments");
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.False(shared.IsCancellationRequested);
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await other).StatusCode);
        Assert.Single(handler.Requests);
        Assert.Equal("not-synced", account.SyncStatus);
        Assert.Null(account.LastSyncError);
        Assert.Null(account.LastSyncedAtUtc);
        Assert.Equal(0, store.Saves);
        Assert.Empty(store.Snapshots);
        Assert.Empty(store.Fills);
    }

    private sealed class CatalogueHandler(string meta, HttpStatusCode status = HttpStatusCode.OK,
        Func<CancellationToken, Task>? wait = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(ct)));
            if (wait is not null) await wait(ct);
            return new(status) { Content = new StringContent(meta, Encoding.UTF8, "application/json") };
        }
    }
}
