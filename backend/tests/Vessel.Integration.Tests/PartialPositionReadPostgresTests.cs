using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Overview;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Persistence;
using Vessel.Domain.Common;

namespace Vessel.Tests;

/// <summary>
/// Issue #89: a refresh whose position read was incomplete (for example a Hyperliquid builder DEX that could not be read)
/// is stored as incomplete, and every reader of stored positions treats it as unknown, never as zero or flat.
/// </summary>
public sealed class PartialPositionReadPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private static PerpetualVenueReadResult Read(int minute, bool complete, params VenuePosition[] positions) =>
        new(new(Start.AddMinutes(minute), "primary-perpetual-dex", 100, 100, 1, positions), [],
            [new("fill-1", "BTC", "buy", "Open Long", 100, 1, 0, "USDC", 0, Start, "order-1", "", "", "open")],
            "Bounded history.", PositionsComplete: complete);

    private static VenuePosition Btc(decimal unrealized) => new("BTC", 1, 100, unrealized, 1, 5);

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

    private sealed record View(OverviewSummaryDto Summary, AccountDto Account, OverviewDto Overview, SnapshotDto Snapshot, ActivityPageDto Activity);

    private static async Task<View> ReadAsync(CoreDatabase database, Guid owner, Guid accountId)
    {
        await using var db = database.Context(owner);
        var store = new WorkspaceStore(db, TestUnitOfWork.For(db));
        var workspace = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(), TestUnitOfWork.For(db));
        // No venue registry: open operations take the snapshot path of unrealized P&L.
        var activity = new ActivityService(new ActivityStore(db), store, TimeProvider.System);
        return new(await new OverviewSummaryService(store, workspace, activity, new SizingStore(db, TestUnitOfWork.For(db)), TimeProvider.System).GetAsync("account", "all", "all", TradingMode.Real, default),
            await workspace.AccountAsync(accountId, TradingMode.Real, default), await workspace.OverviewAsync(TradingMode.Real, default),
            (await workspace.SnapshotAsync(accountId, TradingMode.Real, default))!, await activity.PageAsync(new ActivityQuery(), default));
    }

    [PostgresFact]
    public async Task A_partial_read_leaves_positions_and_unrealized_unknown_until_a_complete_read_restores_them()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Builder", TradingMode.Real);
        account.Configure(null, "0x" + new string('b', 40), null);
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            await SaveAsync(db, account.Id, Read(1, complete: true, Btc(5)));
        }
        var known = await ReadAsync(database, owner, account.Id);
        Assert.Equal((1, "5"), (known.Summary.Totals.OpenPositionCount, known.Summary.Totals.UnrealizedPnl.AmountUsd));
        Assert.Equal("5", Assert.Single(known.Activity.Operations).UnrealizedPnlUsd);

        // The builder DEX holding the position could not be read: the venue returned no position and said so.
        await using (var db = database.Context(owner)) await SaveAsync(db, account.Id, Read(2, complete: false));
        var partial = await ReadAsync(database, owner, account.Id);
        Assert.Null(partial.Summary.Totals.OpenPositionCount);
        Assert.Null(partial.Summary.Totals.UnrealizedPnl.AmountUsd);
        Assert.Contains(partial.Summary.Totals.UnrealizedPnl.Coverage.Reasons,
            r => r is { Reason: OverviewSummaryService.IncompletePositionsReason, Count: 1 });
        var row = Assert.Single(partial.Summary.Rows);
        Assert.Equal(((int?)null, (string?)null), (row.Totals.OpenPositionCount, row.Totals.UnrealizedPnl.AmountUsd));
        Assert.Null(partial.Account.PositionCount);
        Assert.Null(partial.Overview.Totals.OpenPositionCount);
        Assert.False(partial.Snapshot.PositionsComplete);
        Assert.Empty(partial.Snapshot.Positions);
        // No snapshot disagreement and no flat anchor from the absent position; the operation stays open with unknown P&L.
        Assert.Contains(partial.Activity.Coverage, c => c.AccountId == account.Id && c.ContractId is null &&
            c.Notice == ActivityService.IncompleteSnapshotNotice);
        Assert.DoesNotContain(partial.Activity.Coverage, c => c.Notice.Contains("disagrees", StringComparison.Ordinal));
        var open = Assert.Single(partial.Activity.Operations);
        Assert.Equal(("open", "1"), (open.Status, open.OpenQuantity));
        Assert.DoesNotContain(open.Coverage, c => c.Contains("disagrees", StringComparison.Ordinal));
        Assert.Null(open.UnrealizedPnlUsd);

        await using (var db = database.Context(owner)) await SaveAsync(db, account.Id, Read(3, complete: true, Btc(7)));
        var restored = await ReadAsync(database, owner, account.Id);
        Assert.Equal((1, "7"), (restored.Summary.Totals.OpenPositionCount, restored.Summary.Totals.UnrealizedPnl.AmountUsd));
        Assert.Empty(restored.Summary.Totals.UnrealizedPnl.Coverage.Reasons);
        Assert.Equal((1, 1), (restored.Account.PositionCount, restored.Overview.Totals.OpenPositionCount));
        Assert.True(restored.Snapshot.PositionsComplete);
        Assert.DoesNotContain(restored.Activity.Coverage, c => c.Notice == ActivityService.IncompleteSnapshotNotice);
        Assert.Equal("7", Assert.Single(restored.Activity.Operations).UnrealizedPnlUsd);
    }

    [PostgresFact]
    public async Task A_partial_read_that_still_returns_the_position_is_not_the_current_picture()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Builder", TradingMode.Real);
        account.Configure(null, "0x" + new string('c', 40), null);
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            // The primary DEX was read (the position is a real observation), but another DEX of the account was not.
            await SaveAsync(db, account.Id, Read(1, complete: false, Btc(6)));
        }
        var partial = await ReadAsync(database, owner, account.Id);
        Assert.Equal(((int?)null, (string?)null), (partial.Summary.Totals.OpenPositionCount, partial.Summary.Totals.UnrealizedPnl.AmountUsd));
        Assert.Null(partial.Account.PositionCount);
        Assert.Equal((false, "BTC"), (partial.Snapshot.PositionsComplete, Assert.Single(partial.Snapshot.Positions).ContractId));
        var open = Assert.Single(partial.Activity.Operations);
        Assert.Equal(("open", "1"), (open.Status, open.OpenQuantity));
        Assert.Null(open.UnrealizedPnlUsd);
    }

    [PostgresFact]
    public async Task Migration_keeps_existing_snapshots_complete()
    {
        await using var database = await CoreDatabase.CreateAsync("20261102123723_MarketSources");
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Existing", TradingMode.Real);
        await using var db = database.Context(owner);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"VenueId\", \"Name\", \"SyncStatus\") VALUES ({account.Id}, {owner}, 'hyperliquid', 'Existing', 'synced')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO account_snapshots (\"OwnerId\", \"AccountId\", \"ObservedAtUtc\", \"ValueScope\") VALUES ({owner}, {account.Id}, now(), 'primary-perpetual-dex')");
        await db.Database.MigrateAsync();
        Assert.True((await db.Snapshots.SingleAsync()).PositionsComplete);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
