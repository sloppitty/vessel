using System.Text.Json;
using System.Text.Json.Serialization;
using Vessel.Application.Common;
using Vessel.Domain.Plays;

namespace Vessel.Application.Plays;

/// <summary>
/// The rule version Plan it showed (Phase 2, item 3) and the owner's answer, if any: acknowledged, or an exception with a
/// reason. A request without it records no rule, whatever is active (Sol round 1 on item 3).
/// </summary>
public sealed record PlanRuleAnswerDto([property: JsonRequired] int Version, string? Answer = null, string? Reason = null);

/// <summary>The rule a Play was planned under, as revision 1 keeps it: its version, text and the owner's answer, if any.</summary>
public sealed record PlanRuleSnapshotDto(int Version, string If, string Then, Guid? SourcePlayId, string? Answer, string? Reason);

/// <summary>The rule a Play carries from Plan it, for its review's "Followed the rule?".</summary>
public sealed record PlayRuleSeenDto(int Version, string If, string Then);

/// <summary>An if-then rule version. <paramref name="SourceTitle"/> names the Play it was promoted from, when there is one.</summary>
public sealed record PlayRuleDto(int Version, string If, string Then, Guid? SourcePlayId, string? SourceTitle, DateTimeOffset CreatedAtUtc);

/// <summary>The active rule in one trading mode, or none.</summary>
public sealed record PlayRuleStateDto(PlayRuleDto? Active, string TradingMode);

/// <summary>
/// Makes a new rule version the active one. <paramref name="ExpectedVersion"/> is the active version the owner saw, null
/// when there was none; a different active version is a 409. <paramref name="SourcePlayId"/> names the Play whose next
/// action was promoted.
/// </summary>
public sealed record SetPlayRuleRequest([property: JsonRequired] string If, [property: JsonRequired] string Then, Guid? SourcePlayId = null,
    int? ExpectedVersion = null);

/// <summary>The if-then rule's answers and the snapshot kept with revision 1 (Phase 2, item 3).</summary>
public static class PlayRules
{
    public const string Acknowledged = "acknowledged", Exception = "exception";
    public const string Changed = "The if-then rule changed since you read it. Reload it.";
    public const int MaxReasonLength = 500;
    public static readonly IReadOnlyList<string> ReviewAnswers = ["followed", "deviated", "not-applicable"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What Plan it keeps of the rule the owner was shown: its text and version, and their answer, if any. Nothing is kept
    /// when no rule was shown, so an unseen rule is never attributed to the Play. A shown version that is no longer the
    /// active one is a 409: the rule changed or was archived since. An exception needs a reason.
    /// </summary>
    public static PlanRuleSeen? Seen(PlayRule? active, PlanRuleAnswerDto? shown)
    {
        if (shown is null) return null;
        if (active is null)
            throw new ProblemException(409, "The if-then rule was archived since you saw it. Plan it again.");
        if (shown.Version != active.Version)
            throw new ProblemException(409, "The if-then rule changed since you saw it. Review it and plan it again.");
        var reason = string.IsNullOrWhiteSpace(shown.Reason) ? null : shown.Reason.Trim();
        if (shown.Answer is not (null or Acknowledged or Exception) ||
            shown.Answer == Exception && reason is null || shown.Answer != Exception && reason is not null ||
            reason is { Length: > MaxReasonLength } || reason?.Any(c => char.IsControl(c) && c is not ('\r' or '\n')) == true)
            throw new ProblemException(400, $"Answer the rule as acknowledged, or as an exception with a reason of at most {MaxReasonLength} characters.");
        var snapshot = new PlanRuleSnapshotDto(active.Version, active.If, active.Then, active.SourcePlayId, shown.Answer, reason);
        return new(active.Version, active.If, active.Then, JsonSerializer.Serialize(snapshot, Json));
    }

    public static PlanRuleSnapshotDto? Read(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? null : JsonSerializer.Deserialize<PlanRuleSnapshotDto>(stored, Json);
}
