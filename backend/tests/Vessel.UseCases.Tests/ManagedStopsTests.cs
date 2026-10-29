using System.Globalization;
using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Phase 0, 0.5b (decision 2): a filled entry's stops may sit at or beyond it with a revision reason, planned risk is
/// signed and frozen at the first entry fill, and the review probe <c>More4.cs StopRevision</c> inverts.
/// </summary>
public sealed class ManagedStopTests
{
    private static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-10-19T10:00:00Z", CultureInfo.InvariantCulture);
    private readonly Guid owner = Guid.NewGuid();
    private readonly MemoryPlayStore store;
    private readonly PlayService service;
    private readonly Account venue;

    public ManagedStopTests()
    {
        store = new MemoryPlayStore(owner);
        venue = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        store.Accounts.Add(venue);
        service = new PlayService(store, new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.Saving(store.SaveAsync));
    }

    private static ImportedFill EntryFill(string side = ExecutionFacts.Buy, string effect = ExecutionFacts.Open, decimal price = 100) => new()
    {
        Id = Guid.NewGuid(), Price = price, Quantity = 1, Fee = 0, FeeToken = "USDC", PnlBasis = ExecutionFacts.PnlGross,
        OccurredAtUtc = Planned.AddMinutes(5), ExternalOrderId = "entry", ContractId = "BTC", SourceFillId = Guid.NewGuid().ToString(),
        Side = side, PositionEffect = effect, Direction = "", TransactionHash = "",
    };

    private static PlayPlanDocument WithStop(PlayPlanDocument plan, string unit, string value) =>
        plan with { Entries = [plan.Entries[0] with { Stops = [new PlanExit("stop-1", unit, value, "100")] }] };

    /// <summary>A planned long (entry 100, stop 95, target 110, 3×), opened when <paramref name="filled"/>.</summary>
    private Play Play(bool filled = true, PlayPlanDocument? plan = null)
    {
        var play = new Play(Guid.NewGuid(), venue, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan ?? TestPlays.Plan()), Planned);
        play.MarkPlanned(Planned);
        if (filled) play.MarkOpen("Real entry.", Planned.AddMinutes(5));
        store.Plays.Add(play);
        return play;
    }

    private UpdatePlayRequest Revise(Play play, PlayPlanDocument plan, string? reason = "Move stop after entry") =>
        new(play.Version, play.AccountId, play.ContractId, "venue", play.Title, plan, JsonDocument.Parse(play.Drawings).RootElement.Clone(),
            play.Review, reason);

    /// <summary>
    /// The review probe <c>verify-opus-dotnet/More4.cs StopRevision</c>: an Open long filled at 100 had a stop at 100 or
    /// 101 refused ("a long stop goes below the entry price"). Now each saves with its reason; a negative percentage
    /// stays refused, pointing to a price stop.
    /// </summary>
    [Theory]
    [InlineData("price", "100", "100")]
    [InlineData("price", "101", "101")]
    [InlineData("price", "105", "105")]
    [InlineData("percent", "0", "100")]
    [InlineData("percent", "10", "96.666667")]
    public async Task Review_probe_stop_revision_saves_a_stop_at_or_beyond_a_filled_entry_with_a_reason(string unit, string value, string resolved)
    {
        var play = Play();
        store.EntryExecutions.Add(new("entry-1", EntryFill()));
        var saved = await service.UpdateAsync(play.Id, Revise(play, WithStop(TestPlays.Plan(), unit, value)), default);
        Assert.Equal((2, unit, value), (saved.Summary.PlanRevision, saved.Plan.Entries[0].Stops[0].Unit, saved.Plan.Entries[0].Stops[0].Value));
        Assert.Equal(decimal.Parse(resolved, CultureInfo.InvariantCulture),
            Math.Round(ExecutionMatcher.Levels(saved.Plan).Single(l => l.Role == OrderLinkRole.Stop).Price!.Value, 6));
        Assert.Equal("Move stop after entry", store.Revisions.Last().Reason);
    }

    [Fact]
    public async Task A_beyond_entry_stop_needs_a_reason_and_negative_percentages_point_to_a_price()
    {
        var play = Play();
        store.EntryExecutions.Add(new("entry-1", EntryFill()));
        var missing = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.UpdateAsync(play.Id, Revise(play, WithStop(TestPlays.Plan(), "price", "105"), reason: " "), default));
        Assert.Contains("Say why the plan changed", missing.Message);
        var negative = await Assert.ThrowsAsync<ProblemException>(() =>
            service.UpdateAsync(play.Id, Revise(play, WithStop(TestPlays.Plan(), "percent", "-10")), default));
        Assert.Equal((400, "Entry 1 stop 1: use a price for a stop beyond the entry; a percentage cannot be negative."),
            (negative.StatusCode, negative.Message));
        Assert.Equal(PlayDocuments.Serialize(TestPlays.Plan()), play.Plan);
        Assert.Empty(store.Revisions);
    }

    [Fact]
    public async Task Unfilled_entries_keep_the_wrong_side_rule()
    {
        // Planned, a linked entry order that has not filled, and an entry whose only fill contradicts it (a sell close).
        var planned = Play(filled: false);
        store.FilledEntries.Add("entry-1");
        var refused = await Assert.ThrowsAsync<ProblemException>(() =>
            service.UpdateAsync(planned.Id, Revise(planned, WithStop(TestPlays.Plan(), "price", "105")), default));
        Assert.Equal((400, "Entry 1: a long stop goes below the entry price."), (refused.StatusCode, refused.Message));

        var contradicted = Play();
        store.EntryExecutions.Add(new("entry-1", EntryFill(ExecutionFacts.Sell, ExecutionFacts.Close)));
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.UpdateAsync(contradicted.Id, Revise(contradicted, WithStop(TestPlays.Plan(), "price", "100")), default))).StatusCode);

        // Only the filled entry's stops are free; another entry's stop and every target keep their side.
        var two = TestPlays.Plan() with
        {
            Entries = [TestPlays.Plan().Entries[0] with { Share = "50" },
                TestPlays.Plan().Entries[0] with { Id = "entry-2", Name = "Entry 2", Share = "50",
                    Stops = [new PlanExit("stop-2", "price", "95", "100")], Targets = [new PlanExit("target-2", "price", "110", "100")] }]
        };
        var mixed = Play(plan: two);
        store.EntryExecutions.Clear();
        store.EntryExecutions.Add(new("entry-1", EntryFill()));
        PlayPlanDocument Exits(string stop1, string stop2, string target1) => two with
        {
            Entries = [two.Entries[0] with { Stops = [new PlanExit("stop-1", "price", stop1, "100")], Targets = [new PlanExit("target-1", "price", target1, "100")] },
                two.Entries[1] with { Stops = [new PlanExit("stop-2", "price", stop2, "100")] }]
        };
        foreach (var (plan, message) in new[]
        {
            (Exits("105", "105", "110"), "Entry 2: a long stop goes below the entry price."),
            (Exits("105", "95", "95"), "Entry 1: a long target goes above the entry price."),
        })
        {
            var error = await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAsync(mixed.Id, Revise(mixed, plan), default));
            Assert.Equal((400, message), (error.StatusCode, error.Message));
        }
        Assert.Equal("105", (await service.UpdateAsync(mixed.Id, Revise(mixed, Exits("105", "95", "110")), default)).Plan.Entries[0].Stops[0].Value);
    }

    [Fact]
    public async Task Manual_entry_executions_and_shorts_free_their_stops_too()
    {
        var manual = Play();
        store.EntryExecutions.Add(new("entry-1", null));
        Assert.Equal("100", (await service.UpdateAsync(manual.Id, Revise(manual, WithStop(TestPlays.Plan(), "price", "100")), default))
            .Plan.Entries[0].Stops[0].Value);

        var shortPlan = TestPlays.Plan() with
        {
            Direction = "short",
            Entries = [TestPlays.Plan().Entries[0] with { Stops = [new PlanExit("stop-1", "price", "105", "100")], Targets = [new PlanExit("target-1", "price", "90", "100")] }]
        };
        var shortPlay = Play(plan: shortPlan);
        store.EntryExecutions.Clear();
        store.EntryExecutions.Add(new("entry-1", EntryFill(ExecutionFacts.Sell)));
        var moved = shortPlan with { Entries = [shortPlan.Entries[0] with { Stops = [new PlanExit("stop-1", "price", "95", "100")] }] };
        Assert.Equal("95", (await service.UpdateAsync(shortPlay.Id, Revise(shortPlay, moved), default)).Plan.Entries[0].Stops[0].Value);
    }

    /// <summary>A linked level still moves only through a rebind; the plain save keeps refusing it.</summary>
    [Fact]
    public async Task A_linked_stop_still_needs_a_rebind_or_an_unlink_to_move()
    {
        var play = Play();
        store.EntryExecutions.Add(new("entry-1", EntryFill()));
        var order = new ImportedOrder
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = venue.Id, ContractId = "BTC", ExternalOrderId = "linked-stop", Side = "sell",
            OrderType = "Stop Market", LimitPrice = 94, TriggerPrice = 95, ReduceOnly = true, OriginalSize = 1, RemainingSize = 1, Status = "open",
            VenueStatus = "open", PlacedAtUtc = Planned.AddMinutes(1), StatusAtUtc = Planned.AddMinutes(1),
        };
        store.Links.Add(new PlayOrderLink(play, order, OrderLinkRole.Stop, "entry-1", "stop-1", OrderLinkState.Linked, OrderLinkSource.Automatic, Planned));
        store.ExternalOrderIds[order.Id] = order.ExternalOrderId;
        var refused = await Assert.ThrowsAsync<ProblemException>(() =>
            service.UpdateAsync(play.Id, Revise(play, WithStop(TestPlays.Plan(), "price", "105")), default));
        Assert.Equal(409, refused.StatusCode);
        Assert.Contains("Order linked-stop still references this plan level", refused.Message);
    }

    private static PlayPlanDocument Stops(string direction, params (string Price, string Share)[] stops) => TestPlays.Plan() with
    {
        Direction = direction,
        Entries = [TestPlays.Plan().Entries[0] with
        {
            Stops = stops.Select((s, i) => new PlanExit($"stop-{i}", "price", s.Price, s.Share)).ToList(),
            Targets = [new PlanExit("target-1", "price", direction == "long" ? "110" : "90", "100")]
        }]
    };

    /// <summary>The calculator used the absolute distance: a long stop at 105 on an entry at 100 had 5% "risk".</summary>
    [Theory]
    [InlineData("long", "105", null)]
    [InlineData("long", "100", null)]
    [InlineData("long", "95", "0.05")]
    [InlineData("short", "95", null)]
    [InlineData("short", "105", "0.05")]
    public void A_stop_that_is_not_on_the_loss_side_yields_no_planned_risk(string direction, string stop, string? distance) =>
        Assert.Equal(distance is null ? null : decimal.Parse(distance, CultureInfo.InvariantCulture),
            PlayOutcomeCalculator.StopDistance(Stops(direction, (stop, "100"))));

    /// <summary>A stop that is not set is no stop, not break-even: its share is left out, and alone it gives no planned risk.</summary>
    [Fact]
    public void A_stop_that_is_not_set_is_skipped_not_break_even()
    {
        Assert.Equal(0.05m, PlayOutcomeCalculator.StopDistance(Stops("long", ("95", "50"), ("", "50"))));
        Assert.Null(PlayOutcomeCalculator.StopDistance(Stops("long", ("", "100"))));
        var percent = Stops("long", ("95", "100")) with
        {
            Entries = [Stops("long", ("95", "100")).Entries[0] with { Stops = [new PlanExit("stop-0", "percent", "", "100")] }]
        };
        Assert.Null(PlayOutcomeCalculator.StopDistance(percent));
    }

    [Fact]
    public void Locked_in_stops_offset_the_loss_of_the_others()
    {
        Assert.Equal(0.025m, PlayOutcomeCalculator.StopDistance(Stops("long", ("90", "50"), ("105", "50"))));
        Assert.Null(PlayOutcomeCalculator.StopDistance(Stops("long", ("95", "50"), ("105", "50"))));
    }

    private static ImportedFill Fill(string side, decimal price, decimal pnl, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), Price = price, Quantity = 1, ClosedPnlUsd = pnl, Fee = 0, FeeToken = "USDC", PnlBasis = ExecutionFacts.PnlGross,
        OccurredAtUtc = at, ExternalOrderId = "o" + at.Ticks, ContractId = "BTC", SourceFillId = "f" + at.Ticks, Side = side,
        PositionEffect = side == ExecutionFacts.Buy ? ExecutionFacts.Open : ExecutionFacts.Close, Direction = "", TransactionHash = "",
    };

    /// <summary>
    /// Initial risk and R use the revision in force at the first entry fill: the stop moved to 105 after the fill never
    /// changes them. A revision that is already beyond the entry at the first fill (a corrected manual time) gives no risk.
    /// </summary>
    [Theory]
    [InlineData(30, "5", "2")]
    [InlineData(150, null, null)]
    public void Initial_risk_and_r_stay_frozen_at_the_first_entry_fill(int entryMinutes, string? risk, string? r)
    {
        var play = new Play(Guid.NewGuid(), venue, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(TestPlays.Plan()), Planned);
        List<PlayPlanRevision> revisions = [play.MarkPlanned(Planned).Revision];
        play.MarkOpen("Filled.", Planned.AddMinutes(60));
        revisions.Add(play.ChangePlan(PlayDocuments.Serialize(WithStop(TestPlays.Plan(), "price", "105")), "Lock in profit", Planned.AddMinutes(120))!);
        play.MarkClosed("Exited.", Planned.AddDays(1));
        var facts = new ClosedPlayFacts(play, revisions, [new(OrderLinkRole.Entry, Fill(ExecutionFacts.Buy, 100, 0, Planned.AddMinutes(entryMinutes))),
            new(OrderLinkRole.Target, Fill(ExecutionFacts.Sell, 110, 10, Planned.AddHours(20)))]);
        var outcome = PlayOutcomeCalculator.Outcome(facts)!;
        Assert.Equal(10m, outcome.NetResult);
        Assert.Equal(risk is null ? null : decimal.Parse(risk, CultureInfo.InvariantCulture), outcome.PlannedRisk);
        Assert.Equal(r is null ? null : decimal.Parse(r, CultureInfo.InvariantCulture), outcome.RMultiple);
    }
}
