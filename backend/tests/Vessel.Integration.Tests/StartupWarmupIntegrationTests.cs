using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nethereum.Signer;
using Npgsql;
using Vessel.Api;
using Vessel.Tests.Identity;

namespace Vessel.Tests;

public sealed class StartupWarmupIntegrationTests : StartupWarmupTestsSupport
{
    private static readonly string[] JournalSteps = ["journal-model", "overview", "event-head", "notifications", "monitor", "plays", "activity"];

    [PostgresFact]
    public async Task Every_step_succeeds_against_a_read_only_database_then_readiness_applies_its_checks()
    {
        await using var journal = await CoreDatabase.CreateAsync();
        await using var identity = await IdentityDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await identity.SeedAsync(EthECKey.GenerateKey(), owner);
        // PostgreSQL itself refuses any write the warm-up might attempt, which would fail that step.
        var readOnly = new NpgsqlConnectionStringBuilder(journal.ConnectionString) { Options = "-c default_transaction_read_only=on" }
            .ConnectionString;
        var settings = identity.ConfigurationValues(owner);
        settings["ConnectionStrings:Vessel"] = readOnly;
        settings["Vessel:Startup:Warmup:Enabled"] = "true";
        var logs = new Logs();
        await using var factory = new CoreApiFactory(owner, connection: readOnly, settings: settings,
            configure: services => services.AddSingleton<ILoggerProvider>(logs));
        var outcome = await factory.Services.GetRequiredService<StartupWarmup>().Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(outcome.Skipped);
        Assert.False(outcome.TimedOut);
        Assert.Empty(outcome.FailedSteps);
        string[] identitySteps = ["identity-model", "identity-sessions", "identity-sign-in", "identity-signatures"];
        Assert.Equal([.. identitySteps, .. JournalSteps], outcome.Steps);
        Assert.Contains(logs.Entries, entry => entry.Id.Name == "StartupWarmupFinished");
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await using var db = identity.CreateDbContext();
        Assert.Equal(0, await db.Challenges.CountAsync());
        Assert.Equal(0, await db.Sessions.CountAsync());
    }
}
