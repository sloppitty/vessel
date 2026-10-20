using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Vessel.Domain.Numerics;

/// <summary>Why <see cref="ExactDecimal.ParseManualFact"/> refused a value, in the order the checks run.</summary>
public enum ManualFactError
{
    None,
    /// <summary>Not manual-fact text, or a quantity or price that is not positive, or a negative fee.</summary>
    Invalid,
    /// <summary>The written scale is beyond ±96 places.</summary>
    OutOfRange,
    /// <summary>Decimal parsing would round the written value.</summary>
    Inexact,
}

/// <summary>
/// The one home of exact decimal rules. Each entry point keeps its own acceptance rule, named here as a parse
/// profile; no profile rounds its input. Amounts leave the server as <see cref="Format(decimal)"/> text, and sums
/// and checked arithmetic never lose a digit.
/// </summary>
public static partial class ExactDecimal
{
    private const string ExactFormat = "0.############################";
    private const string MaxCoefficient = "79228162514264337593543950335";

    // ---- Parse profiles ----

    /// <summary>Venue JSON: an optional '-' and ASCII digits with at most one inner '.', no exponent; exactly a decimal.</summary>
    public static bool TryParseVenue(string text, out decimal value)
    {
        value = 0;
        var unsigned = text.StartsWith('-') ? text[1..] : text;
        var parts = unsigned.Split('.');
        if (parts.Length > 2 || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
            return false;
        var fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
        var coefficient = (parts[0] + fraction).TrimStart('0');
        // decimal.TryParse alone rounds excess precision. Reject values that cannot
        // be represented exactly by a 96-bit coefficient and a scale of at most 28.
        if (fraction.Length > 28 || coefficient.Length > MaxCoefficient.Length ||
            (coefficient.Length == MaxCoefficient.Length && string.CompareOrdinal(coefficient, MaxCoefficient) > 0))
            return false;
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Manual account value: 1–100 ASCII digits with at most one '.', which may lead or trail; no sign or exponent; exactly a decimal.</summary>
    public static bool TryParseOwnerAmount(string text, out decimal value)
    {
        value = 0;
        if (text.Length is 0 or > 100 || text.Any(c => c is not (>= '0' and <= '9') and not '.') ||
            text.Count(c => c == '.') > 1 || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        // decimal.TryParse alone rounds over-precision input. Round-trip the digits to reject loss.
        static string Normalize(string s)
        {
            var parts = s.Split('.');
            var whole = parts[0].TrimStart('0');
            var fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
            return (whole.Length == 0 ? "0" : whole) + (fraction.Length == 0 ? "" : "." + fraction);
        }
        return Normalize(text) == Normalize(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Risk setting: 1–16 characters that decimal parsing reads as digits with an optional '.'; too short to round.</summary>
    public static bool TryParseRiskPercent([NotNullWhen(true)] string? text, out decimal value)
    {
        value = 0;
        return text is { Length: > 0 and <= 16 } &&
            decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Manual execution fact: up to 96 characters, an optional '+', digits, an optional fraction and a one- or two-digit
    /// exponent; exactly a decimal. A quantity or price must be <paramref name="positive"/>, a fee nonnegative.
    /// </summary>
    public static ManualFactError ParseManualFact(string? text, bool positive, out decimal value)
    {
        value = 0;
        if (text is null || text.Length > 96 || !ManualFactPattern().IsMatch(text) ||
            !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            (positive ? value <= 0 : value < 0))
            return ManualFactError.Invalid;
        // Reject rounding at input. Compare the written digits and scale with the parsed coefficient.
        var pieces = text.ToLowerInvariant().Split('e');
        var mantissa = pieces[0].TrimStart('+');
        var fraction = mantissa.IndexOf('.') is var dot && dot >= 0 ? mantissa.Length - dot - 1 : 0;
        var coefficient = BigInteger.Parse(mantissa.Replace(".", ""), CultureInfo.InvariantCulture);
        var scale = fraction - (pieces.Length == 2 ? int.Parse(pieces[1], CultureInfo.InvariantCulture) : 0);
        if (scale is < -96 or > 96) return ManualFactError.OutOfRange;
        var bits = decimal.GetBits(value);
        var parsed = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        var parsedScale = (bits[3] >> 16) & 0xff;
        if (coefficient * BigInteger.Pow(10, Math.Max(0, parsedScale - scale)) !=
            parsed * BigInteger.Pow(10, Math.Max(0, scale - parsedScale)))
            return ManualFactError.Inexact;
        return ManualFactError.None;
    }

    /// <summary>The longest plan field text.</summary>
    public const int MaxPlanTextLength = 64;

    /// <summary>
    /// Plan field text on write: blank, or 1–64 ASCII digits with at most one inner '.' (no sign, exponent or
    /// whitespace) that a decimal holds exactly. The text is kept as typed. Stored plans are read tolerantly, since
    /// earlier plans kept text this rule now refuses.
    /// </summary>
    public static bool IsPlanText([NotNullWhen(true)] string? text) =>
        text is not null && (text.Length == 0 ||
            text.Length <= MaxPlanTextLength && PlanTextPattern().IsMatch(text) && TryParseVenue(text, out _));

    /// <summary>Adapter amount text: 1–128 characters, an optional '-' and ASCII digits with at most one inner '.'; no range or scale limit.</summary>
    public static bool IsSignedAmountText(string text)
    {
        if (text.Length is < 1 or > 128) return false;
        var number = text.AsSpan();
        if (number[0] == '-') number = number[1..];
        var dot = number.IndexOf('.');
        return dot < 0 ? Digits(number) : Digits(number[..dot]) && Digits(number[(dot + 1)..]);
    }

    private static bool Digits(ReadOnlySpan<char> value) => !value.IsEmpty && value.IndexOfAnyExceptInRange('0', '9') < 0;

    [GeneratedRegex(@"^\+?\d+(?:\.\d+)?(?:[eE][+-]?\d{1,2})?$")]
    private static partial Regex ManualFactPattern();
    // ASCII digits only ("\d" matches other scripts), and "\z" so a final newline cannot pass as "$" would let it.
    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+)?\z")]
    private static partial Regex PlanTextPattern();

    // ---- Formatting ----

    /// <summary>The API text of an amount: invariant, every significant digit, no exponent, grouping or trailing zeros.</summary>
    public static string Format(decimal value) => value.ToString(ExactFormat, CultureInfo.InvariantCulture);
    public static string? Format(decimal? value) => value?.ToString(ExactFormat, CultureInfo.InvariantCulture);
    public static string Format(ExactAmount value) => value.ToString();
    public static string? Format(ExactAmount? value) => value?.ToString();

    /// <summary>Rounds half to even at eight places, then formats: for sizing estimates, never for facts.</summary>
    public static string FormatEightPlaces(decimal value) => decimal.Round(value, 8).ToString("0.########", CultureInfo.InvariantCulture);
    public static string? FormatEightPlaces(decimal? value) => value is { } number ? FormatEightPlaces(number) : null;

    // ---- Exact arithmetic ----

    /// <summary>The signed value as a whole number of 10^-28 units, exact for every decimal.</summary>
    public static BigInteger Units(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        return ((bits[3] & int.MinValue) == 0 ? coefficient : -coefficient) * BigInteger.Pow(10, 28 - ((bits[3] >> 16) & 0xff));
    }

    /// <summary>The exact sum as API text. Decimal addition can round when scales differ widely; this never does.</summary>
    public static string Sum(IEnumerable<decimal> values) =>
        values.Aggregate(default(ExactAmount), (sum, value) => sum + ExactAmount.From(value)).ToString();

    /// <summary>a + b, or false when decimal addition rounded the exact sum. Overflow still throws <see cref="OverflowException"/>.</summary>
    public static bool TryAdd(decimal a, decimal b, out decimal sum)
    {
        sum = a + b;
        return Units(sum) == Units(a) + Units(b);
    }

    /// <summary>a × b, or false when decimal multiplication rounded the exact product. Overflow still throws <see cref="OverflowException"/>.</summary>
    public static bool TryMultiply(decimal a, decimal b, out decimal product)
    {
        product = a * b;
        return Units(product) * BigInteger.Pow(10, 28) == Units(a) * Units(b);
    }
}
