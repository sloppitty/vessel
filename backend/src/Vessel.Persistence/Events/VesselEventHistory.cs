using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Events;

namespace Vessel.Persistence.Events;

// Read/SSE paths never resolve the outbox, a listener or a mutation boundary.
public sealed class VesselEventHistory(VesselDbContext db) : IVesselEventReader
{
    public async Task<VesselEventPage> ReadAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (!VesselEventCursor.TryParse(cursor, out var position) || limit is < 1 or > 200)
            throw new ProblemException(400, "Invalid event cursor or limit.");
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // Cursor, floor, epoch and events come from ONE PostgreSQL statement snapshot, including concurrent pruning.
            await using var command = VesselEventSql.Command(db, """
                WITH cursor AS (
                    SELECT COALESCE(max(sequence),0) AS sequence, COALESCE(max(retention_floor),0) AS floor,
                           (array_agg(epoch))[1] AS epoch
                    FROM event_cursors WHERE owner_id=@owner
                )
                SELECT c.sequence,c.floor,c.epoch,e.sequence,e.payload::text
                FROM cursor c LEFT JOIN LATERAL (
                    SELECT sequence,payload FROM events
                    WHERE owner_id=@owner AND sequence>@after AND sequence>c.floor AND @after<=c.sequence
                      AND (@epoch IS NULL OR c.epoch=@epoch)
                    ORDER BY sequence LIMIT @limit
                ) e ON true ORDER BY e.sequence
                """, ("owner", db.CurrentOwnerId), ("after", position.Sequence), ("limit", limit),
                ("epoch", (object?)position.Epoch ?? DBNull.Value));
            ((Npgsql.NpgsqlParameter)command.Parameters["epoch"]).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
            await using var rows = await command.ExecuteReaderAsync(cancellationToken);
            var events = new List<VesselEvent>();
            long latest = 0, floor = 0, next = position.Sequence;
            Guid? epoch = null;
            while (await rows.ReadAsync(cancellationToken))
            {
                latest = rows.GetInt64(0); floor = rows.GetInt64(1);
                epoch = rows.IsDBNull(2) ? null : rows.GetGuid(2);
                if (rows.IsDBNull(3)) continue;
                next = rows.GetInt64(3);
                events.Add(JsonSerializer.Deserialize<VesselEvent>(rows.GetString(4), VesselEventJson.Options)!);
            }
            // "0" (no epoch) starts at the oldest retained event, so pruning never resets it. Any other epoch,
            // a pruned or a future cursor needs a state reload.
            var start = position.Epoch is null && position.Sequence == 0;
            var reset = position.Epoch is null ? !start : position.Epoch != epoch;
            reset |= (!start && position.Sequence < floor) || position.Sequence > latest;
            if (start && events.Count == 0) next = Math.Max(next, floor);
            return new(reset ? [] : events, VesselEventCursor.Format(epoch, reset ? latest : next), reset);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
