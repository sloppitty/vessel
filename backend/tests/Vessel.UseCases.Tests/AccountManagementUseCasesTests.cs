using Vessel.Application.Workspace;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class AccountManagementUseCasesTests
{

    [Fact]
    public async Task Unassigned_creation_move_unlink_and_portfolio_deletion_keep_account_records()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var account = await service.CreateAccountAsync(new(null, "Account", "manual", ManualAccountValueUsd: "12.123456789"), default);
        Assert.Null(account.PortfolioId); Assert.True(account.IsEnabled); Assert.Empty(store.Portfolios);
        var first = await service.CreatePortfolioAsync(new("First"), default); var second = await service.CreatePortfolioAsync(new("Second"), default);
        await service.UpdateAccountAsync(account.Id, new(" Renamed ", first.Id, true, 1), default);
        Assert.Equal(1, (await service.PortfolioAsync(first.Id, default)).AccountCount);
        await service.UpdateAccountAsync(account.Id, new("Renamed", second.Id, false, 2), default);
        Assert.Equal(0, (await service.PortfolioAsync(first.Id, default)).AccountCount);
        var renamed = await service.UpdatePortfolioAsync(second.Id, new(" Second renamed "), default);
        Assert.Equal("Second renamed", renamed.Name); Assert.Equal(1, renamed.AccountCount); Assert.Null(renamed.TotalValueUsd);
        await service.DeletePortfolioAsync(second.Id, default);
        var after = await service.AccountAsync(account.Id, default);
        Assert.Null(after.PortfolioId); Assert.False(after.IsEnabled); Assert.Equal("Renamed", after.Name); Assert.Equal("12.123456789", after.AccountValueUsd);
        await service.UpdateAccountAsync(account.Id, new("Renamed", null, true, after.SettingsRevision), default);
        Assert.Equal("12.123456789", (await service.OverviewAsync(default)).Totals.TotalAccountValueUsd);
    }

    [Fact]
    public async Task Disabled_records_do_not_affect_coverage_counts_or_recent_activity_and_restore()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var reader = new FixtureReader();
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.None);
        var portfolio = await service.CreatePortfolioAsync(new("Values"), default);
        await service.CreateAccountAsync(new(portfolio.Id, "Known", "manual", ManualAccountValueUsd: "10"), default);
        var unknown = await service.CreateAccountAsync(new(portfolio.Id, "Unknown", "manual"), default);
        var venue = await service.CreateAccountAsync(new(portfolio.Id, "Venue", "hyperliquid", "0x1111111111111111111111111111111111111111"), default);
        store.Snapshots.Add(new AccountSnapshot { OwnerId = owner, AccountId = venue.Id, AccountValueUsd = 20, Positions = [new() { OwnerId = owner, AccountId = venue.Id, ContractId = "BTC" }] });
        store.Fills.Add(new ImportedFill { Id = Guid.NewGuid(), OwnerId = owner, AccountId = venue.Id, ContractId = "BTC" });
        await service.UpdateAccountAsync(unknown.Id, new("Unknown", portfolio.Id, false, 1), default);
        await service.UpdateAccountAsync(venue.Id, new("Venue", portfolio.Id, false, 1), default);
        var overview = await service.OverviewAsync(default);
        Assert.Equal(3, overview.Totals.AccountCount); Assert.Equal(1, overview.Totals.ValuedAccountCount); Assert.Equal("10", overview.Totals.TotalAccountValueUsd);
        Assert.Equal(3, overview.Portfolios.Single().AccountCount); Assert.Equal("complete", overview.Portfolios.Single().ValueCoverage);
        Assert.Equal(0, overview.Totals.ImportedFillCount); Assert.Equal(0, overview.Totals.OpenPositionCount); Assert.Empty(overview.RecentActivity);
        Assert.Null(await service.SnapshotAsync(venue.Id, default)); Assert.Empty(await service.FillsAsync(venue.Id, default));
        Assert.Equal(0, reader.Reads);
        Assert.Single(store.Snapshots); Assert.Single(store.Fills);
        await service.UpdateAccountAsync(venue.Id, new("Venue", portfolio.Id, true, 2), default);
        Assert.NotNull(await service.SnapshotAsync(venue.Id, default)); Assert.Single(await service.FillsAsync(venue.Id, default));
        var restored = await service.OverviewAsync(default);
        Assert.Equal("30", restored.Totals.TotalAccountValueUsd); Assert.Equal(1, restored.Totals.ImportedFillCount); Assert.Equal(1, restored.Totals.OpenPositionCount);
    }

    [Fact]
    public async Task All_disabled_values_are_unavailable_not_zero()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var portfolio = await service.CreatePortfolioAsync(new("Empty"), default);
        var account = await service.CreateAccountAsync(new(portfolio.Id, "Zero", "manual", ManualAccountValueUsd: "0"), default);
        await service.UpdateAccountAsync(account.Id, new("Zero", portfolio.Id, false, 1), default);
        var overview = await service.OverviewAsync(default);
        Assert.Null(overview.Totals.TotalAccountValueUsd); Assert.Null(overview.Portfolios.Single().TotalValueUsd);
        Assert.Equal("unavailable", overview.Portfolios.Single().ValueCoverage); Assert.Equal(1, overview.Totals.AccountCount);
    }
}
