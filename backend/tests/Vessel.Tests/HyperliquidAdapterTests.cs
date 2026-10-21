using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed partial class HyperliquidAdapterTests
{
    // Synthetic fixtures only. All HttpClients below use an intercepting handler.
    private const string Address = "0x0123456789abcdef0123456789ABCDEF01234567";
    private const long StateTime = 1790812800000; // October 1, 2026 UTC
    private const string Meta = """
        {"universe":[
          {"name":"BTC","szDecimals":5,"maxLeverage":40},
          {"name":"ETH","szDecimals":4,"maxLeverage":20,"isDelisted":true}
        ]}
        """;
    private const string State = """
        {
          "marginSummary":{"accountValue":"12345.678901234567890123","totalMarginUsed":"250.123456789"},
          "crossMarginSummary":{"accountValue":"99999","totalMarginUsed":"999"},
          "withdrawable":"12095.555444445567890123","time":1790812800000,
          "assetPositions":[
            {"type":"oneWay","position":{
              "coin":"BTC","szi":"-0.01234567890123456789","entryPx":"61234.1234567890123456",
              "unrealizedPnl":"-12.987654321","marginUsed":"250.123456789","liquidationPx":"73336.0123456789",
              "leverage":{"type":"isolated","value":5,"rawUsd":"-5.1"}
            }},
            {"type":"oneWay","position":{"coin":"ETH","szi":"0","entryPx":null}},
            {"type":"oneWay","position":{"coin":"@123"}},
            {"type":"oneWay","position":{"coin":"PURR/USDC"}},
            {"type":"oneWay","position":{"coin":"xyz:BTC"}},
            {"type":"oneWay","position":{"coin":"UNKNOWN"}}
          ]
        }
        """;
    private const string Fill = """
        {
          "coin":"BTC","px":"61235.1234567890123456","sz":"0.001234567890123456789",
          "side":"B","dir":"Close Short","time":1790812799000,"startPosition":"-0.02",
          "closedPnl":"1.234567890123456789","fee":"-0.000123456789","feeToken":"USDC",
          "oid":9007199254740993,"tid":18446744073709551615,
          "hash":"0xsynthetic-transaction","crossed":false,"builderFee":"0.00001"
        }
        """;

    [Fact]
    public async Task Reads_exact_primary_perpetual_facts_and_only_read_only_info_requests()
    {
        var olderFill = Mutate(Fill, node =>
        {
            node["time"] = StateTime - 2000;
            node["tid"] = "9007199254740995";
            node["oid"] = "18446744073709551614";
            node["side"] = "A";
            node["dir"] = "Open Short";
        });
        var response = $"[{olderFill},{Fill},{{\"coin\":\"@123\"}},{{\"coin\":\"PURR/USDC\"}}," +
            "{\"coin\":\"xyz:BTC\"},{\"coin\":\"UNKNOWN\"}]";
        using var handler = Fixtures(Meta, State, response);
        using var client = Client(handler);
        var reader = new HyperliquidPerpetualReader(client, new TestClock());
        var priorCulture = CultureInfo.CurrentCulture;
        PerpetualVenueReadResult result;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            result = await reader.ReadAsync(Address, CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }

        Assert.Equal("hyperliquid", reader.VenueId);
        Assert.Collection(result.Instruments,
            instrument => Assert.Equal(new VenueInstrument("BTC", 5, 40, "USDC", MaintenanceMarginFraction: 0.0125m), instrument),
            instrument => Assert.Equal(new VenueInstrument("ETH", 4, 20, "USDC", MaintenanceMarginFraction: 0.025m), instrument));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(StateTime), result.Snapshot.ObservedAtUtc);
        Assert.Equal("primary-perpetual-dex", result.Snapshot.ValueScope);
        Assert.Equal(12345.678901234567890123m, result.Snapshot.AccountValueUsd);
        Assert.Equal(12095.555444445567890123m, result.Snapshot.WithdrawableUsd);
        Assert.Equal(250.123456789m, result.Snapshot.MarginUsedUsd);
        // Phase 2, item 8a: the venue's own liquidation price and margin mode, exactly.
        Assert.Equal(new VenuePosition("BTC", -0.01234567890123456789m, 61234.1234567890123456m,
            -12.987654321m, 250.123456789m, 5, LiquidationPrice: 73336.0123456789m, MarginMode: MarginModes.Isolated),
            Assert.Single(result.Snapshot.Positions));

        Assert.Equal(2, result.Fills.Count);
        var fill = result.Fills[0];
        Assert.Equal("18446744073709551615", fill.SourceFillId);
        Assert.Equal("9007199254740993", fill.OrderId);
        Assert.Equal("BTC", fill.ContractId);
        Assert.Equal(("buy", "close"), (fill.Side, fill.PositionEffect));
        Assert.Equal("Close Short", fill.Direction); // Not inferred as "Open Long" from side.
        Assert.Equal(61235.1234567890123456m, fill.Price);
        Assert.Equal(0.001234567890123456789m, fill.Quantity);
        Assert.Equal(-0.000123456789m, fill.Fee); // Negative fees are rebates.
        Assert.Equal("USDC", fill.FeeToken);
        Assert.Equal(1.234567890123456789m, fill.ClosedPnlUsd);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(StateTime - 1000), fill.OccurredAtUtc);
        Assert.Equal("0xsynthetic-transaction", fill.TransactionHash);
        Assert.Equal(Fill, fill.RawJson);
        Assert.Equal("9007199254740995", result.Fills[1].SourceFillId);
        Assert.Equal("18446744073709551614", result.Fills[1].OrderId);
        using var raw = JsonDocument.Parse(fill.RawJson);
        Assert.Equal("-0.02", raw.RootElement.GetProperty("startPosition").GetString());
        Assert.Equal("0.00001", raw.RootElement.GetProperty("builderFee").GetString());
        Assert.Contains("Incomplete history", result.HistoryNotice);
        Assert.Contains("2,000", result.HistoryNotice);
        Assert.Contains("before filtering", result.HistoryNotice);
        Assert.Contains("not total spot/unified", result.HistoryNotice);

        Assert.NotNull(result.Snapshot.StablecoinWallet);
        Assert.Equal("default", result.Snapshot.StablecoinWallet.AccountMode);
        Assert.Equal(4, result.Snapshot.StablecoinWallet.Balances.Count);
        Assert.Equal(6, handler.Requests.Count);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpMethod.Post, handler.Requests[i].Method);
            Assert.Equal("https://api.hyperliquid.xyz/info", handler.Requests[i].Uri);
            using var payload = JsonDocument.Parse(handler.Requests[i].Body);
            Assert.Equal(new[] { "meta", "clearinghouseState", "userFills", "userAbstraction", "spotMeta", "spotClearinghouseState" }[i],
                payload.RootElement.GetProperty("type").GetString());
            if (i < 2)
                Assert.Equal("", payload.RootElement.GetProperty("dex").GetString());
            else
                Assert.False(payload.RootElement.TryGetProperty("dex", out _));
            if (i is 1 or 2 or 3 or 5)
                Assert.Equal(Address, payload.RootElement.GetProperty("user").GetString());
            else
                Assert.False(payload.RootElement.TryGetProperty("user", out _));
            if (i == 2)
            {
                Assert.False(payload.RootElement.GetProperty("aggregateByTime").GetBoolean());
                Assert.False(payload.RootElement.TryGetProperty("dex", out _));
            }
        }
    }

    [Fact]
    public async Task Empty_account_is_honest_zero_with_incomplete_history_and_no_positions()
    {
        var state = Mutate(State, node =>
        {
            node["assetPositions"] = new JsonArray();
            node["marginSummary"]!["accountValue"] = "0.0";
            node["marginSummary"]!["totalMarginUsed"] = "0.0";
            node["withdrawable"] = "0.0";
        });
        var result = await Read(state: state, fills: "[]");
        Assert.Equal(0m, result.Snapshot.AccountValueUsd);
        Assert.Empty(result.Snapshot.Positions);
        Assert.Empty(result.Fills);
        Assert.Contains("Incomplete history", result.HistoryNotice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0x1234")]
    [InlineData("https://private.invalid/info")]
    [InlineData("0X0123456789abcdef0123456789ABCDEF01234567")]
    [InlineData("0x0123456789abcdef0123456789ABCDEF0123456g")]
    [InlineData("0x0123456789abcdef0123456789ABCDEF0123456 ")]
    [InlineData("0x0123456789abcdef0123456789ABCDEF01234567\n")]
    public async Task Invalid_addresses_fail_before_any_request(string? address)
    {
        using var handler = Fixtures(Meta, State, $"[{Fill}]");
        using var client = Client(handler);
        var exception = await Assert.ThrowsAsync<VenueReadException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(address!, CancellationToken.None));
        Assert.Contains("public address", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Spot_and_other_dex_metadata_never_whitelist_nonperpetual_responses()
    {
        var meta = Mutate(Meta, node =>
        {
            foreach (var coin in new[] { "@123", "PURR/USDC", "xyz:BTC" })
                ((JsonArray)node["universe"]!).Add(new JsonObject { ["name"] = coin });
        });
        var result = await Read(meta: meta, fills: $"[{Fill},{{\"coin\":\"xyz:BTC\"}},{{\"coin\":\"@123\"}}]");
        Assert.Equal(2, result.Instruments.Count);
        Assert.Single(result.Fills);
        Assert.Single(result.Snapshot.Positions);
    }

    [Fact]
    public async Task Optional_leverage_is_null_and_signed_quantities_and_negative_equity_are_preserved()
    {
        var state = Mutate(State, node =>
        {
            ((JsonObject)node["assetPositions"]![0]!["position"]!).Remove("leverage");
            node["marginSummary"]!["accountValue"] = "-1.234";
        });
        var result = await Read(state: state);
        Assert.Null(Assert.Single(result.Snapshot.Positions).Leverage);
        Assert.Equal(-1.234m, result.Snapshot.AccountValueUsd);
        Assert.True(result.Snapshot.Positions[0].SignedQuantity < 0);
    }

    [Theory]
    [InlineData("fee", "missing")]
    [InlineData("fee", "null")]
    [InlineData("fee", "empty")]
    [InlineData("closedPnl", "missing")]
    [InlineData("closedPnl", "null")]
    [InlineData("closedPnl", "empty")]
    [InlineData("feeToken", "missing")]
    [InlineData("feeToken", "null")]
    [InlineData("feeToken", "empty")]
    public async Task Missing_execution_money_is_not_zeroed_even_on_an_opening_fill(string field, string value)
    {
        var fill = Mutate(Fill, node =>
        {
            node["dir"] = "Open Long";
            node["startPosition"] = "0";
            node["closedPnl"] = "0";
            if (value == "missing") node.AsObject().Remove(field);
            else node[field] = value == "null" ? null : JsonValue.Create("");
        });
        await Invalid(() => Read(fills: $"[{fill}]"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.000123456789")]
    [InlineData("-0.000123456789")]
    public async Task Opening_fills_keep_explicit_zero_Pnl_and_signed_fees(string fee)
    {
        var fill = Mutate(Fill, node =>
        {
            node["dir"] = "Open Long";
            node["startPosition"] = "0";
            node["closedPnl"] = "0";
            node["fee"] = fee;
        });
        var result = Assert.Single((await Read(fills: $"[{fill}]")).Fills);
        Assert.Equal("open", result.PositionEffect);
        Assert.Equal(0m, result.ClosedPnlUsd);
        Assert.Equal(decimal.Parse(fee, CultureInfo.InvariantCulture), result.Fee);
    }

    [Theory]
    [InlineData("sz", "\"0\"")]
    [InlineData("sz", "\"-0.01\"")]
    [InlineData("px", "\"0\"")]
    [InlineData("px", "\"-1\"")]
    [InlineData("fee", "\"79228162514264337593543950336\"")]
    [InlineData("closedPnl", "\"0.00000000000000000000000000001\"")]
    [InlineData("px", "\"1.00000000000000000000000000001\"")]
    [InlineData("px", "\"7.9228162514264337593543950336\"")]
    [InlineData("px", "\"NaN\"")]
    [InlineData("px", "\"1,000\"")]
    [InlineData("px", "\"1e3\"")]
    [InlineData("px", "\"+1\"")]
    [InlineData("px", "\" 1\"")]
    [InlineData("px", "\"1.\"")]
    [InlineData("px", "\".1\"")]
    [InlineData("px", "12.3")]
    [InlineData("side", "\"buy\"")]
    [InlineData("time", "-1")]
    [InlineData("time", "253402300800000")]
    [InlineData("time", "9223372036854775808")]
    [InlineData("time", "1790813100001")]
    [InlineData("time", "\"1790812799000\"")]
    [InlineData("time", "1790812799000.5")]
    [InlineData("tid", "-1")]
    [InlineData("oid", "1e3")]
    [InlineData("tid", "\"1.5\"")]
    [InlineData("tid", "\"\"")]
    [InlineData("feeToken", "null")]
    [InlineData("feeToken", "\"XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX\"")]
    [InlineData("dir", "\"\"")]
    [InlineData("hash", "\"private\\nvalue\"")]
    public async Task Invalid_fill_fields_are_safe_failures(string field, string json)
    {
        var fill = Mutate(Fill, node => node[field] = JsonNode.Parse(json));
        await Invalid(() => Read(fills: $"[{fill}]"));
    }

    [Theory]
    [InlineData("entryPx", "\"0\"")]
    [InlineData("entryPx", "null")]
    [InlineData("szi", "\"999999999999999999999999999999999\"")]
    [InlineData("marginUsed", "\"-1\"")]
    [InlineData("unrealizedPnl", "\"-0.00000000000000000000000000001\"")]
    [InlineData("leverage", "{\"value\":0}")]
    [InlineData("leverage", "{\"value\":2147483648}")]
    [InlineData("leverage", "\"5\"")]
    public async Task Invalid_position_fields_are_safe_failures(string field, string json)
    {
        var state = Mutate(State, node => node["assetPositions"]![0]!["position"]![field] = JsonNode.Parse(json));
        await Invalid(() => Read(state: state));
    }

    [Theory]
    [InlineData("szDecimals", "-1")]
    [InlineData("szDecimals", "29")]
    [InlineData("szDecimals", "\"5\"")]
    [InlineData("maxLeverage", "0")]
    [InlineData("maxLeverage", "2147483648")]
    [InlineData("name", "null")]
    public async Task Invalid_metadata_fields_are_safe_failures(string field, string json)
    {
        var meta = Mutate(Meta, node => node["universe"]![0]![field] = JsonNode.Parse(json));
        await Invalid(() => Read(meta: meta));
    }

    [Theory]
    [InlineData("time", "-1")]
    [InlineData("time", "1790813100001")]
    [InlineData("withdrawable", "\"-1\"")]
    [InlineData("withdrawable", "1")]
    [InlineData("marginSummary", "{\"accountValue\":\"1\",\"totalMarginUsed\":\"-1\"}")]
    public async Task Invalid_snapshot_fields_are_safe_failures(string field, string json)
    {
        var state = Mutate(State, node => node[field] = JsonNode.Parse(json));
        await Invalid(() => Read(state: state));
    }

    [Fact]
    public async Task Decimal_limits_are_exact_and_redundant_fractional_zeroes_do_not_lose_value()
    {
        var fill = Mutate(Fill, node =>
        {
            node["px"] = "79228162514264337593543950335";
            node["sz"] = "0.0000000000000000000000000001";
            node["fee"] = "-79228162514264337593543950335";
            node["closedPnl"] = "1.00000000000000000000000000000000000";
        });
        var result = await Read(fills: $"[{fill}]");
        var parsed = Assert.Single(result.Fills);
        Assert.Equal(decimal.MaxValue, parsed.Price);
        Assert.Equal(0.0000000000000000000000000001m, parsed.Quantity);
        Assert.Equal(decimal.MinValue, parsed.Fee);
        Assert.Equal(1m, parsed.ClosedPnlUsd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Malformed_json_in_any_call_fails_without_raw_details(int stage)
    {
        var responses = DefaultResponses();
        responses[stage] = "{\"private-secret\":";
        using var handler = Fixtures(responses);
        using var client = Client(handler);
        await Invalid(() => new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None));
        Assert.Equal(stage + 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"universe\":null}")]
    [InlineData("{\"universe\":[],\"universe\":[]}")]
    [InlineData("{\"universe\":[{\"name\":\"BTC\",\"name\":\"ETH\",\"szDecimals\":5,\"maxLeverage\":20}]}")]
    [InlineData("{\"universe\":[],}")]
    [InlineData("{/*comment*/\"universe\":[]}")]
    public async Task Invalid_shapes_and_duplicate_properties_are_rejected(string meta) =>
        await Invalid(() => Read(meta: meta));

    [Fact]
    public async Task Duplicate_contracts_and_positions_are_rejected()
    {
        var meta = Mutate(Meta, node =>
            ((JsonArray)node["universe"]!).Add(node["universe"]![0]!.DeepClone()));
        await Invalid(() => Read(meta: meta));
        var state = Mutate(State, node =>
            ((JsonArray)node["assetPositions"]!).Add(node["assetPositions"]![0]!.DeepClone()));
        await Invalid(() => Read(state: state));
    }

    [Fact]
    public async Task Missing_fill_identity_and_excessive_depth_are_rejected()
    {
        var fill = Mutate(Fill, node => ((JsonObject)node).Remove("tid"));
        await Invalid(() => Read(fills: $"[{fill}]"));
        var deep = "{\"universe\":[],\"extra\":" + new string('[', 40) + "0" + new string(']', 40) + "}";
        await Invalid(() => Read(meta: deep));
    }

    [Fact]
    public async Task Exactly_2000_recent_fills_are_supported_but_over_cap_is_not_silently_truncated()
    {
        var fills = "[" + string.Join(",", Enumerable.Repeat(Fill, 2000)) + "]";
        var result = await Read(fills: fills);
        Assert.Equal(2000, result.Fills.Count);
        Assert.Contains("Incomplete history", result.HistoryNotice);
        await Invalid(() => Read(fills: "[" + string.Join(",", Enumerable.Repeat(Fill, 2001)) + "]"));
    }

    [Theory]
    [InlineData(0, HttpStatusCode.TooManyRequests)]
    [InlineData(1, HttpStatusCode.BadGateway)]
    [InlineData(2, HttpStatusCode.Unauthorized)]
    [InlineData(3, HttpStatusCode.ServiceUnavailable)]
    [InlineData(4, HttpStatusCode.TooManyRequests)]
    [InlineData(5, HttpStatusCode.BadGateway)]
    public async Task Non_success_status_is_safe_and_stops_refresh(int stage, HttpStatusCode status)
    {
        using var handler = new FixtureHandler((index, _) => Task.FromResult(
            index == stage ? new HttpResponseMessage(status) { Content = new StringContent("private-secret") }
                : Json(DefaultResponses()[index])));
        using var client = Client(handler);
        Func<Task> read = () => new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None);
        var exception = status == HttpStatusCode.TooManyRequests
            ? await Assert.ThrowsAsync<VenueRateLimitException>(read)
            : await Assert.ThrowsAsync<VenueReadException>(read);
        Assert.Equal(status == HttpStatusCode.TooManyRequests
            ? "The venue rate limit requires a later retry."
            : "Hyperliquid is unavailable. Try again later.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Equal(stage + 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_responses_are_rejected_even_without_content_length(bool knownLength)
    {
        var data = new string(' ', 4 * 1024 * 1024 + 1);
        using var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new StringContent(data) : new StreamingContent(new MemoryStream(Encoding.UTF8.GetBytes(data)))
        }));
        using var client = Client(handler);
        await Invalid(() => new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("body")]
    [InlineData("timeout")]
    public async Task Transport_and_provider_timeout_errors_do_not_expose_private_details(string failure)
    {
        using var handler = new FixtureHandler((_, _) => throw failure switch
        {
            "network" => new HttpRequestException("private-secret"),
            "body" => new IOException("private-secret"),
            _ => new TaskCanceledException("private-secret")
        });
        using var client = Client(handler);
        var exception = await Assert.ThrowsAsync<VenueReadException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None));
        Assert.Equal(failure == "timeout" ? "Hyperliquid read timed out." : "Hyperliquid is unavailable. Try again later.",
            exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Precancelled_caller_makes_no_requests()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var handler = Fixtures(Meta, State, $"[{Fill}]");
        using var client = Client(handler);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, cancelled.Token));
        Assert.Equal(cancelled.Token, exception.CancellationToken);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_request_propagates_original_token()
    {
        using var caller = new CancellationTokenSource();
        using var handler = new FixtureHandler((_, token) =>
        {
            caller.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Expected cancellation.");
        });
        using var client = Client(handler);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, caller.Token));
        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Single_20_second_deadline_covers_all_six_calls_not_per_call()
    {
        var clock = new TestClock();
        using var handler = new FixtureHandler((index, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(3.5));
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Json(DefaultResponses()[index]));
        });
        using var client = Client(handler);
        var exception = await Assert.ThrowsAsync<VenueReadException>(() =>
            new HyperliquidPerpetualReader(client, clock).ReadAsync(Address, CancellationToken.None));
        Assert.Equal("Hyperliquid read timed out.", exception.Message);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(2, clock.TimerCount); // Account deadline plus independent shared metadata deadline.
        Assert.Equal(TimeSpan.FromSeconds(20), clock.Deadline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Streamed_body_read_is_covered_by_deadline_and_caller_cancellation(bool callerCancels)
    {
        var clock = new TestClock();
        using var caller = new CancellationTokenSource();
        using var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent(new InterruptingStream(() =>
            {
                if (callerCancels)
                    caller.Cancel();
                else
                    clock.Advance(TimeSpan.FromSeconds(21));
            }))
        }));
        using var client = Client(handler);
        var operation = new HyperliquidPerpetualReader(client, clock).ReadAsync(Address, caller.Token);
        if (callerCancels)
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<VenueReadException>(() => operation);
            Assert.Equal("Hyperliquid read timed out.", exception.Message);
        }
    }

    [Fact]
    public async Task Stream_failure_does_not_expose_provider_or_transport_details()
    {
        using var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent(new InterruptingStream(() => throw new IOException("private-secret")))
        }));
        using var client = Client(handler);
        var exception = await Assert.ThrowsAsync<VenueReadException>(() =>
            new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None));
        Assert.Equal("Hyperliquid is unavailable. Try again later.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Timestamp_bounds_allow_epoch_and_at_most_five_minutes_clock_skew()
    {
        var state = Mutate(State, node => node["time"] = 0);
        var fill = Mutate(Fill, node => node["time"] = StateTime + 300000);
        var result = await Read(state: state, fills: $"[{fill}]");
        Assert.Equal(DateTimeOffset.UnixEpoch, result.Snapshot.ObservedAtUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(StateTime + 300000), result.Fills[0].OccurredAtUtc);
    }

    private static async Task Invalid(Func<Task<PerpetualVenueReadResult>> operation)
    {
        var exception = await Assert.ThrowsAsync<VenueReadException>(operation);
        Assert.Equal("Hyperliquid returned an invalid or unsupported response.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static async Task<PerpetualVenueReadResult> Read(string meta = Meta, string state = State, string? fills = null)
    {
        using var handler = Fixtures(meta, state, fills ?? $"[{Fill}]");
        using var client = Client(handler);
        return await new HyperliquidPerpetualReader(client, new TestClock()).ReadAsync(Address, CancellationToken.None);
    }

    private static string Mutate(string json, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(json)!;
        change(node);
        return node.ToJsonString();
    }

    [Fact]
    public async Task Account_reads_refresh_a_cached_catalogue_so_new_listings_are_not_filtered()
    {
        var responses = new[] { Meta, Meta, State, $"[{Fill}]", "\"default\"", SpotMeta, "{\"balances\":[]}" };
        using var handler = new FixtureHandler((index, _) => Task.FromResult(Json(responses[index])));
        using var client = Client(handler);
        var clock = new TestClock();
        var cache = new HyperliquidCatalogueCache(clock);
        await new HyperliquidPerpetualReader(client, clock, cache).ReadInstrumentsAsync(CancellationToken.None);
        await new HyperliquidPerpetualReader(client, clock, cache).ReadAsync(Address, CancellationToken.None);
        Assert.Equal(7, handler.Requests.Count);
        using var payload = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("meta", payload.RootElement.GetProperty("type").GetString());
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/"), Timeout = TimeSpan.FromSeconds(30) };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static FixtureHandler Fixtures(params string[] fixtures) =>
        new((index, _) => Task.FromResult(Json(
            fixtures.Length == 3 && index >= 3 ? DefaultResponses()[index] : fixtures[index])));

    private static string[] DefaultResponses() => [Meta, State, $"[{Fill}]", "\"default\"", SpotMeta, "{\"balances\":[]}"];

    private sealed class FixtureHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<(HttpMethod Method, string Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, body));
            return await respond(Requests.Count - 1, cancellationToken);
        }
    }

    private sealed class StreamingContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => stream.CopyToAsync(destination);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                stream.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class InterruptingStream(Action interrupt) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            interrupt();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(StateTime);
        private readonly List<TestTimer> timers = [];
        internal int TimerCount { get; private set; }
        internal TimeSpan Deadline { get; private set; }
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            TimerCount++;
            Deadline = dueTime;
            var timer = new TestTimer(now + dueTime, callback, state);
            timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan interval)
        {
            now += interval;
            foreach (var timer in timers.ToArray()) timer.FireIfDue(now);
        }

        private sealed class TestTimer(DateTimeOffset due, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal void FireIfDue(DateTimeOffset now)
            {
                if (!disposed && now >= due)
                {
                    disposed = true;
                    callback(state);
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
