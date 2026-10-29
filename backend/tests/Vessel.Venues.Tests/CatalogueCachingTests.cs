using System.Net;
using System.Text;
using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Infrastructure.Venues.Risex;

namespace Vessel.Tests;

public sealed class CatalogueCachingTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoCredentials : IAccountCredentialReader
    {
        public Task<string?> ReadAsync(Guid accountId, CancellationToken ct) => throw new InvalidOperationException("Public metadata must not read credentials.");
        public Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken ct) => throw new InvalidOperationException("Public metadata must not read credentials.");
        public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken ct) => throw new InvalidOperationException();
    }

    private static string LighterMarket(int id, bool active = true, bool frozen = false) => $$"""
        {"market_id":{{id}},"market_type":"perp","symbol":"BTC","status":"{{(active ? "active" : "inactive")}}",
         "is_frozen":{{(frozen ? "true" : "false")}},"supported_size_decimals":5,"supported_price_decimals":1,
         "min_initial_margin_fraction":200,"maintenance_margin_fraction":120}
        """;

    private static string RisexMarket(string id, bool active = true) => $$$"""
        {"market_id":"{{{id}}}","quote_asset_symbol":"USDC","active":{{{(active ? "true" : "false")}}},"category":"crypto",
         "config":{"name":"BTC/USDC","step_size":"0.00001","step_price":"0.1","max_leverage":"50","unlocked":true}}
        """;

    private static string Metadata(string venue, string id = "1", bool active = true) => venue switch
    {
        "hyperliquid" => $$"""{"universe":[{"name":"BTC","szDecimals":5,"maxLeverage":50,"isDelisted":{{(active ? "false" : "true")}}}]}""",
        "lighter" => $$"""{"code":200,"order_book_details":[{{LighterMarket(int.Parse(id), active)}}]}""",
        _ => $$$"""{"data":{"markets":[{{{RisexMarket(id, active)}}}]}}"""
    };

    private sealed class Handler(string venue) : HttpMessageHandler
    {
        private static readonly long At = DateTimeOffset.Parse("2026-10-10T12:00:00Z").ToUnixTimeMilliseconds();
        public int Reads;
        public int CandleReads;
        public string Meta = Metadata(venue);
        public string? LastCandleUrl;
        public bool Fail;
        public Func<CancellationToken, Task>? Wait;
        public CancellationToken LoadToken;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var hyperCandle = false;
            if (venue == "hyperliquid")
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                hyperCandle = payload.RootElement.GetProperty("type").GetString() == "candleSnapshot";
            }
            if (request.RequestUri!.PathAndQuery.Contains("candles", StringComparison.Ordinal) ||
                request.RequestUri.PathAndQuery.Contains("trading-view-data", StringComparison.Ordinal) || hyperCandle)
            {
                CandleReads++;
                LastCandleUrl = request.RequestUri.PathAndQuery;
                var id = venue == "risex" ? LastCandleUrl.Split("/id/")[1].Split('/')[0] : "1";
                var body = venue switch
                {
                    "lighter" => $$"""{"code":200,"r":"1h","c":[{"t":{{At}},"o":"1.0000","h":"2","l":"0","c":"1","v":"10.00"}]}""",
                    "risex" => $$$"""{"data":{"data":[{"market_id":"{{{id}}}","interval":"1h","time":"{{{At * 1_000_000}}}","open":"1.0000","high":"2","low":"0","close":"1","volume":"10.00"}]}}""",
                    _ => $$"""[{"t":{{At}},"T":{{At + 3_599_999}},"s":"BTC","i":"1h","o":"1.0000","h":"2","l":"0","c":"1","v":"10.00","n":1}]"""
                };
                return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
            Interlocked.Increment(ref Reads);
            LoadToken = ct;
            Started.TrySetResult();
            if (Wait is not null) await Wait(ct);
            return new(Fail ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            {
                Content = new StringContent(Meta, Encoding.UTF8, "application/json")
            };
        }
    }

    private static IPerpetualVenueReader Reader(string venue, HttpClient client, Clock clock, object? cache = null) => venue switch
    {
        "hyperliquid" => new HyperliquidPerpetualReader(client, clock, cache as HyperliquidCatalogueCache),
        "lighter" => new LighterReader(client, clock, new NoCredentials(), cache as LighterCatalogueCache),
        _ => new RisexReader(client, clock, (RisexCatalogueCache?)cache ?? new(clock))
    };

    private static object Cache(string venue, Clock clock) => venue switch
    {
        "hyperliquid" => new HyperliquidCatalogueCache(clock),
        "lighter" => new LighterCatalogueCache(clock),
        _ => new RisexCatalogueCache(clock)
    };

    [Theory]
    [InlineData("hyperliquid")]
    [InlineData("lighter")]
    [InlineData("risex")]
    public async Task Catalogue_is_shared_across_adapters_and_expires_at_60_seconds(string venue)
    {
        var clock = new Clock(); using var handler = new Handler(venue);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://venue.test/") };
        var cache = Cache(venue, clock);
        var first = Reader(venue, client, clock, cache); var second = Reader(venue, client, clock, cache);
        Assert.Single(await first.ReadInstrumentsAsync(default));
        clock.Now += TimeSpan.FromMilliseconds(59999);
        Assert.Single(await second.ReadInstrumentsAsync(default));
        Assert.Equal(1, handler.Reads);
        clock.Now += TimeSpan.FromMilliseconds(1);
        Assert.Single(await first.ReadInstrumentsAsync(default));
        Assert.Equal(2, handler.Reads);
    }

    [Theory]
    [InlineData("hyperliquid")]
    [InlineData("lighter")]
    [InlineData("risex")]
    public async Task Concurrent_catalogue_waiters_share_a_read_and_one_cancellation_does_not_cancel_it(string venue)
    {
        var clock = new Clock(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(venue) { Wait = ct => release.Task.WaitAsync(ct) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://venue.test/") };
        var reader = Reader(venue, client, clock);
        using var cancel = new CancellationTokenSource();
        var first = reader.ReadInstrumentsAsync(cancel.Token);
        await handler.Started.Task;
        var second = reader.ReadInstrumentsAsync(default);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(handler.LoadToken.IsCancellationRequested);
        release.SetResult();
        Assert.Single(await second);
        Assert.Equal(1, handler.Reads);
    }

    [Theory]
    [InlineData("hyperliquid")]
    [InlineData("lighter")]
    [InlineData("risex")]
    public async Task Failed_catalogue_read_is_not_cached_and_the_next_request_retries(string venue)
    {
        var clock = new Clock(); using var handler = new Handler(venue) { Fail = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://venue.test/") };
        var reader = Reader(venue, client, clock);
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadInstrumentsAsync(default));
        handler.Fail = false;
        Assert.Single(await reader.ReadInstrumentsAsync(default));
        Assert.Single(await reader.ReadInstrumentsAsync(default));
        Assert.Equal(2, handler.Reads);
    }

    [Theory]
    [InlineData("lighter")]
    [InlineData("risex")]
    public async Task Replaced_native_market_and_retirement_are_checked_before_historical_candle_lookup(string venue)
    {
        var clock = new Clock(); using var handler = new Handler(venue);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://venue.test/") };
        var reader = Reader(venue, client, clock);
        var descriptor = venue == "lighter" ? LighterReader.Descriptor : RisexReader.Descriptor;
        var registry = new VenueRegistry([descriptor], [reader], [], [(ICandleReader)reader], [], [], [], []);
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, venue, "Test");
        store.Accounts.Add(account);
        var cache = new CandleCache(clock);
        var service = new CandleService(store, registry, cache, clock);
        var end = clock.Now.AddDays(-1).ToUnixTimeMilliseconds();
        await service.CandlesAsync(account.Id, "BTC", "1h", end, default);
        Assert.Contains(venue == "lighter" ? "market_id=1" : "/id/1/", handler.LastCandleUrl);
        handler.Meta = Metadata(venue, "2");
        clock.Now += TimeSpan.FromSeconds(60);
        await service.CandlesAsync(account.Id, "BTC", "1h", end, default);
        Assert.Contains(venue == "lighter" ? "market_id=2" : "/id/2/", handler.LastCandleUrl);
        Assert.Equal(2, handler.CandleReads);
        handler.Meta = Metadata(venue, "2", active: false);
        clock.Now += TimeSpan.FromSeconds(60);
        Assert.NotNull(cache.Get($"{venue}|2|1h|{end - 500 * 3_600_000L}|{end}"));
        var retired = await Assert.ThrowsAsync<ProblemException>(() => service.CandlesAsync(account.Id, "BTC", "1h", end, default));
        Assert.Equal(409, retired.StatusCode);
        Assert.Equal("This market is no longer listed.", retired.Message);
        Assert.Equal(2, handler.CandleReads);
    }

    [Fact]
    public async Task Hyperliquid_retirement_rejects_a_still_cached_historical_window()
    {
        var clock = new Clock(); using var handler = new Handler("hyperliquid");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://venue.test/") };
        var reader = Reader("hyperliquid", client, clock);
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Test"); store.Accounts.Add(account);
        var cache = new CandleCache(clock);
        var service = new CandleService(store, TestVenues.With(reader), cache, clock);
        var end = clock.Now.AddDays(-1).ToUnixTimeMilliseconds();
        Assert.Single((await service.CandlesAsync(account.Id, "BTC", "1h", end, default)).Candles);
        handler.Meta = Metadata("hyperliquid", active: false);
        clock.Now += TimeSpan.FromSeconds(60);
        Assert.NotNull(cache.Get($"hyperliquid|BTC|1h|{end - 500 * 3_600_000L}|{end}"));
        var retired = await Assert.ThrowsAsync<ProblemException>(() => service.CandlesAsync(account.Id, "BTC", "1h", end, default));
        Assert.Equal(409, retired.StatusCode);
        Assert.Equal("This market is no longer listed.", retired.Message);
        Assert.Equal(1, handler.CandleReads);
    }

    [Theory]
    [InlineData("hyperliquid")]
    [InlineData("lighter")]
    [InlineData("risex")]
    public async Task Full_cached_metadata_keeps_retired_identity_but_selectable_catalogue_drops_it(string venue)
    {
        var clock = new Clock(); var cache = Cache(venue, clock);
        using var handler = new Handler(venue) { Meta = Metadata(venue, active: false) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://venue.test/") };
        Assert.Empty(await Reader(venue, client, clock, cache).ReadInstrumentsAsync(default));
        var mustNotLoad = new VenueReadException("Expected full metadata cache hit.");
        switch (cache)
        {
            case HyperliquidCatalogueCache hyper:
                Assert.Equal("BTC", Assert.Single((await hyper.GetAsync(_ => throw mustNotLoad, default)).All).ContractId);
                break;
            case LighterCatalogueCache lighter:
                Assert.Equal("1", Assert.Single((await lighter.GetAsync(_ => throw mustNotLoad, default)).Markets).MarketId);
                break;
            case RisexCatalogueCache risex:
                Assert.Equal("1", Assert.Single((await risex.GetAsync(_ => throw mustNotLoad, default)).Markets).MarketId);
                break;
        }
        Assert.Equal(1, handler.Reads);
    }

    [Fact]
    public async Task Lighter_retired_symbol_can_coexist_with_its_active_replacement_or_frozen_identity()
    {
        var clock = new Clock(); using var handler = new Handler("lighter")
        {
            Meta = $$"""{"code":200,"order_book_details":[{{LighterMarket(1, active: false)}},{{LighterMarket(2)}},{{LighterMarket(3, frozen: true)}}]}"""
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://venue.test/") };
        var cache = new LighterCatalogueCache(clock);
        Assert.Equal("2", Assert.Single(await Reader("lighter", client, clock, cache).ReadInstrumentsAsync(default)).VenueContractId);
        var full = await cache.GetAsync(_ => throw new InvalidOperationException(), default);
        Assert.Equal(3, full.Markets.Count);
        Assert.False(full.ById("1")!.Active);
        Assert.False(full.ById("3")!.Active);
    }

    private sealed class Contexts : IMarketContextReader
    {
        public string VenueId => "test";
        public int Reads;
        public CancellationToken Token;
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<VenueMarketContext>> ReadMarketContextsAsync(CancellationToken ct)
        {
            Reads++; Token = ct;
            await Release.Task.WaitAsync(ct);
            return [new("BTC", "1.0000", "1", null, null, "10", "1", "0", null)];
        }
    }

    [Fact]
    public async Task Context_snapshot_single_flight_is_cancellation_independent_with_10_second_TTL()
    {
        var clock = new Clock(); var cache = new MarketContextCache(clock); var reader = new Contexts();
        using var cancel = new CancellationTokenSource();
        var first = cache.GetAsync("test", reader, cancel.Token);
        var second = cache.GetAsync("test", reader, default);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(reader.Token.IsCancellationRequested);
        reader.Release.SetResult();
        Assert.Equal("1.0000", Assert.Single((await second).Value).MarkPrice);
        clock.Now += TimeSpan.FromMilliseconds(9999);
        await cache.GetAsync("test", reader, default);
        Assert.Equal(1, reader.Reads);
        clock.Now += TimeSpan.FromMilliseconds(1);
        await cache.GetAsync("test", reader, default);
        Assert.Equal(2, reader.Reads);
    }
}
