using System.Runtime.CompilerServices;
using System.Text;
using Vessel.Application.Common;
using Vessel.Application.Export;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 5b (G-07, X9): one account's retained facts as CSV, with native IDs, exact decimals and separate fees and
/// funding, so tax and reconciliation tools can read them. Not a tax report: nothing is computed or inferred.
/// </summary>
public sealed class FactsExportTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();

    private sealed class Store(Account? account) : IFactsExportStore
    {
        public List<ImportedFill> Fills { get; } = [];
        public List<ManualExecution> Manual { get; } = [];
        public List<FundingPayment> Funding { get; } = [];
        public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => Task.FromResult(account?.Id == id ? account : null);
        public IAsyncEnumerable<ImportedFill> FillsAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) => Each(Fills);
        public IAsyncEnumerable<ManualExecution> ManualExecutionsAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) => Each(Manual);
        public IAsyncEnumerable<FundingPayment> FundingAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) => Each(Funding);
        private static async IAsyncEnumerable<T> Each<T>(IEnumerable<T> items, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items) { await Task.Yield(); yield return item; }
        }
    }

    private static ImportedFill Fill(Account account, string id, decimal price, decimal pnl, int? leverage = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = account.Id, ContractId = "BTC", VenueContractId = "0", SourceFillId = id,
        ExternalOrderId = "order-" + id, TransactionHash = "0xabc", Side = "sell", Direction = "Close Long", PositionEffect = "close",
        Price = price, Quantity = 0.5m, Fee = 0.0225m, FeeToken = "USDC", FeeBasis = "reported", ClosedPnlUsd = pnl, PnlBasis = "gross",
        OccurredAtUtc = T0, Leverage = leverage,
    };

    private static async Task<string> Read(ExportFile file)
    {
        using var stream = new MemoryStream();
        await file.Write(stream, default);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Executions_keep_native_ids_exact_decimals_and_their_fee_apart()
    {
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", "=Main");
        var store = new Store(account);
        store.Fills.Add(Fill(account, "f1", 100.123456789m, -3.5m, 5));
        var file = await new FactsExportService(store, TestVenues.With(), TimeProvider.System).PrepareAsync(account.Id, "executions", null, null, default);
        Assert.StartsWith("vessel-executions-", file.FileName);
        var lines = Lines(await Read(file));
        Assert.Equal(string.Join(",", FactsExportService.ExecutionColumns), lines[0]);
        // No notice row: the file is data only, for other tools.
        Assert.Equal(2, lines.Length);
        Assert.Equal("imported,'=Main,Hyperliquid,,BTC,0,f1,order-f1,0xabc,2026-10-01T10:00:00.0000000Z,sell,Close Long,close," +
            "100.123456789,0.5,0.0225,USDC,reported,-3.5,gross,5,,,", lines[1]);
    }

    [Fact]
    public async Task Manual_executions_follow_with_their_play_role_and_source_label()
    {
        var account = new Account(Guid.NewGuid(), Owner, "manual", "Broker");
        var store = new Store(account);
        var play = Guid.NewGuid();
        store.Manual.Add(new ManualExecution
        {
            Id = Guid.NewGuid(), OwnerId = Owner, PlayId = play, Role = "entry", EntryId = "e1", OccurredAtUtc = T0, Side = "buy",
            Quantity = "2e3", Price = "+1.5", Fee = null, FeeCurrency = null, Source = "Broker statement", CreatedAtUtc = T0, UpdatedAtUtc = T0,
        });
        var lines = Lines(await Read(await new FactsExportService(store, TestVenues.With(), TimeProvider.System)
            .PrepareAsync(account.Id, "executions", null, null, default)));
        // Owner-entered numbers as exact plain decimals; an unrecorded fee is empty, never zero.
        Assert.Equal($"manual,Broker,Manual,,,,,,,2026-10-01T10:00:00.0000000Z,buy,,open,1.5,2000,,,,,,,{play},entry,Broker statement", lines[1]);
    }

    [Fact]
    public async Task Funding_is_its_own_file_with_native_nanoseconds()
    {
        var account = new Account(Guid.NewGuid(), Owner, "risex", "Rise");
        var store = new Store(account);
        store.Funding.Add(new FundingPayment
        {
            Id = Guid.NewGuid(), OwnerId = Owner, AccountId = account.Id, SourceId = "pay-1", ContractId = "ETH", VenueContractId = "2",
            Amount = -0.000123m, Asset = "USDC", OccurredAtUtc = T0, OccurredAtUnixNanoseconds = 1759312800000000123,
        });
        var file = await new FactsExportService(store, TestVenues.With(), TimeProvider.System).PrepareAsync(account.Id, "funding", "2026-10-01", "2026-10-02", default);
        Assert.EndsWith("-20261001-20261002.csv", file.FileName);
        var lines = Lines(await Read(file));
        Assert.Equal(string.Join(",", FactsExportService.FundingColumns), lines[0]);
        Assert.EndsWith(",ETH,2,pay-1,2026-10-01T10:00:00.0000000Z,1759312800000000123,-0.000123,USDC", lines[1]);
    }

    [Theory]
    [InlineData("orders", null, null, 400)]
    [InlineData("executions", "2026-10-1", null, 400)]
    [InlineData("executions", "2026-10-02", "2026-10-01", 400)]
    public async Task Requests_are_checked_before_anything_is_read(string kind, string? from, string? to, int status)
    {
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Main");
        var error = await Assert.ThrowsAsync<ProblemException>(() =>
            new FactsExportService(new Store(account), TestVenues.With(), TimeProvider.System).PrepareAsync(account.Id, kind, from, to, default));
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task Unknown_disabled_and_paper_accounts_are_refused()
    {
        var disabled = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Off");
        disabled.UpdateSettings("Off", null, false);
        var service = new FactsExportService(new Store(disabled), TestVenues.With(), TimeProvider.System);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.PrepareAsync(Guid.NewGuid(), "executions", null, null, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => service.PrepareAsync(disabled.Id, "executions", null, null, default))).StatusCode);
        var paper = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Paper", "paper");
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() =>
            new FactsExportService(new Store(paper), TestVenues.With(), TimeProvider.System).PrepareAsync(paper.Id, "funding", null, null, default))).StatusCode);
    }

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
        }
        // Another owner reads nothing of this owner's account.
        await using (var foreign = database.Context(Guid.NewGuid()))
            Assert.Empty(await new ExportStore(foreign).FillsAsync(account.Id, null, null, default).ToListAsync());
    }
}
