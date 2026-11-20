using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel.Application.Monitoring;
using Vessel.Application.Notifications;
using Vessel.Messaging.Modules.Monitor;
using Vessel.Messaging.Modules.Notifications;

namespace Vessel.Worker;

/// <summary>
/// When each watched module's loop last made progress: the moment it wrote its heartbeat (monitor scheduler,
/// notifications maintenance, paper scheduler), and for the paper scheduler also each finished candle read inside a tick.
/// </summary>
public sealed class WorkerProgress(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> last = new(StringComparer.Ordinal);

    public void Advance(string module) => last[module] = time.GetUtcNow();

    public DateTimeOffset? Last(string module) => last.TryGetValue(module, out var at) ? at : null;
}

/// <summary>Set once by <see cref="WorkerWatchdog"/>; the entrypoint returns <see cref="Code"/> after the host stops.</summary>
public sealed class WorkerExit
{
    /// <summary>The exit code of a worker stopped because a module's loop made no progress.</summary>
    public const int StaleCode = 3;
    private readonly TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Code { get; private set; }
    public Task Requested => requested.Task;

    public void Request(int code)
    {
        if (Code == 0) Code = code;
        requested.TrySetResult();
    }
}

/// <summary>A module whose progress is watched, and how long it may go without any.</summary>
public sealed record WatchedModule(string Name, TimeSpan StaleAfter);

/// <summary>
/// Stops the worker with exit code <see cref="WorkerExit.StaleCode"/> when a watched module has made no progress for
/// its threshold, so the container's restart policy starts a fresh process. The health check alone only marks the
/// container unhealthy, which Compose never acts on. A module that never made progress is measured from the
/// watchdog's start, which follows every other hosted service's start (including the broker connection).
/// </summary>
public sealed class WorkerWatchdog(WorkerProgress progress, IReadOnlyList<WatchedModule> modules, TimeProvider time,
    IHostApplicationLifetime lifetime, WorkerExit exit, ILogger<WorkerWatchdog> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private DateTimeOffset started = time.GetUtcNow();

    /// <summary>
    /// The watched modules of a host: each selected module with a heartbeat, allowed the larger of
    /// <see cref="WorkerSettings.StaleExitAfter"/> and its own heartbeat timeout, so a container is reported unhealthy
    /// before it is restarted. Modules without a heartbeat (verification) are not watched.
    /// </summary>
    public static IReadOnlyList<WatchedModule> For(IServiceProvider services, WorkerSettings worker)
    {
        var watched = new List<WatchedModule>();
        foreach (var module in worker.Modules)
        {
            TimeSpan? timeout = module switch
            {
                MonitorModule.ModuleName => services.GetRequiredService<MonitorOptions>().HeartbeatTimeout,
                NotificationsModule.ModuleName => services.GetRequiredService<NotificationOptions>().HeartbeatTimeout,
                "paper" => services.GetRequiredService<PaperWorkerOptions>().HeartbeatTimeout,
                _ => null
            };
            if (timeout is { } heartbeat)
                watched.Add(new(module, heartbeat > worker.StaleExitAfter ? heartbeat : worker.StaleExitAfter));
        }
        return watched;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (modules.Count == 0) return;
        started = time.GetUtcNow();
        using var timer = new PeriodicTimer(CheckInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                if (Check()) return;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>True, after requesting the stale exit and stopping the host, when a module is past its threshold.</summary>
    public bool Check()
    {
        var now = time.GetUtcNow();
        foreach (var module in modules)
        {
            var since = progress.Last(module.Name) is { } last && last > started ? last : started;
            if (now - since < module.StaleAfter) continue;
            logger.LogCritical("The {Module} loop made no progress for {Idle} (limit {Limit}). Stopping the worker with exit " +
                "code {Code} so that its restart policy starts a new one.", module.Name, Round(now - since), module.StaleAfter,
                WorkerExit.StaleCode);
            exit.Request(WorkerExit.StaleCode);
            lifetime.StopApplication();
            return true;
        }
        return false;
    }

    private static TimeSpan Round(TimeSpan value) => TimeSpan.FromSeconds(Math.Floor(value.TotalSeconds));
}
