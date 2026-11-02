using System.Runtime.CompilerServices;
using System.Text;
using Vessel.Application.Common;
using Vessel.Application.Export;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 5b (G-07, X9): one account's retained facts as CSV, with native IDs, exact decimals and separate fees and
/// funding, so tax and reconciliation tools can read them. Not a tax report: nothing is computed or inferred.
/// </summary>
public sealed class FactsExportPostgresTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();

    private static ImportedFill Fill(Account account, string id, decimal price, decimal pnl, int? leverage = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = account.Id, ContractId = "BTC", VenueContractId = "0", SourceFillId = id,
        ExternalOrderId = "order-" + id, TransactionHash = "0xabc", Side = "sell", Direction = "Close Long", PositionEffect = "close",
        Price = price, Quantity = 0.5m, Fee = 0.0225m, FeeToken = "USDC", FeeBasis = "reported", ClosedPnlUsd = pnl, PnlBasis = "gross",
        OccurredAtUtc = T0, Leverage = leverage,
    };

    [PostgresFact]
    public async Task The_store_reads_one_accounts_half_open_days_in_time_order()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Main");
        var other = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Other");
        await using (var db = database.Context(Owner))
        {
            db.Accounts.AddRange(account, other);
            var late = Fill(account, "late", 100, 0); late.OccurredAtUtc = Day.AddDays(1).AddTicks(-1);
            var next = Fill(account, "next-day", 100, 0); next.OccurredAtUtc = Day.AddDays(1);
            var early = Fill(account, "early", 100, 0); early.OccurredAtUtc = T0.AddHours(-1);
            db.Fills.AddRange(late, next, early, Fill(other, "other", 100, 0));
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(Owner))
        {
            var store = new ExportStore(db);
            var day = await store.FillsAsync(account.Id, Day, Day.AddDays(1), default).Select(f => f.SourceFillId).ToListAsync();
            Assert.Equal(["early", "late"], day);
            Assert.Equal(["early", "late", "next-day"], await store.FillsAsync(account.Id, null, null, default).Select(f => f.SourceFillId).ToListAsync());
            // Sol round 1 on #39: cancelling the download stops the database read.
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await store.FillsAsync(account.Id, null, null, cancelled.Token).ToListAsync());
        }
        // Another owner reads nothing of this owner's account.
        await using (var foreign = database.Context(Guid.NewGuid()))
            Assert.Empty(await new ExportStore(foreign).FillsAsync(account.Id, null, null, default).ToListAsync());
    }

    [PostgresFact]
    public async Task Manual_rows_carry_their_plays_instrument_and_funding_follows_native_nanoseconds()
    {
        // Sol round 1 on #39: the manual instrument was empty, and two settlements within one microsecond could swap.
        await using var database = await CoreDatabase.CreateAsync();
        var account = new Account(Guid.NewGuid(), Owner, "risex", "Rise");
        var manual = new Account(Guid.NewGuid(), Owner, "manual", "Broker");
        await using (var db = database.Context(Owner))
        {
            db.Accounts.AddRange(account, manual);
            var play = new Play(Guid.NewGuid(), manual, new Vessel.Domain.Markets.PerpetualInstrument("manual", "ETH"), InstrumentSource.Manual,
                PlayDocuments.Serialize(TestPlays.Plan()), T0);
            db.Plays.Add(play);
            db.ManualExecutions.Add(new ManualExecution
            {
                Id = Guid.NewGuid(), OwnerId = Owner, PlayId = play.Id, Role = "entry", EntryId = "entry-1", OccurredAtUtc = T0, Side = "buy",
                Quantity = "1", Price = "100", Source = "Broker statement", CreatedAtUtc = T0, UpdatedAtUtc = T0,
            });
            FundingPayment Pay(string id, long ns) => new()
            {
                Id = Guid.NewGuid(), OwnerId = Owner, AccountId = account.Id, SourceId = id, ContractId = "ETH", VenueContractId = "2", Amount = -1,
                Asset = "USDC", OccurredAtUtc = DateTimeOffset.UnixEpoch.AddTicks(ns / 1000 * 10), OccurredAtUnixNanoseconds = ns,
            };
            // Same microsecond; the later nanosecond has the lexically smaller ID.
            db.FundingPayments.AddRange(Pay("b-first", 1759312800000000101), Pay("a-second", 1759312800000000102));
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(Owner))
        {
            var store = new ExportStore(db);
            Assert.Equal("ETH", (await store.ManualExecutionsAsync(manual.Id, null, null, default).SingleAsync()).Instrument);
            Assert.Equal(["b-first", "a-second"], await store.FundingAsync(account.Id, null, null, default).Select(p => p.SourceId).ToListAsync());
        }
    }

    [PostgresFact]
    public async Task A_paper_request_is_refused_before_any_read()
    {
        // Sol round 1 on #39: a real account read in paper mode returned the CSV.
        await using var database = await CoreDatabase.CreateAsync();
        await using var factory = new CoreApiFactory(Owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        Assert.Equal(System.Net.HttpStatusCode.Conflict,
            (await client.GetAsync($"/api/accounts/{Guid.NewGuid()}/facts?kind=executions&tradingMode=paper")).StatusCode);
    }
}
