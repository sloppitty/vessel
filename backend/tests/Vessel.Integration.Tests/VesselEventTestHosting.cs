using Microsoft.Extensions.Configuration;
using Vessel.Application.Events;
using Vessel.Persistence.Events;
using Microsoft.EntityFrameworkCore;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Reads committed history the way the browser does, resuming in the database's current epoch.</summary>
internal static class EventReads
{
    public static async Task<VesselEventPage> ReadAsync(VesselDbContext db, long after, int limit, CancellationToken ct)
    {
        Guid? epoch = null;
        if (after != 0)
        {
            await using var connection = new Npgsql.NpgsqlConnection(db.Database.GetConnectionString());
            await connection.OpenAsync(ct);
            await using var command = new Npgsql.NpgsqlCommand("SELECT epoch FROM event_cursors WHERE owner_id=@owner", connection);
            command.Parameters.AddWithValue("owner", db.CurrentOwnerId);
            epoch = await command.ExecuteScalarAsync(ct) as Guid?;
        }
        return await new VesselEventHistory(db).ReadAsync(VesselEventCursor.Format(epoch, after), limit, ct);
    }

    public static long Sequence(this VesselEventPage page) =>
        VesselEventCursor.TryParse(page.NextCursor, out var cursor) ? cursor.Sequence : -1;
}
