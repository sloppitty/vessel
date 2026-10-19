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
        var first = await f.Service.EvaluateAsync(f.Observation() with { CurrentOrderBook = false }, default);
        f.Advance(60);
        var second = await f.Service.EvaluateAsync(f.Observation() with { CurrentOrderBook = false }, default);

        Assert.False(first.Incomplete);
        Assert.False(second.Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind.StartsWith("protection.", StringComparison.Ordinal) ||
            x.Kind is "entry.orphaned" or "level.drift");
        var coverage = Assert.Single(second.Coverage!);
        Assert.Equal((MonitorCoverageStates.Unsupported, (bool?)null, "current-book-not-read"), (coverage.State, coverage.Covered, coverage.Reason));
        Assert.Equal(MonitorStopEvidence.NotRead, Assert.Single(coverage.Stops).Evidence);
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
        f.Advance(60 * 11);
        await CheckAsync(coordinator, store, f);

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
