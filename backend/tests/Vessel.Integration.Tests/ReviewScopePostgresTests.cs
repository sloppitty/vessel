using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Issue #109: Review reads only the closed Plays in scope, and counts unassigned fills by the order key. The scope filter in
/// SQL must select exactly what Review's in-memory filter keeps, and the count must follow the rule it had before.
/// </summary>
public sealed class ReviewScopePostgresTests
{
    /// <summary>Forwards everything except the scoped read, which falls back to the interface's in-memory filter.</summary>
    private sealed class InMemoryScope(SizingStore inner) : ISizingStore
    {
        public Task<SizingSettings?> SettingsAsync(CancellationToken ct) => inner.SettingsAsync(ct);
        public Task<List<ClosedPlayFacts>> ClosedPlaysAsync(CancellationToken ct) => inner.ClosedPlaysAsync(ct);
        public Task<List<ClosedPlayFacts>> ClosedPlaysAsync(string tradingMode, CancellationToken ct) => inner.ClosedPlaysAsync(tradingMode, ct);
        public Task<int> UnresolvedPlaysAsync(CancellationToken ct) => inner.UnresolvedPlaysAsync(ct);
        public Task<List<ImportedOrder>> OrdersAsync(IReadOnlyCollection<OrderKey> orders, CancellationToken ct) => inner.OrdersAsync(orders, ct);
        public void Add(SizingSettings settings) => inner.Add(settings);
        public Task SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
    }

    /// <summary>Every closed Play whatever the scope, as Review read them before issue #109.</summary>
    private sealed class Unscoped(SizingStore inner) : ISizingStore
    {
        public Task<SizingSettings?> SettingsAsync(CancellationToken ct) => inner.SettingsAsync(ct);
        public Task<List<ClosedPlayFacts>> ClosedPlaysAsync(CancellationToken ct) => inner.ClosedPlaysAsync(ct);
        public Task<List<ClosedPlayFacts>> ClosedPlaysAsync(string tradingMode, CancellationToken ct) => inner.ClosedPlaysAsync(tradingMode, ct);
        public Task<List<ClosedPlayFacts>> ClosedPlaysAsync(string tradingMode, IReadOnlyCollection<Guid> accountIds, string? instrument,
            CancellationToken ct) => inner.ClosedPlaysAsync(tradingMode, ct);
        public Task<int> UnresolvedPlaysAsync(CancellationToken ct) => inner.UnresolvedPlaysAsync(ct);
        public Task<List<ImportedOrder>> OrdersAsync(IReadOnlyCollection<OrderKey> orders, CancellationToken ct) => inner.OrdersAsync(orders, ct);
        public void Add(SizingSettings settings) => inner.Add(settings);
        public Task SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
    }

    private sealed record Seeded(Guid Owner, Account Main, Account Second, Account Loose, Portfolio Swing);

    private static ImportedFill Fill(Guid owner, Account account, string contract, string order, string id) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ContractId = contract, SourceFillId = id, ExternalOrderId = order,
        Side = ExecutionFacts.Buy, Direction = "", PositionEffect = ExecutionFacts.Open, Quantity = 1, Price = 100, FeeToken = "USDC",
        TransactionHash = "", OccurredAtUtc = ReviewClock.Now.AddHours(-2),
    };

    private static async Task<Seeded> SeedAsync(CoreDatabase database)
    {
        var f = new ReviewFixture();
        var swing = new Portfolio(Guid.NewGuid(), f.Owner, "Swing");
        f.Portfolios.Add(swing);
        var main = f.Account("Main", swing.Id);
        var second = f.Account("Second", swing.Id);
        var loose = f.Account("Loose");
        var disabled = f.Account("Disabled");
        f.Add(10, account: main);
        f.Add(-4, ReviewClock.Now.AddDays(-40), account: main);
        f.Add(-5, account: main, instrument: "ETH");
        f.Add(7, account: second);
        // Instrument equality is ordinal: "btc" is another instrument than "BTC".
        f.Add(3, account: loose, instrument: "btc");
        var renamed = f.Add(2, account: loose, instrument: "SOL");
        f.Add(30, account: disabled);
        disabled.UpdateSettings("Disabled", null, false);
        var foreign = new ReviewFixture();
        foreign.Add(99);
        foreach (var fixture in new[] { f, foreign })
        {
            await using var db = database.Context(fixture.Owner);
            db.Portfolios.AddRange(fixture.Portfolios);
            db.Accounts.AddRange(fixture.Accounts);
            foreach (var facts in fixture.Closed)
            {
                db.Plays.Add(facts.Play);
                db.PlanRevisions.AddRange(facts.Revisions);
                foreach (var linked in facts.Fills)
                {
                    db.Fills.Add(linked.Fill);
                    var order = TestOrders.For(facts.Play, linked.Fill.ExternalOrderId, linked.Fill.Side, size: linked.Fill.Quantity);
                    db.Orders.Add(order);
                    db.OrderLinks.Add(new PlayOrderLink(facts.Play, order, linked.Role,
                        linked.Role == OrderLinkRole.Entry ? "e" : null, null, OrderLinkState.Linked, OrderLinkSource.Owner, ReviewClock.Now));
                }
            }
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(f.Owner))
        {
            var linked = f.Closed[0].Fills[0].Fill;
            var mainBtc = f.Closed[0].Play;
            // A fill without any order; one with a linked order's ID on another contract; one with it on another account.
            db.Fills.Add(Fill(f.Owner, main, "ETH", "no-order", "u1"));
            db.Fills.Add(Fill(f.Owner, main, "ETH", linked.ExternalOrderId, "u2"));
            db.Fills.Add(Fill(f.Owner, second, "BTC", linked.ExternalOrderId, "u3"));
            // An order whose only link was rejected.
            var rejected = TestOrders.For(mainBtc, "rejected");
            db.Orders.Add(rejected);
            db.OrderLinks.Add(new PlayOrderLink(mainBtc, rejected, OrderLinkRole.Exit, null, null, OrderLinkState.Rejected,
                OrderLinkSource.Owner, ReviewClock.Now));
            db.Fills.Add(Fill(f.Owner, main, "BTC", "rejected", "u4"));
            // A fill after the read's time, moved there below.
            db.Fills.Add(Fill(f.Owner, loose, "btc", "after-now", "u5"));
            await db.SaveChangesAsync();
            // A linked Play whose instrument no longer matches its orders (written outside the domain): its fills are not its own.
            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE plays SET ""ContractId"" = 'SOLX' WHERE ""Id"" = {renamed.Play.Id}");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE imported_fills SET ""OccurredAtUtc"" = {ReviewClock.Now.AddHours(1)} WHERE ""SourceFillId"" = 'u5'");
        }
        return new(f.Owner, main, second, loose, swing);
    }

    /// <summary>The count as Review wrote it before issue #109: from each Linked link of the fill's account to its order and Play.</summary>
    private static Task<int> ReferenceUnassignedAsync(VesselDbContext db, IReadOnlyCollection<Guid> ids, string? instrument, DateTimeOffset? from)
    {
        var fills = db.Fills.Where(f => ids.Contains(f.AccountId) && f.OccurredAtUtc <= ReviewClock.Now &&
            (instrument == null || f.ContractId == instrument));
        if (from is { } start) fills = fills.Where(f => f.OccurredAtUtc > start);
        return fills.CountAsync(f => !db.OrderLinks.Any(l => l.AccountId == f.AccountId && l.State == OrderLinkState.Linked &&
                db.Orders.Any(o => o.OwnerId == l.OwnerId && o.AccountId == l.AccountId && o.Id == l.ImportedOrderId &&
                    o.ContractId == f.ContractId && o.ExternalOrderId == f.ExternalOrderId) &&
                db.Plays.Any(p => p.Id == l.PlayId && p.TradingMode == "real" && p.ContractId == f.ContractId)) &&
            !db.FillAllocations.Any(a => a.FillId == f.Id && a.Lines.Any(l => db.Plays.Any(p => p.Id == l.PlayId && p.TradingMode == "real"))));
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, value.GetType());

    [PostgresFact]
    public async Task Review_reads_only_the_closed_plays_in_scope_with_the_same_report_as_the_in_memory_filter()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.Context(s.Owner);
        var sizing = new SizingStore(db, TestUnitOfWork.For(db));
        var queries = new ReviewQuery[]
        {
            new(Period: "all"), new(Period: "month"), new(Period: "all", AccountId: s.Main.Id), new(Period: "month", AccountId: s.Main.Id),
            new(Period: "all", Portfolio: s.Swing.Id.ToString()), new(Period: "all", Portfolio: s.Swing.Id.ToString("N")),
            new(Period: "all", Portfolio: "unassigned"), new(Period: "all", Instrument: "BTC"), new(Period: "all", Instrument: "btc"),
            new(Period: "all", Instrument: "SOL"), new(Period: "all", Instrument: "SOLX"), new(Period: "all", Instrument: "NONE"),
            new(Period: "all", Portfolio: s.Swing.Id.ToString(), Instrument: "BTC"), new(Period: "all", AccountId: s.Loose.Id, Instrument: "btc"),
            new(Period: "all", Portfolio: "unassigned", AccountId: s.Main.Id), new(Period: "all", Week: ReviewService.CurrentWeek),
        };
        var stores = new ISizingStore[] { sizing, new InMemoryScope(sizing), new Unscoped(sizing) };
        var all = await new ReviewService(new ReviewStore(db, sizing), new ReviewClock(), TestVenues.With()).GetAsync(new(Period: "all"), default);
        Assert.Equal(["BTC", "ETH", "SOLX", "btc"], all.Options.Instruments.Order(StringComparer.Ordinal).ToList());
        Assert.Equal((5, 6), (all.Plays.Count, all.Coverage.UnassignedFills));
        foreach (var query in queries)
        {
            var results = new List<string>();
            foreach (var store in stores)
            {
                var service = new ReviewService(new ReviewStore(db, store), new ReviewClock(), TestVenues.With());
                var report = await service.GetAsync(query, default);
                // The instrument filter still offers every closed Play's instrument, whatever the scope.
                Assert.Equal(all.Options.Instruments, report.Options.Instruments);
                var export = await service.ExportAsync(query, default);
                results.Add(Json(report) + Json(await service.ExclusionsAsync(query, default)) + export.FileName + "\n" + export.Content +
                    Json(await service.ProcessAsync(new(query, Lane: "unanswered"), default)));
            }
            Assert.All(results, r => Assert.Equal(results[0], r));
        }

        // Only the Plays in scope are read: the selected account's, on the selected instrument, of the enabled accounts.
        var facts = await new ReviewStore(db, sizing).ReadAsync(new(Period: "all", AccountId: s.Main.Id, Instrument: "BTC"), ReviewClock.Now, default);
        Assert.Equal(2, facts.ClosedPlays.Count);
        Assert.All(facts.ClosedPlays, c => Assert.Equal((s.Main.Id, "BTC"), (c.Play.AccountId, c.Play.ContractId)));
        Assert.Equal(["BTC", "ETH", "SOLX", "btc"], facts.ClosedInstruments!.Order(StringComparer.Ordinal).ToList());
        var unassigned = await new ReviewStore(db, sizing).ReadAsync(new(Period: "all", Portfolio: "unassigned"), ReviewClock.Now, default);
        Assert.Equal(["SOLX", "btc"], unassigned.ClosedPlays.Select(c => c.Play.ContractId!).Order(StringComparer.Ordinal).ToList());
        Assert.Empty((await new ReviewStore(db, sizing).ReadAsync(new(Period: "all", Instrument: "Btc"), ReviewClock.Now, default)).ClosedPlays);
    }

    [PostgresFact]
    public async Task Unassigned_fills_are_counted_by_the_same_rule_at_every_boundary()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.Context(s.Owner);
        var service = new ReviewService(new ReviewStore(db, new SizingStore(db, TestUnitOfWork.For(db))), new ReviewClock(), TestVenues.With());
        var enabled = new[] { s.Main.Id, s.Second.Id, s.Loose.Id };
        var cases = new (ReviewQuery Query, Guid[] Ids, string? Instrument, int Expected)[]
        {
            // u1 (no order), u2 (another contract), u3 (another account), u4 (rejected link) and the renamed Play's two SOL fills.
            (new(Period: "all"), enabled, null, 6),
            (new(Period: "all", AccountId: s.Main.Id), [s.Main.Id], null, 3),
            (new(Period: "all", AccountId: s.Second.Id), [s.Second.Id], null, 1),
            (new(Period: "all", Portfolio: "unassigned"), [s.Loose.Id], null, 2),
            (new(Period: "all", Instrument: "BTC"), enabled, "BTC", 2),
            (new(Period: "all", Instrument: "ETH"), enabled, "ETH", 2),
            (new(Period: "all", Instrument: "btc"), enabled, "btc", 0),
            (new(Period: "all", Instrument: "SOL"), enabled, "SOL", 2),
        };
        foreach (var (query, ids, instrument, expected) in cases)
        {
            var report = await service.GetAsync(query, default);
            Assert.Equal(expected, await ReferenceUnassignedAsync(db, ids, instrument, null));
            Assert.Equal(expected, report.Coverage.UnassignedFills);
        }
        // The rolling month leaves out the fills before its start (none of them unassigned here).
        var month = await service.GetAsync(new(Period: "month"), default);
        Assert.Equal(await ReferenceUnassignedAsync(db, enabled, null, ReviewClock.Now.AddDays(-30)), month.Coverage.UnassignedFills);
    }
}
