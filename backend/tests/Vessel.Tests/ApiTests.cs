using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Vessel.Tests;

public sealed class ApiTests
{
    private const string TestToken = "test-only-bearer-secret";

    [Fact]
    public async Task Liveness_is_anonymous_without_auth_or_database_configuration()
    {
        await using var factory = new ApiFactory(null);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"healthy\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unannotated_routes_use_the_private_fallback_policy()
    {
        await using var factory = new ApiFactory(TestToken);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/future-route")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/future-route")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData("Bearer")]
    public async Task System_rejects_missing_wrong_or_malformed_credentials(string? token)
    {
        await using var factory = new ApiFactory(TestToken);
        using var client = factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        var response = await client.GetAsync("/api/system");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.DoesNotContain(TestToken, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(TestToken, string.Join(Environment.NewLine, factory.Logs));
        if (token is not null) Assert.DoesNotContain($"Bearer {token}", string.Join(Environment.NewLine, factory.Logs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Unconfigured_token_fails_closed_even_with_a_bearer(string? configuredToken)
    {
        await using var factory = new ApiFactory(configuredToken);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/system")).StatusCode);
        Assert.DoesNotContain(TestToken, string.Join(Environment.NewLine, factory.Logs));
    }

    [Fact]
    public async Task Valid_bearer_returns_the_exact_shared_contract_without_a_database()
    {
        await using var factory = new ApiFactory(TestToken);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        var response = await client.GetAsync("/api/system");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(new[] { "application", "stage", "owner", "marketScope", "allowsConcurrentPlays", "venues", "version" },
            root.EnumerateObject().Select(x => x.Name));
        Assert.Equal("Vessel", root.GetProperty("application").GetString());
        var expectedVersion = typeof(ApiTests).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(expectedVersion, root.GetProperty("version").GetString());
        Assert.Equal("core", root.GetProperty("stage").GetString());
        Assert.Equal("perpetuals", root.GetProperty("marketScope").GetString());
        Assert.True(root.GetProperty("allowsConcurrentPlays").GetBoolean());
        Assert.Equal("11111111-1111-1111-1111-111111111111", root.GetProperty("owner").GetProperty("id").GetString());
        Assert.Equal("Owner", root.GetProperty("owner").GetProperty("displayName").GetString());
        Assert.Equal(new[] { "hyperliquid:Hyperliquid:read-only", "risex:RISEx:read-only", "lighter:Lighter:read-only", "lighter-rh:Lighter · Robinhood:read-only", "quantfury:Quantfury:candidate", "manual:Manual:manual" },
            root.GetProperty("venues").EnumerateArray().Select(v =>
                $"{v.GetProperty("id").GetString()}:{v.GetProperty("name").GetString()}:{v.GetProperty("status").GetString()}"));
        // Capabilities tell the browser what each venue can do; planned venues can do nothing yet.
        var venues = root.GetProperty("venues").EnumerateArray().ToDictionary(v => v.GetProperty("id").GetString()!);
        var hyperliquid = venues["hyperliquid"];
        Assert.Equal("evm-address", hyperliquid.GetProperty("source").GetString());
        Assert.Equal("USDC", hyperliquid.GetProperty("quoteAsset").GetString());
        Assert.Equal("https://app.hyperliquid.xyz/trade/{venueContractId}", hyperliquid.GetProperty("tradeUrlTemplate").GetString());
        Assert.All(new[] { "sync", "instruments", "orders", "candles", "marketContext", "stream", "stablecoinWallet" },
            name => Assert.True(hyperliquid.GetProperty("capabilities").GetProperty(name).GetBoolean(), name));
        foreach (var id in new[] { "quantfury" })
            Assert.All(venues[id].GetProperty("capabilities").EnumerateObject(), capability => Assert.False(capability.Value.GetBoolean(), $"{id}.{capability.Name}"));
        Assert.Equal("none", venues["manual"].GetProperty("source").GetString());
        Assert.True(venues["manual"].GetProperty("capabilities").GetProperty("manualExecutions").GetBoolean());
        Assert.All(venues["manual"].GetProperty("capabilities").EnumerateObject().Where(c => c.Name != "manualExecutions"),
            c => Assert.False(c.Value.GetBoolean()));
        Assert.All(new[] { "hyperliquid", "risex", "lighter", "lighter-rh", "quantfury" },
            id => Assert.False(venues[id].GetProperty("capabilities").GetProperty("manualExecutions").GetBoolean()));
        // Logo keys come from the fixed backend set; planned venues have none.
        Assert.Equal(["hyperliquid", "risex", "lighter", "lighter-rh", null, "manual"],
            venues.Values.Select(v => v.GetProperty("logo").GetString()));
        Assert.Equal("significant-figures", hyperliquid.GetProperty("priceRule").GetString());
        Assert.Equal(["1m", "3m", "5m", "15m", "30m", "1h", "2h", "4h", "8h", "12h", "1d", "3d", "1w", "1M"],
            hyperliquid.GetProperty("intervals").EnumerateArray().Select(i => i.GetString()));
        Assert.Empty(venues["manual"].GetProperty("intervals").EnumerateArray());
        var risex = venues["risex"];
        Assert.Equal(("evm-address", "tick-size", "https://www.rise.trade/trade/{instrument}"),
            (risex.GetProperty("source").GetString(), risex.GetProperty("priceRule").GetString(), risex.GetProperty("tradeUrlTemplate").GetString()));
        Assert.Equal(["1m", "5m", "15m", "1h", "4h", "1d", "1w"], risex.GetProperty("intervals").EnumerateArray().Select(i => i.GetString()));
        // Public reads by address; no live stream and no stablecoin wallet.
        Assert.False(risex.GetProperty("capabilities").GetProperty("stream").GetBoolean());
        Assert.False(risex.GetProperty("capabilities").GetProperty("stablecoinWallet").GetBoolean());
        Assert.True(risex.GetProperty("capabilities").GetProperty("orders").GetBoolean());
        // Only an order reader that returns the current open book says so; RISEx reads order history (Phase 0, 0.4c).
        Assert.Equal((true, false, true, true), (hyperliquid.GetProperty("capabilities").GetProperty("currentOrderBook").GetBoolean(),
            risex.GetProperty("capabilities").GetProperty("currentOrderBook").GetBoolean(),
            venues["lighter"].GetProperty("capabilities").GetProperty("currentOrderBook").GetBoolean(),
            venues["lighter-rh"].GetProperty("capabilities").GetProperty("currentOrderBook").GetBoolean()));
        var lighter = venues["lighter"];
        Assert.Equal("account-index", lighter.GetProperty("source").GetString());
        Assert.Equal("https://app.lighter.xyz/read-only-tokens", lighter.GetProperty("credentialSetupUrl").GetString());
        Assert.Equal("tick-size", lighter.GetProperty("priceRule").GetString());
        Assert.All(new[] { "sync", "instruments", "orders", "candles", "accountDiscovery", "readOnlyCredential" },
            name => Assert.True(lighter.GetProperty("capabilities").GetProperty(name).GetBoolean(), name));
        Assert.All(new[] { "marketContext", "stream", "stablecoinWallet" },
            name => Assert.False(lighter.GetProperty("capabilities").GetProperty(name).GetBoolean(), name));
        Assert.Equal(["1m", "5m", "15m", "30m", "1h", "4h", "12h", "1d"],
            lighter.GetProperty("intervals").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal("USDC", lighter.GetProperty("quoteAsset").GetString());
        Assert.Equal(JsonValueKind.Null, lighter.GetProperty("tradeUrlTemplate").ValueKind);
        // Lighter on Robinhood Chain: the same capabilities as a separate exchange, quoted and collateralized in USDG.
        var rh = venues["lighter-rh"];
        Assert.Equal(("account-index", "tick-size", "USDG"),
            (rh.GetProperty("source").GetString(), rh.GetProperty("priceRule").GetString(), rh.GetProperty("quoteAsset").GetString()));
        Assert.Equal("https://robinhoodchain.lighter.xyz/read-only-tokens", rh.GetProperty("credentialSetupUrl").GetString());
        Assert.Equal("https://robinhoodchain.lighter.xyz/trade/{instrument}", rh.GetProperty("tradeUrlTemplate").GetString());
        Assert.Equal(lighter.GetProperty("capabilities").GetRawText(), rh.GetProperty("capabilities").GetRawText());
        Assert.Equal(lighter.GetProperty("intervals").GetRawText(), rh.GetProperty("intervals").GetRawText());
        Assert.DoesNotContain(TestToken, body);
        Assert.DoesNotContain(TestToken, string.Join(Environment.NewLine, factory.Logs));
    }

    [Fact]
    public async Task Configured_owner_is_seeded_by_server_not_client_claims()
    {
        var ownerId = Guid.NewGuid();
        await using var factory = new ApiFactory(TestToken, ownerId.ToString(), "Configured owner");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        using var json = JsonDocument.Parse(await client.GetStringAsync($"/api/system?ownerId={Guid.NewGuid()}"));
        Assert.Equal(ownerId, json.RootElement.GetProperty("owner").GetProperty("id").GetGuid());
        Assert.Equal("Configured owner", json.RootElement.GetProperty("owner").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Empty_owner_identity_fails_closed()
    {
        await using var factory = new ApiFactory(TestToken, Guid.Empty.ToString());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/system")).StatusCode);
    }

    [Fact]
    public async Task Development_openapi_is_bearer_protected_and_renders()
    {
        await using var factory = new ApiFactory(TestToken);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/system", out _));
    }

    private sealed class ApiFactory(string? token, string? ownerId = null, string? ownerName = null)
        : WebApplicationFactory<Program>
    {
        public List<string> Logs { get; } = [];
        protected override IHost CreateHost(IHostBuilder builder)
        {
            VesselEventTestHosting.ConfigureBootstrap(builder);
            return base.CreateHost(builder);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vessel:Auth:Token"] = token,
                ["Vessel:Evidence:Storage:Provider"] = "Local",
                ["Vessel:Auth:OwnerId"] = ownerId ?? "11111111-1111-1111-1111-111111111111",
                ["Vessel:Auth:OwnerName"] = ownerName ?? "Owner",
                ["ConnectionStrings:Vessel"] = null
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(new CaptureLoggerProvider(Logs)));
            builder.ConfigureServices(VesselEventTestHosting.ConfigureServices);
        }
    }

    private sealed class CaptureLoggerProvider(List<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(logs);
        public void Dispose() { }
        private sealed class CaptureLogger(List<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (logs) logs.Add(formatter(state, exception));
            }
        }
    }
}
