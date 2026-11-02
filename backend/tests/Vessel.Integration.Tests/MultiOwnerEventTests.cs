using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Domain.Accounts;
using Vessel.Messaging;
using Vessel.Messaging.Modules;
using Vessel.Messaging.Modules.Verification;
using Vessel.Persistence;
using Vessel.Persistence.Events;
using Vessel.Persistence.Events.Subscriptions;
using Wolverine;

namespace Vessel.Tests;

public sealed class MultiOwnerEventTests
{
    [VesselEventPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incompatible_registration_is_inert_but_warns_unlike_an_absent_registration(bool changedModule)
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        await runtime.RegisterAsync();
        var message = await runtime.AppendAsync(runtime.First, "retained");
        var incompatible = new EventSubscription(runtime.Subscription.Name, changedModule ? "different" : "tests",
            changedModule ? ["#"] : ["monitor.#"]);
        await using var scope = runtime.Scopes.Create(runtime.First);
        var dispatcher = scope.Services.GetRequiredService<EventDispatcher>();
        Assert.Equal(DispatchOutcome.NotResponsible,
            (await dispatcher.DispatchAsync(new("absent", "tests", ["#"]), runtime.First, message.Id, DispatchTrigger.Live, default)).Outcome);
        Assert.DoesNotContain(runtime.Logs.Messages, m => m.Level >= LogLevel.Warning);
        Assert.Equal(DispatchOutcome.NotResponsible,
            (await dispatcher.DispatchAsync(incompatible, runtime.First, message.Id, DispatchTrigger.Live, default)).Outcome);
        Assert.Equal(-1, (await scope.Services.GetRequiredService<EventCatchUp>().RunAsync(incompatible, default)).LowWaterMark);
        Assert.Contains(runtime.Logs.Messages, m => m.Level == LogLevel.Warning &&
            m.Text.Contains(runtime.Subscription.Name, StringComparison.Ordinal) &&
            m.Text.Contains("registered with a different filter", StringComparison.Ordinal));
        Assert.Equal(0, await runtime.CountAsync("SELECT count(*) FROM event_subscriptions"));
        Assert.Equal(0, await runtime.CountAsync("SELECT count(*) FROM event_receipts"));
        Assert.Equal(0, runtime.Subscriber.Calls(message.Id));
    }

    [VesselEventPostgresFact]
    public async Task Listener_acknowledges_directory_failure_without_receipt_and_catch_up_recovers_the_event()
    {
        var module = new VerificationModule();
        await using var runtime = await OwnerEventRuntime.CreateAsync(subscription: module.Subscriptions.Single());
        await runtime.RegisterAsync();
        var message = await runtime.AppendAsync(runtime.Second, "directory-outage");
        var topology = new MessagingTopology(new(), new VesselModuleRegistry([module]));
        var host = new MessagingHost("verification", [module]);
        runtime.Directory.Checking = (_, _) => throw new InvalidOperationException("Synthetic private directory failure");
        await EventSubscriptionListener.Handle(message,
            new Envelope { Destination = new Uri("rabbitmq://queue/" + topology.SubscriptionQueue(runtime.Subscription.Name)) },
            host, topology, runtime.Scopes, runtime.Directory,
            runtime.Services.GetRequiredService<ILogger<EventSubscriptionHandlerLog>>(), default);
        Assert.Empty(await runtime.StatusAsync(runtime.Second));
        Assert.Equal(0, await runtime.CountAsync("SELECT count(*) FROM event_receipts"));
        Assert.Equal([runtime.Second], await runtime.Store.CatchUpOwnersAsync(runtime.Subscription, null, runtime.Clock.GetUtcNow(), default));
        Assert.Contains(runtime.Logs.Messages, m => m.Level == LogLevel.Warning &&
            m.Text.Contains("acknowledged after InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(runtime.Logs.Messages, m => m.Text.Contains("Synthetic private", StringComparison.Ordinal));
        runtime.Directory.Checking = null;
        runtime.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await runtime.CatchUpAsync(runtime.Second);
        Assert.Equal((1, 1L), (recovered.Discovered, recovered.LowWaterMark));
        Assert.Equal(1, runtime.Subscriber.Calls(message.Id));
        Assert.Equal(1, await runtime.CountAsync("SELECT count(*) FROM event_receipts WHERE state='delivered'"));
    }

    [VesselEventPostgresFact]
    public async Task First_events_of_new_owners_initialize_once_and_dispatch_concurrently_in_their_own_scope()
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        Assert.True(await runtime.RegisterAsync());
        Assert.Equal(0, await runtime.CountAsync("SELECT count(*) FROM event_subscriptions"));
        var first = await runtime.AppendAsync(runtime.First, "first");
        var second = await runtime.AppendAsync(runtime.Second, "second");
        var results = await Task.WhenAll(
            runtime.DispatchAsync(runtime.First, first.Id), runtime.DispatchAsync(runtime.First, first.Id),
            runtime.DispatchAsync(runtime.Second, second.Id), runtime.DispatchAsync(runtime.Second, second.Id));
        Assert.Equal(2, results.Count(r => r.Outcome == DispatchOutcome.Delivered));
        Assert.Equal(2, results.Count(r => r.Outcome == DispatchOutcome.AlreadySettled));
        Assert.Equal(1, runtime.Subscriber.Calls(first.Id));
        Assert.Equal(1, runtime.Subscriber.Calls(second.Id));
        Assert.All(await runtime.StatusAsync(runtime.First), s => Assert.Equal(0, s.ResponsibleFrom));
        Assert.All(await runtime.StatusAsync(runtime.Second), s => Assert.Equal(0, s.ResponsibleFrom));
        Assert.Equal(2, await runtime.CountAsync("SELECT count(*) FROM event_receipts"));
    }

    [VesselEventPostgresFact]
    public async Task Only_the_first_server_registration_backfills_existing_cursors()
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        var before = await runtime.AppendAsync(runtime.First, "before");
        Assert.True(await runtime.RegisterAsync());
        Assert.Equal(1, (await runtime.StatusAsync(runtime.First)).Single().ResponsibleFrom);
        Assert.Equal(DispatchOutcome.NotResponsible, (await runtime.DispatchAsync(runtime.First, before.Id)).Outcome);
        var newOwner = await runtime.AppendAsync(runtime.Second, "new-owner");
        Assert.False(await runtime.RegisterAsync());
        Assert.Empty(await runtime.StatusAsync(runtime.Second));
        Assert.Equal(DispatchOutcome.Delivered, (await runtime.DispatchAsync(runtime.Second, newOwner.Id)).Outcome);
        Assert.Equal(0, (await runtime.StatusAsync(runtime.Second)).Single().ResponsibleFrom);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Store.RegisterAsync(
            new(runtime.Subscription.Name, "other-module", ["#"]), default));
        Assert.Equal(2, await runtime.CountAsync("SELECT count(*) FROM event_subscriptions"));
    }

    [VesselEventPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialization_waiting_for_retention_preserves_the_published_gap_even_when_every_event_was_pruned(bool dispatch)
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        await runtime.RegisterAsync();
        var message = await runtime.AppendAsync(runtime.Second, "pruned");
        await using var connection = new NpgsqlConnection(runtime.Database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            command.Parameters.AddWithValue("key", EventRetention.LockKey(runtime.Second));
            await command.ExecuteScalarAsync();
        }
        var waiting = dispatch ? RunDispatch() : RunCatchUp();
        await runtime.WaitBlockedByAsync(connection.ProcessID);
        await using (var prune = new NpgsqlCommand("""
            UPDATE event_cursors SET retention_floor=sequence WHERE owner_id=@owner;
            DELETE FROM events WHERE owner_id=@owner
            """, connection, transaction))
        {
            prune.Parameters.AddWithValue("owner", runtime.Second);
            await prune.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        await waiting.WaitAsync(TimeSpan.FromSeconds(20));
        var status = Assert.Single(await runtime.StatusAsync(runtime.Second));
        Assert.Equal(("lagging", 0L, 1L, 1L, 1L),
            (status.Status, status.ResponsibleFrom, status.LowWaterMark, status.GapFrom, status.GapTo));
        Assert.Equal(0, await runtime.CountAsync("SELECT count(*) FROM event_receipts"));

        async Task RunDispatch() =>
            Assert.Equal(DispatchOutcome.Historical, (await runtime.DispatchAsync(runtime.Second, message.Id)).Outcome);
        async Task RunCatchUp() => await runtime.CatchUpAsync(runtime.Second);
    }

    [VesselEventPostgresFact]
    public async Task Deregistration_waits_for_inflight_dispatch_and_cascades_both_owners_without_orphans()
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        await runtime.RegisterAsync();
        var message = await runtime.AppendAsync(runtime.First, "slow");
        var other = await runtime.AppendAsync(runtime.Second, "other");
        await runtime.DispatchAsync(runtime.Second, other.Id);
        var dispatch = runtime.DispatchAsync(runtime.First, message.Id);
        await runtime.Subscriber.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var deleting = runtime.Store.DeregisterAsync(runtime.Subscription.Name, default);
        await Task.Delay(100);
        Assert.False(deleting.IsCompleted);
        runtime.Subscriber.Release.TrySetResult();
        Assert.Equal(DispatchOutcome.Delivered, (await dispatch).Outcome);
        Assert.True(await deleting);
        await AssertNoSubscriberRowsAsync(runtime);
        Assert.Equal(DispatchOutcome.NotResponsible, (await runtime.DispatchAsync(runtime.First, message.Id)).Outcome);
        await runtime.CatchUpAsync(runtime.First);
        await AssertNoSubscriberRowsAsync(runtime);
    }

    [VesselEventPostgresFact]
    public async Task A_failed_commit_revalidates_registration_before_recording_the_attempt()
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        await runtime.RegisterAsync();
        var message = await runtime.AppendAsync(runtime.First, "commit-failure");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        runtime.Directory.Checking = async (_, ct) =>
        {
            if (Interlocked.Increment(ref checks) != 2) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        };
        runtime.CommitFailure.Armed = true;
        var dispatch = runtime.DispatchAsync(runtime.First, message.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await runtime.DeregisterWhileWaitingAsync(release);
        Assert.Equal(DispatchOutcome.NotResponsible, (await dispatch).Outcome);
        await AssertNoSubscriberRowsAsync(runtime);
    }

    [VesselEventPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_catch_up_initialization_or_low_water_advance_cannot_recreate_a_deleted_registration(bool advancing)
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        await runtime.RegisterAsync();
        var message = await runtime.AppendAsync(runtime.First, "settled");
        if (advancing) await runtime.DispatchAsync(runtime.First, message.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        runtime.Directory.Checking = async (_, ct) =>
        {
            if (Interlocked.Increment(ref checks) != (advancing ? 2 : 1)) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        };
        var catchUp = runtime.CatchUpAsync(runtime.First);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await runtime.DeregisterWhileWaitingAsync(release);
        Assert.Equal(-1, (await catchUp).LowWaterMark);
        await AssertNoSubscriberRowsAsync(runtime);
        Assert.Empty(await runtime.Store.CatchUpOwnersAsync(runtime.Subscription, null, runtime.Clock.GetUtcNow(), default));
    }

    [VesselEventPostgresFact]
    public async Task A_due_admin_retry_below_the_low_water_mark_is_discovered_across_owners()
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync(maxAttempts: 1);
        await runtime.RegisterAsync();
        var message = await runtime.AppendAsync(runtime.Second, "retry");
        Assert.Equal(DispatchOutcome.Failed, (await runtime.DispatchAsync(runtime.Second, message.Id)).Outcome);
        Assert.Equal(1, (await runtime.CatchUpAsync(runtime.Second)).LowWaterMark);
        await using (var scope = runtime.Scopes.Create(runtime.Second))
            Assert.Equal(1, await scope.Services.GetRequiredService<EventReceiptAdmin>()
                .RetryFailedAsync(runtime.Subscription, 10, 1, default));
        Assert.Equal([runtime.Second], await runtime.Store.CatchUpOwnersAsync(runtime.Subscription, null,
            runtime.Clock.GetUtcNow(), default));
        runtime.Subscriber.FailRetries = false;
        await runtime.Maintenance().RunCatchUpAsync(runtime.Subscription, default);
        Assert.Equal(2, runtime.Subscriber.Calls(message.Id));
        Assert.Equal(1, await runtime.CountAsync("SELECT count(*) FROM event_receipts WHERE state='delivered'"));
    }

    [VesselEventPostgresFact]
    public async Task Suspension_allows_inflight_settlement_but_no_new_delivery_and_retention_still_creates_a_gap()
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        await runtime.RegisterAsync();
        var inflight = await runtime.AppendAsync(runtime.Second, "slow");
        var dispatch = runtime.DispatchAsync(runtime.Second, inflight.Id);
        await runtime.Subscriber.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        runtime.Directory.Set(runtime.Second, JournalOwnerStatus.Suspended);
        runtime.Subscriber.Release.TrySetResult();
        Assert.Equal(DispatchOutcome.Delivered, (await dispatch).Outcome);
        var skipped = await runtime.AppendAsync(runtime.Second, "suspended");
        Assert.Equal(DispatchOutcome.Suspended, (await runtime.DispatchAsync(runtime.Second, skipped.Id)).Outcome);
        var active = await runtime.AppendAsync(runtime.First, "active");
        await runtime.Maintenance().RunCatchUpAsync(runtime.Subscription, default);
        Assert.Equal(1, runtime.Subscriber.Calls(active.Id));
        Assert.Equal(0, runtime.Subscriber.Calls(skipped.Id));
        runtime.Clock.Advance(TimeSpan.FromDays(100));
        await runtime.Maintenance().RunRetentionAsync(default);
        Assert.Equal(0, await runtime.CountAsync("SELECT count(*) FROM events"));
        runtime.Directory.Set(runtime.Second, JournalOwnerStatus.Active);
        await runtime.Maintenance().RunCatchUpAsync(runtime.Subscription, default);
        var restored = Assert.Single(await runtime.StatusAsync(runtime.Second));
        Assert.Equal((1L, 2L, 2L), (restored.GapFrom, restored.GapTo, restored.LowWaterMark));
        Assert.Equal(0, runtime.Subscriber.Calls(skipped.Id));
    }

    [VesselEventPostgresFact]
    public async Task Discovery_pages_past_suspended_owners_wraps_and_initializes_owners_with_only_pruned_history()
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        await runtime.RegisterAsync();
        for (var i = 1; i <= 105; i++)
        {
            var owner = Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}");
            runtime.Directory.Set(owner, JournalOwnerStatus.Suspended);
            await runtime.ExecuteAsync($"INSERT INTO event_cursors(owner_id,sequence,retention_floor) VALUES('{owner}',1,1)");
        }
        var message = await runtime.AppendAsync(runtime.Second, "later-active");
        var maintenance = runtime.Maintenance();
        await maintenance.RunCatchUpAsync(runtime.Subscription, default);
        Assert.Equal(0, runtime.Subscriber.Calls(message.Id));
        await maintenance.RunCatchUpAsync(runtime.Subscription, default);
        Assert.Equal(1, runtime.Subscriber.Calls(message.Id));
        var restoredOwner = Guid.Parse("00000000-0000-0000-0000-000000000001");
        runtime.Directory.Set(restoredOwner, JournalOwnerStatus.Active);
        await maintenance.RunCatchUpAsync(runtime.Subscription, default);
        var status = Assert.Single(await runtime.StatusAsync(restoredOwner));
        Assert.Equal(("lagging", 1L, 1L), (status.Status, status.GapFrom, status.GapTo));
        Assert.Equal(0, await runtime.CountAsync("SELECT count(*) FROM event_subscriptions WHERE owner_id IN " +
            "(SELECT owner_id FROM event_cursors WHERE owner_id<'00000000-0000-0000-0000-000000000106')") - 1);
        runtime.Clock.Advance(TimeSpan.FromDays(100));
        await maintenance.RunRetentionAsync(default);
        Assert.Equal(0, await runtime.CountAsync("SELECT count(*) FROM events"));
    }

    [VesselEventPostgresFact]
    public async Task Responsibility_and_advance_read_the_registration_not_the_copied_progress_columns()
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        await runtime.RegisterAsync();
        var first = await runtime.AppendAsync(runtime.First, "first");
        await runtime.DispatchAsync(runtime.First, first.Id);
        await runtime.ExecuteAsync("UPDATE event_subscriptions SET module='stale',filters=ARRAY['play.#']," +
            "filter_fingerprint='" + new string('a', 64) + "'");
        var next = await runtime.AppendAsync(runtime.First, "next");
        Assert.Equal(DispatchOutcome.Delivered, (await runtime.DispatchAsync(runtime.First, next.Id)).Outcome);
        Assert.Equal(2, (await runtime.CatchUpAsync(runtime.First)).LowWaterMark);
        var incompatible = await runtime.AppendAsync(runtime.First, "incompatible");
        await runtime.ExecuteAsync("UPDATE event_subscription_registrations SET filter_fingerprint='" + new string('b', 64) + "'");
        Assert.Equal(DispatchOutcome.NotResponsible, (await runtime.DispatchAsync(runtime.First, incompatible.Id)).Outcome);
        Assert.Equal(-1, (await runtime.CatchUpAsync(runtime.First)).LowWaterMark);
        Assert.Equal(0, runtime.Subscriber.Calls(incompatible.Id));
        Assert.Equal(2, await runtime.CountAsync("SELECT count(*) FROM event_receipts"));
    }

    [VesselEventPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_migration_preserves_progress_and_receipts_or_fails_atomically_on_conflicts(bool conflict)
    {
        await using var database = await CoreDatabase.CreateAsync("20261016174509_FundingPayments");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var eventId = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO event_subscriptions VALUES
            (@a,'legacy','tests',ARRAY['#'],@fp,3,7,'lagging',4,6,now(),now()),
            (@b,'legacy','tests',ARRAY['#'],@other,5,8,'active',NULL,NULL,now(),now());
            INSERT INTO events VALUES(@a,7,@event,'monitor.state-changed',NULL,now(),NULL,'{}');
            INSERT INTO event_receipts VALUES(@a,'legacy',@event,7,'pending',2,12,now(),NULL,now(),NULL);
            """, connection))
        {
            seed.Parameters.AddWithValue("a", first); seed.Parameters.AddWithValue("b", second);
            seed.Parameters.AddWithValue("event", eventId);
            seed.Parameters.AddWithValue("fp", new string('a', 64));
            seed.Parameters.AddWithValue("other", new string(conflict ? 'b' : 'a', 64));
            await seed.ExecuteNonQueryAsync();
        }
        await using var db = database.Context(first);
        var migrator = db.GetService<IMigrator>();
        if (conflict)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
            Assert.Contains("conflicting", error.MessageText);
        }
        else await migrator.MigrateAsync();
        await using var check = new NpgsqlCommand("""
            SELECT responsible_from,low_water_mark,status,gap_from,gap_to,
                   (SELECT attempts FROM event_receipts WHERE owner_id=@owner)
            FROM event_subscriptions WHERE owner_id=@owner
            """, connection);
        check.Parameters.AddWithValue("owner", first);
        await using var row = await check.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal((3L, 7L, "lagging", 4L, 6L, 2),
            (row.GetInt64(0), row.GetInt64(1), row.GetString(2), row.GetInt64(3), row.GetInt64(4), row.GetInt32(5)));
    }

    [VesselEventPostgresFact]
    public async Task Inventory_includes_owners_without_cursors_and_keyset_pages_without_duplicates()
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync();
        await using (var db = runtime.Database.Context(runtime.First))
        {
            db.Accounts.Add(new Account(Guid.NewGuid(), runtime.First, "manual", "No event cursor"));
            await db.SaveChangesAsync();
        }
        await runtime.AppendAsync(runtime.Second, "cursor");
        var inventory = runtime.Services.GetRequiredService<IJournalOwnerInventory>();
        var first = await inventory.DistinctOwnersAsync(null, 1, default);
        var second = await inventory.DistinctOwnersAsync(first.Single(), 1, default);
        Assert.Equal(new[] { runtime.First, runtime.Second }.Order(), first.Concat(second).Order());
        Assert.Empty(await inventory.DistinctOwnersAsync(second.Single(), 1, default));
        // Offline reconciliation must report even malformed legacy owner IDs rather than silently omit them.
        await runtime.ExecuteAsync($"INSERT INTO event_cursors(owner_id) VALUES('{Guid.Empty}')");
        Assert.Equal(Guid.Empty, Assert.Single(await inventory.DistinctOwnersAsync(null, 1, default)));
    }

    [VesselEventPostgresFact]
    public async Task Offline_retry_requires_owner_selection_and_uses_assigned_scopes_for_each_owner()
    {
        await using var runtime = await OwnerEventRuntime.CreateAsync(subscription: new("verification", "verification", ["#"],
            new() { MaxAttempts = 1, RetryJitter = 0, DiscoveryGrace = TimeSpan.Zero }));
        await runtime.RegisterAsync();
        foreach (var owner in new[] { runtime.First, runtime.Second })
        for (var index = 0; index < 2; index++)
        {
            var message = await runtime.AppendAsync(owner, $"retry-{index}");
            Assert.Equal(DispatchOutcome.Failed, (await runtime.DispatchAsync(owner, message.Id)).Outcome);
        }
        var output = new StringWriter();
        Assert.Equal(2, await MessagingCommands.RunAsync(["retry-event-receipts", "verification"], runtime.Configuration, output));
        Assert.Equal(2, await MessagingCommands.RunAsync(["retry-event-receipts", "verification", "--all-owners", "--owner",
            runtime.First.ToString()], runtime.Configuration, output));
        Assert.Equal(0, await MessagingCommands.RunAsync(["retry-event-receipts", "verification", "--owner",
            runtime.First.ToString(), "--limit", "1"], runtime.Configuration, output));
        Assert.Equal(3, await runtime.CountAsync("SELECT count(*) FROM event_receipts WHERE state='failed'"));
        Assert.Equal(0, await MessagingCommands.RunAsync(["retry-event-receipts", "verification", "--all-owners", "--limit", "1"],
            runtime.Configuration, output));
        Assert.Equal(3, await runtime.CountAsync("SELECT count(*) FROM event_receipts WHERE state='pending'"));
        Assert.Equal(1, await runtime.CountAsync("SELECT count(*) FROM event_receipts WHERE state='failed'"));
        Assert.Equal(0, await MessagingCommands.RunAsync(["retry-event-receipts", "verification", "--all-owners", "--limit", "1"],
            runtime.Configuration, output));
        Assert.Equal(4, await runtime.CountAsync("SELECT count(*) FROM event_receipts WHERE state='pending'"));
    }

    private static async Task AssertNoSubscriberRowsAsync(OwnerEventRuntime runtime)
    {
        foreach (var table in new[] { "event_subscription_registrations", "event_subscriptions", "event_receipts" })
            Assert.Equal(0, await runtime.CountAsync("SELECT count(*) FROM " + table));
    }
}

internal sealed class MutableOwnerDirectory : IJournalOwnerDirectory
{
    private readonly ConcurrentDictionary<Guid, JournalOwnerStatus> statuses = new();
    public Func<Guid, CancellationToken, Task>? Checking { get; set; }
    public void Set(Guid owner, JournalOwnerStatus status) => statuses[owner] = status;
    public async Task<JournalOwnerStatus> StatusAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        if (Checking is { } checking) await checking(ownerId, cancellationToken);
        return statuses.GetValueOrDefault(ownerId);
    }
    public Task<IReadOnlyList<Guid>> ActiveOwnersAsync(Guid? after, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(statuses.Where(p => p.Value == JournalOwnerStatus.Active &&
            (after is null || p.Key.CompareTo(after.Value) > 0)).Select(p => p.Key).Order().Take(limit).ToArray());
}

internal sealed class OwnerEventRuntime : IAsyncDisposable
{
    private OwnerEventRuntime(CoreDatabase database, EventSubscription subscription)
    {
        Database = database;
        Subscription = subscription;
        Directory.Set(First, JournalOwnerStatus.Active);
        Directory.Set(Second, JournalOwnerStatus.Active);
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = database.ConnectionString,
            ["Vessel:Messaging:Verification"] = "true"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(Logs));
        services.AddScoped<IJournalOwnerContext, AssignedOwnerContext>();
        services.AddSingleton<IJournalOwnerDirectory>(Directory);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddVesselPersistence(Configuration);
        services.AddVesselEvents(Configuration);
        services.AddScoped<IVesselEventOutbox, NoOutbox>();
        services.AddSingleton<IUnitOfWorkParticipant>(CommitFailure);
        services.AddKeyedScoped<IVesselEventSubscriber>(subscription.Name, (sp, _) =>
            new CheckingSubscriber(sp.GetRequiredService<IJournalOwnerContext>(), Subscriber));
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public Guid First { get; } = Guid.Parse("11111111-0000-0000-0000-000000000001");
    public Guid Second { get; } = Guid.Parse("22222222-0000-0000-0000-000000000002");
    public CoreDatabase Database { get; }
    public EventSubscription Subscription { get; }
    public MutableOwnerDirectory Directory { get; } = new();
    public EventSubscriptionTests.ManualClock Clock { get; } = new();
    public EventSubscriptionTests.GatedSubscriber Subscriber { get; } = new();
    public CommitFailureOnce CommitFailure { get; } = new();
    public BackgroundWorkLogs Logs { get; } = new();
    public ServiceProvider Services { get; }
    public IConfiguration Configuration { get; }
    public IOwnerScopeFactory Scopes => Services.GetRequiredService<IOwnerScopeFactory>();
    public SystemEventSubscriptionStore Store => Services.GetRequiredService<SystemEventSubscriptionStore>();

    public static async Task<OwnerEventRuntime> CreateAsync(int maxAttempts = 12, EventSubscription? subscription = null) =>
        new(await CoreDatabase.CreateAsync(), subscription ?? new("two-owners", "tests", ["#"],
            new() { MaxAttempts = maxAttempts, RetryJitter = 0, DiscoveryGrace = TimeSpan.Zero }));

    public Task<bool> RegisterAsync() => Store.RegisterAsync(Subscription, default);

    public async Task<VesselEvent> AppendAsync(Guid owner, string key)
    {
        await using var scope = Scopes.Create(owner);
        var sink = scope.Services.GetRequiredService<IVesselEventSink>();
        await scope.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(async () =>
        {
            await sink.AppendAsync(new("monitor.state-changed", Guid.CreateVersion7(), key, "info", Clock.GetUtcNow(),
                Clock.GetUtcNow(), new(), new("owner"), JsonSerializer.SerializeToElement(new { current = "live" })), default);
            return true;
        }, default);
        Clock.Advance(TimeSpan.FromSeconds(1));
        await using var reader = Database.Context(owner);
        return (await EventReads.ReadAsync(reader, 0, 200, default)).Events.Single(e => e.DedupKey == key);
    }

    public async Task<DispatchResult> DispatchAsync(Guid owner, Guid eventId)
    {
        await using var scope = Scopes.Create(owner);
        return await scope.Services.GetRequiredService<EventDispatcher>().DispatchAsync(Subscription, owner, eventId, DispatchTrigger.Live, default);
    }

    public async Task<CatchUpResult> CatchUpAsync(Guid owner)
    {
        await using var scope = Scopes.Create(owner);
        return await scope.Services.GetRequiredService<EventCatchUp>().RunAsync(Subscription, default);
    }

    public async Task<IReadOnlyList<EventSubscriptionStatus>> StatusAsync(Guid owner)
    {
        await using var scope = Scopes.Create(owner);
        return await scope.Services.GetRequiredService<EventSubscriptionRegistry>().ListAsync(default);
    }

    public SubscriptionMaintenanceService Maintenance() => new(new("tests", []), Scopes, Directory, Store, Clock,
        NullLogger<SubscriptionMaintenanceService>.Instance);

    public async Task<long> CountAsync(string sql)
    {
        await using var command = Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var command = Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async Task WaitBlockedByAsync(int pid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var command = Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @pid=ANY(pg_blocking_pids(pid)))");
            command.Parameters.AddWithValue("pid", pid);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
            await Task.Delay(20, timeout.Token);
        }
    }

    public async Task DeregisterWhileWaitingAsync(TaskCompletionSource release)
    {
        await using var connection = await Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand(
            "SELECT 1 FROM event_subscription_registrations WHERE name=@name FOR UPDATE", connection, transaction))
        {
            locked.Parameters.AddWithValue("name", Subscription.Name);
            await locked.ExecuteScalarAsync();
        }
        release.TrySetResult();
        await WaitBlockedByAsync(connection.ProcessID);
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM event_subscription_registrations WHERE name=@name", connection, transaction))
        {
            delete.Parameters.AddWithValue("name", Subscription.Name);
            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Subscriber.Release.TrySetResult();
        await Services.DisposeAsync();
        await Database.DisposeAsync();
    }

    private sealed class CheckingSubscriber(IJournalOwnerContext owner, EventSubscriptionTests.GatedSubscriber subscriber) : IVesselEventSubscriber
    {
        public Task<EventHandling> HandleAsync(VesselEvent message, CancellationToken ct)
        {
            Assert.Equal(owner.OwnerId, message.OwnerId);
            return subscriber.HandleAsync(message, ct);
        }
    }

    public sealed class CommitFailureOnce : IUnitOfWorkParticipant
    {
        public bool Armed { get; set; }
        public Task BeforeCommitAsync(CancellationToken ct)
        {
            if (Armed) { Armed = false; throw new InvalidOperationException("Synthetic commit failure."); }
            return Task.CompletedTask;
        }
        public Task AfterCommitAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOutbox : IVesselEventOutbox
    {
        public Task StageAsync(VesselEvent message, CancellationToken ct) => Task.CompletedTask;
        public Task StageAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }
}
