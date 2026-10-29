using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Domain.Monitoring;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Owner Check needs a live worker; check work nobody claimed must not block Refresh indefinitely. These tests use the
/// real stored heartbeat (the signal behind "worker offline"), never <see cref="MonitorWorkerOnline"/>.
/// </summary>
public sealed partial class MonitorPostgresTests
{
    private static readonly TimeSpan Unclaimed = MonitorOptions.Lease + new MonitorOptions().HeartbeatTimeout;

    private static MonitorCoordinator OfflineAware(VesselDbContext db, Guid owner, Clock clock, CountingCommands commands,
        EventSink? sink = null)
    {
        var work = TestUnitOfWork.For(db);
        var options = new MonitorOptions();
        return new(MonitorTestPaths.Store(db, work, options), new WorkspaceStore(db, work), new Importer(),
            TestVenues.With(new Reader(), new Orders()), new Credentials(), work, sink ?? new EventSink(), commands,
            new CoreOwner(owner), options, clock);
    }

    private static async Task<MonitorAccount?> RowAsync(CoreDatabase database, Guid owner)
    {
        await using var read = database.Context(owner);
        return await read.Set<MonitorAccount>().AsNoTracking().SingleOrDefaultAsync();
    }

    [PostgresFact]
    public async Task Offline_check_with_a_stale_heartbeat_is_refused_503_and_records_no_pending_work()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var commands = new CountingCommands();
        var coordinator = OfflineAware(db, owner, clock, commands);

        // Never deployed: no heartbeat at all.
        var refused = await Assert.ThrowsAsync<ProblemException>(() => coordinator.RequestAsync(id, default));
        Assert.Equal((503, MonitorCoordinator.MonitorOffline), (refused.StatusCode, refused.Message));
        Assert.Null(await RowAsync(database, owner));

        // A worker that stopped: its heartbeat is older than the timeout.
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor",
            clock.Now - new MonitorOptions().HeartbeatTimeout - TimeSpan.FromSeconds(1), default);
        var stale = await Assert.ThrowsAsync<ProblemException>(() => coordinator.RequestAsync(id, default));
        Assert.Equal(503, stale.StatusCode);
        Assert.Null(await RowAsync(database, owner));
        Assert.Equal(0, commands.Count);
        Assert.False((await coordinator.AccountAsync(id, default)).WorkerOnline);
    }

    [PostgresFact]
    public async Task Offline_check_with_a_fresh_heartbeat_is_accepted_as_before()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.Now.AddSeconds(-10), default);
        var commands = new CountingCommands();
        var accepted = await OfflineAware(db, owner, clock, commands).RequestAsync(id, default);
        Assert.Equal(("pending", "owner", true), (accepted.Monitor.ManualState, accepted.Monitor.RunTrigger, accepted.Monitor.WorkerOnline));
        Assert.Equal(1, commands.Count);
        var row = (await RowAsync(database, owner))!;
        Assert.Equal((accepted.RequestId, "manual", (DateTimeOffset?)clock.Now), (row.RequestId!.Value, row.Trigger, row.NextRunAtUtc));
    }

    [PostgresFact]
    public async Task Offline_fresh_pending_check_still_refuses_refresh_and_check_with_retry_after()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.Now, default);
        var coordinator = OfflineAware(db, owner, clock, new CountingCommands());
        await coordinator.RequestAsync(id, default);
        // Just inside the threshold the request is still waiting for its worker.
        clock.Now += Unclaimed - TimeSpan.FromSeconds(1);
        var refresh = await Assert.ThrowsAsync<MonitorAdmissionException>(() => coordinator.AdmitRefreshAsync(id, "api", default));
        Assert.Equal(30, refresh.RetryAfter);
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.Now, default);
        Assert.Equal(30, (await Assert.ThrowsAsync<MonitorAdmissionException>(() => coordinator.RequestAsync(id, default))).RetryAfter);
        Assert.Equal("pending", (await RowAsync(database, owner))!.ManualState);
    }

    [PostgresFact]
    public async Task Offline_unclaimed_pending_check_stops_blocking_refresh_and_a_returning_worker_still_runs_it()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.Now, default);
        var coordinator = OfflineAware(db, owner, clock, new CountingCommands());
        var requested = clock.Now;
        await coordinator.RequestAsync(id, default);

        // The worker died right after the Check was accepted; nobody claims it.
        clock.Now += Unclaimed;
        var lease = await coordinator.AdmitRefreshAsync(id, "api", default);
        Assert.Equal("refresh", lease.Trigger);
        Assert.True(await coordinator.RunAsync(lease, default));
        var afterRefresh = (await RowAsync(database, owner))!;
        Assert.Equal("idle", afterRefresh.ManualState);
        // The check is not lost: it stays due from when it was requested.
        Assert.Equal(requested, afterRefresh.NextRunAtUtc);
        Assert.Null(afterRefresh.LastAttemptAtUtc);

        // The worker returns: its next scheduled claim runs the outstanding check.
        clock.Now += TimeSpan.FromSeconds(5);
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.Now, default);
        var claimed = Assert.Single(await store.ClaimDueAsync("worker", clock.Now, 2, default));
        Assert.Equal((id, "monitor"), (claimed.AccountId, claimed.Trigger));
        Assert.True(await coordinator.RunAsync(claimed, default));
        var ran = (await RowAsync(database, owner))!;
        Assert.Equal(("idle", "live", (DateTimeOffset?)clock.Now), (ran.ManualState, ran.State, ran.LastAttemptAtUtc));
    }

    [PostgresFact]
    public async Task Offline_unclaimed_pending_check_is_replaced_by_a_new_check_once_the_worker_is_back()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.Now, default);
        var commands = new CountingCommands();
        var coordinator = OfflineAware(db, owner, clock, commands);
        var first = await coordinator.RequestAsync(id, default);
        clock.Now += Unclaimed;
        // Still offline: refused without touching the stale request.
        Assert.Equal(503, (await Assert.ThrowsAsync<ProblemException>(() => coordinator.RequestAsync(id, default))).StatusCode);
        Assert.Equal((first.RequestId, "pending"), ((await RowAsync(database, owner))!.RequestId!.Value, (await RowAsync(database, owner))!.ManualState));
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.Now, default);
        var second = await coordinator.RequestAsync(id, default);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal(2, commands.Count);
        var row = (await RowAsync(database, owner))!;
        Assert.Equal((second.RequestId, "pending", "manual"), (row.RequestId!.Value, row.ManualState, row.Trigger));
    }

    [PostgresFact]
    public async Task Offline_http_check_returns_503_with_the_fixed_message()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        await using (var seed = database.Context(owner)) await SeedPlannedAsync(seed, DateTimeOffset.UtcNow);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        using var response = await client.PostAsync($"/api/accounts/{id}/monitor/run", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(MonitorCoordinator.MonitorOffline, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
        Assert.Null(await RowAsync(database, owner));
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Offline_an_expired_owner_check_completes_its_own_request_as_superseded_when_admission_replaces_it(bool byCheck)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.Now, default);
        var sink = new EventSink();
        var coordinator = OfflineAware(db, owner, clock, new CountingCommands(), sink);
        var requested = clock.Now;
        var first = await coordinator.RequestAsync(id, default);
        Assert.DoesNotContain(sink.Drafts, d => d.Type == "monitor.run-completed");
        clock.Now += Unclaimed;
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.Now, default);
        Guid replacement;
        if (byCheck) replacement = (await coordinator.RequestAsync(id, default)).RequestId;
        else replacement = (await coordinator.AdmitRefreshAsync(id, "api", default)).RequestId;
        var completed = Assert.Single(sink.Drafts, d => d.Type == "monitor.run-completed");
        Assert.Equal((first.RequestId, $"monitor-run:{id}:{first.RequestId}"), (completed.RunId, completed.DedupKey));
        Assert.Equal("manual", completed.Data.GetProperty("trigger").GetString());
        Assert.Equal("superseded", completed.Data.GetProperty("outcome").GetString());
        var row = (await RowAsync(database, owner))!;
        Assert.Equal(replacement, row.RequestId);
        // A replacing Check is due now; a Refresh leaves the expired check's due time unchanged.
        Assert.Equal(byCheck ? clock.Now : requested, row.NextRunAtUtc);
    }

    [PostgresFact]
    public async Task Offline_a_503_refused_check_writes_no_completion_for_an_expired_request()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", clock.Now, default);
        var sink = new EventSink();
        var coordinator = OfflineAware(db, owner, clock, new CountingCommands(), sink);
        var first = await coordinator.RequestAsync(id, default);
        var before = (await RowAsync(database, owner))!;
        clock.Now += Unclaimed;
        Assert.Equal(503, (await Assert.ThrowsAsync<ProblemException>(() => coordinator.RequestAsync(id, default))).StatusCode);
        Assert.DoesNotContain(sink.Drafts, d => d.Type == "monitor.run-completed");
        var after = (await RowAsync(database, owner))!;
        Assert.Equal((first.RequestId, "pending", "manual", before.NextRunAtUtc, before.Generation),
            (after.RequestId!.Value, after.ManualState, after.Trigger, after.NextRunAtUtc, after.Generation));
    }

    private sealed class CountingCommands : IMonitorCommandSink
    {
        public int Count;
        public Task EnqueueAsync(MonitorAccountCommand command, CancellationToken ct) { Interlocked.Increment(ref Count); return Task.CompletedTask; }
    }
}
