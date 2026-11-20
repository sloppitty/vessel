using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vessel.Application.Events;
using Vessel.Application.Ownership;

namespace Vessel.Persistence.Events.Subscriptions;

public enum DispatchTrigger { Live, CatchUp }

public enum DispatchOutcome
{
    Delivered, Skipped, Expired, RetryScheduled,
    /// <summary>Terminal: attempt cap, unsupported version, unreadable payload or a permanent handler failure.</summary>
    Failed,
    InvalidOwner,
    /// <summary>No new handling while suspended; discovery resumes after restore.</summary>
    Suspended,
    /// <summary>Neither a retained event nor a dedup identity: whatever the message claims, it is not ours.</summary>
    Unknown,
    /// <summary>Pruned (dedup identity only) or retained below the retention floor. Not recorded as handled.</summary>
    Historical,
    AlreadySettled, NotResponsible,
    /// <summary>A pending receipt whose retry deadline has not arrived; catch-up owns it.</summary>
    NotDue,
    /// <summary>Another dispatcher holds the receipt (catch-up only skips; live delivery waits).</summary>
    Busy
}

public readonly record struct DispatchResult(DispatchOutcome Outcome)
{
    /// <summary>Broker action for live delivery: dead-letter for diagnosis, otherwise acknowledge.</summary>
    public bool DeadLetter => Outcome is DispatchOutcome.Failed or DispatchOutcome.InvalidOwner or DispatchOutcome.Unknown;
}

/// <summary>
/// The one dispatcher behind live delivery and catch-up. In one transaction it looks the event up by owner and ID
/// (never trusting the message's sequence or payload), acquires and locks the receipt, checks responsibility,
/// terminal state, expiry, retry deadline and remaining attempts, runs the handler's database-only effects and
/// settles the receipt. The handler and the save of its effects run in a savepoint: a failure rolls the effects
/// back and the attempt is recorded in the same transaction, so the receipt lock is never released in between.
/// </summary>
public sealed class EventDispatcher(VesselDbContext db, UnitOfWork work, IServiceProvider services,
    TimeProvider time, ILogger<EventDispatcher> logger, IJournalOwnerDirectory owners)
{
    private static readonly string[] Terminal = ["delivered", "skipped", "expired", "failed"];

    public async Task<DispatchResult> DispatchAsync(EventSubscription subscription, Guid ownerId, Guid eventId,
        DispatchTrigger trigger, CancellationToken cancellationToken)
    {
        // The scope's owner was fixed before the context was resolved. A message never chooses it.
        if (ownerId == Guid.Empty || ownerId != db.CurrentOwnerId || eventId == Guid.Empty)
            return new(DispatchOutcome.InvalidOwner);
        var eligibility = await EligibilityAsync(cancellationToken);
        if (eligibility is { } outcome) return new(outcome);
        var attempt = new Attempt();
        try
        {
            return new(await work.ExecuteAsync(() => DispatchInTransactionAsync(subscription, eventId, trigger, attempt, cancellationToken),
                cancellationToken));
        }
        catch (Exception error) when (attempt.HandlerStarted && !cancellationToken.IsCancellationRequested)
        {
            // The handler succeeded but the unit failed to commit (for example an event it appended was refused).
            // The whole unit rolled back; record the attempt in a new transaction so it still counts towards the cap.
            logger.LogWarning("Subscription {Subscription} could not commit event {EventId} ({ErrorType}).",
                subscription.Name, eventId, error.GetType().Name);
            eligibility = await EligibilityAsync(cancellationToken);
            if (eligibility is { } failureOutcome) return new(failureOutcome);
            return new(await work.ExecuteAsync(() => RecordFailureAsync(subscription, eventId, attempt.ObservedAttempts,
                error, cancellationToken), cancellationToken));
        }
    }

    private async Task<DispatchOutcome> DispatchInTransactionAsync(EventSubscription subscription, Guid eventId,
        DispatchTrigger trigger, Attempt attempt, CancellationToken ct)
    {
        await LockRetentionSharedAsync(ct);
        var live = trigger != DispatchTrigger.CatchUp;
        // Live delivery waits, by design, for another dispatcher of this owner and subscription to commit: its first-time
        // initialization, then its receipt. No lock limit from here until the receipt is locked (LockReceiptAsync restores it).
        if (live) await VesselEventSql.ExecuteAsync(db, DatabaseLimits.LiftLockLimit, ct);
        var responsible = await SubscriptionProgress.InitializeAsync(db, subscription, logger, ct);
        if (responsible is null) return DispatchOutcome.NotResponsible;
        var retained = await RetainedAsync(eventId, ct);
        if (retained is null)
            return await KnownIdentityAsync(eventId, ct) ? DispatchOutcome.Historical : DispatchOutcome.Unknown;
        var (sequence, type, expiresAt, payload, floor) = retained.Value;
        if (sequence <= floor) return DispatchOutcome.Historical;
        if (sequence <= responsible || !subscription.Matches(type)) return DispatchOutcome.NotResponsible;

        var receipt = await LockReceiptAsync(subscription, eventId, sequence, time.GetUtcNow(), skipLocked: !live, ct);
        if (receipt is null) return DispatchOutcome.Busy;
        var (state, attempts, maxAttempts, nextAttemptAt) = receipt.Value;
        if (Terminal.Contains(state)) return DispatchOutcome.AlreadySettled;
        var now = time.GetUtcNow();
        if (nextAttemptAt > now) return DispatchOutcome.NotDue;
        if (expiresAt is { } expires && expires <= now)
            return await SettleAsync(subscription.Name, eventId, "expired", 0, null, now, ct);
        // A stored payload that cannot be read is a poison event, not a retryable failure: settle it here, in this
        // transaction and before any handler attempt, so catch-up never rediscovers it and the low-water mark moves.
        // Never log the payload.
        if (Read(payload) is not { } message)
        {
            logger.LogWarning("Subscription {Subscription} cannot read event {EventId}; settled as failed.", subscription.Name, eventId);
            return await SettleAsync(subscription.Name, eventId, "failed", 0, UnreadableEvent, now, ct);
        }
        if (message.Version > subscription.Limits.SupportedVersion)
            return await SettleAsync(subscription.Name, eventId, "failed", 0, "Unsupported event version.", now, ct);
        if (attempts >= maxAttempts)
            return await SettleAsync(subscription.Name, eventId, "failed", 0, "Attempt limit reached.", now, ct);

        var handler = services.GetRequiredKeyedService<IVesselEventSubscriber>(subscription.Name);
        attempt.ObservedAttempts = attempts;
        // Stays set through settlement and commit: any later failure is still this handler's attempt.
        attempt.HandlerStarted = true;
        var handled = EventHandling.Delivered;
        var error = await work.IsolateAsync(async () => handled = await handler.HandleAsync(message, ct), ct);
        if (error is null)
            return await SettleAsync(subscription.Name, eventId, handled == EventHandling.Skipped ? "skipped" : "delivered",
                1, null, time.GetUtcNow(), ct);
        // Shutdown is not an attempt: roll everything back and leave the event to catch-up.
        if (error is OperationCanceledException && ct.IsCancellationRequested)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        // Effects are undone; the receipt is still locked by this transaction while the attempt is recorded.
        logger.LogWarning("Subscription {Subscription} failed event {EventId} with {ErrorType}.",
            subscription.Name, eventId, error.GetType().Name);
        attempt.HandlerStarted = false;
        return await FailAttemptAsync(subscription, eventId, attempts, maxAttempts, error, time.GetUtcNow(), ct);
    }

    private async Task<DispatchOutcome> RecordFailureAsync(EventSubscription subscription, Guid eventId, int observedAttempts,
        Exception error, CancellationToken ct)
    {
        await LockRetentionSharedAsync(ct);
        // The same designed waits as live delivery: the failed unit's rollback may have undone this owner's first-time
        // initialization, which another dispatcher may now hold uncommitted. LockReceiptAsync restores the limit.
        await VesselEventSql.ExecuteAsync(db, DatabaseLimits.LiftLockLimit, ct);
        if (await SubscriptionProgress.LockRegistrationAsync(db, subscription, logger, ct) != SubscriptionRegistration.Compatible)
            return DispatchOutcome.NotResponsible;
        if (await RetainedAsync(eventId, ct) is not { } retained || retained.Sequence <= retained.Floor)
            return DispatchOutcome.Historical;
        if (!await ResponsibleAsync(subscription, retained.Sequence, retained.Type, ct)) return DispatchOutcome.NotResponsible;
        var now = time.GetUtcNow();
        if (await LockReceiptAsync(subscription, eventId, retained.Sequence, now, skipLocked: false, ct) is not { } receipt)
            return DispatchOutcome.Busy;
        // Another dispatcher settled or attempted it after this attempt's rollback: its record stands.
        if (Terminal.Contains(receipt.State)) return DispatchOutcome.AlreadySettled;
        if (receipt.Attempts != observedAttempts) return DispatchOutcome.NotDue;
        return await FailAttemptAsync(subscription, eventId, receipt.Attempts, receipt.MaxAttempts, error, now, ct);
    }

    /// <summary>Records one failed attempt on a receipt this transaction has locked: retry later or fail at the cap.</summary>
    private async Task<DispatchOutcome> FailAttemptAsync(EventSubscription subscription, Guid eventId, int previousAttempts,
        int maxAttempts, Exception error, DateTimeOffset now, CancellationToken ct)
    {
        var attempts = previousAttempts + 1;
        var reason = error is TerminalEventException ? Safe(error.Message) : $"Handler failed ({error.GetType().Name}).";
        if (error is TerminalEventException || attempts >= maxAttempts)
            return await SettleAsync(subscription.Name, eventId, "failed", 1, reason, now, ct);
        var next = now + subscription.Limits.RetryDelay(attempts, Random.Shared.NextDouble());
        await VesselEventSql.ExecuteAsync(db, """
            UPDATE event_receipts SET attempts=attempts+1,next_attempt_at=@next,error=@error
            WHERE owner_id=@owner AND subscription=@name AND event_id=@id
            """, ct, ("owner", db.CurrentOwnerId), ("name", subscription.Name), ("id", eventId), ("next", next), ("error", reason));
        return DispatchOutcome.RetryScheduled;
    }

    private async Task<DispatchOutcome> SettleAsync(string subscription, Guid eventId, string state, int attempt,
        string? error, DateTimeOffset now, CancellationToken ct)
    {
        await VesselEventSql.ExecuteAsync(db, """
            UPDATE event_receipts SET state=@state,attempts=attempts+@attempt,settled_at=@now,error=@error
            WHERE owner_id=@owner AND subscription=@name AND event_id=@id
            """, ct, ("owner", db.CurrentOwnerId), ("name", subscription), ("id", eventId), ("state", state),
            ("attempt", attempt), ("now", now), ("error", error));
        return state switch
        {
            "delivered" => DispatchOutcome.Delivered,
            "skipped" => DispatchOutcome.Skipped,
            "expired" => DispatchOutcome.Expired,
            _ => DispatchOutcome.Failed
        };
    }

    // The scalar result of pg_advisory_xact_lock_shared is void; callers only await the lock, so Task is the contract.
#pragma warning disable CA1859
    // Waits behind a floor publication, which waits for in-flight dispatches: blocking by design, so no lock limit.
    private Task LockRetentionSharedAsync(CancellationToken ct) =>
        VesselEventSql.BlockingScalarAsync(db, "SELECT pg_advisory_xact_lock_shared(@key)", ct, ("key", VesselEventSql.RetentionLock(db.CurrentOwnerId)));
#pragma warning restore CA1859

    private async Task<(long Sequence, string Type, DateTimeOffset? ExpiresAt, string Payload, long Floor)?> RetainedAsync(
        Guid eventId, CancellationToken ct)
    {
        // The key-share lock keeps the event until this transaction ends: an account deletion waits for the dispatch
        // instead of removing the event between this lookup and its receipt insert.
        await using var command = VesselEventSql.TransactionCommand(db, """
            SELECT e.sequence,e.type,e.expires_at,e.payload::text,c.retention_floor
            FROM events e JOIN event_cursors c ON c.owner_id=e.owner_id
            WHERE e.owner_id=@owner AND e.event_id=@id
            FOR KEY SHARE OF e
            """, ("owner", db.CurrentOwnerId), ("id", eventId));
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return null;
        return (row.GetInt64(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetFieldValue<DateTimeOffset>(2),
            row.GetString(3), row.GetInt64(4));
    }

    private async Task<bool> KnownIdentityAsync(Guid eventId, CancellationToken ct) =>
        await VesselEventSql.ScalarAsync(db, "SELECT 1 FROM event_dedup WHERE owner_id=@owner AND event_id=@id LIMIT 1",
            ct, ("owner", db.CurrentOwnerId), ("id", eventId)) is not null;

    private async Task<bool> ResponsibleAsync(EventSubscription subscription, long sequence, string type, CancellationToken ct) =>
        subscription.Matches(type) && await SubscriptionProgress.InitializeAsync(db, subscription, logger, ct) is { } responsible &&
        sequence > responsible;

    private async Task<DispatchOutcome?> EligibilityAsync(CancellationToken ct) =>
        await owners.StatusAsync(db.CurrentOwnerId, ct) switch
        {
            JournalOwnerStatus.Active => null,
            JournalOwnerStatus.Suspended => DispatchOutcome.Suspended,
            _ => DispatchOutcome.InvalidOwner
        };

    /// <summary>Creates the receipt if it is missing, then locks it (or skips it while another dispatcher holds it).</summary>
    private async Task<(string State, int Attempts, int MaxAttempts, DateTimeOffset NextAttemptAt)?> LockReceiptAsync(
        EventSubscription subscription, Guid eventId, long sequence, DateTimeOffset now, bool skipLocked, CancellationToken ct)
    {
        // Live delivery waits for the dispatcher that holds the receipt, by design: no lock limit for that wait. It
        // starts at the insert, which already waits when another dispatcher inserted the same receipt and has not committed.
        if (!skipLocked) await VesselEventSql.ExecuteAsync(db, DatabaseLimits.LiftLockLimit, ct);
        await VesselEventSql.ExecuteAsync(db, """
            INSERT INTO event_receipts(owner_id,subscription,event_id,sequence,state,attempts,max_attempts,next_attempt_at,created_at)
            VALUES(@owner,@name,@id,@sequence,'pending',0,@max,@now,@now) ON CONFLICT DO NOTHING
            """, ct, ("owner", db.CurrentOwnerId), ("name", subscription.Name), ("id", eventId), ("sequence", sequence),
            ("max", subscription.Limits.MaxAttempts), ("now", now));
        (string, int, int, DateTimeOffset)? receipt = null;
        await using (var command = VesselEventSql.TransactionCommand(db, $"""
            SELECT state,attempts,max_attempts,next_attempt_at FROM event_receipts
            WHERE owner_id=@owner AND subscription=@name AND event_id=@id FOR UPDATE{(skipLocked ? " SKIP LOCKED" : "")}
            """, ("owner", db.CurrentOwnerId), ("name", subscription.Name), ("id", eventId)))
        await using (var row = await command.ExecuteReaderAsync(ct))
            if (await row.ReadAsync(ct))
                receipt = (row.GetString(0), row.GetInt32(1), row.GetInt32(2), row.GetFieldValue<DateTimeOffset>(3));
        if (!skipLocked) await VesselEventSql.ExecuteAsync(db, DatabaseLimits.RestoreLockLimit, ct);
        return receipt;
    }

    private static string Safe(string message) => message.Length <= 200 ? message : message[..200];

    /// <summary>The fixed receipt error for a stored payload that does not deserialize to an event.</summary>
    public const string UnreadableEvent = "Unreadable event.";

    private static VesselEvent? Read(string payload)
    {
        try { return JsonSerializer.Deserialize<VesselEvent>(payload, VesselEventJson.Options); }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private sealed class Attempt
    {
        public bool HandlerStarted { get; set; }
        public int ObservedAttempts { get; set; }
    }
}
