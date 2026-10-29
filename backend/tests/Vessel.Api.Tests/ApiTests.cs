using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Vessel.Tests;

public sealed class ApiTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Liveness_is_anonymous_without_auth_or_database_configuration()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"healthy\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unannotated_routes_use_the_private_fallback_policy()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/future-route")).StatusCode);
        TestAuthentication.SignIn(client.DefaultRequestHeaders, Owner);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/future-route")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-token")]
    [InlineData("Bearer test-only-retired-bearer-secret")]
    [InlineData("Bearer")]
    public async Task System_requires_a_wallet_session_and_ignores_bearer_credentials(string? authorization)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        if (authorization is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        var response = await client.GetAsync("/api/system");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("session-required", problem.RootElement.GetProperty("code").GetString());
        if (authorization is not null)
        {
            Assert.DoesNotContain(authorization, body);
            Assert.DoesNotContain(authorization, string.Join(Environment.NewLine, factory.Logs));
        }
    }

    [Theory]
    [InlineData("Vessel:Auth:Token", "test-only-retired-bearer-secret")]
    [InlineData("Vessel:Auth:Mode", "Token")]
    [InlineData("Vessel:Auth:OwnerId", "00000000-0000-0000-0000-000000000000")]
    public async Task Removed_token_settings_and_an_empty_owner_refuse_to_start(string key, string value)
    {
        await using var factory = new ApiFactory(new() { [key] = value });
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(key, error.Message);
        if (key != "Vessel:Auth:Mode") Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public async Task Signed_in_owner_receives_the_exact_shared_contract_without_a_database()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        TestAuthentication.SignIn(client.DefaultRequestHeaders, Owner);
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
        Assert.Equal(Owner, root.GetProperty("owner").GetProperty("id").GetGuid());
        Assert.Equal(TestOwnerAuthHandler.NameOf(Owner), root.GetProperty("owner").GetProperty("displayName").GetString());
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
    }

    [Fact]
    public async Task The_owner_is_the_signed_in_user_not_configuration_or_client_input()
    {
        var signedIn = Guid.NewGuid();
        await using var factory = new ApiFactory(new() { ["Vessel:Auth:OwnerId"] = Guid.NewGuid().ToString(), ["Vessel:Auth:OwnerName"] = "Configured owner" });
        using var client = factory.CreateClient();
        TestAuthentication.SignIn(client.DefaultRequestHeaders, signedIn);
        using var json = JsonDocument.Parse(await client.GetStringAsync($"/api/system?ownerId={Guid.NewGuid()}"));
        Assert.Equal(signedIn, json.RootElement.GetProperty("owner").GetProperty("id").GetGuid());
        Assert.Equal(TestOwnerAuthHandler.NameOf(signedIn), json.RootElement.GetProperty("owner").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Development_openapi_requires_a_session_and_renders()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        TestAuthentication.SignIn(client.DefaultRequestHeaders, Owner);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/system", out _));
    }

    /// <summary>A Development API without a database; <see cref="TestAuthentication"/> stands in for wallet sessions.</summary>
    private sealed class ApiFactory(Dictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
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
                ["Vessel:Evidence:Storage:Provider"] = "Local",
                ["Vessel:Auth:OwnerId"] = Owner.ToString(),
                ["Vessel:Identity:Origins:0"] = TestAuthentication.Origin,
                ["ConnectionStrings:Vessel"] = null
            }.Concat(overrides ?? []).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value)));
            builder.ConfigureLogging(logging => logging.AddProvider(new CaptureLoggerProvider(Logs)));
            builder.ConfigureServices(services =>
            {
                VesselEventTestHosting.ConfigureServices(services);
                TestAuthentication.Add(services);
            });
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
