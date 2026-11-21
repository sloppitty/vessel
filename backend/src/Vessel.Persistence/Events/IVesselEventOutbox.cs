using Vessel.Application.Events;

namespace Vessel.Persistence.Events;

/// <summary>
/// The outbox at the unit-of-work boundary. Staging runs on the unit's own transaction (by default a
/// <c>pg_notify</c>, see <see cref="VesselNotifications"/>), so a signal exists only if the commit succeeds. Neither
/// Application nor Domain knows the transport.
/// </summary>
public interface IVesselEventOutbox
{
    /// <summary>Signal one appended event; the outbox knows nothing about subscribers.</summary>
    Task StageAsync(VesselEvent message, CancellationToken cancellationToken);
    Task StageAsync(MonitorAccountCommand command, CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
    void Reset();
}
