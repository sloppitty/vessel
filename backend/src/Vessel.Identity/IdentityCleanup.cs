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

    /// <summary>Rows per delete. Each batch is its own short transaction, so sign-in waits for one batch, never the sweep.</summary>
    public int BatchSize { get; init; } = 1000;

    public async Task CleanAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Preserve the full verification skew; cleanup must not shorten a challenge's accepted lifetime.
        await DeleteAsync(db => db.Challenges.Where(x => x.ExpiresAt < now.AddSeconds(-30)).OrderBy(x => x.Id), ct);
        await DeleteAsync(db => db.Registrations.Where(x => x.ExpiresAt < now).OrderBy(x => x.Id), ct);
        // Finished and expired pairings linger a day after their last deadline; unfinished ones are never cut short.
        await DeleteAsync(db => db.Pairings.Where(x => x.ExpiresAt < now.AddDays(-1)).OrderBy(x => x.Id), ct);
        var cutoff = now.AddDays(-30);
        await DeleteAsync(db => db.Sessions.Where(x => x.RevokedAt < cutoff ||
            x.IdleExpiresAt < cutoff || x.AbsoluteExpiresAt < cutoff).OrderBy(x => x.Id), ct);
    }

    // The identity write lock is taken and released per batch (issue #106), so a long sweep never holds it past the lock timeout.
    private async Task DeleteAsync<T>(Func<IdentityDbContext, IQueryable<T>> expired, CancellationToken ct)
    {
        int deleted;
        do deleted = await new IdentityTransaction(factory).RunAsync(db => expired(db).Take(BatchSize).ExecuteDeleteAsync(ct), ct);
        while (deleted >= BatchSize);
    }
}
