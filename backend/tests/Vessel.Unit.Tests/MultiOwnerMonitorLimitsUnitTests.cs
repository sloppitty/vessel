using Vessel.Application.Monitoring;

namespace Vessel.Tests;

public sealed class MultiOwnerMonitorLimitsUnitTests
{

    [Theory]
    [InlineData(0, 4)]
    [InlineData(65, 4)]
    [InlineData(2, 0)]
    [InlineData(2, 65)]
    public void Both_concurrency_settings_are_bounded(int workers, int venueReads) =>
        Assert.Throws<InvalidOperationException>(() => new MonitorOptions { WorkerConcurrency = workers, VenueReadConcurrency = venueReads }.Validate());
}
