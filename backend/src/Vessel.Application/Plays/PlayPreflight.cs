using System.Text.Json;
using System.Text.Json.Serialization;
using Vessel.Application.Common;

namespace Vessel.Application.Plays;

/// <summary>
/// One pre-flight check as the owner saw it at Plan it: its code and passed, skipped or unknown. <paramref name="Note"/> is
/// the owner's optional cooldown note, only on a <c>loss-limit</c> check the owner went ahead without (Phase 2, item 10b).
/// </summary>
public sealed record PreflightCheckDto(string Code, string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null);

/// <summary>
/// The non-blocking checklist recorded with plan revision 1 (phase 1, 1.5). The browser evaluates the checks against what
/// the owner sees; the server accepts only known codes and outcomes, keeps them in a fixed order and never blocks planning.
/// </summary>
public static class PlayPreflight
{
    /// <summary>
    /// In display order: priced stops, allocations, thesis and invalidation, risk within the setting, liquidation, and the soft
    /// loss limits (Phase 2, item 10b), sent only while the owner has one set.
    /// </summary>
    public static readonly IReadOnlyList<string> Codes = ["priced-stops", "allocations", "thesis", "risk", "liquidation", LossLimit];
    public const string LossLimit = "loss-limit";
    public const int MaxNoteLength = 500;
    public static readonly IReadOnlyList<string> Outcomes = ["passed", "skipped", "unknown"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Validated checks in the fixed order, or null when none were sent (an older client). An empty checklist records
    /// nothing either, so History and Review both read it as not recorded.
    /// </summary>
    public static IReadOnlyList<PreflightCheckDto>? Normalize(IReadOnlyList<PreflightCheckDto>? checks)
    {
        if (checks is null || checks.Count == 0) return null;
        if (checks.Count > Codes.Count || checks.Any(c => c is null || !Codes.Contains(c.Code) || !Outcomes.Contains(c.Outcome)) ||
            checks.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count() != checks.Count)
            throw new ProblemException(400, "Pre-flight checks are priced-stops, allocations, thesis, risk, liquidation and loss-limit, " +
                "each once, as passed, skipped or unknown.");
        // A cooldown note belongs to a reached loss limit only; blank is none.
        checks = checks.Select(c => c with { Note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim() }).ToList();
        if (checks.Any(c => c.Note is { } note && (c.Code != LossLimit || c.Outcome != "skipped" || note.Length > MaxNoteLength ||
                note.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n')))))
            throw new ProblemException(400, $"A cooldown note is plain text of at most {MaxNoteLength} characters, on a reached loss limit only.");
        return checks.OrderBy(c => Codes.ToList().IndexOf(c.Code)).ToList();
    }

    public static string? Serialize(IReadOnlyList<PreflightCheckDto>? checks) => checks is null ? null : JsonSerializer.Serialize(checks, Json);

    /// <summary>
    /// The stored checks, or null when the revision has none. Unknown stored entries are dropped, never guessed, and a
    /// checklist with no known check left counts as not recorded.
    /// </summary>
    public static IReadOnlyList<PreflightCheckDto>? Read(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var checks = (JsonSerializer.Deserialize<List<PreflightCheckDto>>(stored, Json) ?? [])
            .Where(c => c is not null && Codes.Contains(c.Code) && Outcomes.Contains(c.Outcome)).ToList();
        return checks.Count == 0 ? null : checks;
    }
}
