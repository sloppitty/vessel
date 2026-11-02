using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class VenueRateLimitIntegrationTests : VenueRateLimitTestsSupport
{

    [PostgresFact]
    public async Task Review_M1_a_stored_venue_deadline_never_exceeds_one_hour()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        await store.DeferVenueAsync("hyperliquid", DateTimeOffset.MaxValue, Now, default);
        Assert.Equal(Now.AddHours(1), await store.VenueRetryAtAsync("hyperliquid", default));
    }

    [PostgresFact]
    public async Task Api_refresh_manual_and_legacy_check_report_the_persisted_venue_retry_after()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var first = new Account(Guid.NewGuid(), owner, "hyperliquid", "Limited account");
        var second = new Account(Guid.NewGuid(), owner, "hyperliquid", "Other account on the same venue");
        first.Configure(null, "0x" + new string('a', 40), null);
        second.Configure(null, "0x" + new string('b', 40), null);
        var play = new Play(Guid.NewGuid(), second, new PerpetualInstrument("hyperliquid", "BTC"),
            InstrumentSource.Venue, "{}", Now.AddMinutes(-2));
        play.MarkPlanned(Now.AddMinutes(-1));
        await using (var seed = database.Context(owner))
        {
            seed.AddRange(first, second, play);
            await seed.SaveChangesAsync();
        }
        var orders = new AccountHistoryOrders { Failure = new VenueRateLimitException(Now.AddSeconds(600)) };
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString, configure: services =>
        {
            services.AddSingleton<TimeProvider>(new Clock());
            services.AddSingleton<IVenueRegistry>(AccountHistoryOrders.Registry(AccountHistoryOrders.PublicReader(), orders));
        });
        using var client = factory.AuthorizedClient();
        using var publicSuccess = await client.PostAsync($"/api/accounts/{first.Id}/sync", null);
        publicSuccess.EnsureSuccessStatusCode();
        Assert.Contains("rate limit", (await publicSuccess.Content.ReadFromJsonAsync<AccountDto>())!.HistoryNotice);
        foreach (var route in new[] { $"/api/accounts/{second.Id}/sync", $"/api/accounts/{second.Id}/monitor/run",
            $"/api/plays/{play.Id}/execution/check" })
        {
            using var denied = await client.PostAsync(route, null);
            Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(600), denied.Headers.RetryAfter!.Delta);
        }
        Assert.Equal(1, orders.Reads);
    }
}
