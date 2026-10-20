using System.Text.Json;
using System.Text.Json.Serialization;
using Vessel.Domain.Plays;

namespace Vessel.Application.Plays;

/// <summary>Declares an Open Play unresolved (phase 1, 1.3a): a reason, a note and the version the owner saw.</summary>
public sealed record DeclareUnresolvedRequest(
    [property: JsonRequired] long ExpectedVersion,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] string Note);

/// <summary>Returns an Unresolved Play to Open, with the owner's reason.</summary>
public sealed record ReturnToOpenRequest(
    [property: JsonRequired] long ExpectedVersion,
    [property: JsonRequired] string Note);

/// <summary>
/// The Play's execution facts when it was declared unresolved: entered, exited and open quantity, its linked orders with
/// what filled on each, and the integrity problems that kept it from settling. Kept with the declaration, never updated.
/// </summary>
public sealed record UnresolvedEvidenceDto(string Entered, string Exited, string Open, int OrderCount,
    IReadOnlyList<UnresolvedOrderDto> Orders, IReadOnlyList<string> Problems);

/// <summary>One linked order at the declaration: the venue's order ID, its role and level, its status and filled quantity.</summary>
public sealed record UnresolvedOrderDto(string OrderId, string Role, string? EntryId, string? LevelId, string Status, string Filled);

/// <summary>The latest declaration of an Unresolved Play, as Review's exclusion drill-down shows it.</summary>
public sealed record UnresolvedDeclarationDto(string Reason, string? Note, DateTimeOffset DeclaredAtUtc, UnresolvedEvidenceDto? Evidence);

public static class PlayUnresolved
{
    /// <summary>At most this many linked orders are kept in a declaration's evidence; <see cref="UnresolvedEvidenceDto.OrderCount"/> counts all.</summary>
    public const int MaxEvidenceOrders = 200;
    /// <summary>At most this many integrity problems are kept in a declaration's evidence.</summary>
    public const int MaxEvidenceProblems = 50;

    public const string ReasonRule = "Reason must be shared-close, flip, forced-exit-not-retained or other.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ReasonName(UnresolvedReason reason) => reason switch
    {
        UnresolvedReason.SharedClose => "shared-close",
        UnresolvedReason.Flip => "flip",
        UnresolvedReason.ForcedExitNotRetained => "forced-exit-not-retained",
        _ => "other",
    };

    public static UnresolvedReason? ParseReason(string? reason) => reason switch
    {
        "shared-close" => UnresolvedReason.SharedClose,
        "flip" => UnresolvedReason.Flip,
        "forced-exit-not-retained" => UnresolvedReason.ForcedExitNotRetained,
        "other" => UnresolvedReason.Other,
        _ => null,
    };

    /// <summary>The reason as the owner reads it in Review's exclusion drill-down.</summary>
    public static string ReasonLabel(UnresolvedReason reason) => reason switch
    {
        UnresolvedReason.SharedClose => "a shared close",
        UnresolvedReason.Flip => "a flip",
        UnresolvedReason.ForcedExitNotRetained => "a forced exit that was not retained",
        _ => "another reason",
    };

    public static string Serialize(UnresolvedEvidenceDto evidence) => JsonSerializer.Serialize(evidence, Json);

    /// <summary>The stored evidence, or null when there is none or it cannot be read.</summary>
    public static UnresolvedEvidenceDto? Read(string? evidence)
    {
        if (string.IsNullOrEmpty(evidence)) return null;
        try { return JsonSerializer.Deserialize<UnresolvedEvidenceDto>(evidence, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The declaration an Unresolved Play is in: the one its latest transition produced, by the Play version each change
    /// keeps, so a tied or stepped-back clock never picks an earlier one. A change without a version falls back to the one
    /// at the Play's end time, then the latest by time. Null when it has none.
    /// </summary>
    public static PlayStatusChange? Current(IEnumerable<PlayStatusChange> changes, DateTimeOffset? endedAtUtc) =>
        changes.Where(c => c.To == PlayStatus.Unresolved && c.UnresolvedReason is not null)
            .OrderByDescending(c => c.PlayVersion ?? long.MinValue).ThenByDescending(c => c.OccurredAtUtc == endedAtUtc)
            .ThenByDescending(c => c.OccurredAtUtc).ThenByDescending(c => c.Id).FirstOrDefault();

    /// <summary>An Unresolved Play's current declaration, as Review's exclusion drill-down shows it, or null.</summary>
    public static UnresolvedDeclarationDto? Latest(IEnumerable<PlayStatusChange> changes, DateTimeOffset? endedAtUtc) =>
        Current(changes, endedAtUtc) is { } c ? new UnresolvedDeclarationDto(ReasonName(c.UnresolvedReason!.Value), c.Note, c.OccurredAtUtc, Read(c.Evidence)) : null;
}
