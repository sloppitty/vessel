using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Domain.Common;

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
        var portfolio = new Portfolio(Guid.NewGuid(), Owner, "Paper", tradingMode: TradingMode.Paper);
        store.Portfolios.Add(portfolio);
        await Assert.ThrowsAsync<ProblemException>(() => Service(store).CreateAccountAsync(
            new(portfolio.Id, "Invalid", venue, address, manual, source, "paper", capital), default));
        Assert.Empty(store.Accounts);
    }

    [Fact]
    public async Task Reads_default_to_real_and_mode_mismatch_or_invalid_mode_is_rejected()
    {
        var store = new MemoryWorkspaceStore(Owner);
        var real = new Portfolio(Guid.NewGuid(), Owner, "Real", TradingMode.Real);
        var paper = new Portfolio(Guid.NewGuid(), Owner, "Paper", tradingMode: TradingMode.Paper);
        store.Portfolios.AddRange([real, paper]);
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Paper", TradingMode.Paper);
        account.Configure(paper.Id, null, null);
        store.Accounts.Add(account);
        var service = Service(store);
        Assert.Single(await service.PortfoliosAsync(TradingMode.Real, default));
        Assert.Empty(await service.AccountsAsync(TradingMode.Real, default));
        Assert.Equal(TradingMode.Paper, Assert.Single(await service.AccountsAsync(TradingMode.Paper, default)).TradingMode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.AccountAsync(account.Id, TradingMode.Real, default))).StatusCode);
        Assert.Equal(400, Assert.Throws<ProblemException>(() => WorkspaceService.ValidateTradingMode("mixed")).StatusCode);
        Assert.Null(await service.SnapshotAsync(account.Id, TradingMode.Paper, default));
    }

    [Fact]
    public async Task Settings_requests_reject_creation_only_fields_even_before_trading()
    {
        var store = new MemoryWorkspaceStore(Owner);
        var service = Service(store);
        var portfolio = new Portfolio(Guid.NewGuid(), Owner, "Paper", tradingMode: TradingMode.Paper);
        store.Portfolios.Add(portfolio);
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Paper", TradingMode.Paper);
        account.Configure(portfolio.Id, null, null); store.Accounts.Add(account);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAccountAsync(account.Id,
            new("Paper", portfolio.Id, true, 1, StartingCapitalUsd: "5000"), TradingMode.Paper, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAccountAsync(account.Id,
            new("Paper", portfolio.Id, true, 1, TradingMode: "real"), TradingMode.Paper, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdatePortfolioAsync(portfolio.Id,
            new("Paper", TradingMode: "real"), TradingMode.Paper, default))).StatusCode);
        Assert.Equal(4000m, account.StartingCapitalUsd);
        Assert.Equal(TradingMode.Paper, account.TradingMode);
    }

    [Fact]
    public async Task Membership_and_portfolio_deletion_do_not_unassign_paper_accounts()
    {
        var store = new MemoryWorkspaceStore(Owner);
        var real = new Portfolio(Guid.NewGuid(), Owner, "Real", TradingMode.Real);
        var paper = new Portfolio(Guid.NewGuid(), Owner, "Paper", tradingMode: TradingMode.Paper);
        var other = new Portfolio(Guid.NewGuid(), Owner, "Other", tradingMode: TradingMode.Paper);
        store.Portfolios.AddRange([real, paper, other]);
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Paper", TradingMode.Paper);
        account.Configure(paper.Id, null, null);
        store.Accounts.Add(account);
        var service = Service(store);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.MoveAccountsAsync(new([account.Id], real.Id), TradingMode.Paper, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.MoveAccountsAsync(new([account.Id], null), TradingMode.Paper, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.DeletePortfolioAsync(paper.Id, TradingMode.Paper, default))).StatusCode);
        await service.MoveAccountsAsync(new([account.Id], other.Id), TradingMode.Paper, default);
        await service.DeletePortfolioAsync(paper.Id, TradingMode.Paper, default);
        Assert.Equal(other.Id, account.PortfolioId);
        Assert.Equal(4000m, account.StartingCapitalUsd);
    }
}
