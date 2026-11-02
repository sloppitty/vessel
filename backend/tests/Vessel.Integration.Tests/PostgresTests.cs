using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Application.Ownership;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class PostgresTests
{
    [PostgresFact]
    public async Task Migration_persists_concurrent_active_plays_and_filters_both_owners()
    {
        await using var database = await TestDatabase.CreateAsync();
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        var accountA = new Account(Guid.NewGuid(), firstOwner, "hyperliquid", "First owner");
        var accountB = new Account(Guid.NewGuid(), secondOwner, "hyperliquid", "Second owner");
        var instrument = new PerpetualInstrument("hyperliquid", "BTC-PERP");
        await using (var db = database.Context(firstOwner))
        {
            db.Accounts.Add(accountA);
            db.Plays.AddRange(TestPlays.Create(accountA, instrument), TestPlays.Create(accountA, instrument));
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(secondOwner))
        {
            db.Accounts.Add(accountB);
            db.Plays.Add(TestPlays.Create(accountB, instrument));
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(firstOwner))
        {
            Assert.Single(await db.Accounts.ToListAsync());
            var plays = await db.Plays.ToListAsync();
            Assert.Equal(2, plays.Count);
            Assert.All(plays, p => { Assert.Equal(firstOwner, p.OwnerId); Assert.Equal(PlayStatus.Draft, p.Status); Assert.Equal(instrument, p.Instrument); });
            Assert.Null(await db.Accounts.SingleOrDefaultAsync(x => x.Id == accountB.Id));
            Assert.Equal(3, await db.Plays.IgnoreQueryFilters().CountAsync());
        }
        await using (var db = database.Context(secondOwner))
        {
            Assert.Equal(accountB.Id, (await db.Accounts.SingleAsync()).Id);
            Assert.Equal(secondOwner, (await db.Plays.SingleAsync()).OwnerId);
        }
        await using (var db = database.Context(Guid.NewGuid()))
        {
            Assert.Empty(await db.Accounts.ToListAsync());
            Assert.Empty(await db.Plays.ToListAsync());
        }
    }

    [PostgresFact]
    public async Task Context_rejects_writing_another_owners_account()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.Context(Guid.NewGuid());
        db.Accounts.Add(new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Foreign"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task Composite_foreign_key_rejects_a_play_linked_to_another_owners_account()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Trading");
        await using var db = database.Context(owner);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        // Bypass the application deliberately to prove PostgreSQL enforces ownership.
        var foreignOwner = Guid.NewGuid();
        var playId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO plays (\"Id\", \"OwnerId\", \"AccountId\", \"VenueId\", \"ContractId\", \"Status\", \"Direction\") VALUES ({playId}, {foreignOwner}, {account.Id}, 'manual', 'BTC-PERP', 'Active', 'long')"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [PostgresFact]
    public async Task Explicit_filter_bypass_does_not_allow_modifying_foreign_records()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Trading");
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account);
            db.Plays.Add(TestPlays.Create(account, new PerpetualInstrument("manual", "BTC-PERP")));
            await db.SaveChangesAsync();
        }
        await using var foreign = database.Context(Guid.NewGuid());
        (await foreign.Plays.IgnoreQueryFilters().SingleAsync()).Annotate("Foreign", "{}", "", DateTimeOffset.UnixEpoch);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => foreign.SaveChangesAsync());
    }

    private sealed class TestDatabase(string adminConnection, string schema, string scopedConnection) : IAsyncDisposable
    {
        public VesselDbContext Context(Guid ownerId) => new(
            new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(scopedConnection).Options, new TestOwner(ownerId));

        public static async Task<TestDatabase> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable("Vessel_TEST_POSTGRES")!;
            var schema = "vessel_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection);
            await command.ExecuteNonQueryAsync();
            var scoped = new NpgsqlConnectionStringBuilder(admin) { SearchPath = schema, Pooling = false }.ConnectionString;
            var database = new TestDatabase(admin, schema, scoped);
            try
            {
                await using var db = database.Context(Guid.NewGuid());
                await db.Database.MigrateAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
        private sealed record TestOwner(Guid OwnerId) : IJournalOwnerContext;
    }
}
