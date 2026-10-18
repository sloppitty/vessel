using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethereum.Signer;
using Npgsql;
using Vessel.Api;
using Vessel.Tests.Identity;

namespace Vessel.Tests;

public sealed class StartupWarmupTests
{
    private static readonly string[] JournalSteps = ["journal-model", "overview", "event-head", "notifications", "monitor", "plays", "activity"];

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_step_succeeds_against_a_read_only_database_then_readiness_applies_its_checks(bool wallet)
    {
        await using var journal = await CoreDatabase.CreateAsync();
        await using var identity = await IdentityDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        if (wallet) await identity.SeedAsync(EthECKey.GenerateKey(), owner);
        // PostgreSQL itself refuses any write the warm-up might attempt, which would fail that step.
        var readOnly = new NpgsqlConnectionStringBuilder(journal.ConnectionString) { Options = "-c default_transaction_read_only=on" }
            .ConnectionString;
        var settings = wallet ? identity.ConfigurationValues(owner) : new() { ["Vessel:Identity:Schema"] = identity.Schema.Name };
        settings["ConnectionStrings:Vessel"] = readOnly;
        settings["Vessel:Startup:Warmup:Enabled"] = "true";
        var logs = new Logs();
        await using var factory = new CoreApiFactory(owner, connection: readOnly, settings: settings,
            configure: services => services.AddSingleton<ILoggerProvider>(logs));
        var outcome = await factory.Services.GetRequiredService<StartupWarmup>().Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(outcome.Skipped);
        Assert.False(outcome.TimedOut);
        Assert.Empty(outcome.FailedSteps);
        string[] identitySteps = wallet ? ["identity-model", "identity-sessions", "identity-sign-in", "identity-signatures"] : ["identity-model"];
        Assert.Equal([.. identitySteps, .. JournalSteps], outcome.Steps);
        Assert.Contains(logs.Entries, entry => entry.Id.Name == "StartupWarmupFinished");
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await using var db = identity.CreateDbContext();
        Assert.Equal(0, await db.Challenges.CountAsync());
        Assert.Equal(0, await db.Sessions.CountAsync());
    }

    [Fact]
    public async Task Readiness_reports_warming_up_until_the_timeout_then_its_usual_checks()
    {
        // Accepts TCP connections (kernel backlog) but never answers the PostgreSQL startup message.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var connection = $"Host=127.0.0.1;Port={port};Database=unused;Username=unused;Password=unused;Timeout=60;Pooling=false";
            var logs = new Logs();
            await using var factory = new CoreApiFactory(Guid.NewGuid(), connection: connection, settings: new()
            {
                ["Vessel:Startup:Warmup:Enabled"] = "true", ["Vessel:Startup:Warmup:Timeout"] = "00:00:03"
            }, configure: services => services.AddSingleton<ILoggerProvider>(logs));
            using var client = factory.CreateClient();
            var warmup = factory.Services.GetRequiredService<StartupWarmup>();
            var warming = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, warming.StatusCode);
            Assert.Equal("""{"status":"unavailable","reason":"warming-up"}""", await warming.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            var outcome = await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(outcome.TimedOut);
            Assert.Contains(logs.Entries, entry => entry.Id.Name == "StartupWarmupTimedOut");
            listener.Stop();
            var ready = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("""{"status":"unavailable"}""", await ready.Content.ReadAsStringAsync());
        }
        finally { listener.Stop(); }
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
        using var factory = new CoreApiFactory(Guid.NewGuid(), settings: new() { ["Vessel:Startup:Warmup:Timeout"] = timeout });
        var error = Assert.ThrowsAny<Exception>(() => factory.Services);
        Assert.Contains("Vessel:Startup:Warmup:Timeout", error.ToString(), StringComparison.Ordinal);
    }

    private sealed record LogEntry(EventId Id, string Message, Exception? Exception);

    private sealed class Logs : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();
        public IReadOnlyList<LogEntry> Entries => [.. entries];
        public ILogger CreateLogger(string categoryName) => new Logger(entries);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue(new(eventId, formatter(state, exception), exception));
        }
    }
}
