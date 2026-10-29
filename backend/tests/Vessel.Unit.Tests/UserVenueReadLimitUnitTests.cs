using Vessel.Application.MarketData;

namespace Vessel.Tests;

public sealed class UserVenueReadLimitUnitTests
{

    [Fact]
    public void Stream_budgets_are_per_user_and_process_and_idempotently_released()
    {
        var limiter = new MarketStreamLimiter();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var leases = Enumerable.Range(0, 4).Select(_ => limiter.TryAcquire(first)!).ToList();
        Assert.Null(limiter.TryAcquire(first));
        leases.AddRange(Enumerable.Range(0, 4).Select(_ => limiter.TryAcquire(second)!));
        Assert.Null(limiter.TryAcquire(Guid.NewGuid()));
        Assert.Equal(8, limiter.Active);
        leases[0].Dispose(); leases[0].Dispose();
        Assert.Equal(7, limiter.Active);
        leases.Add(limiter.TryAcquire(first)!);
        Assert.Equal(8, limiter.Active);
        foreach (var lease in leases) lease.Dispose();
        Assert.Equal(0, limiter.Active);
    }
}
