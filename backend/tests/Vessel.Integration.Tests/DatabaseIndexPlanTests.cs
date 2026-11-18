using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Vessel.Application.Monitoring;
using Vessel.Persistence;
using Xunit.Abstractions;

namespace Vessel.Tests;

/// <summary>
/// Issue #106: on a seeded database (tens of thousands of fills and search texts across owners), the monitor's
/// per-account newest-fill read and the journal search use the two new indexes. The statements are the ones EF sends,
/// captured and explained with their parameters, before and after the migration; the plans are written to the output.
/// </summary>
public sealed class DatabaseIndexPlanTests(ITestOutputHelper output)
{
    private const string Before = "20261117102251_UnrecognizedRowOmissions";
    private const string FillIndex = "IX_imported_fills_OwnerId_AccountId_OccurredAtUtc";
    private const string SearchIndex = "IX_play_search_texts_Folded";

    private sealed class Capture : DbCommandInterceptor
    {
        public List<(string Sql, NpgsqlParameter[] Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [PostgresFact]
    public async Task The_monitor_time_read_and_journal_search_use_the_new_indexes()
    {
        await using var database = await CoreDatabase.CreateAsync(Before);
        var (owner, account) = await SeedAsync(database.ConnectionString);
        var before = await PlansAsync(database, owner, account);
        await using (var migrate = database.Context(owner))
            await migrate.GetService<IMigrator>().MigrateAsync();
        await ExecuteAsync(database.ConnectionString, "ANALYZE");
        var after = await PlansAsync(database, owner, account);

        foreach (var (name, plan) in new[] { ("monitor newest fill, before", before.Monitor), ("monitor newest fill, after", after.Monitor),
                     ("journal search count, before", before.Search), ("journal search count, after", after.Search) })
            output.WriteLine($"--- {name}\n{plan}");
        Assert.DoesNotContain(FillIndex, before.Monitor);
        Assert.Contains(FillIndex, after.Monitor);
        Assert.DoesNotContain(SearchIndex, before.Search);
        Assert.Contains(SearchIndex, after.Search);
    }

    private async Task<(string Monitor, string Search)> PlansAsync(CoreDatabase database, Guid owner, Guid account)
    {
        var capture = new Capture();
        await using var db = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString)
            .AddInterceptors(capture).Options, new CoreOwner(owner));
        var unit = TestUnitOfWork.For(db);
        var monitor = new MonitorStore(db, unit, new NoServerState(), new ConfiguredOwnerDirectory(owner));
        Assert.NotNull(await monitor.CaptureAsync(account, default));
        var newest = capture.Commands.Single(c => c.Sql.Contains("max(", StringComparison.OrdinalIgnoreCase));
        capture.Commands.Clear();
        var found = await new PlayStore(db, unit).SearchAsync(Vessel.Application.Plays.JournalSearch.LikePattern("zephyrine"), "real", 0, 20, default);
        Assert.Equal(12, found.Total);
        // The first statement that filters the folded texts: the count of matching Plays.
        var count = capture.Commands.First(c => c.Sql.Contains("\"Folded\" LIKE", StringComparison.Ordinal));
        return (await ExplainAsync(database.ConnectionString, newest), await ExplainAsync(database.ConnectionString, count));
    }

    private static async Task<string> ExplainAsync(string connection, (string Sql, NpgsqlParameter[] Parameters) statement)
    {
        await using var open = new NpgsqlConnection(connection);
        await open.OpenAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + statement.Sql, open);
        command.Parameters.AddRange(statement.Parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private static async Task ExecuteAsync(string connection, string sql)
    {
        await using var open = new NpgsqlConnection(connection);
        await open.OpenAsync();
        await using var command = new NpgsqlCommand(sql, open) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Three owners with ten real accounts each and 2,000 fills per account (60,000 fills); 2,000 Plays per owner, each
    /// projected with four texts (24,000 texts), where 12 of the target owner's Plays mention a rare word. One account of
    /// the target owner stopped trading two years before the others, the case a time read without the index scans for.
    /// </summary>
    private static async Task<(Guid Owner, Guid Account)> SeedAsync(string connection)
    {
        Guid[] owners = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        await ExecuteAsync(connection, $"""
            CREATE TEMP TABLE seed_owner(n int, id uuid);
            INSERT INTO seed_owner VALUES (0,'{owners[0]}'),(1,'{owners[1]}'),(2,'{owners[2]}');
            INSERT INTO accounts("Id","OwnerId","Name","VenueId","TradingMode","SyncStatus","IsEnabled","KeepExecutionHistory","SettingsRevision")
            SELECT md5('account' || o.n || '-' || a)::uuid, o.id, 'Account ' || a, 'hyperliquid', 'real', 'not-synced', true, false, 1
            FROM seed_owner o CROSS JOIN generate_series(0, 9) a;
            INSERT INTO imported_fills("Id","OwnerId","AccountId","Backfilled","ClosedPnlUsd","ContractId","Direction","ExternalOrderId","Fee",
                "FeeBasis","FeeToken","OccurredAtUtc","PnlBasis","PositionEffect","Price","Quantity","Side","SourceFillId","TransactionHash")
            SELECT gen_random_uuid(), o.id, md5('account' || o.n || '-' || a)::uuid, false, 0, 'BTC', 'Open Long', 'order-' || f, 0,
                'reported', 'USDC', timestamptz '2024-01-01' + f * interval '17 minutes' + a * interval '1 second'
                    - CASE WHEN o.n = 0 AND a = 3 THEN interval '2 years' ELSE interval '0' END, 'gross', 'open', 100, 1,
                'buy', 'fill-' || f, ''
            FROM seed_owner o CROSS JOIN generate_series(0, 9) a CROSS JOIN generate_series(1, 2000) f;
            INSERT INTO plays("Id","OwnerId","AccountId","Title","VenueId","TradingMode","Status","InstrumentSource","Origin","Direction",
                "Plan","Drawings","Review","ReviewLesson","ReviewNextAction","PlanRevision","ReviewRevision","Version","CreatedAtUtc","UpdatedAtUtc")
            SELECT md5('play' || o.n || '-' || p)::uuid, o.id, md5('account' || o.n || '-0')::uuid, 'Play ' || p, 'hyperliquid', 'real', 'Draft',
                'Manual', 'Planned', 'Long', '{"{"}{"}"}', '{"{"}{"}"}', '', '', '', 1, 0, 1,
                timestamptz '2025-01-01' + p * interval '1 hour', timestamptz '2025-01-01' + p * interval '1 hour'
            FROM seed_owner o CROSS JOIN generate_series(1, 2000) p;
            INSERT INTO play_search("OwnerId","PlayId","State","Version","ProjectedAtUtc")
            SELECT o.id, md5('play' || o.n || '-' || p)::uuid, 'active', {Vessel.Application.Plays.JournalSearch.Version}, now()
            FROM seed_owner o CROSS JOIN generate_series(1, 2000) p;
            INSERT INTO play_search_texts("OwnerId","PlayId","Position","Field","Text","Folded")
            SELECT o.id, md5('play' || o.n || '-' || p)::uuid, t, 'note', words, words
            FROM seed_owner o CROSS JOIN generate_series(1, 2000) p CROSS JOIN generate_series(0, 3) t
            CROSS JOIN LATERAL (SELECT CASE WHEN o.n = 0 AND t = 2 AND p % 160 = 0 THEN 'breakout retest held near zephyrine level'
                ELSE 'entry ' || md5(o.n || '-' || p || '-' || t) || ' waited for the pullback and the volume ' || (p * 7 + t) END AS words) w;
            """);
        await ExecuteAsync(connection, "ANALYZE");
        // An account of the target owner that has been idle while the others traded: its newest fill is the oldest.
        await using var open = new NpgsqlConnection(connection);
        await open.OpenAsync();
        await using var account = new NpgsqlCommand($"SELECT md5('account0-3')::uuid", open);
        return (owners[0], (Guid)(await account.ExecuteScalarAsync())!);
    }

    private sealed class NoServerState : IServerStateStore
    {
        public Task<DateTimeOffset?> VenueRetryAtAsync(string venueId, CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> VenueRetriesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(new Dictionary<string, DateTimeOffset>());
        public Task DeferVenueAsync(string venueId, DateTimeOffset retryAtUtc, DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
        public Task<ServerMonitorHeartbeat?> HeartbeatAsync(CancellationToken ct) => Task.FromResult<ServerMonitorHeartbeat?>(null);
        public Task RecordHeartbeatAsync(string deployment, DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
    }
}
