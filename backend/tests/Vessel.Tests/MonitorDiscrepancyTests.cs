using Vessel.Application.Common;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class MonitorDiscrepancyTests
{
    [Theory]
    [InlineData("protection.missing")]
    [InlineData("protection.cancelled")]
    [InlineData("protection.short")]
    [InlineData("protection.excess")]
    [InlineData("protection.beyond-liquidation")]
    [InlineData("protection.orphaned")]
    [InlineData("level.drift")]
    [InlineData("position.missing")]
    [InlineData("position.opposite")]
    [InlineData("position.short")]
    [InlineData("position.excess")]
    [InlineData("fill.unlinked")]
    public async Task Every_kind_requires_a_fresh_confirmed_candidate_and_emits_once(string kind)
    {
        var f = new DiscrepancyFixture();
        switch (kind)
        {
            case "protection.missing": f.VenueOrders.Clear(); break;
            case "protection.cancelled": f.Stop(o => o with { Status = VenueOrderStatus.Canceled }); break;
            case "protection.short": f.Stop(o => o with { RemainingSize = .5m }); break;
            case "protection.excess": f.Stop(o => o with { RemainingSize = 2m, ReduceOnly = false }); break;
            case "protection.beyond-liquidation": f.Liquidation = 95; break;
            case "protection.orphaned": f.Play.MarkClosed("fixture", f.Clock.Now.AddMinutes(-1)); break;
            case "level.drift": f.Stop(o => o with { TriggerPrice = 80m }); break;
            case "position.missing": f.Position = 0; break;
            case "position.opposite": f.Position = -1; break;
            case "position.short": f.Position = .5m; break;
            case "position.excess": f.Position = 2; break;
            case "fill.unlinked": f.Fill("unlinked", "other-order", .2m, f.Clock.Now.AddMinutes(-5)); break;
        }
        var first = await f.Evaluate();
        Assert.NotNull(first.NextConfirmationAtUtc);
        Assert.Empty(await f.Service.ReadAsync(f.Account.Id, null, default));
        f.Advance(59); await f.Evaluate();
        Assert.Empty(f.Events.Items);
        f.Advance(1); await f.Evaluate();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == kind);
        Assert.Equal("open", row.State);
        Assert.NotNull(row.ConfirmedAtUtc);
        var opened = Assert.Single(f.Events.Items, x => x.Type == "discrepancy.opened" && x.Subject.DiscrepancyId == row.Id);
        Assert.Equal(kind, opened.Data.GetProperty("kind").GetString());
        f.Advance(61); await f.Evaluate();
        Assert.Single(f.Events.Items, x => x.Subject.DiscrepancyId == row.Id);
    }

    [Fact]
    public async Task A_stop_beyond_liquidation_is_critical_and_a_drifting_cross_price_keeps_confirming()
    {
        // C12 / D-21: a long's stop at 90 sits below its venue liquidation price of 95, so it cannot act before liquidation.
        var f = new DiscrepancyFixture { Liquidation = 95 };
        await f.Evaluate();
        // Under cross margin the price moves with the account; the condition holds, so confirmation is not restarted.
        f.Advance(60); f.Liquidation = 95.5m; await f.Evaluate();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "protection.beyond-liquidation");
        Assert.Equal(("open", "critical"), (row.State, row.Severity));
        Assert.Equal("95.5", row.Details.GetProperty("liquidationPrice").GetString());
        Assert.Equal("cross", row.Details.GetProperty("marginMode").GetString());
        Assert.True(row.Details.GetProperty("stops")[0].GetProperty("beyond").GetBoolean());
    }

    [Fact]
    public async Task An_incomplete_book_never_resolves_a_stop_beyond_liquidation()
    {
        // Sol round 1 on #42: a fresh but incomplete book omitting the stop proved nothing, yet resolved the critical incident.
        var f = new DiscrepancyFixture { Liquidation = 95 };
        await f.Evaluate();
        f.Advance(60); await f.Evaluate();
        Assert.Equal("open", Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "protection.beyond-liquidation").State);
        f.OrdersComplete = false;
        f.VenueOrders.RemoveAll(o => o.OrderId == "stop");
        f.Advance(60); await f.Evaluate();
        Assert.Equal("open", Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "protection.beyond-liquidation").State);
        Assert.DoesNotContain(f.Events.Items, x => x.Type == "discrepancy.resolved");
    }

    [Fact]
    public async Task An_opposite_position_never_stands_for_the_plays_exposure()
    {
        // Sol round 1 on #42: a long Play's sell-stop at 90 was compared with a short position's liquidation at 105.
        var f = new DiscrepancyFixture { Liquidation = 105, Position = -1 };
        await f.Evaluate();
        f.Advance(60); await f.Evaluate();
        Assert.DoesNotContain(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "protection.beyond-liquidation");
    }

    [Theory]
    [InlineData(80, false)]   // the stop at 90 acts first
    [InlineData(null, true)]  // the venue reports no liquidation price: undecidable by design, never missing
    public async Task A_stop_inside_liquidation_or_no_reported_price_opens_nothing(int? liquidation, bool undecided)
    {
        var f = new DiscrepancyFixture { Liquidation = liquidation };
        var result = await f.Evaluate();
        f.Advance(60); await f.Evaluate();
        Assert.DoesNotContain(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "protection.beyond-liquidation");
        // Neither case degrades the run.
        Assert.False(result.Incomplete, undecided ? "an unreported price must not degrade the run" : "a decided check must not degrade the run");
    }

    [Fact]
    public async Task Read_records_carry_the_last_decisive_observation_time_in_the_api_json()
    {
        var f = new DiscrepancyFixture { Position = 0 };
        await f.Confirm();
        var observed = f.Clock.Now;
        f.Advance(90); f.PositionsComplete = false;
        // An incomplete read is not an observation: the time stays at the last fresh decision.
        await f.Evaluate();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "position.missing");
        Assert.Equal(observed, row.LastObservedAtUtc);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(row, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(observed, json.GetProperty("lastObservedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Bounded_lifetime_history_does_not_disable_safe_current_comparisons()
    {
        var f = new DiscrepancyFixture { Position = 2, Truncated = true };
        Assert.False((await f.Evaluate()).Incomplete);
        f.Advance(60); Assert.False((await f.Evaluate()).Incomplete);
        Assert.Contains(f.Store.Rows, x => x.Kind == "position.excess" && x.State == "open");
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("positions")]
    [InlineData("gap")]
    [InlineData("omitted")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("load-cap")]
    public async Task Unknown_or_stale_sources_neither_open_nor_resolve(string reason)
    {
        var f = new DiscrepancyFixture { Position = 0 };
        f.VenueOrders.Clear();
        switch (reason)
        {
            case "orders": f.OrdersComplete = false; break;
            case "positions": f.PositionsComplete = false; break;
            case "gap": f.HistoryGap = true; break;
            case "omitted": f.Omitted = 1; break;
            case "stale": f.ObservationOffset = TimeSpan.FromMinutes(-6); break;
            case "future": f.ObservationOffset = TimeSpan.FromMinutes(1); break;
            case "load-cap": f.Store.Complete = false; break;
        }
        Assert.True((await f.Evaluate()).Incomplete);
        f.Advance(60); await f.Evaluate();
        var suppressed = reason == "orders" ? "protection.missing" : "position.missing";
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == suppressed && x.State == "open");
    }

    [Fact]
    public async Task Partial_reads_break_confirmation_and_cannot_resolve_an_open_warning()
    {
        var f = new DiscrepancyFixture { Position = 0 };
        await f.Evaluate();
        f.Advance(60); f.PositionsComplete = false; await f.Evaluate();
        f.Advance(60); f.PositionsComplete = true; await f.Evaluate();
        Assert.Empty(f.Events.Items);
        f.Advance(60); await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "position.missing");
        f.Position = 1; f.PositionsComplete = false;
        f.Advance(60); await f.Evaluate(); f.Advance(60); await f.Evaluate();
        Assert.Equal("open", row.State);
        f.PositionsComplete = true;
        f.Advance(60); await f.Evaluate(); Assert.Equal("resolved", row.State);
        f.Advance(60); await f.Evaluate(); Assert.Equal("resolved", row.State);
        Assert.Single(f.Events.Items, x => x.Type == "discrepancy.resolved");
    }

    [Fact]
    public async Task Replaying_the_same_read_is_not_confirmation()
    {
        var f = new DiscrepancyFixture { Position = 0 };
        var read = f.Observation();
        await f.Service.EvaluateAsync(read, default);
        f.Advance(61);
        Assert.True((await f.Service.EvaluateAsync(read, default)).Incomplete);
        Assert.Empty(f.Events.Items);
        await f.Evaluate();
        Assert.Contains(f.Events.Items, x => x.Type == "discrepancy.opened");
    }

    [Fact]
    public async Task Missing_protection_waits_for_grace_then_a_separate_confirmation()
    {
        var f = new DiscrepancyFixture(); f.VenueOrders.Clear();
        f.Store.Fills[0].OccurredAtUtc = f.Clock.Now.AddMinutes(-9);
        var before = await f.Evaluate();
        Assert.Equal(f.Clock.Now.AddMinutes(1), before.NextConfirmationAtUtc);
        f.Advance(60); await f.Evaluate(); Assert.Empty(f.Events.Items);
        f.Advance(60); await f.Evaluate();
        Assert.Contains(f.Events.Items, x => x.Data.GetProperty("kind").GetString() == "protection.missing");
    }

    [Fact]
    public async Task Dismissal_is_owner_scoped_idempotent_and_not_revived_by_observation_timestamps()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "position.excess");
        var unrelated = f.AddPlay("long", 1); // Not part of the confirmed record's involved Play list.
        var error = await Assert.ThrowsAsync<ProblemException>(() => f.Service.DismissAsync(f.Account.Id, unrelated.Id, row.Id, null, default));
        Assert.Equal(404, error.StatusCode);
        f.Store.Plays.Remove(unrelated);
        await f.Service.DismissAsync(f.Account.Id, f.Play.Id, row.Id, " accepted ", default);
        await f.Service.DismissAsync(f.Account.Id, f.Play.Id, row.Id, "duplicate", default);
        Assert.Equal("accepted", row.Note);
        Assert.Single(f.Events.Items, x => x.Data.TryGetProperty("resolution", out var r) && r.GetString() == "dismissed");
        f.Advance(600); await f.Evaluate(); f.Advance(60); await f.Evaluate();
        Assert.Equal("dismissed", row.State);
        f.Position = 3;
        f.Advance(60); await f.Evaluate(); Assert.Equal("dismissed", row.State);
        f.Advance(60); await f.Evaluate(); Assert.Equal("open", row.State);
        Assert.Null(row.Note);
        var foreign = new MonitorDiscrepancyService(f.Store, TestUnitOfWork.None, new CoreOwner(Guid.NewGuid()), f.Events, new(), f.Clock);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => foreign.ReadAsync(f.Account.Id, null, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => f.Service.DismissAsync(f.Account.Id, null, row.Id, new string('a', 2001), default))).StatusCode);
    }

    [Fact]
    public async Task Dismissed_orphan_keeps_being_evaluated_and_rearms_only_when_order_changes()
    {
        var f = new DiscrepancyFixture(); f.Play.MarkClosed("fixture", f.Clock.Now.AddMinutes(-1));
        await f.Confirm(); var row = f.Store.Rows.Single(x => x.Kind == "protection.orphaned");
        await f.Service.DismissAsync(f.Account.Id, f.Play.Id, row.Id, null, default);
        f.Advance(60); await f.Evaluate(); f.Advance(60); await f.Evaluate();
        Assert.Equal("dismissed", row.State);
        f.Stop(o => o with { RemainingSize = 2 });
        f.Advance(60); await f.Confirm(); Assert.Equal("open", row.State);
        f.VenueOrders.Clear(); f.OrdersComplete = false;
        f.Advance(60); await f.Confirm(); Assert.Equal("open", row.State);
        f.OrdersComplete = true;
        f.Advance(60); await f.Confirm(); Assert.Equal("resolved", row.State);
    }

    [Theory]
    [InlineData("flip")]
    [InlineData("wrong-side")]
    [InlineData("missing-filled")]
    [InlineData("exit-first")]
    [InlineData("wrong-native")]
    [InlineData("shared-order")]
    public async Task P1_integrity_failures_never_become_expected_quantity(string problem)
    {
        var f = new DiscrepancyFixture { Position = 0 };
        switch (problem)
        {
            case "flip": f.Store.Fills[0].PositionEffect = ExecutionFacts.Flip; break;
            case "wrong-side": f.Store.Fills[0].Side = "sell"; break;
            case "missing-filled": f.Store.Orders[0].OriginalSize = 2; break;
            case "exit-first": f.Fill("bad-exit", "stop", .5m, f.Clock.Now.AddHours(-1), "sell", ExecutionFacts.Close); break;
            case "wrong-native": f.Store.Fills[0].VenueContractId = "different"; break;
            case "shared-order":
                var another = f.AddPlay("long", 1);
                f.Store.Links.Add(f.Link(another, "entry", OrderLinkRole.Entry, "e", null, OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now));
                break;
        }
        Assert.True((await f.Evaluate()).Incomplete);
        f.Advance(60); await f.Evaluate();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind.StartsWith("position.") && x.State == "open");
    }

    [Fact]
    public async Task Simultaneous_opposite_plays_net_exactly_and_quantities_use_instrument_precision()
    {
        var f = new DiscrepancyFixture { Position = 0 };
        var shortPlay = f.AddPlay("short", 1);
        await f.Confirm();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind.StartsWith("position.") && x.State == "open");
        f.Store.Fills.Single(x => x.ExternalOrderId == shortPlay.Id.ToString()).Quantity = .5m;
        f.Store.Orders.Single(x => x.ExternalOrderId == shortPlay.Id.ToString()).OriginalSize = .5m;
        f.Position = .50000001m;
        f.Advance(60); await f.Confirm();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind.StartsWith("position.") && x.State == "open");
        f.Position = .6m;
        f.Advance(60); await f.Confirm();
        var discrepancy = Assert.Single(await f.Service.ReadAsync(f.Account.Id, shortPlay.Id, default), x => x.Kind == "position.excess");
        Assert.Equal(2, discrepancy.Details.GetProperty("playIds").GetArrayLength());
        Assert.Equal("0.5", discrepancy.Details.GetProperty("expectedQuantity").GetString());
    }

    [Fact]
    public async Task Exact_sums_do_not_overflow_decimal_or_lose_tiny_quantities()
    {
        var f = new DiscrepancyFixture();
        f.Store.Fills[0].Quantity = decimal.MaxValue; f.Store.Orders[0].OriginalSize = decimal.MaxValue;
        f.AddPlay("long", decimal.MaxValue); f.Position = decimal.MaxValue;
        await f.Confirm();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "position.short");
        Assert.Equal("158456325028528675187087900670", row.Details.GetProperty("expectedQuantity").GetString());
    }

    [Fact]
    public async Task Whole_position_stops_never_blanket_cover_multiple_plays()
    {
        var f = new DiscrepancyFixture { Position = 2 }; f.AddPlay("long", 1);
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        // Shared coverage is undecidable by design: unknown, but not missing evidence.
        Assert.False((await f.Evaluate()).Incomplete);
        f.Advance(60); Assert.False((await f.Evaluate()).Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.PlayId == f.Play.Id && x.Kind is "protection.missing" or "protection.short" or "protection.excess");
    }

    [Theory]
    [InlineData("protection.missing")]
    [InlineData("protection.short")]
    [InlineData("protection.cancelled")]
    public async Task Whole_position_target_does_not_suppress_stop_warnings(string kind)
    {
        var f = new DiscrepancyFixture();
        if (kind == "protection.missing") f.VenueOrders.Clear();
        if (kind == "protection.short") f.Stop(o => o with { RemainingSize = .5m });
        if (kind == "protection.cancelled") f.Stop(o => o with { Status = VenueOrderStatus.Canceled });
        f.AddOrder("target", OrderLinkRole.Target, 0, 120);
        var index = f.VenueOrders.FindIndex(x => x.OrderId == "target");
        f.VenueOrders[index] = f.VenueOrders[index] with { IsPositionTpsl = true };
        // The only Open Play matches the venue position, so the whole-position target's coverage is established.
        Assert.False((await f.Evaluate()).Incomplete);
        f.Advance(60); await f.Evaluate();
        Assert.Contains(await f.Service.ReadAsync(f.Account.Id, f.Play.Id, default), x => x.Kind == kind && x.State == "open");
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.excess" && x.State == "open");
    }

    [Fact]
    public async Task Historical_open_status_is_not_a_current_resting_order()
    {
        var f = new DiscrepancyFixture { StampOpenOrders = false };
        f.Play.MarkClosed("fixture", f.Clock.Now.AddMinutes(-1));
        f.Stop(o => o with { StatusAtUtc = f.Clock.Now.AddHours(-1) });
        await f.Confirm();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.orphaned" && x.State == "open");
    }

    [Fact]
    public async Task Targets_may_scale_out_but_excess_and_tick_aware_drift_are_detected()
    {
        var f = new DiscrepancyFixture { PriceStep = 1m };
        f.AddOrder("target", OrderLinkRole.Target, .25m, 120.9m);
        await f.Confirm();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind is "protection.excess" or "level.drift" && x.State == "open");
        var index = f.VenueOrders.FindIndex(x => x.OrderId == "target");
        f.VenueOrders[index] = f.VenueOrders[index] with { RemainingSize = 2, TriggerPrice = 123, ReduceOnly = false };
        f.Advance(60); await f.Confirm();
        Assert.Contains(f.Store.Rows, x => x.Kind == "protection.excess" && x.State == "open");
        Assert.Contains(f.Store.Rows, x => x.Kind == "level.drift" && x.State == "open");
    }

    [Fact]
    public async Task Entry_price_change_rearms_dismissed_level_drift_but_timestamps_do_not()
    {
        var f = new DiscrepancyFixture();
        f.Store.Orders[0].Status = "open";
        f.Store.Orders[0].OriginalSize = 2;
        f.Store.Orders[0].RemainingSize = 1;
        f.VenueOrders.Add(new("entry", "BTC", "buy", "Limit", 105, null, false, false, 2, 1,
            f.Clock.Now.AddMinutes(-25), VenueOrderStatus.Open, "open", f.Clock.Now));
        await f.Confirm();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, f.Play.Id, default), x => x.Kind == "level.drift" && x.State == "open");
        await f.Service.DismissAsync(f.Account.Id, f.Play.Id, row.Id, null, default);
        f.Advance(60); await f.Confirm();
        Assert.Equal("dismissed", (await f.Service.ReadAsync(f.Account.Id, f.Play.Id, default)).Single(x => x.Id == row.Id).State);
        var index = f.VenueOrders.FindIndex(x => x.OrderId == "entry");
        f.VenueOrders[index] = f.VenueOrders[index] with { LimitPrice = 110 };
        f.Advance(60); await f.Evaluate();
        Assert.Equal("dismissed", (await f.Service.ReadAsync(f.Account.Id, f.Play.Id, default)).Single(x => x.Id == row.Id).State);
        f.Advance(60); await f.Evaluate();
        Assert.Equal("open", (await f.Service.ReadAsync(f.Account.Id, f.Play.Id, default)).Single(x => x.Id == row.Id).State);
    }

    [Fact]
    public async Task Unlinked_fill_is_not_erased_by_bounded_history_but_suggestion_resolves_it()
    {
        var f = new DiscrepancyFixture(); f.Fill("extra", "extra-order", 1, f.Clock.Now.AddMinutes(-2));
        await f.Confirm(); var row = f.Store.Rows.Single(x => x.Kind == "fill.unlinked");
        f.Advance(60); await f.Evaluate(); Assert.Equal("open", row.State);
        f.Store.Links.Add(f.Link(f.Play, "extra-order", OrderLinkRole.Entry, "e", null, OrderLinkState.Suggested, OrderLinkSource.Automatic, f.Clock.Now));
        f.Advance(60); await f.Confirm(); Assert.Equal("resolved", row.State);
    }

    [Fact]
    public async Task Incomplete_assignment_coverage_neither_confirms_nor_resolves_unlinked_fills()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        f.Fill("extra", "extra-order", 1, f.Clock.Now.AddMinutes(-2));
        f.Store.AssignmentLinksComplete = false;
        await f.Confirm();
        var rows = await f.Service.ReadAsync(f.Account.Id, null, default);
        Assert.DoesNotContain(rows, x => x.Kind == "fill.unlinked");
        Assert.Contains(rows, x => x.Kind == "position.excess" && x.State == "open");
        f.Store.AssignmentLinksComplete = true;
        f.Advance(60); await f.Confirm();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "fill.unlinked" && x.State == "open");
        f.Store.Links.Add(f.Link(f.Play, "extra-order", OrderLinkRole.Entry, "e", null,
            OrderLinkState.Suggested, OrderLinkSource.Automatic, f.Clock.Now));
        f.Store.AssignmentLinksComplete = false;
        f.Advance(60);
        Assert.True((await f.Evaluate()).Incomplete);
        Assert.Equal("open", (await f.Service.ReadAsync(f.Account.Id, null, default)).Single(x => x.Id == row.Id).State);
        f.Store.AssignmentLinksComplete = true;
        f.Advance(60); await f.Evaluate();
        Assert.Equal("resolved", (await f.Service.ReadAsync(f.Account.Id, null, default)).Single(x => x.Id == row.Id).State);
    }

    [Fact]
    public async Task Cancellation_does_not_resolve_when_it_falls_out_of_bounded_order_history()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { Status = VenueOrderStatus.Canceled });
        var stop = f.VenueOrders.Single();
        // The linked stop's retained row records the cancellation.
        var retained = f.Store.Orders.Single(x => x.ExternalOrderId == stop.OrderId && x.ContractId == stop.ContractId);
        (retained.Side, retained.OrderType, retained.Status, retained.VenueStatus) = (stop.Side, stop.OrderType, "canceled", "canceled");
        (retained.RemainingSize, retained.OriginalSize, retained.TriggerPrice, retained.LimitPrice) = (1, 1, 90, 90);
        (retained.StatusAtUtc, retained.ObservedAtUtc) = (stop.StatusAtUtc, stop.StatusAtUtc);
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.cancelled");
        f.VenueOrders.Clear();
        f.Advance(60); await f.Confirm();
        Assert.Equal("open", row.State);
        Assert.DoesNotContain(f.Events.Items, x => x.Type == "discrepancy.resolved" && x.Subject.DiscrepancyId == row.Id);
    }

    [Theory]
    [InlineData("protection.orphaned")]
    [InlineData("protection.excess")]
    [InlineData("level.drift")]
    public async Task Fresh_positive_evidence_is_usable_even_when_other_orders_may_be_missing(string kind)
    {
        var f = new DiscrepancyFixture { OrdersComplete = false };
        if (kind == "protection.orphaned") f.Play.MarkClosed("fixture", f.Clock.Now.AddMinutes(-1));
        if (kind == "protection.excess") f.Stop(o => o with { RemainingSize = 2, ReduceOnly = false });
        if (kind == "level.drift") f.Stop(o => o with { TriggerPrice = 80 });
        Assert.True((await f.Evaluate()).Incomplete);
        f.Advance(60); await f.Evaluate();
        var row = Assert.Single(f.Store.Rows, x => x.Kind == kind);
        Assert.Equal("open", row.State);
        f.VenueOrders.Clear(); f.Advance(60); await f.Confirm();
        Assert.Equal("open", row.State);
    }

    [Fact]
    public async Task Unknown_native_position_and_snapshot_before_latest_fill_cannot_prove_a_difference()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        var read = f.Observation();
        var wrong = read with { Read = read.Read with { Sync = read.Read.Sync with { Snapshot = read.Read.Sync.Snapshot with {
            Positions = [new("BTC", 2, 100, 0, 0, 1, "not-BTC")] } } } };
        Assert.True((await f.Service.EvaluateAsync(wrong, default)).Incomplete);
        f.Advance(60);
        f.Store.Fills[0].OccurredAtUtc = f.Clock.Now.AddSeconds(1);
        Assert.True((await f.Evaluate()).Incomplete);
        Assert.Empty(f.Events.Items);
    }

    [Fact]
    public async Task Changes_to_an_involved_execution_order_rearm_a_dismissed_position_warning()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "position.excess");
        await f.Service.DismissAsync(f.Account.Id, null, row.Id, null, default);
        f.Store.Links.RemoveAll(x => x.ImportedOrderId == f.Store.Orders[0].Id);
        f.Store.Fills[0].ExternalOrderId = "replacement";
        // A different order: a new internal ID with the replacement's venue ID.
        f.Store.Orders[0].Id = Guid.NewGuid();
        f.Store.Orders[0].ExternalOrderId = "replacement";
        f.Store.Links.Add(f.Link(f.Play, "replacement", OrderLinkRole.Entry, "e", null, OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now));
        f.Advance(60); await f.Confirm();
        Assert.Equal("open", row.State);
    }

    [Fact]
    public async Task A_fresh_complete_clearance_resolves_orphan_before_the_terminal_watch_stops()
    {
        var f = new DiscrepancyFixture();
        f.Play.MarkClosed("fixture", f.Clock.Now.AddMinutes(-1));
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.orphaned");
        f.VenueOrders.Clear(); f.Advance(60);
        await f.Evaluate();
        Assert.Equal("resolved", row.State);
        Assert.Single(f.Events.Items, x => x.Type == "discrepancy.resolved" && x.Subject.DiscrepancyId == row.Id);
    }

    [Fact]
    public async Task A_cancelled_stop_with_only_partial_replacement_still_warns()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { Status = VenueOrderStatus.Canceled });
        f.AddOrder("replacement-stop", OrderLinkRole.Stop, .5m, 90);
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.cancelled");
        Assert.Equal("open", row.State);
        var index = f.VenueOrders.FindIndex(x => x.OrderId == "replacement-stop");
        f.VenueOrders[index] = f.VenueOrders[index] with { RemainingSize = 1 };
        f.Advance(60); await f.Evaluate();
        Assert.Equal("resolved", row.State);
    }
}

internal sealed class DiscrepancyFixture
{
    public readonly DiscrepancyClock Clock = new();
    public readonly DiscrepancyMemoryStore Store = new();
    public readonly DiscrepancyEvents Events = new();
    public readonly Account Account;
    public readonly Play Play;
    public readonly MonitorDiscrepancyService Service;
    public readonly List<VenueOrder> VenueOrders = [];
    public decimal Position = 1;
    public decimal? PriceStep;
    /// <summary>The venue's reported liquidation price for the position (Phase 2, item 8b); null when it reports none.</summary>
    public decimal? Liquidation;
    public string? MarginMode = "cross";
    public bool PositionsComplete = true, OrdersComplete = true, HistoryGap, StampOpenOrders = true, Retired, CurrentOrderBook = true;
    public bool? Truncated;
    public int Omitted;
    public TimeSpan ObservationOffset;
    public DiscrepancyFixture()
    {
        Account = new(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Synthetic"); Store.Account = Account;
        Play = AddPlay("long", 1, "entry");
        AddOrder("stop", OrderLinkRole.Stop, 1, 90);
        Service = new(Store, TestUnitOfWork.None, new CoreOwner(Account.OwnerId), Events, new(), Clock);
    }
    public Play AddPlay(string direction, decimal quantity, string? entryOrder = null)
    {
        var plan = new PlayPlanDocument(direction, "quantity", "1", "1", null,
            [new("e", "Entry", "#ffffff", "100", "100", [new("s", "price", direction == "long" ? "90" : "110", "100")],
                [new("t", "price", direction == "long" ? "120" : "80", "100")])], new("", "", "", ""));
        var play = new Play(Guid.NewGuid(), Account, new("hyperliquid", "BTC"), InstrumentSource.Venue, PlayDocuments.Serialize(plan), Clock.Now.AddHours(-1));
        play.MarkPlanned(Clock.Now.AddMinutes(-30)); play.MarkOpen("fixture", Clock.Now.AddMinutes(-20)); Store.Plays.Add(play);
        entryOrder ??= play.Id.ToString();
        var side = direction == "long" ? "buy" : "sell";
        Fill(entryOrder + "-fill", entryOrder, quantity, Clock.Now.AddMinutes(-20), side);
        Store.Orders.Add(new() { Id = Guid.NewGuid(), OwnerId = Account.OwnerId, AccountId = Account.Id, ContractId = "BTC", ExternalOrderId = entryOrder,
            Side = side, OrderType = "Limit", OriginalSize = quantity, Status = "filled", VenueStatus = "filled", PlacedAtUtc = Clock.Now.AddMinutes(-25),
            StatusAtUtc = Clock.Now.AddMinutes(-20), ObservedAtUtc = Clock.Now.AddMinutes(-20), LimitPrice = 100 });
        Store.Links.Add(Link(play, entryOrder, OrderLinkRole.Entry, "e", null, OrderLinkState.Linked, OrderLinkSource.Owner, Clock.Now.AddMinutes(-25)));
        return play;
    }
    /// <summary>
    /// The retained order with this venue ID, created on the Play's contract if missing: links reference retained orders.
    /// A created order rests unfilled on the side its role implies.
    /// </summary>
    public ImportedOrder Retained(Play play, string id, OrderLinkRole role, decimal quantity = 1, decimal price = 100,
        string type = "Limit", bool reduceOnly = false)
    {
        var existing = Store.Orders.FirstOrDefault(o => o.AccountId == play.AccountId && o.ContractId == play.ContractId && o.ExternalOrderId == id);
        if (existing is not null) return existing;
        var entrySide = PlayDocuments.Read(play.Plan).Direction == "long" ? "buy" : "sell";
        var order = new ImportedOrder { Id = Guid.NewGuid(), OwnerId = play.OwnerId, AccountId = play.AccountId, ContractId = play.ContractId!,
            ExternalOrderId = id, Side = role == OrderLinkRole.Entry ? entrySide : entrySide == "buy" ? "sell" : "buy", OrderType = type,
            LimitPrice = price, TriggerPrice = type == "Limit" ? null : price, ReduceOnly = reduceOnly, OriginalSize = quantity, RemainingSize = quantity,
            Status = "open", VenueStatus = "open", PlacedAtUtc = Clock.Now.AddMinutes(-15), StatusAtUtc = Clock.Now.AddMinutes(-15),
            ObservedAtUtc = Clock.Now.AddMinutes(-15) };
        Store.Orders.Add(order);
        return order;
    }
    /// <summary>A link to the retained order with this venue ID (see <see cref="Retained"/>).</summary>
    public PlayOrderLink Link(Play play, string id, OrderLinkRole role, string? entryId, string? levelId, OrderLinkState state,
        OrderLinkSource source, DateTimeOffset at) =>
        new(play, Retained(play, id, role), role, entryId, levelId, state, source, at);
    public void AddOrder(string id, OrderLinkRole role, decimal quantity, decimal price)
    {
        Retained(Play, id, role, quantity, price, role == OrderLinkRole.Stop ? "Stop Market" : "Take Profit Market", true);
        Store.Links.Add(Link(Play, id, role, "e", role == OrderLinkRole.Stop ? "s" : "t", OrderLinkState.Linked, OrderLinkSource.Owner, Clock.Now.AddMinutes(-15)));
        VenueOrders.Add(new(id, "BTC", "sell", role == OrderLinkRole.Stop ? "Stop Market" : "Take Profit Market", price, price, true, false,
            quantity, quantity, Clock.Now.AddMinutes(-15), VenueOrderStatus.Open, "open", Clock.Now));
    }
    public void Fill(string id, string order, decimal quantity, DateTimeOffset at, string side = "buy", string effect = ExecutionFacts.Open) => Store.Fills.Add(new()
    {
        Id = Guid.NewGuid(), OwnerId = Account.OwnerId, AccountId = Account.Id, ContractId = "BTC", SourceFillId = id, ExternalOrderId = order,
        Side = side, Direction = side, PositionEffect = effect, Quantity = quantity, Price = 100, OccurredAtUtc = at, FeeToken = "USDC", TransactionHash = "synthetic"
    });
    public void Stop(Func<VenueOrder, VenueOrder> change) { var index = VenueOrders.FindIndex(x => x.OrderId == "stop"); VenueOrders[index] = change(VenueOrders[index]); }
    public void Advance(int seconds) => Clock.Now = Clock.Now.AddSeconds(seconds);
    public MonitorEvaluation Observation()
    {
        var at = Clock.Now + ObservationOffset;
        return new(Guid.NewGuid(), Account, new(new(new(at, "primary-perpetuals", 100, 100, 0,
            Position == 0 || Retired ? [] : [new("BTC", Position, 100, 0, 0, 1, LiquidationPrice: Liquidation, MarginMode: MarginMode)]), Retired ? [] : [new("BTC", 4, 10, "USDC", PriceStep)], [], "bounded",
            PositionsComplete, false, Truncated, OmittedExecutions: Omitted), new(VenueOrders.Select(x => StampOpenOrders && x.Status == VenueOrderStatus.Open ? x with { StatusAtUtc = at } : x).ToArray(), at, "bounded", OrdersComplete)),
            null, null, HistoryGap, "scheduled", CurrentOrderBook);
    }
    public Task<MonitorEvaluationResult> Evaluate() => Service.EvaluateAsync(Observation(), default);
    public async Task Confirm() { await Evaluate(); Advance(60); await Evaluate(); }
}
internal sealed class DiscrepancyClock : TimeProvider
{
    public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class DiscrepancyEvents : IVesselEventSink
{
    public List<VesselEventDraft> Items { get; } = [];
    public Task AppendAsync(VesselEventDraft draft, CancellationToken ct) { Items.Add(draft); return Task.CompletedTask; }
    public Task SetRunAsync(Guid runId, string trigger, CancellationToken ct) => Task.CompletedTask;
    public Task EnsureMonitoringBaselineAsync(Guid accountId, CancellationToken ct) => Task.CompletedTask;
}
internal sealed class DiscrepancyMemoryStore : IMonitorDiscrepancyStore
{
    public Account Account = null!;
    public List<Play> Plays = [];
    public List<PlayOrderLink> Links = [];
    public List<ImportedOrder> Orders = [];
    public List<ImportedFill> Fills = [];
    public List<PlayDiscrepancy> Rows = [];
    /// <summary>Owner-confirmed allocations the facts carry (Phase 2, item 11).</summary>
    public List<Vessel.Application.Plays.Execution.AllocatedFill> Allocations = [];
    public bool Complete = true, AssignmentLinksComplete = true;
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => Task.FromResult(id == Account.Id ? Account : null);
    public Task<Play?> PlayAsync(Guid id, CancellationToken ct) => Task.FromResult(Plays.SingleOrDefault(x => x.Id == id));
    public HashSet<string> TruncatedFillContracts = [];
    public DateTimeOffset? RecentSince;
    public Task<MonitorDiscrepancyFacts> LoadAsync(Guid id, DateTimeOffset recentSince, IReadOnlyCollection<OrderKey> resting, CancellationToken ct)
    {
        RecentSince = recentSince;
        return Task.FromResult(new MonitorDiscrepancyFacts(Plays, Links, Orders, Fills, Complete, Links, AssignmentLinksComplete, TruncatedFillContracts)
            { Allocations = Allocations });
    }
    public Task<IReadOnlyList<PlayDiscrepancy>> RecordsAsync(Guid id, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlayDiscrepancy>>(Rows.Where(x => x.AccountId == id).ToArray());
    public void Add(PlayDiscrepancy row) => Rows.Add(row);
    public Task LockAsync(Guid accountId, CancellationToken ct) => Task.CompletedTask;
}
