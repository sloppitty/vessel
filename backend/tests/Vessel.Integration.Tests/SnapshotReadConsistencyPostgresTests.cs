using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Domain.Common;

namespace Vessel.Tests;

/// <summary>
/// Issue #91: a reader never combines a snapshot row from one refresh with position or wallet rows from another, and a
/// separate-ledger wallet that was never read is unknown, not empty.
/// </summary>
public sealed class SnapshotReadConsistencyPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private const string Usdc = "0x6d1e7cde53ba9467b783cb7c530ce054";

    // Perps equity, one position and a wallet total, all read together at one time on a separate-ledger account.
    private static PerpetualVenueReadResult Read(int minute, decimal perps, decimal? wallet, params VenuePosition[] positions) =>
        new(new(Start.AddMinutes(minute), "primary-perpetual-dex", perps, perps, 0, positions,
                wallet is { } total ? new(Start.AddMinutes(minute), "default", "spot", total == 0 ? [] : [new("USDC", 0, Usdc, total, 0, total)]) : null,
                AccountMode: "default"),
            [], [], "Bounded history.", PositionsComplete: true);

    private static async Task SaveAsync(VesselDbContext db, Guid accountId, PerpetualVenueReadResult read)
    {
        var work = TestUnitOfWork.For(db);
        var store = new WorkspaceStore(db, work);
        await work.ExecuteAsync(() => store.WithAccountLockAsync(accountId, async locked =>
        {
            await store.SaveRefreshAsync(locked, read, default);
            return true;
        }, default), default);
    }

    private static async Task<Account> SeedAsync(CoreDatabase database, Guid owner)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Separate ledgers", TradingMode.Real);
        account.Configure(null, "0x" + new string('d', 40), null);
        await using var db = database.Context(owner);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    public static TheoryData<bool> Reads => new() { true, false };

    [PostgresTheory]
    [MemberData(nameof(Reads))]
    public async Task A_refresh_between_the_halves_of_a_read_never_yields_a_mixed_snapshot(bool single)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = await SeedAsync(database, owner);
        // Before: 100 in perps with a BTC position and an empty wallet. After a transfer: 0 in perps, flat, 100 in the wallet.
        await using (var db = database.Context(owner)) await SaveAsync(db, account.Id, Read(1, 100, 0, new VenuePosition("BTC", 1, 100, 0, 1, 5)));

        // The refresh runs to completion just before the read's first statement that reads child rows.
        var gate = new RefreshBeforeChildRows(async () =>
        {
            await using var db = database.Context(owner);
            await SaveAsync(db, account.Id, Read(2, 0, 100));
        });
        await using var reader = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(gate).Options, new CoreOwner(owner));
        var store = new WorkspaceStore(reader, TestUnitOfWork.For(reader));
        var snapshot = single ? await store.SnapshotAsync(account.Id, default) : Assert.Single(await store.SnapshotsAsync(default));
        Assert.True(gate.Ran);

        Assert.NotNull(snapshot);
        var view = (snapshot.ObservedAtUtc, snapshot.Positions.Count, snapshot.Stablecoins.Sum(s => s.Total));
        Assert.Contains(view, new[] { (Start.AddMinutes(1), 1, 0m), (Start.AddMinutes(2), 0, 100m) });
        var balance = new WorkspaceService(store, new CoreOwner(owner), TestVenues.With(), TestUnitOfWork.For(reader));
        Assert.Equal("100", (await balance.AccountAsync(account.Id, TradingMode.Real, default)).BalanceUsd);
    }

    [PostgresFact]
    public async Task A_wallet_never_read_leaves_a_separate_ledger_balance_unknown_and_an_observed_empty_one_is_zero()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = await SeedAsync(database, owner);
        // The first read's wallet failed: the perps and the account mode were read, the wallet was not.
        await using (var db = database.Context(owner)) await SaveAsync(db, account.Id, Read(1, 40, null));
        await using (var db = database.Context(owner))
        {
            var service = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(), TestUnitOfWork.For(db));
            var unread = await service.AccountAsync(account.Id, TradingMode.Real, default);
            Assert.Null(unread.BalanceUsd);
            Assert.Equal(("40", "withdrawable"), (unread.AvailableBalanceUsd, unread.AvailableBalanceSource));
            Assert.Equal((0, "unavailable"), ((await service.OverviewAsync(TradingMode.Real, default)).Totals.BalanceAccountCount,
                (await service.OverviewAsync(TradingMode.Real, default)).Totals.BalanceCoverage));
            var summary = await new Vessel.Application.Overview.OverviewSummaryService(new WorkspaceStore(db, TestUnitOfWork.For(db)), service,
                ActivityTestSupport.Activity(db), new SizingStore(db, TestUnitOfWork.For(db)), TimeProvider.System).GetAsync("account", "all", "all", TradingMode.Real, default);
            Assert.Null(summary.Totals.Balance.AmountUsd);
            Assert.Contains(summary.Totals.Balance.Coverage.Reasons, r => r is { Reason: WorkspaceService.WalletNotReadReason, Count: 1 });
        }

        // A later read observes an empty wallet: a known zero, so the balance is the perps alone.
        await using (var db = database.Context(owner)) await SaveAsync(db, account.Id, Read(2, 40, 0));
        await using (var db = database.Context(owner))
        {
            var service = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(), TestUnitOfWork.For(db));
            Assert.Equal("40", (await service.AccountAsync(account.Id, TradingMode.Real, default)).BalanceUsd);
            Assert.Equal("0", (await service.SnapshotAsync(account.Id, TradingMode.Real, default))!.StablecoinWallet!.TotalNominalUsd);
        }
    }

    private sealed class RefreshBeforeChildRows(Func<Task> refresh) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Ran && (command.CommandText.Contains("account_positions", StringComparison.Ordinal) ||
                    command.CommandText.Contains("account_stablecoins", StringComparison.Ordinal)))
            {
                Ran = true;
                await refresh();
            }
            return result;
        }
    }
}
