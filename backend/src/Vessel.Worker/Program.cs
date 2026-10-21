using Vessel.Application.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
            await builder.Build().RunAsync();
            return 0;
        }
        catch (Exception error)
        {
            // Startup failures can contain database/broker connection strings. Only the type name is printed.
            Console.Error.WriteLine($"Vessel worker could not start ({error.GetType().Name}). Check private configuration, " +
                "the module list, explicit migrations and messaging provisioning.");
            return 1;
        }
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

    public static WorkerSettings From(IConfiguration configuration)
    {
        // A fresh instance per read: binding never appends to a populated default collection.
        var settings = new WorkerSettings();
        configuration.GetSection(Section).Bind(settings);
        if (settings.Modules.Count == 0) settings.Modules = [MonitorModule.ModuleName];
        settings.Deployment ??= settings.Modules[0];
        if (!MessagingNames.ValidName(settings.Deployment) || settings.Deployment == MessagingHosting.ApiDeployment)
            throw new InvalidOperationException("Vessel:Worker:Deployment must name a worker deployment.");
        return settings;
    }
}

public static class WorkerHost
{
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
