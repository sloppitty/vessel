using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Text;
using System.Text.Json;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class CandleApiTests(CandleApiTests.InvalidParameterHost host) : IClassFixture<CandleApiTests.InvalidParameterHost>
{
    private static string Url(Guid id, string query) => $"/api/accounts/{id}/candles?{query}";
    private const long End = 1_790_000_000_000L;

    private static (Guid Owner, MemoryWorkspaceStore Store, Account Account) Setup(string venue = "hyperliquid", bool foreign = false, bool disabled = false)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), foreign ? Guid.NewGuid() : owner, venue, "Private account");
        if (disabled) account.UpdateSettings(account.Name, null, false);
        store.Accounts.Add(account);
        return (owner, store, account);
    }

    private static string Candle(long t, string o = "100.50", string h = "110.0", string l = "90.00", string c = "105.250", string v = "12.5000", int n = 7,
        string coin = "BTC", string interval = "1h") =>
        $$"""{"t":{{t}},"T":{{t + 3599999}},"s":"{{coin}}","i":"{{interval}}","o":"{{o}}","c":"{{c}}","h":"{{h}}","l":"{{l}}","v":"{{v}}","n":{{n}}}""";

    internal sealed class Candles : ICandleReader
    {
        public string VenueId { get; init; } = "hyperliquid";
        public int Reads; public Exception? Failure;
        public (string Contract, string Interval, long From, long To)? Last;
        public IReadOnlyList<VenueCandle> Result { get; set; } = [];
        public Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(string contractId, string interval, long fromMs, long toMs, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads); Last = (contractId, interval, fromMs, toMs);
            return Failure is null ? Task.FromResult(Result) : Task.FromException<IReadOnlyList<VenueCandle>>(Failure);
        }
    }

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static HyperliquidPerpetualReader ReaderFor(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/") }, TimeProvider.System);

    [Fact]
    public async Task Requires_authentication()
    {
        var (owner, store, account) = Setup(); var reader = new Candles();
        await using var factory = new CoreApiFactory(owner, store, candles: reader);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url(account.Id, "instrument=BTC&interval=1h"))).StatusCode);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Maps_venue_payload_preserving_strings_sorted_and_deduplicated_with_exact_request()
    {
        var (owner, store, account) = Setup();
        var body = "[" + string.Join(",", Candle(End - 3_600_000, o: "100.50"), Candle(End - 7_200_000, o: "99.10", h: "99.9", l: "99.0", c: "99.5"),
            Candle(End - 3_600_000, o: "100.50"), Candle(End - 600 * 3_600_000L)) + "]";
        // The last item is far outside the window and must be dropped.
        var handler = new Handler(body);
        await using var factory = new CoreApiFactory(owner, store, candles: ReaderFor(handler));
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, $"instrument=BTC&interval=1h&endTime={End}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(new[] { "venueId", "instrument", "interval", "priceSource", "candles", "requestedFrom", "requestedTo", "retrievedAt", "historyExhausted", "notice" },
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("hyperliquid", root.GetProperty("venueId").GetString());
        Assert.Equal("trades", root.GetProperty("priceSource").GetString());
        Assert.False(root.GetProperty("historyExhausted").GetBoolean());
        Assert.Equal(End, root.GetProperty("requestedTo").GetInt64());
        Assert.Equal(End - 500 * 3_600_000L, root.GetProperty("requestedFrom").GetInt64());
        Assert.Equal("Hyperliquid exposes only the latest 5,000 candles per interval. Prices are trade candles, not fills.", root.GetProperty("notice").GetString());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("retrievedAt").GetString(), out _));
        var candles = root.GetProperty("candles").EnumerateArray().ToList();
        Assert.Equal(2, candles.Count);
        Assert.Equal(new[] { "openTime", "closeTime", "open", "high", "low", "close", "volume", "trades" }, candles[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(End - 7_200_000, candles[0].GetProperty("openTime").GetInt64());
        Assert.Equal(End - 3_600_000, candles[1].GetProperty("openTime").GetInt64());
        Assert.Equal("100.50", candles[1].GetProperty("open").GetString());
        Assert.Equal("105.250", candles[1].GetProperty("close").GetString());
        Assert.Equal("12.5000", candles[1].GetProperty("volume").GetString());
        Assert.Equal(7, candles[1].GetProperty("trades").GetInt32());

        using var sent = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("candleSnapshot", sent.RootElement.GetProperty("type").GetString());
        var req = sent.RootElement.GetProperty("req");
        Assert.Equal("BTC", req.GetProperty("coin").GetString());
        Assert.Equal("1h", req.GetProperty("interval").GetString());
        Assert.Equal(End - 500 * 3_600_000L, req.GetProperty("startTime").GetInt64());
        Assert.Equal(End, req.GetProperty("endTime").GetInt64());
    }

    [Fact]
    public async Task Interval_aligned_end_time_returns_at_most_500_newest_candles()
    {
        var (owner, store, account) = Setup();
        const long hour = 3_600_000L;
        var aligned = End / hour * hour;
        // Both inclusive window ends exist upstream: 501 opens from aligned - 500h through aligned.
        var reader = new Candles
        {
            Result = Enumerable.Range(0, 501).Select(i => aligned - (500 - i) * hour)
                .Select(t => new VenueCandle(t, t + hour - 1, "1", "2", "0.5", "1.5", "3", 4)).ToList(),
        };
        await using var factory = new CoreApiFactory(owner, store, candles: reader);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, $"instrument=BTC&interval=1h&endTime={aligned}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var opens = json.RootElement.GetProperty("candles").EnumerateArray().Select(c => c.GetProperty("openTime").GetInt64()).ToList();
        Assert.Equal(CandleService.MaxCandles, opens.Count);
        Assert.Equal(aligned - 499 * hour, opens[0]);
        Assert.Equal(aligned, opens[^1]);
    }

    [Fact]
    public async Task Empty_venue_array_marks_history_exhausted_and_monthly_window_uses_31_days()
    {
        var (owner, store, account) = Setup();
        var reader = new Candles();
        await using var factory = new CoreApiFactory(owner, store, candles: reader);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, $"instrument=ETH&interval=1M&endTime={End}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("historyExhausted").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("candles").EnumerateArray());
        Assert.Equal(("ETH", "1M", End - 500 * 31 * 86_400_000L, End), reader.Last);
    }

    [Theory]
    [InlineData("interval=1h")]
    [InlineData("instrument=BTC")]
    [InlineData("instrument=BT%20C&interval=1h")]
    [InlineData("instrument=xyz:BTC&interval=1h")]
    [InlineData("instrument=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&interval=1h")]
    [InlineData("instrument=BTC&interval=2m")]
    [InlineData("instrument=BTC&interval=1H")]
    [InlineData("instrument=BTC&interval=1h&endTime=0")]
    [InlineData("instrument=BTC&interval=1h&endTime=-5")]
    [InlineData("instrument=BTC&interval=1h&endTime=99999999999999")]
    [InlineData("instrument=BTC&interval=1h&endTime=abc")]
    public async Task Invalid_parameters_return_400_without_venue_read(string query)
    {
        // One host for the matrix: a case that read the venue would leave a nonzero count for every later case too.
        using var client = host.Factory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Url(host.Account.Id, query))).StatusCode);
        Assert.Equal(0, host.Reader.Reads);
    }

    /// <summary>The invalid-parameter matrix's shared host; its requests are refused before any read or write.</summary>
    public sealed class InvalidParameterHost : IAsyncDisposable
    {
        internal Candles Reader { get; } = new();
        internal Account Account { get; }
        internal CoreApiFactory Factory { get; }

        public InvalidParameterHost()
        {
            var (owner, store, account) = Setup();
            Account = account;
            Factory = new CoreApiFactory(owner, store, candles: Reader);
        }

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    [Theory]
    [InlineData("manual", false, false, HttpStatusCode.Conflict)]
    [InlineData("hyperliquid", true, false, HttpStatusCode.NotFound)]
    [InlineData("hyperliquid", false, true, HttpStatusCode.Conflict)]
    public async Task Guards_return_expected_status_without_venue_read(string venue, bool foreign, bool disabled, HttpStatusCode status)
    {
        var (owner, store, account) = Setup(venue, foreign, disabled); var reader = new Candles();
        await using var factory = new CoreApiFactory(owner, store, candles: reader);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, "instrument=BTC&interval=1h"));
        Assert.Equal(status, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Private account", text);
        if (venue == "manual") Assert.Contains("No market data provider for manual accounts.", text);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Unknown_account_is_404()
    {
        var (owner, store, _) = Setup();
        await using var factory = new CoreApiFactory(owner, store, candles: new Candles());
        using var client = factory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(Guid.NewGuid(), "instrument=BTC&interval=1h"))).StatusCode);
    }

    [Theory]
    [InlineData("venue")]
    [InlineData("http")]
    [InlineData("mismatch")]
    [InlineData("status")]
    [InlineData("malformed")]
    [InlineData("wrong-coin")]
    [InlineData("wrong-interval")]
    [InlineData("bad-decimal")]
    [InlineData("negative")]
    [InlineData("high-below-close")]
    [InlineData("not-array")]
    public async Task Venue_failures_and_malformed_payloads_return_generic_502(string failure)
    {
        var (owner, store, account) = Setup();
        ICandleReader reader = failure switch
        {
            "venue" => new Candles { Failure = new VenueReadException("secret") },
            "http" => new Candles { Failure = new HttpRequestException("secret") },
            "mismatch" => new Candles { VenueId = "other" },
            "status" => ReaderFor(new Handler("[]", HttpStatusCode.TooManyRequests)),
            "malformed" => ReaderFor(new Handler("[{\"secret\":")),
            "wrong-coin" => ReaderFor(new Handler($"[{Candle(End, coin: "ETH")}]")),
            "wrong-interval" => ReaderFor(new Handler($"[{Candle(End, interval: "4h")}]")),
            "bad-decimal" => ReaderFor(new Handler($"[{Candle(End, o: "1e5")}]")),
            "negative" => ReaderFor(new Handler($"[{Candle(End, l: "-1")}]")),
            "high-below-close" => ReaderFor(new Handler($"[{Candle(End, h: "100", c: "105")}]")),
            _ => ReaderFor(new Handler("{\"secret\":1}"))
        };
        // "mismatch": the account's venue has no candle adapter, only another venue's.
        var mismatch = failure == "mismatch";
        await using var factory = new CoreApiFactory(owner, store, candles: mismatch ? null : reader,
            configure: mismatch ? (Action<IServiceCollection>)(services => { services.RemoveAll<ICandleReader>(); services.AddSingleton(reader); }) : null);
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync(Url(account.Id, $"instrument=BTC&interval=1h&endTime={End}"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", text);
        Assert.Contains("The venue candle read failed. Try again later.", text);
    }

    [Fact]
    public async Task Repeated_request_within_ten_seconds_hits_cache_and_new_window_does_not()
    {
        var (owner, store, account) = Setup();
        var handler = new Handler($"[{Candle(End - 3_600_000)}]");
        await using var factory = new CoreApiFactory(owner, store, candles: ReaderFor(handler));
        using var client = factory.AuthorizedClient();
        var url = Url(account.Id, $"instrument=BTC&interval=1h&endTime={End}");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        Assert.Single(handler.Bodies);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(account.Id, $"instrument=BTC&interval=1h&endTime={End - 1}"))).StatusCode);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task Omitted_end_time_is_bucketed_so_requests_in_the_same_ten_seconds_share_one_read()
    {
        var (owner, store, account) = Setup(); var reader = new Candles();
        var time = new FixedTime(DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_003_000L));
        var service = new CandleService(store, TestVenues.With(reader), new CandleCache(time), time);
        var first = await service.CandlesAsync(account.Id, "BTC", "1h", null, default);
        time.Now = time.Now.AddSeconds(4);
        await service.CandlesAsync(account.Id, "BTC", "1h", null, default);
        Assert.Equal(1, reader.Reads);
        Assert.Equal(1_790_000_000_000L, first.RequestedTo);
        time.Now = time.Now.AddSeconds(20);
        await service.CandlesAsync(account.Id, "BTC", "1h", null, default);
        Assert.Equal(2, reader.Reads);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
