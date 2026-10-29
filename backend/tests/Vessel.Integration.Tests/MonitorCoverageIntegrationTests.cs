using Microsoft.EntityFrameworkCore;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Infrastructure.Venues.Risex;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class MonitorCoveragePostgresTests
{
    /// <summary>
    /// D-09: RISEx reads order history only, so every check used to end "degraded" and a cancelled or missing stop could
    /// never be flagged. The capability, not the venue name, now labels coverage unsupported and leaves the run live.
    /// The control keeps the old rule where the venue does read the current book: an incomplete book still degrades.
    /// </summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_check_without_a_current_book_is_not_degraded_by_it_and_records_unsupported_coverage(bool currentBook)
    {
        await using var database = await DiscrepancyDatabase.Create();
        // History reads only: open rows never carry the read time and the book is never complete; the stop was cancelled.
        var f = new DiscrepancyFixture { OrdersComplete = false, StampOpenOrders = false };
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        await database.Seed(f);
        await using var scope = database.Scope(f);
        f.Stop(o => o with { Status = VenueOrderStatus.Canceled, VenueStatus = "ORDER_STATUS_CANCELLED" });
        // The descriptor keeps the account's venue ID: only the capability differs, so the name decides nothing.
        var capabilities = currentBook ? HyperliquidPerpetualReader.Descriptor.Capabilities : RisexReader.Descriptor.Capabilities;
        var (store, coordinator) = Create(scope, f, capabilities);

        await CheckAsync(coordinator, store, f);
        await CheckWhenDueAsync(coordinator, store, scope, f);

        scope.Db.ChangeTracker.Clear();
        var row = await scope.Db.Set<MonitorAccount>().AsNoTracking().SingleAsync();
        Assert.Equal(currentBook ? "degraded" : "live", row.State);
        var coverage = Assert.Single((await coordinator.AccountAsync(f.Account.Id, default)).Coverage ?? []);
        if (currentBook)
        {
            Assert.Equal((MonitorCoverageStates.Unknown, "incomplete-book"), (coverage.State, coverage.Reason));
            return;
        }
        Assert.Null(row.Error);
        Assert.Equal((MonitorCoverageStates.Unsupported, MonitorCoverageStates.Unsupported, (bool?)null, "current-book-not-read"),
            (coverage.State, coverage.RecordedState, coverage.Covered, coverage.Reason));
        Assert.Equal(MonitorStopEvidence.NotRead, Assert.Single(coverage.Stops).Evidence);
        // Missing, short and cancelled stop checks did not run, past the grace and after a confirmation interval.
        Assert.Empty(await scope.Db.Set<PlayDiscrepancy>().AsNoTracking()
            .Where(x => x.Kind.StartsWith("protection.")).ToListAsync());
    }

    [PostgresFact]
    public async Task Coverage_rows_persist_with_check_time_and_reason_and_read_back_stale_after_two_intervals()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        await database.Seed(f);
        await using var scope = database.Scope(f);
        var (store, coordinator) = Create(scope, f, HyperliquidPerpetualReader.Descriptor.Capabilities);

        await CheckAsync(coordinator, store, f);
        var checkedAt = f.Clock.Now;
        scope.Db.ChangeTracker.Clear();
        var stored = await scope.Db.Set<MonitorCoverage>().AsNoTracking().SingleAsync();
        Assert.Equal((f.Play.Id, f.Account.Id, f.Account.OwnerId, "BTC", MonitorCoverageStates.Known, (bool?)true, "stop-orders", checkedAt),
            (stored.PlayId, stored.AccountId, stored.OwnerId, stored.ContractId, stored.State, stored.Covered, stored.Reason, stored.CheckedAtUtc));

        var read = Assert.Single((await coordinator.AccountAsync(f.Account.Id, default)).Coverage!);
        Assert.Equal((f.Play.Id, MonitorCoverageStates.Known, MonitorCoverageStates.Known, (bool?)true, "stop-orders", checkedAt),
            (read.PlayId, read.State, read.RecordedState, read.Covered, read.Reason, read.CheckedAtUtc));
        var stop = Assert.Single(read.Stops);
        Assert.Equal(("e", "s", MonitorStopEvidence.Resting, "stop", (DateTimeOffset?)checkedAt),
            (stop.EntryId, stop.LevelId, stop.Evidence, stop.OrderId, stop.ObservedAtUtc));
        var summary = Assert.Single((await coordinator.SummaryAsync(default)).Accounts);
        Assert.Equal(MonitorCoverageStates.Known, Assert.Single(summary.Coverage!).State);

        // Two intervals later without a check, reads project stale; the stored outcome is unchanged.
        f.Advance((int)(new MonitorOptions().Interval * 2).TotalSeconds + 1);
        read = Assert.Single((await coordinator.AccountAsync(f.Account.Id, default)).Coverage!);
        Assert.Equal((MonitorCoverageStates.Stale, MonitorCoverageStates.Known, "stop-orders", checkedAt),
            (read.State, read.RecordedState, read.Reason, read.CheckedAtUtc));
        scope.Db.ChangeTracker.Clear();
        Assert.Equal(MonitorCoverageStates.Known, (await scope.Db.Set<MonitorCoverage>().AsNoTracking().SingleAsync()).State);

        // The next check replaces it; a fresh read shows the venue without the stop.
        f.VenueOrders.Clear();
        await CheckAsync(coordinator, store, f);
        read = Assert.Single((await coordinator.AccountAsync(f.Account.Id, default)).Coverage!);
        Assert.Equal((MonitorCoverageStates.Known, (bool?)false, "no-stop", f.Clock.Now),
            (read.State, read.Covered, read.Reason, read.CheckedAtUtc));
        Assert.Equal(MonitorStopEvidence.NotSeen, Assert.Single(read.Stops).Evidence);
    }

    [PostgresTheory] // Review: coverage never outlives the Play's open position.
    [InlineData("closed")]
    [InlineData("cancelled")]
    public async Task A_play_that_closes_or_is_cancelled_is_hidden_at_once_and_its_row_removed_by_the_next_check(string ending)
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        await database.Seed(f);
        await using var scope = database.Scope(f);
        var (store, coordinator) = Create(scope, f, HyperliquidPerpetualReader.Descriptor.Capabilities);
        await CheckAsync(coordinator, store, f);
        Assert.Single((await coordinator.AccountAsync(f.Account.Id, default)).Coverage!);

        scope.Db.ChangeTracker.Clear();
        if (ending == "closed")
        {
            var play = await scope.Db.Plays.SingleAsync(x => x.Id == f.Play.Id);
            play.MarkClosed("synthetic close", f.Clock.Now);
            await scope.Db.SaveChangesAsync();
        }
        // An Open Play cannot be cancelled through the domain; the read must still hide any stored row for it.
        else await scope.Db.Plays.Where(x => x.Id == f.Play.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, PlayStatus.Cancelled));

        // Hidden on every read straight away: the store returns only currently Open Plays.
        Assert.Empty(await store.CoverageAsync([f.Account.Id], default));
        Assert.Empty((await coordinator.AccountAsync(f.Account.Id, default)).Coverage!);
        Assert.Empty(Assert.Single((await coordinator.SummaryAsync(default)).Accounts).Coverage!);
        Assert.Single(await scope.Db.Set<MonitorCoverage>().AsNoTracking().ToListAsync());

        // The next check (the linked stop is still watched) no longer evaluates it as Open and removes the row.
        await CheckWhenDueAsync(coordinator, store, scope, f);
        scope.Db.ChangeTracker.Clear();
        Assert.Empty(await scope.Db.Set<MonitorCoverage>().AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Review: account detail said "Monitored" before any coverage existed (after the migration, or once reconciliation opens
    /// a Play, until the next check). The account monitor now counts its Open tracked Plays without a coverage row.
    /// </summary>
    [PostgresFact]
    public async Task The_account_monitor_counts_open_plays_without_coverage_until_a_check_records_it()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        var sibling = f.AddPlay("long", 1);
        f.Position = 2;
        // A Closed Play never counts: only Open Plays have a position to cover.
        var closed = f.AddPlay("long", 1);
        closed.MarkClosed("fixture", f.Clock.Now.AddMinutes(-5));
        await database.Seed(f);
        await using var scope = database.Scope(f);
        var (store, coordinator) = Create(scope, f, HyperliquidPerpetualReader.Descriptor.Capabilities);
        // A live monitor row from before the migration: a success, but no coverage rows yet.
        await coordinator.DiscoverAsync([HyperliquidPerpetualReader.Id], f.Clock.Now, default);

        var before = await coordinator.AccountAsync(f.Account.Id, default);
        Assert.Equal((2, 0), (before.UncheckedPlays, before.Coverage!.Count));
        Assert.Equal(2, Assert.Single((await coordinator.SummaryAsync(default)).Accounts).UncheckedPlays);
        Assert.Equal(2, (await store.UncheckedPlaysAsync([f.Account.Id], default))[f.Account.Id]);

        await CheckAsync(coordinator, store, f);
        var after = await coordinator.AccountAsync(f.Account.Id, default);
        Assert.Equal((0, 2), (after.UncheckedPlays, after.Coverage!.Count));
        Assert.Equal(0, Assert.Single((await coordinator.SummaryAsync(default)).Accounts).UncheckedPlays);
        Assert.Empty(await store.UncheckedPlaysAsync([f.Account.Id], default));
        Assert.Contains(sibling.Id, after.Coverage!.Select(x => x.PlayId));
    }

    [PostgresFact]
    public async Task Coverage_reads_only_open_plays_and_rows_cascade_with_their_play_and_account()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        await database.Seed(f);
        await using var scope = database.Scope(f);
        var store = MonitorTestPaths.Store(scope.Db, scope.Work);
        // A second, Closed Play on the account, and a third Open one without links that can be deleted on its own.
        var closed = new Play(Guid.NewGuid(), f.Account, new("hyperliquid", "BTC"), InstrumentSource.Venue, f.Play.Plan, f.Clock.Now.AddHours(-1));
        closed.MarkPlanned(f.Clock.Now.AddMinutes(-30)); closed.MarkOpen("fixture", f.Clock.Now.AddMinutes(-20)); closed.MarkClosed("fixture", f.Clock.Now.AddMinutes(-5));
        var loose = new Play(Guid.NewGuid(), f.Account, new("hyperliquid", "BTC"), InstrumentSource.Venue, f.Play.Plan, f.Clock.Now.AddHours(-1));
        loose.MarkPlanned(f.Clock.Now.AddMinutes(-30)); loose.MarkOpen("fixture", f.Clock.Now.AddMinutes(-20));
        scope.Db.AddRange(closed, loose);
        await scope.Db.SaveChangesAsync();
        MonitorCoverage Row(Play play) => new()
        {
            PlayId = play.Id, OwnerId = f.Account.OwnerId, AccountId = f.Account.Id, ContractId = "BTC",
            State = MonitorCoverageStates.Unknown, Reason = "incomplete-book", CheckedAtUtc = f.Clock.Now
        };
        await scope.Work.ExecuteAsync(async () =>
        {
            await store.SaveCoverageAsync(f.Account.Id, [Row(f.Play), Row(closed), Row(loose)], default);
            return true;
        }, default);

        Assert.Equal(new[] { f.Play.Id, loose.Id }.Order(), (await store.CoverageAsync([f.Account.Id], default)).Select(x => x.PlayId).Order());
        Assert.Equal(3, await scope.Db.Set<MonitorCoverage>().CountAsync());

        // Deleting a Play removes its row with it.
        await scope.Db.Plays.Where(x => x.Id == loose.Id).ExecuteDeleteAsync();
        Assert.Equal(new[] { f.Play.Id, closed.Id }.Order(), await scope.Db.Set<MonitorCoverage>().Select(x => x.PlayId).OrderBy(x => x).ToListAsync());
        // Both foreign keys cascade: an account cannot keep its Plays (they restrict it), but its rows never outlive it.
        var rules = await scope.Db.Database.SqlQuery<string>($"""
            SELECT confrelid::regclass::text || ':' || confdeltype::text AS "Value" FROM pg_constraint
            WHERE conrelid = 'monitor_coverage'::regclass AND contype = 'f' ORDER BY 1
            """).ToListAsync();
        Assert.Equal(["accounts:c", "plays:c"], rules);
    }

    private static async Task CheckWhenDueAsync(MonitorCoordinator coordinator, MonitorStore store, DiscrepancyDatabaseScope scope, DiscrepancyFixture f)
    {
        scope.Db.ChangeTracker.Clear();
        var due = (await scope.Db.Set<MonitorAccount>().AsNoTracking().SingleAsync()).NextRunAtUtc!.Value;
        f.Clock.Now = (due > f.Clock.Now ? due : f.Clock.Now).AddSeconds(1);
        await CheckAsync(coordinator, store, f);
    }

    private static (MonitorStore, MonitorCoordinator) Create(DiscrepancyDatabaseScope scope, DiscrepancyFixture f, VenueCapabilities capabilities)
    {
        var monitor = MonitorTestPaths.Store(scope.Db, scope.Work);
        var venues = new VenueRegistry([VenueDescriptor.Manual, HyperliquidPerpetualReader.Descriptor with { Capabilities = capabilities }],
            [new Reader(f)], [new Orders(f)], [], [], [], [], []);
        return (monitor, new(monitor, new WorkspaceStore(scope.Db, scope.Work), new NoImporter(), venues, new NoCredentials(),
            scope.Work, scope.Events, new NoCommands(), new CoreOwner(f.Account.OwnerId), new MonitorOptions(), f.Clock, scope.Service));
    }

    private static async Task CheckAsync(MonitorCoordinator coordinator, MonitorStore store, DiscrepancyFixture f)
    {
        await coordinator.DiscoverAsync([HyperliquidPerpetualReader.Id], f.Clock.Now, default);
        var lease = Assert.Single(await store.ClaimDueAsync("scheduler", f.Clock.Now, 2, default));
        Assert.True(await coordinator.RunAsync(lease, default));
    }

    private sealed class Reader(DiscrepancyFixture f) : IPerpetualVenueReader
    {
        public string VenueId => HyperliquidPerpetualReader.Id;
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => Task.FromResult(f.Observation().Read.Sync.Instruments);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct) =>
            Task.FromResult(f.Observation().Read.Sync with { FillHistoryComplete = true });
    }
    private sealed class Orders(DiscrepancyFixture f) : IVenueOrderReader
    {
        public string VenueId => HyperliquidPerpetualReader.Id;
        public Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct) => Task.FromResult(f.Observation().Read.Orders!);
    }
    private sealed class NoImporter : IMonitorImporter
    {
        public Task RetainAsync(Account a, VenueOrderReadResult o, CancellationToken ct) => Task.CompletedTask;
        public Task ReconcileAsync(Account a, VenueOrderReadResult o, IReadOnlyList<VenueInstrument> i, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class NoCredentials : IAccountCredentialReader
    {
        public Task<string?> ReadAsync(Guid id, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<CredentialAttempt?> ReadAttemptAsync(Guid id, CancellationToken ct) => Task.FromResult<CredentialAttempt?>(null);
        public Task MarkRefusedAsync(Guid id, string revision, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class NoCommands : IMonitorCommandSink
    {
        public Task EnqueueAsync(MonitorAccountCommand c, CancellationToken ct) => Task.CompletedTask;
    }
}
