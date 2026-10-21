using Vessel.Application.Ownership;
using System.Runtime.CompilerServices;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;

namespace Vessel.Application.MarketData;

public sealed record MarketStreamStatusDto(string State, DateTimeOffset ObservedAt);

/// <summary>One server-sent event: a named event with a DTO payload, or a keepalive comment when <see cref="Event"/> is null.</summary>
public sealed record MarketStreamMessage(string? Event, object? Data)
{
    public static readonly MarketStreamMessage Keepalive = new(null, null);
}

/// <summary>Process-wide bound on concurrent live streams. Register as a singleton.</summary>
public sealed class MarketStreamLimiter
{
    public const int MaxStreams = 8;
    public const int MaxStreamsPerUser = 4;
    private readonly Dictionary<Guid, int> users = [];
    private int active;
    public int Active { get { lock (users) return active; } }

    public IDisposable? TryAcquire(Guid owner)
    {
        lock (users)
        {
            var count = users.GetValueOrDefault(owner);
            if (active >= MaxStreams || count >= MaxStreamsPerUser) return null;
            active++;
            users[owner] = count + 1;
            return new Lease(this, owner);
        }
    }

    private sealed class Lease(MarketStreamLimiter limiter, Guid owner) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            lock (limiter.users)
            {
                limiter.active--;
                if (--limiter.users[owner] == 0) limiter.users.Remove(owner);
            }
        }
    }
}

public sealed class MarketStreamService(IWorkspaceStore store, IVenueRegistry venues, MarketStreamLimiter limiter, TimeProvider time, IJournalOwnerContext? owner = null)
{
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(15);
    private const string VenueFailure = "The venue market stream is unavailable. Try again later.";
    private const string Busy = "Too many live market streams are open. Try again later.";

    /// <summary>Runs every request check and reserves capacity before any response is written.</summary>
    public async Task<MarketStreamSession> OpenAsync(Guid accountId, string? instrument, string? interval, CancellationToken ct)
    {
        var account = await MarketDataGuard.AccountAsync(store, accountId, ct);
        if (account.TradingMode == "paper")
            throw new ProblemException(409, "Paper accounts use scheduled closed-candle checks, not a continuous price stream.");
        instrument = MarketDataGuard.Instrument(instrument);
        var venue = venues.Descriptor(account.VenueId);
        var (validInterval, _) = MarketDataGuard.Interval(interval, venue);
        if (venues.Stream(account.VenueId) is not { } stream)
            throw new ProblemException(502, VenueFailure);
        ct.ThrowIfCancellationRequested();

        var lease = limiter.TryAcquire(owner?.OwnerId ?? Guid.Empty) ?? throw new ProblemException(429, Busy, code: "rate-limited", retryAfterSeconds: 5);
        try
        {
            var subscription = stream.Subscribe(instrument, validInterval);
            return new MarketStreamSession(account.VenueId, instrument, subscription, lease, time,
                venue?.MarketContextNotice ?? MarketContextService.Notice);
        }
        catch (MarketStreamCapacityException)
        {
            lease.Dispose();
            throw new ProblemException(429, Busy, code: "rate-limited", retryAfterSeconds: 5);
        }
        catch (VenueReadException)
        {
            lease.Dispose();
            throw new ProblemException(502, VenueFailure);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}

public sealed class MarketStreamSession : IDisposable
{
    private readonly string venueId;
    private readonly string contextNotice;
    private readonly string instrument;
    private readonly IMarketStreamSubscription subscription;
    private readonly IDisposable lease;
    private readonly TimeProvider time;
    private int disposed;

    internal MarketStreamSession(string venueId, string instrument, IMarketStreamSubscription subscription,
        IDisposable lease, TimeProvider time, string contextNotice)
    {
        this.venueId = venueId; this.instrument = instrument; this.subscription = subscription;
        this.lease = lease; this.time = time; this.contextNotice = contextNotice;
    }

    /// <summary>
    /// Yields mapped events plus a keepalive when nothing was sent for 15 s. Ends quietly on
    /// cancellation or when the one-hour lifetime elapses.
    /// </summary>
    public async IAsyncEnumerable<MarketStreamMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var lifetime = new CancellationTokenSource(MarketStreamService.MaxLifetime, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        var events = subscription.ReadAllAsync(linked.Token).GetAsyncEnumerator(linked.Token);
        Task<bool>? next = null;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                next ??= events.MoveNextAsync().AsTask();
                using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
                {
                    var keepalive = Task.Delay(MarketStreamService.KeepaliveInterval, time, quiet.Token);
                    var winner = await Task.WhenAny(next, keepalive);
                    quiet.Cancel();
                    if (winner != next)
                    {
                        if (keepalive.IsCanceled) break;
                        yield return MarketStreamMessage.Keepalive;
                        continue;
                    }
                }
                var completed = next;
                next = null;
                if (completed.IsCanceled) break;
                if (!await completed) break;
                if (Map(events.Current) is { } message) yield return message;
            }
        }
        finally
        {
            linked.Cancel();
            if (next is not null)
            {
                // Abandoning a pending read during shutdown; its outcome no longer matters.
                try { await next; }
                catch (Exception) { }
            }
            await events.DisposeAsync();
        }
    }

    private MarketStreamMessage? Map(MarketStreamEvent value) => value switch
    {
        MarketCandleEvent { Candle: var c } =>
            new("candle", new CandleDto(c.OpenTime, c.CloseTime, c.Open, c.High, c.Low, c.Close, c.Volume, c.Trades)),
        MarketContextEvent { Context: var c, ObservedAt: var observed } when c.ContractId == instrument =>
            new("context", new MarketContextDto(venueId, instrument, c.MarkPrice, c.OraclePrice, c.MidPrice,
                c.PreviousDayPrice, c.DayNotionalVolume, c.OpenInterest, c.FundingRate, c.Premium, observed,
                contextNotice)),
        MarketStatusEvent { State: var state, ObservedAt: var observed } =>
            new("status", new MarketStreamStatusDto(state switch
            {
                MarketStreamState.Live => "live",
                MarketStreamState.Reconnecting => "reconnecting",
                _ => "stale"
            }, observed)),
        _ => null
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        subscription.Dispose();
        lease.Dispose();
    }
}
