using Vessel.Application.Activity;
using Vessel.Application.Sizing;
using Vessel.Domain.Sizing;

namespace Vessel.Application.Review;

/// <summary>
/// A closed Activity position on no Play, scored like a Play: the reported net result over the exact entry notional,
/// with no plan, R or review. <paramref name="Outcome"/> is null for a closed position Review cannot score.
/// </summary>
public sealed record ReviewPosition(string OperationId, Guid AccountId, string ContractId, string? Direction,
    DateTimeOffset ClosedAtUtc, PlayOutcome? Outcome)
{
    /// <summary>The operation's fees by token as Activity reports them (stablecoins at nominal USD), for the CSV export.</summary>
    public string? Fees { get; init; }
    /// <summary>Why Review cannot score it, when <see cref="Outcome"/> is null.</summary>
    public string? Omission { get; init; }
}

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
        var kept = complete.ToHashSet(ReferenceEqualityComparer.Instance);
        return complete.Concat(omitted).Where(Unclaimed).Select(o => new ReviewPosition(o.Id, o.AccountId, o.ContractId, o.Direction,
            o.ClosedAtUtc!.Value, scored.GetValueOrDefault(o.Id))
        {
            Fees = Fees(o),
            Omission = scored.ContainsKey(o.Id) ? null : Omission(o, kept.Contains(o)),
        }).ToList();
    }

    /// <summary>"stablecoins (nominal USD)=0.3; HYPE=0.01", the way Activity totals them; empty parts are left out.</summary>
    private static string Fees(ActivityOperationDto o) => string.Join("; ",
        new[] { $"stablecoins (nominal USD)={o.StablecoinFeesNominalUsd}" }
            .Concat(o.OtherFees.OrderBy(f => f.Token, StringComparer.Ordinal).Select(f => $"{f.Token}={f.Amount}")));

    /// <summary>The sizing fallback's rule, read back as the reason a closed position is left out.</summary>
    private static string Omission(ActivityOperationDto o, bool complete) =>
        complete ? "Its return or entry notional is outside what Review can score." :
        o.Status == "incomplete" ? "Its execution history is incomplete." :
        o.Coverage.Contains(ActivityPositions.AmbiguousIdentityNotice) ? "Its fills cannot be told apart by native market." :
        o.Direction is null ? "Its direction is unknown." :
        o.NetResultUsd is null ? "Its net result is unknown." :
        o.ExactEntryNotional is not { Sign: > 0 } ? "Its entry notional is unknown." :
        "An order of the position has known missing executions.";

    private static bool Unclaimed(ActivityOperationDto operation) => operation.PlayAssociation == "unassigned";
}
