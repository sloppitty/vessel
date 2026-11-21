using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Persistence.Events.Subscriptions;

namespace Vessel.Messaging.Modules;

/// <summary>
/// A feature module's messaging declaration. Any host may request a module's commands; only a host that runs the
/// module listens for them and for its subscriptions, and adds its handlers and hosted services. Provisioning
/// registers every known module's subscriptions, whichever modules a deployment runs.
/// </summary>
public interface IVesselModule
{
    /// <summary>Module name; also the default logical deployment of a worker that runs it.</summary>
    string Name { get; }
    /// <summary>Point-to-point commands this module consumes.</summary>
    IReadOnlyList<CommandQueue> Commands { get; }
    /// <summary>Event subscriptions this module owns. Their handlers are registered by <see cref="AddRunningServices"/>.</summary>
    IReadOnlyList<EventSubscription> Subscriptions { get; }
    /// <summary>Services needed only by a host that runs this module, such as keyed subscription handlers.</summary>
    void AddRunningServices(IServiceCollection services, IConfiguration configuration);
}

/// <summary>
/// A command a module consumes: the notification channel <c>commands.{Name}</c> and the message type sent on it. The
/// durable request lives in PostgreSQL; the notification only wakes the module. A host that runs the module listens
/// with <paramref name="Handler"/>, an <see cref="INotificationHandler"/> this project adapts to an Application port.
/// </summary>
public sealed record CommandQueue(string Name, Type MessageType, Type? Handler = null)
{
    public string Channel => "commands." + Name;
}

/// <summary>Handles the payloads of one notification channel, one at a time. It must not throw for a bad payload.</summary>
public interface INotificationHandler
{
    Task HandleAsync(string payload, CancellationToken cancellationToken);
}
