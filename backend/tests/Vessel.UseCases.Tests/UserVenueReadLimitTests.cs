using Vessel.Application.MarketData;
using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public sealed class UserVenueReadLimitTests : UserVenueReadLimitTestsSupport
{

    [Fact]
    public void Account_and_candle_windows_do_not_leak_between_users_and_expire_after_one_minute()
    {
        var clock = new ManualTime(SecurityFixture.Now);
        var limits = new UserReadLimits(clock);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        for (var index = 0; index < 10; index++) limits.Admit(first, "account-discovery", 10);
        Assert.Equal(60, Assert.Throws<Vessel.Application.Common.ProblemException>(() => limits.Admit(first, "account-discovery", 10)).RetryAfterSeconds);
        limits.Admit(second, "account-discovery", 10);
        limits.Admit(first, "hyperliquid|BTC|1h", 1);
        limits.Admit(second, "hyperliquid|BTC|1h", 1);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, Assert.Throws<Vessel.Application.Common.ProblemException>(() => limits.Admit(first, "hyperliquid|BTC|1h", 1)).RetryAfterSeconds);
        clock.Advance(TimeSpan.FromSeconds(1));
        limits.Admit(first, "account-discovery", 10);
        limits.Admit(first, "hyperliquid|BTC|1h", 1);
    }

    [Fact]
    public async Task Unlisted_candle_refreshes_do_not_spend_keys_or_block_another_users_first_refresh()
    {
        var clock = new ManualTime(SecurityFixture.Now);
        var cache = new CandleCache(clock);
        var registry = TestVenues.With(new Candles());
        CandleService Service(Guid owner, out Guid id)
        {
            var store = new MemoryWorkspaceStore(owner);
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic market");
            store.Accounts.Add(account); id = account.Id;
            return new(store, registry, cache, clock, new CoreOwner(owner));
        }
        var first = Service(Guid.NewGuid(), out var firstId);
        for (var index = 0; index < 8192; index++)
            Assert.Equal(409, (await Assert.ThrowsAsync<Vessel.Application.Common.ProblemException>(() =>
                first.CandlesAsync(firstId, "UNLISTED" + index, "1h", null, default, "true"))).StatusCode);
        var second = Service(Guid.NewGuid(), out var secondId);
        Assert.Equal("BTC", (await second.CandlesAsync(secondId, "BTC", "1h", null, default, "true")).Instrument);
        // Even the offending user retains their entire valid-series budget.
        Assert.Equal("BTC", (await first.CandlesAsync(firstId, "BTC", "1h", null, default, "true")).Instrument);
    }

    [Fact]
    public void Key_cap_is_per_user_and_expired_keys_release_that_users_capacity()
    {
        var clock = new ManualTime(SecurityFixture.Now);
        var limits = new UserReadLimits(clock);
        var owner = Guid.NewGuid();
        for (var index = 0; index < 256; index++) limits.Admit(owner, "series-" + index, 1);
        Assert.Equal(429, Assert.Throws<Vessel.Application.Common.ProblemException>(() => limits.Admit(owner, "overflow", 1)).StatusCode);
        limits.Admit(Guid.NewGuid(), "first-series", 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        for (var index = 0; index < 256; index++) limits.Admit(owner, "new-series-" + index, 1);
        Assert.Throws<Vessel.Application.Common.ProblemException>(() => limits.Admit(owner, "overflow", 1));
    }

    [Fact]
    public void Full_key_store_admits_a_smaller_user_without_resetting_evicted_users_live_budgets()
    {
        var clock = new ManualTime(SecurityFixture.Now);
        var limits = new UserReadLimits(clock);
        var owners = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var owner in owners)
            for (var index = 0; index < 256; index++) limits.Admit(owner, "series-" + index, 1);
        var newcomer = Guid.NewGuid();
        limits.Admit(newcomer, "first-series", 1);
        foreach (var owner in owners)
            Assert.Throws<Vessel.Application.Common.ProblemException>(() => limits.Admit(owner, "bypass", 1));
        Assert.Throws<Vessel.Application.Common.ProblemException>(() => limits.Admit(newcomer, "first-series", 1));
        clock.Advance(TimeSpan.FromMinutes(1));
        foreach (var owner in owners) limits.Admit(owner, "after-expiry", 1);
    }

    [Fact]
    public void Global_backstop_reclaims_expired_keys_before_penalizing_a_live_user()
    {
        var clock = new ManualTime(SecurityFixture.Now);
        var limits = new UserReadLimits(clock);
        var expired = Guid.NewGuid();
        for (var index = 0; index < 256; index++) limits.Admit(expired, "old-" + index, 1);
        clock.Advance(TimeSpan.FromSeconds(30));
        var live = Enumerable.Range(0, 31).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var owner in live)
            for (var index = 0; index < 256; index++) limits.Admit(owner, "live-" + index, 2);
        clock.Advance(TimeSpan.FromSeconds(30));
        limits.Admit(Guid.NewGuid(), "first-series", 1);
        foreach (var owner in live) limits.Admit(owner, "live-0", 2);
    }
}
