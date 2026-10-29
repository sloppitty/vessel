using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Workspace;

namespace Vessel.Tests;

public sealed class StablecoinPersistenceTests
{

    [PostgresFact]
    public async Task Balances_use_the_wallet_alone_in_unified_mode_and_add_perps_otherwise_and_sum_per_portfolio()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var reader = new FixtureReader();
        var service = new WorkspaceService(new Vessel.Persistence.WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.For(db));
        var portfolio = await service.CreatePortfolioAsync(new("Both modes"), default);
        Vessel.Application.Venues.PerpetualVenueReadResult With(string mode, decimal perps) => reader.Result with
        {
            Snapshot = reader.Result.Snapshot with
            {
                AccountValueUsd = perps,
                StablecoinWallet = new(DateTimeOffset.UtcNow, mode, "hypercore-spot-stablecoins", [new("USDC", 0, "0x6d1e7cde53ba9467b783cb7c530ce054", 151.77m, 1.77m, 150m)]),
            }
        };
        var unified = await service.CreateAccountAsync(new(portfolio.Id, "Unified", "hyperliquid", "0x" + new string('2', 40)), default);
        reader.Result = With("unifiedAccount", 0m);
        unified = await MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), unified.Id);
        Assert.Equal(("151.77", "151.77", "150"), (unified.BalanceUsd, unified.TotalStablecoinNominalUsd, unified.AvailableStablecoinNominalUsd));

        var standard = await service.CreateAccountAsync(new(portfolio.Id, "Standard", "hyperliquid", "0x" + new string('3', 40)), default);
        reader.Result = With("default", 1000m);
        standard = await MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), standard.Id);
        Assert.Equal("1151.77", standard.BalanceUsd);

        var summary = Assert.Single(await service.PortfoliosAsync(default));
        Assert.Equal(("1303.54", "complete"), (summary.BalanceUsd, summary.BalanceCoverage));
        // The perps-only total keeps its meaning.
        Assert.Equal("1000", summary.TotalValueUsd);
    }

    [PostgresFact]
    public async Task Wallet_roundtrips_without_overwriting_perp_equity_and_survives_disable_enable()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var store = new Vessel.Persistence.WorkspaceStore(db, TestUnitOfWork.For(db));
        var reader = new FixtureReader();
        reader.Result = reader.Result with
        {
            Snapshot = reader.Result.Snapshot with
            {
                StablecoinWallet = new(DateTimeOffset.UtcNow, "unifiedAccount", "hypercore-spot-stablecoins", [
                    new("USDC", 0, "0x6d1e7cde53ba9467b783cb7c530ce054", 12.00000000000000000000000001m, 2m, 10.00000000000000000000000001m),
                    new("USDH", 360, "0x54e00a5988577cb0b0c9ab0cb6ef7f4b", 4m, 0m, 4m)])
            }
        };
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.For(db));
        var portfolio = await service.CreatePortfolioAsync(new("Wallet group"), default);
        var account = await service.CreateAccountAsync(new(portfolio.Id, "Wallet", "hyperliquid", "0x" + new string('1', 40)), default);
        var synced = await MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), account.Id);
        Assert.Equal("14.00000000000000000000000001", synced.AvailableStablecoinNominalUsd);
        Assert.Equal("unifiedAccount", synced.AccountMode);
        var snapshot = (await service.SnapshotAsync(account.Id, default))!;
        Assert.Equal(reader.Result.Snapshot.AccountValueUsd.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.AccountValueUsd);
        Assert.Equal("16.00000000000000000000000001", snapshot.StablecoinWallet!.TotalNominalUsd);
        Assert.Equal(2, snapshot.StablecoinWallet.Balances.Count);
        Assert.Equal("14.00000000000000000000000001", (await service.OverviewAsync(default)).Totals.AvailableStablecoinNominalUsd);
        Assert.Equal(1, (await service.OverviewAsync(default)).Totals.StablecoinAccountCount);
        await service.UpdateAccountAsync(account.Id, new("Wallet", null, false, 1), default);
        Assert.Null(await service.SnapshotAsync(account.Id, default));
        Assert.Null((await service.OverviewAsync(default)).Totals.AvailableStablecoinNominalUsd);
        Assert.Equal(2, await db.Stablecoins.CountAsync());
        await service.UpdateAccountAsync(account.Id, new("Wallet", null, true, 2), default);
        Assert.Equal("14.00000000000000000000000001", (await service.AccountAsync(account.Id, default)).AvailableStablecoinNominalUsd);
        reader.Fail = true;
        await Assert.ThrowsAsync<ProblemException>(() => MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), account.Id));
        Assert.Equal("14.00000000000000000000000001", (await service.SnapshotAsync(account.Id, default))!.StablecoinWallet!.AvailableNominalUsd);
        await service.DeleteAccountAsync(account.Id, default);
        Assert.Empty(await db.Stablecoins.ToListAsync());
    }

    [PostgresFact]
    public async Task Replacement_can_empty_wallet_without_duplicates_and_owner_leaks()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var reader = new FixtureReader();
        reader.Result = reader.Result with
        {
            Snapshot = reader.Result.Snapshot with
            {
                StablecoinWallet = new(DateTimeOffset.UtcNow, "disabled", "hypercore-spot-stablecoins", [
                new("USDC", 0, "known-token", 10m, 1m, 9m)])
            }
        };
        var service = new WorkspaceService(new Vessel.Persistence.WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.For(db));
        var account = await service.CreateAccountAsync(new(null, "Wallet", "hyperliquid", "0x" + new string('2', 40)), default);
        await MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), account.Id);
        await MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), account.Id);
        Assert.Single(await db.Stablecoins.ToListAsync());
        await using (var foreign = database.Context(Guid.NewGuid()))
        {
            Assert.Empty(await foreign.Stablecoins.ToListAsync());
            var foreignService = new WorkspaceService(new Vessel.Persistence.WorkspaceStore(foreign, TestUnitOfWork.For(foreign)), new CoreOwner(foreign.CurrentOwnerId), TestVenues.With(reader), TestUnitOfWork.For(foreign));
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => foreignService.SnapshotAsync(account.Id, default))).StatusCode);
        }
        reader.Result = reader.Result with
        {
            Snapshot = reader.Result.Snapshot with
            {
                StablecoinWallet = new(DateTimeOffset.UtcNow, "unifiedAccount", "hypercore-spot-stablecoins", [])
            }
        };
        await MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), account.Id);
        Assert.Equal("0", (await service.AccountAsync(account.Id, default)).AvailableStablecoinNominalUsd);
        Assert.Empty(await db.Stablecoins.ToListAsync());
    }

    [PostgresFact]
    public async Task Existing_snapshot_has_unknown_wallet_until_refreshed()
    {
        await using var database = await CoreDatabase.CreateAsync("20261001112809_AccountManagement");
        var owner = Guid.NewGuid();
        await using (var old = database.Context(owner)) await old.Database.MigrateAsync();
        await using var db = database.Context(owner);
        var reader = new FixtureReader();
        var service = new WorkspaceService(new Vessel.Persistence.WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.For(db));
        var account = await service.CreateAccountAsync(new(null, "Legacy reader", "hyperliquid", "0x" + new string('3', 40)), default);
        await MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader), account.Id);
        Assert.Null((await service.AccountAsync(account.Id, default)).AvailableStablecoinNominalUsd);
        Assert.Null((await service.SnapshotAsync(account.Id, default))!.StablecoinWallet);
    }
}
