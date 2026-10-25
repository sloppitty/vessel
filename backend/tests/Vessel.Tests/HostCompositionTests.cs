using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel.Application.Credentials;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Workspace;
using Vessel.Infrastructure.Monitoring;
using Vessel.Messaging.Modules.Verification;
using Vessel.Worker;

namespace Vessel.Tests;

/// <summary>
/// Both hosts share one application composition: their containers validate on build and resolve the same
/// implementations for the use cases the monitor depends on. Nothing connects; the hosts are never started.
/// </summary>
public sealed class HostCompositionTests
{
    private const string UnusedDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    private static readonly Type[] MonitorServices =
    [
        typeof(MonitorCoordinator), typeof(MonitorRunExecutor), typeof(MonitorExecutionView), typeof(MonitorDiscrepancyService),
        typeof(IMonitorEvaluator), typeof(IMonitorImporter), typeof(IMonitorStore), typeof(IMonitorDiscrepancyStore),
        typeof(IMonitorRunLog), typeof(MonitorOptions)
    ];

    private static readonly Type[] SharedServices =
    [
        typeof(WorkspaceService), typeof(PlayExecutionService), typeof(IAccountCredentialReader), typeof(AccountCredentialReader),
        typeof(IUnitOfWork), typeof(IWorkspaceStore)
    ];

    [Fact]
    public void Api_and_monitor_worker_resolve_the_same_collaborators()
    {
        var owner = Guid.NewGuid();
        using var api = new CoreApiFactory(owner, connection: UnusedDatabase).WithWebHostBuilder(builder =>
            builder.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; }));
        using var worker = Worker(owner, modules: null);
        using var apiScope = api.Services.CreateScope();
        using var workerScope = worker.Services.CreateScope();
        foreach (var type in SharedServices.Concat(MonitorServices))
        {
            var fromApi = apiScope.ServiceProvider.GetRequiredService(type);
            var fromWorker = workerScope.ServiceProvider.GetRequiredService(type);
            Assert.True(fromApi.GetType() == fromWorker.GetType(), $"{type.Name}: API {fromApi.GetType().Name}, worker {fromWorker.GetType().Name}");
        }
        // The monitor importer is the scope's own execution service, not a second instance.
        Assert.Same(workerScope.ServiceProvider.GetRequiredService<PlayExecutionService>(),
            workerScope.ServiceProvider.GetRequiredService<IMonitorImporter>());
        Assert.Same(workerScope.ServiceProvider.GetRequiredService<MonitorDiscrepancyService>(),
            workerScope.ServiceProvider.GetRequiredService<IMonitorEvaluator>());
    }

    [Fact]
    public void A_worker_without_the_monitor_module_registers_no_monitor_use_cases()
    {
        using var worker = Worker(Guid.NewGuid(), modules: VerificationModule.ModuleName);
        using var scope = worker.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<WorkspaceService>());
        Assert.Null(scope.ServiceProvider.GetService<MonitorCoordinator>());
        Assert.Null(scope.ServiceProvider.GetService<MonitorRunExecutor>());
        Assert.Null(scope.ServiceProvider.GetService<MonitorOptions>());
        Assert.DoesNotContain(worker.Services.GetServices<IHostedService>(), service => service is MonitorScheduler);
    }

    private static IHost Worker(Guid owner, string? modules)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = UnusedDatabase,
            ["Vessel:Auth:OwnerId"] = owner.ToString(),
            ["Vessel:Identity:Origins:0"] = TestAuthentication.Origin,
            ["Vessel:Messaging:RabbitMqUri"] = "amqp://unused:unused@127.0.0.1:1",
            ["Vessel:Messaging:Verification"] = "true",
            ["Vessel:Monitor:HeartbeatFile"] = Path.Combine(Path.GetTempPath(), "vessel-composition-test-" + Guid.NewGuid().ToString("N"))
        };
        if (modules is not null) settings["Vessel:Worker:Modules:0"] = modules;
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        WorkerHost.Configure(builder);
        VesselEventTestHosting.ConfigureServices(builder.Services);
        // As in Development: every registration must be constructible, and every Vessel service is then resolved.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
        var vessel = builder.Services
            .Where(d => !d.IsKeyedService && !d.ServiceType.ContainsGenericParameters &&
                d.ServiceType.Namespace?.StartsWith("Vessel.", StringComparison.Ordinal) == true)
            .Select(d => d.ServiceType).Distinct().ToArray();
        var host = builder.Build();
        using var scope = host.Services.CreateScope();
        foreach (var type in vessel) Assert.NotNull(scope.ServiceProvider.GetService(type));
        return host;
    }
}
