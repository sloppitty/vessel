using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 6c: on an account that keeps its execution history, an open position with no Open Play and no resting
/// stop is a warning, confirmed and resolved like every discrepancy. A run without protection work checks nothing else.
/// </summary>
public sealed class MonitorUnprotectedPositionTests
{
    private const string Kind = MonitorDiscrepancyService.UnprotectedKind;

    /// <summary>The fixture's account with no Play, linked order or fill: a position only the venue knows about.</summary>
    private static DiscrepancyFixture Untracked(bool keep = true)
    {
        var f = new DiscrepancyFixture();
        f.Store.Plays.Clear(); f.Store.Links.Clear(); f.Store.Orders.Clear(); f.Store.Fills.Clear(); f.VenueOrders.Clear();
        f.Account.Configure(null, "0x" + new string('a', 40), null);
        f.Account.UpdateSettings(f.Account.Name, null, true, keepExecutionHistory: keep);
        return f;
    }

    private static Task<MonitorEvaluationResult> KeptOnly(DiscrepancyFixture f) => f.Service.EvaluateAsync(f.Observation() with { Protection = false }, default);
    private static PlayDiscrepancy? Row(DiscrepancyFixture f) => f.Store.Rows.SingleOrDefault(x => x.Kind == Kind);
    private static VenueOrder StopOrder(DiscrepancyFixture f, string type = "Stop Market", string side = "sell") =>
        new("venue-stop", "BTC", side, type, 90, 90, true, false, 1, 1, f.Clock.Now.AddMinutes(-5), VenueOrderStatus.Open, "open", f.Clock.Now);

    [Fact]
    public async Task An_untracked_position_without_a_resting_stop_opens_one_warning_and_nothing_else_is_compared()
    {
        var f = Untracked();
        await KeptOnly(f);
        f.Advance(60);
        var result = await KeptOnly(f);
        var row = Assert.Single(f.Store.Rows);
        Assert.Equal((Kind, DiscrepancyLifecycle.Open, "warning"), (row.Kind, row.State, row.Severity));
        Assert.Contains("\"direction\":\"long\"", row.Details, StringComparison.Ordinal);
        Assert.False(result.Incomplete);
        Assert.Null(result.Coverage);
        Assert.Single(f.Events.Items, e => e.Type == "discrepancy.opened");
        // A changing quantity updates the evidence without announcing it again.
        f.Position = 2;
        f.Advance(60);
        await KeptOnly(f);
        Assert.Contains("\"venueQuantity\":\"2\"", Row(f)!.Details, StringComparison.Ordinal);
        Assert.Single(f.Events.Items, e => e.Type.StartsWith("discrepancy.", StringComparison.Ordinal));
        // A stop resting on the closing side resolves it, whatever its size.
        f.VenueOrders.Add(StopOrder(f));
        f.Advance(60);
        await KeptOnly(f);
        Assert.Equal(DiscrepancyLifecycle.Resolved, Row(f)!.State);
        Assert.Single(f.Events.Items, e => e.Type == "discrepancy.resolved");
    }

    [Fact]
    public async Task A_stop_on_the_wrong_side_does_not_protect_and_an_unrecognized_trigger_leaves_it_unknown()
    {
        var f = Untracked();
        f.VenueOrders.Add(StopOrder(f, side: "buy"));
        await KeptOnly(f); f.Advance(60); await KeptOnly(f);
        Assert.Equal(DiscrepancyLifecycle.Open, Row(f)!.State);
        var unknown = Untracked();
        unknown.VenueOrders.Add(StopOrder(unknown, type: "Trigger Market"));
        await KeptOnly(unknown); unknown.Advance(60);
        var result = await KeptOnly(unknown);
        Assert.Null(Row(unknown));
        Assert.True(result.Incomplete);
    }

    [Fact]
    public async Task Without_a_current_book_the_check_is_undecidable_by_design_and_a_flat_account_has_nothing()
    {
        var f = Untracked();
        f.CurrentOrderBook = false;
        await KeptOnly(f); f.Advance(60);
        var result = await KeptOnly(f);
        Assert.Null(Row(f));
        Assert.False(result.Incomplete);
        var flat = Untracked();
        flat.Position = 0;
        await KeptOnly(flat); flat.Advance(60); await KeptOnly(flat);
        Assert.Empty(flat.Store.Rows);
    }

    [Fact]
    public async Task Turning_the_opt_in_off_resolves_the_warning_and_an_Open_Play_on_the_market_owns_the_position()
    {
        var f = Untracked();
        await KeptOnly(f); f.Advance(60); await KeptOnly(f);
        Assert.Equal(DiscrepancyLifecycle.Open, Row(f)!.State);
        f.Account.UpdateSettings(f.Account.Name, null, true, keepExecutionHistory: false);
        f.Advance(60);
        await KeptOnly(f);
        Assert.Equal(DiscrepancyLifecycle.Resolved, Row(f)!.State);
        // With an Open Play on the market (a protection run), the position is the Play's and its own checks apply.
        var tracked = new DiscrepancyFixture();
        tracked.Account.Configure(null, "0x" + new string('b', 40), null);
        tracked.Account.UpdateSettings(tracked.Account.Name, null, true, keepExecutionHistory: true);
        tracked.VenueOrders.Clear();
        await tracked.Confirm();
        Assert.Null(tracked.Store.Rows.SingleOrDefault(x => x.Kind == Kind && x.State == DiscrepancyLifecycle.Open));
        Assert.Contains(tracked.Store.Rows, x => x.Kind == "protection.missing");
    }

    [Fact]
    public async Task A_protection_run_checks_an_untracked_position_beside_the_existing_comparisons()
    {
        var f = Untracked();
        await f.Confirm();
        Assert.Contains(f.Store.Rows, x => x.Kind == Kind && x.State == DiscrepancyLifecycle.Open);
        Assert.Contains(f.Store.Rows, x => x.Kind == "position.excess" && x.State == DiscrepancyLifecycle.Open);
    }
}
