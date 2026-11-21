using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Persistence.Events;

namespace Vessel.Messaging.Modules.Monitor;

/// <summary>
/// Handles <c>commands.monitor</c> notifications. It validates the owner before resolving anything owner-scoped,
/// then hands the command to the module's Application handler. Unknown owners and invalid payloads are logged and
/// dropped; suspended owners are dropped without starting work. Infrastructure failures are logged: the pending
/// request remains in PostgreSQL for the scheduler.
/// </summary>
public sealed class MonitorCommandListener(IOwnerScopeFactory scopes, IJournalOwnerDirectory owners,
    ILogger<MonitorCommandListener> logger) : INotificationHandler
{
    public Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        if (VesselNotifications.ParseCommand(payload) is { } command) return HandleAsync(command, cancellationToken);
        logger.LogWarning("A monitor notification could not be read; it was dropped.");
        return Task.CompletedTask;
    }

    public async Task HandleAsync(MonitorAccountCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var status = await owners.StatusAsync(command.OwnerId, cancellationToken);
            if (command.OwnerId == Guid.Empty || command.RequestId == Guid.Empty || command.AccountId == Guid.Empty ||
                status == JournalOwnerStatus.Unknown)
            {
                logger.LogWarning("Monitor command {RequestId} for account {AccountId} names unknown owner {OwnerId} or invalid IDs; dropped.",
                    command.RequestId, command.AccountId, command.OwnerId);
                return;
            }
            if (status != JournalOwnerStatus.Active)
            {
                logger.LogInformation("Monitor command {RequestId} for suspended owner {OwnerId} was dropped.", command.RequestId, command.OwnerId);
                return;
            }
            await using var scope = scopes.Create(command.OwnerId);
            await scope.Services.GetRequiredService<IVesselCommandHandler<MonitorAccountCommand>>()
                .HandleAsync(command, cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Monitor command {RequestId} for account {AccountId} and owner {OwnerId} was dropped after {ErrorType}; the scheduler retries pending work.",
                command.RequestId, command.AccountId, command.OwnerId, error.GetType().Name);
        }
    }
}
