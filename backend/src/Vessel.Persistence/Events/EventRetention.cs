using Microsoft.Extensions.Options;

namespace Vessel.Persistence.Events;

public sealed record RetentionResult(long Floor, int Deleted);

/// <summary>
/// Subscription-aware retention. A short, per-owner serialized floor publication decides a contiguous prefix of
/// events, all older than <c>RetentionDays</c>, none with a pending receipt and none above the lowest low-water mark
/// of an active subscription. A subscription whose mark is behind that window is marked lagging with an explicit
/// gap and stops holding retention. Physical deletion below the floor follows in repeatable set-based batches.
/// Dedup and baseline identities survive.
/// </summary>
public sealed class EventRetention(VesselDbContext db, UnitOfWork work, IOptions<VesselEventSettings> settings, TimeProvider time)
{
    /// <summary>The owner's retention advisory key: floor publication takes it exclusively, dispatch and admin retry shared.</summary>
    public static long LockKey(Guid owner) => VesselEventSql.RetentionLock(owner);

    /// <summary>Largest sequence span one floor decision examines; later runs continue.</summary>
    internal const int DecisionWindow = 10_000;

    public async Task<RetentionResult> RunAsync(CancellationToken cancellationToken)
    {
        var floor = await PublishFloorAsync(cancellationToken);
        var deleted = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await DeleteBatchAsync(cancellationToken);
            deleted += batch;
            if (batch < settings.Value.RetentionBatch) break;
        }
        return new(floor, deleted);
    }

    /// <summary>Raises the logical floor. Never takes the owner cursor lock while scanning.</summary>
    public Task<long> PublishFloorAsync(CancellationToken cancellationToken) => work.ExecuteAsync(async () =>
    {
        // Serializes floor decisions per owner and waits for in-flight dispatches, which hold the key shared: a wait that
        // blocks by design, and a sweep whose scans are long on purpose, so neither session limit applies here.
        await VesselEventSql.LiftStatementLimitAsync(db, cancellationToken);
        await VesselEventSql.BlockingScalarAsync(db, "SELECT pg_advisory_xact_lock(@key)", cancellationToken,
            ("key", VesselEventSql.RetentionLock(db.CurrentOwnerId)));
        await using var cursor = VesselEventSql.TransactionCommand(db,
            "SELECT retention_floor FROM event_cursors WHERE owner_id=@owner", ("owner", db.CurrentOwnerId));
        if (await cursor.ExecuteScalarAsync(cancellationToken) is not long floor) return 0L;
        var cutoff = time.GetUtcNow().AddDays(-settings.Value.RetentionDays);
        // The age-eligible contiguous prefix: up to the first retained event inside the window.
        var aged = (long)(await VesselEventSql.ScalarAsync(db, """
            WITH window_rows AS (
                SELECT sequence,detected_at FROM events WHERE owner_id=@owner AND sequence>@floor
                ORDER BY sequence LIMIT @window
            )
            SELECT COALESCE((SELECT min(sequence)-1 FROM window_rows WHERE detected_at>=@cutoff),
                            (SELECT max(sequence) FROM window_rows), @floor)
            """, cancellationToken, ("owner", db.CurrentOwnerId), ("floor", floor), ("window", DecisionWindow),
            ("cutoff", cutoff)))!;
        // Pending delivery work is never silently deleted.
        if (await VesselEventSql.ScalarAsync(db, """
            SELECT min(sequence) FROM event_receipts WHERE owner_id=@owner AND state='pending' AND sequence>@floor
            """, cancellationToken, ("owner", db.CurrentOwnerId), ("floor", floor)) is long pending)
            aged = Math.Min(aged, pending - 1);
        if (aged <= floor) return floor;
        // Behind the retention window: lagging, with the explicit gap it will never be delivered.
        await VesselEventSql.ExecuteAsync(db, """
            UPDATE event_subscriptions SET status='lagging',updated_at=@now,
                   gap_from=CASE WHEN gap_to IS NULL OR gap_to<low_water_mark THEN low_water_mark+1 ELSE gap_from END,
                   gap_to=@candidate
            WHERE owner_id=@owner AND low_water_mark<@candidate
            """, cancellationToken, ("owner", db.CurrentOwnerId), ("candidate", aged), ("now", time.GetUtcNow()));
        if (await VesselEventSql.ScalarAsync(db, """
            SELECT min(low_water_mark) FROM event_subscriptions WHERE owner_id=@owner AND status='active'
            """, cancellationToken, ("owner", db.CurrentOwnerId)) is long lowest)
            aged = Math.Min(aged, lowest);
        if (aged <= floor) return floor;
        // The only cursor write: a brief row lock at the very end, after every scan.
        await VesselEventSql.ExecuteAsync(db, """
            UPDATE event_cursors SET retention_floor=GREATEST(retention_floor,@candidate) WHERE owner_id=@owner
            """, cancellationToken, ("owner", db.CurrentOwnerId), ("candidate", aged));
        return aged;
    }, cancellationToken);

    /// <summary>Deletes one batch of events below the published floor; receipts follow by cascade. Repeatable.</summary>
    public Task<int> DeleteBatchAsync(CancellationToken cancellationToken) => work.ExecuteAsync(async () =>
    {
        await VesselEventSql.LiftStatementLimitAsync(db, cancellationToken);
        return await VesselEventSql.ExecuteAsync(db, """
            DELETE FROM events WHERE owner_id=@owner AND sequence IN (
                SELECT e.sequence FROM events e JOIN event_cursors c ON c.owner_id=e.owner_id
                WHERE e.owner_id=@owner AND e.sequence<=c.retention_floor ORDER BY e.sequence LIMIT @batch)
            """, cancellationToken, ("owner", db.CurrentOwnerId), ("batch", settings.Value.RetentionBatch));
    }, cancellationToken);
}
