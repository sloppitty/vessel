using System.Net;
using System.Text;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed partial class HyperliquidOrderTests
{
    private const string Address = "0x0123456789abcdef0123456789ABCDEF01234567";
    private const string Meta = """{"universe":[{"name":"BTC","szDecimals":5,"maxLeverage":40}]}""";

    private static string Order(long oid, string side = "B", string px = "84553.0", long time = 1791022190435, bool trigger = false,
        string type = "Limit", string coin = "BTC", string sz = "0.25") =>
        $$"""{"coin":"{{coin}}","side":"{{side}}","limitPx":"{{px}}","sz":"{{sz}}","oid":{{oid}},"timestamp":{{time}},"triggerCondition":"N/A","isTrigger":{{(trigger ? "true" : "false")}},"triggerPx":"{{(trigger ? "80000.0" : "0.0")}}","children":[],"isPositionTpsl":false,"reduceOnly":{{(trigger ? "true" : "false")}},"orderType":"{{type}}","origSz":"0.25","tif":"Gtc","cloid":null}""";

    private sealed class Handler(params string[] bodies) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent(bodies[Requests.Count - 1], Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Reads_latest_order_status_with_the_open_book_taking_precedence()
    {
        var history = $$"""
        [
          {"order":{{Order(1)}},"status":"open","statusTimestamp":1791022190435},
          {"order":{{Order(1, sz: "0.0")}},"status":"filled","statusTimestamp":1791022290435},
          {"order":{{Order(2, "A", trigger: true, type: "Stop Market")}},"status":"open","statusTimestamp":1791022190435},
          {"order":{{Order(3)}},"status":"badAloPxRejected","statusTimestamp":1791022190435},
          {"order":{{Order(4, coin: "xyz:TSLA")}},"status":"open","statusTimestamp":1791022190435}
        ]
        """;
        var open = $"[{Order(2, "A", trigger: true, type: "Stop Market")}]";
        var handler = new Handler(Meta, history, open);
        var reader = new HyperliquidPerpetualReader(new HttpClient(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/") }, TimeProvider.System);
        var result = await reader.ReadOrdersAsync(Address, default);
        var orders = result.Orders.ToDictionary(o => o.OrderId);
        Assert.Equal(["1", "2", "3"], orders.Keys.Order());
        Assert.Equal((VenueOrderStatus.Filled, "filled", 0m), (orders["1"].Status, orders["1"].VenueStatus, orders["1"].RemainingSize));
        Assert.Equal((VenueOrderStatus.Open, 80000.0m, 80000.0m, true), (orders["2"].Status, orders["2"].TriggerPrice, orders["2"].ActingPrice, orders["2"].ReduceOnly));
        Assert.Equal(VenueOrderStatus.Rejected, orders["3"].Status);
        Assert.Null(orders["1"].TriggerPrice);
        Assert.Contains("\"type\":\"historicalOrders\"", handler.Requests[1]);
        Assert.Contains("\"type\":\"frontendOpenOrders\"", handler.Requests[2]);
    }

    [Fact]
    public async Task Malformed_orders_are_a_safe_venue_error()
    {
        var handler = new Handler(Meta, """[{"order":{"coin":"BTC","side":"X"},"status":"open","statusTimestamp":1}]""", "[]");
        var reader = new HyperliquidPerpetualReader(new HttpClient(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/") }, TimeProvider.System);
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadOrdersAsync(Address, default));
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadOrdersAsync("0xnope", default));
    }
}
