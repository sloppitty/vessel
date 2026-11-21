using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethereum.Signer;
using Npgsql;
using Vessel.Domain.Monitoring;
using Vessel.Messaging;
using Vessel.Tests.Identity;
using Xunit.Abstractions;

namespace Vessel.Tests;

/// <summary>
/// Issue #99 (replacing #98's broker-outage tests): the API and the monitor worker need only PostgreSQL. They start at
/// once, a Check wakes the worker through a PostgreSQL notification, and losing the worker's listening connection
/// only delays work to the next scheduler tick.
/// </summary>
public sealed partial class MonitorEndToEndTests(ITestOutputHelper output)
{
    [VesselEventPostgresFact]
    public async Task Api_and_monitor_worker_start_with_only_postgresql_and_a_check_wakes_the_worker()
    {
        await using var identity = await IdentityDatabase.CreateAsync();
        await using var fixture = await MonitorFixture.PrepareAsync();
        await identity.SeedAsync(EthECKey.GenerateKey(), fixture.Owner);
        fixture.UseHostSetting("Vessel:Identity:Schema", identity.Schema.Name);
        Assert.DoesNotContain(fixture.HostSettings, key => key.Contains("RabbitMq", StringComparison.OrdinalIgnoreCase));
        var logs = new BackgroundWorkLogs();
        var clock = Stopwatch.StartNew();
        await fixture.StartWorkerAsync(logs);
        using var client = fixture.StartApi(logs);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        output.WriteLine($"worker and api started and ready after {clock.Elapsed}");
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        // The first scheduled run succeeds and every event it appended reaches the subscriber once, live.
        await fixture.WaitForRunAsync(1);
        Assert.Equal("live", (await fixture.MonitorAsync()).State);
        var events = await fixture.ScalarAsync("SELECT count(*) FROM events");
        Assert.True(events > 0);
        await fixture.WaitForReceiptsAsync((int)events);

        // A Check wakes the scheduler through its notification: no tick is needed.
        var before = await fixture.MonitorAsync();
        fixture.Clock.Set(before.NextManualAllowedAtUtc!.Value);
        clock.Restart();
        using (var check = await client.PostAsync($"/api/accounts/{fixture.AccountId}/monitor/run", null))
            Assert.Equal(HttpStatusCode.Accepted, check.StatusCode);
        await fixture.WaitForRunAsync(before.Generation + 1);
        output.WriteLine($"the check ran {clock.Elapsed} after it was accepted");
        Assert.DoesNotContain(logs.Messages, m => m.Level is >= LogLevel.Warning and < LogLevel.None &&
            m.Category.StartsWith("Vessel", StringComparison.Ordinal));
    }

    [VesselEventPostgresFact]
    public async Task A_lost_listening_connection_only_delays_a_check_to_the_next_tick_and_reconnects()
    {
        await using var fixture = await MonitorFixture.StartAsync();
        using var client = fixture.ApiClient();
        var listener = fixture.WorkerListener();
        Assert.Equal(1, listener.Connections);

        // Without a listener the notification is lost: the pending request waits for the scheduler tick.
        await listener.StopAsync(default);
        var before = await fixture.MonitorAsync();
        fixture.Clock.Set(before.NextManualAllowedAtUtc!.Value);
        using (var check = await client.PostAsync($"/api/accounts/{fixture.AccountId}/monitor/run", null))
            Assert.Equal(HttpStatusCode.Accepted, check.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(("pending", before.Generation), await fixture.ManualStateAsync());
        fixture.Clock.TickScheduler();
        await fixture.WaitForRunAsync(before.Generation + 1);

        // A killed connection is replaced, and the next Check wakes the worker again without a tick.
        await listener.StartAsync(default);
        await Eventually(() => Task.FromResult(listener.ProcessId != 0), "The listener did not reconnect after its restart.");
        var killed = listener.ProcessId;
        Assert.True(await fixture.TerminateAsync(killed));
        await Eventually(() => Task.FromResult(listener.ProcessId is not 0 && listener.ProcessId != killed),
            "The listener did not reconnect after its connection was terminated.");
        Assert.True(listener.Connections >= 3);
        before = await fixture.MonitorAsync();
        fixture.Clock.Set(before.NextManualAllowedAtUtc!.Value);
        using (var check = await client.PostAsync($"/api/accounts/{fixture.AccountId}/monitor/run", null))
            Assert.Equal(HttpStatusCode.Accepted, check.StatusCode);
        await fixture.WaitForRunAsync(before.Generation + 1);
    }

    private sealed partial class MonitorFixture
    {
        public IEnumerable<string> HostSettings => HostConfig.Keys;

        public void UseHostSetting(string key, string? value) => HostConfig[key] = value;

        public async Task StartWorkerAsync(ILoggerProvider logs)
        {
            worker = BuildWorker(logs);
            await worker.StartAsync();
            await ListeningAsync(worker);
        }

        public HttpClient StartApi(ILoggerProvider logs)
        {
            api = new(new(HostConfig), Clock, Venue, Owner, logs);
            return ApiClient();
        }

        public PostgresNotificationListener WorkerListener() =>
            worker!.Services.GetServices<IHostedService>().OfType<PostgresNotificationListener>().Single();

        public async Task<(string ManualState, long Generation)> ManualStateAsync()
        {
            await using var db = Database.Context(Owner);
            var row = await db.Set<MonitorAccount>().AsNoTracking().SingleAsync(m => m.AccountId == AccountId);
            return (row.ManualState, row.Generation);
        }

        public async Task<bool> TerminateAsync(int processId)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection);
            command.Parameters.AddWithValue("pid", processId);
            return (bool)(await command.ExecuteScalarAsync())!;
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }
}
