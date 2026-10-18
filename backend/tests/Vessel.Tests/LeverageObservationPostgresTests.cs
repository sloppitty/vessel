using Microsoft.EntityFrameworkCore;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>October 18, 2026: refreshes retain observed position leverage; Activity attributes it after the position closes.</summary>
public sealed class LeverageObservationPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    private static PerpetualVenueReadResult Read(int minute, IReadOnlyList<VenuePosition> positions, params VenueFill[] fills) =>
        new(new(Start.AddMinutes(minute), "primary-perpetual-dex", 100, 100, 1, positions), [], fills, "Bounded history.");

    private static VenuePosition Position(decimal quantity, int? leverage, decimal entry = 100, string contract = "BTC", string? native = null) =>
        new(contract, quantity, entry, 0, 1, leverage, native);

    private static VenueFill Fill(string id, int minute, string side, string effect, int? leverage = null) =>
        new(id, "BTC", side, effect == "open" ? "Open Long" : "Close Long", 100, 1, 0, "USDC", effect == "open" ? 0 : 5, Start.AddMinutes(minute),
            "order-" + id, "", "", effect, Leverage: leverage);

    private static async Task SaveAsync(VesselDbContext db, Guid accountId, PerpetualVenueReadResult read)
    {
        var work = TestUnitOfWork.For(db);
        var store = new WorkspaceStore(db, work);
        await work.ExecuteAsync(() => store.WithAccountLockAsync(accountId, async locked =>
        {
            await store.SaveRefreshAsync(locked, read, default);
            return true;
        }, default), default);
    }

    private static async Task<Account> AddAccountAsync(VesselDbContext db, Guid owner)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        account.Configure(null, "0x" + new string('a', 40), null);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    [PostgresFact]
    public async Task Refreshes_record_observations_only_when_a_position_appears_or_changes()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = await AddAccountAsync(db, owner);

        await SaveAsync(db, account.Id, Read(0, [Position(1, 3), Position(-2, null, contract: "ETH")]));
        await SaveAsync(db, account.Id, Read(1, [Position(1, 3)]));              // unchanged: nothing new
        await SaveAsync(db, account.Id, Read(2, [Position(1, 5)]));              // leverage changed
        await SaveAsync(db, account.Id, Read(3, [Position(2, 5, entry: 101)]));  // size changed
        await SaveAsync(db, account.Id, Read(1, [Position(9, 9)]));              // older observation: ignored entirely
        await SaveAsync(db, account.Id, Read(4, []));                            // closed
        await SaveAsync(db, account.Id, Read(5, [Position(2, 5, entry: 101)]));  // reopened identically: a presence gap
        await SaveAsync(db, account.Id, Read(6, [Position(2, 5, entry: 101, native: "7")])); // another native identity

        var observed = await db.LeverageObservations.OrderBy(o => o.ObservedAtUtc).ThenBy(o => o.VenueContractId).ToListAsync();
        Assert.Equal([(0, 1m, 3, (string?)null), (2, 1m, 5, null), (3, 2m, 5, null), (5, 2m, 5, null), (6, 2m, 5, "7")],
            observed.Select(o => ((int)(o.ObservedAtUtc - Start).TotalMinutes, o.SignedQuantity, o.Leverage, o.VenueContractId)));
        Assert.All(observed, o => Assert.Equal((owner, account.Id, "BTC"), (o.OwnerId, o.AccountId, o.ContractId)));
    }

    [PostgresFact]
    public async Task Closed_operation_keeps_leverage_after_the_snapshot_drops_it_and_fill_leverage_is_stored()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using (var db = database.Context(owner))
        {
            var account = await AddAccountAsync(db, owner);
            await SaveAsync(db, account.Id, Read(5, [Position(1, 4)], Fill("a", 0, "buy", "open", leverage: 4)));
            await SaveAsync(db, account.Id, Read(20, [], Fill("b", 10, "sell", "close")));
        }
        await using (var db = database.Context(owner))
        {
            Assert.Equal([4, null], await db.Fills.OrderBy(f => f.OccurredAtUtc).Select(f => f.Leverage).ToListAsync());
            Assert.Empty(await db.Positions.ToListAsync());
            var op = Assert.Single((await ActivityTestSupport.Activity(db).PageAsync()).Operations);
            Assert.Equal(("closed", 4, 4, 4), (op.Status, op.Leverage, op.LeverageMin, op.LeverageMax));
        }
        // Another owner never sees the observations.
        await using (var other = database.Context(Guid.NewGuid()))
            Assert.Empty(await other.LeverageObservations.ToListAsync());
    }

    [PostgresFact]
    public async Task A_position_closed_and_reopened_identically_between_refreshes_keeps_its_leverage()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using (var db = database.Context(owner))
        {
            var account = await AddAccountAsync(db, owner);
            VenueFill a = Fill("a", 0, "buy", "open"), b = Fill("b", 10, "sell", "close"), c = Fill("c", 15, "buy", "open");
            await SaveAsync(db, account.Id, Read(5, [Position(1, 3)], a));
            // A closed and B opened with the same quantity, entry and leverage: only the new executions show the boundary.
            await SaveAsync(db, account.Id, Read(20, [Position(1, 3)], a, b, c));
            await SaveAsync(db, account.Id, Read(21, [Position(1, 3)], a, b, c)); // idle: the same executions again add nothing
            await SaveAsync(db, account.Id, Read(30, [], a, b, c, Fill("d", 25, "sell", "close")));
            Assert.Equal([5, 20], (await db.LeverageObservations.OrderBy(o => o.ObservedAtUtc).ToListAsync())
                .Select(o => (int)(o.ObservedAtUtc - Start).TotalMinutes));
        }
        await using (var db = database.Context(owner))
        {
            Assert.Empty(await db.Positions.ToListAsync());
            var ops = (await ActivityTestSupport.Activity(db).PageAsync()).Operations.OrderBy(o => o.OpenedAtUtc).ToList();
            Assert.Equal(2, ops.Count);
            Assert.All(ops, op => Assert.Equal(("closed", 3, 3, 3), (op.Status, op.Leverage, op.LeverageMin, op.LeverageMax)));
        }
    }

    [PostgresFact]
    public async Task Disabled_accounts_hide_observations_and_deletion_removes_them()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = await AddAccountAsync(db, owner);
        await SaveAsync(db, account.Id, Read(5, [Position(1, 4)], Fill("a", 0, "buy", "open")));
        account.UpdateSettings(account.Name, null, false);
        await db.SaveChangesAsync();
        Assert.Empty((await ActivityTestSupport.Activity(db).PageAsync()).Operations);

        var work = TestUnitOfWork.For(db);
        var store = new WorkspaceStore(db, work);
        await work.ExecuteAsync(() => store.WithAccountLockAsync(account.Id, async locked =>
        {
            await store.DeleteAccountAsync(locked, default);
            return true;
        }, default), default);
        Assert.Empty(await db.LeverageObservations.ToListAsync());
    }

    [PostgresFact]
    public async Task Migration_backfills_current_positions_with_leverage()
    {
        await using var database = await CoreDatabase.CreateAsync("20261018120000_ServerNotificationState");
        var owner = Guid.NewGuid(); var id = Guid.NewGuid();
        await using (var legacy = database.Context(owner))
        {
            await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"VenueId\", \"Name\", \"SyncStatus\", \"IsEnabled\", \"SettingsRevision\") VALUES ({id}, {owner}, 'hyperliquid', 'Legacy', 'synced', true, 1)");
            await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO account_snapshots (\"OwnerId\", \"AccountId\", \"ObservedAtUtc\", \"ValueScope\") VALUES ({owner}, {id}, {Start.AddDays(-1)}, 'legacy')");
            await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO account_positions (\"OwnerId\", \"AccountId\", \"ContractId\", \"SignedQuantity\", \"EntryPrice\", \"UnrealizedPnlUsd\", \"MarginUsedUsd\", \"Leverage\") VALUES ({owner}, {id}, 'BTC', -0.5, 100, 0, 1, 7), ({owner}, {id}, 'ETH', 2, 10, 0, 1, NULL)");
            await legacy.Database.MigrateAsync();
            Assert.False(legacy.Database.HasPendingModelChanges());
        }
        await using var db = database.Context(owner);
        var observed = Assert.Single(await db.LeverageObservations.ToListAsync());
        Assert.Equal(("BTC", -0.5m, 7, Start.AddDays(-1)),
            (observed.ContractId, observed.SignedQuantity, observed.Leverage, observed.ObservedAtUtc));
    }
}
