using Vessel.Application.Activity;
using Vessel.Application.Sizing;
using Vessel.Domain.Sizing;

namespace Vessel.Application.Review;

/// <summary>
/// A closed Activity position on no Play, scored like a Play: the reported net result over the exact entry notional,
/// with no plan, R or review. <paramref name="Outcome"/> is null for a closed position Review cannot score.
/// </summary>
public sealed record ReviewPosition(string OperationId, Guid AccountId, string ContractId, string? Direction,
    DateTimeOffset ClosedAtUtc, PlayOutcome? Outcome);

public interface IReviewPositionSource
{
    /// <summary>Closed positions of these enabled accounts that no Play claims, scorable or not.</summary>
    Task<IReadOnlyList<ReviewPosition>> ReadAsync(IReadOnlySet<Guid> accountIds, CancellationToken ct);
}

/// <summary>
/// The sizing fallback's eligibility rule (closed, complete, known net and notional, no ambiguous identity, no known missing
/// execution), restricted to positions whose fills belong to no Play, so a Play's result is never counted twice.
/// </summary>
public sealed class ActivityReviewPositions(ActivityService activity, ISizingStore store) : IReviewPositionSource
{
    public async Task<IReadOnlyList<ReviewPosition>> ReadAsync(IReadOnlySet<Guid> accountIds, CancellationToken ct)
    {
        if (accountIds.Count == 0) return [];
        var (complete, omitted) = await SizingService.CompleteOperationsAsync(activity, store, accountIds, ct);
        var scored = SizingService.ActivityOutcomes(complete.Where(Unclaimed))
            .ToDictionary(s => s.Row.OperationId!, s => s.Outcome, StringComparer.Ordinal);
        return complete.Concat(omitted).Where(Unclaimed).Select(o => new ReviewPosition(o.Id, o.AccountId, o.ContractId, o.Direction,
            o.ClosedAtUtc ?? o.LastOccurredAtUtc, scored.GetValueOrDefault(o.Id))).ToList();
    }

    private static bool Unclaimed(ActivityOperationDto operation) => operation.PlayAssociation == "unassigned";
}
