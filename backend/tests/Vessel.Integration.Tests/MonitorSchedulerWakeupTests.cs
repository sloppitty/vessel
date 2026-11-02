using Vessel.Worker;

namespace Vessel.Tests;

/// <summary>
/// The scheduler loop keeps one wake waiter across timer ticks: timer-only ticks never accumulate channel waiters,
/// while startup, coalesced wakes, a wake during a pass and shutdown keep their behaviour.
/// </summary>
public sealed class MonitorSchedulerWakeupTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Timer_only_ticks_keep_one_outstanding_waiter_and_a_wake_causes_exactly_one_early_pass()
    {
        await using var loop = new Loop();
        await loop.NextPass(); // startup pass
        for (var i = 0; i < 500; i++)
        {
            loop.Time.Fire();
            await loop.NextPass();
        }
        await loop.WaitForWaiters(1);
        Assert.Equal(1, loop.Wakeup.PendingWaiters);
        Assert.Equal(501, loop.Passes);

        loop.Wakeup.Wake();
        await loop.NextPass();
        await loop.AssertNoFurtherPass();
        Assert.Equal(502, loop.Passes);
        await loop.WaitForWaiters(1);
    }

    [Fact]
    public async Task Wakes_during_a_pass_coalesce_into_one_more_pass()
    {
        await using var loop = new Loop(blocking: true);
        await loop.NextPass(); // startup pass, blocked
        loop.Wakeup.Wake();
        loop.Wakeup.Wake();
        loop.Wakeup.Wake();
        loop.Release();
        await loop.NextPass();
        loop.Release();
        await loop.AssertNoFurtherPass();
        Assert.Equal(2, loop.Passes);
        await loop.WaitForWaiters(1);
    }

    [Fact]
    public async Task A_simultaneous_tick_and_wake_cause_one_more_pass()
    {
        await using var loop = new Loop(blocking: true);
        await loop.NextPass(); // startup pass, blocked: the tick and the wake both arrive during it
        loop.Time.Fire();
        loop.Wakeup.Wake();
        loop.Release();
        await loop.NextPass();
        loop.Release();
        await loop.AssertNoFurtherPass();
        Assert.Equal(2, loop.Passes);
        Assert.Equal(1, loop.Wakeup.PendingWaiters);
    }

    [Fact]
    public async Task A_wake_waiting_across_ticks_is_not_lost()
    {
        await using var loop = new Loop();
        await loop.NextPass();
        for (var i = 0; i < 3; i++)
        {
            loop.Time.Fire();
            await loop.NextPass();
        }
        await loop.WaitForWaiters(1);
        await loop.NextPassAfter(loop.Wakeup.Wake);
        await loop.AssertNoFurtherPass();
        Assert.Equal(5, loop.Passes);
    }

    [Fact]
    public async Task Shutdown_ends_the_loop_cleanly_and_releases_the_waiter()
    {
        var loop = new Loop();
        await loop.NextPass();
        loop.Time.Fire();
        await loop.NextPass();
        await loop.WaitForWaiters(1);
        await loop.DisposeAsync();
        Assert.True(loop.Run.IsCompletedSuccessfully);
        Assert.Equal(0, loop.Wakeup.PendingWaiters);
    }

    [Fact]
    public async Task A_failed_pass_is_reported_and_the_loop_continues()
    {
        var failures = 0;
        await using var loop = new Loop(fail: true, failed: _ => Interlocked.Increment(ref failures));
        await loop.NextPass();
        loop.Time.Fire();
        await loop.NextPass();
        Assert.True(Volatile.Read(ref failures) >= 1);
    }

    private sealed class Loop : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly SemaphoreSlim passed = new(0);
        private readonly SemaphoreSlim gate = new(0);
        private readonly bool blocking;
        private int passes;

        public Loop(bool blocking = false, bool fail = false, Action<Exception>? failed = null)
        {
            this.blocking = blocking;
            Run = Task.Run(() => MonitorSchedulerLoop.RunAsync(Time, TimeSpan.FromSeconds(30), Wakeup, async ct =>
            {
                Interlocked.Increment(ref passes);
                passed.Release();
                if (this.blocking) await gate.WaitAsync(ct);
                if (fail) throw new InvalidOperationException("synthetic");
            }, failed ?? (_ => { }), stop.Token));
        }

        public ManualTimeProvider Time { get; } = new();
        public MonitorWakeup Wakeup { get; } = new();
        public Task Run { get; }
        public int Passes => Volatile.Read(ref passes);

        public async Task NextPass() => Assert.True(await passed.WaitAsync(Wait), "Expected a scheduling pass.");

        public async Task NextPassAfter(Action action)
        {
            action();
            await NextPass();
        }

        public void Release() => gate.Release();

        public async Task AssertNoFurtherPass() =>
            Assert.False(await passed.WaitAsync(TimeSpan.FromMilliseconds(300)), "Unexpected extra scheduling pass.");

        public async Task WaitForWaiters(int expected)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (Wakeup.PendingWaiters != expected && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.Equal(expected, Wakeup.PendingWaiters);
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            gate.Release(100);
            await Run.WaitAsync(Wait);
            var deadline = DateTime.UtcNow + Wait;
            while (Wakeup.PendingWaiters != 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
            stop.Dispose();
        }
    }

    /// <summary>A clock whose timers fire only when the test says so.</summary>
    internal sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (timers) timers.Add(timer);
            return timer;
        }

        public void Fire()
        {
            ManualTimer[] current;
            lock (timers) current = [.. timers];
            foreach (var timer in current) timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private volatile bool disposed;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }
        }
    }
}
