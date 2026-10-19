using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 0, 0.5b (decision 2): a filled entry's stops may sit at or beyond it with a revision reason, planned risk is
/// signed and frozen at the first entry fill, and the review probe <c>More4.cs StopRevision</c> inverts.
/// </summary>
public sealed class ManagedStopTests
{
    private static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-10-19T10:00:00Z", CultureInfo.InvariantCulture);
    private readonly Guid owner = Guid.NewGuid();
    private readonly MemoryPlayStore store;
    private readonly PlayService service;
    private readonly Account venue;

    public ManagedStopTests()
    {
        store = new MemoryPlayStore(owner);
        venue = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        store.Accounts.Add(venue);
        service = new PlayService(store, new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.Saving(store.SaveAsync));
    }

    private static ImportedFill EntryFill(string side = ExecutionFacts.Buy, string effect = ExecutionFacts.Open, decimal price = 100) => new()
    {
        Id = Guid.NewGuid(), Price = price, Quantity = 1, Fee = 0, FeeToken = "USDC", PnlBasis = ExecutionFacts.PnlGross,
        OccurredAtUtc = Planned.AddMinutes(5), ExternalOrderId = "entry", ContractId = "BTC", SourceFillId = Guid.NewGuid().ToString(),
        Side = side, PositionEffect = effect, Direction = "", TransactionHash = "",
    };

    private static PlayPlanDocument WithStop(PlayPlanDocument plan, string unit, string value) =>
        plan with { Entries = [plan.Entries[0] with { Stops = [new PlanExit("stop-1", unit, value, "100")] }] };

    /// <summary>A planned long (entry 100, stop 95, target 110, 3×), opened when <paramref name="filled"/>.</summary>
    private Play Play(bool filled = true, PlayPlanDocument? plan = null)
    {
        var play = new Play(Guid.NewGuid(), venue, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan ?? TestPlays.Plan()), Planned);
        play.MarkPlanned(Planned);
        if (filled) play.MarkOpen("Real entry.", Planned.AddMinutes(5));
        store.Plays.Add(play);
        return play;
    }

    private UpdatePlayRequest Revise(Play play, PlayPlanDocument plan, string? reason = "Move stop after entry") =>
        new(play.Version, play.AccountId, play.ContractId, "venue", play.Title, plan, JsonDocument.Parse(play.Drawings).RootElement.Clone(),
            play.Review, reason);

    /// <summary>
    /// The review probe <c>verify-opus-dotnet/More4.cs StopRevision</c>: an Open long filled at 100 had a stop at 100 or
    /// 101 refused ("a long stop goes below the entry price"). Now each saves with its reason; a negative percentage
    /// stays refused, pointing to a price stop.
    /// </summary>
    [Theory]
    [InlineData("price", "100", "100")]
    [InlineData("price", "101", "101")]
    [InlineData("price", "105", "105")]
    [InlineData("percent", "0", "100")]
    [InlineData("percent", "10", "96.666667")]
    public async Task Review_probe_stop_revision_saves_a_stop_at_or_beyond_a_filled_entry_with_a_reason(string unit, string value, string resolved)
    {
        var play = Play();
        store.EntryExecutions.Add(new("entry-1", EntryFill()));
        var saved = await service.UpdateAsync(play.Id, Revise(play, WithStop(TestPlays.Plan(), unit, value)), default);
        Assert.Equal((2, unit, value), (saved.Summary.PlanRevision, saved.Plan.Entries[0].Stops[0].Unit, saved.Plan.Entries[0].Stops[0].Value));
        Assert.Equal(decimal.Parse(resolved, CultureInfo.InvariantCulture),
            Math.Round(ExecutionMatcher.Levels(saved.Plan).Single(l => l.Role == OrderLinkRole.Stop).Price!.Value, 6));
        Assert.Equal("Move stop after entry", store.Revisions.Last().Reason);
    }

    [Fact]
    public async Task A_beyond_entry_stop_needs_a_reason_and_negative_percentages_point_to_a_price()
    {
        var play = Play();
        store.EntryExecutions.Add(new("entry-1", EntryFill()));
        var missing = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.UpdateAsync(play.Id, Revise(play, WithStop(TestPlays.Plan(), "price", "105"), reason: " "), default));
        Assert.Contains("Say why the plan changed", missing.Message);
        var negative = await Assert.ThrowsAsync<ProblemException>(() =>
            service.UpdateAsync(play.Id, Revise(play, WithStop(TestPlays.Plan(), "percent", "-10")), default));
        Assert.Equal((400, "Entry 1 stop 1: use a price for a stop beyond the entry; a percentage cannot be negative."),
            (negative.StatusCode, negative.Message));
        Assert.Equal(PlayDocuments.Serialize(TestPlays.Plan()), play.Plan);
        Assert.Empty(store.Revisions);
    }

    [Fact]
    public async Task Unfilled_entries_keep_the_wrong_side_rule()
    {
        // Planned, a linked entry order that has not filled, and an entry whose only fill contradicts it (a sell close).
        var planned = Play(filled: false);
        store.FilledEntries.Add("entry-1");
        var refused = await Assert.ThrowsAsync<ProblemException>(() =>
            service.UpdateAsync(planned.Id, Revise(planned, WithStop(TestPlays.Plan(), "price", "105")), default));
        Assert.Equal((400, "Entry 1: a long stop goes below the entry price."), (refused.StatusCode, refused.Message));

        var contradicted = Play();
        store.EntryExecutions.Add(new("entry-1", EntryFill(ExecutionFacts.Sell, ExecutionFacts.Close)));
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.UpdateAsync(contradicted.Id, Revise(contradicted, WithStop(TestPlays.Plan(), "price", "100")), default))).StatusCode);

        // Only the filled entry's stops are free; another entry's stop and every target keep their side.
        var two = TestPlays.Plan() with
        {
            Entries = [TestPlays.Plan().Entries[0] with { Share = "50" },
                TestPlays.Plan().Entries[0] with { Id = "entry-2", Name = "Entry 2", Share = "50",
                    Stops = [new PlanExit("stop-2", "price", "95", "100")], Targets = [new PlanExit("target-2", "price", "110", "100")] }]
        };
        var mixed = Play(plan: two);
        store.EntryExecutions.Clear();
        store.EntryExecutions.Add(new("entry-1", EntryFill()));
        PlayPlanDocument Exits(string stop1, string stop2, string target1) => two with
        {
            Entries = [two.Entries[0] with { Stops = [new PlanExit("stop-1", "price", stop1, "100")], Targets = [new PlanExit("target-1", "price", target1, "100")] },
                two.Entries[1] with { Stops = [new PlanExit("stop-2", "price", stop2, "100")] }]
        };
        foreach (var (plan, message) in new[]
        {
            (Exits("105", "105", "110"), "Entry 2: a long stop goes below the entry price."),
            (Exits("105", "95", "95"), "Entry 1: a long target goes above the entry price."),
        })
        {
            var error = await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAsync(mixed.Id, Revise(mixed, plan), default));
            Assert.Equal((400, message), (error.StatusCode, error.Message));
        }
        Assert.Equal("105", (await service.UpdateAsync(mixed.Id, Revise(mixed, Exits("105", "95", "110")), default)).Plan.Entries[0].Stops[0].Value);
    }

    [Fact]
    public async Task Manual_entry_executions_and_shorts_free_their_stops_too()
    {
        var manual = Play();
        store.EntryExecutions.Add(new("entry-1", null));
        Assert.Equal("100", (await service.UpdateAsync(manual.Id, Revise(manual, WithStop(TestPlays.Plan(), "price", "100")), default))
            .Plan.Entries[0].Stops[0].Value);

        var shortPlan = TestPlays.Plan() with
        {
            Direction = "short",
            Entries = [TestPlays.Plan().Entries[0] with { Stops = [new PlanExit("stop-1", "price", "105", "100")], Targets = [new PlanExit("target-1", "price", "90", "100")] }]
        };
        var shortPlay = Play(plan: shortPlan);
        store.EntryExecutions.Clear();
        store.EntryExecutions.Add(new("entry-1", EntryFill(ExecutionFacts.Sell)));
        var moved = shortPlan with { Entries = [shortPlan.Entries[0] with { Stops = [new PlanExit("stop-1", "price", "95", "100")] }] };
        Assert.Equal("95", (await service.UpdateAsync(shortPlay.Id, Revise(shortPlay, moved), default)).Plan.Entries[0].Stops[0].Value);
    }

    /// <summary>A linked level still moves only through a rebind; the plain save keeps refusing it.</summary>
    [Fact]
    public async Task A_linked_stop_still_needs_a_rebind_or_an_unlink_to_move()
    {
        var play = Play();
        store.EntryExecutions.Add(new("entry-1", EntryFill()));
        var order = new ImportedOrder
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = venue.Id, ContractId = "BTC", ExternalOrderId = "linked-stop", Side = "sell",
            OrderType = "Stop Market", LimitPrice = 94, TriggerPrice = 95, ReduceOnly = true, OriginalSize = 1, RemainingSize = 1, Status = "open",
            VenueStatus = "open", PlacedAtUtc = Planned.AddMinutes(1), StatusAtUtc = Planned.AddMinutes(1),
        };
        store.Links.Add(new PlayOrderLink(play, order, OrderLinkRole.Stop, "entry-1", "stop-1", OrderLinkState.Linked, OrderLinkSource.Automatic, Planned));
        store.ExternalOrderIds[order.Id] = order.ExternalOrderId;
        var refused = await Assert.ThrowsAsync<ProblemException>(() =>
            service.UpdateAsync(play.Id, Revise(play, WithStop(TestPlays.Plan(), "price", "105")), default));
        Assert.Equal(409, refused.StatusCode);
        Assert.Contains("Order linked-stop still references this plan level", refused.Message);
    }

    private static PlayPlanDocument Stops(string direction, params (string Price, string Share)[] stops) => TestPlays.Plan() with
    {
        Direction = direction,
        Entries = [TestPlays.Plan().Entries[0] with
        {
            Stops = stops.Select((s, i) => new PlanExit($"stop-{i}", "price", s.Price, s.Share)).ToList(),
            Targets = [new PlanExit("target-1", "price", direction == "long" ? "110" : "90", "100")]
        }]
    };

    /// <summary>The calculator used the absolute distance: a long stop at 105 on an entry at 100 had 5% "risk".</summary>
    [Theory]
    [InlineData("long", "105", null)]
    [InlineData("long", "100", null)]
    [InlineData("long", "95", "0.05")]
    [InlineData("short", "95", null)]
    [InlineData("short", "105", "0.05")]
    public void A_stop_that_is_not_on_the_loss_side_yields_no_planned_risk(string direction, string stop, string? distance) =>
        Assert.Equal(distance is null ? null : decimal.Parse(distance, CultureInfo.InvariantCulture),
            PlayOutcomeCalculator.StopDistance(Stops(direction, (stop, "100"))));

    [Fact]
    public void Locked_in_stops_offset_the_loss_of_the_others()
    {
        Assert.Equal(0.025m, PlayOutcomeCalculator.StopDistance(Stops("long", ("90", "50"), ("105", "50"))));
        Assert.Null(PlayOutcomeCalculator.StopDistance(Stops("long", ("95", "50"), ("105", "50"))));
    }

    private static ImportedFill Fill(string side, decimal price, decimal pnl, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), Price = price, Quantity = 1, ClosedPnlUsd = pnl, Fee = 0, FeeToken = "USDC", PnlBasis = ExecutionFacts.PnlGross,
        OccurredAtUtc = at, ExternalOrderId = "o" + at.Ticks, ContractId = "BTC", SourceFillId = "f" + at.Ticks, Side = side,
        PositionEffect = side == ExecutionFacts.Buy ? ExecutionFacts.Open : ExecutionFacts.Close, Direction = "", TransactionHash = "",
    };

    /// <summary>
    /// Initial risk and R use the revision in force at the first entry fill: the stop moved to 105 after the fill never
    /// changes them. A revision that is already beyond the entry at the first fill (a corrected manual time) gives no risk.
    /// </summary>
    [Theory]
    [InlineData(30, "5", "2")]
    [InlineData(150, null, null)]
    public void Initial_risk_and_r_stay_frozen_at_the_first_entry_fill(int entryMinutes, string? risk, string? r)
    {
        var play = new Play(Guid.NewGuid(), venue, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), Planned);
        List<PlayPlanRevision> revisions = [play.MarkPlanned(Planned).Revision];
        play.MarkOpen("Filled.", Planned.AddMinutes(60));
        revisions.Add(play.ChangePlan(PlayDocuments.Serialize(WithStop(TestPlays.Plan(), "price", "105")), "Lock in profit", Planned.AddMinutes(120))!);
        play.MarkClosed("Exited.", Planned.AddDays(1));
        var facts = new ClosedPlayFacts(play, revisions, [new(OrderLinkRole.Entry, Fill(ExecutionFacts.Buy, 100, 0, Planned.AddMinutes(entryMinutes))),
            new(OrderLinkRole.Target, Fill(ExecutionFacts.Sell, 110, 10, Planned.AddHours(20)))]);
        var outcome = PlayOutcomeCalculator.Outcome(facts)!;
        Assert.Equal(10m, outcome.NetResult);
        Assert.Equal(risk is null ? null : decimal.Parse(risk, CultureInfo.InvariantCulture), outcome.PlannedRisk);
        Assert.Equal(r is null ? null : decimal.Parse(r, CultureInfo.InvariantCulture), outcome.RMultiple);
    }
}

/// <summary>The acceptance scenario end to end and the atomic, version-checked rebind of a linked level (0.5b).</summary>
public sealed class ManagedStopPostgresTests
{
    private sealed class Harness(VesselDbContext db, Guid owner, FixtureReader reader, FakeOrderReader orders)
    {
        public VesselDbContext Db => db;
        public FixtureReader Reader => reader;
        public FakeOrderReader Orders => orders;
        public WorkspaceService Workspace { get; } = new(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.For(db));
        public PlayService Plays { get; } = new(new PlayStore(db, TestUnitOfWork.For(db)), new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.For(db));
        public PlayExecutionService Execution => new(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)),
            TestVenues.With(orders), TimeProvider.System, TestUnitOfWork.For(db));
        public Task<PlayExecutionDto> CheckAsync(Guid playId) => MonitorTestPaths.CheckAsync(db, owner,
            TestVenues.With(MonitorTestPaths.Advancing(reader), orders), playId);
        public SizingStore Sizing => new(db, TestUnitOfWork.For(db));
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

    private static VenueOrder Order(string id, string side, decimal price, string status = "open", decimal? trigger = null,
        string type = "Limit", bool reduceOnly = false, int placedLater = 0) => new(id, "BTC", side, type, price, trigger, reduceOnly, false,
        1m, status == "open" ? 1m : 0m, DateTimeOffset.UtcNow.AddSeconds(placedLater), HyperliquidPerpetualReader.Normalize(status), status,
        DateTimeOffset.UtcNow);

    private static VenueFill Fill(string id, string orderId, string dir, decimal price, decimal pnl = 0, decimal quantity = 1m) =>
        new(id, "BTC", HyperliquidPerpetualReader.SideOf(dir.Contains("Long") == dir.StartsWith("Open") ? "B" : "A"), dir, price, quantity, 0m, "USDC",
            pnl, DateTimeOffset.UtcNow, orderId, "hash-" + id, "{}", HyperliquidPerpetualReader.EffectOf(dir));

    /// <summary>A long planned at 100 (stop 95, target 110, 3×) whose entry filled 1 at 100, with its stop order resting at 95.</summary>
    private static async Task<PlayDto> OpenPlay(Harness h, Guid account)
    {
        var draft = await h.Plays.CreateAsync(new CreatePlayRequest(account, "BTC", "venue", "Breakout", TestPlays.Plan()), default);
        var play = await h.Plays.ChangeStatusAsync(draft.Summary.Id, new(draft.Summary.Version, "planned"), default);
        h.Orders.Orders.AddRange([Order("entry", "buy", 100m, "filled"), Order("stop", "sell", 94m, trigger: 95m, type: "Stop Market", reduceOnly: true)]);
        h.Reader.Result = h.Reader.Result with { Fills = [Fill("f-entry", "entry", "Open Long", 100m)] };
        var execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(("open", "100"), (execution.Status, execution.Entries.Single().AverageFillPrice));
        Assert.Equal(["entry", "stop"], execution.Links.Select(l => l.Role));
        return await h.Plays.GetAsync(play.Summary.Id, default);
    }

    private static UpdatePlayRequest Revise(PlayDto play, PlayPlanDocument plan, string? reason) =>
        new(play.Summary.Version, play.Summary.AccountId, play.Summary.Instrument, play.Summary.InstrumentSource, play.Summary.Title,
            plan, play.Drawings, play.Review, reason);

    /// <summary>
    /// Acceptance: an Open long whose entry filled at 100 saves a stop at 105 with a reason (refused before 0.5b); the R
    /// recorded at close still uses the initial stop at 95.
    /// </summary>
    [PostgresFact]
    public async Task An_open_long_filled_at_100_saves_a_stop_at_105_and_closes_with_r_from_the_initial_stop()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var draft = await h.Plays.CreateAsync(new CreatePlayRequest(account, "BTC", "venue", "Breakout", TestPlays.Plan()), default);
        var play = await h.Plays.ChangeStatusAsync(draft.Summary.Id, new(draft.Summary.Version, "planned"), default);
        h.Orders.Orders.Add(Order("entry", "buy", 100m, "filled"));
        h.Reader.Result = h.Reader.Result with { Fills = [Fill("f-entry", "entry", "Open Long", 100m)] };
        Assert.Equal("open", (await h.CheckAsync(play.Summary.Id)).Status);
        play = await h.Plays.GetAsync(play.Summary.Id, default);

        var lockedIn = TestPlays.Plan(stop: "105");
        await Assert.ThrowsAsync<DomainRuleException>(() => h.Plays.UpdateAsync(play.Summary.Id, Revise(play, lockedIn, null), default));
        play = await h.Plays.UpdateAsync(play.Summary.Id, Revise(play, lockedIn, "Lock in +5 after the breakout held"), default);
        Assert.Equal((2, "105"), (play.Summary.PlanRevision, play.Plan.Entries[0].Stops[0].Value));

        h.Orders.Orders.Add(Order("exit", "sell", 110m, "filled", reduceOnly: true, placedLater: 5));
        h.Reader.Result = h.Reader.Result with { Fills = [.. h.Reader.Result.Fills, Fill("f-exit", "exit", "Close Long", 110m, pnl: 10m)] };
        Assert.Equal("closed", (await h.CheckAsync(play.Summary.Id)).Status);

        var facts = Assert.Single(await h.Sizing.ClosedPlaysAsync(default));
        var outcome = PlayOutcomeCalculator.Outcome(facts)!;
        // Revision 1 (stop 95) was in force at the first entry fill: risk 100 notional × 5%, R = 10 ÷ 5.
        Assert.Equal((10m, 5m, 2m), (outcome.NetResult, outcome.PlannedRisk, outcome.RMultiple));
        // The current plan, stop 105, is a locked-in profit: it has no planned risk of its own.
        Assert.Null(PlayOutcomeCalculator.StopDistance(facts.CurrentPlan));
    }

    /// <summary>What a rebind may change, read through a fresh context: revisions, plan, version and every link of the Play.</summary>
    private sealed record StoredPlay(int Revisions, string Plan, long Version, string Links)
    {
        public Dictionary<Guid, OrderLinkState> LinkStates => Links.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(':')).ToDictionary(l => Guid.Parse(l[0]), l => Enum.Parse<OrderLinkState>(l[2]));
    }

    private static async Task<StoredPlay> Stored(Harness h, Guid playId)
    {
        await using var read = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(h.Db.Database.GetConnectionString()).Options,
            new CoreOwner(h.Db.CurrentOwnerId));
        var play = await read.Plays.AsNoTracking().SingleAsync(p => p.Id == playId);
        var links = await read.OrderLinks.AsNoTracking().Where(l => l.PlayId == playId).OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id).ToListAsync();
        return new(await read.PlanRevisions.CountAsync(r => r.PlayId == playId), play.Plan, play.Version,
            string.Join(';', links.Select(l => $"{l.Id}:{l.ImportedOrderId}:{l.State}")));
    }

    private static string Plan(string stored) => PlayDocuments.Serialize(PlayDocuments.Read(stored));

    /// <summary>The venue moved the linked stop to 101: one revision moves the level to the order, the link stays.</summary>
    [PostgresFact]
    public async Task A_drifted_linked_stop_rebinds_to_the_observed_order_price_in_one_revision()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await OpenPlay(h, account);
        h.Orders.Orders[1] = Order("stop", "sell", 100m, trigger: 101m, type: "Stop Market", reduceOnly: true);
        var execution = await h.CheckAsync(play.Summary.Id);
        var stop = execution.Links.Single(l => l.Role == "stop");
        Assert.Equal("101", stop.Order!.TriggerPrice);
        var before = await Stored(h, play.Summary.Id);

        var rebound = await h.Execution.RebindAsync(play.Summary.Id, stop.Id, new(play.Summary.Version, "stop", "101", "Trailed at the venue"), default);
        Assert.Equal(new PlanExit("stop-1", "price", "101", "100"), rebound.Plan.Entries[0].Stops[0]);
        Assert.Equal((2, play.Summary.Version + 1), (rebound.Summary.PlanRevision, rebound.Summary.Version));
        var after = await Stored(h, play.Summary.Id);
        Assert.Equal((before.Revisions + 1, before.Links), (after.Revisions, after.Links));
        var history = await h.Plays.HistoryAsync(play.Summary.Id, default);
        Assert.Equal(("Trailed at the venue", "101"), (history.Revisions[^1].Reason, history.Revisions[^1].Plan.Entries[0].Stops[0].Value));
        // The plan level is the order's price now; the target and the entry are untouched.
        Assert.Equal(PlayDocuments.Serialize(TestPlays.Plan(stop: "101")), Plan(after.Plan));
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RebindAsync(play.Summary.Id, stop.Id,
            new(rebound.Summary.Version, "stop", "101", "Again"), default))).StatusCode);
    }

    [PostgresFact]
    public async Task A_stale_play_version_or_a_changed_order_price_conflicts_and_writes_nothing()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await OpenPlay(h, account);
        h.Orders.Orders[1] = Order("stop", "sell", 96m, trigger: 97m, type: "Stop Market", reduceOnly: true);
        var stop = (await h.CheckAsync(play.Summary.Id)).Links.Single(l => l.Role == "stop");
        var before = await Stored(h, play.Summary.Id);

        foreach (var (request, message) in new[]
        {
            (new RebindLevelRequest(play.Summary.Version - 1, "stop", "97", "Trailed"), "This Play changed elsewhere. Reload it before saving again."),
            (new RebindLevelRequest(play.Summary.Version, "stop", "96.5", "Trailed"), "The order's price changed since it was observed. Check again before rebinding."),
        })
        {
            var error = await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RebindAsync(play.Summary.Id, stop.Id, request, default));
            Assert.Equal((409, message), (error.StatusCode, error.Message));
            Assert.Equal(before, await Stored(h, play.Summary.Id));
        }
        // The venue moves the order again after the owner saw 97: the old observation no longer rebinds.
        h.Orders.Orders[1] = Order("stop", "sell", 97m, trigger: 98m, type: "Stop Market", reduceOnly: true);
        await h.CheckAsync(play.Summary.Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RebindAsync(play.Summary.Id, stop.Id,
            new(play.Summary.Version, "stop", "97", "Trailed"), default))).StatusCode);
        Assert.Equal(before, await Stored(h, play.Summary.Id));
        // Malformed requests are refused before any read.
        foreach (var request in new[] { new RebindLevelRequest(play.Summary.Version, "stop", "98", " "), new RebindLevelRequest(play.Summary.Version, "stop", "9.8e1", "Trailed") })
            Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RebindAsync(play.Summary.Id, stop.Id, request, default))).StatusCode);
        Assert.Equal(before, await Stored(h, play.Summary.Id));
    }

    /// <summary>
    /// The observed order may be another one: the old link is dismissed and the new order linked to the level, in the
    /// same unit as the revision. A refused rebind (a conflicting order, a target on the wrong side) changes nothing.
    /// </summary>
    [PostgresFact]
    public async Task A_rebind_to_another_order_replaces_the_link_atomically()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await OpenPlay(h, account);
        // Not reduce-only, so it is no unplanned exit either: only the owner's rebind links it.
        h.Orders.Orders.AddRange([Order("new-stop", "sell", 95m, trigger: 96m, type: "Stop Market", placedLater: 5),
            Order("buy-stop", "buy", 96m, trigger: 96m, type: "Stop Market", placedLater: 5),
            Order("target", "sell", 110m, reduceOnly: true, placedLater: 5)]);
        var execution = await h.CheckAsync(play.Summary.Id);
        var stop = execution.Links.Single(l => l.Role == "stop");
        var target = execution.Links.Single(l => l.Role == "target");
        var before = await Stored(h, play.Summary.Id);

        var conflict = await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RebindAsync(play.Summary.Id, stop.Id,
            new(play.Summary.Version, "buy-stop", "96", "Replaced at the venue"), default));
        Assert.Equal((409, "A buy order cannot be an exit of a long play."), (conflict.StatusCode, conflict.Message));
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RebindAsync(play.Summary.Id, stop.Id,
            new(play.Summary.Version, "target", "110", "Replaced at the venue"), default))).StatusCode);
        Assert.Equal(before, await Stored(h, play.Summary.Id));
        // The target order moved below the filled entry: a long target there is refused, so neither plan nor link changes.
        h.Orders.Orders[^1] = Order("target", "sell", 99m, reduceOnly: true, placedLater: 5);
        await h.CheckAsync(play.Summary.Id);
        var wrongSide = await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RebindAsync(play.Summary.Id, target.Id,
            new(play.Summary.Version, "target", "99", "Moved at the venue"), default));
        Assert.Equal((400, "Entry 1: a long target goes above the entry price."), (wrongSide.StatusCode, wrongSide.Message));
        Assert.Equal(before, await Stored(h, play.Summary.Id));

        var rebound = await h.Execution.RebindAsync(play.Summary.Id, stop.Id, new(play.Summary.Version, "new-stop", "96", "Replaced at the venue"), default);
        Assert.Equal("96", rebound.Plan.Entries[0].Stops[0].Value);
        var after = await Stored(h, play.Summary.Id);
        Assert.Equal(before.Revisions + 1, after.Revisions);
        Assert.Equal(OrderLinkState.Dismissed, after.LinkStates[stop.Id]);
        execution = await h.Execution.GetAsync(play.Summary.Id, default);
        var replaced = execution.Links.Single(l => l.Role == "stop");
        Assert.Equal(("new-stop", "stop-1", "owner"), (replaced.Order!.OrderId, replaced.LevelId, replaced.Source));
        Assert.DoesNotContain(execution.Links, l => l.Order?.OrderId == "stop");
    }

    /// <summary>Fails the unit's first command that inserts a link, after the revision and the dismissal are staged in it.</summary>
    private sealed class FailLinkInsert : DbCommandInterceptor
    {
        public int Failed { get; private set; }
        private void Fail(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO play_order_links", StringComparison.Ordinal)) return;
            Failed++;
            throw new InvalidOperationException("Simulated failure while linking the observed order.");
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>The revision, the dismissal and the new link commit together or not at all.</summary>
    [PostgresFact]
    public async Task A_rebind_that_fails_while_linking_writes_no_revision_and_keeps_the_old_link()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await OpenPlay(h, account);
        h.Orders.Orders.Add(Order("new-stop", "sell", 95m, trigger: 96m, type: "Stop Market", placedLater: 5));
        var stop = (await h.CheckAsync(play.Summary.Id)).Links.Single(l => l.Role == "stop");
        var before = await Stored(h, play.Summary.Id);

        var fail = new FailLinkInsert();
        await using (var db = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString)
            .AddInterceptors(fail).Options, new CoreOwner(h.Db.CurrentOwnerId)))
        {
            var failing = new PlayExecutionService(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)),
                TestVenues.With(h.Orders), TimeProvider.System, TestUnitOfWork.For(db));
            await Assert.ThrowsAnyAsync<Exception>(() => failing.RebindAsync(play.Summary.Id, stop.Id,
                new(play.Summary.Version, "new-stop", "96", "Replaced at the venue"), default));
        }
        Assert.Equal(1, fail.Failed);
        Assert.Equal(before, await Stored(h, play.Summary.Id));
        Assert.Equal("stop", (await h.Execution.GetAsync(play.Summary.Id, default)).Links.Single(l => l.Role == "stop").Order!.OrderId);
    }

    /// <summary>
    /// Review finding: an Open long saved a stop at 105 while its entry was filled, then the owner unlinked the entry. The
    /// stop is unchanged from the stored plan, so a title-only save and a rebind of another level still succeed; a new
    /// stop beyond the now unfilled entry is still refused.
    /// </summary>
    [PostgresFact]
    public async Task Unlinking_a_filled_entry_keeps_a_stored_beyond_entry_stop_saveable()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var draft = await h.Plays.CreateAsync(new CreatePlayRequest(account, "BTC", "venue", "Breakout", TestPlays.Plan()), default);
        var play = await h.Plays.ChangeStatusAsync(draft.Summary.Id, new(draft.Summary.Version, "planned"), default);
        h.Orders.Orders.AddRange([Order("entry", "buy", 100m, "filled"), Order("target", "sell", 110m, reduceOnly: true)]);
        h.Reader.Result = h.Reader.Result with { Fills = [Fill("f-entry", "entry", "Open Long", 100m)] };
        var execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal("open", execution.Status);
        Assert.Equal(["entry", "target"], execution.Links.Select(l => l.Role));
        play = await h.Plays.UpdateAsync(play.Summary.Id, Revise(await h.Plays.GetAsync(play.Summary.Id, default), TestPlays.Plan(stop: "105"), "Lock in +5"), default);
        await h.Execution.UnlinkAsync(play.Summary.Id, execution.Links.Single(l => l.Role == "entry").Id, default);
        play = await h.Plays.GetAsync(play.Summary.Id, default);

        // Before the fix every save failed with "a long stop goes below the entry price", even a title.
        play = await h.Plays.UpdateAsync(play.Summary.Id, Revise(play, play.Plan, null) with { Title = "Renamed", Review = "Still holding" }, default);
        Assert.Equal(("Renamed", "105"), (play.Summary.Title, play.Plan.Entries[0].Stops[0].Value));
        h.Orders.Orders[1] = Order("target", "sell", 112m, reduceOnly: true);
        var target = (await h.CheckAsync(play.Summary.Id)).Links.Single(l => l.Role == "target");
        play = await h.Execution.RebindAsync(play.Summary.Id, target.Id, new(play.Summary.Version, "target", "112", "Target moved at the venue"), default);
        Assert.Equal(("112", "105"), (play.Plan.Entries[0].Targets[0].Value, play.Plan.Entries[0].Stops[0].Value));

        // The entry has no fill now: a changed stop beyond it is checked again.
        var error = await Assert.ThrowsAsync<ProblemException>(() => h.Plays.UpdateAsync(play.Summary.Id,
            Revise(play, TestPlays.Plan(stop: "106") with { Entries = [TestPlays.Plan(stop: "106").Entries[0] with { Targets = play.Plan.Entries[0].Targets }] }, "Trail"), default));
        Assert.Equal((400, "Entry 1: a long stop goes below the entry price."), (error.StatusCode, error.Message));
    }

    /// <summary>Review finding: replacing a link whose order already filled would drop those exits from the Play.</summary>
    [PostgresFact]
    public async Task A_rebind_does_not_replace_a_link_whose_order_has_fills()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await OpenPlay(h, account);
        // Half of the stop executed and the rest still rests; the owner places another stop for the remainder.
        h.Orders.Orders[1] = new("stop", "BTC", "sell", "Stop Market", 94m, 95m, true, false, 1m, 0.5m, DateTimeOffset.UtcNow,
            VenueOrderStatus.Open, "open", DateTimeOffset.UtcNow);
        h.Orders.Orders.Add(Order("new-stop", "sell", 95m, trigger: 96m, type: "Stop Market", placedLater: 5));
        h.Reader.Result = h.Reader.Result with { Fills = [.. h.Reader.Result.Fills, Fill("f-stop", "stop", "Close Long", 95m, pnl: -2.5m, quantity: 0.5m)] };
        var stop = (await h.CheckAsync(play.Summary.Id)).Links.Single(l => l.Role == "stop");
        Assert.Equal("0.5", stop.FilledQuantity);
        var before = await Stored(h, play.Summary.Id);
        var error = await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RebindAsync(play.Summary.Id, stop.Id,
            new(play.Summary.Version, "new-stop", "96", "Replaced the rest at the venue"), default));
        Assert.Equal((409, "Order stop has fills that count for this play, so its link cannot be replaced."), (error.StatusCode, error.Message));
        Assert.Equal(before, await Stored(h, play.Summary.Id));
    }

    /// <summary>Review finding: a level with two linked orders moves through neither, as a save refuses it.</summary>
    [PostgresFact]
    public async Task A_rebind_refuses_a_level_another_linked_order_still_references()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await OpenPlay(h, account);
        h.Orders.Orders.Add(Order("stop-b", "sell", 94m, trigger: 95m, type: "Stop Market", reduceOnly: true, placedLater: 5));
        var links = (await h.CheckAsync(play.Summary.Id)).Links.Where(l => l.Role == "stop").ToList();
        Assert.Equal(["stop", "stop-b"], links.Select(l => l.Order!.OrderId).Order());
        h.Orders.Orders[1] = Order("stop", "sell", 96m, trigger: 97m, type: "Stop Market", reduceOnly: true);
        await h.CheckAsync(play.Summary.Id);
        var before = await Stored(h, play.Summary.Id);
        var error = await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RebindAsync(play.Summary.Id,
            links.Single(l => l.Order!.OrderId == "stop").Id, new(play.Summary.Version, "stop", "97", "Trailed one of two"), default));
        Assert.Equal((409, "Order stop-b still references this plan level. Unlink it before changing or removing the level."), (error.StatusCode, error.Message));
        Assert.Equal(before, await Stored(h, play.Summary.Id));
    }
}
