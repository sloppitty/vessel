using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vessel.Application.Ownership;
using Vessel.Messaging.Modules;
using Vessel.Persistence;
using Vessel.Persistence.Events;
using Vessel.Persistence.Events.Subscriptions;

namespace Vessel.Messaging;

public sealed record ProvisioningResult(IReadOnlyList<string> Registered, IReadOnlyList<string> Existing);

/// <summary>
/// The explicit provision step, run after the EF migrations and never at runtime: insert-only registrations of every
/// known module's subscriptions. Nothing else needs provisioning: delivery uses PostgreSQL notifications.
/// </summary>
public static class MessagingProvisioning
{
    public static async Task<ProvisioningResult> ProvisionAsync(IConfiguration configuration, CancellationToken cancellationToken,
        VesselModuleRegistry? registry = null)
    {
        var connection = configuration.GetConnectionString("Vessel");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("The event database is not configured.");
        registry ??= VesselModuleRegistry.From(configuration);
        var registered = new List<string>();
        var existing = new List<string>();
        await using var provider = RegistrationServices(configuration);
        foreach (var subscription in registry.Modules.SelectMany(m => m.Subscriptions))
        {
            var created = await provider.GetRequiredService<SystemEventSubscriptionStore>()
                .RegisterAsync(subscription, cancellationToken);
            (created ? registered : existing).Add(subscription.Name);
        }
        return new(registered, existing);
    }

    /// <summary>Removes a subscription's registration, progress and receipts, releasing what it held.</summary>
    public static async Task<bool> DeregisterAsync(IConfiguration configuration, string subscription, CancellationToken cancellationToken)
    {
        await using var provider = RegistrationServices(configuration);
        return await provider.GetRequiredService<SystemEventSubscriptionStore>().DeregisterAsync(subscription, cancellationToken);
    }

    /// <summary>The bounded admin retry for a compiled-in subscription's failed receipts.</summary>
    public static async Task<int> RetryFailedAsync(IConfiguration configuration, string subscription, int limit, int extraAttempts,
        Guid? ownerId, bool allOwners, CancellationToken cancellationToken)
    {
        if (allOwners == ownerId.HasValue || ownerId == Guid.Empty)
            throw new ArgumentException("Specify --owner <guid> or --all-owners.");
        var settings = MessagingHosting.Settings(configuration);
        var definition = VesselModuleRegistry.From(settings).Modules.SelectMany(m => m.Subscriptions)
            .SingleOrDefault(s => s.Name == subscription) ?? throw new InvalidOperationException("Unknown subscription.");
        if (limit is < 1 or > 1000 || extraAttempts < 1 || extraAttempts > definition.Limits.MaxAdminRetryBudget)
            throw new ArgumentOutOfRangeException(nameof(extraAttempts), "The admin retry budget is bounded.");
        await using var provider = RegistrationServices(configuration);
        var scopes = provider.GetRequiredService<IOwnerScopeFactory>();
        var store = provider.GetRequiredService<SystemEventSubscriptionStore>();
        var total = 0;
        Guid? after = null;
        do
        {
            var page = ownerId is { } owner ? new[] { owner } : await store.RetentionOwnersAsync(after, cancellationToken);
            if (page.Count == 0) break;
            foreach (var id in page)
            {
                await using var scope = scopes.Create(id);
                total += await scope.Services.GetRequiredService<EventReceiptAdmin>()
                    .RetryFailedAsync(definition, limit, extraAttempts, cancellationToken);
                after = id;
            }
        } while (allOwners);
        return total;
    }

    private static ServiceProvider RegistrationServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IJournalOwnerContext, AssignedOwnerContext>();
        services.AddVesselOfflinePersistence(configuration);
        services.AddVesselEvents(configuration);
        // Registration and admin paths append no events; nothing is published from here.
        services.AddScoped<IVesselEventOutbox, NoPublication>();
        return services.BuildServiceProvider();
    }

    private sealed class NoPublication : IVesselEventOutbox
    {
        public Task StageAsync(Vessel.Application.Events.VesselEvent message, CancellationToken ct) =>
            throw new InvalidOperationException("Provisioning never publishes events.");
        public Task StageAsync(Vessel.Application.Events.MonitorAccountCommand command, CancellationToken ct) =>
            throw new InvalidOperationException("Provisioning never sends commands.");
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }
}
