using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;
using Vessel.Application.MarketData;
using Vessel.Application.Overview;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class ActivityCachePostgresTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At.AddDays(1); }
    private static ImportedFill Fill(Account account, int n) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = "BTC",
        SourceFillId = n.ToString(), ExternalOrderId = n.ToString(), Side = n % 2 == 0 ? "buy" : "sell",
        PositionEffect = n % 2 == 0 ? "open" : "close", Direction = "fixture", Quantity = 1,
        Price = 100, Fee = 0.01m, FeeToken = "USDC", ClosedPnlUsd = n % 2, TransactionHash = "", OccurredAtUtc = At.AddMinutes(n)
    };
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static Task<long> Revision(VesselDbContext db, Guid owner) => db.Database.SqlQuery<long>($"""
        SELECT COALESCE((SELECT revision FROM activity_facts_revisions WHERE owner_id = {owner}), 0) AS "Value"
        """).SingleAsync();

    private sealed class Reads
    {
        public int Count;
        public Func<Task>? BeforeRead;
    }
    private sealed class CountingStore(VesselDbContext db, Reads reads) : IActivityStore
    {
        public async Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null)
        {
            Interlocked.Increment(ref reads.Count);
            if (reads.BeforeRead is { } before) await before();
            return await new ActivityStore(db).ReadAsync(accountId, ct, accountIds);
        }
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
            new ActivityStore(db).PlayTitlesAsync(playIds, ct);
    }
    private static ServiceProvider Provider(CoreDatabase database, Reads reads, bool verifyReleased = false)
    {
        var services = new ServiceCollection();
        services.AddVesselPersistence(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Vessel"] = database.ConnectionString }).Build());
        services.AddScoped<IJournalOwnerContext, AssignedOwnerContext>();
        services.AddSingleton(reads);
        services.AddScoped<IActivityStore, CountingStore>();
        if (verifyReleased) services.AddScoped<IVenueRegistry, ReleasedConnectionRegistry>();
        return services.BuildServiceProvider();
    }
    private static async Task<ActivitySummaries> Read(ServiceProvider provider, Guid owner, Guid? account = null, IReadOnlySet<Guid>? ids = null)
    {
        await using var scope = provider.GetRequiredService<IOwnerScopeFactory>().Create(owner);
        return await scope.Services.GetRequiredService<IActivitySummaryReader>().ReadAsync(account, default, ids);
    }

    private sealed class ReleasedConnectionRegistry(VesselDbContext db) : IVenueRegistry
    {
        public IReadOnlyList<VenueDescriptor> Descriptors => [];
        public VenueDescriptor? Descriptor(string id) => null;
        public IPerpetualVenueReader? Reader(string id) => null;
        public IVenueFundingReader? Funding(string id)
        {
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
            Assert.Empty(db.ChangeTracker.Entries());
            return null;
        }
        public IVenueOrderReader? Orders(string id) => null;
        public ICandleReader? Candles(string id) => null;
        public IMarketContextReader? MarketContext(string id) => null;
        public IMarketStream? Stream(string id) => null;
        public IVenueAccountDiscovery? Discovery(string id) => null;
        public IVenueCredentialVerifier? CredentialVerifier(string id) => null;
    }

    [PostgresFact]
    public async Task Producer_releases_snapshot_and_connection_before_aggregation_and_still_hits_the_cache()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Fixture");
        await using (var db = database.Context(owner))
        { db.Accounts.Add(account); db.Fills.Add(Fill(account, 0)); await db.SaveChangesAsync(); }
        var reads = new Reads(); await using var provider = Provider(database, reads, verifyReleased: true);
        var result = await Read(provider, owner);
        Assert.Single(result.Facts.Fills);
        Assert.Same(result, await Read(provider, owner)); Assert.Equal(1, reads.Count);
    }

    [PostgresTheory]
    [InlineData("price")]
    [InlineData("result")]
    [InlineData("fee")]
    [InlineData("snapshot-coverage")]
    [InlineData("history-coverage")]
    [InlineData("funding-detail")]
    public async Task Version_tracks_corrected_metrics_coverage_and_funding_details_without_new_executions(string change)
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Fixture");
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account); db.Fills.AddRange(Fill(account, 0), Fill(account, 1));
            db.Snapshots.Add(new() { OwnerId = owner, AccountId = account.Id, ObservedAtUtc = At, ValueScope = "fixture" });
            db.FundingPayments.Add(new() { Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, SourceId = "original",
                ContractId = "BTC", Asset = "USDC", Amount = -0.01m, OccurredAtUtc = At.AddSeconds(30) });
            await db.SaveChangesAsync();
        }
        var reads = new Reads(); await using var provider = Provider(database, reads);
        var before = await Read(provider, owner);
        Assert.Same(before, await Read(provider, owner));
        var original = Assert.Single(before.Operations);
        Assert.Contains(before.Coverage, c => c.Notice.Contains("predates retained fills", StringComparison.Ordinal));
        await using (var writer = database.Context(owner))
        {
            switch (change)
            {
                case "price": (await writer.Fills.SingleAsync(f => f.Side == "buy")).Price = 101; break;
                case "result": (await writer.Fills.SingleAsync(f => f.Side == "sell")).ClosedPnlUsd = 2; break;
                case "fee": (await writer.Fills.SingleAsync(f => f.Side == "sell")).Fee = 0.02m; break;
                case "snapshot-coverage": (await writer.Snapshots.SingleAsync()).ObservedAtUtc = At.AddMinutes(2); break;
                case "history-coverage": (await writer.Accounts.SingleAsync()).RecordSync(At, "Updated retained history coverage."); break;
                case "funding-detail": (await writer.FundingPayments.SingleAsync()).SourceId = "corrected"; break;
            }
            await writer.SaveChangesAsync();
        }
        var after = await Read(provider, owner);
        var corrected = Assert.Single(after.Operations);
        Assert.Equal(original.Id, corrected.Id);
        Assert.Equal(original.FillCount, corrected.FillCount);
        Assert.Equal(original.LastOccurredAtUtc, corrected.LastOccurredAtUtc);
        Assert.NotEqual(before.Version, after.Version);
        switch (change)
        {
            case "price": Assert.NotEqual(original.AverageEntryPrice, corrected.AverageEntryPrice); break;
            case "result": Assert.NotEqual(original.NetResultUsd, corrected.NetResultUsd); break;
            case "fee": Assert.NotEqual(original.StablecoinFeesNominalUsd, corrected.StablecoinFeesNominalUsd); break;
            case "snapshot-coverage": Assert.DoesNotContain(after.Coverage, c => c.Notice.Contains("predates retained fills", StringComparison.Ordinal)); break;
            case "history-coverage": Assert.Contains(after.Coverage, c => c.Notice == "Updated retained history coverage."); break;
            case "funding-detail":
                Assert.Equal(original.FundingNetUsd, corrected.FundingNetUsd);
                Assert.Equal(original.FundingPaymentCount, corrected.FundingPaymentCount);
                Assert.Equal("corrected", Assert.Single(after.Facts.FundingPayments).SourceId);
                break;
        }
        await using var fresh = database.Context(owner);
        Assert.Equal(Json(ActivityService.Aggregate(await new ActivityStore(fresh).ReadAsync(null, default))), Json(after));
        Assert.Same(after, await Read(provider, owner));
        Assert.Equal(2, reads.Count);
    }

    [PostgresFact]
    public async Task Cached_and_uncached_activity_and_overview_are_byte_identical_across_scopes_filters_pages()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Fixture");
        var first = new Account(Guid.NewGuid(), owner, "manual", "First"); first.Configure(portfolio.Id, null, 100);
        var second = new Account(Guid.NewGuid(), owner, "manual", "Second"); second.Configure(null, null, 200);
        await using (var db = database.Context(owner))
        {
            db.Portfolios.Add(portfolio); db.Accounts.AddRange(first, second);
            db.Fills.AddRange(Enumerable.Range(0, 120).Select(n => Fill(first, n)).Concat(Enumerable.Range(0, 8).Select(n => Fill(second, n))));
            await db.SaveChangesAsync();
        }
        var reads = new Reads(); await using var provider = Provider(database, reads);
        await using var scope = provider.GetRequiredService<IOwnerScopeFactory>().Create(owner);
        var reader = scope.Services.GetRequiredService<IActivitySummaryReader>();
        await using var dbRead = database.Context(owner);
        var store = new WorkspaceStore(dbRead, TestUnitOfWork.For(dbRead));
        var workspace = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(), TestUnitOfWork.For(dbRead));
        var cold = new ActivityService(new ActivityStore(dbRead), store, new Clock());
        var cached = new ActivityService(new ActivityStore(dbRead), store, new Clock(), summaries: reader);
        foreach (var selected in new[] { "all", "unassigned", $"portfolio:{portfolio.Id}", $"account:{first.Id}" })
        {
            foreach (var query in new[]
            {
                new ActivityQuery(Scope: selected, PageSize: "25"),
                new ActivityQuery(Scope: selected, Page: "2", PageSize: "25", Direction: "long", Status: "closed", Period: "7"),
                new ActivityQuery(Scope: selected, Account: second.Id.ToString(), Portfolio: "unassigned", Contract: "BTC", Period: "custom", From: "2026-10-18", To: "2026-10-18")
            })
            {
                var expected = Json(await cold.PageAsync(query, default));
                Assert.Equal(expected, Json(await cached.PageAsync(query, default))); // miss
                Assert.Equal(expected, Json(await cached.PageAsync(query, default))); // hit
            }
            foreach (var period in new[] { "7", "30", "90", "all" })
            foreach (var group in new[] { "account", "portfolio" })
            {
                var expected = await new OverviewSummaryService(store, workspace, cold, new Clock()).GetAsync(group, period, selected, default);
                Assert.Equal(Json(expected), Json(await new OverviewSummaryService(store, workspace, cached, new Clock()).GetAsync(group, period, selected, default)));
            }
        }
        Assert.Equal(3, reads.Count); // all, unassigned, first account; portfolio and account share their exact set.
    }

    [PostgresFact]
    public async Task Every_fact_table_write_from_a_separate_connection_invalidates_and_matches_uncached_results()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Fixture");
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Moved");
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("manual", "BTC"), InstrumentSource.Manual, "{}", At);
        var order = TestOrders.For(play, "0");
        var link = new PlayOrderLink(play, order, OrderLinkRole.Entry, "entry", null, OrderLinkState.Linked, OrderLinkSource.Owner, At);
        await using (var db = database.Context(owner))
        {
            db.Portfolios.Add(portfolio); db.Accounts.Add(account); db.Plays.Add(play); db.Orders.Add(order); db.OrderLinks.Add(link);
            db.Fills.AddRange(Fill(account, 0), Fill(account, 1));
            db.Snapshots.Add(new() { OwnerId = owner, AccountId = account.Id, ObservedAtUtc = At.AddDays(1), ValueScope = "fixture",
                Positions = [new() { OwnerId = owner, AccountId = account.Id, ContractId = "BTC" }],
                Stablecoins = [new() { OwnerId = owner, AccountId = account.Id, TokenId = "USDC", Symbol = "USDC" }] });
            db.FundingPayments.Add(new() { Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, SourceId = "1", ContractId = "BTC", Asset = "USDC", Amount = -0.01m, OccurredAtUtc = At.AddSeconds(30) });
            db.FundingStates.Add(new() { OwnerId = owner, AccountId = account.Id, LastAttemptFromUtc = At, LastAttemptToUtc = At.AddDays(1), Notice = "fixture" });
            await db.SaveChangesAsync();
        }
        var reads = new Reads(); await using var provider = Provider(database, reads);
        var previous = await Read(provider, owner);
        async Task Change(Func<VesselDbContext, Task> write)
        {
            await using (var worker = database.Context(owner))
            { await write(worker); await worker.SaveChangesAsync(); }
            var next = await Read(provider, owner);
            Assert.NotSame(previous, next);
            await using var fresh = database.Context(owner);
            Assert.Equal(Json(ActivityService.Aggregate(await new ActivityStore(fresh).ReadAsync(null, default))), Json(next));
            Assert.Same(next, await Read(provider, owner)); previous = next;
        }
        await Change(db => { db.Fills.Add(Fill(account, 2)); return Task.CompletedTask; });
        await Change(db =>
        {
            db.LeverageObservations.Add(new()
            {
                Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ContractId = "BTC",
                ObservedAtUtc = At.AddMinutes(2).AddSeconds(30), SignedQuantity = 1, EntryPrice = 100, Leverage = 5
            });
            return Task.CompletedTask;
        });
        await Change(async db => (await db.LeverageObservations.SingleAsync()).Leverage = 7);
        await Change(async db => db.LeverageObservations.Remove(await db.LeverageObservations.SingleAsync()));
        await Change(async db => (await db.Fills.OrderBy(f => f.OccurredAtUtc).FirstAsync()).Price = 101);
        await Change(async db => (await db.Orders.SingleAsync()).ExternalOrderId = "other");
        await Change(async db => (await db.Orders.SingleAsync()).ExternalOrderId = "0");
        await Change(async db => (await db.OrderLinks.SingleAsync()).Dismiss(At));
        await Change(async db => (await db.OrderLinks.SingleAsync()).Confirm(At));
        await Change(async db => db.OrderLinks.Remove(await db.OrderLinks.SingleAsync()));
        await Change(async db => (await db.FundingPayments.SingleAsync()).Amount = 0.1234567890123456789012345678m);
        await Change(async db => (await db.FundingStates.SingleAsync()).Notice = "new funding coverage");
        await Change(async db => (await db.Snapshots.SingleAsync()).ObservedAtUtc = At);
        await Change(async db => (await db.Positions.SingleAsync()).SignedQuantity = 7);
        await Change(async db => (await db.Stablecoins.SingleAsync()).Total = 100);
        await Change(async db => (await db.Accounts.SingleAsync()).RecordSync(At, "new history coverage"));
        await Change(async db => (await db.Accounts.SingleAsync()).UpdateSettings("Renamed", null, true));
        await Change(async db => (await db.Accounts.SingleAsync()).UpdateSettings("Renamed", portfolio.Id, true));
        await Change(async db => (await db.Accounts.SingleAsync()).UpdateSettings("Renamed", null, false));
        await Change(async db => (await db.Accounts.SingleAsync()).UpdateSettings("Renamed", null, true));
        await Change(async db => { db.Fills.RemoveRange(await db.Fills.ToListAsync()); db.FundingPayments.RemoveRange(await db.FundingPayments.ToListAsync()); db.FundingStates.RemoveRange(await db.FundingStates.ToListAsync()); db.Snapshots.RemoveRange(await db.Snapshots.ToListAsync()); db.Orders.RemoveRange(await db.Orders.ToListAsync()); db.Plays.RemoveRange(await db.Plays.ToListAsync()); });
        await Change(async db => db.Accounts.Remove(await db.Accounts.SingleAsync()));
        Assert.Equal(23, reads.Count);
    }

    [PostgresFact]
    public async Task Revision_and_facts_share_snapshot_and_owners_never_share_results()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Fixture");
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); db.Fills.Add(Fill(account, 0)); await db.SaveChangesAsync(); }
        var reads = new Reads(); await using var provider = Provider(database, reads);
        var started = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        reads.BeforeRead = async () => { started.SetResult(); await resume.Task; };
        var pending = Read(provider, owner); await started.Task;
        await using (var worker = database.Context(owner)) { worker.Fills.Add(Fill(account, 1)); await worker.SaveChangesAsync(); }
        reads.BeforeRead = null; resume.SetResult();
        Assert.Single((await pending).Facts.Fills);
        var latest = await Read(provider, owner); Assert.Equal(2, latest.Facts.Fills.Count);
        Assert.Same(latest, await Read(provider, owner));
        Assert.Empty((await Read(provider, other)).Facts.Fills);
        // Same account input in a foreign owner scope must still return 404, even with a cached own result.
        await Read(provider, owner, account.Id);
        var error = await Assert.ThrowsAsync<Vessel.Application.Common.ProblemException>(() => Read(provider, other, account.Id));
        Assert.Equal(404, error.StatusCode);
    }

    [PostgresFact]
    public async Task Revision_is_commit_ordered_once_per_transaction_and_takes_no_lock_before_commit()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var first = new Account(Guid.NewGuid(), owner, "manual", "First");
        var second = new Account(Guid.NewGuid(), owner, "manual", "Second");
        var play = new Play(Guid.NewGuid(), second, new PerpetualInstrument("manual", "BTC"), InstrumentSource.Manual, "{}", At);
        var order = TestOrders.For(play, "0");
        await using (var db = database.Context(owner))
        { db.Accounts.AddRange(first, second); db.Plays.Add(play); db.Orders.Add(order); db.OrderLinks.Add(new(play, order, OrderLinkRole.Entry, "entry", null, OrderLinkState.Linked, OrderLinkSource.Owner, At)); await db.SaveChangesAsync(); }
        await using var observer = database.Context(owner); var initial = await Revision(observer, owner);
        await using var sync = database.Context(owner); await using var older = await sync.Database.BeginTransactionAsync();
        await sync.Database.ExecuteSqlInterpolatedAsync($"UPDATE accounts SET \"Name\" = 'sync' WHERE \"OwnerId\" = {owner} AND \"Id\" = {first.Id}");
        await using var linker = database.Context(owner); await using var newer = await linker.Database.BeginTransactionAsync();
        await linker.Database.ExecuteSqlInterpolatedAsync($"UPDATE play_order_links SET \"UpdatedAtUtc\" = {At.AddHours(1)} WHERE \"OwnerId\" = {owner}");
        Assert.Equal(initial, await Revision(observer, owner));
        // Account sync holds A; linking has changed a link and now waits for A. A revision lock here
        // would deadlock sync's commit against linking's account lock request.
        var blocked = linker.Database.ExecuteSqlInterpolatedAsync($"UPDATE accounts SET \"Name\" = 'link' WHERE \"OwnerId\" = {owner} AND \"Id\" = {first.Id}");
        await older.CommitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        await newer.CommitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(initial + 2, await Revision(observer, owner));
        // An older xid can commit after a newer one: both must advance the revision.
        await using var oldDb = database.Context(owner); await using var oldTx = await oldDb.Database.BeginTransactionAsync();
        await oldDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE accounts SET \"Name\" = 'old' WHERE \"OwnerId\" = {owner} AND \"Id\" = {first.Id}");
        await using (var newDb = database.Context(owner))
            await newDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE accounts SET \"Name\" = 'new' WHERE \"OwnerId\" = {owner} AND \"Id\" = {second.Id}");
        Assert.Equal(initial + 3, await Revision(observer, owner));
        await oldTx.CommitAsync(); Assert.Equal(initial + 4, await Revision(observer, owner));
    }

    [PostgresFact]
    public async Task Immediate_constraints_bump_later_owners_once_and_savepoint_rollback_restores_the_sets()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var rolledBack = Guid.NewGuid();
        var accounts = new[] { first, second, rolledBack }.Select(o => new Account(Guid.NewGuid(), o, "manual", "Fixture")).ToArray();
        foreach (var account in accounts)
        {
            await using var db = database.Context(account.OwnerId);
            db.Accounts.Add(account); await db.SaveChangesAsync();
        }
        await using var writer = database.Context(first);
        await using var tx = await writer.Database.BeginTransactionAsync();
        async Task Insert(Account account, int n)
        {
            await using var db = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>()
                .UseNpgsql(writer.Database.GetDbConnection()).Options, new CoreOwner(account.OwnerId));
            await db.Database.UseTransactionAsync(tx.GetDbTransaction());
            db.Fills.Add(Fill(account, n)); await db.SaveChangesAsync();
        }
        await Insert(accounts[0], 0);
        await writer.Database.ExecuteSqlRawAsync("SET CONSTRAINTS ALL IMMEDIATE");
        Assert.Equal(2, await Revision(writer, first));
        await tx.CreateSavepointAsync("owner_rollback");
        await Insert(accounts[2], 0);
        await tx.RollbackToSavepointAsync("owner_rollback");
        await Insert(accounts[1], 0);
        await Insert(accounts[0], 1); await Insert(accounts[1], 1);
        await tx.CommitAsync();
        await using var observer = database.Context(first);
        Assert.Equal(2, await Revision(observer, first)); Assert.Equal(2, await Revision(observer, second));
        Assert.Equal(1, await Revision(observer, rolledBack));
    }

    [PostgresFact]
    public async Task Trigger_functions_keep_the_migration_schema_when_the_writer_changes_search_path()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Fixture");
        await using var writer = database.Context(owner);
        writer.Accounts.Add(account); await writer.SaveChangesAsync();
        var schema = new Npgsql.NpgsqlConnectionStringBuilder(database.ConnectionString).SearchPath;
        await using var tx = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlRawAsync("SET LOCAL search_path = pg_catalog");
        await using (var command = writer.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = tx.GetDbTransaction();
            // Schema comes only from CoreDatabase's generated identifier, never from user input.
            command.CommandText = $"UPDATE \"{schema}\".accounts SET \"Name\" = 'changed'";
            await command.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        await using var observer = database.Context(owner);
        Assert.Equal(2, await Revision(observer, owner));
        Assert.Equal("changed", (await observer.Accounts.SingleAsync()).Name);
    }

    [PostgresFact]
    public async Task Unit_of_work_pins_fact_writes_to_read_committed_even_with_a_stricter_session_default()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var writer = database.Context(owner);
        await writer.Database.OpenConnectionAsync();
        await writer.Database.ExecuteSqlRawAsync("SET default_transaction_isolation = 'serializable'");
        await new UnitOfWork(writer).ExecuteAsync(() =>
        {
            Assert.Equal(System.Data.IsolationLevel.ReadCommitted, writer.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel);
            writer.Accounts.Add(new(Guid.NewGuid(), owner, "manual", "Fixture"));
            return Task.FromResult(true);
        }, default);
        await using var observer = database.Context(owner);
        Assert.Equal(1, await Revision(observer, owner));
    }

    [PostgresFact]
    public async Task Real_refresh_unit_invalidates_imports_funding_and_snapshot_from_another_context()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Fixture");
        account.Configure(null, "0x" + new string('1', 40), null);
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); }
        var reads = new Reads(); await using var provider = Provider(database, reads);
        var empty = await Read(provider, owner);
        var result = new FixtureReader().Result;
        result = result with { Funding = new([new("funding-1", "BTC", null, -0.01m, "USDC", result.Snapshot.ObservedAtUtc.AddMinutes(-1), "")],
            result.Snapshot.ObservedAtUtc.AddDays(-1), result.Snapshot.ObservedAtUtc, true, "complete fixture") };
        await using (var worker = database.Context(owner))
        {
            var unit = TestUnitOfWork.For(worker); var store = new WorkspaceStore(worker, unit);
            await unit.ExecuteAsync(() => store.WithAccountLockAsync(account.Id, async locked =>
            { await store.SaveRefreshAsync(locked, result, default); return true; }, default), default);
        }
        var refreshed = await Read(provider, owner);
        Assert.NotSame(empty, refreshed); Assert.Single(refreshed.Facts.Fills);
        Assert.Single(refreshed.Facts.FundingPayments); Assert.Single(refreshed.Facts.FundingStates);
        Assert.Single(refreshed.Facts.Snapshots); Assert.Single(refreshed.Facts.Snapshots[0].Positions);
        Assert.Same(refreshed, await Read(provider, owner)); Assert.Equal(2, reads.Count);
    }

    [PostgresFact]
    public async Task Migration_backfills_existing_owners_and_initializes_new_owners_and_rollback_does_not_invalidate()
    {
        await using var database = await CoreDatabase.CreateAsync("20261018120000_ServerNotificationState");
        var owner = Guid.NewGuid(); var account = new Account(Guid.NewGuid(), owner, "manual", "Existing");
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); await db.Database.MigrateAsync(); }
        await using var observer = database.Context(owner); Assert.Equal(1, await Revision(observer, owner));
        await using (var db = database.Context(owner))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            (await db.Accounts.SingleAsync()).UpdateSettings("rolled back", null, false); await db.SaveChangesAsync(); await tx.RollbackAsync();
        }
        Assert.Equal(1, await Revision(observer, owner));
        var empty = Guid.NewGuid(); Assert.Equal(0, await Revision(observer, empty));
        await using (var db = database.Context(empty)) { db.Accounts.Add(new(Guid.NewGuid(), empty, "manual", "New")); await db.SaveChangesAsync(); }
        Assert.Equal(1, await Revision(observer, empty));
    }
}
