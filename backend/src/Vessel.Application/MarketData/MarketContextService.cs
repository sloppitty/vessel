using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;

namespace Vessel.Application.MarketData;

// Read-only public venue statistics. Values keep the venue's decimal strings; nothing is derived.
public interface IMarketContextReader
{
    string VenueId { get; }
    Task<IReadOnlyList<VenueMarketContext>> ReadMarketContextsAsync(CancellationToken cancellationToken);
}

public sealed record VenueMarketContext(
    string ContractId, string MarkPrice, string OraclePrice, string? MidPrice, string? PreviousDayPrice,
    string DayNotionalVolume, string OpenInterest, string FundingRate, string? Premium);

public sealed record MarketContextDto(string VenueId, string Instrument, string MarkPrice, string OraclePrice,
    string? MidPrice, string? PreviousDayPrice, string DayNotionalVolume, string OpenInterest, string FundingRate,
    string? Premium, DateTimeOffset ObservedAt, string Notice);

/// <summary>Bounded short-lived cache of one whole upstream snapshot per venue. Register as a singleton.</summary>
public sealed class MarketContextCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);
    public sealed record Snapshot(DateTimeOffset Observed, IReadOnlyList<VenueMarketContext> Value);
    private readonly BoundedMarketDataCache<Snapshot> entries = new(time, 16, 16, _ => 1);

    public Task<Snapshot> GetAsync(string venueId, IMarketContextReader reader, CancellationToken ct) =>
        entries.GetAsync(venueId, async token =>
        {
            var value = await reader.ReadMarketContextsAsync(token);
            return new Snapshot(time.GetUtcNow(), value);
        }, _ => Lifetime, ct);
}

public sealed class MarketContextService(IWorkspaceStore store, IVenueRegistry venues, MarketContextCache cache)
{
    /// <summary>Used when the venue's descriptor has no notice of its own.</summary>
    public const string Notice =
        "Venue market context. Open interest is in base units. Not a fill or valuation.";
    private const string VenueFailure = "The venue market read failed. Try again later.";

    public async Task<MarketContextDto> ContextAsync(Guid accountId, string? instrument, CancellationToken ct)
    {
        var account = await MarketDataGuard.AccountAsync(store, accountId, ct);
        instrument = MarketDataGuard.Instrument(instrument);
        var notice = venues.Descriptor(account.VenueId)?.MarketContextNotice ?? Notice;
        if (venues.MarketContext(account.VenueId) is not { } reader)
            throw new ProblemException(502, VenueFailure);

        MarketContextCache.Snapshot snapshot;
        try
        {
            snapshot = await cache.GetAsync(account.VenueId, reader, ct);
        }
        catch (Exception ex) when (VenueFailures.IsReadFailure(ex, ct))
        {
            throw new ProblemException(502, VenueFailure);
        }

        var match = snapshot.Value.FirstOrDefault(c => c.ContractId == instrument)
            ?? throw new ProblemException(400, "The instrument is not in the venue's perpetual catalogue.");
        return new MarketContextDto(account.VenueId, instrument, match.MarkPrice, match.OraclePrice, match.MidPrice,
            match.PreviousDayPrice, match.DayNotionalVolume, match.OpenInterest, match.FundingRate, match.Premium,
            snapshot.Observed, notice);
    }
}
