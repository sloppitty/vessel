using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vessel.Domain.Common;

/// <summary>
/// Which journal a request, account, portfolio or Play belongs to: the real one, or the paper playground. No member is
/// zero, so an unset value is never mistaken for either mode. On the wire and in the database it is the exact string
/// <c>real</c> or <c>paper</c> (<see cref="TradingModes"/>).
/// </summary>
[JsonConverter(typeof(TradingModeJsonConverter))]
public enum TradingMode
{
    Real = 1,
    Paper = 2,
}

/// <summary>The one mapping between <see cref="TradingMode"/> and its wire and stored strings.</summary>
public static class TradingModes
{
    public const string RealValue = "real";
    public const string PaperValue = "paper";
    public const string Invalid = "Trading mode must be real or paper.";

    public static string ToValue(this TradingMode mode) => mode switch
    {
        TradingMode.Real => RealValue,
        TradingMode.Paper => PaperValue,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown trading mode."),
    };

    public static bool TryParse(string? value, out TradingMode mode)
    {
        mode = value switch { RealValue => TradingMode.Real, PaperValue => TradingMode.Paper, _ => default };
        return mode != default;
    }

    /// <summary>A defined mode; an unset or out-of-range value is refused rather than read as either journal.</summary>
    public static TradingMode Validate(TradingMode mode) =>
        mode is TradingMode.Real or TradingMode.Paper ? mode : throw new DomainValidationException(Invalid);

    /// <summary>The exact stored or sent value; anything else is a 400.</summary>
    public static TradingMode Parse(string? value) =>
        TryParse(value, out var mode) ? mode : throw new DomainValidationException(Invalid);

    /// <summary>
    /// A request's mode, where an absent query parameter or body field has always meant the real journal. This is the
    /// wire rule, not a default for callers: code past the request boundary passes a <see cref="TradingMode"/>.
    /// </summary>
    public static TradingMode FromRequest(string? value) => value is null ? TradingMode.Real : Parse(value);
}

public sealed class TradingModeJsonConverter : JsonConverter<TradingMode>
{
    public override TradingMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && TradingModes.TryParse(reader.GetString(), out var mode)
            ? mode : throw new JsonException(TradingModes.Invalid);

    public override void Write(Utf8JsonWriter writer, TradingMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToValue());
}
