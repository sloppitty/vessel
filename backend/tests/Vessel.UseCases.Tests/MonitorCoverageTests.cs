using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>
/// Phase 0 item 0.4c, coverage and evidence (review defects D-09, D-18 and the chart part of D-08): the monitor records each
/// Open Play's stop coverage (known, shared, unknown or unsupported) with its check time, reason and per-level evidence.
/// Structural unknowns never become missing and never degrade the run; a venue whose order reader does not return the
/// current book is labelled "current book not read" and its book-dependent checks do not run.
/// </summary>
public sealed class MonitorCoverageTests
{

    [Fact]
    public async Task Sized_stop_orders_give_known_coverage_with_dated_resting_evidence_per_level()
    {
        var f = new DiscrepancyFixture();
        var coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((f.Play.Id, "BTC", MonitorCoverageStates.Known, (bool?)true, "stop-orders"),
            (coverage.PlayId, coverage.ContractId, coverage.State, coverage.Covered, coverage.Reason));
        var stop = Assert.Single(coverage.Stops);
        Assert.Equal(("e", "s", MonitorStopEvidence.Resting, "stop", (DateTimeOffset?)f.Clock.Now),
            (stop.EntryId, stop.LevelId, stop.Evidence, stop.OrderId, stop.ObservedAtUtc));

        // A complete, fresh book without the stop: known not covered, and the level was not seen at the venue.
        f.VenueOrders.Clear(); f.Advance(60);
        coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((MonitorCoverageStates.Known, (bool?)false, "no-stop"), (coverage.State, coverage.Covered, coverage.Reason));
        Assert.Equal(MonitorStopEvidence.NotSeen, Assert.Single(coverage.Stops).Evidence);

        // An incomplete book proves nothing either way.
        f.OrdersComplete = false; f.Advance(60);
        coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((MonitorCoverageStates.Unknown, (bool?)null, "incomplete-book"), (coverage.State, coverage.Covered, coverage.Reason));
        Assert.Equal(MonitorStopEvidence.Unknown, Assert.Single(coverage.Stops).Evidence);
    }

    [Fact] // D-18: structural unknowns were shown as "Monitored"; they must read unknown, never missing or degraded.
    public async Task Opposite_plays_sharing_whole_position_stops_are_unknown_never_missing()
    {
        var f = new DiscrepancyFixture { Position = 1 }; // long 1 + long 1 − short 1
        var sibling = f.AddPlay("long", 1);
        var opposite = f.AddPlay("short", 1);
        // The first long's whole-position sell stop, and the short's own whole-position buy stop.
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        WholePositionStop(f, opposite, "short-stop", "buy", 110);
        await f.Evaluate(); f.Advance(60);
        var result = await f.Evaluate();

        Assert.False(result.Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind is "protection.missing" or "protection.short" or "protection.cancelled");
        var coverage = result.Coverage!.ToDictionary(x => x.PlayId);
        Assert.Equal(new[] { f.Play.Id, sibling.Id, opposite.Id }.Order(), coverage.Keys.Order());
        Assert.All(coverage.Values, x => Assert.Equal((MonitorCoverageStates.Unknown, (bool?)null, "opposing-plays"), (x.State, x.Covered, x.Reason)));
    }

    [Fact]
    public async Task One_whole_position_stop_covering_same_direction_siblings_is_shared()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        var sibling = f.AddPlay("long", 1);
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        await f.Evaluate(); f.Advance(60);
        var result = await f.Evaluate();

        Assert.False(result.Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.missing");
        var coverage = result.Coverage!.ToDictionary(x => x.PlayId);
        Assert.All([coverage[f.Play.Id], coverage[sibling.Id]], x =>
            Assert.Equal((MonitorCoverageStates.Shared, (bool?)null, "shared-whole-position-stop"), (x.State, x.Covered, x.Reason)));
        // The owner's own planned stop rests; the sibling's planned stop is not at the venue (the shared one covers it).
        Assert.Equal(MonitorStopEvidence.Resting, Assert.Single(coverage[f.Play.Id].Stops).Evidence);
        Assert.Equal(MonitorStopEvidence.NotSeen, Assert.Single(coverage[sibling.Id].Stops).Evidence);
    }

    [Fact] // D-09 at the evaluator: without the capability nothing book-dependent is decided, and that alone is not incomplete.
    public async Task Without_a_current_book_protection_is_unsupported_and_book_checks_do_not_run()
    {
        // RISEx-like: history rows only (never stamped with the read time), never a complete book; the stop was cancelled.
        var f = new DiscrepancyFixture { OrdersComplete = false, StampOpenOrders = false };
        f.Stop(o => o with { Status = VenueOrderStatus.Canceled, VenueStatus = "ORDER_STATUS_CANCELLED" });
        f.CurrentOrderBook = false;
        var first = await f.Evaluate();
        f.Advance(60);
        var second = await f.Evaluate();

        Assert.False(first.Incomplete);
        Assert.False(second.Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind.StartsWith("protection.", StringComparison.Ordinal) ||
            x.Kind is "entry.orphaned" or "level.drift");
        var coverage = Assert.Single(second.Coverage!);
        Assert.Equal((MonitorCoverageStates.Unsupported, (bool?)null, "current-book-not-read"), (coverage.State, coverage.Covered, coverage.Reason));
        Assert.Equal(MonitorStopEvidence.NotRead, Assert.Single(coverage.Stops).Evidence);
    }

    [Fact] // Review: per-level evidence counts only what coverage counts as a closing stop.
    public async Task A_resting_linked_order_that_is_not_a_recognized_closing_stop_never_confirms_its_level()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { OrderType = "Trigger Market" }); // a trigger whose mechanics are not recognized
        var coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((MonitorCoverageStates.Unknown, "mechanics"), (coverage.State, coverage.Reason));
        var stop = Assert.Single(coverage.Stops);
        Assert.Equal((MonitorStopEvidence.Unknown, "stop", (DateTimeOffset?)null), (stop.Evidence, stop.OrderId, stop.ObservedAtUtc));

        // A plain limit linked as the stop rests, but no stop protects the position: not confirmed either.
        f.Stop(o => o with { OrderType = "Limit", TriggerPrice = null }); f.Advance(60);
        coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((MonitorCoverageStates.Known, (bool?)false, "no-stop"), (coverage.State, coverage.Covered, coverage.Reason));
        Assert.Equal(MonitorStopEvidence.Unknown, Assert.Single(coverage.Stops).Evidence);

        // A buy stop does not close a long: same answer.
        f.Stop(o => o with { OrderType = "Stop Market", TriggerPrice = 90m, Side = "buy" }); f.Advance(60);
        coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal(MonitorStopEvidence.Unknown, Assert.Single(coverage.Stops).Evidence);
    }

    [Fact] // Review: a stop resting away from its planned price protects, but does not confirm the planned price.
    public async Task A_stop_resting_away_from_its_planned_price_is_said_with_its_price()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { TriggerPrice = 85m, LimitPrice = 85m });
        var coverage = Assert.Single((await f.Evaluate()).Coverage!);
        Assert.Equal((MonitorCoverageStates.Known, (bool?)true, "stop-orders"), (coverage.State, coverage.Covered, coverage.Reason));
        var stop = Assert.Single(coverage.Stops);
        Assert.Equal((MonitorStopEvidence.RestingElsewhere, "stop", "85", "90", (DateTimeOffset?)f.Clock.Now),
            (stop.Evidence, stop.OrderId, stop.Price, stop.PlannedPrice, stop.ObservedAtUtc));

        // Within the matcher's step of the planned price it confirms the level, with both prices.
        f.Stop(o => o with { TriggerPrice = 90.0004m, LimitPrice = 90.0004m }); f.Advance(60);
        stop = Assert.Single(Assert.Single((await f.Evaluate()).Coverage!).Stops);
        Assert.Equal((MonitorStopEvidence.Resting, "90.0004", "90"), (stop.Evidence, stop.Price, stop.PlannedPrice));
    }

    [Fact] // Review: missing evidence comes before a structural unknown, and each order-read failure is named.
    public async Task Missing_evidence_reasons_come_first_and_name_the_order_read_failure()
    {
        // Opposing Plays without a complete position read: the position is unknown, not the opposing structure.
        var f = new DiscrepancyFixture { Position = 1, PositionsComplete = false };
        var sibling = f.AddPlay("long", 1);
        f.AddPlay("short", 1);
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        Assert.Equal("position-unknown", (await f.Evaluate()).Coverage!.Single(x => x.PlayId == sibling.Id).Reason);
        f.PositionsComplete = true; f.Advance(60);
        Assert.Equal("opposing-plays", (await f.Evaluate()).Coverage!.Single(x => x.PlayId == sibling.Id).Reason);

        async Task<string> Reason(Action<DiscrepancyFixture> change)
        {
            var g = new DiscrepancyFixture();
            change(g);
            return Assert.Single((await g.Evaluate()).Coverage!).Reason;
        }
        Assert.Equal("incomplete-book", await Reason(g => g.OrdersComplete = false));
        // An order read older than the freshness window is stale, not incomplete.
        Assert.Equal("stale-order-read", await Reason(g => g.ObservationOffset = -MonitorOptions.Lease - TimeSpan.FromMinutes(1)));
        // The same venue order twice in one read: the read cannot be trusted, though it claims completeness.
        Assert.Equal("order-read-unreliable", await Reason(g => g.VenueOrders.Add(g.VenueOrders[0])));
    }

    /// <summary>A resting whole-position stop linked to <paramref name="play"/>'s first stop level.</summary>
    internal static void WholePositionStop(DiscrepancyFixture f, Play play, string id, string side, decimal price)
    {
        f.Retained(play, id, OrderLinkRole.Stop, 0, price, "Stop Market", true);
        f.Store.Links.Add(f.Link(play, id, OrderLinkRole.Stop, "e", "s", OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now.AddMinutes(-15)));
        f.VenueOrders.Add(new(id, "BTC", side, "Stop Market", price, price, true, true, 0, 0, f.Clock.Now.AddMinutes(-15),
            VenueOrderStatus.Open, "open", f.Clock.Now));
    }
}
