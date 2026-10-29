using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class PortfolioMembershipTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static WorkspaceService Service(MemoryWorkspaceStore store) => new(store, new CoreOwner(Owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.None);
    private static Account Account(Guid? portfolio = null, Guid? owner = null)
    {
        var account = new Account(Guid.NewGuid(), owner ?? Owner, "manual", "Account");
        account.Configure(portfolio, null, 123.456m);
        return account;
    }

    [Fact]
    public async Task Guarded_moves_match_revisions_by_id_and_keep_no_op_revisions()
    {
        var store = new MemoryWorkspaceStore(Owner); var target = new Portfolio(Guid.NewGuid(), Owner, "Target"); store.Portfolios.Add(target);
        var first = Account(); var same = Account(target.Id); store.Accounts.AddRange([first, same]);
        await Service(store).MoveAccountsAsync(new([first.Id, same.Id], target.Id, [new(same.Id, 1), new(first.Id, 1)]), default);
        Assert.Equal(2, first.SettingsRevision); Assert.Equal(1, same.SettingsRevision);
        var created = await Service(store).CreatePortfolioAsync(new("New", AccountIds: [first.Id], Expected: [new(first.Id, 2)]), default);
        Assert.Equal(created.Id, first.PortfolioId); Assert.Equal(3, first.SettingsRevision);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Stale_membership_rejects_all_changes_before_mutation(bool create, bool unlink)
    {
        var store = new MemoryWorkspaceStore(Owner); var old = new Portfolio(Guid.NewGuid(), Owner, "Old");
        var target = new Portfolio(Guid.NewGuid(), Owner, "Other"); store.Portfolios.AddRange([old, target]);
        // Sort the valid sibling first, so a mutate-as-you-check implementation would partially move it.
        var accounts = new[] { Account(old.Id), Account(old.Id) }.OrderBy(a => a.Id).ToArray();
        store.Accounts.AddRange(accounts); var first = accounts[0]; var stale = accounts[1];
        stale.UpdateSettings(stale.Name, target.Id, stale.IsEnabled);
        var expected = new AccountSettingsExpectation[] { new(first.Id, 1), new(stale.Id, 1) };
        var error = await Assert.ThrowsAsync<ProblemException>(async () =>
        {
            if (create) await Service(store).CreatePortfolioAsync(new("Rejected", AccountIds: [first.Id, stale.Id], Expected: expected), default);
            else await Service(store).MoveAccountsAsync(new([first.Id, stale.Id], unlink ? null : target.Id, expected), default);
        });
        Assert.Equal(409, error.StatusCode); Assert.Contains("No accounts were moved", error.Message);
        Assert.Equal(old.Id, first.PortfolioId); Assert.Equal(1, first.SettingsRevision);
        Assert.Equal(target.Id, stale.PortfolioId); Assert.Equal(2, stale.SettingsRevision);
        Assert.Equal(2, store.Portfolios.Count); Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("null")]
    public async Task Rejects_incomplete_or_invalid_expectations(string kind)
    {
        var store = new MemoryWorkspaceStore(Owner); var first = Account(); var second = Account(); store.Accounts.AddRange([first, second]);
        AccountSettingsExpectation[] expected = kind switch
        {
            "empty" => [],
            "missing" => [new(first.Id, 1)],
            "duplicate" => [new(first.Id, 1), new(first.Id, 1)],
            "foreign" => [new(first.Id, 1), new(Guid.NewGuid(), 1)],
            "zero" => [new(first.Id, 0), new(second.Id, 1)],
            "negative" => [new(first.Id, -1), new(second.Id, 1)],
            _ => [new(first.Id, 1), null!]
        };
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            Service(store).MoveAccountsAsync(new([first.Id, second.Id], null, expected), default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            Service(store).CreatePortfolioAsync(new("Rejected", AccountIds: [first.Id, second.Id], Expected: expected), default))).StatusCode);
        Assert.Empty(store.Portfolios); Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task Expectations_without_selected_accounts_cannot_create_a_portfolio()
    {
        var store = new MemoryWorkspaceStore(Owner);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            Service(store).CreatePortfolioAsync(new("Rejected", Expected: [new(Guid.NewGuid(), 1)]), default))).StatusCode);
        Assert.Empty(store.Portfolios);
    }

    [Fact]
    public async Task Bulk_move_includes_disabled_accounts_bumps_only_changes_and_preserves_settings()
    {
        var store = new MemoryWorkspaceStore(Owner); var portfolio = new Portfolio(Guid.NewGuid(), Owner, "Target"); store.Portfolios.Add(portfolio);
        var first = Account(); var disabled = Account(); disabled.UpdateSettings("Disabled", null, false); var same = Account(portfolio.Id);
        store.Accounts.AddRange([first, disabled, same]);
        var result = await Service(store).MoveAccountsAsync(new([first.Id, disabled.Id, same.Id], portfolio.Id), default);
        Assert.Equal(3, result.Count); Assert.All(result, a => Assert.Equal(portfolio.Id, a.PortfolioId));
        Assert.Equal(2, first.SettingsRevision); Assert.Equal(3, disabled.SettingsRevision); Assert.Equal(1, same.SettingsRevision);
        Assert.False(disabled.IsEnabled); Assert.Equal("Disabled", disabled.Name); Assert.Equal(123.456m, disabled.ManualAccountValueUsd);
        await Service(store).MoveAccountsAsync(new([first.Id, disabled.Id], null), default);
        Assert.Null(first.PortfolioId); Assert.Null(disabled.PortfolioId); Assert.Equal(3, first.SettingsRevision);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => Service(store).UpdateAccountAsync(first.Id, new("Stale", null, true, 1), default))).StatusCode);
    }

    [Fact]
    public async Task Creation_moves_existing_accounts_and_returns_real_coverage()
    {
        var store = new MemoryWorkspaceStore(Owner); var first = Account(); var second = Account(Guid.NewGuid()); store.Accounts.AddRange([first, second]);
        var result = await Service(store).CreatePortfolioAsync(new("Together", "anchor", [first.Id, second.Id]), default);
        Assert.Equal(2, result.AccountCount); Assert.Equal("246.912", result.BalanceUsd); Assert.Equal("complete", result.BalanceCoverage);
        Assert.Equal("anchor", result.Icon); Assert.All(store.Accounts, a => { Assert.Equal(result.Id, a.PortfolioId); Assert.Equal(2, a.SettingsRevision); });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Unknown_or_foreign_account_rejects_entire_move_or_creation(bool create, bool foreign)
    {
        var store = new MemoryWorkspaceStore(Owner); var valid = Account(); var invalid = Account(owner: Guid.NewGuid());
        var target = new Portfolio(Guid.NewGuid(), Owner, "Target"); store.Portfolios.Add(target); store.Accounts.Add(valid);
        if (foreign) store.Accounts.Add(invalid);
        var ids = new[] { valid.Id, invalid.Id };
        var error = await Assert.ThrowsAsync<ProblemException>(async () =>
        {
            if (create) await Service(store).CreatePortfolioAsync(new("Rejected", AccountIds: ids), default);
            else await Service(store).MoveAccountsAsync(new(ids, target.Id), default);
        });
        Assert.Equal(404, error.StatusCode); Assert.Null(valid.PortfolioId); Assert.Equal(1, valid.SettingsRevision);
        Assert.Single(store.Portfolios); Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_or_foreign_portfolio_is_not_assignable(bool foreign)
    {
        var store = new MemoryWorkspaceStore(Owner); var account = Account(); store.Accounts.Add(account);
        var target = new Portfolio(Guid.NewGuid(), Guid.NewGuid(), "Foreign"); if (foreign) store.Portfolios.Add(target);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => Service(store).MoveAccountsAsync(new([account.Id], target.Id), default))).StatusCode);
        Assert.Null(account.PortfolioId); Assert.Equal(1, account.SettingsRevision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task Rejects_empty_over_limit_duplicate_or_null_lists(int count)
    {
        var store = new MemoryWorkspaceStore(Owner);
        Guid[]? ids = count < 0 ? null : count == 2 ? [Guid.Empty, Guid.Empty] : Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Service(store).MoveAccountsAsync(new(ids, null), default))).StatusCode);
        if (ids is not null)
            Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Service(store).CreatePortfolioAsync(new("Rejected", AccountIds: ids), default))).StatusCode);
        Assert.Empty(store.Portfolios);
    }
}
