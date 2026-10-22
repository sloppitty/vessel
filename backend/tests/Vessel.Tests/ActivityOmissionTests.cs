using Vessel.Application.Activity;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 7b: Activity is gap-aware. An omitted execution after a partition's first retained fill is never anchored
/// or walked across: the operation it interrupts reads "direction unknown: executions omitted" instead of a phantom
/// opposite operation, and forced events show as rows with their exclusion.
/// </summary>
public sealed class ActivityOmissionTests
{
    private static readonly Guid Owner = Guid.NewGuid(), AccountId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static ImportedFill Fill(int minute, string side, string effect, decimal quantity) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, ContractId = "BTC", Side = side, Direction = "display",
        PositionEffect = effect, Quantity = quantity, Price = 100m, ClosedPnlUsd = 0, Fee = 0, FeeToken = "USDC", PnlBasis = "gross",
        FeeBasis = "reported", OccurredAtUtc = Start.AddMinutes(minute), ExternalOrderId = minute.ToString(), SourceFillId = minute.ToString(),
        TransactionHash = ""
    };

    private static ExecutionOmission Omitted(int? minute, string contract = "BTC", int? from = null, int? to = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, Kind = ExecutionOmission.Forced, Reason = "liquidation",
        SourceId = Guid.NewGuid().ToString(), ContractId = contract, OccurredAtUtc = minute is { } m ? Start.AddMinutes(m) : null,
        UncertainFromUtc = from is { } f ? Start.AddMinutes(f) : null, UncertainToUtc = to is { } t ? Start.AddMinutes(t) : null
    };

    private static AccountSnapshot Snapshot(decimal quantity = 0) => new()
    {
        AccountId = AccountId, ObservedAtUtc = Start.AddMinutes(100),
        Positions = quantity == 0 ? [] : [new() { ContractId = "BTC", SignedQuantity = quantity }]
    };

    private static ActivitySummaries Aggregate(ImportedFill[] fills, ExecutionOmission[] omissions, ForcedEvent[]? forced = null, decimal snapshot = 0)
    {
        var account = new Account(AccountId, Owner, "manual", "Fixture");
        return ActivityService.Aggregate(new([account], fills, [Snapshot(snapshot)], new Dictionary<Guid, Guid>())
        { Omissions = omissions, ForcedEvents = forced ?? [] });
    }

    private static readonly ImportedFill[] Probe = [Fill(0, "buy", "open", 1), Fill(20, "buy", "open", 2), Fill(30, "sell", "close", 2)];

    /// <summary>The review probe correctness-dotnet/Activity.cs C6: buy, an omitted liquidation, buy 2, sell 2.</summary>
    [Fact]
    public void An_omitted_liquidation_ends_the_operation_it_interrupts_instead_of_a_phantom_short()
    {
        // Without the marker the flat snapshot anchors a phantom short before the first buy.
        var before = Aggregate(Probe, []).Operations;
        Assert.Contains(before, o => o.Direction == "short");
        var operations = Aggregate(Probe, [Omitted(10)]).Operations.OrderBy(o => o.LastOccurredAtUtc).ToList();
        Assert.Equal(2, operations.Count);
        Assert.Equal(((string?)null, "incomplete"), (operations[0].Direction, operations[0].Status));
        Assert.Contains(OperationAggregator.OmittedNotice, operations[0].Coverage);
        // After the last omission the snapshot anchors the rest again: a complete long round trip.
        Assert.Equal(("long", "closed", "2"), (operations[1].Direction, operations[1].Status, operations[1].Quantity));
        Assert.DoesNotContain(operations, o => o.Direction == "short");
    }

    [Fact]
    public void An_omission_after_the_last_fill_leaves_its_open_operation_unknown_and_one_before_the_first_changes_nothing()
    {
        var after = Assert.Single(Aggregate([Fill(0, "buy", "open", 1)], [Omitted(10)]).Operations);
        Assert.Equal(((string?)null, "incomplete"), (after.Direction, after.Status));
        Assert.Contains(OperationAggregator.OmittedNotice, after.Coverage);
        var round = new[] { Fill(10, "buy", "open", 1), Fill(20, "sell", "close", 1) };
        var earlier = Assert.Single(Aggregate(round, [Omitted(5)]).Operations);
        Assert.Equal(("long", "closed"), (earlier.Direction, earlier.Status));
        // Another market's omission is not this partition's.
        Assert.Equal(("long", "closed"), (Assert.Single(Aggregate(round, [Omitted(15, "ETH")]).Operations) is var other ? (other.Direction, other.Status) : default));
    }

    [Fact]
    public void An_omission_without_a_time_is_bounded_by_its_read_window_on_both_sides()
    {
        // Somewhere between minute 5 and minute 25: fills inside that window cannot be placed, and only later ones anchor.
        var fills = new[] { Fill(0, "buy", "open", 1), Fill(20, "buy", "open", 1), Fill(30, "buy", "open", 1), Fill(40, "sell", "close", 1) };
        var operations = Aggregate(fills, [Omitted(null, from: 5, to: 25)], snapshot: 0).Operations.OrderBy(o => o.LastOccurredAtUtc).ToList();
        Assert.All(operations.Take(operations.Count - 1), o => Assert.Null(o.Direction));
        Assert.Equal(("long", "closed"), (operations[^1].Direction, operations[^1].Status));
    }

    [Fact]
    public void Forced_events_are_rows_with_their_exclusion_and_markers_change_the_version()
    {
        var forced = new ForcedEvent
        {
            Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, SourceId = "f1", Kind = ForcedEvent.Liquidation, ContractId = "BTC",
            Side = "sell", Quantity = 1m, Price = 80m, OccurredAtUtc = Start.AddMinutes(10), Exclusion = "excluded"
        };
        var page = Aggregate(Probe, [Omitted(10)], [forced]);
        var row = Assert.Single(page.Coverage, c => c.Notice.StartsWith("Liquidation", StringComparison.Ordinal));
        Assert.Equal(("BTC", "Liquidation on 2026-10-12 00:10:00 UTC: sell 1 at 80; realized P&L unknown, fee unknown. Excluded: venue-forced, kept beside the fills, never linked or scored."),
            (row.ContractId, row.Notice));
        Assert.NotEqual(Aggregate(Probe, []).Version, page.Version);
    }
}
