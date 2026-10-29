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
    string environment = "Development", bool testAuthentication = true) : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        VesselEventTestHosting.ConfigureBootstrap(builder);
        // Production validation runs before ConfigureAppConfiguration on a minimal host.
        // Supply synthetic bootstrap settings early; never change the process environment.
        if (environment != "Development")
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vessel:Auth:OwnerId"] = owner.ToString(),
                ["Vessel:Identity:Origins:0"] = TestAuthentication.Origin,
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
            ["Vessel:Auth:OwnerId"] = owner.ToString(),
            ["Vessel:Identity:Origins:0"] = TestAuthentication.Origin,
            ["Vessel:Evidence:Storage:Provider"] = "Local",
            ["ConnectionStrings:Vessel"] = connection
        }.Concat(settings ?? []).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value)));
        builder.ConfigureServices(services =>
        {
            VesselEventTestHosting.ConfigureServices(services);
            if (testAuthentication)
            {
                TestAuthentication.Add(services);
                // Test-scheme owners are no identity users: like the installation before wallets, only the owner is known.
                services.RemoveAll<Vessel.Application.Ownership.IJournalOwnerDirectory>();
                services.AddSingleton<Vessel.Application.Ownership.IJournalOwnerDirectory>(new ConfiguredOwnerDirectory(owner));
            }
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
    /// <summary>A client signed in as the factory's owner through the test scheme.</summary>
    public HttpClient AuthorizedClient() => AuthorizedClient(owner);

    /// <summary>A client signed in as <paramref name="as"/> through the test scheme.</summary>
    public HttpClient AuthorizedClient(Guid @as)
    {
        var client = CreateClient(); TestAuthentication.SignIn(client.DefaultRequestHeaders, @as); return client;
    }

    /// <summary>Signs one request in as the factory's owner.</summary>
    public void SignIn(HttpRequestMessage request) => TestAuthentication.SignIn(request.Headers, owner);
}
