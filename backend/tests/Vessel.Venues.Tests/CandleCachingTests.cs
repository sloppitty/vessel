using Vessel.Application.Common;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class CandleCachingTests : CandleCachingTestsSupport
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("1m", 60_000L, -1, true)]
    [InlineData("1m", 60_000L, 0, true)]
    [InlineData("1m", 60_000L, 1, false)]
    [InlineData("1w", 604_800_000L, -1, true)]
    [InlineData("1w", 604_800_000L, 0, true)]
    [InlineData("1w", 604_800_000L, 1, false)]
    [InlineData("1M", 2_678_400_000L, -1, true)]
    [InlineData("1M", 2_678_400_000L, 0, true)]
    [InlineData("1M", 2_678_400_000L, 1, false)]
    public async Task Historical_cutoff_uses_the_maximum_interval_duration(string interval, long step, int offset, bool historical)
    {
        var (_, store, account) = Setup();
        var clock = new Clock(); var reader = new Reader(); var cache = new CandleCache(clock);
        var service = new CandleService(store, TestVenues.With(reader), cache, clock);
        var end = clock.Now.ToUnixTimeMilliseconds() - 3 * step + offset;
        await service.CandlesAsync(account.Id, "BTC", interval, end, default);
        clock.Now += TimeSpan.FromSeconds(10);
        await service.CandlesAsync(account.Id, "BTC", interval, end, default);
        Assert.Equal(historical ? 1 : 2, reader.Reads);
    }

    [Theory]
    [InlineData("2024-02-29T12:00:00Z")]
    [InlineData("2026-02-28T12:00:00Z")]
    public async Task Monthly_cutoff_is_93_days_even_in_February(string now)
    {
        var (_, store, account) = Setup();
        var clock = new Clock { Now = DateTimeOffset.Parse(now) };
        var reader = new Reader(); var service = new CandleService(store, TestVenues.With(reader), new CandleCache(clock), clock);
        var end = clock.Now.AddDays(-93).ToUnixTimeMilliseconds();
        await service.CandlesAsync(account.Id, "BTC", "1M", end, default);
        clock.Now += TimeSpan.FromHours(23);
        await service.CandlesAsync(account.Id, "BTC", "1M", end, default);
        Assert.Equal(1, reader.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_end_or_future_end_is_tail(bool future)
    {
        var (_, store, account) = Setup();
        var clock = new Clock(); var cache = new CandleCache(clock); var reader = new Reader();
        var service = new CandleService(store, TestVenues.With(reader), cache, clock);
        var result = await service.CandlesAsync(account.Id, "BTC", "1h",
            future ? clock.Now.AddHours(1).ToUnixTimeMilliseconds() : null, default);
        var key = $"hyperliquid|BTC|1h|{result.RequestedFrom}|{result.RequestedTo}";
        clock.Now += TimeSpan.FromMilliseconds(9999);
        Assert.NotNull(cache.Get(key));
        clock.Now += TimeSpan.FromMilliseconds(1);
        Assert.Null(cache.Get(key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_tail_and_historical_windows_expire_at_60_seconds(bool historical)
    {
        var (_, store, account) = Setup();
        var clock = new Clock(); var reader = new Reader { Empty = true };
        var service = new CandleService(store, TestVenues.With(reader), new CandleCache(clock), clock);
        var end = clock.Now.AddHours(historical ? -4 : 0).ToUnixTimeMilliseconds();
        await service.CandlesAsync(account.Id, "BTC", "1h", end, default);
        clock.Now += TimeSpan.FromMilliseconds(59999);
        await service.CandlesAsync(account.Id, "BTC", "1h", end, default);
        Assert.Equal(1, reader.Reads);
        clock.Now += TimeSpan.FromMilliseconds(1);
        await service.CandlesAsync(account.Id, "BTC", "1h", end, default);
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task Historical_result_expires_at_24_hours_and_hits_do_not_extend_it()
    {
        var (_, store, account) = Setup();
        var clock = new Clock(); var reader = new Reader();
        var service = new CandleService(store, TestVenues.With(reader), new CandleCache(clock), clock);
        var end = clock.Now.AddDays(-1).ToUnixTimeMilliseconds();
        await service.CandlesAsync(account.Id, "BTC", "1h", end, default);
        clock.Now += TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(1);
        await service.CandlesAsync(account.Id, "BTC", "1h", end, default);
        Assert.Equal(1, reader.Reads);
        clock.Now += TimeSpan.FromMilliseconds(1);
        await service.CandlesAsync(account.Id, "BTC", "1h", end, default);
        Assert.Equal(2, reader.Reads);
    }

    private static CandleResponseDto Response(int size) => new("test", "BTC", "1h", "trades",
        Enumerable.Range(0, size).Select(i => new CandleDto(i, i, "1", "1", "1", "1", "1", null)).ToArray(),
        0, 500, DateTimeOffset.UnixEpoch, size == 0, "");

    // A refresh load stores the value exactly as a cache miss does, replacing any entry under the key.
    private static Task Put(CandleCache cache, string key, CandleResponseDto value) =>
        cache.GetAsync(key, historical: false, refresh: true, _ => Task.FromResult(value), default);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Entry_bound_evicts_one_LRU_entry_even_under_empty_pressure(int size)
    {
        var cache = new CandleCache(new Clock()); var result = Response(size);
        for (var i = 0; i < CandleCache.MaxEntries; i++) await Put(cache, i.ToString(), result);
        Assert.NotNull(cache.Get("0"));
        await Put(cache, "new", result);
        Assert.Null(cache.Get("1"));
        Assert.NotNull(cache.Get("0"));
        Assert.NotNull(cache.Get("2"));
        Assert.NotNull(cache.Get("new"));
    }

    [Fact]
    public async Task Candle_bound_is_200000_and_replacement_updates_weight()
    {
        var cache = new CandleCache(new Clock()); var result = Response(500);
        for (var i = 0; i < 400; i++) await Put(cache, i.ToString(), result);
        await Put(cache, "399", Response(0)); // Free 500 candles without adding an entry.
        await Put(cache, "400", result);
        Assert.NotNull(cache.Get("0"));
        await Put(cache, "401", result);
        Assert.Null(cache.Get("1"));
        Assert.NotNull(cache.Get("0"));
        Assert.NotNull(cache.Get("2"));
        Assert.NotNull(cache.Get("399"));
        Assert.NotNull(cache.Get("401"));
    }

    [Fact]
    public async Task One_cancelled_waiter_does_not_cancel_the_shared_candle_read()
    {
        var (_, store, account) = Setup(); var clock = new Clock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { Wait = ct => release.Task.WaitAsync(ct) };
        var service = new CandleService(store, TestVenues.With(reader), new CandleCache(clock), clock);
        using var cancelled = new CancellationTokenSource();
        var first = service.CandlesAsync(account.Id, "BTC", "1h", null, cancelled.Token);
        await reader.Started.Task;
        var second = service.CandlesAsync(account.Id, "BTC", "1h", null, default);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(reader.LoadToken.IsCancellationRequested);
        release.SetResult();
        Assert.Single((await second).Candles);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task Failed_read_is_not_cached_and_its_flight_is_removed()
    {
        var (_, store, account) = Setup(); var clock = new Clock();
        var reader = new Reader { Failure = new VenueReadException("private") };
        var service = new CandleService(store, TestVenues.With(reader), new CandleCache(clock), clock);
        var failure = await Assert.ThrowsAsync<ProblemException>(() => service.CandlesAsync(account.Id, "BTC", "1h", null, default));
        Assert.Equal(502, failure.StatusCode);
        reader.Failure = null;
        Assert.Single((await service.CandlesAsync(account.Id, "BTC", "1h", null, default)).Candles);
        await service.CandlesAsync(account.Id, "BTC", "1h", null, default);
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task Refresh_bypasses_replaces_and_coalesces_identical_requests()
    {
        var (_, store, account) = Setup(); var clock = new Clock(); var reader = new Reader();
        var cache = new CandleCache(clock);
        var service = new CandleService(store, TestVenues.With(reader), cache, clock);
        var original = await service.CandlesAsync(account.Id, "BTC", "1h", null, default);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Wait = ct => release.Task.WaitAsync(ct);
        reader.Empty = true;
        var first = service.CandlesAsync(account.Id, "BTC", "1h", null, default, "true");
        var (otherOwner, otherStore, otherAccount) = Setup();
        var other = new CandleService(otherStore, TestVenues.With(reader), cache, clock, new CoreOwner(otherOwner));
        var second = other.CandlesAsync(otherAccount.Id, "BTC", "1h", null, default, "true");
        var normal = service.CandlesAsync(account.Id, "BTC", "1h", null, default, "false");
        Assert.Equal(2, reader.Reads);
        release.SetResult();
        var results = await Task.WhenAll(first, second, normal);
        Assert.All(results, result => Assert.Empty(result.Candles));
        Assert.NotSame(original, await service.CandlesAsync(account.Id, "BTC", "1h", null, default));
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task Canonical_identity_is_resolved_to_native_and_replacements_do_not_share_cache()
    {
        var (_, store, account) = Setup(); var clock = new Clock(); var reader = new Reader();
        var catalogue = new FixtureReader();
        catalogue.Result = catalogue.Result with { Instruments = [new("1000PEPE", 2, 10, "USDC", VenueContractId: "kPEPE")] };
        var service = new CandleService(store, TestVenues.With(reader, catalogue), new CandleCache(clock), clock);
        var end = clock.Now.AddDays(-1).ToUnixTimeMilliseconds();
        var result = await service.CandlesAsync(account.Id, "1000PEPE", "1h", end, default);
        Assert.Equal("1000PEPE", result.Instrument);
        Assert.Equal("kPEPE", reader.Native);
        var unknown = await Assert.ThrowsAsync<ProblemException>(() => service.CandlesAsync(account.Id, "kPEPE", "1h", end, default));
        Assert.Equal(409, unknown.StatusCode);
        Assert.Equal("This market is no longer listed.", unknown.Message);
        catalogue.Result = catalogue.Result with { Instruments = [new("1000PEPE", 2, 10, "USDC", VenueContractId: "18")] };
        await service.CandlesAsync(account.Id, "1000PEPE", "1h", end, default);
        Assert.Equal(2, reader.Reads);
        Assert.Equal("18", reader.Native);
    }

    [Fact]
    public async Task Interval_support_is_rechecked_before_a_cached_window()
    {
        var (_, store, account) = Setup(); var clock = new Clock(); var reader = new Reader();
        var intervals = new List<string> { "1h" };
        var descriptor = HyperliquidPerpetualReader.Descriptor with { Intervals = intervals };
        var registry = new VenueRegistry([descriptor], [new FixtureReader()], [], [reader], [], [], [], []);
        var service = new CandleService(store, registry, new CandleCache(clock), clock);
        var end = clock.Now.AddDays(-1).ToUnixTimeMilliseconds();
        await service.CandlesAsync(account.Id, "BTC", "1h", end, default);
        intervals.Remove("1h");
        var unsupported = await Assert.ThrowsAsync<ProblemException>(() => service.CandlesAsync(account.Id, "BTC", "1h", end, default));
        Assert.Equal(400, unsupported.StatusCode);
        Assert.Equal(1, reader.Reads);
    }
}
