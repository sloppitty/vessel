
namespace Vessel.Tests;

/// <summary>Deterministic clock: timers fire only when the test advances time.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private readonly object sync = new();
    private readonly List<ManualTimer> timers = [];
    private readonly DateTimeOffset origin = start;
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() { lock (sync) return now; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => (GetUtcNow() - origin).Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public int TimerCount { get { lock (sync) return timers.Count; } }

    public void Advance(TimeSpan delta)
    {
        DateTimeOffset target;
        lock (sync) target = now + delta;
        while (true)
        {
            ManualTimer? next;
            lock (sync)
            {
                next = timers.Where(t => t.Due <= target).MinBy(t => t.Due);
                if (next is null) { now = target; return; }
                now = next.Due;
                if (next.Period > TimeSpan.Zero) next.Due += next.Period;
                else timers.Remove(next);
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due;
        public TimeSpan Period;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.sync)
            {
                owner.timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = owner.now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                owner.timers.Add(this);
            }
            return true;
        }

        public void Dispose() { lock (owner.sync) owner.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
