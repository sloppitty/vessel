using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vessel.Application.Ownership;

namespace Vessel.Persistence;

public sealed class MissingDatabaseConfigurationException()
    : InvalidOperationException("ConnectionStrings__Vessel is required for persistence.");

public static class DependencyInjection
{
    /// <summary>Request and worker hosts: every connection carries the <see cref="DatabaseLimits"/> lock and statement timeouts.</summary>
    public static IServiceCollection AddVesselPersistence(this IServiceCollection services, IConfiguration config) =>
        services.AddPersistence(config, DatabaseLimits.Read(config));

    /// <summary>
    /// Offline commands (search projection rebuild, messaging provisioning and receipt retries, identity verification): the
    /// same stores without the session limits, because their work is long on purpose and runs with the hosts stopped.
    /// </summary>
    public static IServiceCollection AddVesselOfflinePersistence(this IServiceCollection services, IConfiguration config) =>
        services.AddPersistence(config, null);

    private static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration config, DatabaseLimits? limits)
    {
        services.TryAddScoped<OwnerScopeAssignment>();
        services.TryAddSingleton<IOwnerScopeFactory, OwnerScopeFactory>();
        NpgsqlDataSource Create(bool pooled)
        {
            var connection = config.GetConnectionString("Vessel");
            if (string.IsNullOrWhiteSpace(connection)) throw new MissingDatabaseConfigurationException();
            var builder = new NpgsqlDataSourceBuilder(connection);
            limits?.Apply(builder.ConnectionStringBuilder);
            if (!pooled) builder.ConnectionStringBuilder.Pooling = false;
            return builder.Build();
        }
        // See DatabasePools for the order in which these may nest.
        services.TryAddSingleton(_ => Create(pooled: true));
        services.TryAddKeyedSingleton(DatabasePools.Journal, (_, _) => Create(pooled: true));
        services.TryAddKeyedSingleton(DatabasePools.Dedicated, (_, _) => Create(pooled: false));
        services.TryAddSingleton<IJournalOwnerInventory, JournalOwnerInventory>();
        // Every worker registers persistence, including deployments that do not run the monitor module.
        var deployments = config.GetSection("Vessel:Messaging:Deployments").GetChildren().ToArray();
        services.TryAddSingleton(new Vessel.Application.Monitoring.MonitorDeployments(deployments.Length == 0 ? ["monitor"] : deployments
            .Where(d => d.GetChildren().Any(m => m.Value == "monitor")).Select(d => d.Key).ToArray()));
        services.TryAddSingleton<Vessel.Application.Monitoring.IServerStateStore, ServerStateStore>();
        services.TryAddSingleton<Vessel.Application.Monitoring.ISystemMonitorStore, SystemMonitorStore>();
        // Resolve only when persistence is consumed. Metadata and liveness do not open a database.
        // The context borrows the registered journal pool, never one built from the connection string.
        services.AddDbContext<VesselDbContext>((provider, options) =>
        {
            options.UseNpgsql(provider.GetRequiredKeyedService<NpgsqlDataSource>(DatabasePools.Journal)).ConfigureWarnings(warnings => warnings.Log(
                // These Error events embed exception text, the database name and host:port.
                // The API records request failures with a safe event, so keep them at Debug.
                (RelationalEventId.ConnectionError, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug),
                (RelationalEventId.TransactionError, LogLevel.Debug),
                (CoreEventId.QueryIterationFailed, LogLevel.Debug),
                (CoreEventId.SaveChangesFailed, LogLevel.Debug)));
        });
        services.AddScoped<UnitOfWork>();
        services.AddScoped<Vessel.Application.Ownership.IUnitOfWork>(sp => sp.GetRequiredService<UnitOfWork>());
        // Phase 2, item 13: every host's units of work keep the journal search projection in step with what they change.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitOfWorkParticipant, Search.PlaySearchProjector>());
        services.AddScoped<Search.PlaySearchRebuild>();
        services.AddScoped<Vessel.Application.Workspace.IWorkspaceStore, WorkspaceStore>();
        services.AddScoped<Vessel.Application.Activity.IActivityStore, ActivityStore>();
        var activityCacheBytes = config.GetValue<long?>("Vessel:Activity:CacheCapacityBytes") ?? 256L * 1024 * 1024;
        if (activityCacheBytes < 0) throw new InvalidOperationException("Vessel:Activity:CacheCapacityBytes must be non-negative.");
        services.AddSingleton(new ActivitySummaryCache(activityCacheBytes));
        services.AddScoped<Vessel.Application.Activity.IActivitySummaryReader, CachedActivitySummaryReader>();
        services.AddScoped<Vessel.Application.Credentials.IAccountCredentialStore, AccountCredentialStore>();
        services.AddScoped<Vessel.Application.Evidence.IEvidenceMetadataStore, EvidenceStore>();
        services.AddScoped<Vessel.Application.Plays.IPlayStore, PlayStore>();
        services.AddScoped<Vessel.Application.Plays.IDiaryStore, DiaryStore>();
        services.AddScoped<Vessel.Application.Plays.Execution.IPlayExecutionStore, ExecutionStore>();
        services.AddScoped<Vessel.Application.Sizing.ISizingStore, SizingStore>();
        services.AddScoped<Vessel.Application.Risk.IOpenRiskStore, RiskStore>();
        services.AddScoped<Vessel.Application.Reporting.IReportingStore, ReportingStore>();
        services.AddScoped<Vessel.Application.MarketSources.IMarketSourceStore, MarketSourceStore>();
        services.AddScoped<Vessel.Application.Export.IFactsExportStore, ExportStore>();
        services.AddScoped<Vessel.Application.Review.IReviewStore, ReviewStore>();
        services.AddScoped<Vessel.Application.Monitoring.IMonitorStore, MonitorStore>();
        services.AddScoped<Vessel.Application.Monitoring.IMonitorDiscrepancyStore, MonitorDiscrepancyStore>();
        return services;
    }
}
