using System.Globalization;
using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public sealed class WorkspaceReviewTests
{
    [Theory]
    [InlineData("25000.123456789012345678901234", 4, "100000.493827156049382715604936")]
    [InlineData("79228162514264337593543950335", 2, "158456325028528675187087900670")]
    public async Task Portfolio_and_overview_sums_preserve_accepted_decimal_values(string value, int count, string expected)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var portfolio = await service.CreatePortfolioAsync(new("Exact"), default);
        for (var i = 0; i < count; i++)
            await service.CreateAccountAsync(new(portfolio.Id, $"Account {i}", "manual", ManualAccountValueUsd: value), default);
        var overview = await service.OverviewAsync(default);
        Assert.Equal(expected, overview.Totals.TotalAccountValueUsd);
        Assert.Equal(expected, overview.Portfolios.Single().TotalValueUsd);
        Assert.Equal(expected, (await service.PortfolioAsync(portfolio.Id, default)).TotalValueUsd);
    }

    [Fact]
    public async Task Maximum_decimal_plus_one_is_not_an_overflow_or_unknown_total()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var portfolio = await service.CreatePortfolioAsync(new("Exact"), default);
        var first = await service.CreateAccountAsync(new(portfolio.Id, "Max", "manual", ManualAccountValueUsd: decimal.MaxValue.ToString(CultureInfo.InvariantCulture)), default);
        await service.CreateAccountAsync(new(portfolio.Id, "One", "manual", ManualAccountValueUsd: "1"), default);
        Assert.Equal("79228162514264337593543950336", (await service.OverviewAsync(default)).Totals.TotalAccountValueUsd);
        Assert.Equal("79228162514264337593543950336", (await service.PortfolioAsync(portfolio.Id, default)).TotalValueUsd);
        Assert.Equal(decimal.MaxValue.ToString(CultureInfo.InvariantCulture), (await service.AccountAsync(first.Id, default)).AccountValueUsd);
    }

    [Fact]
    public async Task Exact_totals_handle_negative_equity_mixed_scales_and_disabled_records()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var portfolio = await service.CreatePortfolioAsync(new("Exact"), default);
        await service.CreateAccountAsync(new(portfolio.Id, "Manual", "manual", ManualAccountValueUsd: "79228162514264337593543950335"), default);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Negative"); account.Configure(portfolio.Id, null, null); store.Accounts.Add(account);
        store.Snapshots.Add(new() { OwnerId = owner, AccountId = account.Id, AccountValueUsd = -0.0000000000000000000000000001m });
        var disabled = new Account(Guid.NewGuid(), owner, "manual", "Hidden"); disabled.Configure(portfolio.Id, null, decimal.MaxValue); disabled.UpdateSettings("Hidden", portfolio.Id, false); store.Accounts.Add(disabled);
        var overview = await service.OverviewAsync(default);
        Assert.Equal("79228162514264337593543950334.9999999999999999999999999999", overview.Totals.TotalAccountValueUsd);
        Assert.Equal(3, overview.Portfolios.Single().AccountCount); Assert.Equal(2, overview.Totals.ValuedAccountCount);
        Assert.Equal("complete", overview.Portfolios.Single().ValueCoverage);
        Assert.Equal(overview.Totals.TotalAccountValueUsd, overview.Portfolios.Single().TotalValueUsd);
    }
    [Fact]
    public async Task Detail_and_settings_use_targeted_snapshot_query_not_overview()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var dto = await service.CreateAccountAsync(new(null, "Manual", "manual"), default);
        store.RejectOwnerWideReads = true;
        Assert.Equal(dto.Id, (await service.AccountAsync(dto.Id, default)).Id);
        Assert.Null(await service.SnapshotAsync(dto.Id, default));
        var updated = await service.UpdateAccountAsync(dto.Id, new("Renamed", null, true, dto.SettingsRevision), default);
        Assert.Equal(2, updated.SettingsRevision);
    }

    [Fact]
    public async Task Revision_rejects_stale_settings_without_changing_source_or_sync_time()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var original = await service.CreateAccountAsync(new(null, "Manual", "manual"), default);
        Assert.Equal(1, original.SettingsRevision);
        var disabled = await service.UpdateAccountAsync(original.Id, new("Manual", null, false, original.SettingsRevision), default);
        Assert.Equal(2, disabled.SettingsRevision);
        var conflict = await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAccountAsync(original.Id, new("Stale rename", null, true, original.SettingsRevision), default));
        Assert.Equal(409, conflict.StatusCode);
        var current = await service.AccountAsync(original.Id, default); Assert.Equal("Manual", current.Name); Assert.False(current.IsEnabled); Assert.Equal(2, current.SettingsRevision);
        Assert.Null(current.LastSyncedAtUtc);
    }

}
