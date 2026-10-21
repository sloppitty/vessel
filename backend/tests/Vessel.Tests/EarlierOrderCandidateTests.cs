using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 4 (D-20): a fast entry journaled minutes later was never offered, so the Play stayed Planned while its
/// position was open. Compatible retained orders placed in the lookback before the Play was created are now offered for
/// the owner to link, labelled as placed before it was saved; the automatic matcher still never reaches back.
/// </summary>
public sealed class EarlierOrderCandidateTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-01T12:00:00Z");

    private static ImportedOrder Order(Account account, string id, decimal price, DateTimeOffset placed, string status = "filled") => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ExternalOrderId = id, ContractId = "BTC", Side = "buy",
        OrderType = "Limit", LimitPrice = price, OriginalSize = 1, RemainingSize = status == "open" ? 1 : 0, PlacedAtUtc = placed,
        Status = status, VenueStatus = status, StatusAtUtc = placed, ObservedAtUtc = placed,
    };

    private static ImportedFill Fill(Account account, string orderId, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = "BTC", SourceFillId = orderId + "-fill",
        ExternalOrderId = orderId, Side = "buy", Direction = "", PositionEffect = "open", Price = 100, Quantity = 1, Fee = 0, FeeToken = "USDC",
        ClosedPnlUsd = 0, OccurredAtUtc = at, TransactionHash = "",
    };

    private static PlayExecutionService Service(VesselDbContext db, PlayExecutionOptions? options = null) =>
        new(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)), TestVenues.With(), TimeProvider.System,
            TestUnitOfWork.For(db), executionOptions: options);

    /// <summary>A Play created at noon and planned a minute later, with orders on its entry level placed around it.</summary>
    private static async Task<(CoreDatabase Database, Guid Owner, Account Account, Guid Play)> Seed()
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Trading");
        await using var db = database.Context(owner);
        db.Accounts.Add(account);
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), Created);
        var (change, revision) = play.MarkPlanned(Created.AddMinutes(1));
        db.Plays.Add(play); db.Add(change); db.Add(revision);
        db.Orders.AddRange(
            // The fast entry: placed and filled two minutes before the Play was saved.
            Order(account, "fast", 100, Created.AddMinutes(-2)),
            // Placed before the Play, still resting, filled only after planning.
            Order(account, "resting", 100, Created.AddHours(-3), "open"),
            // Older than the lookback.
            Order(account, "old", 100, Created.AddHours(-30)),
            // A cancelled order that never filled is not a candidate, as today.
            Order(account, "cancelled", 100, Created.AddMinutes(-5), "canceled"));
        db.Fills.Add(Fill(account, "fast", Created.AddMinutes(-2)));
        await db.SaveChangesAsync();
        return (database, owner, account, play.Id);
    }

    [PostgresFact]
    public async Task Earlier_orders_are_offered_for_the_owner_never_matched()
    {
        var (database, owner, account, playId) = await Seed();
        await using var _ = database;
        await using (var db = database.Context(owner))
        {
            var service = Service(db);
            // A check reconciles: the matcher keeps its rule, so nothing links and the Play stays Planned.
            await TestUnitOfWork.For(db).ExecuteAsync(async () =>
            {
                await service.ReconcileImportedAsync(account, new VenueOrderReadResult([], Created.AddMinutes(5), "test"), [], default);
                return true;
            }, default);
            var execution = await service.GetAsync(playId, default);
            Assert.Equal("planned", execution.Status);
            Assert.Empty(execution.Links);
            Assert.Empty(execution.Suggestions);
            Assert.Empty(execution.UnlinkedOrders);
            Assert.Equal(["fast", "resting"], execution.EarlierOrders.Select(o => o.OrderId));
        }
        await using (var db = database.Context(owner))
        {
            // A zero lookback offers nothing earlier, as before.
            var execution = await Service(db, new PlayExecutionOptions { EarlierOrderLookback = TimeSpan.Zero }).GetAsync(playId, default);
            Assert.Empty(execution.EarlierOrders);
        }
    }

    [PostgresFact]
    public async Task Linking_a_fast_entry_opens_the_play_and_marks_its_plan_as_written_after()
    {
        var (database, owner, _, playId) = await Seed();
        await using var _ = database;
        await using var db = database.Context(owner);
        var execution = await Service(db).LinkAsync(playId, new LinkOrderRequest("fast", "entry", "entry-1"), default);
        Assert.Equal("open", execution.Status);
        Assert.DoesNotContain(execution.EarlierOrders, o => o.OrderId == "fast");
        // The first fill precedes the plan: the existing badge rule follows the actual times (R is read at close, by its rule).
        Assert.True(execution.Receipt!.PlannedAfterExecution);
    }

    [PostgresFact]
    public async Task An_order_placed_before_the_play_but_filled_after_planning_keeps_a_contemporaneous_plan()
    {
        var (database, owner, account, playId) = await Seed();
        await using var _ = database;
        await using (var db = database.Context(owner))
        {
            await Service(db).LinkAsync(playId, new LinkOrderRequest("resting", "entry", "entry-1"), default);
            var order = db.Orders.Single(o => o.ExternalOrderId == "resting");
            order.Status = "filled"; order.VenueStatus = "filled"; order.RemainingSize = 0;
            db.Fills.Add(Fill(account, "resting", Created.AddMinutes(10)));
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(owner))
        {
            var execution = await Service(db).GetAsync(playId, default);
            // Placed before the Play, but filled after the plan was written: the plan is contemporaneous.
            Assert.False(execution.Receipt!.PlannedAfterExecution);
        }
    }

    [Fact]
    public void The_lookback_is_bounded()
    {
        new PlayExecutionOptions().Validate();
        Assert.Equal(TimeSpan.FromHours(24), new PlayExecutionOptions().EarlierOrderLookback);
        Assert.Throws<InvalidOperationException>(() => new PlayExecutionOptions { EarlierOrderLookback = TimeSpan.FromDays(8) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayExecutionOptions { EarlierOrderLookback = TimeSpan.FromHours(-1) }.Validate());
    }
}
