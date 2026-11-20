using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Vessel.Application.Monitoring;
using Vessel.Application.Notifications;
using Vessel.Worker;

namespace Vessel.Tests;

/// <summary>Issue #105: a worker whose own loop stops making progress exits non-zero so the restart policy applies.</summary>
public sealed class WorkerWatchdogTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_module_without_progress_for_its_threshold_stops_the_worker_with_the_stale_exit_code()
    {
        var (watchdog, clock, progress, lifetime, exit) = Watchdog(new WatchedModule("monitor", TimeSpan.FromMinutes(10)));
        // Measured from the watchdog's start until the first heartbeat.
        clock.Now = Start.AddMinutes(9).AddSeconds(59);
        Assert.False(watchdog.Check());
        progress.Advance("monitor");
        clock.Now = Start.AddMinutes(19).AddSeconds(58);
        Assert.False(watchdog.Check());
        Assert.False(lifetime.Stopped);
        Assert.Equal(0, exit.Code);
        clock.Now = Start.AddMinutes(19).AddSeconds(59);
        Assert.True(watchdog.Check());
        Assert.True(lifetime.Stopped);
        Assert.Equal(WorkerExit.StaleCode, exit.Code);
        Assert.True(exit.Requested.IsCompleted);
    }

    [Fact]
    public void Every_watched_module_is_checked_on_its_own_and_one_stale_module_is_enough()
    {
        var (watchdog, clock, progress, lifetime, _) = Watchdog(
            new WatchedModule("monitor", TimeSpan.FromMinutes(10)), new WatchedModule("notifications", TimeSpan.FromMinutes(15)));
        for (var minute = 1; minute <= 14; minute++)
        {
            clock.Now = Start.AddMinutes(minute);
            progress.Advance("monitor");
            Assert.False(watchdog.Check());
        }
        clock.Now = Start.AddMinutes(15);
        progress.Advance("monitor");
        Assert.True(watchdog.Check());
        Assert.True(lifetime.Stopped);
    }

    [Fact]
    public void The_threshold_is_the_setting_but_never_below_a_modules_heartbeat_timeout_and_unwatched_modules_are_skipped()
    {
        var services = new ServiceCollection()
            .AddSingleton(new MonitorOptions { HeartbeatTimeout = TimeSpan.FromSeconds(90) })
            .AddSingleton(new NotificationOptions { HeartbeatTimeout = TimeSpan.FromMinutes(10) })
            .BuildServiceProvider();
        var worker = WorkerSettings.From(Configuration(new()
        {
            ["Vessel:Worker:Modules:0"] = "monitor", ["Vessel:Worker:Modules:1"] = "notifications",
            ["Vessel:Worker:Modules:2"] = "verification", ["Vessel:Worker:StaleExitAfter"] = "00:05:00"
        }));
        Assert.Equal([new WatchedModule("monitor", TimeSpan.FromMinutes(5)), new WatchedModule("notifications", TimeSpan.FromMinutes(10))],
            WorkerWatchdog.For(services, worker));
        Assert.Equal(TimeSpan.FromMinutes(10), WorkerSettings.From(Configuration([])).StaleExitAfter);
        foreach (var invalid in new[] { "00:01:59", "02:00:01" })
            Assert.Throws<InvalidOperationException>(() => WorkerSettings.From(Configuration(new() { ["Vessel:Worker:StaleExitAfter"] = invalid })));
    }

    [Fact]
    public async Task A_stopped_worker_returns_the_stale_code_after_a_wedged_loop_ignores_cancellation()
    {
        using var host = Host(new WedgedLoop());
        var exit = host.Services.GetRequiredService<WorkerExit>();
        var hung = false;
        var run = WorkerProgram.RunAsync(host, TimeSpan.FromSeconds(30), _ => hung = true);
        await host.Services.GetRequiredService<WedgedLoop>().Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        exit.Request(WorkerExit.StaleCode);
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        Assert.Equal(WorkerExit.StaleCode, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(hung);
    }

    [Fact]
    public async Task A_shutdown_that_hangs_too_ends_the_process_after_the_shutdown_timeout()
    {
        var host = Host(new WedgedLoop(), new HangingStop());
        var exit = host.Services.GetRequiredService<WorkerExit>();
        var hung = new TaskCompletionSource<string>();
        var run = WorkerProgram.RunAsync(host, TimeSpan.Zero, message => hung.TrySetResult(message));
        await host.Services.GetRequiredService<WedgedLoop>().Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        exit.Request(WorkerExit.StaleCode);
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        Assert.Contains("did not stop", await hung.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(WorkerExit.StaleCode, await run.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task A_running_worker_host_stops_itself_through_the_watchdog_and_exits_with_the_stale_code()
    {
        var clock = new Clock();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<WorkerExit>();
        builder.Services.AddSingleton(new WorkerProgress(clock));
        builder.Services.AddHostedService(sp => new WorkerWatchdog(sp.GetRequiredService<WorkerProgress>(),
            [new WatchedModule("paper", TimeSpan.FromMinutes(2))], clock, sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<WorkerExit>(), NullLogger<WorkerWatchdog>.Instance));
        using var host = builder.Build();
        var run = WorkerProgram.RunAsync(host);
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Now = Start.AddMinutes(1);
        clock.Fire();
        await Task.Delay(100);
        Assert.False(run.IsCompleted, run.IsFaulted ? run.Exception!.ToString() : run.IsCompleted ? "completed without a stale exit" : null);
        Assert.False(host.Services.GetRequiredService<WorkerExit>().Requested.IsCompleted);
        clock.Now = Start.AddMinutes(2);
        clock.Fire();
        Assert.Equal(WorkerExit.StaleCode, await run.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    private static (WorkerWatchdog, Clock, WorkerProgress, Lifetime, WorkerExit) Watchdog(params WatchedModule[] modules)
    {
        var clock = new Clock();
        var progress = new WorkerProgress(clock);
        var lifetime = new Lifetime();
        var exit = new WorkerExit();
        return (new WorkerWatchdog(progress, modules, clock, lifetime, exit, NullLogger<WorkerWatchdog>.Instance),
            clock, progress, lifetime, exit);
    }

    private static IHost Host(params IHostedService[] services)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(1));
        builder.Services.AddSingleton<WorkerExit>();
        foreach (var service in services)
        {
            builder.Services.AddSingleton(service.GetType(), service);
            builder.Services.AddSingleton(sp => (IHostedService)sp.GetRequiredService(service.GetType()));
        }
        return builder.Build();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>A loop stuck in an await that never completes and never observes cancellation.</summary>
    private sealed class WedgedLoop : BackgroundService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.TrySetResult();
            return Task.Delay(Timeout.Infinite, CancellationToken.None);
        }
    }

    private sealed class HangingStop : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public bool Stopped;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped = true;
    }

    /// <summary>A manual clock whose timers fire only when the test says so.</summary>
    internal sealed class Clock : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> timers = [];
        public DateTimeOffset Now = Start;
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (timers) timers.Add((callback, state));
            TimerCreated.TrySetResult();
            return new NoTimer();
        }

        public void Fire()
        {
            (TimerCallback Callback, object? State)[] due;
            lock (timers) due = [.. timers];
            foreach (var (callback, state) in due) callback(state);
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
