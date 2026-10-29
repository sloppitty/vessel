using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class PaperAccountTests : PaperAccountTestsValues
{

    private static WorkspaceService Service(MemoryWorkspaceStore store) =>
        new(store, new CoreOwner(Owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);

    [Theory]
    [InlineData(null, null, null, null, "lighter")]
    [InlineData("wallet", null, null, null, "hyperliquid")]
    [InlineData(null, "1", null, null, "hyperliquid")]
    [InlineData(null, null, "0", null, "hyperliquid")]
    [InlineData(null, null, null, "-1", "hyperliquid")]
    [InlineData(null, null, null, "1.00000000000000000000000000001", "hyperliquid")]
    public async Task Paper_creation_rejects_real_identity_balance_unsupported_venue_or_inexact_capital(
        string? address, string? source, string? manual, string? capital, string venue)
    {
        var store = new MemoryWorkspaceStore(Owner);
        var portfolio = new Portfolio(Guid.NewGuid(), Owner, "Paper", tradingMode: "paper");
        store.Portfolios.Add(portfolio);
        await Assert.ThrowsAsync<ProblemException>(() => Service(store).CreateAccountAsync(
            new(portfolio.Id, "Invalid", venue, address, manual, source, "paper", capital), default));
        Assert.Empty(store.Accounts);
    }

    [Fact]
    public async Task Reads_default_to_real_and_mode_mismatch_or_invalid_mode_is_rejected()
    {
        var store = new MemoryWorkspaceStore(Owner);
        var real = new Portfolio(Guid.NewGuid(), Owner, "Real");
        var paper = new Portfolio(Guid.NewGuid(), Owner, "Paper", tradingMode: "paper");
        store.Portfolios.AddRange([real, paper]);
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Paper", "paper");
        account.Configure(paper.Id, null, null);
        store.Accounts.Add(account);
        var service = Service(store);
        Assert.Single(await service.PortfoliosAsync(default));
        Assert.Empty(await service.AccountsAsync(default));
        Assert.Equal("paper", Assert.Single(await service.AccountsAsync(default, "paper")).TradingMode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.AccountAsync(account.Id, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.AccountsAsync(default, "mixed"))).StatusCode);
        Assert.Null(await service.SnapshotAsync(account.Id, default, "paper"));
    }

    [Fact]
    public async Task Settings_requests_reject_creation_only_fields_even_before_trading()
    {
        var store = new MemoryWorkspaceStore(Owner);
        var service = Service(store);
        var portfolio = new Portfolio(Guid.NewGuid(), Owner, "Paper", tradingMode: "paper");
        store.Portfolios.Add(portfolio);
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Paper", "paper");
        account.Configure(portfolio.Id, null, null); store.Accounts.Add(account);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAccountAsync(account.Id,
            new("Paper", portfolio.Id, true, 1, StartingCapitalUsd: "5000"), default, "paper"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAccountAsync(account.Id,
            new("Paper", portfolio.Id, true, 1, TradingMode: "real"), default, "paper"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdatePortfolioAsync(portfolio.Id,
            new("Paper", TradingMode: "real"), default, "paper"))).StatusCode);
        Assert.Equal(4000m, account.StartingCapitalUsd);
        Assert.Equal("paper", account.TradingMode);
    }

    [Fact]
    public async Task Membership_and_portfolio_deletion_do_not_unassign_paper_accounts()
    {
        var store = new MemoryWorkspaceStore(Owner);
        var real = new Portfolio(Guid.NewGuid(), Owner, "Real");
        var paper = new Portfolio(Guid.NewGuid(), Owner, "Paper", tradingMode: "paper");
        var other = new Portfolio(Guid.NewGuid(), Owner, "Other", tradingMode: "paper");
        store.Portfolios.AddRange([real, paper, other]);
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Paper", "paper");
        account.Configure(paper.Id, null, null);
        store.Accounts.Add(account);
        var service = Service(store);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.MoveAccountsAsync(new([account.Id], real.Id), default, "paper"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.MoveAccountsAsync(new([account.Id], null), default, "paper"))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.DeletePortfolioAsync(paper.Id, default, "paper"))).StatusCode);
        await service.MoveAccountsAsync(new([account.Id], other.Id), default, "paper");
        await service.DeletePortfolioAsync(paper.Id, default, "paper");
        Assert.Equal(other.Id, account.PortfolioId);
        Assert.Equal(4000m, account.StartingCapitalUsd);
    }
}
