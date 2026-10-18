using Vessel.Application.Activity;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>October 18, 2026: observed venue leverage attributed to Activity operations, never inferred.</summary>
public sealed class ActivityLeverageTests
{
    private static readonly Guid Owner = Guid.NewGuid(), AccountId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(10);

    private static ImportedFill Fill(int minute, string side = "buy", string effect = "open", decimal quantity = 1m, string? native = null,
        int? leverage = null, string contract = "BTC") => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, ContractId = contract, VenueContractId = native, Side = side,
        Direction = "fixture", PositionEffect = effect, Quantity = quantity, Price = 100m, ClosedPnlUsd = 0, Fee = 0, FeeToken = "USDC",
        PnlBasis = "gross", FeeBasis = "reported", OccurredAtUtc = Start.AddMinutes(minute), ExternalOrderId = minute.ToString(),
        SourceFillId = Guid.NewGuid().ToString(), TransactionHash = "", Leverage = leverage
    };

    private static LeverageObservation Observed(int minute, decimal signedQuantity, int leverage, string? native = null, string contract = "BTC") => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, ContractId = contract, VenueContractId = native,
        ObservedAtUtc = Start.AddMinutes(minute), SignedQuantity = signedQuantity, EntryPrice = 100m, Leverage = leverage
    };

    private static AccountSnapshot Snapshot(int minute, params AccountPosition[] positions) => new()
    {
        OwnerId = Owner, AccountId = AccountId, ObservedAtUtc = Start.AddMinutes(minute), ValueScope = "fixture", Positions = positions.ToList()
    };

    private static AccountPosition Position(decimal signedQuantity, int? leverage, string? native = null, string contract = "BTC") => new()
    {
        OwnerId = Owner, AccountId = AccountId, ContractId = contract, VenueContractId = native, SignedQuantity = signedQuantity, EntryPrice = 100m,
        Leverage = leverage
    };

    private static async Task<IReadOnlyList<ActivityOperationSummaryDto>> Operations(IReadOnlyList<ImportedFill> fills,
        IReadOnlyList<LeverageObservation>? observed = null, params AccountSnapshot[] snapshots) =>
        (await Page(fills, observed, snapshots)).Operations;

    private static Task<ActivityPageDto> Page(IReadOnlyList<ImportedFill> fills, IReadOnlyList<LeverageObservation>? observed = null,
        params AccountSnapshot[] snapshots)
    {
        var account = new Account(AccountId, Owner, "manual", "Fixture");
        var facts = new ActivityFacts([account], fills, snapshots, new Dictionary<Guid, Guid>()) { LeverageObservations = observed ?? [] };
        var workspace = new MemoryWorkspaceStore(Owner);
        workspace.Accounts.Add(account);
        return new ActivityService(new Store(facts), workspace, new Clock(Now)).PageAsync(new(PageSize: "100"), default);
    }

    [Fact]
    public async Task Closed_operation_keeps_leverage_observed_inside_its_lifetime_with_its_direction()
    {
        var ops = await Operations([Fill(0), Fill(10, "sell", "close"), Fill(20), Fill(30, "sell", "close")],
            [Observed(5, 1, 5), Observed(25, 1, 20), Observed(26, -1, 50), Observed(40, 1, 7)]);
        Assert.Equal(2, ops.Count);
        var first = ops.Single(o => o.OpenedAtUtc == Start);
        Assert.Equal((5, 5, 5), (first.Leverage, first.LeverageMin, first.LeverageMax));
        // The short observation never belongs to a long; the one after the close belongs to neither.
        var second = ops.Single(o => o.OpenedAtUtc == Start.AddMinutes(20));
        Assert.Equal((20, 20, 20), (second.Leverage, second.LeverageMin, second.LeverageMax));
    }

    [Fact]
    public async Task Changed_leverage_is_shown_as_a_range_with_the_latest_never_an_average()
    {
        var op = Assert.Single(await Operations([Fill(0), Fill(60, "sell", "close")], [Observed(10, 1, 10), Observed(20, 1, 3), Observed(30, 1, 5)]));
        Assert.Equal((5, 3, 10), (op.Leverage, op.LeverageMin, op.LeverageMax));
    }

    [Fact]
    public async Task Unobserved_operations_stay_unknown()
    {
        var op = Assert.Single(await Operations([Fill(0), Fill(10, "sell", "close")], [Observed(11, 1, 5), Observed(-1, 1, 4)]));
        Assert.Null(op.Leverage); Assert.Null(op.LeverageMin); Assert.Null(op.LeverageMax);
    }

    [Fact]
    public async Task Open_operation_uses_the_latest_snapshot_position_of_its_direction()
    {
        var ops = await Operations([Fill(0, quantity: 2)], [Observed(1, 2, 3)], Snapshot(30, Position(2, 4)));
        var op = Assert.Single(ops);
        Assert.Equal("open", op.Status);
        Assert.Equal((4, 3, 4), (op.Leverage, op.LeverageMin, op.LeverageMax));

        // A snapshot without leverage adds nothing, and an observation before the opening is ignored.
        op = Assert.Single(await Operations([Fill(10, quantity: 2)], [Observed(1, 2, 3)], Snapshot(30, Position(2, null))));
        Assert.Null(op.Leverage);
    }

    [Fact]
    public async Task Native_contract_partitions_never_share_observations()
    {
        var ops = await Operations([Fill(0, native: "13"), Fill(10, "sell", "close", native: "13"), Fill(0, native: "14"), Fill(10, "sell", "close", native: "14")],
            [Observed(5, 1, 8, "13")]);
        Assert.Equal(2, ops.Count);
        Assert.Equal(8, Assert.Single(ops, o => o.Leverage is not null).Leverage);
        Assert.Single(ops, o => o.Leverage is null);
        // Different canonical contracts or accounts never match either.
        var other = await Operations([Fill(0), Fill(10, "sell", "close")], [Observed(5, 1, 8, contract: "ETH")]);
        Assert.Null(Assert.Single(other).Leverage);
    }

    [Fact]
    public async Task Ambiguous_native_identity_leaves_leverage_unknown()
    {
        // Two native markets share the canonical key; ID-less facts form the unresolved partition.
        var ops = await Operations([Fill(0, native: "13"), Fill(1, "sell", "close", native: "13"), Fill(2, native: "14"), Fill(3, "sell", "close", native: "14"),
            Fill(5), Fill(8, "sell", "close")], [Observed(6, 1, 9)]);
        var unresolved = ops.Where(o => o.Coverage.Any(c => c.Contains("ambiguous native contract identity"))).ToList();
        Assert.NotEmpty(unresolved);
        Assert.All(unresolved, o => Assert.Null(o.Leverage));
    }

    [Fact]
    public async Task Reported_fill_leverage_counts_but_a_split_flip_does_not()
    {
        var ops = await Operations([Fill(0, leverage: 10), Fill(5, leverage: 12, quantity: 1), Fill(10, "sell", "flip", quantity: 3, leverage: 50),
            Fill(20, "buy", "close", leverage: 7)]);
        Assert.Equal(2, ops.Count);
        var longOp = ops.Single(o => o.Direction == "long");
        Assert.Equal((12, 10, 12), (longOp.Leverage, longOp.LeverageMin, longOp.LeverageMax));
        var shortOp = ops.Single(o => o.Direction == "short");
        Assert.Equal((7, 7, 7), (shortOp.Leverage, shortOp.LeverageMin, shortOp.LeverageMax));
    }

    [Fact]
    public async Task Missing_entries_use_the_first_retained_execution_as_the_lower_bound()
    {
        // The snapshot proves a long existed before the first retained fill: the operation is incomplete but directed.
        var ops = await Operations([Fill(10, "sell", "close"), Fill(20), Fill(30, "sell", "close")],
            [Observed(5, 1, 4), Observed(12, 1, 6)], Snapshot(40));
        var missing = ops.Single(o => o.Status == "incomplete");
        Assert.Equal("long", missing.Direction);
        // Minute 5 precedes the first retained fill and is not attributed; it could belong to an earlier operation.
        Assert.Null(missing.Leverage);
        var later = ops.Single(o => o.Status == "closed");
        Assert.Null(later.Leverage); // Minute 12 is between operations.
    }

    [Fact]
    public async Task Conflicting_values_at_the_latest_time_leave_the_latest_unknown_and_keep_the_range()
    {
        var op = Assert.Single(await Operations([Fill(0), Fill(60, "sell", "close")], [Observed(10, 1, 4), Observed(30, 1, 10), Observed(30, 1, 5)]));
        Assert.Equal(((int?)null, 4, 10), (op.Leverage, op.LeverageMin, op.LeverageMax));
        // Agreeing values at the latest time are no conflict, and an earlier tie never hides a later value.
        op = Assert.Single(await Operations([Fill(0), Fill(60, "sell", "close")], [Observed(10, 1, 4), Observed(10, 1, 9), Observed(30, 1, 5), Observed(30, 1, 5)]));
        Assert.Equal((5, 4, 9), (op.Leverage, op.LeverageMin, op.LeverageMax));
        // A fill's reported leverage at the same time as a differing observation is a conflict too.
        op = Assert.Single(await Operations([Fill(0), Fill(60, "sell", "close", leverage: 7)], [Observed(60, 1, 3)]));
        Assert.Equal(((int?)null, 3, 7), (op.Leverage, op.LeverageMin, op.LeverageMax));
    }

    [Fact]
    public async Task A_uniquely_identified_observation_reaches_operations_of_id_less_legacy_fills()
    {
        // Legacy fills carry no native ID and the latest snapshot is empty, so the operation uses the canonical partition.
        var op = Assert.Single(await Operations([Fill(0), Fill(60, "sell", "close")], [Observed(10, 1, 6, "7"), Observed(20, 1, 6, "7")], Snapshot(90)));
        Assert.Equal((6, 6, 6), (op.Leverage, op.LeverageMin, op.LeverageMax));
        Assert.Equal("closed", op.Status);
        // Two observed native markets for the canonical contract cannot be told apart: nothing is placed.
        op = Assert.Single(await Operations([Fill(0), Fill(60, "sell", "close")], [Observed(10, 1, 6, "7"), Observed(20, 1, 8, "8")], Snapshot(90)));
        Assert.Null(op.Leverage); Assert.Null(op.LeverageMin);
        // Observations never change how fills group: native fills keep their own partition and the observation follows the resolver.
        var ops = await Operations([Fill(0, native: "7"), Fill(10, "sell", "close", native: "7"), Fill(20), Fill(30, "sell", "close")],
            [Observed(5, 1, 6, "7"), Observed(25, 1, 9, "7")], Snapshot(90));
        Assert.Equal(2, ops.Count);
        Assert.Equal([6, 9], ops.OrderBy(o => o.OpenedAtUtc).Select(o => o.Leverage));
    }

    [Fact]
    public async Task Id_less_observations_follow_one_native_market_and_stay_unknown_beside_several()
    {
        // ID-less legacy fills, empty snapshot: one observed native market maps consistently with ID-less observations.
        var op = Assert.Single(await Operations([Fill(0), Fill(60, "sell", "close")], [Observed(10, 1, 6, "7"), Observed(20, 1, 9)], Snapshot(90)));
        Assert.Equal((9, 6, 9), (op.Leverage, op.LeverageMin, op.LeverageMax));
        // Several observed native markets: an ID-less observation could belong to either, so nothing is placed.
        op = Assert.Single(await Operations([Fill(0), Fill(60, "sell", "close")],
            [Observed(10, 1, 6, "7"), Observed(20, 1, 8, "8"), Observed(30, 1, 9)], Snapshot(90)));
        Assert.Equal(((int?)null, (int?)null, (int?)null), (op.Leverage, op.LeverageMin, op.LeverageMax));
        Assert.Equal("closed", op.Status); // Fill grouping is unchanged.
        // Native fills of market 7 with another observed market: ID-less observations stay out, native ones still count.
        op = Assert.Single(await Operations([Fill(0, native: "7"), Fill(60, "sell", "close", native: "7")],
            [Observed(10, 1, 5, "7"), Observed(20, 1, 8, "8"), Observed(30, 1, 9)], Snapshot(90)));
        Assert.Equal((5, 5, 5), (op.Leverage, op.LeverageMin, op.LeverageMax));
        // An ID-less latest-snapshot position is an ID-less source too.
        op = Assert.Single(await Operations([Fill(0)], [Observed(10, 1, 6, "7"), Observed(20, 1, 8, "8")], Snapshot(30, Position(1, 9))));
        Assert.Equal(("open", (int?)null, (int?)null), (op.Status, op.Leverage, op.LeverageMin));
        op = Assert.Single(await Operations([Fill(0)], [Observed(10, 1, 6, "7")], Snapshot(30, Position(1, 9))));
        Assert.Equal((9, 6, 9), (op.Leverage, op.LeverageMin, op.LeverageMax));
    }

    [Fact]
    public async Task Many_operations_each_read_only_their_own_interval()
    {
        var fills = new List<ImportedFill>();
        var observed = new List<LeverageObservation>();
        for (var i = 0; i < 2_000; i++)
        {
            fills.Add(Fill(i * 10)); fills.Add(Fill(i * 10 + 5, "sell", "close"));
            observed.Add(Observed(i * 10 + 2, 1, i % 50 + 1));
            observed.Add(Observed(i * 10 + 7, 1, 99)); // between operations
        }
        var account = new Account(AccountId, Owner, "manual", "Fixture");
        var workspace = new MemoryWorkspaceStore(Owner);
        workspace.Accounts.Add(account);
        var service = new ActivityService(new Store(new ActivityFacts([account], fills, [], new Dictionary<Guid, Guid>()) { LeverageObservations = observed }),
            workspace, new Clock(Now));
        var ops = new List<ActivityOperationSummaryDto>();
        for (var page = 1; page <= 20; page++)
            ops.AddRange((await service.PageAsync(new(Page: page.ToString(), PageSize: "100"), default)).Operations);
        ops = ops.OrderBy(o => o.OpenedAtUtc).ToList();
        Assert.Equal(2_000, ops.Count);
        Assert.All(ops.Select((o, i) => (o, i)), x => Assert.Equal((x.i % 50 + 1, x.i % 50 + 1), (x.o.Leverage!.Value, x.o.LeverageMax!.Value)));
    }

    [Fact]
    public async Task Version_changes_when_observed_leverage_changes()
    {
        var fills = new[] { Fill(0) };
        var before = await Page(fills, [], Snapshot(30, Position(1, 3)));
        var after = await Page(fills, [], Snapshot(30, Position(1, 5)));
        Assert.NotEqual(before.Version, after.Version);
        Assert.Contains("never inferred", before.ScopeNote);
    }

    private sealed class Store(ActivityFacts facts) : IActivityStore
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) => Task.FromResult(facts);
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
