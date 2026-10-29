using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Vessel.Tests;

public sealed class WorkspaceReviewMigrationTests
{
    private const string PreviousMigration = "20261001125452_StablecoinWallet";

    [PostgresFact]
    public async Task Migration_rejects_normalized_duplicates_before_changing_any_accounts_or_history()
    {
        await using var database = await CoreDatabase.CreateAsync(PreviousMigration); var owner = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await using var db = database.Context(owner); var lower = "0x" + new string('a', 40); var upper = "0x" + new string('A', 40);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"Name\", \"VenueId\", \"Address\", \"SyncStatus\", \"IsEnabled\") VALUES ({first}, {owner}, 'First', 'hyperliquid', {lower}, 'synced', true), ({second}, {owner}, 'Disabled alias', 'hyperliquid', {upper}, 'synced', false)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO account_snapshots (\"OwnerId\", \"AccountId\", \"ObservedAtUtc\", \"ValueScope\", \"AccountValueUsd\") VALUES ({owner}, {first}, now(), 'primary-perpetual-dex', 12.00000000000000000000000001)");
        var play = Guid.NewGuid(); await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO plays (\"Id\", \"OwnerId\", \"AccountId\", \"VenueId\", \"ContractId\", \"Status\") VALUES ({play}, {owner}, {first}, 'hyperliquid', 'BTC', 'Active')");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Equal("P0001", error.SqlState); Assert.Contains("Resolve them explicitly", error.MessageText);
        Assert.DoesNotContain(lower, error.MessageText); Assert.DoesNotContain(upper, error.MessageText); Assert.DoesNotContain(owner.ToString(), error.MessageText);
        Assert.Equal(2L, await Scalar(db, "SELECT count(*) FROM accounts"));
        Assert.Equal(upper, await Scalar(db, "SELECT \"Address\" FROM accounts WHERE \"Name\" = 'Disabled alias'"));
        Assert.Equal(false, await Scalar(db, "SELECT \"IsEnabled\" FROM accounts WHERE \"Name\" = 'Disabled alias'"));
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM account_snapshots")); Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM plays"));
        Assert.Equal(0L, await Scalar(db, "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'accounts' AND column_name = 'SettingsRevision'"));
        Assert.Equal(PreviousMigration, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    [PostgresFact]
    public async Task Migration_normalizes_unique_sources_keeps_null_addresses_and_initializes_revision()
    {
        await using var database = await CoreDatabase.CreateAsync(PreviousMigration); var owner = Guid.NewGuid(); var foreign = Guid.NewGuid();
        await using var db = database.Context(owner); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var manual = Guid.NewGuid(); var manual2 = Guid.NewGuid();
        var address = "0x" + new string('A', 40);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"Name\", \"VenueId\", \"Address\", \"SyncStatus\", \"IsEnabled\") VALUES ({first}, {owner}, 'First', 'hyperliquid', {address}, 'not-synced', false), ({second}, {foreign}, 'Foreign', 'hyperliquid', {address}, 'not-synced', true), ({manual}, {owner}, 'Manual', 'manual', NULL, 'manual', true), ({manual2}, {owner}, 'Manual2', 'manual', NULL, 'manual', true)");
        await db.Database.MigrateAsync();
        var accounts = await db.Accounts.ToListAsync(); Assert.Equal(3, accounts.Count);
        Assert.All(accounts, a => Assert.Equal(1, a.SettingsRevision));
        var venue = accounts.Single(a => a.Id == first); Assert.Equal(address.ToLowerInvariant(), venue.Address); Assert.False(venue.IsEnabled); Assert.Equal("First", venue.Name);
        Assert.All(accounts.Where(a => a.VenueId == "manual"), a => Assert.Null(a.Address));
        Assert.False(db.Database.HasPendingModelChanges());
        var duplicate = Guid.NewGuid(); var lower = address.ToLowerInvariant();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"Name\", \"VenueId\", \"Address\", \"SourceId\", \"SyncStatus\") VALUES ({duplicate}, {owner}, 'Alias', 'hyperliquid', {lower}, {lower}, 'not-synced')"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState); Assert.Equal("UX_accounts_owner_venue_source", error.ConstraintName);
        var badNormalization = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"Name\", \"VenueId\", \"Address\", \"SourceId\", \"SyncStatus\") VALUES ({duplicate}, {owner}, 'Mixed case', 'hyperliquid', {address}, {address}, 'not-synced')"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badNormalization.SqlState); Assert.Equal("CK_accounts_normalized_address", badNormalization.ConstraintName);
    }

    private static async Task<object?> Scalar(Vessel.Persistence.VesselDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
