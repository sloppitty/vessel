using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vessel.Identity;

public sealed class IdentityCleanup(IDbContextFactory<IdentityDbContext> factory, TimeProvider clock, ILogger<IdentityCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await CleanAsync(stoppingToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogWarning("Identity cleanup did not complete. Failure type: {FailureType}.", error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task CleanAsync(CancellationToken ct)
    {
        await new IdentityTransaction(factory).RunAsync(async db =>
        {
            var now = clock.GetUtcNow();
            // Preserve the full verification skew; cleanup must not shorten a challenge's accepted lifetime.
            await db.Challenges.Where(x => x.ExpiresAt < now.AddSeconds(-30)).ExecuteDeleteAsync(ct);
            await db.Registrations.Where(x => x.ExpiresAt < now).ExecuteDeleteAsync(ct);
            // Finished and expired pairings linger a day after their last deadline; unfinished ones are never cut short.
            await db.Pairings.Where(x => x.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
            var cutoff = now.AddDays(-30);
            await db.Sessions.Where(x => x.RevokedAt < cutoff ||
                x.IdleExpiresAt < cutoff || x.AbsoluteExpiresAt < cutoff).ExecuteDeleteAsync(ct);
            return true;
        }, ct);
    }
}
