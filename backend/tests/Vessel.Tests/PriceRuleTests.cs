using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 14b (F-11, probe C9): Hyperliquid prices take at most five significant figures, any integer, and at most
/// 6 − szDecimals decimals. A planned 0.051234 at szDecimals = 1 can only be placed as 0.05123, and now matches it.
/// </summary>
public sealed class PriceRuleTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-01T12:00:00Z");

    [Fact]
    public void The_minimum_step_is_the_tick_or_the_last_allowed_decimal()
    {
        Assert.Equal(0.00001m, PriceRules.MinimumStep(new VenueInstrument("KPEPE", 1, 10, "USDC", MaxPriceDecimals: 5)));
        Assert.Equal(1m, PriceRules.MinimumStep(new VenueInstrument("BTC", 6, 40, "USDC", MaxPriceDecimals: 0)));
        Assert.Equal(0.5m, PriceRules.MinimumStep(new VenueInstrument("ETH", 3, 20, "USDC", PriceStep: 0.5m)));
        Assert.Null(PriceRules.MinimumStep(new VenueInstrument("SOL", 2, 20, "USDC")));
        // Before the fix the fifth significant figure alone (0.000001 here) refused the only price the venue accepts.
        Assert.False(ExecutionMatcher.Near(0.05123m, 0.051234m));
        Assert.True(ExecutionMatcher.Near(0.05123m, 0.051234m, PriceRules.DecimalStep(5)));
        Assert.False(ExecutionMatcher.Near(0.05125m, 0.051234m, PriceRules.DecimalStep(5)));
        // Coarse prices keep today's fifth-figure step: nothing becomes stricter.
        Assert.True(ExecutionMatcher.Near(84542m, 84541m, PriceRules.DecimalStep(1)));
    }

    [PostgresFact]
    public async Task An_order_at_the_venue_rounded_price_links_to_its_level()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Trading");
        db.Accounts.Add(account);
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan(price: "0.051234", stop: "0.04")), Created);
        var (change, revision) = play.MarkPlanned(Created.AddMinutes(1));
        db.Plays.Add(play); db.Add(change); db.Add(revision);
        db.Orders.Add(new ImportedOrder
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ExternalOrderId = "venue-rounded", ContractId = "BTC", Side = "buy",
            OrderType = "Limit", LimitPrice = 0.05123m, OriginalSize = 1, RemainingSize = 1, PlacedAtUtc = Created.AddMinutes(2),
            Status = "open", VenueStatus = "open", StatusAtUtc = Created.AddMinutes(2), ObservedAtUtc = Created.AddMinutes(2),
        });
        await db.SaveChangesAsync();
        var service = new PlayExecutionService(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)),
            TestVenues.With(), TimeProvider.System, TestUnitOfWork.For(db));
        await TestUnitOfWork.For(db).ExecuteAsync(async () =>
        {
            await service.ReconcileImportedAsync(account, new VenueOrderReadResult([], Created.AddMinutes(5), "test"),
                [new VenueInstrument("BTC", 1, 20, "USDC", MaxPriceDecimals: 5)], default);
            return true;
        }, default);
        var execution = await service.GetAsync(play.Id, default);
        Assert.Equal("venue-rounded", Assert.Single(execution.Links).Order!.OrderId);
    }
}
