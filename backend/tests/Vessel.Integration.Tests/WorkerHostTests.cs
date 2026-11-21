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
using Vessel.Persistence.Events;
using Vessel.Worker;
using Vessel.Domain.Common;

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
            ["ConnectionStrings:Vessel"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
        }));
        var error = Assert.Throws<InvalidOperationException>(() => WorkerHost.Configure(builder));
        Assert.Contains("Unknown messaging module", error.Message);
    }

    [VesselEventPostgresFact]
    public async Task Monitor_worker_starts_with_only_postgresql_and_listens_on_its_own_connection()
    {
        await using var scope = await DeliveryScope.CreateAsync();
        // Neither provisioning nor a broker is needed to start: the worker's only dependency is PostgreSQL.
        using var worker = Worker(scope, MonitorModule.ModuleName);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await worker.StartAsync();
        var listener = DeliveryScope.Listener(worker)!;
        await scope.EventuallyAsync(() => Task.FromResult(listener.Connections == 1));
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Assert.Equal(1, await scope.CountAsync(
            $"SELECT count(*) FROM pg_stat_activity WHERE pid={listener.ProcessId} AND application_name='vessel-listen-monitor'"));
        await worker.StopAsync();
        // The API listens to nothing.
        Assert.Null(DeliveryScope.Listener(scope.Build(MessagingHosting.ApiDeployment, [])));
    }

    [VesselEventPostgresFact]
    public async Task Unknown_monitor_owner_is_dropped_while_active_owners_run_and_suspended_commands_are_ignored()
    {
        await using var scope = await DeliveryScope.CreateAsync();
        var owners = new MutableOwnerDirectory();
        owners.Set(scope.Owner, JournalOwnerStatus.Active);
        var other = Guid.NewGuid(); var suspended = Guid.NewGuid();
        owners.Set(other, JournalOwnerStatus.Active);
        owners.Set(suspended, JournalOwnerStatus.Suspended);
        var activeCommand = await PendingCommandAsync(scope.Database, other);
        var suspendedCommand = await PendingCommandAsync(scope.Database, suspended);
        var logs = new BackgroundWorkLogs();
        var wakeups = new ConcurrentBag<Guid>();
        using var worker = Worker(scope, MonitorModule.ModuleName, logs: logs, owners: owners, wakeups: wakeups);
        await worker.StartAsync();
        await scope.EventuallyAsync(() => Task.FromResult(DeliveryScope.Listener(worker)!.Connections == 1));
        var unknown = new MonitorAccountCommand(Guid.CreateVersion7(), Guid.NewGuid(), Guid.NewGuid(), "manual");
        await scope.NotifyAsync(VesselNotifications.MonitorCommands, VesselNotifications.CommandPayload(unknown));
        await scope.NotifyAsync(VesselNotifications.MonitorCommands, "{\"requestId\":");
        await scope.NotifyAsync(VesselNotifications.MonitorCommands,
            VesselNotifications.CommandPayload(new(Guid.CreateVersion7(), Guid.NewGuid(), scope.Owner, "manual")));
        await scope.NotifyAsync(VesselNotifications.MonitorCommands, VesselNotifications.CommandPayload(activeCommand));
        await scope.NotifyAsync(VesselNotifications.MonitorCommands, VesselNotifications.CommandPayload(suspendedCommand));
        await scope.EventuallyAsync(() => Task.FromResult(wakeups.Contains(other) && logs.Messages.Any(m =>
            m.Text.Contains(suspendedCommand.RequestId.ToString(), StringComparison.Ordinal) &&
            m.Text.Contains("was dropped", StringComparison.Ordinal))));
        await worker.StopAsync();
        Assert.Equal([other], wakeups.ToArray());
        await using (var db = scope.Database.Context(suspended))
        {
            var pending = await db.Set<MonitorAccount>().SingleAsync();
            Assert.Equal(suspendedCommand.RequestId, pending.RequestId);
            Assert.Equal("pending", pending.ManualState);
        }
        Assert.Contains(logs.Messages, m => m.Level == LogLevel.Warning &&
            m.Text.Contains(unknown.RequestId.ToString(), StringComparison.Ordinal) && m.Text.Contains("unknown owner", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, m => m.Level == LogLevel.Warning && m.Text.Contains("could not be read", StringComparison.Ordinal));
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
        await new MonitorCommandListener(provider.GetRequiredService<IOwnerScopeFactory>(), directory,
            provider.GetRequiredService<ILogger<MonitorCommandListener>>()).HandleAsync(command, default);
        await using var db = database.Context(owner);
        var pending = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal((command.RequestId, "pending"), (pending.RequestId, pending.ManualState));
        Assert.Contains(logs.Messages, m => m.Level == LogLevel.Warning &&
            m.Text.Contains("dropped after InvalidOperationException", StringComparison.Ordinal) &&
            m.Text.Contains(command.RequestId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Text.Contains("Synthetic private", StringComparison.Ordinal));
    }

    [VesselEventPostgresFact]
    public async Task A_monitor_request_notifies_the_worker_only_when_its_pending_row_commits()
    {
        await using var scope = await DeliveryScope.CreateAsync();
        var api = await scope.StartAsync(MessagingHosting.ApiDeployment);
        var wakeups = new ConcurrentBag<Guid>();
        using var worker = Worker(scope, MonitorModule.ModuleName, owners: scope.Owners, wakeups: wakeups);
        await worker.StartAsync();
        await scope.EventuallyAsync(() => Task.FromResult(DeliveryScope.Listener(worker)!.Connections == 1));
        var command = await PendingCommandAsync(scope.Database, scope.Owner);
        async Task EnqueueAsync(bool commit)
        {
            await using var owned = api.Services.GetRequiredService<IOwnerScopeFactory>().Create(scope.Owner);
            var sink = owned.Services.GetRequiredService<IMonitorCommandSink>();
            var error = await Record.ExceptionAsync(() => owned.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(async () =>
            {
                await sink.EnqueueAsync(command, default);
                if (!commit) throw new InvalidOperationException("Synthetic rollback.");
                return true;
            }, default));
            Assert.Equal(commit, error is null);
        }
        await EnqueueAsync(commit: false);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(wakeups);
        await EnqueueAsync(commit: true);
        await scope.EventuallyAsync(() => Task.FromResult(wakeups.Contains(scope.Owner)));
        // The worker may request work too: there is no route that only the API may use.
        await using (var owned = worker.Services.GetRequiredService<IOwnerScopeFactory>().Create(scope.Owner))
            await owned.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(async () =>
            {
                await owned.Services.GetRequiredService<IMonitorCommandSink>().EnqueueAsync(command, default);
                return true;
            }, default);
        await scope.EventuallyAsync(() => Task.FromResult(wakeups.Count(o => o == scope.Owner) == 2));
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

    private static IHost Worker(DeliveryScope broker, string module, string? deployment = null, ILoggerProvider? logs = null,
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
        var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic pending command", TradingMode.Real);
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
