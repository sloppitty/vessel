using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vessel.Application.Ownership;

namespace Vessel.Persistence.Events.Subscriptions;

public sealed record CatchUpResult(int Discovered, int Retried, long LowWaterMark);

/// <summary>
/// Subscriber-side recovery from PostgreSQL. Each bounded pass discovers matching events after the low-water mark
/// that have no receipt (past the grace period), retries due pending receipts at any sequence, then advances the
/// low-water mark across a settled prefix. Everything goes through the one dispatcher; replicas are safe.
/// </summary>
public sealed class EventCatchUp(VesselDbContext db, UnitOfWork work, EventDispatcher dispatcher, TimeProvider time,
    ILogger<EventCatchUp> logger, IJournalOwnerDirectory owners)
{
    /// <summary>Largest sequence span examined by one low-water advance.</summary>
    internal const int AdvanceWindow = 10_000;

    public async Task<CatchUpResult> RunAsync(EventSubscription subscription, CancellationToken cancellationToken)
    {
        if (await owners.StatusAsync(db.CurrentOwnerId, cancellationToken) != JournalOwnerStatus.Active)
            return new(0, 0, -1);
        var initialized = await work.ExecuteAsync(async () =>
        {
            await LockRetentionAsync(cancellationToken);
            return await SubscriptionProgress.InitializeAsync(db, subscription, logger, cancellationToken) is not null;
        }, cancellationToken);
        if (!initialized) return new(0, 0, -1);
        var types = EventSubscriptionRegistry.MatchingTypes(subscription);
        var now = time.GetUtcNow();
        var discovered = await IdsAsync("""
            SELECT e.event_id FROM events e
            JOIN event_subscriptions s ON s.owner_id=e.owner_id AND s.name=@name
            JOIN event_cursors c ON c.owner_id=e.owner_id
            WHERE e.owner_id=@owner AND e.sequence>s.low_water_mark AND e.sequence>s.responsible_from
              AND e.sequence>c.retention_floor AND e.type=ANY(@types) AND e.detected_at<@grace
              AND NOT EXISTS(SELECT 1 FROM event_receipts r
                             WHERE r.owner_id=e.owner_id AND r.subscription=@name AND r.event_id=e.event_id)
            ORDER BY e.sequence LIMIT @batch
            """, subscription, cancellationToken, ("types", types), ("grace", now - subscription.Limits.DiscoveryGrace));
        foreach (var id in discovered)
            await DispatchAsync(subscription, id, cancellationToken);
        // Admin retries can sit behind the low-water mark; retry work is joined to retained events, not to the mark.
        var due = await IdsAsync("""
            SELECT r.event_id FROM event_receipts r
            JOIN events e ON e.owner_id=r.owner_id AND e.event_id=r.event_id
            JOIN event_cursors c ON c.owner_id=r.owner_id
            WHERE r.owner_id=@owner AND r.subscription=@name AND r.state='pending' AND r.next_attempt_at<=@now
              AND e.sequence>c.retention_floor
            ORDER BY r.next_attempt_at,r.sequence LIMIT @batch
            """, subscription, cancellationToken, ("now", time.GetUtcNow()));
        foreach (var id in due)
            await DispatchAsync(subscription, id, cancellationToken);
        return new(discovered.Count, due.Count, await AdvanceAsync(subscription, types, cancellationToken));
    }

    // One event that cannot even record its attempt (an infrastructure failure) must not stop the pass:
    // later events are still dispatched and the mark still advances up to it. A later pass retries it.
    private async Task DispatchAsync(EventSubscription subscription, Guid id, CancellationToken ct)
    {
        try { await dispatcher.DispatchAsync(subscription, db.CurrentOwnerId, id, DispatchTrigger.CatchUp, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Catch-up for subscription {Subscription} could not dispatch event {EventId} ({ErrorType}).",
                subscription.Name, id, error.GetType().Name);
        }
    }

    /// <summary>
    /// Moves the mark monotonically to just before the first unsettled matching event, bounded by the committed
    /// high-water sequence. A hole below that sequence (a deleted account's events, pruned history) never fills.
    /// </summary>
    private async Task<long> AdvanceAsync(EventSubscription subscription, string[] types, CancellationToken ct)
    {
        if (await owners.StatusAsync(db.CurrentOwnerId, ct) != JournalOwnerStatus.Active) return -1;
        return await work.ExecuteAsync(async () =>
        {
            await LockRetentionAsync(ct);
            if (await SubscriptionProgress.LockRegistrationAsync(db, subscription, logger, ct) != SubscriptionRegistration.Compatible)
                return -1L;
            await using var locked = VesselEventSql.TransactionCommand(db, """
                SELECT s.low_water_mark,s.responsible_from,c.sequence,c.retention_floor
                FROM event_subscriptions s LEFT JOIN event_cursors c ON c.owner_id=s.owner_id
                WHERE s.owner_id=@owner AND s.name=@name FOR UPDATE OF s
                """, ("owner", db.CurrentOwnerId), ("name", subscription.Name));
            long mark, responsible, high, floor;
            await using (var row = await locked.ExecuteReaderAsync(ct))
            {
                if (!await row.ReadAsync(ct)) return -1L;
                mark = row.GetInt64(0); responsible = row.GetInt64(1);
                high = row.IsDBNull(2) ? 0 : row.GetInt64(2); floor = row.IsDBNull(3) ? 0 : row.GetInt64(3);
            }
            var upper = Math.Min(high, mark + AdvanceWindow);
            if (upper <= mark) return mark;
            var first = await VesselEventSql.ScalarAsync(db, """
                SELECT min(e.sequence) FROM events e
                WHERE e.owner_id=@owner AND e.sequence>@mark AND e.sequence<=@upper AND e.sequence>@floor
                  AND e.sequence>@responsible AND e.type=ANY(@types)
                  AND NOT EXISTS(SELECT 1 FROM event_receipts r WHERE r.owner_id=e.owner_id AND r.subscription=@name
                                 AND r.event_id=e.event_id AND r.state IN ('delivered','skipped','expired','failed'))
                """, ct, ("owner", db.CurrentOwnerId), ("name", subscription.Name), ("mark", mark), ("upper", upper),
                ("floor", floor), ("responsible", responsible), ("types", types));
            var next = first is long unsettled ? unsettled - 1 : upper;
            if (next <= mark) return mark;
            await VesselEventSql.ExecuteAsync(db, """
                UPDATE event_subscriptions SET low_water_mark=@next,updated_at=@now,
                       status=CASE WHEN @next>=@floor THEN 'active' ELSE status END
                WHERE owner_id=@owner AND name=@name AND low_water_mark<@next
                """, ct, ("owner", db.CurrentOwnerId), ("name", subscription.Name), ("next", next), ("floor", floor),
                ("now", time.GetUtcNow()));
            return next;
        }, ct);
    }

    // Waits behind a floor publication, which waits for in-flight dispatches: blocking by design, so no lock limit.
    private Task<object?> LockRetentionAsync(CancellationToken ct) =>
        VesselEventSql.BlockingScalarAsync(db, "SELECT pg_advisory_xact_lock_shared(@key)", ct,
            ("key", VesselEventSql.RetentionLock(db.CurrentOwnerId)));

    private async Task<IReadOnlyList<Guid>> IdsAsync(string sql, EventSubscription subscription, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = VesselEventSql.Command(db, sql, [("owner", db.CurrentOwnerId),
                ("name", subscription.Name), ("batch", subscription.Limits.BatchSize), .. parameters]);
            await using var rows = await command.ExecuteReaderAsync(ct);
            var ids = new List<Guid>();
            while (await rows.ReadAsync(ct)) ids.Add(rows.GetGuid(0));
            return ids;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
