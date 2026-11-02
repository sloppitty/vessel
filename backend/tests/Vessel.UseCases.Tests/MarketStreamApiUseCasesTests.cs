using Vessel.Application.Common;
using Vessel.Application.MarketData;
using Vessel.Application.Workspace;

namespace Vessel.Tests;

public sealed class MarketStreamApiUseCasesTests : MarketStreamApiTestsSupport
{

    private static MarketStreamService Service(IWorkspaceStore store, IMarketStream stream, MarketStreamLimiter? limiter = null, TimeProvider? time = null) =>
        new(store, TestVenues.With(stream), limiter ?? new MarketStreamLimiter(), time ?? TimeProvider.System);

    // Service-level behaviour with a deterministic clock.

    [Fact]
    public async Task Service_checks_run_in_contract_order_before_capacity()
    {
        var (_, store, account) = Setup(disabled: true);
        var limiter = new MarketStreamLimiter();
        var leases = Enumerable.Range(0, 8).Select(_ => limiter.TryAcquire(Guid.NewGuid())!).ToList();
        var service = Service(store, new FakeMarketStream { VenueId = "other" }, limiter);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.OpenAsync(Guid.NewGuid(), null, null, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => service.OpenAsync(account.Id, null, null, default))).StatusCode);
        account.UpdateSettings(account.Name, null, true);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.OpenAsync(account.Id, null, null, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.OpenAsync(account.Id, "BTC", "7m", default))).StatusCode);
        Assert.Equal(502, (await Assert.ThrowsAsync<ProblemException>(() => service.OpenAsync(account.Id, "BTC", "1m", default))).StatusCode);
        service = Service(store, new FakeMarketStream(), limiter);
        Assert.Equal(429, (await Assert.ThrowsAsync<ProblemException>(() => service.OpenAsync(account.Id, "BTC", "1m", default))).StatusCode);
        leases.ForEach(lease => lease.Dispose());
    }

    [Fact]
    public async Task Service_allows_four_streams_per_user_and_frees_slots_on_dispose()
    {
        var (_, store, account) = Setup(); var stream = new FakeMarketStream(); var limiter = new MarketStreamLimiter();
        var service = Service(store, stream, limiter);
        var sessions = new List<MarketStreamSession>();
        for (var i = 0; i < 4; i++) sessions.Add(await service.OpenAsync(account.Id, "BTC", "1m", default));
        var busy = await Assert.ThrowsAsync<ProblemException>(() => service.OpenAsync(account.Id, "ETH", "1m", default));
        Assert.Equal(429, busy.StatusCode);
        Assert.Equal(4, stream.Subscriptions.Count);
        sessions[0].Dispose();
        sessions[0].Dispose();
        Assert.True(stream.Subscriptions.First().Disposed);
        sessions.Add(await service.OpenAsync(account.Id, "ETH", "1m", default));
        Assert.Equal(4, limiter.Active);
        sessions.ForEach(session => session.Dispose());
        Assert.Equal(0, limiter.Active);
    }

    [Fact]
    public async Task Session_sends_keepalives_when_quiet_and_ends_after_one_hour()
    {
        var (_, store, account) = Setup(); var stream = new FakeMarketStream();
        var time = new ManualTime(DateTimeOffset.FromUnixTimeMilliseconds(1_790_927_520_000));
        using var session = await Service(store, stream, time: time).OpenAsync(account.Id, "BTC", "1m", default);
        await using var messages = session.ReadAllAsync(default).GetAsyncEnumerator();

        var pending = messages.MoveNextAsync().AsTask();
        await Eventually.True(() => time.TimerCount >= 2, "lifetime and keepalive timers armed");
        time.Advance(TimeSpan.FromSeconds(14));
        await Task.Delay(30);
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(MarketStreamMessage.Keepalive, messages.Current);

        stream.Latest.Write(new MarketStatusEvent(MarketStreamState.Stale, time.GetUtcNow()));
        var status = await Eventually.Next(messages);
        Assert.Equal("status", status.Event);
        Assert.Equal(new MarketStreamStatusDto("stale", time.GetUtcNow()), status.Data);

        pending = messages.MoveNextAsync().AsTask();
        await Eventually.True(() => time.TimerCount >= 2, "keepalive re-armed");
        time.Advance(TimeSpan.FromHours(1));
        // Keepalives may fire on the way; the lifetime ends the stream.
        var ended = false;
        while (!ended)
        {
            if (!await pending.WaitAsync(TimeSpan.FromSeconds(10))) ended = true;
            else
            {
                Assert.Same(MarketStreamMessage.Keepalive, messages.Current);
                pending = messages.MoveNextAsync().AsTask();
            }
        }
    }

    [Fact]
    public async Task Session_ends_quietly_when_the_client_cancels()
    {
        var (_, store, account) = Setup(); var stream = new FakeMarketStream();
        using var session = await Service(store, stream).OpenAsync(account.Id, "BTC", "1m", default);
        using var cancel = new CancellationTokenSource();
        await using var messages = session.ReadAllAsync(cancel.Token).GetAsyncEnumerator();
        var pending = messages.MoveNextAsync().AsTask();
        cancel.Cancel();
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
