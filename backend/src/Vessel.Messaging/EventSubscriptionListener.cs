using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Messaging.Modules;
using Vessel.Persistence.Events;
using Vessel.Persistence.Events.Subscriptions;

namespace Vessel.Messaging;

/// <summary>
/// Live delivery: handles an <c>events</c> notification for every subscription this host runs whose filter matches the
/// event type, through the same dispatcher as catch-up. The notification is only a hint (owner, event ID and type);
/// the dispatcher reads the event from PostgreSQL and settles or records the attempt in its receipt. Terminal
/// failures, invalid owners and unknown events are logged. Catch-up owns retries, so a failure here is logged and
/// left to it.
/// </summary>
public sealed class EventSubscriptionListener(MessagingHost host, IOwnerScopeFactory scopes, IJournalOwnerDirectory owners,
    ILogger<EventSubscriptionHandlerLog> logger) : INotificationHandler
{
    /// <summary>Handles the notification that appending <paramref name="message"/> staged.</summary>
    public Task HandleAsync(VesselEvent message, CancellationToken cancellationToken) =>
        HandleAsync(VesselNotifications.EventPayload(message), cancellationToken);

    public async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        if (!VesselNotifications.TryParseEvent(payload, out var ownerId, out var eventId, out var type))
        {
            logger.LogWarning("An event notification could not be read; catch-up covers the event.");
            return;
        }
        var subscriptions = host.RunningSubscriptions.Where(s => s.Matches(type)).ToArray();
        if (subscriptions.Length == 0) return;
        JournalOwnerStatus status;
        try { status = await owners.StatusAsync(ownerId, cancellationToken); }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Event {EventId} was not delivered live after {ErrorType}; catch-up retries it.", eventId, error.GetType().Name);
            return;
        }
        if (status == JournalOwnerStatus.Suspended) return;
        if (status != JournalOwnerStatus.Active)
        {
            logger.LogWarning("Event {EventId} names unknown owner {OwnerId}; it was not delivered.", eventId, ownerId);
            return;
        }
        foreach (var subscription in subscriptions)
        {
            DispatchResult result;
            try
            {
                await using var scope = scopes.Create(ownerId);
                result = await scope.Services.GetRequiredService<EventDispatcher>()
                    .DispatchAsync(subscription, ownerId, eventId, DispatchTrigger.Live, cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // The receipt (or its absence) is in PostgreSQL, so nothing is lost.
                logger.LogWarning("Event {EventId} for subscription {Subscription} was not settled after {ErrorType}; catch-up retries it.",
                    eventId, subscription.Name, error.GetType().Name);
                continue;
            }
            if (result.DeadLetter)
                logger.LogWarning("Event {EventId} for subscription {Subscription} ended {Outcome}.", eventId, subscription.Name, result.Outcome);
        }
    }
}

/// <summary>Log category for subscription deliveries; logs carry IDs and type names only.</summary>
public sealed class EventSubscriptionHandlerLog;
