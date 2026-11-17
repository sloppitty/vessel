using Vessel.Application.Activity;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Paper;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>October 27, 2026: Activity rows show an open operation's unrealized P&amp;L, read per page.</summary>
public sealed class ActivityUnrealizedTests
{
    private static readonly Guid Owner = Guid.NewGuid(), AccountId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(1);

    private static ImportedFill Fill(int minute, string side, string effect, decimal quantity, decimal price, string contract = "BTC",
        string? native = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, ContractId = contract, VenueContractId = native, Side = side,
        Direction = "fixture", PositionEffect = effect, Quantity = quantity, Price = price, ClosedPnlUsd = 0, Fee = 0, FeeToken = "USDC",
        PnlBasis = "gross", FeeBasis = "reported", OccurredAtUtc = Start.AddMinutes(minute), ExternalOrderId = minute.ToString(),
        SourceFillId = Guid.NewGuid().ToString(), TransactionHash = ""
    };

    private static AccountSnapshot Snapshot(int minute, decimal signedQuantity, decimal unrealized) => new()
    {
        OwnerId = Owner, AccountId = AccountId, ObservedAtUtc = Start.AddMinutes(minute), ValueScope = "fixture",
        Positions = [new() { OwnerId = Owner, AccountId = AccountId, ContractId = "BTC", SignedQuantity = signedQuantity, EntryPrice = 100m,
            UnrealizedPnlUsd = unrealized }]
    };

    private static async Task<IReadOnlyList<ActivityOperationSummaryDto>> Page(string venueId, IReadOnlyList<ImportedFill> fills,
        IMarketContextReader? reader = null, params AccountSnapshot[] snapshots)
    {
        var account = new Account(AccountId, Owner, venueId, "Fixture");
        var facts = new ActivityFacts([account], fills, snapshots, new Dictionary<Guid, Guid>());
        var workspace = new MemoryWorkspaceStore(Owner);
        workspace.Accounts.Add(account);
        var clock = new Clock(Now);
        return (await new ActivityService(new Store(facts), workspace, clock, new Registry(reader), marks: new MarketContextCache(clock))
            .PageAsync(new(PageSize: "100"), default)).Operations;
    }

    [Fact]
    public async Task Open_long_and_short_remainders_are_marked_at_the_current_mark_and_closed_ones_stay_null()
    {
        var reader = new Reader("hyperliquid", new VenueMarketContext("BTC", "110", "109", null, null, "0", "0", "0", null),
            new VenueMarketContext("ETH", "1900", "1900", null, null, "0", "0", "0", null));
        var ops = await Page("hyperliquid", [
            Fill(0, "buy", "open", 2m, 100m), Fill(1, "buy", "open", 2m, 104m), Fill(2, "sell", "close", 1m, 108m),
            Fill(3, "sell", "open", 0.5m, 2000m, "ETH"),
            Fill(4, "buy", "open", 1m, 50m, "SOL"), Fill(5, "sell", "close", 1m, 60m, "SOL")], reader);
        var btc = ops.Single(o => o.ContractId == "BTC");
        // Remaining 3 at the average entry 102: 3 × (110 − 102).
        Assert.Equal("24", btc.UnrealizedPnlUsd);
        Assert.Equal(("mark", "110", Now), (btc.UnrealizedPnlSource, btc.UnrealizedPnlMarkPrice, btc.UnrealizedPnlAsOfUtc));
        var eth = ops.Single(o => o.ContractId == "ETH");
        Assert.Equal("50", eth.UnrealizedPnlUsd);
        var sol = ops.Single(o => o.ContractId == "SOL");
        Assert.Equal("closed", sol.Status);
        Assert.Null(sol.UnrealizedPnlUsd);
        Assert.Null(sol.UnrealizedPnlSource);
    }

    [Fact]
    public async Task A_failed_mark_read_or_missing_market_leaves_the_value_unknown_without_failing_the_page()
    {
        var failed = await Page("hyperliquid", [Fill(0, "buy", "open", 1m, 100m)], new Reader("hyperliquid") { Fail = true });
        Assert.Null(Assert.Single(failed).UnrealizedPnlUsd);
        var missing = await Page("hyperliquid", [Fill(0, "buy", "open", 1m, 100m)],
            new Reader("hyperliquid", new VenueMarketContext("ETH", "1900", "1900", null, null, "0", "0", "0", null)));
        Assert.Null(Assert.Single(missing).UnrealizedPnlUsd);
    }

    [Fact]
    public async Task Venues_without_marks_use_the_latest_snapshot_only_when_it_is_newer_than_the_remaining_position()
    {
        var fills = new[] { Fill(0, "sell", "open", 2m, 100m) };
        var matched = Assert.Single(await Page("lighter", fills, null, Snapshot(10, -2m, -7.25m)));
        Assert.Equal(("-7.25", "snapshot", (string?)null), (matched.UnrealizedPnlUsd, matched.UnrealizedPnlSource, matched.UnrealizedPnlMarkPrice));
        Assert.Equal(Start.AddMinutes(10), matched.UnrealizedPnlAsOfUtc);
        // A snapshot taken before the latest execution does not describe the current remainder.
        Assert.Null(Assert.Single(await Page("lighter", [Fill(20, "sell", "open", 2m, 100m)], null, Snapshot(10, -2m, -7.25m))).UnrealizedPnlUsd);
    }

    private static VenueMarketContext Mark(string contract, string mark, string? native = null) =>
        new(contract, mark, mark, null, null, "0", "0", "0", null, native);

    /// <summary>Issue #90: the remaining quantity is valued at its average cost, not at the average of every entry.</summary>
    [Fact]
    public async Task An_exit_then_an_add_marks_the_remainder_at_its_average_cost_long_and_short()
    {
        var reader = new Reader("hyperliquid", Mark("AVAX", "130"), Mark("SOL", "70"));
        var ops = await Page("hyperliquid", [
            Fill(0, "buy", "open", 10m, 100m, "AVAX"), Fill(1, "sell", "close", 5m, 110m, "AVAX"), Fill(2, "buy", "open", 5m, 120m, "AVAX"),
            Fill(0, "sell", "open", 10m, 100m, "SOL"), Fill(1, "buy", "close", 5m, 90m, "SOL"), Fill(2, "sell", "open", 5m, 80m, "SOL")], reader);
        var avax = ops.Single(o => o.ContractId == "AVAX");
        // Entry display stays the average of every entry, 1600 / 15; the remainder costs (5 × 100 + 5 × 120) / 10 = 110.
        Assert.Equal(("106.6666666666666666666666666667", "10"), (avax.AverageEntryPrice, avax.OpenQuantity));
        Assert.Equal(("200", "mark"), (avax.UnrealizedPnlUsd, avax.UnrealizedPnlSource));
        // The short mirror: the remainder costs (5 × 100 + 5 × 80) / 10 = 90, so −10 × (70 − 90).
        Assert.Equal(("200", "mark"), (ops.Single(o => o.ContractId == "SOL").UnrealizedPnlUsd, ops.Single(o => o.ContractId == "SOL").UnrealizedPnlSource));
        // The venue's snapshot figure for the same position, on a venue without marks, is the same 200.
        var snapshot = new AccountSnapshot
        {
            OwnerId = Owner, AccountId = AccountId, ObservedAtUtc = Start.AddMinutes(10), ValueScope = "fixture",
            Positions = [new() { OwnerId = Owner, AccountId = AccountId, ContractId = "AVAX", SignedQuantity = 10m, EntryPrice = 110m, UnrealizedPnlUsd = 200m }]
        };
        var reported = Assert.Single(await Page("lighter", [Fill(0, "buy", "open", 10m, 100m, "AVAX"), Fill(1, "sell", "close", 5m, 110m, "AVAX"),
            Fill(2, "buy", "open", 5m, 120m, "AVAX")], null, snapshot));
        Assert.Equal((avax.UnrealizedPnlUsd, "snapshot"), (reported.UnrealizedPnlUsd, reported.UnrealizedPnlSource));
    }

    [Fact]
    public async Task A_non_terminating_basis_rounds_once_at_the_end()
    {
        // Remaining 2 at 302 / 3: 2 × (101 − 100.666…) = 0.666…, rounded once. Marking from the rounded average would end in …6.
        var ops = await Page("hyperliquid", [Fill(0, "buy", "open", 1m, 100m), Fill(1, "buy", "open", 2m, 101m), Fill(2, "sell", "close", 1m, 102m)],
            new Reader("hyperliquid", Mark("BTC", "101")));
        Assert.Equal("0.6666666666666666666666666667", Assert.Single(ops).UnrealizedPnlUsd);
    }

    [Fact]
    public async Task An_entry_and_an_exit_at_one_timestamp_leave_the_basis_unknown()
    {
        var ops = await Page("hyperliquid", [Fill(0, "buy", "open", 10m, 100m), Fill(1, "sell", "close", 5m, 110m), Fill(1, "buy", "open", 5m, 120m)],
            new Reader("hyperliquid", Mark("BTC", "130")));
        var op = Assert.Single(ops);
        Assert.Equal(("open", "10"), (op.Status, op.OpenQuantity));
        Assert.Null(op.UnrealizedPnlUsd);
        Assert.Null(op.UnrealizedPnlSource);
    }

    [Fact]
    public async Task A_retired_market_never_takes_its_successors_mark_and_an_id_less_partition_takes_no_native_mark()
    {
        // RISEx retired DOGE market 13 and lists market 14 under the same canonical key; only 14 has a mark.
        var reader = new Reader("risex", Mark("DOGE", "0.2", "14"));
        var ops = await Page("risex", [Fill(0, "buy", "open", 100m, 0.1m, "DOGE", "13"), Fill(1, "buy", "open", 100m, 0.15m, "DOGE", "14")], reader);
        Assert.Equal(2, ops.Count);
        Assert.Null(ops.Single(o => o.AverageEntryPrice == "0.1").UnrealizedPnlUsd);
        Assert.Equal("5", ops.Single(o => o.AverageEntryPrice == "0.15").UnrealizedPnlUsd);
        // A legacy fill without a market ID, and nothing naming its market: it cannot prove it is market 14.
        Assert.Null(Assert.Single(await Page("risex", [Fill(0, "buy", "open", 100m, 0.1m, "DOGE")], reader)).UnrealizedPnlUsd);
        // A venue that names no native market for the key still marks an ID-less partition.
        Assert.Equal("10", Assert.Single(await Page("risex", [Fill(0, "buy", "open", 100m, 0.1m, "DOGE")],
            new Reader("risex", Mark("DOGE", "0.2")))).UnrealizedPnlUsd);
    }

    [Fact]
    public async Task Paper_marks_its_remainder_at_the_average_cost()
    {
        var paper = PaperReportingTestsValues.Paper(Owner);
        var facts = paper.Facts.Paper!;
        var template = facts.Executions[0];
        PaperExecution Execution(string role, decimal quantity, decimal price, int minute) => new()
        {
            Id = Guid.NewGuid(), OwnerId = Owner, AccountId = paper.Account.Id, PlayId = facts.Play.Id, EntryId = "e",
            LevelId = role == "entry" ? null : "target", Role = role, Price = price, Quantity = quantity, PnlUsd = 0, MarginUsd = 0,
            PlanVersion = template.PlanVersion, OccurredAtUtc = Start.AddMinutes(minute), CandleOpenAtUtc = Start.AddMinutes(minute - 1),
            CandleCloseAtUtc = Start.AddMinutes(minute)
        };
        var activity = new ActivityFacts([paper.Account], [], [], new Dictionary<Guid, Guid>())
        {
            PaperPlays = [facts with { Executions = [Execution("entry", 10m, 100m, 0), Execution("target", 5m, 110m, 1), Execution("entry", 5m, 120m, 2)] }]
        };
        var workspace = new MemoryWorkspaceStore(Owner);
        workspace.Accounts.Add(paper.Account);
        workspace.Portfolios.Add(paper.Portfolio);
        var clock = new Clock(Now);
        var row = Assert.Single((await new ActivityService(new Store(activity), workspace, clock,
                new Registry(new Reader("hyperliquid", Mark("BTC", "130"))), marks: new MarketContextCache(clock))
            .PageAsync(new(PageSize: "100", TradingMode: "paper"), default)).Operations);
        Assert.Equal(("open", "200"), (row.Status, row.UnrealizedPnlUsd));
    }

    private sealed class Reader(string venueId, params VenueMarketContext[] contexts) : IMarketContextReader
    {
        public bool Fail { get; init; }
        public string VenueId => venueId;
        public Task<IReadOnlyList<VenueMarketContext>> ReadMarketContextsAsync(CancellationToken cancellationToken) =>
            Fail ? throw new VenueReadException("down") : Task.FromResult<IReadOnlyList<VenueMarketContext>>(contexts);
    }

    private sealed class Registry(IMarketContextReader? reader) : IVenueRegistry
    {
        public IReadOnlyList<VenueDescriptor> Descriptors => [];
        public VenueDescriptor? Descriptor(string venueId) => null;
        public IPerpetualVenueReader? Reader(string venueId) => null;
        public IVenueOrderReader? Orders(string venueId) => null;
        public ICandleReader? Candles(string venueId) => null;
        public IMarketContextReader? MarketContext(string venueId) => reader?.VenueId == venueId ? reader : null;
        public IMarketStream? Stream(string venueId) => null;
        public IVenueAccountDiscovery? Discovery(string venueId) => null;
        public IVenueCredentialVerifier? CredentialVerifier(string venueId) => null;
    }

    private sealed class Store(ActivityFacts facts) : IActivityStore
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) => Task.FromResult(facts);
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
