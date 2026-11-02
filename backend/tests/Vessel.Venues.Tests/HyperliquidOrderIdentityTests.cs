using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

/// <summary>Order merges key by (canonical contract, oid): a venue ID repeated on another contract is another order.</summary>
public sealed partial class HyperliquidOrderTests
{
    private const string TwoMarketMeta =
        """{"universe":[{"name":"BTC","szDecimals":5,"maxLeverage":40},{"name":"kPEPE","szDecimals":0,"maxLeverage":10}]}""";

    private static async Task<VenueOrderReadResult> ReadTwoMarketOrders(string history, string book)
    {
        using var handler = new Handler(TwoMarketMeta, history, book);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/") };
        return await new HyperliquidPerpetualReader(client, TimeProvider.System).ReadOrdersAsync(Address, default);
    }

    private static string Update(string order, string status, long at) =>
        $$"""{"order":{{order}},"status":"{{status}}","statusTimestamp":{{at}}}""";

    [Fact]
    public async Task Identity_same_oid_on_two_contracts_yields_two_orders_from_history_and_book()
    {
        var history = $"[{Update(Order(7), "open", 1791022190435)},{Update(Order(7, coin: "kPEPE", px: "0.012345", sz: "0.0"), "filled", 1791022290435)}]";
        var result = await ReadTwoMarketOrders(history, $"[{Order(7)}]");
        Assert.True(result.OpenOrdersComplete);
        Assert.Equal(2, result.Orders.Count);
        var btc = Assert.Single(result.Orders, o => o.ContractId == "BTC");
        var pepe = Assert.Single(result.Orders, o => o.ContractId == "1000PEPE");
        Assert.Equal(("7", VenueOrderStatus.Open, "open", (string?)null), (btc.OrderId, btc.Status, btc.VenueStatus, btc.VenueContractId));
        Assert.Equal(("7", VenueOrderStatus.Filled, "filled", "kPEPE", 0.012345m),
            (pepe.OrderId, pepe.Status, pepe.VenueStatus, pepe.VenueContractId, pepe.LimitPrice));
    }

    [Fact]
    public async Task Identity_same_oid_open_on_two_contracts_keeps_the_book_complete()
    {
        var result = await ReadTwoMarketOrders("[]", $"[{Order(7)},{Order(7, coin: "kPEPE", px: "0.012345")}]");
        Assert.True(result.OpenOrdersComplete);
        Assert.Equal(["1000PEPE", "BTC"], result.Orders.Select(o => o.ContractId).Order(StringComparer.Ordinal));
        Assert.All(result.Orders, o => Assert.Equal(("7", VenueOrderStatus.Open), (o.OrderId, o.Status)));
    }

    [Fact]
    public async Task Identity_duplicate_oid_within_one_contract_is_still_one_order_and_incomplete()
    {
        var result = await ReadTwoMarketOrders("[]", $"[{Order(7, coin: "kPEPE")},{Order(7, coin: "kPEPE")},{Order(7)}]");
        Assert.False(result.OpenOrdersComplete);
        Assert.Equal(2, result.Orders.Count);
        Assert.Single(result.Orders, o => o.ContractId == "1000PEPE");
        Assert.Single(result.Orders, o => o.ContractId == "BTC");
    }

    [Fact]
    public async Task Identity_newest_status_still_wins_within_one_contract()
    {
        var history = "[" + string.Join(",",
            Update(Order(7, sz: "0.0"), "filled", 1791022290435),
            Update(Order(7), "open", 1791022190435),
            Update(Order(7, coin: "kPEPE"), "open", 1791022390435),
            Update(Order(7, coin: "kPEPE"), "canceled", 1791022190435)) + "]";
        var result = await ReadTwoMarketOrders(history, "[]");
        Assert.True(result.OpenOrdersComplete);
        Assert.Equal(2, result.Orders.Count);
        Assert.Equal(VenueOrderStatus.Filled, Assert.Single(result.Orders, o => o.ContractId == "BTC").Status);
        Assert.Equal(VenueOrderStatus.Open, Assert.Single(result.Orders, o => o.ContractId == "1000PEPE").Status);
    }
}
