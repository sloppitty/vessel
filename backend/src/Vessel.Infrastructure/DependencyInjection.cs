using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vessel.Infrastructure.Credentials;
using Vessel.Infrastructure.Venues;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Application.Credentials;
using Vessel.Application.Evidence;
using Vessel.Application.MarketData;
using Vessel.Application.Ownership;
using Vessel.Infrastructure.Auth;
using Vessel.Infrastructure.Evidence;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Infrastructure.Venues.Risex;
using Vessel.Infrastructure.Venues.Pacifica;

namespace Vessel.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddVesselInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<VesselAuthOptions>(config.GetSection("Vessel:Auth"));
        services.AddSingleton(TimeProvider.System);
        AddVenueHosts(services, config);
        // Venue modules register their descriptor and adapter ports; use cases find them by venue ID.
        // Catalogue caches hold public data only and outlive the scoped adapters.
        services.AddSingleton(VenueDescriptor.Manual);
        services.AddSingleton<HyperliquidCatalogueCache>();
        services.AddVenueModule(HyperliquidPerpetualReader.Descriptor, (sp, client) =>
        {
            client.BaseAddress = Hosts(sp).BaseUri(nameof(VenueHostOptions.Hyperliquid));
            client.Timeout = TimeSpan.FromSeconds(20);
        }, (sp, client) => ActivatorUtilities.CreateInstance<HyperliquidPerpetualReader>(sp, client));
        // RISEx: public reads by address, no credential. The API's firewall rejects some default user agents.
        services.AddSingleton<RisexCatalogueCache>();
        services.AddVenueModule(RisexReader.Descriptor, (sp, client) =>
        {
            client.BaseAddress = Hosts(sp).BaseUri(nameof(VenueHostOptions.Risex));
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Vessel/1.0 (self-hosted trading journal; read-only)");
        }, (sp, client) => ActivatorUtilities.CreateInstance<RisexReader>(sp, client));
        // Lighter and Lighter on Robinhood Chain: one protocol module, one configured instance per exchange, each with
        // its own client, catalogue cache and reader. The read-only token is added per request by the exchange's own
        // handler. No redirects, so Authorization cannot leave the venue host.
        foreach (var exchange in LighterExchange.All)
        {
            var id = exchange.VenueId;
            services.AddKeyedSingleton(id, (sp, _) => new LighterCatalogueCache(sp.GetRequiredService<TimeProvider>(), id));
            services.AddVenueModule(exchange.Descriptor, (sp, client) =>
            {
                client.BaseAddress = Hosts(sp).BaseUri(exchange.HostSection);
                client.Timeout = TimeSpan.FromSeconds(20);
            }, (sp, client) => new LighterReader(client, sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IAccountCredentialReader>(), sp.GetRequiredKeyedService<LighterCatalogueCache>(id), exchange),
                builder => builder.AddHttpMessageHandler(() => new LighterAuthenticationHandler(exchange.Name))
                    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }));
        }
        services.AddSingleton<PacificaCatalogueCache>();
        services.AddVenueModule(PacificaReader.Descriptor, (sp, client) =>
        {
            client.BaseAddress = Hosts(sp).BaseUri(nameof(VenueHostOptions.Pacifica));
            client.Timeout = TimeSpan.FromSeconds(20);
        }, (sp, client) => ActivatorUtilities.CreateInstance<PacificaReader>(sp, client),
            builder => builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }));
        services.AddScoped<IVenueRegistry, VenueRegistry>();
        services.AddSingleton<CandleCache>();
        services.AddSingleton<MarketContextCache>();
        services.AddSingleton<MarketStreamLimiter>();
        services.AddSingleton<IWebSocketTransportFactory, ClientWebSocketTransportFactory>();
        services.AddSingleton<IMarketStream>(sp => new HyperliquidMarketStream(sp.GetRequiredService<IWebSocketTransportFactory>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<HyperliquidMarketStream>>(),
            Hosts(sp).StreamUri(nameof(VenueHostOptions.Hyperliquid))));
        AddEvidenceStorage(services, config);
        services.AddVesselCredentials(config);
        services.AddHttpContextAccessor();
        services.AddScoped<IJournalOwnerContext, HttpJournalOwnerContext>();
        services.TryAddScoped<OwnerScopeAssignment>();
        services.TryAddSingleton<IJournalOwnerDirectory>(
            new ConfiguredOwnerDirectory(JournalOwner.Parse(config[JournalOwner.ConfigurationKey])));
        services.AddSingleton<AuthFailureLimiter>();
        if (config["Vessel:Auth:Mode"] != "Wallet")
            services.AddAuthentication(BearerTokenHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, BearerTokenHandler>(BearerTokenHandler.SchemeName, _ => { });
        services.AddAuthorization(options =>
        {
            // New endpoints are private unless they explicitly opt out, such as liveness.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
        return services;
    }

    private static readonly Type[] VenuePorts =
    [
        typeof(IPerpetualVenueReader), typeof(IVenueOrderReader), typeof(ICandleReader), typeof(IMarketContextReader),
        typeof(IVenueAccountDiscovery), typeof(IVenueCredentialVerifier)
    ];

    /// <summary>
    /// Registers a venue module: its descriptor, one logger-free HttpClient named after the venue, and every venue port
    /// the adapter implements, forwarded to one adapter per scope keyed by venue ID, so several venues may share an
    /// adapter type (Lighter exchanges). A later registration of a port wins in the registry.
    /// </summary>
    private static void AddVenueModule<TAdapter>(this IServiceCollection services, VenueDescriptor descriptor,
        Action<IServiceProvider, HttpClient> configureClient, Func<IServiceProvider, HttpClient, TAdapter> create,
        Action<IHttpClientBuilder>? configureBuilder = null) where TAdapter : class
    {
        services.AddSingleton(descriptor);
        var name = descriptor.Id;
        var client = services.AddHttpClient(name, configureClient).RemoveAllLoggers();
        configureBuilder?.Invoke(client);
        services.AddKeyedScoped(name, (sp, _) => create(sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient(name)));
        foreach (var port in VenuePorts.Where(port => port.IsAssignableFrom(typeof(TAdapter))))
            services.AddScoped(port, sp => sp.GetRequiredKeyedService<TAdapter>(name));
    }

    /// <summary>
    /// Venue hosts are configuration (<c>Vessel:Venues</c>), validated at startup so a bad value fails fast and the
    /// message names only the key. Defaults are the production hosts.
    /// </summary>
    private static void AddVenueHosts(IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<VenueHostOptions>().Bind(config.GetSection(VenueHostOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<VenueHostOptions>, VenueHostValidation>();
    }

    private static VenueHostOptions Hosts(IServiceProvider sp) => sp.GetRequiredService<IOptions<VenueHostOptions>>().Value;

    /// <summary>Shared by the API and explicit offline evidence-copy command.</summary>
    public static void AddEvidenceStorage(IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<EvidenceSettings>().Bind(config.GetSection(EvidenceSettings.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EvidenceSettings>, EvidenceStorageValidation>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<EvidenceSettings>>().Value;
            return new EvidenceLimits(settings.MaxUploadBytes, settings.MaxPerPlay);
        });
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var s3 = sp.GetRequiredService<IOptions<EvidenceSettings>>().Value.Storage.S3;
            return new AmazonS3Client(new BasicAWSCredentials(s3.AccessKeyId, s3.SecretAccessKey), new AmazonS3Config
            {
                ServiceURL = s3.Endpoint,
                AuthenticationRegion = s3.Region,
                ForcePathStyle = s3.ForcePathStyle,
                // Use SigV4 payload signing, not optional AWS-only streaming checksum trailers.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
                Timeout = TimeSpan.FromSeconds(30),
                MaxErrorRetry = 2
            });
        });
        services.AddSingleton<IEvidenceObjectStore>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<EvidenceSettings>>().Value.Storage;
            if (settings.Provider == EvidenceSettings.S3Provider)
                return new S3EvidenceObjectStore(sp.GetRequiredService<IAmazonS3>(), settings.S3.Bucket);
            var contentRoot = sp.GetRequiredService<IHostEnvironment>().ContentRootPath;
            return new LocalEvidenceObjectStore(Path.GetFullPath(settings.Local.RootPath, contentRoot));
        });
    }
}
