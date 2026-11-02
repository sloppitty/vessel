using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Monitoring;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// A venue order ID is unique only within its account and contract. The monitor matches fresh venue observations by
/// (contract, external ID) and retained watches and assignments by internal IDs, so the same external ID on another
/// contract never admits, satisfies or hides anything.
/// </summary>
public sealed class MonitorOrderIdentityTests
{
    private const string Shared = "shared-oid";

    [PostgresFact]
    public async Task Same_external_id_resting_on_two_contracts_admits_only_the_play_on_the_reported_contract()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        var btc = TerminalPlay(f, "BTC", Shared, OrderLinkRole.Entry);
        var eth = TerminalPlay(f, "ETH", Shared, OrderLinkRole.Entry);
        await database.Seed(f);
        // Both watches already ended, so only fresh resting evidence can admit either terminal Play.
        await using (var seed = database.Scope(f))
        {
            foreach (var link in f.Store.Links.Where(x => x.PlayId == btc.Id || x.PlayId == eth.Id))
                seed.Db.Add(new MonitorExitWatch { LinkId = link.Id, OwnerId = link.OwnerId, AccountId = link.AccountId, PlayId = link.PlayId,
                    LinkRevisionAtUtc = link.UpdatedAtUtc, StartedAtUtc = f.Clock.Now.AddHours(-2), EndedAtUtc = f.Clock.Now.AddHours(-1),
                    Outcome = MonitorWatchOutcomes.Absent });
            await seed.Db.SaveChangesAsync();
        }

        await using (var load = database.Scope(f))
        {
            async Task<Guid[]> Admitted(params OrderKey[] resting) => await load.Work.ExecuteAsync(async () =>
                (await load.Store.LoadAsync(f.Account.Id, f.Clock.Now, resting, default)).Plays.Select(x => x.Id).ToArray(), default);
            var onEth = await Admitted(new OrderKey(f.Account.Id, "ETH", Shared));
            Assert.Contains(eth.Id, onEth);
            Assert.DoesNotContain(btc.Id, onEth);
            var onBtc = await Admitted(new OrderKey(f.Account.Id, "BTC", Shared));
            Assert.Contains(btc.Id, onBtc);
            Assert.DoesNotContain(eth.Id, onBtc);
            var none = await Admitted(new OrderKey(f.Account.Id, "SOL", Shared), new OrderKey(Guid.NewGuid(), "ETH", Shared));
            Assert.DoesNotContain(btc.Id, none);
            Assert.DoesNotContain(eth.Id, none);
        }

        // End to end: the venue reports the ID resting on ETH only; only the ETH Play's orphaned entry is reported.
        MonitorEvaluation Observation() => WithEth(f.Observation(), 0,
            new VenueOrder(Shared, "ETH", "buy", "Limit", 100, null, false, false, 1, 1, f.Clock.Now.AddMinutes(-15), VenueOrderStatus.Open, "open", f.Clock.Now));
        await using (var first = database.Scope(f)) await first.Evaluate(Observation());
        f.Advance(60);
        await using var second = database.Scope(f);
        await second.Evaluate(Observation());
        var orphaned = (await second.Db.Set<PlayDiscrepancy>().AsNoTracking().ToListAsync()).Where(x => x.Kind == "entry.orphaned").ToArray();
        var row = Assert.Single(orphaned);
        Assert.Equal(eth.Id, row.PlayId);
        Assert.Equal("ETH", row.ContractId);
        Assert.Equal(DiscrepancyLifecycle.Open, row.State);
        Assert.DoesNotContain(await second.Db.Set<PlayDiscrepancy>().AsNoTracking().ToListAsync(), x => x.PlayId == btc.Id);
    }

    [PostgresFact]
    public async Task Terminal_watch_on_a_btc_order_is_neither_ended_nor_reopened_by_an_eth_order_with_the_same_id()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        var btc = TerminalPlay(f, "BTC", Shared, OrderLinkRole.Stop);
        await database.Seed(f);
        var link = f.Store.Links.Single(x => x.PlayId == btc.Id);
        await using var scope = database.Scope(f);
        var monitor = MonitorTestPaths.Store(scope.Db, scope.Work);
        async Task<MonitorExitWatch> Update(string contract, VenueOrderStatus status, bool complete)
        {
            f.Advance(1);
            var read = new VenueOrderReadResult([new VenueOrder(Shared, contract, "sell", "Stop Market", 90, 90, true, false, 1,
                status == VenueOrderStatus.Open ? 1 : 0, f.Clock.Now.AddMinutes(-15), status, status.ToString().ToLowerInvariant(), f.Clock.Now)],
                f.Clock.Now, "bounded", complete);
            await scope.Work.ExecuteAsync(async () => { await monitor.UpdateWatchesAsync(f.Account.Id, read, f.Clock.Now, default); return true; }, default);
            scope.Db.ChangeTracker.Clear();
            return await scope.Db.Set<MonitorExitWatch>().AsNoTracking().SingleAsync(x => x.LinkId == link.Id);
        }

        // Another contract's terminal status is not this order's end.
        var watch = await Update("ETH", VenueOrderStatus.Canceled, complete: false);
        Assert.Null(watch.EndedAtUtc);
        Assert.Null(watch.Outcome);
        watch = await Update("ETH", VenueOrderStatus.Filled, complete: false);
        Assert.Null(watch.Outcome);
        // The linked order's own contract ends it.
        watch = await Update("BTC", VenueOrderStatus.Canceled, complete: false);
        Assert.Equal(MonitorWatchOutcomes.Terminal, watch.Outcome);
        var ended = watch.EndedAtUtc;
        Assert.NotNull(ended);
        // Another contract's resting order with the same ID does not reopen the ended watch.
        watch = await Update("ETH", VenueOrderStatus.Open, complete: false);
        Assert.Equal(MonitorWatchOutcomes.Terminal, watch.Outcome);
        Assert.Equal(ended, watch.EndedAtUtc);
    }

    [PostgresFact]
    public async Task Unlinked_eth_fill_sharing_an_assigned_btc_orders_id_is_still_reported()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        // The fixture's BTC Play links its stop order "stop". An ETH Play is open, and a stray ETH fill reuses "stop".
        var eth = OpenPlay(f, "ETH", "eth-entry");
        var stray = AddFill(f, "ETH", "eth-stray", "stop", 1, f.Clock.Now.AddMinutes(-5));
        await database.Seed(f);
        var btcStop = f.Store.Links.Single(x => x.PlayId == f.Play.Id && x.Role == OrderLinkRole.Stop);

        await using (var load = database.Scope(f))
        {
            var facts = await load.Work.ExecuteAsync(() => load.Store.LoadAsync(f.Account.Id, f.Clock.Now, [], default), default);
            Assert.Contains(facts.Fills, x => x.Id == stray.Id);
            // SQL narrows assignments by external ID; the contract rule drops the BTC link for the ETH fill's order.
            Assert.DoesNotContain(facts.AssignmentLinks!, x => x.Id == btcStop.Id);
            Assert.True(facts.AssignmentLinksComplete);
        }

        MonitorEvaluation Observation() => WithEth(f.Observation(), 2);
        await using (var first = database.Scope(f)) await first.Evaluate(Observation());
        f.Advance(60);
        await using var second = database.Scope(f);
        await second.Evaluate(Observation());
        var rows = await second.Service.ReadAsync(f.Account.Id, null, default);
        var unlinked = Assert.Single(rows, x => x.Kind == "fill.unlinked");
        Assert.Equal("ETH", unlinked.ContractId);
        Assert.Equal("open", unlinked.State);
        var listed = Assert.Single(unlinked.Details.GetProperty("fills").EnumerateArray());
        Assert.Equal("eth-stray", listed.GetProperty("fillId").GetString());
        Assert.Equal("stop", listed.GetProperty("orderId").GetString());
        Assert.Contains(unlinked.Details.GetProperty("playIds").EnumerateArray(), x => x.GetGuid() == eth.Id);
    }

    [PostgresFact]
    public async Task Missing_executions_count_only_fills_on_the_orders_own_contract()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 2 };
        // The BTC entry order filled 2 but only 1 is retained on BTC; an ETH fill with the same ID must not complete it.
        var entry = f.Store.Orders.Single(x => x.ExternalOrderId == "entry");
        entry.OriginalSize = 2;
        var foreign = AddFill(f, "ETH", "eth-same-id", entry.ExternalOrderId, 1, f.Clock.Now.AddMinutes(-20));
        await database.Seed(f);

        await using (var load = database.Scope(f))
        {
            var facts = await load.Work.ExecuteAsync(() => load.Store.LoadAsync(f.Account.Id, f.Clock.Now, [], default), default);
            Assert.DoesNotContain(facts.Fills, x => x.Id == foreign.Id);
            Assert.Single(facts.Fills, x => x.ExternalOrderId == entry.ExternalOrderId);
        }

        await using (var first = database.Scope(f)) Assert.True((await first.Evaluate(f.Observation())).Incomplete);
        f.Advance(60);
        await using var second = database.Scope(f);
        Assert.True((await second.Evaluate(f.Observation())).Incomplete);
        // The Play's quantity is unknown, so the 2 vs 1 position is never reported as excess.
        Assert.DoesNotContain(await second.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind.StartsWith("position.", StringComparison.Ordinal));
    }

    private static PlayPlanDocument Plan() => new("long", "quantity", "1", "1", null,
        [new("e", "Entry", "#ffffff", "100", "100", [new("s", "price", "90", "100")], [new("t", "price", "120", "100")])], new("", "", "", ""));

    /// <summary>A Play on <paramref name="contract"/>, cancelled two hours ago with one linked resting order.</summary>
    private static Play TerminalPlay(DiscrepancyFixture f, string contract, string orderId, OrderLinkRole role)
    {
        var play = new Play(Guid.NewGuid(), f.Account, new("hyperliquid", contract), InstrumentSource.Venue,
            PlayDocuments.Serialize(Plan()), f.Clock.Now.AddHours(-4));
        play.MarkPlanned(f.Clock.Now.AddHours(-3));
        f.Store.Plays.Add(play);
        f.Store.Links.Add(f.Link(play, orderId, role, "e", role == OrderLinkRole.Stop ? "s" : null, OrderLinkState.Linked,
            OrderLinkSource.Owner, f.Clock.Now.AddHours(-3)));
        play.Cancel(CancelReason.ChangedMind, null, f.Clock.Now.AddHours(-2));
        return play;
    }

    /// <summary>An Open long Play on <paramref name="contract"/> with a filled linked entry of 1.</summary>
    private static Play OpenPlay(DiscrepancyFixture f, string contract, string entryOrder)
    {
        var play = new Play(Guid.NewGuid(), f.Account, new("hyperliquid", contract), InstrumentSource.Venue,
            PlayDocuments.Serialize(Plan()), f.Clock.Now.AddHours(-1));
        play.MarkPlanned(f.Clock.Now.AddMinutes(-30));
        play.MarkOpen("fixture", f.Clock.Now.AddMinutes(-20));
        f.Store.Plays.Add(play);
        var order = f.Retained(play, entryOrder, OrderLinkRole.Entry);
        order.Status = order.VenueStatus = "filled";
        order.RemainingSize = 0;
        AddFill(f, contract, entryOrder + "-fill", entryOrder, 1, f.Clock.Now.AddMinutes(-20));
        f.Store.Links.Add(f.Link(play, entryOrder, OrderLinkRole.Entry, "e", null, OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now.AddMinutes(-25)));
        return play;
    }

    private static ImportedFill AddFill(DiscrepancyFixture f, string contract, string id, string order, decimal quantity, DateTimeOffset at)
    {
        var fill = new ImportedFill
        {
            Id = Guid.NewGuid(), OwnerId = f.Account.OwnerId, AccountId = f.Account.Id, ContractId = contract, SourceFillId = id,
            ExternalOrderId = order, Side = "buy", Direction = "buy", PositionEffect = ExecutionFacts.Open, Quantity = quantity, Price = 100,
            OccurredAtUtc = at, FeeToken = "USDC", TransactionHash = "synthetic"
        };
        f.Store.Fills.Add(fill);
        return fill;
    }

    /// <summary>Adds the ETH instrument, an optional ETH position and current-book ETH orders to a fixture observation.</summary>
    private static MonitorEvaluation WithEth(MonitorEvaluation o, decimal position, params VenueOrder[] orders)
    {
        var sync = o.Read.Sync;
        var at = o.Read.Orders!.ObservedAtUtc;
        IReadOnlyList<VenuePosition> positions = position == 0 ? sync.Snapshot.Positions :
            [.. sync.Snapshot.Positions, new VenuePosition("ETH", position, 100, 0, 0, 1)];
        return o with
        {
            Read = o.Read with
            {
                Sync = sync with { Snapshot = sync.Snapshot with { Positions = positions },
                    Instruments = [.. sync.Instruments, new VenueInstrument("ETH", 4, 10, "USDC")] },
                Orders = o.Read.Orders with { Orders = [.. o.Read.Orders.Orders,
                    .. orders.Select(x => x.Status == VenueOrderStatus.Open ? x with { StatusAtUtc = at } : x)] }
            }
        };
    }
}
