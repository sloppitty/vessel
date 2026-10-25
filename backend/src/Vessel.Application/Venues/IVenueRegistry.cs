using Vessel.Application.MarketData;

namespace Vessel.Application.Venues;

/// <summary>
/// Finds the descriptor and adapters for a venue ID. A missing adapter means the venue does not offer that capability
/// (or is not available); callers turn that into their own safe error.
/// </summary>
public interface IVenueRegistry
{
    IReadOnlyList<VenueDescriptor> Descriptors { get; }
    VenueDescriptor? Descriptor(string venueId);
    IPerpetualVenueReader? Reader(string venueId);
    IVenueFundingReader? Funding(string venueId) => Reader(venueId) as IVenueFundingReader;
    /// <summary>Phase 2, item 6b: time-window execution pages for an owner-started backfill, where the venue offers them.</summary>
    IVenueFillHistoryReader? FillHistory(string venueId) => Descriptor(venueId)?.Capabilities.FillHistory == true
        ? Reader(venueId) as IVenueFillHistoryReader : null;
    IVenueOrderReader? Orders(string venueId);
    ICandleReader? Candles(string venueId);
    IMarketContextReader? MarketContext(string venueId);
    IMarketStream? Stream(string venueId);
    IVenueAccountDiscovery? Discovery(string venueId);
    IVenueCredentialVerifier? CredentialVerifier(string venueId);
}

/// <summary>
/// Registry over the registered descriptors and adapters. When a venue has several registrations of one port the
/// last one wins, matching how a single injected service resolves.
/// </summary>
public sealed class VenueRegistry(
    IEnumerable<VenueDescriptor> descriptors, IEnumerable<IPerpetualVenueReader> readers,
    IEnumerable<IVenueOrderReader> orders, IEnumerable<ICandleReader> candles,
    IEnumerable<IMarketContextReader> contexts, IEnumerable<IMarketStream> streams,
    IEnumerable<IVenueAccountDiscovery> discoveries, IEnumerable<IVenueCredentialVerifier> verifiers) : IVenueRegistry
{
    private readonly IReadOnlyList<VenueDescriptor> descriptors = Validate(descriptors.GroupBy(d => d.Id).Select(g => g.Last()).ToList());
    private readonly Dictionary<string, IPerpetualVenueReader> readers = Last(readers, r => r.VenueId);
    private readonly Dictionary<string, IVenueOrderReader> orders = Last(orders, r => r.VenueId);
    private readonly Dictionary<string, ICandleReader> candles = Last(candles, r => r.VenueId);
    private readonly Dictionary<string, IMarketContextReader> contexts = Last(contexts, r => r.VenueId);
    private readonly Dictionary<string, IMarketStream> streams = Last(streams, r => r.VenueId);
    private readonly Dictionary<string, IVenueAccountDiscovery> discoveries = Last(discoveries, r => r.VenueId);
    private readonly Dictionary<string, IVenueCredentialVerifier> verifiers = Last(verifiers, r => r.VenueId);

    public IReadOnlyList<VenueDescriptor> Descriptors => descriptors;
    public VenueDescriptor? Descriptor(string venueId) => descriptors.FirstOrDefault(d => d.Id == venueId);
    public IPerpetualVenueReader? Reader(string venueId) => readers.GetValueOrDefault(venueId);
    public IVenueOrderReader? Orders(string venueId) => orders.GetValueOrDefault(venueId);
    public ICandleReader? Candles(string venueId) => candles.GetValueOrDefault(venueId);
    public IMarketContextReader? MarketContext(string venueId) => contexts.GetValueOrDefault(venueId);
    public IMarketStream? Stream(string venueId) => streams.GetValueOrDefault(venueId);
    public IVenueAccountDiscovery? Discovery(string venueId) => discoveries.GetValueOrDefault(venueId);
    public IVenueCredentialVerifier? CredentialVerifier(string venueId) => verifiers.GetValueOrDefault(venueId);

    private static List<VenueDescriptor> Validate(List<VenueDescriptor> descriptors) =>
        descriptors.FirstOrDefault(d => !VenueLogos.IsValid(d.Logo)) is { } invalid
            ? throw new InvalidOperationException($"Venue '{invalid.Id}' registers an unknown logo key.")
            : descriptors;

    private static Dictionary<string, T> Last<T>(IEnumerable<T> items, Func<T, string> venue) =>
        items.GroupBy(venue, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
}
