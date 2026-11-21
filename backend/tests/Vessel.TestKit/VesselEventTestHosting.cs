using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vessel.Messaging;

namespace Vessel.Tests;

internal static class VesselEventTestHosting
{
    public static void ConfigureBootstrap(IHostBuilder builder) =>
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Program validates the event database before WebApplicationFactory applies app settings.
            // No network is used: tests that need a database replace this connection string.
            ["ConnectionStrings:Vessel"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
        }));

    /// <summary>
    /// Keeps the real outbox (a <c>pg_notify</c> on the unit's own transaction) but starts no listening connection.
    /// VesselEventTransportTests exercise live delivery; these hosts drive their work directly.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services)
    {
        foreach (var listener in services.Where(d => d.ImplementationType == typeof(PostgresNotificationListener)).ToArray())
            services.Remove(listener);
    }
}
