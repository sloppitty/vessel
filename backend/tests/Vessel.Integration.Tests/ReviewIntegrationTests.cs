using Microsoft.EntityFrameworkCore;
using Vessel.Application.Review;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class ReviewIntegrationTests : ReviewTestsSupport
{

    [PostgresFact]
    public async Task Store_is_owner_scoped_joins_real_links_and_counts_only_unassigned_scoped_fills()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var foreign = new ReviewFixture();
        var owned = f.Add(10);
        var other = foreign.Add(99);
        var disabledAccount = f.Account("Disabled");
        var disabled = f.Add(30, account: disabledAccount);
        disabledAccount.UpdateSettings("Disabled", null, false);
        foreach (var fixture in new[] { f, foreign })
        {
            await using var db = database.Context(fixture.Owner);
            db.Accounts.AddRange(fixture.Accounts);
            foreach (var facts in fixture.Closed)
            {
                db.Plays.Add(facts.Play);
                db.PlanRevisions.AddRange(facts.Revisions);
                foreach (var linked in facts.Fills)
                {
                    db.Fills.Add(linked.Fill);
                    // Links reference retained orders; each order here executed exactly its one fill.
                    var order = TestOrders.For(facts.Play, linked.Fill.ExternalOrderId, linked.Fill.Side, size: linked.Fill.Quantity);
                    db.Orders.Add(order);
                    db.OrderLinks.Add(new PlayOrderLink(facts.Play, order, linked.Role,
                        linked.Role == OrderLinkRole.Entry ? "e" : null, null, OrderLinkState.Linked, OrderLinkSource.Owner, ReviewClock.Now));
                }
            }
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(f.Owner))
        {
            db.Fills.Add(new ImportedFill
            {
                Id = Guid.NewGuid(), OwnerId = f.Owner, AccountId = owned.Play.AccountId, ContractId = "ETH",
                SourceFillId = "unlinked", ExternalOrderId = "unlinked", Side = ExecutionFacts.Buy, Direction = "",
                Quantity = 1, Price = 100, FeeToken = "USDC", TransactionHash = "", OccurredAtUtc = ReviewClock.Now,
            });
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(f.Owner))
        {
            var service = new ReviewService(new ReviewStore(db, new SizingStore(db, TestUnitOfWork.For(db))), new ReviewClock());
            var report = await service.GetAsync(new(), default);
            Assert.Equal(("10", 1, 1), (report.Metrics.Net, report.Options.Accounts.Count, report.Coverage.UnassignedFills));
            Assert.Equal(0, (await service.GetAsync(new(Instrument: "BTC"), default)).Coverage.UnassignedFills);
            Assert.Null((await service.GetAsync(new(AccountId: other.Play.AccountId), default)).Metrics.Net);
            Assert.DoesNotContain(report.Plays, p => p.Id == disabled.Play.Id);
        }
        await using (var db = database.Context(foreign.Owner))
        {
            var report = await new ReviewService(new ReviewStore(db, new SizingStore(db, TestUnitOfWork.For(db))), new ReviewClock()).GetAsync(new(), default);
            Assert.Equal(("99", 0), (report.Metrics.Net, report.Coverage.UnassignedFills));
        }
        await using (var db = database.Context(f.Owner))
        {
            ImportedOrder Order(string id, decimal size) => new()
            {
                Id = Guid.NewGuid(), OwnerId = f.Owner, AccountId = owned.Play.AccountId, ContractId = "BTC",
                ExternalOrderId = id, Side = ExecutionFacts.Buy, OrderType = "Limit", LimitPrice = 100,
                OriginalSize = size, RemainingSize = 0, PlacedAtUtc = ReviewClock.Now.AddDays(-2),
                Status = "filled", VenueStatus = "filled", StatusAtUtc = ReviewClock.Now, ObservedAtUtc = ReviewClock.Now,
            };
            var known = await db.Orders.SingleAsync(o => o.AccountId == owned.Play.AccountId && o.ExternalOrderId == owned.Fills[0].Fill.ExternalOrderId);
            known.OriginalSize = 2;
            await db.SaveChangesAsync();
            var service = new ReviewService(new ReviewStore(db, new SizingStore(db, TestUnitOfWork.For(db))), new ReviewClock());
            var partial = await service.GetAsync(new(), default);
            Assert.Equal((0, 1), (partial.Metrics.Count, partial.Coverage.IncompleteExecutions));
            known.OriginalSize = 1;
            var missing = Order("executed-with-no-retained-fills", 1);
            db.Orders.Add(missing);
            db.OrderLinks.Add(new PlayOrderLink(owned.Play, missing, OrderLinkRole.Entry, "e", null,
                OrderLinkState.Linked, OrderLinkSource.Owner, ReviewClock.Now));
            await db.SaveChangesAsync();
            var absent = await service.GetAsync(new(), default);
            Assert.Equal((0, 1), (absent.Metrics.Count, absent.Coverage.IncompleteExecutions));
            // Zero remaining does not establish a fill for rejected/cancelled/triggered orders.
            foreach (var status in new[] { "rejected", "canceled", "triggered", "other", "open" })
            {
                missing.Status = status;
                await db.SaveChangesAsync();
                Assert.Equal(1, (await service.GetAsync(new(), default)).Metrics.Count);
            }
            missing.Status = "filled"; missing.IsPositionTpsl = true;
            await db.SaveChangesAsync();
            Assert.Equal(1, (await service.GetAsync(new(), default)).Metrics.Count);
        }
    }
}
