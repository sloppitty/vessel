using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public partial class VenueRegistryTestsSupport
{
    /// <summary>A second venue: its own name, source rule, capabilities and reader, never mentioned by a use case.</summary>
    internal sealed class OtherReader : IPerpetualVenueReader
    {
        public string VenueId => "other";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueInstrument>>([new("ABC", 3, 10, "USDC")]);
        public Task<PerpetualVenueReadResult> ReadAsync(string address, CancellationToken ct) => throw new NotSupportedException();
    }

    internal static readonly VenueDescriptor Other = new("other", "Other", "read-only", VenueSources.EvmAddress,
        new VenueCapabilities(Sync: true, Instruments: true, Orders: false, Candles: false, MarketContext: false, Stream: false, StablecoinWallet: false), "USDC");

    internal static VenueRegistry Registry(params object[] adapters) => new(
        [VenueDescriptor.Manual, HyperliquidPerpetualReader.Descriptor, Other],
        adapters.OfType<IPerpetualVenueReader>(), [], [], [], [], [], []);
}
