using System.Numerics;
using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Domain.Numerics;

namespace Vessel.Infrastructure.Venues.Common;

/// <summary>
/// Strict readers for venue JSON. Every failure is a <see cref="VenueReadException"/> with the venue's own
/// "invalid response" message, so no payload detail reaches logs or clients. Decimals keep their exact text and
/// value: anything a 96-bit coefficient with scale ≤ 28 cannot hold is rejected rather than rounded.
/// </summary>
public sealed class StrictJson(string invalidResponse)
{
    public string InvalidResponse { get; } = invalidResponse;

    public VenueReadException Invalid() => new(InvalidResponse) { IsDeterministicSourceFailure = true };

    public JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            throw Invalid();
        return value;
    }

    public JsonElement Array(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Invalid();
        return element;
    }

    public string Text(JsonElement element, int maxLength = 128)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw Invalid();
        var value = element.GetString()!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw Invalid();
        return value;
    }

    /// <summary>Like <see cref="Text"/>, but an empty string is allowed (some venues send "" for "none").</summary>
    public string TextOrEmpty(JsonElement element, int maxLength = 128)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw Invalid();
        var value = element.GetString()!;
        if (value.Length > maxLength || value.Any(char.IsControl))
            throw Invalid();
        return value;
    }

    /// <summary>Optional reported amount. Missing, null and empty string mean unknown, never zero.</summary>
    public decimal? OptionalAmount(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = TextOrEmpty(value);
        return text.Length == 0 ? null : ParseDecimal(text);
    }

    public int Integer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            throw Invalid();
        return value;
    }

    public bool Boolean(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Invalid()
    };

    /// <summary>A Unix millisecond timestamp, no later than <paramref name="latestTimestamp"/>.</summary>
    public DateTimeOffset Timestamp(JsonElement element, long latestTimestamp)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value) ||
            value < 0 || value > latestTimestamp)
            throw Invalid();
        return DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    public decimal Positive(JsonElement element)
    {
        var value = Number(element);
        if (value <= 0)
            throw Invalid();
        return value;
    }

    public decimal Nonnegative(JsonElement element)
    {
        var value = Number(element);
        if (value < 0)
            throw Invalid();
        return value;
    }

    /// <summary>A decimal sent as a JSON string.</summary>
    public decimal Number(JsonElement element) => ParseDecimal(Text(element));

    /// <summary>The exact text of a decimal sent as a string, or as a raw JSON number when <paramref name="numbers"/> is set.</summary>
    public string DecimalText(JsonElement element, bool numbers) =>
        numbers && element.ValueKind == JsonValueKind.Number ? element.GetRawText() : Text(element);

    /// <summary>A venue amount under the <see cref="ExactDecimal.TryParseVenue"/> profile.</summary>
    public decimal ParseDecimal(string text) => ExactDecimal.TryParseVenue(text, out var value) ? value : throw Invalid();

    /// <summary>
    /// The magnitude at a common scale, for checking that decimal arithmetic did not round. The sign is dropped, as it
    /// always was: callers compare nonnegative amounts. <see cref="ExactDecimal.Units"/> keeps the sign.
    /// </summary>
    public static BigInteger DecimalUnits(decimal value) => BigInteger.Abs(ExactDecimal.Units(value));

    public void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Invalid();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                RejectDuplicateProperties(child);
        }
    }
}
