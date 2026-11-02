using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 9b: settled funding beside a Play only with proven sole exposure over its holding window. The facts come
/// from the stores: retained fills, links, the coverage ledger, omission markers, forced events, the snapshot and funding.
/// Every unproven condition reads shared or incomplete, never a guessed sum, and no result changes.
/// </summary>
public sealed class PlayFundingPostgresTests
{
    // The baseline: a long entered at T and exited at T + 3 h, two settlements inside, one before and one after. The ledger
    // proves T − 2 h to T + 5 h continuous, the latest snapshot (T + 5 h) is flat and funding is covered from T − 1 d.
    private static readonly DateTimeOffset T = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Exit = T.AddHours(3), Read = T.AddHours(5);
    private const string Inside = "0.3765432109876543210987654322"; // 0.5 − 0.1234567890123456789012345678

    /// <summary>A venue that offers settled funding. The execution view must never call it.</summary>
    private sealed class FundingVenue : IPerpetualVenueReader, IVenueFundingReader
    {
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<PerpetualVenueReadResult> ReadAsync(string sourceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<VenueFundingReadResult> ReadFundingAsync(Account account, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            throw new NotSupportedException("The execution view reads retained facts only.");
    }

    private static PlayExecutionService Service(VesselDbContext db, bool funding = true)
    {
        var venues = funding ? TestVenues.With(new FundingVenue()) : TestVenues.With();
        return new(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)), venues, TimeProvider.System,
            TestUnitOfWork.For(db), activity: new ActivityService(new ActivityStore(db), new WorkspaceStore(db, TestUnitOfWork.For(db)),
                TimeProvider.System, venues));
    }

    private static async Task<PlayFundingDto?> FundingAsync(CoreDatabase database, Guid owner, Guid playId, bool funding = true)
    {
        await using var db = database.Context(owner);
        return (await Service(db, funding).GetAsync(playId, default)).Funding;
    }

    private static PlayFundingDto Not(string state, string reason) => new(state, reason, null, null, null, null, null);

    private static Account AddAccount(VesselDbContext db, Guid owner, string mode = "real")
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Funding", mode);
        if (mode == "real") account.Configure(null, "0x" + new string('b', 40), null);
        else
        {
            // A paper account lives in a paper portfolio.
            var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: mode);
            db.Portfolios.Add(portfolio);
            account.Configure(portfolio.Id, null, null);
        }
        db.Accounts.Add(account);
        return account;
    }

    private static Play AddPlay(VesselDbContext db, Account account, bool closed, DateTimeOffset? closedAt = null)
    {
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), T.AddDays(-1));
        var (change, revision) = play.MarkPlanned(T.AddDays(-1));
        db.Plays.Add(play); db.Add(change); db.Add(revision);
        db.Add(play.MarkOpen("Entry fill.", T));
        if (closed) db.Add(play.MarkClosed("Linked exits covered the entries.", closedAt ?? Exit));
        return play;
    }

    /// <summary>One filled order and its fill, linked to <paramref name="play"/> in <paramref name="role"/> when given.</summary>
    private static ImportedOrder Trade(VesselDbContext db, Account account, Play? play, string id, string side, decimal quantity, DateTimeOffset at,
        OrderLinkRole role = OrderLinkRole.Entry, decimal price = 100, decimal pnl = 0, string? native = null)
    {
        var order = new ImportedOrder
        {
            Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ExternalOrderId = "order-" + id, ContractId = "BTC",
            Side = side, OrderType = "Limit", LimitPrice = price, OriginalSize = quantity, RemainingSize = 0, PlacedAtUtc = at.AddMinutes(-1),
            Status = "filled", VenueStatus = "filled", StatusAtUtc = at, ObservedAtUtc = at,
        };
        db.Orders.Add(order);
        if (play is not null)
            db.OrderLinks.Add(new PlayOrderLink(play, order, role, role == OrderLinkRole.Exit ? null : "entry-1", null, OrderLinkState.Linked,
                OrderLinkSource.Automatic, at));
        db.Fills.Add(new ImportedFill
        {
            Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = "BTC", SourceFillId = "fill-" + id, Side = side,
            Direction = side == "buy" ? "Open Long" : "Close Long", PositionEffect = side == "buy" ? "open" : "close", Price = price,
            Quantity = quantity, Fee = 0.05m, FeeToken = "USDC", ClosedPnlUsd = pnl, OccurredAtUtc = at, ExternalOrderId = "order-" + id,
            TransactionHash = "0x" + id, VenueContractId = native,
        });
        return order;
    }

    /// <summary>A filled order on BTC whose execution is not retained: known missing executions by the shared rule.</summary>
    private static void MissingOrder(VesselDbContext db, Account account, string id, string side, decimal quantity, DateTimeOffset at) =>
        db.Orders.Add(new ImportedOrder
        {
            Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ExternalOrderId = "order-" + id, ContractId = "BTC",
            Side = side, OrderType = "Limit", LimitPrice = 100, OriginalSize = quantity, RemainingSize = 0, PlacedAtUtc = at.AddMinutes(-1),
            Status = "filled", VenueStatus = "filled", StatusAtUtc = at, ObservedAtUtc = at,
        });

    private static ForcedEvent Forced(Account account, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, SourceId = "liquidation-" + at.ToUnixTimeMilliseconds(),
        Kind = ForcedEvent.Liquidation, ContractId = "BTC", Side = "sell", Quantity = 0.1m, Price = 90, OccurredAtUtc = at,
        Exclusion = "Venue-forced.", FirstSeenAtUtc = Read, LastSeenAtUtc = Read,
    };

    private static void Pay(VesselDbContext db, Account account, string id, decimal amount, DateTimeOffset at, long? nanoseconds = null) =>
        db.FundingPayments.Add(new FundingPayment
        {
            Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, SourceId = id, ContractId = "BTC", Amount = amount,
            Asset = "USDC", OccurredAtUtc = at, OccurredAtUnixNanoseconds = nanoseconds,
        });

    private static AccountSnapshot Snapshot(Account account, DateTimeOffset at, decimal? quantity = null) => new()
    {
        OwnerId = account.OwnerId, AccountId = account.Id, ObservedAtUtc = at, ValueScope = "primary-perpetual-dex", AccountValueUsd = 1000,
        Positions = quantity is { } q ? [new AccountPosition { OwnerId = account.OwnerId, AccountId = account.Id, ContractId = "BTC",
            SignedQuantity = q, EntryPrice = 100, MarginUsedUsd = 10 }] : [],
    };

    private static FillCoverageRecord Coverage(Account account, DateTimeOffset? from, DateTimeOffset to) => new()
    {
        Id = Guid.CreateVersion7(), OwnerId = account.OwnerId, AccountId = account.Id, Source = FillCoverageRecord.Check,
        FirstObservedAtUtc = to, LastObservedAtUtc = to, RequestedToUtc = to, Truncated = false, Complete = from is null,
        CoveredFromUtc = from, CoveredToUtc = to,
    };

    private static AccountFundingState FundingState(Account account, DateTimeOffset from, DateTimeOffset to) => new()
    {
        OwnerId = account.OwnerId, AccountId = account.Id, CoveredFromUtc = from, CoveredToUtc = to, LastAttemptFromUtc = from,
        LastAttemptToUtc = to, LastReadComplete = true, PnlBasis = FundingFacts.Unknown, Notice = "Synthetic settled funding.",
    };

    /// <summary>The baseline closed Play with one condition changed by <paramref name="change"/>.</summary>
    private static async Task<(Guid Account, Guid Play)> SeedAsync(CoreDatabase database, Guid owner, string change = "none")
    {
        await using var db = database.Context(owner);
        var account = AddAccount(db, owner);
        var play = AddPlay(db, account, closed: true);
        var entry = Trade(db, account, play, "in", "buy", 1, T);
        Trade(db, account, play, "out", "sell", 1, Exit, OrderLinkRole.Exit, price: 110, pnl: 10);
        db.Snapshots.Add(Snapshot(account, Read));
        db.FillCoverage.Add(change switch
        {
            "no-continuity" => Coverage(account, T.AddHours(1), Read),
            "complete-history" or "forced-before-complete" or "missing-before-complete" => Coverage(account, null, Read),
            _ => Coverage(account, T.AddHours(-2), Read),
        });
        db.FundingStates.Add(FundingState(account, change == "partial-funding" ? T.AddHours(1) : T.AddDays(-1), Read));
        Pay(db, account, "before", -1, T.AddHours(-1));
        Pay(db, account, "inside-1", -0.1234567890123456789012345678m, T.AddHours(1));
        Pay(db, account, "inside-2", 0.5m, T.AddHours(2));
        Pay(db, account, "after", 2, T.AddHours(4));
        await db.SaveChangesAsync();
        switch (change)
        {
            case "open-gap":
                db.HistoryGaps.Add(new AccountHistoryGap { Id = Guid.CreateVersion7(), OwnerId = owner, AccountId = account.Id,
                    FromUtc = T.AddHours(1), ToUtc = T.AddHours(1).AddMinutes(1), OpenedAtUtc = Read });
                break;
            case "unlinked-fill":
                Trade(db, account, null, "other-in", "buy", 0.5m, T.AddMinutes(90));
                Trade(db, account, null, "other-out", "sell", 0.5m, T.AddMinutes(150));
                break;
            case "missing-order":
                entry.OriginalSize = 2; // filled at the venue, half of it retained
                break;
            case "marker-time":
                db.ExecutionOmissions.Add(Marker(account, T.AddHours(2).AddMinutes(30), null, null));
                break;
            case "marker-window":
                // No reported time: its read window starts before the holding window and ends inside it.
                db.ExecutionOmissions.Add(Marker(account, null, T.AddHours(-3), T.AddMinutes(30)));
                break;
            case "forced-event":
                db.ForcedEvents.Add(Forced(account, T.AddHours(2).AddMinutes(30)));
                break;
            case "missing-in-window":
                // An order linked to no Play filled inside the window, its execution not retained (Sol round 1 on 9b).
                MissingOrder(db, account, "hidden", "buy", 0.5m, T.AddMinutes(90));
                break;
            case "missing-after-exit":
                // Sol round 1 on 9b: another long was open before the Play; its close after the exit is known only from its
                // order. The flat snapshot, walked back over retained fills alone, would "prove" a flat start.
                Trade(db, account, null, "earlier-position", "buy", 1, T.AddHours(-1));
                MissingOrder(db, account, "missing-later-close", "sell", 1, T.AddHours(4));
                break;
            case "forced-before-window":
                // Before the window, it matters to neither the window nor the snapshot anchor.
                db.ForcedEvents.Add(Forced(account, T.AddHours(-1)));
                break;
            case "forced-before-complete":
            case "missing-before-complete":
                // With complete history the proof walks from the account's start, so evidence before the window counts and is
                // read, never scoped away.
                if (change == "forced-before-complete") db.ForcedEvents.Add(Forced(account, T.AddHours(-1)));
                else MissingOrder(db, account, "old", "sell", 1, T.AddHours(-1));
                db.Snapshots.Remove(await db.Snapshots.SingleAsync(s => s.AccountId == account.Id));
                break;
            case "unanchored-start":
            case "complete-history":
                db.Snapshots.Remove(await db.Snapshots.SingleAsync(s => s.AccountId == account.Id));
                break;
            case "no-flat-after-exit":
                // Another buy in the exit's millisecond (500 µs later): imports may keep only milliseconds, so its order is unknown.
                Trade(db, account, null, "after-exit", "buy", 0.2m, Exit.AddTicks(5_000));
                var position = await db.Snapshots.Include(s => s.Positions).SingleAsync(s => s.AccountId == account.Id);
                position.Positions.Add(new AccountPosition { OwnerId = owner, AccountId = account.Id, ContractId = "BTC", SignedQuantity = 0.2m,
                    EntryPrice = 100, MarginUsedUsd = 2 });
                break;
            case "ambiguous-entry":
                // A native-nanosecond settlement inside the entry's millisecond bucket.
                Pay(db, account, "at-entry", 0.01m, T.AddTicks(4_000), T.ToUnixTimeMilliseconds() * 1_000_000 + 400_000);
                break;
            case "idless-legacy":
                // The Play trades native market "0"; a retired market "9" shares the instrument, so an ID-less fill of it stays in
                // Activity's unresolved partition and could be either market's.
                foreach (var fill in db.Fills.Local) fill.VenueContractId = "0";
                foreach (var payment in db.FundingPayments.Local) payment.VenueContractId = "0";
                Trade(db, account, null, "retired-in", "buy", 1, T.AddHours(-10), native: "9");
                Trade(db, account, null, "retired-out", "sell", 1, T.AddHours(-9), native: "9");
                Trade(db, account, null, "legacy-in", "buy", 0.1m, T.AddHours(-5));
                Trade(db, account, null, "legacy-out", "sell", 0.1m, T.AddHours(-4));
                break;
            case "ambiguous-exit":
                // A whole-second settlement in the exit's second.
                Pay(db, account, "at-exit", 0.01m, Exit);
                break;
            case "allocated-exit":
            {
                // Phase 2, item 11: the Play's own linked exit is shared with another Play that entered inside the window. Its
                // allocated part alone makes the position shared, before any other check.
                var other = AddPlay(db, account, closed: false);
                Trade(db, account, other, "other-in", "buy", 0.5m, T.AddMinutes(30));
                var exit = db.Fills.Local.Single(f => f.SourceFillId == "fill-out");
                exit.Quantity = 1.5m;
                (await db.Orders.SingleAsync(o => o.ExternalOrderId == "order-out")).OriginalSize = 1.5m;
                await db.SaveChangesAsync();
                Allocate(db, exit, AllocationReason.SharedClose, (play.Id, "long", AllocationRole.Exit, 1m), (other.Id, "long", AllocationRole.Exit, 0.5m));
                break;
            }
            case "allocated-in-window":
            case "allocated-after-window":
            case "flip-in-window":
            {
                // Phase 2, item 11: a round trip of two other Plays, its exit split between them (a shared exit) or flipped from
                // one into the other, inside the window or after the exit, where only the snapshot anchor walks over it.
                var after = change == "allocated-after-window";
                var (opened, closed) = after ? (Exit.AddMinutes(20), Exit.AddMinutes(40)) : (T.AddMinutes(90), T.AddMinutes(150));
                var first = AddPlay(db, account, closed: false);
                var second = change == "flip-in-window" ? AddShortPlay(db, account) : AddPlay(db, account, closed: false);
                // The flip's fill as the venue labels it; the quantities stay flat by the snapshot either way.
                Trade(db, account, null, "other-in", "buy", change == "flip-in-window" ? 1m : 0.5m, opened);
                Trade(db, account, null, "other-out", "sell", change == "flip-in-window" ? 1m : 0.5m, closed);
                var shared = db.Fills.Local.Single(f => f.SourceFillId == "fill-other-out");
                if (change == "flip-in-window") (shared.PositionEffect, shared.Direction) = (ExecutionFacts.Flip, "Long > Short");
                await db.SaveChangesAsync();
                if (change == "flip-in-window")
                    Allocate(db, shared, AllocationReason.Flip, (first.Id, "long", AllocationRole.Exit, 0.5m), (second.Id, "short", AllocationRole.Entry, 0.5m));
                else
                    Allocate(db, shared, AllocationReason.SharedClose, (first.Id, "long", AllocationRole.Exit, 0.25m), (second.Id, "long", AllocationRole.Exit, 0.25m));
                break;
            }
        }
        await db.SaveChangesAsync();
        return (account.Id, play.Id);
    }

    /// <summary>An owner-confirmed allocation of one retained fill (Phase 2, item 11), saved with its first revision.</summary>
    private static void Allocate(VesselDbContext db, ImportedFill fill, AllocationReason reason,
        params (Guid Play, string Direction, AllocationRole Role, decimal Quantity)[] lines)
    {
        var (allocation, revision) = FillAllocation.Create(fill, reason, "Split between plays.",
            lines.Select(l => new AllocationLineRequest(l.Play, l.Direction, l.Role, l.Role == AllocationRole.Entry ? "entry-1" : null, l.Quantity)).ToList(), Read);
        db.FillAllocations.Add(allocation);
        db.FillAllocationRevisions.Add(revision);
    }

    /// <summary>A planned short Play on the instrument, the entry side of a flip.</summary>
    private static Play AddShortPlay(VesselDbContext db, Account account)
    {
        var plan = TestPlays.Plan() with { Direction = "short" };
        plan = plan with { Entries = [plan.Entries[0] with { Stops = [new PlanExit("stop-1", "price", "105", "100")],
            Targets = [new PlanExit("target-1", "price", "90", "100")] }] };
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan), T.AddDays(-1));
        var (change, revision) = play.MarkPlanned(T.AddDays(-1));
        db.Plays.Add(play); db.Add(change); db.Add(revision);
        return play;
    }

    private static ExecutionOmission Marker(Account account, DateTimeOffset? at, DateTimeOffset? from, DateTimeOffset? to) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, Kind = ExecutionOmission.Fee, Reason = "fee-bearing or unverified fee",
        SourceId = "omitted-1", ContractId = "BTC", OccurredAtUtc = at, UncertainFromUtc = from, UncertainToUtc = to, FirstSeenAtUtc = Read,
        LastSeenAtUtc = Read,
    };

    [PostgresFact]
    public async Task Sole_exposure_shows_the_exact_settled_sum_beside_unchanged_results()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (_, playId) = await SeedAsync(database, owner);
        await using var db = database.Context(owner);
        var execution = await Service(db).GetAsync(playId, default);
        // Only the two settlements inside the window, summed exactly; the one before and the one after are not this Play's.
        Assert.Equal(new PlayFundingDto("sole", null, Inside, "USDC", 2, T, Exit), execution.Funding);
        // Funding is never added to a result: the venue net and the Play-local result are the fills' alone.
        Assert.Equal("9.9", execution.Totals.VenueNetUsd);
        Assert.Equal("9.9", execution.Totals.Local!.NetUsd);
        Assert.Equal("10", execution.Totals.ClosedPnlUsd);
        // Activity still reports the operation's funding separately, its combined total unverified.
        var operation = Assert.Single((await ActivityTestSupport.Activity(db).PageAsync()).Operations);
        Assert.Equal(Inside, operation.FundingNetUsd);
        Assert.Equal(("complete", "unknown"), (operation.FundingStatus, operation.FundingPnlBasis));
        Assert.Null(operation.TotalResultUsd);
        Assert.Equal("9.9", operation.NetResultUsd);
    }

    [PostgresTheory]
    [InlineData("open-gap", "incomplete", "history-gap")]
    [InlineData("no-continuity", "incomplete", "history-unproven")]
    [InlineData("unlinked-fill", "shared", "unlinked-fill")]
    [InlineData("missing-order", "incomplete", "missing-executions")]
    [InlineData("marker-time", "incomplete", "omitted-executions")]
    [InlineData("marker-window", "incomplete", "omitted-executions")]
    [InlineData("forced-event", "incomplete", "omitted-executions")]
    [InlineData("unanchored-start", "incomplete", "flat-start-unproven")]
    [InlineData("no-flat-after-exit", "incomplete", "flat-end-unproven")]
    [InlineData("partial-funding", "incomplete", "funding-coverage")]
    [InlineData("ambiguous-entry", "incomplete", "funding-ambiguous")]
    [InlineData("ambiguous-exit", "incomplete", "funding-ambiguous")]
    [InlineData("idless-legacy", "incomplete", "identity")]
    [InlineData("missing-in-window", "incomplete", "missing-executions")]
    [InlineData("missing-after-exit", "incomplete", "flat-start-unproven")]
    [InlineData("forced-before-complete", "incomplete", "flat-start-unproven")]
    [InlineData("missing-before-complete", "incomplete", "flat-start-unproven")]
    // Phase 2, item 11: a fill split between plays, the Play's own or another's, in the window or where a proof walks.
    [InlineData("allocated-exit", "shared", "allocated")]
    [InlineData("allocated-in-window", "shared", "allocated")]
    [InlineData("flip-in-window", "shared", "allocated")]
    [InlineData("allocated-after-window", "shared", "allocated")]
    public async Task An_unproven_condition_reads_shared_or_incomplete_never_a_sum(string change, string state, string reason)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (_, playId) = await SeedAsync(database, owner, change);
        Assert.Equal(Not(state, reason), await FundingAsync(database, owner, playId));
    }

    [PostgresTheory]
    [InlineData("complete-history")]
    [InlineData("forced-before-window")]
    public async Task Complete_history_proves_the_flat_start_and_evidence_before_the_window_needs_it(string change)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (_, playId) = await SeedAsync(database, owner, change);
        Assert.Equal(new PlayFundingDto("sole", null, Inside, "USDC", 2, T, Exit), await FundingAsync(database, owner, playId));
    }

    /// <summary>An Activity aggregate captured earlier, as a cache entry or a racing read can serve it.</summary>
    private sealed class EarlierSummaries(ActivitySummaries value) : IActivitySummaryReader
    {
        public Task<ActivitySummaries> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) =>
            Task.FromResult(value);
    }

    private static async Task<PlayFundingDto?> FundingWithAsync(CoreDatabase database, Guid owner, Guid playId, ActivitySummaries summaries)
    {
        await using var db = database.Context(owner);
        var venues = TestVenues.With(new FundingVenue());
        var activity = new ActivityService(new ActivityStore(db), new WorkspaceStore(db, TestUnitOfWork.For(db)), TimeProvider.System, venues,
            new EarlierSummaries(summaries));
        return (await new PlayExecutionService(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)),
            venues, TimeProvider.System, TestUnitOfWork.For(db), activity: activity).GetAsync(playId, default)).Funding;
    }

    [PostgresFact]
    public async Task A_backfill_between_the_aggregate_and_the_ledger_read_never_certifies_the_older_fills()
    {
        // Sol round 1 on 9b: Activity's aggregate was read while the gap was open; a backfill then retained an unlinked round
        // trip inside the window and closed the gap before the ledger was read.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (accountId, playId) = await SeedAsync(database, owner, "open-gap");
        ActivitySummaries before;
        await using (var db = database.Context(owner))
            before = ActivityService.Aggregate(await new ActivityStore(db).ReadAsync(accountId, default));
        await using (var db = database.Context(owner))
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            Trade(db, account, null, "backfilled-entry", "buy", 0.5m, T.AddMinutes(90));
            Trade(db, account, null, "backfilled-exit", "sell", 0.5m, T.AddMinutes(150));
            (await db.HistoryGaps.SingleAsync()).Close((await db.FillCoverage.SingleAsync()).Id, Read);
            await db.SaveChangesAsync();
        }
        // The facts moved past the aggregate's revision: read again, still earlier, so nothing is shown.
        Assert.Equal(Not("incomplete", "changing"), await FundingWithAsync(database, owner, playId, before));
        // A fresh read sees the backfilled round trip.
        Assert.Equal(Not("shared", "unlinked-fill"), await FundingAsync(database, owner, playId));
    }

    [PostgresFact]
    public async Task Inside_a_read_snapshot_such_as_the_bundles_the_evidence_reuses_it()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (_, playId) = await SeedAsync(database, owner);
        await using var db = database.Context(owner);
        var execution = await TestUnitOfWork.For(db).ReadSnapshotAsync(() => Service(db).GetAsync(playId, default), default);
        Assert.Equal(new PlayFundingDto("sole", null, Inside, "USDC", 2, T, Exit), execution.Funding);
    }

    [PostgresFact]
    public async Task A_ledger_change_without_a_facts_change_is_read_with_the_same_facts()
    {
        // Closing a gap moves no facts revision: the aggregate's facts are exactly the ones the ledger was read with.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (accountId, playId) = await SeedAsync(database, owner, "open-gap");
        ActivitySummaries before;
        await using (var db = database.Context(owner))
            before = ActivityService.Aggregate(await new ActivityStore(db).ReadAsync(accountId, default));
        Assert.Equal(Not("incomplete", "history-gap"), await FundingWithAsync(database, owner, playId, before));
        await using (var db = database.Context(owner))
        {
            (await db.HistoryGaps.SingleAsync()).Close((await db.FillCoverage.SingleAsync()).Id, Read);
            await db.SaveChangesAsync();
        }
        Assert.Equal(new PlayFundingDto("sole", null, Inside, "USDC", 2, T, Exit), await FundingWithAsync(database, owner, playId, before));
    }

    [PostgresFact]
    public async Task Concurrent_plays_on_one_position_are_shared_not_allocated()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        Guid outer, inner;
        await using (var db = database.Context(owner))
        {
            var account = AddAccount(db, owner);
            var a = AddPlay(db, account, closed: true);
            var b = AddPlay(db, account, closed: true, T.AddHours(2));
            Trade(db, account, a, "a-in", "buy", 1, T);
            Trade(db, account, a, "a-out", "sell", 1, Exit, OrderLinkRole.Exit, price: 110, pnl: 10);
            Trade(db, account, b, "b-in", "buy", 0.5m, T.AddHours(1));
            Trade(db, account, b, "b-out", "sell", 0.5m, T.AddHours(2), OrderLinkRole.Exit, price: 105, pnl: 2.5m);
            db.Snapshots.Add(Snapshot(account, Read));
            db.FillCoverage.Add(Coverage(account, T.AddHours(-2), Read));
            db.FundingStates.Add(FundingState(account, T.AddDays(-1), Read));
            Pay(db, account, "inside", 0.5m, T.AddMinutes(90));
            await db.SaveChangesAsync();
            (outer, inner) = (a.Id, b.Id);
        }
        // The outer Play's window holds the inner Play's fills; the inner one starts on a position already open.
        Assert.Equal(Not("shared", "other-play"), await FundingAsync(database, owner, outer));
        Assert.Equal(Not("shared", "open-position"), await FundingAsync(database, owner, inner));
    }

    [PostgresFact]
    public async Task An_open_play_runs_to_the_latest_covered_snapshot_in_whole_milliseconds()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var read = Read.AddTicks(1_230); // a venue's sub-millisecond observation
        Guid playId;
        await using (var db = database.Context(owner))
        {
            var account = AddAccount(db, owner);
            var play = AddPlay(db, account, closed: false);
            Trade(db, account, play, "in", "buy", 1, T);
            // The venue's position is the Play's own: the snapshot walked back over covered history is flat before T.
            db.Snapshots.Add(Snapshot(account, read, 1));
            db.FillCoverage.Add(Coverage(account, T.AddHours(-2), read));
            db.FundingStates.Add(FundingState(account, T.AddDays(-1), Read));
            Pay(db, account, "first", -0.25m, T.AddHours(1));
            Pay(db, account, "second", 0.125m, T.AddHours(4));
            await db.SaveChangesAsync();
            playId = play.Id;
        }
        Assert.Equal(new PlayFundingDto("sole", null, "-0.125", "USDC", 2, T, Read), await FundingAsync(database, owner, playId));

        // A position larger than the Play's own is someone else's exposure too.
        await using (var db = database.Context(owner))
        {
            (await db.Snapshots.Include(s => s.Positions).SingleAsync()).Positions.Single().SignedQuantity = 1.5m;
            await db.SaveChangesAsync();
        }
        Assert.Equal(Not("shared", "open-position"), await FundingAsync(database, owner, playId));
    }

    [PostgresFact]
    public async Task Owners_are_isolated_and_a_disabled_account_or_a_venue_without_a_funding_read_shows_no_amount()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var (accountId, playId) = await SeedAsync(database, owner);
        // Another owner's account on the same address, with an unlinked fill, an open gap and a marker in the window.
        var other = Guid.NewGuid();
        Guid otherPlay;
        await using (var db = database.Context(other))
        {
            var account = AddAccount(db, other);
            var play = AddPlay(db, account, closed: true);
            Trade(db, account, play, "in", "buy", 1, T);
            Trade(db, account, play, "out", "sell", 1, Exit, OrderLinkRole.Exit);
            Trade(db, account, null, "stray", "buy", 1, T.AddHours(1));
            db.HistoryGaps.Add(new AccountHistoryGap { Id = Guid.CreateVersion7(), OwnerId = other, AccountId = account.Id, OpenedAtUtc = Read });
            db.ExecutionOmissions.Add(Marker(account, T.AddHours(2), null, null));
            await db.SaveChangesAsync();
            otherPlay = play.Id;
        }
        Assert.Equal(new PlayFundingDto("sole", null, Inside, "USDC", 2, T, Exit), await FundingAsync(database, owner, playId));
        await using (var db = database.Context(other))
        {
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => Service(db).GetAsync(playId, default))).StatusCode);
            // The other owner's own Play sees its own facts, never this owner's ledger or funding.
            Assert.Equal(Not("shared", "unlinked-fill"), (await Service(db).GetAsync(otherPlay, default)).Funding);
        }

        Assert.Equal(Not("unavailable", "no-funding-read"), await FundingAsync(database, owner, playId, funding: false));
        await using (var db = database.Context(owner))
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.UpdateSettings(account.Name, account.PortfolioId, false);
            await db.SaveChangesAsync();
        }
        Assert.Equal(Not("unavailable", "account-disabled"), await FundingAsync(database, owner, playId));
    }

    [PostgresFact]
    public async Task Paper_plays_and_plays_without_an_entry_fill_get_no_funding_line()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        Guid paperPlay, plannedPlay;
        await using (var db = database.Context(owner))
        {
            var paper = AddAccount(db, owner, "paper");
            var play = new Play(Guid.NewGuid(), paper, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
                PlayDocuments.Serialize(TestPlays.Plan()), T);
            var (change, revision) = play.MarkPlanned(T);
            db.Plays.Add(play); db.Add(change); db.Add(revision);
            var real = AddAccount(db, owner);
            var planned = new Play(Guid.NewGuid(), real, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
                PlayDocuments.Serialize(TestPlays.Plan()), T);
            (change, revision) = planned.MarkPlanned(T);
            db.Plays.Add(planned); db.Add(change); db.Add(revision);
            db.FundingStates.Add(FundingState(real, T.AddDays(-1), Read));
            await db.SaveChangesAsync();
            (paperPlay, plannedPlay) = (play.Id, planned.Id);
        }
        await using (var db = database.Context(owner))
        {
            // Venue-fact features are real-only: a paper Play's execution view, funding included, is refused.
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => Service(db).GetAsync(paperPlay, default))).StatusCode);
            // Nothing held yet, so there is no holding window and no line.
            Assert.Null((await Service(db).GetAsync(plannedPlay, default)).Funding);
        }
    }
}
