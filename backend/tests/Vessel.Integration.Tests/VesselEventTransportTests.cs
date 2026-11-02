using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;
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
using Wolverine;

namespace Vessel.Tests;

/// <summary>
/// Pinned WolverineFx 6.50.0 against the CI RabbitMQ 4.3 image. Proves the inline listener settles every row of the
/// plan's settlement table, that publishers know nothing about subscribers, and that catch-up from PostgreSQL
/// recovers what the broker lost.
/// </summary>
[Collection("Monitor transport")]
public sealed class VesselEventTransportTests
{
    [VesselEventRabbitFact]
    public async Task Event_and_outbox_commit_together_and_publish_once_to_a_verification_subscription()
    {
        await using var broker = await BrokerScope.CreateAsync();
        await broker.ProvisionAsync();
        var api = await broker.StartAsync(MessagingHosting.ApiDeployment);
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VesselDbContext>();
        var sink = scope.ServiceProvider.GetRequiredService<VesselEventStore>();
        var draft = Draft("transport-test");
        var rollback = true;
        var verifyCommit = new CommitProbe { After = async () =>
        {
            // Runs before the event participant's flush, but after the real UoW commit.
            Assert.Equal(1, await broker.CountAsync("SELECT count(*) FROM events"));
            Assert.Equal(1, await broker.CountAsync($"SELECT count(*) FROM {broker.Schema("api")}.wolverine_outgoing_envelopes"));
        }};
        var verifyStage = new CommitProbe { Before = async () =>
        {
            Assert.Equal(1, await CountInTransaction(db, broker.Schema("api")));
            Assert.Equal(0, await broker.CountAsync($"SELECT count(*) FROM {broker.Schema("api")}.wolverine_outgoing_envelopes"));
            if (rollback) throw new InvalidOperationException("Synthetic rollback after durable staging.");
        }};
        var work = new UnitOfWork(db, [verifyCommit, sink, verifyStage]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            await sink.AppendAsync(draft, default);
            return true;
        }, default));
        Assert.Equal(0, await broker.CountAsync($"SELECT count(*) FROM {broker.Schema("api")}.wolverine_outgoing_envelopes"));
        Assert.Equal(0, await broker.CountAsync("SELECT count(*) FROM events"));
        rollback = false;
        await work.ExecuteAsync(async () => { await sink.AppendAsync(draft, default); return true; }, default);
        Assert.Null(verifyCommit.Error);
        var original = Assert.Single((await EventReads.ReadAsync(db, 0, 200, default)).Events);
        // Publication wrote no per-subscriber rows; the subscription's receipt is the subscriber's own record.
        Assert.Equal(0, await broker.CountAsync("SELECT count(*) FROM event_receipts"));
        await broker.StartAsync(VerificationModule.ModuleName, VerificationModule.ModuleName);
        await broker.EventuallyAsync(async () => await broker.ReceiptAsync(VerificationModule.SubscriptionName, original.Id) == ("delivered", 1));
        // A duplicate transport envelope with the SAME business event ID is acknowledged without a second effect.
        var outbox = scope.ServiceProvider.GetRequiredService<IVesselEventOutbox>();
        await work.ExecuteAsync(async () => { await outbox.StageAsync(original, default); return true; }, default);
        await broker.EventuallyAsync(async () =>
            await broker.CountAsync($"SELECT count(*) FROM {broker.Schema("api")}.wolverine_outgoing_envelopes") == 0);
        await Task.Delay(500);
        Assert.Equal(("delivered", 1), await broker.ReceiptAsync(VerificationModule.SubscriptionName, original.Id));
    }

    [VesselEventRabbitFact]
    public async Task Inline_listener_settles_every_row_of_the_settlement_table()
    {
        var scripted = new ScriptedModule("scripted", new() { MaxAttempts = 2 });
        await using var broker = await BrokerScope.CreateAsync(scripted);
        await broker.ProvisionAsync();
        var api = await broker.StartAsync(MessagingHosting.ApiDeployment);
        var worker = await broker.StartAsync("scripted", "scripted");
        var ok = await broker.AppendAsync(api, Draft("ok"));
        var retry = await broker.AppendAsync(api, Draft("retry"));
        var terminal = await broker.AppendAsync(api, Draft("terminal"));
        // Success: effects and terminal receipt commit together. Retryable: the attempt is recorded and acked.
        // Terminal: the receipt commits failed and the delivery is dead-lettered.
        await broker.EventuallyAsync(async () => await broker.ReceiptAsync("scripted", ok.Id) == ("delivered", 1) &&
            await broker.ReceiptAsync("scripted", retry.Id) == ("pending", 1) &&
            await broker.ReceiptAsync("scripted", terminal.Id) == ("failed", 1));
        Assert.True(await broker.CountAsync($"SELECT count(*) FROM event_receipts WHERE event_id='{retry.Id}' AND next_attempt_at>now()") == 1);

        // Invalid owner and unknown event (claiming a below-floor sequence): no receipt, dead-lettered.
        var bus = api.Services.GetRequiredService<IMessageBus>();
        await bus.BroadcastToTopicAsync(ok.Type, ok with { Id = Guid.CreateVersion7(), OwnerId = Guid.NewGuid() });
        var unknown = ok with { Id = Guid.CreateVersion7(), Sequence = "0" };
        await bus.BroadcastToTopicAsync(unknown.Type, unknown);
        // Known historical: a pruned event keeps its dedup identity, so its late duplicate is acknowledged.
        await broker.ExecuteAsync($"DELETE FROM events WHERE event_id='{ok.Id}'");
        await bus.BroadcastToTopicAsync(ok.Type, ok);
        // Already settled: a duplicate of the failed delivery is acknowledged and changes nothing.
        await bus.BroadcastToTopicAsync(terminal.Type, terminal);
        var queue = broker.Topology.SubscriptionQueue("scripted");
        await broker.EventuallyAsync(async () => await broker.ReadyAsync(MessagingTopology.DeadLetterQueue(queue)) == 3);
        await Task.Delay(500);
        await worker.StopAsync();
        // Stopping the consumer returns any unacknowledged delivery to the queue; nothing was left unsettled.
        Assert.Equal(0u, await broker.ReadyAsync(queue));
        Assert.Equal(3u, await broker.ReadyAsync(MessagingTopology.DeadLetterQueue(queue)));
        Assert.Equal(0, await broker.CountAsync($"SELECT count(*) FROM event_receipts WHERE event_id IN ('{ok.Id}','{unknown.Id}')"));
        Assert.Equal(("failed", 1), await broker.ReceiptAsync("scripted", terminal.Id));
        Assert.Equal(("pending", 1), await broker.ReceiptAsync("scripted", retry.Id));
        Assert.Equal(1, scripted.Subscriber.Calls(terminal.Id));

        // Redriving the dead letters never reopens a receipt: the failed one is acked as settled, the others
        // dead-letter again because they are still not ours.
        await broker.RedriveAsync(queue);
        worker = await broker.StartAsync("scripted", "scripted");
        await broker.EventuallyAsync(async () => await broker.ReadyAsync(MessagingTopology.DeadLetterQueue(queue)) == 2);
        await Task.Delay(500);
        await worker.StopAsync();
        Assert.Equal(0u, await broker.ReadyAsync(queue));
        Assert.Equal(("failed", 1), await broker.ReceiptAsync("scripted", terminal.Id));
        Assert.Equal(1, scripted.Subscriber.Calls(terminal.Id));
    }

    [VesselEventRabbitFact]
    public async Task Catch_up_recovers_an_unroutable_publish_and_a_lost_queue_from_postgresql()
    {
        var scripted = new ScriptedModule("recovering", new() { DiscoveryGrace = TimeSpan.Zero });
        await using var broker = await BrokerScope.CreateAsync(scripted);
        await broker.ProvisionAsync();
        var api = await broker.StartAsync(MessagingHosting.ApiDeployment);
        var queue = broker.Topology.SubscriptionQueue("recovering");
        // A topic with no binding acknowledges the publish and drops it: no longer a loss, only latency.
        await broker.ChannelAsync(channel => channel.QueueUnbindAsync(queue, broker.Topology.EventExchange, "#"));
        var unroutable = await broker.AppendAsync(api, Draft("unroutable"));
        await broker.EventuallyAsync(async () =>
            await broker.CountAsync($"SELECT count(*) FROM {broker.Schema("api")}.wolverine_outgoing_envelopes") == 0);
        Assert.Equal(0u, await broker.ReadyAsync(queue));
        await broker.ChannelAsync(channel => channel.QueueBindAsync(queue, broker.Topology.EventExchange, "#"));
        var purged = await broker.AppendAsync(api, Draft("purged"));
        await broker.EventuallyAsync(async () => await broker.ReadyAsync(queue) == 1);
        await broker.ChannelAsync(channel => channel.QueuePurgeAsync(queue));
        Assert.Empty(await broker.ReceiptsAsync("recovering"));

        // The worker was down for both. Its catch-up pass dispatches them through the same dispatcher.
        var worker = await broker.StartAsync("recovering", "recovering");
        await using var scope = worker.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<EventCatchUp>()
            .RunAsync(scripted.Subscriptions[0], default);
        Assert.Equal(2, result.Discovered);
        Assert.Equal(("delivered", 1), await broker.ReceiptAsync("recovering", unroutable.Id));
        Assert.Equal(("delivered", 1), await broker.ReceiptAsync("recovering", purged.Id));
        Assert.Equal(long.Parse(purged.Sequence), result.LowWaterMark);
    }

    [VesselEventRabbitFact]
    public async Task Owner_link_in_the_api_reaches_the_verification_subscription()
    {
        await using var broker = await BrokerScope.CreateAsync();
        await broker.ProvisionAsync();
        var account = new Account(Guid.NewGuid(), broker.Owner, "hyperliquid", "Synthetic");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        await using (var seed = broker.Database.Context(broker.Owner))
        {
            seed.Accounts.Add(account); seed.Plays.Add(play);
            seed.Orders.Add(TestOrders.For(play, "order-1", status: "open"));
            await seed.SaveChangesAsync();
        }
        var api = await broker.StartAsync(MessagingHosting.ApiDeployment);
        await broker.StartAsync(VerificationModule.ModuleName, VerificationModule.ModuleName);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VesselDbContext>();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(async () =>
            {
                db.OrderLinks.Add(new(await db.Plays.SingleAsync(), await db.Orders.SingleAsync(), OrderLinkRole.Entry, "entry-1", null,
                    OrderLinkState.Linked, OrderLinkSource.Owner, DateTimeOffset.UtcNow));
                return true;
            }, default);
        }
        await using var reader = broker.Database.Context(broker.Owner);
        var linked = Assert.Single((await EventReads.ReadAsync(reader, 0, 200, default)).Events);
        Assert.Equal("execution.link-changed", linked.Type);
        await broker.EventuallyAsync(async () => await broker.ReceiptAsync(VerificationModule.SubscriptionName, linked.Id) == ("delivered", 1));
    }

    [VesselEventRabbitFact]
    public async Task Envelope_storage_exists_only_after_explicit_provisioning()
    {
        await using var broker = await BrokerScope.CreateAsync();
        using var api = broker.Build(MessagingHosting.ApiDeployment);
        await Assert.ThrowsAnyAsync<Exception>(() => api.StartAsync());
        Assert.Equal(0, await broker.CountAsync($"SELECT count(*) FROM information_schema.schemata WHERE schema_name='{broker.Schema("api")}'"));
        var provisioned = await broker.ProvisionAsync();
        Assert.Contains(broker.Schema("api"), provisioned.Schemas);
        Assert.Equal([VerificationModule.SubscriptionName], provisioned.Registered);
        // Provisioning is insert-only: a second run keeps the registration and its progress.
        Assert.Equal([VerificationModule.SubscriptionName], (await broker.ProvisionAsync()).Existing);
        using var started = broker.Build(MessagingHosting.ApiDeployment);
        await started.StartAsync();
        await started.StopAsync();
    }

    private static VesselEventDraft Draft(string key) => new("monitor.state-changed", Guid.CreateVersion7(), key,
        "info", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(), new("owner"),
        JsonSerializer.SerializeToElement(new { previous = "idle", current = "live" }));

    private static async Task<long> CountInTransaction(VesselDbContext db, string schema)
    {
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = $"SELECT count(*) FROM {schema}.wolverine_outgoing_envelopes";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class CommitProbe : IUnitOfWorkParticipant
    {
        public Func<Task> Before { get; init; } = () => Task.CompletedTask;
        public Func<Task> After { get; init; } = () => Task.CompletedTask;
        public Exception? Error { get; private set; }
        public Task BeforeCommitAsync(CancellationToken ct) => Before();
        public async Task AfterCommitAsync(CancellationToken ct) => Error = await Record.ExceptionAsync(After);
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

/// <summary>A disposable database, broker namespace and envelope schemas for one test.</summary>
internal sealed class BrokerScope : IAsyncDisposable
{
    private readonly List<IHost> hosts = [];
    private readonly string id = Guid.NewGuid().ToString("N")[..12];
    private BrokerScope(CoreDatabase database, IVesselModule[] modules)
    {
        Database = database;
        Owners = new ConfiguredOwnerDirectory(Owner);
        Registry = new([new MonitorModule(), new VerificationModule(), .. modules]);
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = database.ConnectionString,
            ["Vessel:Auth:OwnerId"] = Owner.ToString(),
            ["Vessel:Identity:Origins:0"] = TestAuthentication.Origin,
            ["Vessel:Messaging:RabbitMqUri"] = Environment.GetEnvironmentVariable("Vessel_TEST_EVENT_RABBIT"),
            ["Vessel:Messaging:Prefix"] = "vt" + id
        };
        foreach (var deployment in new[] { MessagingHosting.ApiDeployment }.Concat(Registry.Modules.Select(m => m.Name)))
            values[$"Vessel:Messaging:StorageSchemas:{deployment}"] = Schema(deployment);
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Topology = new(MessagingHosting.Settings(Configuration), Registry);
    }

    public IJournalOwnerDirectory Owners { get; set; }
    public CoreDatabase Database { get; }
    public Guid Owner { get; } = Guid.NewGuid();
    public VesselModuleRegistry Registry { get; }
    public IConfiguration Configuration { get; }
    public MessagingTopology Topology { get; }
    public string Schema(string deployment) => $"vt_{deployment.Replace('-', '_')}_{id}";

    public static async Task<BrokerScope> CreateAsync(params IVesselModule[] modules) =>
        new(await CoreDatabase.CreateAsync(), modules);

    public Task<ProvisioningResult> ProvisionAsync() => MessagingProvisioning.ProvisionAsync(Configuration, default, Registry);

    public IHost Build(string deployment, params string[] run) => Build(deployment, run, logs: null);

    public IHost Build(string deployment, string[] run, ILoggerProvider? logs)
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

    public async Task<IHost> StartAsync(string deployment, params string[] run)
    {
        var host = Build(deployment, run);
        await host.StartAsync();
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

    public async Task ChannelAsync(Func<IChannel, Task> action)
    {
        var factory = new ConnectionFactory { Uri = new(Environment.GetEnvironmentVariable("Vessel_TEST_EVENT_RABBIT")!) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await action(channel);
    }

    public async Task<uint> ReadyAsync(string queue)
    {
        uint count = 0;
        await ChannelAsync(async channel => count = (await channel.QueueDeclarePassiveAsync(queue)).MessageCount);
        return count;
    }

    /// <summary>Moves every dead letter back onto its queue with its original body and headers.</summary>
    public Task RedriveAsync(string queue) => ChannelAsync(async channel =>
    {
        while (await channel.BasicGetAsync(MessagingTopology.DeadLetterQueue(queue), autoAck: false) is { } message)
        {
            var properties = new BasicProperties(message.BasicProperties);
            await channel.BasicPublishAsync("", queue, mandatory: true, properties, message.Body);
            await channel.BasicAckAsync(message.DeliveryTag, multiple: false);
        }
    });

    public async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }
        Assert.Fail("The disposable broker did not reach the expected state.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in hosts)
        {
            try { await host.StopAsync(); } catch { }
            host.Dispose();
        }
        try
        {
            await ChannelAsync(async channel =>
            {
                var queues = Registry.Modules.SelectMany(m => m.Subscriptions).Select(s => Topology.SubscriptionQueue(s.Name))
                    .Concat(Registry.Modules.SelectMany(m => m.Commands).Select(c => Topology.CommandQueue(c.Name)));
                foreach (var queue in queues)
                {
                    await channel.QueueDeleteAsync(queue, false, false);
                    await channel.QueueDeleteAsync(MessagingTopology.DeadLetterQueue(queue), false, false);
                    await channel.ExchangeDeleteAsync(MessagingTopology.DeadLetterExchange(queue), false);
                }
                await channel.ExchangeDeleteAsync(Topology.EventExchange, false);
            });
        }
        finally
        {
            await using (var connection = new NpgsqlConnection(Database.ConnectionString))
            {
                await connection.OpenAsync();
                foreach (var deployment in new[] { MessagingHosting.ApiDeployment }.Concat(Registry.Modules.Select(m => m.Name)))
                {
                    await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Schema(deployment)} CASCADE", connection);
                    await drop.ExecuteNonQueryAsync();
                }
            }
            await Database.DisposeAsync();
        }
    }
}
