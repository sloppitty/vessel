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
public sealed class ActivityOmissionPostgresTests
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
