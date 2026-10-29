using Microsoft.EntityFrameworkCore;
using Vessel.Application.Venues;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Second review round for PR #46: eligibility, drift identity and shared stop coverage.</summary>
public sealed class MonitorDiscrepancyRound2Tests
{
    [Fact] // Finding 2
    public async Task Partial_close_rearms_a_dismissed_level_drift()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { TriggerPrice = 80m });
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "level.drift" && x.State == "open");
        await f.Service.DismissAsync(f.Account.Id, f.Play.Id, row.Id, null, default);
        f.Advance(60); await f.Confirm();
        Assert.Equal("dismissed", row.State);
        // A target fills half the position; the stop keeps its drifted price.
        f.AddOrder("target", OrderLinkRole.Target, .5m, 120);
        var index = f.VenueOrders.FindIndex(x => x.OrderId == "target");
        f.VenueOrders[index] = f.VenueOrders[index] with { Status = VenueOrderStatus.Filled, VenueStatus = "filled", RemainingSize = 0 };
        f.Fill("tp-fill", "target", .5m, f.Clock.Now.AddMinutes(-1), "sell", ExecutionFacts.Close);
        f.Position = .5m;
        f.Advance(60); await f.Confirm();
        Assert.Equal("open", row.State);
        Assert.Equal(2, f.Events.Items.Count(x => x.Type == "discrepancy.opened" && x.Subject.DiscrepancyId == row.Id));
    }

    [Fact] // Finding 2: open records still follow quantity changes silently.
    public async Task Open_level_drift_follows_a_partial_close_without_another_opened_event()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { TriggerPrice = 80m });
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "level.drift" && x.State == "open");
        var fingerprint = row.Fingerprint;
        f.Stop(o => o with { RemainingSize = .5m });
        f.Advance(60); await f.Confirm();
        Assert.Equal("open", row.State);
        Assert.NotEqual(fingerprint, row.Fingerprint);
        Assert.Single(f.Events.Items, x => x.Type == "discrepancy.opened" && x.Subject.DiscrepancyId == row.Id);
    }

    [Fact] // Finding 3: one precomputed lookup per instrument, still correct with many siblings.
    public async Task Many_sibling_plays_share_one_whole_position_stop()
    {
        var f = new DiscrepancyFixture { Position = 61 };
        var siblings = Enumerable.Range(0, 60).Select(_ => f.AddPlay("long", 1)).ToArray();
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        await f.Evaluate(); f.Advance(60);
        Assert.False((await f.Evaluate()).Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.missing");
        Assert.Equal(60, siblings.Length);
    }

    [Fact] // Finding 4: a whole-position target never establishes stop protection.
    public async Task Whole_position_target_never_protects_a_sibling()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        var sibling = f.AddPlay("long", 1);
        f.VenueOrders.Clear();
        f.AddOrder("target", OrderLinkRole.Target, 0, 120);
        var index = f.VenueOrders.FindIndex(x => x.OrderId == "target");
        f.VenueOrders[index] = f.VenueOrders[index] with { IsPositionTpsl = true };
        await f.Confirm();
        Assert.Contains(f.Store.Rows, x => x.PlayId == sibling.Id && x.Kind == "protection.missing" && x.State == "open" && x.Severity == "critical");
        Assert.Contains(f.Store.Rows, x => x.PlayId == f.Play.Id && x.Kind == "protection.missing" && x.State == "open");
    }

    [Fact] // Finding 4: a buy stop does not protect long exposure.
    public async Task Wrong_side_whole_position_stop_protects_nothing()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        var sibling = f.AddPlay("long", 1);
        f.Stop(o => o with { Side = "buy", IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        await f.Confirm();
        Assert.Contains(f.Store.Rows, x => x.PlayId == sibling.Id && x.Kind == "protection.missing" && x.State == "open");
        Assert.Contains(f.Store.Rows, x => x.PlayId == f.Play.Id && x.Kind == "protection.missing" && x.State == "open");
    }

    [Fact] // Finding 4: net-position equality never allocates per-Play protection across opposing Plays.
    public async Task Opposing_plays_that_net_out_do_not_share_whole_position_coverage()
    {
        var f = new DiscrepancyFixture { Position = 1 }; // long 1 + long 1 + short 1
        var sameSide = f.AddPlay("long", 1);
        var opposite = f.AddPlay("short", 1);
        f.Stop(o => o with { RemainingSize = 1 }); // Only the first Play's own stop at first.
        await f.Confirm();
        var unprotected = f.Store.Rows.Single(x => x.PlayId == sameSide.Id && x.Kind == "protection.missing" && x.State == "open");
        var shortMissing = f.Store.Rows.Single(x => x.PlayId == opposite.Id && x.Kind == "protection.missing" && x.State == "open");
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        f.Advance(60); await f.Confirm();
        Assert.Equal("open", unprotected.State); // Unknown, so not cleared by the net match.
        Assert.Equal("open", shortMissing.State); // A sell stop never covers the short.
        Assert.DoesNotContain(f.Events.Items, x => x.Type == "discrepancy.resolved" && x.Subject.DiscrepancyId == unprotected.Id);
    }

    [Fact] // Event contract: transitions bump the revision and stay distinct; silent updates do not.
    public async Task Open_resolve_reopen_dismiss_emits_opened_resolved_opened_resolved_with_revisioned_keys()
    {
        var f = new DiscrepancyFixture { Position = 0 };
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "position.missing");
        f.Position = 1; f.Advance(60); await f.Evaluate();
        Assert.Equal("resolved", row.State);
        f.Position = 0; f.Advance(60); await f.Confirm();
        Assert.Equal("open", row.State);
        await f.Service.DismissAsync(f.Account.Id, null, row.Id, null, default);
        var events = f.Events.Items.Where(x => x.Subject.DiscrepancyId == row.Id).ToArray();
        Assert.Equal(["discrepancy.opened", "discrepancy.resolved", "discrepancy.opened", "discrepancy.resolved"], events.Select(x => x.Type).ToArray());
        Assert.Equal([$"discrepancy:{row.Id}:1:opened", $"discrepancy:{row.Id}:2:resolved", $"discrepancy:{row.Id}:3:opened",
            $"discrepancy:{row.Id}:4:dismissed"], events.Select(x => x.DedupKey).ToArray());
    }
}
