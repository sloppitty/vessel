using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class ExecutionMatcherVenuesTests
{

    [Theory]
    [InlineData("open", VenueOrderStatus.Open)]
    [InlineData("filled", VenueOrderStatus.Filled)]
    [InlineData("canceled", VenueOrderStatus.Canceled)]
    [InlineData("marginCanceled", VenueOrderStatus.Canceled)]
    [InlineData("badAloPxRejected", VenueOrderStatus.Rejected)]
    [InlineData("triggered", VenueOrderStatus.Triggered)]
    [InlineData("somethingNew", VenueOrderStatus.Other)]
    public void Normalizes_hyperliquid_order_statuses(string status, VenueOrderStatus expected) =>
        Assert.Equal(expected, HyperliquidPerpetualReader.Normalize(status));
}
