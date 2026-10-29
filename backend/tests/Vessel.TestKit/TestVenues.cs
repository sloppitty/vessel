using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

/// <summary>Builds a venue registry around fake adapters, with the manual and Hyperliquid descriptors.</summary>
internal static class TestVenues
{
    public static VenueRegistry With(params object?[] adapters)
    {
        var all = adapters.OfType<object>().ToList();
        if (all.OfType<ICandleReader>().Any() && !all.OfType<IPerpetualVenueReader>().Any()) all.Add(new FixtureReader());
        return new([VenueDescriptor.Manual, HyperliquidPerpetualReader.Descriptor],
            all.OfType<IPerpetualVenueReader>(), all.OfType<IVenueOrderReader>(), all.OfType<ICandleReader>(),
            all.OfType<IMarketContextReader>(), all.OfType<IMarketStream>(),
            all.OfType<IVenueAccountDiscovery>(), all.OfType<IVenueCredentialVerifier>());
    }
}
