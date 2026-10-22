using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;

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

    [Fact]
    public async Task A_dismissed_warning_reopens_when_the_position_keeps_changing_and_stays_dismissed_while_it_does_not()
    {
        // Sol round 1: confirmed on its condition, but its fingerprint keeps the quantity.
        var f = Untracked();
        await KeptOnly(f); f.Advance(60); await KeptOnly(f);
        var row = Row(f)!;
        await f.Service.DismissAsync(f.Account.Id, null, row.Id, null, default);
        f.Advance(60); await KeptOnly(f);
        Assert.Equal(DiscrepancyLifecycle.Dismissed, row.State);
        // The position grows, and grows again before the delay: the sequence continues and the warning reopens.
        f.Position = 10;
        f.Advance(60); await KeptOnly(f);
        f.Position = 12;
        f.Advance(30); await KeptOnly(f);
        Assert.Equal(DiscrepancyLifecycle.Dismissed, row.State);
        f.Advance(30); await KeptOnly(f);
        Assert.Equal(DiscrepancyLifecycle.Open, row.State);
        Assert.Contains("\"venueQuantity\":\"12\"", row.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_confirms_only_when_both_the_snapshot_and_the_book_are_the_delay_apart()
    {
        // Sol round 1: the first book was read 19 s after its snapshot; a check 60 s after the snapshot is only 41 s after it.
        var f = Untracked();
        var first = f.Observation();
        f.Advance(19);
        var book = f.Observation().Read.Orders!;
        await f.Service.EvaluateAsync(first with { Protection = false, Read = first.Read with { Orders = book } }, default);
        f.Advance(41);
        await f.Service.EvaluateAsync(f.Observation() with { Protection = false, PreviousSuccessAtUtc = f.Clock.Now.AddSeconds(-41) }, default);
        Assert.Equal(DiscrepancyLifecycle.Candidate, Row(f)!.State);
        f.Advance(19);
        await f.Service.EvaluateAsync(f.Observation() with { Protection = false, PreviousSuccessAtUtc = f.Clock.Now.AddSeconds(-19) }, default);
        Assert.Equal(DiscrepancyLifecycle.Open, Row(f)!.State);
        Assert.Equal(first.Read.Orders!.ObservedAtUtc.AddSeconds(19), Row(f)!.OpenedAtUtc);
    }

    [Fact]
    public async Task A_Planned_Play_does_not_own_the_position_until_its_entry_is_linked_and_it_is_Open()
    {
        var f = Untracked();
        await KeptOnly(f); f.Advance(60); await KeptOnly(f);
        var play = new Play(Guid.NewGuid(), f.Account, f.Play.Instrument, InstrumentSource.Venue, f.Play.Plan, f.Clock.Now);
        play.MarkPlanned(f.Clock.Now);
        f.Store.Plays.Add(play);
        f.Advance(60);
        await f.Service.EvaluateAsync(f.Observation(), default);
        Assert.Equal(DiscrepancyLifecycle.Open, Row(f)!.State);
    }
}
