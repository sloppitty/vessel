using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Signer;
using Npgsql;
using Vessel.Application.MarketData;
using Vessel.Application.Notifications;
using Vessel.Persistence;
using Vessel.Persistence.Notifications;
using Vessel.Tests.Identity;
using Vessel.Worker;

namespace Vessel.Tests;

public sealed class NotificationServerStateTests
{
    [VesselEventPostgresFact]
    public async Task Notification_health_is_per_configured_deployment_and_never_makes_monitor_health_green()
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var data = NpgsqlDataSource.Create(database.ConnectionString);
        var notifications = new NotificationWorkerState(data, new(["alerts"]));
        var unrelated = new NotificationWorkerState(data, new(["other"]));
        var monitor = new ServerStateStore(data, new(["checks"]));
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Null(await notifications.HeartbeatAsync(default));
        await unrelated.RecordAsync("other", now, default);
        Assert.Null(await notifications.HeartbeatAsync(default));
        Assert.Null(await monitor.HeartbeatAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => notifications.RecordAsync("checks", now, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.RecordHeartbeatAsync("alerts", now, default));
        await monitor.RecordHeartbeatAsync("checks", now, default);
        Assert.Null(await notifications.HeartbeatAsync(default));
        await notifications.RecordAsync("alerts", now, default);
        await notifications.RecordAsync("alerts", now.AddMinutes(-1), default);
        Assert.Equal(now, await notifications.HeartbeatAsync(default));
        Assert.Equal("checks", (await monitor.HeartbeatAsync(default))!.Deployment);
        foreach (var owner in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString, settings: new()
            {
                ["Vessel:Messaging:Deployments:alerts:0"] = "notifications",
                ["Vessel:Messaging:Deployments:checks:0"] = "monitor"
            });
            using var client = factory.AuthorizedClient();
            var head = (await client.GetFromJsonAsync<NotificationHeadDto>("/api/notifications/head"))!;
            Assert.True(head.WorkerOnline);
            Assert.Equal(now, head.HeartbeatAtUtc);
        }
        await using var oldTable = data.CreateCommand("SELECT to_regclass('notification_heartbeats') IS NULL");
        Assert.Equal(true, await oldTable.ExecuteScalarAsync());
    }

    [VesselEventPostgresFact]
    public async Task Upgrade_discards_owner_heartbeats_and_hub_stays_offline_until_a_productive_pass()
    {
        await using var runtime = await NotificationOwnerTests.Runtime.CreateAsync("20261018110000_ServerMonitorState");
        await using (var seed = runtime.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            INSERT INTO notification_heartbeats(owner_id,worker_id,heartbeat_at)
            VALUES(@first,'old-notifications',@now),(@second,'old-other-worker',@now)
            """))
        {
            seed.Parameters.AddWithValue("first", runtime.First);
            seed.Parameters.AddWithValue("second", runtime.Second);
            seed.Parameters.AddWithValue("now", runtime.Clock.GetUtcNow());
            Assert.Equal(2, await seed.ExecuteNonQueryAsync());
        }
        await using (var db = runtime.Database.Context(runtime.First))
            await db.GetService<IMigrator>().MigrateAsync("20261018120000_ServerNotificationState");
        Assert.Equal(0, await runtime.CountAsync("server_notification_heartbeats"));
        await using (var oldTable = runtime.Services.GetRequiredService<NpgsqlDataSource>()
                         .CreateCommand("SELECT to_regclass('notification_heartbeats') IS NULL"))
            Assert.Equal(true, await oldTable.ExecuteScalarAsync());
        await using var factory = new CoreApiFactory(runtime.First, connection: runtime.Database.ConnectionString,
            configure: services => services.AddSingleton<TimeProvider>(runtime.Clock));
        using var client = factory.AuthorizedClient();
        var head = (await client.GetFromJsonAsync<NotificationHeadDto>("/api/notifications/head"))!;
        Assert.False(head.WorkerOnline);
        Assert.Null(head.HeartbeatAtUtc);
        var file = Path.Combine(Path.GetTempPath(), "vessel-mu-upgrade-" + Guid.NewGuid().ToString("N"));
        try
        {
            var heartbeat = new NotificationHeartbeat(runtime.Services.GetRequiredService<NotificationWorkerState>(),
                new() { Deployment = "notifications", Modules = ["notifications"] }, new() { HeartbeatFile = file }, runtime.Clock);
            var maintenance = runtime.Maintenance(heartbeat);
            await runtime.RequestAsync(runtime.First);
            runtime.Clock.Advance(TimeSpan.FromMinutes(1));
            runtime.Directory.Checking = (_, _) => Task.FromException(new InvalidOperationException("synthetic owner failure"));
            Assert.False(await maintenance.RunCatchUpPassAsync(default));
            Assert.Equal(0, await runtime.CountAsync("server_notification_heartbeats"));
            Assert.False((await client.GetFromJsonAsync<NotificationHeadDto>("/api/notifications/head"))!.WorkerOnline);
            runtime.Directory.Checking = null;
            Assert.True(await maintenance.RunCatchUpPassAsync(default));
            head = (await client.GetFromJsonAsync<NotificationHeadDto>("/api/notifications/head"))!;
            Assert.True(head.WorkerOnline);
            Assert.Equal(runtime.Clock.GetUtcNow(), head.HeartbeatAtUtc);
            Assert.Single((await runtime.PageAsync(runtime.First)).Items);
        }
        finally { File.Delete(file); }
    }

    [VesselEventPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_a_productive_cross_owner_catch_up_pass_records_the_notification_deployment_heartbeat(bool secondHealthy)
    {
        await using var runtime = await NotificationOwnerTests.Runtime.CreateAsync();
        var file = Path.Combine(Path.GetTempPath(), "vessel-mu-notifications-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = runtime.Services.GetRequiredService<NotificationWorkerState>();
            var worker = new WorkerSettings { Deployment = "notifications", Modules = ["notifications"] };
            var heartbeat = new NotificationHeartbeat(state, worker, new() { HeartbeatFile = file }, runtime.Clock);
            var maintenance = runtime.Maintenance(heartbeat);
            await runtime.RequestAsync(runtime.First);
            await runtime.RequestAsync(runtime.Second);
            runtime.Clock.Advance(TimeSpan.FromMinutes(1));
            runtime.Directory.Checking = (owner, _) => owner == runtime.First || !secondHealthy
                ? Task.FromException(new InvalidOperationException("synthetic owner failure")) : Task.CompletedTask;
            Assert.Equal(secondHealthy, await maintenance.RunCatchUpPassAsync(default));
            Assert.Equal(secondHealthy ? runtime.Clock.GetUtcNow() : (DateTimeOffset?)null, await state.HeartbeatAsync(default));
            Assert.Equal(secondHealthy, File.Exists(file));
            Assert.Equal(secondHealthy ? 1 : 0, (await runtime.PageAsync(runtime.Second)).Items.Count);
            runtime.Directory.Checking = null;
            Assert.True(await maintenance.RunCatchUpPassAsync(default));
            Assert.Equal(runtime.Clock.GetUtcNow(), await state.HeartbeatAsync(default));
            Assert.True(File.Exists(file));
            var monitor = new ServerStateStore(runtime.Services.GetRequiredService<NpgsqlDataSource>(), new(["monitor"]));
            Assert.Null(await monitor.HeartbeatAsync(default));
            var lastProductive = await state.HeartbeatAsync(default);
            runtime.Clock.Advance(TimeSpan.FromSeconds(30));
            worker.Deployment = "not-notifications";
            Assert.False(await maintenance.RunCatchUpPassAsync(default));
            Assert.Equal(lastProductive, await state.HeartbeatAsync(default));
            Assert.False(File.Exists(file));
        }
        finally { File.Delete(file); }
    }

    [VesselEventPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_notifications_are_capped_at_six_requests_per_user_per_minute_in_both_auth_modes(bool wallet)
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var identity = await IdentityDatabase.CreateAsync();
        var firstKey = EthECKey.GenerateKey();
        var secondKey = EthECKey.GenerateKey();
        var first = await identity.SeedAsync(firstKey);
        var second = await identity.SeedAsync(secondKey);
        var clock = new NotificationHubPostgresTests.HubClock();
        var limits = new UserReadLimits(clock);
        var settings = wallet ? identity.ConfigurationValues(first.User.Id) : new Dictionary<string, string?>();
        settings["ConnectionStrings:Vessel"] = database.ConnectionString;
        settings["Vessel:Notifications:TestCooldown"] = "00:00:01";
        void Configure(IServiceCollection services)
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(limits);
        }
        await using var firstFactory = new CoreApiFactory(first.User.Id, settings: settings, configure: Configure);
        await using var secondFactory = new CoreApiFactory(second.User.Id, connection: database.ConnectionString,
            settings: new() { ["Vessel:Notifications:TestCooldown"] = "00:00:01" }, configure: Configure);
        using var firstClient = wallet
            ? firstFactory.CreateClient(new() { BaseAddress = new("https://vessel.test"), HandleCookies = false })
            : firstFactory.AuthorizedClient();
        using var secondClient = wallet
            ? firstFactory.CreateClient(new() { BaseAddress = new("https://vessel.test"), HandleCookies = false })
            : secondFactory.AuthorizedClient();
        if (wallet)
            foreach (var (client, key, owner) in new[]
                     { (firstClient, firstKey, first.User.Id), (secondClient, secondKey, second.User.Id) })
            {
                var session = await identity.SignInAsync(key);
                client.DefaultRequestHeaders.Add("Cookie", "__Host-vessel-session=" + session.Token);
                client.DefaultRequestHeaders.Add("X-Vessel-User", owner.ToString());
                client.DefaultRequestHeaders.Add("X-Vessel-Request", "1");
                client.DefaultRequestHeaders.Add("Origin", "https://vessel.test");
            }
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await firstClient.PostAsync("/api/notifications/test", null)).StatusCode);
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        var limited = await firstClient.PostAsync("/api/notifications/test", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        Assert.Equal(HttpStatusCode.Accepted, (await secondClient.PostAsync("/api/notifications/test", null)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Accepted, (await firstClient.PostAsync("/api/notifications/test", null)).StatusCode);
    }
}
