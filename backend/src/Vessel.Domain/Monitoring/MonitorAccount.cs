using Vessel.Domain.Common;

namespace Vessel.Domain.Monitoring;

/// <summary>Durable admission and fencing, shared by scheduler, Check and account refresh.</summary>
public sealed class MonitorAccount : IOwned
{
    public Guid AccountId { get; set; }
    public Guid OwnerId { get; set; }
    public string State { get; private set; } = "idle";
    /// <summary>
    /// The persisted identity of <see cref="State"/>: incremented under the account and monitor lock whenever the
    /// state changes. It keys <c>monitor.state-changed</c>; the lease <see cref="Generation"/> only fences runs.
    /// </summary>
    public long StateRevision { get; private set; }
    public DateTimeOffset? NextRunAtUtc { get; set; }
    public Guid? RequestId { get; set; }
    public string Trigger { get; set; } = "monitor";
    public string ManualState { get; set; } = "idle";
    public DateTimeOffset? NextManualAllowedAtUtc { get; set; }
    public string? LeaseHolder { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public long Generation { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? LastSuccessAtUtc { get; set; }
    public DateTimeOffset? LastObservationAtUtc { get; set; }
    public DateTimeOffset? LastOrdersObservationAtUtc { get; set; }
    public DateTimeOffset? NewestFillAtUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public bool HasHistoryGap { get; set; }
    public string? Error { get; set; }
    public string? CredentialRevision { get; set; }
    public DateTimeOffset? ConfirmationAtUtc { get; set; }

    /// <returns>Whether the state changed, and so took a new <see cref="StateRevision"/>.</returns>
    public bool SetState(string state)
    {
        if (State == state) return false;
        State = state;
        StateRevision = checked(StateRevision + 1);
        return true;
    }

    /// <summary>Nothing to watch: no scheduled run and no stale error text.</summary>
    public void Idle()
    {
        SetState("idle");
        NextRunAtUtc = null;
        Error = null;
    }

    public int RetryAfter(DateTimeOffset now)
    {
        if (ManualState != "idle")
            return Math.Max(1, (int)Math.Ceiling(((LeaseUntilUtc ?? now.AddSeconds(30)) - now).TotalSeconds));
        return NextManualAllowedAtUtc > now
            ? Math.Max(1, (int)Math.Ceiling((NextManualAllowedAtUtc.Value - now).TotalSeconds)) : 0;
    }

    /// <param name="scheduled">A scheduled or confirmation run, which the manual cooldown never gates.</param>
    public void Admit(Guid requestId, string trigger, DateTimeOffset now, bool scheduled = false)
    {
        ReleaseAbandonedRefresh(now);
        if (requestId == Guid.Empty || ManualState != "idle" || !scheduled && RetryAfter(now) != 0)
            throw new InvalidOperationException("Monitor admission is busy or cooling down.");
        RequestId = requestId;
        Trigger = trigger;
        ManualState = "pending";
        if (!Background(trigger)) NextRunAtUtc = now;
    }

    public bool Claim(Guid requestId, string holder, DateTimeOffset now, TimeSpan lease)
    {
        // A delayed command must match the durable request, not create new work.
        if (RequestId != requestId || ManualState == "idle" ||
            LeaseUntilUtc > now && ManualState == "running") return false;
        Generation = checked(Generation + 1);
        LeaseHolder = holder;
        LeaseUntilUtc = now + lease;
        // An owner refresh or a backfill (Phase 2, item 6b) is not a check attempt.
        if (!Background(Trigger)) LastAttemptAtUtc = now;
        ManualState = "running";
        return true;
    }

    public bool Owns(long generation, string holder, DateTimeOffset now) =>
        Generation == generation && LeaseHolder == holder && ManualState == "running" && LeaseUntilUtc > now;

    /// <summary>
    /// The owner received the refresh's answer (or its API crashed), so nobody else may replay it later.
    /// An abandoned refresh leaves the check schedule as it was.
    /// </summary>
    public bool ReleaseAbandonedRefresh(DateTimeOffset now)
    {
        // An abandoned backfill run (Phase 2, item 6b) is released the same way; its backfill stays queued.
        if (!Background(Trigger) || ManualState == "idle" || ManualState == "running" && LeaseUntilUtc > now) return false;
        Release();
        return true;
    }

    /// <summary>
    /// Pending check work (an owner Check, or a scheduled run deferred by a venue budget) that no worker claimed within
    /// <paramref name="unclaimedAfter"/> of becoming due stops blocking manual admission: it returns to idle. It is not
    /// dropped: <see cref="NextRunAtUtc"/> stays due (in the past), so a returning worker claims it as a scheduled run.
    /// Running or leased work and refresh work are unaffected.
    /// </summary>
    public bool ReleaseUnclaimedWork(DateTimeOffset now, TimeSpan unclaimedAfter)
    {
        if (ManualState != "pending" || Background(Trigger) || LeaseUntilUtc is not null ||
            NextRunAtUtc is not { } due || now - due < unclaimedAfter) return false;
        Release();
        return true;
    }

    /// <summary>
    /// Runs that never touch the check schedule: an owner Refresh, and a backfill run (Phase 2, item 6b), which reads behind
    /// protection checks on its own lease.
    /// </summary>
    public static bool Background(string trigger) => trigger is "refresh" or "backfill";

    public void Release()
    {
        ManualState = "idle";
        LeaseHolder = null;
        LeaseUntilUtc = null;
    }

    /// <summary>A check finished: the only outcome that moves backoff, state, last success and the due time.</summary>
    public void Complete(DateTimeOffset now, TimeSpan cooldown, TimeSpan nextDelay, bool success,
        bool degraded = false, bool credentialError = false, string? error = null)
    {
        Release();
        NextManualAllowedAtUtc = now + cooldown;
        NextRunAtUtc = credentialError ? null : now + nextDelay;
        ConsecutiveFailures = success ? 0 : checked(ConsecutiveFailures + 1);
        if (success) LastSuccessAtUtc = now;
        SetState(credentialError ? "credential-error" : success ? degraded ? "degraded" : "live" :
            ConsecutiveFailures >= 3 ? "stale" : "degraded");
        Error = error;
    }

    /// <summary>
    /// An owner refresh finished, either way. It shares the admission gate, so the manual cooldown starts, but it
    /// never changes check backoff, state, last success, consecutive failures or the scheduled due time.
    /// </summary>
    public void CompleteRefresh(DateTimeOffset now, TimeSpan cooldown)
    {
        Release();
        NextManualAllowedAtUtc = now + cooldown;
    }

    /// <summary>
    /// Changed settings or a newer observation superseded the run, or it was interrupted. That is not a failure:
    /// backoff, state and last success stay; scheduled work is retried after <paramref name="retry"/>.
    /// </summary>
    public void Supersede(DateTimeOffset now, TimeSpan cooldown, TimeSpan retry)
    {
        Release();
        NextManualAllowedAtUtc = now + cooldown;
        if (NextRunAtUtc is not null) NextRunAtUtc = now + retry;
    }
}

public static class MonitorWatchOutcomes
{
    /// <summary>A read reported the order filled, cancelled or rejected.</summary>
    public const string Terminal = "terminal";
    /// <summary>A complete open-order read no longer listed it.</summary>
    public const string Absent = "absent";
    /// <summary>The watch reached its lifetime without proof. It neither resolves an orphan nor claims the order is gone.</summary>
    public const string Unverifiable = "unverifiable";
}

/// <summary>
/// A linked order of a Closed or Cancelled Play (an exit, or an entry that may still rest), watched until venue
/// evidence shows it is no longer resting or its lifetime ends. <see cref="Outcome"/> is null while watching.
/// </summary>
public sealed class MonitorExitWatch : IOwned
{
    public Guid LinkId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public Guid PlayId { get; set; }
    public DateTimeOffset LinkRevisionAtUtc { get; set; }
    /// <summary>When this link revision came under watch: the later of the Play's end and the link change.</summary>
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string? Outcome { get; set; }
}
