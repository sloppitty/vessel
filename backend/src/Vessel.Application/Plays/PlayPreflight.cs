using System.Text.Json;
using Vessel.Application.Common;

namespace Vessel.Application.Plays;

/// <summary>One pre-flight check as the owner saw it at Plan it: its code and passed, skipped or unknown.</summary>
public sealed record PreflightCheckDto(string Code, string Outcome);

/// <summary>
/// The non-blocking checklist recorded with plan revision 1 (phase 1, 1.5). The browser evaluates the checks against what
/// the owner sees; the server accepts only known codes and outcomes, keeps them in a fixed order and never blocks planning.
/// </summary>
public static class PlayPreflight
{
    /// <summary>In display order: priced stops, allocations, thesis and invalidation, risk within the setting, liquidation.</summary>
    public static readonly IReadOnlyList<string> Codes = ["priced-stops", "allocations", "thesis", "risk", "liquidation"];
    public static readonly IReadOnlyList<string> Outcomes = ["passed", "skipped", "unknown"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Validated checks in the fixed order, or null when none were sent (an older client).</summary>
    public static IReadOnlyList<PreflightCheckDto>? Normalize(IReadOnlyList<PreflightCheckDto>? checks)
    {
        if (checks is null) return null;
        if (checks.Count > Codes.Count || checks.Any(c => c is null || !Codes.Contains(c.Code) || !Outcomes.Contains(c.Outcome)) ||
            checks.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count() != checks.Count)
            throw new ProblemException(400, "Pre-flight checks are priced-stops, allocations, thesis, risk and liquidation, " +
                "each once, as passed, skipped or unknown.");
        return checks.OrderBy(c => Codes.ToList().IndexOf(c.Code)).ToList();
    }

    public static string? Serialize(IReadOnlyList<PreflightCheckDto>? checks) => checks is null ? null : JsonSerializer.Serialize(checks, Json);

    /// <summary>The stored checks, or null when the revision has none. Unknown stored entries are dropped, never guessed.</summary>
    public static IReadOnlyList<PreflightCheckDto>? Read(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var checks = JsonSerializer.Deserialize<List<PreflightCheckDto>>(stored, Json) ?? [];
        return checks.Where(c => c is not null && Codes.Contains(c.Code) && Outcomes.Contains(c.Outcome)).ToList();
    }
}
