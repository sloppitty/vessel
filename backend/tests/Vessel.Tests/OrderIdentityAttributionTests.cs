using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vessel.Application.Common;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Persistence.Events;

namespace Vessel.Tests;

/// <summary>
/// One account reports the same venue order ID on two contracts. Every attribution path (Activity, Diary, Sizing,
/// Review, execution integrity, lifecycle, manual linking and events) must keep the orders apart: a fill belongs to
/// the order with its account, contract and external ID. See docs/architecture/order-identity.md.
/// </summary>
public sealed class OrderIdentityAttributionTests
{
    // Seconds precision, so PostgreSQL round trips keep timestamps equal.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    [VesselEventPostgresFact]
    public async Task The_same_external_order_id_on_another_contract_never_reaches_the_linked_play()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), Now.AddHours(-6));
        var btcEntry = Order(account, "BTC", "7", "buy", 100, 1);
        var btcExit = Order(account, "BTC", "8", "sell", 110, 1);
        var ethEntry = Order(account, "ETH", "7", "buy", 2000, 1);
        var ethExit = Order(account, "ETH", "8", "sell", 2100, 1);
        var ethOnly = Order(account, "ETH", "only", "sell", 2200, 1, "open");
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            seed.Plays.Add(play);
            var planned = play.MarkPlanned(Now.AddHours(-6));
            seed.StatusChanges.Add(planned.Change); seed.PlanRevisions.Add(planned.Revision);
            seed.Orders.AddRange(btcEntry, btcExit, ethEntry, ethExit, ethOnly);
            seed.OrderLinks.AddRange(
                new PlayOrderLink(play, btcEntry, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, Now.AddHours(-5)),
                new PlayOrderLink(play, btcExit, OrderLinkRole.Exit, null, null, OrderLinkState.Linked, OrderLinkSource.Owner, Now.AddHours(-5)));
            await seed.SaveChangesAsync();
        }

        // The ETH round trip under the same venue IDs neither opens nor closes the BTC Play (matcher and transitions).
        await Insert(database, owner, Fill(account, "ETH", "7", "eth-in", "buy", "open", 1, 2000, -4),
            Fill(account, "ETH", "8", "eth-out", "sell", "close", 1, 2100, -3, pnl: 100));
        await Reconcile(database, owner, account);
        Assert.Equal(PlayStatus.Planned, await Status(database, owner, play.Id));

        await Insert(database, owner, Fill(account, "BTC", "7", "btc-in", "buy", "open", 1, 100, -2, fee: 0.05m));
        await Reconcile(database, owner, account);
        Assert.Equal(PlayStatus.Open, await Status(database, owner, play.Id));

        // Manual linking resolves the venue ID within the Play's contract: an ETH-only ID is unknown here.
        await using (var scope = new Scope(database, owner))
        {
            var error = await Assert.ThrowsAsync<ProblemException>(() =>
                Execution(scope).LinkAsync(play.Id, new LinkOrderRequest("only", "exit"), default));
            Assert.Equal(404, error.StatusCode);
            // "7" resolves to the BTC order, which is already linked, not to the unlinked ETH order.
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() =>
                Execution(scope).LinkAsync(play.Id, new LinkOrderRequest("7", "entry", "entry-1"), default))).StatusCode);
            var execution = await Execution(scope).GetAsync(play.Id, default);
            Assert.Equal(("1", "0"), (execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity));
            // The filled BTC exit order has no BTC fill yet; the ETH fill with the same venue ID does not complete it.
            Assert.Equal(["Order 8 is filled at the venue, but not all of its executions are retained yet."], execution.Issues);
            Assert.Equal(["btc-in"], execution.Links.Single(l => l.Role == "entry").Fills.Select(f => f.SourceFillId));
            Assert.Empty(execution.Links.Single(l => l.Role == "exit").Fills);
        }

        await Insert(database, owner, Fill(account, "BTC", "8", "btc-out", "sell", "close", 1, 110, -1, pnl: 10, fee: 0.055m));
        await Reconcile(database, owner, account);
        Assert.Equal(PlayStatus.Closed, await Status(database, owner, play.Id));

        await using var db = database.Context(owner);
        // The matcher linked nothing on ETH; only the two seeded BTC links exist.
        Assert.Equal(new[] { btcEntry.Id, btcExit.Id }.Order(), (await db.OrderLinks.AsNoTracking().ToListAsync()).Select(l => l.ImportedOrderId).Order());
        var btcFills = await db.Fills.Where(f => f.ContractId == "BTC").Select(f => f.Id).ToListAsync();
        var ethFills = await db.Fills.Where(f => f.ContractId == "ETH").Select(f => f.Id).ToListAsync();

        // Closed-event totals count only BTC fills.
        var closed = Assert.Single((await EventReads.ReadAsync(db, 0, 200, default)).Events, e => e.Type == "play.closed").Data;
        Assert.Equal(("1", "1", "10"), (closed.GetProperty("enteredQuantity").GetString(), closed.GetProperty("exitedQuantity").GetString(),
            closed.GetProperty("closedPnlUsd").GetString()));
        var fee = Assert.Single(closed.GetProperty("fees").EnumerateArray());
        Assert.Equal(("USDC", "0.105"), (fee.GetProperty("token").GetString(), fee.GetProperty("amount").GetString()));
        Assert.DoesNotContain((await EventReads.ReadAsync(db, 0, 200, default)).Events, e => e.Subject.ContractId == "ETH");

        // Activity's per-fill Play map labels only the BTC fills; the ETH operation stays unassigned.
        var facts = await new ActivityStore(db).ReadAsync(account.Id, default);
        Assert.Equal(btcFills.Order(), facts.FillPlayIds.Keys.Order());
        Assert.All(facts.FillPlayIds.Values, id => Assert.Equal(play.Id, id));
        var operations = (await ActivityTestSupport.Activity(db).PageAsync(account.Id)).Operations;
        var btcOperation = operations.Single(o => o.ContractId == "BTC");
        var ethOperation = operations.Single(o => o.ContractId == "ETH");
        Assert.Equal(play.Id, btcOperation.PlayId);
        Assert.Null(ethOperation.PlayId);
        Assert.Equal("unassigned", ethOperation.PlayAssociation);

        // Sizing facts and the Review outcome contain only the BTC fills, with the exact P&L.
        var sizing = Assert.Single(await new SizingStore(db, TestUnitOfWork.For(db)).ClosedPlaysAsync(default));
        Assert.Equal(btcFills.Order(), sizing.Fills.Select(f => f.Fill.Id).Order());
        Assert.Empty(sizing.IncompleteOrders ?? []);
        var review = await new ReviewService(new ReviewStore(db, new SizingStore(db, TestUnitOfWork.For(db))), TimeProvider.System)
            .GetAsync(new(Period: "all"), default);
        var scored = Assert.Single(review.Plays);
        Assert.Equal((play.Id, "9.895"), (scored.Id, scored.Net));
        Assert.Equal(ethFills.Count, review.Coverage.UnassignedFills);

        // The ETH operation is not blocked by the BTC links; the BTC one is; their fingerprints differ.
        var ethPreview = await DiaryPostgresTests.Service(db).PreviewAsync(account.Id, ethOperation.Id, default);
        Assert.True(ethPreview.Eligible, string.Join(" ", ethPreview.Reasons));
        var btcPreview = await DiaryPostgresTests.Service(db).PreviewAsync(account.Id, btcOperation.Id, default);
        Assert.False(btcPreview.Eligible);
        Assert.Contains("This operation contains existing or mixed Play associations.", btcPreview.Reasons);
        Assert.NotEqual(ethPreview.Fingerprint, btcPreview.Fingerprint);
        // The fingerprint is stable across reads.
        Assert.Equal(ethPreview.Fingerprint, (await DiaryPostgresTests.Service(db).PreviewAsync(account.Id, ethOperation.Id, default)).Fingerprint);

        // Creation links the ETH orders' internal IDs, never the BTC orders with the same venue IDs.
        var retrospective = await DiaryPostgresTests.Service(db).CreateRetrospectiveAsync(
            new(Guid.NewGuid(), account.Id, ethOperation.Id, ethPreview.Fingerprint, "ETH"), default);
        Assert.Equal(new[] { ethEntry.Id, ethExit.Id }.Order(), (await db.OrderLinks.AsNoTracking().Where(l => l.PlayId == retrospective.Summary.Id)
            .ToListAsync()).Select(l => l.ImportedOrderId).Order());
    }

    [PostgresFact]
    public async Task Missing_executions_count_only_the_orders_own_contract_fills()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        var open = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), Now.AddHours(-6));
        var closed = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), Now.AddHours(-6));
        // Each BTC order is filled at size 2 with only 1 retained; the ETH order with the same venue ID retained the other 1.
        var openEntry = Order(account, "BTC", "7", "buy", 100, 2);
        var closedEntry = Order(account, "BTC", "17", "buy", 100, 2);
        var closedExit = Order(account, "BTC", "18", "sell", 110, 1);
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            seed.Plays.AddRange(open, closed);
            foreach (var play in new[] { open, closed })
            {
                var planned = play.MarkPlanned(Now.AddHours(-6));
                seed.StatusChanges.Add(planned.Change); seed.PlanRevisions.Add(planned.Revision);
                seed.StatusChanges.Add(play.MarkOpen("Entry fill.", Now.AddHours(-2)));
            }
            seed.StatusChanges.Add(closed.MarkClosed("Exits covered entries.", Now.AddHours(-1)));
            seed.Orders.AddRange(openEntry, closedEntry, closedExit, Order(account, "ETH", "7", "buy", 2000, 1), Order(account, "ETH", "17", "buy", 2000, 1));
            seed.OrderLinks.AddRange(
                new PlayOrderLink(open, openEntry, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, Now.AddHours(-5)),
                new PlayOrderLink(closed, closedEntry, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, Now.AddHours(-5)),
                new PlayOrderLink(closed, closedExit, OrderLinkRole.Exit, null, null, OrderLinkState.Linked, OrderLinkSource.Owner, Now.AddHours(-5)));
            seed.Fills.AddRange(
                Fill(account, "BTC", "7", "a", "buy", "open", 1, 100, -2), Fill(account, "ETH", "7", "b", "buy", "open", 1, 2000, -2),
                Fill(account, "BTC", "17", "c", "buy", "open", 1, 100, -2), Fill(account, "ETH", "17", "d", "buy", "open", 1, 2000, -2),
                Fill(account, "BTC", "18", "e", "sell", "close", 1, 110, -1, pnl: 10));
            await seed.SaveChangesAsync();
        }

        await using var db = database.Context(owner);
        var execution = await new PlayExecutionService(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)),
            TestVenues.With(), TimeProvider.System, TestUnitOfWork.For(db)).GetAsync(open.Id, default);
        Assert.Equal("1", execution.Totals.EnteredQuantity);
        Assert.Contains("Order 7 is filled at the venue, but not all of its executions are retained yet.", execution.Issues);

        var facts = Assert.Single(await new SizingStore(db, TestUnitOfWork.For(db)).ClosedPlaysAsync(default));
        Assert.Equal(["17"], facts.IncompleteOrders);
        Assert.Equal(["c", "e"], facts.Fills.Select(f => f.Fill.SourceFillId).Order());
        Assert.False(ExecutionIntegrity.Settled(facts));
    }

    [VesselEventPostgresFact]
    public async Task Execution_filled_events_for_one_venue_id_on_two_contracts_are_both_emitted()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var at = Now.AddMinutes(10);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        var btc = TestPlays.Create(account, new("hyperliquid", "BTC"));
        var eth = TestPlays.Create(account, new("hyperliquid", "ETH"));
        var btcOrder = Order(account, "BTC", "7", "buy", 100, 1, "open");
        var ethOrder = Order(account, "ETH", "7", "buy", 100, 1, "open");
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            seed.Plays.AddRange(btc, eth);
            foreach (var play in new[] { btc, eth })
            {
                var planned = play.MarkPlanned(Now.AddHours(-3));
                seed.StatusChanges.Add(planned.Change); seed.PlanRevisions.Add(planned.Revision);
            }
            seed.Orders.AddRange(btcOrder, ethOrder);
            seed.OrderLinks.AddRange(
                new PlayOrderLink(btc, btcOrder, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, Now),
                new PlayOrderLink(eth, ethOrder, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, Now));
            await seed.SaveChangesAsync();
        }
        await using var scope = new Scope(database, owner);
        await scope.Work.ExecuteAsync(async () => { await scope.Store.EnsureMonitoringBaselineAsync(account.Id, default); return true; }, default);
        await scope.Work.ExecuteAsync(() =>
        {
            // Equal cumulative quantities: a dedup key on the venue ID alone would collide.
            scope.Db.Fills.AddRange(Fill(account, "BTC", "7", "f-btc", "buy", "open", 0.5m, 100, at),
                Fill(account, "ETH", "7", "f-eth", "buy", "open", 0.5m, 100, at));
            return Task.FromResult(true);
        }, default);

        var filled = (await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events.Where(e => e.Type == "execution.filled").ToList();
        Assert.Equal(2, filled.Count);
        Assert.Equal(new[] { $"order:{account.Id}:{btcOrder.Id}:filled:0.5", $"order:{account.Id}:{ethOrder.Id}:filled:0.5" }
            .Order(StringComparer.Ordinal), filled.Select(e => e.DedupKey).Order(StringComparer.Ordinal));
        Assert.Equal([("BTC", btc.Id, "f-btc"), ("ETH", eth.Id, "f-eth")], filled.Select(e =>
            (e.Subject.ContractId!, e.Subject.PlayId!.Value, Assert.Single(e.Source.FillIds!))).OrderBy(x => x.Item1, StringComparer.Ordinal));
        Assert.All(filled, e => Assert.Equal(["7"], e.Source.OrderIds!));
    }

    [VesselEventPostgresFact]
    public async Task Fills_retained_before_their_order_emit_exactly_one_execution_filled_once_linked()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var at = Now.AddMinutes(10);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            seed.Plays.Add(play);
            var planned = play.MarkPlanned(Now.AddHours(-3));
            seed.StatusChanges.Add(planned.Change); seed.PlanRevisions.Add(planned.Revision);
            await seed.SaveChangesAsync();
        }
        await using var scope = new Scope(database, owner);
        await scope.Work.ExecuteAsync(async () => { await scope.Store.EnsureMonitoringBaselineAsync(account.Id, default); return true; }, default);
        int Filled(IEnumerable<VesselEvent> events, string order) =>
            events.Count(e => e.Type == "execution.filled" && e.Source.OrderIds!.Contains(order));

        // Same unit: the fill is saved first, then its order is retained and linked.
        await scope.Work.ExecuteAsync(async () =>
        {
            scope.Db.Fills.Add(Fill(account, "BTC", "same-unit", "s1", "buy", "open", 1, 100, at));
            await scope.Db.SaveChangesAsync();
            var order = Order(account, "BTC", "same-unit", "buy", 100, 1);
            scope.Db.Orders.Add(order);
            await scope.Db.SaveChangesAsync();
            scope.Db.OrderLinks.Add(new PlayOrderLink(await scope.Db.Plays.SingleAsync(p => p.Id == play.Id), order, OrderLinkRole.Entry,
                "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, Now));
            return true;
        }, default);
        // One unit retains the fill and then its order; a later unit links it.
        var later = Order(account, "BTC", "later", "buy", 100, 1);
        await scope.Work.ExecuteAsync(async () =>
        {
            scope.Db.Fills.Add(Fill(account, "BTC", "later", "l1", "buy", "open", 1, 100, at));
            await scope.Db.SaveChangesAsync();
            scope.Db.Orders.Add(later);
            return true;
        }, default);
        Assert.Equal(0, Filled((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events, "later"));
        await scope.Work.ExecuteAsync(async () =>
        {
            scope.Db.OrderLinks.Add(new PlayOrderLink(await scope.Db.Plays.SingleAsync(p => p.Id == play.Id), later, OrderLinkRole.Entry,
                "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, Now));
            return true;
        }, default);
        // A refresh that changes nothing emits nothing more.
        await scope.Work.ExecuteAsync(() => Task.FromResult(true), default);

        var events = (await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events;
        Assert.Equal(1, Filled(events, "same-unit"));
        Assert.Equal(1, Filled(events, "later"));
    }

    /// <summary>
    /// Phase 2, item 6b: fills a backfill adds are history. Claimed with them, they draft no execution event in their unit,
    /// nor later when their order is linked, while an ordinary import of the same order's next fill still does.
    /// </summary>
    [VesselEventPostgresFact]
    public async Task Phase2_backfilled_fills_never_emit_an_execution_event_even_once_their_order_is_linked()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var at = Now.AddMinutes(10);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        var linked = Order(account, "BTC", "linked", "buy", 100, 2);
        var unlinked = Order(account, "BTC", "unlinked", "buy", 100, 1);
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            seed.Plays.Add(play);
            var planned = play.MarkPlanned(Now.AddHours(-3));
            seed.StatusChanges.Add(planned.Change); seed.PlanRevisions.Add(planned.Revision);
            seed.Orders.AddRange(linked, unlinked);
            seed.OrderLinks.Add(new PlayOrderLink(play, linked, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, Now));
            await seed.SaveChangesAsync();
        }
        await using var scope = new Scope(database, owner);
        await scope.Work.ExecuteAsync(async () => { await scope.Store.EnsureMonitoringBaselineAsync(account.Id, default); return true; }, default);
        int Filled(IEnumerable<VesselEvent> events, string order) =>
            events.Count(e => e.Type == "execution.filled" && e.Source.OrderIds!.Contains(order));

        // The backfill's unit: fills after the monitoring baseline, on a linked and an unlinked order, claimed as history.
        await scope.Work.ExecuteAsync(async () =>
        {
            var backfilled = new[] { Fill(account, "BTC", "linked", "b1", "buy", "open", 1, 100, at),
                Fill(account, "BTC", "unlinked", "b2", "buy", "open", 1, 100, at) };
            scope.Db.Fills.AddRange(backfilled);
            await scope.Db.SaveChangesAsync();
            await scope.Store.ClaimHistoricalFillsAsync(backfilled, default);
            return true;
        }, default);
        Assert.Equal(0, Filled((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events, "linked"));
        // Linking the other order later still announces nothing for its backfilled fill.
        await scope.Work.ExecuteAsync(async () =>
        {
            scope.Db.OrderLinks.Add(new PlayOrderLink(await scope.Db.Plays.SingleAsync(p => p.Id == play.Id), unlinked, OrderLinkRole.Entry,
                "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, Now));
            return true;
        }, default);
        // An ordinary import of the linked order's next fill is a fresh execution.
        await scope.Work.ExecuteAsync(() =>
        {
            scope.Db.Fills.Add(Fill(account, "BTC", "linked", "n1", "buy", "open", 1, 100, at.AddMinutes(1)));
            return Task.FromResult(true);
        }, default);
        var events = (await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events;
        Assert.Equal(0, Filled(events, "unlinked"));
        var fresh = Assert.Single(events, e => e.Type == "execution.filled");
        Assert.Equal(["n1"], fresh.Source.FillIds!);
    }

    private static ImportedOrder Order(Account account, string contract, string id, string side, decimal price, decimal size,
        string status = "filled") => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = contract, ExternalOrderId = id,
        Side = side, OrderType = "Limit", LimitPrice = price, OriginalSize = size, RemainingSize = status == "open" ? size : 0,
        PlacedAtUtc = Now.AddHours(-5), Status = status, VenueStatus = status, StatusAtUtc = Now.AddHours(-5), ObservedAtUtc = Now.AddHours(-5)
    };

    private static ImportedFill Fill(Account account, string contract, string order, string id, string side, string effect, decimal quantity,
        decimal price, int hours, decimal pnl = 0, decimal fee = 0) =>
        Fill(account, contract, order, id, side, effect, quantity, price, Now.AddHours(hours), pnl, fee);

    private static ImportedFill Fill(Account account, string contract, string order, string id, string side, string effect, decimal quantity,
        decimal price, DateTimeOffset at, decimal pnl = 0, decimal fee = 0) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = contract, SourceFillId = id,
        ExternalOrderId = order, Side = side, Direction = effect, PositionEffect = effect, Price = price, Quantity = quantity,
        Fee = fee, FeeToken = "USDC", ClosedPnlUsd = pnl, OccurredAtUtc = at, TransactionHash = "synthetic"
    };

    private static async Task Insert(CoreDatabase database, Guid owner, params ImportedFill[] fills)
    {
        await using var scope = new Scope(database, owner);
        await scope.Work.ExecuteAsync(() => { scope.Db.Fills.AddRange(fills); return Task.FromResult(true); }, default);
    }

    private static PlayExecutionService Execution(Scope scope) => new(new PlayStore(scope.Db, scope.Unit),
        new ExecutionStore(scope.Db, scope.Unit), TestVenues.With(), TimeProvider.System, scope.Unit);

    private static async Task Reconcile(CoreDatabase database, Guid owner, Account account)
    {
        await using var scope = new Scope(database, owner);
        await scope.Work.ExecuteAsync(async () =>
        {
            await Execution(scope).ReconcileImportedAsync(account, new VenueOrderReadResult([], Now, "test"), [], default);
            return true;
        }, default);
    }

    private static async Task<PlayStatus> Status(CoreDatabase database, Guid owner, Guid playId)
    {
        await using var db = database.Context(owner);
        return (await db.Plays.AsNoTracking().SingleAsync(p => p.Id == playId)).Status;
    }

    /// <summary>A context with event capture, so lifecycle and fill events are written in the same transaction.</summary>
    private sealed class Scope : IAsyncDisposable
    {
        public VesselDbContext Db { get; }
        public VesselEventStore Store { get; }
        public UnitOfWork Unit { get; }
        public UnitOfWork Work => Unit;
        public Scope(CoreDatabase database, Guid owner)
        {
            var capture = new VesselEventMutationCapture();
            Db = new(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString)
                .AddInterceptors(capture).Options, new CoreOwner(owner));
            Store = new(Db, capture, new NullOutbox(), Options.Create(new VesselEventSettings()), NullLogger<VesselEventStore>.Instance);
            Unit = new(Db, [Store]);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class NullOutbox : IVesselEventOutbox
    {
        public Task StageAsync(VesselEvent message, CancellationToken ct) => Task.CompletedTask;
        public Task StageAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }
}
