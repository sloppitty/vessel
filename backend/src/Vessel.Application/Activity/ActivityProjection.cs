using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Activity;

/// <summary>
/// Issue #108: one stored operation. <see cref="Summary"/> is the list row exactly as the version hashes it
/// (<see cref="ActivityOperationSummaryDto"/> serialized with default options); the other columns are what SQL filters and
/// orders by, and what the unrealized marks read beside the row (the partition and the remaining cost basis).
/// </summary>
public sealed record ActivityProjectedOperation(string Id, DateTimeOffset LastOccurredAtUtc, string ContractId, string? Direction,
    string Status, bool Partial, string ContractIdentity, bool ContractNative, string? BasisNotional, string? BasisQuantity, string Summary);

/// <summary>
/// Issue #108: one account's part of the Activity aggregate and what it was built from. The aggregate partitions by account,
/// so each account's operations, its own coverage notices and its unattributed funding depend on its facts alone; the
/// forced-event rows, funding coverage notices and the order of the whole list are assembled per read.
/// <see cref="ObservationKey"/> and the snapshot window say which observations the part stays valid for (issue #107's rule).
/// </summary>
public sealed record ActivityProjectedAccount(Guid AccountId, string ObservationKey, DateTimeOffset? SnapshotAtUtc,
    DateTimeOffset? SnapshotAfterUtc, DateTimeOffset? SnapshotBeforeUtc, bool SnapshotPinned, int FillCount,
    IReadOnlyList<string> Contracts, IReadOnlyList<ActivityCoverageDto> Coverage, IReadOnlyList<OperationFundingDto> UnassignedFunding,
    IReadOnlyList<ActivityProjectedOperation> Operations);

/// <summary>
/// Issue #108: what a stored account part keeps for page reads, without its operations: the build's identity (a new one for
/// every rebuild, so a version cached for it never outlives it), its counts, contracts, coverage and unattributed funding.
/// </summary>
public sealed record ActivityProjectedAccountState(Guid AccountId, long BuildId, int FillCount, int OperationCount,
    IReadOnlyList<string> Contracts, IReadOnlyList<ActivityCoverageDto> Coverage, IReadOnlyList<OperationFundingDto> UnassignedFunding);

/// <summary>
/// Issue #108: the Activity read model's rules, shared by the API read, the rebuild and the verify commands. Every figure
/// comes from <see cref="ActivityService.Aggregate"/>; this only splits its result by account and puts it back together.
/// </summary>
public static class ActivityProjection
{
    /// <summary>One account's part from facts read for that account alone, or null when it is not an enabled real account.</summary>
    public static ActivityProjectedAccount? Build(ActivityFacts facts, Guid accountId, IVenueRegistry? venues) =>
        Split(ActivityService.Aggregate(facts, TradingMode.Real, accountId, null, venues)).SingleOrDefault(a => a.AccountId == accountId);

    /// <summary>
    /// Every account's part of a real aggregate. Applied to an aggregate over several accounts it gives what each account's
    /// own aggregate stores, which <c>activity-verify</c> compares.
    /// </summary>
    public static IReadOnlyList<ActivityProjectedAccount> Split(ActivitySummaries summaries)
    {
        var facts = summaries.Facts;
        var operations = summaries.Operations.GroupBy(o => o.AccountId).ToDictionary(g => g.Key, g => g.ToList());
        var coverage = summaries.Coverage.Take(summaries.AccountCoverageCount).GroupBy(c => c.AccountId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var unassigned = summaries.UnassignedFunding.GroupBy(p => p.AccountId).ToDictionary(g => g.Key, g => g.ToList());
        var fills = facts.Fills.GroupBy(f => f.AccountId).ToDictionary(g => g.Key, g => g.Count());
        var snapshots = facts.Snapshots.ToDictionary(s => s.AccountId);
        var states = facts.FundingStates.ToDictionary(s => s.AccountId);
        return facts.Accounts.Select(account =>
        {
            var rows = operations.GetValueOrDefault(account.Id) ?? [];
            var snapshot = snapshots.GetValueOrDefault(account.Id);
            var (after, before, pinned) = snapshot is null ? (null, null, false) : summaries.SnapshotWindow(account.Id, snapshot.ObservedAtUtc);
            return new ActivityProjectedAccount(account.Id, ObservationKey(account, snapshot, states.GetValueOrDefault(account.Id)),
                snapshot?.ObservedAtUtc, after, before, pinned, fills.GetValueOrDefault(account.Id),
                rows.Select(o => o.ContractId).Distinct().Order(StringComparer.Ordinal).ToList(),
                coverage.GetValueOrDefault(account.Id) ?? [],
                // In a fixed order: the funding read's order is not one, and pages sort these again anyway.
                (unassigned.GetValueOrDefault(account.Id) ?? []).OrderBy(p => p.OccurredAtUtc).ThenBy(p => p.SourceId, StringComparer.Ordinal)
                    .ThenBy(p => p.Id).Select(OperationFunding.ToDto).ToList(),
                rows.Select(Row).ToList());
        }).ToList();
    }

    private static ActivityProjectedOperation Row(ActivityOperationDto o) => new(o.Id, o.LastOccurredAtUtc, o.ContractId, o.Direction,
        o.Status, ActivityFilter.IsPartial(o), o.ContractKey.Identity, o.ContractKey.IsNative,
        o.RemainingBasis?.Notional.ToString(CultureInfo.InvariantCulture), o.RemainingBasis?.Quantity.ToString(CultureInfo.InvariantCulture),
        JsonSerializer.Serialize(ActivityOperationSummaryDto.From(o)));

    /// <summary>
    /// Everything besides retained history that the account's part depends on, as issue #107's rebase compares it: the
    /// account's venue, history notice, enabled state and mode; whether a snapshot exists, its completeness and positions
    /// (market, native market, exact quantity and leverage); and its funding coverage window, P&amp;L basis and notice.
    /// The snapshot's time is checked separately against the window it was built in.
    /// </summary>
    public static string ObservationKey(Account account, AccountSnapshot? snapshot, AccountFundingState? funding)
    {
        const char Unit = '\u001f', Record = '\u001e';
        var text = new StringBuilder()
            .Append(account.VenueId).Append(Unit).Append(account.HistoryNotice ?? "\u0000").Append(Unit)
            .Append(account.IsEnabled).Append(Unit).Append(account.TradingMode.ToValue()).Append(Record);
        if (snapshot is null) text.Append("no-snapshot");
        else
        {
            text.Append(snapshot.PositionsComplete).Append(Record);
            foreach (var position in snapshot.Positions.Select(p => string.Join(Unit, p.ContractId, p.VenueContractId ?? "\u0000",
                         p.SignedQuantity.ToString(CultureInfo.InvariantCulture), p.Leverage?.ToString(CultureInfo.InvariantCulture) ?? "\u0000"))
                         .Order(StringComparer.Ordinal))
                text.Append(position).Append(Record);
        }
        text.Append(Record);
        if (funding is null) text.Append("no-funding");
        else
            text.Append(funding.CoveredFromUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "\u0000").Append(Unit)
                .Append(funding.CoveredToUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "\u0000").Append(Unit)
                .Append(funding.PnlBasis).Append(Unit).Append(funding.Notice);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Whether a part built at <paramref name="builtAt"/> still holds for a snapshot observed at <paramref name="current"/>.</summary>
    public static bool SnapshotHolds(DateTimeOffset? builtAt, DateTimeOffset? after, DateTimeOffset? before, bool pinned, DateTimeOffset? current)
    {
        if (builtAt is null || current is null) return builtAt is null && current is null;
        if (builtAt.Value == current.Value) return true;
        return !pinned && (after is null || current.Value > after.Value) && (before is null || current.Value < before.Value);
    }

    /// <summary>A stored row as the operation the list and the unrealized marks read; its constituent executions are not stored.</summary>
    public static ActivityOperationDto Operation(string summaryJson, string contractIdentity, bool contractNative, string? basisNotional,
        string? basisQuantity)
    {
        var s = JsonSerializer.Deserialize<ActivityOperationSummaryDto>(summaryJson)
            ?? throw new InvalidOperationException("A stored Activity operation is empty.");
        return new(s.Id, s.AccountId, s.ContractId, s.Direction, s.Status, s.OpenedAtUtc, s.ClosedAtUtc, s.LastOccurredAtUtc,
            s.Quantity, s.TotalEnteredQuantity, s.TotalExitedQuantity, s.OpenQuantity, s.AverageEntryPrice, s.AverageExitPrice,
            s.EntryOrderCount, s.ExitOrderCount, s.FillCount, s.ReportedClosedPnlUsd, s.StablecoinFeesNominalUsd, s.OtherFees,
            s.NetResultUsd, s.PnlBasis, s.PlayId, s.PlayAssociation, s.Coverage, [])
        {
            GrossClosedPnlUsd = s.GrossClosedPnlUsd, TotalResultUsd = s.TotalResultUsd, FundingNetUsd = s.FundingNetUsd,
            FundingStatus = s.FundingStatus, FundingPnlBasis = s.FundingPnlBasis, FundingAsOfUtc = s.FundingAsOfUtc,
            FundingPaymentCount = s.FundingPaymentCount, Leverage = s.Leverage, LeverageMin = s.LeverageMin, LeverageMax = s.LeverageMax,
            TradingMode = s.TradingMode, ExecutionSource = s.ExecutionSource, PaperExecutionCount = s.PaperExecutionCount,
            ContractKey = new(s.AccountId, contractIdentity, contractNative),
            RemainingBasis = basisNotional is null || basisQuantity is null ? null
                : new CostBasis(BigInteger.Parse(basisNotional, CultureInfo.InvariantCulture), BigInteger.Parse(basisQuantity, CultureInfo.InvariantCulture))
        };
    }

    /// <summary>
    /// The page's coverage in the aggregate's order: each scoped account's own notices in <paramref name="accounts"/>' order
    /// (the observation read's), the newest forced events and the count of the rest, funding coverage notices, then
    /// unattributed funding counts.
    /// </summary>
    public static List<ActivityCoverageDto> Coverage(IReadOnlyList<Account> accounts, IReadOnlyDictionary<Guid, ActivityProjectedAccountState> parts,
        IReadOnlyList<ForcedEvent> forced, IReadOnlyDictionary<Guid, int>? forcedCounts, IReadOnlyList<AccountFundingState> fundingStates)
    {
        var ids = accounts.Select(a => a.Id).ToHashSet();
        var coverage = accounts.SelectMany(a => parts[a.Id].Coverage).ToList();
        var (rows, counts) = ActivityService.Forced(forced, forcedCounts, ids.Contains);
        coverage.AddRange(ActivityService.CoverageTail(rows, counts, fundingStates.Where(s => ids.Contains(s.AccountId)),
            accounts.Where(a => parts[a.Id].UnassignedFunding.Count > 0).Select(a => (a.Id, parts[a.Id].UnassignedFunding.Count))));
        return coverage;
    }

    /// <summary>The scope's version from its stored list rows in page order, as <see cref="ActivityService.Aggregate"/> computes it.</summary>
    public static Task<string> VersionAsync(IAsyncEnumerable<string> operationJson, IEnumerable<ActivityCoverageDto> coverage,
        IEnumerable<FundingPayment> funding, IEnumerable<AccountFundingState> fundingStates, IEnumerable<ActivityOmission> omissions,
        CancellationToken ct) =>
        ActivityService.VersionAsync(operationJson, coverage, funding, fundingStates, omissions, ct);
}
