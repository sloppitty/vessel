using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Persistence.Events.Subscriptions;

namespace Vessel.Persistence.Events;

public static class VesselEventRegistration
{
    /// <summary>
    /// The event log, browser reader, subscription runtime and the <c>pg_notify</c> outbox. Listening is the
    /// worker's job (Vessel.Messaging); read paths never resolve the outbox.
    /// </summary>
    public static IServiceCollection AddVesselEvents(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<VesselEventSettings>().Bind(config.GetSection(VesselEventSettings.Section))
            .Configure(s => s.MonitorInterval = config.GetValue<TimeSpan?>("Vessel:Monitor:Interval") ?? TimeSpan.FromMinutes(10))
            .Validate(s => s.Valid(), "Vessel:Events settings or Vessel:Monitor:Interval (1 to 60 minutes) are invalid.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<VesselEventMutationCapture>();
        services.TryAddScoped<IVesselEventOutbox, PostgresNotificationOutbox>();
        services.TryAddScoped<VesselEventStore>();
        services.TryAddScoped<IVesselEventSink>(sp => sp.GetRequiredService<VesselEventStore>());
        services.TryAddScoped<IMonitorCommandSink>(sp => sp.GetRequiredService<VesselEventStore>());
        services.TryAddScoped<IVesselEventReader, VesselEventHistory>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitOfWorkParticipant, VesselEventUnitOfWorkParticipant>());
        services.TryAddScoped<EventDispatcher>();
        services.TryAddScoped<EventCatchUp>();
        services.TryAddScoped<EventSubscriptionRegistry>();
        services.TryAddSingleton<SystemEventSubscriptionStore>();
        services.TryAddScoped<EventReceiptAdmin>();
        services.TryAddScoped<EventRetention>();
        // Attaches to the existing context registration. No separate context or EF upgrade.
        services.ConfigureDbContext<VesselDbContext>((sp, options) =>
            options.AddInterceptors(sp.GetRequiredService<VesselEventMutationCapture>()));
        return services;
    }
}

// Resolve the same scoped sink as Application uses. UoW construction does not resolve its DbContext twice.
internal sealed class VesselEventUnitOfWorkParticipant(VesselEventStore store) : IUnitOfWorkParticipant
{
    public Task BeforeCommitAsync(CancellationToken ct) => store.BeforeCommitAsync(ct);
    public Task AfterCommitAsync(CancellationToken ct) => store.AfterCommitAsync(ct);
    public Task RollbackAsync(CancellationToken ct) => store.RollbackAsync(ct);
}
