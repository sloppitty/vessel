using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

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

    private static ImportedFill Fill(int minute, string side, string effect, decimal quantity, string contract = "BTC", string? native = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, ContractId = contract, VenueContractId = native, Side = side,
        Direction = "display", PositionEffect = effect, Quantity = quantity, Price = 100m, ClosedPnlUsd = 0, Fee = 0, FeeToken = "USDC",
        PnlBasis = "gross", FeeBasis = "reported", OccurredAtUtc = Start.AddMinutes(minute), ExternalOrderId = minute.ToString(),
        SourceFillId = minute.ToString(), TransactionHash = ""
    };

    private static ExecutionOmission Omitted(int? minute, string? contract = "BTC", int? from = null, int? to = null, string? native = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, Kind = ExecutionOmission.Forced, Reason = "liquidation",
        SourceId = Guid.NewGuid().ToString(), ContractId = contract, VenueContractId = native,
        OccurredAtUtc = minute is { } m ? Start.AddMinutes(m) : null,
        UncertainFromUtc = from is { } f ? Start.AddMinutes(f) : null, UncertainToUtc = to is { } t ? Start.AddMinutes(t) : null
    };

    private static AccountSnapshot Snapshot(decimal quantity = 0) => new()
    {
        AccountId = AccountId, ObservedAtUtc = Start.AddMinutes(100),
        Positions = quantity == 0 ? [] : [new() { ContractId = "BTC", SignedQuantity = quantity }]
    };

    /// <summary>A flat snapshot after every fill by default; <paramref name="snapshot"/> null reads without one.</summary>
    private static ActivitySummaries Aggregate(ImportedFill[] fills, ExecutionOmission[] omissions, ForcedEvent[]? forced = null, decimal? snapshot = 0)
    {
        var account = new Account(AccountId, Owner, "manual", "Fixture");
        return ActivityService.Aggregate(new([account], fills, snapshot is { } quantity ? [Snapshot(quantity)] : [], new Dictionary<Guid, Guid>())
        { Omissions = omissions.Select(ActivityOmission.From).ToList(), ForcedEvents = forced ?? [] });
    }

    private static List<ActivityOperationDto> Operations(ImportedFill[] fills, ExecutionOmission[] omissions, decimal? snapshot = 0) =>
        Aggregate(fills, omissions, snapshot: snapshot).Operations.OrderBy(o => o.LastOccurredAtUtc).ToList();

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

    /// <summary>
    /// Sol round 1, finding 1: the walk recovered inside the window and reported a closed short for 20 to 30, though an
    /// omitted sell-open at 25 would leave it open.
    /// </summary>
    [Fact]
    public void Nothing_inside_an_omission_window_proves_flatness_or_direction()
    {
        ImportedFill[] repro = [Fill(0, "buy", "open", 1), Fill(10, "sell", "close", 1), Fill(20, "sell", "open", 1), Fill(30, "buy", "close", 1)];
        var inside = Operations(repro, [Omitted(null, from: 5, to: 40)]);
        Assert.All(inside, o => Assert.Equal(((string?)null, "incomplete"), (o.Direction, o.Status)));
        Assert.All(inside, o => Assert.Contains(OperationAggregator.OmittedNotice, o.Coverage));
        // A close inside the window proves no direction for a later open, even one after the window.
        Assert.DoesNotContain(Operations(repro, [Omitted(null, from: 5, to: 15)], snapshot: null), o => o.Status == "closed");
        // An unknown end never passes.
        ImportedFill[] longer = [.. repro, Fill(45, "sell", "close", 1), Fill(50, "sell", "open", 1), Fill(60, "buy", "close", 1)];
        Assert.DoesNotContain(Operations(longer, [Omitted(null, from: 5)], snapshot: null), o => o.Status == "closed");
        // A proof entirely after the window's end recovers the walk: a sell-close, then a sell-open.
        ImportedFill[] after = [Fill(0, "buy", "open", 1), Fill(10, "sell", "close", 1), Fill(20, "sell", "close", 1),
            Fill(30, "sell", "open", 1), Fill(40, "buy", "close", 1)];
        var recovered = Operations(after, [Omitted(null, from: 5, to: 15)], snapshot: null);
        Assert.Equal(("short", "closed", Start.AddMinutes(30)), (recovered[^1].Direction, recovered[^1].Status, recovered[^1].OpenedAtUtc));
        Assert.DoesNotContain(OperationAggregator.OmittedNotice, recovered[^1].Coverage);
    }

    /// <summary>Sol round 1, finding 2: a marker without a canonical contract was discarded.</summary>
    [Fact]
    public void A_marker_without_a_market_breaks_every_partition_and_one_without_a_canonical_contract_uses_its_native_market()
    {
        ImportedFill[] eth = [Fill(5, "buy", "open", 1, "ETH"), Fill(15, "sell", "close", 1, "ETH")];
        var marketless = Operations([.. Probe, .. eth], [Omitted(10, contract: null)]);
        Assert.DoesNotContain(marketless, o => o.Direction == "short");
        Assert.DoesNotContain(marketless, o => o.ContractId == "ETH" && o.Status == "closed");
        Assert.Contains(marketless, o => o.ContractId == "ETH" && o.Coverage.Contains(OperationAggregator.OmittedNotice));
        // A market Vessel does not recognize keeps its native ID, the partition's identity.
        var native = Probe.Select(f => Fill((int)(f.OccurredAtUtc - Start).TotalMinutes, f.Side, f.PositionEffect, f.Quantity, native: "7")).ToArray();
        Assert.DoesNotContain(Operations(native, [Omitted(10, contract: null, native: "7")]), o => o.Direction == "short");
        // An ID-less fill's native identity is its key, as for orders (Hyperliquid's ordinary coins).
        var coin = Probe.Select(f => Fill((int)(f.OccurredAtUtc - Start).TotalMinutes, f.Side, f.PositionEffect, f.Quantity, "FOO")).ToArray();
        Assert.DoesNotContain(Operations(coin, [Omitted(10, contract: null, native: "FOO")]), o => o.Direction == "short");
        // Another native market is not this one.
        Assert.Contains(Operations(native, [Omitted(10, contract: null, native: "8")]), o => o.Direction == "short");
    }

    /// <summary>Sol round 1, finding 3: native markers missed legacy partitions, and ID-less ones missed native partitions.</summary>
    [Fact]
    public void A_marker_breaks_every_partition_it_could_belong_to_without_merging_their_fills()
    {
        // ID-less legacy fills use the canonical partition; a marker naming native market 7 may lie there.
        Assert.DoesNotContain(Operations(Probe, [Omitted(10, native: "7")]), o => o.Direction == "short");
        // An ID-less marker beside two native markets could be on either: both are broken, and neither absorbs the other.
        var seven = Probe.Select(f => Fill((int)(f.OccurredAtUtc - Start).TotalMinutes, f.Side, f.PositionEffect, f.Quantity, native: "7")).ToArray();
        ImportedFill[] eight = [Fill(5, "sell", "open", 1, native: "8"), Fill(15, "buy", "close", 1, native: "8")];
        var operations = Operations([.. seven, .. eight], [Omitted(10)]);
        Assert.DoesNotContain(operations, o => o.Direction == "short" && o.Status == "closed");
        Assert.Contains(operations, o => o.Fills.Any(f => f.Fill.Id == eight[0].Id) && o.Coverage.Contains(OperationAggregator.OmittedNotice));
        var sevenIds = seven.Select(f => f.Id).ToHashSet();
        Assert.All(operations, o => Assert.True(o.Fills.All(f => sevenIds.Contains(f.Fill.Id)) || o.Fills.All(f => !sevenIds.Contains(f.Fill.Id))));
        // Without the marker, both are complete round trips (the snapshot shows both markets flat).
        Assert.Contains(Operations([.. seven, .. eight], []), o => o.Fills.Any(f => f.Fill.Id == eight[0].Id) && o.Status == "closed");
    }

    /// <summary>Sol round 1, finding 5: an unknown operation from an omission on lacked its explanation.</summary>
    [Fact]
    public void Every_unknown_operation_from_an_omission_on_explains_it()
    {
        // A marker at the first retained fill's time: the walk starts unknown.
        Assert.Contains(OperationAggregator.OmittedNotice, Assert.Single(Operations([Fill(0, "buy", "open", 1)], [Omitted(0)])).Coverage);
        // A new unknown operation after a break, before any proof of flatness.
        var operations = Operations([Fill(0, "buy", "open", 1), Fill(10, "sell", "close", 1)], [Omitted(5)], snapshot: null);
        Assert.Equal(2, operations.Count);
        Assert.All(operations, o => Assert.Contains(OperationAggregator.OmittedNotice, o.Coverage));
    }

    /// <summary>Sol round 1, finding 6: every retained forced event was loaded to show 50.</summary>
    [PostgresFact]
    public async Task Activity_reads_only_the_newest_forced_events_and_counts_the_rest_in_sql()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            seed.ForcedEvents.AddRange(Enumerable.Range(0, ActivityService.MaxForcedEventRows + 2).Select(i => new ForcedEvent
            {
                Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, SourceId = $"f{i}", Kind = ForcedEvent.Liquidation,
                ContractId = "BTC", VenueContractId = "1", Side = "sell", Quantity = 1, OccurredAtUtc = Start.AddMinutes(i),
                Exclusion = "excluded", FirstSeenAtUtc = Start, LastSeenAtUtc = Start
            }));
            await seed.SaveChangesAsync();
        }
        await using var db = database.Context(owner);
        var facts = await new ActivityStore(db).ReadAsync(null, default);
        Assert.Equal(ActivityService.MaxForcedEventRows, facts.ForcedEvents.Count);
        Assert.DoesNotContain(facts.ForcedEvents, e => e.SourceId is "f0" or "f1");
        Assert.Equal(ActivityService.MaxForcedEventRows + 2, facts.ForcedEventCounts![account.Id]);
        var coverage = ActivityService.Aggregate(facts).Coverage;
        Assert.Equal(ActivityService.MaxForcedEventRows, coverage.Count(c => c.Notice.StartsWith("Liquidation", StringComparison.Ordinal)));
        Assert.Contains(coverage, c => c.AccountId == account.Id && c.Notice == "2 more venue-forced executions are retained and excluded.");
        // At or below the cap, nothing is counted separately.
        await db.ForcedEvents.Where(e => e.SourceId == "f0" || e.SourceId == "f1").ExecuteDeleteAsync();
        var fewer = await new ActivityStore(db).ReadAsync(null, default);
        Assert.Equal((ActivityService.MaxForcedEventRows, (IReadOnlyDictionary<Guid, int>?)null), (fewer.ForcedEvents.Count, fewer.ForcedEventCounts));
        Assert.DoesNotContain(ActivityService.Aggregate(fewer).Coverage, c => c.Notice.Contains("more venue-forced", StringComparison.Ordinal));
    }
}
