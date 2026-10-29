using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Infrastructure.Venues.Risex;

namespace Vessel.Tests;

/// <summary>
/// Phase 0 item 0.4c, coverage and evidence (review defects D-09, D-18 and the chart part of D-08): the monitor records each
/// Open Play's stop coverage (known, shared, unknown or unsupported) with its check time, reason and per-level evidence.
/// Structural unknowns never become missing and never degrade the run; a venue whose order reader does not return the
/// current book is labelled "current book not read" and its book-dependent checks do not run.
/// </summary>
public sealed class MonitorCoverageVenuesTests
{
    [Fact]
    public void Venue_descriptors_say_whether_the_order_reader_returns_the_current_book()
    {
        Assert.True(HyperliquidPerpetualReader.Descriptor.Capabilities.CurrentOrderBook);
        Assert.True(LighterExchange.Mainnet.Descriptor.Capabilities.CurrentOrderBook);
        Assert.True(LighterExchange.Robinhood.Descriptor.Capabilities.CurrentOrderBook);
        // RISEx reads bounded order history and TP/SL records, not /v1/orders/open (owner decision: label it).
        Assert.False(RisexReader.Descriptor.Capabilities.CurrentOrderBook);
        Assert.False(VenueDescriptor.Manual.Capabilities.CurrentOrderBook);
        Assert.False(VenueCapabilities.None.CurrentOrderBook);
    }
}
