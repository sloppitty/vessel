using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;

namespace Vessel.Infrastructure.Venues.Hyperliquid;

/// <summary>
/// One shared, lazily connected Hyperliquid WebSocket per process (register as a singleton).
/// Upstream subscriptions are reference-counted across listeners; the socket closes after 60 s
/// without listeners, pings every 30 s and reconnects with jittered exponential backoff,
/// resubscribing everything. State is guarded by one lock; listener queues are bounded and
/// coalescing so a slow reader never blocks the receive loop.
/// </summary>
public sealed class HyperliquidMarketStream : IMarketStream, IAsyncDisposable
{
    // https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/websocket
    // https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/websocket/timeouts-and-heartbeats
    // Default only; the host is configuration (Vessel:Venues:Hyperliquid:StreamUrl).
    public static readonly Uri DefaultEndpoint = new("wss://api.hyperliquid.xyz/ws");
    public const int MaxSubscriptions = 100;
    public const int MaxMessageBytes = 1024 * 1024;
    public static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);
    // A socket that stays silent despite pings is presumed dead and replaced.
    public static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private const int MaxOutbound = 256;
    private const string Ping = """{"method":"ping"}""";

    private readonly IWebSocketTransportFactory transports;
    private readonly Uri endpoint;
    private readonly TimeProvider time;
    private readonly ILogger<HyperliquidMarketStream> logger;
    private readonly object gate = new();
    private readonly Dictionary<string, Upstream> upstream = new(StringComparer.Ordinal);
    private readonly List<Listener> listeners = [];
    private readonly CancellationTokenSource shutdown = new();
    private Connection? connection;
    private bool running, down, stale, disposed;
    private DateTimeOffset idleSince;
    private long dropped;
    private Task loop = Task.CompletedTask;

    public HyperliquidMarketStream(IWebSocketTransportFactory transports, TimeProvider time, ILogger<HyperliquidMarketStream> logger,
        Uri? endpoint = null)
    {
        this.transports = transports; this.time = time; this.logger = logger;
        this.endpoint = endpoint ?? DefaultEndpoint;
    }

    public string VenueId => HyperliquidPerpetualReader.Id;

    /// <summary>Malformed upstream messages dropped since start.</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    /// <summary>Active upstream subscription count (for diagnostics and tests).</summary>
    public int SubscriptionCount { get { lock (gate) return upstream.Count; } }

    public IMarketStreamSubscription Subscribe(string contractId, string interval)
    {
        if (string.IsNullOrWhiteSpace(contractId) || !HyperliquidPerpetualReader.IsPerpetualContract(contractId) ||
            string.IsNullOrWhiteSpace(interval))
            throw new VenueReadException("A perpetual contract and interval are required.");
        var listener = new Listener(this, HyperliquidInstruments.Native(contractId), interval);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var needed = listener.Keys.Count(key => !upstream.ContainsKey(key));
            if (upstream.Count + needed > MaxSubscriptions)
                throw new MarketStreamCapacityException("The upstream subscription limit is reached.");
            foreach (var (key, subscribe, unsubscribe) in listener.Subscriptions())
            {
                if (!upstream.TryGetValue(key, out var entry))
                {
                    upstream.Add(key, entry = new Upstream(subscribe, unsubscribe));
                    connection?.Send(subscribe);
                }
                entry.References++;
            }
            listeners.Add(listener);
            var now = time.GetUtcNow();
            if (connection is not null)
            {
                // Late joiners share an already acknowledged subscription; replay the latest data.
                var candle = upstream[listener.CandleKey];
                var context = upstream[listener.ContextKey];
                listener.Acknowledged = candle.Acknowledged || context.Acknowledged ||
                    candle.LastCandle is not null || context.LastContext is not null;
                RefreshStatus(listener, now);
                if (candle.LastCandle is { } lastCandle) listener.Post(lastCandle);
                if (context.LastContext is { } lastContext) listener.Post(lastContext);
            }
            else RefreshStatus(listener, now);
            if (!running)
            {
                running = true;
                loop = Task.Run(RunAsync);
            }
        }
        return listener;
    }

    private void Remove(Listener listener)
    {
        lock (gate)
        {
            if (!listeners.Remove(listener)) return;
            foreach (var key in listener.Keys)
            {
                var entry = upstream[key];
                if (--entry.References > 0) continue;
                upstream.Remove(key);
                connection?.Send(entry.Unsubscribe);
            }
            if (listeners.Count == 0) idleSince = time.GetUtcNow();
        }
    }

    private async Task RunAsync()
    {
        var attempt = 0;
        while (true)
        {
            lock (gate)
            {
                if (listeners.Count == 0 || disposed)
                {
                    running = false; down = false; stale = false;
                    return;
                }
            }

            var conn = new Connection();
            var transport = transports.Create();
            try
            {
                using (var connectDeadline = new CancellationTokenSource(ConnectTimeout, time))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(connectDeadline.Token, shutdown.Token))
                    await transport.ConnectAsync(endpoint, linked.Token);

                lock (gate)
                {
                    // Disposal that raced the connect found no connection to cancel; close this one instead.
                    if (disposed) throw new OperationCanceledException(shutdown.Token);
                    var now = time.GetUtcNow();
                    connection = conn;
                    down = false; stale = false;
                    conn.LastMessage = conn.LastPing = now;
                    foreach (var entry in upstream.Values)
                    {
                        entry.Acknowledged = false;
                        conn.Send(entry.Subscribe);
                    }
                    if (listeners.Count == 0) idleSince = now;
                    conn.Timer = time.CreateTimer(_ => Tick(conn), null, TickInterval, TickInterval);
                }

                var sender = SendAsync(conn, transport);
                try
                {
                    while (!conn.Token.IsCancellationRequested)
                    {
                        var payload = await transport.ReceiveAsync(MaxMessageBytes, conn.Token);
                        if (payload is null) break;
                        attempt = 0;
                        Handle(conn, payload);
                    }
                }
                finally
                {
                    conn.Cancel();
                    await sender;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Every failure leads to a reconnect; the loop itself must never fault.
                logger.LogDebug("Hyperliquid market stream connection ended ({Error}).", ex.GetType().Name);
            }
            finally
            {
                conn.Timer?.Dispose();
                conn.Cancel();
                try
                {
                    using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2), time);
                    await transport.CloseAsync(closeDeadline.Token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
                await transport.DisposeAsync();
                conn.Dispose();
            }

            lock (gate)
            {
                if (connection == conn) connection = null;
                foreach (var entry in upstream.Values)
                {
                    entry.Acknowledged = false;
                    entry.LastCandle = null;
                    entry.LastContext = null;
                }
                foreach (var listener in listeners) listener.Acknowledged = false;
                if (listeners.Count == 0 || disposed)
                {
                    running = false; down = false; stale = false;
                    return;
                }
                stale = false;
                if (conn.IdleClosed) continue; // A listener arrived during the idle close: reconnect now.
                down = true;
                var now = time.GetUtcNow();
                foreach (var listener in listeners) RefreshStatus(listener, now);
            }

            try { await Task.Delay(Backoff(attempt++), time, shutdown.Token); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Jittered exponential backoff between 1 s and 30 s.</summary>
    internal static TimeSpan Backoff(int attempt)
    {
        var ceiling = Math.Min(MaxBackoff.TotalMilliseconds, MinBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 10)));
        var jittered = ceiling * (0.5 + Random.Shared.NextDouble() * 0.5);
        return TimeSpan.FromMilliseconds(Math.Clamp(jittered, MinBackoff.TotalMilliseconds, MaxBackoff.TotalMilliseconds));
    }

    private static async Task SendAsync(Connection conn, IWebSocketTransport transport)
    {
        try
        {
            await foreach (var message in conn.Outbound.Reader.ReadAllAsync(conn.Token))
                await transport.SendTextAsync(message, conn.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any send failure replaces the connection; the receive loop observes the cancellation.
            conn.Cancel();
        }
    }

    private void Tick(Connection conn)
    {
        var cancel = false;
        lock (gate)
        {
            if (connection != conn) return;
            var now = time.GetUtcNow();
            if (listeners.Count == 0 && now - idleSince >= IdleClose)
            {
                conn.IdleClosed = true;
                cancel = true;
            }
            else
            {
                if (now - conn.LastPing >= PingInterval)
                {
                    conn.Send(Ping);
                    conn.LastPing = now;
                }
                var silent = now - conn.LastMessage;
                if (silent >= DeadAfter) cancel = true;
                else if (silent >= StaleAfter && !stale)
                {
                    stale = true;
                    foreach (var listener in listeners) RefreshStatus(listener, now);
                }
            }
        }
        // Cancel outside the lock so no continuation runs while it is held.
        if (cancel) conn.Cancel();
    }

    private void Handle(Connection conn, byte[] payload)
    {
        HyperliquidStreamMessage? message;
        try
        {
            message = HyperliquidPerpetualReader.ParseStreamMessage(payload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            message = null;
            var count = Interlocked.Increment(ref dropped);
            logger.LogDebug("Dropped a malformed Hyperliquid stream message ({Count} total).", count);
        }
        if (message is HyperliquidStreamControl { Channel: "error" })
            logger.LogDebug("Hyperliquid stream reported an error message.");

        lock (gate)
        {
            if (connection != conn) return;
            var now = time.GetUtcNow();
            conn.LastMessage = now;
            stale = false;
            switch (message)
            {
                case HyperliquidStreamAck ack:
                    var key = ack.Type == "candle" ? CandleKey(ack.Coin, ack.Interval!) : ContextKey(ack.Coin);
                    if (upstream.TryGetValue(key, out var acknowledged))
                    {
                        acknowledged.Acknowledged = true;
                        foreach (var listener in listeners)
                            if (listener.Keys.Contains(key)) listener.Acknowledged = true;
                    }
                    break;
                case HyperliquidStreamCandles candles:
                    foreach (var (coin, interval, candle) in candles.Candles)
                    {
                        if (!upstream.TryGetValue(CandleKey(coin, interval), out var entry)) continue;
                        var update = new MarketCandleEvent(candle);
                        if (entry.LastCandle is null || entry.LastCandle.Candle.OpenTime <= candle.OpenTime)
                            entry.LastCandle = update;
                        foreach (var listener in listeners)
                        {
                            if (listener.Coin != coin || listener.Interval != interval) continue;
                            listener.Acknowledged = true;
                            RefreshStatus(listener, now); // Status precedes the data it qualifies.
                            listener.Post(update);
                        }
                    }
                    break;
                case HyperliquidStreamContext { Coin: var coin, Context: var context }:
                    if (upstream.TryGetValue(ContextKey(coin), out var contextEntry))
                    {
                        var update = new MarketContextEvent(context, now);
                        contextEntry.LastContext = update;
                        foreach (var listener in listeners)
                        {
                            if (listener.Coin != coin) continue;
                            listener.Acknowledged = true;
                            RefreshStatus(listener, now); // Status precedes the data it qualifies.
                            listener.Post(update);
                        }
                    }
                    break;
            }
            foreach (var listener in listeners) RefreshStatus(listener, now);
        }
    }

    // Caller holds the gate. Emits a status only when the listener's visible state changes.
    private void RefreshStatus(Listener listener, DateTimeOffset now)
    {
        MarketStreamState? state = connection is null
            ? down ? MarketStreamState.Reconnecting : null
            : stale ? MarketStreamState.Stale
            : listener.Acknowledged ? MarketStreamState.Live : null;
        if (state is null || state == listener.LastStatus) return;
        listener.LastStatus = state;
        listener.Post(new MarketStatusEvent(state.Value, now));
    }

    internal static string CandleKey(string coin, string interval) => $"candle|{coin}|{interval}";
    internal static string ContextKey(string coin) => $"activeAssetCtx|{coin}";

    public async ValueTask DisposeAsync()
    {
        Connection? current;
        List<Listener> open;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            current = connection;
            open = [.. listeners];
        }
        current?.Cancel();
        await shutdown.CancelAsync();
        foreach (var listener in open) listener.Dispose();
        try { await loop; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        shutdown.Dispose();
    }

    private sealed class Upstream(string subscribe, string unsubscribe)
    {
        public string Subscribe { get; } = subscribe;
        public string Unsubscribe { get; } = unsubscribe;
        public int References;
        public bool Acknowledged;
        public MarketCandleEvent? LastCandle;
        public MarketContextEvent? LastContext;
    }

    private sealed class Connection : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        public Channel<string> Outbound { get; } = Channel.CreateBounded<string>(
            new BoundedChannelOptions(MaxOutbound) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public CancellationToken Token { get; }
        public DateTimeOffset LastMessage, LastPing;
        public bool IdleClosed;
        public ITimer? Timer;
        private int cancelled;

        public Connection() => Token = cancellation.Token;

        // A stuck socket must not grow memory: a full outbound queue replaces the connection,
        // which then resubscribes everything from the current reference counts.
        public void Send(string message)
        {
            if (!Outbound.Writer.TryWrite(message)) ThreadPool.QueueUserWorkItem(_ => Cancel());
        }

        public void Cancel()
        {
            if (Interlocked.Exchange(ref cancelled, 1) != 0) return;
            Outbound.Writer.TryComplete();
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => cancellation.Dispose();
    }

    private sealed class Listener : IMarketStreamSubscription
    {
        public const int MaxPending = 64;
        private readonly HyperliquidMarketStream owner;
        private readonly object sync = new();
        private readonly List<MarketStreamEvent> pending = [];
        private TaskCompletionSource? signal;
        private bool completed;
        private int reading, disposed;

        public Listener(HyperliquidMarketStream owner, string coin, string interval)
        {
            this.owner = owner; Coin = coin; Interval = interval;
            CandleKey = HyperliquidMarketStream.CandleKey(coin, interval);
            ContextKey = HyperliquidMarketStream.ContextKey(coin);
            Keys = [CandleKey, ContextKey];
        }

        public string Coin { get; }
        public string Interval { get; }
        public string CandleKey { get; }
        public string ContextKey { get; }
        public IReadOnlyList<string> Keys { get; }
        // Guarded by the owner's gate.
        public bool Acknowledged;
        public MarketStreamState? LastStatus;

        public IEnumerable<(string Key, string Subscribe, string Unsubscribe)> Subscriptions()
        {
            var candle = new { type = "candle", coin = Coin, interval = Interval };
            var context = new { type = "activeAssetCtx", coin = Coin };
            yield return (CandleKey, JsonSerializer.Serialize(new { method = "subscribe", subscription = candle }),
                JsonSerializer.Serialize(new { method = "unsubscribe", subscription = candle }));
            yield return (ContextKey, JsonSerializer.Serialize(new { method = "subscribe", subscription = context }),
                JsonSerializer.Serialize(new { method = "unsubscribe", subscription = context }));
        }

        // Never blocks: candles coalesce by open time, context and status keep only the latest.
        public void Post(MarketStreamEvent update)
        {
            lock (sync)
            {
                if (completed) return;
                var index = update switch
                {
                    MarketCandleEvent candle => pending.FindIndex(p =>
                        p is MarketCandleEvent other && other.Candle.OpenTime == candle.Candle.OpenTime),
                    MarketContextEvent => pending.FindIndex(p => p is MarketContextEvent),
                    MarketStatusEvent => pending.FindIndex(p => p is MarketStatusEvent),
                    _ => -1
                };
                if (index >= 0) pending[index] = update;
                else
                {
                    if (pending.Count >= MaxPending)
                    {
                        var oldestCandle = pending.FindIndex(p => p is MarketCandleEvent);
                        pending.RemoveAt(oldestCandle >= 0 ? oldestCandle : 0);
                    }
                    pending.Add(update);
                }
                signal?.TrySetResult();
                signal = null;
            }
        }

        public async IAsyncEnumerable<MarketStreamEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref reading, 1) != 0)
                throw new InvalidOperationException("A market stream subscription has a single reader.");
            var batch = new List<MarketStreamEvent>();
            while (true)
            {
                Task? wait = null;
                var done = false;
                lock (sync)
                {
                    if (pending.Count > 0)
                    {
                        batch.AddRange(pending);
                        pending.Clear();
                    }
                    else if (completed) done = true;
                    else
                    {
                        signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        wait = signal.Task;
                    }
                }
                if (done) yield break;
                if (wait is not null)
                {
                    await wait.WaitAsync(cancellationToken);
                    continue;
                }
                foreach (var update in batch) yield return update;
                batch.Clear();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            owner.Remove(this);
            lock (sync)
            {
                completed = true;
                pending.Clear();
                signal?.TrySetResult();
                signal = null;
            }
        }
    }
}
