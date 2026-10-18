using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Entry notional is the exact sum of quantity × price products; one that is not exactly representable excludes the
/// Play, never rounding the return first.
/// </summary>
public sealed class PlayOutcomeNotionalPrecisionTests
{
    private const decimal Unit = 0.0000000000000000000000000001m;
    private const decimal Tiny = 0.00000000000001m;
    private readonly ReviewFixture review = new();
    private readonly MemorySizingStore sizing = new();

    /// <summary>A closed long: one entry and one exit of <paramref name="quantity"/>, with a plan stop at <paramref name="stop"/> under a 100 entry.</summary>
    private ClosedPlayFacts Closed(decimal quantity, decimal price, decimal net, string? stop = "50")
    {
        var account = review.Accounts.FirstOrDefault() ?? review.Account();
        var at = ReviewClock.Now.AddHours(-1);
        var plan = new PlayPlanDocument("long", "margin", "100", "1", null,
            [new PlanEntry("e", "Entry", "#ffffff", "100", "100", stop is null ? [] : [new PlanExit("s", "price", stop, "100")], [])],
            new PlanNotes("", "", "", ""));
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument(account.VenueId, "BTC"),
            InstrumentSource.Venue, PlayDocuments.Serialize(plan), at.AddDays(-2));
        var revision = play.MarkPlanned(at.AddDays(-2)).Revision;
        play.MarkOpen("filled", at.AddDays(-1));
        play.MarkClosed("synced later", ReviewClock.Now);
        ImportedFill Fill(bool opening) => new()
        {
            Id = Guid.NewGuid(), OwnerId = review.Owner, AccountId = account.Id, ContractId = "BTC",
            SourceFillId = Guid.NewGuid().ToString(), ExternalOrderId = Guid.NewGuid().ToString(), TransactionHash = "",
            Side = opening ? ExecutionFacts.Buy : ExecutionFacts.Sell, Direction = "",
            PositionEffect = opening ? ExecutionFacts.Open : ExecutionFacts.Close,
            Quantity = quantity, Price = price, ClosedPnlUsd = opening ? 0 : net, Fee = 0, FeeToken = "USDC",
            OccurredAtUtc = opening ? at.AddDays(-1) : at,
        };
        var facts = new ClosedPlayFacts(play, [revision],
            [new LinkedFill(OrderLinkRole.Entry, Fill(true)), new LinkedFill(OrderLinkRole.Exit, Fill(false))]);
        review.Closed.Add(facts);
        sizing.Closed.Add(facts);
        return facts;
    }

    [Fact]
    public void An_entry_notional_beyond_exact_precision_is_excluded_not_rounded()
    {
        // 1e-14 × 1.5e-14 = 1.5e-28: decimal multiplication rounds it to 2e-28 and scored a 50% return, not the exact 66.67%.
        var facts = Closed(Tiny, 1.5m * Tiny, Unit);

        Assert.Null(PlayOutcomeCalculator.Outcome(facts, out var problem));
        Assert.Contains("entry notional", problem, StringComparison.Ordinal);
        Assert.Contains("exact precision", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tiny_exactly_representable_entry_notional_still_scores()
    {
        // 1e-14 × 2e-14 = 2e-28 exactly; the 50% stop distance gives a 1e-28 planned risk, also exact.
        var outcome = PlayOutcomeCalculator.Outcome(Closed(Tiny, 2 * Tiny, Unit), out var problem)!;

        Assert.Null(problem);
        Assert.Equal((2 * Unit, Unit, Unit), (outcome.EntryNotional, outcome.NetResult, outcome.PlannedRisk));
        Assert.Equal((50m, 1m), (outcome.ReturnPercent, outcome.RMultiple));
    }

    [Fact]
    public async Task Review_excludes_an_unrepresentable_entry_notional_with_the_arithmetic_reason()
    {
        Closed(Tiny, 1.5m * Tiny, Unit);
        Closed(Tiny, 2 * Tiny, Unit);

        var report = await review.Service.GetAsync(new("all"), default);
        Assert.Equal((1, 1), (report.Metrics.Count, report.Coverage.IncompleteExecutions));
        Assert.Equal("50", Assert.Single(report.Plays).ReturnPercent);
        var excluded = await review.Service.ExclusionsAsync(new("all"), default);
        Assert.Contains(Assert.Single(excluded.Items).Reasons, r => r.Contains("entry notional", StringComparison.Ordinal) &&
            r.Contains("exact precision", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sizing_leaves_an_unrepresentable_entry_notional_out_of_the_record()
    {
        Closed(Tiny, 1.5m * Tiny, Unit);
        var service = new SizingService(sizing, new CoreOwner(review.Owner), TimeProvider.System, TestUnitOfWork.None);

        var excluded = await service.GetAsync(default);
        Assert.Equal(0, excluded.Record.ClosedPlays);
        Assert.Empty(excluded.Record.Recent);

        Closed(Tiny, 2 * Tiny, Unit);
        var scored = await service.GetAsync(default);
        Assert.Equal((1, 1), (scored.Record.ClosedPlays, scored.Record.Wins));
        Assert.Equal("50", Assert.Single(scored.Record.Recent).ReturnPercent);
    }
}
