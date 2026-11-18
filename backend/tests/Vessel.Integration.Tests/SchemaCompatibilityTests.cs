using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethereum.Signer;
using Vessel.Persistence;
using Vessel.Tests.Identity;
using Vessel.Worker;

namespace Vessel.Tests;

/// <summary>Issue #105: API readiness and worker start share one schema check, which also refuses a newer schema.</summary>
public sealed class SchemaCompatibilityTests
{
    private const string FutureMigration = "29990101000000_FromANewerImage";
    private const string EarlierJournal = "20261117090000_SnapshotPositionsComplete";

    [Fact]
    public void Reasons_name_the_schema_and_migrations_and_never_appear_for_a_current_schema()
    {
        Assert.Null(SchemaCheck.Current.Reason);
        var ahead = new SchemaCheck(SchemaState.Ahead, "journal", [FutureMigration]).Reason!;
        Assert.Contains("journal schema is newer than this image", ahead);
        Assert.Contains(FutureMigration, ahead);
        Assert.Contains("restore the backup", ahead);
        var behind = new SchemaCheck(SchemaState.Behind, "identity", ["1", "2", "3", "4", "5", "6", "7"]).Reason!;
        Assert.Contains("identity schema is behind this image: 7 migration(s)", behind);
        Assert.Contains("(1, 2, 3, 4, 5 and 2 more)", behind);
        Assert.Contains("migrate", behind);
    }

    [PostgresFact]
    public async Task A_schema_equal_to_the_image_is_current_and_ready()
    {
        await using var journal = await CoreDatabase.CreateAsync();
        await using var identity = await IdentityDatabase.CreateAsync();
        Assert.Equal(SchemaCheck.Current, await CheckAsync(journal, identity));
        var (status, body, _) = await ReadinessAsync(journal, identity);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"status":"ready"}""", body);
    }

    [PostgresFact]
    public async Task A_schema_behind_the_image_is_not_ready_as_before()
    {
        await using var journal = await CoreDatabase.CreateAsync(EarlierJournal);
        await using var identity = await IdentityDatabase.CreateAsync();
        var check = await CheckAsync(journal, identity);
        Assert.Equal(SchemaState.Behind, check.State);
        Assert.Equal("journal", check.Schema);
        Assert.Equal(["20261117102251_UnrecognizedRowOmissions"], check.Migrations);
        var (status, body, logs) = await ReadinessAsync(journal, identity);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("""{"status":"unavailable"}""", body);
        Assert.Contains(logs, entry => entry.Level == LogLevel.Warning && entry.Text.Contains("journal schema is behind this image"));
    }

    [PostgresFact]
    public async Task A_journal_schema_ahead_of_the_image_is_not_ready_and_the_log_says_why_once()
    {
        await using var journal = await CoreDatabase.CreateAsync();
        await using var identity = await IdentityDatabase.CreateAsync();
        await using (var db = journal.Context(Guid.NewGuid())) await RecordMigrationAsync(db, FutureMigration);
        var check = await CheckAsync(journal, identity);
        Assert.Equal(new SchemaCheck(SchemaState.Ahead, "journal", [FutureMigration]).Reason, check.Reason);
        var (status, body, logs) = await ReadinessAsync(journal, identity, probes: 3);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        // Publicly only "not ready": no schema, migration or reason.
        Assert.Equal("""{"status":"unavailable"}""", body);
        var logged = Assert.Single(logs, entry => entry.Text.StartsWith("Not ready:", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.Contains(FutureMigration, logged.Text);
        Assert.Contains("newer than this image", logged.Text);
    }

    [PostgresFact]
    public async Task An_identity_schema_ahead_of_the_image_wins_over_a_journal_behind_it()
    {
        await using var journal = await CoreDatabase.CreateAsync(EarlierJournal);
        await using var identity = await IdentityDatabase.CreateAsync();
        await using (var db = identity.CreateDbContext()) await RecordMigrationAsync(db, FutureMigration);
        var check = await CheckAsync(journal, identity);
        Assert.Equal(SchemaState.Ahead, check.State);
        Assert.Equal("identity", check.Schema);
        Assert.Equal([FutureMigration], check.Migrations);
    }

    [PostgresFact]
    public async Task A_worker_refuses_to_start_on_a_newer_schema_and_says_why()
    {
        await using var journal = await CoreDatabase.CreateAsync();
        await using var identity = await IdentityDatabase.CreateAsync();
        await using (var db = journal.Context(Guid.NewGuid())) await RecordMigrationAsync(db, FutureMigration);
        var owner = Guid.NewGuid();
        // The paper module needs no broker, so the whole entrypoint runs; it must stop before the host starts.
        string[] args = [.. Settings(journal, identity, owner).Select(pair => $"--{pair.Key}={pair.Value}"),
            "--Vessel:Worker:Modules:0=paper", "--Vessel:Worker:Deployment=paper", "--environment=Production"];
        var error = new StringWriter();
        var previous = Console.Error;
        Console.SetError(TextWriter.Synchronized(error));
        int code;
        try
        {
            code = await WorkerProgram.Main(args).WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally { Console.SetError(previous); }
        Assert.Equal(1, code);
        Assert.Contains("Vessel worker refused to start.", error.ToString());
        Assert.Contains(FutureMigration, error.ToString());
        Assert.DoesNotContain("Password", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task The_worker_uses_the_same_check_and_lets_a_current_schema_through()
    {
        await using var journal = await CoreDatabase.CreateAsync();
        await using var identity = await IdentityDatabase.CreateAsync();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(Settings(journal, identity, Guid.NewGuid()));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Vessel:Worker:Modules:0"] = "paper", ["Vessel:Worker:Deployment"] = "paper" });
        WorkerHost.Configure(builder);
        using var host = builder.Build();
        Assert.Equal(SchemaCheck.Current, await WorkerHost.SchemaCheckAsync(host.Services, default));
        await using (var db = identity.CreateDbContext()) await RecordMigrationAsync(db, FutureMigration);
        Assert.Equal(SchemaState.Ahead, (await WorkerHost.SchemaCheckAsync(host.Services, default)).State);
    }

    private static Dictionary<string, string?> Settings(CoreDatabase journal, IdentityDatabase identity, Guid owner)
    {
        var settings = identity.ConfigurationValues(owner);
        settings["ConnectionStrings:Vessel"] = journal.ConnectionString;
        // Production hosts refuse the plain-HTTP development origin; the HTTPS one is enough here.
        settings.Remove("Vessel:Identity:Origins:1");
        return settings;
    }

    private static async Task<SchemaCheck> CheckAsync(CoreDatabase journal, IdentityDatabase identity)
    {
        await using var db = journal.Context(Guid.NewGuid());
        await using var identityDb = identity.CreateDbContext();
        return await SchemaCompatibility.CheckAsync([("journal", db), ("identity", identityDb)], default);
    }

    private static async Task RecordMigrationAsync(DbContext db, string id)
    {
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")));
    }

    private static async Task<(HttpStatusCode Status, string Body, IReadOnlyList<(string Category, LogLevel Level, string Text, string? Exception)> Logs)>
        ReadinessAsync(CoreDatabase journal, IdentityDatabase identity, int probes = 1)
    {
        var owner = Guid.NewGuid();
        await identity.SeedAsync(EthECKey.GenerateKey(), owner);
        var logs = new BackgroundWorkLogs();
        await using var factory = new CoreApiFactory(owner, connection: journal.ConnectionString,
            settings: Settings(journal, identity, owner), configure: services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();
        HttpResponseMessage? response = null;
        for (var i = 0; i < probes; i++)
        {
            response?.Dispose();
            response = await client.GetAsync("/health/ready");
        }
        using (response)
            return (response!.StatusCode, await response.Content.ReadAsStringAsync(), logs.Messages);
    }
}
