using System.Text.Json.Nodes;
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

    private static string WithChildren(string order, string children = "[]")
    {
        var parent = JsonNode.Parse(order)!;
        parent["children"] = JsonNode.Parse(children == "[]" ? $"[{Order(99, "A", trigger: true, type: "Stop Market")}]" : children);
        return parent.ToJsonString();
    }

    [Fact] // #95: attached TP/SL children are the parent's own market; only that contract's book is incomplete.
    public async Task Coverage_attached_children_leave_only_their_parents_contract_incomplete()
    {
        var result = await ReadTwoMarketOrders("[]", $"[{WithChildren(Order(7, coin: "kPEPE", px: "0.012345"))},{Order(8)}]");
        Assert.False(result.OpenOrdersComplete);
        Assert.Equal([new VenueIncompleteBook("1000PEPE", "kPEPE", VenueIncompleteBook.AttachedTpsl)], result.IncompleteContracts);
        Assert.False(result.OpenOrdersCompleteFor("1000PEPE", "kPEPE"));
        Assert.True(result.OpenOrdersCompleteFor("BTC", null));
        // The parent itself is still read; its children are not.
        Assert.Equal(["7", "8"], result.Orders.Select(o => o.OrderId).Order(StringComparer.Ordinal));
    }

    [Theory] // #95: an unread parent, a malformed child list or a whole-read condition still leaves every contract unknown.
    [InlineData("spot-parent")]
    [InlineData("malformed-children")]
    [InlineData("duplicate")]
    public async Task Coverage_children_that_cannot_be_attributed_leave_the_whole_book_incomplete(string scope)
    {
        var book = scope switch
        {
            "spot-parent" => $"[{WithChildren(Order(7, coin: "@123"))},{Order(8)}]",
            "malformed-children" => $"[{WithChildren(Order(7, coin: "kPEPE"), "{}")},{Order(8)}]",
            _ => $"[{WithChildren(Order(7, coin: "kPEPE"))},{Order(8)},{Order(8)}]",
        };
        var result = await ReadTwoMarketOrders("[]", book);
        Assert.False(result.OpenOrdersComplete);
        Assert.Null(result.IncompleteContracts);
        Assert.False(result.OpenOrdersCompleteFor("BTC", null));
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
