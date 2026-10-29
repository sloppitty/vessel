using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vessel.Application.Events;
using Vessel.Persistence.Events;
using Wolverine;

namespace Vessel.Tests;

internal static class VesselEventTestHosting
{
    public static void ConfigureBootstrap(IHostBuilder builder) =>
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Program validates transport configuration before WebApplicationFactory applies app settings.
            // No network is used: ConfigureServices explicitly replaces transport for these API tests.
            ["Vessel:Messaging:RabbitMqUri"] = "amqp://unused:unused@127.0.0.1:1",
            ["ConnectionStrings:Vessel"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
        }));

    public static void ConfigureServices(IServiceCollection services)
    {
        services.DisableAllExternalWolverineTransports();
        services.DisableAllWolverineMessagePersistence();
        // No database at startup: the deployment registration check belongs to the broker tests.
        foreach (var guard in services.Where(d => d.ImplementationType == typeof(Vessel.Messaging.MessagingDeploymentGuard)).ToArray())
            services.Remove(guard);
        services.Replace(ServiceDescriptor.Scoped<IVesselEventOutbox, NonTransportOutbox>());
    }

    // Retains real mutation capture and event history. Only network publication is replaced;
    // VesselEventTransportTests exercise the actual broker, outbox and subscription settlement.
    private sealed class NonTransportOutbox : IVesselEventOutbox
    {
        public Task StageAsync(VesselEvent message, CancellationToken ct) => Task.CompletedTask;
        public Task StageAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }
}
