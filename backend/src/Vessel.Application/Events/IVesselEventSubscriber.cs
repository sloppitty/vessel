namespace Vessel.Application.Events;

public enum EventHandling
{
    /// <summary>The handler's effects are complete.</summary>
    Delivered,
    /// <summary>The handler deliberately did nothing, for example an event no longer relevant at delivery.</summary>
    Skipped
}

/// <summary>
/// A subscription's handler. It runs inside the dispatcher's database transaction, so its effects must be
/// database-only and idempotent on the event ID; the receipt settles with them. It never sees transport types.
/// Throw <see cref="TerminalEventException"/> for a permanent failure; any other exception is retried.
/// </summary>
public interface IVesselEventSubscriber
{
    Task<EventHandling> HandleAsync(VesselEvent message, CancellationToken cancellationToken);
}

/// <summary>A permanent subscription failure. The message is safe, server-authored text.</summary>
public sealed class TerminalEventException(string message) : Exception(message);

/// <summary>A module's command handler, behind the command queue listener.</summary>
public interface IVesselCommandHandler<in TCommand>
{
    Task HandleAsync(TCommand command, CancellationToken cancellationToken);
}
