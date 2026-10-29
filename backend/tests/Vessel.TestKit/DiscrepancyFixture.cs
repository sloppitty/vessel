using Vessel.Application.Monitoring;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

internal sealed class DiscrepancyFixture
{
    public readonly DiscrepancyClock Clock = new();
    public readonly DiscrepancyMemoryStore Store = new();
    public readonly DiscrepancyEvents Events = new();
    public readonly Account Account;
    public readonly Play Play;
    public readonly MonitorDiscrepancyService Service;
    public readonly List<VenueOrder> VenueOrders = [];
    public decimal Position = 1;
    public decimal? PriceStep;
    public bool PositionsComplete = true, OrdersComplete = true, HistoryGap, StampOpenOrders = true, Retired, CurrentOrderBook = true;
    public bool? Truncated;
    public int Omitted;
    public TimeSpan ObservationOffset;
    public DiscrepancyFixture()
    {
        Account = new(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Synthetic"); Store.Account = Account;
        Play = AddPlay("long", 1, "entry");
        AddOrder("stop", OrderLinkRole.Stop, 1, 90);
        Service = new(Store, TestUnitOfWork.None, new CoreOwner(Account.OwnerId), Events, new(), Clock);
    }
    public Play AddPlay(string direction, decimal quantity, string? entryOrder = null)
    {
        var plan = new PlayPlanDocument(direction, "quantity", "1", "1", null,
            [new("e", "Entry", "#ffffff", "100", "100", [new("s", "price", direction == "long" ? "90" : "110", "100")],
                [new("t", "price", direction == "long" ? "120" : "80", "100")])], new("", "", "", ""));
        var play = new Play(Guid.NewGuid(), Account, new("hyperliquid", "BTC"), InstrumentSource.Venue, PlayDocuments.Serialize(plan), Clock.Now.AddHours(-1));
        play.MarkPlanned(Clock.Now.AddMinutes(-30)); play.MarkOpen("fixture", Clock.Now.AddMinutes(-20)); Store.Plays.Add(play);
        entryOrder ??= play.Id.ToString();
        var side = direction == "long" ? "buy" : "sell";
        Fill(entryOrder + "-fill", entryOrder, quantity, Clock.Now.AddMinutes(-20), side);
        Store.Orders.Add(new() { Id = Guid.NewGuid(), OwnerId = Account.OwnerId, AccountId = Account.Id, ContractId = "BTC", ExternalOrderId = entryOrder,
            Side = side, OrderType = "Limit", OriginalSize = quantity, Status = "filled", VenueStatus = "filled", PlacedAtUtc = Clock.Now.AddMinutes(-25),
            StatusAtUtc = Clock.Now.AddMinutes(-20), ObservedAtUtc = Clock.Now.AddMinutes(-20), LimitPrice = 100 });
        Store.Links.Add(Link(play, entryOrder, OrderLinkRole.Entry, "e", null, OrderLinkState.Linked, OrderLinkSource.Owner, Clock.Now.AddMinutes(-25)));
        return play;
    }
    /// <summary>
    /// The retained order with this venue ID, created on the Play's contract if missing: links reference retained orders.
    /// A created order rests unfilled on the side its role implies.
    /// </summary>
    public ImportedOrder Retained(Play play, string id, OrderLinkRole role, decimal quantity = 1, decimal price = 100,
        string type = "Limit", bool reduceOnly = false)
    {
        var existing = Store.Orders.FirstOrDefault(o => o.AccountId == play.AccountId && o.ContractId == play.ContractId && o.ExternalOrderId == id);
        if (existing is not null) return existing;
        var entrySide = PlayDocuments.Read(play.Plan).Direction == "long" ? "buy" : "sell";
        var order = new ImportedOrder { Id = Guid.NewGuid(), OwnerId = play.OwnerId, AccountId = play.AccountId, ContractId = play.ContractId!,
            ExternalOrderId = id, Side = role == OrderLinkRole.Entry ? entrySide : entrySide == "buy" ? "sell" : "buy", OrderType = type,
            LimitPrice = price, TriggerPrice = type == "Limit" ? null : price, ReduceOnly = reduceOnly, OriginalSize = quantity, RemainingSize = quantity,
            Status = "open", VenueStatus = "open", PlacedAtUtc = Clock.Now.AddMinutes(-15), StatusAtUtc = Clock.Now.AddMinutes(-15),
            ObservedAtUtc = Clock.Now.AddMinutes(-15) };
        Store.Orders.Add(order);
        return order;
    }
    /// <summary>A link to the retained order with this venue ID (see <see cref="Retained"/>).</summary>
    public PlayOrderLink Link(Play play, string id, OrderLinkRole role, string? entryId, string? levelId, OrderLinkState state,
        OrderLinkSource source, DateTimeOffset at) =>
        new(play, Retained(play, id, role), role, entryId, levelId, state, source, at);
    public void AddOrder(string id, OrderLinkRole role, decimal quantity, decimal price)
    {
        Retained(Play, id, role, quantity, price, role == OrderLinkRole.Stop ? "Stop Market" : "Take Profit Market", true);
        Store.Links.Add(Link(Play, id, role, "e", role == OrderLinkRole.Stop ? "s" : "t", OrderLinkState.Linked, OrderLinkSource.Owner, Clock.Now.AddMinutes(-15)));
        VenueOrders.Add(new(id, "BTC", "sell", role == OrderLinkRole.Stop ? "Stop Market" : "Take Profit Market", price, price, true, false,
            quantity, quantity, Clock.Now.AddMinutes(-15), VenueOrderStatus.Open, "open", Clock.Now));
    }
    public void Fill(string id, string order, decimal quantity, DateTimeOffset at, string side = "buy", string effect = ExecutionFacts.Open) => Store.Fills.Add(new()
    {
        Id = Guid.NewGuid(), OwnerId = Account.OwnerId, AccountId = Account.Id, ContractId = "BTC", SourceFillId = id, ExternalOrderId = order,
        Side = side, Direction = side, PositionEffect = effect, Quantity = quantity, Price = 100, OccurredAtUtc = at, FeeToken = "USDC", TransactionHash = "synthetic"
    });
    public void Stop(Func<VenueOrder, VenueOrder> change) { var index = VenueOrders.FindIndex(x => x.OrderId == "stop"); VenueOrders[index] = change(VenueOrders[index]); }
    public void Advance(int seconds) => Clock.Now = Clock.Now.AddSeconds(seconds);
    public MonitorEvaluation Observation()
    {
        var at = Clock.Now + ObservationOffset;
        return new(Guid.NewGuid(), Account, new(new(new(at, "primary-perpetuals", 100, 100, 0,
            Position == 0 || Retired ? [] : [new("BTC", Position, 100, 0, 0, 1)]), Retired ? [] : [new("BTC", 4, 10, "USDC", PriceStep)], [], "bounded",
            PositionsComplete, false, Truncated, OmittedExecutions: Omitted), new(VenueOrders.Select(x => StampOpenOrders && x.Status == VenueOrderStatus.Open ? x with { StatusAtUtc = at } : x).ToArray(), at, "bounded", OrdersComplete)),
            null, null, HistoryGap, "scheduled", CurrentOrderBook);
    }
    public Task<MonitorEvaluationResult> Evaluate() => Service.EvaluateAsync(Observation(), default);
    public async Task Confirm() { await Evaluate(); Advance(60); await Evaluate(); }
}
