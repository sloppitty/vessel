using Vessel.Application.Common;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Infrastructure.Monitoring;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed partial class MonitorPostgresTests
{
    [PostgresFact]
    public async Task Rejected_order_credential_pauses_scheduled_monitoring_instead_of_generic_retry()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(), new PerpetualInstrument("hyperliquid", "BTC"),
            InstrumentSource.Venue, "{}", clock.Now.AddMinutes(-2));
        play.MarkPlanned(clock.Now.AddMinutes(-1));
        db.Add(play);
        await db.SaveChangesAsync();
        var coordinator = Coordinator(db, owner, new Reader(), clock: clock,
            orders: new Orders { Failure = new VenueCredentialRejectedException() });
        var request = await coordinator.RequestAsync(id, default);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        var lease = (await store.ClaimAsync(id, request.RequestId, "worker", clock.Now, default))!;
        Assert.False(await coordinator.RunAsync(lease, default));
        var row = (await store.FindAsync(id, default))!;
        Assert.Equal("credential-error", row.State);
        Assert.Null(row.NextRunAtUtc);
        Assert.Null(row.LastSuccessAtUtc);
        Assert.Equal(clock.Now.AddSeconds(60), row.NextManualAllowedAtUtc);
        clock.Now = clock.Now.AddHours(1);
        await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default);
        Assert.Empty(await store.ClaimDueAsync("scheduler", clock.Now, 2, default));
    }

    [PostgresFact]
    public async Task A_completed_owner_check_with_unchanged_state_emits_a_run_completed_event()
    {
        // Complete() returns ManualState to idle; the browser needs an event even when State is unchanged.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(), new PerpetualInstrument("hyperliquid", "BTC"),
            InstrumentSource.Venue, "{}", clock.Now.AddMinutes(-2));
        play.MarkPlanned(clock.Now.AddMinutes(-1));
        db.Add(play);
        await db.SaveChangesAsync();
        var sink = new EventSink();
        var coordinator = Coordinator(db, owner, new Reader(), sink, clock);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        var first = await coordinator.RequestAsync(id, default);
        Assert.Equal("pending", first.Monitor.ManualState);
        Assert.True(await coordinator.RunAsync((await store.ClaimAsync(id, first.RequestId, "worker", clock.Now, default))!, default));
        Assert.Equal("live", (await store.FindAsync(id, default))!.State);
        clock.Now = clock.Now.AddMinutes(2);
        await Task.Delay(20);
        var second = await coordinator.RequestAsync(id, default);
        Assert.Equal("pending", second.Monitor.ManualState);
        Assert.Equal("owner", second.Monitor.RunTrigger);
        Assert.True(await coordinator.RunAsync((await store.ClaimAsync(id, second.RequestId, "worker", clock.Now, default))!, default));
        var row = (await store.FindAsync(id, default))!;
        Assert.Equal("idle", row.ManualState);
        Assert.Equal("live", row.State);
        Assert.Null((await coordinator.AccountAsync(id, default)).RunTrigger);
        // The browser needs some event for this run to leave "Check pending…" without waiting for the 60 s poll.
        var completed = Assert.Single(sink.Drafts, d => d.RunId == second.RequestId);
        Assert.Equal("monitor.run-completed", completed.Type);
        Assert.Equal($"monitor-run:{id}:{second.RequestId}", completed.DedupKey);
        Assert.Equal("info", completed.Severity);
        Assert.Equal(id, completed.Subject.AccountId);
        Assert.Equal("""{"trigger":"manual","outcome":"succeeded","state":"live"}""", completed.Data.GetRawText());
        Assert.Contains(VesselEventValues.Types, type => type == completed.Type);
    }

    [PostgresFact]
    public async Task Scheduled_runs_are_labelled_apart_from_owner_checks_and_do_not_emit_run_completed()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(), new PerpetualInstrument("hyperliquid", "BTC"),
            InstrumentSource.Venue, "{}", clock.Now.AddMinutes(-2));
        play.MarkPlanned(clock.Now.AddMinutes(-1));
        db.Add(play);
        await db.SaveChangesAsync();
        var sink = new EventSink();
        var coordinator = Coordinator(db, owner, new Reader(), sink, clock);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default);
        var lease = Assert.Single(await store.ClaimDueAsync("scheduler", clock.Now, 2, default));
        Assert.Equal("scheduled", (await coordinator.AccountAsync(id, default)).RunTrigger);
        Assert.True(await coordinator.RunAsync(lease, default));
        Assert.Null((await coordinator.AccountAsync(id, default)).RunTrigger);
        Assert.DoesNotContain(sink.Drafts, d => d.Type == "monitor.run-completed");
    }

    [PostgresFact]
    public async Task Venue_retry_budget_survives_scopes_and_blocks_refresh_manual_scheduled_and_command_claims()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var ids = await AccountsAsync(database, owner, 3);
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using (var seed = database.Context(owner))
        {
            foreach (var account in await seed.Accounts.ToListAsync())
            {
                var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"),
                    InstrumentSource.Venue, "{}", clock.Now.AddMinutes(-2));
                play.MarkPlanned(clock.Now.AddMinutes(-1));
                seed.Add(play);
            }
            await seed.SaveChangesAsync();
        }
        Guid pending;
        await using (var api = database.Context(owner))
            pending = (await Coordinator(api, owner, new Reader(), clock: clock).RequestAsync(ids[1], default)).RequestId;
        var deadline = clock.Now.AddSeconds(600);
        await using (var worker = database.Context(owner))
        {
            var coordinator = Coordinator(worker, owner, new Reader { Failure = new VenueRateLimitException(deadline) }, clock: clock);
            var lease = await coordinator.AdmitRefreshAsync(ids[0], "api", default);
            Assert.False(await coordinator.RunAsync(lease, default));
            var row = (await MonitorTestPaths.Store(worker, TestUnitOfWork.For(worker)).FindAsync(ids[0], default))!;
            // A refresh never schedules a check; the venue deadline still gates manual work.
            Assert.Null(row.NextRunAtUtc);
            Assert.Equal(deadline, row.NextManualAllowedAtUtc);
        }
        await using (var anotherProcess = database.Context(owner))
        {
            var store = MonitorTestPaths.Store(anotherProcess, TestUnitOfWork.For(anotherProcess));
            await store.DeferVenueAsync("hyperliquid", clock.Now.AddSeconds(120), clock.Now, default);
            Assert.Equal(deadline, await store.VenueRetryAtAsync("hyperliquid", default));
            Assert.Null(await store.VenueRetryAtAsync("risex", default));
            await Coordinator(anotherProcess, owner, new Reader(), clock: clock).DiscoverAsync(["hyperliquid"], clock.Now, default);
            Assert.Empty(await store.ClaimDueAsync("scheduler", clock.Now, 2, default));
            Assert.Null(await store.ClaimAsync(ids[1], pending, "command", clock.Now, default));
            Assert.Equal(deadline, (await Coordinator(anotherProcess, owner, new Reader(), clock: clock)
                .AccountAsync(ids[2], default)).NextManualAllowedAtUtc);
        }
        // Each denied API request gets its own scope, just as separate API instances do.
        await using (var api = database.Context(owner))
            Assert.Equal(600, (await Assert.ThrowsAsync<MonitorAdmissionException>(() =>
                Coordinator(api, owner, new Reader(), clock: clock).AdmitRefreshAsync(ids[2], "api", default))).RetryAfter);
        await using (var api = database.Context(owner))
            Assert.Equal(600, (await Assert.ThrowsAsync<MonitorAdmissionException>(() =>
                Coordinator(api, owner, new Reader(), clock: clock).RequestAsync(ids[2], default))).RetryAfter);
        var foreignOwner = Guid.NewGuid();
        var foreignId = (await AccountsAsync(database, foreignOwner, 1)).Single();
        await using (var foreign = database.Context(foreignOwner))
        {
            Assert.Equal(deadline, await MonitorTestPaths.Store(foreign, TestUnitOfWork.For(foreign)).VenueRetryAtAsync("hyperliquid", default));
            Assert.Equal(600, (await Assert.ThrowsAsync<MonitorAdmissionException>(() =>
                Coordinator(foreign, foreignOwner, new Reader(), clock: clock).AdmitRefreshAsync(foreignId, "foreign", default))).RetryAfter);
        }
        clock.Now = deadline;
        await using (var resumed = database.Context(owner))
        {
            var store = MonitorTestPaths.Store(resumed, TestUnitOfWork.For(resumed));
            Assert.Equal(pending, (await store.ClaimAsync(ids[1], pending, "command", clock.Now, default))!.RequestId);
            Assert.Single(await store.ClaimDueAsync("scheduler", clock.Now, 2, default));
        }
    }

    [PostgresFact]
    public async Task A_new_throttle_between_claim_and_read_answers_the_refresh_without_network_and_never_replays_it()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.UtcNow);
        await using var db = database.Context(owner);
        var coordinator = Coordinator(db, owner, new Reader { BeforeRead = _ => throw new Exception("Must not read.") }, clock: clock);
        var lease = await coordinator.AdmitRefreshAsync(id, "api", default);
        await using (var concurrent = database.Context(owner))
            await MonitorTestPaths.Store(concurrent, TestUnitOfWork.For(concurrent))
                .DeferVenueAsync("hyperliquid", clock.Now.AddSeconds(600), clock.Now, default);
        Assert.Equal(600, (await Assert.ThrowsAsync<MonitorAdmissionException>(() => coordinator.RunAsync(lease, default))).RetryAfter);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        // Review L1: the owner was answered with 429, so the refresh is released, not left pending for the worker.
        var released = (await store.FindAsync(id, default))!;
        Assert.Equal("idle", released.ManualState);
        Assert.Null(released.LeaseUntilUtc);
        Assert.Null(released.LastSuccessAtUtc);
        Assert.True(released.NextManualAllowedAtUtc > clock.Now.AddSeconds(599));
        clock.Now = clock.Now.AddSeconds(600);
        Assert.Empty(await store.ClaimDueAsync("scheduler", clock.Now, 2, default));
    }

    [PostgresFact]
    public async Task Optional_order_rate_limit_preserves_public_refresh_and_persists_venue_budget()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var ids = await AccountsAsync(database, owner, 2);
        var clock = new Clock(DateTimeOffset.UtcNow);
        await using (var db = database.Context(owner))
        {
            var coordinator = Coordinator(db, owner, new Reader(), clock: clock,
                orders: new Orders { Failure = new VenueRateLimitException(clock.Now.AddSeconds(600)) });
            var lease = await coordinator.AdmitRefreshAsync(ids[0], "api", default);
            Assert.True(await coordinator.RunAsync(lease, default));
            var account = await db.Accounts.SingleAsync(a => a.Id == ids[0]);
            Assert.Equal("synced", account.SyncStatus);
            Assert.Contains("rate limit", account.HistoryNotice);
            Assert.Single(await db.Snapshots.ToListAsync());
            Assert.Empty(await db.Orders.ToListAsync());
        }
        await using var api = database.Context(owner);
        Assert.Equal(600, (await Assert.ThrowsAsync<MonitorAdmissionException>(() =>
            Coordinator(api, owner, new Reader(), clock: clock).AdmitRefreshAsync(ids[1], "api", default))).RetryAfter);
    }

    [PostgresFact]
    public async Task A_rate_limit_is_retained_even_if_account_settings_changed_during_the_failed_read()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var ids = await AccountsAsync(database, owner, 2);
        var clock = new Clock(DateTimeOffset.UtcNow);
        await using (var db = database.Context(owner))
        {
            var reader = new Reader
            {
                BeforeRead = async _ =>
                {
                    await using var changed = database.Context(owner);
                    var account = await changed.Accounts.SingleAsync(a => a.Id == ids[0]);
                    account.UpdateSettings("Renamed during read", null, true);
                    await changed.SaveChangesAsync();
                },
                Failure = new VenueRateLimitException(clock.Now.AddSeconds(600))
            };
            var coordinator = Coordinator(db, owner, reader, clock: clock);
            Assert.False(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(ids[0], "api", default), default));
        }
        await using var api = database.Context(owner);
        Assert.Equal(600, (await Assert.ThrowsAsync<MonitorAdmissionException>(() =>
            Coordinator(api, owner, new Reader(), clock: clock).AdmitRefreshAsync(ids[1], "api", default))).RetryAfter);
    }

    [PostgresFact]
    public async Task Scheduled_claim_coalesces_an_accepted_manual_command_and_completion_rejects_replay()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        await using (var seeding = database.Context(owner))
        {
            var play = new Play(Guid.NewGuid(), await seeding.Accounts.SingleAsync(),
                new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, "{}", DateTimeOffset.UtcNow.AddMinutes(-1));
            play.MarkPlanned(DateTimeOffset.UtcNow.AddSeconds(-30));
            seeding.Add(play);
            await seeding.SaveChangesAsync();
        }
        MonitorRunAccepted accepted;
        await using (var api = database.Context(owner))
            accepted = await Coordinator(api, owner, new Reader()).RequestAsync(id, default);
        Assert.Equal("pending", accepted.Monitor.ManualState);
        await using (var worker = database.Context(owner))
        {
            var store = MonitorTestPaths.Store(worker, TestUnitOfWork.For(worker));
            var lease = Assert.Single(await store.ClaimDueAsync("scheduler", DateTimeOffset.UtcNow, 2, default));
            Assert.Equal(accepted.RequestId, lease.RequestId);
            Assert.Null(await store.ClaimAsync(id, accepted.RequestId, "duplicate-command", DateTimeOffset.UtcNow, default));
            Assert.True(await Coordinator(worker, owner, new Reader()).RunAsync(lease, default));
        }
        await using var replay = database.Context(owner);
        Assert.Null(await MonitorTestPaths.Store(replay, TestUnitOfWork.For(replay))
            .ClaimAsync(id, accepted.RequestId, "delayed-command", DateTimeOffset.UtcNow, default));
        await Assert.ThrowsAsync<MonitorAdmissionException>(() => Coordinator(replay, owner, new Reader()).RequestAsync(id, default));
    }

    [PostgresFact]
    public async Task Terminal_exit_followup_needs_fresh_complete_absence_and_reopens_after_link_revision()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var now = DateTimeOffset.UtcNow;
        await using var db = database.Context(owner);
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(),
            new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, "{}", now.AddMinutes(-10));
        play.MarkPlanned(now.AddMinutes(-9));
        var order = TestOrders.For(play, "stop-1", "sell", "open");
        var link = new PlayOrderLink(play, order, OrderLinkRole.Stop, "entry-1", "stop-1",
            OrderLinkState.Linked, OrderLinkSource.Owner, now.AddMinutes(-8));
        play.Cancel(CancelReason.ChangedMind, null, now);
        db.AddRange(play, order, link);
        await db.SaveChangesAsync();
        var work = TestUnitOfWork.For(db); var store = MonitorTestPaths.Store(db, work);
        Assert.True(await store.EligibleAsync(id, DateTimeOffset.UtcNow, default));
        async Task Observe(VenueOrderReadResult read) => await work.ExecuteAsync(async () =>
        {
            await store.UpdateWatchesAsync(id, read, DateTimeOffset.UtcNow, default);
            await work.SaveChangesAsync(default);
            return true;
        }, default);
        await Observe(new([], now.AddSeconds(1), "Incomplete", OpenOrdersComplete: false));
        Assert.True(await store.EligibleAsync(id, DateTimeOffset.UtcNow, default));
        await Observe(new([], now.AddSeconds(-1), "Earlier than cancellation", OpenOrdersComplete: true));
        Assert.True(await store.EligibleAsync(id, DateTimeOffset.UtcNow, default));
        await Observe(new([], now.AddSeconds(2), "Fresh absence", OpenOrdersComplete: true));
        Assert.False(await store.EligibleAsync(id, DateTimeOffset.UtcNow, default));
        link.Confirm(now.AddSeconds(3));
        await db.SaveChangesAsync();
        Assert.True(await store.EligibleAsync(id, DateTimeOffset.UtcNow, default));
        await Observe(new([new("stop-1", "BTC", "sell", "Stop", 90, 90, true, false, 1, 1,
            now.AddMinutes(-8), VenueOrderStatus.Open, "open", now.AddSeconds(4))],
            now.AddSeconds(4), "Still resting", OpenOrdersComplete: true));
        Assert.True(await store.EligibleAsync(id, DateTimeOffset.UtcNow, default));
        Assert.Null((await db.Set<MonitorExitWatch>().SingleAsync()).EndedAtUtc);
    }

    [PostgresFact]
    public async Task Refresh_retains_orders_without_reconciliation_and_preserves_pending_confirmation()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        await using var db = database.Context(owner);
        var now = DateTimeOffset.UtcNow;
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(),
            new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, "{}", now.AddMinutes(-1));
        play.MarkPlanned(now.AddSeconds(-30));
        db.Add(play);
        db.Add(new MonitorAccount { OwnerId = owner, AccountId = id,
            NextRunAtUtc = now.AddSeconds(80), ConfirmationAtUtc = now.AddSeconds(80) });
        await db.SaveChangesAsync();
        var importer = new Importer();
        var coordinator = Coordinator(db, owner, new Reader(), importer: importer);
        var lease = await coordinator.AdmitRefreshAsync(id, "refresh", default);
        Assert.True(await coordinator.RunAsync(lease, default));
        Assert.Equal(1, importer.Retained);
        Assert.Equal(0, importer.Reconciled);
        db.ChangeTracker.Clear();
        var row = await db.Set<MonitorAccount>().SingleAsync();
        Assert.NotNull(row.ConfirmationAtUtc);
        Assert.Equal(row.ConfirmationAtUtc, row.NextRunAtUtc);
        Assert.Equal(PlayStatus.Planned, (await db.Plays.SingleAsync()).Status);
    }

    [PostgresFact]
    public async Task Order_failure_does_not_discard_successful_public_refresh_or_leak_error_payloads()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        await using var db = database.Context(owner);
        var importer = new Importer();
        var coordinator = Coordinator(db, owner, new Reader(), importer: importer,
            orders: new Orders { Failure = new HttpRequestException("https://secret.invalid/?token=SECRET") });
        var lease = await coordinator.AdmitRefreshAsync(id, "refresh", default);
        Assert.True(await coordinator.RunAsync(lease, default));
        db.ChangeTracker.Clear();
        Assert.Single(await db.Snapshots.ToListAsync());
        var account = await db.Accounts.SingleAsync();
        Assert.NotNull(account.LastSyncedAtUtc);
        Assert.Contains("order coverage is unavailable", account.HistoryNotice);
        Assert.DoesNotContain("SECRET", account.HistoryNotice);
        Assert.Equal(0, importer.Reconciled);
        Assert.Equal(0, importer.Retained);
        var row = await db.Set<MonitorAccount>().SingleAsync();
        Assert.NotNull(row.NextManualAllowedAtUtc);
        // The refresh outcome is the account's notice; it never becomes the check state or error.
        Assert.Equal("idle", row.State);
        Assert.Null(row.Error);
    }

    [PostgresFact]
    public async Task Cancelling_optional_orders_preserves_committed_public_import_without_holding_a_network_lock()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        await using var db = database.Context(owner);
        using var cancelled = new CancellationTokenSource();
        var orders = new Orders
        {
            BeforeRead = async ct =>
            {
                Assert.Null(db.Database.CurrentTransaction);
                await using var observed = database.Context(owner);
                Assert.Single(await observed.Snapshots.ToListAsync(ct));
                Assert.Equal("synced", (await observed.Accounts.SingleAsync(ct)).SyncStatus);
                cancelled.Cancel();
                ct.ThrowIfCancellationRequested();
            }
        };
        var coordinator = Coordinator(db, owner, new Reader(), orders: orders);
        var lease = await coordinator.AdmitRefreshAsync(id, "refresh", cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(lease, cancelled.Token));
        db.ChangeTracker.Clear();
        Assert.Single(await db.Snapshots.ToListAsync());
        Assert.Equal("synced", (await db.Accounts.SingleAsync()).SyncStatus);
        Assert.Empty(await db.Orders.ToListAsync());
        var row = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal("idle", row.ManualState);
        Assert.NotNull(row.NextManualAllowedAtUtc);
    }

    [PostgresFact]
    public async Task Refresh_admission_is_shared_across_scopes_and_each_owners_read_budget_is_two()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var ids = await AccountsAsync(database, owner, 3);
        await using var first = database.Context(owner);
        var a = Coordinator(first, owner, new Reader());
        var lease = await a.AdmitRefreshAsync(ids[0], "first", default);
        await using var second = database.Context(owner);
        var b = Coordinator(second, owner, new Reader());
        Assert.True((await Assert.ThrowsAsync<MonitorAdmissionException>(() => b.AdmitRefreshAsync(ids[0], "other-tab", default))).RetryAfter > 0);
        _ = await b.AdmitRefreshAsync(ids[1], "second", default);
        await using var third = database.Context(owner);
        Assert.Equal(30, (await Assert.ThrowsAsync<MonitorAdmissionException>(() =>
            Coordinator(third, owner, new Reader()).AdmitRefreshAsync(ids[2], "third", default))).RetryAfter);
        Assert.False(await MonitorTestPaths.Store(third, TestUnitOfWork.For(third))
            .RenewAsync(lease with { Generation = lease.Generation - 1 }, DateTimeOffset.UtcNow, default));
        var otherOwner = Guid.NewGuid();
        var otherId = (await AccountsAsync(database, otherOwner, 1)).Single();
        await using var other = database.Context(otherOwner);
        Assert.Equal(otherId, (await Coordinator(other, otherOwner, new Reader()).AdmitRefreshAsync(otherId, "other-owner", default)).AccountId);
    }

    [PostgresFact]
    public async Task Baseline_commits_before_network_and_settings_change_discards_the_observation_without_blocking_update()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        await using var db = database.Context(owner);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new EventSink();
        var reader = new Reader
        {
            BeforeRead = async ct =>
            {
                Assert.True(sink.Baseline);
                Assert.Null(db.Database.CurrentTransaction);
                entered.SetResult();
                await gate.Task.WaitAsync(ct);
            }
        };
        var monitor = Coordinator(db, owner, reader, sink);
        var lease = await monitor.AdmitRefreshAsync(id, "worker", default);
        var run = monitor.RunAsync(lease, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var updating = database.Context(owner))
        {
            var work = TestUnitOfWork.For(updating);
            await work.ExecuteAsync(() => new WorkspaceStore(updating, work).WithAccountLockAsync(id, async account =>
            {
                account.UpdateSettings("Disabled during read", null, false);
                await work.SaveChangesAsync(default);
                return true;
            }, default), default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        gate.SetResult();
        Assert.False(await run);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Snapshots.ToListAsync());
        var row = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal("idle", row.State);
        Assert.Equal("idle", row.ManualState);
        Assert.NotNull(row.NextManualAllowedAtUtc);
    }

    [PostgresFact]
    public async Task Failed_refresh_records_a_safe_account_failure_and_cooldown_without_changing_check_state()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        await using (var db = database.Context(owner))
        {
            var monitor = Coordinator(db, owner, new Reader { Failure = new HttpRequestException("ro:SECRET?auth=TOKEN") });
            var lease = await monitor.AdmitRefreshAsync(id, "worker", default);
            Assert.False(await monitor.RunAsync(lease, default));
        }
        await using var next = database.Context(owner);
        var state = await Coordinator(next, owner, new Reader()).AccountAsync(id, default);
        Assert.Equal("idle", state.State);
        Assert.Null(state.Error);
        Assert.Null(state.LastSuccessAtUtc);
        Assert.Null(state.LastAttemptAtUtc);
        var account = await next.Accounts.SingleAsync();
        Assert.Equal("error", account.SyncStatus);
        Assert.DoesNotContain("SECRET", account.LastSyncError ?? "");
        Assert.DoesNotContain("TOKEN", account.LastSyncError ?? "");
        var refused = await Assert.ThrowsAsync<MonitorAdmissionException>(() =>
            Coordinator(next, owner, new Reader()).AdmitRefreshAsync(id, "next-tab", default));
        Assert.InRange(refused.RetryAfter, 1, 60);
    }

    [PostgresFact]
    public async Task Expired_lease_recovery_fences_a_late_network_result()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { BeforeRead = async ct => { entered.SetResult(); await gate.Task.WaitAsync(ct); } };
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var monitor = Coordinator(db, owner, reader, clock: clock);
        var accepted = await monitor.RequestAsync(id, default);
        var original = (await MonitorTestPaths.Store(db, TestUnitOfWork.For(db)).ClaimAsync(id, accepted.RequestId, "lost-worker", clock.Now, default))!;
        var running = monitor.RunAsync(original, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now += MonitorOptions.Lease;
        await using var recovered = database.Context(owner);
        var next = await MonitorTestPaths.Store(recovered, TestUnitOfWork.For(recovered))
            .ClaimAsync(id, original.RequestId, "new-worker", clock.Now, default);
        Assert.NotNull(next);
        Assert.Equal(original.Generation + 1, next.Generation);
        gate.SetResult();
        Assert.False(await running);
        Assert.Empty(await recovered.Snapshots.ToListAsync());
        recovered.ChangeTracker.Clear();
        Assert.Equal("new-worker", (await recovered.Set<MonitorAccount>().SingleAsync()).LeaseHolder);
    }

    [PostgresFact]
    public async Task Heartbeat_is_server_wide_but_account_monitor_rows_stay_owner_scoped()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        await using (var db = database.Context(owner))
        {
            _ = await Coordinator(db, owner, new Reader()).AdmitRefreshAsync(id, "worker", default);
            await MonitorServerState.For(db).RecordHeartbeatAsync("monitor", DateTimeOffset.UtcNow, default);
        }
        await using var foreign = database.Context(Guid.NewGuid());
        Assert.Empty(await foreign.Set<MonitorAccount>().ToListAsync());
        Assert.NotNull(await MonitorTestPaths.Store(foreign, TestUnitOfWork.For(foreign)).HeartbeatAsync(default));
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() =>
            Coordinator(foreign, foreign.CurrentOwnerId, new Reader()).AccountAsync(id, default))).StatusCode);
        var ownId = (await AccountsAsync(database, foreign.CurrentOwnerId, 1)).Single();
        _ = await Coordinator(foreign, foreign.CurrentOwnerId, new Reader()).AdmitRefreshAsync(ownId, "other-worker", default);
        var ownStore = MonitorTestPaths.Store(foreign, TestUnitOfWork.For(foreign));
        await MonitorServerState.For(foreign).RecordHeartbeatAsync("monitor", DateTimeOffset.UtcNow, default);
        Assert.Equal(ownId, (await foreign.Set<MonitorAccount>().SingleAsync()).AccountId);
        Assert.Equal("monitor", (await ownStore.HeartbeatAsync(default))!.Deployment);
        await using var original = database.Context(owner);
        Assert.Equal(id, (await original.Set<MonitorAccount>().SingleAsync()).AccountId);
        Assert.Equal("monitor", (await MonitorTestPaths.Store(original, TestUnitOfWork.For(original)).HeartbeatAsync(default))!.Deployment);
    }

    [PostgresFact]
    public async Task Review_H1_terminal_exit_resolves_from_explicit_cancel_even_when_open_book_is_incomplete()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var now = DateTimeOffset.UtcNow;
        await using var db = database.Context(owner);
        var (play, _) = await SeedCancelledWithStopAsync(db, now);
        var work = TestUnitOfWork.For(db); var store = MonitorTestPaths.Store(db, work);
        // RISEx always reports OpenOrdersComplete:false; the scheduled check sees the stop explicitly cancelled.
        await work.ExecuteAsync(async () =>
        {
            await store.UpdateWatchesAsync(id, new([Stop(VenueOrderStatus.Canceled, now.AddSeconds(1))],
                now.AddSeconds(1), "Bounded history", OpenOrdersComplete: false), now.AddSeconds(1), default);
            return true;
        }, default);
        Assert.False(await store.EligibleAsync(id, now.AddSeconds(1), default));
        var watch = Assert.Single(await store.WatchesAsync(play.Id, now.AddSeconds(1), default));
        Assert.Equal("resolved", watch.State);
    }

    [PostgresFact]
    public async Task Review_H1_absence_on_an_incomplete_read_never_resolves_and_the_watch_ends_unverifiable()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var now = DateTimeOffset.UtcNow;
        await using var db = database.Context(owner);
        var (play, _) = await SeedCancelledWithStopAsync(db, now);
        var options = new MonitorOptions { WatchLifetime = TimeSpan.FromHours(2) };
        var work = TestUnitOfWork.For(db); var store = MonitorTestPaths.Store(db, work, options);
        async Task Observe(VenueOrderReadResult? read, DateTimeOffset at) => await work.ExecuteAsync(async () =>
        {
            await store.UpdateWatchesAsync(id, read, at, default);
            return true;
        }, default);
        await Observe(new([], now.AddMinutes(1), "Incomplete", OpenOrdersComplete: false), now.AddMinutes(1));
        Assert.True(await store.EligibleAsync(id, now.AddMinutes(1), default));
        Assert.Equal("watching", Assert.Single(await store.WatchesAsync(play.Id, now.AddMinutes(1), default)).State);
        // Past its lifetime without proof, even before a run records it, it is never shown as resolved.
        var later = now.AddHours(3);
        Assert.False(await store.EligibleAsync(id, later, default));
        Assert.Equal("unverifiable", Assert.Single(await store.WatchesAsync(play.Id, later, default)).State);
        await Observe(null, later);
        var recorded = await db.Set<MonitorExitWatch>().AsNoTracking().SingleAsync();
        Assert.Equal(MonitorWatchOutcomes.Unverifiable, recorded.Outcome);
        // Persistent: a later incomplete absence does not turn it into "gone".
        await Observe(new([], later.AddMinutes(1), "Incomplete", OpenOrdersComplete: false), later.AddMinutes(1));
        Assert.Equal("unverifiable", Assert.Single(await store.WatchesAsync(play.Id, later.AddMinutes(1), default)).State);
    }

    [PostgresFact]
    public async Task Review_L1_throttled_refresh_is_not_replayed_by_the_worker_after_the_api_returned_429()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var coordinator = Coordinator(db, owner, new Reader(), clock: clock);
        var lease = await coordinator.AdmitRefreshAsync(id, "api", default);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        await store.DeferVenueAsync("hyperliquid", clock.Now.AddSeconds(120), clock.Now, default);
        await Assert.ThrowsAsync<MonitorAdmissionException>(() => coordinator.RunAsync(lease, default));
        db.ChangeTracker.Clear();
        var claimed = await store.ClaimDueAsync("scheduler", clock.Now.AddSeconds(121), 2, default);
        Assert.DoesNotContain(claimed, l => l.Trigger == "refresh");
    }

    [PostgresFact]
    public async Task Review_L1_a_refresh_whose_api_died_is_abandoned_not_replayed_after_lease_expiry()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        _ = await Coordinator(db, owner, new Reader(), clock: clock).AdmitRefreshAsync(id, "api-crashed", default);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        clock.Now += MonitorOptions.Lease + TimeSpan.FromSeconds(1);
        db.ChangeTracker.Clear();
        // Nothing was scheduled for checks, so the abandoned refresh yields no run at all.
        Assert.Empty(await store.ClaimDueAsync("scheduler", clock.Now, 2, default));
        var row = (await store.FindAsync(id, default))!;
        Assert.Equal("idle", row.ManualState);
        Assert.Null(row.LeaseHolder);
        // And it does not block the owner's next request either.
        Assert.Equal("pending", (await Coordinator(db, owner, new Reader(), clock: clock).RequestAsync(id, default)).Monitor.ManualState);
    }

    [PostgresFact]
    public async Task Review_L2_refresh_does_not_reset_check_backoff_or_scheduling()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using var db = database.Context(owner);
        db.Add(Row(owner, id, "stale", now));
        await db.SaveChangesAsync();
        var coordinator = Coordinator(db, owner, new Reader(), clock: new Clock(now));
        Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(id, "api", default), default));
        db.ChangeTracker.Clear();
        AssertUnchanged(await db.Set<MonitorAccount>().SingleAsync(), now);
    }

    [PostgresFact]
    public async Task Review_L2_failed_refresh_does_not_postpone_a_due_check()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using var db = database.Context(owner);
        db.Add(Row(owner, id, "stale", now));
        await db.SaveChangesAsync();
        var coordinator = Coordinator(db, owner, new Reader { Failure = new HttpRequestException("down") }, clock: new Clock(now));
        Assert.False(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(id, "api", default), default));
        db.ChangeTracker.Clear();
        AssertUnchanged(await db.Set<MonitorAccount>().SingleAsync(), now);
    }

    private static MonitorAccount Row(Guid owner, Guid id, string state, DateTimeOffset now)
    {
        var row = new MonitorAccount { OwnerId = owner, AccountId = id, ConsecutiveFailures = 3,
            NextRunAtUtc = now.AddSeconds(30), LastSuccessAtUtc = now.AddHours(-1), LastAttemptAtUtc = now.AddMinutes(-3),
            Error = "The venue check failed. Monitoring will retry." };
        row.SetState(state);
        return row;
    }

    private static void AssertUnchanged(MonitorAccount row, DateTimeOffset now)
    {
        Assert.Equal("stale", row.State);
        Assert.Equal(1, row.StateRevision);
        Assert.Equal(3, row.ConsecutiveFailures);
        Assert.Equal(now.AddSeconds(30), row.NextRunAtUtc);
        Assert.Equal(now.AddHours(-1), row.LastSuccessAtUtc);
        Assert.Equal(now.AddMinutes(-3), row.LastAttemptAtUtc);
        Assert.Equal("The venue check failed. Monitoring will retry.", row.Error);
        Assert.Equal("idle", row.ManualState);
        Assert.Equal(now.AddSeconds(60), row.NextManualAllowedAtUtc);
    }

    [PostgresFact]
    public async Task Review_L3_a_check_superseded_by_changed_settings_is_not_a_failure()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var reader = new Reader
        {
            BeforeRead = async _ =>
            {
                await using var changed = database.Context(owner);
                (await changed.Accounts.SingleAsync()).UpdateSettings("Renamed during read", null, true);
                await changed.SaveChangesAsync();
            }
        };
        var coordinator = Coordinator(db, owner, reader, clock: clock);
        var accepted = await coordinator.RequestAsync(id, default);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        var lease = (await store.ClaimAsync(id, accepted.RequestId, "worker", clock.Now, default))!;
        Assert.False(await coordinator.RunAsync(lease, default));
        db.ChangeTracker.Clear();
        var row = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal(0, row.ConsecutiveFailures);
        Assert.Equal("idle", row.State);
        Assert.Null(row.Error);
        Assert.Equal(clock.Now + MonitorOptions.FirstRetry, row.NextRunAtUtc);
    }

    [PostgresFact]
    public async Task Review_L4_the_manual_cooldown_does_not_gate_a_scheduled_claim()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, now);
        var row = Row(owner, id, "live", now);
        row.NextRunAtUtc = now;
        row.NextManualAllowedAtUtc = now.AddSeconds(45);
        db.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var lease = Assert.Single(await MonitorTestPaths.Store(db, TestUnitOfWork.For(db)).ClaimDueAsync("scheduler", now, 2, default));
        Assert.Equal("monitor", lease.Trigger);
    }

    [PostgresFact]
    public async Task Review_b_repeated_state_cycles_emit_distinct_events_and_an_unchanged_state_emits_none()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var sink = new EventSink();
        var failing = true;
        var reader = new Reader { BeforeRead = _ => failing ? throw new HttpRequestException("down") : Task.CompletedTask };
        var coordinator = Coordinator(db, owner, reader, sink, clock);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        async Task CheckAsync(bool fail)
        {
            failing = fail;
            var accepted = await coordinator.RequestAsync(id, default);
            var lease = (await store.ClaimAsync(id, accepted.RequestId, "worker", clock.Now, default))!;
            await coordinator.RunAsync(lease, default);
            clock.Now = clock.Now.AddMinutes(2);
            db.ChangeTracker.Clear();
        }
        await CheckAsync(true);   // idle → degraded
        await CheckAsync(false);  // degraded → live
        await CheckAsync(true);   // live → degraded
        await CheckAsync(true);   // degraded, unchanged: no event
        await CheckAsync(false);  // degraded → live
        await CheckAsync(false);  // live, unchanged: no event
        var keys = sink.Drafts.Where(d => d.Type == "monitor.state-changed").Select(d => d.DedupKey).ToArray();
        Assert.Equal([$"monitor:{id}:1:degraded", $"monitor:{id}:2:live", $"monitor:{id}:3:degraded", $"monitor:{id}:4:live"], keys);
        Assert.Equal(4, (await store.FindAsync(id, default))!.StateRevision);
    }

    [PostgresFact]
    public async Task Review_b_and_L5_discovery_idles_a_disabled_account_once_and_locks_nothing_unchanged()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var sink = new EventSink();
        var coordinator = Coordinator(db, owner, new Reader(), sink, clock);
        Assert.Equal(1, await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default));
        Assert.Equal(0, await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default));
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        await coordinator.RunAsync(Assert.Single(await store.ClaimDueAsync("scheduler", clock.Now, 2, default)), default);
        Assert.Equal("live", (await store.FindAsync(id, default))!.State);
        db.ChangeTracker.Clear();
        (await db.Accounts.SingleAsync()).UpdateSettings("Disabled", null, false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(1, await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default));
        Assert.Equal(0, await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default));
        var row = (await store.FindAsync(id, default))!;
        Assert.Equal("idle", row.State);
        Assert.Null(row.NextRunAtUtc);
        Assert.Equal($"monitor:{id}:2:idle", sink.Drafts.Last(d => d.Type == "monitor.state-changed").DedupKey);
        // Summary reads only enabled, eligible accounts.
        Assert.Empty((await coordinator.SummaryAsync(default)).Accounts);
    }

    [PostgresFact]
    public async Task Review2_a_cancelled_play_whose_only_linked_order_is_a_resting_entry_stays_watched()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var now = DateTimeOffset.UtcNow;
        await using var db = database.Context(owner);
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(),
            new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, "{}", now.AddMinutes(-10));
        play.MarkPlanned(now.AddMinutes(-9));
        var order = TestOrders.For(play, "entry-order", "buy", "open");
        var link = new PlayOrderLink(play, order, OrderLinkRole.Entry, "entry-1", null,
            OrderLinkState.Linked, OrderLinkSource.Owner, now.AddMinutes(-8));
        play.Cancel(CancelReason.ChangedMind, null, now);
        db.AddRange(play, order, link);
        await db.SaveChangesAsync();
        var work = TestUnitOfWork.For(db); var store = MonitorTestPaths.Store(db, work);
        // The cancelled Play has no active Plays beside it: only the resting entry keeps the account monitored.
        Assert.True(await store.EligibleAsync(id, now.AddSeconds(1), default));
        VenueOrder Entry(VenueOrderStatus status, DateTimeOffset at) => new("entry-order", "BTC", "buy", "Limit", 100, null,
            false, false, 1, status == VenueOrderStatus.Open ? 1 : 0, now.AddMinutes(-8), status, status.ToString().ToLowerInvariant(), at);
        async Task Observe(VenueOrderReadResult read, DateTimeOffset at) => await work.ExecuteAsync(async () =>
        {
            await store.UpdateWatchesAsync(id, read, at, default);
            return true;
        }, default);
        await Observe(new([Entry(VenueOrderStatus.Open, now.AddSeconds(2))], now.AddSeconds(2), "Still resting", OpenOrdersComplete: true), now.AddSeconds(2));
        var watching = Assert.Single(await store.WatchesAsync(play.Id, now.AddSeconds(2), default));
        Assert.Equal(("entry", "watching"), (watching.Role, watching.State));
        Assert.True(await store.EligibleAsync(id, now.AddSeconds(2), default));
        // Absence on an incomplete read proves nothing; a fresh cancellation of that order ends the watch.
        await Observe(new([], now.AddSeconds(3), "Incomplete", OpenOrdersComplete: false), now.AddSeconds(3));
        Assert.True(await store.EligibleAsync(id, now.AddSeconds(3), default));
        await Observe(new([Entry(VenueOrderStatus.Canceled, now.AddSeconds(4))], now.AddSeconds(4), "Cancelled", OpenOrdersComplete: false), now.AddSeconds(4));
        Assert.Equal("resolved", Assert.Single(await store.WatchesAsync(play.Id, now.AddSeconds(4), default)).State);
        Assert.False(await store.EligibleAsync(id, now.AddSeconds(4), default));
    }

    [PostgresFact]
    public async Task Review2_a_run_with_known_omitted_executions_is_degraded_not_live()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        // What LighterReader reports for a bounded, untruncated read that left out a trade without realized PnL.
        var reader = new Reader { Shape = read => read with { FillHistoryComplete = false, FillHistoryTruncated = false,
            OldestRawFillAtUtc = clock.Now.AddMinutes(-1), OmittedExecutions = 1 } };
        var coordinator = Coordinator(db, owner, reader, clock: clock);
        var accepted = await coordinator.RequestAsync(id, default);
        var lease = (await MonitorTestPaths.Store(db, TestUnitOfWork.For(db)).ClaimAsync(id, accepted.RequestId, "worker", clock.Now, default))!;
        Assert.True(await coordinator.RunAsync(lease, default));
        db.ChangeTracker.Clear();
        var row = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal("degraded", row.State);
        Assert.Equal(clock.Now, row.LastSuccessAtUtc);
        Assert.Contains("could not verify", row.Error);
    }

    [PostgresFact]
    public async Task Review2_discovery_reaches_eligible_accounts_beyond_the_first_batch()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var ids = await AccountsAsync(database, owner, 5);
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using var db = database.Context(owner);
        // Only the account that sorts last (as PostgreSQL orders uuids) has a Play.
        var last = (await db.Accounts.OrderBy(a => a.Id).Select(a => a.Id).ToListAsync()).Last();
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(a => a.Id == last), new PerpetualInstrument("hyperliquid", "BTC"),
            InstrumentSource.Venue, "{}", now.AddMinutes(-2));
        play.MarkPlanned(now.AddMinutes(-1));
        db.Add(play);
        await db.SaveChangesAsync();
        var coordinator = Coordinator(db, owner, new Reader(), clock: new Clock(now));
        Assert.Equal(1, await coordinator.DiscoverAsync(["hyperliquid"], now, default, batch: 2));
        db.ChangeTracker.Clear();
        var row = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal(last, row.AccountId);
        Assert.Equal(now, row.NextRunAtUtc);
    }

    [PostgresFact]
    public async Task Review2_venue_failures_are_logged_by_ids_and_exception_type_only()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, DateTimeOffset.UtcNow);
        var logs = new CapturingLogger();
        var coordinator = Coordinator(db, owner,
            new Reader { Failure = new HttpRequestException("https://venue.invalid/?token=ro:SENTINEL-SECRET body") },
            log: new MonitorRunLog(logs));
        var accepted = await coordinator.RequestAsync(id, default);
        var lease = (await MonitorTestPaths.Store(db, TestUnitOfWork.For(db)).ClaimAsync(id, accepted.RequestId, "worker", DateTimeOffset.UtcNow, default))!;
        Assert.False(await coordinator.RunAsync(lease, default));
        var message = Assert.Single(logs.Messages);
        Assert.Contains(nameof(HttpRequestException), message);
        Assert.Contains(lease.RequestId.ToString(), message);
        Assert.Contains(id.ToString(), message);
        Assert.DoesNotContain("SENTINEL", message);
        Assert.DoesNotContain("venue.invalid", message);
        Assert.Null(logs.Exception);
    }

    [PostgresFact]
    public async Task Final_an_unclaimed_request_on_a_disabled_account_is_settled_not_stranded()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var coordinator = Coordinator(db, owner, new Reader(), clock: clock);
        Assert.Equal("pending", (await coordinator.RequestAsync(id, default)).Monitor.ManualState);
        await DisableAsync(db);
        await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default);
        db.ChangeTracker.Clear();
        var row = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal(("idle", "idle"), (row.ManualState, row.State));
        Assert.Null(row.NextRunAtUtc);
        Assert.Null(row.LeaseHolder);
        // The settlement starts the manual cooldown: an immediately restored account is still cooling down.
        Assert.Equal(clock.Now + new MonitorOptions().ManualCooldown, row.NextManualAllowedAtUtc);
        db.ChangeTracker.Clear();
        (await db.Accounts.SingleAsync()).UpdateSettings("Restored", null, true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var refused = await Assert.ThrowsAsync<MonitorAdmissionException>(() => coordinator.RequestAsync(id, default));
        Assert.InRange(refused.RetryAfter, 1, (int)new MonitorOptions().ManualCooldown.TotalSeconds);
    }

    [PostgresFact]
    public async Task Final_an_expired_running_lease_on_a_disabled_account_is_settled_and_a_live_one_keeps_its_fence()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var coordinator = Coordinator(db, owner, new Reader(), clock: clock);
        var accepted = await coordinator.RequestAsync(id, default);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        var lease = (await store.ClaimAsync(id, accepted.RequestId, "dead-worker", clock.Now, default))!;
        await DisableAsync(db);
        // The holder may still be alive: its lease fences the row, and its own run settles it.
        await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default);
        db.ChangeTracker.Clear();
        Assert.Equal("running", (await db.Set<MonitorAccount>().SingleAsync()).ManualState);
        clock.Now += MonitorOptions.Lease + TimeSpan.FromSeconds(1);
        await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default);
        db.ChangeTracker.Clear();
        var row = await db.Set<MonitorAccount>().SingleAsync();
        Assert.Equal(("idle", "idle"), (row.ManualState, row.State));
        Assert.Null(row.LeaseHolder);
        Assert.False(row.Owns(lease.Generation, lease.WorkerId, clock.Now));
    }

    [PostgresFact]
    public async Task Final_a_settled_owner_check_on_a_disabled_account_completes_as_superseded_once()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var sink = new EventSink();
        var coordinator = Coordinator(db, owner, new Reader(), sink, clock);
        var accepted = await coordinator.RequestAsync(id, default);
        await DisableAsync(db);
        await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default);
        var completed = Assert.Single(sink.Drafts, d => d.Type == "monitor.run-completed");
        Assert.Equal((accepted.RequestId, $"monitor-run:{id}:{accepted.RequestId}"), (completed.RunId, completed.DedupKey));
        Assert.Equal("""{"trigger":"manual","outcome":"superseded","state":"idle"}""", completed.Data.GetRawText());
        // A later pass finds nothing left to settle.
        await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default);
        Assert.Single(sink.Drafts, d => d.Type == "monitor.run-completed");
    }

    private static async Task DisableAsync(VesselDbContext db)
    {
        db.ChangeTracker.Clear();
        (await db.Accounts.SingleAsync()).UpdateSettings("Disabled", null, false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [PostgresFact]
    public async Task Final_a_historical_open_row_is_not_resting_evidence_but_a_current_book_open_is()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var now = DateTimeOffset.UtcNow;
        await using var db = database.Context(owner);
        var (play, _) = await SeedCancelledWithStopAsync(db, now);
        var work = TestUnitOfWork.For(db); var store = MonitorTestPaths.Store(db, work);
        async Task<string> Observe(DateTimeOffset statusAt, DateTimeOffset observed, bool complete)
        {
            await work.ExecuteAsync(async () =>
            {
                await store.UpdateWatchesAsync(id, new([Stop(VenueOrderStatus.Open, statusAt)], observed, "History and book",
                    OpenOrdersComplete: complete), observed, default);
                return true;
            }, default);
            return Assert.Single(await store.WatchesAsync(play.Id, observed, default)).State;
        }
        // A history row last seen open before the cancellation, with the current book incomplete: still watching.
        Assert.Equal("watching", await Observe(now.AddMinutes(-1), now.AddSeconds(1), complete: false));
        // The current book lists it: watching.
        Assert.Equal("watching", await Observe(now.AddSeconds(2), now.AddSeconds(2), complete: true));
        // Only the historical row remains and the complete current book does not list the order: resolved.
        Assert.Equal("resolved", await Observe(now.AddMinutes(-1), now.AddSeconds(3), complete: true));
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<MonitorRunLog>
    {
        public List<string> Messages { get; } = [];
        public Exception? Exception { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception) + " " + string.Join(" ", (state as IEnumerable<KeyValuePair<string, object?>>)?
                .Select(p => p.Value?.ToString()) ?? []));
            Exception ??= exception;
        }
    }

    private static async Task<(Play Play, PlayOrderLink Link)> SeedCancelledWithStopAsync(VesselDbContext db, DateTimeOffset now)
    {
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(),
            new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, "{}", now.AddMinutes(-10));
        play.MarkPlanned(now.AddMinutes(-9));
        var order = TestOrders.For(play, "stop-1", "sell", "open");
        var link = new PlayOrderLink(play, order, OrderLinkRole.Stop, "entry-1", "stop-1",
            OrderLinkState.Linked, OrderLinkSource.Owner, now.AddMinutes(-8));
        play.Cancel(CancelReason.ChangedMind, null, now);
        db.AddRange(play, order, link);
        await db.SaveChangesAsync();
        return (play, link);
    }

    private static VenueOrder Stop(VenueOrderStatus status, DateTimeOffset observed) => new("stop-1", "BTC", "sell", "Stop", 90, 90,
        true, false, 1, status == VenueOrderStatus.Open ? 1 : 0, observed.AddMinutes(-8), status, status.ToString().ToLowerInvariant(), observed);

    private static async Task<Play> SeedPlannedAsync(VesselDbContext db, DateTimeOffset now)
    {
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(), new PerpetualInstrument("hyperliquid", "BTC"),
            InstrumentSource.Venue, "{}", now.AddMinutes(-2));
        play.MarkPlanned(now.AddMinutes(-1));
        db.Add(play);
        await db.SaveChangesAsync();
        return play;
    }

    private static async Task<Guid[]> AccountsAsync(CoreDatabase database, Guid owner, int count)
    {
        await using var db = database.Context(owner);
        var accounts = Enumerable.Range(0, count).Select(i =>
        {
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Monitor " + i);
            account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
            return account;
        }).ToArray();
        db.Accounts.AddRange(accounts);
        await db.SaveChangesAsync();
        return accounts.Select(a => a.Id).ToArray();
    }

    private static MonitorCoordinator Coordinator(VesselDbContext db, Guid owner, Reader reader, EventSink? sink = null,
        TimeProvider? clock = null, Importer? importer = null, Orders? orders = null, MonitorOptions? options = null,
        IMonitorRunLog? log = null)
    {
        var work = TestUnitOfWork.For(db);
        options ??= new MonitorOptions();
        return new(new MonitorWorkerOnline(MonitorTestPaths.Store(db, work, options), clock ?? TimeProvider.System), new WorkspaceStore(db, work), importer ?? new Importer(), TestVenues.With(reader, orders ?? new Orders()),
            new Credentials(), work, sink ?? new EventSink(), new Commands(), new CoreOwner(owner),
            options, clock ?? TimeProvider.System, log: log);
    }

    private sealed class Reader : IPerpetualVenueReader
    {
        public string VenueId => "hyperliquid";
        public Func<CancellationToken, Task>? BeforeRead { get; init; }
        public Exception? Failure { get; init; }
        /// <summary>Replaces the complete synthetic read, e.g. with the coverage an adapter reports.</summary>
        public Func<PerpetualVenueReadResult, PerpetualVenueReadResult>? Shape { get; init; }
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueInstrument>>([]);
        public async Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct)
        {
            if (BeforeRead is { } before) await before(ct);
            if (Failure is { } failure) throw failure;
            var read = new PerpetualVenueReadResult(new(DateTimeOffset.UtcNow, "synthetic", 100, 100, 0, []), [], [], "Synthetic isolated read",
                PositionsComplete: true, FillHistoryComplete: true);
            return Shape is null ? read : Shape(read);
        }
    }
    private sealed class Importer : IMonitorImporter
    {
        public int Retained;
        public int Reconciled;
        public Task RetainAsync(Account account, VenueOrderReadResult orders, CancellationToken ct) { Retained++; return Task.CompletedTask; }
        public Task ReconcileAsync(Account account, VenueOrderReadResult orders, IReadOnlyList<VenueInstrument> instruments, CancellationToken ct)
            { Reconciled++; return Task.CompletedTask; }
    }
    private sealed class Orders : IVenueOrderReader
    {
        public string VenueId => "hyperliquid";
        public Exception? Failure { get; init; }
        public Func<CancellationToken, Task>? BeforeRead { get; init; }
        public async Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct)
        {
            if (BeforeRead is { } before) await before(ct);
            if (Failure is { } failure) throw failure;
            return new([], DateTimeOffset.UtcNow, "Synthetic isolated orders", OpenOrdersComplete: true);
        }
    }
    private sealed class Credentials : IAccountCredentialReader
    {
        public Task<string?> ReadAsync(Guid accountId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken ct) => Task.FromResult<CredentialAttempt?>(null);
        public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Commands : IMonitorCommandSink
    {
        public Task EnqueueAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class EventSink : IVesselEventSink
    {
        public bool Baseline;
        public List<string> RunTriggers { get; } = [];
        public Task SetRunAsync(Guid runId, string trigger, CancellationToken ct) { RunTriggers.Add(trigger); return Task.CompletedTask; }
        public Task EnsureMonitoringBaselineAsync(Guid accountId, CancellationToken ct) { Baseline = true; return Task.CompletedTask; }
        public List<VesselEventDraft> Drafts { get; } = [];
        public Task AppendAsync(VesselEventDraft draft, CancellationToken ct) { Drafts.Add(draft); return Task.CompletedTask; }
        public int HistoricalFills;
        public Task ClaimHistoricalFillsAsync(IReadOnlyCollection<Vessel.Domain.Workspace.ImportedFill> fills, CancellationToken ct)
        { HistoricalFills += fills.Count; return Task.CompletedTask; }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
