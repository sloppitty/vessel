using Microsoft.EntityFrameworkCore;
using Vessel.Application.Venues;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>Third review round for PR #46: durable re-confirmation, stop mechanics and bounded unobservable lifetime.</summary>
public sealed class MonitorDiscrepancyRound3Tests
{
    [Fact] // Finding 2 (Sol r2)
    public async Task Take_profit_linked_as_stop_must_not_protect_sibling()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        var sibling = f.AddPlay("long", 1);
        f.Stop(o => o with { OrderType = "Take Profit Market", TriggerPrice = 120, IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
        await f.Confirm();
        Assert.Contains(f.Store.Rows, x => x.Kind == "protection.missing" && x.PlayId == sibling.Id && x.State == "open");
        Assert.Contains(f.Store.Rows, x => x.Kind == "protection.missing" && x.PlayId == f.Play.Id && x.State == "open");
    }

    [Fact] // Finding 2 (Sol r2)
    public async Task Shared_buy_stop_protects_short_siblings()
    {
        var f = new DiscrepancyFixture { Position = -2 };
        f.Store.Plays.Clear(); f.Store.Links.Clear(); f.Store.Orders.Clear(); f.Store.Fills.Clear(); f.VenueOrders.Clear();
        var first = f.AddPlay("short", 1, "entry");
        f.AddPlay("short", 1);
        f.Store.Links.Add(f.Link(first, "stop", OrderLinkRole.Stop, "e", "s", OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now.AddMinutes(-15)));
        f.VenueOrders.Add(new("stop", "BTC", "buy", "Stop Market", 110, 110, true, true, 0, 0, f.Clock.Now.AddMinutes(-15), VenueOrderStatus.Open, "open", f.Clock.Now));
        await f.Confirm();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.missing");
        f.Advance(60);
        Assert.False((await f.Evaluate()).Incomplete);
    }

    [Fact] // Finding 2: venue-neutral words, e.g. hyphenated adapter types.
    public async Task Hyphenated_take_profit_linked_as_stop_does_not_protect_its_own_play()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { OrderType = "take-profit", TriggerPrice = 120 });
        await f.Confirm();
        Assert.Contains(f.Store.Rows, x => x.Kind == "protection.missing" && x.State == "open");
    }

    [Fact] // Finding 2: unrecognized mechanics are unknown, never protected and never critical missing.
    public async Task Unrecognized_trigger_mechanics_leave_stop_coverage_unknown()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { OrderType = "Trigger" });
        Assert.True((await f.Evaluate()).Incomplete);
        f.Advance(60); await f.Evaluate();
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind is "protection.missing" or "protection.short" or "protection.cancelled");
    }

    [Fact] // Finding 1: an interrupted re-confirmation stays outstanding work and still confirms later.
    public async Task Interrupted_reconfirmation_stays_outstanding_and_confirms()
    {
        var f = new DiscrepancyFixture { Position = 2 };
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "position.excess");
        f.Position = 1; f.Advance(60); await f.Evaluate();
        Assert.Equal("resolved", row.State);
        f.Position = 2; f.Advance(60); await f.Evaluate();
        Assert.True(row.PendingFingerprint is not null);
        f.PositionsComplete = false; f.Advance(60);
        Assert.True((await f.Evaluate()).Incomplete);
        Assert.Equal("resolved", row.State);
        Assert.True(row.PendingFingerprint is not null);
        f.PositionsComplete = true; f.Advance(60); await f.Confirm();
        Assert.Equal("open", row.State);
        Assert.False(row.PendingFingerprint is not null);
    }

    [Fact] // Finding 3
    public async Task Unobservable_open_record_becomes_visible_unverifiable_after_its_lifetime()
    {
        var f = Untracked();
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "position.excess" && x.State == "open");
        f.Retired = true;
        f.Advance(60); Assert.True((await f.Evaluate()).Incomplete);
        f.Advance((int)TimeSpan.FromDays(7).TotalSeconds);
        Assert.False((await f.Evaluate()).Incomplete);
        Assert.Equal("open", row.State);
        Assert.NotNull(row.UnverifiableAtUtc);
        var dto = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Id == row.Id);
        Assert.Equal("unverifiable", dto.Outcome);
        Assert.Null(dto.ResolvedAtUtc);
        Assert.DoesNotContain(f.Events.Items, x => x.Type == "discrepancy.resolved");
        f.Advance(600); Assert.False((await f.Evaluate()).Incomplete);
        // A fresh decision makes it verifiable again.
        f.Retired = false; f.Advance(60); await f.Evaluate();
        Assert.Null(row.UnverifiableAtUtc);
        Assert.Equal("open", row.State);
    }

    [Fact] // Finding 3: an unconfirmed candidate becomes visible and dismissable.
    public async Task Unobservable_candidate_becomes_visible_and_dismissable()
    {
        var f = Untracked();
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "position.excess" && x.State == "candidate");
        f.Retired = true;
        f.Advance((int)TimeSpan.FromDays(7).TotalSeconds + 60);
        await f.Evaluate();
        var dto = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Id == row.Id);
        Assert.Equal("candidate", dto.State);
        Assert.Equal("unverifiable", dto.Outcome);
        await f.Service.DismissAsync(f.Account.Id, null, row.Id, "venue retired the market", default);
        Assert.Equal("dismissed", row.State);
        // Announced as unverifiable, so its dismissal is published like any other.
        Assert.Equal(["discrepancy.unverifiable", "discrepancy.resolved"], f.Events.Items.Select(x => x.Type).ToArray());
    }

    private static DiscrepancyFixture Untracked()
    {
        var f = new DiscrepancyFixture { Position = 1 };
        f.Store.Plays.Clear(); f.Store.Links.Clear(); f.Store.Orders.Clear(); f.Store.Fills.Clear(); f.VenueOrders.Clear();
        return f;
    }
}
