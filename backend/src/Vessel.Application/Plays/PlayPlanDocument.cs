using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vessel.Application.Common;
using Vessel.Domain.Numerics;

namespace Vessel.Application.Plays;

/// <summary>
/// A stop or target of one entry. A percent value is the return on margin at the plan's leverage, so the
/// price distance from the entry is the value divided by the leverage. Share is the part of the entry it closes.
/// </summary>
public sealed record PlanExit(string Id, string Unit, string Value, string Share);
public sealed record PlanEntry(string Id, string Name, string Color, string Share, string Price, IReadOnlyList<PlanExit> Stops, IReadOnlyList<PlanExit> Targets);
public sealed record PlanNotes(string Thesis, string Invalidation, string Strategy, string Evidence);

/// <summary>
/// What the owner intends: direction, sizing, entries with their exits, and the pre-trade notes. Number
/// fields keep the exact text entered; a Draft may leave them blank. Plan revisions snapshot this document.
/// </summary>
public sealed record PlayPlanDocument(string Direction, string SizingMode, string Size, string Leverage, string? BudgetOverride,
    IReadOnlyList<PlanEntry> Entries, PlanNotes Notes);

/// <summary>Validates and normalizes plans and chart drawings before they are stored.</summary>
public static partial class PlayDocuments
{
    public const int MaxEntries = 20;
    public const int MaxTargets = 10;
    public const int MaxStops = 10;
    public const int MaxNoteLength = 20000;
    public const int MaxDrawingsLength = 1_000_000;
    public const int MaxDrawingInstruments = 100;
    public const int MaxDrawingsPerInstrument = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What a plan number must be on write; the field's name goes before it.</summary>
    public const string PlainDecimalRule =
        "use a plain decimal such as 100 or 0.5, with no sign, exponent or spaces, at most 64 characters and within decimal precision (28 decimal places).";

    /// <summary>
    /// Validates a plan for saving. Number fields must be plain decimals (<see cref="ExactDecimal.IsPlanText"/>), except
    /// that a field whose text is unchanged from <paramref name="stored"/> (the Play's current plan) still saves, so an
    /// earlier plan never becomes unsaveable and nothing stored is rewritten. Unchanged means the same field of the same
    /// item: an entry's price or share by entry ID, a stop's or target's value (in the same unit) or share by entry and
    /// level ID, and the plan's size or budget. A negative percentage is refused with a pointer to a price stop.
    /// </summary>
    public static PlayPlanDocument Normalize(PlayPlanDocument? plan, PlayPlanDocument? stored = null)
    {
        if (plan is null) throw Invalid("A plan is required.");
        if (plan.Direction is not ("long" or "short")) throw Invalid("Direction must be long or short.");
        if (plan.SizingMode is not ("margin" or "quantity")) throw Invalid("Sizing must be margin or quantity.");
        if (plan.Leverage is null || !int.TryParse(plan.Leverage, NumberStyles.None, CultureInfo.InvariantCulture, out var leverage) ||
            leverage is < 1 or > 100 || plan.Leverage != leverage.ToString(CultureInfo.InvariantCulture))
            throw Invalid("Leverage must be a whole number from 1 to 100.");
        if (plan.Entries is not { Count: > 0 and <= MaxEntries }) throw Invalid($"A plan has 1 to {MaxEntries} entries.");
        if (plan.Notes is null) throw Invalid("Plan notes are required.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string Id(string? id) => id is not null && IdPattern().IsMatch(id) && ids.Add(id) ? id : throw Invalid("Plan items need unique IDs.");
        var kept = StoredNumbers(stored);
        string Number(string? value, string key, string label) =>
            ExactDecimal.IsPlanText(value) || value is not null && kept.TryGetValue(key, out var text) && text == value
                ? value : throw Invalid($"{label}: {PlainDecimalRule}");
        var entries = plan.Entries.Select(entry =>
        {
            if (entry is null || entry.Stops is null || entry.Targets is null) throw Invalid("Entries need stop and target lists.");
            if (entry.Targets.Count > MaxTargets) throw Invalid($"An entry has at most {MaxTargets} targets.");
            if (entry.Stops.Count > MaxStops) throw Invalid($"An entry has at most {MaxStops} stops.");
            if (entry.Name is null || entry.Name.Length is 0 or > 100 || entry.Name.Any(char.IsControl)) throw Invalid("Entry names need 1 to 100 characters.");
            if (entry.Color is null || !ColorPattern().IsMatch(entry.Color)) throw Invalid("Entry colours must be #rrggbb.");
            var id = Id(entry.Id);
            return new PlanEntry(id, entry.Name, entry.Color.ToLowerInvariant(),
                Number(entry.Share, EntryKey(id, "share"), $"{entry.Name} share"), Number(entry.Price, EntryKey(id, "price"), $"{entry.Name} price"),
                Exits(entry.Stops, "stop", id, entry.Name), Exits(entry.Targets, "target", id, entry.Name));
        }).ToList();
        List<PlanExit> Exits(IReadOnlyList<PlanExit> exits, string kind, string entryId, string entryName) => exits.Select((exit, index) =>
        {
            if (exit is null) throw Invalid($"{(kind == "stop" ? "Stops" : "Targets")} must not be null.");
            var (id, unit, label) = (Id(exit.Id), Unit(exit.Unit), $"{entryName} {kind} {index + 1}");
            var valueKey = LevelKey(kind, entryId, id, $"{unit} value");
            if (unit == "percent" && !(kept.TryGetValue(valueKey, out var text) && text == exit.Value) &&
                decimal.TryParse(exit.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) && percent < 0)
                throw Invalid(kind == "stop" ? $"{label}: use a price for a stop beyond the entry; a percentage cannot be negative."
                    : $"{label}: a percentage cannot be negative; use a price instead.");
            return new PlanExit(id, unit, Number(exit.Value, valueKey, label), Number(exit.Share, LevelKey(kind, entryId, id, "share"), $"{label} share"));
        }).ToList();
        foreach (var entry in entries) RequireSides(entry, plan.Direction);
        return new PlayPlanDocument(plan.Direction, plan.SizingMode, Number(plan.Size, "size", "Size"), plan.Leverage,
            plan.BudgetOverride is null ? null : Number(plan.BudgetOverride, "budget", "Budget"), entries,
            new PlanNotes(Note(plan.Notes.Thesis), Note(plan.Notes.Invalidation), Note(plan.Notes.Strategy), Note(plan.Notes.Evidence)));
    }

    // Keys of the number fields a stored plan holds, for the unchanged-text rule. Plan IDs never contain spaces.
    private static string EntryKey(string entryId, string field) => $"entry {entryId} {field}";
    private static string LevelKey(string kind, string entryId, string levelId, string field) => $"{kind} {entryId} {levelId} {field}";

    private static Dictionary<string, string> StoredNumbers(PlayPlanDocument? stored)
    {
        var numbers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (stored is null) return numbers;
        void Keep(string key, string? text) { if (text is not null) numbers[key] = text; }
        Keep("size", stored.Size);
        Keep("budget", stored.BudgetOverride);
        foreach (var entry in stored.Entries ?? [])
        {
            if (entry?.Id is not { } entryId) continue;
            Keep(EntryKey(entryId, "share"), entry.Share);
            Keep(EntryKey(entryId, "price"), entry.Price);
            foreach (var (kind, exits) in new[] { ("stop", entry.Stops), ("target", entry.Targets) })
                foreach (var exit in exits ?? [])
                {
                    if (exit?.Id is not { } levelId) continue;
                    Keep(LevelKey(kind, entryId, levelId, $"{exit.Unit} value"), exit.Value);
                    Keep(LevelKey(kind, entryId, levelId, "share"), exit.Share);
                }
        }
        return numbers;
    }

    /// <summary>
    /// Price stops and targets must sit on their side of a priced entry: long stops below and targets above,
    /// short the reverse. Percent levels are placed by direction, so they always do.
    /// </summary>
    private static void RequireSides(PlanEntry entry, string direction)
    {
        if (Positive(entry.Price) is not { } entryPrice) return;
        foreach (var (kind, exits) in new[] { ("stop", entry.Stops), ("target", entry.Targets) })
        {
            var above = kind == "target" == (direction == "long");
            if (exits.Any(exit => exit.Unit == "price" && Positive(exit.Value) is { } price && (above ? price <= entryPrice : price >= entryPrice)))
                throw Invalid($"{entry.Name}: a {direction} {kind} goes {(above ? "above" : "below")} the entry price.");
        }
    }

    private static decimal? Positive(string value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    /// <summary>A plan is ready to commit when at least one entry has a positive price.</summary>
    public static void RequirePlannable(PlayPlanDocument plan)
    {
        if (!plan.Entries.Any(entry => decimal.TryParse(entry.Price, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) && price > 0))
            throw new ProblemException(409, "Give at least one entry a price before planning.");
    }

    public static string Serialize(PlayPlanDocument plan) => JsonSerializer.Serialize(plan, Json);
    public static PlayPlanDocument Read(string stored) => JsonSerializer.Deserialize<PlayPlanDocument>(stored, Json)!;

    /// <summary>Chart drawings keyed by venue instrument. Their shape belongs to the chart; only bounds are checked here.</summary>
    public static string NormalizeDrawings(JsonElement? drawings)
    {
        if (drawings is not { } value || value.ValueKind == JsonValueKind.Null) return "{}";
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Drawings must be an object keyed by instrument.");
        var count = 0;
        foreach (var instrument in value.EnumerateObject())
        {
            if (++count > MaxDrawingInstruments) throw Invalid($"Drawings are kept for at most {MaxDrawingInstruments} instruments.");
            if (instrument.Name.Length is 0 or > 200) throw Invalid("Drawing instrument keys need 1 to 200 characters.");
            if (instrument.Value.ValueKind != JsonValueKind.Array || instrument.Value.GetArrayLength() > MaxDrawingsPerInstrument ||
                instrument.Value.EnumerateArray().Any(drawing => drawing.ValueKind != JsonValueKind.Object))
                throw Invalid($"Each instrument holds a list of at most {MaxDrawingsPerInstrument} drawings.");
        }
        var text = JsonSerializer.Serialize(value);
        return text.Length <= MaxDrawingsLength ? text : throw Invalid("Drawings are too large to save.");
    }

    /// <summary>The stored drawings as a standalone element; the parsed document returns its pooled buffers.</summary>
    public static JsonElement ReadDrawings(string stored)
    {
        using var document = JsonDocument.Parse(stored);
        return document.RootElement.Clone();
    }

    private static string Unit(string? unit) => unit is "price" or "percent" ? unit : throw Invalid("Level units are price or percent.");

    private static string Note(string? note) => note is not null && note.Length <= MaxNoteLength
        ? note : throw Invalid($"Notes are limited to {MaxNoteLength} characters.");

    private static ProblemException Invalid(string detail) => new(400, detail);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();
}
