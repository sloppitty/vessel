using Npgsql;
using NpgsqlTypes;
using Vessel.Application.Monitoring;

namespace Vessel.Persistence;

public sealed class SystemMonitorStore(NpgsqlDataSource data, TimeProvider time) : ISystemMonitorStore
{
    public async Task<IReadOnlyList<DueMonitorCandidate>> DueCandidatesAsync(DueMonitorKey? after, DateTimeOffset dueBy, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        await using var command = data.CreateCommand("""
            WITH due AS (
                SELECT a."OwnerId" AS owner_id, a."Id" AS account_id, a."VenueId" AS venue_id,
                    COALESCE(m."NextRunAtUtc", m."LastAttemptAtUtc", TIMESTAMPTZ '1970-01-01') AS due_at
                FROM accounts a JOIN monitor_accounts m ON m."AccountId" = a."Id" AND m."OwnerId" = a."OwnerId"
                WHERE a."IsEnabled" AND a."SourceId" IS NOT NULL
                    AND (m."NextRunAtUtc" <= @now OR m."ManualState" <> 'idle')
                    AND (m."LeaseUntilUtc" IS NULL OR m."LeaseUntilUtc" <= @now)
                    AND NOT EXISTS (SELECT 1 FROM server_venue_deadlines d
                        WHERE d.venue_id = a."VenueId" AND d.retry_after_until > @now)
            )
            SELECT owner_id, account_id, venue_id, due_at FROM due WHERE due_at <= @due_by
                AND (@after_due IS NULL OR (due_at, owner_id, account_id) > (@after_due, @after_owner, @after_account))
            ORDER BY due_at, owner_id, account_id LIMIT @limit
            """);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        command.Parameters.AddWithValue("due_by", NpgsqlDbType.TimestampTz, dueBy.ToUniversalTime());
        After(command, after);
        command.Parameters.AddWithValue("limit", limit);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new List<DueMonitorCandidate>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetGuid(0), rows.GetGuid(1), rows.GetString(2), rows.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }

    public async Task<IReadOnlyList<DueMonitorCandidate>> DueBackfillsAsync(DueMonitorKey? after, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        // An abandoned run's expired lease does not hide its account: the claim releases it.
        await using var command = data.CreateCommand("""
            SELECT a."OwnerId", a."Id", a."VenueId", b."NextPageAtUtc"
            FROM account_history_backfills b
            JOIN accounts a ON a."Id" = b."AccountId" AND a."OwnerId" = b."OwnerId"
            LEFT JOIN monitor_accounts m ON m."AccountId" = a."Id" AND m."OwnerId" = a."OwnerId"
            WHERE b."State" = 'queued' AND b."NextPageAtUtc" <= @now
                AND a."IsEnabled" AND a."TradingMode" = 'real' AND a."SourceId" IS NOT NULL
                AND (m."AccountId" IS NULL OR m."ManualState" = 'idle' OR m."LeaseUntilUtc" <= @now)
                AND NOT EXISTS (SELECT 1 FROM server_venue_deadlines d
                    WHERE d.venue_id = a."VenueId" AND d.retry_after_until > @now)
                AND (@after_due IS NULL OR (b."NextPageAtUtc", a."OwnerId", a."Id") > (@after_due, @after_owner, @after_account))
            ORDER BY b."NextPageAtUtc", a."OwnerId", a."Id" LIMIT @limit
            """);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        After(command, after);
        command.Parameters.AddWithValue("limit", limit);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new List<DueMonitorCandidate>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetGuid(0), rows.GetGuid(1), rows.GetString(2), rows.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }

    public async Task<IReadOnlyList<DueMonitorCandidate>> DueFundingAsync(IReadOnlyCollection<string> venueIds, DueMonitorKey? after, int limit,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        if (venueIds.Count == 0) return [];
        // Keys only. An account never read has no due time and comes first; an abandoned run's expired lease does not hide
        // its account, and an account whose own check is due waits for it. Each claim rechecks all of this in its owner's scope.
        await using var command = data.CreateCommand("""
            SELECT a."OwnerId", a."Id", a."VenueId", COALESCE(m."NextFundingAtUtc", TIMESTAMPTZ '1970-01-01') AS due_at
            FROM accounts a
            LEFT JOIN monitor_accounts m ON m."AccountId" = a."Id" AND m."OwnerId" = a."OwnerId"
            WHERE a."IsEnabled" AND a."TradingMode" = 'real' AND a."SourceId" IS NOT NULL AND a."VenueId" = ANY(@venues)
                AND (a."KeepExecutionHistory" OR EXISTS (SELECT 1 FROM plays p
                    WHERE p."OwnerId" = a."OwnerId" AND p."AccountId" = a."Id" AND p."TradingMode" = 'real'
                        AND p."InstrumentSource" = 'Venue' AND p."Status" = 'Open'))
                AND (m."NextFundingAtUtc" IS NULL OR m."NextFundingAtUtc" <= @now)
                AND (m."NextRunAtUtc" IS NULL OR m."NextRunAtUtc" > @now)
                AND (m."AccountId" IS NULL OR m."ManualState" = 'idle' OR m."LeaseUntilUtc" <= @now)
                AND NOT EXISTS (SELECT 1 FROM server_venue_deadlines d
                    WHERE d.venue_id = a."VenueId" AND d.retry_after_until > @now)
                AND (@after_due IS NULL OR (COALESCE(m."NextFundingAtUtc", TIMESTAMPTZ '1970-01-01'), a."OwnerId", a."Id")
                    > (@after_due, @after_owner, @after_account))
            ORDER BY due_at, a."OwnerId", a."Id" LIMIT @limit
            """);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        command.Parameters.AddWithValue("venues", venueIds.ToArray());
        After(command, after);
        command.Parameters.AddWithValue("limit", limit);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new List<DueMonitorCandidate>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetGuid(0), rows.GetGuid(1), rows.GetString(2), rows.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }

    /// <summary>The (due, owner, account) key a page continues after; none starts at the beginning.</summary>
    private static void After(NpgsqlCommand command, DueMonitorKey? after)
    {
        command.Parameters.AddWithValue("after_due", NpgsqlDbType.TimestampTz, (object?)after?.DueAt ?? DBNull.Value);
        command.Parameters.AddWithValue("after_owner", NpgsqlDbType.Uuid, (object?)after?.OwnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("after_account", NpgsqlDbType.Uuid, (object?)after?.AccountId ?? DBNull.Value);
    }
}
