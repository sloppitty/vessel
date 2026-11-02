using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Vessel.Infrastructure;

namespace Vessel.Tests;

/// <summary>
/// Lighter on Robinhood Chain is a second configured instance of the Lighter module. Fixtures under
/// Fixtures/LighterRh were captured from the public, unauthenticated https://api.rh.lighter.xyz/api/v1/orderBookDetails,
/// orderBooks and assetDetails on 2026-10-14 and trimmed to a few markets (crypto, a stock, a commodity, a pre-IPO
/// market and two spot markets). No account, address or credential was used.
/// </summary>
public sealed class LighterRobinhoodIntegrationTests
{

    // A plain host, not WebApplicationFactory: when startup fails, the factory's deferred host can resolve
    // services from the host the entry point already disposed, so under parallel test load it sometimes reports
    // ObjectDisposedException instead of the validation error. The factory wiring is covered by the test above.
    [Fact]
    public async Task Invalid_configured_host_fails_startup_naming_the_key_not_the_value()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Venues:LighterRh:BaseUrl"] = "https://user:do-not-log-this@rh.example.test/"
        });
        builder.Services.AddVesselInfrastructure(builder.Configuration);
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        var message = error.ToString();
        Assert.Contains("Vessel:Venues:LighterRh:BaseUrl", message);
        Assert.DoesNotContain("do-not-log-this", message);
    }
}
