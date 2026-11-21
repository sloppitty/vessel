using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Messaging;
using Vessel.Messaging.Modules;
using Vessel.Messaging.Modules.Monitor;
using Vessel.Messaging.Modules.Verification;
using Vessel.Persistence;
using Vessel.Persistence.Events;
using Vessel.Persistence.Events.Subscriptions;
using Vessel.Domain.Common;
using Xunit.Abstractions;

namespace Vessel.Tests;

/// <summary>
/// Issue #99: live delivery through PostgreSQL <c>LISTEN/NOTIFY</c>. Proves the notification commits with the event,
/// that live delivery settles every row of the settlement table, that publishers know nothing about subscribers, and
/// that catch-up from PostgreSQL recovers what a lost notification or listening connection missed.
/// </summary>
[Collection("Monitor transport")]
public sealed class VesselEventTransportTests(ITestOutputHelper output)
{
    [VesselEventPostgresFact]
    public async Task Event_and_notification_commit_together_and_reach_a_verification_subscription_once()
    {
        await using var scope = await DeliveryScope.CreateAsync();
        await scope.ProvisionAsync();
        var api = await scope.StartAsync(MessagingHosting.ApiDeployment);
        Assert.Null(DeliveryScope.Listener(api));
        await using var probe = await NotificationProbe.ListenAsync(scope.Database.ConnectionString, VesselNotifications.Events);
        await using var services = api.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<VesselDbContext>();
        var sink = services.ServiceProvider.GetRequiredService<VesselEventStore>();
        var draft = Draft("transport-test");
        var rollback = true;
        var verifyStage = new CommitProbe { Before = () =>
        {
            if (rollback) throw new InvalidOperationException("Synthetic rollback after the notification was staged.");
            return Task.CompletedTask;
        }};
        var work = new UnitOfWork(db, [sink, verifyStage]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            await sink.AppendAsync(draft, default);
            return true;
        }, default));
        // A rollback drops the staged notification with the event.
        Assert.Empty(await probe.ReceiveAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, await scope.CountAsync("SELECT count(*) FROM events"));
        rollback = false;
        var worker = await scope.StartAsync(VerificationModule.ModuleName, VerificationModule.ModuleName);
        await work.ExecuteAsync(async () => { await sink.AppendAsync(draft, default); return true; }, default);
        var original = Assert.Single((await EventReads.ReadAsync(db, 0, 200, default)).Events);
        Assert.Equal([VesselNotifications.EventPayload(original)], await probe.ReceiveAsync(TimeSpan.FromSeconds(5)));
        await scope.EventuallyAsync(async () => await scope.ReceiptAsync(VerificationModule.SubscriptionName, original.Id) == ("delivered", 1));
        // Publication wrote no per-subscriber rows; the only receipt is the subscription's own record.
        Assert.Equal(1, await scope.CountAsync("SELECT count(*) FROM event_receipts"));
        // A repeated notification with the SAME business event ID is answered as settled, without a second effect.
        await scope.NotifyAsync(VesselNotifications.Events, VesselNotifications.EventPayload(original));
        var barrier = await scope.AppendAsync(api, Draft("barrier"));
        await scope.EventuallyAsync(async () => await scope.ReceiptAsync(VerificationModule.SubscriptionName, barrier.Id) == ("delivered", 1));
        Assert.Equal(("delivered", 1), await scope.ReceiptAsync(VerificationModule.SubscriptionName, original.Id));
        Assert.Equal(1, DeliveryScope.Listener(worker)!.Connections);
    }

    [VesselEventPostgresFact]
    public async Task Live_delivery_settles_every_row_of_the_settlement_table()
    {
        var scripted = new ScriptedModule("scripted", new() { MaxAttempts = 2 });
        await using var scope = await DeliveryScope.CreateAsync(scripted);
        await scope.ProvisionAsync();
        var logs = new BackgroundWorkLogs();
        var api = await scope.StartAsync(MessagingHosting.ApiDeployment);
        await scope.StartAsync("scripted", ["scripted"], logs);
        var ok = await scope.AppendAsync(api, Draft("ok"));
        var retry = await scope.AppendAsync(api, Draft("retry"));
        var terminal = await scope.AppendAsync(api, Draft("terminal"));
        // Success: effects and terminal receipt commit together. Retryable: the attempt is recorded. Terminal: the
        // receipt commits failed and the outcome is logged.
        await scope.EventuallyAsync(async () => await scope.ReceiptAsync("scripted", ok.Id) == ("delivered", 1) &&
            await scope.ReceiptAsync("scripted", retry.Id) == ("pending", 1) &&
            await scope.ReceiptAsync("scripted", terminal.Id) == ("failed", 1));
        Assert.True(await scope.CountAsync($"SELECT count(*) FROM event_receipts WHERE event_id='{retry.Id}' AND next_attempt_at>now()") == 1);

        // Invalid owner and unknown event: no receipt, logged. Garbage is logged and ignored.
        var foreign = ok with { Id = Guid.CreateVersion7(), OwnerId = Guid.NewGuid() };
        await scope.NotifyAsync(VesselNotifications.Events, VesselNotifications.EventPayload(foreign));
        var unknown = ok with { Id = Guid.CreateVersion7() };
        await scope.NotifyAsync(VesselNotifications.Events, VesselNotifications.EventPayload(unknown));
        await scope.NotifyAsync(VesselNotifications.Events, "not a notification");
        // Known historical: a pruned event keeps its dedup identity, so its late duplicate changes nothing.
        await scope.ExecuteAsync($"DELETE FROM events WHERE event_id='{ok.Id}'");
        await scope.NotifyAsync(VesselNotifications.Events, VesselNotifications.EventPayload(ok));
        // Already settled: a duplicate of the failed delivery changes nothing.
        await scope.NotifyAsync(VesselNotifications.Events, VesselNotifications.EventPayload(terminal));
        // Notifications of one channel are handled in order, so a later delivery is a barrier.
        var barrier = await scope.AppendAsync(api, Draft("barrier"));
        await scope.EventuallyAsync(async () => await scope.ReceiptAsync("scripted", barrier.Id) == ("delivered", 1));
        Assert.Equal(0, await scope.CountAsync($"SELECT count(*) FROM event_receipts WHERE event_id IN ('{foreign.Id}','{unknown.Id}')"));
        Assert.Equal(("failed", 1), await scope.ReceiptAsync("scripted", terminal.Id));
        Assert.Equal(("pending", 1), await scope.ReceiptAsync("scripted", retry.Id));
        Assert.Equal(1, scripted.Subscriber.Calls(terminal.Id));
        Assert.Equal(0, scripted.Subscriber.Calls(foreign.Id) + scripted.Subscriber.Calls(unknown.Id));
        var warnings = logs.Messages.Where(m => m.Level == LogLevel.Warning).Select(m => m.Text).ToArray();
        Assert.Contains(warnings, t => t.Contains(terminal.Id.ToString(), StringComparison.Ordinal) && t.Contains("Failed", StringComparison.Ordinal));
        Assert.Contains(warnings, t => t.Contains(foreign.Id.ToString(), StringComparison.Ordinal) && t.Contains("unknown owner", StringComparison.Ordinal));
        Assert.Contains(warnings, t => t.Contains(unknown.Id.ToString(), StringComparison.Ordinal) && t.Contains("Unknown", StringComparison.Ordinal));
        Assert.Contains(warnings, t => t.Contains("could not be read", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, t => t.Contains(ok.Id.ToString(), StringComparison.Ordinal));
    }

    [VesselEventPostgresFact]
    public async Task Catch_up_recovers_events_whose_notification_no_listener_received()
    {
        var scripted = new ScriptedModule("recovering", new() { DiscoveryGrace = TimeSpan.Zero });
        await using var scope = await DeliveryScope.CreateAsync(scripted);
        await scope.ProvisionAsync();
        var api = await scope.StartAsync(MessagingHosting.ApiDeployment);
        // Nobody listens: the notification is gone, the event is not.
        var unheard = await scope.AppendAsync(api, Draft("unheard"));
        var worker = await scope.StartAsync("recovering", "recovering");
        // A stopped listener misses notifications too.
        await DeliveryScope.Listener(worker)!.StopAsync(default);
        var stopped = await scope.AppendAsync(api, Draft("stopped"));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(await scope.ReceiptsAsync("recovering"));

        // Catch-up dispatches both through the same dispatcher.
        await using var services = worker.Services.CreateAsyncScope();
        var result = await services.ServiceProvider.GetRequiredService<EventCatchUp>().RunAsync(scripted.Subscriptions[0], default);
        Assert.Equal(2, result.Discovered);
        Assert.Equal(("delivered", 1), await scope.ReceiptAsync("recovering", unheard.Id));
        Assert.Equal(("delivered", 1), await scope.ReceiptAsync("recovering", stopped.Id));
        Assert.Equal(long.Parse(stopped.Sequence), result.LowWaterMark);
    }

    [VesselEventPostgresFact]
    public async Task A_killed_listening_connection_reconnects_and_live_delivery_resumes()
    {
        var scripted = new ScriptedModule("reconnecting", new() { DiscoveryGrace = TimeSpan.Zero });
        await using var scope = await DeliveryScope.CreateAsync(scripted);
        await scope.ProvisionAsync();
        var logs = new BackgroundWorkLogs();
        var api = await scope.StartAsync(MessagingHosting.ApiDeployment);
        var worker = await scope.StartAsync("reconnecting", ["reconnecting"], logs);
        var listener = DeliveryScope.Listener(worker)!;
        var killed = listener.ProcessId;
        Assert.Equal(1, await scope.CountAsync(
            $"SELECT count(*) FROM pg_stat_activity WHERE pid={killed} AND application_name='vessel-listen-reconnecting'"));
        Assert.True(await scope.TerminateAsync(killed));
        // Appended while the listener is away (it waits a second before reconnecting): live or by catch-up, never lost.
        await scope.EventuallyAsync(() => Task.FromResult(listener.ProcessId != killed));
        var during = await scope.AppendAsync(api, Draft("during"));
        await scope.EventuallyAsync(() => Task.FromResult(listener.ProcessId is not 0 && listener.ProcessId != killed));
        Assert.Equal(2, listener.Connections);
        var after = await scope.AppendAsync(api, Draft("after"));
        var clock = Stopwatch.StartNew();
        await scope.EventuallyAsync(async () => await scope.ReceiptAsync("reconnecting", after.Id) == ("delivered", 1));
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        await using (var services = worker.Services.CreateAsyncScope())
            await services.ServiceProvider.GetRequiredService<EventCatchUp>().RunAsync(scripted.Subscriptions[0], default);
        Assert.Equal(("delivered", 1), await scope.ReceiptAsync("reconnecting", during.Id));
        // Only the type is logged, never the driver's message (which can name the host).
        var lost = Assert.Single(logs.Messages, m => m.Text.Contains("lost its PostgreSQL connection", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, lost.Level);
        Assert.Null(lost.Exception);
    }

    [VesselEventPostgresFact]
    public async Task A_notification_wakes_live_delivery_well_before_catch_up_could()
    {
        // No maintenance runs in these hosts, and the default discovery grace is 30 s: only the notification can deliver.
        await using var scope = await DeliveryScope.CreateAsync();
        await scope.ProvisionAsync();
        var api = await scope.StartAsync(MessagingHosting.ApiDeployment);
        await scope.StartAsync(VerificationModule.ModuleName, VerificationModule.ModuleName);
        var latencies = new List<TimeSpan>();
        for (var i = 0; i < 20; i++)
        {
            var clock = Stopwatch.StartNew();
            var appended = await scope.AppendAsync(api, Draft("latency-" + i));
            while (await scope.ReceiptAsync(VerificationModule.SubscriptionName, appended.Id) != ("delivered", 1))
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "The notification did not deliver the event.");
                await Task.Delay(10);
            }
            latencies.Add(clock.Elapsed);
        }
        var p95 = latencies.Order().ElementAt(18);
        output.WriteLine($"append to delivered receipt: median {latencies.Order().ElementAt(10)}, p95 {p95}, max {latencies.Max()}");
        Assert.True(p95 < TimeSpan.FromSeconds(2), $"p95 {p95}");
    }

    [VesselEventPostgresFact]
    public async Task Owner_link_in_the_api_reaches_the_verification_subscription()
    {
        await using var scope = await DeliveryScope.CreateAsync();
        await scope.ProvisionAsync();
        var account = new Account(Guid.NewGuid(), scope.Owner, "hyperliquid", "Synthetic", TradingMode.Real);
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        await using (var seed = scope.Database.Context(scope.Owner))
        {
            seed.Accounts.Add(account); seed.Plays.Add(play);
            seed.Orders.Add(TestOrders.For(play, "order-1", status: "open"));
            await seed.SaveChangesAsync();
        }
        var api = await scope.StartAsync(MessagingHosting.ApiDeployment);
        await scope.StartAsync(VerificationModule.ModuleName, VerificationModule.ModuleName);
        await using (var services = api.Services.CreateAsyncScope())
        {
            var db = services.ServiceProvider.GetRequiredService<VesselDbContext>();
            await services.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(async () =>
            {
                db.OrderLinks.Add(new(await db.Plays.SingleAsync(), await db.Orders.SingleAsync(), OrderLinkRole.Entry, "entry-1", null,
                    OrderLinkState.Linked, OrderLinkSource.Owner, DateTimeOffset.UtcNow));
                return true;
            }, default);
        }
        await using var reader = scope.Database.Context(scope.Owner);
        var linked = Assert.Single((await EventReads.ReadAsync(reader, 0, 200, default)).Events);
        Assert.Equal("execution.link-changed", linked.Type);
        await scope.EventuallyAsync(async () => await scope.ReceiptAsync(VerificationModule.SubscriptionName, linked.Id) == ("delivered", 1));
    }

    [VesselEventPostgresFact]
    public async Task Provisioning_only_registers_subscriptions_and_is_insert_only()
    {
        await using var scope = await DeliveryScope.CreateAsync();
        var provisioned = await scope.ProvisionAsync();
        Assert.Equal([VerificationModule.SubscriptionName], provisioned.Registered);
        // A second run keeps the registration and its progress.
        Assert.Equal([VerificationModule.SubscriptionName], (await scope.ProvisionAsync()).Existing);
        // Nothing else exists: no envelope schema, no deployment registration.
        Assert.Equal(0, await scope.CountAsync("SELECT count(*) FROM messaging_deployments"));
        Assert.Equal(0, await scope.CountAsync("SELECT count(*) FROM information_schema.schemata WHERE schema_name LIKE 'wolverine%'"));
    }

    private static VesselEventDraft Draft(string key) => new("monitor.state-changed", Guid.CreateVersion7(), key,
        "info", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(), new("owner"),
        JsonSerializer.SerializeToElement(new { previous = "idle", current = "live" }));

    private sealed class CommitProbe : IUnitOfWorkParticipant
    {
        public Func<Task> Before { get; init; } = () => Task.CompletedTask;
        public Task BeforeCommitAsync(CancellationToken ct) => Before();
        public Task AfterCommitAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

/// <summary>A test-only module whose subscription handler is scripted by the event's dedup key.</summary>
internal sealed class ScriptedModule : IVesselModule
{
    public ScriptedModule(string name, EventSubscriptionLimits? limits = null)
    {
        Name = name;
        Subscriptions = [new(name, name, ["#"], limits)];
    }

    public string Name { get; }
    public IReadOnlyList<CommandQueue> Commands { get; } = [];
    public IReadOnlyList<EventSubscription> Subscriptions { get; }
    public ScriptedSubscriber Subscriber { get; } = new();
    public void AddRunningServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddKeyedSingleton<IVesselEventSubscriber>(Name, Subscriber);
}

internal sealed class ScriptedSubscriber : IVesselEventSubscriber
{
    private readonly ConcurrentDictionary<Guid, int> calls = new();
    public int Calls(Guid id) => calls.GetValueOrDefault(id);

    public Task<EventHandling> HandleAsync(VesselEvent message, CancellationToken cancellationToken)
    {
        calls.AddOrUpdate(message.Id, 1, (_, count) => count + 1);
        return message.DedupKey switch
        {
            var key when key.StartsWith("retry", StringComparison.Ordinal) => throw new InvalidOperationException("Synthetic retryable failure."),
            var key when key.StartsWith("terminal", StringComparison.Ordinal) => throw new TerminalEventException("Synthetic permanent failure."),
            var key when key.StartsWith("skip", StringComparison.Ordinal) => Task.FromResult(EventHandling.Skipped),
            _ => Task.FromResult(EventHandling.Delivered)
        };
    }
}

/// <summary>A raw <c>LISTEN</c> on one Vessel channel of a test schema, outside any Vessel host.</summary>
internal sealed class NotificationProbe : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly ConcurrentQueue<string> payloads = new();

    private NotificationProbe(NpgsqlConnection connection)
    {
        this.connection = connection;
        connection.Notification += (_, e) => payloads.Enqueue(e.Payload);
    }

    public static async Task<NotificationProbe> ListenAsync(string connectionString, string name)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var probe = new NotificationProbe(connection);
        await using var resolve = new NpgsqlCommand($"SELECT {VesselNotifications.ChannelSql}", connection);
        resolve.Parameters.AddWithValue("channel", name);
        var channel = (string)(await resolve.ExecuteScalarAsync())!;
        await using var listen = new NpgsqlCommand($"LISTEN \"{channel}\"", connection);
        await listen.ExecuteNonQueryAsync();
        return probe;
    }

    /// <summary>Everything received within <paramref name="wait"/>, returning as soon as something arrived.</summary>
    public async Task<string[]> ReceiveAsync(TimeSpan wait)
    {
        if (payloads.IsEmpty) await connection.WaitAsync(wait);
        // Anything sent together arrives together; drain what is already buffered.
        while (await connection.WaitAsync(TimeSpan.FromMilliseconds(100))) { }
        var received = new List<string>();
        while (payloads.TryDequeue(out var payload)) received.Add(payload);
        return received.ToArray();
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

/// <summary>A disposable database for one test; hosts listen on its schema's own channels.</summary>
internal sealed class DeliveryScope : IAsyncDisposable
{
    private readonly List<IHost> hosts = [];
    private DeliveryScope(CoreDatabase database, IVesselModule[] modules)
    {
        Database = database;
        Owners = new ConfiguredOwnerDirectory(Owner);
        Registry = new([new MonitorModule(), new VerificationModule(), .. modules]);
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = database.ConnectionString,
            ["Vessel:Auth:OwnerId"] = Owner.ToString(),
            ["Vessel:Identity:Origins:0"] = TestAuthentication.Origin
        }).Build();
    }

    public IJournalOwnerDirectory Owners { get; set; }
    public CoreDatabase Database { get; }
    public Guid Owner { get; } = Guid.NewGuid();
    public VesselModuleRegistry Registry { get; }
    public IConfiguration Configuration { get; }

    public static async Task<DeliveryScope> CreateAsync(params IVesselModule[] modules) =>
        new(await CoreDatabase.CreateAsync(), modules);

    public Task<ProvisioningResult> ProvisionAsync() => MessagingProvisioning.ProvisionAsync(Configuration, default, Registry);

    public static PostgresNotificationListener? Listener(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<PostgresNotificationListener>().SingleOrDefault();

    public IHost Build(string deployment, string[] run, ILoggerProvider? logs = null)
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(Configuration);
        builder.Logging.ClearProviders();
        if (logs is not null) builder.Logging.AddProvider(logs);
        builder.Services.AddScoped<IJournalOwnerContext>(sp =>
            new CoreOwner(sp.GetRequiredService<OwnerScopeAssignment>().OwnerId ?? Owner));
        builder.Services.AddSingleton(Owners);
        builder.Services.AddVesselPersistence(builder.Configuration);
        builder.AddVesselMessaging(deployment, run, registry: Registry);
        var host = builder.Build();
        hosts.Add(host);
        return host;
    }

    public Task<IHost> StartAsync(string deployment, params string[] run) => StartAsync(deployment, run, null);

    /// <summary>Starts a host and, if it listens, waits until its listening connection is subscribed.</summary>
    public async Task<IHost> StartAsync(string deployment, string[] run, ILoggerProvider? logs)
    {
        var host = Build(deployment, run, logs);
        await host.StartAsync();
        if (Listener(host) is { } listener) await EventuallyAsync(() => Task.FromResult(listener.Connections > 0));
        return host;
    }

    public async Task<VesselEvent> AppendAsync(IHost api, VesselEventDraft draft, Guid? ownerId = null)
    {
        await using var scope = api.Services.GetRequiredService<IOwnerScopeFactory>().Create(ownerId ?? Owner);
        var sink = scope.Services.GetRequiredService<IVesselEventSink>();
        await scope.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(async () =>
        {
            await sink.AppendAsync(draft, default);
            return true;
        }, default);
        await using var db = Database.Context(ownerId ?? Owner);
        return (await EventReads.ReadAsync(db, 0, 200, default)).Events.Single(e => e.DedupKey == draft.DedupKey);
    }

    /// <summary>Sends a raw notification on one of this schema's Vessel channels, as a stray or repeated signal would.</summary>
    public async Task NotifyAsync(string channel, string payload)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT pg_notify({VesselNotifications.ChannelSql}, @payload)", connection);
        command.Parameters.AddWithValue("channel", channel);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> TerminateAsync(int processId)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection);
        command.Parameters.AddWithValue("pid", processId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    public async Task<(string State, int Attempts)?> ReceiptAsync(string subscription, Guid eventId)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT state,attempts FROM event_receipts WHERE subscription=@name AND event_id=@id", connection);
        command.Parameters.AddWithValue("name", subscription);
        command.Parameters.AddWithValue("id", eventId);
        await using var row = await command.ExecuteReaderAsync();
        return await row.ReadAsync() ? (row.GetString(0), row.GetInt32(1)) : null;
    }

    public async Task<IReadOnlyList<Guid>> ReceiptsAsync(string subscription)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT event_id FROM event_receipts WHERE subscription=@name", connection);
        command.Parameters.AddWithValue("name", subscription);
        await using var rows = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await rows.ReadAsync()) ids.Add(rows.GetGuid(0));
        return ids;
    }

    public async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }
        Assert.Fail("The disposable database did not reach the expected state.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in hosts)
        {
            try { await host.StopAsync(); } catch { }
            host.Dispose();
        }
        await Database.DisposeAsync();
    }
}
