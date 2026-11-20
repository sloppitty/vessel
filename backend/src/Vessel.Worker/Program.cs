using Vessel.Application.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vessel.Infrastructure;
using Vessel.Messaging;
using Vessel.Messaging.Modules;
using Vessel.Messaging.Modules.Monitor;
using Vessel.Messaging.Modules.Notifications;
using Vessel.Persistence.Notifications;
using Vessel.Persistence;

namespace Vessel.Worker;

internal static class WorkerProgram
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            // Keep --modules as a convenient explicit standalone selector without changing transport deployments.
            var normalized = args.Select(x => x == "--modules" ? "--Vessel:Worker:Modules:0" : x).ToArray();
            var builder = Host.CreateApplicationBuilder(normalized);
            WorkerHost.Configure(builder);
            var host = builder.Build();
            // Before any module runs: never work on a schema this image does not know, or one not yet migrated.
            var schema = await WorkerHost.SchemaCheckAsync(host.Services, CancellationToken.None);
            if (schema.Reason is { } reason)
            {
                host.Dispose();
                Console.Error.WriteLine($"Vessel worker refused to start. {reason}");
                return 1;
            }
            return await RunAsync(host);
        }
        catch (Exception error)
        {
            // Startup failures can contain database/broker connection strings. Only the type name is printed.
            Console.Error.WriteLine($"Vessel worker could not start ({error.GetType().Name}). Check private configuration, " +
                "the module list, explicit migrations and messaging provisioning.");
            return 1;
        }
    }

    /// <summary>
    /// Runs the host and returns its exit code: 0, or <see cref="WorkerExit.StaleCode"/> when the watchdog stopped it.
    /// A stale stop gets the host's shutdown timeout plus <paramref name="margin"/> (10 seconds by default); a wedged
    /// loop that also blocks the shutdown then ends the process through <paramref name="hung"/> (a fail-fast exit).
    /// </summary>
    internal static async Task<int> RunAsync(IHost host, TimeSpan? margin = null, Action<string>? hung = null)
    {
        // Resolve everything before RunAsync: it disposes the host's services when it finishes.
        var exit = host.Services.GetRequiredService<WorkerExit>();
        var grace = host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout
            + (margin ?? TimeSpan.FromSeconds(10));
        var run = host.RunAsync();
        if (await Task.WhenAny(run, exit.Requested) != run)
        {
            if (await Task.WhenAny(run, Task.Delay(grace)) != run)
            {
                (hung ?? Environment.FailFast)("Vessel worker did not stop after a stale loop; ending the process.");
                return exit.Code;
            }
        }
        await run;
        return exit.Code;
    }
}

/// <summary>
/// <c>Vessel:Worker</c>: the modules this worker runs (default <c>["monitor"]</c>) and its logical deployment, which
/// names its envelope schema and service (default: the first module). Replicas of one deployment share its schema.
/// </summary>
public sealed class WorkerSettings
{
    public const string Section = "Vessel:Worker";
    public List<string> Modules { get; set; } = [];
    public string? Deployment { get; set; }
    /// <summary>
    /// How long a module's loop may make no progress before the worker exits with <see cref="WorkerExit.StaleCode"/>
    /// (never less than that module's heartbeat timeout). See <see cref="WorkerWatchdog"/>.
    /// </summary>
    public TimeSpan StaleExitAfter { get; set; } = TimeSpan.FromMinutes(10);

    public static WorkerSettings From(IConfiguration configuration)
    {
        // A fresh instance per read: binding never appends to a populated default collection.
        var settings = new WorkerSettings();
        configuration.GetSection(Section).Bind(settings);
        if (settings.Modules.Count == 0) settings.Modules = [MonitorModule.ModuleName];
        settings.Deployment ??= settings.Modules[0];
        if (!MessagingNames.ValidName(settings.Deployment) || settings.Deployment == MessagingHosting.ApiDeployment)
            throw new InvalidOperationException("Vessel:Worker:Deployment must name a worker deployment.");
        if (settings.StaleExitAfter < TimeSpan.FromMinutes(2) || settings.StaleExitAfter > TimeSpan.FromHours(2))
            throw new InvalidOperationException("Vessel:Worker:StaleExitAfter must be between 2 minutes and 2 hours.");
        return settings;
    }
}

public static class WorkerHost
{
    /// <summary>The API readiness schema check, run once at worker start (<see cref="SchemaCompatibility"/>).</summary>
    public static async Task<SchemaCheck> SchemaCheckAsync(IServiceProvider services, CancellationToken ct)
    {
        // No owner scope exists yet, so neither context comes from one.
        var connection = services.GetRequiredService<IConfiguration>().GetConnectionString("Vessel");
        if (string.IsNullOrWhiteSpace(connection)) throw new MissingDatabaseConfigurationException();
        await using var journal = SchemaCompatibility.JournalContext(connection);
        await using var identity = await services.GetRequiredService<IDbContextFactory<Vessel.Identity.IdentityDbContext>>()
            .CreateDbContextAsync(ct);
        return await SchemaCompatibility.CheckAsync([("journal", journal), ("identity", identity)], ct);
    }

    public static void Configure(HostApplicationBuilder builder)
    {
        var worker = WorkerSettings.From(builder.Configuration);
        if (worker.Modules.Contains("paper", StringComparer.Ordinal))
        {
            if (worker.Modules.Count != 1 || worker.Deployment != "paper")
                throw new InvalidOperationException("Paper is a standalone module and deployment; it cannot be combined with transport modules.");
            builder.Services.AddSingleton(worker);
            builder.Services.AddVesselInfrastructure(builder.Configuration);
            Vessel.Identity.DependencyInjection.AddVesselIdentityDirectory(builder.Services, builder.Configuration);
            builder.Services.AddVesselPersistence(builder.Configuration);
            builder.Services.AddVesselApplication();
            builder.Services.AddPaperTrading();
            builder.Services.AddSingleton(PaperWorkerOptions.From(builder.Configuration));
            builder.Services.AddHostedService<PaperScheduler>();
            AddWatchdog(builder.Services, worker);
            return;
        }
        // An unknown module name stops startup before anything connects.
        var modules = VesselModuleRegistry.From(builder.Configuration).Select(worker.Modules);
        builder.Services.AddSingleton(worker);
        builder.Services.AddVesselInfrastructure(builder.Configuration);
        Vessel.Identity.DependencyInjection.AddVesselIdentityDirectory(builder.Services, builder.Configuration);
        builder.Services.AddVesselPersistence(builder.Configuration);
        // The same use-case registrations as the API; module-specific use cases follow per selected module.
        builder.Services.AddVesselApplication();
        foreach (var module in modules) WorkerModules.AddHostedServices(module.Name, builder.Services, builder.Configuration);
        builder.AddVesselMessaging(worker.Deployment!, modules.Select(m => m.Name));
        // Every worker runs catch-up for the subscriptions it hosts and retention for all retained owners. Both are
        // replica-safe (claims in PostgreSQL, retention serialized per owner), so replicas and deployments can overlap.
        builder.Services.AddSubscriptionMaintenance();
        AddWatchdog(builder.Services, worker);
    }

    /// <summary>Registered last, so its clock starts once every other hosted service, the broker included, has started.</summary>
    private static void AddWatchdog(IServiceCollection services, WorkerSettings worker)
    {
        services.AddSingleton<WorkerProgress>();
        services.AddSingleton<WorkerExit>();
        services.AddHostedService(sp => new WorkerWatchdog(sp.GetRequiredService<WorkerProgress>(),
            WorkerWatchdog.For(sp, worker), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<WorkerExit>(), sp.GetRequiredService<ILogger<WorkerWatchdog>>()));
    }
}

/// <summary>Worker-only registrations per module. A module without worker services needs no entry.</summary>
public static class WorkerModules
{
    public static void AddHostedServices(string module, IServiceCollection services, IConfiguration configuration)
    {
        switch (module)
        {
            case MonitorModule.ModuleName:
                services.AddVesselMonitoring(configuration);
                services.AddSingleton<MonitorWakeup>();
                services.AddSingleton<IMonitorWakeup>(sp => sp.GetRequiredService<MonitorWakeup>());
                services.AddHostedService<MonitorScheduler>();
                break;
            case NotificationsModule.ModuleName:
                services.AddVesselNotifications(configuration);
                services.AddSingleton<IMaintenanceHeartbeat, NotificationHeartbeat>();
                services.AddHostedService<NotificationRetentionService>();
                break;
        }
    }
}
