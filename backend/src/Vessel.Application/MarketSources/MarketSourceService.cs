using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.MarketSources;

namespace Vessel.Application.MarketSources;

/// <summary>One market source of a venue, its selectable markets and whether the owner's selector offers it.</summary>
public sealed record MarketSourceDto(string Id, string Name, int Markets, bool Enabled);
/// <summary>A venue's sources. <paramref name="Available"/> is false when its catalogue could not be read just now.</summary>
public sealed record VenueMarketSourcesDto(string VenueId, string VenueName, bool Available, IReadOnlyList<MarketSourceDto> Sources);
public sealed record UpdateMarketSourceRequest([property: JsonRequired] bool Enabled);

public interface IMarketSourceStore
{
    Task<IReadOnlyList<DisabledMarketSource>> DisabledAsync(CancellationToken ct);
    Task SetEnabledAsync(Guid ownerId, string venueId, string sourceId, bool enabled, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// The owner's on/off choice per market source, for venues whose descriptor has <see cref="VenueCapabilities.MarketSources"/>.
/// It only filters the selector: the catalogue still returns the hidden markets, so a saved Play keeps its leverage and tick.
/// </summary>
public sealed partial class MarketSourceService(IMarketSourceStore store, IVenueRegistry venues, IJournalOwnerContext owner, TimeProvider time, IUnitOfWork unitOfWork)
{
    public async Task<IReadOnlyList<VenueMarketSourcesDto>> ListAsync(CancellationToken ct)
    {
        var disabled = await store.DisabledAsync(ct);
        var result = new List<VenueMarketSourcesDto>();
        foreach (var venue in venues.Descriptors.Where(v => v.Capabilities.MarketSources))
        {
            IReadOnlyList<VenueInstrument> instruments;
            try { instruments = await (venues.Reader(venue.Id) ?? throw new VenueReadException("No reader.")).ReadInstrumentsAsync(ct); }
            catch (Exception ex) when (ex is VenueReadException or HttpRequestException ||
                ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                result.Add(new(venue.Id, venue.Name, false, []));
                continue;
            }
            var off = disabled.Where(d => d.VenueId == venue.Id).Select(d => d.SourceId).ToHashSet(StringComparer.Ordinal);
            var sources = instruments.Where(i => i.Source is not null).GroupBy(i => i.Source!, StringComparer.Ordinal)
                .Select(g => new MarketSourceDto(g.Key, g.First().SourceName ?? g.Key, g.Count(), !off.Contains(g.Key)))
                .OrderByDescending(s => s.Markets).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
            result.Add(new(venue.Id, venue.Name, true, sources));
        }
        return result;
    }

    public async Task SetAsync(string venueId, string sourceId, UpdateMarketSourceRequest request, CancellationToken ct)
    {
        if (venues.Descriptor(venueId)?.Capabilities.MarketSources != true)
            throw new ProblemException(404, "This venue has no market sources.");
        if (!SourcePattern().IsMatch(sourceId))
            throw new ProblemException(400, "Market source must be 1 to 16 lowercase letters or digits.");
        var now = time.GetUtcNow();
        await unitOfWork.ExecuteAsync(async () =>
        {
            await store.SetEnabledAsync(owner.OwnerId, venueId, sourceId, request.Enabled, now, ct);
            return true;
        }, ct);
    }

    /// <summary>The catalogue with the owner's hidden sources named; the browser leaves them out of the selector.</summary>
    public async Task<AccountInstrumentsDto> ApplyAsync(AccountInstrumentsDto catalogue, CancellationToken ct) =>
        venues.Descriptor(catalogue.VenueId)?.Capabilities.MarketSources != true ? catalogue : catalogue with
        {
            DisabledSources = (await store.DisabledAsync(ct)).Where(d => d.VenueId == catalogue.VenueId).Select(d => d.SourceId).ToList()
        };

    [GeneratedRegex("^[a-z0-9]{1,16}$")]
    private static partial Regex SourcePattern();
}
