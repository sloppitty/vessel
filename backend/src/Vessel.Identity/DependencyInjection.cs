using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel.Application.Ownership;

namespace Vessel.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddVesselIdentityDirectory(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(sp => IdentitySettings.Read(configuration, sp.GetRequiredService<IHostEnvironment>().IsDevelopment()));
        services.TryAddSingleton(sp => new IdentitySchema(sp.GetRequiredService<IdentitySettings>().Schema));
        services.TryAddSingleton(TimeProvider.System);
        // Directory/cleanup construction, liveness and requests without cookies do not resolve a database.
        services.AddSingleton<IDbContextFactory<IdentityDbContext>>(sp =>
            new IdentityContextFactory(configuration, sp.GetRequiredService<IdentitySchema>()));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<IdentityDbContext>>().CreateDbContext());
        services.AddHostedService<IdentityStartupValidation>();
        services.Replace(ServiceDescriptor.Singleton<IJournalOwnerDirectory, IdentityOwnerDirectory>());
        return services;
    }

    public static IServiceCollection AddVesselIdentityApi(this IServiceCollection services)
    {
        services.AddHostedService<IdentityCleanup>();
        services.AddAuthentication(SessionAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthentication>(SessionAuthentication.SchemeName, _ => { });
        // New endpoints need a signed-in user unless they explicitly opt out, such as liveness and public sign-in.
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddSingleton<IWalletSignatureVerifier, WalletSignatures>();
        services.AddScoped<IdentityTransaction>();
        services.AddScoped<IdentityService>();
        services.AddScoped<IdentityAccountService>();
        services.AddScoped<DevicePairingService>();
        services.AddScoped(sp => new SessionStore(() => sp.GetRequiredService<IDbContextFactory<IdentityDbContext>>(),
            sp.GetRequiredService<IdentitySettings>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<SessionPrincipal>();
        services.AddScoped<IPrincipalValidity>(sp => sp.GetRequiredService<SessionPrincipal>());
        services.AddSingleton<IdentityCookies>();
        services.AddSingleton<IdentityRateLimits>();
        services.AddRateLimiter(_ => { });
        services.AddOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>().Configure<IdentityRateLimits>((options, limits) =>
        {
            options.GlobalLimiter = limits.Requests;
            options.OnRejected = (context, _) => throw IdentityErrors.Error(429, "rate-limited", IdentityRateLimits.RetryAfter(context.Lease));
        });
        return services;
    }
}

internal sealed class IdentityContextFactory(IConfiguration configuration, IdentitySchema schema) : IDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext()
    {
        var connection = configuration.GetConnectionString("Vessel");
        if (string.IsNullOrWhiteSpace(connection))
            throw IdentityErrors.Error(503, "identity-unavailable");
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityDbContext.Configure(options, connection, schema);
        options.ConfigureWarnings(warnings => warnings.Log(
            (RelationalEventId.ConnectionError, LogLevel.Debug), (RelationalEventId.CommandError, LogLevel.Debug),
            (RelationalEventId.TransactionError, LogLevel.Debug), (CoreEventId.QueryIterationFailed, LogLevel.Debug),
            (CoreEventId.SaveChangesFailed, LogLevel.Debug)));
        return new IdentityDbContext(options.Options, schema);
    }
}

internal sealed class IdentityStartupValidation(IdentitySettings settings, ILogger<IdentityStartupValidation> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.OwnerId == JournalOwner.Default && settings.LegacyOwnerOverride)
            logger.LogWarning("Vessel:Identity:AllowLegacyDefaultOwner permits the legacy default owner.");
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class IdentityOwnerDirectory(IDbContextFactory<IdentityDbContext> factory) : IJournalOwnerDirectory
{
    public async Task<JournalOwnerStatus> StatusAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var status = await db.Users.AsNoTracking().Where(x => x.Id == ownerId).Select(x => x.Status).SingleOrDefaultAsync(cancellationToken);
        return status switch { "active" => JournalOwnerStatus.Active, "suspended" => JournalOwnerStatus.Suspended, _ => JournalOwnerStatus.Unknown };
    }

    public async Task<IReadOnlyList<Guid>> ActiveOwnersAsync(Guid? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().Where(x => x.Status == "active" && (after == null || x.Id.CompareTo(after.Value) > 0))
            .OrderBy(x => x.Id).Take(limit).Select(x => x.Id).ToListAsync(cancellationToken);
    }
}
