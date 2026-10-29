using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class PortfolioMembershipPostgresTests
{
    [PostgresFact]
    public async Task Stale_tab_cannot_unlink_an_account_after_a_concurrent_move()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid account, old, target;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); old = (await service.CreatePortfolioAsync(new("Old"), default)).Id;
            target = (await service.CreatePortfolioAsync(new("Other"), default)).Id;
            account = (await service.CreateAccountAsync(new(old, "Member", "manual"), default)).Id;
        }
        await using var tabA = database.Context(owner);
        // Keep a stale tracked entity too; the locked check must use the reloaded row.
        var displayed = await tabA.Accounts.SingleAsync();
        await using (var tabB = database.Context(owner))
            await Service(tabB, owner).MoveAccountsAsync(new([account], target), default);
        Assert.Equal(old, displayed.PortfolioId); Assert.Equal(1, displayed.SettingsRevision);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() =>
            Service(tabA, owner).MoveAccountsAsync(new([account], null, [new(account, 1)]), default))).StatusCode);
        await using var verify = database.Context(owner); var retained = await verify.Accounts.SingleAsync();
        Assert.Equal(target, retained.PortfolioId); Assert.Equal(2, retained.SettingsRevision);
    }

    [PostgresFact]
    public async Task Stale_member_rejects_bulk_and_creation_without_any_partial_changes()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid first, stale, old, target;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); old = (await service.CreatePortfolioAsync(new("Old"), default)).Id;
            target = (await service.CreatePortfolioAsync(new("Other"), default)).Id;
            var ids = new[] { (await service.CreateAccountAsync(new(old, "First", "manual"), default)).Id,
                (await service.CreateAccountAsync(new(old, "Second", "manual"), default)).Id }.Order().ToArray();
            first = ids[0]; stale = ids[1];
            await service.MoveAccountsAsync(new([stale], target), default);
        }
        foreach (var action in new[] { "unlink", "move", "create" })
        {
            await using (var db = database.Context(owner))
            {
                var service = Service(db, owner); var expected = new AccountSettingsExpectation[] { new(first, 1), new(stale, 1) };
                Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(async () =>
                {
                    if (action == "create") await service.CreatePortfolioAsync(new("Rejected", AccountIds: [first, stale], Expected: expected), default);
                    else await service.MoveAccountsAsync(new([first, stale], action == "unlink" ? null : target, expected), default);
                })).StatusCode);
            }
            await using var verify = database.Context(owner);
            Assert.Equal(2, await verify.Portfolios.CountAsync());
            var untouched = await verify.Accounts.SingleAsync(a => a.Id == first);
            Assert.Equal(old, untouched.PortfolioId); Assert.Equal(1, untouched.SettingsRevision);
            var moved = await verify.Accounts.SingleAsync(a => a.Id == stale);
            Assert.Equal(target, moved.PortfolioId); Assert.Equal(2, moved.SettingsRevision);
        }
    }

    private static WorkspaceService Service(VesselDbContext db, Guid owner) =>
        new(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(new FixtureReader()), TestUnitOfWork.For(db));

    [PostgresFact]
    public async Task Bulk_move_null_noop_disabled_and_stale_settings_retain_all_facts()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid first, second, target;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); target = (await service.CreatePortfolioAsync(new("Target"), default)).Id;
            first = (await service.CreateAccountAsync(new(null, "Venue", "hyperliquid", "0x1111111111111111111111111111111111111111"), default)).Id;
            second = (await service.CreateAccountAsync(new(null, "Disabled", "manual", ManualAccountValueUsd: "1.25"), default)).Id;
            await MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(new FixtureReader()), first); await service.UpdateAccountAsync(second, new("Disabled", null, false, 1), default);
            var moved = await service.MoveAccountsAsync(new([first, second], target), default);
            Assert.Equal(2, moved.Count); Assert.All(moved, a => Assert.Equal(target, a.PortfolioId));
            Assert.Equal(2, moved.Single(a => a.Id == first).SettingsRevision); Assert.Equal(3, moved.Single(a => a.Id == second).SettingsRevision);
            await service.MoveAccountsAsync(new([first, second], target), default);
            Assert.Equal(2, (await service.AccountAsync(first, default)).SettingsRevision);
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAccountAsync(first, new("Stale", null, true, 1), default))).StatusCode);
        }
        await using (var db = database.Context(owner))
        {
            await Service(db, owner).MoveAccountsAsync(new([first, second], null), default);
            Assert.All(await db.Accounts.ToListAsync(), a => Assert.Null(a.PortfolioId));
            Assert.False((await db.Accounts.SingleAsync(a => a.Id == second)).IsEnabled);
            Assert.Single(await db.Fills.ToListAsync()); Assert.Single(await db.Snapshots.ToListAsync()); Assert.Single(await db.Positions.ToListAsync());
        }
    }

    [PostgresFact]
    public async Task Creation_commits_portfolio_and_all_members_together()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid first, second, portfolio;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); var old = await service.CreatePortfolioAsync(new("Old"), default);
            first = (await service.CreateAccountAsync(new(old.Id, "First", "manual", ManualAccountValueUsd: "10.125"), default)).Id;
            second = (await service.CreateAccountAsync(new(null, "Second", "manual", ManualAccountValueUsd: "20"), default)).Id;
            var result = await service.CreatePortfolioAsync(new("New", "gem", [first, second]), default); portfolio = result.Id;
            Assert.Equal(2, result.AccountCount); Assert.Equal("30.125", result.BalanceUsd);
        }
        await using var verify = database.Context(owner);
        Assert.Equal(2, await verify.Portfolios.CountAsync());
        Assert.All(await verify.Accounts.ToListAsync(), a => { Assert.Equal(portfolio, a.PortfolioId); Assert.Equal(2, a.SettingsRevision); });
    }

    [PostgresFact]
    public async Task Missing_or_foreign_member_rolls_back_everything()
    {
        foreach (var (create, foreign) in new[] { (false, false), (false, true), (true, false), (true, true) })
        {
            await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var other = Guid.NewGuid(); Guid valid, invalid, target;
            await using (var db = database.Context(owner))
            {
                var service = Service(db, owner); target = (await service.CreatePortfolioAsync(new("Target"), default)).Id;
                valid = (await service.CreateAccountAsync(new(null, "Valid", "manual"), default)).Id;
            }
            await using (var db = database.Context(other)) invalid = (await Service(db, other).CreateAccountAsync(new(null, "Foreign", "manual"), default)).Id;
            if (!foreign) invalid = Guid.NewGuid();
            await using (var db = database.Context(owner))
            {
                var service = Service(db, owner);
                Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(async () =>
                {
                    if (create) await service.CreatePortfolioAsync(new("Rejected", AccountIds: [valid, invalid]), default);
                    else await service.MoveAccountsAsync(new([valid, invalid], target), default);
                })).StatusCode);
            }
            await using var verify = database.Context(owner);
            Assert.Single(await verify.Portfolios.ToListAsync()); var account = await verify.Accounts.SingleAsync();
            Assert.Null(account.PortfolioId); Assert.Equal(1, account.SettingsRevision);
        }
    }

    [PostgresFact]
    public async Task Failure_after_parent_save_rolls_back_portfolio_and_member_settings()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid id, old;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); old = (await service.CreatePortfolioAsync(new("Old"), default)).Id;
            id = (await service.CreateAccountAsync(new(old, "Member", "manual"), default)).Id;
            var revision = long.MaxValue;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE accounts SET \"SettingsRevision\" = {revision} WHERE \"Id\" = {id}");
        }
        await using (var db = database.Context(owner))
            await Assert.ThrowsAsync<OverflowException>(() => Service(db, owner).CreatePortfolioAsync(new("Rejected", AccountIds: [id]), default));
        await using var verify = database.Context(owner);
        Assert.Equal(old, (await verify.Portfolios.SingleAsync()).Id);
        var account = await verify.Accounts.SingleAsync(); Assert.Equal(old, account.PortfolioId); Assert.Equal(long.MaxValue, account.SettingsRevision);
    }

    [PostgresFact]
    public async Task Foreign_portfolio_rejection_does_not_change_accounts()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var other = Guid.NewGuid(); Guid target, id;
        await using (var db = database.Context(other)) target = (await Service(db, other).CreatePortfolioAsync(new("Foreign"), default)).Id;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); id = (await service.CreateAccountAsync(new(null, "Valid", "manual"), default)).Id;
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.MoveAccountsAsync(new([id], target), default))).StatusCode);
        }
        await using var verify = database.Context(owner); Assert.Null((await verify.Accounts.SingleAsync()).PortfolioId);
    }

    [PostgresFact]
    public async Task Bulk_move_waits_for_sync_row_lock_then_keeps_refreshed_metadata()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); Guid id, target;
        await using (var db = database.Context(owner))
        {
            var service = Service(db, owner); target = (await service.CreatePortfolioAsync(new("Target"), default)).Id;
            id = (await service.CreateAccountAsync(new(null, "Account", "manual"), default)).Id;
        }
        await using var syncing = database.Context(owner); await using var transaction = await syncing.Database.BeginTransactionAsync();
        var locked = await syncing.Accounts.FromSqlInterpolated($"SELECT * FROM accounts WHERE \"Id\" = {id} FOR UPDATE").SingleAsync();
        await using var moving = database.Context(owner); await moving.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)moving.Database.GetDbConnection()).ProcessID;
        var move = Service(moving, owner).MoveAccountsAsync(new([id], target), default);
        await using var inspect = new NpgsqlConnection(database.ConnectionString); await inspect.OpenAsync();
        var blocked = false;
        for (var attempt = 0; attempt < 200 && !blocked; attempt++)
        {
            await using var query = new NpgsqlCommand("SELECT wait_event_type = 'Lock' FROM pg_stat_activity WHERE pid = @pid", inspect);
            query.Parameters.AddWithValue("pid", pid); blocked = await query.ExecuteScalarAsync() is true;
            if (!blocked) await Task.Delay(10);
        }
        Assert.True(blocked); Assert.False(move.IsCompleted);
        locked.RecordSync(DateTimeOffset.Parse("2026-10-13T10:00:00Z"), "Retained observation"); await syncing.SaveChangesAsync(); await transaction.CommitAsync();
        var result = Assert.Single(await move.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(target, result.PortfolioId); Assert.Equal("Retained observation", result.HistoryNotice); Assert.Equal("synced", result.SyncStatus);
    }
}
