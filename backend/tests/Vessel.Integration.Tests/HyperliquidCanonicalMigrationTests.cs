using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class HyperliquidCanonicalMigrationTests : HyperliquidCanonicalMigrationTestsSupport
{

    private const string BeforeMigration = "20261005174006_VenueContractIds";

    [PostgresFact]
    public async Task Canonical_migration_rewrites_only_exact_venue_symbols_preserves_notes_links_and_is_idempotent()
    {
        await using var database = await CoreDatabase.CreateAsync(BeforeMigration);
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        // Isolate the data recipe from concurrent SourceId/credential schema work.
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE account_positions ADD COLUMN "VenueContractId" varchar(128) NULL""");
        var account = await Account(db, owner, "hyperliquid");
        var otherVenue = await Account(db, owner, "risex");
        var otherOwner = Guid.NewGuid();
        var otherAccount = await Account(db, otherOwner, "hyperliquid");
        var play = await Seed(db, owner, account, "hyperliquid", "kPEPE");
        var btc = await Seed(db, owner, account, "hyperliquid", "BTC");
        var unknown = await Seed(db, owner, account, "hyperliquid", "kUNKNOWN");
        var risex = await Seed(db, owner, otherVenue, "risex", "kPEPE");
        var anotherOwner = await Seed(db, otherOwner, otherAccount, "hyperliquid", "kDOGS");
        var manual = await Play(db, owner, account, "hyperliquid", "kPEPE", "Manual");
        var retainedDrawing = """{"hyperliquid:kPEPE":[{"id":"retained"}]}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE plays SET "Drawings" = "Drawings" || CAST({retainedDrawing} AS jsonb) WHERE "Id" = {risex}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO play_order_links ("Id", "OwnerId", "PlayId", "AccountId", "OrderId", "Role", "State", "Source", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES ({Guid.NewGuid()}, {owner}, {play}, {account}, 'kPEPE', 'Entry', 'Linked', 'Owner', now(), now())
            """);
        var planBefore = await db.Plays.Where(p => p.Id == play).Select(p => p.Plan).SingleAsync();
        await db.Database.ExecuteSqlRawAsync(UpSql);
        await db.Database.ExecuteSqlRawAsync(UpSql);
        // This is a historical schema. Read only its columns, not today's full Play model.
        var plays = await db.Database.SqlQueryRaw<MigratedPlay>(
            """SELECT "Id", "ContractId", "Plan"::text AS "Plan", "Drawings"::text AS "Drawings" FROM plays""").ToDictionaryAsync(p => p.Id);
        Assert.Equal("1000PEPE", plays[play].ContractId);
        Assert.Equal("1000DOGS", plays[anotherOwner].ContractId);
        Assert.Equal("BTC", plays[btc].ContractId);
        Assert.Equal("kUNKNOWN", plays[unknown].ContractId);
        Assert.Equal("kPEPE", plays[risex].ContractId);
        Assert.Equal("kPEPE", plays[manual].ContractId);
        Assert.Equal(planBefore, plays[play].Plan);
        using var drawing = JsonDocument.Parse(plays[play].Drawings);
        Assert.False(drawing.RootElement.TryGetProperty("hyperliquid:kPEPE", out _));
        Assert.Equal("kPEPE", drawing.RootElement.GetProperty("hyperliquid:1000PEPE")[0].GetProperty("text").GetString());
        Assert.True(drawing.RootElement.TryGetProperty("risex:kPEPE", out _));
        using var manualDrawing = JsonDocument.Parse(plays[manual].Drawings);
        Assert.Equal("kPEPE", manualDrawing.RootElement.GetProperty("hyperliquid:1000PEPE")[0].GetProperty("text").GetString());
        using var switchedDrawing = JsonDocument.Parse(plays[risex].Drawings);
        Assert.Equal("retained", switchedDrawing.RootElement.GetProperty("hyperliquid:1000PEPE")[0].GetProperty("id").GetString());
        Assert.True(switchedDrawing.RootElement.TryGetProperty("risex:kPEPE", out _));
        // Historical columns ("OrderId" before the order identity change), read without today's model.
        var fill = await db.Database.SqlQueryRaw<MigratedFill>("""
            SELECT "ContractId", "VenueContractId", "Price", "Quantity" FROM imported_fills WHERE "AccountId" = {0} AND "SourceFillId" = 'kPEPE'
            """, account).SingleAsync();
        Assert.Equal(("1000PEPE", "kPEPE", 0.0012300m, 123m), (fill.ContractId, fill.VenueContractId, fill.Price, fill.Quantity));
        var order = await db.Database.SqlQueryRaw<MigratedOrder>("""
            SELECT "ContractId", "VenueContractId" FROM imported_orders WHERE "AccountId" = {0} AND "OrderId" = 'kPEPE'
            """, account).SingleAsync();
        Assert.Equal(("1000PEPE", "kPEPE"), (order.ContractId, order.VenueContractId));
        Assert.Equal("kPEPE", await db.Database.SqlQueryRaw<string>("""SELECT "OrderId" AS "Value" FROM play_order_links""").SingleAsync());
        Assert.Equal("1000PEPE", await db.Database.SqlQueryRaw<string>(
            """SELECT "ContractId" AS "Value" FROM account_positions WHERE "VenueContractId" = 'kPEPE'""").SingleAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>(
            """SELECT count(*)::int AS "Value" FROM play_plan_revisions WHERE "Plan" -> 'notes' ->> 'thesis' = 'keep kPEPE and hyperliquid:kPEPE verbatim'""").SingleAsync());
    }

    private sealed record MigratedPlay(Guid Id, string? ContractId, string Plan, string Drawings);
    private sealed record MigratedFill(string ContractId, string? VenueContractId, decimal Price, decimal Quantity);
    private sealed record MigratedOrder(string ContractId, string? VenueContractId);

    [PostgresFact]
    public async Task Canonical_migration_rejects_drawing_collisions_without_overwriting_or_partial_rewrites()
    {
        await using var database = await CoreDatabase.CreateAsync(BeforeMigration);
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE account_positions ADD COLUMN "VenueContractId" varchar(128) NULL""");
        var account = await Account(db, owner, "hyperliquid");
        var play = await Seed(db, owner, account, "hyperliquid", "kPEPE");
        var collisionPlay = await Seed(db, owner, account, "hyperliquid", "kSHIB");
        var conflict = """{"hyperliquid:1000SHIB":[{"id":"keep"}]}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE plays SET "Drawings" = "Drawings" || CAST({conflict} AS jsonb) WHERE "Id" = {collisionPlay}
            """);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(UpSql));
        Assert.Equal("kPEPE", await db.Plays.Where(p => p.Id == play).Select(p => p.ContractId).SingleAsync());
        Assert.Equal("kSHIB", await db.Plays.Where(p => p.Id == collisionPlay).Select(p => p.ContractId).SingleAsync());
        Assert.All(await db.Fills.Select(f => f.VenueContractId).ToListAsync(), Assert.Null);
    }

    private static async Task<Guid> Account(VesselDbContext db, Guid owner, string venue)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts ("Id", "OwnerId", "Name", "VenueId", "SyncStatus", "IsEnabled")
            VALUES ({id}, {owner}, 'Test', {venue}, 'not-synced', true)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO account_snapshots ("OwnerId", "AccountId", "ObservedAtUtc", "ValueScope")
            VALUES ({owner}, {id}, now(), 'primary-perpetual-dex')
            """);
        return id;
    }

    private static async Task<Guid> Play(VesselDbContext db, Guid owner, Guid account, string venue, string native, string source = "Venue")
    {
        var id = Guid.NewGuid();
        var plan = """{"notes":{"thesis":"keep kPEPE and hyperliquid:kPEPE verbatim"}}""";
        var drawings = $$"""{"{{venue}}:{{native}}":[{"id":"one","text":"{{native}}"}],"risex:kPEPE":[]}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO plays ("Id", "OwnerId", "AccountId", "VenueId", "ContractId", "Status", "InstrumentSource", "Plan", "Drawings")
            VALUES ({id}, {owner}, {account}, {venue}, {native}, 'Draft', {source}, CAST({plan} AS jsonb), CAST({drawings} AS jsonb))
            """);
        if (native == "kPEPE" && source == "Venue")
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO play_plan_revisions ("Id", "OwnerId", "PlayId", "Number", "Status", "Plan", "Reason", "CreatedAtUtc")
                VALUES ({Guid.NewGuid()}, {owner}, {id}, 1, 'Draft', CAST({plan} AS jsonb), 'test', now())
                """);
        return id;
    }

    private static async Task<Guid> Seed(VesselDbContext db, Guid owner, Guid account, string venue, string native)
    {
        var play = await Play(db, owner, account, venue, native);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO account_positions ("OwnerId", "AccountId", "ContractId", "SignedQuantity", "EntryPrice", "UnrealizedPnlUsd", "MarginUsedUsd")
            VALUES ({owner}, {account}, {native}, 123, 0.0012300, 0, 0.1)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO imported_fills ("Id", "OwnerId", "AccountId", "ContractId", "SourceFillId", "Side", "Direction", "Price", "Quantity", "Fee", "FeeToken", "ClosedPnlUsd", "OccurredAtUtc", "OrderId", "TransactionHash")
            VALUES ({Guid.NewGuid()}, {owner}, {account}, {native}, {native}, 'buy', 'Open Long', 0.0012300, 123, 0.01, 'USDC', 0, now(), {native}, 'fixture')
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO imported_orders ("Id", "OwnerId", "AccountId", "ContractId", "OrderId", "Side", "OrderType", "LimitPrice", "ReduceOnly", "IsPositionTpsl", "OriginalSize", "RemainingSize", "PlacedAtUtc", "Status", "VenueStatus", "StatusAtUtc", "ObservedAtUtc")
            VALUES ({Guid.NewGuid()}, {owner}, {account}, {native}, {native}, 'buy', 'Limit', 0.0012300, false, false, 123, 123, now(), 'open', 'open', now(), now())
            """);
        return play;
    }
}
