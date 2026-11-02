using Vessel.Application.Activity;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>October 27, 2026: Activity rows show an open operation's unrealized P&amp;L, read per page.</summary>
public sealed class ActivityUnrealizedTests
{
    private static readonly Guid Owner = Guid.NewGuid(), AccountId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(1);

    private static ImportedFill Fill(int minute, string side, string effect, decimal quantity, decimal price, string contract = "BTC") => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, ContractId = contract, Side = side,
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
