using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;
using Vessel.Domain.Workspace;
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
    public async Task Slow_throwing_cancellation_callback_never_holds_the_gate_or_breaks_replacement()
    {
        var cache = new ActivitySummaryCache(); var key = Key(); var hitKey = Key();
        await cache.ReadAsync(hitKey, _ => Task.FromResult(Value), Unused, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var abandoned = cache.ReadAsync(key, async ct =>
        {
            using var registration = ct.Register(() =>
            {
                try
                {
                    entered.SetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    throw new InvalidOperationException("fixture callback");
                }
                finally { callbackReturned.SetResult(); }
            });
            await finish.Task;
            return Value;
        }, Unused, cancel.Token);
        var cancellation = Task.Run(() => cancel.Cancel());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(callbackReturned.Task.IsCompleted);
            // A separate thread proves the monitor is free even while the synchronous callback blocks.
            await Task.Run(async () =>
            {
                Assert.Same(Value, await cache.ReadAsync(hitKey, _ => throw new Exception(), Unused, default));
                Assert.Same(Value, await cache.ReadAsync(key, _ => Task.FromResult(Value), Unused, default));
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(callbackReturned.Task.IsCompleted);
        }
        finally { release.Set(); finish.SetResult(); }
        await cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Same(Value, await cache.ReadAsync(key, _ => throw new Exception(), Unused, default));
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
    public async Task Persistence_registration_honours_the_configured_capacity_and_rejects_negative_limits()
    {
        static IConfiguration Config(string capacity) =>
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Vessel:Activity:CacheCapacityBytes"] = capacity }).Build();
        var services = new ServiceCollection();
        services.AddVesselPersistence(Config("0"));
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ActivitySummaryCache>();
        var calls = 0; var key = Key();
        Task<ActivitySummaries> Compute(CancellationToken _) { calls++; return Task.FromResult(Value); }
        await cache.ReadAsync(key, Compute, Unused, default); await cache.ReadAsync(key, Compute, Unused, default);
        Assert.Equal(2, calls);
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddVesselPersistence(Config("-1")));
    }

    [Fact]
    public async Task Leverage_observations_count_toward_the_retained_memory_budget()
    {
        var cache = new ActivitySummaryCache(capacity: 16 * 1024); var key = Key(); var calls = 0;
        var observed = Value with
        {
            Facts = Value.Facts with
            {
                LeverageObservations = Enumerable.Range(0, 20).Select(_ => new LeverageObservation
                {
                    Id = Guid.NewGuid(), OwnerId = key.OwnerId, AccountId = Guid.NewGuid(), ContractId = "BTC",
                    ObservedAtUtc = DateTimeOffset.UnixEpoch, SignedQuantity = 1, EntryPrice = 100, Leverage = 5
                }).ToList()
            }
        };
        Task<ActivitySummaries> Compute(CancellationToken _) { calls++; return Task.FromResult(observed); }
        await cache.ReadAsync(key, Compute, Unused, default);
        await cache.ReadAsync(key, Compute, Unused, default);
        Assert.Equal(2, calls);
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
