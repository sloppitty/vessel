using System.Net;
using System.Text;
using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class HyperliquidBuilderMarketTests
{
    private const string Address = "0x1234567890abcdef1234567890abcdef12345678";

    private const string Meta = """{"universe":[{"name":"BTC","szDecimals":5,"maxLeverage":40}]}""";
    private const string AllMetas = """
        [{"universe":[{"name":"BTC","szDecimals":5,"maxLeverage":40}],"collateralToken":0},
         {"universe":[{"name":"xyz:GOLD","szDecimals":4,"maxLeverage":25},
                      {"name":"xyz:OLD","szDecimals":2,"maxLeverage":5,"isDelisted":true},
                      {"name":"xyz:AMD","szDecimals":2,"maxLeverage":10,"onlyIsolated":true,"marginMode":"noCross"},
                      {"name":"xyz:bad name!","szDecimals":2,"maxLeverage":5}],"collateralToken":0},
         {"universe":[{"name":"flx:GOLD","szDecimals":2,"maxLeverage":20}],"collateralToken":360},
         {"universe":[{"name":"gone:ONE","szDecimals":2,"maxLeverage":5,"isDelisted":true}],"collateralToken":0}]
        """;
    private const string Dexes = """[null,{"name":"xyz","fullName":"XYZ"},{"name":"flx","fullName":"Felix Exchange"},{"name":"gone","fullName":"Gone"}]""";
    private const string Annotations = """[["xyz:GOLD",{"category":"commodities","displayName":"GOLD","keywords":["xau","bullion"]}],["bad"],["xyz:OLD",{"category":"preipo"}]]""";
    private const string EmptyState = """{"marginSummary":{"accountValue":"100.0","totalNtlPos":"0.0","totalRawUsd":"100.0","totalMarginUsed":"0.0"},"withdrawable":"100.0","assetPositions":[],"time":1700000000000}""";
    private const string GoldState = """
        {"marginSummary":{"accountValue":"227.0","totalNtlPos":"0.0","totalRawUsd":"0.0","totalMarginUsed":"89.9"},"withdrawable":"0.1",
         "assetPositions":[{"type":"oneWay","position":{"coin":"xyz:GOLD","szi":"0.4608","leverage":{"type":"cross","value":20},"entryPx":"3905.8",
         "positionValue":"1799.9","unrealizedPnl":"0.13","liquidationPx":"3629.3","marginUsed":"89.9"}}],"time":1700000000000}
        """;
    private const string GoldFill = """
        [{"coin":"xyz:GOLD","px":"3905.8","sz":"0.4608","side":"B","time":1700000000000,"startPosition":"0.0","dir":"Open Long","closedPnl":"0.0",
          "hash":"0x15f696375209d16817700447e806c502015f001ced0cf03ab9bf418a110dab52","oid":592466210216,"crossed":true,"fee":"1.61","tid":826608950852160,"feeToken":"USDC"},
         {"coin":"flx:GOLD","px":"3905.8","sz":"1","side":"B","time":1700000000000,"startPosition":"0.0","dir":"Open Long","closedPnl":"0.0",
          "hash":"0x25f696375209d16817700447e806c502015f001ced0cf03ab9bf418a110dab52","oid":2,"crossed":true,"fee":"1","tid":2,"feeToken":"USDH"}]
        """;
    private const string GoldBook = """
        [{"coin":"xyz:GOLD","side":"A","limitPx":"3564.0","sz":"0.4608","oid":592466210218,"timestamp":1700000000000,"origSz":"0.4608",
          "triggerCondition":"Price below 3600","isTrigger":true,"triggerPx":"3600.0","children":[],"isPositionTpsl":false,"reduceOnly":true,"orderType":"Stop Market","tif":null,"cloid":null}]
        """;

    private sealed class Routes(Func<string, string?, string?> reply) : HttpMessageHandler
    {
        public List<(string Type, string? Dex)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var type = body.RootElement.GetProperty("type").GetString()!;
            var dex = body.RootElement.TryGetProperty("dex", out var value) ? value.GetString() : null;
            lock (Requests) Requests.Add((type, dex));
            return reply(type, dex) is { } json
                ? new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new(HttpStatusCode.InternalServerError);
        }
    }

    private static string? Default(string type, string? dex) => type switch
    {
        "meta" => Meta,
        "allPerpMetas" => AllMetas,
        "perpDexs" => Dexes,
        "perpConciseAnnotations" => Annotations,
        "clearinghouseState" => dex == "xyz" ? GoldState : EmptyState,
        "userFills" => GoldFill,
        "userAbstraction" => "\"default\"",
        "spotMeta" => """{"tokens":[],"universe":[]}""",
        "spotClearinghouseState" => """{"balances":[]}""",
        "historicalOrders" => "[]",
        "frontendOpenOrders" => dex == "xyz" ? GoldBook : "[]",
        _ => null,
    };

    private static HyperliquidPerpetualReader Reader(HttpMessageHandler handler, HttpClient? client = null, bool builders = true) =>
        new(client ?? new HttpClient(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/") }, TimeProvider.System, null, builders);

    [Fact]
    public async Task Catalogue_adds_usdc_builder_markets_with_labels_and_skips_other_collateral_and_bad_rows()
    {
        var instruments = await Reader(new Routes(Default)).ReadInstrumentsAsync(CancellationToken.None);
        Assert.Equal(["BTC", "xyz:GOLD", "xyz:AMD"], instruments.Select(i => i.ContractId));
        Assert.Equal([false, false, true], instruments.Select(i => i.IsolatedOnly));
        Assert.Equal(MarketCategories.Crypto, instruments[0].Category);
        Assert.Null(instruments[0].Source);
        var gold = instruments[1];
        Assert.Equal(("xyz", "XYZ", "GOLD", MarketCategories.Commodities, "USDC", 25), (gold.Source, gold.SourceName, gold.DisplayName, gold.Category, gold.QuoteAsset, gold.MaxLeverage));
        Assert.Equal(["xau", "bullion"], gold.Keywords);
        Assert.Equal(0.02m, gold.MaintenanceMarginFraction);
        Assert.Equal(2, gold.MaxPriceDecimals);
    }

    [Fact]
    public async Task Missing_annotations_leave_builder_markets_uncategorized_rather_than_failing()
    {
        var handler = new Routes((type, dex) => type == "perpConciseAnnotations" ? null : Default(type, dex));
        var gold = (await Reader(handler).ReadInstrumentsAsync(CancellationToken.None)).Single(i => i.ContractId == "xyz:GOLD");
        Assert.Equal(MarketCategories.Other, gold.Category);
        Assert.Equal("GOLD", gold.DisplayName);
    }

    [Fact]
    public async Task Builder_metadata_failure_keeps_the_primary_read_and_marks_coverage_partial()
    {
        var handler = new Routes((type, dex) => type == "allPerpMetas" ? null : Default(type, dex));
        var reader = Reader(handler);
        Assert.Equal(["BTC"], (await reader.ReadInstrumentsAsync(CancellationToken.None)).Select(i => i.ContractId));
        var result = await reader.ReadAsync(Address, CancellationToken.None);
        Assert.False(result.PositionsComplete);
        Assert.Empty(result.Fills);
        Assert.False((await reader.ReadOrdersAsync(Address, CancellationToken.None)).OpenOrdersComplete);
    }

    [Fact]
    public async Task Full_read_adds_builder_positions_and_fills_but_leaves_primary_equity_alone()
    {
        var handler = new Routes(Default);
        var result = await Reader(handler).ReadAsync(Address, CancellationToken.None);
        var position = Assert.Single(result.Snapshot.Positions);
        Assert.Equal(("xyz:GOLD", 0.4608m, 20, 3629.3m), (position.ContractId, position.SignedQuantity, position.Leverage!.Value, position.LiquidationPrice!.Value));
        Assert.True(result.PositionsComplete);
        Assert.Equal(100m, result.Snapshot.AccountValueUsd);
        var fill = Assert.Single(result.Fills); // flx:GOLD is another collateral: out of scope, and not an omission.
        Assert.Equal("xyz:GOLD", fill.ContractId);
        Assert.Equal(0, result.OmittedExecutions);
        Assert.Contains("builder market margin", result.HistoryNotice);
        Assert.Contains(("clearinghouseState", "xyz"), handler.Requests);
        Assert.DoesNotContain(handler.Requests, r => r is ("clearinghouseState", "flx" or "gone"));
    }

    [Fact]
    public async Task Open_orders_are_read_per_builder_dex()
    {
        var result = await Reader(new Routes(Default)).ReadOrdersAsync(Address, CancellationToken.None);
        var order = Assert.Single(result.Orders);
        Assert.Equal(("xyz:GOLD", VenueOrderStatus.Open), (order.ContractId, order.Status));
        Assert.True(result.OpenOrdersComplete);
    }

    [Fact]
    public async Task Builder_market_context_failure_never_hides_primary_marks()
    {
        const string context = """{"funding":"0.00001","openInterest":"10.0","prevDayPx":"3880.0","dayNtlVlm":"42350227.0","premium":"0.0","oraclePx":"3906.0","markPx":"3907.3","midPx":"3907.0"}""";
        string Pair(string name) => $$"""[{"universe":[{"name":"{{name}}","szDecimals":4,"maxLeverage":25}]},[{{context}}]]""";
        var good = new Routes((type, dex) => type == "metaAndAssetCtxs" ? Pair(dex is null ? "BTC" : "xyz:GOLD") : Default(type, dex));
        Assert.Equal(["BTC", "xyz:GOLD"], (await Reader(good).ReadMarketContextsAsync(CancellationToken.None)).Select(c => c.ContractId));
        var bad = new Routes((type, dex) => type == "metaAndAssetCtxs" ? dex is null ? Pair("BTC") : null : Default(type, dex));
        Assert.Equal(["BTC"], (await Reader(bad).ReadMarketContextsAsync(CancellationToken.None)).Select(c => c.ContractId));
    }

    [Theory]
    [InlineData("xyz:GOLD", true)]
    [InlineData("BTC", true)]
    [InlineData("@107", false)]
    [InlineData("PURR/USDC", false)]
    public void Perpetual_contracts_include_builder_markets_only(string name, bool perpetual) =>
        Assert.Equal(perpetual, HyperliquidPerpetualReader.IsPerpetualContract(name));

    [Theory]
    [InlineData("stocks", "stocks")]
    [InlineData("stock", "stocks")]
    [InlineData("FX", "fx")]
    [InlineData("commodity", "commodities")]
    [InlineData("commodities", "commodities")]
    [InlineData("index_etf", "indices")]
    [InlineData("preipo", "pre-ipo")]
    [InlineData("crypto", "crypto")]
    [InlineData("nonsense", "other")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Every_venue_label_maps_to_one_category_set(string? label, string? category) =>
        Assert.Equal(category, MarketCategories.Normalize(label));
}
