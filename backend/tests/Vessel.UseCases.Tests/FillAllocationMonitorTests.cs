using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 11, Sol round 1: the monitor's protection checks read a Play's part of an allocated fill, so a Play a flip
/// opened is protected like any other and a shared exit closes only its part. The position comparison is unchanged.
/// </summary>
public sealed class FillAllocationMonitorTests
{
    private static ImportedFill Last(DiscrepancyFixture f) => f.Store.Fills[^1];

    [Fact]
    public async Task A_Play_a_flip_opened_without_an_entry_link_opens_protection_missing_after_its_grace()
    {
        // The fixture's long closes through the flip's exit line; a short opens through its entry line, with no link at all.
        var f = new DiscrepancyFixture { Position = -1 };
        f.VenueOrders.Clear();
        var plan = new PlayPlanDocument("short", "quantity", "1", "1", null,
            [new("e", "Entry", "#ffffff", "100", "100", [new("s", "price", "110", "100")], [new("t", "price", "80", "100")])], new("", "", "", ""));
        var opened = new Play(Guid.NewGuid(), f.Account, new("hyperliquid", "BTC"), InstrumentSource.Venue, PlayDocuments.Serialize(plan),
            f.Clock.Now.AddHours(-1));
        opened.MarkPlanned(f.Clock.Now.AddMinutes(-30));
        opened.MarkOpen("Entry fill, allocated from a shared fill.", f.Clock.Now.AddMinutes(-15));
        f.Store.Plays.Add(opened);
        var order = new ImportedOrder
        {
            Id = Guid.NewGuid(), OwnerId = f.Account.OwnerId, AccountId = f.Account.Id, ContractId = "BTC", ExternalOrderId = "flip", Side = "sell",
            OrderType = "Limit", OriginalSize = 2, Status = "filled", VenueStatus = "filled", PlacedAtUtc = f.Clock.Now.AddMinutes(-16),
            StatusAtUtc = f.Clock.Now.AddMinutes(-15), ObservedAtUtc = f.Clock.Now.AddMinutes(-15), LimitPrice = 100,
        };
        f.Store.Orders.Add(order);
        f.Fill("flip-fill", "flip", 2, f.Clock.Now.AddMinutes(-15), "sell", ExecutionFacts.Flip);
        var (allocation, _) = FillAllocation.Create(Last(f), AllocationReason.Flip, "Reversed in one sell.",
            [new(f.Play.Id, "long", AllocationRole.Exit, null, 1), new(opened.Id, "short", AllocationRole.Entry, "e", 1)], f.Clock.Now);
        f.Store.Allocations.Add(new(allocation, Last(f), order, false));
        f.Play.MarkClosed("Linked and allocated exits covered the 1 entered.", f.Clock.Now.AddMinutes(-15));

        await f.Evaluate();
        f.Advance(60); await f.Evaluate();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "protection.missing" && x.PlayId == opened.Id);
        Assert.Equal(("open", "critical"), (row.State, row.Severity));
        Assert.Equal("1", row.Details.GetProperty("quantity").GetString());
        // The position comparison still reads linked fills only, as accepted: the opened Play has none, so it stays unknown.
        Assert.DoesNotContain(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind.StartsWith("position.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_shared_exit_closes_only_its_allocated_part_so_a_stop_for_the_rest_reads_short()
    {
        // The fixture's long enters 2 and its stop covers 1. One sell of 1, linked to no Play, is split 0.5 / 0.5 with another
        // long: 1.5 is still open, so the stop is short, and the record says 1.5, never the 2 its linked fills alone give.
        var f = new DiscrepancyFixture { Position = 2 };
        f.Store.Fills.Single(x => x.SourceFillId == "entry-fill").Quantity = 2;
        f.Store.Orders.Single(x => x.ExternalOrderId == "entry").OriginalSize = 2;
        var other = f.AddPlay("long", 1, "other-entry");
        var exit = f.Retained(f.Play, "shared", OrderLinkRole.Exit);
        (exit.Status, exit.VenueStatus, exit.RemainingSize) = ("filled", "filled", 0);
        f.Fill("shared-fill", "shared", 1, f.Clock.Now.AddMinutes(-10), "sell", ExecutionFacts.Close);
        var (allocation, _) = FillAllocation.Create(Last(f), AllocationReason.SharedClose, "Closed half of each.",
            [new(f.Play.Id, "long", AllocationRole.Exit, null, 0.5m), new(other.Id, "long", AllocationRole.Exit, null, 0.5m)], f.Clock.Now);
        f.Store.Allocations.Add(new(allocation, Last(f), exit, false));

        await f.Evaluate();
        f.Advance(60); await f.Evaluate();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "protection.short" && x.PlayId == f.Play.Id);
        Assert.Equal("open", row.State);
        Assert.Equal(("1.5", "1"), (row.Details.GetProperty("quantity").GetString(), row.Details.GetProperty("stopQuantity").GetString()));
        // The allocated fill is the Plays' own, never an unlinked one, though no link names its order.
        Assert.DoesNotContain(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "fill.unlinked");
    }
}
