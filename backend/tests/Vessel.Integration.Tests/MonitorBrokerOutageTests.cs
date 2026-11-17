using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethereum.Signer;
using Npgsql;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Domain.Monitoring;
using Vessel.Tests.Identity;
using Wolverine.Transports;
using Xunit.Abstractions;

namespace Vessel.Tests;

/// <summary>
/// Issue #98: what the API and the monitor worker do when RabbitMQ is unreachable at start, when it returns, and when
/// it disappears while they run. The worker and the API reach the disposable broker through <see cref="BrokerProxy"/>;
/// provisioning and the verification subscriber use it directly. These tests record today's behaviour, measured; they
/// do not prescribe it (issue #99 decides whether hosts should start without the broker). The timings they print are
/// the source of the table in docs/architecture/messaging-and-workers.md.
/// </summary>
public sealed partial class MonitorEndToEndTests(ITestOutputHelper output)
{
    [VesselEventRabbitFact]
    public async Task Worker_and_api_retry_an_unreachable_broker_for_about_95_seconds_then_fail_to_start()
    {
        await using var proxy = BrokerProxy.Create();
        await using var fixture = await MonitorFixture.PrepareAsync(proxy.Uri);
        var logs = new BackgroundWorkLogs();
        var clock = Stopwatch.StartNew();
        // The worker's broker port refuses connections; the API's broker host name does not resolve.
        var worker = Timed(fixture.StartWorkerAsync(logs), clock);
        fixture.UseHostSetting("Vessel:Messaging:RabbitMqUri", "amqp://verify:unused@vessel-no-such-broker.invalid:5672/");
        var api = Timed(Task.Run(() => fixture.StartApi(logs).Dispose()), clock);
        var (workerError, workerAfter) = await worker;
        var (apiError, apiAfter) = await api;
        output.WriteLine($"worker gave up after {workerAfter}; api gave up after {apiAfter}");

        // Neither host starts. Each makes 20 connection attempts (about 95 s in all) and then fails; the real
        // entrypoints exit (the worker with code 1), so no readiness endpoint ever answers.
        foreach (var (error, after) in new[] { (workerError, workerAfter), (apiError, apiAfter) })
        {
            Assert.IsType<BrokerInitializationException>(error);
            Assert.InRange(after, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(180));
        }
        Assert.Equal(2, logs.Messages.Count(m => m.Text.Contains("on Attempt 20 of 20", StringComparison.Ordinal)));

        // The scheduler starts before Wolverine. Its first pass claims the account, but the run cannot stage its
        // events while the transport is not started, so it fails and leaves its lease for recovery to retry.
        Assert.Contains(logs.Messages, m => m.Category == "Vessel.Worker.MonitorScheduler" &&
            m.Text.Contains("failed with InvalidOperationException", StringComparison.Ordinal));
        var row = await fixture.MonitorRowAsync();
        Assert.NotNull(row);
        Assert.Equal(1, row.Generation);
        Assert.Equal("running", row.ManualState);
        Assert.Null(row.LastSuccessAtUtc);
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM events"));
    }

    [VesselEventRabbitFact]
    public async Task Hosts_finish_starting_when_the_broker_returns_within_the_retry_window()
    {
        await using var proxy = BrokerProxy.Create();
        await using var fixture = await MonitorFixture.PrepareAsync(proxy.Uri);
        var logs = new BackgroundWorkLogs();
        HttpClient? client = null;
        var worker = fixture.StartWorkerAsync(logs);
        var api = Task.Run(() => client = fixture.StartApi(logs));
        await Eventually(() => Task.FromResult(logs.Messages.Any(m => m.Category == "Vessel.Worker.MonitorScheduler" &&
            m.Text.Contains("failed with InvalidOperationException", StringComparison.Ordinal))), "The first scheduled run did not fail.");
        // That is the failure: Wolverine refuses to stage an outgoing event before its transport has started.
        var staged = await Assert.ThrowsAsync<InvalidOperationException>(fixture.AppendThroughWorkerAsync);
        Assert.Contains("sending connection has not been created", staged.Message, StringComparison.Ordinal);

        await Task.Delay(TimeSpan.FromSeconds(10));
        Assert.False(worker.IsCompleted);
        Assert.False(api.IsCompleted);
        await proxy.UpAsync();
        var clock = Stopwatch.StartNew();
        await worker.WaitAsync(TimeSpan.FromSeconds(30));
        await api.WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine($"both hosts started {clock.Elapsed} after the broker returned");
        using var _ = client;

        // The account claimed by the failed run stays leased: the next tick does not run it.
        var row = (await fixture.MonitorRowAsync())!;
        Assert.Equal(1, row.Generation);
        Assert.Equal("running", row.ManualState);
        fixture.Clock.TickScheduler();
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(1, (await fixture.MonitorRowAsync())!.Generation);
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM events"));
        // Lease recovery retries it once the five-minute lease has expired.
        fixture.Clock.Set(row.LeaseUntilUtc!.Value.AddMilliseconds(1));
        fixture.Clock.TickScheduler();
        await fixture.WaitForRunAsync(2);
        Assert.Equal("live", (await fixture.MonitorAsync()).State);
        var events = await fixture.ScalarAsync("SELECT count(*) FROM events");
        Assert.True(events > 0);
        await fixture.WaitForReceiptsAsync((int)events);
    }

    /// <summary>
    /// With <c>Vessel_TEST_BROKER_SWITCH</c> set, the outage is a real broker stop and start instead of the relay
    /// dropping its connections (see <see cref="BrokerProxy"/>); the assertions are the same.
    /// </summary>
    [VesselEventRabbitFact]
    public async Task Checks_continue_while_the_broker_is_away_and_its_events_are_delivered_once_when_it_returns()
    {
        await using var proxy = BrokerProxy.Create();
        await proxy.UpAsync();
        await using var identity = await IdentityDatabase.CreateAsync();
        await using var fixture = await MonitorFixture.PrepareAsync(proxy.Uri);
        await identity.SeedAsync(EthECKey.GenerateKey(), fixture.Owner);
        fixture.UseHostSetting("Vessel:Identity:Schema", identity.Schema.Name);
        var logs = new BackgroundWorkLogs();
        await fixture.StartWorkerAsync(logs);
        using var client = fixture.StartApi(logs);
        await fixture.WaitForRunAsync(1);
        var delivered = await fixture.ScalarAsync("SELECT count(*) FROM events");
        await fixture.WaitForReceiptsAsync((int)delivered);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var quiet = logs.Messages.Count;

        await proxy.DownAsync();
        var clock = Stopwatch.StartNew();
        // Readiness covers PostgreSQL, migrations and identity only.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        // Scheduled checks keep running; their events commit in PostgreSQL and wait in the worker's outbox.
        await fixture.ScheduledReadAsync(1);
        Assert.True(await fixture.ScalarAsync("SELECT count(*) FROM events") > delivered);
        Assert.True(await fixture.OutboxAsync("monitor") > 0);

        // A manual Check is accepted; its command waits in the API's outbox, and the next scheduler tick runs it.
        var before = await fixture.MonitorAsync();
        fixture.Clock.Set(before.NextManualAllowedAtUtc!.Value);
        using (var check = await client.PostAsync($"/api/accounts/{fixture.AccountId}/monitor/run", null))
        {
            Assert.Equal(HttpStatusCode.Accepted, check.StatusCode);
            Assert.Equal("pending", (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("monitor")
                .GetProperty("manualState").GetString());
        }
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(before.Generation, (await fixture.MonitorAsync()).Generation);
        Assert.Equal(1, await fixture.OutboxAsync("api"));
        fixture.Clock.TickScheduler();
        await fixture.WaitForRunAsync(before.Generation + 1);
        Assert.Equal("live", (await fixture.MonitorAsync()).State);
        await fixture.ScheduledReadAsync(2);
        var appended = await fixture.ScalarAsync("SELECT count(*) FROM events");
        Assert.Equal(delivered, await fixture.ScalarAsync("SELECT count(*) FROM event_receipts"));
        output.WriteLine($"outage lasted {clock.Elapsed}; {appended - delivered} events appended meanwhile");

        // Only Wolverine logs the outage (at Error; a graceful broker stop logs its channel shutdowns at Warning), without the
        // broker credentials. Vessel's own code logs nothing.
        var outage = logs.Messages.Skip(quiet).Where(m => m.Level is >= LogLevel.Warning and < LogLevel.None).ToArray();
        foreach (var group in outage.GroupBy(m => (m.Level, m.Category, Text: m.Text.Split('\n')[0])))
            output.WriteLine($"x{group.Count()} {group.Key.Level} {group.Key.Category}: {group.Key.Text}");
        Assert.Contains(outage, m => m.Level == LogLevel.Error && m.Category.StartsWith("Wolverine.RabbitMQ", StringComparison.Ordinal));
        Assert.DoesNotContain(outage, m => m.Category.StartsWith("Vessel", StringComparison.Ordinal));
        var password = new Uri(proxy.Uri).UserInfo.Split(':')[1];
        Assert.DoesNotContain(logs.Messages, m => m.Text.Contains(password, StringComparison.Ordinal));

        await proxy.UpAsync();
        clock.Restart();
        // Both outboxes drain and every event appended meanwhile reaches the subscriber exactly once.
        await Eventually(async () => await fixture.OutboxAsync("monitor") == 0 && await fixture.OutboxAsync("api") == 0,
            "The outboxes did not drain after the broker returned.");
        await fixture.WaitForReceiptsAsync((int)appended);
        Assert.Equal(appended, await fixture.ScalarAsync("SELECT count(*) FROM event_receipts"));
        output.WriteLine($"outboxes drained and receipts settled {clock.Elapsed} after the broker returned");
        foreach (var group in logs.Messages.Skip(quiet).Where(m => m.Level == LogLevel.Information && m.Category.StartsWith("Wolverine.RabbitMQ",
                     StringComparison.Ordinal)).GroupBy(m => (m.Category, Text: m.Text.Split('\n')[0])))
            output.WriteLine($"x{group.Count()} {group.Key.Category}: {group.Key.Text}");

        // The worker's command listener consumes again: a Check wakes the scheduler without a tick.
        before = await fixture.MonitorAsync();
        fixture.Clock.Set(before.NextManualAllowedAtUtc!.Value);
        using (var check = await client.PostAsync($"/api/accounts/{fixture.AccountId}/monitor/run", null))
            Assert.Equal(HttpStatusCode.Accepted, check.StatusCode);
        await fixture.WaitForRunAsync(before.Generation + 1);
    }

    private static async Task<(Exception? Error, TimeSpan After)> Timed(Task start, Stopwatch clock)
    {
        var error = await Record.ExceptionAsync(() => start.WaitAsync(TimeSpan.FromMinutes(4)));
        return (error, clock.Elapsed);
    }

    private sealed partial class MonitorFixture
    {
        public void UseHostSetting(string key, string? value) => HostConfig[key] = value;

        public async Task StartWorkerAsync(ILoggerProvider logs)
        {
            worker = BuildWorker(logs);
            await worker.StartAsync();
        }

        public HttpClient StartApi(ILoggerProvider logs)
        {
            api = new(new(HostConfig), Clock, Venue, Owner, logs);
            return ApiClient();
        }

        /// <summary>Appends one event through the worker's own unit of work and outbox.</summary>
        public async Task AppendThroughWorkerAsync()
        {
            await using var scope = worker!.Services.GetRequiredService<IOwnerScopeFactory>().Create(Owner);
            var sink = scope.Services.GetRequiredService<IVesselEventSink>();
            await scope.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(async () =>
            {
                await sink.AppendAsync(new("monitor.state-changed", Guid.CreateVersion7(), "probe-" + Guid.NewGuid().ToString("N"),
                    "info", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(), new("owner"),
                    JsonSerializer.SerializeToElement(new { previous = "idle", current = "live" })), default);
                return true;
            }, default);
        }

        public async Task<MonitorAccount?> MonitorRowAsync()
        {
            await using var db = Database.Context(Owner);
            return await db.Set<MonitorAccount>().AsNoTracking().SingleOrDefaultAsync(m => m.AccountId == AccountId);
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public Task<long> OutboxAsync(string deployment) =>
            ScalarAsync($"SELECT count(*) FROM {Schema(deployment)}.wolverine_outgoing_envelopes");
    }
}
