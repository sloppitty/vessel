using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Venues;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Risex;

namespace Vessel.Tests;

/// <summary>RISEx adapter against recorded response shapes (October 5, 2026). The address is synthetic.</summary>
public sealed partial class RisexAdapterTests
{
    private const string Address = "0x00000000000000000000000000000000000000aa";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
    private static string Ns(DateTimeOffset at) => (at.ToUnixTimeMilliseconds() * 1_000_000).ToString();

    private static string Market(string id, string name, bool active = true, string category = "crypto", string tick = "0.1",
        string step = "0.000001", string leverage = "25", bool unlocked = true) => $$"""
        {"market_id":"{{id}}","config":{"name":"{{name}}","quote":"0xe4","step_size":"{{step}}","step_price":"{{tick}}",
         "maintenance_margin_factor":"37.5","max_leverage":"{{leverage}}","min_order_size":"0.00015","unlocked":{{(unlocked ? "true" : "false")}},"open_interest_limit":"750"},
         "base_asset_symbol":"{{name}}","quote_asset_symbol":"USDC","display_name":"{{name}}","quote_volume_24h":"35255149.8695194",
         "change_24h":"1476.4","last_price":"86650.9","mark_price":"86627.713809202553651873","index_price":"86659.469794009055",
         "open_interest":"187.059842","funding_interval":"3600000000000","current_funding_rate":"0.000004298138961497",
         "active":{{(active ? "true" : "false")}},"category":"{{category}}"}
        """;

    private static readonly string Markets = $$"""
        {"data":{"markets":[{{Market("1", "BTC/USDC")}},
          {{Market("13", "DOGE/USDC [deprecated-1779958099]", active: false, category: "", tick: "0.0001", step: "0.01", leverage: "10")}},
          {{Market("14", "DOGE/USDC", tick: "0.00001", step: "0.01", leverage: "10")}},
          {{Market("40", "TSLA/USDC", category: "stocks", tick: "0.01", step: "0.001", leverage: "5")}},
          {{Market("50", "weird name", category: "crypto")}}],"cached_at":"0"},"request_id":"r"}
        """;

    private static string Portfolio => """
        {"data":{"account":"0x00000000000000000000000000000000000000aa","summary":{"collateral_margin_balance":"11853.7","cross_margin_balance":"12183.26",
          "free_collateral":"131.89","total_account_value":"12183.268781664941504325","usdc_balance":"11853.7","total_unrealized_pnl":"146.14",
          "total_initial_margin":"12051.377019174158945586","total_maintenance_margin":"3213.7","realized_pnl":"-1987.9","unsettled_usdc":"352.98",
          "in_liquidation":false,"risk_level":"NORMAL"},
          "positions":[
            {"size":"0","quote_amount":"","margin_mode":0,"side":0,"market_id":"40","market_name":"TSLA/USDC","avg_entry_price":"0","mark_price":"300",
             "leverage":"0","unrealized_pnl":"0","liquidation_price":"0","initial_margin_requirement":"0"},
            {"size":"0.702475","quote_amount":"","margin_mode":0,"side":0,"market_id":"1","market_name":"BTC/USDC","avg_entry_price":"84927.632925174710359523",
             "mark_price":"86001.3","leverage":"10","unrealized_pnl":"754.238926063737700192","liquidation_price":"73336.01",
             "initial_margin_requirement":"6041.377786517584236"},
            {"size":"-2000","quote_amount":"","margin_mode":0,"side":0,"market_id":"14","market_name":"DOGE/USDC","avg_entry_price":"0.0951",
             "mark_price":"0.0946","leverage":"5","unrealized_pnl":"1","liquidation_price":"0.11","initial_margin_requirement":"38"}]},"request_id":"r"}
        """;

    private static string Trade(string id, string market, string side, string positionSide, string fee, string pnl, DateTimeOffset at,
        string order = "0xorder1", bool liquidation = false) => $$"""
        {"id":"{{id}}","market_id":"{{market}}","order_id":"{{order}}","side":"{{side}}","price":"86000.5","size":"0.01","fee":"{{fee}}",
         "liquidity_indicator":"MAKER","time":"{{Ns(at)}}","blockchain_data":{"block_number":"1","tx_hash":"0xhash{{id}}","log_index":"1"},
         "is_liquidation":{{(liquidation ? "true" : "false")}},"realized_pnl":"{{pnl}}","leverage":"10","margin_mode":0,"realized_pnl_percentage":"0",
         "avg_price":"86000","position_side":"{{positionSide}}","is_otc":false,"client_order_id":""}
        """;

    private static readonly string Trades = $$"""
        {"data":{"market_id":"0","wallet_address":"{{Address}}","trades":[
          {{Trade("0xopen", "1", "BUY", "BUY", "0.086", "-0.086", Now.AddMinutes(-30))}},
          {{Trade("0xclose", "1", "SELL", "BUY", "0.086", "12.5", Now.AddMinutes(-10), "0xorder2")}},
          {{Trade("0xflip", "14", "SELL", "SELL", "0.01", "3.2", Now.AddMinutes(-5), "0xorder3")}},
          {{Trade("0xretired", "13", "BUY", "BUY", "0.01", "-0.01", Now.AddDays(-200), "0xorder4")}},
          {{Trade("0xunknown", "999", "BUY", "BUY", "0.01", "-0.01", Now.AddMinutes(-1), "0xorder5")}}],"page":1,"has_next_page":false},"request_id":"r"}
        """;

    private static readonly string Orders = $$"""
        {"data":{"orders":[
          {"id":"0xorder1","price":"86000.5","size":"0.01","market_id":"1","side":"BUY","type":"LIMIT","time_in_force":"GTC","expiry":"0",
           "post_only":false,"reduce_only":false,"cancel_reason":"","filled_size":"0.01","status":"ORDER_STATUS_FILLED","fee_bps":"300",
           "avg_price":"86000.5","cancel_requested":false,"created_at":"{{Ns(Now.AddMinutes(-31))}}","cancel_requested_at":"0",
           "stop_type":"STOP_TYPE_NONE","stop_price":"","stop_price_option":"PRICE_OPTION_NONE","is_liquidation":false},
          {"id":"0xorder9","price":"90000","size":"0.01","market_id":"1","side":"SELL","type":"LIMIT","time_in_force":"GTC","expiry":"0",
           "post_only":false,"reduce_only":true,"cancel_reason":"","filled_size":"0","status":"ORDER_STATUS_OPEN","fee_bps":"300",
           "avg_price":"0","cancel_requested":false,"created_at":"{{Ns(Now.AddMinutes(-20))}}","cancel_requested_at":"0",
           "stop_type":"STOP_TYPE_NONE","stop_price":"","stop_price_option":"PRICE_OPTION_NONE","is_liquidation":false}],
          "page":1,"has_next_page":false},"request_id":"r"}
        """;

    private static readonly string Tpsl = $$"""
        {"data":{"orders":[
          {"order_id":"0xtpsl1","account":"{{Address}}","market_id":"1","side":"SELL","size":"0","size_percent_bps":10000,
           "stop_type":"STOP_LOSS","stop_price":"80000","limit_price":"","order_type":"MARKET","status":"TPSL_ORDER_STATUS_ACCEPTED",
           "created_at":"{{Ns(Now.AddMinutes(-15))}}","triggered_at":"0","tif":"GTC"}],"total":"1","page":1,"limit":1000},"request_id":"r"}
        """;

    private sealed class Routes(Dictionary<string, string> bodies) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            var path = request.RequestUri.AbsolutePath;
            var body = bodies.TryGetValue(path, out var found) ? found : "{}";
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static (RisexReader Reader, Routes Routes) Reader(Dictionary<string, string>? extra = null, RisexCatalogueCache? cache = null)
    {
        var bodies = new Dictionary<string, string>
        {
            ["/v1/markets"] = Markets, ["/v1/portfolio/details"] = Portfolio, ["/v1/trade-history"] = Trades,
            ["/v1/orders"] = Orders, ["/v1/orders/tpsl"] = Tpsl,
        };
        foreach (var (path, body) in extra ?? []) bodies[path] = body;
        var routes = new Routes(bodies);
        var time = new FixedTime(Now);
        return (new RisexReader(new HttpClient(routes) { BaseAddress = new Uri("https://api.rise.trade/") }, time, cache ?? new RisexCatalogueCache(time)), routes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Imports_refresh_a_warm_catalogue_before_filtering_new_market_facts(bool orders)
    {
        var cache = new RisexCatalogueCache(new FixedTime(Now));
        var oldMarkets = JsonNode.Parse(Markets)!;
        oldMarkets["data"]!["markets"]!.AsArray().RemoveAt(0); // BTC was not listed when the cache was filled.
        await Reader(new() { ["/v1/markets"] = oldMarkets.ToJsonString() }, cache).Reader.ReadInstrumentsAsync(default);
        var (reader, routes) = Reader(cache: cache);
        if (orders)
        {
            var result = await reader.ReadOrdersAsync(Address, default);
            Assert.Contains(result.Orders, o => o.OrderId == "0xorder1");
            Assert.Contains(result.Orders, o => o.OrderId == "0xtpsl1");
        }
        else
        {
            var result = await reader.ReadAsync(Address, default);
            Assert.Equal(4, result.Fills.Count);
            Assert.Contains(result.Fills, f => f.SourceFillId == "0xopen");
            Assert.Contains(result.Snapshot.Positions, p => p.VenueContractId == "1");
        }
        Assert.Single(routes.Requests, r => r == "GET /v1/markets");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_import_catalogue_refresh_never_falls_back_to_stale_market_mapping(bool orders)
    {
        var cache = new RisexCatalogueCache(new FixedTime(Now));
        await Reader(cache: cache).Reader.ReadInstrumentsAsync(default);
        var (reader, routes) = Reader(new() { ["/v1/markets"] = "{}" }, cache);
        await Assert.ThrowsAsync<VenueReadException>(async () =>
        {
            if (orders) await reader.ReadOrdersAsync(Address, default);
            else await reader.ReadAsync(Address, default);
        });
        Assert.Equal(["GET /v1/markets"], routes.Requests);
    }

    [Theory]
    [InlineData("fee", "missing")]
    [InlineData("fee", "null")]
    [InlineData("fee", "empty")]
    [InlineData("realized_pnl", "missing")]
    [InlineData("realized_pnl", "null")]
    [InlineData("realized_pnl", "empty")]
    public async Task Missing_execution_money_is_not_zeroed_even_on_an_opening_trade(string field, string value)
    {
        var history = JsonNode.Parse(Trades)!;
        var trade = history["data"]!["trades"]![0]!.AsObject();
        if (value == "missing") trade.Remove(field);
        else trade[field] = value == "null" ? null : JsonValue.Create("");
        var reader = Reader(new() { ["/v1/trade-history"] = history.ToJsonString() }).Reader;
        var error = await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadAsync(Address, default));
        Assert.Equal(field == "realized_pnl"
            ? "RISEx omitted realized PnL for an execution. Its net-of-fee result is unknown; this refresh was not imported."
            : "RISEx returned an invalid or unsupported response.", error.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]
    [InlineData("\"0\"", "0")]
    [InlineData("\"-0.0000000000000000000000000001\"", "-0.0000000000000000000000000001")]
    [InlineData("\"131.89123456789012345678901234\"", "131.89123456789012345678901234")]
    public async Task Available_margin_preserves_reported_value_and_distinguishes_missing_from_zero(string? field, string? expected)
    {
        var body = Portfolio.Replace("\"free_collateral\":\"131.89\",", field is null ? "" : "\"free_collateral\":" + field + ",");
        var snapshot = (await Reader(new() { ["/v1/portfolio/details"] = body }).Reader.ReadAsync(Address, default)).Snapshot;
        Assert.Equal(expected is null ? (decimal?)null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), snapshot.AvailableMarginUsd);
        Assert.Null(snapshot.WithdrawableUsd);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("\" \"")]
    [InlineData("\"1e3\"")]
    [InlineData("\"0.00000000000000000000000000001\"")]
    public async Task Invalid_available_margin_rejects_the_snapshot_without_rounding(string field)
    {
        var body = Portfolio.Replace("\"free_collateral\":\"131.89\",", field is null ? "" : "\"free_collateral\":" + field + ",");
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(new() { ["/v1/portfolio/details"] = body }).Reader.ReadAsync(Address, default));
    }

    [Fact]
    public async Task A_negative_liquidation_price_is_an_invalid_response_never_unknown()
    {
        // Phase 2, item 8a (Sol round 1 on #41): zero means none; a negative price was silently read as unknown.
        var body = Portfolio.Replace("\"liquidation_price\":\"73336.01\"", "\"liquidation_price\":\"-1\"");
        Assert.NotEqual(Portfolio, body);
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(new() { ["/v1/portfolio/details"] = body }).Reader.ReadAsync(Address, default));
    }


    [Fact]
    public async Task Catalogue_maps_markets_to_assets_with_ticks_categories_and_market_ids_and_hides_retired_ones()
    {
        var instruments = await Reader().Reader.ReadInstrumentsAsync(default);
        Assert.Equal(["BTC", "DOGE", "TSLA"], instruments.Select(i => i.ContractId));
        var btc = instruments[0];
        Assert.Equal((6, 25, "USDC", 0.1m, "crypto", "1"), (btc.QuantityDecimals, btc.MaxLeverage, btc.QuoteAsset, btc.PriceStep, btc.Category, btc.VenueContractId));
        var doge = instruments[1];
        Assert.Equal((0.00001m, "14", 2), (doge.PriceStep, doge.VenueContractId, doge.QuantityDecimals));
        Assert.Equal("stocks", instruments[2].Category);
    }

    [Fact]
    public async Task Two_active_markets_for_one_asset_are_refused_rather_than_guessed()
    {
        var duplicate = $$"""{"data":{"markets":[{{Market("1", "BTC/USDC")}},{{Market("2", "BTC/USDC")}}]},"request_id":"r"}""";
        var error = await Assert.ThrowsAsync<VenueReadException>(() => Reader(new() { ["/v1/markets"] = duplicate }).Reader.ReadInstrumentsAsync(default));
        Assert.Equal("RISEx returned an invalid or unsupported response.", error.Message);
    }

    [Fact]
    public async Task Refresh_reads_signed_positions_equity_and_fills_in_vessels_vocabulary()
    {
        var (reader, routes) = Reader();
        var result = await reader.ReadAsync(Address.ToUpperInvariant().Replace("0X", "0x"), default);
        Assert.Contains($"GET /v1/portfolio/details?account={Address}", routes.Requests);
        Assert.Contains($"GET /v1/trade-history?account={Address}&limit=1000&page=1", routes.Requests);
        Assert.All(routes.Requests, request => Assert.StartsWith("GET ", request));

        var snapshot = result.Snapshot;
        Assert.Equal(12183.268781664941504325m, snapshot.AccountValueUsd);
        Assert.Null(snapshot.WithdrawableUsd);
        Assert.Equal(131.89m, snapshot.AvailableMarginUsd);
        Assert.Equal(12051.377019174158945586m, snapshot.MarginUsedUsd);
        Assert.Null(snapshot.StablecoinWallet);
        Assert.Equal([("BTC", 0.702475m, 10), ("DOGE", -2000m, 5)], snapshot.Positions.Select(p => (p.ContractId, p.SignedQuantity, p.Leverage!.Value)));
        Assert.Equal(["1", "14"], snapshot.Positions.Select(p => p.VenueContractId));
        // Phase 2, item 8a: the reported liquidation price and margin mode (0 is cross).
        Assert.Equal([(73336.01m, MarginModes.Cross), (0.11m, MarginModes.Cross)],
            snapshot.Positions.Select(p => (p.LiquidationPrice!.Value, p.MarginMode!)));

        var fills = result.Fills.ToDictionary(f => f.SourceFillId);
        Assert.Equal(4, fills.Count); // the unknown market is left out
        Assert.All(fills.Values, f => Assert.True(VenueFactChecks.Valid(f)));
        Assert.Equal((ExecutionFacts.Buy, ExecutionFacts.Open, "Open Long", "1"), (fills["0xopen"].Side, fills["0xopen"].PositionEffect, fills["0xopen"].Direction, fills["0xopen"].VenueContractId));
        Assert.Equal((ExecutionFacts.Sell, ExecutionFacts.Close, "Close Long", 12.5m), (fills["0xclose"].Side, fills["0xclose"].PositionEffect, fills["0xclose"].Direction, fills["0xclose"].ClosedPnlUsd));
        // Same side as the position with PnL beyond the fee: probably a flip, not verified, so unknown.
        Assert.Equal((ExecutionFacts.Unknown, "Sell"), (fills["0xflip"].PositionEffect, fills["0xflip"].Direction));
        Assert.Equal(("DOGE", "13"), (fills["0xretired"].ContractId, fills["0xretired"].VenueContractId));
        Assert.All(fills.Values, f => Assert.Equal((ExecutionFacts.PnlNetOfFee, ExecutionFacts.FeeReported, "USDC"), (f.PnlBasis, f.FeeBasis, f.FeeToken)));
        Assert.Equal(Now.AddMinutes(-30), fills["0xopen"].OccurredAtUtc);
        Assert.Equal("0xhash0xopen", fills["0xopen"].TransactionHash);
        Assert.Contains("net of fees", result.HistoryNotice);
    }

    [Theory]
    [InlineData("\"10\"", 10)]
    [InlineData("\"25.0\"", 25)]
    [InlineData("\"2.5\"", null)]
    [InlineData("\"0\"", null)]
    [InlineData("\"-3\"", null)]
    [InlineData("\"1001\"", null)]
    [InlineData("\"\"", null)]
    [InlineData("null", null)]
    [InlineData(null, null)]
    public async Task Fill_leverage_is_kept_only_as_a_reported_whole_number(string? field, int? expected)
    {
        var history = JsonNode.Parse(Trades)!;
        var trade = history["data"]!["trades"]![0]!.AsObject();
        if (field is null) trade.Remove("leverage");
        else trade["leverage"] = JsonNode.Parse(field);
        var result = await Reader(new() { ["/v1/trade-history"] = history.ToJsonString() }).Reader.ReadAsync(Address, default);
        var fill = result.Fills.Single(f => f.SourceFillId == "0xopen");
        Assert.Equal(expected, fill.Leverage);
        Assert.True(VenueFactChecks.Valid(fill));
        Assert.Equal(10, result.Fills.Single(f => f.SourceFillId == "0xclose").Leverage);
    }

    [Fact]
    public async Task Malformed_fill_leverage_is_an_invalid_response_not_a_guess()
    {
        var history = JsonNode.Parse(Trades)!;
        history["data"]!["trades"]![0]!["leverage"] = "ten";
        var reader = Reader(new() { ["/v1/trade-history"] = history.ToJsonString() }).Reader;
        Assert.Equal("RISEx returned an invalid or unsupported response.",
            (await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadAsync(Address, default))).Message);
    }

    [Fact]
    public void Fact_checks_reject_leverage_outside_the_whole_number_range()
    {
        var fill = new VenueFill("f", "BTC", ExecutionFacts.Buy, "Open Long", 1, 1, 0, "USDC", 0, Now, "o", "h", "{}", ExecutionFacts.Open);
        Assert.True(VenueFactChecks.Valid(fill with { Leverage = 1 }));
        Assert.True(VenueFactChecks.Valid(fill with { Leverage = 1000 }));
        Assert.False(VenueFactChecks.Valid(fill with { Leverage = 0 }));
        Assert.False(VenueFactChecks.Valid(fill with { Leverage = 1001 }));
    }

    [Fact]
    public async Task Orders_include_tpsl_records_as_trigger_orders()
    {
        var read = await Reader().Reader.ReadOrdersAsync(Address, default);
        var orders = read.Orders.ToDictionary(o => o.OrderId);
        Assert.All(orders.Values, o => Assert.True(VenueFactChecks.Valid(o)));
        Assert.Equal((VenueOrderStatus.Filled, "Limit", 0m, (decimal?)null), (orders["0xorder1"].Status, orders["0xorder1"].OrderType, orders["0xorder1"].RemainingSize, orders["0xorder1"].TriggerPrice));
        Assert.Equal((VenueOrderStatus.Open, true, ExecutionFacts.Sell), (orders["0xorder9"].Status, orders["0xorder9"].ReduceOnly, orders["0xorder9"].Side));
        var stop = orders["0xtpsl1"];
        Assert.Equal((VenueOrderStatus.Open, "Stop Market", 80000m, true, true), (stop.Status, stop.OrderType, stop.TriggerPrice, stop.ReduceOnly, stop.IsPositionTpsl));
        Assert.Equal("1", stop.VenueContractId);
    }

    [Fact]
    public async Task Candles_use_market_id_nanoseconds_and_refuse_a_fallback_interval()
    {
        var start = Now.AddHours(-2);
        string Row(string interval, DateTimeOffset at) =>
            $$"""{"market_id":"14","interval":"{{interval}}","time":"{{Ns(at)}}","low":"0.0940","high":"0.0950","open":"0.0945","close":"0.0948","volume":"1000"}""";
        // The second row opens at the previous close, above its own high, as RISEx reports it.
        var carried = $$"""{"market_id":"14","interval":"1h","time":"{{Ns(start.AddHours(1))}}","low":"0.0940","high":"0.0947","open":"0.0948","close":"0.0941","volume":"5"}""";
        var good = $$"""{"data":{"data":[{{Row("1h", start)}},{{carried}}]},"request_id":"r"}""";
        var (reader, routes) = Reader(new() { ["/v1/markets/id/14/trading-view-data"] = good });
        var candles = await reader.ReadCandlesAsync("DOGE", "1h", start.ToUnixTimeMilliseconds(), Now.ToUnixTimeMilliseconds(), default);
        Assert.Equal(2, candles.Count);
        Assert.Equal((start.ToUnixTimeMilliseconds(), start.ToUnixTimeMilliseconds() + 3_599_999, "0.0945", (int?)null),
            (candles[0].OpenTime, candles[0].CloseTime, candles[0].Open, candles[0].Trades));
        Assert.Contains(routes.Requests, r => r.Contains($"/v1/markets/id/14/trading-view-data?interval=3600000000000&from={start.ToUnixTimeMilliseconds() * 1_000_000}"));

        // RISEx answers an unsupported interval with 1m rows; a mislabelled row is never accepted.
        var fallback = $$"""{"data":{"data":[{{Row("1m", start)}}]},"request_id":"r"}""";
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(new() { ["/v1/markets/id/14/trading-view-data"] = fallback }).Reader
            .ReadCandlesAsync("DOGE", "1h", start.ToUnixTimeMilliseconds(), Now.ToUnixTimeMilliseconds(), default));
        Assert.Equal("0.0948", candles[1].Open);
        var broken = $$"""{"data":{"data":[{"market_id":"14","interval":"1h","time":"{{Ns(start)}}","low":"0.0940","high":"0.0947","open":"0.0945","close":"0.0950","volume":"5"}]},"request_id":"r"}""";
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(new() { ["/v1/markets/id/14/trading-view-data"] = broken }).Reader
            .ReadCandlesAsync("DOGE", "1h", start.ToUnixTimeMilliseconds(), Now.ToUnixTimeMilliseconds(), default));
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadCandlesAsync("DOGE", "3m", 0, 1, default));
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadCandlesAsync("NOPE", "1h", 0, 1, default));
    }

    [Fact]
    public async Task Market_context_keeps_exact_strings_and_leaves_unreported_fields_unknown()
    {
        var contexts = await Reader().Reader.ReadMarketContextsAsync(default);
        var btc = contexts.Single(c => c.ContractId == "BTC");
        Assert.Equal(("86627.713809202553651873", "86659.469794009055", "35255149.8695194", "187.059842", "0.000004298138961497"),
            (btc.MarkPrice, btc.OraclePrice, btc.DayNotionalVolume, btc.OpenInterest, btc.FundingRate));
        Assert.Null(btc.PreviousDayPrice);
        Assert.Null(btc.MidPrice);
        Assert.DoesNotContain(contexts, c => c.ContractId == "weird name");
        Assert.Equal(3, contexts.Count);
    }

    [Fact]
    public async Task Failures_are_safe_messages()
    {
        var (reader, routes) = Reader();
        routes.Status = HttpStatusCode.Forbidden;
        var error = await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadAsync(Address, default));
        Assert.Equal("RISEx is unavailable. Try again later.", error.Message);
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadAsync("0x12", default));
        var bad = Reader(new() { ["/v1/portfolio/details"] = """{"data":{"summary":{},"positions":[]},"request_id":"r"}""" }).Reader;
        Assert.Equal("RISEx returned an invalid or unsupported response.", (await Assert.ThrowsAsync<VenueReadException>(() => bad.ReadAsync(Address, default))).Message);
    }

    [Fact]
    public async Task Each_venue_keeps_its_own_http_client_configuration()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();
        Assert.Equal("https://api.rise.trade/", clients.CreateClient("risex").BaseAddress!.AbsoluteUri);
        Assert.Equal("https://api.hyperliquid.xyz/", clients.CreateClient("hyperliquid").BaseAddress!.AbsoluteUri);
        Assert.Equal("https://mainnet.zklighter.elliot.ai/", clients.CreateClient("lighter").BaseAddress!.AbsoluteUri);
        Assert.Contains("Vessel", clients.CreateClient("risex").DefaultRequestHeaders.UserAgent.ToString());
        Assert.DoesNotContain("Vessel", clients.CreateClient("hyperliquid").DefaultRequestHeaders.UserAgent.ToString());
        // Only Lighter's client adds the token, never follows redirects, and no venue client logs requests.
        var handlers = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        var lighter = Handlers(handlers.CreateHandler("lighter"));
        Assert.Single(lighter.OfType<Vessel.Infrastructure.Venues.Lighter.LighterAuthenticationHandler>());
        Assert.False(Assert.Single(lighter.OfType<HttpClientHandler>()).AllowAutoRedirect);
        foreach (var venue in new[] { "hyperliquid", "risex" })
            Assert.DoesNotContain(Handlers(handlers.CreateHandler(venue)), h => h is Vessel.Infrastructure.Venues.Lighter.LighterAuthenticationHandler);
        foreach (var venue in new[] { "hyperliquid", "risex", "lighter" })
            Assert.DoesNotContain(Handlers(handlers.CreateHandler(venue)), h => h.GetType().Name.Contains("Logging", StringComparison.Ordinal));
        using var scope = factory.Services.CreateScope();
        var venues = scope.ServiceProvider.GetRequiredService<IVenueRegistry>();
        Assert.Equal("RISEx", venues.Descriptor("risex")!.Name);
        Assert.Equal(["hyperliquid", "risex", "lighter"],
            new[] { "hyperliquid", "risex", "lighter" }.Where(id => venues.Reader(id)?.VenueId == id));
        Assert.Equal("risex", venues.Orders("risex")!.VenueId);
        Assert.Null(venues.Stream("risex"));
        // Every port of one venue is served by the same scoped adapter.
        Assert.Same(venues.Reader("lighter"), venues.CredentialVerifier("lighter"));
        Assert.Same(venues.Reader("lighter"), venues.Discovery("lighter"));
        Assert.Same(venues.Reader("hyperliquid"), venues.MarketContext("hyperliquid"));
        Assert.Null(venues.MarketContext("lighter"));
        Assert.Null(venues.Discovery("risex")); Assert.Null(venues.CredentialVerifier("hyperliquid"));
    }

    private static List<HttpMessageHandler> Handlers(HttpMessageHandler? handler)
    {
        var chain = new List<HttpMessageHandler>();
        for (; handler is not null; handler = (handler as DelegatingHandler)?.InnerHandler) chain.Add(handler);
        return chain;
    }
}
