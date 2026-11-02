using Vessel.Application.Activity;
using Vessel.Application.Credentials;
using Vessel.Application.Evidence;
using Vessel.Application.MarketData;
using Vessel.Application.Overview;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Application.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Monitoring;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Monitoring;

namespace Vessel.Infrastructure;

/// <summary>
/// Application use-case registrations shared by both hosts (API and worker), so they resolve the same scoped
/// collaborators. Persistence registers only the DbContext and the stores behind their ports.
/// </summary>
public static class ApplicationServices
{
    public static IServiceCollection AddVesselApplication(this IServiceCollection services)
    {
        services.TryAddSingleton<UserReadLimits>();
        services.TryAddScoped<AccountReadPolicy>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<ActivityService>();
        services.AddScoped<OverviewSummaryService>();
        services.AddScoped<AccountDiscoveryService>();
        services.AddScoped<AccountCredentialService>();
        services.AddScoped<AccountCredentialReader>();
        // The venue registry is also used by database-free system metadata. Delay DB resolution
        // until an adapter actually reads a credential, retaining the request's scoped context.
        services.AddScoped<IAccountCredentialReader>(sp =>
            new DeferredAccountCredentialReader(() => sp.GetRequiredService<AccountCredentialReader>()));
        services.AddScoped<EvidenceService>();
        services.AddScoped<PlayService>();
        services.AddScoped<DiaryService>();
        services.AddScoped<PlayExecutionService>();
        services.AddScoped<SizingService>();
        services.AddScoped<Vessel.Application.Risk.OpenRiskService>();
        services.AddScoped<Vessel.Application.Export.FactsExportService>();
        services.AddScoped<Vessel.Application.Export.PlayBundleService>();
        // Review resolves Activity (and the database) only when a report includes imported positions.
        services.AddScoped<ActivityReviewPositions>();
        services.AddScoped<IReviewPositionSource>(sp =>
            new DeferredReviewPositions(() => sp.GetRequiredService<ActivityReviewPositions>()));
        services.AddScoped<ReviewService>();
        services.AddScoped<CandleService>();
        services.AddScoped<MarketContextService>();
        services.AddScoped<MarketStreamService>();
        return services;
    }

    /// <summary>
    /// The monitor use cases and <c>Vessel:Monitor</c> settings. The API always registers them (Check and Refresh);
    /// a worker only when it runs the monitor module.
    /// </summary>
    public static IServiceCollection AddVesselMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new MonitorOptions();
        configuration.GetSection("Vessel:Monitor").Bind(options);
        options.Validate();
        services.AddSingleton(options);
        var plays = new PlayExecutionOptions();
        configuration.GetSection(PlayExecutionOptions.Section).Bind(plays);
        plays.Validate();
        services.AddSingleton(plays);
        var funding = new FundingOptions();
        configuration.GetSection(FundingOptions.Section).Bind(funding);
        funding.Validate();
        services.AddSingleton(funding);
        services.TryAddSingleton(TimeProvider.System);
        var deployments = configuration.GetSection("Vessel:Messaging:Deployments").GetChildren().ToArray();
        services.TryAddSingleton(new MonitorDeployments(deployments.Length == 0 ? ["monitor"] : deployments
            .Where(d => d.GetChildren().Any(m => m.Value == "monitor")).Select(d => d.Key).ToArray()));
        services.TryAddSingleton<UserReadLimits>();
        services.TryAddScoped<AccountReadPolicy>();
        services.AddScoped<MonitorDiscrepancyService>();
        services.AddScoped<IMonitorEvaluator>(sp => sp.GetRequiredService<MonitorDiscrepancyService>());
        services.AddScoped<IMonitorImporter>(sp => sp.GetRequiredService<PlayExecutionService>());
        services.TryAddSingleton<IMonitorRunLog, MonitorRunLog>();
        services.AddScoped<MonitorCoordinator>();
        services.AddScoped<MonitorExecutionView>();
        services.AddScoped<MonitorRunExecutor>();
        return services;
    }
}

internal sealed class DeferredReviewPositions(Func<IReviewPositionSource> source) : IReviewPositionSource
{
    public Task<IReadOnlyList<ReviewPosition>> ReadAsync(IReadOnlySet<Guid> accountIds, CancellationToken ct) =>
        source().ReadAsync(accountIds, ct);
}

internal sealed class DeferredAccountCredentialReader(Func<AccountCredentialReader> reader) : IAccountCredentialReader
{
    public Task<string?> ReadAsync(Guid accountId, CancellationToken cancellationToken) =>
        reader().ReadAsync(accountId, cancellationToken);

    public Task<bool> IsUsableAsync(Account account, CancellationToken cancellationToken) =>
        reader().IsUsableAsync(account, cancellationToken);

    public Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken cancellationToken) =>
        reader().ReadAttemptAsync(accountId, cancellationToken);

    public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken cancellationToken) =>
        reader().MarkRefusedAsync(accountId, attemptedRevision, cancellationToken);
}
