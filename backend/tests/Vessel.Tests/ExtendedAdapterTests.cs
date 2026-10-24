using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues;
using Vessel.Infrastructure.Venues.Extended;

namespace Vessel.Tests;

/// <summary>Trimmed public, unauthenticated fixtures from October 23, 2026. No account or secret was used.</summary>
public sealed class ExtendedAdapterTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    internal sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    internal static string Fixture(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Extended", file));
    private static JsonObject Markets => JsonNode.Parse(Fixture("markets.json"))!.AsObject();
    private static JsonObject Candles => JsonNode.Parse(Fixture("candles.json"))!.AsObject();

    internal sealed class Handler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Body { get; set; }
        public Task? ReplyGate { get; set; }
        public Exception? Failure { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.False(request.Headers.Contains("X-Api-Key"));
            Assert.Null(request.Headers.Authorization);
            Assert.DoesNotContain("/user/", request.RequestUri!.AbsolutePath);
            Requests.Add(request.RequestUri.PathAndQuery);
            if (ReplyGate is not null) await ReplyGate.WaitAsync(ct);
            if (Failure is not null) throw Failure;
            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body ?? Fixture(request.RequestUri.AbsolutePath.EndsWith("/markets") ? "markets.json" : "candles.json"),
                    Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
            return response;
        }
    }

    internal static ExtendedReader Reader(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new("https://extended.example.test/") }, new Clock(), new(new Clock()));

    [Fact]
    public async Task Catalogue_retains_exact_native_identity_tick_precision_and_reported_whole_leverage()
    {
        var handler = new Handler();
        var reader = Reader(handler);
        var markets = await reader.ReadInstrumentsAsync(default);
        Assert.Equal(["SHOP", "BTC"], markets.Select(m => m.ContractId));
        var btc = markets.Single(m => m.ContractId == "BTC");
        Assert.Equal("BTC-USD", btc.VenueContractId);
        Assert.Equal("USD", btc.QuoteAsset);
        Assert.Equal(5, btc.QuantityDecimals);
        Assert.Equal(50, btc.MaxLeverage);
        Assert.Equal(1m, btc.PriceStep);
        Assert.Null(btc.MaintenanceMarginFraction);
        Assert.Null(btc.Category);
        Assert.Equal("RWA", markets.Single(m => m.ContractId == "SHOP").Category);
        // SPOT, delisted-but-active and unverified kDOGS multiplier are not silently advertised as supported.
        Assert.DoesNotContain(markets, m => m.ContractId is "USDTSPOT" or "NOW_24_5" or "kDOGS" or "1000DOGS");
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => reader.ReadInstrumentsAsync(default)));
        Assert.Equal("/api/v1/info/markets", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task Concurrent_catalogue_reads_share_one_inflight_load_and_preserve_waiter_cancellation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { ReplyGate = release.Task };
        var reader = Reader(handler);
        using var stopped = new CancellationTokenSource();
        var first = reader.ReadInstrumentsAsync(stopped.Token);
        var second = reader.ReadInstrumentsAsync(default);
        Assert.Single(handler.Requests);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.NotEmpty(await second);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("envelope")]
    [InlineData("pagination")]
    [InlineData("duplicate")]
    [InlineData("precision")]
    [InlineData("tick")]
    [InlineData("leverage")]
    [InlineData("quantity")]
    [InlineData("rows")]
    [InlineData("duplicate-property")]
    public async Task Invalid_or_partial_catalogues_fail_closed_with_no_source_detail(string defect)
    {
        var root = Markets;
        var data = root["data"]!.AsArray();
        var btc = data.Single(n => (string)n!["assetName"]! == "BTC")!;
        switch (defect)
        {
            case "envelope": root["status"] = "ERROR"; root["error"] = "sensitive-upstream-body"; break;
            case "pagination": root["pagination"] = new JsonObject { ["cursor"] = 123 }; break;
            case "duplicate": data.Add(btc.DeepClone()); break;
            case "precision": btc["tradingConfig"]!["minPriceChange"] = "0.12345678901234567890123456789"; break;
            case "tick": btc["tradingConfig"]!["minPriceChange"] = "0"; break;
            case "leverage": btc["tradingConfig"]!["maxLeverage"] = "2.5"; break;
            case "quantity": btc["assetPrecision"] = 29; break;
            case "rows": while (data.Count <= 2000) data.Add(btc.DeepClone()); break;
        }
        var body = defect == "duplicate-property" ? """{"status":"OK","status":"ERROR","data":[]}""" : root.ToJsonString();
        var error = await Assert.ThrowsAsync<VenueReadException>(() => Reader(new() { Body = body }).ReadInstrumentsAsync(default));
        Assert.Equal("Extended returned an invalid or unsupported public response.", error.Message);
        Assert.DoesNotContain("sensitive", error.ToString());
    }

    [Fact]
    public async Task Incompatible_quote_inactive_reduce_only_and_mismatched_native_id_are_not_selectable()
    {
        var root = Markets;
        var data = root["data"]!.AsArray();
        var btc = data.Single(n => (string)n!["assetName"]! == "BTC")!;
        foreach (var field in new[] { "collateralAssetName", "active", "status", "name" })
        {
            var copy = root.DeepClone();
            var row = copy["data"]!.AsArray().Single(n => (string)n!["assetName"]! == "BTC")!;
            row[field] = field switch
            {
                "active" => JsonValue.Create(false),
                "status" => JsonValue.Create("REDUCE_ONLY"),
                "name" => JsonValue.Create("OTHER-USD"),
                _ => JsonValue.Create("OTHER")
            };
            var result = await Reader(new() { Body = copy.ToJsonString() }).ReadInstrumentsAsync(default);
            Assert.DoesNotContain(result, m => m.ContractId == "BTC");
        }
        // A distinct schedule key is retained, not mapped onto a different BTC contract.
        btc["assetName"] = "BTC_24_5"; btc["name"] = "BTC_24_5-USD";
        var scheduled = await Reader(new() { Body = root.ToJsonString() }).ReadInstrumentsAsync(default);
        Assert.Contains(scheduled, m => m.ContractId == "BTC_24_5" && m.VenueContractId == "BTC_24_5-USD");
    }

    internal static readonly VenueInstrument Btc = new("BTC", 5, 50, "USD", 1m, VenueContractId: "BTC-USD");
    internal const long First = 1792749600000, Last = First + 120000;

    [Fact]
    public async Task Trade_candles_use_native_interval_endTime_and_exact_strings_without_inventing_trade_count()
    {
        var handler = new Handler();
        var candles = await Reader(handler).ReadNativeCandlesAsync(Btc, "1m", First, Last, default);
        Assert.Equal($"/api/v1/info/candles/BTC-USD/trades?interval=PT1M&limit=3&endTime={Last}", Assert.Single(handler.Requests));
        Assert.Equal([First, First + 60000, Last], candles.Select(c => c.OpenTime));
        Assert.Equal(First + 59999, candles[0].CloseTime);
        Assert.Equal(("81533.5", "81533.5", "81523.5", "81523.5", "0.00058"),
            (candles[0].Open, candles[0].High, candles[0].Low, candles[0].Close, candles[0].Volume));
        Assert.All(candles, candle => Assert.Null(candle.Trades));
        var window = new Handler();
        Assert.Equal(2, (await Reader(window).ReadNativeCandlesAsync(Btc, "1m", First + 1, Last + 59999, default)).Count);
    }

    [Fact]
    public async Task Large_windows_are_one_bounded_request_and_empty_is_not_an_account_observation()
    {
        var handler = new Handler { Body = """{"status":"OK","data":[]}""" };
        Assert.Empty(await Reader(handler).ReadNativeCandlesAsync(Btc, "1m", 0, Last, default));
        Assert.Contains("limit=500&", Assert.Single(handler.Requests));
        var accountError = await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler).ReadAsync("never-echo-this-source", default));
        Assert.DoesNotContain("never-echo", accountError.ToString());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("precision")]
    [InlineData("volume")]
    [InlineData("missing-volume")]
    [InlineData("range")]
    [InlineData("future")]
    [InlineData("duplicate")]
    [InlineData("unaligned")]
    [InlineData("overflow")]
    public async Task Malformed_candles_never_become_rounded_or_defaulted_prices(string defect)
    {
        var root = Candles;
        var data = root["data"]!.AsArray();
        switch (defect)
        {
            case "precision": data[0]!["o"] = "0.12345678901234567890123456789"; break;
            case "volume": data[0]!["v"] = "-1"; break;
            case "missing-volume": data[0]!.AsObject().Remove("v"); break;
            case "range": data[0]!["h"] = "1"; break;
            case "future": data[0]!["T"] = Last + 60000; break;
            case "duplicate": data[1]!["T"] = Last; break;
            case "unaligned": data[0]!["T"] = Last - 1; break;
            case "overflow": data.Add(data[2]!.DeepClone()); break;
        }
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(new() { Body = root.ToJsonString() })
            .ReadNativeCandlesAsync(Btc, "1m", First, Last, default));
    }

    [Theory]
    [InlineData("3m", "BTC", "BTC-USD", "USD", 0, Last)]
    [InlineData("1m", "BTC", "../BTC", "USD", 0, Last)]
    [InlineData("1m", "BTC", "ETH-USD", "USD", 0, Last)]
    [InlineData("1m", "BTC", "BTC-USD", "USDC", 0, Last)]
    [InlineData("1m", "BTC", "BTC-USD", "USD", -1, Last)]
    [InlineData("1m", "BTC", "BTC-USD", "USD", Last, Last)]
    [InlineData("1m", "BTC", "BTC-USD", "USD", 0, long.MaxValue)]
    public async Task Unsupported_native_identity_interval_and_window_fail_without_http(string interval, string key, string native,
        string quote, long from, long to)
    {
        var handler = new Handler();
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler)
            .ReadNativeCandlesAsync(Btc with { ContractId = key, VenueContractId = native, QuoteAsset = quote }, interval, from, to, default));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Http_errors_do_not_include_body_host_or_credential_text(HttpStatusCode status)
    {
        var error = await Assert.ThrowsAsync<VenueReadException>(() => Reader(new()
        {
            Status = status, Body = "sensitive-upstream-body-key"
        }).ReadInstrumentsAsync(default));
        Assert.Equal("Extended public data is unavailable. Try again later.", error.Message);
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.DoesNotContain("example.test", error.ToString());
    }

    [Fact]
    public async Task Transport_exception_does_not_leak_a_source_url_or_upstream_details()
    {
        var error = await Assert.ThrowsAsync<VenueReadException>(() => Reader(new()
        {
            Failure = new HttpRequestException("https://upstream.example/?secret=never-echo")
        }).ReadInstrumentsAsync(default));
        Assert.Equal("Extended public data is unavailable. Try again later.", error.Message);
        Assert.DoesNotContain("never-echo", error.ToString());
    }

    [Fact]
    public async Task Oversized_response_and_rate_limit_are_bounded_and_pre_cancelled_calls_send_nothing()
    {
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(new() { Body = new string('x', 4 * 1024 * 1024 + 1) })
            .ReadInstrumentsAsync(default));
        var limited = await Assert.ThrowsAsync<VenueRateLimitException>(() => Reader(new() { Status = HttpStatusCode.TooManyRequests })
            .ReadInstrumentsAsync(default));
        Assert.DoesNotContain("example.test", limited.ToString());
        var handler = new Handler();
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(handler).ReadInstrumentsAsync(stopped.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Registration_uses_configured_https_host_and_only_public_ports()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid(), settings: new()
        {
            ["Vessel:Venues:Extended:BaseUrl"] = "https://public.extended.example.test"
        });
        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("extended");
        Assert.Equal("https://public.extended.example.test/", client.BaseAddress!.AbsoluteUri);
        Assert.NotEmpty(client.DefaultRequestHeaders.UserAgent);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Equal(TimeSpan.FromSeconds(20), client.Timeout);
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IVenueRegistry>();
        Assert.IsType<ExtendedReader>(registry.Reader("extended"));
        Assert.IsType<ExtendedReader>(registry.Candles("extended"));
        Assert.Null(registry.Orders("extended")); Assert.Null(registry.Funding("extended"));
        Assert.Null(registry.Discovery("extended")); Assert.Null(registry.CredentialVerifier("extended"));
        Assert.Null(registry.MarketContext("extended")); Assert.Null(registry.Stream("extended"));
        Assert.Single(scope.ServiceProvider.GetServices<IPerpetualVenueReader>(), r => r.VenueId == "extended");
    }

    [Theory]
    [InlineData("http://public.example.test/")]
    [InlineData("https://user:secret@public.example.test/")]
    [InlineData("https://public.example.test/?key=secret")]
    [InlineData("https://public.example.test/api/v1")]
    [InlineData(null)]
    public void Host_validation_never_echoes_values(string? host)
    {
        var options = new VenueHostOptions(); options.Extended.BaseUrl = host;
        var failure = Assert.Single(new VenueHostValidation().Validate(null, options).Failures!);
        Assert.StartsWith("Vessel:Venues:Extended:BaseUrl ", failure);
        Assert.DoesNotContain("secret", failure);
        Assert.DoesNotContain("public.example.test", failure);
    }
}
