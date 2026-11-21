using System.Text.Json;
using Vessel.Application.Events;

namespace Vessel.Persistence.Events;

/// <summary>
/// PostgreSQL <c>LISTEN/NOTIFY</c> wake-ups. A notification is staged with <c>pg_notify</c> on the unit of work's own
/// transaction, so PostgreSQL delivers it only if the commit succeeds. It is a hint, never the record: the event row,
/// the receipt and the pending monitor request are, and catch-up or the scheduler tick recovers a lost notification.
/// </summary>
/// <remarks>
/// Channels are <c>vessel.{name}:{current_schema()}</c>, cut to PostgreSQL's 63-character limit, so two journals in
/// separate schemas of one database never wake each other. Publisher and listener must use the same connection
/// string (search path). Notifications do not pass through a transaction-pooling proxy such as PgBouncer.
/// </remarks>
public static class VesselNotifications
{
    /// <summary>Every appended event. The payload names the owner, the event and its type, nothing else.</summary>
    public const string Events = "events";
    /// <summary>Monitor requests. The payload is the <see cref="MonitorAccountCommand"/> as JSON.</summary>
    public const string MonitorCommands = "commands.monitor";

    /// <summary>The SQL expression of a channel name; <c>@channel</c> is the logical name.</summary>
    public const string ChannelSql = "left('vessel.' || @channel || ':' || current_schema(), 63)";

    public static string EventPayload(VesselEvent message) =>
        string.Join(' ', message.OwnerId.ToString("D"), message.Id.ToString("D"), message.Type);

    public static bool TryParseEvent(string payload, out Guid ownerId, out Guid eventId, out string type)
    {
        var parts = payload.Split(' ');
        type = parts.Length == 3 ? parts[2] : "";
        eventId = Guid.Empty;
        return Guid.TryParseExact(parts[0], "D", out ownerId) && parts.Length == 3 &&
            Guid.TryParseExact(parts[1], "D", out eventId) && VesselEventValues.Types.Contains(type);
    }

    public static string CommandPayload(MonitorAccountCommand command) => JsonSerializer.Serialize(command, VesselEventJson.Options);

    public static MonitorAccountCommand? ParseCommand(string payload)
    {
        try { return JsonSerializer.Deserialize<MonitorAccountCommand>(payload, VesselEventJson.Options); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The outbox: each staged event or command becomes a <c>pg_notify</c> on the unit's transaction. Nothing is sent
/// after commit; PostgreSQL delivers the notifications with it, or drops them with a rollback or savepoint rollback.
/// </summary>
internal sealed class PostgresNotificationOutbox(VesselDbContext db) : IVesselEventOutbox
{
    public Task StageAsync(VesselEvent message, CancellationToken cancellationToken) =>
        NotifyAsync(VesselNotifications.Events, VesselNotifications.EventPayload(message), cancellationToken);

    public Task StageAsync(MonitorAccountCommand command, CancellationToken cancellationToken) =>
        NotifyAsync(VesselNotifications.MonitorCommands, VesselNotifications.CommandPayload(command), cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Reset() { }

    private Task<int> NotifyAsync(string channel, string payload, CancellationToken cancellationToken) =>
        VesselEventSql.ExecuteAsync(db, $"SELECT pg_notify({VesselNotifications.ChannelSql}, @payload)", cancellationToken,
            ("channel", channel), ("payload", payload));
}
