using Vessel.Application.Venues;

namespace Vessel.Tests;

/// <summary>Order merges key by (canonical contract, ID): a venue ID repeated on another contract is another order.</summary>
public sealed partial class RisexAdapterTests
{
    private static string HistoryOrder(string id, string market, string status, DateTimeOffset created, string price = "86000.5") => $$"""
        {"id":"{{id}}","price":"{{price}}","size":"0.01","market_id":"{{market}}","side":"BUY","type":"LIMIT","time_in_force":"GTC","expiry":"0",
         "post_only":false,"reduce_only":false,"cancel_reason":"","filled_size":"0","status":"{{status}}","fee_bps":"300",
         "avg_price":"0","cancel_requested":false,"created_at":"{{Ns(created)}}","cancel_requested_at":"0",
         "stop_type":"STOP_TYPE_NONE","stop_price":"","stop_price_option":"PRICE_OPTION_NONE","is_liquidation":false}
        """;

    private static string TpslOrder(string id, string market, string status, DateTimeOffset created, DateTimeOffset? triggered) => $$"""
        {"order_id":"{{id}}","account":"{{Address}}","market_id":"{{market}}","side":"SELL","size":"0","size_percent_bps":10000,
         "stop_type":"STOP_LOSS","stop_price":"80000","limit_price":"","order_type":"MARKET","status":"{{status}}",
         "created_at":"{{Ns(created)}}","triggered_at":"{{(triggered is { } at ? Ns(at) : "0")}}","tif":"GTC"}
        """;

    private static string OrderPage(params string[] rows) =>
        "{\"data\":{\"orders\":[" + string.Join(",", rows) + "],\"page\":1,\"has_next_page\":false},\"request_id\":\"r\"}";

    [Fact]
    public async Task Identity_same_order_id_on_two_markets_yields_two_orders()
    {
        var (reader, _) = Reader(new()
        {
            ["/v1/orders"] = OrderPage(HistoryOrder("0xsame", "1", "ORDER_STATUS_OPEN", Now.AddMinutes(-20)),
                HistoryOrder("0xsame", "14", "ORDER_STATUS_FILLED", Now.AddMinutes(-10), "0.0951")),
            ["/v1/orders/tpsl"] = OrderPage(),
        });
        var result = await reader.ReadOrdersAsync(Address, default);
        Assert.Equal(2, result.Orders.Count);
        var btc = Assert.Single(result.Orders, o => o.ContractId == "BTC");
        var doge = Assert.Single(result.Orders, o => o.ContractId == "DOGE");
        Assert.Equal(("0xsame", "1", VenueOrderStatus.Open), (btc.OrderId, btc.VenueContractId, btc.Status));
        Assert.Equal(("0xsame", "14", VenueOrderStatus.Filled, 0.0951m), (doge.OrderId, doge.VenueContractId, doge.Status, doge.LimitPrice));
    }

    [Fact]
    public async Task Identity_tpsl_and_history_with_one_id_merge_only_within_the_same_market()
    {
        var (reader, _) = Reader(new()
        {
            ["/v1/orders"] = OrderPage(HistoryOrder("0xsame", "1", "ORDER_STATUS_OPEN", Now.AddMinutes(-20))),
            ["/v1/orders/tpsl"] = OrderPage(
                TpslOrder("0xsame", "1", "TPSL_ORDER_STATUS_TRIGGERED", Now.AddMinutes(-30), Now.AddMinutes(-5)),
                TpslOrder("0xsame", "14", "TPSL_ORDER_STATUS_ACCEPTED", Now.AddMinutes(-15), null)),
        });
        var result = await reader.ReadOrdersAsync(Address, default);
        Assert.False(result.OpenOrdersComplete);
        Assert.Equal(2, result.Orders.Count);
        // Within BTC the newest status (the triggered TP/SL record) still wins over the older open history row.
        var btc = Assert.Single(result.Orders, o => o.ContractId == "BTC");
        Assert.Equal((VenueOrderStatus.Triggered, "TPSL_ORDER_STATUS_TRIGGERED", "Stop Market"), (btc.Status, btc.VenueStatus, btc.OrderType));
        var doge = Assert.Single(result.Orders, o => o.ContractId == "DOGE");
        Assert.Equal((VenueOrderStatus.Open, "14"), (doge.Status, doge.VenueContractId));
    }

    [Fact]
    public async Task Identity_duplicate_id_within_one_market_keeps_the_newest_status()
    {
        var (reader, _) = Reader(new()
        {
            ["/v1/orders"] = OrderPage(HistoryOrder("0xsame", "14", "ORDER_STATUS_OPEN", Now.AddMinutes(-20), "0.0951")),
            ["/v1/orders/tpsl"] = OrderPage(TpslOrder("0xsame", "14", "TPSL_ORDER_STATUS_CANCELLED", Now.AddMinutes(-30), null)),
        });
        var result = await reader.ReadOrdersAsync(Address, default);
        var order = Assert.Single(result.Orders);
        Assert.Equal(("DOGE", VenueOrderStatus.Open, "Limit"), (order.ContractId, order.Status, order.OrderType));
    }

    [Fact]
    public async Task Identity_retired_and_active_markets_sharing_a_canonical_key_both_reach_order_import()
    {
        var (reader, _) = Reader(new()
        {
            ["/v1/orders"] = OrderPage(HistoryOrder("0xsame", "13", "ORDER_STATUS_CANCELLED", Now.AddMinutes(-30), "0.0951"),
                HistoryOrder("0xsame", "14", "ORDER_STATUS_OPEN", Now.AddMinutes(-10), "0.0951")),
            ["/v1/orders/tpsl"] = OrderPage(),
        });
        var result = await reader.ReadOrdersAsync(Address, default);
        // Both native markets are kept, so the import guard sees the conflicting identity and fails closed.
        Assert.Equal(["13", "14"], result.Orders.Where(o => o.OrderId == "0xsame").Select(o => o.VenueContractId).Order(StringComparer.Ordinal));
        Assert.All(result.Orders, o => Assert.Equal("DOGE", o.ContractId));
    }
}
