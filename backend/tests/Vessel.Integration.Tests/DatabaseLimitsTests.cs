using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Infrastructure;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Issue #106: request and worker connections carry a lock and a statement timeout; a blocked statement fails within
/// the limit with a message the browser can show; waits designed to block and offline commands are exempt.
/// </summary>
public sealed class DatabaseLimitsTests
{
    private static Dictionary<string, string?> Limits(string connection) => new()
    {
        ["ConnectionStrings:Vessel"] = connection,
        ["Vessel:Database:LockTimeout"] = "00:00:01",
        ["Vessel:Database:StatementTimeout"] = "00:00:02",
    };

    private static ServiceProvider Services(Dictionary<string, string?> settings, bool offline = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVesselInfrastructure(configuration);
        if (offline) services.AddVesselOfflinePersistence(configuration);
        else services.AddVesselPersistence(configuration);
        services.Replace(ServiceDescriptor.Singleton<IJournalOwnerDirectory>(new ConfiguredOwnerDirectory(Guid.NewGuid())));
        return services.BuildServiceProvider();
    }

    private static async Task<string> ShowAsync(NpgsqlConnection connection, string setting)
    {
        await using var command = new NpgsqlCommand($"SHOW {setting}", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    // One physical connection, so the second borrow is the connection the first one changed.
    private static string Pooled(CoreDatabase database) =>
        new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = true, MaxPoolSize = 1 }.ConnectionString;

    [PostgresFact]
    public async Task Request_and_worker_connections_carry_the_limits_and_the_pool_restores_them()
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var provider = Services(Limits(Pooled(database)));
        var data = provider.GetRequiredService<NpgsqlDataSource>();
        await using (var first = await data.OpenConnectionAsync())
        {
            Assert.Equal("1s", await ShowAsync(first, "lock_timeout"));
            Assert.Equal("2s", await ShowAsync(first, "statement_timeout"));
            // An exemption a borrower set for its session must not reach the next borrower.
            await using var lift = new NpgsqlCommand("SET statement_timeout = 0; SET lock_timeout = 0", first);
            await lift.ExecuteNonQueryAsync();
        }
        await using var second = await data.OpenConnectionAsync();
        Assert.Equal("1s", await ShowAsync(second, "lock_timeout"));
        Assert.Equal("2s", await ShowAsync(second, "statement_timeout"));
    }

    [PostgresFact]
    public async Task Offline_commands_connect_without_the_limits()
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var provider = Services(Limits(database.ConnectionString), offline: true);
        await using var connection = await provider.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        Assert.Equal("0", await ShowAsync(connection, "lock_timeout"));
        Assert.Equal("0", await ShowAsync(connection, "statement_timeout"));
    }

    [PostgresFact]
    public async Task A_blocked_statement_fails_within_the_lock_timeout_as_database_busy()
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(106106)", holder)) await hold.ExecuteScalarAsync();
        await using var provider = Services(Limits(database.ConnectionString));
        await using var connection = await provider.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var wait = new NpgsqlCommand("SELECT pg_advisory_xact_lock(106106)", connection, transaction);
        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<PostgresException>(() => wait.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"The wait took {clock.Elapsed}.");
        Assert.True(DatabaseBusy.Is(error));
    }

    [PostgresFact]
    public async Task A_long_statement_fails_at_the_statement_timeout_as_database_busy()
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var provider = Services(Limits(database.ConnectionString));
        await using var connection = await provider.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var sleep = new NpgsqlCommand("SELECT pg_sleep(20)", connection);
        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<PostgresException>(() => sleep.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.QueryCanceled, error.SqlState);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"The statement ran {clock.Elapsed}.");
        Assert.True(DatabaseBusy.Is(error));
    }

    [PostgresFact]
    public async Task The_venue_budget_wait_blocks_by_design_past_the_lock_timeout_and_keeps_the_limit_afterwards()
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var provider = Services(Limits(database.ConnectionString));
        // The paper scheduler holds this key over one public candle read, longer than the lock timeout.
        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended('vessel-venue-budget:hyperliquid',0))", holder))
            await hold.ExecuteScalarAsync();
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended('vessel-venue-budget:hyperliquid',0))", holder);
            await unlock.ExecuteScalarAsync();
        });
        await using var scope = provider.GetRequiredService<IOwnerScopeFactory>().Create(Guid.NewGuid());
        var store = scope.Services.GetRequiredService<IMonitorStore>();
        var db = scope.Services.GetRequiredService<VesselDbContext>();
        var lockLimit = await scope.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(async () =>
        {
            Assert.True(await store.ReadCapacityAsync("hyperliquid", DateTimeOffset.UtcNow, default));
            return await db.Database.SqlQueryRaw<string>("SELECT current_setting('lock_timeout') AS \"Value\"").SingleAsync();
        }, default);
        await release;
        Assert.Equal("1s", lockLimit);
    }

    [PostgresFact]
    public async Task A_request_blocked_on_a_lock_answers_503_with_a_message_the_browser_can_show()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString, settings: Limits(database.ConnectionString));
        using var client = factory.AuthorizedClient();
        // Another unit holds the owner's management lock, as a long portfolio move would.
        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using (var hold = new NpgsqlCommand($"SELECT pg_advisory_lock(hashtextextended('vessel-management:{owner}',0))", holder))
            await hold.ExecuteScalarAsync();
        var clock = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync("/api/portfolios", new { name = "Blocked" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"The request took {clock.Elapsed}.");
        Assert.Equal("1", Assert.Single(response.Headers.RetryAfter!.ToString().Split(',')));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(DatabaseBusy.Message, problem!.Detail);
        await using (var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock(hashtextextended('vessel-management:{owner}',0))", holder))
            await unlock.ExecuteScalarAsync();
        Assert.True((await client.PostAsJsonAsync("/api/portfolios", new { name = "Unblocked" })).IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("Vessel:Database:LockTimeout", "soon", "Vessel:Database:LockTimeout must be")]
    [InlineData("Vessel:Database:LockTimeout", "00:00:00", "Vessel:Database:LockTimeout must be")]
    [InlineData("Vessel:Database:StatementTimeout", "02:00:00", "Vessel:Database:StatementTimeout must be")]
    [InlineData("Vessel:Database:LockTimeout", "00:00:26", "LockTimeout must not be longer")]
    [InlineData("Vessel:Database:StatementTimeout", "00:00:30", "shorter than the connection's Command Timeout")]
    public void Invalid_settings_are_refused_by_name(string key, string value, string message)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=secret-value",
            [key] = value,
        }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => DatabaseLimits.Read(configuration));
        Assert.Contains(message, error.Message);
        Assert.DoesNotContain("secret-value", error.Message);
    }

    [Fact]
    public void Defaults_stay_inside_the_client_command_timeout()
    {
        var limits = DatabaseLimits.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = "Host=127.0.0.1;Database=unused",
        }).Build());
        Assert.Equal(DatabaseLimits.Default, limits);
        Assert.True(limits.StatementTimeout < TimeSpan.FromSeconds(new NpgsqlConnectionStringBuilder().CommandTimeout));
    }
}
