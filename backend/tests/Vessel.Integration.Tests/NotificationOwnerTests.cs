using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vessel.Application.Events;
using Vessel.Application.Notifications;
using Vessel.Application.Ownership;
using Vessel.Messaging;
using Vessel.Messaging.Modules;
using Vessel.Messaging.Modules.Notifications;
using Vessel.Domain.Accounts;
using Vessel.Persistence;
using Vessel.Persistence.Events;
using Vessel.Persistence.Events.Subscriptions;
using Vessel.Persistence.Notifications;
using Vessel.Worker;
using Wolverine;

namespace Vessel.Tests;

public sealed class NotificationOwnerTests
{
    [VesselEventPostgresFact]
    public async Task Subscription_API_counts_preferences_claims_reads_and_stream_frames_stay_in_their_owner()
    {
        await using var runtime = await Runtime.CreateAsync();
        var first = await runtime.RequestAsync(runtime.First);
        var second = await runtime.RequestAsync(runtime.Second);
        await runtime.DeliverAsync(first);
        await runtime.DeliverAsync(second);
        Assert.False(await runtime.Store.RegisterAsync(runtime.Subscription, default));
        Assert.Equal(2, await runtime.CountAsync("event_subscriptions"));
        foreach (var (owner, foreign) in new[] { (runtime.First, runtime.Second), (runtime.Second, runtime.First) })
        {
            await using var factory = new CoreApiFactory(owner, connection: runtime.Database.ConnectionString);
            using var client = factory.AuthorizedClient();
            var page = (await client.GetFromJsonAsync<NotificationPage>("/api/notifications?tab=all"))!;
            var item = Assert.Single(page.Items);
            Assert.Equal((1, 1), (page.Total, page.UnreadTotal));
            var head = (await client.GetFromJsonAsync<NotificationHeadDto>("/api/notifications/head"))!;
            Assert.Equal(1, head.UnreadCount);
            var foreignPage = await runtime.PageAsync(foreign);
            Assert.NotEqual(item.Id, Assert.Single(foreignPage.Items).Id);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(
                $"/api/notifications/{foreignPage.Items[0].Id}/read", null)).StatusCode);
            var cursor = head.NextCursor[..(head.NextCursor.LastIndexOf('.') + 1)] + "0";
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var response = await client.GetAsync("/api/events/stream?after=0&notificationsAfter=" + cursor,
                       HttpCompletionOption.ResponseHeadersRead, stop.Token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stop.Token));
                var notifications = new List<Guid>();
                while (await reader.ReadLineAsync(stop.Token) is var line && line != ": keepalive")
                {
                    Assert.NotNull(line);
                    if (line != "event: notification") continue;
                    var data = (await reader.ReadLineAsync(stop.Token))!;
                    Assert.StartsWith("data: ", data); // Notification frames must not set the event feed's id.
                    using var json = JsonDocument.Parse(data[6..]);
                    notifications.Add(json.RootElement.GetProperty("notification").GetProperty("id").GetGuid());
                    Assert.Equal(1, json.RootElement.GetProperty("unreadCount").GetInt32());
                }
                Assert.Equal(item.Id, Assert.Single(notifications));
                await stop.CancelAsync();
            }
            var claim = (await (await client.PostAsync("/api/notifications/claim", null))
                .Content.ReadFromJsonAsync<NotificationClaim>())!;
            Assert.Equal(item.Id, Assert.Single(claim.Popups).Id);
        }
        await using var firstFactory = new CoreApiFactory(runtime.First, connection: runtime.Database.ConnectionString);
        using var firstClient = firstFactory.AuthorizedClient();
        var preferences = await firstClient.PutAsJsonAsync("/api/notifications/preferences",
            new { enabled = false, minimumSeverity = "critical", categories = Array.Empty<string>(), expectedRevision = 0 });
        Assert.Equal(HttpStatusCode.OK, preferences.StatusCode);
        await using (var scope = runtime.Scopes.Create(runtime.Second))
            Assert.Equal((true, "info", 0L), await DefaultsAsync(scope));
        var firstHead = (await firstClient.GetFromJsonAsync<NotificationHeadDto>("/api/notifications/head"))!;
        Assert.Equal(HttpStatusCode.OK, (await firstClient.PostAsJsonAsync("/api/notifications/read", new { upTo = firstHead.NextCursor })).StatusCode);
        await using (var scope = runtime.Scopes.Create(runtime.First))
            Assert.Equal(0, (await scope.Services.GetRequiredService<NotificationService>().HeadAsync(default)).UnreadCount);
        await using (var scope = runtime.Scopes.Create(runtime.Second))
            Assert.Equal(1, (await scope.Services.GetRequiredService<NotificationService>().HeadAsync(default)).UnreadCount);
    }

    [VesselEventPostgresFact]
    public async Task Suspended_delivery_has_no_receipt_and_catches_up_after_restore_unknown_owner_dead_letters()
    {
        await using var runtime = await Runtime.CreateAsync();
        runtime.Directory.Set(runtime.First, JournalOwnerStatus.Suspended);
        var message = await runtime.RequestAsync(runtime.First);
        await runtime.DeliverAsync(message);
        Assert.Equal(0, await runtime.CountAsync("event_receipts"));
        Assert.Empty((await runtime.PageAsync(runtime.First)).Items);
        runtime.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await runtime.Maintenance().RunCatchUpPassAsync(default));
        Assert.Empty((await runtime.PageAsync(runtime.First)).Items);
        runtime.Directory.Set(runtime.First, JournalOwnerStatus.Active);
        Assert.True(await runtime.Maintenance().RunCatchUpPassAsync(default));
        Assert.Single((await runtime.PageAsync(runtime.First)).Items);
        Assert.Equal(1, await runtime.CountAsync("event_receipts"));
        await Assert.ThrowsAsync<EventDeadLetterException>(() =>
            runtime.DeliverAsync(message with { OwnerId = Guid.NewGuid() }));
    }

    [VesselEventPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catch_up_beats_with_a_healthy_owner_but_not_when_every_owner_fails(bool secondHealthy)
    {
        await using var runtime = await Runtime.CreateAsync();
        await runtime.RequestAsync(runtime.First);
        await runtime.RequestAsync(runtime.Second);
        runtime.Clock.Advance(TimeSpan.FromMinutes(1));
        runtime.Directory.Checking = (owner, _) => owner == runtime.First || !secondHealthy
            ? Task.FromException(new InvalidOperationException("synthetic directory failure")) : Task.CompletedTask;
        var heartbeat = new CountingHeartbeat();
        var maintenance = runtime.Maintenance(heartbeat);
        Assert.Equal(secondHealthy, await maintenance.RunCatchUpPassAsync(default));
        Assert.Equal(secondHealthy ? 1 : 0, heartbeat.Beats);
        Assert.Empty((await runtime.PageAsync(runtime.First)).Items);
        Assert.Equal(secondHealthy ? 1 : 0, (await runtime.PageAsync(runtime.Second)).Items.Count);
        runtime.Directory.Checking = null;
        Assert.True(await maintenance.RunCatchUpPassAsync(default));
        Assert.Equal(secondHealthy ? 2 : 1, heartbeat.Beats);
        Assert.Single((await runtime.PageAsync(runtime.First)).Items);
    }

    [VesselEventPostgresFact]
    public async Task Catch_up_beats_when_no_owner_needs_work_and_none_failed()
    {
        await using var runtime = await Runtime.CreateAsync();
        var heartbeat = new CountingHeartbeat();
        Assert.True(await runtime.Maintenance(heartbeat).RunCatchUpPassAsync(default));
        Assert.Equal(1, heartbeat.Beats);
    }

    [VesselEventPostgresFact]
    public async Task Notification_retention_includes_suspended_owners_and_isolates_failures()
    {
        await using var runtime = await Runtime.CreateAsync();
        await runtime.DeliverAsync(await runtime.RequestAsync(runtime.First));
        await runtime.DeliverAsync(await runtime.RequestAsync(runtime.Second));
        runtime.Directory.Set(runtime.Second, JournalOwnerStatus.Suspended);
        // Journal facts and preferences alone do not require notification retention.
        var preferencesOnly = Guid.NewGuid();
        await using (var seed = runtime.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            INSERT INTO notification_preferences(owner_id,channel,enabled,minimum_severity,categories,revision,updated_at)
            VALUES(@owner,'in-app',false,'info','{}',1,@now)
            """))
        {
            seed.Parameters.AddWithValue("owner", preferencesOnly);
            seed.Parameters.AddWithValue("now", runtime.Clock.GetUtcNow());
            await seed.ExecuteNonQueryAsync();
        }
        var journalOnly = Guid.NewGuid();
        await using (var db = runtime.Database.Context(journalOnly))
        {
            db.Accounts.Add(new Account(Guid.NewGuid(), journalOnly, "manual", "Journal only"));
            await db.SaveChangesAsync();
        }
        var inventory = runtime.Services.GetRequiredService<SystemNotificationStore>();
        Assert.Equal(new[] { runtime.First, runtime.Second }, await inventory.RetentionOwnersAsync(null, default));
        runtime.Clock.Advance(TimeSpan.FromDays(91));
        var failing = new FailingScopes(runtime.Scopes, runtime.First);
        var retention = new NotificationRetentionService(failing, inventory, new(), runtime.Clock,
            NullLogger<NotificationRetentionService>.Instance);
        await retention.RunRetentionAsync(default);
        Assert.Single((await runtime.PageAsync(runtime.First)).Items);
        Assert.Empty((await runtime.PageAsync(runtime.Second)).Items);
        Assert.Contains(runtime.Second, failing.Attempted);
        Assert.DoesNotContain(preferencesOnly, failing.Attempted);
        Assert.DoesNotContain(journalOnly, failing.Attempted);
        Assert.Equal(2, await runtime.CountAsync("notification_cursors"));
        Assert.Equal(1, await runtime.CountAsync("notification_sources"));
        Assert.Equal(1, await runtime.CountAsync("notification_deliveries"));
        failing.Fail = false;
        await retention.RunRetentionAsync(default);
        Assert.Empty((await runtime.PageAsync(runtime.First)).Items);
        Assert.Equal(0, await runtime.CountAsync("notification_sources"));
        Assert.Equal(0, await runtime.CountAsync("notification_deliveries"));
    }

    [VesselEventPostgresFact]
    public async Task Retention_prunes_suspended_source_only_owners_without_creating_cursors()
    {
        await using var runtime = await Runtime.CreateAsync();
        runtime.Directory.Set(runtime.First, JournalOwnerStatus.Suspended);
        await using var scope = runtime.Scopes.Create(runtime.First);
        var work = scope.Services.GetRequiredService<IUnitOfWork>();
        var hub = scope.Services.GetRequiredService<INotificationHubStore>();
        await work.ExecuteAsync(async () =>
        {
            await hub.RecordHandledAsync([new(Guid.NewGuid(), "test.requested", null)], default);
            return true;
        }, default);
        runtime.Clock.Advance(TimeSpan.FromDays(91));
        await work.ExecuteAsync(async () =>
        {
            await hub.RecordHandledAsync([new(Guid.NewGuid(), "test.requested", null)], default);
            return true;
        }, default);
        Assert.Equal(0, await runtime.CountAsync("notification_cursors"));
        var owners = runtime.Services.GetRequiredService<SystemNotificationStore>();
        Assert.Equal(runtime.First, Assert.Single(await owners.RetentionOwnersAsync(null, default)));
        var retention = new NotificationRetentionService(runtime.Scopes, owners, new(), runtime.Clock,
            NullLogger<NotificationRetentionService>.Instance);
        await retention.RunRetentionAsync(default);
        Assert.Equal(1, await runtime.CountAsync("notification_sources"));
        Assert.Equal(0, await runtime.CountAsync("notification_cursors"));
        // A direct, racing retention call for an owner with no notification rows must also stay read-only.
        await using var empty = runtime.Scopes.Create(runtime.Second);
        await empty.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            () => empty.Services.GetRequiredService<INotificationStore>().RetainAsync(runtime.Clock.GetUtcNow(), default), default);
        Assert.Equal(0, await runtime.CountAsync("notification_cursors"));
    }

    [VesselEventPostgresFact]
    public async Task Notification_retention_discovery_keyset_pages_the_distinct_notification_owner_union()
    {
        await using var runtime = await Runtime.CreateAsync();
        var expected = Enumerable.Range(1, 205).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:x12}")).ToArray();
        await using var seed = runtime.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            INSERT INTO notification_cursors(owner_id) SELECT unnest(@cursors);
            INSERT INTO notification_sources(owner_id,event_id,event_type,handled_at)
            SELECT owner_id,gen_random_uuid(),'test.requested',now()
            FROM unnest(@sources) AS owner_id CROSS JOIN generate_series(1,2);
            """);
        seed.Parameters.AddWithValue("cursors", expected[..150]);
        seed.Parameters.AddWithValue("sources", expected[100..]);
        await seed.ExecuteNonQueryAsync();
        var store = runtime.Services.GetRequiredService<SystemNotificationStore>();
        var first = await store.RetentionOwnersAsync(null, default);
        Assert.Equal(expected[..100], first);
        var second = await store.RetentionOwnersAsync(first[^1], default);
        Assert.Equal(expected[100..200], second);
        var last = await store.RetentionOwnersAsync(second[^1], default);
        Assert.Equal(expected[200..], last);
        Assert.Empty(await store.RetentionOwnersAsync(last[^1], default));
    }

    private static async Task<(bool, string, long)> DefaultsAsync(IOwnerScope scope)
    {
        var preferences = await scope.Services.GetRequiredService<NotificationService>().PreferencesAsync(default);
        return (preferences.Enabled, preferences.MinimumSeverity, preferences.Revision);
    }

    private sealed class FailingScopes(IOwnerScopeFactory inner, Guid failing) : IOwnerScopeFactory
    {
        public bool Fail { get; set; } = true;
        public List<Guid> Attempted { get; } = [];
        public IOwnerScope Create(Guid ownerId)
        {
            Attempted.Add(ownerId);
            if (Fail && ownerId == failing) throw new InvalidOperationException("synthetic scope failure");
            return inner.Create(ownerId);
        }
    }

    private sealed class CountingHeartbeat : IMaintenanceHeartbeat
    {
        public int Beats { get; private set; }
        public Task BeatAsync(CancellationToken cancellationToken) { Beats++; return Task.CompletedTask; }
    }

    internal sealed class Runtime : IAsyncDisposable
    {
        private readonly NotificationsModule module = new();
        private Runtime(CoreDatabase database)
        {
            Database = database;
            Directory.Set(First, JournalOwnerStatus.Active);
            Directory.Set(Second, JournalOwnerStatus.Active);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vessel"] = database.ConnectionString
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<IJournalOwnerContext, AssignedOwnerContext>();
            services.AddSingleton<IJournalOwnerDirectory>(Directory);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddVesselPersistence(configuration);
            services.AddVesselEvents(configuration);
            services.AddScoped<IVesselEventOutbox, NoOutbox>();
            module.AddRunningServices(services, configuration);
            Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }
        public Guid First { get; } = Guid.Parse("11111111-0000-0000-0000-000000000001");
        public Guid Second { get; } = Guid.Parse("22222222-0000-0000-0000-000000000002");
        public CoreDatabase Database { get; }
        public MutableOwnerDirectory Directory { get; } = new();
        public NotificationHubPostgresTests.HubClock Clock { get; } = new();
        public ServiceProvider Services { get; }
        public IOwnerScopeFactory Scopes => Services.GetRequiredService<IOwnerScopeFactory>();
        public SystemEventSubscriptionStore Store => Services.GetRequiredService<SystemEventSubscriptionStore>();
        public EventSubscription Subscription => module.Subscriptions[0];
        public static async Task<Runtime> CreateAsync(string? targetMigration = null)
        {
            var runtime = new Runtime(await CoreDatabase.CreateAsync(targetMigration));
            Assert.True(await runtime.Store.RegisterAsync(runtime.Subscription, default));
            return runtime;
        }
        public async Task<VesselEvent> RequestAsync(Guid owner)
        {
            await using var scope = Scopes.Create(owner);
            await scope.Services.GetRequiredService<NotificationService>().RequestTestAsync(default);
            return Assert.Single((await scope.Services.GetRequiredService<IVesselEventReader>().ReadAsync("0", 100, default)).Events);
        }
        public Task DeliverAsync(VesselEvent message)
        {
            var settings = new MessagingSettings();
            var registry = new VesselModuleRegistry([module]);
            var topology = new MessagingTopology(settings, registry);
            return EventSubscriptionListener.Handle(message,
                new Envelope { Destination = new Uri("rabbitmq://queue/" + topology.SubscriptionQueue(Subscription.Name)) },
                new MessagingHost("notifications", [module]), topology, Scopes, Directory,
                NullLogger<EventSubscriptionHandlerLog>.Instance, default);
        }
        public SubscriptionMaintenanceService Maintenance(params IMaintenanceHeartbeat[] heartbeats) =>
            new(new("notifications", [module]), Scopes, Directory, Store, Clock,
                NullLogger<SubscriptionMaintenanceService>.Instance, heartbeats);
        public async Task<NotificationPage> PageAsync(Guid owner)
        {
            await using var scope = Scopes.Create(owner);
            return await scope.Services.GetRequiredService<NotificationService>().PageAsync(new("all", null, null, 1, 25), default);
        }
        public async Task<long> CountAsync(string table)
        {
            // Test-only fixed table names; never supplied by an API caller.
            await using var command = Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT count(*) FROM " + table);
            return (long)(await command.ExecuteScalarAsync())!;
        }
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed class NoOutbox : IVesselEventOutbox
    {
        public Task StageAsync(VesselEvent message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StageAsync(MonitorAccountCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Reset() { }
    }
}
