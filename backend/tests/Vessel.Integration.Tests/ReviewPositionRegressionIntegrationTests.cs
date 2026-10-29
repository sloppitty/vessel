using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Uncommitted independent reviewer-A reproductions and controls for 6f261c2.</summary>
public sealed class ReviewPositionRegressionIntegrationTests : ReviewPositionRegressionTestsSupport
{

    private sealed class AfterReviewRead(IReviewStore inner, Func<Task> commit) : IReviewStore
    {
        private bool committed;
        public async Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct)
        {
            var facts = await inner.ReadAsync(query, asOf, ct);
            // Deterministic interleaving of a second request, once, after ReviewStore's first snapshot commits.
            if (!committed) { committed = true; await commit(); }
            return facts;
        }
        public Task<IReadOnlyList<string>> MistakeTagsAsync(int limit, CancellationToken ct) => inner.MistakeTagsAsync(limit, ct);
    }

    [PostgresFact]
    public async Task Retrospective_conversion_between_reads_must_not_erase_the_result()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var f = new ReviewFixture();
        var account = f.Account();
        var template = f.Add(10);
        var orders = template.Fills.Select(l => TestOrders.For(account, "BTC", l.Fill.ExternalOrderId, l.Fill.Side)).ToList();
        await using (var seed = database.Context(f.Owner))
        {
            seed.Accounts.Add(account);
            seed.Fills.AddRange(template.Fills.Select(l => l.Fill));
            seed.Orders.AddRange(orders);
            await seed.SaveChangesAsync(); // The complete operation is initially unassigned, with no saved Play.
        }

        await using var read = database.Context(f.Owner);
        var sizing = new SizingStore(read, TestUnitOfWork.For(read));
        var store = new ReviewStore(read, sizing);
        var source = new ActivityReviewPositions(ActivityTestSupport.Activity(read, new ReviewClock()), sizing);
        var before = await new ReviewService(store, new ReviewClock(), TestVenues.With(), source)
            .GetAsync(new(Positions: "include"), default);
        Assert.Equal(("10", 1, 1), (before.Metrics.Net, before.Metrics.Count, before.Metrics.Positions));

        var interleaved = new AfterReviewRead(store, async () =>
        {
            await using var write = database.Context(f.Owner);
            var emptyPlan = new PlayPlanDocument("long", "margin", "", "0", null, [], new PlanNotes("", "", "", ""));
            var retrospective = Play.Retrospective(Guid.NewGuid(), account, new PerpetualInstrument(account.VenueId, "BTC"),
                PlayDocuments.Serialize(emptyPlan), template.Fills[1].Fill.OccurredAtUtc, ReviewClock.Now, "Reviewed BTC");
            write.Plays.Add(retrospective);
            for (var i = 0; i < orders.Count; i++)
                write.OrderLinks.Add(new PlayOrderLink(retrospective, orders[i], template.Fills[i].Role,
                    i == 0 ? "retrospective" : null, null, OrderLinkState.Linked, OrderLinkSource.Owner, ReviewClock.Now));
            await write.SaveChangesAsync(); // Atomic conversion, as another request could commit.
        });
        var during = await new ReviewService(interleaved, new ReviewClock(), TestVenues.With(), source)
            .GetAsync(new(Positions: "include"), default);
        var after = await new ReviewService(store, new ReviewClock(), TestVenues.With(), source)
            .GetAsync(new(Positions: "include"), default);
        Assert.Equal(("10", 1, 0), (after.Metrics.Net, after.Metrics.Count, after.Metrics.Positions));

        // Before and after both contain the same $10 result. A coherent snapshot cannot contain zero results.
        Assert.Equal(("10", 1), (during.Metrics.Net, during.Metrics.Count));
    }

    [PostgresFact]
    public async Task Check_Real_stores_exclude_foreign_and_disabled_accounts_and_intersect_portfolio()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var f = new ReviewFixture();
        var foreign = new ReviewFixture();
        var portfolio = new Portfolio(Guid.NewGuid(), f.Owner, "Selected");
        var selected = f.Account("Selected", portfolio.Id);
        var other = f.Account("Unassigned");
        var disabled = f.Account("Disabled");
        disabled.UpdateSettings(disabled.Name, null, false);
        foreign.Account("Foreign");
        foreach (var fixture in new[] { f, foreign })
        {
            await using var seed = database.Context(fixture.Owner);
            if (fixture == f) seed.Portfolios.Add(portfolio);
            seed.Accounts.AddRange(fixture.Accounts);
            foreach (var account in fixture.Accounts)
                seed.Fills.AddRange(Fill(account, 0), Fill(account, 1, "sell", "close", pnl: 10));
            await seed.SaveChangesAsync();
        }
        await using var db = database.Context(f.Owner);
        var sizing = new SizingStore(db, TestUnitOfWork.For(db));
        var service = new ReviewService(new ReviewStore(db, sizing), new ReviewClock(), TestVenues.With(),
            new ActivityReviewPositions(ActivityTestSupport.Activity(db, new ReviewClock()), sizing));
        Assert.Equal(("20", 2), ((await service.GetAsync(new(Positions: "include"), default)).Metrics.Net,
            (await service.GetAsync(new(Positions: "include"), default)).Metrics.Count));
        var scoped = await service.GetAsync(new(Portfolio: portfolio.Id.ToString(), Positions: "include"), default);
        Assert.Equal(("10", selected.Id), (scoped.Metrics.Net, Assert.Single(scoped.Plays).AccountId));
        foreach (var account in new[] { other, disabled, foreign.Accounts[0] })
        {
            var empty = await service.GetAsync(new(Portfolio: portfolio.Id.ToString(), AccountId: account.Id, Positions: "include"), default);
            Assert.Null(empty.Metrics.Net);
        }
        Assert.Equal("10", (await service.GetAsync(new(Portfolio: "unassigned", Positions: "include"), default)).Metrics.Net);
    }
}
