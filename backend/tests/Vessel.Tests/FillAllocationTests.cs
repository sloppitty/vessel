using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Phase 2, item 11: the owner's split of one fill between Plays, its conservation, fee rule and revisions.</summary>
public sealed class FillAllocationDomainTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T10:00:00Z");
    private static readonly Guid Owner = Guid.NewGuid(), AccountId = Guid.NewGuid();
    // Fixed IDs, so the lowest Play ID as lowercase text is known: A < B < C.
    private static readonly Guid A = Guid.Parse("10000000-0000-0000-0000-000000000000"),
        B = Guid.Parse("20000000-0000-0000-0000-000000000000"), C = Guid.Parse("30000000-0000-0000-0000-000000000000");

    internal static ImportedFill Fill(decimal quantity, decimal fee, string side = ExecutionFacts.Sell, string effect = ExecutionFacts.Close,
        string feeBasis = ExecutionFacts.FeeReported) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, ContractId = "BTC", SourceFillId = "x1", Side = side, Direction = "Close Long",
        PositionEffect = effect, Price = 105m, Quantity = quantity, Fee = fee, FeeToken = "USDC", FeeBasis = feeBasis, ClosedPnlUsd = 15m,
        OccurredAtUtc = At, ExternalOrderId = "23", TransactionHash = "hash",
    };

    private static AllocationLineRequest Exit(Guid play, decimal quantity, string direction = "long") => new(play, direction, AllocationRole.Exit, null, quantity);
    private static AllocationLineRequest Entry(Guid play, decimal quantity, string direction = "short") => new(play, direction, AllocationRole.Entry, "entry-1", quantity);

    [Fact]
    public void Lines_conserve_the_fill_exactly_and_a_difference_of_any_size_is_refused()
    {
        var fill = Fill(3m, 0m);
        var (allocation, revision) = FillAllocation.Create(fill, AllocationReason.SharedClose, "Closed both in one sell.", [Exit(A, 1m), Exit(B, 2m)], At);
        Assert.Equal((1, 2, fill.Id), (allocation.Revision, allocation.Lines.Count, allocation.FillId));
        Assert.Equal(1, revision.Revision);
        // A whole 28th place, over or under, is a difference: nothing is rounded to make the lines fit.
        foreach (var lines in new[]
        {
            new[] { Exit(A, 1m), Exit(B, 1.9999999999999999999999999999m) },
            new[] { Exit(A, 1m), Exit(B, 2.0000000000000000000000000001m) },
            new[] { Exit(A, 1m), Exit(B, 1m) },
            new[] { Exit(A, 3m), Exit(B, 0.0000000000000000000000000001m) },
        })
        {
            var refused = Assert.Throws<DomainValidationException>(() => FillAllocation.Create(fill, AllocationReason.SharedClose, "Split.", lines, At));
            Assert.Contains("must match exactly", refused.Message);
        }
        Assert.Throws<DomainValidationException>(() => FillAllocation.Create(fill, AllocationReason.SharedClose, "Split.", [Exit(A, 3m), Exit(A, 0m)], At));
        Assert.Throws<DomainValidationException>(() => FillAllocation.Create(fill, AllocationReason.SharedClose, "Split.", [Exit(A, 1.5m), Exit(A, 1.5m)], At));
        Assert.Throws<DomainValidationException>(() => FillAllocation.Create(fill, AllocationReason.SharedClose, "Split.", [], At));
        Assert.Throws<DomainValidationException>(() => FillAllocation.Create(fill, AllocationReason.SharedClose, " ", [Exit(A, 1m), Exit(B, 2m)], At));
        Assert.Throws<DomainValidationException>(() => FillAllocation.Create(fill, (AllocationReason)9, "Split.", [Exit(A, 1m), Exit(B, 2m)], At));
    }

    [Fact]
    public void Fees_are_shared_pro_rata_truncated_to_the_fee_precision_with_the_remainder_on_the_largest_line()
    {
        // 0.01 over thirds: 0.003333 and 0.006666 at six places; the 0.000001 left goes to the largest line.
        var (allocation, _) = FillAllocation.Create(Fill(3m, 0.01m), AllocationReason.SharedClose, "Shared.", [Exit(A, 1m), Exit(B, 2m)], At);
        var a = allocation.Lines.Single(l => l.PlayId == A);
        var b = allocation.Lines.Single(l => l.PlayId == B);
        Assert.Equal((0.003333m, false, 0.006667m, true), (a.Fee, a.TookRemainder, b.Fee, b.TookRemainder));
        Assert.Equal(0.000001m, allocation.FeeRemainder);
        Assert.Equal(0.01m, a.Fee + b.Fee);

        // Equal lines tie: the lowest Play ID as text takes it, whatever the request order.
        var (tied, _) = FillAllocation.Create(Fill(3m, 0.01m), AllocationReason.SharedClose, "Shared.", [Exit(C, 1m), Exit(B, 1m), Exit(A, 1m)], At);
        Assert.Equal([(A, 0.003334m, true), (B, 0.003333m, false), (C, 0.003333m, false)],
            tied.Lines.OrderBy(l => l.PlayId.ToString("D")).Select(l => (l.PlayId, l.Fee!.Value, l.TookRemainder)));

        // A fee reported finer than six places keeps its own precision; an exact split has no remainder line.
        var (fine, _) = FillAllocation.Create(Fill(4m, 0.00000004m), AllocationReason.SharedClose, "Shared.", [Exit(A, 1m), Exit(B, 3m)], At);
        Assert.Equal((0.00000001m, 0.00000003m, 0m), (fine.Lines.Single(l => l.PlayId == A).Fee!.Value, fine.Lines.Single(l => l.PlayId == B).Fee!.Value,
            fine.FeeRemainder!.Value));
        Assert.DoesNotContain(fine.Lines, l => l.TookRemainder);
        Assert.Equal(8, FillAllocation.FeePlaces(0.00000004m));
        Assert.Equal(6, FillAllocation.FeePlaces(0.01m));

        // A rebate is truncated toward zero too, and the remainder keeps its sign.
        var (rebate, _) = FillAllocation.Create(Fill(3m, -0.01m), AllocationReason.SharedClose, "Shared.", [Exit(A, 1m), Exit(B, 2m)], At);
        Assert.Equal((-0.003333m, -0.006667m, -0.000001m), (rebate.Lines.Single(l => l.PlayId == A).Fee!.Value,
            rebate.Lines.Single(l => l.PlayId == B).Fee!.Value, rebate.FeeRemainder!.Value));
    }

    [Fact]
    public void An_unknown_fee_stays_unknown_on_every_line()
    {
        var fill = Fill(3m, 0.01m, feeBasis: "unknown");
        var (allocation, _) = FillAllocation.Create(fill, AllocationReason.SharedClose, "Shared.", [Exit(A, 1m), Exit(B, 2m)], At);
        Assert.All(allocation.Lines, l => Assert.Equal(((decimal?)null, false), (l.Fee, l.TookRemainder)));
        Assert.Null(allocation.FeeRemainder);
        var portion = FillAllocation.Portion(fill, allocation.Lines.Single(l => l.PlayId == A));
        Assert.Equal((1m, 0m, "unknown"), (portion.Quantity, portion.Fee, portion.FeeBasis));
    }

    [Fact]
    public void A_flip_splits_into_one_exit_and_one_entry_of_the_other_direction()
    {
        var flip = Fill(2m, 0.02m, effect: ExecutionFacts.Flip);
        var (allocation, _) = FillAllocation.Create(flip, AllocationReason.Flip, "Reversed in one sell.", [Exit(A, 1m), Entry(B, 1m)], At);
        var exit = FillAllocation.Portion(flip, allocation.Lines.Single(l => l.Role == AllocationRole.Exit));
        var entry = FillAllocation.Portion(flip, allocation.Lines.Single(l => l.Role == AllocationRole.Entry));
        // The closing half closes and the opening half opens; the venue's P&L of the whole fill is never carried.
        Assert.Equal((ExecutionFacts.Close, 1m, 0m, flip.Id), (exit.PositionEffect, exit.Quantity, exit.ClosedPnlUsd, exit.Id));
        Assert.Equal((ExecutionFacts.Open, "entry-1"), (entry.PositionEffect, allocation.Lines.Single(l => l.Role == AllocationRole.Entry).EntryId));

        // Shapes: a flip has exactly one exit and one entry, and only a reported flip splits that way.
        Assert.Throws<DomainRuleException>(() => FillAllocation.Create(flip, AllocationReason.SharedClose, "Split.", [Exit(A, 1m), Exit(B, 1m)], At));
        Assert.Throws<DomainRuleException>(() => FillAllocation.Create(flip, AllocationReason.Flip, "Split.", [Exit(A, 1m), Entry(B, 0.5m), Entry(C, 0.5m)], At));
        Assert.Throws<DomainRuleException>(() => FillAllocation.Create(Fill(2m, 0m), AllocationReason.Flip, "Split.", [Exit(A, 1m), Entry(B, 1m)], At));
        Assert.Throws<DomainRuleException>(() => FillAllocation.Create(Fill(2m, 0m, effect: ExecutionFacts.Open), AllocationReason.Other, "Split.",
            [Exit(A, 1m), Exit(B, 1m)], At));
        // The reason fits the shape.
        Assert.Throws<DomainValidationException>(() => FillAllocation.Create(flip, AllocationReason.SharedClose, "Split.", [Exit(A, 1m), Entry(B, 1m)], At));
        Assert.Throws<DomainValidationException>(() => FillAllocation.Create(Fill(2m, 0m), AllocationReason.Flip, "Split.", [Exit(A, 1m), Exit(B, 1m)], At));
        Assert.NotNull(FillAllocation.Create(flip, AllocationReason.Other, "Reversed.", [Exit(A, 1m), Entry(B, 1m)], At).Allocation);
    }

    [Fact]
    public void A_later_opening_execution_of_a_split_order_takes_entries_alone()
    {
        // Sol round 1: the rest of a flip order opens Plays only; the service checks the order was split by a flip.
        var opening = Fill(1m, 0.01m, effect: ExecutionFacts.Open);
        var (allocation, _) = FillAllocation.Create(opening, AllocationReason.Flip, "The rest of the flip.", [Entry(B, 0.4m), Entry(C, 0.6m)], At);
        Assert.All(allocation.Lines, l => Assert.Equal(AllocationRole.Entry, l.Role));
        Assert.Equal(0.01m, allocation.Lines.Sum(l => l.Fee!.Value));
        // Entries alone never take a closing or flipping fill, nor the reason shared-close.
        Assert.Throws<DomainRuleException>(() => FillAllocation.Create(Fill(1m, 0m), AllocationReason.Other, "Split.", [Entry(B, 1m)], At));
        Assert.Throws<DomainRuleException>(() => FillAllocation.Create(Fill(1m, 0m, effect: ExecutionFacts.Flip), AllocationReason.Flip, "Split.", [Entry(B, 1m)], At));
        Assert.Throws<DomainValidationException>(() => FillAllocation.Create(opening, AllocationReason.SharedClose, "Split.", [Entry(B, 1m)], At));
        Assert.Throws<DomainRuleException>(() => FillAllocation.Create(opening, AllocationReason.Flip, "Split.", [Entry(B, 1m, "long")], At));
    }

    [Fact]
    public void Roles_follow_the_fill_side_and_each_Play_s_direction()
    {
        // A sell closes longs and opens shorts; a buy closes shorts and opens longs.
        Assert.Throws<DomainRuleException>(() => FillAllocation.Create(Fill(2m, 0m), AllocationReason.SharedClose, "Split.",
            [Exit(A, 1m), Exit(B, 1m, "short")], At));
        Assert.NotNull(FillAllocation.Create(Fill(2m, 0m, ExecutionFacts.Buy), AllocationReason.SharedClose, "Split.",
            [Exit(A, 1m, "short"), Exit(B, 1m, "short")], At).Allocation);
        var flip = Fill(2m, 0m, effect: ExecutionFacts.Flip);
        Assert.Throws<DomainRuleException>(() => FillAllocation.Create(flip, AllocationReason.Flip, "Split.", [Exit(A, 1m), Entry(B, 1m, "long")], At));
        Assert.Throws<DomainValidationException>(() => FillAllocation.Create(flip, AllocationReason.Flip, "Split.",
            [Exit(A, 1m), new AllocationLineRequest(B, "short", AllocationRole.Entry, null, 1m)], At));
        Assert.Throws<DomainValidationException>(() => FillAllocation.Create(Fill(2m, 0m), AllocationReason.SharedClose, "Split.",
            [Exit(A, 1m), new AllocationLineRequest(B, "long", AllocationRole.Exit, "entry-1", 1m)], At));
    }

    [Fact]
    public void Each_change_is_the_next_revision_and_a_Play_still_on_the_lines_keeps_its_row()
    {
        var fill = Fill(3m, 0.03m);
        var (allocation, first) = FillAllocation.Create(fill, AllocationReason.SharedClose, "First split.", [Exit(A, 1m), Exit(B, 2m)], At);
        var rowA = allocation.Lines.Single(l => l.PlayId == A).Id;
        var second = allocation.Replace(fill, AllocationReason.Other, "Second split.", [Exit(A, 2m), Exit(C, 1m)], At.AddMinutes(1));
        Assert.Equal((2, 2, "Second split.", AllocationReason.Other), (allocation.Revision, second.Revision, allocation.Note, allocation.Reason));
        Assert.Equal(rowA, allocation.Lines.Single(l => l.PlayId == A).Id);
        Assert.Equal([A, C], allocation.Lines.Select(l => l.PlayId).Order());
        Assert.Equal((2m, 0.02m), (allocation.Lines.Single(l => l.PlayId == A).Quantity, allocation.Lines.Single(l => l.PlayId == A).Fee));
        Assert.Contains("\"quantity\":\"1\"", first.Lines);
        Assert.Contains(C.ToString(), second.Lines);

        var removed = allocation.Remove(AllocationReason.Other, "Not shared after all.", At.AddMinutes(2));
        Assert.Equal((3, 3, 0, "[]"), (allocation.Revision, removed.Revision, allocation.Lines.Count, removed.Lines));
        Assert.Null(allocation.FeeRemainder);
        Assert.Throws<DomainRuleException>(() => allocation.Remove(AllocationReason.Other, "Again.", At.AddMinutes(3)));
        // After a removal the fill can be allocated again, continuing the numbering.
        allocation.Replace(fill, AllocationReason.SharedClose, "Third split.", [Exit(B, 3m)], At.AddMinutes(4));
        Assert.Equal((4, 1), (allocation.Revision, allocation.Lines.Count));
        Assert.Throws<DomainRuleException>(() => allocation.Replace(Fill(3m, 0m), AllocationReason.SharedClose, "Another fill.", [Exit(B, 3m)], At));
    }

    [Fact]
    public void An_allocation_moves_a_real_active_Play_to_its_next_version_only()
    {
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", "Trading");
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), At);
        play.MarkPlanned(At);
        var version = play.Version;
        play.RecordFillAllocation(At.AddMinutes(1));
        Assert.Equal(version + 1, play.Version);
        play.MarkOpen("Entry.", At.AddMinutes(2));
        play.MarkClosed("Exits.", At.AddMinutes(3));
        Assert.Throws<DomainRuleException>(() => play.RecordFillAllocation(At.AddMinutes(4)));
    }
}

/// <summary>Phase 2, item 11: the read rule every Play reader applies, and how Review and search treat its result.</summary>
public sealed class FillAllocationReadRuleTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T10:00:00Z");
    private readonly Account account = new(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Trading");

    private Play Open(string direction = "long")
    {
        var plan = TestPlays.Plan() with { Direction = direction };
        if (direction == "short")
            plan = plan with { Entries = [plan.Entries[0] with { Stops = [new PlanExit("stop-1", "price", "105", "100")], Targets = [new PlanExit("target-1", "price", "90", "100")] }] };
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, PlayDocuments.Serialize(plan), At);
        play.MarkPlanned(At);
        play.MarkOpen("Entry.", At.AddMinutes(1));
        return play;
    }

    private ImportedOrder Order(string id, string side) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = "BTC", ExternalOrderId = id, Side = side,
        OrderType = "Limit", OriginalSize = 1, Status = "filled", VenueStatus = "filled", PlacedAtUtc = At, StatusAtUtc = At, ObservedAtUtc = At,
    };

    private ImportedFill Fill(string id, string order, string side, string effect, decimal quantity, decimal price, int minutes) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = "BTC", SourceFillId = id, Side = side,
        Direction = effect, PositionEffect = effect, Price = price, Quantity = quantity, Fee = 0.01m, FeeToken = "USDC", ClosedPnlUsd = 9m,
        OccurredAtUtc = At.AddMinutes(minutes), ExternalOrderId = order, TransactionHash = "hash",
    };

    [Fact]
    public void An_allocated_fill_counts_only_through_each_Play_s_own_line()
    {
        var (a, b) = (Open(), Open());
        var exitOrder = Order("23", "sell");
        var aEntry = new PlayOrderLink(a, Order("21", "buy"), OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, At);
        var aStop = new PlayOrderLink(a, exitOrder, OrderLinkRole.Stop, "entry-1", "stop-1", OrderLinkState.Linked, OrderLinkSource.Owner, At);
        var entry = Fill("e1", "21", "buy", "open", 1m, 100m, 1);
        var shared = Fill("x1", "23", "sell", "close", 3m, 105m, 5);
        var later = Fill("x2", "23", "sell", "close", 0.5m, 106m, 6);
        var (allocation, _) = FillAllocation.Create(shared, AllocationReason.SharedClose, "Both.",
            [new(a.Id, "long", AllocationRole.Exit, null, 1m), new(b.Id, "long", AllocationRole.Exit, null, 2m)], At);
        AllocatedFill[] allocations = [new(allocation, shared, exitOrder, false)];

        var forA = FillAllocations.For(a.Id, [(aEntry, entry), (aStop, shared), (aStop, later)], allocations);
        // A's stop link keeps its level on A's line; an unallocated fill of the same order still counts through the link.
        Assert.Equal([("e1", OrderLinkRole.Entry, 1m, false), ("x2", OrderLinkRole.Stop, 0.5m, false), ("x1", OrderLinkRole.Stop, 1m, true)],
            forA.Select(f => (f.Fill.SourceFillId, f.Role, f.Fill.Quantity, f.Allocated)));
        Assert.Equal("stop-1", forA.Single(f => f.Allocated).LevelId);
        Assert.Equal(0.003333m, forA.Single(f => f.Allocated).Fill.Fee);
        // B has no link to the order: its line reaches it as an unplanned exit.
        var forB = Assert.Single(FillAllocations.For(b.Id, [], allocations));
        Assert.Equal((OrderLinkRole.Exit, 2m, true, (string?)null), (forB.Role, forB.Fill.Quantity, forB.Allocated, forB.LevelId));
        // A third Play linked to the order gets nothing of the allocated fill, only what is not allocated.
        var c = Open();
        var cLink = new PlayOrderLink(c, exitOrder, OrderLinkRole.Exit, null, null, OrderLinkState.Linked, OrderLinkSource.Owner, At);
        Assert.Equal(["x2"], FillAllocations.For(c.Id, [(cLink, shared), (cLink, later)], allocations).Select(f => f.Fill.SourceFillId));
        // A removed allocation (no lines) is no allocation.
        allocation.Remove(AllocationReason.Other, "Undone.", At);
        Assert.Equal(3m, FillAllocations.For(a.Id, [(aStop, shared)], allocations).Single().Fill.Quantity);
        // Known missing executions on the line's order make each Play on the lines incomplete.
        var (again, _) = FillAllocation.Create(shared, AllocationReason.SharedClose, "Both.",
            [new(a.Id, "long", AllocationRole.Exit, null, 1m), new(b.Id, "long", AllocationRole.Exit, null, 2m)], At);
        Assert.Equal(["23"], FillAllocations.Incomplete(b.Id, [new(again, shared, exitOrder, true)]));
        Assert.Empty(FillAllocations.Incomplete(c.Id, [new(again, shared, exitOrder, true)]));
    }

    [Fact]
    public void A_split_order_s_other_executions_count_for_no_Play_until_allocated_and_its_resting_order_holds_the_opened_Play()
    {
        // Sol round 1: a sell order linked to a long as its target flips it into a short, and still rests. A later execution
        // of it is neither the long's exit nor the short's entry until the owner allocates it.
        var (exitPlay, opened) = (Open(), Open("short"));
        var order = Order("24", "sell");
        order.Status = "open";
        var target = new PlayOrderLink(exitPlay, order, OrderLinkRole.Target, "entry-1", "target-1", OrderLinkState.Linked, OrderLinkSource.Owner, At);
        var flip = Fill("f1", "24", "sell", ExecutionFacts.Flip, 2m, 105m, 5);
        var later = Fill("f2", "24", "sell", ExecutionFacts.Open, 1m, 105m, 6);
        var (allocation, _) = FillAllocation.Create(flip, AllocationReason.Flip, "Reversed.",
            [new(exitPlay.Id, "long", AllocationRole.Exit, null, 1m), new(opened.Id, "short", AllocationRole.Entry, "entry-1", 1m)], At);
        AllocatedFill[] allocations = [new(allocation, flip, order, false) { UnallocatedOrderFills = 1 }];

        Assert.Equal([("f1", OrderLinkRole.Target, 1m)],
            FillAllocations.For(exitPlay.Id, [(target, flip), (target, later)], allocations).Select(f => (f.Fill.SourceFillId, f.Role, f.Fill.Quantity)));
        Assert.Equal(["f1"], FillAllocations.For(opened.Id, [], allocations).Select(f => f.Fill.SourceFillId));
        Assert.Equal(["24"], FillAllocations.Unallocated(opened.Id, allocations));
        Assert.Equal(["24"], FillAllocations.Unallocated(exitPlay.Id, allocations));
        Assert.True(FillAllocations.EntryResting(opened.Id, allocations));
        Assert.True(FillAllocations.EntryResting(opened.Id, allocations, "entry-1"));
        Assert.False(FillAllocations.EntryResting(exitPlay.Id, allocations));
        // Once every execution is allocated nothing waits, and a filled order rests no more.
        order.Status = "filled";
        allocations = [new(allocation, flip, order, false)];
        Assert.Empty(FillAllocations.Unallocated(opened.Id, allocations));
        Assert.False(FillAllocations.EntryResting(opened.Id, allocations));
    }

    [Fact]
    public void A_settled_Play_with_an_allocated_fill_has_no_venue_result_but_keeps_its_Play_local_result()
    {
        var play = Open();
        play.MarkClosed("Exits.", At.AddMinutes(10));
        var entry = Fill("e1", "21", "buy", "open", 1m, 100m, 1);
        var shared = Fill("x1", "23", "sell", "close", 3m, 105m, 5);
        var (allocation, _) = FillAllocation.Create(shared, AllocationReason.SharedClose, "Both.",
            [new(play.Id, "long", AllocationRole.Exit, null, 1m), new(Guid.NewGuid(), "long", AllocationRole.Exit, null, 2m)], At);
        var portion = FillAllocation.Portion(shared, allocation.Lines.Single(l => l.PlayId == play.Id));
        var facts = new ClosedPlayFacts(play, [], [new(OrderLinkRole.Entry, entry), new(OrderLinkRole.Exit, portion) { Allocated = true }]);

        Assert.True(Vessel.Application.Plays.Execution.ExecutionIntegrity.Settled(facts));
        Assert.Null(PlayOutcomeCalculator.Outcome(facts));
        var score = ReviewService.Score(facts, At.AddHours(1), false);
        Assert.Equal((ReviewService.NotAllocated, (PlayOutcome?)null, At.AddMinutes(5)), (score.Exclusion, score.Result, score.ClosedAt));
        // 1 × 105 − 1 × 100 less the entry fee and the line's share 0.003333.
        var local = PlayLocalResult.From(facts, "USDC");
        Assert.Equal("4.986667", local.Net!.Value.ToString());
        Assert.Equal(PlayReceipts.NotAllocatedNote, PlayReceipts.Build(facts, At.AddHours(1)).EstimatedRReason);
        var outcome = JournalSearch.Outcome(play, facts, At.AddHours(1));
        Assert.Equal((PlaySearchStates.NotAllocated, (decimal?)null, At.AddMinutes(5)), (outcome.State, outcome.ResultUsd, outcome.ExitAtUtc));
        Assert.Equal(PlayReceipts.NotAllocatedNote, JournalSearch.Dto(outcome).Reason);

        // The same Play without the allocation flag is scored on the venue basis, so the flag alone decides.
        Assert.Null(ReviewService.Score(facts with { Fills = [new(OrderLinkRole.Entry, entry), new(OrderLinkRole.Exit, portion)] }, At.AddHours(1), false).Exclusion);
    }
}
