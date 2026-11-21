using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vessel.Messaging.Modules;
using Vessel.Persistence.Events;
using Vessel.Persistence.Events.Subscriptions;

namespace Vessel.Messaging;

/// <summary>What this process is: its logical deployment and the modules it runs.</summary>
public sealed record MessagingHost(string Deployment, IReadOnlyList<IVesselModule> RunningModules)
{
    public IReadOnlyList<EventSubscription> RunningSubscriptions { get; } = RunningModules.SelectMany(m => m.Subscriptions).ToArray();

    /// <summary>Commands this host listens for.</summary>
    public IReadOnlyList<CommandQueue> ConsumedCommands { get; } =
        RunningModules.SelectMany(m => m.Commands).Where(c => c.Handler is not null).ToArray();

    public bool Consumes(Type commandType) => ConsumedCommands.Any(c => c.MessageType == commandType);

    /// <summary>Whether this host needs a listening connection.</summary>
    public bool Listens => RunningSubscriptions.Count != 0 || ConsumedCommands.Count != 0;
}

public static class MessagingHosting
{
    public const string ApiDeployment = "api";

    public static MessagingSettings Settings(IConfiguration configuration)
    {
        var settings = new MessagingSettings();
        configuration.GetSection(MessagingSettings.Section).Bind(settings);
        return settings;
    }

    /// <summary>
    /// Adds event delivery for one logical deployment: the <c>pg_notify</c> outbox every host stages on, and, only for
    /// the modules it runs, their services and one listening connection (<see cref="PostgresNotificationListener"/>).
    /// PostgreSQL is the only dependency; nothing is created at runtime.
    /// </summary>
    /// <param name="registry">The compiled-in registry by default; tests may supply test-only modules.</param>
    public static IHostApplicationBuilder AddVesselMessaging(this IHostApplicationBuilder builder, string deployment,
        IEnumerable<string>? runModules = null, VesselModuleRegistry? registry = null)
    {
        var settings = Settings(builder.Configuration);
        if (!MessagingNames.ValidName(deployment)) throw new InvalidOperationException("Invalid messaging deployment name.");
        var connection = builder.Configuration.GetConnectionString("Vessel");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:Vessel (the event database) is not configured.");
        registry ??= VesselModuleRegistry.From(settings);
        var host = new MessagingHost(deployment, registry.Select(runModules ?? []));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(host);
        builder.Services.AddVesselEvents(builder.Configuration);
        foreach (var module in host.RunningModules) module.AddRunningServices(builder.Services, builder.Configuration);
        // Fail fast on a subscription without its handler, before the listener starts.
        builder.Services.AddHostedService<EventSubscriberGuard>();
        if (host.Listens)
        {
            builder.Services.AddHostedService<PostgresNotificationListener>();
        }
        return builder;
    }

    /// <summary>Catch-up and retention sweeps. Worker hosts register this; the API never does.</summary>
    public static IServiceCollection AddSubscriptionMaintenance(this IServiceCollection services) =>
        services.AddHostedService<SubscriptionMaintenanceService>();
}

/// <summary>Stops host start when a running subscription has no keyed handler (a configuration error, not a poison event).</summary>
internal sealed class EventSubscriberGuard(MessagingHost host, IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        EventSubscriptionRegistry.EnsureHandlers(services, host.RunningSubscriptions);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
