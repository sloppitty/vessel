using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Credentials;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Evidence;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class UnitOfWorkTests
{
    [PostgresFact]
    public async Task A_read_snapshot_is_one_repeatable_read_transaction_and_only_outermost()
    {
        // Phase 2, item 5c (Sol round 1 on #40): a Play bundle's parts come from one snapshot.
        await using var database = await CoreDatabase.CreateAsync();
        await using var db = database.Context(Guid.NewGuid());
        var work = TestUnitOfWork.For(db);
        var isolation = await work.ReadSnapshotAsync(async () =>
            await db.Database.SqlQueryRaw<string>("SELECT current_setting('transaction_isolation') AS \"Value\"").SingleAsync(), default);
        Assert.Equal("repeatable read", isolation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            work.ExecuteAsync(() => work.ReadSnapshotAsync(() => Task.FromResult(1), default), default));
        Assert.Equal("read committed", await work.ExecuteAsync(async () =>
            await db.Database.SqlQueryRaw<string>("SELECT current_setting('transaction_isolation') AS \"Value\"").SingleAsync(), default));
    }

    [PostgresFact]
    public async Task DI_shares_one_owner_context_and_boundary_per_scope()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddSingleton<IJournalOwnerContext>(new CoreOwner(owner));
        services.AddVesselPersistence(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = database.ConnectionString
        }).Build());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VesselDbContext>();
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        Assert.Same(work, scope.ServiceProvider.GetRequiredService<UnitOfWork>());
        var accounts = scope.ServiceProvider.GetRequiredService<IWorkspaceStore>();
        var plays = scope.ServiceProvider.GetRequiredService<IPlayStore>();
        var executions = scope.ServiceProvider.GetRequiredService<IPlayExecutionStore>();
        var diary = scope.ServiceProvider.GetRequiredService<IDiaryStore>();
        await work.ExecuteAsync(async () =>
        {
            var account = Account(owner);
            await accounts.AddAccountAsync(account, default);
            var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
            plays.Add(play);
            // The linked order is an imported fact; links reference it by internal ID.
            var order = TestOrders.For(play, "order");
            db.Orders.Add(order);
            executions.Add(new PlayOrderLink(play, order, OrderLinkRole.Entry, "entry-1", null,
                OrderLinkState.Linked, OrderLinkSource.Owner, DateTimeOffset.UnixEpoch));
            diary.Add(new DiaryRequest { Id = Guid.NewGuid(), OwnerId = owner, PayloadHash = "hash", Response = "{}" });
            Assert.Same(account, await executions.AccountAsync(account.Id, default));
            Assert.Equal(5, db.ChangeTracker.Entries().Count());
            return true;
        }, default);
        await using var other = provider.CreateAsyncScope();
        Assert.NotSame(db, other.ServiceProvider.GetRequiredService<VesselDbContext>());
        Assert.NotSame(work, other.ServiceProvider.GetRequiredService<IUnitOfWork>());
        await using var verify = database.Context(owner);
        Assert.Single(await verify.Plays.ToListAsync());
        Assert.Single(await verify.OrderLinks.ToListAsync());
        Assert.Single(await verify.DiaryRequests.ToListAsync());
        await using var foreign = database.Context(Guid.NewGuid());
        Assert.Empty(await foreign.Plays.ToListAsync());
    }

    [PostgresFact]
    public async Task Nested_services_and_intermediate_saves_commit_once_and_are_not_visible_before_commit()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var transactions = new Transactions();
        await using var db = Context(database, owner, transactions);
        var work = TestUnitOfWork.For(db);
        var plays = new PlayService(new PlayStore(db, work), new MemoryObjectStore(), TimeProvider.System, work);
        var accounts = new WorkspaceService(new WorkspaceStore(db, work), new CoreOwner(owner),
            TestVenues.With(new FixtureReader()), work);
        var executions = new ExecutionStore(db, work);
        var play = await work.ExecuteAsync(async () =>
        {
            var account = await accounts.CreateAccountAsync(new(null, "Account", "hyperliquid", "0x" + new string('a', 40)), default);
            var draft = await plays.CreateAsync(new(account.Id, "BTC", "venue", "Idea", TestPlays.Plan()), default);
            await work.SaveChangesAsync(default);
            var planned = await plays.ChangeStatusAsync(draft.Summary.Id, new(draft.Summary.Version, "planned"), default);
            await executions.WithAccountLockAsync(account.Id, async () =>
            {
                await executions.UpsertOrdersAsync((await executions.AccountAsync(account.Id, default))!,
                    [new("order-1", "BTC", "buy", "Limit", 100, null, false, false, 1, 1,
                        DateTimeOffset.UnixEpoch, VenueOrderStatus.Open, "open", DateTimeOffset.UnixEpoch)],
                    DateTimeOffset.UnixEpoch, default);
                Assert.Single(await executions.OrdersAsync(account.Id, default));
                return true;
            }, default);
            await using var outside = database.Context(owner);
            Assert.Empty(await outside.Accounts.ToListAsync());
            Assert.Empty(await outside.Plays.ToListAsync());
            Assert.Empty(await outside.Orders.ToListAsync());
            Assert.Equal(0, transactions.Commits);
            return planned;
        }, default);
        Assert.Equal(1, transactions.Starts);
        Assert.Equal(1, transactions.Commits);
        Assert.Equal(0, transactions.Rollbacks);
        await using var verify = database.Context(owner);
        Assert.Equal(PlayStatus.Planned, (await verify.Plays.SingleAsync()).Status);
        Assert.Equal(play.Summary.Id, (await verify.StatusChanges.SingleAsync()).PlayId);
        Assert.Single(await verify.PlanRevisions.ToListAsync());
        Assert.Single(await verify.Orders.ToListAsync());
    }

    [PostgresFact]
    public async Task Rollback_restores_replaced_composite_keys_imported_orders_and_fills_and_clears_tracking()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var accounts = new WorkspaceStore(db, work);
        var executions = new ExecutionStore(db, work);
        var account = Account(owner);
        var first = Snapshot(1, 10);
        await work.ExecuteAsync(async () =>
        {
            await accounts.AddAccountAsync(account, default);
            await accounts.WithAccountLockAsync(account.Id, async locked =>
            {
                await accounts.SaveRefreshAsync(locked, first, default);
                return true;
            }, default);
            return true;
        }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync<bool>(async () =>
        {
            await accounts.WithAccountLockAsync(account.Id, async locked =>
            {
                await accounts.SaveRefreshAsync(locked, Snapshot(2, 20), default);
                await executions.UpsertOrdersAsync(locked,
                    [new("pending", "BTC", "buy", "Limit", 100, null, false, false, 1, 1,
                        DateTimeOffset.UnixEpoch, VenueOrderStatus.Open, "open", DateTimeOffset.UnixEpoch)], DateTimeOffset.UnixEpoch, default);
                return true;
            }, default);
            Assert.Equal(2, (await db.Positions.SingleAsync()).SignedQuantity);
            Assert.Equal(20, (await db.Stablecoins.SingleAsync()).Total);
            throw new InvalidOperationException("Abort after intermediate saves.");
        }, default));
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
        await work.ExecuteAsync(async () =>
        {
            await accounts.AddPortfolioAsync(new(Guid.NewGuid(), owner, "Later operation"), default);
            return true;
        }, default);
        await using var verify = database.Context(owner);
        Assert.Equal(1, (await verify.Positions.SingleAsync()).SignedQuantity);
        Assert.Equal(10, (await verify.Stablecoins.SingleAsync()).Total);
        Assert.Equal(first.Snapshot.ObservedAtUtc, (await verify.Snapshots.SingleAsync()).ObservedAtUtc);
        Assert.Equal(first.Snapshot.ObservedAtUtc, (await verify.Accounts.SingleAsync()).LastSyncedAtUtc);
        Assert.Equal("fill-1", (await verify.Fills.SingleAsync()).SourceFillId);
        Assert.Empty(await verify.Orders.ToListAsync());
        Assert.Single(await verify.Portfolios.ToListAsync());
    }

    [PostgresFact]
    public async Task Caught_nested_failure_poisoning_rolls_back_already_saved_changes()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var store = new WorkspaceStore(db, work);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            try
            {
                await work.ExecuteAsync<bool>(async () =>
                {
                    await store.AddAccountAsync(Account(owner), default);
                    throw new InvalidOperationException("Nested failure.");
                }, default);
            }
            catch (InvalidOperationException) { }
            return true;
        }, default));
        Assert.Empty(db.ChangeTracker.Entries());
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Caught_lock_callback_failure_cannot_commit_partial_mutations()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var store = new WorkspaceStore(db, work);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            try
            {
                await store.WithManagementLockAsync<bool>(async () =>
                {
                    await store.AddAccountAsync(Account(owner), default);
                    throw new InvalidOperationException("Lock callback failed.");
                }, default);
            }
            catch (InvalidOperationException) { }
            return true;
        }, default));
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Accounts.ToListAsync());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [PostgresFact]
    public async Task Cancelled_intermediate_save_rolls_back_without_using_cancelled_token_for_rollback()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var transactions = new Transactions();
        await using var db = Context(database, owner, transactions);
        var work = TestUnitOfWork.For(db);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.ExecuteAsync(async () =>
        {
            db.Accounts.Add(Account(owner));
            await work.SaveChangesAsync(default);
            cancellation.Cancel();
            return true;
        }, cancellation.Token));
        Assert.Equal(0, transactions.Commits);
        Assert.Equal(1, transactions.Rollbacks);
        Assert.False(transactions.CancelledRollback);
        Assert.Empty(db.ChangeTracker.Entries());
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Caught_failed_save_poisoning_is_not_a_successful_no_op()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            db.Accounts.Add(Account(Guid.NewGuid()));
            try { await work.SaveChangesAsync(default); }
            catch (UnauthorizedAccessException) { }
            return true;
        }, default));
        Assert.Empty(db.ChangeTracker.Entries());
        await work.ExecuteAsync(() => Task.FromResult(true), default);
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Lock_and_save_require_the_callers_boundary_and_never_adopt_an_external_transaction()
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var db = database.Context(Guid.NewGuid());
        var work = TestUnitOfWork.For(db);
        var accounts = new WorkspaceStore(db, work);
        var plays = new PlayStore(db, work);
        var executions = new ExecutionStore(db, work);
        var diary = new DiaryStore(db, work);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.SaveChangesAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.WithManagementLockAsync(() => Task.FromResult(true), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.WithAccountLockAsync(Guid.NewGuid(), _ => Task.FromResult(true), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plays.WithPlayLockAsync(Guid.NewGuid(), () => Task.FromResult(true), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executions.WithAccountLockAsync(Guid.NewGuid(), () => Task.FromResult(true), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => diary.WithRequestLockAsync(Guid.NewGuid(), () => Task.FromResult(true), default));
        Assert.Null(db.Database.CurrentTransaction);
        await using var external = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(() => Task.FromResult(true), default));
        Assert.Same(external, db.Database.CurrentTransaction);
    }

    [PostgresFact]
    public async Task Concurrent_top_level_start_is_rejected_and_poisoned_root_rolls_back()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var entered = Signal();
        var release = Signal();
        var first = work.ExecuteAsync(async () =>
        {
            db.Accounts.Add(Account(owner));
            await work.SaveChangesAsync(default);
            entered.SetResult();
            await release.Task;
            return true;
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(() => Task.FromResult(true), default)); }
        finally { release.SetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.Empty(db.ChangeTracker.Entries());
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Root_inherited_parallel_callbacks_never_enter_together_and_roll_back()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            db.Accounts.Add(Account(owner));
            using var start = new Barrier(2);
            var entered = Signal();
            var rejected = Signal();
            var admitted = 0;
            Task Race() => Task.Run(async () =>
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
                try
                {
                    await work.ExecuteAsync(async () =>
                    {
                        Interlocked.Increment(ref admitted);
                        entered.TrySetResult();
                        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        return true;
                    }, default);
                }
                catch (InvalidOperationException) { rejected.TrySetResult(); }
            });
            var first = Race();
            var second = Race();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, admitted);
            return true;
        }, default));
        Assert.Empty(db.ChangeTracker.Entries());
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Final_save_lease_and_late_failure_check_prevent_commit_of_a_swallowed_concurrent_failure()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var gate = new SaveGate();
        await using var db = Context(database, owner, gate);
        var work = TestUnitOfWork.For(db);
        Task? attempt = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(() =>
        {
            db.Accounts.Add(Account(owner));
            attempt = Task.Run(async () =>
            {
                await gate.Entered.Task;
                try { await work.ExecuteAsync<int>(() => throw new InvalidOperationException("Late failure."), default); }
                catch (InvalidOperationException) { }
                finally { gate.Release.SetResult(); }
            });
            return Task.FromResult(true);
        }, default));
        await attempt!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(db.ChangeTracker.Entries());
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Dangling_participant_cannot_write_after_rollback_or_reuse_the_scope()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var plays = new PlayStore(db, work);
        var release = Signal();
        Task? dangling = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(() =>
        {
            dangling = work.ExecuteAsync(async () =>
            {
                await release.Task;
                plays.Add(TestPlays.Create(Account(owner), new("hyperliquid", "BTC")));
                return true;
            }, default);
            return Task.FromResult(true);
        }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(() => Task.FromResult(true), default));
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => dangling!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(db.ChangeTracker.Entries());
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Plays.ToListAsync());
    }

    [PostgresFact]
    public async Task Old_execution_context_cannot_join_or_poison_a_later_boundary()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var release = Signal();
        Task? old = null;
        await work.ExecuteAsync(() =>
        {
            old = Task.Run(async () =>
            {
                await release.Task;
                await work.ExecuteAsync(() => Task.FromResult(true), default);
            });
            return Task.FromResult(true);
        }, default);
        await work.ExecuteAsync(async () =>
        {
            db.Accounts.Add(Account(owner));
            release.SetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => old!.WaitAsync(TimeSpan.FromSeconds(10)));
            return true;
        }, default);
        await using var verify = database.Context(owner);
        Assert.Single(await verify.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Completed_nested_context_cannot_reenter_while_a_sibling_is_running()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var release = Signal();
        Task? stale = null;
        var entered = false;
        await work.ExecuteAsync(async () =>
        {
            db.Accounts.Add(Account(owner));
            await work.ExecuteAsync(() =>
            {
                stale = Task.Run(async () =>
                {
                    await release.Task;
                    await work.ExecuteAsync(() =>
                    {
                        entered = true;
                        return Task.FromResult(true);
                    }, default);
                });
                return Task.FromResult(true);
            }, default);
            await work.ExecuteAsync(async () =>
            {
                release.SetResult();
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => stale!.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(entered);
                return true;
            }, default);
            return true;
        }, default);
        await using var verify = database.Context(owner);
        Assert.Single(await verify.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Rollback_closes_admission_before_awaiting_the_database()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var gate = new RollbackGate();
        await using var db = Context(database, owner, gate);
        var work = TestUnitOfWork.For(db);
        Task? stale = null;
        var admitted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync<bool>(() =>
        {
            stale = Task.Run(async () =>
            {
                await gate.Entered.Task;
                try
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(() =>
                    {
                        admitted = true;
                        db.Accounts.Add(Account(owner));
                        return Task.FromResult(true);
                    }, default));
                }
                finally { gate.Release.TrySetResult(); }
            });
            throw new InvalidOperationException("Deliberate application failure.");
        }, default));
        await stale!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(admitted);
        Assert.Empty(db.ChangeTracker.Entries());
        await work.ExecuteAsync(() => Task.FromResult(true), default);
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Accounts.ToListAsync());
    }

    // A3: nested units flush before returning, so the outer unit reads what a nested service just created.
    [PostgresFact]
    public async Task Nested_create_is_visible_to_the_same_outer_unit_of_work()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic");
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var work = TestUnitOfWork.For(db);
        var plays = new PlayService(new PlayStore(db, work), new MemoryObjectStore(), TimeProvider.System, work);
        var read = await work.ExecuteAsync(async () =>
        {
            var created = await plays.CreateAsync(new CreatePlayRequest(account.Id, "BTC", "manual", "Nested", TestPlays.Plan()), default);
            return await plays.GetAsync(created.Summary.Id, default);
        }, default);
        Assert.Equal("Nested", read.Summary.Title);
    }

    // A4: once the last cancellation check passes, the commit itself is not abandoned halfway.
    [PostgresFact]
    public async Task Commit_runs_without_the_callers_cancellation_token()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var transactions = new Transactions();
        await using var db = Context(database, owner, transactions);
        var work = TestUnitOfWork.For(db);
        using var cancellation = new CancellationTokenSource();
        await work.ExecuteAsync(() =>
        {
            db.Accounts.Add(Account(owner));
            return Task.FromResult(true);
        }, cancellation.Token);
        Assert.Equal(1, transactions.Commits);
        Assert.False(transactions.CancellableCommit);
        await using var verify = database.Context(owner);
        Assert.Single(await verify.Accounts.ToListAsync());
    }

    // A4: a failed rollback must not replace the error that caused it.
    [PostgresFact]
    public async Task Rollback_failure_keeps_the_original_error_and_retires_the_scope()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = Context(database, owner, new FailingRollback());
        var work = TestUnitOfWork.For(db);
        var original = new InvalidOperationException("Deliberate application failure.");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync<bool>(async () =>
        {
            db.Accounts.Add(Account(owner));
            await work.SaveChangesAsync(default);
            throw original;
        }, default));
        Assert.Same(original, thrown);
        Assert.IsType<TimeoutException>(thrown.Data[UnitOfWork.RollbackErrorKey]);
        Assert.Empty(db.ChangeTracker.Entries());
        var retired = await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(() => Task.FromResult(true), default));
        Assert.Contains("new scope", retired.Message);
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Accounts.ToListAsync());
    }

    // Execution writes keep their own conflict wording rather than the generic Play message.
    [PostgresFact]
    public async Task Execution_conflicts_ask_to_check_the_venue_again()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using (var seed = database.Context(owner))
        {
            var account = Account(owner);
            seed.Accounts.Add(account);
            seed.Plays.Add(TestPlays.Create(account, new("hyperliquid", "BTC")));
            await seed.SaveChangesAsync();
        }
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var executions = new ExecutionStore(db, work);
        var conflict = await Assert.ThrowsAsync<Vessel.Application.Common.ProblemException>(() => work.ExecuteAsync(async () =>
        {
            var play = await db.Plays.SingleAsync();
            db.Entry(play).Property(p => p.Title).CurrentValue = "Changed here";
            await using (var elsewhere = database.Context(owner))
                await elsewhere.Plays.ExecuteUpdateAsync(set => set.SetProperty(p => p.Version, p => p.Version + 1));
            await executions.SaveAsync(default);
            return true;
        }, default));
        Assert.Equal((409, UnitOfWork.ExecutionConflict), (conflict.StatusCode, conflict.Message));
    }

    // A1: adapters make credential calls (read and refusal mark) before failing at the venue. On the monitor
    // refresh path the venue read runs outside the account transaction, so those calls commit on their own; the
    // handled venue failure is still recorded beside the refusal and reported as a venue error, not a server error.
    [PostgresFact]
    public async Task Handled_venue_failure_after_participating_credential_calls_is_recorded()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = SecurityFixture.Account(owner);
        await using (var seed = database.Context(owner)) { seed.Accounts.Add(account); await seed.SaveChangesAsync(); }
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var store = new WorkspaceStore(db, work);
        using var vault = SecurityFixture.Vault();
        await SecurityFixture.Service(owner, store, new AccountCredentialStore(db, work), vault, unitOfWork: work)
            .PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        var credentials = new AccountCredentialReader(store, new AccountCredentialStore(db, work),
            new CoreOwner(owner), vault, SecurityFixture.Clock, work);
        var reader = new RefusedThenFailingReader(credentials);
        var error = await Assert.ThrowsAsync<Vessel.Application.Common.ProblemException>(() =>
            MonitorTestPaths.RefreshAsync(db, owner, SecurityFixture.Registry(reader), account.Id, credentials));
        Assert.Equal(502, error.StatusCode);
        Assert.True(reader.Opened);
        await using var verify = database.Context(owner);
        var stored = await verify.Accounts.SingleAsync();
        Assert.Equal(("error", "The venue refresh failed. Try again later."), (stored.SyncStatus, stored.LastSyncError));
        Assert.Equal(AccountCredentialReader.RefusedError, (await verify.AccountCredentials.SingleAsync()).LastError);
        // The scope stays usable for the next request on it.
        await work.ExecuteAsync(() => Task.FromResult(true), default);
    }

    // A2: evidence and sizing writes join the unit of work like the other stores; none opens its own transaction.
    [PostgresFact]
    public async Task Evidence_and_sizing_writes_require_and_roll_back_with_the_unit_of_work()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = Account(owner);
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            seed.Plays.Add(play);
            await seed.SaveChangesAsync();
        }
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var evidence = new EvidenceStore(db, work);
        var sizing = new Vessel.Application.Sizing.SizingService(new SizingStore(db, work), new CoreOwner(owner), TimeProvider.System, work);
        PlayEvidence Image() => new(Guid.NewGuid(), db.Plays.Single(), $"{owner:N}/{play.Id:N}/{Guid.NewGuid():N}.png", "image/png", 12,
            new string('a', 64), EvidenceSource.Upload, "", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => evidence.AddAsync(Image(), 3, default));
        Assert.Throws<InvalidOperationException>(() => new SizingStore(db, work).Add(new(owner, 2m, DateTimeOffset.UtcNow)));
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync<bool>(async () =>
        {
            Assert.True(await evidence.AddAsync(Image(), 3, default));
            await sizing.UpdateSettingsAsync(new("2"), default);
            Assert.Equal(1, await db.Evidence.CountAsync());
            throw new InvalidOperationException("Roll back the outer operation.");
        }, default));
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Evidence.ToListAsync());
        Assert.Empty(await verify.SizingSettings.ToListAsync());
    }

    // A5: the database-free double enforces the same participation rules as UnitOfWork.
    [Fact]
    public async Task Memory_unit_of_work_rejects_commit_after_a_caught_nested_failure()
    {
        var saves = 0;
        var work = TestUnitOfWork.Saving(_ => { saves++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            try { await work.ExecuteAsync<bool>(() => throw new InvalidOperationException("participant"), default); }
            catch (InvalidOperationException) { }
            return true;
        }, default));
        Assert.Equal(0, saves);
        await work.ExecuteAsync(() => Task.FromResult(true), default);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task Memory_unit_of_work_requires_an_active_unit_and_flushes_nested_units()
    {
        var saves = 0;
        var work = TestUnitOfWork.Saving(_ => { saves++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.SaveChangesAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestUnitOfWork.None.SaveChangesAsync(default));
        var seen = await work.ExecuteAsync(async () =>
        {
            await work.ExecuteAsync(() => Task.FromResult(true), default);
            return saves;
        }, default);
        Assert.Equal((1, 2), (seen, saves));
        var failing = TestUnitOfWork.Saving(_ => throw new IOException("Save failed."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ExecuteAsync(async () =>
        {
            try { await failing.SaveChangesAsync(default); }
            catch (IOException) { }
            return true;
        }, default));
    }

    // Round 2: object storage cannot be rolled back, so image I/O never runs inside a unit or transaction.
    [PostgresFact]
    public async Task Image_storage_entry_points_refuse_enclosing_units_and_never_do_io_in_a_transaction()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = Account(owner);
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            seed.Plays.Add(play);
            await seed.SaveChangesAsync();
        }
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var objects = new TransactionRecordingObjectStore(db);
        var evidence = new Vessel.Application.Evidence.EvidenceService(new EvidenceStore(db, work), objects,
            Vessel.Application.Evidence.EvidenceLimits.Default, new CoreOwner(owner), TimeProvider.System, work);
        var plays = new PlayService(new PlayStore(db, work), objects, TimeProvider.System, work);
        Task<Vessel.Application.Evidence.EvidenceDto> Upload() =>
            evidence.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", "upload", null, default);
        var kept = await Upload();

        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync<bool>(async () =>
        {
            Assert.Contains("Evidence upload", (await Assert.ThrowsAsync<InvalidOperationException>(Upload)).Message);
            Assert.Contains("Evidence deletion", (await Assert.ThrowsAsync<InvalidOperationException>(
                () => evidence.DeleteAsync(kept.Id, default))).Message);
            Assert.Contains("Play deletion", (await Assert.ThrowsAsync<InvalidOperationException>(
                () => plays.DeleteAsync(play.Id, default))).Message);
            throw new InvalidOperationException("Roll back the outer operation.");
        }, default));
        await using (var external = await db.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Upload);
            await Assert.ThrowsAsync<InvalidOperationException>(() => evidence.DeleteAsync(kept.Id, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => plays.DeleteAsync(play.Id, default));
            await external.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        // Refused before any side effect: the earlier image and its record are untouched.
        Assert.Equal([("put", false)], objects.Log);
        Assert.Single(objects.Objects);
        await using (var verify = database.Context(owner))
            Assert.Equal(kept.Id, (await verify.Evidence.SingleAsync()).Id);

        await evidence.DeleteAsync(kept.Id, default);
        await Upload();
        await plays.DeleteAsync(play.Id, default);
        Assert.Equal([("put", false), ("delete", false), ("put", false), ("delete", false)], objects.Log);
        Assert.Empty(objects.Objects);
        await using (var verify = database.Context(owner))
        {
            Assert.Empty(await verify.Evidence.ToListAsync());
            Assert.Empty(await verify.Plays.ToListAsync());
        }
    }

    // Round 3: every kind of enclosing transaction is refused before any storage call, not only EF's own.
    [PostgresFact]
    public async Task Image_storage_entry_points_refuse_ambient_enlisted_ef_and_unit_transactions()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = Account(owner);
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            seed.Plays.Add(play);
            await seed.SaveChangesAsync();
        }
        Guid kept;
        var objects = new MemoryObjectStore();
        await using (var db = database.Context(owner))
        {
            var work = TestUnitOfWork.For(db);
            kept = (await new Vessel.Application.Evidence.EvidenceService(new EvidenceStore(db, work), objects,
                Vessel.Application.Evidence.EvidenceLimits.Default, new CoreOwner(owner), TimeProvider.System, work)
                .UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", "upload", null, default)).Id;
        }
        var stored = objects.Objects.ToDictionary();

        async Task RefusedInside(string kind, Func<VesselDbContext, UnitOfWork, Func<Task>, Task> enclose)
        {
            await using var db = database.Context(owner);
            var work = new UnitOfWork(db);
            var recording = new TransactionRecordingObjectStore(db);
            var evidence = new Vessel.Application.Evidence.EvidenceService(new EvidenceStore(db, work), recording,
                Vessel.Application.Evidence.EvidenceLimits.Default, new CoreOwner(owner), TimeProvider.System, work);
            var plays = new PlayService(new PlayStore(db, work), recording, TimeProvider.System, work);
            var refused = 0;
            await enclose(db, work, async () =>
            {
                Assert.True(work.IsActive, kind);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    evidence.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png), "", "upload", null, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => evidence.DeleteAsync(kept, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => plays.DeleteAsync(play.Id, default));
                refused = 3;
            });
            Assert.Equal(3, refused);
            Assert.Empty(recording.Log);
            Assert.False(work.IsActive, kind);
        }

        await RefusedInside("unit", async (_, work, body) =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync<bool>(async () =>
            {
                await body();
                throw new InvalidOperationException("Roll back the outer operation.");
            }, default)));
        await RefusedInside("ef", async (db, _, body) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await body();
            await transaction.RollbackAsync();
        });
        await RefusedInside("enlisted", async (db, _, body) =>
        {
            using var transaction = new System.Transactions.CommittableTransaction();
            await db.Database.OpenConnectionAsync();
            System.Transactions.TransactionsDatabaseFacadeExtensions.EnlistTransaction(db.Database, transaction);
            Assert.Null(System.Transactions.Transaction.Current);
            await body();
            transaction.Rollback();
            await db.Database.CloseConnectionAsync();
        });
        await RefusedInside("ambient", async (db, _, body) =>
        {
            using (new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
            {
                Assert.Null(System.Transactions.TransactionsDatabaseFacadeExtensions.GetEnlistedTransaction(db.Database));
                await body();
            }
        });

        Assert.Equal(stored.Keys, objects.Objects.Keys);
        await using var verify = database.Context(owner);
        Assert.Equal(kept, (await verify.Evidence.SingleAsync()).Id);
        Assert.Single(await verify.Plays.ToListAsync());
    }

    // Round 2: cancelled participants never run and still fail the outer unit, in the real unit and the double.
    [PostgresFact]
    public async Task Cancelled_participants_never_run_and_fail_the_unit()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        await CancelledParticipantsFail(TestUnitOfWork.For(db), () => db.Accounts.Add(Account(owner)));
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Accounts.ToListAsync());
    }

    [Fact]
    public async Task Memory_unit_of_work_matches_cancelled_participant_rules()
    {
        var saves = 0;
        await CancelledParticipantsFail(TestUnitOfWork.Saving(_ => { saves++; return Task.CompletedTask; }), () => { });
        Assert.Equal(0, saves);
    }

    private static async Task CancelledParticipantsFail(IUnitOfWork work, Action write)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var ran = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            write();
            try
            {
                await work.ExecuteAsync(() =>
                {
                    ran = true;
                    return Task.FromResult(true);
                }, cancelled.Token);
            }
            catch (OperationCanceledException) { }
            return true;
        }, default));
        Assert.False(ran);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
        {
            write();
            try { await work.SaveChangesAsync(cancelled.Token); }
            catch (OperationCanceledException) { }
            return true;
        }, default));
        Assert.False(work.IsActive);
    }

    private static Account Account(Guid owner)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        account.Configure(null, "0x" + new string('a', 40), null);
        return account;
    }

    private static PerpetualVenueReadResult Snapshot(int quantity, int balance)
    {
        var time = DateTimeOffset.UnixEpoch.AddDays(quantity);
        return new(new(time, "primary-perpetual-dex", balance, balance, 1,
            [new("BTC", quantity, 100, 0, 1, 3)], new(time, "default", "stablecoins",
                [new("USDC", 0, "token", balance, 0, balance)])), [],
            [new("fill-" + quantity, "BTC", "buy", "Open Long", 100, quantity, 0, "USDC", 0, time, "order-" + quantity, "", "", "open")],
            "Bounded history.");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static VesselDbContext Context(CoreDatabase database, Guid owner, IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor).Options, new CoreOwner(owner));

    private sealed class SaveGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class RollbackGate : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public override async ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class TransactionRecordingObjectStore(VesselDbContext db) : Vessel.Application.Evidence.IEvidenceObjectStore
    {
        private readonly MemoryObjectStore inner = new();
        public List<(string Operation, bool InTransaction)> Log { get; } = [];
        public Dictionary<string, byte[]> Objects => inner.Objects;
        public Task PutAsync(string key, Stream content, CancellationToken ct)
        {
            Log.Add(("put", db.Database.CurrentTransaction is not null));
            return inner.PutAsync(key, content, ct);
        }
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => inner.OpenReadAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct)
        {
            Log.Add(("delete", db.Database.CurrentTransaction is not null));
            return inner.DeleteAsync(key, ct);
        }
    }

    private sealed class FailingRollback : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("The rollback could not reach the database.");
    }

    private sealed class RefusedThenFailingReader(IAccountCredentialReader credentials) : IPerpetualVenueReader
    {
        public string VenueId => SecurityFixture.Venue.Id;
        public bool Opened { get; private set; }
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueInstrument>>([]);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Use the Account overload.");
        public async Task<PerpetualVenueReadResult> ReadAsync(Account account, CancellationToken cancellationToken)
        {
            var attempt = await credentials.ReadAttemptAsync(account.Id, cancellationToken);
            Opened = attempt?.Token == SecurityFixture.Token;
            await credentials.MarkRefusedAsync(account.Id, attempt!.Revision, cancellationToken);
            throw new VenueReadException("The venue refused the request.");
        }
    }

    private sealed class Transactions : DbTransactionInterceptor
    {
        public int Starts, Commits, Rollbacks;
        public bool CancelledRollback, CancellableCommit;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            CancellableCommit |= cancellationToken.CanBeCanceled;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            Starts++;
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            return Task.CompletedTask;
        }
        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Rollbacks++;
            CancelledRollback |= cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }
    }
}
