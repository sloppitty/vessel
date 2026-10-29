using Microsoft.Extensions.Logging.Abstractions;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class HyperliquidMarketStreamTests
{
    private const string SubscribeBtc1m = """{"method":"subscribe","subscription":{"type":"candle","coin":"BTC","interval":"1m"}}""";
    private const string SubscribeBtcCtx = """{"method":"subscribe","subscription":{"type":"activeAssetCtx","coin":"BTC"}}""";
    private const string UnsubscribeBtc1m = """{"method":"unsubscribe","subscription":{"type":"candle","coin":"BTC","interval":"1m"}}""";
    private const string UnsubscribeBtcCtx = """{"method":"unsubscribe","subscription":{"type":"activeAssetCtx","coin":"BTC"}}""";
    private const string AckBtc1m = """{"channel":"subscriptionResponse","data":{"method":"subscribe","subscription":{"type":"candle","interval":"1m","coin":"BTC"}}}""";
    private const string Pong = """{"channel":"pong"}""";

    private static string Candle(string coin = "BTC", string interval = "1m", long t = 1_790_927_520_000, string close = "85903.0",
        string high = "85903.0", string low = "85887.0", string volume = "6.19755") =>
        $$"""{"t":{{t}},"T":{{t + 59_999}},"s":"{{coin}}","i":"{{interval}}","o":"85891.0","c":"{{close}}","h":"{{high}}","l":"{{low}}","v":"{{volume}}","n":58}""";

    private static string CandleMessage(string data) => $$"""{"channel":"candle","data":{{data}}}""";

    private const string StringCtx =
        """{"channel":"activeAssetCtx","data":{"coin":"BTC","ctx":{"funding":"0.0000125","openInterest":"34715.04452","prevDayPx":"83388.0","dayNtlVlm":"2524355036.618314743","premium":"-0.0001513106","oraclePx":"85916.0","markPx":"85896.4","midPx":"85902.5","impactPxs":["85902.0","85903.0"],"dayBaseVlm":"29799.4898"}}}""";

    private const string NumericCtx =
        """{"channel":"activeAssetCtx","data":{"coin":"BTC","ctx":{"funding":0.0000125,"openInterest":34715.04450,"prevDayPx":83388.0,"dayNtlVlm":2524355036.618314743,"premium":null,"oraclePx":85916,"markPx":85896.40,"midPx":null,"impactPxs":null,"dayBaseVlm":1}}}""";

    private static (HyperliquidMarketStream Stream, FakeSocketFactory Sockets, ManualTime Time) Create()
    {
        var sockets = new FakeSocketFactory();
        var time = new ManualTime(DateTimeOffset.FromUnixTimeMilliseconds(1_790_927_520_000));
        return (new HyperliquidMarketStream(sockets, time, NullLogger<HyperliquidMarketStream>.Instance), sockets, time);
    }

    private static async Task<FakeSocket> Connected(FakeSocketFactory sockets, int count = 1)
    {
        await Eventually.True(() => sockets.Sockets.Count >= count && sockets.Sockets[count - 1].Connected, "socket connects");
        return sockets.Sockets[count - 1];
    }

    // Waits until the receive loop has handled every pushed message.
    private static async Task Handled(FakeSocket socket, params string[] messages)
    {
        var before = socket.Reads;
        foreach (var message in messages) socket.Push(message);
        await Eventually.True(() => socket.Reads >= before + messages.Length, "messages handled");
    }

    [Fact]
    public async Task Connects_lazily_reference_counts_and_unsubscribes_when_the_last_listener_leaves()
    {
        var (stream, sockets, _) = Create();
        await using var _stream = stream;
        Assert.Empty(sockets.Sockets);

        var first = stream.Subscribe("BTC", "1m");
        var socket = await Connected(sockets);
        await Eventually.True(() => socket.Sent.Count == 2, "subscribes once");
        Assert.Equal(new[] { SubscribeBtc1m, SubscribeBtcCtx }.Order(), socket.Sent.Order());

        var second = stream.Subscribe("BTC", "1m");
        var other = stream.Subscribe("BTC", "5m");
        Assert.Equal(3, stream.SubscriptionCount);
        await Eventually.True(() => socket.Sent.Count == 3, "only the new candle interval is subscribed");
        Assert.Contains("""{"method":"subscribe","subscription":{"type":"candle","coin":"BTC","interval":"5m"}}""", socket.Sent);

        first.Dispose();
        first.Dispose();
        Assert.Equal(3, stream.SubscriptionCount);
        second.Dispose();
        await Eventually.True(() => socket.Sent.Contains(UnsubscribeBtc1m), "last 1m listener unsubscribes");
        Assert.DoesNotContain(UnsubscribeBtcCtx, socket.Sent);
        other.Dispose();
        await Eventually.True(() => socket.Sent.Contains(UnsubscribeBtcCtx), "last context listener unsubscribes");
        Assert.Equal(0, stream.SubscriptionCount);
        Assert.Single(sockets.Sockets);
    }

    [Fact]
    public async Task Closes_after_sixty_idle_seconds_and_reconnects_lazily()
    {
        var (stream, sockets, time) = Create();
        await using var _stream = stream;
        var leaving = stream.Subscribe("BTC", "1m");
        var socket = await Connected(sockets);
        leaving.Dispose();
        await Eventually.True(() => time.TimerCount > 0, "tick timer armed");
        time.Advance(TimeSpan.FromSeconds(55));
        await Task.Delay(50);
        Assert.False(socket.Disposed);
        time.Advance(TimeSpan.FromSeconds(10));
        await Eventually.True(() => socket.Disposed && socket.Closed, "idle socket closes");

        using var again = stream.Subscribe("BTC", "1m");
        var next = await Connected(sockets, 2);
        await Eventually.True(() => next.Sent.Count == 2, "resubscribes on the new socket");
    }

    [Fact]
    public async Task A_listener_that_returns_before_idle_close_keeps_the_socket()
    {
        var (stream, sockets, time) = Create();
        await using var _stream = stream;
        var leaving = stream.Subscribe("BTC", "1m");
        var socket = await Connected(sockets);
        leaving.Dispose();
        await Eventually.True(() => time.TimerCount > 0, "tick timer armed");
        time.Advance(TimeSpan.FromSeconds(40));
        using var back = stream.Subscribe("BTC", "1m");
        time.Advance(TimeSpan.FromSeconds(40));
        await Task.Delay(50);
        Assert.False(socket.Disposed);
        Assert.Single(sockets.Sockets);
    }

    [Fact]
    public async Task Pings_every_thirty_seconds()
    {
        var (stream, sockets, time) = Create();
        await using var _stream = stream;
        using var listener = stream.Subscribe("BTC", "1m");
        var socket = await Connected(sockets);
        await Eventually.True(() => time.TimerCount > 0, "tick timer armed");
        time.Advance(TimeSpan.FromSeconds(25));
        await Task.Delay(20);
        Assert.DoesNotContain("""{"method":"ping"}""", socket.Sent);
        time.Advance(TimeSpan.FromSeconds(5));
        await Eventually.True(() => socket.Sent.Count(m => m == """{"method":"ping"}""") == 1, "first ping");
        await Handled(socket, Pong);
        time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.True(() => socket.Sent.Count(m => m == """{"method":"ping"}""") == 2, "second ping");
    }

    [Fact]
    public async Task Reconnects_with_backoff_resubscribes_everything_and_reports_status()
    {
        var (stream, sockets, time) = Create();
        await using var _stream = stream;
        using var listener = stream.Subscribe("BTC", "1m");
        await using var events = listener.ReadAllAsync(default).GetAsyncEnumerator();
        var socket = await Connected(sockets);
        await Handled(socket, AckBtc1m);
        var live = Assert.IsType<MarketStatusEvent>(await Eventually.Next(events));
        Assert.Equal(MarketStreamState.Live, live.State);

        sockets.FailConnect = true;
        socket.Fail();
        var down = Assert.IsType<MarketStatusEvent>(await Eventually.Next(events));
        Assert.Equal(MarketStreamState.Reconnecting, down.State);
        Assert.True(socket.Disposed);

        // Failed attempts keep backing off without duplicate status events.
        await Eventually.True(() => sockets.Sockets.Count >= 3, "retries", () => time.Advance(TimeSpan.FromSeconds(1)));
        sockets.FailConnect = false;
        await Eventually.True(() => sockets.Latest.Connected, "reconnects", () => time.Advance(TimeSpan.FromSeconds(1)));
        var next = sockets.Latest;
        await Eventually.True(() => next.Sent.Count >= 2, "resubscribes");
        Assert.Equal(new[] { SubscribeBtc1m, SubscribeBtcCtx }.Order(), next.Sent.Take(2).Order());

        await Handled(next, AckBtc1m);
        var back = Assert.IsType<MarketStatusEvent>(await Eventually.Next(events));
        Assert.Equal(MarketStreamState.Live, back.State);
    }

    [Fact]
    public async Task Peer_close_also_reconnects()
    {
        var (stream, sockets, time) = Create();
        await using var _stream = stream;
        using var listener = stream.Subscribe("ETH", "15m");
        var socket = await Connected(sockets);
        socket.PeerClose();
        await Eventually.True(() => sockets.Sockets.Count == 2 && sockets.Latest.Connected, "reconnects",
            () => time.Advance(TimeSpan.FromSeconds(1)));
        await Eventually.True(() => sockets.Latest.Sent.Count == 2, "resubscribes");
    }

    [Fact]
    public void Backoff_is_jittered_exponential_between_one_and_thirty_seconds()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            for (var sample = 0; sample < 50; sample++)
            {
                var delay = Backoff(attempt);
                Assert.InRange(delay, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
                Assert.True(delay.TotalSeconds <= Math.Min(30, Math.Pow(2, attempt)) + 0.001);
            }
        }
        Assert.True(Enumerable.Range(0, 50).Select(_ => Backoff(10)).Min() >= TimeSpan.FromSeconds(15));
    }

    private static TimeSpan Backoff(int attempt) =>
        (TimeSpan)typeof(HyperliquidMarketStream).GetMethod("Backoff",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [attempt])!;

    [Fact]
    public async Task Reports_stale_after_sixty_silent_seconds_and_live_when_messages_resume()
    {
        var (stream, sockets, time) = Create();
        await using var _stream = stream;
        using var listener = stream.Subscribe("BTC", "1m");
        await using var events = listener.ReadAllAsync(default).GetAsyncEnumerator();
        var socket = await Connected(sockets);
        await Handled(socket, AckBtc1m);
        Assert.Equal(MarketStreamState.Live, Assert.IsType<MarketStatusEvent>(await Eventually.Next(events)).State);
        await Eventually.True(() => time.TimerCount > 0, "tick timer armed");

        time.Advance(TimeSpan.FromSeconds(55));
        time.Advance(TimeSpan.FromSeconds(5));
        var stale = Assert.IsType<MarketStatusEvent>(await Eventually.Next(events));
        Assert.Equal(MarketStreamState.Stale, stale.State);
        Assert.Equal(time.GetUtcNow(), stale.ObservedAt);

        await Handled(socket, Pong);
        Assert.Equal(MarketStreamState.Live, Assert.IsType<MarketStatusEvent>(await Eventually.Next(events)).State);
    }

    [Fact]
    public async Task A_silent_socket_is_replaced()
    {
        var (stream, sockets, time) = Create();
        await using var _stream = stream;
        using var listener = stream.Subscribe("BTC", "1m");
        var socket = await Connected(sockets);
        await Eventually.True(() => time.TimerCount > 0, "tick timer armed");
        time.Advance(TimeSpan.FromSeconds(90));
        await Eventually.True(() => socket.Disposed, "dead socket closes");
        await Eventually.True(() => sockets.Sockets.Count == 2, "reconnects", () => time.Advance(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Parses_candle_objects_and_arrays_keeping_exact_strings_and_routes_by_coin_and_interval()
    {
        var (stream, sockets, _) = Create();
        await using var _stream = stream;
        using var btc = stream.Subscribe("BTC", "1m");
        using var eth = stream.Subscribe("ETH", "1m");
        await using var btcEvents = btc.ReadAllAsync(default).GetAsyncEnumerator();
        await using var ethEvents = eth.ReadAllAsync(default).GetAsyncEnumerator();
        var socket = await Connected(sockets);

        await Handled(socket, CandleMessage(Candle()));
        Assert.Equal(MarketStreamState.Live, Assert.IsType<MarketStatusEvent>(await Eventually.Next(btcEvents)).State);
        var candle = Assert.IsType<MarketCandleEvent>(await Eventually.Next(btcEvents)).Candle;
        Assert.Equal(new VenueCandle(1_790_927_520_000, 1_790_927_579_999, "85891.0", "85903.0", "85887.0", "85903.0", "6.19755", 58), candle);

        await Handled(socket, CandleMessage($"[{Candle("BTC", "5m")},{Candle("ETH", t: 1_790_927_580_000, volume: "0.10")}]"));
        Assert.Equal(MarketStreamState.Live, Assert.IsType<MarketStatusEvent>(await Eventually.Next(ethEvents)).State);
        var ethCandle = Assert.IsType<MarketCandleEvent>(await Eventually.Next(ethEvents)).Candle;
        Assert.Equal(1_790_927_580_000, ethCandle.OpenTime);
        Assert.Equal("0.10", ethCandle.Volume);

        // Numeric decimal tokens keep their raw text.
        await Handled(socket, CandleMessage(Candle().Replace("\"v\":\"6.19755\"", "\"v\":6.197550")));
        Assert.Equal("6.197550", Assert.IsType<MarketCandleEvent>(await Eventually.Next(btcEvents)).Candle.Volume);
        Assert.Equal(0, stream.Dropped);
    }

    [Fact]
    public async Task Parses_active_asset_context_from_strings_and_numbers_without_floating_point()
    {
        var (stream, sockets, time) = Create();
        await using var _stream = stream;
        using var listener = stream.Subscribe("BTC", "1m");
        await using var events = listener.ReadAllAsync(default).GetAsyncEnumerator();
        var socket = await Connected(sockets);

        await Handled(socket, StringCtx);
        Assert.IsType<MarketStatusEvent>(await Eventually.Next(events));
        var text = Assert.IsType<MarketContextEvent>(await Eventually.Next(events));
        Assert.Equal(new VenueMarketContext("BTC", "85896.4", "85916.0", "85902.5", "83388.0", "2524355036.618314743",
            "34715.04452", "0.0000125", "-0.0001513106"), text.Context);
        Assert.Equal(time.GetUtcNow(), text.ObservedAt);

        await Handled(socket, NumericCtx);
        var numeric = Assert.IsType<MarketContextEvent>(await Eventually.Next(events)).Context;
        Assert.Equal(new VenueMarketContext("BTC", "85896.40", "85916", null, "83388.0", "2524355036.618314743",
            "34715.04450", "0.0000125", null), numeric);
        Assert.Equal(0, stream.Dropped);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"channel":"candle"}""")]
    [InlineData("""{"channel":"candle","channel":"candle","data":{}}""")]
    [InlineData("""{"channel":"activeAssetCtx","data":{"coin":"BTC","ctx":{"funding":"1e-5","openInterest":"1","prevDayPx":"1","dayNtlVlm":"1","premium":null,"oraclePx":"1","markPx":"1","midPx":null}}}""")]
    [InlineData("""{"channel":"activeAssetCtx","data":{"coin":"BTC","ctx":{"funding":"0","openInterest":"1","prevDayPx":"1","dayNtlVlm":"1","premium":null,"oraclePx":"1","markPx":-1,"midPx":null}}}""")]
    [InlineData("""{"channel":"activeAssetCtx","data":{"coin":"BTC","ctx":{"funding":1e-5,"openInterest":"1","prevDayPx":"1","dayNtlVlm":"1","premium":null,"oraclePx":"1","markPx":"1","midPx":null}}}""")]
    [InlineData("""{"channel":"activeAssetCtx","data":{"coin":"BTC","ctx":{"funding":"0"}}}""")]
    [InlineData("high-below-low")]
    [InlineData("negative-volume")]
    [InlineData("string-time")]
    public async Task Malformed_messages_are_dropped_and_counted(string message)
    {
        message = message switch
        {
            "high-below-low" => CandleMessage(Candle(high: "1.0")),
            "negative-volume" => CandleMessage(Candle(volume: "-1")),
            "string-time" => CandleMessage(Candle().Replace("\"t\":1790927520000", "\"t\":\"1790927520000\"")),
            _ => message
        };
        var (stream, sockets, _) = Create();
        await using var _stream = stream;
        using var listener = stream.Subscribe("BTC", "1m");
        await using var events = listener.ReadAllAsync(default).GetAsyncEnumerator();
        var socket = await Connected(sockets);
        await Handled(socket, message);
        // The socket counts a read before the stream has parsed it.
        await Eventually.True(() => stream.Dropped == 1, "malformed message dropped");
        Assert.Equal(1, stream.Dropped);
        // The next valid message is the first thing the listener sees.
        await Handled(socket, CandleMessage(Candle(close: "85900.0")));
        Assert.IsType<MarketStatusEvent>(await Eventually.Next(events));
        Assert.Equal("85900.0", Assert.IsType<MarketCandleEvent>(await Eventually.Next(events)).Candle.Close);
    }

    [Fact]
    public async Task Venue_error_messages_are_ignored_without_counting_as_malformed()
    {
        var (stream, sockets, _) = Create();
        await using var _stream = stream;
        using var listener = stream.Subscribe("BTC", "1m");
        var socket = await Connected(sockets);
        await Handled(socket, """{"channel":"error","data":"Invalid subscription"}""", Pong);
        Assert.Equal(0, stream.Dropped);
    }

    [Fact]
    public async Task Slow_readers_get_coalesced_candles_and_only_the_latest_context()
    {
        var (stream, sockets, _) = Create();
        await using var _stream = stream;
        using var listener = stream.Subscribe("BTC", "1m");
        var socket = await Connected(sockets);
        await Handled(socket,
            CandleMessage(Candle(close: "85890.0")), StringCtx, CandleMessage(Candle(close: "85895.0")),
            CandleMessage(Candle(t: 1_790_927_580_000, close: "85899.0", high: "85903.0")), NumericCtx,
            CandleMessage(Candle(close: "85901.0")));

        await using var events = listener.ReadAllAsync(default).GetAsyncEnumerator();
        var received = new List<MarketStreamEvent>();
        for (var i = 0; i < 4; i++) received.Add(await Eventually.Next(events));
        Assert.Equal(MarketStreamState.Live, Assert.IsType<MarketStatusEvent>(received[0]).State);
        var first = Assert.IsType<MarketCandleEvent>(received[1]).Candle;
        Assert.Equal((1_790_927_520_000L, "85901.0"), (first.OpenTime, first.Close));
        Assert.Equal("85916", Assert.IsType<MarketContextEvent>(received[2]).Context.OraclePrice);
        Assert.Equal(1_790_927_580_000, Assert.IsType<MarketCandleEvent>(received[3]).Candle.OpenTime);
    }

    [Fact]
    public async Task Late_joiners_get_the_latest_shared_data_and_live_status()
    {
        var (stream, sockets, _) = Create();
        await using var _stream = stream;
        using var first = stream.Subscribe("BTC", "1m");
        var socket = await Connected(sockets);
        await Handled(socket, AckBtc1m, CandleMessage(Candle()), StringCtx);
        using var late = stream.Subscribe("BTC", "1m");
        await using var events = late.ReadAllAsync(default).GetAsyncEnumerator();
        Assert.Equal(MarketStreamState.Live, Assert.IsType<MarketStatusEvent>(await Eventually.Next(events)).State);
        Assert.IsType<MarketCandleEvent>(await Eventually.Next(events));
        Assert.IsType<MarketContextEvent>(await Eventually.Next(events));
    }

    [Fact]
    public async Task Disposing_while_connecting_does_not_wait_forever_for_the_late_connection()
    {
        var (stream, sockets, _) = Create();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sockets.ConnectGate = gate;
        using var listener = stream.Subscribe("BTC", "1m");
        await Eventually.True(() => sockets.Sockets.Count == 1 && sockets.Sockets[0].Connecting, "socket starts connecting");
        var disposing = stream.DisposeAsync().AsTask();
        gate.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sockets.Sockets[0].Disposed);
    }

    [Fact]
    public async Task Bounds_upstream_subscriptions_at_one_hundred()
    {
        var (stream, _, _) = Create();
        await using var _stream = stream;
        var listeners = Enumerable.Range(0, 50).Select(i => stream.Subscribe($"C{i}", "1m")).ToList();
        Assert.Equal(100, stream.SubscriptionCount);
        Assert.Throws<MarketStreamCapacityException>(() => stream.Subscribe("OTHER", "1m"));
        Assert.Throws<MarketStreamCapacityException>(() => stream.Subscribe("C0", "5m"));
        using var shared = stream.Subscribe("C0", "1m"); // Shares existing subscriptions.
        listeners[1].Dispose();
        using var room = stream.Subscribe("OTHER", "1m");
        Assert.Equal(100, stream.SubscriptionCount);
        foreach (var listener in listeners) listener.Dispose();
    }

    [Theory]
    [InlineData("xyz:BTC")]
    [InlineData("@107")]
    [InlineData("PURR/USDC")]
    public async Task Rejects_non_primary_contracts(string coin)
    {
        var (stream, sockets, _) = Create();
        await using var _stream = stream;
        Assert.Throws<VenueReadException>(() => stream.Subscribe(coin, "1m"));
        Assert.Empty(sockets.Sockets);
    }

    [Fact]
    public async Task Disposing_a_subscription_ends_its_reader()
    {
        var (stream, sockets, _) = Create();
        await using var _stream = stream;
        var listener = stream.Subscribe("BTC", "1m");
        await Connected(sockets);
        await using var events = listener.ReadAllAsync(default).GetAsyncEnumerator();
        var next = events.MoveNextAsync();
        listener.Dispose();
        Assert.False(await next.AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
