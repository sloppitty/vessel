using Vessel.Application.MarketData;

namespace Vessel.Tests;

public sealed class MarketContextApiUseCasesTests : MarketContextApiTestsSupport
{

    [Fact]
    public async Task Cache_expires_after_ten_seconds()
    {
        var (_, store, account) = Setup(); var reader = new Contexts
        {
            Result = [new("BTC", "1", "1", null, "1", "1", "1", "0", null)]
        };
        var time = new FixedTime(DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000L));
        var service = new MarketContextService(store, TestVenues.With(reader), new MarketContextCache(time));
        await service.ContextAsync(account.Id, "BTC", default);
        time.Now = time.Now.AddSeconds(9);
        await service.ContextAsync(account.Id, "BTC", default);
        Assert.Equal(1, reader.Reads);
        time.Now = time.Now.AddSeconds(2);
        await service.ContextAsync(account.Id, "BTC", default);
        Assert.Equal(2, reader.Reads);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
