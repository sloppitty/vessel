using System.Diagnostics;
using Vessel.Application.Activity;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Notifications;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;
using Vessel.Identity;
using Vessel.Persistence;

namespace Vessel.Api;

/// <summary>Result of the startup warm-up: the steps attempted and failed, as names only, never exception text.</summary>
public sealed record StartupWarmupOutcome(bool Skipped, bool TimedOut, IReadOnlyList<string> Steps, IReadOnlyList<string> FailedSteps);

/// <summary>
/// Moves first-use EF model building, query compilation and JIT off the first user's requests. After Kestrel listens,
/// it builds both models and runs the hot read shapes once: cookie-session and sign-in lookups and EOA recovery of a
/// public vector (Wallet mode), then Overview, Plays, notifications, the event head, monitor and Activity reads for a
/// fresh random owner that has no rows. Readiness answers 503 <c>warming-up</c> until it finishes or its timeout
/// passes; after that every existing readiness check applies unchanged. It never writes, never issues or consumes
/// challenges or sessions, keeps no result (authorization is never cached) and logs step names and exception types only.
/// A failed or timed-out step never stops the API from starting.
/// </summary>
public sealed class StartupWarmup : BackgroundService
{
    private readonly IServiceProvider services;
    private readonly ILogger<StartupWarmup> logger;
    private readonly bool enabled;
    private readonly TimeSpan timeout;
    private readonly TaskCompletionSource<StartupWarmupOutcome> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupWarmup(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment, ILogger<StartupWarmup> logger)
    {
        (this.services, this.logger) = (services, logger);
        // Development hosts and tests restart often and are not behind the readiness gate: opt in there.
        enabled = configuration.GetValue("Vessel:Startup:Warmup:Enabled", !environment.IsDevelopment());
        timeout = configuration.GetValue("Vessel:Startup:Warmup:Timeout", TimeSpan.FromSeconds(30));
        // The image healthcheck (10 s start period, six 10 s retries) must outlast the gate, or Compose gives up on web.
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromSeconds(60))
            throw new InvalidOperationException("Vessel:Startup:Warmup:Timeout must be between 00:00:01 and 00:01:00.");
        if (!enabled) completion.SetResult(new(true, false, [], []));
    }

    public Task<StartupWarmupOutcome> Completion => completion.Task;
    public bool Completed => completion.Task.IsCompleted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled) return;
        await Task.Yield(); // Never hold up host start: Kestrel listens while the warm-up runs.
        var (steps, failed) = (new List<string>(), new List<string>());
        StartupWarmupOutcome Outcome(bool timedOut) { lock (steps) return new(false, timedOut, [.. steps], [.. failed]); }
        var elapsed = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = deadline.Token;
        async Task Step(string name, Func<Task> action)
        {
            ct.ThrowIfCancellationRequested();
            lock (steps) steps.Add(name);
            try { await action(); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lock (steps) failed.Add(name);
                logger.LogWarning(new EventId(1101, "StartupWarmupStepFailed"),
                    "Startup warm-up step {Step} failed: ExceptionType={ExceptionType}", name, ex.GetType().FullName);
            }
        }
        async Task RunAsync()
        {
            await using (var scope = services.CreateAsyncScope())
                await IdentityWarmup.RunAsync(scope.ServiceProvider, Step, ct);
            await using (var scope = services.GetRequiredService<IOwnerScopeFactory>().Create(Guid.NewGuid()))
                await JournalAsync(scope.Services, Step, ct);
        }
        var work = RunAsync();
        try
        {
            // WaitAsync bounds readiness even when a database call ignores cancellation (a stalled handshake).
            await work.WaitAsync(timeout, stoppingToken);
            var outcome = Outcome(false);
            logger.LogInformation(new EventId(1100, "StartupWarmupFinished"),
                "Startup warm-up finished in {ElapsedMs} ms; {FailedCount} steps failed.", elapsed.ElapsedMilliseconds, outcome.FailedSteps.Count);
            completion.TrySetResult(outcome);
        }
        catch (Exception ex) when (ex is TimeoutException || stoppingToken.IsCancellationRequested)
        {
            await deadline.CancelAsync();
            _ = work.ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted); // Observed, not logged.
            if (!stoppingToken.IsCancellationRequested)
                logger.LogWarning(new EventId(1102, "StartupWarmupTimedOut"),
                    "Startup warm-up stopped after its {TimeoutSeconds} s timeout; readiness no longer waits for it.", timeout.TotalSeconds);
            completion.TrySetResult(Outcome(true));
        }
        catch (Exception ex)
        {
            // Never stop the host: readiness then applies its usual checks.
            lock (steps) failed.Add("warmup");
            logger.LogWarning(new EventId(1101, "StartupWarmupStepFailed"),
                "Startup warm-up step {Step} failed: ExceptionType={ExceptionType}", "warmup", ex.GetType().FullName);
            completion.TrySetResult(Outcome(false));
        }
    }

    // Each read below is the endpoint's own service call. The owner is new, so every query matches nothing.
    private static async Task JournalAsync(IServiceProvider owned, Func<string, Func<Task>, Task> step, CancellationToken ct)
    {
        await step("journal-model", () =>
        {
            _ = owned.GetRequiredService<VesselDbContext>().Model;
            return Task.CompletedTask;
        });
        await step("overview", () => owned.GetRequiredService<WorkspaceService>().OverviewAsync(ct));
        await step("event-head", () => owned.GetRequiredService<IVesselEventReader>().ReadAsync(long.MaxValue.ToString(
            System.Globalization.CultureInfo.InvariantCulture), 1, ct));
        await step("notifications", async () =>
        {
            var notifications = owned.GetRequiredService<NotificationService>();
            await notifications.HeadAsync(ct);
            await notifications.FeedAsync(null, VesselEventEndpoints.NotificationBatch, ct);
            await notifications.PageAsync(new("unread", null, null, 1, 25), ct);
        });
        await step("monitor", () => owned.GetRequiredService<MonitorCoordinator>().SummaryAsync(ct));
        await step("plays", async () =>
        {
            var plays = owned.GetRequiredService<PlayService>();
            await plays.ListAsync("progress", null, "all", null, "5", ct);
            await plays.CountsAsync(null, "all", ct);
            await plays.ListAsync("progress", null, null, null, null, ct);
            await plays.CountsAsync(null, null, ct);
        });
        await step("activity", async () =>
        {
            await owned.GetRequiredService<ActivityService>().PageAsync(new(Page: "1", PageSize: "5"), ct);
            await owned.GetRequiredService<Vessel.Application.Overview.OverviewSummaryService>().GetAsync("portfolio", "30", "all", ct);
            // With no accounts the services above skip the fact reads, so run those shapes for one unknown account.
            await owned.GetRequiredService<IActivityStore>().ReadAsync(null, ct, new HashSet<Guid> { Guid.NewGuid() });
        });
    }
}
