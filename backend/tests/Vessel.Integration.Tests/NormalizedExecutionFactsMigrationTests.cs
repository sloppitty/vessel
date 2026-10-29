using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class NormalizedExecutionFactsMigrationTests
{
    private const string PreviousMigration = "20261005111008_SizingSettings";

    [PostgresFact]
    public async Task Migration_turns_hyperliquid_sides_and_directions_into_vessel_facts()
    {
        await using var database = await CoreDatabase.CreateAsync(PreviousMigration);
        var owner = Guid.NewGuid(); var account = Guid.NewGuid();
        await using var db = database.Context(owner);
        var address = "0x" + new string('b', 40);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"Name\", \"VenueId\", \"Address\", \"SyncStatus\", \"IsEnabled\") VALUES ({account}, {owner}, 'Main', 'hyperliquid', {address}, 'synced', true)");
        foreach (var (id, side, direction) in new[]
        {
            ("1", "B", "Open Long"), ("2", "A", "Close Long"), ("3", "A", "Long > Short"), ("4", "B", "Liquidated Isolated Short")
        })
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO imported_fills ("Id", "OwnerId", "AccountId", "ContractId", "SourceFillId", "Side", "Direction", "Price", "Quantity", "Fee", "FeeToken", "ClosedPnlUsd", "OccurredAtUtc", "OrderId", "TransactionHash")
                VALUES ({Guid.NewGuid()}, {owner}, {account}, 'BTC', {id}, {side}, {direction}, 100, 1, 0.1, 'USDC', 0, now(), {id}, '0x1')
                """);
        foreach (var (id, side) in new[] { ("10", "B"), ("11", "A") })
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO imported_orders ("Id", "OwnerId", "AccountId", "ContractId", "OrderId", "Side", "OrderType", "LimitPrice", "ReduceOnly", "IsPositionTpsl", "OriginalSize", "RemainingSize", "PlacedAtUtc", "Status", "VenueStatus", "StatusAtUtc", "ObservedAtUtc")
                VALUES ({Guid.NewGuid()}, {owner}, {account}, 'BTC', {id}, {side}, 'Limit', 100, false, false, 1, 1, now(), 'open', 'open', now(), now())
                """);

        // The order identity change refuses retained facts, so this data migration is checked just before it.
        await db.GetService<IMigrator>().MigrateAsync(TestOrders.BeforeOrderIdentity);

        var fills = await db.Database.SqlQueryRaw<MigratedFill>("""
            SELECT "SourceFillId", "Side", "PositionEffect", "Direction", "FeeBasis", "PnlBasis" FROM imported_fills
            """).ToDictionaryAsync(f => f.SourceFillId);
        Assert.Equal((ExecutionFacts.Buy, ExecutionFacts.Open, "Open Long"), (fills["1"].Side, fills["1"].PositionEffect, fills["1"].Direction));
        Assert.Equal((ExecutionFacts.Sell, ExecutionFacts.Close), (fills["2"].Side, fills["2"].PositionEffect));
        Assert.Equal((ExecutionFacts.Sell, ExecutionFacts.Flip), (fills["3"].Side, fills["3"].PositionEffect));
        Assert.Equal((ExecutionFacts.Buy, ExecutionFacts.Unknown), (fills["4"].Side, fills["4"].PositionEffect));
        Assert.All(fills.Values, f => Assert.Equal((ExecutionFacts.FeeReported, ExecutionFacts.PnlGross), (f.FeeBasis, f.PnlBasis)));
        var orders = await db.Database.SqlQueryRaw<MigratedOrder>("""SELECT "OrderId", "Side" FROM imported_orders""")
            .ToDictionaryAsync(o => o.OrderId);
        Assert.Equal((ExecutionFacts.Buy, ExecutionFacts.Sell), (orders["10"].Side, orders["11"].Side));
        Assert.False(db.Database.HasPendingModelChanges());

        // The constraints keep venue encodings out from now on.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE imported_fills SET \"Side\" = 'B' WHERE \"SourceFillId\" = '1'"));
    }

    private sealed record MigratedFill(string SourceFillId, string Side, string PositionEffect, string Direction, string FeeBasis, string PnlBasis);
    private sealed record MigratedOrder(string OrderId, string Side);
}
