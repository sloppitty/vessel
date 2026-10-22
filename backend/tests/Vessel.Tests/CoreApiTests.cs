using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;

namespace Vessel.Tests;

internal sealed class CoreApiFactory(Guid owner, IWorkspaceStore? store = null, string? connection = null, IPerpetualVenueReader? reader = null, Vessel.Application.MarketData.ICandleReader? candles = null, Vessel.Application.MarketData.IMarketContextReader? market = null, Vessel.Application.MarketData.IMarketStream? stream = null,
    Dictionary<string, string?>? settings = null, Action<IServiceCollection>? configure = null,
    string environment = "Development") : WebApplicationFactory<Program>
{
    public const string Token = "core-tests-only-token-not-a-real-secret";
    protected override IHost CreateHost(IHostBuilder builder)
    {
        VesselEventTestHosting.ConfigureBootstrap(builder);
        // Production validation runs before ConfigureAppConfiguration on a minimal host.
        // Supply synthetic bootstrap settings early; never change the process environment.
        if (environment != "Development")
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vessel:Auth:Token"] = Token,
                ["Vessel:Auth:OwnerId"] = owner.ToString(),
                ["ConnectionStrings:Vessel"] = connection
            }));
        // Startup-bound settings (auth schemes, deployment maps, notification cooldown) are read before
        // ConfigureAppConfiguration runs on a minimal host. Explicit settings override synthetic defaults.
        if (settings is not null)
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(settings));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Auth:Token"] = Token,
            ["Vessel:Auth:OwnerId"] = owner.ToString(),
            ["Vessel:Evidence:Storage:Provider"] = "Local",
            ["ConnectionStrings:Vessel"] = connection
        }.Concat(settings ?? []).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value)));
        builder.ConfigureServices(services =>
        {
            VesselEventTestHosting.ConfigureServices(services);
            if (store is not null)
            {
                services.AddSingleton(store);
                // In-memory workspace fixtures use their supplied facts store, without a database revision probe.
                services.RemoveAll<Vessel.Application.Activity.IActivitySummaryReader>();
                services.AddSingleton<Vessel.Application.Ownership.IUnitOfWork>(TestUnitOfWork.None);
            }
            if (reader is not null) services.AddSingleton<IPerpetualVenueReader>(reader);
            else if (candles is not null)
            {
                var catalogue = new FixtureReader();
                catalogue.Result = catalogue.Result with { Instruments = [new("BTC", 5, 50, "USDC"), new("ETH", 4, 20, "USDC")] };
                services.AddSingleton<IPerpetualVenueReader>(catalogue);
            }
            if (candles is not null) services.AddSingleton<Vessel.Application.MarketData.ICandleReader>(candles);
            if (market is not null) services.AddSingleton<Vessel.Application.MarketData.IMarketContextReader>(market);
            if (stream is not null) services.AddSingleton(stream);
            configure?.Invoke(services);
        });
    }
    public HttpClient AuthorizedClient()
    {
        var client = CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token); return client;
    }
}

/// <summary>
/// Hosts shared by CoreApiTests' rejection matrices. <see cref="Bare"/> has no database or store, so a route
/// that reached persistence before authentication would answer 503, not 401. <see cref="Rejecting"/> has an
/// in-memory store with one portfolio; the cases that use it are refused before any write and assert so.
/// </summary>
public sealed class CoreApiRejectionHosts : IAsyncDisposable
{
    internal CoreApiFactory Bare { get; } = new(Guid.NewGuid());
    internal MemoryWorkspaceStore Store { get; }
    internal Vessel.Domain.Workspace.Portfolio Portfolio { get; }
    internal CoreApiFactory Rejecting { get; }

    public CoreApiRejectionHosts()
    {
        var owner = Guid.NewGuid();
        Store = new MemoryWorkspaceStore(owner);
        Portfolio = new Vessel.Domain.Workspace.Portfolio(Guid.NewGuid(), owner, "Trading");
        Store.Portfolios.Add(Portfolio);
        Rejecting = new CoreApiFactory(owner, Store);
    }

    public async ValueTask DisposeAsync()
    {
        await Bare.DisposeAsync();
        await Rejecting.DisposeAsync();
    }
}

public sealed class CoreApiTests(CoreApiRejectionHosts hosts) : IClassFixture<CoreApiRejectionHosts>
{
    [Theory]
    [InlineData("GET", "/api/portfolios")]
    [InlineData("POST", "/api/portfolios")]
    [InlineData("GET", "/api/accounts")]
    [InlineData("GET", "/api/portfolios/11111111-1111-1111-1111-111111111111")]
    [InlineData("GET", "/api/accounts/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/accounts")]
    [InlineData("GET", "/api/overview")]
    [InlineData("GET", "/api/accounts/11111111-1111-1111-1111-111111111111/snapshot")]
    [InlineData("GET", "/api/accounts/11111111-1111-1111-1111-111111111111/fills")]
    [InlineData("POST", "/api/accounts/11111111-1111-1111-1111-111111111111/sync")]
    public async Task Core_routes_require_authentication_before_database_or_provider(string method, string path)
    {
        using var client = hosts.Bare.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new(new HttpMethod(method), path))).StatusCode);
    }

    [PostgresFact]
    public async Task Manual_account_shape_null_snapshot_and_owner_cannot_be_supplied_by_body()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString); using var client = factory.AuthorizedClient();
        var response = await client.PostAsJsonAsync("/api/portfolios", new { name = "Swing", ownerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var portfolio = (await response.Content.ReadFromJsonAsync<PortfolioDto>())!;
        var accountResponse = await client.PostAsJsonAsync("/api/accounts", new
        { portfolioId = portfolio.Id, name = "Manual", venueId = "manual", manualAccountValueUsd = "123.12345678901234567890123456", ownerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);
        var account = (await accountResponse.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("123.12345678901234567890123456", account.AccountValueUsd);
        await using (var db = database.Context(owner))
        {
            Assert.Equal(owner, (await db.Accounts.IgnoreQueryFilters().SingleAsync()).OwnerId);
            Assert.Equal(owner, (await db.Portfolios.IgnoreQueryFilters().SingleAsync()).OwnerId);
        }
        using var json = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "id", "portfolioId", "name", "venueId", "address", "accountValueUsd", "lastSyncedAtUtc", "syncStatus", "lastSyncError", "positionCount", "historyNotice", "isEnabled", "settingsRevision", "availableStablecoinNominalUsd", "stablecoinScope", "accountMode", "totalStablecoinNominalUsd", "balanceUsd", "sourceId", "availableBalanceUsd", "availableBalanceSource", "tradingMode", "keepExecutionHistory" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("real", json.RootElement.GetProperty("tradingMode").GetString());
        Assert.False(json.RootElement.TryGetProperty("startingCapitalUsd", out _));
        Assert.Null(account.SourceId);
        Assert.Null(account.AvailableStablecoinNominalUsd);
        Assert.Null(account.AvailableBalanceUsd);
        Assert.Null(account.AvailableBalanceSource);
        Assert.Equal("null", await client.GetStringAsync($"/api/accounts/{account.Id}/snapshot"));
        var sync = await client.PostAsync($"/api/accounts/{account.Id}/sync", null);
        Assert.Equal(HttpStatusCode.BadRequest, sync.StatusCode); Assert.Equal("application/problem+json", sync.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Created_resource_locations_can_be_read_back()
    {
        var owner = Guid.NewGuid();
        var store = new MemoryWorkspaceStore(owner);
        await using var factory = new CoreApiFactory(owner, store);
        using var client = factory.AuthorizedClient();
        var createdPortfolio = await client.PostAsJsonAsync("/api/portfolios", new { name = "Owned portfolio" });
        var portfolio = (await createdPortfolio.Content.ReadFromJsonAsync<PortfolioDto>())!;
        Assert.NotNull(createdPortfolio.Headers.Location);
        var readPortfolio = await client.GetFromJsonAsync<PortfolioDto>(createdPortfolio.Headers.Location);
        Assert.Equal(portfolio, readPortfolio);
        var createdAccount = await client.PostAsJsonAsync("/api/accounts", new
        {
            portfolioId = portfolio.Id,
            name = "Owned account",
            venueId = "manual"
        });
        var account = (await createdAccount.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.NotNull(createdAccount.Headers.Location);
        var readAccount = await client.GetFromJsonAsync<AccountDto>(createdAccount.Headers.Location);
        Assert.Equal(account, readAccount);
    }

    [Fact]
    public async Task Read_resource_locations_do_not_expose_other_owners()
    {
        var owner = Guid.NewGuid();
        var foreignOwner = Guid.NewGuid();
        var store = new MemoryWorkspaceStore(owner);
        var portfolio = new Vessel.Domain.Workspace.Portfolio(Guid.NewGuid(), foreignOwner, "Private foreign portfolio");
        var account = new Vessel.Domain.Accounts.Account(Guid.NewGuid(), foreignOwner, "manual", "Private foreign account");
        store.Portfolios.Add(portfolio);
        store.Accounts.Add(account);
        await using var factory = new CoreApiFactory(owner, store);
        using var client = factory.AuthorizedClient();
        foreach (var path in new[] { $"/api/portfolios/{portfolio.Id}", $"/api/accounts/{account.Id}" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("Private foreign", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("manual", "-1", null)]
    [InlineData("manual", "0.00000000000000000000000000001", null)]
    [InlineData("manual", "1e2", null)]
    [InlineData("manual", null, "0x1111111111111111111111111111111111111111")]
    [InlineData("hyperliquid", null, null)]
    [InlineData("hyperliquid", null, "https://evil.invalid/info")]
    [InlineData("hyperliquid", null, "0xzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("hyperliquid", "1", "0x1111111111111111111111111111111111111111")]
    [InlineData("lighter", null, null)]
    public async Task Invalid_account_configuration_returns_safe_problem(string venue, string? value, string? address)
    {
        using var client = hosts.Rejecting.AuthorizedClient();
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(hosts.Portfolio.Id, "Account", venue, address, value));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(hosts.Store.Accounts);
    }

    [Fact]
    public async Task Valid_public_address_normalizes_without_inventing_balance()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var portfolio = new Vessel.Domain.Workspace.Portfolio(Guid.NewGuid(), owner, "Trading"); store.Portfolios.Add(portfolio);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(portfolio.Id, "HL", "hyperliquid", "0xABCDEF1111111111111111111111111111111111"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("0xabcdef1111111111111111111111111111111111", dto.Address);
        Assert.Null(dto.AccountValueUsd); Assert.Null(dto.LastSyncedAtUtc); Assert.Equal("not-synced", dto.SyncStatus);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":\" \"}")]
    [InlineData("{broken")]
    public async Task Invalid_portfolio_body_is_a_safe_400(string body)
    {
        using var client = hosts.Rejecting.AuthorizedClient();
        var before = hosts.Store.Portfolios.Count;
        var response = await client.PostAsync("/api/portfolios", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Exception", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, hosts.Store.Portfolios.Count);
    }

    [Fact]
    public async Task Financial_json_number_is_not_accepted_as_decimal_string()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        await using var factory = new CoreApiFactory(owner, store); using var client = factory.AuthorizedClient();
        var response = await client.PostAsJsonAsync("/api/accounts", new { portfolioId = Guid.NewGuid(), name = "Manual", venueId = "manual", manualAccountValueUsd = 123.1 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(store.Accounts);
    }

    [Fact]
    public async Task Missing_database_returns_generic_503_without_connection_details()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid()); using var client = factory.AuthorizedClient();
        var response = await client.GetAsync("/api/overview");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ConnectionStrings", body); Assert.DoesNotContain("Exception", body);
    }
}
