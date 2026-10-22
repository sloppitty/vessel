namespace Vessel.Application.Monitoring;

public sealed class MonitorOptions
{
    public const string Section = "Vessel:Monitor";
    public int WorkerConcurrency { get; set; } = 2;
    public int VenueReadConcurrency { get; set; } = 4;
    /// <summary>
    /// Venue read slots kept free for protection checks when an account is read only because it keeps its execution
    /// history (Phase 2, item 6); such a read also leaves one of the owner's two slots free. With
    /// <see cref="VenueReadConcurrency"/> at or below it, those accounts are read only by Check or Refresh.
    /// </summary>
    public int ProtectionHeadroom { get; set; } = 1;
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ProtectionGrace { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Gates manual Check and Refresh only, never a scheduled claim. At most <see cref="Interval"/>.</summary>
    public TimeSpan ManualCooldown { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public string HeartbeatFile { get; set; } = "/tmp/vessel-worker-heartbeat";
    /// <summary>How long a terminal Play's linked order (exit or entry) is watched before it ends as <c>unverifiable</c>.</summary>
    public TimeSpan WatchLifetime { get; set; } = TimeSpan.FromDays(7);
    /// <summary>How long a discrepancy subject may stay unobservable before its record becomes <c>unverifiable</c>.</summary>
    public TimeSpan UnobservableLifetime { get; set; } = TimeSpan.FromDays(7);
    public static readonly TimeSpan SchedulerTick = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(1);

    public void Validate()
    {
        if (WorkerConcurrency is < 1 or > 64 || VenueReadConcurrency is < 1 or > 64)
            throw new InvalidOperationException("Monitor concurrency must be between 1 and 64.");
        if (ProtectionHeadroom is < 0 or > 64)
            throw new InvalidOperationException("Vessel:Monitor:ProtectionHeadroom must be between 0 and 64.");
        if (Interval < TimeSpan.FromMinutes(1) || Interval > TimeSpan.FromMinutes(60))
            throw new InvalidOperationException("Vessel:Monitor:Interval must be between 1 and 60 minutes.");
        if (ManualCooldown < TimeSpan.Zero || ManualCooldown > Interval)
            throw new InvalidOperationException("Vessel:Monitor:ManualCooldown must be between zero and the interval.");
        if (ProtectionGrace < TimeSpan.Zero || ProtectionGrace > TimeSpan.FromHours(24) ||
            HeartbeatTimeout < SchedulerTick || HeartbeatTimeout > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("The monitor grace or heartbeat timeout is outside its supported range.");
        // Both lifetimes bound how long work without venue proof is kept before it ends as unverifiable.
        if (WatchLifetime < TimeSpan.FromHours(1) || WatchLifetime > TimeSpan.FromDays(90))
            throw new InvalidOperationException("Vessel:Monitor:WatchLifetime must be between 1 hour and 90 days.");
        if (UnobservableLifetime < TimeSpan.FromHours(1) || UnobservableLifetime > TimeSpan.FromDays(90))
            throw new InvalidOperationException("Vessel:Monitor:UnobservableLifetime must be between 1 hour and 90 days.");
    }

    public TimeSpan NextDelay(int failures, double jitter) => failures switch
    {
        1 => FirstRetry,
        2 => TimeSpan.FromMinutes(3),
        _ => TimeSpan.FromTicks((long)(Interval.Ticks * (0.9 + Math.Clamp(jitter, 0, 1) * 0.2)))
    };
}
