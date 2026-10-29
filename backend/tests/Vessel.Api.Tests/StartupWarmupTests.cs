using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel.Api;
using Vessel.Identity;

namespace Vessel.Tests;

public sealed class StartupWarmupTests : StartupWarmupTestsSupport
{

    [Fact]
    public async Task Readiness_reports_warming_up_until_the_timeout_then_its_usual_checks()
    {
        var clock = new WarmupClock();
        var startup = new DelayedHostStart();
        using var contexts = new BlockingWarmupContexts(callbackThrows: false);
        var logs = new Logs();
        await using var factory = new CoreApiFactory(Guid.NewGuid(), settings: new()
        {
            ["Vessel:Startup:Warmup:Enabled"] = "true", ["Vessel:Startup:Warmup:Timeout"] = "00:00:03"
        }, configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton(provider => ActivatorUtilities.CreateInstance<StartupWarmup>(provider, clock)));
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton<IHostedService>(startup);
            services.Replace(ServiceDescriptor.Singleton<IDbContextFactory<IdentityDbContext>>(contexts));
        });
        var starting = Task.Run(() => factory.CreateClient());
        try
        {
            await startup.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await contexts.CallbackRegistered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Deliberately hold host startup until one tick before the deadline. Neither startup nor
            // scheduling the first request consumes virtual time, regardless of real runner delays.
            clock.Advance(TimeSpan.FromSeconds(3) - TimeSpan.FromTicks(1));
            Assert.False(starting.IsCompleted);
            startup.Release.TrySetResult();
            using var client = await starting.WaitAsync(TimeSpan.FromSeconds(30));
            var warmup = factory.Services.GetRequiredService<StartupWarmup>();
            Assert.False(warmup.Completed);
            var warming = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, warming.StatusCode);
            Assert.Equal("""{"status":"unavailable","reason":"warming-up"}""", await warming.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            clock.Advance(TimeSpan.FromTicks(1));
            var outcome = await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(outcome.TimedOut);
            Assert.Contains(logs.Entries, entry => entry.Id.Name == "StartupWarmupTimedOut");
            var ready = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("""{"status":"unavailable"}""", await ready.Content.ReadAsStringAsync());
        }
        finally
        {
            startup.Release.TrySetResult();
            contexts.ReleaseCallback.Set();
            contexts.ReleaseWork.TrySetResult();
        }
        await contexts.WorkFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_completes_before_slow_cancellation_callbacks_and_callback_failures_cannot_stop_the_host(bool callbackThrows)
    {
        var clock = new WarmupClock();
        using var contexts = new BlockingWarmupContexts(callbackThrows);
        await using var factory = new CoreApiFactory(Guid.NewGuid(), settings: new()
        {
            ["Vessel:Startup:Warmup:Enabled"] = "true", ["Vessel:Startup:Warmup:Timeout"] = "00:00:01"
        }, configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton(provider => ActivatorUtilities.CreateInstance<StartupWarmup>(provider, clock)));
            services.Replace(ServiceDescriptor.Singleton<IDbContextFactory<IdentityDbContext>>(contexts));
        });
        using var client = factory.CreateClient();
        var warmup = factory.Services.GetRequiredService<StartupWarmup>();
        try
        {
            await contexts.CallbackRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(1));
            await contexts.CallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var outcome = await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(outcome.TimedOut);
            Assert.False(contexts.CallbackFinished.Task.IsCompleted);
            Assert.False(contexts.WorkFinished.Task.IsCompleted);
            // The hosted service has finished successfully while cancellation cleanup is still blocked.
            await warmup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            var ready = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("""{"status":"unavailable"}""", await ready.Content.ReadAsStringAsync());
            contexts.ReleaseCallback.Set();
            await contexts.CallbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(contexts.WorkFinished.Task.IsCompleted);
            contexts.ReleaseWork.TrySetResult();
            await contexts.WorkFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }
        finally
        {
            contexts.ReleaseCallback.Set();
            contexts.ReleaseWork.TrySetResult();
        }
    }

    [Fact]
    public async Task Deadline_also_bounds_work_that_blocks_before_its_first_await()
    {
        var clock = new WarmupClock();
        using var contexts = new BlockingWarmupContexts(callbackThrows: false, blockSynchronously: true);
        await using var factory = new CoreApiFactory(Guid.NewGuid(), settings: new()
        {
            ["Vessel:Startup:Warmup:Enabled"] = "true", ["Vessel:Startup:Warmup:Timeout"] = "00:00:01"
        }, configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton(provider => ActivatorUtilities.CreateInstance<StartupWarmup>(provider, clock)));
            services.Replace(ServiceDescriptor.Singleton<IDbContextFactory<IdentityDbContext>>(contexts));
        });
        using var client = factory.CreateClient();
        var warmup = factory.Services.GetRequiredService<StartupWarmup>();
        try
        {
            await contexts.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(1));
            var outcome = await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(outcome.TimedOut);
            Assert.False(contexts.WorkFinished.Task.IsCompleted);
            await warmup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }
        finally
        {
            contexts.ReleaseCallback.Set();
            contexts.ReleaseWork.TrySetResult();
            contexts.ReleaseSynchronous.Set();
        }
        await contexts.WorkFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Production_warms_up_by_default_and_failures_neither_block_startup_nor_log_details()
    {
        const string connection = "Host=127.0.0.1;Port=1;Database=warmup_database_name;Username=warmup_user;Password=warmup-password-never-logged";
        var logs = new Logs();
        await using var factory = new CoreApiFactory(Guid.NewGuid(), connection: connection, environment: "Production",
            configure: services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();
        var outcome = await factory.Services.GetRequiredService<StartupWarmup>().Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(outcome.Skipped);
        Assert.False(outcome.TimedOut);
        Assert.Contains("overview", outcome.FailedSteps);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal("""{"status":"unavailable"}""", await ready.Content.ReadAsStringAsync());
        var warmupLogs = logs.Entries.Where(entry => entry.Id.Name?.StartsWith("StartupWarmup", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(warmupLogs);
        Assert.All(warmupLogs, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("warmup-password", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("warmup_user", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("warmup_database_name", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Development_hosts_skip_the_warmup_unless_enabled()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        var warmup = factory.Services.GetRequiredService<StartupWarmup>();
        Assert.True(warmup.Completed);
        Assert.True((await warmup.Completion).Skipped);
    }

    [Theory]
    [InlineData("00:00:00.5")]
    [InlineData("00:01:01")]
    public void An_out_of_range_timeout_refuses_to_start(string timeout)
    {
        var logs = new Logs();
        using var factory = new CoreApiFactory(Guid.NewGuid(), settings: new() { ["Vessel:Startup:Warmup:Timeout"] = timeout },
            configure: services => services.AddSingleton<ILoggerProvider>(logs));
        Assert.ThrowsAny<Exception>(() => factory.Services);
        // TestServer teardown can surface ObjectDisposedException to the caller instead of the startup
        // exception. The host records the actual failure before teardown, so require that exact cause.
        Assert.Contains(logs.Entries, entry => entry.Exception is InvalidOperationException error
            && error.Message == "Vessel:Startup:Warmup:Timeout must be between 00:00:01 and 00:01:00.");
    }

    // Only the warm-up uses this clock: TimerCreated must not observe unrelated hosted-service timers.
    private sealed class WarmupClock : TimeProvider
    {
        private readonly ManualTime time = new(DateTimeOffset.UnixEpoch);
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
        public override long TimestampFrequency => time.TimestampFrequency;
        public override long GetTimestamp() => time.GetTimestamp();
        public void Advance(TimeSpan delta) => time.Advance(delta);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = time.CreateTimer(callback, state, dueTime, period);
            TimerCreated.TrySetResult();
            return timer;
        }
    }

    private sealed class DelayedHostStart : IHostedService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Release.Task.WaitAsync(cancellationToken);
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BlockingWarmupContexts(bool callbackThrows, bool blockSynchronously = false)
        : IDbContextFactory<IdentityDbContext>, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallbackRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WorkFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWork { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseCallback { get; } = new();
        public ManualResetEventSlim ReleaseSynchronous { get; } = new();

        public IdentityDbContext CreateDbContext() => throw new NotSupportedException();

        public async Task<IdentityDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (blockSynchronously && !ReleaseSynchronous.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Fixture synchronous work was not released.");
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    CallbackStarted.TrySetResult();
                    if (!ReleaseCallback.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Fixture callback was not released.");
                    if (callbackThrows) throw new InvalidOperationException("Fixture cancellation callback failure.");
                }
                finally { CallbackFinished.TrySetResult(); }
            });
            CallbackRegistered.TrySetResult();
            try
            {
                await ReleaseWork.Task;
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Fixture work must be cancelled.");
            }
            finally { WorkFinished.TrySetResult(); }
        }

        public void Dispose()
        {
            ReleaseCallback.Dispose();
            ReleaseSynchronous.Dispose();
        }
    }
}
