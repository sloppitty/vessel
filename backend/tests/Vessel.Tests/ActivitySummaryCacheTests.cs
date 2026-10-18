using Vessel.Application.Activity;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class ActivitySummaryCacheTests
{
    private static readonly ActivitySummaries Value = new([], [], new([], [], [], new Dictionary<Guid, Guid>()));
    private static ActivitySummaryKey Key(Guid? owner = null, long revision = 1, string accounts = "*") => new(owner ?? Guid.NewGuid(), revision, null, accounts);
    private static ValueTask Unused() => ValueTask.CompletedTask;

    [Fact]
    public async Task Single_flight_survives_first_waiter_cancellation_and_caches_success()
    {
        var cache = new ActivitySummaryCache(); var key = Key(); var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource(); var calls = 0; var unused = 0;
        CancellationToken shared = default;
        async Task<ActivitySummaries> Compute(CancellationToken ct)
        {
            Interlocked.Increment(ref calls); shared = ct; started.SetResult();
            await finish.Task.WaitAsync(ct); return Value;
        }
        using var cancel = new CancellationTokenSource();
        var first = cache.ReadAsync(key, Compute, Unused, cancel.Token);
        await started.Task;
        var second = cache.ReadAsync(key, Compute, () => { unused++; return ValueTask.CompletedTask; }, default);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(shared.IsCancellationRequested); finish.SetResult();
        Assert.Same(Value, await second);
        Assert.Same(Value, await cache.ReadAsync(key, Compute, Unused, default));
        Assert.Equal((1, 1), (calls, unused));
    }

    [Fact]
    public async Task All_waiters_cancel_computation_and_new_call_does_not_join_or_cache_it()
    {
        var cache = new ActivitySummaryCache(); var key = Key();
        var cancelled = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();
        var abandoned = cache.ReadAsync(key, async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled.SetResult(); }
            return Value;
        }, Unused, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = Value with { Version = "replacement" };
        Assert.Same(replacement, await cache.ReadAsync(key, _ => Task.FromResult(replacement), Unused, default));
    }

    [Fact]
    public async Task Failed_flight_is_shared_but_not_cached()
    {
        var cache = new ActivitySummaryCache(); var key = Key(); var finish = new TaskCompletionSource(); var calls = 0;
        async Task<ActivitySummaries> Compute(CancellationToken ct)
        { calls++; await finish.Task; throw new InvalidOperationException("fixture"); }
        var first = cache.ReadAsync(key, Compute, Unused, default);
        var second = cache.ReadAsync(key, Compute, Unused, default);
        finish.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(1, calls);
        Assert.Same(Value, await cache.ReadAsync(key, _ => Task.FromResult(Value), Unused, default));
    }

    [Fact]
    public async Task Owner_revision_and_inputs_are_separate_and_LRU_is_bounded()
    {
        var cache = new ActivitySummaryCache(entryLimit: 2); var owner = Guid.NewGuid();
        var first = Key(owner); var other = Key(); var calls = 0;
        Task<ActivitySummaries> Compute(CancellationToken _) { calls++; return Task.FromResult(Value); }
        await cache.ReadAsync(first, Compute, Unused, default);
        await cache.ReadAsync(other, Compute, Unused, default);
        await cache.ReadAsync(first, Compute, Unused, default); // Touch first; other is evicted next.
        await cache.ReadAsync(first with { Revision = 2 }, Compute, Unused, default);
        await cache.ReadAsync(other, Compute, Unused, default);
        await cache.ReadAsync(first with { Accounts = "" }, Compute, Unused, default);
        await cache.ReadAsync(first with { AccountId = Guid.NewGuid() }, Compute, Unused, default);
        Assert.Equal(6, calls);
        var tiny = new ActivitySummaryCache(capacity: 1);
        await tiny.ReadAsync(first, Compute, Unused, default);
        await tiny.ReadAsync(first, Compute, Unused, default);
        Assert.Equal(8, calls);
    }
}
