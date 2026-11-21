using Microsoft.Extensions.Logging;

namespace Vessel.Persistence.Events.Subscriptions;

/// <summary>
/// The explicit, bounded admin retry. Retained <c>failed</c> receipts return to <c>pending</c> with a bounded extra
/// attempt budget on top of their preserved attempts; the low-water mark is never rewound. A repeated notification
/// never reopens a receipt.
/// </summary>
public sealed class EventReceiptAdmin(VesselDbContext db, UnitOfWork work, TimeProvider time, ILogger<EventReceiptAdmin> logger)
{
    public Task<int> RetryFailedAsync(EventSubscription subscription, int limit, int extraAttempts, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000 || extraAttempts < 1 || extraAttempts > subscription.Limits.MaxAdminRetryBudget)
            throw new ArgumentOutOfRangeException(nameof(extraAttempts), "The admin retry budget is bounded.");
        return work.ExecuteAsync(async () =>
        {
            // Like dispatch, hold the owner's retention key shared: a floor decision computed before this retry
            // cannot be published over the receipts it reopens, and a published floor excludes them here.
            await VesselEventSql.BlockingScalarAsync(db, "SELECT pg_advisory_xact_lock_shared(@key)", cancellationToken,
                ("key", VesselEventSql.RetentionLock(db.CurrentOwnerId)));
            if (await SubscriptionProgress.LockRegistrationAsync(db, subscription, logger, cancellationToken) != SubscriptionRegistration.Compatible)
                return 0;
            return await VesselEventSql.ExecuteAsync(db, """
            UPDATE event_receipts r SET state='pending',max_attempts=r.attempts+@extra,next_attempt_at=@now,settled_at=NULL
            WHERE (r.owner_id,r.subscription,r.event_id) IN (
                SELECT f.owner_id,f.subscription,f.event_id FROM event_receipts f
                JOIN events e ON e.owner_id=f.owner_id AND e.event_id=f.event_id
                JOIN event_cursors c ON c.owner_id=f.owner_id
                WHERE f.owner_id=@owner AND f.subscription=@name AND f.state='failed' AND e.sequence>c.retention_floor
                ORDER BY f.sequence LIMIT @limit FOR UPDATE OF f SKIP LOCKED)
            """, cancellationToken, ("owner", db.CurrentOwnerId), ("name", subscription.Name), ("extra", extraAttempts),
            ("now", time.GetUtcNow()), ("limit", limit));
        }, cancellationToken);
    }
}
