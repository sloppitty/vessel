using System.Net;
using System.Text.Json.Nodes;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Pacifica;

namespace Vessel.Tests;

public sealed class PacificaAdapterTests
{
    // A synthetic public identity, never used to query the live API.
    internal const string Source = "So11111111111111111111111111111111111111112";
    internal static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1792742400000);
    internal static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pacifica", "read-contract.json")))!.AsObject();
    internal sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    internal sealed class Routes(JsonObject fixture) : HttpMessageHandler
    {
        internal readonly List<string> Requests = [];
        internal Func<HttpRequestMessage, string?>? Override { get; set; }
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Requests.Add(request.RequestUri!.PathAndQuery);
            var key = request.RequestUri.AbsolutePath switch
            {
                "/api/v1/info" => "info", "/api/v1/account" => "account", "/api/v1/positions" => "positions",
                "/api/v1/trades/history" => "trades", "/api/v1/orders" => "orders",
                "/api/v1/orders/history" => "history", "/api/v1/kline" => "candles", _ => throw new Exception("Unexpected route")
            };
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Override?.Invoke(request) ?? fixture[key]!.ToJsonString()) };
            if (Status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(35));
            return Task.FromResult(response);
        }
    }
    internal static (PacificaReader Reader, Routes Routes) Reader(JsonObject? fixture = null, PacificaCatalogueCache? cache = null)
    {
        var routes = new Routes(fixture ?? Fixture());
        return (new(new HttpClient(routes) { BaseAddress = new Uri("https://synthetic.invalid") }, new Clock(), cache ?? new(new Clock())), routes);
    }

    [Fact]
    public async Task Public_refresh_preserves_exact_source_native_symbols_ids_net_fees_and_raw_coverage()
    {
        var fixture = Fixture();
        fixture["trades"]!["data"]![0]!["history_id"] = 9007199254740993L;
        var (reader, routes) = Reader(fixture);
        var result = await reader.ReadAsync(Source, default);
        Assert.Equal(Source, System.Web.HttpUtility.ParseQueryString(new Uri("https://synthetic.invalid" +
            routes.Requests.Single(p => p.StartsWith("/api/v1/account?", StringComparison.Ordinal))).Query)["account"]);
        Assert.Equal(1000.123456m, result.Snapshot.AccountValueUsd);
        Assert.Equal(900.000001m, result.Snapshot.AvailableMarginUsd);
        Assert.Null(result.Snapshot.StablecoinWallet);
        Assert.True(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.False(result.FillHistoryTruncated);
        Assert.NotNull(result.OldestRawFillAtUtc);
        Assert.Equal(0, result.OmittedExecutions);
        Assert.Equal("9007199254740993", result.Fills[0].SourceFillId);
        Assert.Collection(result.Fills,
            f => { Assert.Equal(ExecutionFacts.Buy, f.Side); Assert.Equal(ExecutionFacts.Open, f.PositionEffect); Assert.Equal(-0.1m, f.ClosedPnlUsd); },
            f => { Assert.Equal(ExecutionFacts.Sell, f.Side); Assert.Equal(ExecutionFacts.Close, f.PositionEffect); },
            f => { Assert.Equal(ExecutionFacts.Sell, f.Side); Assert.Equal(ExecutionFacts.Open, f.PositionEffect); },
            f => { Assert.Equal(ExecutionFacts.Buy, f.Side); Assert.Equal(ExecutionFacts.Close, f.PositionEffect); });
        Assert.All(result.Fills, f =>
        {
            Assert.True(VenueFactChecks.Valid(f)); Assert.Equal("BTC", f.VenueContractId);
            Assert.Equal(ExecutionFacts.PnlNetOfFee, f.PnlBasis);
            Assert.Equal(ExecutionFacts.FeeReported, f.FeeBasis);
            Assert.Equal("USDC", f.FeeToken); Assert.Null(f.Leverage);
        });
        Assert.Null(result.Funding);
        Assert.Contains("payout sign is unverified", result.HistoryNotice);
        var pepe = Assert.Single(result.Instruments, i => i.VenueContractId == "kPEPE");
        Assert.Equal("1000PEPE", pepe.ContractId); Assert.Equal(0.000001m, pepe.PriceStep);
        Assert.DoesNotContain(result.Instruments, i => i.VenueContractId == "SOL-USDC");
    }

    [Theory]
    [InlineData("fee", null)]
    [InlineData("fee", "")]
    [InlineData("fee", "-0.1")]
    [InlineData("pnl", null)]
    [InlineData("pnl", "")]
    [InlineData("cause", "market_liquidation")]
    [InlineData("cause", "backstop_liquidation")]
    [InlineData("cause", "settlement")]
    [InlineData("cause", "correction")]
    [InlineData("side", "flip")]
    [InlineData("event_type", "correction")]
    [InlineData("symbol", "retired-native-market")]
    [InlineData("order_id", "0")]
    public async Task Unsupported_executions_are_counted_once_not_guessed_and_keep_raw_boundary(string field, string? value)
    {
        var fixture = Fixture();
        var rows = fixture["trades"]!["data"]!.AsArray();
        rows[0]![field] = value;
        rows[0]!["created_at"] = Now.AddDays(-200).ToUnixTimeMilliseconds();
        rows.Add(rows[0]!.DeepClone());
        var result = await Reader(fixture).Reader.ReadAsync(Source, default);
        Assert.Equal(3, result.Fills.Count); Assert.Equal(1, result.OmittedExecutions);
        Assert.Equal(Now.AddDays(-200), result.OldestRawFillAtUtc);
        Assert.False(result.FillHistoryComplete); Assert.False(result.FillHistoryTruncated);
    }

    [Fact]
    public async Task Missing_fields_never_default_to_zero_and_unknown_excluded_timestamp_clears_boundary()
    {
        var fixture = Fixture();
        var row = fixture["trades"]!["data"]![0]!.AsObject();
        row.Remove("pnl"); row.Remove("created_at");
        var result = await Reader(fixture).Reader.ReadAsync(Source, default);
        Assert.Equal(1, result.OmittedExecutions); Assert.Null(result.OldestRawFillAtUtc);
        Assert.Equal(3, result.Fills.Count);
    }

    [Fact]
    public async Task Pagination_uses_opaque_escaped_cursors_deduplicates_and_never_claims_lifetime_coverage()
    {
        var fixture = Fixture();
        var first = fixture["trades"]!.DeepClone();
        first["has_more"] = true; first["next_cursor"] = "a+/=&?";
        var (reader, routes) = Reader(fixture);
        routes.Override = req => req.RequestUri!.AbsolutePath == "/api/v1/trades/history" &&
            !req.RequestUri.Query.Contains("cursor=", StringComparison.Ordinal) ? first.ToJsonString() : null;
        var result = await reader.ReadAsync(Source, default);
        Assert.Equal(4, result.Fills.Count);
        Assert.False(result.FillHistoryComplete); Assert.False(result.FillHistoryTruncated);
        Assert.Equal(2, routes.Requests.Count(p => p.StartsWith("/api/v1/trades/history", StringComparison.Ordinal)));
        Assert.Contains(routes.Requests, p => p.Contains("cursor=a%2B%2F%3D%26%3F", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ten_page_raw_cap_is_truncated_even_when_all_facts_are_omitted()
    {
        var fixture = Fixture();
        fixture["trades"]!["data"]!.AsArray().Clear();
        var template = Fixture()["trades"]!["data"]![0]!;
        var (reader, routes) = Reader(fixture);
        var pages = 0;
        routes.Override = req =>
        {
            if (req.RequestUri!.AbsolutePath != "/api/v1/trades/history") return null;
            var row = template.DeepClone(); row["history_id"] = ++pages; row["cause"] = "settlement";
            return new JsonObject { ["success"] = true, ["data"] = new JsonArray(row), ["has_more"] = true, ["next_cursor"] = $"opaque-{pages}" }.ToJsonString();
        };
        var result = await reader.ReadAsync(Source, default);
        Assert.True(result.FillHistoryTruncated); Assert.Equal(10, pages);
        Assert.Equal(10, result.OmittedExecutions); Assert.Empty(result.Fills);
    }

    [Fact]
    public async Task Repeated_cursor_or_conflicting_duplicate_is_a_safe_failure()
    {
        var fixture = Fixture();
        fixture["trades"]!["has_more"] = true; fixture["trades"]!["next_cursor"] = "loop";
        var repeated = await Assert.ThrowsAsync<VenueReadException>(() => Reader(fixture).Reader.ReadAsync(Source, default));
        Assert.DoesNotContain(Source, repeated.Message);
        fixture = Fixture();
        var rows = fixture["trades"]!["data"]!.AsArray();
        var correction = rows[0]!.DeepClone(); correction["pnl"] = "10"; rows.Add(correction);
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(fixture).Reader.ReadAsync(Source, default));
    }

    [Fact]
    public async Task Open_positions_are_incomplete_not_fabricated_zero_pnl_margin_or_max_leverage()
    {
        var fixture = Fixture();
        fixture["positions"]!["data"]!.AsArray().Add(new JsonObject
            { ["symbol"] = "BTC", ["side"] = "bid", ["amount"] = "1", ["entry_price"] = "100", ["margin"] = "0", ["isolated"] = false });
        fixture["account"]!["data"]!["positions_count"] = 1;
        var result = await Reader(fixture).Reader.ReadAsync(Source, default);
        Assert.False(result.PositionsComplete); Assert.Empty(result.Snapshot.Positions);
        Assert.Contains("Leverage is not inferred", result.HistoryNotice);
    }

    [Theory]
    [InlineData("spot_market_value")]
    [InlineData("spot_collateral")]
    [InlineData("pending_interest")]
    public async Task Unsupported_unified_spot_or_lending_valuation_is_not_silently_imported(string key)
    {
        var fixture = Fixture(); fixture["account"]!["data"]![key] = "1";
        Assert.Contains("No refresh was imported", (await Assert.ThrowsAsync<VenueReadException>(
            () => Reader(fixture).Reader.ReadAsync(Source, default))).Message);
    }

    [Fact]
    public async Task Current_and_historical_orders_are_distinct_and_partial_book_never_proves_absence()
    {
        var result = await Reader().Reader.ReadOrdersAsync(Source, default);
        Assert.False(result.OpenOrdersComplete);
        Assert.Equal(VenueOrderStatus.Filled, Assert.Single(result.Orders, o => o.OrderId == "101").Status);
        var current = Assert.Single(result.Orders, o => o.OrderId == "999");
        Assert.Equal(VenueOrderStatus.Open, current.Status); Assert.Equal(105m, current.TriggerPrice);
        Assert.True(current.ReduceOnly); Assert.Equal(0.01m, current.RemainingSize);
        Assert.Contains("TWAP/strategy", result.Notice);
    }

    [Fact]
    public async Task Current_order_read_failure_does_not_turn_historical_open_into_current_coverage()
    {
        var (reader, routes) = Reader();
        routes.Override = req => req.RequestUri!.AbsolutePath == "/api/v1/orders" ? """{"success":false,"data":[]}""" : null;
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadOrdersAsync(Source, default));
    }

    [Fact]
    public async Task Candles_preserve_strings_native_symbols_and_refuse_fallback_intervals()
    {
        var (reader, routes) = Reader();
        var candle = Assert.Single(await reader.ReadCandlesAsync("BTC", "1m", Now.AddMinutes(-1).ToUnixTimeMilliseconds(), Now.ToUnixTimeMilliseconds(), default));
        Assert.Equal("100.00", candle.Open); Assert.Equal("0.01000001", candle.Volume);
        Assert.Equal(Now.ToUnixTimeMilliseconds() - 1, candle.CloseTime);
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadCandlesAsync("BTC", "3d", 0, 1, default));
        Assert.Single(routes.Requests, p => p.StartsWith("/api/v1/kline?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Canonical_thousand_contract_uses_native_symbol_on_candles_fills_and_trade_links()
    {
        var fixture = Fixture();
        fixture["candles"]!["data"]![0]!["s"] = "kPEPE";
        fixture["trades"]!["data"]![0]!["symbol"] = "kPEPE";
        var (reader, routes) = Reader(fixture);
        var fill = (await reader.ReadAsync(Source, default)).Fills[0];
        Assert.Equal("1000PEPE", fill.ContractId); Assert.Equal("kPEPE", fill.VenueContractId);
        Assert.Single(await reader.ReadCandlesAsync("1000PEPE", "1m", Now.AddMinutes(-1).ToUnixTimeMilliseconds(), Now.ToUnixTimeMilliseconds(), default));
        Assert.Contains(routes.Requests, p => p.Contains("symbol=kPEPE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Metadata_refresh_observes_new_markets_and_never_guesses_an_unverified_thousand_denomination()
    {
        var fixture = Fixture();
        var (reader, _) = Reader(fixture);
        Assert.DoesNotContain(await reader.ReadInstrumentsAsync(default), i => i.ContractId == "NEW");
        var market = fixture["info"]!["data"]![0]!.DeepClone(); market["symbol"] = "NEW";
        fixture["info"]!["data"]!.AsArray().Add(market);
        var unverified = market.DeepClone(); unverified["symbol"] = "kSHIB";
        fixture["info"]!["data"]!.AsArray().Add(unverified);
        fixture["trades"]!["data"]![0]!["symbol"] = "NEW";
        var result = await reader.ReadAsync(Source, default);
        Assert.Contains(result.Instruments, i => i.ContractId == "NEW");
        Assert.DoesNotContain(result.Instruments, i => i.VenueContractId == "kSHIB");
        Assert.Equal("NEW", result.Fills[0].ContractId);
    }

    [Theory]
    [InlineData("""{"success":false,"data":[]}""")]
    [InlineData("""{"success":true,"success":false,"data":[]}""")]
    [InlineData("""{"success":true,"data":[{"symbol":"BTC","instrument_type":"future"}]}""")]
    public async Task Invalid_envelopes_duplicate_properties_and_unknown_products_fail_safely(string body)
    {
        var (reader, routes) = Reader(); routes.Override = _ => body;
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadAsync(Source, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Http_failures_are_sanitized_and_rate_limits_keep_retry_deadlines(HttpStatusCode code)
    {
        var (reader, routes) = Reader(); routes.Status = code;
        var failure = await Assert.ThrowsAnyAsync<VenueReadException>(() => reader.ReadAsync(Source, default));
        Assert.DoesNotContain(Source, failure.Message);
        if (code == HttpStatusCode.TooManyRequests) Assert.Equal(Now.AddSeconds(35), Assert.IsType<VenueRateLimitException>(failure).RetryAtUtc);
    }

    [Fact]
    public async Task Source_validation_precedes_any_http_and_cancellation_propagates()
    {
        var (reader, routes) = Reader();
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadAsync("0x" + new string('1', 40), default));
        Assert.Empty(routes.Requests);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(Source, cancel.Token));
    }
}
