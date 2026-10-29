using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Persistence;

namespace Vessel.Tests;

internal sealed class FakeOrderReader : IVenueOrderReader
{
    public string VenueId => "hyperliquid";
    public List<VenueOrder> Orders { get; } = [];
    public bool Fail { get; set; }
    public Task<VenueOrderReadResult> ReadOrdersAsync(string publicAddress, CancellationToken ct) => Fail
        ? Task.FromException<VenueOrderReadResult>(new VenueReadException("secret failure"))
        : Task.FromResult(new VenueOrderReadResult(Orders.ToList(), DateTimeOffset.UtcNow, "notice"));
}

public sealed partial class ExecutionPostgresTests
{
    private sealed class Harness(VesselDbContext db, Guid owner, FixtureReader reader, FakeOrderReader orders)
    {
        public VesselDbContext Db => db;
        public Guid Owner => owner;
        public FixtureReader Reader => reader;
        public FakeOrderReader Orders => orders;
        public WorkspaceService Workspace { get; } = new(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.For(db));
        public PlayService Plays { get; } = new(new PlayStore(db, TestUnitOfWork.For(db)), new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.For(db));
        public PlayExecutionService Execution => new(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)), TestVenues.With(orders), TimeProvider.System, TestUnitOfWork.For(db));
        /// <summary>The owner's Check through the monitor: admission, then the worker's claim and run of that request.</summary>
        public Task<PlayExecutionDto> CheckAsync(Guid playId, bool expectCompleted = true, TimeProvider? time = null) => MonitorTestPaths.CheckAsync(db, owner,
            TestVenues.With(MonitorTestPaths.Advancing(reader), orders), playId, expectCompleted: expectCompleted, time: time);
        public SizingService Sizing => new(new SizingStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.For(db));
    }

    private static async Task<(CoreDatabase Database, Harness Harness, Guid Account)> SetUp()
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var reader = new FixtureReader();
        reader.Result = reader.Result with { Fills = [] };
        var harness = new Harness(database.Context(owner), owner, reader, new FakeOrderReader());
        var account = await harness.Workspace.CreateAccountAsync(new(null, "Venue", "hyperliquid", "0x" + Guid.NewGuid().ToString("N") + "00000000"), default);
        return (database, harness, account.Id);
    }

    private static async Task<PlayDto> PlannedPlay(Harness h, Guid account, string title = "Breakout")
    {
        var play = await h.Plays.CreateAsync(new CreatePlayRequest(account, "BTC", "venue", title, TestPlays.Plan()), default);
        return await h.Plays.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "planned"), default);
    }

    private static VenueOrder Order(string id, string side, decimal price, string status = "open", decimal? trigger = null,
        string type = "Limit", bool reduceOnly = false, int placedLater = 0, decimal size = 1m, DateTimeOffset? placed = null) => new(id, "BTC", side,
        type, price, trigger, reduceOnly, false, size, status == "open" ? size : 0m, placed ?? DateTimeOffset.UtcNow.AddSeconds(placedLater),
        HyperliquidPerpetualReader.Normalize(status), status, DateTimeOffset.UtcNow);

    private static VenueFill Fill(string id, string orderId, string dir, decimal price, decimal quantity, DateTimeOffset? at = null) =>
        new(id, "BTC", HyperliquidPerpetualReader.SideOf(dir.Contains("Long") == dir.StartsWith("Open") ? "B" : "A"), dir, price, quantity, 0.01m, "USDC",
            dir.StartsWith("Close") ? 5m : 0m, at ?? DateTimeOffset.UtcNow, orderId, "hash-" + id, "{}", HyperliquidPerpetualReader.EffectOf(dir));

    private static void AddFills(Harness h, params VenueFill[] fills) => h.Reader.Result = h.Reader.Result with { Fills = [.. h.Reader.Result.Fills, .. fills] };

    [PostgresFact]
    public async Task Accepted_percentage_overflow_does_not_break_execution_reads_or_link_an_unresolved_target()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var plan = TestPlays.Plan("100000") with
        {
            Leverage = "1",
            Entries = [TestPlays.Plan("100000").Entries[0] with
            {
                Targets = [new PlanExit("target-1", "percent", "10000000000000000000000000000", "100")]
            }]
        };
        var play = await h.Plays.CreateAsync(new(account, "BTC", "venue", "Extreme target", plan), default);
        play = await h.Plays.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "planned"), default);

        var execution = await h.Execution.GetAsync(play.Summary.Id, default);
        Assert.Equal("planned", execution.Status);
        Assert.Empty(execution.Links);
        Assert.Equal("0", execution.Totals.EnteredQuantity);
        Assert.Equal("10000000000000000000000000000", play.Plan.Entries[0].Targets[0].Value);
        Assert.Null(ExecutionMatcher.Levels(play.Plan).Single(l => l.Role == OrderLinkRole.Target).Price);

        h.Orders.Orders.AddRange([Order("entry", "buy", 100000m), Order("exit", "sell", 1000000m, reduceOnly: true)]);
        execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("entry", "entry"), (Assert.Single(execution.Links).Role, execution.Links[0].Order!.OrderId));
        Assert.Equal("exit", Assert.Single(execution.UnlinkedOrders).OrderId);
        Assert.Empty(execution.Suggestions);
    }

    [PostgresFact]
    public async Task Accepted_tiny_entry_does_not_block_another_plays_account_reconciliation()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var ordinary = await PlannedPlay(h, account);
        var plan = TestPlays.Plan("0.0000000000000000000000001") with
        {
            Entries = [TestPlays.Plan("0.0000000000000000000000001").Entries[0] with { Stops = [], Targets = [] }]
        };
        var tiny = await h.Plays.CreateAsync(new(account, "BTC", "venue", "Tiny entry", plan), default);
        tiny = await h.Plays.ChangeStatusAsync(tiny.Summary.Id, new(tiny.Summary.Version, "planned"), default);
        h.Orders.Orders.AddRange([Order("ordinary", "buy", 100m), Order("tiny", "buy", 0.0000000000000000000000001m)]);

        var execution = await h.CheckAsync(ordinary.Summary.Id);
        Assert.Equal("ordinary", Assert.Single(execution.Links).Order!.OrderId);
        Assert.Empty(execution.Suggestions);
        var tinyExecution = await h.Execution.GetAsync(tiny.Summary.Id, default);
        Assert.Equal("tiny", Assert.Single(tinyExecution.Links).Order!.OrderId);
        Assert.Empty(tinyExecution.Suggestions);
        Assert.Equal("planned", tinyExecution.Status);
        Assert.Equal("0", tinyExecution.Totals.EnteredQuantity);
        Assert.Equal("0.0000000000000000000000001", tiny.Plan.Entries[0].Price);
    }

    [PostgresFact]
    public async Task Execution_receipt_sets_each_entry_beside_its_plan_and_scores_R_as_Review_does()
    {
        // Phase 1.1a (G-03): entry planned at 100 for 7.5 (margin 250 at 3x) fills 0.5 at an average of 100.4.
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        h.Orders.Orders.AddRange([Order("21", "buy", 100.01m, "filled", size: 0.5m), Order("23", "sell", 110m, reduceOnly: true, size: 0.5m)]);
        var at = DateTimeOffset.UtcNow;
        var clock = new ManualTime(at);
        AddFills(h, Fill("r1", "21", "Open Long", 100.2m, 0.25m, at), Fill("r2", "21", "Open Long", 100.6m, 0.25m, at));
        var execution = await h.CheckAsync(play.Summary.Id, time: clock);
        var entry = execution.Entries.Single();
        Assert.Equal(("open", "100", "7.5", 1), (execution.Status, entry.PlannedPrice, entry.PlannedQuantity, entry.PlanRevision));
        Assert.Equal(("0.5", "100.4"), (entry.FilledQuantity, entry.AverageFillPrice));
        Assert.Equal((null, PlayReceipts.OpenReason), (execution.Receipt!.EstimatedR, execution.Receipt.EstimatedRReason));

        h.Orders.Orders[1] = Order("23", "sell", 110m, "filled", reduceOnly: true, size: 0.5m);
        AddFills(h, Fill("r3", "23", "Close Long", 110m, 0.5m, at.AddSeconds(1)));
        // Both checks use one monotonic clock; scoring follows the exit regardless of wall-clock speed.
        clock.Advance(TimeSpan.FromSeconds(2));
        execution = await h.CheckAsync(play.Summary.Id, time: clock);
        // Net 5 − 0.03 fees over 50.2 notional × 5% stop distance = 2.51 initial risk.
        Assert.Equal(("closed", ExactDecimal.Format(4.97m / 2.51m), "2.51", "100.4", "110", 3),
            (execution.Status, execution.Receipt!.EstimatedR, execution.Receipt.InitialRisk, execution.Receipt.AverageEntryPrice,
                execution.Receipt.AverageExitPrice, execution.Receipt.FillCount));
        var report = await new ReviewService(new ReviewStore(h.Db, new SizingStore(h.Db, TestUnitOfWork.For(h.Db))), clock)
            .GetAsync(new(Period: "all"), default);
        Assert.Equal(execution.Receipt.EstimatedR, Assert.Single(report.Plays).RMultiple);
    }

    [PostgresFact]
    public async Task Links_orders_automatically_and_opens_then_closes_from_linked_fills()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        // Entry and target orders of 0.5; a filled order's size is what its retained fills must add up to.
        h.Orders.Orders.AddRange([Order("11", "buy", 100.01m, size: 0.5m), Order("12", "sell", 94m, trigger: 95m, type: "Stop Market", reduceOnly: true),
            Order("13", "sell", 110m, reduceOnly: true, size: 0.5m), Order("14", "buy", 80m)]);
        var execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(["entry", "stop", "target"], execution.Links.Select(l => l.Role));
        Assert.All(execution.Links, l => Assert.Equal(("linked", "automatic"), (l.State, l.Source)));
        Assert.Equal("14", Assert.Single(execution.UnlinkedOrders).OrderId);
        Assert.Equal(("planned", 1), (execution.Status, execution.Entries.Single().RestingOrders));

        h.Orders.Orders[0] = Order("11", "buy", 100.01m, "filled", size: 0.5m);
        h.Reader.Result = h.Reader.Result with { Fills = [Fill("f1", "11", "Open Long", 100m, 0.5m)] };
        execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("open", "0.5", "0.5", "100"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.OpenQuantity,
            execution.Entries.Single().AverageFillPrice));

        h.Orders.Orders[2] = Order("13", "sell", 110m, "filled", reduceOnly: true, size: 0.5m);
        h.Reader.Result = h.Reader.Result with { Fills = [Fill("f1", "11", "Open Long", 100m, 0.5m), Fill("f2", "13", "Close Long", 110m, 0.5m)] };
        execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("closed", "0", "5"), (execution.Status, execution.Totals.OpenQuantity, execution.Totals.ClosedPnlUsd));
        Assert.Equal("0.02", Assert.Single(execution.Totals.Fees).Amount);

        var history = await h.Plays.HistoryAsync(play.Summary.Id, default);
        Assert.Equal(["planned", "open", "closed"], history.StatusChanges.Select(c => c.To));
        Assert.Contains("order 11", history.StatusChanges[1].Note);
        var stored = await h.Db.StatusChanges.Where(c => c.PlayId == play.Summary.Id).OrderBy(c => c.OccurredAtUtc).ToListAsync();
        Assert.Equal([StatusChangeSource.Owner, StatusChangeSource.Venue, StatusChangeSource.Venue], stored.Select(c => c.Source));
    }

    [PostgresFact]
    public async Task Ambiguous_orders_wait_for_the_owner_and_dismissals_stick()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var first = await PlannedPlay(h, account, "First");
        var second = await PlannedPlay(h, account, "Second");
        h.Orders.Orders.Add(Order("21", "buy", 100m));
        var execution = await h.CheckAsync(first.Summary.Id);
        Assert.Empty(execution.Links);
        var suggestion = Assert.Single(execution.Suggestions);
        Assert.Single((await h.Execution.GetAsync(second.Summary.Id, default)).Suggestions);

        execution = await h.Execution.LinkAsync(first.Summary.Id, new("21", "entry", "entry-1"), default);
        Assert.Equal(("linked", "owner"), (execution.Links.Single().State, execution.Links.Single().Source));
        Assert.Empty((await h.Execution.GetAsync(second.Summary.Id, default)).Suggestions);
        await Assert.ThrowsAsync<ProblemException>(() => h.Execution.LinkAsync(second.Summary.Id, new("21", "entry", "entry-1"), default));

        // Unlinking is remembered: the next check does not link it again.
        execution = await h.Execution.UnlinkAsync(first.Summary.Id, execution.Links.Single().Id, default);
        Assert.Empty(execution.Links);
        await h.CheckAsync(first.Summary.Id);
        Assert.Empty((await h.Execution.GetAsync(first.Summary.Id, default)).Links);
        Assert.Single((await h.Execution.GetAsync(second.Summary.Id, default)).Links);
        Assert.NotEqual(Guid.Empty, suggestion.Id);
    }

    [PostgresFact]
    public async Task The_owner_links_an_order_to_one_of_several_stops()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var plan = TestPlays.Plan() with
        {
            Entries = [TestPlays.Plan().Entries[0] with { Stops = [new PlanExit("stop-1", "price", "95", "50"), new PlanExit("stop-2", "price", "90", "50")] }],
        };
        var draft = await h.Plays.CreateAsync(new CreatePlayRequest(account, "BTC", "venue", "Two stops", plan), default);
        var play = await h.Plays.ChangeStatusAsync(draft.Summary.Id, new(draft.Summary.Version, "planned"), default);
        h.Orders.Orders.AddRange([Order("41", "sell", 89m, trigger: 90m, type: "Stop Market", reduceOnly: true), Order("42", "sell", 80m, trigger: 85m, type: "Stop Market", reduceOnly: true)]);
        var execution = await h.CheckAsync(play.Summary.Id);
        var automatic = Assert.Single(execution.Links);
        Assert.Equal(("41", "stop", "stop-2"), (automatic.Order!.OrderId, automatic.Role, automatic.LevelId));

        await Assert.ThrowsAsync<ProblemException>(() => h.Execution.LinkAsync(play.Summary.Id, new("42", "stop", "entry-1", "target-1"), default));
        execution = await h.Execution.LinkAsync(play.Summary.Id, new("42", "stop", "entry-1", "stop-1"), default);
        Assert.Equal(["stop-1", "stop-2"], execution.Links.Select(l => l.LevelId));
    }

    [PostgresFact]
    public async Task Unplanned_closes_and_filled_entries_are_protected()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        h.Orders.Orders.AddRange([Order("31", "buy", 100m, "filled"), Order("32", "sell", 103m, "filled", placedLater: 5)]);
        h.Reader.Result = h.Reader.Result with { Fills = [Fill("f1", "31", "Open Long", 100m, 1m), Fill("f2", "32", "Close Long", 103m, 1m)] };
        var execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(["entry", "exit"], execution.Links.Select(l => l.Role));
        Assert.Equal("closed", execution.Status);

        // The closed plan is fixed; on an open play only unfilled entries could move.
        var current = await h.Plays.GetAsync(play.Summary.Id, default);
        Assert.Equal("closed", current.Summary.Status);
        var moved = TestPlays.Plan("101");
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => h.Plays.UpdateAsync(play.Summary.Id,
            new(current.Summary.Version, account, "BTC", "venue", current.Summary.Title, moved, current.Drawings, "", "Chased"), default))).StatusCode);
    }

    [PostgresFact]
    public async Task Filled_entry_price_cannot_be_revised_but_stops_can()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        h.Orders.Orders.Add(Order("41", "buy", 100m, "filled"));
        h.Reader.Result = h.Reader.Result with { Fills = [Fill("f1", "41", "Open Long", 100m, 0.2m)] };
        Assert.Equal("open", (await h.CheckAsync(play.Summary.Id)).Status);
        var current = await h.Plays.GetAsync(play.Summary.Id, default);
        var error = await Assert.ThrowsAsync<ProblemException>(() => h.Plays.UpdateAsync(play.Summary.Id,
            new(current.Summary.Version, account, "BTC", "venue", current.Summary.Title, TestPlays.Plan("101"), current.Drawings, "", "Chased"), default));
        Assert.Contains("has fills", error.Message);
        var revised = await h.Plays.UpdateAsync(play.Summary.Id,
            new(current.Summary.Version, account, "BTC", "venue", current.Summary.Title, TestPlays.Plan(stop: "97"), current.Drawings, "", "Trail"), default);
        Assert.Equal(2, revised.Summary.PlanRevision);
    }

    [PostgresFact]
    public async Task A_known_missing_entry_execution_keeps_the_play_open_until_it_arrives()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        var at = DateTimeOffset.UtcNow;
        // The entry order filled 2, but only one of its executions is retained so far.
        h.Orders.Orders.AddRange([Order("51", "buy", 100m, "filled", size: 2m, placed: at.AddMinutes(1)),
            Order("52", "sell", 110m, "filled", reduceOnly: true, placed: at.AddMinutes(3))]);
        AddFills(h, Fill("g1", "51", "Open Long", 100m, 1m, at.AddMinutes(2)), Fill("g2", "52", "Close Long", 110m, 1m, at.AddMinutes(4)));
        var execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("open", "1", "1"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity));
        Assert.Equal(0, (await h.Sizing.GetAsync(default)).Record.ClosedPlays);

        // The missing execution arrives later under its own fill ID: one unit is still open.
        AddFills(h, Fill("g3", "51", "Open Long", 100m, 1m, at.AddMinutes(2)));
        execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("open", "2", "1", "1"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity,
            execution.Totals.OpenQuantity));

        // The final exit closes it, and only then does sizing score it.
        h.Orders.Orders.Add(Order("53", "sell", 110m, "filled", reduceOnly: true, placed: at.AddMinutes(5)));
        AddFills(h, Fill("g4", "53", "Close Long", 110m, 1m, at.AddMinutes(6)));
        Assert.Equal("closed", (await h.CheckAsync(play.Summary.Id)).Status);
        var sizing = await h.Sizing.GetAsync(default);
        Assert.Equal((1, 1), (sizing.Record.ClosedPlays, sizing.Record.Wins));
    }

    [PostgresFact]
    public async Task Exits_that_filled_before_the_entry_belong_to_something_else()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        var at = DateTimeOffset.UtcNow;
        // An earlier position's target and stop fill at this play's levels before its entry fills.
        h.Orders.Orders.AddRange([Order("61", "sell", 110m, "filled", reduceOnly: true, placed: at.AddMinutes(1)),
            Order("62", "sell", 94m, "filled", trigger: 95m, type: "Stop Market", reduceOnly: true, placed: at.AddMinutes(1)),
            Order("63", "buy", 100m, "filled", placed: at.AddMinutes(3))]);
        AddFills(h, Fill("h1", "61", "Close Long", 110m, 1m, at.AddMinutes(2)), Fill("h2", "62", "Close Long", 95m, 1m, at.AddMinutes(2)),
            Fill("h3", "63", "Open Long", 100m, 1m, at.AddMinutes(4)));
        var execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("open", "1", "0"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity));
        Assert.Equal(["63"], execution.Links.Select(l => l.Order!.OrderId));
        Assert.Equal(0, (await h.Sizing.GetAsync(default)).Record.ClosedPlays);

        // A target placed before the entry filled but executed after it is this play's exit.
        h.Orders.Orders.Add(Order("64", "sell", 110m, "filled", reduceOnly: true, placed: at.AddMinutes(2)));
        AddFills(h, Fill("h4", "64", "Close Long", 110m, 1m, at.AddMinutes(5)));
        execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("closed", "1"), (execution.Status, execution.Totals.ExitedQuantity));
        Assert.Equal(["63", "64"], execution.Links.Select(l => l.Order!.OrderId));
    }

    [PostgresFact]
    public async Task Fills_decide_whether_an_order_can_enter_or_exit_a_play()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        var at = DateTimeOffset.UtcNow;
        // A buy at the entry price that closed an earlier short does not open this long play.
        h.Orders.Orders.Add(Order("71", "buy", 100m, "filled", placed: at.AddMinutes(1)));
        AddFills(h, Fill("k1", "71", "Close Short", 100m, 1m, at.AddMinutes(2)));
        var execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("planned", "0"), (execution.Status, execution.Totals.EnteredQuantity));
        Assert.Empty(execution.Links);

        // A flip at the target closes the long and opens a short in one fill that cannot be split, so it exits nothing.
        h.Orders.Orders.AddRange([Order("72", "buy", 100m, "filled", placed: at.AddMinutes(3)),
            Order("73", "sell", 110m, "filled", size: 2m, placed: at.AddMinutes(5))]);
        AddFills(h, Fill("k2", "72", "Open Long", 100m, 1m, at.AddMinutes(4)), Fill("k3", "73", "Long > Short", 110m, 2m, at.AddMinutes(6)));
        execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("open", "1", "0"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity));
        Assert.Equal(["72"], execution.Links.Select(l => l.Order!.OrderId));

        // The owner decides which play owns an order, not what its fills did: a second buy adds to the long.
        h.Orders.Orders.Add(Order("74", "buy", 102m, "filled", placed: at.AddMinutes(7)));
        AddFills(h, Fill("k4", "74", "Open Long", 102m, 1m, at.AddMinutes(8)));
        await h.CheckAsync(play.Summary.Id);
        foreach (var order in new[] { "74", "73" })
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => h.Execution.LinkAsync(play.Summary.Id, new(order, "exit"), default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => h.Execution.LinkAsync(play.Summary.Id, new("71", "entry", "entry-1"), default))).StatusCode);
        execution = await h.Execution.GetAsync(play.Summary.Id, default);
        Assert.Equal(("open", "1", "0", "1"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity, execution.Totals.OpenQuantity));
        Assert.Equal(["72"], execution.Links.Select(l => l.Order!.OrderId));
    }

    [PostgresFact]
    public async Task Closed_plays_keep_their_links_even_for_a_request_that_read_them_open()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        var at = DateTimeOffset.UtcNow;
        h.Orders.Orders.AddRange([Order("81", "buy", 100m, "filled", placed: at.AddMinutes(1)), Order("83", "buy", 101m, placed: at.AddMinutes(1))]);
        AddFills(h, Fill("m1", "81", "Open Long", 100m, 1m, at.AddMinutes(2)));
        Assert.Equal("open", (await h.CheckAsync(play.Summary.Id)).Status);

        // Another request reads the open play, then its exit closes it before that request takes the account lock.
        await using var other = database.Context(h.Owner);
        var stale = new Harness(other, h.Owner, h.Reader, h.Orders);
        Assert.Equal("open", (await stale.Plays.GetAsync(play.Summary.Id, default)).Summary.Status);
        h.Orders.Orders.Add(Order("82", "sell", 110m, "filled", reduceOnly: true, placed: at.AddMinutes(3)));
        AddFills(h, Fill("m2", "82", "Close Long", 110m, 1m, at.AddMinutes(4)));
        var closed = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("closed", 2), (closed.Status, closed.Links.Count));

        foreach (var link in closed.Links)
        {
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => h.Execution.UnlinkAsync(play.Summary.Id, link.Id, default))).StatusCode);
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => stale.Execution.UnlinkAsync(play.Summary.Id, link.Id, default))).StatusCode);
        }
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() =>
            stale.Execution.LinkAsync(play.Summary.Id, new("83", "entry", "entry-1"), default))).StatusCode);
        var after = await h.Execution.GetAsync(play.Summary.Id, default);
        Assert.Equal(("closed", 2, "0"), (after.Status, after.Links.Count, after.Totals.OpenQuantity));
        Assert.Equal(1, (await h.Sizing.GetAsync(default)).Record.Wins);
    }

    [PostgresFact]
    public async Task A_play_with_entry_fills_keeps_its_direction()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var shortPlan = ShortPlan();
        // Before any fill the idea can still turn around.
        var unfilled = await PlannedPlay(h, account, "Unfilled");
        var turned = await h.Plays.UpdateAsync(unfilled.Summary.Id, new(unfilled.Summary.Version, account, "BTC", "venue", "Unfilled", shortPlan,
            unfilled.Drawings, "", "Rejected at resistance"), default);
        Assert.Equal(("short", 2), (turned.Summary.Direction, turned.Summary.PlanRevision));

        var play = await PlannedPlay(h, account);
        var at = DateTimeOffset.UtcNow;
        h.Orders.Orders.Add(Order("91", "buy", 100m, "filled", placed: at.AddMinutes(1)));
        AddFills(h, Fill("n1", "91", "Open Long", 100m, 1m, at.AddMinutes(2)));
        Assert.Equal("open", (await h.CheckAsync(play.Summary.Id)).Status);
        var current = await h.Plays.GetAsync(play.Summary.Id, default);
        var error = await Assert.ThrowsAsync<ProblemException>(() => h.Plays.UpdateAsync(play.Summary.Id,
            new(current.Summary.Version, account, "BTC", "venue", current.Summary.Title, shortPlan, current.Drawings, "", "Changed my mind"), default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(("long", 1), ((await h.Plays.GetAsync(play.Summary.Id, default)).Summary.Direction,
            (await h.Plays.HistoryAsync(play.Summary.Id, default)).Revisions.Count));

        // Ordinary stop revisions still work, and the real long exit still links and closes the play.
        current = await h.Plays.GetAsync(play.Summary.Id, default);
        Assert.Equal(2, (await h.Plays.UpdateAsync(play.Summary.Id, new(current.Summary.Version, account, "BTC", "venue", current.Summary.Title,
            TestPlays.Plan(stop: "97"), current.Drawings, "", "Trail"), default)).Summary.PlanRevision);
        h.Orders.Orders.Add(Order("92", "sell", 110m, "filled", reduceOnly: true, placed: at.AddMinutes(3)));
        AddFills(h, Fill("n2", "92", "Close Long", 110m, 1m, at.AddMinutes(4)));
        Assert.Equal("closed", (await h.CheckAsync(play.Summary.Id)).Status);
    }

    [PostgresFact]
    public async Task A_cancel_waits_for_a_link_or_unlink_that_already_checked_the_play()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var linking = await PlannedPlay(h, account, "Linking");
        var unlinking = await PlannedPlay(h, account, "Unlinking");
        // Away from every planned level, so only the owner links them.
        h.Orders.Orders.AddRange([Order("101", "buy", 80m), Order("102", "buy", 80m)]);
        await h.CheckAsync(linking.Summary.Id);
        var link = Assert.Single((await h.Execution.LinkAsync(unlinking.Summary.Id, new("102", "entry", "entry-1"), default)).Links);

        async Task CancelWhileHeld(Guid play, string write, Func<Harness, Task> request)
        {
            var gate = new Gate(write);
            var (owner, _) = await Request(database, h, gate);
            await using var ownerDb = owner.Db;
            var (other, pid) = await Request(database, h);
            await using var otherDb = other.Db;
            var version = (await other.Plays.GetAsync(play, default)).Summary.Version;
            var pending = request(owner);
            Task<PlayDto> cancel;
            try
            {
                await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancel = other.Plays.ChangeStatusAsync(play, new(version, "cancelled", "changed-mind"), default);
                // The request has checked that the play is active, so the cancel waits for it instead of committing underneath it.
                Assert.NotSame(cancel, await Task.WhenAny(cancel, AccountManagementPostgresTests.WaitForLock(database, pid)));
            }
            finally { gate.Release.TrySetResult(); }
            await pending;
            Assert.Equal("cancelled", (await cancel).Summary.Status);
        }
        await CancelWhileHeld(linking.Summary.Id, "INSERT INTO play_order_links", owner => owner.Execution.LinkAsync(linking.Summary.Id, new("101", "entry", "entry-1"), default));
        await CancelWhileHeld(unlinking.Summary.Id, "UPDATE play_order_links", owner => owner.Execution.UnlinkAsync(unlinking.Summary.Id, link.Id, default));

        // Each request ran as if before the cancel, and the cancelled plays keep the links they ended with.
        var (verify, _) = await Request(database, h);
        await using var verifyDb = verify.Db;
        var kept = Assert.Single((await verify.Execution.GetAsync(linking.Summary.Id, default)).Links);
        Assert.Equal(("101", "cancelled"), (kept.Order!.OrderId, (await verify.Plays.GetAsync(linking.Summary.Id, default)).Summary.Status));
        Assert.Empty((await verify.Execution.GetAsync(unlinking.Summary.Id, default)).Links);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => verify.Execution.UnlinkAsync(linking.Summary.Id, kept.Id, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() =>
            verify.Execution.LinkAsync(unlinking.Summary.Id, new("102", "entry", "entry-1"), default))).StatusCode);
    }

    [PostgresFact]
    public async Task A_link_follows_the_plan_and_status_committed_before_it_took_the_lock()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var at = DateTimeOffset.UtcNow;
        var (scaling, firstOnly) = await OpenWithSecondEntry(h, account, at);
        var turning = await PlannedPlay(h, account, "Turning");
        var dropped = await PlannedPlay(h, account, "Dropped");
        h.Orders.Orders.AddRange([Order("111", "buy", 80m, placed: at.AddMinutes(5)), Order("112", "buy", 80m, "filled", placed: at.AddMinutes(5))]);
        AddFills(h, Fill("p2", "112", "Open Long", 80m, 1m, at.AddMinutes(6)));
        await h.CheckAsync(scaling);

        // The owner's link request reads the play, then another request changes it before the link takes the account lock.
        async Task<int> ChangedBeforeLock(Guid play, LinkOrderRequest request, Func<Harness, PlayDto, Task> change)
        {
            // Pause before the first lock, now the account row ahead of the execution advisory.
            var gate = new Gate("FROM accounts");
            var (owner, _) = await Request(database, h, gate);
            await using var ownerDb = owner.Db;
            var (other, _) = await Request(database, h);
            await using var otherDb = other.Db;
            var pending = owner.Execution.LinkAsync(play, request, default);
            try
            {
                await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await change(other, await other.Plays.GetAsync(play, default));
            }
            finally { gate.Release.TrySetResult(); }
            return (await Assert.ThrowsAsync<ProblemException>(() => pending)).StatusCode;
        }
        // The play turned short: a buy is no longer an entry.
        Assert.Equal(409, await ChangedBeforeLock(turning.Summary.Id, new("111", "entry", "entry-1"), (other, current) => Revise(other, current, ShortPlan())));
        // The second entry was removed: its filled order has no entry to join.
        Assert.Equal(400, await ChangedBeforeLock(scaling, new("112", "entry", "entry-2"), (other, current) => Revise(other, current, firstOnly)));
        // The play was cancelled.
        Assert.Equal(409, await ChangedBeforeLock(dropped.Summary.Id, new("111", "entry", "entry-1"), (other, current) =>
            other.Plays.ChangeStatusAsync(current.Summary.Id, new(current.Summary.Version, "cancelled", "invalidated"), default)));

        var (verify, _) = await Request(database, h);
        await using var verifyDb = verify.Db;
        Assert.Empty((await verify.Execution.GetAsync(turning.Summary.Id, default)).Links);
        Assert.Empty((await verify.Execution.GetAsync(dropped.Summary.Id, default)).Links);
        var execution = await verify.Execution.GetAsync(scaling, default);
        Assert.Equal(["110"], execution.Links.Select(l => l.Order!.OrderId));
        Assert.Equal("1", execution.Totals.EnteredQuantity);
    }

    [PostgresFact]
    public async Task A_plan_revision_and_a_link_to_the_entry_it_changes_take_turns()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var at = DateTimeOffset.UtcNow;
        var (scaling, firstOnly) = await OpenWithSecondEntry(h, account, at);
        var both = (await h.Plays.GetAsync(scaling, default)).Plan;
        h.Orders.Orders.AddRange([Order("121", "buy", 80m, "filled", placed: at.AddMinutes(5)), Order("122", "buy", 80m, "filled", placed: at.AddMinutes(5))]);
        AddFills(h, Fill("q1", "121", "Open Long", 80m, 1m, at.AddMinutes(6)), Fill("q2", "122", "Open Long", 80m, 1m, at.AddMinutes(6)));
        await h.CheckAsync(scaling);

        // The first request is held at its gated command; the second must wait for it, then decide from what it committed.
        async Task<(Task First, Task Second)> Turns(string gated, Func<Harness, PlayDto, Task> first, Func<Harness, PlayDto, Task> second)
        {
            var gate = new Gate(gated);
            var (a, _) = await Request(database, h, gate);
            var (b, pid) = await Request(database, h);
            var current = await b.Plays.GetAsync(scaling, default);
            var held = first(a, current);
            Task waiting;
            try
            {
                await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                waiting = second(b, current);
                Assert.NotSame(waiting, await Task.WhenAny(waiting, AccountManagementPostgresTests.WaitForLock(database, pid)));
            }
            finally { gate.Release.TrySetResult(); }
            await Task.WhenAll(held, waiting).ContinueWith(_ => { }, TaskScheduler.Default);
            await a.Db.DisposeAsync();
            await b.Db.DisposeAsync();
            return (held, waiting);
        }
        // A revision that already checked which entries have fills holds the play: the link then finds entry 2 gone.
        var (revision, link) = await Turns("UPDATE plays", (a, current) => Revise(a, current, firstOnly),
            (b, _) => b.Execution.LinkAsync(scaling, new("121", "entry", "entry-2"), default));
        await revision;
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => link)).StatusCode);

        // A link that already checked the plan holds the play: the revision then finds entry 2 filled.
        var (restore, _) = await Request(database, h);
        await using (restore.Db) await Revise(restore, await restore.Plays.GetAsync(scaling, default), both);
        (link, revision) = await Turns("INSERT INTO play_order_links", (a, _) => a.Execution.LinkAsync(scaling, new("122", "entry", "entry-2"), default),
            (b, current) => Revise(b, current, firstOnly));
        await link;
        Assert.Contains("has fills", (await Assert.ThrowsAsync<ProblemException>(() => revision)).Message);

        var (verify, _) = await Request(database, h);
        await using var verifyDb = verify.Db;
        Assert.Equal(["entry-1", "entry-2"], (await verify.Plays.GetAsync(scaling, default)).Plan.Entries.Select(e => e.Id));
        var execution = await verify.Execution.GetAsync(scaling, default);
        Assert.Equal(["110", "122"], execution.Links.Select(l => l.Order!.OrderId));
        Assert.Equal("2", execution.Totals.EnteredQuantity);
    }

    private static Task<PlayDto> Revise(Harness h, PlayDto current, PlayPlanDocument plan) => h.Plays.UpdateAsync(current.Summary.Id,
        new(current.Summary.Version, current.Summary.AccountId, "BTC", "venue", current.Summary.Title, plan, current.Drawings, "", "Changed the idea"), default);

    private static PlayPlanDocument ShortPlan() => TestPlays.Plan() with
    {
        Direction = "short",
        Entries = [TestPlays.Plan().Entries[0] with { Stops = [new PlanExit("stop-1", "price", "105", "100")], Targets = [new PlanExit("target-1", "price", "90", "100")] }],
    };

    /// <summary>An open play whose first entry filled and whose second at 98 has not, with the plan that keeps only the first.</summary>
    private static async Task<(Guid Play, PlayPlanDocument FirstOnly)> OpenWithSecondEntry(Harness h, Guid account, DateTimeOffset at)
    {
        var first = TestPlays.Plan().Entries[0] with { Share = "50" };
        var plan = TestPlays.Plan() with
        {
            Entries = [first, new PlanEntry("entry-2", "Entry 2", "#c9b9e4", "50", "98", [new PlanExit("stop-2", "price", "95", "100")],
                [new PlanExit("target-2", "price", "110", "100")])],
        };
        var draft = await h.Plays.CreateAsync(new CreatePlayRequest(account, "BTC", "venue", "Scaling", plan), default);
        var play = await h.Plays.ChangeStatusAsync(draft.Summary.Id, new(draft.Summary.Version, "planned"), default);
        h.Orders.Orders.Add(Order("110", "buy", 100m, "filled", placed: at.AddMinutes(1)));
        AddFills(h, Fill("p1", "110", "Open Long", 100m, 1m, at.AddMinutes(2)));
        Assert.Equal("open", (await h.CheckAsync(play.Summary.Id)).Status);
        return (play.Summary.Id, plan with { Entries = [first] });
    }

    /// <summary>Holds a request before its first matching command until released, so another request can act in between.</summary>
    private sealed class Gate(string sql) : DbCommandInterceptor
    {
        private int held;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Hold(DbCommand command)
        {
            if (!command.CommandText.Contains(sql, StringComparison.Ordinal) || Interlocked.Exchange(ref held, 1) == 1) return;
            Reached.TrySetResult();
            await Release.Task;
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { await Hold(command); return result; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) { await Hold(command); return result; }
    }

    /// <summary>Another API request on its own connection, optionally gated; its process ID shows PostgreSQL lock waits.</summary>
    private static async Task<(Harness Harness, int Pid)> Request(CoreDatabase database, Harness h, Gate? gate = null)
    {
        var options = new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString);
        if (gate is not null) options.AddInterceptors(gate);
        var db = new VesselDbContext(options.Options, new CoreOwner(h.Owner));
        await db.Database.OpenConnectionAsync();
        return (new Harness(db, h.Owner, h.Reader, h.Orders), ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
    }

    [PostgresFact]
    public async Task Untracked_plays_explain_why_and_venue_failures_are_safe()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var manual = await h.Plays.CreateAsync(new CreatePlayRequest(account, "My label", "manual", "Manual", TestPlays.Plan()), default);
        Assert.Equal("Plan the play to start tracking venue orders.", (await h.Execution.GetAsync(manual.Summary.Id, default)).Reason);
        manual = await h.Plays.ChangeStatusAsync(manual.Summary.Id, new(manual.Summary.Version, "planned"), default);
        var execution = await h.Execution.GetAsync(manual.Summary.Id, default);
        Assert.False(execution.Tracked);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => h.CheckAsync(manual.Summary.Id))).StatusCode);

        var play = await PlannedPlay(h, account);
        h.Orders.Fail = true;
        // A failed check is recorded on the account monitor for the owner, with a fixed message and never the venue's text.
        Assert.Equal("planned", (await h.CheckAsync(play.Summary.Id, expectCompleted: false)).Status);
        var monitor = (await MonitorTestPaths.Store(h.Db, TestUnitOfWork.For(h.Db)).FindAsync(account, default))!;
        Assert.Equal(("degraded", "The venue check failed. Monitoring will retry."), (monitor.State, monitor.Error));
        Assert.DoesNotContain("secret", monitor.Error);
    }
}
