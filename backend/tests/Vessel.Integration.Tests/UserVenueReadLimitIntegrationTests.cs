using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Signer;
using Vessel.Application.Credentials;
using Vessel.Application.MarketData;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Tests.Identity;

namespace Vessel.Tests;

public sealed class UserVenueReadLimitIntegrationTests : UserVenueReadLimitTestsSupport
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_auth_modes_return_429_with_retry_after_for_all_per_user_caps(bool wallet)
    {
        await using var identity = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = (await identity.SeedAsync(key)).User.Id;
        var store = new MemoryWorkspaceStore(owner);
        var marketAccount = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic market account");
        var credentialAccount = SecurityFixture.Account(owner);
        store.Accounts.AddRange([marketAccount, credentialAccount]);
        using var vault = SecurityFixture.Vault();
        var state = new MonitorServerState();
        var candles = new Candles();
        var registry = new VenueRegistry([VenueDescriptor.Manual, HyperliquidPerpetualReader.Descriptor, SecurityFixture.Venue],
            [new FixtureReader()], [], [candles], [], [new FakeMarketStream()], [new SecurityDiscovery([])], [new SecurityVerifier()]);
        void Configure(IServiceCollection services)
        {
            services.AddSingleton<IWorkspaceStore>(store);
            services.AddSingleton<IUnitOfWork>(TestUnitOfWork.None);
            services.AddSingleton<IServerStateStore>(state);
            services.AddSingleton<IVenueRegistry>(registry);
            services.AddSingleton<ICredentialVault>(vault);
            services.AddSingleton<IAccountCredentialStore>(new SecurityCredentialStore());
            services.AddSingleton<TimeProvider>(SecurityFixture.Clock);
        }
        await using var factory = wallet ? IdentityHttpTest.Factory(identity, owner, configure: Configure)
            : new CoreApiFactory(owner, store, connection: identity.Connection, configure: Configure);
        using var client = wallet ? IdentityHttpTest.Client(factory) : factory.AuthorizedClient();
        if (wallet) await IdentityHttpTest.SignInAsync(client, key);

        var limiter = factory.Services.GetRequiredService<MarketStreamLimiter>();
        var leases = Enumerable.Range(0, 4).Select(_ => limiter.TryAcquire(owner)!).ToArray();
        try
        {
            using var rejected = await client.GetAsync($"/api/accounts/{marketAccount.Id}/market-stream?instrument=BTC&interval=1m");
            await LimitedAsync(rejected, 5);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }

        var candleUrl = $"/api/accounts/{marketAccount.Id}/candles?instrument=BTC&interval=1h&refresh=true";
        (await client.GetAsync(candleUrl)).EnsureSuccessStatusCode();
        using (var rejected = await client.GetAsync(candleUrl + "&endTime=100000000")) await LimitedAsync(rejected, 60);
        // A different series has its own budget. A normal read never spends the manual-refresh budget.
        (await client.GetAsync(candleUrl.Replace("interval=1h", "interval=1m", StringComparison.Ordinal))).EnsureSuccessStatusCode();
        (await client.GetAsync(candleUrl.Replace("refresh=true", "refresh=false", StringComparison.Ordinal))).EnsureSuccessStatusCode();

        for (var index = 0; index < 10; index++)
            (await client.GetAsync($"/api/venues/{SecurityFixture.Venue.Id}/accounts?address={SecurityFixture.Address}")).EnsureSuccessStatusCode();
        using (var rejected = await client.GetAsync($"/api/venues/{SecurityFixture.Venue.Id}/accounts?address={SecurityFixture.Address}"))
            await LimitedAsync(rejected, 60);
        // Exhausting discovery does not prevent a token-bearing import of up to 30 subaccounts.
        for (var index = 0; index < 30; index++)
            (await client.PutAsJsonAsync($"/api/accounts/{credentialAccount.Id}/credential", new { token = SecurityFixture.Token })).EnsureSuccessStatusCode();
        using (var rejected = await client.PutAsJsonAsync($"/api/accounts/{credentialAccount.Id}/credential", new { token = SecurityFixture.Token }))
            await LimitedAsync(rejected, 60);

        // Public reads have their own backoff, not the account-read server deadline.
        await state.DeferVenueAsync("hyperliquid", SecurityFixture.Now.AddMinutes(5), SecurityFixture.Now, default);
        (await client.GetAsync(candleUrl.Replace("interval=1h", "interval=5m", StringComparison.Ordinal))).EnsureSuccessStatusCode();
        candles.Error = new VenueRateLimitException(SecurityFixture.Now.AddMinutes(10));
        Assert.Equal(502, (int)(await client.GetAsync(candleUrl.Replace("interval=1h", "interval=15m", StringComparison.Ordinal))).StatusCode);
        Assert.Equal(SecurityFixture.Now.AddMinutes(5), await state.VenueRetryAtAsync("hyperliquid", default));
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 30)]
    public async Task Venue_deadline_refusals_consume_neither_discovery_nor_verification_budget(bool verification, int budget)
    {
        var owner = Guid.NewGuid();
        var clock = new ManualTime(SecurityFixture.Now);
        var state = new MonitorServerState();
        var policy = new AccountReadPolicy(state, new UserReadLimits(clock), clock);
        var accounts = new MemoryWorkspaceStore(owner);
        var account = SecurityFixture.Account(owner); accounts.Accounts.Add(account);
        var verifier = new SecurityVerifier();
        var adapter = new SecurityDiscovery([]);
        var registry = SecurityFixture.Registry(verifier, adapter);
        using var vault = SecurityFixture.Vault();
        var credentials = new AccountCredentialService(accounts, new SecurityCredentialStore(), new CoreOwner(owner),
            registry, vault, clock, TestUnitOfWork.None, policy);
        var discovery = new AccountDiscoveryService(accounts, new CoreOwner(owner), registry, policy);
        async Task AttemptAsync()
        {
            if (verification) await credentials.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
            else await discovery.DiscoverAsync(SecurityFixture.Venue.Id,
                new CredentialDiscoveryRequest { Address = SecurityFixture.Address, Token = SecurityFixture.Token }, default);
        }
        await state.DeferVenueAsync(SecurityFixture.Venue.Id, clock.GetUtcNow().AddSeconds(30), clock.GetUtcNow(), default);
        for (var index = 0; index <= budget; index++)
        {
            var error = await Assert.ThrowsAsync<Vessel.Application.Common.ProblemException>(AttemptAsync);
            Assert.Equal(429, error.StatusCode);
            Assert.Equal(30, error.RetryAfterSeconds);
        }
        Assert.Null(verifier.ReceivedToken);
        Assert.Null(adapter.ReceivedToken);
        clock.Advance(TimeSpan.FromSeconds(30));
        for (var index = 0; index < budget; index++) await AttemptAsync();
        var exhausted = await Assert.ThrowsAsync<Vessel.Application.Common.ProblemException>(AttemptAsync);
        Assert.Equal(429, exhausted.StatusCode);
        Assert.Equal(60, exhausted.RetryAfterSeconds);
    }

    private static async Task LimitedAsync(HttpResponseMessage response, int seconds)
    {
        Assert.Equal(429, (int)response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(seconds), response.Headers.RetryAfter?.Delta);
        Assert.Equal("rate-limited", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
