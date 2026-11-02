using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Venues;

namespace Vessel.Tests;

/// <summary>RISEx adapter against recorded response shapes (October 5, 2026). The address is synthetic.</summary>
public sealed partial class RisexAdapterApiTests
{

    [Fact]
    public async Task Each_venue_keeps_its_own_http_client_configuration()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();
        Assert.Equal("https://api.rise.trade/", clients.CreateClient("risex").BaseAddress!.AbsoluteUri);
        Assert.Equal("https://api.hyperliquid.xyz/", clients.CreateClient("hyperliquid").BaseAddress!.AbsoluteUri);
        Assert.Equal("https://mainnet.zklighter.elliot.ai/", clients.CreateClient("lighter").BaseAddress!.AbsoluteUri);
        Assert.Contains("Vessel", clients.CreateClient("risex").DefaultRequestHeaders.UserAgent.ToString());
        Assert.DoesNotContain("Vessel", clients.CreateClient("hyperliquid").DefaultRequestHeaders.UserAgent.ToString());
        // Only Lighter's client adds the token, never follows redirects, and no venue client logs requests.
        var handlers = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        var lighter = Handlers(handlers.CreateHandler("lighter"));
        Assert.Single(lighter.OfType<Vessel.Infrastructure.Venues.Lighter.LighterAuthenticationHandler>());
        Assert.False(Assert.Single(lighter.OfType<HttpClientHandler>()).AllowAutoRedirect);
        foreach (var venue in new[] { "hyperliquid", "risex" })
            Assert.DoesNotContain(Handlers(handlers.CreateHandler(venue)), h => h is Vessel.Infrastructure.Venues.Lighter.LighterAuthenticationHandler);
        foreach (var venue in new[] { "hyperliquid", "risex", "lighter" })
            Assert.DoesNotContain(Handlers(handlers.CreateHandler(venue)), h => h.GetType().Name.Contains("Logging", StringComparison.Ordinal));
        using var scope = factory.Services.CreateScope();
        var venues = scope.ServiceProvider.GetRequiredService<IVenueRegistry>();
        Assert.Equal("RISEx", venues.Descriptor("risex")!.Name);
        Assert.Equal(["hyperliquid", "risex", "lighter"],
            new[] { "hyperliquid", "risex", "lighter" }.Where(id => venues.Reader(id)?.VenueId == id));
        Assert.Equal("risex", venues.Orders("risex")!.VenueId);
        Assert.Null(venues.Stream("risex"));
        // Every port of one venue is served by the same scoped adapter.
        Assert.Same(venues.Reader("lighter"), venues.CredentialVerifier("lighter"));
        Assert.Same(venues.Reader("lighter"), venues.Discovery("lighter"));
        Assert.Same(venues.Reader("hyperliquid"), venues.MarketContext("hyperliquid"));
        Assert.Null(venues.MarketContext("lighter"));
        Assert.Null(venues.Discovery("risex")); Assert.Null(venues.CredentialVerifier("hyperliquid"));
    }

    private static List<HttpMessageHandler> Handlers(HttpMessageHandler? handler)
    {
        var chain = new List<HttpMessageHandler>();
        for (; handler is not null; handler = (handler as DelegatingHandler)?.InnerHandler) chain.Add(handler);
        return chain;
    }
}
