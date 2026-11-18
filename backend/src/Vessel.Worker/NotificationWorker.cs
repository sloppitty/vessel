using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel.Application.Notifications;
using Vessel.Application.Ownership;
using Vessel.Messaging;
using Vessel.Persistence.Notifications;

namespace Vessel.Worker;

/// <summary>
/// The notifications worker's health: after a pass with a successful owner or no failures, it writes
/// <c>&lt;unix seconds&gt; &lt;timeout seconds&gt;</c> to its own heartbeat file and its own heartbeat row. It never
/// touches the monitor's, so a healthy notifications worker cannot make a stopped monitor look online.
/// </summary>
public sealed class NotificationHeartbeat(NotificationWorkerState state, WorkerSettings worker,
    NotificationOptions options, TimeProvider time, WorkerProgress? progress = null) : IMaintenanceHeartbeat
{
    private readonly string workerId = Guid.NewGuid().ToString("N");

    public async Task BeatAsync(CancellationToken cancellationToken)
    {
        var file = options.HeartbeatFile;
        try
        {
            var now = time.GetUtcNow();
            var temporary = file + "." + workerId + ".new";
            await File.WriteAllTextAsync(temporary, string.Create(CultureInfo.InvariantCulture,
                $"{now.ToUnixTimeSeconds()} {(long)options.HeartbeatTimeout.TotalSeconds}"), cancellationToken);
            File.Move(temporary, file, overwrite: true);
            await state.RecordAsync(worker.Deployment!, now, cancellationToken);
            progress?.Advance(Vessel.Messaging.Modules.Notifications.NotificationsModule.ModuleName);
        }
        catch
        {
            // Neither the container nor the API may report a worker healthy whose heartbeat cannot be recorded.
            try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}

/// <summary>Hourly: deletes notifications older than <c>RetentionDays</c> with their sources and deliveries.</summary>
public sealed class NotificationRetentionService(IOwnerScopeFactory scopes, SystemNotificationStore owners,
    NotificationOptions options, TimeProvider time,
    ILogger<NotificationRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await RunRetentionAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Notification retention failed with {ErrorType}.", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunRetentionAsync(CancellationToken ct)
    {
        Guid? after = null;
        var cutoff = time.GetUtcNow().AddDays(-options.RetentionDays);
        while (true)
        {
            var page = await owners.RetentionOwnersAsync(after, ct);
            if (page.Count == 0) return;
            foreach (var owner in page)
            {
                after = owner;
                try
                {
                    await using var scope = scopes.Create(owner);
                    var store = scope.Services.GetRequiredService<INotificationStore>();
                    await scope.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(() => store.RetainAsync(cutoff, ct), ct);
                }
                catch (Exception error) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Notification retention for owner {OwnerId} failed with {ErrorType}.", owner, error.GetType().Name);
                }
            }
        }
    }
}
