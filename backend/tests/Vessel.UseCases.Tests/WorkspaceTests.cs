using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class WorkspaceTests
{

    [Fact]
    public async Task Overview_excludes_foreign_records_and_reports_all_coverage_states()
    {
        var owner = Guid.NewGuid();
        var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var complete = await service.CreatePortfolioAsync(new("Complete"), default);
        var partial = await service.CreatePortfolioAsync(new("Partial"), default);
        var empty = await service.CreatePortfolioAsync(new("Unavailable"), default);
        await service.CreateAccountAsync(new(complete.Id, "Known zero", "manual", ManualAccountValueUsd: "0"), default);
        await service.CreateAccountAsync(new(partial.Id, "Known", "manual", ManualAccountValueUsd: "12.00000000000000000000000001"), default);
        var unknown = await service.CreateAccountAsync(new(partial.Id, "Unknown", "manual"), default);
        store.Accounts.Add(new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Foreign"));
        store.Portfolios.Add(new Portfolio(Guid.NewGuid(), Guid.NewGuid(), "Foreign"));
        var overview = await service.OverviewAsync(default);
        Assert.Equal(3, overview.Totals.AccountCount);
        Assert.Equal(2, overview.Totals.ValuedAccountCount);
        Assert.Equal("12.00000000000000000000000001", overview.Totals.TotalAccountValueUsd);
        Assert.Equal("complete", overview.Portfolios.Single(p => p.Id == complete.Id).ValueCoverage);
        Assert.Equal("0", overview.Portfolios.Single(p => p.Id == complete.Id).TotalValueUsd);
        Assert.Equal("partial", overview.Portfolios.Single(p => p.Id == partial.Id).ValueCoverage);
        Assert.Equal("unavailable", overview.Portfolios.Single(p => p.Id == empty.Id).ValueCoverage);
        Assert.Null(unknown.AccountValueUsd);
        Assert.Null(await service.SnapshotAsync(unknown.Id, default));
    }

    [Fact]
    public async Task Empty_overview_and_unknown_balances_are_not_zero()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        Assert.Null((await service.OverviewAsync(default)).Totals.TotalAccountValueUsd);
        var portfolio = await service.CreatePortfolioAsync(new("Unknown"), default);
        await service.CreateAccountAsync(new(portfolio.Id, "Manual", "manual"), default);
        var overview = await service.OverviewAsync(default);
        Assert.Null(overview.Totals.TotalAccountValueUsd);
        Assert.Null(overview.Portfolios.Single().TotalValueUsd);
        Assert.Equal("unavailable", overview.Portfolios.Single().ValueCoverage);
    }

    [Fact]
    public async Task All_foreign_account_operations_return_not_found()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
        var foreign = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Foreign"); store.Accounts.Add(foreign);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.SnapshotAsync(foreign.Id, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.FillsAsync(foreign.Id, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(new(Guid.NewGuid(), "Foreign", "manual"), default))).StatusCode);
    }
}
