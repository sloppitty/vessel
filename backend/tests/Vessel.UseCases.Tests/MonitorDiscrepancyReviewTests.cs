using System.Text;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Venues;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Review regressions for PR #46 (owner-approved discrepancy rules a–j).</summary>
public sealed class MonitorDiscrepancyReviewTests
{
    private static readonly string[] Protection = ["protection.missing", "protection.short", "protection.excess", "protection.cancelled"];

    [Fact] // Rule a
    public async Task Whole_position_stop_on_one_play_does_not_raise_critical_missing_for_a_sibling()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        var sibling = f.AddPlay("long", 1);
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        await f.Confirm();
        Assert.DoesNotContain(f.Store.Rows, x => x.PlayId == sibling.Id && x.Kind == "protection.missing");
        f.Advance(60); Assert.False((await f.Evaluate()).Incomplete);
    }

    [Fact] // Rule a
    public async Task Whole_position_stop_with_a_position_mismatch_leaves_sibling_coverage_unknown_not_missing()
    {
        var f = new DiscrepancyFixture { Position = 3 };
        var sibling = f.AddPlay("long", 1);
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        await f.Confirm();
        Assert.DoesNotContain(f.Store.Rows, x => x.PlayId == sibling.Id && x.Kind == "protection.missing");
        Assert.Contains(f.Store.Rows, x => x.Kind == "position.excess" && x.State == "open");
    }

    [Fact] // Rule f
    public async Task Changed_details_of_an_open_discrepancy_do_not_reopen_it()
    {
        var f = new DiscrepancyFixture { Position = .5m };
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "position.short" && x.State == "open");
        var openedAt = row.OpenedAtUtc;
        var revision = row.Revision;
        var fingerprint = row.Fingerprint;
        f.Position = .6m;
        f.Advance(60); await f.Evaluate(); f.Advance(60); await f.Evaluate();
        Assert.Single(f.Events.Items, x => x.Type == "discrepancy.opened" && x.Subject.DiscrepancyId == row.Id);
        Assert.Equal(openedAt, row.OpenedAtUtc);
        Assert.Equal(revision, row.Revision);
        Assert.Equal("open", row.State);
        Assert.NotEqual(fingerprint, row.Fingerprint);
        Assert.Contains("\"venueQuantity\":\"0.6\"", row.Details);
        Assert.Null(row.PendingFingerprint);
    }

    [Fact] // Rule b
    public async Task Single_play_whole_position_stop_is_not_permanently_unknown()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        Assert.False((await f.Evaluate()).Incomplete); f.Advance(60);
        Assert.False((await f.Evaluate()).Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => Protection.Contains(x.Kind));
    }

    [Fact] // Rule b
    public async Task Established_whole_position_coverage_still_decides_excess()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        f.AddOrder("extra-stop", OrderLinkRole.Stop, .5m, 90);
        var index = f.VenueOrders.FindIndex(x => x.OrderId == "extra-stop");
        f.VenueOrders[index] = f.VenueOrders[index] with { ReduceOnly = false };
        await f.Confirm();
        Assert.Contains(f.Store.Rows, x => x.Kind == "protection.excess" && x.State == "open");
    }

    [Fact] // Rule b
    public async Task Single_play_whole_position_stop_with_a_position_mismatch_is_unknown_without_degrading()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        await f.Evaluate(); f.Advance(60);
        Assert.False((await f.Evaluate()).Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind is "protection.short" or "protection.excess");
        Assert.Contains(f.Store.Rows, x => x.Kind == "position.excess" && x.State == "open");
    }

    [Fact] // Rule c
    public async Task Reduce_only_stop_after_partial_target_fill_is_not_excess()
    {
        var f = PartiallyTargeted(new DiscrepancyFixture { Position = .5m });
        await f.Confirm();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.excess" && x.State == "open");
    }

    [Fact] // Rule c
    public async Task Reduce_only_excess_still_counts_with_another_open_play_on_the_instrument()
    {
        var f = PartiallyTargeted(new DiscrepancyFixture { Position = 1.5m });
        f.AddPlay("long", 1);
        await f.Confirm();
        Assert.Contains(f.Store.Rows, x => x.PlayId == f.Play.Id && x.Kind == "protection.excess" && x.State == "open");
    }

    [Fact] // Rule g
    public async Task Many_unlinked_fills_still_confirm_the_warning()
    {
        var f = new DiscrepancyFixture { Position = 1 };
        for (var i = 0; i < 300; i++) f.Fill($"unlinked-{i:D4}", $"other-{i:D4}", .001m, f.Clock.Now.AddMinutes(-5));
        await f.Confirm();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "fill.unlinked" && x.State == "open");
        Assert.Equal(300, row.Details.GetProperty("count").GetInt32());
        Assert.Equal("0.3", row.Details.GetProperty("totalQuantity").GetString());
        Assert.Equal(20, row.Details.GetProperty("fills").GetArrayLength());
        Assert.Equal("unlinked-0000", row.Details.GetProperty("fills")[0].GetProperty("fillId").GetString());
    }

    [Fact] // Rule g
    public async Task Unlinked_fill_details_stay_bounded_and_the_fingerprint_covers_unlisted_fills()
    {
        var f = new DiscrepancyFixture { Position = 1 };
        string Id(char c, int i) => $"{c}{i:D6}".PadRight(128, 'x');
        for (var i = 0; i < 2000; i++) f.Fill(Id('a', i), Id('o', i), .001m, f.Clock.Now.AddMinutes(-5));
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "fill.unlinked" && x.State == "open");
        Assert.True(Encoding.UTF8.GetByteCount(row.Details) < 16_000);
        await f.Service.DismissAsync(f.Account.Id, null, row.Id, null, default);
        f.Fill(Id('z', 0), Id('p', 0), .001m, f.Clock.Now.AddMinutes(-1)); // Sorts after every listed fill.
        f.Advance(60); await f.Confirm();
        Assert.Equal("open", row.State);
        Assert.Contains("\"count\":2001", row.Details);
    }

    [Fact] // Rule h
    public async Task One_order_without_a_planned_price_does_not_make_drift_unknown()
    {
        var f = new DiscrepancyFixture();
        f.Store.Links.Add(f.Link(f.Play, "orphan-level", OrderLinkRole.Target, "e", "removed-by-revision", OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now.AddMinutes(-15)));
        f.VenueOrders.Add(new("orphan-level", "BTC", "sell", "Trigger", 130, 130, true, false, .1m, .1m, f.Clock.Now.AddMinutes(-15), VenueOrderStatus.Open, "open", f.Clock.Now));
        f.Stop(o => o with { TriggerPrice = 80m }); // A real drift on a priced level.
        await f.Confirm();
        Assert.Contains(f.Store.Rows, x => x.Kind == "level.drift" && x.State == "open");
        f.Stop(o => o with { TriggerPrice = 90m });
        f.Advance(60); Assert.False((await f.Evaluate()).Incomplete);
    }

    [Fact] // Rule d
    public async Task Resting_entry_on_a_closed_play_is_a_critical_orphan()
    {
        var f = new DiscrepancyFixture { Position = 0 };
        f.Play.MarkClosed("fixture", f.Clock.Now.AddMinutes(-1));
        f.VenueOrders.Clear();
        f.Store.Links.Add(f.Link(f.Play, "late-entry", OrderLinkRole.Entry, "e", null, OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now.AddMinutes(-15)));
        f.VenueOrders.Add(new("late-entry", "BTC", "buy", "Limit", 100, null, false, false, 1, 1, f.Clock.Now.AddMinutes(-15), VenueOrderStatus.Open, "open", f.Clock.Now));
        await f.Confirm();
        var row = Assert.Single(f.Store.Rows, x => x.PlayId == f.Play.Id && x.State == "open");
        Assert.Equal("entry.orphaned", row.Kind);
        Assert.Equal("critical", row.Severity);
        Assert.Contains(f.Events.Items, x => x.Type == "discrepancy.opened" && x.Severity == "critical" && x.Subject.DiscrepancyId == row.Id);
        f.VenueOrders.Clear(); f.Advance(60); await f.Evaluate();
        Assert.Equal("resolved", row.State);
    }

    [Fact] // Rule e
    public async Task One_cancelled_stop_raises_one_critical_protection_record()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { Status = VenueOrderStatus.Canceled });
        await f.Confirm();
        var critical = f.Store.Rows.Where(x => x.PlayId == f.Play.Id && x.State == "open" && x.Severity == "critical").Select(x => x.Kind).ToArray();
        Assert.Equal(new[] { "protection.cancelled" }, critical);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.short" && x.State == "open");
    }

    [Fact] // Rule j
    public async Task A_play_that_closed_after_the_snapshot_makes_the_position_comparison_unknown()
    {
        var f = new DiscrepancyFixture { Position = 2, ObservationOffset = TimeSpan.FromSeconds(-30) };
        var closing = f.AddPlay("long", 1);
        f.Store.Links.Add(f.Link(closing, "closing-exit", OrderLinkRole.Stop, "e", "s", OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now.AddMinutes(-15)));
        f.Fill("closing-exit-fill", "closing-exit", 1, f.Clock.Now.AddSeconds(-10), "sell", ExecutionFacts.Close);
        closing.MarkClosed("fixture", f.Clock.Now.AddSeconds(-5));
        Assert.True((await f.Evaluate()).Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind.StartsWith("position."));
        Assert.Equal(f.Clock.Now.AddSeconds(-30), f.Store.RecentSince);
    }

    private static DiscrepancyFixture PartiallyTargeted(DiscrepancyFixture f)
    {
        f.AddOrder("target", OrderLinkRole.Target, .5m, 120);
        var index = f.VenueOrders.FindIndex(x => x.OrderId == "target");
        f.VenueOrders[index] = f.VenueOrders[index] with { Status = VenueOrderStatus.Filled, VenueStatus = "filled", RemainingSize = 0 };
        f.Fill("tp-fill", "target", .5m, f.Clock.Now.AddMinutes(-5), "sell", ExecutionFacts.Close);
        return f;
    }
}
