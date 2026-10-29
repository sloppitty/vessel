using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Plays;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class ExecutionQuantityPrecisionIntegrationTests : ExecutionQuantityPrecisionTestsSupport
{

    [PostgresFact]
    public async Task Persisted_missing_executions_remain_visible_to_real_sizing_and_review_stores()
    {
        await using var database = await CoreDatabase.CreateAsync();
        foreach (var direction in new[] { "long", "short" })
        {
            var f = new Fixture(direction);
            f.Add(OrderLinkRole.Entry, Huge - 1, 0, orderId: "in");
            f.Add(OrderLinkRole.Entry, 0.9m, 1, price: 1, orderId: "in");
            f.Add(OrderLinkRole.Exit, Huge - 1, 2, orderId: "out");
            f.Add(OrderLinkRole.Exit, 0.9m, 3, price: 1, pnl: 0.1m, orderId: "out");
            foreach (var order in f.Store.Orders) order.OriginalSize = Huge;
            await using var db = database.Context(f.Account.OwnerId);
            await SaveLegacyAsync(db, f);
            Assert.NotNull(PlayOutcomeCalculator.Outcome(f.Facts));

            var store = new SizingStore(db, TestUnitOfWork.For(db));
            var facts = Assert.Single(await store.ClosedPlaysAsync(default));
            Assert.Equal(new[] { "in", "out" }, facts.IncompleteOrders!.Order(StringComparer.Ordinal));
            Assert.False(ExecutionIntegrity.Settled(facts));
            Assert.Equal(f.Store.Fills.Select(fill => (fill.Quantity, fill.Price, fill.Fee, fill.ClosedPnlUsd)),
                facts.Fills.OrderBy(fill => fill.Fill.OccurredAtUtc).Select(fill =>
                    (fill.Fill.Quantity, fill.Fill.Price, fill.Fill.Fee, fill.Fill.ClosedPnlUsd)));
            var reviewStore = new ReviewStore(db, store);
            var coverage = await reviewStore.ReadAsync(new("all"), ReviewClock.Now, default);
            Assert.Contains(f.Play.Id, coverage.IncompleteOrderPlayIds!);
            var review = new ReviewService(reviewStore, new ReviewClock());
            var report = await review.GetAsync(new("all"), default);
            var record = await new SizingService(store, new CoreOwner(f.Account.OwnerId), new ReviewClock(), TestUnitOfWork.For(db)).GetAsync(default);
            var exclusions = await review.ExclusionsAsync(new("all"), default);

            Assert.Equal((0, 1), (report.Metrics.Count, report.Coverage.IncompleteExecutions));
            Assert.Empty(report.Plays);
            Assert.Equal(0, record.Record.ClosedPlays);
            Assert.Empty(record.Record.Recent);
            Assert.Equal(new[]
            {
                "Order in is filled at the venue, but not all of its executions are retained yet.",
                "Order out is filled at the venue, but not all of its executions are retained yet.",
            }, Assert.Single(exclusions.Items).Reasons);
        }
    }

    [PostgresFact]
    public async Task Persisted_quantity_exclusions_report_the_residual_even_above_decimal_range()
    {
        await using var database = await CoreDatabase.CreateAsync();
        foreach (var direction in new[] { "long", "short" })
        foreach (var aboveDecimalRange in new[] { false, true })
        {
            var f = new Fixture(direction);
            var large = aboveDecimalRange ? Huge * 5 : Huge;
            f.Add(OrderLinkRole.Entry, large, 0);
            if (aboveDecimalRange) f.Add(OrderLinkRole.Entry, large, 1);
            f.Add(OrderLinkRole.Entry, 0.1m, 2, price: 1);
            f.Add(OrderLinkRole.Exit, large, 3, pnl: 0.1m);
            if (aboveDecimalRange) f.Add(OrderLinkRole.Exit, large, 4);
            await using var db = database.Context(f.Account.OwnerId);
            await SaveLegacyAsync(db, f);
            Assert.NotNull(PlayOutcomeCalculator.Outcome(f.Facts));

            var store = new SizingStore(db, TestUnitOfWork.For(db));
            var facts = Assert.Single(await store.ClosedPlaysAsync(default));
            Assert.Empty(facts.IncompleteOrders!);
            Assert.False(ExecutionIntegrity.Settled(facts));
            var review = new ReviewService(new ReviewStore(db, store), new ReviewClock());
            var report = await review.GetAsync(new("all"), default);
            var exclusions = await review.ExclusionsAsync(new("all"), default);
            var record = await new SizingService(store, new CoreOwner(f.Account.OwnerId), new ReviewClock(), TestUnitOfWork.For(db)).GetAsync(default);

            Assert.Equal((0, 1), (report.Metrics.Count, report.Coverage.IncompleteExecutions));
            Assert.Equal(0, record.Record.ClosedPlays);
            var exclusion = Assert.Single(exclusions.Items);
            Assert.Equal(f.Play.Id, exclusion.PlayId);
            Assert.Equal("Retained entry and exit quantities do not form a closed round trip.", Assert.Single(exclusion.Reasons));
        }
    }

    private static async Task SaveLegacyAsync(VesselDbContext db, Fixture f)
    {
        f.Play.MarkOpen("Legacy entry", At);
        f.Play.MarkClosed("Legacy rounded quantities", At.AddMinutes(10));
        db.Accounts.Add(f.Account);
        db.Plays.Add(f.Play);
        db.PlanRevisions.AddRange(f.Plays.Revisions);
        db.Orders.AddRange(f.Store.Orders);
        db.Fills.AddRange(f.Store.Fills);
        db.OrderLinks.AddRange(f.Fills.DistinctBy(fill => fill.Fill.ExternalOrderId).Select(fill => new PlayOrderLink(
            f.Play, f.Store.Orders.Single(o => o.ExternalOrderId == fill.Fill.ExternalOrderId), fill.Role, fill.Role == OrderLinkRole.Entry ? "entry-1" : null, null,
            OrderLinkState.Linked, OrderLinkSource.Owner, At)));
        await db.SaveChangesAsync();
    }
}
