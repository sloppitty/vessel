using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Events;
using Vessel.Application.Notifications;
using Vessel.Persistence.Events.Subscriptions;
using Vessel.Persistence.Notifications;

namespace Vessel.Messaging.Modules.Notifications;

/// <summary>
/// The Notification Hub: one subscription, <c>notifications</c>, to every event type; its rules decide what notifies,
/// and unknown types are skipped, so adding a category never needs a new subscription name. The in-app channel is
/// written in the receipt's own transaction, so the module consumes no command.
/// </summary>
public sealed class NotificationsModule : IVesselModule
{
    public const string ModuleName = NotificationValues.Subscription;
    public string Name => ModuleName;
    public IReadOnlyList<CommandQueue> Commands { get; } = [];
    public IReadOnlyList<EventSubscription> Subscriptions { get; } = [new(NotificationValues.Subscription, ModuleName, ["#"])];

    public void AddRunningServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddVesselNotifications(configuration);
        services.AddKeyedScoped<IVesselEventSubscriber, NotificationHub>(NotificationValues.Subscription);
    }
}
