using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Domain.Common;

namespace Vessel.Tests;

public sealed class ActivitySummaryCacheTests
{
    private static readonly ActivitySummaries Value = new([], [], new([], [], [], new Dictionary<Guid, Guid>()));
    private static ActivitySummaryKey Key(Guid? owner = null, long revision = 1, string accounts = "*") => new(owner ?? Guid.NewGuid(), revision, null, accounts, TradingMode.Real);
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
    public async Task A_rebase_that_walks_the_history_again_is_measured_again_and_leaves_when_it_outgrows_the_cache()
    {
        var cache = new ActivitySummaryCache(capacity: 16 * 1024); var key = Key(); var calls = 0;
        var small = Value with { Facts = Value.Facts with { Revision = 1 } };
        Task<ActivitySummaries> Compute(CancellationToken _) { calls++; return Task.FromResult(small); }
        var cached = await cache.ReadAsync(key, Compute, Unused, default);
        Assert.Same(cached, await cache.ReadAsync(key, Compute, Unused, default));
        Assert.Equal(1, calls);

        // Newer observations sharing the operations keep the entry and its charge.
        var shared = cached with { Facts = cached.Facts with { Revision = 2 } };
        cache.Replace(key, cached, shared);
        Assert.Same(shared, await cache.ReadAsync(key, Compute, Unused, default));

        // A rebuilt aggregate far larger than the cache leaves it, so it is never kept over capacity.
        var rebuilt = new ActivitySummaries(new List<ActivityOperationDto>(), [], shared.Facts with
        {
            Revision = 3,
            LeverageObservations = Enumerable.Range(0, 200).Select(_ => new LeverageObservation
            {
                Id = Guid.NewGuid(), OwnerId = key.OwnerId, AccountId = Guid.NewGuid(), ContractId = "BTC",
                ObservedAtUtc = DateTimeOffset.UnixEpoch, SignedQuantity = 1, EntryPrice = 100, Leverage = 5
            }).ToList()
        });
        cache.Replace(key, shared, rebuilt);
        Assert.Same(small, await cache.ReadAsync(key, Compute, Unused, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_grown_replacement_at_the_lru_tail_evicts_the_others_to_capacity()
    {
        static ActivitySummaries Sized(long revision, int rows) => Value with
        {
            Facts = Value.Facts with
            {
                Revision = revision,
                LeverageObservations = Enumerable.Range(0, rows).Select(_ => new LeverageObservation
                {
                    Id = Guid.NewGuid(), OwnerId = Guid.Empty, AccountId = Guid.NewGuid(), ContractId = "BTC",
                    ObservedAtUtc = DateTimeOffset.UnixEpoch, SignedQuantity = 1, EntryPrice = 100, Leverage = 5
                }).ToList()
            }
        };
        var oldest = Key(); var newer = Key(); var calls = 0;
        var tail = Sized(1, 1); var other = Sized(1, 1);
        var grown = new ActivitySummaries(new List<ActivityOperationDto>(), [], Sized(2, 60).Facts);
        var charge = typeof(ActivitySummaryCache).GetMethod("Charge",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        long ChargeOf(ActivitySummaryKey key, ActivitySummaries value) => (long)charge.Invoke(null, [key, value])!;
        // Room for the grown entry alone, or for both small ones, but not for the grown one beside another.
        var cache = new ActivitySummaryCache(capacity: ChargeOf(oldest, grown) + ChargeOf(newer, other) - 1);
        Task<ActivitySummaries> Of(ActivitySummaries value) { calls++; return Task.FromResult(value); }
        await cache.ReadAsync(oldest, _ => Of(tail), Unused, default);
        await cache.ReadAsync(newer, _ => Of(other), Unused, default);
        Assert.Equal(2, calls);

        // The tail entry grows to fit the cache only alone: the other entry leaves, the grown one stays.
        cache.Replace(oldest, tail, grown);
        Assert.Same(grown, await cache.ReadAsync(oldest, _ => Of(tail), Unused, default));
        await cache.ReadAsync(newer, _ => Of(other), Unused, default);
        Assert.Equal(3, calls);
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
