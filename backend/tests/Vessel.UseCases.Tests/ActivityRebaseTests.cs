using System.Collections;
using System.Text.Json;
using Vessel.Application.Activity;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Issue #107: a cached Activity aggregate is rebased onto newer observations (snapshots, funding coverage, sync state)
/// instead of being walked again when they cannot change it, narrowed to one account from a wider aggregate, and read in its
/// stored page order.
/// </summary>
public sealed class ActivityRebaseTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Account First = new(Guid.NewGuid(), Owner, "manual", "First"), Second = new(Guid.NewGuid(), Owner, "manual", "Second");
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static ImportedFill Fill(Account account, int minute, string side, string effect) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = account.Id, ContractId = "BTC", Side = side, Direction = "fixture",
        PositionEffect = effect, Quantity = 1m, Price = 100m + minute, ClosedPnlUsd = side == "sell" ? 1m : 0m, Fee = 0.01m,
        FeeToken = "USDC", OccurredAtUtc = Start.AddMinutes(minute), ExternalOrderId = minute.ToString(),
        SourceFillId = Guid.NewGuid().ToString(), TransactionHash = ""
    };

    private static AccountSnapshot Snapshot(Account account, int minute, decimal quantity = 1m, int? leverage = 5) => new()
    {
        OwnerId = Owner, AccountId = account.Id, ObservedAtUtc = Start.AddMinutes(minute), ValueScope = "fixture",
        Positions = [new() { OwnerId = Owner, AccountId = account.Id, ContractId = "BTC", SignedQuantity = quantity, EntryPrice = 100m, Leverage = leverage }]
    };

    private static AccountFundingState Funding(Account account, int coveredTo, int attemptTo) => new()
    {
        OwnerId = Owner, AccountId = account.Id, CoveredFromUtc = Start.AddMinutes(-60), CoveredToUtc = Start.AddMinutes(coveredTo),
        LastAttemptFromUtc = Start.AddMinutes(-60), LastAttemptToUtc = Start.AddMinutes(attemptTo), LastReadComplete = true, Notice = "fixture"
    };

    // A closed round trip, then a position opened at minute 20 and still open, which the snapshot confirms.
    private static ActivityFacts Facts(AccountSnapshot snapshot, AccountFundingState funding, long revision) =>
        new([First], [Fill(First, 0, "buy", "open"), Fill(First, 10, "sell", "close"), Fill(First, 20, "buy", "open")], [snapshot],
            new Dictionary<Guid, Guid>())
        {
            FundingStates = [funding], Revision = revision,
            FundingPayments = [new() { Id = Guid.NewGuid(), OwnerId = Owner, AccountId = First.Id, SourceId = "f1", ContractId = "BTC",
                Asset = "USDC", Amount = -0.01m, OccurredAtUtc = Start.AddMinutes(30) }],
            LeverageObservations = [new() { Id = Guid.NewGuid(), OwnerId = Owner, AccountId = First.Id, ContractId = "BTC",
                ObservedAtUtc = Start.AddMinutes(21), SignedQuantity = 1m, EntryPrice = 100m, Leverage = 5 }]
        };

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static (ActivitySummaries Cached, ActivityFacts Facts) Cached(int snapshotAt = 60, int coveredTo = 40, int attemptTo = 40)
    {
        var facts = Facts(Snapshot(First, snapshotAt), Funding(First, coveredTo, attemptTo), 1);
        return (ActivityService.Aggregate(facts), facts);
    }

    // The same retained history with the observations a later check wrote, and the aggregate a fresh read would compute.
    private static (ActivitySummaries Rebased, ActivitySummaries Fresh) Check(ActivitySummaries cached, ActivityFacts facts,
        AccountSnapshot snapshot, AccountFundingState funding)
    {
        var rebased = ActivityService.Rebase(cached, new([First], [snapshot], [funding]), 2);
        var fresh = ActivityService.Aggregate(facts with { Snapshots = [snapshot], FundingStates = [funding], Revision = 2 });
        Assert.Equal(Json(fresh), Json(rebased));
        Assert.Equal(fresh.Version, rebased.Version);
        Assert.Equal(Json(fresh.Ordered), Json(rebased.Ordered));
        Assert.Equal(2, rebased.Facts.Revision);
        Assert.Same(snapshot, Assert.Single(rebased.Facts.Snapshots));
        return (rebased, fresh);
    }

    [Fact]
    public void A_newer_observation_time_that_passes_no_retained_fact_reuses_the_walk()
    {
        var (cached, facts) = Cached();
        var (rebased, _) = Check(cached, facts, Snapshot(First, 120), Funding(First, 40, 40));
        Assert.Same(cached.Operations, rebased.Operations);
        Assert.Same(cached.Coverage, rebased.Coverage);
        Assert.Same(cached.Ordered, rebased.Ordered);
        Assert.Equal(cached.Version, rebased.Version);
        // The same revision is the cached aggregate itself.
        Assert.Same(cached, ActivityService.Rebase(cached, new([First], facts.Snapshots, facts.FundingStates), 1));
    }

    [Theory]
    [InlineData("passes-a-fill")]
    [InlineData("passes-funding-coverage")]
    [InlineData("position")]
    [InlineData("leverage")]
    [InlineData("funding-coverage")]
    [InlineData("history-notice")]
    public void A_changed_observation_walks_the_cached_history_again(string change)
    {
        var (cached, facts) = Cached(snapshotAt: change == "passes-a-fill" ? 15 : 60, coveredTo: change == "passes-funding-coverage" ? 70 : 40);
        var snapshot = change switch
        {
            "position" => Snapshot(First, 120, quantity: 2m),
            "leverage" => Snapshot(First, 120, leverage: 10),
            _ => Snapshot(First, 120)
        };
        var funding = Funding(First, change switch { "passes-funding-coverage" => 70, "funding-coverage" => 50, _ => 40 }, 40);
        if (change == "history-notice")
        {
            var account = new Account(First.Id, Owner, "manual", "First");
            account.RecordSync(Start, "Changed history coverage.");
            var rebased = ActivityService.Rebase(cached, new([account], [snapshot], [funding]), 2);
            Assert.NotSame(cached.Operations, rebased.Operations);
            Assert.Contains(rebased.Coverage, c => c.Notice == "Changed history coverage.");
            return;
        }
        var (result, _) = Check(cached, facts, snapshot, funding);
        Assert.NotSame(cached.Operations, result.Operations);
    }

    [Fact]
    public void A_newer_funding_attempt_alone_keeps_the_walk_and_rehashes_the_version()
    {
        var (cached, facts) = Cached();
        var (rebased, _) = Check(cached, facts, Snapshot(First, 120), Funding(First, 40, 45));
        Assert.Same(cached.Operations, rebased.Operations);
        Assert.NotEqual(cached.Version, rebased.Version);
    }

    private static ForcedEvent Forced(Account account, int minute) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = account.Id, SourceId = $"{account.Id}-{minute}", Kind = ForcedEvent.Liquidation,
        ContractId = "BTC", Side = "sell", Quantity = 1m, OccurredAtUtc = Start.AddMinutes(minute)
    };

    private static ActivityFacts Both(IReadOnlyList<ForcedEvent> forced) =>
        new([First, Second], [Fill(First, 0, "buy", "open"), Fill(First, 10, "sell", "close"), Fill(Second, 5, "sell", "open")],
            [Snapshot(First, 60, 0m), Snapshot(Second, 60, -1m)], new Dictionary<Guid, Guid>())
        { ForcedEvents = forced, Revision = 3 };

    private static ActivityFacts Only(ActivityFacts facts, Account account) =>
        new([account], facts.Fills.Where(f => f.AccountId == account.Id).ToList(), facts.Snapshots.Where(s => s.AccountId == account.Id).ToList(),
            new Dictionary<Guid, Guid>())
        { ForcedEvents = facts.ForcedEvents.Where(e => e.AccountId == account.Id).ToList(), Revision = facts.Revision };

    [Fact]
    public void One_account_is_narrowed_from_a_wider_aggregate_with_its_own_observations()
    {
        var facts = Both([Forced(First, 1), Forced(Second, 2)]);
        var wide = ActivityService.Aggregate(facts, accountIds: new HashSet<Guid> { First.Id, Second.Id });
        var direct = Only(facts, First);
        var narrowed = ActivityService.Narrow(wide, First.Id, new(direct.Accounts, direct.Snapshots, []), 3);
        Assert.NotNull(narrowed);
        var expected = ActivityService.Aggregate(direct, First.Id);
        var actual = ActivityService.Aggregate(narrowed, First.Id);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(Json(expected.Ordered), Json(actual.Ordered));
        Assert.Equal(Json(expected.Coverage), Json(actual.Coverage));
        Assert.Equal(Json(expected.Facts), Json(actual.Facts));
    }

    [Fact]
    public void An_account_whose_forced_events_the_wider_aggregate_left_out_is_read_again()
    {
        // The wider aggregate keeps the newest 50 forced events across its accounts: all of the second's, none of the first's.
        var facts = Both([.. Enumerable.Range(1, 3).Select(m => Forced(First, m)), .. Enumerable.Range(100, 50).Select(m => Forced(Second, m))]);
        var wide = ActivityService.Aggregate(facts, accountIds: new HashSet<Guid> { First.Id, Second.Id });
        Assert.Null(ActivityService.Narrow(wide, First.Id, new([First], [], []), 3));
        var second = Only(facts, Second);
        var narrowed = ActivityService.Narrow(wide, Second.Id, new([Second], second.Snapshots, []), 3);
        Assert.Equal(Json(ActivityService.Aggregate(second, Second.Id).Coverage), Json(ActivityService.Aggregate(narrowed!, Second.Id).Coverage));
    }

    private sealed class Unsortable(IReadOnlyList<ActivityOperationDto> inner) : IReadOnlyList<ActivityOperationDto>
    {
        public ActivityOperationDto this[int index] => throw new InvalidOperationException("A cached page must not re-read the operations.");
        public int Count => inner.Count;
        public IEnumerator<ActivityOperationDto> GetEnumerator() => throw new InvalidOperationException("A cached page must not re-sort the operations.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Reader(ActivitySummaries summaries) : IActivitySummaryReaderDefaults
    {
        public Task<ActivitySummaries> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) => Task.FromResult(summaries);
    }

    private sealed class Store(ActivityFacts facts) : IActivityStoreDefaults
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) => Task.FromResult(facts);
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [Fact]
    public async Task A_cached_page_reads_the_stored_order_and_facets_and_sorts_nothing()
    {
        var facts = Both([]);
        var workspace = new MemoryWorkspaceStore(Owner);
        workspace.Accounts.AddRange([First, Second]);
        var computed = ActivityService.Aggregate(facts);
        var cached = new ActivitySummaries(new Unsortable(computed.Operations), computed.Coverage, computed.Facts)
        {
            Version = computed.Version, Ordered = computed.Ordered
        };
        var clock = new Clock(Start.AddDays(1));
        foreach (var query in new ActivityQuery[] { new(PageSize: "25"), new(PageSize: "25", Direction: "long", Status: "closed") })
        {
            var expected = await new ActivityService(new Store(facts), workspace, clock).PageAsync(query, default);
            var page = await new ActivityService(new Store(facts), workspace, clock, summaries: new Reader(cached)).PageAsync(query, default);
            Assert.Equal(Json(expected), Json(page));
        }
    }
}
