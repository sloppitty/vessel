using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Messaging;
using Vessel.Messaging.Modules.Monitor;
using Vessel.Messaging.Modules.Verification;
using Vessel.Persistence;
using Vessel.Worker;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Vessel.Tests;

public sealed class WorkerHostTests
{
    [Fact]
    public void Worker_runs_the_monitor_module_by_default_in_its_own_deployment()
    {
        var settings = WorkerSettings.From(new ConfigurationBuilder().Build());
        Assert.Equal([MonitorModule.ModuleName], settings.Modules);
        Assert.Equal(MonitorModule.ModuleName, settings.Deployment);
        var configured = WorkerSettings.From(Configuration(new()
        {
            ["Vessel:Worker:Modules:0"] = "verification", ["Vessel:Worker:Deployment"] = "checks"
        }));
        Assert.Equal(["verification"], configured.Modules);
        Assert.Equal("checks", configured.Deployment);
        Assert.Throws<InvalidOperationException>(() => WorkerSettings.From(Configuration(new() { ["Vessel:Worker:Deployment"] = "api" })));
    }

    [Fact]
    public void An_unknown_worker_module_stops_startup()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddConfiguration(Configuration(new()
        {
            ["Vessel:Worker:Modules:0"] = "nonexistent",
            ["Vessel:Messaging:RabbitMqUri"] = "amqp://unused:unused@127.0.0.1:1",
            ["ConnectionStrings:Vessel"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
        }));
        var error = Assert.Throws<InvalidOperationException>(() => WorkerHost.Configure(builder));
        Assert.Contains("Unknown messaging module", error.Message);
    }

    [Fact]
    public void Provisioning_knows_every_deployment_and_its_module_set()
    {
        var settings = new MessagingSettings { Verification = true };
        var registry = Vessel.Messaging.Modules.VesselModuleRegistry.From(settings);
        var defaults = settings.DeploymentModules(registry);
        Assert.Equal(["api", "monitor", "notifications", "verification"], defaults.Keys);
        Assert.Equal(["notifications"], defaults["notifications"]);
        Assert.Empty(defaults["api"]);
        Assert.Equal(["monitor"], defaults["monitor"]);
        settings.Deployments["monitor"] = ["verification", "monitor"];
        Assert.Equal(["monitor", "verification"], settings.DeploymentModules(registry)["monitor"]);
        settings.Deployments["monitor"] = ["nonexistent"];
        Assert.Throws<InvalidOperationException>(() => settings.DeploymentModules(registry));
        settings.Deployments.Clear();
        settings.Deployments["api"] = ["monitor"];
        Assert.Throws<InvalidOperationException>(() => settings.DeploymentModules(registry));
    }

    [VesselEventRabbitFact]
    public async Task Startup_rejects_a_schema_provisioned_for_another_module_set()
    {
        await using var broker = await BrokerScope.CreateAsync();
        // Not provisioned yet: no deployment may start, and no schema is created at runtime.
        using (var early = Worker(broker, MonitorModule.ModuleName))
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => early.StartAsync());
        await broker.ProvisionAsync();
        // The monitor schema was provisioned for ["monitor"]; a host running other modules in it must not start.
        using (var wrong = Worker(broker, VerificationModule.ModuleName, deployment: MonitorModule.ModuleName))
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => wrong.StartAsync());
        // Changing a deployment's module set at provisioning is rejected too, until its schema is drained.
        var changed = new ConfigurationBuilder().AddConfiguration(broker.Configuration).AddInMemoryCollection(
            new Dictionary<string, string?> { ["Vessel:Messaging:Deployments:monitor:0"] = VerificationModule.ModuleName }).Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MessagingProvisioning.ProvisionAsync(changed, default, broker.Registry));
        Assert.Contains("another deployment or module set", error.Message);
        using var worker = Worker(broker, MonitorModule.ModuleName);
        await worker.StartAsync();
        await worker.StopAsync();
    }

    [VesselEventRabbitFact]
    public async Task Unknown_monitor_owner_dead_letters_while_active_owners_run_and_suspended_commands_are_acknowledged()
    {
        await using var broker = await BrokerScope.CreateAsync();
        await broker.ProvisionAsync();
        var api = await broker.StartAsync(MessagingHosting.ApiDeployment);
        var owners = new MutableOwnerDirectory();
        owners.Set(broker.Owner, JournalOwnerStatus.Active);
        var other = Guid.NewGuid(); var suspended = Guid.NewGuid();
        owners.Set(other, JournalOwnerStatus.Active);
        owners.Set(suspended, JournalOwnerStatus.Suspended);
        var activeCommand = await PendingCommandAsync(broker.Database, other);
        var suspendedCommand = await PendingCommandAsync(broker.Database, suspended);
        var logs = new BackgroundWorkLogs();
        var wakeups = new ConcurrentBag<Guid>();
        using var worker = Worker(broker, MonitorModule.ModuleName, logs: logs, owners: owners, wakeups: wakeups);
        await worker.StartAsync();
        var bus = api.Services.GetRequiredService<IMessageBus>();
        var queue = broker.Topology.CommandQueue(MonitorModule.ModuleName);
        await bus.SendAsync(new MonitorAccountCommand(Guid.CreateVersion7(), Guid.NewGuid(), Guid.NewGuid(), "manual"));
        await broker.EventuallyAsync(async () => await broker.ReadyAsync(MessagingTopology.DeadLetterQueue(queue)) == 1);
        await bus.SendAsync(new MonitorAccountCommand(Guid.CreateVersion7(), Guid.NewGuid(), broker.Owner, "manual"));
        await bus.SendAsync(activeCommand);
        await bus.SendAsync(suspendedCommand);
        await broker.EventuallyAsync(() => Task.FromResult(wakeups.Contains(other) && logs.Messages.Any(m =>
            m.Text.Contains(suspendedCommand.RequestId.ToString(), StringComparison.Ordinal) &&
            m.Text.Contains("was dropped", StringComparison.Ordinal))));
        await worker.StopAsync();
        Assert.Equal([other], wakeups.ToArray());
        await using (var db = broker.Database.Context(suspended))
        {
            var pending = await db.Set<MonitorAccount>().SingleAsync();
            Assert.Equal(suspendedCommand.RequestId, pending.RequestId);
            Assert.Equal("pending", pending.ManualState);
        }
        Assert.Equal(0u, await broker.ReadyAsync(queue));
        Assert.Equal(1u, await broker.ReadyAsync(MessagingTopology.DeadLetterQueue(queue)));
    }

    [PostgresFact]
    public async Task Monitor_command_directory_failure_is_acknowledged_and_pending_work_survives()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var command = await PendingCommandAsync(database, owner);
        var directory = new MutableOwnerDirectory
        {
            Checking = (_, _) => throw new InvalidOperationException("Synthetic private directory failure")
        };
        var services = new ServiceCollection();
        var logs = new BackgroundWorkLogs();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddVesselPersistence(Configuration(new() { ["ConnectionStrings:Vessel"] = database.ConnectionString }));
        await using var provider = services.BuildServiceProvider();
        await MonitorCommandListener.Handle(command, provider.GetRequiredService<IOwnerScopeFactory>(), directory,
            provider.GetRequiredService<ILogger<MonitorCommandListener>>(), default);
        await using var db = database.Context(owner);
        var pending = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal((command.RequestId, "pending"), (pending.RequestId, pending.ManualState));
        Assert.Contains(logs.Messages, m => m.Level == LogLevel.Warning &&
            m.Text.Contains("acknowledged after InvalidOperationException", StringComparison.Ordinal) &&
            m.Text.Contains(command.RequestId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Text.Contains("Synthetic private", StringComparison.Ordinal));
    }

    [VesselEventRabbitFact]
    public async Task Command_queue_is_published_durably_by_the_api_and_listened_inline_by_its_module()
    {
        await using var broker = await BrokerScope.CreateAsync();
        await broker.ProvisionAsync();
        var logs = new BackgroundWorkLogs();
        var api = broker.Build(MessagingHosting.ApiDeployment, [], logs);
        await api.StartAsync();
        using var worker = Worker(broker, MonitorModule.ModuleName, logs: logs);
        await worker.StartAsync();
        var queue = new Uri($"rabbitmq://queue/{broker.Topology.CommandQueue(MonitorModule.ModuleName)}");

        // The API only publishes: through the durable outbox, never inline.
        var published = Endpoint(api, queue);
        Assert.False(published.IsListener);
        Assert.Equal(EndpointMode.Durable, published.Mode);
        Assert.Contains(Routes(api), route => route == queue);

        // The worker only listens, inline, so a delivery is acknowledged after the handler settles. It declares no
        // publishing side for the same endpoint, so no second mode request can replace this one.
        var listened = Endpoint(worker, queue);
        Assert.True(listened.IsListener);
        Assert.Equal(EndpointMode.Inline, listened.Mode);
        Assert.DoesNotContain(Routes(worker), route => route == queue);
        // Nothing in the worker may stage the command it consumes: it would bypass the API's durable outbox.
        await using (var scope = worker.Services.GetRequiredService<IOwnerScopeFactory>().Create(broker.Owner))
        {
            var outbox = scope.Services.GetRequiredService<Vessel.Persistence.Events.IVesselEventOutbox>();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                outbox.StageAsync(new MonitorAccountCommand(Guid.CreateVersion7(), Guid.NewGuid(), broker.Owner, "manual"), default));
            Assert.Contains("consumes", error.Message);
        }
        await worker.StopAsync();
        await api.StopAsync();

        // Wolverine validates every listener at startup and warns when both sides asked for different modes.
        Assert.Contains(logs.Messages, m => m.Category.StartsWith("Wolverine", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Text.Contains("Conflicting endpoint mode", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Level >= LogLevel.Warning && m.Text.Contains("commands.", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Review_M2_heartbeat_advances_only_after_a_productive_scheduling_pass()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var file = Path.Combine(Path.GetTempPath(), "vessel-heartbeat-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var healthy = Scheduler(database.ConnectionString, owner, file, out var scheduler))
            {
                await scheduler.TickAsync(default);
                var parts = (await File.ReadAllTextAsync(file)).Split(' ');
                Assert.Equal(2, parts.Length);
                Assert.Equal("90", parts[1]);
                await using var db = database.Context(owner);
                Assert.NotNull(await MonitorTestPaths.Store(db, TestUnitOfWork.For(db)).HeartbeatAsync(default));
            }
            // Discovery cannot run: the pass is not productive, so neither heartbeat advances.
            await File.WriteAllTextAsync(file, "1 90");
            using (var broken = Scheduler("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused", owner, file, out var scheduler))
                await Assert.ThrowsAnyAsync<Exception>(() => scheduler.TickAsync(default));
            Assert.Equal("1 90", await File.ReadAllTextAsync(file));
            // The heartbeat file cannot be written: the database heartbeat is not recorded either.
            var other = Guid.NewGuid();
            var missing = Path.Combine(Path.GetTempPath(), "vessel-missing-" + Guid.NewGuid().ToString("N"), "heartbeat");
            await using var beforeContext = database.Context(owner);
            var before = await MonitorServerState.For(beforeContext).HeartbeatAsync(default);
            using (var unwritable = Scheduler(database.ConnectionString, other, missing, out var scheduler))
                await Assert.ThrowsAnyAsync<IOException>(() => scheduler.TickAsync(default));
            await using var foreign = database.Context(other);
            Assert.Equal(before, await MonitorServerState.For(foreign).HeartbeatAsync(default));
        }
        finally { File.Delete(file); }
    }

    private static IHost Scheduler(string connection, Guid owner, string file, out MonitorScheduler scheduler)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddConfiguration(Configuration(new()
        {
            ["ConnectionStrings:Vessel"] = connection,
            ["Vessel:Auth:OwnerId"] = owner.ToString(),
            ["Vessel:Identity:Origins:0"] = TestAuthentication.Origin,
            ["Vessel:Messaging:RabbitMqUri"] = "amqp://unused:unused@127.0.0.1:1",
            ["Vessel:Monitor:HeartbeatFile"] = file
        }));
        builder.Logging.ClearProviders();
        WorkerHost.Configure(builder);
        VesselEventTestHosting.ConfigureServices(builder.Services);
        // No identity schema here: the configured owner is the only active one.
        builder.Services.Replace(ServiceDescriptor.Singleton<IJournalOwnerDirectory>(new ConfiguredOwnerDirectory(owner)));
        var host = builder.Build();
        scheduler = host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();
        return host;
    }

    private static Wolverine.Configuration.Endpoint Endpoint(IHost host, Uri uri) =>
        host.Services.GetRequiredService<IWolverineRuntime>().Options.Transports.TryGetEndpoint(uri)
        ?? throw new InvalidOperationException($"No endpoint {uri}.");

    private static Uri[] Routes(IHost host) => host.Services.GetRequiredService<IWolverineRuntime>()
        .RoutingFor(typeof(MonitorAccountCommand)).Routes.OfType<MessageRoute>().Select(r => r.Uri).ToArray();

    private static IHost Worker(BrokerScope broker, string module, string? deployment = null, ILoggerProvider? logs = null,
        IJournalOwnerDirectory? owners = null, ConcurrentBag<Guid>? wakeups = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddConfiguration(broker.Configuration);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Messaging:Verification"] = "true",
            ["Vessel:Worker:Modules:0"] = module,
            ["Vessel:Worker:Deployment"] = deployment ?? module,
            ["Vessel:Monitor:HeartbeatFile"] = Path.Combine(Path.GetTempPath(), "vessel-worker-test-" + Guid.NewGuid().ToString("N"))
        });
        builder.Logging.ClearProviders();
        if (logs is not null) builder.Logging.AddProvider(logs);
        WorkerHost.Configure(builder);
        if (owners is not null) builder.Services.Replace(ServiceDescriptor.Singleton(owners));
        if (wakeups is not null)
        {
            // Exercise the real command handler, but never start venue reads for these pending synthetic requests.
            builder.Services.Remove(builder.Services.Single(d => d.ImplementationType == typeof(MonitorScheduler)));
            builder.Services.Replace(ServiceDescriptor.Scoped<IMonitorWakeup>(sp =>
                new RecordingWakeup(sp.GetRequiredService<IJournalOwnerContext>(), wakeups)));
        }
        return builder.Build();
    }

    private static async Task<MonitorAccountCommand> PendingCommandAsync(CoreDatabase database, Guid owner)
    {
        await using var db = database.Context(owner);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic pending command");
        var command = new MonitorAccountCommand(Guid.CreateVersion7(), account.Id, owner, "manual");
        var row = new MonitorAccount { OwnerId = owner, AccountId = account.Id };
        row.Admit(command.RequestId, command.Trigger, DateTimeOffset.UtcNow);
        db.AddRange(account, row);
        await db.SaveChangesAsync();
        return command;
    }

    private sealed class RecordingWakeup(IJournalOwnerContext owner, ConcurrentBag<Guid> wakeups) : IMonitorWakeup
    {
        public void Wake() => wakeups.Add(owner.OwnerId);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
