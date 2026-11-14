using Vessel.Application.Ownership;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;

namespace Vessel.Application.MarketData;

public sealed record CandleDto(long OpenTime, long CloseTime, string Open, string High, string Low, string Close,
    string Volume, int? Trades);

public sealed record CandleResponseDto(string VenueId, string Instrument, string Interval, string PriceSource,
    IReadOnlyList<CandleDto> Candles, long RequestedFrom, long RequestedTo, DateTimeOffset RetrievedAt,
    bool HistoryExhausted, string Notice);

/// <summary>Bounded public candle windows. Register as a singleton; authorization is never cached.</summary>
public sealed class CandleCache(TimeProvider time)
{
    public const int MaxEntries = 1024, MaxStoredCandles = 200_000;
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HistoricalLifetime = TimeSpan.FromHours(24), EmptyLifetime = TimeSpan.FromSeconds(60);
    private readonly BoundedMarketDataCache<CandleResponseDto> entries = new(time, MaxEntries, MaxStoredCandles, v => v.Candles.Count);

    private readonly UserReadLimits refreshLimits = new(time);
    public void AdmitRefresh(Guid owner, string series) => refreshLimits.Admit(owner, series, 1);

    public CandleResponseDto? Get(string key) => entries.Get(key);
    public Task<CandleResponseDto> GetAsync(string key, bool historical, bool refresh,
        Func<CancellationToken, Task<CandleResponseDto>> load, CancellationToken ct) =>
        entries.GetAsync(key, load, value => value.Candles.Count == 0 ? EmptyLifetime :
            historical ? HistoricalLifetime : Lifetime, ct, refresh);
}

public sealed class CandleService(IWorkspaceStore store, IVenueRegistry venues, CandleCache cache, TimeProvider time, IJournalOwnerContext? owner = null)
{
    public const int MaxCandles = 500;
    /// <summary>Used when the venue's descriptor has no notice of its own.</summary>
    public const string Notice = "Prices are trade candles, not fills.";
    private static readonly TimeSpan MaxFuture = TimeSpan.FromDays(1);
    private const long CacheBucketMs = 10_000;

    public async Task<CandleResponseDto> CandlesAsync(Guid accountId, string? instrument, string? interval, long? endTime,
        CancellationToken ct, string? refresh = null)
    {
        var account = await MarketDataGuard.AccountAsync(store, accountId, ct);
        instrument = MarketDataGuard.Instrument(instrument);
        var venue = venues.Descriptor(account.VenueId);
        (interval, var step) = MarketDataGuard.Interval(interval, venue);
        var now = time.GetUtcNow();
        if (endTime is <= 0 || endTime > now.Add(MaxFuture).ToUnixTimeMilliseconds())
            throw new ProblemException(400, "endTime must be a positive UTC millisecond timestamp, at most one day ahead.");
        if (refresh is not (null or "true" or "false"))
            throw new ProblemException(400, "refresh must be true or false.");
        if (venues.Candles(account.VenueId) is not { } reader)
            throw new ProblemException(502, VenueFailure);

        VenueInstrument market;
        try
        {
            if (venues.Reader(account.VenueId) is not { } catalogue) throw new VenueReadException(VenueFailure);
            market = (await catalogue.ReadInstrumentsAsync(ct)).FirstOrDefault(i => i.ContractId == instrument)
                ?? throw new ProblemException(409, "This market is no longer listed.");
        }
        catch (Exception ex) when (VenueFailures.IsReadFailure(ex, ct))
        {
            throw new ProblemException(502, VenueFailure);
        }

        if (refresh == "true") cache.AdmitRefresh(owner?.OwnerId ?? Guid.Empty, $"{account.VenueId}|{instrument}|{interval}");

        var to = endTime ?? now.ToUnixTimeMilliseconds() / CacheBucketMs * CacheBucketMs;
        var from = Math.Max(0, to - MaxCandles * step);
        var key = $"{account.VenueId}|{market.VenueContractId ?? market.ContractId}|{interval}|{from}|{to}";
        var historical = endTime is { } end && end <= now.ToUnixTimeMilliseconds() - 3 * step;
        try
        {
            return await cache.GetAsync(key, historical, refresh == "true", async token =>
            {
                var raw = await reader.ReadNativeCandlesAsync(market, interval, from, to, token);
                var candles = raw.Where(c => c.OpenTime >= from && c.OpenTime <= to)
                    .GroupBy(c => c.OpenTime).Select(g => g.Last()).OrderBy(c => c.OpenTime)
                    // Inclusive aligned windows can contain 501 opens; retain the newest 500.
                    .TakeLast(MaxCandles)
                    .Select(c => new CandleDto(c.OpenTime, c.CloseTime, c.Open, c.High, c.Low, c.Close, c.Volume, c.Trades))
                    .ToList();
                return new CandleResponseDto(account.VenueId, market.ContractId, interval, "trades", candles, from, to,
                    time.GetUtcNow(), candles.Count == 0, venue?.CandleNotice ?? Notice);
            }, ct);
        }
        catch (Exception ex) when (VenueFailures.IsReadFailure(ex, ct))
        {
            throw new ProblemException(502, VenueFailure);
        }
    }

    private const string VenueFailure = "The venue candle read failed. Try again later.";
}
