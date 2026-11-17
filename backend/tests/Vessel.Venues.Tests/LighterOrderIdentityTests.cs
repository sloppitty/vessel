using System.Text.Json.Nodes;
using Vessel.Application.Venues;

namespace Vessel.Tests;

/// <summary>Order merges key by (canonical contract, order index): a venue ID repeated on another market is another order.</summary>
public sealed partial class LighterAdapterTests
{
    private static string OrderOn(int market, string status = "pending") =>
        Change(Change(Order, "market_index", JsonValue.Create(market)), "status", JsonValue.Create(status));

    private static Handler OrderHandler(string active, string inactive = """{"code":200,"orders":[]}""") => new()
    {
        Reply = request =>
            request.RequestUri!.AbsolutePath.EndsWith("/accountActiveOrders", StringComparison.Ordinal) ? Response(active)
            : request.RequestUri.AbsolutePath.EndsWith("/accountInactiveOrders", StringComparison.Ordinal) ? Response(inactive)
            : null
    };

    [Fact]
    public async Task Identity_same_active_order_index_on_two_markets_yields_two_orders()
    {
        using var handler = OrderHandler(Orders(OrderOn(1) + "," + OrderOn(4)));
        var result = await Reader(handler, new(Token)).ReadOrdersAsync(Account(), default);
        Assert.True(result.OpenOrdersComplete);
        Assert.Equal(2, result.Orders.Count);
        var btc = Assert.Single(result.Orders, o => o.ContractId == "BTC");
        var pepe = Assert.Single(result.Orders, o => o.ContractId == "1000PEPE");
        Assert.Equal(("9007199254740995", "1", VenueOrderStatus.Open), (btc.OrderId, btc.VenueContractId, btc.Status));
        Assert.Equal(("9007199254740995", "4", VenueOrderStatus.Open), (pepe.OrderId, pepe.VenueContractId, pepe.Status));
    }

    [Fact]
    public async Task Identity_duplicate_active_order_within_one_market_still_fails()
    {
        using var handler = OrderHandler(Orders(OrderOn(4) + "," + OrderOn(1) + "," + OrderOn(4)));
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler, new(Token)).ReadOrdersAsync(Account(), default));
    }

    [Fact]
    public async Task Identity_inactive_history_replaces_only_the_same_market_order()
    {
        using var handler = OrderHandler(Orders(OrderOn(1) + "," + OrderOn(4)), Orders(OrderOn(1, "filled")));
        var result = await Reader(handler, new(Token)).ReadOrdersAsync(Account(), default);
        Assert.True(result.OpenOrdersComplete);
        Assert.Equal(2, result.Orders.Count);
        var btc = Assert.Single(result.Orders, o => o.ContractId == "BTC");
        var pepe = Assert.Single(result.Orders, o => o.ContractId == "1000PEPE");
        // The later inactive observation still replaces the active row within its own market.
        Assert.Equal((VenueOrderStatus.Filled, "filled"), (btc.Status, btc.VenueStatus));
        Assert.Equal((VenueOrderStatus.Open, "pending"), (pepe.Status, pepe.VenueStatus));
        Assert.All(result.Orders, o => Assert.Equal("9007199254740995", o.OrderId));
    }

    // A TWAP is not a trigger order, so like any read order it carries no trigger price.
    private static string Unread(int market, string kind) => kind switch
    {
        "twap" or "twap-sub" => Change(Change(OrderOn(market), "type", JsonValue.Create(kind)), "trigger_price", JsonValue.Create("0")),
        _ => Change(Change(OrderOn(market), "initial_base_amount", JsonValue.Create("0")), "remaining_base_amount", JsonValue.Create("0")),
    };

    [Theory] // #95: an order Vessel cannot model leaves only its own market's book incomplete.
    [InlineData("twap", VenueIncompleteBook.Twap)]
    [InlineData("twap-sub", VenueIncompleteBook.Twap)]
    [InlineData("unsized", VenueIncompleteBook.Unsized)]
    public async Task Coverage_an_unread_order_leaves_only_its_market_incomplete(string row, string kind)
    {
        using var handler = OrderHandler(Orders(Unread(4, row) + "," + OrderOn(1)));
        var result = await Reader(handler, new(Token)).ReadOrdersAsync(Account(), default);
        Assert.False(result.OpenOrdersComplete);
        Assert.Equal([new VenueIncompleteBook("1000PEPE", "4", kind)], result.IncompleteContracts);
        Assert.False(result.OpenOrdersCompleteFor("1000PEPE", "4"));
        Assert.True(result.OpenOrdersCompleteFor("BTC", "1"));
        Assert.Equal("BTC", Assert.Single(result.Orders).ContractId);
    }

    [Theory] // #95: with a whole-read condition beside it, no market is complete.
    [InlineData("cursor")]
    [InlineData("unknown-status")]
    // Flags have no documented market scope, whatever the order's type or size (review S2).
    [InlineData("flagged")]
    [InlineData("flagged-twap")]
    [InlineData("flagged-unsized")]
    // A skipped order is checked like a read one before it may narrow the book (review S3).
    [InlineData("skipped-unknown-status")]
    [InlineData("skipped-repeated")]
    [InlineData("skipped-malformed-id")]
    [InlineData("skipped-repeats-a-read-order")]
    [InlineData("skipped-trigger-price")]
    public async Task Coverage_an_unread_order_beside_a_whole_read_condition_leaves_every_market_incomplete(string condition)
    {
        var twap = Unread(4, "twap");
        string[] rows = condition switch
        {
            "unknown-status" => [twap, OrderOn(1, "unknown")],
            "flagged" => [Change(OrderOn(4), "order_flags", JsonValue.Create(1)), OrderOn(1)],
            "flagged-twap" => [Change(twap, "order_flags", JsonValue.Create(1)), OrderOn(1)],
            "flagged-unsized" => [Change(Unread(4, "unsized"), "order_flags", JsonValue.Create(1)), OrderOn(1)],
            "skipped-unknown-status" => [Change(twap, "status", JsonValue.Create("unknown")), OrderOn(1)],
            "skipped-repeated" => [twap, twap, OrderOn(1)],
            "skipped-malformed-id" => [Change(twap, "order_id", JsonValue.Create("1")), OrderOn(1)],
            "skipped-repeats-a-read-order" => [Unread(1, "unsized"), OrderOn(1), OrderOn(4)],
            "skipped-trigger-price" => [Change(twap, "trigger_price", JsonValue.Create("1")), OrderOn(1)],
            _ => [twap, OrderOn(1)],
        };
        var body = Orders(string.Join(",", rows));
        if (condition == "cursor")
        {
            var root = JsonNode.Parse(body)!;
            root["next_cursor"] = "more";
            body = root.ToJsonString();
        }
        using var handler = OrderHandler(body);
        var result = await Reader(handler, new(Token)).ReadOrdersAsync(Account(), default);
        Assert.False(result.OpenOrdersComplete);
        Assert.Null(result.IncompleteContracts);
        Assert.False(result.OpenOrdersCompleteFor("BTC", "1"));
        Assert.False(result.OpenOrdersCompleteFor("1000PEPE", "4"));
    }

    [Fact]
    public async Task Identity_inactive_history_on_another_market_adds_an_order()
    {
        using var handler = OrderHandler(Orders(OrderOn(1)), Orders(OrderOn(4, "canceled")));
        var result = await Reader(handler, new(Token)).ReadOrdersAsync(Account(), default);
        Assert.Equal(2, result.Orders.Count);
        Assert.Equal(VenueOrderStatus.Open, Assert.Single(result.Orders, o => o.ContractId == "BTC").Status);
        Assert.Equal(VenueOrderStatus.Canceled, Assert.Single(result.Orders, o => o.ContractId == "1000PEPE").Status);
    }
}
