using Vessel.Application.Workspace;
using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;
using System.Globalization;
using System.Numerics;

namespace Vessel.Application.Activity;

/// <summary>Funding belongs to the native position interval, never to a fill, order or inferred Play.</summary>
internal static class OperationFunding
{
    internal sealed record Result(IReadOnlyList<ActivityOperationDto> Operations, IReadOnlyList<FundingPayment> Unassigned);

    public static Result Apply(IReadOnlyList<ActivityOperationDto> operations, ActivityFacts facts, ActivityPositions positions,
        IReadOnlySet<Guid> fundingAccounts)
    {
        var assigned = new Dictionary<string, List<FundingPayment>>();
        var ambiguous = new HashSet<string>();
        var unassigned = new List<FundingPayment>();
        // Same-time flips finish zero-duration intervals before the interval continuing past that instant.
        var windows = operations.Where(o => o.OpenedAtUtc is not null).GroupBy(o => o.ContractKey)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.OpenedAtUtc)
                .ThenBy(o => o.ClosedAtUtc ?? DateTimeOffset.MaxValue).ThenBy(o => o.Id, StringComparer.Ordinal).ToList());
        var boundaries = operations.SelectMany(o =>
                new[] { o.OpenedAtUtc, o.ClosedAtUtc }.OfType<DateTimeOffset>().Select(at => (o.ContractKey, At: Time(at), o.Id)))
            .GroupBy(p => (p.ContractKey, p.At)).ToDictionary(g => g.Key, g => g.Select(p => p.Id).Distinct().ToList());
        // Legacy execution imports can have only millisecond precision even when funding supplies native ns.
        // Do not manufacture event ordering inside a boundary's precision bucket.
        var boundaryMilliseconds = boundaries.GroupBy(p => (p.Key.ContractKey, Millisecond: p.Key.At / 1_000_000))
            .ToDictionary(g => g.Key, g => g.SelectMany(p => p.Value).Distinct().ToList());
        // Whole-second settlements may be coarser than fills (e.g. Lighter); precision is not inferred from venue names.
        var boundarySeconds = boundaries.GroupBy(p => (p.Key.ContractKey, Second: p.Key.At / 1_000_000_000))
            .ToDictionary(g => g.Key, g => g.SelectMany(p => p.Value).Distinct().ToList());
        foreach (var group in facts.FundingPayments.GroupBy(positions.Key))
        {
            var intervals = windows.GetValueOrDefault(group.Key) ?? [];
            var index = -1;
            foreach (var payment in group.OrderBy(Time).ThenBy(p => p.SourceId, StringComparer.Ordinal))
            {
                var at = Time(payment);
                var foundBoundary = payment.OccurredAtUnixNanoseconds is not null
                    ? boundaryMilliseconds.TryGetValue((group.Key, at / 1_000_000), out var sameTime)
                    : at % 1_000_000_000 == 0
                    ? boundarySeconds.TryGetValue((group.Key, at / 1_000_000_000), out sameTime)
                    : boundaries.TryGetValue((group.Key, at), out sameTime);
                if (foundBoundary)
                {
                    ambiguous.UnionWith(sameTime!); unassigned.Add(payment);
                    continue;
                }
                while (index + 1 < intervals.Count && Time(intervals[index + 1].OpenedAtUtc!.Value) <= at) index++;
                var current = index < 0 ? null : intervals[index];
                // The APIs do not supply a shared ordering between a fill and a funding settlement at the same instant.
                if (current is null || current.ClosedAtUtc is { } closed && Time(closed) < at)
                {
                    unassigned.Add(payment);
                    continue;
                }
                if (!assigned.TryGetValue(current.Id, out var rows)) assigned[current.Id] = rows = [];
                rows.Add(payment);
            }
        }
        var states = facts.FundingStates.ToDictionary(s => s.AccountId);
        return new(operations.Select(operation =>
        {
            if (!fundingAccounts.Contains(operation.AccountId)) return operation;
            var state = states.GetValueOrDefault(operation.AccountId);
            var payments = assigned.GetValueOrDefault(operation.Id) ?? [];
            var supported = payments.All(p => NominalStablecoins.Contains(p.Asset));
            var amount = payments.Aggregate(default(ExactAmount), (sum, p) =>
                NominalStablecoins.Contains(p.Asset) ? sum + ExactAmount.From(p.Amount) : sum);
            var end = End(operation.ClosedAtUtc, operation.LastOccurredAtUtc, positions.Snapshots.GetValueOrDefault(operation.AccountId));
            var complete = operation.OpenedAtUtc is { } start &&
                state is { CoveredFromUtc: { } coveredFrom, CoveredToUtc: { } coveredTo } &&
                end >= start && coveredFrom <= start && coveredTo >= end && !ambiguous.Contains(operation.Id) && supported &&
                positions.Quantity(operation.ContractKey) is not null;
            var status = complete ? "complete" : payments.Count > 0 || state is not null ? "partial" : "unavailable";
            var basis = state?.PnlBasis ?? FundingFacts.Unknown;
            var coverage = operation.Coverage.ToList();
            if (!complete) coverage.Add("Funding coverage is incomplete or attribution is uncertain; missing funding is not zero.");
            if (ambiguous.Contains(operation.Id))
                coverage.Add("A funding settlement shares an opening/closing timestamp or its millisecond precision bucket with an execution; it is not assigned to either operation.");
            if (!supported) coverage.Add("Funding includes an unsupported asset; no currency conversion is performed.");
            if (basis == FundingFacts.Unknown)
                coverage.Add("Whether reported execution P&L includes funding is unverified. Funding is shown separately; the combined net result is unavailable.");
            var net = !complete || operation.NetResultUsd is not { } trading ? null :
                basis == FundingFacts.Included ? trading :
                basis == FundingFacts.Excluded && complete ? (ExactAmount.Parse(trading) + amount).ToString() : null;
            return operation with
            {
                TotalResultUsd = net,
                FundingNetUsd = supported && (payments.Count > 0 || complete) ? amount.ToString() : null,
                FundingStatus = status, FundingPnlBasis = basis, FundingAsOfUtc = state?.CoveredToUtc,
                FundingPaymentCount = payments.Count, FundingPayments = payments, Coverage = coverage,
                FundingAmbiguous = ambiguous.Contains(operation.Id), FundingUnsupported = !supported
            };
        }).ToList(), unassigned);
    }

    /// <summary>
    /// Where an interval's funding ends: at its close, or while open at the later of its last execution and the account's
    /// latest snapshot, in whole milliseconds as the shared refresh bounds are (a venue's snapshot can be sub-millisecond).
    /// Phase 2, item 9b ends a Play's holding window by the same rule.
    /// </summary>
    internal static DateTimeOffset End(DateTimeOffset? closedAt, DateTimeOffset lastOccurred, AccountSnapshot? snapshot)
    {
        if (closedAt is { } closed) return closed;
        var end = snapshot is not null && snapshot.ObservedAtUtc > lastOccurred ? snapshot.ObservedAtUtc : lastOccurred;
        return DateTimeOffset.FromUnixTimeMilliseconds(end.ToUnixTimeMilliseconds());
    }

    private static BigInteger Time(DateTimeOffset at) => (BigInteger)(at - DateTimeOffset.UnixEpoch).Ticks * 100;
    private static BigInteger Time(FundingPayment payment) => payment.OccurredAtUnixNanoseconds is { } ns ? ns : Time(payment.OccurredAtUtc);

    public static OperationFundingDto ToDto(FundingPayment payment) => new(payment.Id, payment.AccountId, payment.SourceId,
        payment.ContractId, payment.VenueContractId, payment.OccurredAtUtc, ExactAmount.From(payment.Amount).ToString(), payment.Asset,
        payment.OccurredAtUnixNanoseconds?.ToString(CultureInfo.InvariantCulture));
}
