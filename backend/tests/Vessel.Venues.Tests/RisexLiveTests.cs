using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Risex;

namespace Vessel.Tests;

public sealed class RisexLiveTests
{
    private static RisexReader Reader()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.rise.trade/"), Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Vessel/1.0 (self-hosted trading journal; read-only)");
        return new RisexReader(client, TimeProvider.System, new RisexCatalogueCache(TimeProvider.System));
    }

    [RisexLiveFact]
    public async Task Live_account_orders_and_market_data_parse_under_the_strict_rules()
    {
        var address = Environment.GetEnvironmentVariable("Vessel_TEST_RISEX_ADDRESS")!;
        var reader = Reader();
        var instruments = await reader.ReadInstrumentsAsync(default);
        Assert.NotEmpty(instruments);
        Assert.All(instruments, i => Assert.True(i.PriceStep > 0));
        var account = await reader.ReadAsync(address, default);
        Assert.All(account.Fills, f => Assert.True(VenueFactChecks.Valid(f)));
        var orders = await reader.ReadOrdersAsync(address, default);
        Assert.All(orders.Orders, o => Assert.True(VenueFactChecks.Valid(o)));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.NotEmpty(await reader.ReadCandlesAsync("BTC", "1h", now - 24 * 3_600_000L, now, default));
        Assert.NotEmpty(await reader.ReadMarketContextsAsync(default));
    }
}
