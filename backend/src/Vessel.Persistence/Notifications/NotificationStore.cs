using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Notifications;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Persistence.Events;

namespace Vessel.Persistence.Notifications;

/// <summary>
/// Notification storage with explicit owner predicates, like the event tables. Every delivery state change takes
/// the owner's <c>notification_cursors</c> row first (held to commit), so the feed sequence, unread count and reset
/// version change together and in commit order. Popup claims change only <c>shown_at</c> and need no cursor.
/// </summary>
public sealed class NotificationStore(VesselDbContext db, TimeProvider time, NotificationWorkerState workerState) : INotificationHubStore, INotificationStore
{
    private Guid Owner => db.CurrentOwnerId;
    private const string Delivery = """
        n.notification_id,n.kind,n.category,n.severity,n.content::text,n.occurred_at,n.detected_at,n.created_at,d.state,d.read_at
        """;

    // ---- Hub ----------------------------------------------------------------------------------------------------

    public Task LockAsync(string key, CancellationToken ct) => VesselEventSql.ParticipateAsync(db, () =>
        VesselEventSql.ExecuteAsync(db, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", ct,
            ("key", $"{Owner:N}:{key}")), ct);

    public Task<bool> HandledAsync(Guid eventId, CancellationToken ct) => VesselEventSql.ParticipateAsync(db, async () =>
        await VesselEventSql.ScalarAsync(db, "SELECT 1 FROM notification_sources WHERE owner_id=@owner AND event_id=@id",
            ct, ("owner", Owner), ("id", eventId)) is not null, ct);

    public async Task<NotificationPreferences> PreferencesAsync(CancellationToken ct)
    {
        await using var reader = await ReaderAsync("""
            SELECT enabled,minimum_severity,categories,revision FROM notification_preferences
            WHERE owner_id=@owner AND channel='in-app'
            """, ct, P("owner", Owner));
        if (!await reader.Rows.ReadAsync(ct)) return NotificationPreferences.Defaults;
        return new(reader.Rows.GetBoolean(0), reader.Rows.GetString(1), reader.Rows.GetFieldValue<string[]>(2), reader.Rows.GetInt64(3));
    }

    public Task<IReadOnlyList<VesselEvent>> RunCandidatesAsync(Guid runId, Guid? accountId, long afterSequence, int limit, CancellationToken ct) =>
        VesselEventSql.ParticipateAsync(db, async () =>
        {
            await using var command = Command("""
                SELECT e.payload::text FROM events e
                JOIN event_cursors c ON c.owner_id=e.owner_id
                JOIN event_subscriptions s ON s.owner_id=e.owner_id AND s.name=@subscription
                WHERE e.owner_id=@owner AND e.payload->>'runId'=@run AND e.account_id IS NOT DISTINCT FROM @account
                  AND e.sequence>c.retention_floor AND e.sequence>s.responsible_from AND e.sequence>@after
                  AND NOT EXISTS (SELECT 1 FROM notification_sources x WHERE x.owner_id=e.owner_id AND x.event_id=e.event_id)
                ORDER BY e.sequence LIMIT @limit
                """, P("owner", Owner), P("subscription", NotificationValues.Subscription), P("run", runId.ToString("D")),
                P("account", accountId, NpgsqlDbType.Uuid), P("after", afterSequence), P("limit", limit));
            await using var rows = await command.ExecuteReaderAsync(ct);
            var events = new List<VesselEvent>();
            while (await rows.ReadAsync(ct))
                events.Add(JsonSerializer.Deserialize<VesselEvent>(rows.GetString(0), VesselEventJson.Options)!);
            return (IReadOnlyList<VesselEvent>)events;
        }, ct);

    public async Task<NotificationContext?> ContextAsync(Guid? accountId, IReadOnlyCollection<Guid> playIds, CancellationToken ct)
    {
        string? name = null, venue = null;
        if (accountId is { } id)
        {
            var account = await db.Accounts.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.Name, a.VenueId }).SingleOrDefaultAsync(ct);
            if (account is null) return null;
            (name, venue) = (account.Name, account.VenueId);
        }
        var plays = playIds.Count == 0 ? [] : await db.Plays.AsNoTracking()
            .Where(p => playIds.Contains(p.Id) && (accountId == null || p.AccountId == accountId))
            .Select(p => new NotificationPlay(p.Id, p.Title, p.ContractId)).ToListAsync(ct);
        return new(accountId, name, venue, plays.ToDictionary(p => p.Id));
    }

    public async Task<bool> LinkActionableAsync(Guid linkId, CancellationToken ct)
    {
        var link = await db.OrderLinks.AsNoTracking().Where(l => l.Id == linkId)
            .Select(l => new { l.State, l.ImportedOrderId, l.PlayId }).SingleOrDefaultAsync(ct);
        if (link is not { State: OrderLinkState.Suggested }) return false;
        var status = await db.Plays.AsNoTracking().Where(p => p.Id == link.PlayId).Select(p => (PlayStatus?)p.Status).SingleOrDefaultAsync(ct);
        if (status is not (PlayStatus.Planned or PlayStatus.Paused or PlayStatus.Open)) return false;
        // A suggestion stays stored while another link of its order is confirmed, but it is hidden and not actionable.
        return !await db.OrderLinks.AsNoTracking().AnyAsync(l => l.ImportedOrderId == link.ImportedOrderId &&
            l.State == OrderLinkState.Linked, ct);
    }

    public async Task<NotificationSubjectState> SubjectStateAsync(NotificationIdentity identity, CancellationToken ct)
    {
        switch (identity.Kind)
        {
            case NotificationKind.Discrepancy:
                var discrepancy = await db.Set<PlayDiscrepancy>().AsNoTracking().Where(d => d.Id == identity.SubjectId)
                    .Select(d => new { d.PublishedRevision }).SingleOrDefaultAsync(ct);
                return new(discrepancy is not null, discrepancy?.PublishedRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case NotificationKind.Monitor:
                var monitor = await db.Set<MonitorAccount>().AsNoTracking().Where(m => m.AccountId == identity.SubjectId)
                    .Select(m => new { m.StateRevision }).SingleOrDefaultAsync(ct);
                return new(monitor is not null, monitor?.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case NotificationKind.Credential:
                // Metadata only: the sealed token is hashed for its revision, never opened.
                var venue = await db.Accounts.AsNoTracking().Where(a => a.Id == identity.SubjectId).Select(a => a.VenueId).SingleOrDefaultAsync(ct);
                if (venue is null) return new(false, null);
                var credential = await db.AccountCredentials.AsNoTracking()
                    .SingleOrDefaultAsync(c => c.AccountId == identity.SubjectId && c.Purpose == venue + "-read-token", ct);
                return credential is null ? new(false, null) : new(true, CredentialAttempt.RevisionOf(credential), credential.ExpiresAt);
            default:
                return new(false, null);
        }
    }

    public Task<IReadOnlyList<NotificationSubjectSource>> SubjectSourcesAsync(string subject, CancellationToken ct) =>
        VesselEventSql.ParticipateAsync(db, async () =>
        {
            // Everything from the subject's last closing generation on, and anything still unread. Older generations
            // were settled by that closing event, so the read stays bounded however long the subject's history is.
            await using var command = Command("""
                WITH closing AS (
                    SELECT COALESCE(max(generation), -1) AS generation FROM notification_sources
                    WHERE owner_id=@owner AND subject=@subject AND transition IN ('resolved','dismissed','live','idle')
                )
                SELECT s.event_id,s.notification_id,s.generation,s.revision,s.transition,d.state
                FROM notification_sources s CROSS JOIN closing c
                LEFT JOIN notification_deliveries d ON d.owner_id=s.owner_id AND d.notification_id=s.notification_id AND d.channel='in-app'
                WHERE s.owner_id=@owner AND s.subject=@subject AND (s.generation>=c.generation OR d.state='unread')
                ORDER BY s.generation DESC LIMIT 200
                """, P("owner", Owner), P("subject", subject));
            await using var rows = await command.ExecuteReaderAsync(ct);
            var sources = new List<NotificationSubjectSource>();
            while (await rows.ReadAsync(ct))
                sources.Add(new(rows.GetGuid(0), rows.IsDBNull(1) ? null : rows.GetGuid(1), rows.GetInt64(2),
                    rows.IsDBNull(3) ? null : rows.GetString(3), rows.IsDBNull(4) ? null : rows.GetString(4),
                    rows.IsDBNull(5) ? null : rows.GetString(5)));
            return (IReadOnlyList<NotificationSubjectSource>)sources;
        }, ct);

    public Task<int> WithdrawAsync(IReadOnlyCollection<Guid> notificationIds, CancellationToken ct) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        await LockCursorAsync(db, ct);
        return await TransitionAsync(db, "d.notification_id = ANY(@ids)", NotificationValues.Withdrawn, time.GetUtcNow(), ct,
            P("ids", notificationIds.ToArray()));
    }, ct);

    public Task<bool> CreateAsync(NewNotification notification, CancellationToken ct) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        await LockCursorAsync(db, ct);
        // Rechecked after the cursor lock: a disable that committed first is seen here, and one still waiting for
        // this lock withdraws what this transaction creates.
        if (notification.AccountId is { } account && await VesselEventSql.ScalarAsync(db,
                """SELECT "IsEnabled" FROM accounts WHERE "OwnerId"=@owner AND "Id"=@account""", ct,
                ("owner", Owner), ("account", account)) is not true)
        {
            await InsertSourcesAsync(null, notification.Sources, ct);
            return false;
        }
        var now = time.GetUtcNow();
        var sequence = (long)(await VesselEventSql.ScalarAsync(db, """
            UPDATE notification_cursors SET sequence=sequence+1,unread_count=unread_count+1 WHERE owner_id=@owner RETURNING sequence
            """, ct, ("owner", Owner)))!;
        await using (var insert = Command("""
            INSERT INTO notifications(owner_id,notification_id,created_sequence,account_id,play_id,run_id,kind,category,severity,
                                      content,occurred_at,detected_at,created_at)
            VALUES(@owner,@id,@sequence,@account,@play,@run,@kind,@category,@severity,CAST(@content AS jsonb),@occurred,@detected,@now);
            INSERT INTO notification_deliveries(owner_id,notification_id,channel,state,state_at,feed_sequence)
            VALUES(@owner,@id,'in-app','unread',@now,@sequence);
            """, P("owner", Owner), P("id", notification.Id), P("sequence", sequence),
            P("account", notification.AccountId, NpgsqlDbType.Uuid), P("play", notification.PlayId, NpgsqlDbType.Uuid),
            P("run", notification.RunId, NpgsqlDbType.Uuid), P("kind", Kind(notification.Kind)), P("category", notification.Category),
            P("severity", notification.Severity), P("content", JsonSerializer.Serialize(notification.Content, VesselEventJson.Options)),
            P("occurred", notification.OccurredAt.ToUniversalTime()), P("detected", notification.DetectedAt.ToUniversalTime()), P("now", now)))
        {
            VesselEventSql.RequireTransaction(db);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await InsertSourcesAsync(notification.Id, notification.Sources, ct);
        return true;
    }, ct);

    public Task RecordHandledAsync(IReadOnlyList<NotificationSource> sources, CancellationToken ct) =>
        VesselEventSql.ParticipateAsync(db, () => InsertSourcesAsync(null, sources, ct), ct);

    private async Task InsertSourcesAsync(Guid? notification, IReadOnlyList<NotificationSource> sources, CancellationToken ct)
    {
        foreach (var source in sources)
        {
            await using var command = Command("""
                INSERT INTO notification_sources(owner_id,event_id,notification_id,event_type,account_id,subject,generation,revision,transition,handled_at)
                VALUES(@owner,@event,@notification,@type,@account,@subject,@generation,@revision,@transition,@now)
                ON CONFLICT (owner_id,event_id) DO NOTHING
                """, P("owner", Owner), P("event", source.EventId), P("notification", notification, NpgsqlDbType.Uuid),
                P("type", source.EventType), P("account", source.AccountId, NpgsqlDbType.Uuid),
                P("subject", source.Subject, NpgsqlDbType.Varchar), P("generation", source.Generation, NpgsqlDbType.Bigint),
                P("revision", source.Revision, NpgsqlDbType.Varchar), P("transition", source.Transition, NpgsqlDbType.Varchar),
                P("now", time.GetUtcNow()));
            VesselEventSql.RequireTransaction(db);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    // ---- Owner reads and actions --------------------------------------------------------------------------------

    private const string HeadSql = """
        SELECT COALESCE((SELECT sequence FROM notification_cursors WHERE owner_id=@owner),0) AS sequence,
               COALESCE((SELECT unread_count FROM notification_cursors WHERE owner_id=@owner),0) AS unread,
               COALESCE((SELECT reset_version FROM notification_cursors WHERE owner_id=@owner),0) AS reset,
               (SELECT epoch FROM event_cursors WHERE owner_id=@owner) AS epoch,
               COALESCE((SELECT enabled FROM notification_preferences WHERE owner_id=@owner AND channel='in-app'),true) AS enabled
        """;

    public async Task<NotificationHead> HeadAsync(CancellationToken ct)
    {
        await using var reader = await ReaderAsync(HeadSql, ct, P("owner", Owner));
        await reader.Rows.ReadAsync(ct);
        var head = ReadHead(reader.Rows);
        return new(NotificationCursor.Format(head.Epoch, head.Reset, head.Sequence), head.Unread, head.Enabled);
    }

    public async Task<NotificationFeed> FeedAsync(string? after, int limit, CancellationToken ct)
    {
        var valid = NotificationCursor.TryParse(after, out var cursor);
        // Head, count, reset version, epoch and rows come from ONE statement snapshot.
        await using var reader = await ReaderAsync($"""
            WITH head AS ({HeadSql})
            SELECT h.sequence,h.unread,h.reset,h.epoch,h.enabled,r.feed_sequence,r.notification_id,r.kind,r.category,r.severity,
                   r.content,r.occurred_at,r.detected_at,r.created_at,r.state,r.read_at
            FROM head h LEFT JOIN LATERAL (
                SELECT d.feed_sequence,{Delivery}
                FROM notification_deliveries d JOIN notifications n ON n.owner_id=d.owner_id AND n.notification_id=d.notification_id
                WHERE d.owner_id=@owner AND d.channel='in-app' AND d.feed_sequence>@after AND @valid
                  AND h.epoch IS NOT DISTINCT FROM @epoch AND h.reset=@reset AND @after<=h.sequence
                ORDER BY d.feed_sequence LIMIT @limit
            ) r ON true ORDER BY r.feed_sequence
            """, ct, P("owner", Owner), P("after", cursor.Sequence), P("valid", valid), P("epoch", cursor.Epoch, NpgsqlDbType.Uuid),
            P("reset", cursor.Reset), P("limit", limit));
        var items = new List<NotificationFeedItem>();
        (long Sequence, int Unread, long Reset, Guid? Epoch, bool Enabled) head = default;
        while (await reader.Rows.ReadAsync(ct))
        {
            head = ReadHead(reader.Rows);
            if (reader.Rows.IsDBNull(5)) continue;
            items.Add(new(NotificationCursor.Format(head.Epoch, head.Reset, reader.Rows.GetInt64(5)), ReadDto(reader.Rows, 6)));
        }
        var reset = !valid || cursor.Epoch != head.Epoch || cursor.Reset != head.Reset || cursor.Sequence > head.Sequence;
        var next = reset ? NotificationCursor.Format(head.Epoch, head.Reset, head.Sequence)
            : items.Count > 0 ? items[^1].Cursor : NotificationCursor.Format(head.Epoch, head.Reset, cursor.Sequence);
        return new(reset ? [] : items, next, head.Unread, head.Enabled, reset);
    }

    public async Task<NotificationPage> PageAsync(NotificationPageQuery query, CancellationToken ct)
    {
        const string visible = """
            FROM notifications n JOIN notification_deliveries d ON d.owner_id=n.owner_id AND d.notification_id=n.notification_id AND d.channel='in-app'
            LEFT JOIN accounts a ON a."OwnerId"=n.owner_id AND a."Id"=n.account_id
            WHERE n.owner_id=@owner AND (n.account_id IS NULL OR a."IsEnabled")
              AND (@category='' OR n.category=@category) AND (@severity='' OR n.severity=@severity)
            """;
        var parameters = () => new[] { P("owner", Owner), P("category", query.Category ?? ""), P("severity", query.Severity ?? "") };
        int unread, all;
        await using (var counts = await ReaderAsync($"SELECT count(*) FILTER (WHERE d.state='unread'),count(*) {visible}", ct, parameters()))
        {
            await counts.Rows.ReadAsync(ct);
            (unread, all) = ((int)counts.Rows.GetInt64(0), (int)counts.Rows.GetInt64(1));
        }
        var total = query.Tab == "unread" ? unread : all;
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (total + query.PageSize - 1) / query.PageSize));
        await using var reader = await ReaderAsync($"""
            SELECT {Delivery} {visible} AND (@tab='all' OR d.state='unread')
            ORDER BY n.created_sequence DESC OFFSET @offset LIMIT @limit
            """, ct, [.. parameters(), P("tab", query.Tab), P("offset", (page - 1) * query.PageSize), P("limit", query.PageSize)]);
        var items = new List<NotificationDto>();
        while (await reader.Rows.ReadAsync(ct)) items.Add(ReadDto(reader.Rows, 0));
        return new(items, page, query.PageSize, total, unread, all);
    }

    public Task<NotificationClaim> ClaimAsync(DateTimeOffset now, TimeSpan window, CancellationToken ct) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        if (!(await PreferencesAsync(ct)).Enabled) return new NotificationClaim([], 0);
        // Row locks make the claim atomic: a concurrent claim waits, then finds shown_at set and takes nothing.
        await using var command = Command($"""
            WITH claimed AS (
                UPDATE notification_deliveries d SET shown_at=@now FROM notifications n
                WHERE d.owner_id=@owner AND d.channel='in-app' AND d.state='unread' AND d.shown_at IS NULL
                  AND n.owner_id=d.owner_id AND n.notification_id=d.notification_id
                  AND (n.account_id IS NULL OR EXISTS (
                      SELECT 1 FROM accounts a WHERE a."OwnerId"=n.owner_id AND a."Id"=n.account_id AND a."IsEnabled"))
                RETURNING {Delivery},n.created_sequence
            ) SELECT * FROM claimed
            """, P("owner", Owner), P("now", now));
        VesselEventSql.RequireTransaction(db);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var claimed = new List<(NotificationDto Dto, long Sequence)>();
        while (await rows.ReadAsync(ct)) claimed.Add((ReadDto(rows, 0), rows.GetInt64(10)));
        var popups = claimed.Where(c => c.Dto.CreatedAt >= now - window)
            .OrderByDescending(c => NotificationValues.Rank(c.Dto.Severity)).ThenByDescending(c => c.Sequence)
            .Take(3).Select(c => c.Dto).ToList();
        return new NotificationClaim(popups, claimed.Count - popups.Count);
    }, ct);

    public Task<NotificationDto?> ReadAsync(Guid id, DateTimeOffset now, CancellationToken ct) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        await LockCursorAsync(db, ct);
        await TransitionAsync(db, "d.notification_id = @id", NotificationValues.Read, now, ct, P("id", id));
        await using var command = Command($"""
            SELECT {Delivery} FROM notifications n
            JOIN notification_deliveries d ON d.owner_id=n.owner_id AND d.notification_id=n.notification_id AND d.channel='in-app'
            WHERE n.owner_id=@owner AND n.notification_id=@id
            """, P("owner", Owner), P("id", id));
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? ReadDto(rows, 0) : null;
    }, ct);

    public Task<int> ReadAllAsync(NotificationCursor upTo, DateTimeOffset now, CancellationToken ct) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        await LockCursorAsync(db, ct);
        await using (var reader = await ReaderAsync(HeadSql, ct, P("owner", Owner)))
        {
            await reader.Rows.ReadAsync(ct);
            var head = ReadHead(reader.Rows);
            if (upTo.Epoch != head.Epoch || upTo.Sequence > head.Sequence)
                throw new ProblemException(409, "Notifications changed. Reload them before marking all as read.");
        }
        // Created sequences never change, so every notification that existed at the reader's head is included.
        return await TransitionAsync(db, "n.created_sequence <= @upTo", NotificationValues.Read, now, ct, P("upTo", upTo.Sequence));
    }, ct);

    public Task<NotificationPreferences> SavePreferencesAsync(NotificationPreferences preferences, long expectedRevision, DateTimeOffset now,
        CancellationToken ct) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        await LockCursorAsync(db, ct);
        var current = (await PreferencesAsync(ct)).Revision;
        if (current != expectedRevision) throw new ProblemException(409, "Notification preferences changed. Reload them before saving.");
        await VesselEventSql.ExecuteAsync(db, """
            INSERT INTO notification_preferences(owner_id,channel,enabled,minimum_severity,categories,revision,updated_at)
            VALUES(@owner,'in-app',@enabled,@minimum,@categories,@revision,@now)
            ON CONFLICT (owner_id,channel) DO UPDATE SET enabled=EXCLUDED.enabled,minimum_severity=EXCLUDED.minimum_severity,
                categories=EXCLUDED.categories,revision=EXCLUDED.revision,updated_at=EXCLUDED.updated_at;
            UPDATE notification_cursors SET reset_version=reset_version+1 WHERE owner_id=@owner;
            """, ct, ("owner", Owner), ("enabled", preferences.Enabled), ("minimum", preferences.MinimumSeverity),
            ("categories", preferences.Categories.ToArray()), ("revision", expectedRevision + 1), ("now", now));
        return preferences with { Revision = expectedRevision + 1 };
    }, ct);

    public Task<TimeSpan?> StartTestAsync(DateTimeOffset now, TimeSpan cooldown, CancellationToken ct) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        await LockCursorAsync(db, ct);
        if (await VesselEventSql.ScalarAsync(db, "SELECT last_test_at FROM notification_cursors WHERE owner_id=@owner", ct,
                ("owner", Owner)) is DateTime last && new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) + cooldown is var allowed && allowed > now)
            return (TimeSpan?)(allowed - now);
        await VesselEventSql.ExecuteAsync(db, "UPDATE notification_cursors SET last_test_at=@now WHERE owner_id=@owner", ct,
            ("owner", Owner), ("now", now));
        return null;
    }, ct);

    public Task<int> RetainAsync(DateTimeOffset cutoff, CancellationToken ct) => VesselEventSql.ParticipateAsync(db, async () =>
    {
        // Retention never creates progress. Source-only owners can still age out handled events. A sweep is long on
        // purpose (the notifications delete is unbatched), so the statement limit is lifted for this unit.
        await VesselEventSql.LiftStatementLimitAsync(db, ct);
        await VesselEventSql.ScalarAsync(db, "SELECT 1 FROM notification_cursors WHERE owner_id=@owner FOR UPDATE",
            ct, ("owner", Owner));
        var deleted = await DeleteNotificationsAsync(db, "n.created_at < @cutoff", ct, P("cutoff", cutoff));
        await VesselEventSql.ExecuteAsync(db, """
            DELETE FROM notification_sources WHERE owner_id=@owner AND notification_id IS NULL AND event_id IN (
                SELECT event_id FROM notification_sources WHERE owner_id=@owner AND notification_id IS NULL AND handled_at<@cutoff LIMIT 1000)
            """, ct, ("owner", Owner), ("cutoff", cutoff));
        return deleted;
    }, ct);

    public Task<DateTimeOffset?> HeartbeatAsync(CancellationToken ct) => workerState.HeartbeatAsync(ct);

    public async Task<NotificationSubscriptionHealth> SubscriptionHealthAsync(CancellationToken ct)
    {
        await using var reader = await ReaderAsync("""
            SELECT s.status,(SELECT count(*) FROM event_receipts r
                             WHERE r.owner_id=s.owner_id AND r.subscription=s.name AND r.state='failed')
            FROM event_subscriptions s WHERE s.owner_id=@owner AND s.name=@subscription
            """, ct, P("owner", Owner), P("subscription", NotificationValues.Subscription));
        return await reader.Rows.ReadAsync(ct)
            ? new(reader.Rows.GetString(0), (int)reader.Rows.GetInt64(1))
            : new(null, 0);
    }

    // ---- Shared with account management -------------------------------------------------------------------------

    /// <summary>Takes the owner's notification cursor row (created if absent), held until the transaction ends.</summary>
    internal static async Task LockCursorAsync(VesselDbContext db, CancellationToken ct)
    {
        await VesselEventSql.ExecuteAsync(db, "INSERT INTO notification_cursors(owner_id) VALUES(@owner) ON CONFLICT DO NOTHING",
            ct, ("owner", db.CurrentOwnerId));
        await VesselEventSql.ScalarAsync(db, "SELECT 1 FROM notification_cursors WHERE owner_id=@owner FOR UPDATE",
            ct, ("owner", db.CurrentOwnerId));
    }

    /// <summary>
    /// Moves matching unread in-app deliveries to <paramref name="state"/>, each with the next feed sequence, and
    /// lowers the unread count by the rows actually moved. The caller holds the cursor lock.
    /// </summary>
    internal static async Task<int> TransitionAsync(VesselDbContext db, string predicate, string state, DateTimeOffset now,
        CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var command = Command(db, $"""
            WITH target AS (
                SELECT d.notification_id,row_number() OVER (ORDER BY n.created_sequence) AS position
                FROM notification_deliveries d JOIN notifications n ON n.owner_id=d.owner_id AND n.notification_id=d.notification_id
                WHERE d.owner_id=@owner AND d.channel='in-app' AND d.state='unread' AND ({predicate})
            ), moved AS (
                UPDATE notification_deliveries d SET state=@state,state_at=@now,
                       read_at=CASE WHEN @state='read' THEN @now ELSE d.read_at END,
                       feed_sequence=c.sequence+t.position
                FROM target t, notification_cursors c
                WHERE d.owner_id=@owner AND d.channel='in-app' AND d.notification_id=t.notification_id AND c.owner_id=@owner
                RETURNING 1
            )
            UPDATE notification_cursors SET sequence=sequence+(SELECT count(*) FROM moved),
                   unread_count=unread_count-(SELECT count(*) FROM moved)
            WHERE owner_id=@owner RETURNING (SELECT count(*) FROM moved)
            """, [P("owner", db.CurrentOwnerId), P("state", state), P("now", now), .. parameters]);
        VesselEventSql.RequireTransaction(db);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct) ?? 0L);
    }

    /// <summary>Deletes matching notifications (sources and deliveries cascade), adjusting the unread count; bumps the reset version.</summary>
    internal static async Task<int> DeleteNotificationsAsync(VesselDbContext db, string predicate, CancellationToken ct,
        params NpgsqlParameter[] parameters)
    {
        await using var command = Command(db, $"""
            WITH doomed AS (
                SELECT n.notification_id FROM notifications n WHERE n.owner_id=@owner AND ({predicate})
            ), unread AS (
                SELECT count(*) AS count FROM notification_deliveries d JOIN doomed USING (notification_id)
                WHERE d.owner_id=@owner AND d.channel='in-app' AND d.state='unread'
            ), deleted AS (
                DELETE FROM notifications n USING doomed WHERE n.owner_id=@owner AND n.notification_id=doomed.notification_id RETURNING 1
            )
            UPDATE notification_cursors SET unread_count=unread_count-(SELECT count FROM unread),
                   reset_version=reset_version+CASE WHEN EXISTS (SELECT 1 FROM deleted) THEN 1 ELSE 0 END
            WHERE owner_id=@owner RETURNING (SELECT count(*) FROM deleted)
            """, [P("owner", db.CurrentOwnerId), .. parameters]);
        VesselEventSql.RequireTransaction(db);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct) ?? 0L);
    }

    /// <summary>
    /// Disabling an account withdraws its unread notifications and hides them; enabling shows them again, still
    /// withdrawn. Either way open readers reload. Runs under the account management locks, then the cursor.
    /// </summary>
    public static Task AccountVisibilityChangedAsync(VesselDbContext db, Guid accountId, bool enabled, DateTimeOffset now, CancellationToken ct) =>
        VesselEventSql.ParticipateAsync(db, async () =>
        {
            await LockCursorAsync(db, ct);
            if (!enabled)
                await TransitionAsync(db, "n.account_id = @account", NotificationValues.Withdrawn, now, ct, P("account", accountId));
            await VesselEventSql.ExecuteAsync(db, "UPDATE notification_cursors SET reset_version=reset_version+1 WHERE owner_id=@owner",
                ct, ("owner", db.CurrentOwnerId));
        }, ct);

    /// <summary>
    /// Removes the account's notifications and handled sources. Account deletion calls it after deleting the
    /// account's events (which waits for in-flight receipts), so it never holds the cursor while a dispatcher waits.
    /// </summary>
    public static Task AccountDeletedAsync(VesselDbContext db, Guid accountId, CancellationToken ct) =>
        VesselEventSql.ParticipateAsync(db, async () =>
        {
            await LockCursorAsync(db, ct);
            await DeleteNotificationsAsync(db, "n.account_id = @account", ct, P("account", accountId));
            await VesselEventSql.ExecuteAsync(db, "DELETE FROM notification_sources WHERE owner_id=@owner AND account_id=@account",
                ct, ("owner", db.CurrentOwnerId), ("account", accountId));
            await VesselEventSql.ExecuteAsync(db, "UPDATE notification_cursors SET reset_version=reset_version+1 WHERE owner_id=@owner",
                ct, ("owner", db.CurrentOwnerId));
        }, ct);

    // ---- Plumbing -----------------------------------------------------------------------------------------------

    private static (long Sequence, int Unread, long Reset, Guid? Epoch, bool Enabled) ReadHead(DbDataReader row) =>
        (row.GetInt64(0), row.GetInt32(1), row.GetInt64(2), row.IsDBNull(3) ? null : row.GetGuid(3), row.GetBoolean(4));

    private static NotificationDto ReadDto(DbDataReader row, int at) => new(row.GetGuid(at), row.GetString(at + 1), row.GetString(at + 2),
        row.GetString(at + 3), JsonSerializer.Deserialize<NotificationContent>(row.GetString(at + 4), VesselEventJson.Options)!,
        row.GetFieldValue<DateTimeOffset>(at + 5), row.GetFieldValue<DateTimeOffset>(at + 6), row.GetFieldValue<DateTimeOffset>(at + 7),
        row.GetString(at + 8), row.IsDBNull(at + 9) ? null : row.GetFieldValue<DateTimeOffset>(at + 9));

    private static string Kind(NotificationKind kind) => kind switch
    {
        NotificationKind.Run => "run",
        NotificationKind.Discrepancy => "discrepancy",
        NotificationKind.Monitor => "monitor",
        NotificationKind.Credential => "credential",
        _ => "test"
    };

    private static NpgsqlParameter P(string name, object? value, NpgsqlDbType? type = null)
    {
        var parameter = new NpgsqlParameter(name, value ?? DBNull.Value);
        if (type is { } known) parameter.NpgsqlDbType = known;
        return parameter;
    }

    private DbCommand Command(string sql, params NpgsqlParameter[] parameters) => Command(db, sql, parameters);

    private static DbCommand Command(VesselDbContext db, string sql, NpgsqlParameter[] parameters)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return command;
    }

    /// <summary>A read on the current transaction when there is one, otherwise on a connection opened for it.</summary>
    private async Task<OpenReader> ReaderAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var command = Command(sql, parameters);
            return new(db, command, await command.ExecuteReaderAsync(ct));
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    private sealed class OpenReader(VesselDbContext db, DbCommand command, DbDataReader rows) : IAsyncDisposable
    {
        public DbDataReader Rows { get; } = rows;
        public async ValueTask DisposeAsync()
        {
            await Rows.DisposeAsync();
            await command.DisposeAsync();
            await db.Database.CloseConnectionAsync();
        }
    }
}
