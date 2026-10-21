using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Execution and retrospective reads load only what can matter: the active Plays' contracts and links, one order or link
/// for owner actions, and the operation's orders for a preview. Each scenario runs on PostgreSQL against the narrowed
/// store and against a reference that reads the whole account, and both must agree exactly.
/// </summary>
public sealed class ExecutionNarrowedReadsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
    private static readonly Guid Closed = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid Eth = Guid.Parse("00000000-0000-0000-0000-0000000000e4");
    private static readonly Guid Cancelled = Guid.Parse("00000000-0000-0000-0000-0000000000d5");

    /// <summary>Implements only the whole-account reads, so every targeted read falls back to the port's reference rule.</summary>
    private sealed class FullReadStore(ExecutionStore inner) : IPlayExecutionStore
    {
        public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => inner.AccountAsync(id, ct);
        public Task<List<Play>> ActivePlaysAsync(Guid accountId, CancellationToken ct, bool settling = false) => inner.ActivePlaysAsync(accountId, ct, settling);
        public Task<HashSet<Guid>> SuggestedElsewhereAsync(Guid accountId, Guid playId, IReadOnlyCollection<Guid> orderIds, CancellationToken ct) =>
            inner.SuggestedElsewhereAsync(accountId, playId, orderIds, ct);
        public Task<List<ImportedOrder>> OrdersAsync(Guid accountId, CancellationToken ct) => inner.OrdersAsync(accountId, ct);
        public Task UpsertOrdersAsync(Account account, IReadOnlyList<VenueOrder> orders, DateTimeOffset observedAt, CancellationToken ct) =>
            inner.UpsertOrdersAsync(account, orders, observedAt, ct);
        public Task<List<PlayOrderLink>> LinksAsync(Guid accountId, CancellationToken ct) => inner.LinksAsync(accountId, ct);
        public Task<List<ImportedFill>> FillsAsync(Guid accountId, IReadOnlyCollection<OrderKey> orders, CancellationToken ct) =>
            inner.FillsAsync(accountId, orders, ct);
        public void Add(PlayOrderLink link) => inner.Add(link);
        public void Add(PlayStatusChange change) => inner.Add(change);
        public Task ForgetWatchesAsync(Guid playId, CancellationToken ct) => inner.ForgetWatchesAsync(playId, ct);
        public Task SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
        public Task<T> WithAccountLockAsync<T>(Guid accountId, Func<Task<T>> action, CancellationToken ct) =>
            inner.WithAccountLockAsync(accountId, action, ct);
    }

    /// <summary>The narrowed store, counting the orders, links and fill order IDs it hands out.</summary>
    private sealed class CountingStore(ExecutionStore inner) : IPlayExecutionStore
    {
        public int Orders { get; private set; }
        public int Links { get; private set; }
        public int FillOrderIds { get; private set; }
        public bool FullReads { get; private set; }
        public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => inner.AccountAsync(id, ct);
        public Task<List<Play>> ActivePlaysAsync(Guid accountId, CancellationToken ct, bool settling = false) => inner.ActivePlaysAsync(accountId, ct, settling);
        public Task<HashSet<Guid>> SuggestedElsewhereAsync(Guid accountId, Guid playId, IReadOnlyCollection<Guid> orderIds, CancellationToken ct) =>
            inner.SuggestedElsewhereAsync(accountId, playId, orderIds, ct);
        public Task<List<ImportedOrder>> OrdersAsync(Guid accountId, CancellationToken ct) { FullReads = true; return inner.OrdersAsync(accountId, ct); }
        public async Task<List<ImportedOrder>> OrdersAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds,
            IReadOnlyDictionary<string, DateTimeOffset> placedSince, CancellationToken ct)
        {
            var rows = await inner.OrdersAsync(accountId, orderIds, placedSince, ct);
            Orders += rows.Count;
            return rows;
        }
        public Task UpsertOrdersAsync(Account account, IReadOnlyList<VenueOrder> orders, DateTimeOffset observedAt, CancellationToken ct) =>
            inner.UpsertOrdersAsync(account, orders, observedAt, ct);
        public Task<List<PlayOrderLink>> LinksAsync(Guid accountId, CancellationToken ct) { FullReads = true; return inner.LinksAsync(accountId, ct); }
        public async Task<List<PlayOrderLink>> LinksAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds, IReadOnlyCollection<Guid> playIds,
            CancellationToken ct)
        {
            var rows = await inner.LinksAsync(accountId, orderIds, playIds, ct);
            Links += rows.Count;
            return rows;
        }
        public Task<ImportedOrder?> OrderAsync(Guid accountId, string contractId, string externalOrderId, CancellationToken ct) =>
            inner.OrderAsync(accountId, contractId, externalOrderId, ct);
        public Task<List<PlayOrderLink>> PlayLinksAsync(Guid accountId, Guid playId, CancellationToken ct) => inner.PlayLinksAsync(accountId, playId, ct);
        public Task<PlayOrderLink?> LinkAsync(Guid accountId, Guid playId, Guid linkId, CancellationToken ct) => inner.LinkAsync(accountId, playId, linkId, ct);
        public Task<HashSet<Guid>> LinkedOrderIdsAsync(Guid accountId, CancellationToken ct) => inner.LinkedOrderIdsAsync(accountId, ct);
        public Task<List<ImportedFill>> FillsAsync(Guid accountId, IReadOnlyCollection<OrderKey> orders, CancellationToken ct)
        {
            FillOrderIds += orders.Count;
            return inner.FillsAsync(accountId, orders, ct);
        }
        public void Add(PlayOrderLink link) => inner.Add(link);
        public void Add(PlayStatusChange change) => inner.Add(change);
        public Task ForgetWatchesAsync(Guid playId, CancellationToken ct) => inner.ForgetWatchesAsync(playId, ct);
        public Task SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
        public Task<T> WithAccountLockAsync<T>(Guid accountId, Func<Task<T>> action, CancellationToken ct) =>
            inner.WithAccountLockAsync(accountId, action, ct);
    }

    private sealed record Seeded(CoreDatabase Database, Guid Owner, Account Account, Account Other) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private static PlayExecutionService Service(VesselDbContext db, IPlayExecutionStore store) =>
        new(new PlayStore(db, TestUnitOfWork.For(db)), store, TestVenues.With(), TimeProvider.System, TestUnitOfWork.For(db));

    private static IPlayExecutionStore Store(VesselDbContext db, bool narrowed) =>
        narrowed ? new ExecutionStore(db, TestUnitOfWork.For(db)) : new FullReadStore(new ExecutionStore(db, TestUnitOfWork.For(db)));

    private static Task Reconcile(VesselDbContext db, PlayExecutionService service, Account account) =>
        TestUnitOfWork.For(db).ExecuteAsync(async () =>
        {
            await service.ReconcileImportedAsync(account, new VenueOrderReadResult([], T0.AddHours(1), "test"), [], default);
            return true;
        }, default);

    private static ImportedOrder Order(Account account, string id, string contract, string side, decimal price, int minute,
        string status = "open", bool reduceOnly = false, decimal size = 1) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ExternalOrderId = id, ContractId = contract, Side = side,
        OrderType = "Limit", LimitPrice = price, ReduceOnly = reduceOnly, OriginalSize = size, RemainingSize = status == "open" ? size : 0,
        PlacedAtUtc = T0.AddMinutes(minute), Status = status, VenueStatus = status, StatusAtUtc = T0.AddMinutes(minute),
        ObservedAtUtc = T0.AddMinutes(minute),
    };

    private static ImportedFill Fill(Account account, string orderId, string contract, string side, string effect, decimal price, int minute) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = contract, SourceFillId = orderId + "-fill-" + minute,
        ExternalOrderId = orderId, Side = side, Direction = "", PositionEffect = effect, Price = price, Quantity = 1, Fee = 0, FeeToken = "USDC",
        ClosedPnlUsd = 0, OccurredAtUtc = T0.AddMinutes(minute), TransactionHash = "",
    };

    private static Play Planned(VesselDbContext db, Account account, Guid id, string contract, int minute)
    {
        var play = new Play(id, account, new PerpetualInstrument("hyperliquid", contract), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), T0.AddMinutes(minute));
        var (change, revision) = play.MarkPlanned(T0.AddMinutes(minute));
        db.Plays.Add(play);
        db.Add(change);
        db.Add(revision);
        return play;
    }

    private static PlayOrderLink Link(Play play, ImportedOrder order, OrderLinkRole role, OrderLinkState state, int minute,
        OrderLinkSource source = OrderLinkSource.Owner) =>
        new(play, order, role, role == OrderLinkRole.Exit ? null : "entry-1", role is OrderLinkRole.Stop ? "stop-1" : role is OrderLinkRole.Target ? "target-1" : null,
            state, source, T0.AddMinutes(minute));

    /// <summary>
    /// Two active Plays on BTC, an open Play-to-be on ETH whose manual entry predates it, a Closed BTC Play holding an order
    /// that matches the active levels, a Cancelled BTC Play holding a BTC order, and unrelated DOGE history.
    /// </summary>
    private static async Task<Seeded> Seed(int unrelated = 0)
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Trading");
        var other = new Account(Guid.NewGuid(), owner, "hyperliquid", "Other");
        await using var db = database.Context(owner);
        db.Accounts.AddRange(account, other);
        await db.SaveChangesAsync();

        var closed = Planned(db, account, Closed, "BTC", 0);
        db.Add(closed.MarkOpen("Entry fill.", T0.AddMinutes(12)));
        db.Add(closed.MarkClosed("Exited.", T0.AddMinutes(13)));
        var first = Planned(db, account, First, "BTC", 10);
        var second = Planned(db, account, Second, "BTC", 10);
        var eth = Planned(db, account, Eth, "ETH", 30);
        var cancelled = Planned(db, account, Cancelled, "BTC", 1);
        db.Add(cancelled.Cancel(CancelReason.Other, null, T0.AddMinutes(2)));
        var orders = new List<ImportedOrder>
        {
            // Placed after the active BTC Plays were created and on their entry level, but linked to the Closed Play.
            Order(account, "closed-entry", "BTC", "buy", 100, 11, "filled"),
            // On the active entry level, but linked by the Cancelled Play.
            Order(account, "btc-by-eth", "BTC", "buy", 100, 15),
            // Ambiguous between both active BTC Plays; First declined it twice over (a suggested and a dismissed row).
            Order(account, "declined", "BTC", "buy", 100, 20),
            // First declined; Second already holds a suggestion that becomes the only candidate.
            Order(account, "suggested", "BTC", "buy", 100, 21),
            // Ambiguous: both active BTC Plays get a suggestion.
            Order(account, "ambiguous", "BTC", "sell", 110, 22, reduceOnly: true),
            Order(account, "resting", "BTC", "sell", 120, 23),
            Order(account, "old-btc", "BTC", "buy", 100, 5),
            // The ETH Play's manual entry predates the Play.
            Order(account, "eth-entry", "ETH", "buy", 100, 25, "filled"),
            Order(account, "eth-exit", "ETH", "sell", 105, 40, "filled", reduceOnly: true),
            Order(account, "sol-order", "SOL", "buy", 100, 26),
            Order(other, "other-btc", "BTC", "buy", 100, 24),
        };
        db.Orders.AddRange(orders);
        var o = orders.Where(x => x.AccountId == account.Id).ToDictionary(x => x.ExternalOrderId);
        for (var i = 0; i < unrelated; i++)
            db.Orders.Add(Order(account, $"doge-{i}", "DOGE", "buy", 100, 20 + i % 30, "filled"));
        db.Fills.AddRange(
            Fill(account, "closed-entry", "BTC", "buy", "open", 100, 12),
            Fill(account, "eth-entry", "ETH", "buy", "open", 100, 26),
            Fill(account, "eth-exit", "ETH", "sell", "close", 105, 41));
        for (var i = 0; i < unrelated; i++)
            db.Fills.Add(Fill(account, $"doge-{i}", "DOGE", "buy", "open", 100, 20 + i % 30));
        db.OrderLinks.AddRange(
            Link(closed, o["closed-entry"], OrderLinkRole.Entry, OrderLinkState.Linked, 12),
            Link(cancelled, o["btc-by-eth"], OrderLinkRole.Entry, OrderLinkState.Linked, 16),
            Link(first, o["declined"], OrderLinkRole.Entry, OrderLinkState.Suggested, 20, OrderLinkSource.Automatic),
            Link(first, o["declined"], OrderLinkRole.Entry, OrderLinkState.Dismissed, 21),
            Link(first, o["suggested"], OrderLinkRole.Entry, OrderLinkState.Dismissed, 21),
            Link(second, o["suggested"], OrderLinkRole.Entry, OrderLinkState.Suggested, 21, OrderLinkSource.Automatic),
            Link(eth, o["eth-entry"], OrderLinkRole.Entry, OrderLinkState.Linked, 31));
        await db.SaveChangesAsync();
        return new(database, owner, account, other);
    }

    private static string Normalized(PlayExecutionDto dto) => JsonSerializer.Serialize(dto with
    {
        Links = dto.Links.Select(l => l with { Id = Guid.Empty }).ToList(),
        Suggestions = dto.Suggestions.Select(l => l with { Id = Guid.Empty }).ToList(),
    });

    private static async Task<List<string>> Outcome(Seeded seeded, bool narrowed)
    {
        await using (var db = seeded.Database.Context(seeded.Owner))
            await Reconcile(db, Service(db, Store(db, narrowed)), seeded.Account);
        var outcome = new List<string>();
        await using (var db = seeded.Database.Context(seeded.Owner))
        {
            foreach (var id in new[] { Closed, First, Second, Eth, Cancelled })
                outcome.Add(Normalized(await Service(db, Store(db, narrowed)).GetAsync(id, default)));
            var external = await db.Orders.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.ExternalOrderId);
            outcome.AddRange((await db.OrderLinks.AsNoTracking().ToListAsync())
                .Select(l => $"{l.PlayId}|{external[l.ImportedOrderId]}|{l.LevelKey}|{l.State}|{l.Source}").Order(StringComparer.Ordinal));
            outcome.AddRange((await db.Plays.AsNoTracking().ToListAsync()).OrderBy(p => p.Id).Select(p => $"{p.Id}|{p.Status}"));
        }
        return outcome;
    }

    [PostgresFact]
    public async Task Narrowed_reconciliation_and_reads_match_whole_account_reads_exactly()
    {
        await using var narrowed = await Seed(unrelated: 40);
        await using var reference = await Seed(unrelated: 40);
        var expected = await Outcome(reference, narrowed: false);
        Assert.Equal(expected, await Outcome(narrowed, narrowed: true));
    }

    [PostgresFact]
    public async Task Reconciliation_respects_closed_holders_declines_ambiguity_and_a_manual_entry_that_predates_its_play()
    {
        await using var seeded = await Seed();
        await using (var db = seeded.Database.Context(seeded.Owner))
            await Reconcile(db, Service(db, Store(db, narrowed: true)), seeded.Account);
        await using var verify = seeded.Database.Context(seeded.Owner);
        var links = await verify.OrderLinks.AsNoTracking().ToListAsync();
        var external = await verify.Orders.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.ExternalOrderId);
        string[] Held(string order) => links.Where(l => external[l.ImportedOrderId] == order)
            .Select(l => $"{(l.PlayId == First ? "first" : l.PlayId == Second ? "second" : l.PlayId == Closed ? "closed" : l.PlayId == Eth ? "eth" : "cancelled")}:{l.State}")
            .Order(StringComparer.Ordinal).ToArray();

        // An order linked to a Closed or Cancelled Play is never linked or suggested again.
        Assert.Equal(["closed:Linked"], Held("closed-entry"));
        Assert.Equal(["cancelled:Linked"], Held("btc-by-eth"));
        // A level declined on one Play leaves the other as the only candidate, even with a duplicate suggested row.
        Assert.Equal(["first:Dismissed", "first:Suggested", "second:Linked"], Held("declined"));
        // The existing suggestion is the row that becomes linked; no new row is added.
        Assert.Equal(["first:Dismissed", "second:Linked"], Held("suggested"));
        Assert.Equal(OrderLinkSource.Automatic, links.Single(l => external[l.ImportedOrderId] == "suggested" && l.PlayId == Second).Source);
        // Ambiguous between two active Plays on one instrument: suggestions only.
        Assert.Equal(["first:Suggested", "second:Suggested"], Held("ambiguous"));
        Assert.Empty(Held("old-btc"));
        Assert.Empty(Held("resting"));
        // The manual entry placed before the ETH Play still dates its first entry fill: it opened, its exit linked and it closed.
        Assert.Equal(["eth:Linked"], Held("eth-exit"));
        var eth = await verify.Plays.AsNoTracking().SingleAsync(p => p.Id == Eth);
        Assert.Equal(PlayStatus.Closed, eth.Status);
        var opened = await verify.StatusChanges.AsNoTracking().SingleAsync(c => c.PlayId == Eth && c.To == PlayStatus.Open);
        Assert.Contains("on order eth-entry", opened.Note);
        // Linked entries that have not filled leave both BTC Plays planned.
        Assert.Equal(PlayStatus.Planned, (await verify.Plays.AsNoTracking().SingleAsync(p => p.Id == Second)).Status);
        Assert.Equal(PlayStatus.Planned, (await verify.Plays.AsNoTracking().SingleAsync(p => p.Id == First)).Status);
    }

    [PostgresFact]
    public async Task Execution_read_shows_cross_contract_orders_and_never_offers_orders_linked_elsewhere()
    {
        await using var seeded = await Seed();
        await using var db = seeded.Database.Context(seeded.Owner);
        var service = Service(db, Store(db, narrowed: true));

        var first = await service.GetAsync(First, default);
        // An order on another contract never belongs to a BTC Play, not even as a suggestion.
        Assert.DoesNotContain(first.Suggestions, s => s.Order?.OrderId == "sol-order");
        // Unlinked: BTC orders placed since the Play was created that no Play of the account has linked.
        Assert.Equal(["resting", "ambiguous", "suggested", "declined"], first.UnlinkedOrders.Select(o => o.OrderId));

        // The Cancelled Play still shows its linked order.
        var cancelled = await service.GetAsync(Cancelled, default);
        Assert.Equal("btc-by-eth", Assert.Single(cancelled.Links).Order!.OrderId);
        // The ETH Play's manual entry predates it and still shows with its fill.
        var eth = await service.GetAsync(Eth, default);
        var entry = Assert.Single(eth.Links);
        Assert.Equal(("eth-entry", "1"), (entry.Order!.OrderId, entry.FilledQuantity));
    }

    [PostgresFact]
    public async Task Link_and_unlink_reject_missing_foreign_and_other_contract_ids()
    {
        await using var seeded = await Seed();
        Guid secondSuggestion, closedLink, dismissed;
        await using (var db = seeded.Database.Context(seeded.Owner))
        {
            var suggested = (await db.Orders.SingleAsync(o => o.ExternalOrderId == "suggested")).Id;
            secondSuggestion = (await db.OrderLinks.SingleAsync(l => l.PlayId == Second && l.ImportedOrderId == suggested)).Id;
            closedLink = (await db.OrderLinks.SingleAsync(l => l.PlayId == Closed)).Id;
            dismissed = (await db.OrderLinks.SingleAsync(l => l.PlayId == First && l.ImportedOrderId == suggested)).Id;
        }
        async Task<int> Status(Func<PlayExecutionService, Task> action, Guid? owner = null)
        {
            await using var db = seeded.Database.Context(owner ?? seeded.Owner);
            return (await Assert.ThrowsAsync<ProblemException>(() => action(Service(db, Store(db, narrowed: true))))).StatusCode;
        }

        Assert.Equal(404, await Status(s => s.LinkAsync(First, new("missing", "entry", "entry-1"), default)));
        // Same owner, same contract, but another account's order.
        Assert.Equal(404, await Status(s => s.LinkAsync(First, new("other-btc", "entry", "entry-1"), default)));
        // This account's order on another contract.
        Assert.Equal(404, await Status(s => s.LinkAsync(First, new("sol-order", "entry", "entry-1"), default)));
        // Linked to a Closed Play already.
        Assert.Equal(409, await Status(s => s.LinkAsync(First, new("closed-entry", "entry", "entry-1"), default)));
        Assert.Equal(404, await Status(s => s.LinkAsync(First, new("resting", "entry", "entry-1"), default), Guid.NewGuid()));

        Assert.Equal(404, await Status(s => s.UnlinkAsync(First, Guid.NewGuid(), default)));
        // Another Play's link, and a link of another Play on the account.
        Assert.Equal(404, await Status(s => s.UnlinkAsync(First, secondSuggestion, default)));
        Assert.Equal(404, await Status(s => s.UnlinkAsync(First, closedLink, default)));
        // Already dismissed.
        Assert.Equal(404, await Status(s => s.UnlinkAsync(First, dismissed, default)));
        Assert.Equal(404, await Status(s => s.UnlinkAsync(Second, secondSuggestion, default), Guid.NewGuid()));

        // The owner can confirm and then decline on the Play that holds them.
        await using (var db = seeded.Database.Context(seeded.Owner))
        {
            var service = Service(db, Store(db, narrowed: true));
            var linked = await service.LinkAsync(Second, new("suggested", "entry", "entry-1"), default);
            Assert.Equal(("suggested", "owner"), (Assert.Single(linked.Links).Order!.OrderId, linked.Links[0].Source));
            Assert.Equal("planned", (await service.LinkAsync(First, new("resting", "target", "entry-1", "target-1"), default)).Status);
        }
        await using (var db = seeded.Database.Context(seeded.Owner))
        {
            var unlinked = await Service(db, Store(db, narrowed: true)).UnlinkAsync(Second, secondSuggestion, default);
            Assert.Empty(unlinked.Links);
        }
        await using var verify = seeded.Database.Context(seeded.Owner);
        Assert.Equal(OrderLinkState.Dismissed, (await verify.OrderLinks.SingleAsync(l => l.Id == secondSuggestion)).State);
    }

    [PostgresFact]
    public async Task Reconciliation_reads_do_not_grow_with_unrelated_contract_history()
    {
        async Task<(int Orders, int Links, int Fills, bool Full)> Count(int unrelated)
        {
            await using var seeded = await Seed(unrelated);
            await using var db = seeded.Database.Context(seeded.Owner);
            var store = new CountingStore(new ExecutionStore(db, TestUnitOfWork.For(db)));
            var service = Service(db, store);
            await Reconcile(db, service, seeded.Account);
            foreach (var id in new[] { First, Second, Eth }) await service.GetAsync(id, default);
            return (store.Orders, store.Links, store.FillOrderIds, store.FullReads);
        }
        var small = await Count(0);
        Assert.False(small.Full);
        Assert.Equal(small, await Count(200));
    }

    // Retrospective preview -------------------------------------------------------------------------------------------

    /// <summary>Implements only the whole-account reads, so the targeted preview reads fall back to the port's reference rule.</summary>
    private sealed class FullReadDiaryStore(DiaryStore inner) : IDiaryStore
    {
        public Task<T> WithRequestLockAsync<T>(Guid requestId, Func<Task<T>> action, CancellationToken ct) => inner.WithRequestLockAsync(requestId, action, ct);
        public Task<DiaryRequest?> RequestAsync(Guid requestId, CancellationToken ct) => inner.RequestAsync(requestId, ct);
        public Task<List<ManualExecution>> ManualAsync(Guid playId, CancellationToken ct) => inner.ManualAsync(playId, ct);
        public Task<List<ManualExecutionHistory>> ManualHistoryAsync(Guid playId, CancellationToken ct) => inner.ManualHistoryAsync(playId, ct);
        public Task<ActivityFacts> ActivityAsync(Guid accountId, CancellationToken ct) => inner.ActivityAsync(accountId, ct);
        public Task<List<ImportedOrder>> OrdersAsync(Guid accountId, CancellationToken ct) => inner.OrdersAsync(accountId, ct);
        public Task<List<PlayOrderLink>> LinksAsync(Guid accountId, CancellationToken ct) => inner.LinksAsync(accountId, ct);
        public void Add(ManualExecution execution) => inner.Add(execution);
        public void Add(ManualExecutionHistory history) => inner.Add(history);
        public void Add(DiaryRequest request) => inner.Add(request);
        public void Add(PlayOrderLink link) => inner.Add(link);
    }

    private static DiaryService Diary(VesselDbContext db, bool narrowed)
    {
        var store = new DiaryStore(db, TestUnitOfWork.For(db));
        return new(new PlayStore(db, TestUnitOfWork.For(db)), narrowed ? store : new FullReadDiaryStore(store),
            new ExecutionStore(db, TestUnitOfWork.For(db)), TestVenues.With(), TimeProvider.System, TestUnitOfWork.For(db));
    }

    [PostgresFact]
    public async Task Retrospective_previews_are_identical_with_operation_scoped_order_and_link_reads()
    {
        await using var seeded = await Seed(unrelated: 5);
        var account = seeded.Account;
        await using (var db = seeded.Database.Context(seeded.Owner))
        {
            // An eligible SOL round trip, an AVAX round trip with an order linked to a Play, and an ARB round trip whose exit
            // has no order record, beside the seeded linked ETH round trip and open BTC and DOGE positions.
            var avaxOut = Order(account, "avax-out", "AVAX", "sell", 110, 51, "filled");
            db.Orders.AddRange(Order(account, "sol-in", "SOL", "buy", 100, 50, "filled"), Order(account, "sol-out", "SOL", "sell", 110, 51, "filled"),
                Order(account, "avax-in", "AVAX", "buy", 100, 50, "filled"), avaxOut,
                Order(account, "arb-in", "ARB", "buy", 100, 50, "filled"));
            db.Fills.AddRange(Fill(account, "sol-in", "SOL", "buy", "open", 100, 50), Fill(account, "sol-out", "SOL", "sell", "close", 110, 51),
                Fill(account, "avax-in", "AVAX", "buy", "open", 100, 50), Fill(account, "avax-out", "AVAX", "sell", "close", 110, 51),
                Fill(account, "arb-in", "ARB", "buy", "open", 100, 50), Fill(account, "arb-out", "ARB", "sell", "close", 110, 51));
            var avax = Planned(db, account, Guid.NewGuid(), "AVAX", 49);
            db.OrderLinks.Add(Link(avax, avaxOut, OrderLinkRole.Exit, OrderLinkState.Linked, 52));
            await db.SaveChangesAsync();
        }
        List<ActivityOperationSummaryDto> operations;
        await using (var db = seeded.Database.Context(seeded.Owner))
            operations = (await ActivityTestSupport.Activity(db).PageAsync(account.Id)).Operations.ToList();
        Assert.NotEmpty(operations);
        var eligible = 0;
        foreach (var operation in operations)
        {
            RetrospectivePreviewDto expected, actual;
            await using (var db = seeded.Database.Context(seeded.Owner))
                expected = await Diary(db, narrowed: false).PreviewAsync(account.Id, operation.Id, default);
            await using (var db = seeded.Database.Context(seeded.Owner))
                actual = await Diary(db, narrowed: true).PreviewAsync(account.Id, operation.Id, default);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            if (actual.Eligible) eligible++;
        }
        Assert.True(eligible > 0);
    }
}
