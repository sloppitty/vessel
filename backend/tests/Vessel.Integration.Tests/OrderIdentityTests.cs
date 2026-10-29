using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Orders have an internal ID and an external identity (account, contract, venue order ID); links reference the internal ID.
/// See docs/architecture/order-identity.md.
/// </summary>
public sealed class OrderIdentityTests : OrderIdentityTestsSupport
{

    [PostgresFact]
    public async Task The_same_venue_order_id_on_two_contracts_is_two_orders()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Main");
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();
        }
        await Upsert(database, owner, account, [Order("7", "BTC", At), Order("7", "ETH", At)]);
        // A later status of the BTC order updates only that row; neither row moves to the other contract.
        await Upsert(database, owner, account, [Order("7", "BTC", At.AddMinutes(1), VenueOrderStatus.Filled)]);

        await using var verify = database.Context(owner);
        var rows = await verify.Orders.AsNoTracking().OrderBy(o => o.ContractId).ToListAsync();
        Assert.Equal([("BTC", "7", "filled"), ("ETH", "7", "open")], rows.Select(o => (o.ContractId, o.ExternalOrderId, o.Status)));
        Assert.NotEqual(rows[0].Id, rows[1].Id);
    }

    [PostgresFact]
    public async Task A_known_order_reported_on_another_native_market_fails_closed_without_changes()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "lighter", "Main");
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();
        }
        await Upsert(database, owner, account, [Order("7", "BTC", At, native: "1")]);

        await Assert.ThrowsAsync<VenueReadException>(() =>
            Upsert(database, owner, account, [Order("8", "BTC", At), Order("7", "BTC", At.AddMinutes(1), VenueOrderStatus.Filled, native: "2")]));
        // Two rows for one new order that disagree on the native market are refused too.
        await Assert.ThrowsAsync<VenueReadException>(() =>
            Upsert(database, owner, account, [Order("9", "BTC", At, native: "1"), Order("9", "BTC", At.AddMinutes(1), native: "2")]));

        await using var verify = database.Context(owner);
        var row = Assert.Single(await verify.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(("7", "1", "open"), (row.ExternalOrderId, row.VenueContractId, row.Status));
    }

    [PostgresFact]
    public async Task An_observation_without_a_native_market_keeps_the_established_one()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "lighter", "Main");
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();
        }
        await Upsert(database, owner, account, [Order("7", "BTC", At, native: "1")]);
        await Upsert(database, owner, account, [Order("7", "BTC", At.AddMinutes(1))]);

        await Assert.ThrowsAsync<VenueReadException>(() =>
            Upsert(database, owner, account, [Order("7", "BTC", At.AddMinutes(2), VenueOrderStatus.Filled, native: "2")]));

        await using var verify = database.Context(owner);
        var row = Assert.Single(await verify.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(("7", "1", "open"), (row.ExternalOrderId, row.VenueContractId, row.Status));
    }

    [PostgresFact]
    public async Task A_fresh_database_migrates_to_the_current_model()
    {
        await using var database = await CoreDatabase.CreateAsync(TestOrders.BeforeOrderIdentity);
        await using var db = database.Context(Guid.NewGuid());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(0L, await Scalar(db, """
            SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND column_name = 'OrderId'
                AND table_name IN ('imported_orders', 'imported_fills', 'play_order_links', 'monitor_exit_watches')
            """));
    }

    [PostgresTheory]
    [InlineData("imported_orders")]
    [InlineData("imported_fills")]
    [InlineData("play_order_links")]
    public async Task The_order_identity_migration_refuses_a_database_with_order_facts_or_links(string table)
    {
        await using var database = await CoreDatabase.CreateAsync(TestOrders.BeforeOrderIdentity);
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Main");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        // The intentionally historical schema has no paper-mode columns; keep its fixture historical too.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts ("Id","OwnerId","VenueId","Name","SyncStatus")
            VALUES ({account.Id},{owner},{account.VenueId},{account.Name},{account.SyncStatus})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO plays ("Id","OwnerId","AccountId","VenueId","ContractId","InstrumentSource","Status","Plan","Direction")
            VALUES ({play.Id},{owner},{account.Id},{play.VenueId},{play.ContractId},
                {play.InstrumentSource.ToString()},{play.Status.ToString()},{play.Plan}::jsonb,{play.Direction})
            """);
        // Historical columns: "OrderId" before the change.
        switch (table)
        {
            case "imported_orders":
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO imported_orders ("Id", "OwnerId", "AccountId", "ContractId", "OrderId", "Side", "OrderType", "LimitPrice", "ReduceOnly",
                        "IsPositionTpsl", "OriginalSize", "RemainingSize", "PlacedAtUtc", "Status", "VenueStatus", "StatusAtUtc", "ObservedAtUtc")
                    VALUES ({Guid.NewGuid()}, {owner}, {account.Id}, 'BTC', '7', 'buy', 'Limit', 100, false, false, 1, 1, now(), 'open', 'open', now(), now())
                    """);
                break;
            case "imported_fills":
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO imported_fills ("Id", "OwnerId", "AccountId", "ContractId", "SourceFillId", "Side", "Direction", "PositionEffect", "Price",
                        "Quantity", "Fee", "FeeToken", "FeeBasis", "ClosedPnlUsd", "PnlBasis", "OccurredAtUtc", "OrderId", "TransactionHash")
                    VALUES ({Guid.NewGuid()}, {owner}, {account.Id}, 'BTC', 'f', 'buy', 'Open Long', 'open', 100, 1, 0, 'USDC', 'reported', 0, 'gross', now(), '7', '0x1')
                    """);
                break;
            default:
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO play_order_links ("Id", "OwnerId", "PlayId", "AccountId", "OrderId", "Role", "EntryId", "State", "Source", "CreatedAtUtc", "UpdatedAtUtc")
                    VALUES ({Guid.NewGuid()}, {owner}, {play.Id}, {account.Id}, '7', 'Entry', 'entry-1', 'Dismissed', 'Owner', now(), now())
                    """);
                break;
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Equal("P0001", error.SqlState);
        Assert.Contains("recreate the database", error.MessageText);
        // Nothing changed: the row and the historical column remain, and the migration is still pending.
        Assert.Equal(1L, await Scalar(db, $"SELECT count(*) FROM {table}"));
        Assert.Equal(1L, await Scalar(db, $"""
            SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = '{table}' AND column_name = 'OrderId'
            """));
        Assert.Equal(TestOrders.BeforeOrderIdentity, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    private static VenueOrder Order(string id, string contract, DateTimeOffset at, VenueOrderStatus status = VenueOrderStatus.Open,
        string? native = null) =>
        new(id, contract, "buy", "Limit", 100, null, false, false, 1, status == VenueOrderStatus.Open ? 1 : 0, At,
            status, status.ToString().ToLowerInvariant(), at, native);

    private static async Task Upsert(CoreDatabase database, Guid owner, Account account, IReadOnlyList<VenueOrder> orders)
    {
        await using var db = database.Context(owner);
        var store = new ExecutionStore(db, TestUnitOfWork.For(db));
        await TestUnitOfWork.For(db).ExecuteAsync(async () =>
        {
            await store.UpsertOrdersAsync(account, orders, At.AddMinutes(5), default);
            return true;
        }, default);
    }

    private static async Task<object?> Scalar(VesselDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
