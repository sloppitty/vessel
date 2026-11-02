using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues;
using Vessel.Infrastructure.Venues.Lighter;

namespace Vessel.Tests;

/// <summary>
/// Lighter on Robinhood Chain is a second configured instance of the Lighter module. Fixtures under
/// Fixtures/LighterRh were captured from the public, unauthenticated https://api.rh.lighter.xyz/api/v1/orderBookDetails,
/// orderBooks and assetDetails on 2026-10-14 and trimmed to a few markets (crypto, a stock, a commodity, a pre-IPO
/// market and two spot markets). No account, address or credential was used.
/// </summary>
public sealed class LighterRobinhoodApiTests
{

    [Fact]
    public async Task Configured_hosts_reach_each_named_client_and_each_exchange_registers_once()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid(), settings: new()
        {
            ["Vessel:Venues:Hyperliquid:BaseUrl"] = "https://hl.example.test",
            ["Vessel:Venues:Hyperliquid:StreamUrl"] = "wss://hl.example.test/ws",
            ["Vessel:Venues:Risex:BaseUrl"] = "https://risex.example.test/",
            ["Vessel:Venues:Lighter:BaseUrl"] = "https://lighter.example.test/",
            ["Vessel:Venues:LighterRh:BaseUrl"] = "https://rh.example.test/"
        });
        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();
        Assert.Equal("https://hl.example.test/", clients.CreateClient("hyperliquid").BaseAddress!.AbsoluteUri);
        Assert.Equal("https://risex.example.test/", clients.CreateClient("risex").BaseAddress!.AbsoluteUri);
        Assert.Equal("https://lighter.example.test/", clients.CreateClient("lighter").BaseAddress!.AbsoluteUri);
        Assert.Equal("https://rh.example.test/", clients.CreateClient("lighter-rh").BaseAddress!.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(20), clients.CreateClient("lighter-rh").Timeout);

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var venues = services.GetRequiredService<IVenueRegistry>();
        foreach (var id in new[] { "lighter", "lighter-rh" })
        {
            Assert.Equal(id, venues.Reader(id)!.VenueId);
            Assert.Equal(id, venues.Orders(id)!.VenueId);
            Assert.Equal(id, venues.Candles(id)!.VenueId);
            Assert.Single(services.GetServices<IVenueCredentialVerifier>(), v => v.VenueId == id);
            Assert.Single(services.GetServices<IVenueAccountDiscovery>(), v => v.VenueId == id);
            Assert.Single(services.GetServices<IPerpetualVenueReader>(), v => v.VenueId == id);
            Assert.Single(services.GetServices<ICandleReader>(), v => v.VenueId == id);
            Assert.Single(venues.Descriptors, d => d.Id == id);
        }
        Assert.NotSame(services.GetRequiredKeyedService<LighterCatalogueCache>("lighter"), services.GetRequiredKeyedService<LighterCatalogueCache>("lighter-rh"));
        Assert.Equal("lighter-rh", services.GetRequiredKeyedService<LighterCatalogueCache>("lighter-rh").VenueId);
        Assert.Equal("Lighter · Robinhood", venues.Descriptor("lighter-rh")!.Name);
        Assert.Equal("https://hl.example.test/", services.GetRequiredService<IOptions<VenueHostOptions>>().Value.BaseUri("Hyperliquid").AbsoluteUri);
    }
}
