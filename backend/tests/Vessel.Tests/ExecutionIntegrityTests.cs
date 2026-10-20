using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Execution facts of one account, kept in memory for owner linking and lifecycle checks.</summary>
internal sealed class MemoryExecutionStore(MemoryPlayStore plays) : IPlayExecutionStore
{
    public List<ImportedOrder> Orders { get; } = [];
    public List<ImportedFill> Fills { get; } = [];
    public List<PlayOrderLink> Links { get; } = [];
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => plays.AccountAsync(id, ct);
    public Task<List<Play>> ActivePlaysAsync(Guid accountId, CancellationToken ct, bool settling = false) =>
        Task.FromResult(plays.Plays.Where(p => p.AccountId == accountId && (ExecutionMatcher.IsActive(p) || settling && p.Status == PlayStatus.Unresolved)).ToList());
    public Task<List<ImportedOrder>> OrdersAsync(Guid accountId, CancellationToken ct) => Task.FromResult(Orders.Where(o => o.AccountId == accountId).ToList());
    public Task UpsertOrdersAsync(Account account, IReadOnlyList<VenueOrder> orders, DateTimeOffset observedAt, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<List<PlayOrderLink>> LinksAsync(Guid accountId, CancellationToken ct) => Task.FromResult(Links.Where(l => l.AccountId == accountId).ToList());
    public Task<List<ImportedFill>> FillsAsync(Guid accountId, IReadOnlyCollection<OrderKey> orders, CancellationToken ct) =>
        Task.FromResult(Fills.Where(f => f.AccountId == accountId && orders.Contains(OrderKey.Of(f))).OrderBy(f => f.OccurredAtUtc).ToList());
    /// <summary>The retained order with this venue ID, for building links in tests.</summary>
    public ImportedOrder Order(string externalOrderId) => Orders.Single(o => o.ExternalOrderId == externalOrderId);
    public void Add(PlayOrderLink link) => Links.Add(link);
    public void Add(PlayStatusChange change) => plays.Add(change);
    public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<T> WithAccountLockAsync<T>(Guid accountId, Func<Task<T>> action, CancellationToken ct) => action();
}

public sealed class ExecutionIntegrityTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-10T10:00:00Z");
    private readonly MemoryPlayStore plays;
    private readonly MemoryExecutionStore store;
    private readonly Account account;
    private readonly Play play;

    public ExecutionIntegrityTests()
    {
        var owner = Guid.NewGuid();
        plays = new MemoryPlayStore(owner);
        account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Trading");
        plays.Accounts.Add(account);
        play = Planned();
        store = new MemoryExecutionStore(plays);
    }

    private Play Planned(PlayPlanDocument? plan = null)
    {
        var planned = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan ?? TestPlays.Plan()), Created);
        plays.Add(planned);
        plays.Add(planned.MarkPlanned(Created).Revision);
        return planned;
    }

    private PlayExecutionService Service => new(plays, store, TestVenues.With(), TimeProvider.System, TestUnitOfWork.None);

    private void Add(string id, string side, decimal price, int minute, decimal size = 1, string status = "filled", bool reduceOnly = false,
        params ImportedFill[] fills)
    {
        store.Orders.Add(new()
        {
            Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ExternalOrderId = id, ContractId = "BTC", Side = side,
            OrderType = "Limit", LimitPrice = price, ReduceOnly = reduceOnly, OriginalSize = size, RemainingSize = status == "open" ? size : 0,
            PlacedAtUtc = Created.AddMinutes(minute), Status = status, VenueStatus = status, StatusAtUtc = Created.AddMinutes(minute),
            ObservedAtUtc = Created.AddMinutes(minute),
        });
        store.Fills.AddRange(fills);
    }

    private ImportedFill Fill(string orderId, string side, string effect, decimal price, decimal quantity, int minute, decimal pnl = 0) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = "BTC", SourceFillId = Guid.NewGuid().ToString("N"),
        ExternalOrderId = orderId, Side = side, Direction = "", PositionEffect = effect, Price = price, Quantity = quantity, Fee = 0, FeeToken = "USDC",
        ClosedPnlUsd = pnl, OccurredAtUtc = Created.AddMinutes(minute), TransactionHash = "",
    };

    private Task<PlayExecutionDto> Link(string orderId, string role, Play? target = null) => Service.LinkAsync((target ?? play).Id,
        role switch { "entry" => new(orderId, role, "entry-1"), "exit" => new(orderId, role), _ => new(orderId, role, "entry-1", role + "-1") }, default);

    [Fact]
    public async Task Owner_links_cannot_contradict_the_side_or_effect_of_the_fills()
    {
        Add("in", "buy", 100, 1, fills: Fill("in", "buy", "open", 100, 1, 2));
        Add("increase", "buy", 102, 3, fills: Fill("increase", "buy", "open", 102, 1, 4));
        Add("short", "sell", 105, 3, fills: Fill("short", "sell", "open", 105, 1, 4));
        Add("flip", "sell", 110, 3, size: 2, fills: Fill("flip", "sell", "flip", 110, 2, 4, pnl: 10));
        Add("cover", "buy", 100, 3, fills: Fill("cover", "buy", "close", 100, 1, 4));
        Add("reduce", "buy", 99, 3, status: "open", reduceOnly: true);
        Assert.Equal("open", (await Link("in", "entry")).Status);

        foreach (var (order, role) in new[] { ("increase", "exit"), ("short", "exit"), ("flip", "target"), ("cover", "entry"), ("reduce", "entry") })
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => Link(order, role))).StatusCode);
        var execution = await Service.GetAsync(play.Id, default);
        Assert.Equal(("open", "1", "0", "1"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity,
            execution.Totals.OpenQuantity));
        Assert.Equal([store.Order("in").Id], store.Links.Select(l => l.ImportedOrderId));

        // A reducing fill on the exit side is an exit.
        Add("out", "sell", 110, 5, fills: Fill("out", "sell", "close", 110, 1, 6, pnl: 10));
        Assert.Equal("closed", (await Link("out", "target")).Status);
    }

    [Fact]
    public async Task A_known_missing_execution_keeps_the_play_open_until_it_is_retained()
    {
        Add("in", "buy", 100, 1, size: 2, fills: Fill("in", "buy", "open", 100, 1, 2));
        Add("out", "sell", 110, 3, fills: Fill("out", "sell", "close", 110, 1, 4, pnl: 10));
        await Link("in", "entry");
        var execution = await Link("out", "target");
        Assert.Equal(("open", "1", "1"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity));
        Assert.Equal("Order in is filled at the venue, but not all of its executions are retained yet.", Assert.Single(execution.Issues));

        store.Fills.Add(Fill("in", "buy", "open", 100, 1, 2));
        execution = await Service.GetAsync(play.Id, default);
        Assert.Equal(("open", "2", "1", "1"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity,
            execution.Totals.OpenQuantity));
        Assert.Empty(execution.Issues);

        // A filled entry order with none of its executions retained holds the play open the same way.
        Add("again", "buy", 100, 5);
        await Link("again", "entry");
        Add("rest", "sell", 110, 6, fills: Fill("rest", "sell", "close", 110, 1, 7, pnl: 10));
        Assert.Equal("open", (await Link("rest", "exit")).Status);
        store.Fills.Add(Fill("again", "buy", "open", 100, 1, 5));
        Add("last", "sell", 110, 8, fills: Fill("last", "sell", "close", 110, 1, 9, pnl: 10));
        Assert.Equal("closed", (await Link("last", "exit")).Status);
    }

    [Fact]
    public async Task An_exit_that_filled_before_the_entry_or_beyond_it_does_not_close_the_play()
    {
        Add("old-close", "sell", 110, 1, fills: Fill("old-close", "sell", "close", 110, 1, 2, pnl: 10));
        Add("in", "buy", 100, 3, fills: Fill("in", "buy", "open", 100, 1, 4));
        await Link("old-close", "target");
        var early = await Link("in", "entry");
        Assert.Equal("open", early.Status);
        Assert.Equal("A linked exit filled before the entry it would close.", Assert.Single(early.Issues));

        // One sell of 2 closes this play's 1 and another play's 1 on the same instrument; it cannot settle either alone.
        var other = Planned(TestPlays.Plan("90", "85"));
        Add("other-in", "buy", 90, 3, fills: Fill("other-in", "buy", "open", 90, 1, 4));
        await Link("other-in", "entry", other);
        var second = Planned();
        Add("second-in", "buy", 100, 5, fills: Fill("second-in", "buy", "open", 100, 1, 6));
        Add("both-out", "sell", 110, 7, size: 2, fills: Fill("both-out", "sell", "close", 110, 2, 8, pnl: 30));
        await Link("second-in", "entry", second);
        var execution = await Link("both-out", "target", second);
        Assert.Equal(("open", "1", "2", "0"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity,
            execution.Totals.OpenQuantity));
        Assert.Contains("exceed the entered quantity", Assert.Single(execution.Issues));
        Assert.Equal(PlayStatus.Open, other.Status);
    }

    [Fact]
    public async Task Fills_linked_before_they_contradicted_the_role_count_toward_nothing()
    {
        // A resting buy at the entry price links automatically; it later fills by closing an earlier short.
        Add("cover", "buy", 100, 1, fills: Fill("cover", "buy", "close", 100, 1, 2, pnl: 4));
        store.Links.Add(new PlayOrderLink(play, store.Order("cover"), OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Automatic, Created));
        Add("out", "sell", 110, 3, fills: Fill("out", "sell", "close", 110, 1, 4, pnl: 10));
        var execution = await Link("out", "target");
        Assert.Equal(("planned", "0", "1", "0"), (execution.Status, execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity,
            execution.Totals.OpenQuantity));
        Assert.Equal(["Order cover has buy close fills, so it cannot be an entry of this long play.", "Linked exits exceed the entered quantity; a fill cannot be split between plays."],
            execution.Issues);
        Assert.Equal("0", Assert.Single(execution.Entries).FilledQuantity);
    }

    [Fact]
    public async Task Closed_plays_keep_their_links_but_stale_suggestions_can_be_declined()
    {
        Add("in", "buy", 100, 1, fills: Fill("in", "buy", "open", 100, 1, 2));
        Add("out", "sell", 110, 3, fills: Fill("out", "sell", "close", 110, 1, 4, pnl: 10));
        await Link("in", "entry");
        Assert.Equal("closed", (await Link("out", "target")).Status);
        foreach (var link in store.Links.ToList())
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => Service.UnlinkAsync(play.Id, link.Id, default))).StatusCode);
        var execution = await Service.GetAsync(play.Id, default);
        Assert.Equal(("closed", 2, "0"), (execution.Status, execution.Links.Count, execution.Totals.OpenQuantity));
        Assert.All(store.Links, l => Assert.Equal(OrderLinkState.Linked, l.State));

        Add("late", "sell", 120, 5, status: "open");
        var suggestion = new PlayOrderLink(play, store.Order("late"), OrderLinkRole.Exit, null, null, OrderLinkState.Suggested, OrderLinkSource.Automatic, Created);
        store.Links.Add(suggestion);
        await Service.UnlinkAsync(play.Id, suggestion.Id, default);
        Assert.Equal(OrderLinkState.Dismissed, suggestion.State);
    }
}
