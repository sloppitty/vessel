using Microsoft.EntityFrameworkCore;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Infrastructure.Venues.Risex;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 0 item 0.4c, coverage and evidence (review defects D-09, D-18 and the chart part of D-08): the monitor records each
/// Open Play's stop coverage (known, shared, unknown or unsupported) with its check time, reason and per-level evidence.
/// Structural unknowns never become missing and never degrade the run; a venue whose order reader does not return the
/// current book is labelled "current book not read" and its book-dependent checks do not run.
/// </summary>
public sealed class MonitorCoverageTests
{
    [Fact]
    public void Venue_descriptors_say_whether_the_order_reader_returns_the_current_book()
    {
        Assert.True(HyperliquidPerpetualReader.Descriptor.Capabilities.CurrentOrderBook);
        Assert.True(LighterExchange.Mainnet.Descriptor.Capabilities.CurrentOrderBook);
        Assert.True(LighterExchange.Robinhood.Descriptor.Capabilities.CurrentOrderBook);
        // RISEx reads bounded order history and TP/SL records, not /v1/orders/open (owner decision: label it).
        Assert.False(RisexReader.Descriptor.Capabilities.CurrentOrderBook);
        Assert.False(VenueDescriptor.Manual.Capabilities.CurrentOrderBook);
        Assert.False(VenueCapabilities.None.CurrentOrderBook);
    }

    [Fact]
    public async Task Sized_stop_orders_give_known_coverage_with_dated_resting_evidence_per_level()
    {
        var f = new DiscrepancyFixture();
        var coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((f.Play.Id, "BTC", MonitorCoverageStates.Known, (bool?)true, "stop-orders"),
            (coverage.PlayId, coverage.ContractId, coverage.State, coverage.Covered, coverage.Reason));
        var stop = Assert.Single(coverage.Stops);
        Assert.Equal(("e", "s", MonitorStopEvidence.Resting, "stop", (DateTimeOffset?)f.Clock.Now),
            (stop.EntryId, stop.LevelId, stop.Evidence, stop.OrderId, stop.ObservedAtUtc));

        // A complete, fresh book without the stop: known not covered, and the level was not seen at the venue.
        f.VenueOrders.Clear(); f.Advance(60);
        coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((MonitorCoverageStates.Known, (bool?)false, "no-stop"), (coverage.State, coverage.Covered, coverage.Reason));
        Assert.Equal(MonitorStopEvidence.NotSeen, Assert.Single(coverage.Stops).Evidence);

        // An incomplete book proves nothing either way.
        f.OrdersComplete = false; f.Advance(60);
        coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((MonitorCoverageStates.Unknown, (bool?)null, "incomplete-book"), (coverage.State, coverage.Covered, coverage.Reason));
        Assert.Equal(MonitorStopEvidence.Unknown, Assert.Single(coverage.Stops).Evidence);
    }

    [Fact] // D-18: structural unknowns were shown as "Monitored"; they must read unknown, never missing or degraded.
    public async Task Opposite_plays_sharing_whole_position_stops_are_unknown_never_missing()
    {
        var f = new DiscrepancyFixture { Position = 1 }; // long 1 + long 1 − short 1
        var sibling = f.AddPlay("long", 1);
        var opposite = f.AddPlay("short", 1);
        // The first long's whole-position sell stop, and the short's own whole-position buy stop.
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        WholePositionStop(f, opposite, "short-stop", "buy", 110);
        await f.Evaluate(); f.Advance(60);
        var result = await f.Evaluate();

        Assert.False(result.Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind is "protection.missing" or "protection.short" or "protection.cancelled");
        var coverage = result.Coverage!.ToDictionary(x => x.PlayId);
        Assert.Equal(new[] { f.Play.Id, sibling.Id, opposite.Id }.Order(), coverage.Keys.Order());
        Assert.All(coverage.Values, x => Assert.Equal((MonitorCoverageStates.Unknown, (bool?)null, "opposing-plays"), (x.State, x.Covered, x.Reason)));
    }

    [Fact]
    public async Task One_whole_position_stop_covering_same_direction_siblings_is_shared()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        var sibling = f.AddPlay("long", 1);
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        await f.Evaluate(); f.Advance(60);
        var result = await f.Evaluate();

        Assert.False(result.Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.missing");
        var coverage = result.Coverage!.ToDictionary(x => x.PlayId);
        Assert.All([coverage[f.Play.Id], coverage[sibling.Id]], x =>
            Assert.Equal((MonitorCoverageStates.Shared, (bool?)null, "shared-whole-position-stop"), (x.State, x.Covered, x.Reason)));
        // The owner's own planned stop rests; the sibling's planned stop is not at the venue (the shared one covers it).
        Assert.Equal(MonitorStopEvidence.Resting, Assert.Single(coverage[f.Play.Id].Stops).Evidence);
        Assert.Equal(MonitorStopEvidence.NotSeen, Assert.Single(coverage[sibling.Id].Stops).Evidence);
    }

    [Fact] // D-09 at the evaluator: without the capability nothing book-dependent is decided, and that alone is not incomplete.
    public async Task Without_a_current_book_protection_is_unsupported_and_book_checks_do_not_run()
    {
        // RISEx-like: history rows only (never stamped with the read time), never a complete book; the stop was cancelled.
        var f = new DiscrepancyFixture { OrdersComplete = false, StampOpenOrders = false };
        f.Stop(o => o with { Status = VenueOrderStatus.Canceled, VenueStatus = "ORDER_STATUS_CANCELLED" });
        f.CurrentOrderBook = false;
        var first = await f.Evaluate();
        f.Advance(60);
        var second = await f.Evaluate();

        Assert.False(first.Incomplete);
        Assert.False(second.Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind.StartsWith("protection.", StringComparison.Ordinal) ||
            x.Kind is "entry.orphaned" or "level.drift");
        var coverage = Assert.Single(second.Coverage!);
        Assert.Equal((MonitorCoverageStates.Unsupported, (bool?)null, "current-book-not-read"), (coverage.State, coverage.Covered, coverage.Reason));
        Assert.Equal(MonitorStopEvidence.NotRead, Assert.Single(coverage.Stops).Evidence);
    }

    [Fact] // Review: per-level evidence counts only what coverage counts as a closing stop.
    public async Task A_resting_linked_order_that_is_not_a_recognized_closing_stop_never_confirms_its_level()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { OrderType = "Trigger Market" }); // a trigger whose mechanics are not recognized
        var coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((MonitorCoverageStates.Unknown, "mechanics"), (coverage.State, coverage.Reason));
        var stop = Assert.Single(coverage.Stops);
        Assert.Equal((MonitorStopEvidence.Unknown, "stop", (DateTimeOffset?)null), (stop.Evidence, stop.OrderId, stop.ObservedAtUtc));

        // A plain limit linked as the stop rests, but no stop protects the position: not confirmed either.
        f.Stop(o => o with { OrderType = "Limit", TriggerPrice = null }); f.Advance(60);
        coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((MonitorCoverageStates.Known, (bool?)false, "no-stop"), (coverage.State, coverage.Covered, coverage.Reason));
        Assert.Equal(MonitorStopEvidence.Unknown, Assert.Single(coverage.Stops).Evidence);

        // A buy stop does not close a long: same answer.
        f.Stop(o => o with { OrderType = "Stop Market", TriggerPrice = 90m, Side = "buy" }); f.Advance(60);
        coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal(MonitorStopEvidence.Unknown, Assert.Single(coverage.Stops).Evidence);
    }

    [Fact] // Review: a stop resting away from its planned price protects, but does not confirm the planned price.
    public async Task A_stop_resting_away_from_its_planned_price_is_said_with_its_price()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { TriggerPrice = 85m, LimitPrice = 85m });
        var coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((MonitorCoverageStates.Known, (bool?)true, "stop-orders"), (coverage.State, coverage.Covered, coverage.Reason));
        var stop = Assert.Single(coverage.Stops);
        Assert.Equal((MonitorStopEvidence.RestingElsewhere, "stop", "85", "90", (DateTimeOffset?)f.Clock.Now),
            (stop.Evidence, stop.OrderId, stop.Price, stop.PlannedPrice, stop.ObservedAtUtc));

        // Within the matcher's step of the planned price it confirms the level, with both prices.
        f.Stop(o => o with { TriggerPrice = 90.0004m, LimitPrice = 90.0004m }); f.Advance(60);
        stop = Assert.Single(Assert.Single((await f.Evaluate()).Coverage!).Stops);
        Assert.Equal((MonitorStopEvidence.Resting, "90.0004", "90"), (stop.Evidence, stop.Price, stop.PlannedPrice));
    }

    [Fact] // Review: missing evidence comes before a structural unknown, and each order-read failure is named.
    public async Task Missing_evidence_reasons_come_first_and_name_the_order_read_failure()
    {
        // Opposing Plays without a complete position read: the position is unknown, not the opposing structure.
        var f = new DiscrepancyFixture { Position = 1, PositionsComplete = false };
        var sibling = f.AddPlay("long", 1);
        f.AddPlay("short", 1);
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        Assert.Equal("position-unknown", (await f.Evaluate()).Coverage!.Single(x => x.PlayId == sibling.Id).Reason);
        f.PositionsComplete = true; f.Advance(60);
        Assert.Equal("opposing-plays", (await f.Evaluate()).Coverage!.Single(x => x.PlayId == sibling.Id).Reason);

        async Task<string> Reason(Action<DiscrepancyFixture> change)
        {
            var g = new DiscrepancyFixture();
            change(g);
            return Assert.Single((await g.Evaluate()).Coverage!).Reason;
        }
        Assert.Equal("incomplete-book", await Reason(g => g.OrdersComplete = false));
        // An order read older than the freshness window is stale, not incomplete.
        Assert.Equal("stale-order-read", await Reason(g => g.ObservationOffset = -MonitorOptions.Lease - TimeSpan.FromMinutes(1)));
        // The same venue order twice in one read: the read cannot be trusted, though it claims completeness.
        Assert.Equal("order-read-unreliable", await Reason(g => g.VenueOrders.Add(g.VenueOrders[0])));
    }

    /// <summary>A resting whole-position stop linked to <paramref name="play"/>'s first stop level.</summary>
    internal static void WholePositionStop(DiscrepancyFixture f, Play play, string id, string side, decimal price)
    {
        f.Retained(play, id, OrderLinkRole.Stop, 0, price, "Stop Market", true);
        f.Store.Links.Add(f.Link(play, id, OrderLinkRole.Stop, "e", "s", OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now.AddMinutes(-15)));
        f.VenueOrders.Add(new(id, "BTC", side, "Stop Market", price, price, true, true, 0, 0, f.Clock.Now.AddMinutes(-15),
            VenueOrderStatus.Open, "open", f.Clock.Now));
    }
}

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
